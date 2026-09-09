using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;

namespace DiracToApo;

/// <summary>Converts captured, causal stereo impulse responses without moving their time origin.</summary>
public static class ImpulseExporter
{
    private const double CrossAmplitudeLimit = 0.000031622776601683795; // -90 dB
    private const double LastWindowEnergyLimit = 1e-10; // -100 dB energy ratio
    private const double TailL1Limit = 1e-7;
    private const double ValidationLimitDb = -70;
    private static readonly Regex CaptureName = new(@"^capture_input(\d+)_output(\d+)\.f32$", RegexOptions.CultureInvariant);

    public static ConversionResult Export(WorkerResult result, string workDirectory, string outputDirectory,
        string sourceFilterPath, IProgress<ConversionProgress> progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(progress);
        ct.ThrowIfCancellationRequested();
        if (result.SampleRate < 8000 || result.SampleRate > 768000 || result.FramesPerCapture <= 0 ||
            result.ImpulseIndex < 0 || result.ImpulseIndex >= result.FramesPerCapture ||
            result.InputChannels < 2 || result.OutputChannels < 2 ||
            !double.IsFinite(result.ImpulseAmplitude) || !double.IsFinite(result.GlobalGain) ||
            result.ImpulseAmplitude == 0 || result.GlobalGain == 0)
            throw new InvalidDataException("Métadonnées de capture invalides.");
        double scale = result.ImpulseAmplitude * result.GlobalGain;
        if (!double.IsFinite(scale) || scale == 0)
            throw new InvalidDataException("Amplitude ou gain de capture invalide.");
        string sourcePath = Path.GetFullPath(sourceFilterPath);
        string sourceHash;
        using (FileStream source = File.OpenRead(sourcePath))
            sourceHash = Convert.ToHexString(SHA256.HashData(source)).ToLowerInvariant();
        progress.Report(new ConversionProgress(78, "Analyse des réponses impulsionnelles et des canaux…"));
        var h = new double[2][];
        var diagEnergy = new double[2];
        var diagPeak = new double[2];
        var preImpulseEnergy = new double[2];
        for (int channel = 0; channel < 2; channel++)
        {
            ct.ThrowIfCancellationRequested();
            double[] capture = ReadFloatFile(Path.Combine(workDirectory, $"capture_input{channel}_output{channel}.f32"), ct);
            if (capture.Length != result.FramesPerCapture)
                throw new InvalidDataException("La longueur d'une capture ne correspond pas aux métadonnées.");
            h[channel] = new double[capture.Length - result.ImpulseIndex];
            for (int i = 0; i < capture.Length; i++)
            {
                double value = capture[i] / scale;
                if (!double.IsFinite(value)) throw new InvalidDataException("Réponse impulsionnelle non finie.");
                if (i < result.ImpulseIndex) preImpulseEnergy[channel] += value * value;
                else
                {
                    h[channel][i - result.ImpulseIndex] = value;
                    diagEnergy[channel] += value * value;
                    diagPeak[channel] = Math.Max(diagPeak[channel], Math.Abs(value));
                }
            }
            if (!(diagEnergy[channel] > 1e-24) || !double.IsFinite(diagEnergy[channel]))
                throw new InvalidDataException($"Le canal {ChannelName(channel)} est silencieux ou invalide.");
            if (preImpulseEnergy[channel] > diagEnergy[channel] * CrossAmplitudeLimit * CrossAmplitudeLimit)
                throw new InvalidDataException("Signal présent avant l'impulsion : état résiduel ou capture non causale.");
        }
        var routing = new List<object>();
        foreach (string path in Directory.EnumerateFiles(workDirectory, "capture_input*_output*.f32"))
        {
            ct.ThrowIfCancellationRequested();
            Match match = CaptureName.Match(Path.GetFileName(path));
            if (!match.Success) continue;
            int input = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            int output = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
            if (input > 1 || output >= result.OutputChannels)
                throw new InvalidDataException("Canal de capture inattendu.");
            if (input == output) continue;
            double[] extra = ReadFloatFile(path, ct);
            if (extra.Length != result.FramesPerCapture)
                throw new InvalidDataException("Longueur de capture multicanal incorrecte.");
            double energy = 0, peak = 0;
            foreach (double raw in extra)
            {
                double value = raw / scale;
                energy += value * value;
                peak = Math.Max(peak, Math.Abs(value));
            }
            double relativeRms = Math.Sqrt(energy / diagEnergy[input]);
            double relativePeak = peak / diagPeak[input];
            if (!double.IsFinite(relativeRms) || !double.IsFinite(relativePeak) ||
                relativeRms > CrossAmplitudeLimit || relativePeak > CrossAmplitudeLimit)
                throw new InvalidDataException($"Routage input{input} → output{output} non négligeable : incompatible avec une convolution stéréo diagonale.");
            routing.Add(new { input, output, relativeRmsDb = Db(relativeRms), relativePeakDb = Db(relativePeak) });
        }
        // A unity tap, including a pure common delay, cannot demonstrate an active correction.
        // Non-unity gain-only corrections are allowed, and still must pass the independent render.
        bool[] unity = h.Select(IsUnityOrPureDelay).ToArray();
        if (unity[0] && unity[1])
            throw new InvalidDataException("Réponse unité / délai pur : bypass plausible. L'activation de la correction ne peut pas être validée.");
        int availableFrames = h[0].Length;
        int lastWindow = Math.Max(1, result.SampleRate / 10);
        if (availableFrames < lastWindow + 512)
            throw new InvalidDataException("Capture trop courte pour vérifier les 100 dernières millisecondes et la marge de 512 échantillons.");
        var lastWindowEnergyRatio = new double[2];
        int requiredFrames = 1;
        for (int channel = 0; channel < 2; channel++)
        {
            double energy = 0;
            for (int i = availableFrames - lastWindow; i < availableFrames; i++) energy += h[channel][i] * h[channel][i];
            lastWindowEnergyRatio[channel] = energy / diagEnergy[channel];
            if (lastWindowEnergyRatio[channel] > LastWindowEnergyLimit)
                throw new InvalidDataException($"Queue du canal {ChannelName(channel)} encore active dans les 100 dernières ms. Une capture plus longue est requise.");
            double suffixL1 = 0;
            int keep = availableFrames;
            while (keep > 1 && suffixL1 + Math.Abs(h[channel][keep - 1]) < TailL1Limit)
                suffixL1 += Math.Abs(h[channel][--keep]);
            requiredFrames = Math.Max(requiredFrames, (int)Math.Min(availableFrames, (long)keep + 512));
        }
        int rounded = NextPowerOfTwo(requiredFrames);
        int frames = rounded <= availableFrames ? rounded : requiredFrames;
        var discardedL1 = new double[2];
        var quantizationL1 = new double[2];
        for (int channel = 0; channel < 2; channel++)
        {
            for (int i = frames; i < availableFrames; i++) discardedL1[channel] += Math.Abs(h[channel][i]);
            if (!(discardedL1[channel] < TailL1Limit)) throw new InvalidDataException("Borne L1 de troncature dépassée.");
            Array.Resize(ref h[channel], frames);
            // Validate the exact float32 coefficients that will be written, not a higher precision surrogate.
            for (int i = 0; i < frames; i++)
            {
                float coefficient = (float)h[channel][i];
                if (!float.IsFinite(coefficient)) throw new InvalidDataException("Coefficient hors plage float32.");
                quantizationL1[channel] += Math.Abs(h[channel][i] - coefficient);
                h[channel][i] = coefficient;
            }
        }
        progress.Report(new ConversionProgress(84, "Validation indépendante par convolution FFT double précision…"));
        double[] inputStereo = ReadFloatFile(Path.Combine(workDirectory, "validation_input.f32"), ct);
        if (inputStereo.Length == 0 || (inputStereo.Length & 1) != 0)
            throw new InvalidDataException("Entrée de validation stéréo invalide.");
        int validationFrames = inputStereo.Length / 2;
        if (validationFrames < frames)
            throw new InvalidDataException("Le rendu de validation est plus court que la réponse exportée.");
        var validation = new ValidationMetrics[2];
        var frequencyPeakDb = new double[2];
        for (int channel = 0; channel < 2; channel++)
        {
            ct.ThrowIfCancellationRequested();
            double[] input = new double[validationFrames];
            double inputEnergy = 0;
            for (int i = 0; i < input.Length; i++) { input[i] = inputStereo[2 * i + channel]; inputEnergy += input[i] * input[i]; }
            if (!(inputEnergy > 1e-24)) throw new InvalidDataException("Entrée de validation silencieuse.");
            double[] reference = ReadFloatFile(Path.Combine(workDirectory, $"validation_output{channel}.f32"), ct);
            if (reference.Length != validationFrames)
                throw new InvalidDataException("Longueurs entrée/sortie de validation différentes.");
            double[] predicted = Convolve(input, h[channel], ct);
            double signalEnergy = 0, errorEnergy = 0, maxError = 0, peak = 0;
            for (int i = 0; i < reference.Length; i++)
            {
                double observed = reference[i] / result.GlobalGain;
                double error = predicted[i] - observed;
                signalEnergy += observed * observed;
                errorEnergy += error * error;
                maxError = Math.Max(maxError, Math.Abs(error));
                peak = Math.Max(peak, Math.Abs(observed));
            }
            if (!(signalEnergy > 1e-24) || !double.IsFinite(signalEnergy) || !double.IsFinite(errorEnergy))
                throw new InvalidDataException("Rendu de validation silencieux ou non fini.");
            double errorDb = Db(Math.Sqrt(errorEnergy / signalEnergy));
            validation[channel] = new ValidationMetrics(ChannelName(channel), Math.Sqrt(signalEnergy / validationFrames),
                Math.Sqrt(errorEnergy / validationFrames), errorDb, maxError, peak);
            if (errorDb > ValidationLimitDb)
                throw new InvalidDataException($"Validation refusée sur {ChannelName(channel)} : erreur {errorDb:F2} dB, limite {ValidationLimitDb:F0} dB. Aucun WAV validé n'a été créé.");
            frequencyPeakDb[channel] = MeasurePeakDb(h[channel], ct);
        }
        double preampDb = -Math.Max(0, Math.Ceiling(frequencyPeakDb.Max()) + 1);
        double worstError = validation.Max(x => x.RelativeErrorDb);
        ct.ThrowIfCancellationRequested();
        progress.Report(new ConversionProgress(95, "Écriture du WAV stéréo et du rapport de validation…"));
        string directory = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(directory);
        string basename = SafeBasename(Path.GetFileNameWithoutExtension(sourcePath));
        string stem = $"Dirac_{basename}_{result.SampleRate}_{DateTime.UtcNow:yyyyMMdd_HHmmss_fff}_{Guid.NewGuid():N}";
        string wavPath = Path.Combine(directory, stem + ".wav");
        string configPath = Path.Combine(directory, stem + ".txt");
        string reportPath = Path.Combine(directory, stem + ".json");
        string[] targets = { wavPath, configPath, reportPath };
        string[] temporary = targets.Select(x => x + "." + Guid.NewGuid().ToString("N") + ".tmp").ToArray();
        var committed = new List<string>();
        try
        {
            WriteStereoWave(temporary[0], h[0], h[1], result.SampleRate, ct);
            string config = "# Dirac FIR — validated offline; import this file manually in Equalizer APO.\r\n" +
                "# Sample rate: " + result.SampleRate.ToString(CultureInfo.InvariantCulture) + " Hz.\r\n" +
                "# Inactive at other sample rates; no automatic FIR resampling.\r\n" +
                "If: sampleRate == " + result.SampleRate.ToString(CultureInfo.InvariantCulture) + "\r\n" +
                "Channel: L R\r\n" +
                "Preamp: " + preampDb.ToString("0", CultureInfo.InvariantCulture) + " dB\r\n" +
                "Convolution: " + Path.GetFileName(wavPath) + "\r\nEndIf:\r\n";
            WriteNewText(temporary[1], config);
            var report = new
            {
                schemaVersion = 1, createdUtc = DateTime.UtcNow, status = "validated_offline",
                source = new { fileName = Path.GetFileName(sourcePath), sha256 = sourceHash },
                capture = result,
                export = new { wav = Path.GetFileName(wavPath), config = Path.GetFileName(configPath), sampleRate = result.SampleRate,
                    channels = 2, frames, format = "WAV IEEE float32", delayPreserved = true,
                    timeOrigin = "Capture ImpulseIndex; no peak recentering; InitialDelay is not subtracted",
                    captureGainRemoved = true, preampDb, frequencyPeakDb,
                    frequencyFftFrames = NextPowerOfTwo(Math.Max(4096, checked(frames * 2))) },
                tail = new { availableFrames, lastWindowFrames = lastWindow, lastWindowEnergyRatio,
                    maximumLastWindowEnergyRatio = LastWindowEnergyLimit, discardedL1Bound = discardedL1,
                    maximumDiscardedL1Bound = TailL1Limit, guardFrames = 512, quantizationL1 },
                routing = new { limitDb = -90, testedNonDiagonalCaptures = routing,
                    omittedPaths = "Worker contract: omitted capture files are exactly zero paths." },
                validation = new { passed = true, frames = validationFrames, maximumRelativeErrorDb = ValidationLimitDb,
                    worstRelativeErrorDb = worstError, channels = validation,
                    method = "Independent radix-2 double-precision FFT linear convolution of float32 WAV coefficients; no time alignment or fitted gain" },
                warnings = unity.Any(x => x) ? new[] { "One channel is unity / pure delay; only the other channel demonstrates non-unity correction." } : Array.Empty<string>(),
                limits = new[] {
                    "Offline numerical validation against a separate plugin render, not a live Windows audio test.",
                    "Only the captured sample rate and static diagonal linear response are validated; dynamic or time-varying behavior is not reproduced.",
                    "The measured final 100 ms cannot exclude a later isolated response outside the capture window.",
                    "Validation covers the supplied render interval; convolution beyond that interval is not independently measured.",
                    "Headroom uses a sampled FFT frequency maximum plus at least 1 dB; it is not a time-domain clipping guarantee.",
                    "No system APO configuration or Dirac slot was modified by the exporter." }
            };
            WriteNewText(temporary[2], JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            ct.ThrowIfCancellationRequested();
            // A filesystem cannot atomically rename three files. Stage all first, publish without
            // overwrite, and roll back any files published by this invocation on failure.
            for (int i = 0; i < targets.Length; i++)
            {
                ct.ThrowIfCancellationRequested();
                File.Move(temporary[i], targets[i], false);
                committed.Add(targets[i]);
            }
            ct.ThrowIfCancellationRequested();
            return new ConversionResult(wavPath, configPath, reportPath, preampDb, worstError, frames);
        }
        catch
        {
            foreach (string path in committed) TryDelete(path);
            throw;
        }
        finally { foreach (string path in temporary) TryDelete(path); }
    }

    internal static double[] ReadFloatFile(string path, CancellationToken ct)
    {
        using FileStream stream = File.OpenRead(path);
        if (stream.Length == 0 || stream.Length % 4 != 0 || stream.Length / 4 > int.MaxValue)
            throw new InvalidDataException($"Fichier float32 invalide : {Path.GetFileName(path)}");
        double[] data = new double[(int)(stream.Length / 4)];
        using var reader = new BinaryReader(stream);
        for (int i = 0; i < data.Length; i++)
        {
            if ((i & 16383) == 0) ct.ThrowIfCancellationRequested();
            float value = reader.ReadSingle(); // BinaryReader explicitly reads little endian.
            if (!float.IsFinite(value)) throw new InvalidDataException($"Valeur NaN/Infinity dans {Path.GetFileName(path)}.");
            data[i] = value;
        }
        return data;
    }

    internal static bool IsUnityOrPureDelay(double[] h)
    {
        int peakIndex = 0;
        for (int i = 1; i < h.Length; i++) if (Math.Abs(h[i]) > Math.Abs(h[peakIndex])) peakIndex = i;
        if (Math.Abs(h[peakIndex] - 1) > 1e-5) return false;
        double remainder = 0;
        for (int i = 0; i < h.Length; i++) if (i != peakIndex) remainder += Math.Abs(h[i]);
        return remainder <= 1e-5;
    }

    internal static int NextPowerOfTwo(int value)
    {
        if (value <= 0 || value > (1 << 29)) throw new InvalidDataException("Longueur FFT hors plage.");
        int n = 1;
        while (n < value) n <<= 1;
        return n;
    }

    /// <summary>Local iterative Cooley-Tukey FFT; no DSP or plugin code is used.</summary>
    internal static void Fft(Complex[] data, bool inverse, CancellationToken ct)
    {
        int n = data.Length;
        if (n == 0 || (n & (n - 1)) != 0) throw new ArgumentException("FFT length must be a power of two.");
        for (int i = 1, j = 0; i < n; i++)
        {
            if ((i & 16383) == 0) ct.ThrowIfCancellationRequested();
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j) (data[i], data[j]) = (data[j], data[i]);
        }
        for (int width = 2; width <= n; width <<= 1)
        {
            int half = width / 2;
            double angle = (inverse ? 2 : -2) * Math.PI / width;
            Complex step = new(Math.Cos(angle), Math.Sin(angle));
            for (int start = 0; start < n; start += width)
            {
                if ((start & 16383) == 0) ct.ThrowIfCancellationRequested();
                Complex twiddle = Complex.One;
                for (int k = 0; k < half; k++)
                {
                    // Periodic exact restart avoids accumulated oscillator error on large FFTs.
                    if ((k & 1023) == 0)
                    {
                        ct.ThrowIfCancellationRequested();
                        twiddle = new Complex(Math.Cos(angle * k), Math.Sin(angle * k));
                    }
                    Complex even = data[start + k], odd = data[start + k + half] * twiddle;
                    data[start + k] = even + odd;
                    data[start + k + half] = even - odd;
                    twiddle *= step;
                }
            }
            if (width == n) break;
        }
        if (inverse)
            for (int i = 0; i < n; i++)
            {
                if ((i & 16383) == 0) ct.ThrowIfCancellationRequested();
                data[i] /= n;
            }
    }

