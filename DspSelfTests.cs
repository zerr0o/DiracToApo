using System;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace DiracToApo;

/// <summary>Dependency-free deterministic DSP and file-format tests. Does not load Dirac or change APO.</summary>
public static class DspSelfTests
{
    public static int Run()
    {
        string directory = Path.Combine(Path.GetTempPath(), "DiracToApoSelfTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            TestFft();
            TestWave(directory);
            TestExport(directory);
            TestRejectedCaptures(directory);
            TestRealArtifacts(directory);
            Console.WriteLine("DSP self-tests: PASS (FFT, WAV, causal export, bypass, cross-routing, invalid samples, tail, validation, cancellation).");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("DSP self-tests: FAIL\n" + ex);
            return 1;
        }
        finally
        {
            try { Directory.Delete(directory, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static void TestFft()
    {
        double[] x = { 1, -2, 3, 0.125, -0.75, 0, 4 };
        double[] h = { 0, 0.25, -0.125, 0.75 };
        double[] actual = ImpulseExporter.Convolve(x, h, CancellationToken.None);
        double[] expected = DirectConvolve(x, h);
        for (int i = 0; i < expected.Length; i++) Near(actual[i], expected[i], 1e-11, "FFT linear convolution");
        double[] identity = ImpulseExporter.Convolve(x, new[] { 1.0 }, CancellationToken.None);
        for (int i = 0; i < x.Length; i++) Near(identity[i], x[i], 1e-11, "FFT identity");
        var data = new Complex[1024];
        for (int i = 0; i < data.Length; i++) data[i] = new Complex(Math.Sin(i * .13), Math.Cos(i * .27));
        var original = (Complex[])data.Clone();
        ImpulseExporter.Fft(data, false, CancellationToken.None);
        ImpulseExporter.Fft(data, true, CancellationToken.None);
        for (int i = 0; i < data.Length; i++) Near((data[i] - original[i]).Magnitude, 0, 1e-11, "FFT round trip");
        Expect<ArgumentException>(() => ImpulseExporter.Fft(new Complex[3], false, CancellationToken.None), "non radix-2 FFT");
        Assert(ImpulseExporter.IsUnityOrPureDelay(new[] { 0.0, 0, 1, 0 }), "Pure delay bypass detection");
        Assert(!ImpulseExporter.IsUnityOrPureDelay(new[] { 0.0, 0, .5, 0 }), "Gain-only correction allowed");
    }

    private static void TestWave(string directory)
    {
        string path = Path.Combine(directory, "header.wav");
        ImpulseExporter.WriteStereoWave(path, new[] { 0.0, .5, -1 }, new[] { .25, -.75, 1.0 }, 48000, CancellationToken.None);
        using var reader = new BinaryReader(File.OpenRead(path));
        Assert(Ascii(reader, 4) == "RIFF", "RIFF magic");
        Assert(reader.ReadUInt32() == reader.BaseStream.Length - 8, "RIFF size");
        Assert(Ascii(reader, 8) == "WAVEfmt ", "fmt chunk");
        Assert(reader.ReadUInt32() == 16 && reader.ReadUInt16() == 3 && reader.ReadUInt16() == 2, "IEEE float stereo");
        Assert(reader.ReadUInt32() == 48000 && reader.ReadUInt32() == 384000, "WAV sample/byte rate");
        Assert(reader.ReadUInt16() == 8 && reader.ReadUInt16() == 32, "WAV block/sample size");
        Assert(Ascii(reader, 4) == "fact" && reader.ReadUInt32() == 4 && reader.ReadUInt32() == 3, "fact frames");
        Assert(Ascii(reader, 4) == "data" && reader.ReadUInt32() == 24, "data size");
        foreach (float value in new[] { 0f, .25f, .5f, -.75f, -1f, 1f }) Near(reader.ReadSingle(), value, 0, "WAV interleaving");
        Assert(reader.BaseStream.Position == reader.BaseStream.Length, "WAV exact length");
        Expect<IOException>(() => ImpulseExporter.WriteStereoWave(path, new[] { 1.0 }, new[] { 1.0 }, 48000, CancellationToken.None), "Never overwrite WAV");
    }

    private static void TestExport(string directory)
    {
        Fixture fixture = MakeFixture(directory, "valid", false);
        ConversionResult result = Export(fixture);
        Assert(result.Frames == 1024, "Shared conservative power-of-two length");
        Assert(Path.GetFileName(result.WavPath).StartsWith("Dirac_filter_8000_", StringComparison.Ordinal), "Source name without extension");
        Assert(result.WorstRelativeErrorDb <= -70, "Independent validation threshold");
        string config = File.ReadAllText(result.ConfigPath);
        Assert(config.Contains("Channel: L R") && config.Contains("Convolution: " + Path.GetFileName(result.WavPath)), "Relative APO convolution path");
        Assert(config.Contains("If: sampleRate == 8000\r\n") && config.EndsWith("EndIf:\r\n", StringComparison.Ordinal), "APO sample-rate guard");
        Assert(config.IndexOf("Channel: L R", StringComparison.Ordinal) < config.IndexOf("Preamp:", StringComparison.Ordinal), "Preamp is limited to L/R channels");
        using (var reader = new BinaryReader(File.OpenRead(result.WavPath)))
        {
            reader.BaseStream.Position = 56;
            for (int i = 0; i < result.Frames; i++)
            {
                Near(reader.ReadSingle(), i == 37 ? .75 : i == 38 ? -.125 : 0, 1e-7, "Left causal delay retained");
                Near(reader.ReadSingle(), i == 61 ? .5 : i == 69 ? .0625 : 0, 1e-7, "Right relative delay retained");
            }
        }
        using (JsonDocument report = JsonDocument.Parse(File.ReadAllText(result.ReportPath)))
        {
            Assert(report.RootElement.GetProperty("validation").GetProperty("passed").GetBoolean(), "Report validation");
            Assert(report.RootElement.GetProperty("source").GetProperty("sha256").GetString()!.Length == 64, "SHA-256 report");
        }
        ConversionResult second = Export(fixture);
        Assert(result.WavPath != second.WavPath && File.Exists(result.WavPath), "Repeated export preserves old files");
        Assert(Directory.GetFiles(fixture.Output).Length == 6, "No transaction temporary files remain");
        Fixture gainOnly = MakeFixture(directory, "gain_only", true, .5);
        Assert(Export(gainOnly).WorstRelativeErrorDb <= -70, "Non-unity gain-only valid export");
    }

    private static void TestRejectedCaptures(string directory)
    {
        Fixture unity = MakeFixture(directory, "unity", true);
        Reject(unity, "Unity bypass rejected");
        Fixture cross = MakeFixture(directory, "cross", false);
        double[] crossData = new double[cross.Result.FramesPerCapture];
        crossData[cross.Result.ImpulseIndex + 37] = .001;
        WriteFloats(Path.Combine(cross.Work, "capture_input0_output1.f32"), crossData);
        Reject(cross, "Cross-routing rejected");
        Fixture extra = MakeFixture(directory, "extra_output", false);
        extra = extra with { Result = extra.Result with { OutputChannels = 3 } };
        WriteFloats(Path.Combine(extra.Work, "capture_input0_output2.f32"), crossData);
        Reject(extra, "Extra output rejected");
        Fixture nonfinite = MakeFixture(directory, "nan", false);
        string capture = Path.Combine(nonfinite.Work, "capture_input0_output0.f32");
        using (var stream = new FileStream(capture, FileMode.Open, FileAccess.Write))
        using (var writer = new BinaryWriter(stream)) writer.Write(float.NaN);
        Reject(nonfinite, "Non-finite capture rejected");
        Fixture silent = MakeFixture(directory, "zero", false);
        WriteFloats(Path.Combine(silent.Work, "capture_input0_output0.f32"), new double[silent.Result.FramesPerCapture]);
        Reject(silent, "Silent impulse rejected");
        Fixture tail = MakeFixture(directory, "tail", false);
        string tailPath = Path.Combine(tail.Work, "capture_input0_output0.f32");
        using (var stream = new FileStream(tailPath, FileMode.Open, FileAccess.Write))
        using (var writer = new BinaryWriter(stream)) { stream.Position = stream.Length - 4; writer.Write(.001f); }
        Reject(tail, "Insufficient tail rejected");
        Fixture mismatch = MakeFixture(directory, "mismatch", false);
        string reference = Path.Combine(mismatch.Work, "validation_output0.f32");
        using (var stream = new FileStream(reference, FileMode.Open, FileAccess.Write))
        using (var writer = new BinaryWriter(stream)) writer.Write(.5f);
        Reject(mismatch, "Independent validation mismatch rejected");
        Fixture shortFile = MakeFixture(directory, "short", false);
        WriteFloats(Path.Combine(shortFile.Work, "validation_output1.f32"), new[] { .1 });
        Reject(shortFile, "Validation length mismatch rejected");
        Fixture cancellation = MakeFixture(directory, "cancel", false);
        using var cts = new CancellationTokenSource(); cts.Cancel();
        Expect<OperationCanceledException>(() => ImpulseExporter.Export(cancellation.Result, cancellation.Work, cancellation.Output,
            cancellation.Source, new QuietProgress(), cts.Token), "Cancellation");
        Assert(!Directory.Exists(cancellation.Output), "No artifacts after cancellation");
    }

    // Existing, already validated captures are optional. No plugin, download, or Python is run.
    // Their known metadata comes from the adjacent validation-report.json and capture script.
    private static void TestRealArtifacts(string directory)
    {
        string sourceDirectory = Path.Combine(Path.GetTempPath(), "DiracToAPO");
        string[] names = { "capture_input0_output0", "capture_input1_output1", "validation_input", "validation_output0", "validation_output1" };
        if (!names.All(name => File.Exists(Path.Combine(sourceDirectory, name + "_48000.f32"))))
        {
            Console.WriteLine("DSP real-artifact test: SKIP (pre-existing 48000 Hz captures not found).");
            return;
        }
        string work = Path.Combine(directory, "real");
        Directory.CreateDirectory(work);
        foreach (string name in names) File.Copy(Path.Combine(sourceDirectory, name + "_48000.f32"), Path.Combine(work, name + ".f32"));
        // A stand-in is used ONLY to test source hashing; it is not represented as the actual preset.
        string source = Path.Combine(work, "test_fixture_source_not_original.bin");
        File.WriteAllText(source, "Test-only placeholder: captures are pre-existing validated artifacts.");
        int frames = checked((int)(new FileInfo(Path.Combine(work, names[0] + ".f32")).Length / 4));
        var metadata = new WorkerResult(48000, frames, 2048, .1, .01, 0, 2, 2, "pre-existing 48000 Hz test fixture");
        var fixture = new Fixture(work, Path.Combine(work, "output"), source, metadata);
        ConversionResult result = Export(fixture);
        Assert(result.Frames == 32768, "Real artifact expected 32768-frame response");
        Assert(result.WorstRelativeErrorDb < -90, "Real artifact expected error below -90 dB");
        Near(result.PreampDb, -9, 0, "Real artifact preamp");
        Console.WriteLine($"DSP real-artifact test: PASS ({result.Frames} frames, error {result.WorstRelativeErrorDb:F2} dB, preamp {result.PreampDb:F0} dB).");
    }

    private static Fixture MakeFixture(string parent, string name, bool singleTap, double gain = 1)
    {
        string work = Path.Combine(parent, name); Directory.CreateDirectory(work);
        string source = Path.Combine(work, "filter.bin"); File.WriteAllText(source, "synthetic test preset");
        const int impulseIndex = 64, length = 4096, validationLength = 8192;
        const double amplitude = .1, globalGain = .1;
        var result = new WorkerResult(8000, length + impulseIndex, impulseIndex, amplitude, globalGain, 999, 2, 2, "synthetic");
        var channels = new double[2][];
        var stereo = new double[validationLength * 2];
        var random = new Random(42);
        for (int i = 0; i < stereo.Length; i++) stereo[i] = (float)((random.NextDouble() * 2 - 1) * .05);
        WriteFloats(Path.Combine(work, "validation_input.f32"), stereo);
        for (int channel = 0; channel < 2; channel++)
        {
            channels[channel] = new double[length];
            if (singleTap) channels[channel][37] = gain;
            else if (channel == 0) { channels[channel][37] = .75; channels[channel][38] = -.125; }
            else { channels[channel][61] = .5; channels[channel][69] = .0625; }
            var capture = new double[length + impulseIndex];
            for (int i = 0; i < length; i++) capture[i + impulseIndex] = channels[channel][i] * amplitude * globalGain;
            WriteFloats(Path.Combine(work, $"capture_input{channel}_output{channel}.f32"), capture);
            var input = new double[validationLength];
            for (int i = 0; i < input.Length; i++) input[i] = stereo[i * 2 + channel];
            double[] output = DirectConvolve(input, channels[channel]);
            WriteFloats(Path.Combine(work, $"validation_output{channel}.f32"), output.Take(validationLength).Select(x => x * globalGain).ToArray());
        }
        return new Fixture(work, Path.Combine(work, "output"), source, result);
    }

    private static double[] DirectConvolve(double[] input, double[] impulse)
    {
        var output = new double[input.Length + impulse.Length - 1];
        // Sparse FIR loop gives a genuinely independent reference without slowing the tests.
        for (int tap = 0; tap < impulse.Length; tap++)
            if (impulse[tap] != 0)
                for (int i = 0; i < input.Length; i++) output[i + tap] += input[i] * impulse[tap];
        return output;
    }
    private static ConversionResult Export(Fixture f) => ImpulseExporter.Export(f.Result, f.Work, f.Output, f.Source, new QuietProgress(), CancellationToken.None);
    private static void Reject(Fixture f, string message)
    {
        Expect<InvalidDataException>(() => Export(f), message);
        Assert(!Directory.Exists(f.Output) || Directory.GetFiles(f.Output).Length == 0, message + ": no export files");
    }
    private static void WriteFloats(string path, double[] data)
    {
        using var writer = new BinaryWriter(File.Create(path));
        foreach (double value in data) writer.Write((float)value);
    }
    private static string Ascii(BinaryReader reader, int count) => Encoding.ASCII.GetString(reader.ReadBytes(count));
    private static void Near(double actual, double expected, double tolerance, string message) => Assert(double.IsFinite(actual) && Math.Abs(actual - expected) <= tolerance, $"{message}: expected {expected}, got {actual}");
    private static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Expect<T>(Action action, string message) where T : Exception
    {
        try { action(); } catch (T) { return; }
        throw new InvalidOperationException(message + ": expected " + typeof(T).Name);
    }
    private sealed record Fixture(string Work, string Output, string Source, WorkerResult Result);
    private sealed class QuietProgress : IProgress<ConversionProgress> { public void Report(ConversionProgress value) { } }
}
