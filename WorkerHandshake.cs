using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DiracToApo;

/// <summary>No native plug-in code may run until the parent has persisted the worker identity.</summary>
public static class WorkerHandshake
{
    private sealed record Permission(int WorkerPid, long WorkerStartTicks);

    internal static void GrantPermission(string directory, int pid, long startTicks)
    {
        string gate = Path.Combine(directory, "worker-go.json");
        SafeFileIO.WriteNewText(gate + ".tmp", JsonSerializer.Serialize(new Permission(pid, startTicks)));
        File.Move(gate + ".tmp", gate, false);
    }

    public static void WaitForPermission(WorkerRequest request)
    {
        SafeFileIO.RejectReparsePoints(request.WorkDirectory);
        string gate = Path.Combine(request.WorkDirectory, "worker-go.json");
        using var self = Process.GetCurrentProcess();
        long ticks = self.StartTime.ToUniversalTime().Ticks;
        var elapsed = Stopwatch.StartNew();
        while (elapsed.Elapsed < TimeSpan.FromSeconds(15))
        {
            if (File.Exists(gate))
            {
                SafeFileIO.RejectReparsePoints(gate);
                using var stream = File.OpenRead(gate);
                if (stream.Length > 4096) throw new InvalidDataException("Autorisation worker invalide.");
                var permission = JsonSerializer.Deserialize<Permission>(stream);
                if (permission is null || permission.WorkerPid != self.Id || permission.WorkerStartTicks != ticks)
                    throw new InvalidDataException("Autorisation worker invalide.");
                return;
            }
            Thread.Sleep(50);
        }
        throw new TimeoutException("Le parent n’a pas autorisé le chargement du plugin. Aucun plugin chargé.");
    }
}

internal static class SafeFileIO
{
    internal static void RejectReparsePoints(string path)
    {
        string? current = Path.GetFullPath(path);
        while (current is not null)
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Lien ou jonction refusé pour préserver les fichiers : " + current);
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            current = Path.GetDirectoryName(current);
        }
    }

    internal static string Hash(string path)
    {
        RejectReparsePoints(path);
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    internal static void WriteNewText(string path, string text)
    {
        RejectReparsePoints(path);
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var writer = new StreamWriter(stream, new UTF8Encoding(false), 4096, true);
        writer.Write(text);
        writer.Flush();
        stream.Flush(true);
    }

    internal static void CopyDurable(string source, string destination)
    {
        RejectReparsePoints(source);
        RejectReparsePoints(destination);
        using var input = File.OpenRead(source);
        using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        input.CopyTo(output);
        output.Flush(true);
    }
}