    internal static double[] Convolve(double[] input, double[] impulse, CancellationToken ct)
    {
        int length = checked(input.Length + impulse.Length - 1);
        int n = NextPowerOfTwo(length);
        var a = new Complex[n];
        var b = new Complex[n];
        for (int i = 0; i < input.Length; i++) a[i] = new Complex(input[i], 0);
        for (int i = 0; i < impulse.Length; i++) b[i] = new Complex(impulse[i], 0);
        Fft(a, false, ct);
        Fft(b, false, ct);
        for (int i = 0; i < n; i++)
        {
            if ((i & 16383) == 0) ct.ThrowIfCancellationRequested();
            a[i] *= b[i];
        }
        Fft(a, true, ct);
        var output = new double[length];
        for (int i = 0; i < length; i++) output[i] = a[i].Real;
        return output;
    }

    private static double MeasurePeakDb(double[] impulse, CancellationToken ct)
    {
        var spectrum = new Complex[NextPowerOfTwo(Math.Max(4096, checked(impulse.Length * 2)))];
        for (int i = 0; i < impulse.Length; i++) spectrum[i] = new Complex(impulse[i], 0);
        Fft(spectrum, false, ct);
        double peak = 0;
        for (int i = 0; i <= spectrum.Length / 2; i++) peak = Math.Max(peak, spectrum[i].Magnitude);
        if (!double.IsFinite(peak) || peak <= 0) throw new InvalidDataException("Réponse fréquentielle invalide.");
        return Db(peak);
    }

