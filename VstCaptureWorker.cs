using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Windows.Forms;

namespace DiracToApo;

/// <summary>Isolated, in-memory VST2 host. Never opens an audio device or a plug-in editor.</summary>
public static unsafe class VstCaptureWorker
{
    private const int BlockSize = 512;
    private const int MaximumChannels = 36;
    private const int ImpulseIndex = 2048;
    private const float ImpulseAmplitude = 0.1f;
    private const double GlobalGain = 0.1; // The state and parameter readback must both say -20 dB.

    public static int Run(WorkerRequest request)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(request);
            Directory.CreateDirectory(request.WorkDirectory);
            // A result from an earlier attempt must never be mistaken for success.
            File.Delete(Path.Combine(request.WorkDirectory, "result.json"));
            File.Delete(Path.Combine(request.WorkDirectory, "failure.txt"));
            if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess || !BitConverter.IsLittleEndian)
                throw new PlatformNotSupportedException("The VST worker requires Windows x64, little-endian.");
            if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
                throw new InvalidOperationException("The VST worker must run on the STA main thread.");
            if (request.SampleRate < 8000 || request.SampleRate > 384000 || request.Slot < 0 || request.Slot > 7)
                throw new ArgumentOutOfRangeException(nameof(request), "Invalid sample rate or zero-based filter slot.");
            if (!File.Exists(request.PluginPath))
                throw new FileNotFoundException("The VST2 plug-in was not found.", request.PluginPath);
            ValidateAbi();
            WorkerResult result;
            using (var host = new Host(request))
                result = host.Capture();
            // Publish only after effClose succeeds. The parent still has to validate the DSP.
            File.WriteAllText(Path.Combine(request.WorkDirectory, "result.json"),
                JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        catch (Exception ex)
        {
            // Native access violations can terminate the process instead: the parent must check exit status.
            try
            {
                if (request is not null)
                {
                    Directory.CreateDirectory(request.WorkDirectory);
                    File.WriteAllText(Path.Combine(request.WorkDirectory, "failure.txt"),
                        $"{ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
                }
            }
            catch { /* Preserve the original failure exit code if diagnostics cannot be written. */ }
            return 1;
        }
    }

    private static void ValidateAbi()
    {
        if (sizeof(AEffect) != 192 || sizeof(TimeInfo) != 88 ||
            Marshal.OffsetOf<AEffect>(nameof(AEffect.Dispatcher)).ToInt32() != 8 ||
            Marshal.OffsetOf<AEffect>(nameof(AEffect.InitialDelay)).ToInt32() != 80 ||
            Marshal.OffsetOf<AEffect>(nameof(AEffect.ProcessReplacing)).ToInt32() != 120)
            throw new PlatformNotSupportedException("Unexpected VST2 x64 ABI layout.");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AEffect
    {
        public int Magic;
        public nint Dispatcher, Process, SetParameter, GetParameter;
        public int NumPrograms, NumParams, NumInputs, NumOutputs, Flags;
        public nint Reserved1, Reserved2;
        public int InitialDelay, RealQualities, OffQualities;
        public float IoRatio;
        public nint Object, User;
        public int UniqueId, Version;
        public nint ProcessReplacing, ProcessDoubleReplacing;
        public fixed byte Future[56];
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TimeInfo
    {
        public double SamplePosition, SampleRate, NanoSeconds, PpqPosition, Tempo, BarStartPosition,
            CycleStartPosition, CycleEndPosition;
        public int TimeSignatureNumerator, TimeSignatureDenominator, SmpteOffset, SmpteFrameRate,
            SamplesToNextClock, Flags;
    }

    // VST2 uses cdecl, VstInt32 and pointer-sized VstIntPtr, including the dispatcher return.
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint HostCallback(nint effect, int opcode, int index, nint value, nint pointer, float option);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint EntryPoint(HostCallback callback);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint Dispatcher(nint effect, int opcode, int index, nint value, nint pointer, float option);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void ProcessReplacing(nint effect, float** inputs, float** outputs, int frames);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate float GetParameter(nint effect, int index);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern uint MsgWaitForMultipleObjects(uint count, nint handles,
        [MarshalAs(UnmanagedType.Bool)] bool waitAll, uint milliseconds, uint wakeMask);

    private sealed class Host : IDisposable
    {
        private readonly WorkerRequest request;
        // This field roots the reverse P/Invoke delegate until effClose and DLL unloading finish.
        private readonly HostCallback callback;
        private readonly ConcurrentDictionary<int, long> callbackCounts = new();
        private nint library, effect;
        // Native delegates become available in Open, which may fail at any point.
        private Dispatcher? dispatcher;
        private ProcessReplacing? process;
        private GetParameter? getParameter;
        private TimeInfo* time;
        private float** inputs;
        private float** outputs;
        private int inputChannels, outputChannels;
        private bool opened, mainsOn, started, disposed;
        private int callbackFailed;

        public Host(WorkerRequest request)
        {
            this.request = request;
            callback = OnHostCallback;
        }

        private nint Call(int opcode, int index = 0, nint value = 0, nint pointer = 0, float option = 0)
            => (dispatcher ?? throw new InvalidOperationException("The VST dispatcher is not initialized."))
                (effect, opcode, index, value, pointer, option);

        private void Log(string stage, object data)
        {
            string line = JsonSerializer.Serialize(new { stage, data });
            File.AppendAllText(Path.Combine(request.WorkDirectory, "progress.jsonl"),
                line + Environment.NewLine, Encoding.UTF8);
            File.WriteAllText(Path.Combine(request.WorkDirectory, "progress.txt"), line, Encoding.UTF8);
        }

        private nint OnHostCallback(nint ignoredEffect, int opcode, int index, nint value, nint pointer, float option)
        {
            try
            {
                callbackCounts.AddOrUpdate(opcode, 1, (_, n) => n + 1);
                switch (opcode)
                {
                    case 1: return 2400; // audioMasterVersion
                    case 7: return (nint)time; // audioMasterGetTime
                    case 16: return request.SampleRate;
                    case 17: return BlockSize;
                    case 23: return 2; // Realtime semantics! Dirac can bypass offline/render processing.
                    case 24: return 0;
                    case 32:
                    case 33:
                        if (pointer == 0) return 0;
                        byte[] name = Encoding.ASCII.GetBytes("Local IR Capture\0");
                        Marshal.Copy(name, 0, pointer, name.Length);
                        return 1;
                    case 34: return 1000;
                    case 37:
                        string capability = pointer == 0 ? "" : Marshal.PtrToStringAnsi(pointer) ?? "";
                        return capability is "sendVstTimeInfo" or "startStopProcess" or "acceptIOChanges" ? 1 : 0;
                    case 13: // IOChanged: the block path checks bounds before each native call.
                    case 42:
                    case 43: return 1;
                    default: return 0;
                }
            }
            catch
            {
                // Never unwind a managed exception through a native callback.
                Interlocked.Exchange(ref callbackFailed, 1);
                return 0;
            }
        }

        private void Open()
        {
            Log("loading", new { request.SampleRate, request.Slot, blockSize = BlockSize });
            time = (TimeInfo*)NativeMemory.AllocZeroed((nuint)sizeof(TimeInfo));
            time->SampleRate = request.SampleRate;
            time->Tempo = 120;
            time->TimeSignatureNumerator = 4;
            time->TimeSignatureDenominator = 4;
            time->Flags = 2 | 1024 | 8192; // playing, tempo valid, time signature valid
            library = NativeLibrary.Load(Path.GetFullPath(request.PluginPath));
            if (!NativeLibrary.TryGetExport(library, "VSTPluginMain", out nint address))
                throw new InvalidDataException("The DLL does not export VSTPluginMain.");
            var entry = Marshal.GetDelegateForFunctionPointer<EntryPoint>(address);
            effect = entry(callback);
            if (effect == 0 || ((AEffect*)effect)->Magic != 0x56737450)
                throw new InvalidDataException("The DLL did not return a valid VST2 AEffect.");
            if (((AEffect*)effect)->Dispatcher == 0)
                throw new InvalidDataException("Missing VST dispatcher.");
            dispatcher = Marshal.GetDelegateForFunctionPointer<Dispatcher>(((AEffect*)effect)->Dispatcher);
            opened = true;
            Call(0); // effOpen
            Call(10, option: request.SampleRate);
            Call(11, value: BlockSize);
            byte[] state = BuildState(request.Slot);
            fixed (byte* statePointer = state)
                Call(24, value: state.Length, pointer: (nint)statePointer); // effSetChunk, bank

            // VstSpeakerArrangement contains a two-int header and eight 112-byte speaker records.
            byte* speakerIn = stackalloc byte[8 + 112 * 8];
            byte* speakerOut = stackalloc byte[8 + 112 * 8];
            new Span<byte>(speakerIn, 8 + 112 * 8).Clear();
            new Span<byte>(speakerOut, 8 + 112 * 8).Clear();
            ((int*)speakerIn)[0] = ((int*)speakerOut)[0] = 1; // kSpeakerArrStereo
            ((int*)speakerIn)[1] = ((int*)speakerOut)[1] = 2;
            nint stereoResult = Call(42, value: (nint)speakerIn, pointer: (nint)speakerOut);
            CheckChannels();
            AEffect* e = (AEffect*)effect;
            if (e->ProcessReplacing == 0 || e->GetParameter == 0)
                throw new InvalidDataException("Missing VST float processing or parameter readback.");
            process = Marshal.GetDelegateForFunctionPointer<ProcessReplacing>(e->ProcessReplacing);
            getParameter = Marshal.GetDelegateForFunctionPointer<GetParameter>(e->GetParameter);
            // Allocate for the advertised maximum, not just the requested stereo layout.
            inputs = AllocateBuffers();
            outputs = AllocateBuffers();
            Log("opened", new { inputs = e->NumInputs, outputs = e->NumOutputs,
                initialDelay = e->InitialDelay, stereoResult = (long)stereoResult, parameters = ReadParameters() });
            mainsOn = true;
            Call(12, value: 1);
            started = true;
            Call(71);
            Log("warmup", new { seconds = 12, hardwareAudio = false, processLevel = 2 });
            var timer = Stopwatch.StartNew();
            while (timer.Elapsed < TimeSpan.FromSeconds(12))
            {
                Application.DoEvents();
                ProcessBlock();
                MsgWaitForMultipleObjects(0, 0, false, 10, 0x04FF);
            }
            VerifyState();
            Log("initialized", new { initialDelay = e->InitialDelay,
                globalGain = GlobalGain, parameters = ReadParameters(), hostCalls = callbackCounts.ToArray() });
        }

        private static float** AllocateBuffers()
        {
            float** pointers = (float**)NativeMemory.AllocZeroed(MaximumChannels, (nuint)sizeof(float*));
            try
            {
                for (int c = 0; c < MaximumChannels; c++)
                    pointers[c] = (float*)NativeMemory.AllocZeroed(BlockSize, sizeof(float));
                return pointers;
            }
            catch
            {
                FreeBuffers(pointers);
                throw;
            }
        }

        private static void FreeBuffers(float** buffers)
        {
            if (buffers == null) return;
            for (int c = 0; c < MaximumChannels; c++) NativeMemory.Free(buffers[c]);
            NativeMemory.Free(buffers);
        }

        private void CheckChannels()
        {
            AEffect* e = (AEffect*)effect;
            if (e->NumInputs < 2 || e->NumOutputs < 2 ||
                e->NumInputs > MaximumChannels || e->NumOutputs > MaximumChannels)
                throw new InvalidDataException("The plug-in reported an unsupported I/O layout.");
            inputChannels = Math.Max(inputChannels, e->NumInputs);
            outputChannels = Math.Max(outputChannels, e->NumOutputs);
        }

        private void ClearInputs()
        {
            for (int c = 0; c < MaximumChannels; c++) new Span<float>(inputs[c], BlockSize).Clear();
        }

        private void ProcessBlock()
        {
            CheckChannels();
            for (int c = 0; c < MaximumChannels; c++) new Span<float>(outputs[c], BlockSize).Clear();
            var render = process ?? throw new InvalidOperationException("VST processing is not initialized.");
            render(effect, inputs, outputs, BlockSize);
            time->SamplePosition += BlockSize;
            time->PpqPosition = time->SamplePosition / request.SampleRate * 2;
            if (Volatile.Read(ref callbackFailed) != 0)
                throw new InvalidOperationException("A VST host callback failed.");
            // Check all allocated outputs, including channels outside the requested stereo bus.
            for (int c = 0; c < MaximumChannels; c++)
                for (int i = 0; i < BlockSize; i++)
                    if (!float.IsFinite(outputs[c][i]))
                        throw new InvalidDataException("The plug-in produced non-finite audio.");
        }

        private void FlushSilence()
        {
            ClearInputs();
            // At least three seconds, and never less than the published delay plus one second.
            int frames = Math.Max(checked(request.SampleRate * 3),
                checked(Math.Max(0, ((AEffect*)effect)->InitialDelay) + request.SampleRate));
            if (frames > request.SampleRate * 30L)
                throw new InvalidDataException("The plug-in reported an excessive processing delay.");
            for (int offset = 0; offset < frames; offset += BlockSize)
            {
                ProcessBlock();
                if (offset % (BlockSize * 16) == 0) Application.DoEvents();
            }
        }

        private sealed record Parameter(int Index, string Name, float Normalized, string Display);

        private List<Parameter> ReadParameters()
        {
            int count = ((AEffect*)effect)->NumParams;
            if (count < 0 || count > 256) throw new InvalidDataException("Invalid VST parameter count.");
            var parameters = new List<Parameter>();
            var read = getParameter ?? throw new InvalidOperationException("VST parameter readback is not initialized.");
            byte* text = stackalloc byte[256];
            for (int i = 0; i < count; i++)
            {
                new Span<byte>(text, 256).Clear();
                Call(8, index: i, pointer: (nint)text);
                text[255] = 0;
                string name = Marshal.PtrToStringAnsi((nint)text) ?? "";
                new Span<byte>(text, 256).Clear();
                Call(7, index: i, pointer: (nint)text);
                text[255] = 0;
                string display = Marshal.PtrToStringAnsi((nint)text) ?? "";
                parameters.Add(new Parameter(i, name, read(effect, i), display));
            }
            return parameters;
        }

        private void VerifyState()
        {
            List<Parameter> parameters = ReadParameters();
            Parameter Find(string name) => parameters.FirstOrDefault(p => p.Name == name)
                ?? throw new InvalidDataException($"Required VST parameter missing: {name}.");
            double DisplayNumber(string name)
            {
                string text = Find(name).Display.Trim();
                if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) ||
                    !double.IsFinite(value))
                    throw new InvalidDataException($"Cannot verify VST parameter: {name}.");
                return value;
            }
            if (Math.Abs(DisplayNumber("Gain") - (-20)) > 0.051)
                throw new InvalidDataException("The plug-in did not retain the known -20 dB global gain.");
            foreach (string name in new[] { "Filter Enabled", "Delay Compensation", "Gain Compensation" })
                if (!(Find(name).Normalized >= 0.999f))
                    throw new InvalidDataException($"The plug-in did not enable {name}.");
            if (DisplayNumber("Filter Slot") != request.Slot || DisplayNumber("Channel Index") != 36)
                throw new InvalidDataException("The plug-in did not retain the requested slot and stereo channel set.");
            for (int slot = 0; slot < 8; slot++)
                if (Math.Abs(DisplayNumber($"Gain Offset Filter Slot {slot}")) > 0.051)
                    throw new InvalidDataException("An unexpected per-slot gain offset is active.");
            // This is state verification, NOT proof that the loaded correction is active.
            // The parent must reject identity/bypass and compare independent validation audio.
        }

        public WorkerResult Capture()
        {
            Open();
            int captureFrames = checked(request.SampleRate * 3);
            for (int source = 0; source < 2; source++)
            {
                Log("capture", new { source, frames = captureFrames, impulseIndex = ImpulseIndex });
                FlushSilence();
                var data = new float[MaximumChannels][];
                for (int c = 0; c < MaximumChannels; c++) data[c] = new float[captureFrames];
                for (int offset = 0; offset < captureFrames; offset += BlockSize)
                {
                    if (offset == ImpulseIndex) inputs[source][0] = ImpulseAmplitude;
                    ProcessBlock();
                    if (offset == ImpulseIndex) inputs[source][0] = 0;
                    int count = Math.Min(BlockSize, captureFrames - offset);
                    for (int c = 0; c < MaximumChannels; c++)
                        new ReadOnlySpan<float>(outputs[c], count).CopyTo(data[c].AsSpan(offset, count));
                    if (offset % (BlockSize * 16) == 0) Application.DoEvents();
                }
                var summary = new List<object>();
                for (int c = 0; c < MaximumChannels; c++)
                {
                    double peak = 0;
                    int peakIndex = 0;
                    for (int i = 0; i < captureFrames; i++)
                    {
                        double magnitude = Math.Abs(data[c][i]);
                        if (magnitude > peak) { peak = magnitude; peakIndex = i; }
                    }
                    string file = $"capture_input{source}_output{c}.f32";
                    if (c < 2 || peak > 0)
                    {
                        WriteFloats(file, data[c]);
                        summary.Add(new { output = c, file, peak, peakIndex });
                    }
                    else File.Delete(Path.Combine(request.WorkDirectory, file));
                }
                Log("captured", new { source, outputs = summary });
            }
            RenderValidation();
            VerifyState();
            AEffect* e = (AEffect*)effect;
            Log("captured_unvalidated", new { initialDelay = e->InitialDelay, globalGain = GlobalGain });
            return new WorkerResult(request.SampleRate, captureFrames, ImpulseIndex, ImpulseAmplitude,
                GlobalGain, e->InitialDelay, inputChannels, outputChannels,
                e->Version.ToString(CultureInfo.InvariantCulture));
        }

        private void RenderValidation()
        {
            FlushSilence();
            int frames = checked(request.SampleRate * 4);
            var source = new float[checked(frames * 2)];
            uint state = 0x6D2B79F5;
            float Noise()
            {
                state ^= state << 13;
                state ^= state >> 17;
                state ^= state << 5;
                return ((state >> 8) / 8388608f - 1f) * 0.03f;
            }
            for (int i = request.SampleRate / 10; i < request.SampleRate * 2; i++)
            {
                source[2 * i] = Noise();
                source[2 * i + 1] = Noise();
            }
            source[137 * 2] += 0.08f;
            source[137 * 2 + 1] -= 0.06f;
            int lateImpulse = request.SampleRate * 5 / 2 + 31;
            source[lateImpulse * 2] -= 0.07f;
            source[lateImpulse * 2 + 1] += 0.09f;
            WriteFloats("validation_input.f32", source);
            var rendered = new[] { new float[frames], new float[frames] };
            Log("validation", new { frames, seed = "6D2B79F5" });
            for (int offset = 0; offset < frames; offset += BlockSize)
            {
                ClearInputs();
                int count = Math.Min(BlockSize, frames - offset);
                for (int i = 0; i < count; i++)
                {
                    inputs[0][i] = source[(offset + i) * 2];
                    inputs[1][i] = source[(offset + i) * 2 + 1];
                }
                ProcessBlock();
                for (int c = 0; c < 2; c++)
                    new ReadOnlySpan<float>(outputs[c], count).CopyTo(rendered[c].AsSpan(offset, count));
                if (offset % (BlockSize * 16) == 0) Application.DoEvents();
            }
            ClearInputs();
            for (int c = 0; c < 2; c++) WriteFloats($"validation_output{c}.f32", rendered[c]);
            Log("validation_rendered", new { frames });
        }

        private void WriteFloats(string name, float[] samples)
        {
            using var stream = new FileStream(Path.Combine(request.WorkDirectory, name),
                FileMode.Create, FileAccess.Write, FileShare.Read);
            stream.Write(MemoryMarshal.AsBytes(samples.AsSpan()));
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            // Nest finally blocks so failures in stop/suspend do not skip effClose or managed cleanup.
            try
            {
                try { if (started) Call(72); }
                finally
                {
                    try { if (mainsOn) Call(12, value: 0); }
                    finally { if (opened) Call(1); }
                }
            }
            finally
            {
                effect = 0;
                FreeBuffers(inputs);
                FreeBuffers(outputs);
                inputs = outputs = null;
                if (library != 0) NativeLibrary.Free(library);
                library = 0;
                NativeMemory.Free(time);
                time = null;
                GC.KeepAlive(callback);
            }
        }
    }

    // JUCE ValueTree::writeToStream, not XML and not an FXP wrapper.
    // Strings are NUL-terminated UTF-8; counts and var lengths use JUCE compressed ints.
    private static byte[] BuildState(int slot)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        void Compressed(int value)
        {
            if (value == 0) { writer.Write((byte)0); return; }
            uint n = checked((uint)value);
            int bytes = n <= 0xff ? 1 : n <= 0xffff ? 2 : n <= 0xffffff ? 3 : 4;
            writer.Write((byte)bytes);
            for (int i = 0; i < bytes; i++) writer.Write((byte)(n >> (8 * i)));
        }
        void Text(string text)
        {
            writer.Write(Encoding.UTF8.GetBytes(text));
            writer.Write((byte)0);
        }
        void StringVar(string text)
        {
            Compressed(Encoding.UTF8.GetByteCount(text) + 2);
            writer.Write((byte)5); // varMarker_String
            Text(text);
        }
        void Parameter(string id, double value)
        {
            Text("PARAM");
            Compressed(2);
            Text("id"); StringVar(id);
            Text("value"); Compressed(9); writer.Write((byte)4); writer.Write(value);
            Compressed(0); // no child trees
        }
        Text("PluginProcessorState");
        Compressed(4);
        Text("deviceName"); StringVar("Dirac IR Capture - " + Guid.NewGuid().ToString("N"));
        Text("channelMapping"); Compressed(0); // void JUCE var
        Text("customChannelsConfig"); StringVar("");
        Text("customChannelsRouting");
        StringVar(string.Concat(Enumerable.Range(0, 36).Select(n => n.ToString("D2", CultureInfo.InvariantCulture))));
        Compressed(14);
        Parameter("channelSet", 36);
        Parameter("delayCompensation", 1);
        Parameter("filterEnabled", 1);
        Parameter("filterSlot", slot);
        Parameter("gain", -20);
        Parameter("gainCompensation", 1);
        for (int i = 1; i <= 8; i++) Parameter($"gainOffsetSlot{i}", 0);
        writer.Flush();
        return stream.ToArray();
    }
}
