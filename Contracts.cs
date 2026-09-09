namespace DiracToApo;

public sealed record ConversionRequest(string FilterPath, string PluginPath, string OutputDirectory, int SampleRate, bool OtherDiracAppsClosed);
public sealed record ConversionProgress(int Percent, string Message);
public sealed record ConversionResult(string WavPath, string ConfigPath, string ReportPath, double PreampDb, double WorstRelativeErrorDb, int Frames);
public sealed record WorkerRequest(string PluginPath, string WorkDirectory, int SampleRate, int Slot);
public sealed record WorkerResult(int SampleRate, int FramesPerCapture, int ImpulseIndex, double ImpulseAmplitude, double GlobalGain, int InitialDelay, int InputChannels, int OutputChannels, string PluginVersion);

public interface IConversionService
{
    Task<ConversionResult> ConvertAsync(ConversionRequest request, IProgress<ConversionProgress> progress, CancellationToken cancellationToken);
}