    internal static void WriteStereoWave(string path, double[] left, double[] right, int sampleRate, CancellationToken ct)
    {
        if (left.Length != right.Length || left.Length == 0) throw new ArgumentException("Stereo channels must have the same nonzero length.");
        uint dataSize = checked((uint)left.Length * 8u);
        uint riffSize = checked(dataSize + 48u);
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
        writer.Write(Encoding.ASCII.GetBytes("RIFF")); writer.Write(riffSize);
        writer.Write(Encoding.ASCII.GetBytes("WAVEfmt ")); writer.Write(16u);
        writer.Write((ushort)3); writer.Write((ushort)2); writer.Write((uint)sampleRate);
        writer.Write(checked((uint)sampleRate * 8u)); writer.Write((ushort)8); writer.Write((ushort)32);
        writer.Write(Encoding.ASCII.GetBytes("fact")); writer.Write(4u); writer.Write((uint)left.Length);
        writer.Write(Encoding.ASCII.GetBytes("data")); writer.Write(dataSize);
        for (int i = 0; i < left.Length; i++)
        {
            if ((i & 16383) == 0) ct.ThrowIfCancellationRequested();
            float l = (float)left[i], r = (float)right[i];
            if (!float.IsFinite(l) || !float.IsFinite(r)) throw new InvalidDataException("WAV coefficient non-finite.");
            writer.Write(l); writer.Write(r);
        }
        writer.Flush(); stream.Flush(true);
    }

    private static void WriteNewText(string path, string text)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var writer = new StreamWriter(stream, new UTF8Encoding(false), 4096, true);
        writer.Write(text); writer.Flush(); stream.Flush(true);
    }

    private static string SafeBasename(string value)
    {
        string safe = Regex.Replace(value, @"[^A-Za-z0-9_-]+", "_").Trim('_', '-');
        if (safe.Length == 0) safe = "filter";
        return safe.Length <= 72 ? safe : safe[..72];
    }

    // JSON cannot encode -Infinity. -6000 dB is a finite floor far below float32.
    private static double Db(double amplitude) => 20 * Math.Log10(Math.Max(amplitude, 1e-300));
    private static string ChannelName(int channel) => channel == 0 ? "L" : "R";
    private static void TryDelete(string path) { try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    private sealed record ValidationMetrics(string Channel, double SignalRms, double ErrorRms, double RelativeErrorDb, double MaxAbsoluteError, double OutputPeak);
}
