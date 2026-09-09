using System.Diagnostics;
using System.Text.Json;

namespace DiracToApo;

public sealed partial class ConversionService : IConversionService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly string[] SettingsNames = ["DiracLiveProcessor.settings", "Dirac_Live_Processor.config"];
    private static string StateRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DiracToApo");
    private static string DiracRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Dirac", "Dirac_Live_Processor");
    private const string OwnerMarker = ".diractoapo-owner";
    private sealed record Journal(string JobId, int Slot, string FilterSha256, Dictionary<string, bool> SettingsExisted,
        Dictionary<string, string>? BackupHashes = null, int WorkerPid = 0, long WorkerStartTicks = 0, bool WorkerMayHaveWritten = false);

    // A progress sink must never prevent worker termination or profile restoration.
    private static void Report(IProgress<ConversionProgress> progress, int percent, string message)
    {
        try { progress.Report(new(percent, message)); } catch { }
    }

    public Task<ConversionResult> ConvertAsync(ConversionRequest request, IProgress<ConversionProgress> progress, CancellationToken cancellationToken)
        => Task.Run(() => RunAsync(request, progress, cancellationToken), cancellationToken);

    private static async Task<ConversionResult> RunAsync(ConversionRequest request, IProgress<ConversionProgress> progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(progress);
        if (!request.OtherDiracAppsClosed)
            throw new InvalidOperationException("Confirmez la fermeture de Dirac et des logiciels qui utilisent son plugin avant de convertir.");
        EnsureAppsClosed();
        if (!Environment.Is64BitProcess) throw new InvalidOperationException("Le convertisseur exige Windows 64 bits.");
        SafeFileIO.RejectReparsePoints(StateRoot);
        SafeFileIO.RejectReparsePoints(DiracRoot);
        Directory.CreateDirectory(StateRoot);
        using var fileLock = AcquireLock();
        RecoverPending(); // Recovery does not depend on the newly selected input/plugin being valid.
        ct.ThrowIfCancellationRequested();
        var filter = DiracFilterInspector.Read(request.FilterPath);
        if (!filter.SampleRates.Contains(request.SampleRate))
            throw new InvalidOperationException($"Ce filtre ne contient pas de jeu à {request.SampleRate} Hz. Fréquences présentes : {string.Join(", ", filter.SampleRates)} Hz.");
        ValidatePlugin(request.PluginPath);
        string outputDirectory = Path.GetFullPath(request.OutputDirectory);
        if (File.Exists(outputDirectory)) throw new IOException("La destination est un fichier, pas un dossier.");
        if (!Directory.Exists(DiracRoot))
            throw new InvalidOperationException("Dirac n’a pas encore de profil utilisateur. Ouvrez une fois le plugin et activez votre licence, puis fermez-le.");
        Report(progress, 5, "Filtre stéréo reconnu. Préparation d’un emplacement temporaire…");
        string jobId = Guid.NewGuid().ToString("N");
        string job = Path.Combine(StateRoot, "jobs", jobId);
        SafeFileIO.RejectReparsePoints(job);
        Directory.CreateDirectory(Path.Combine(job, "backups"));
        Directory.CreateDirectory(Path.Combine(job, "input"));
        string inputSnapshot = Path.Combine(job, "input", Path.GetFileName(request.FilterPath));
        ct.ThrowIfCancellationRequested();
        SafeFileIO.CopyDurable(request.FilterPath, inputSnapshot);
        if (Hash(inputSnapshot) != filter.Sha256)
            throw new IOException("Le fichier source a changé pendant sa lecture. Relancez la conversion.");
        string filtersPath = Path.Combine(DiracRoot, "filters");
        SafeFileIO.RejectReparsePoints(filtersPath);
        int slot = Enumerable.Range(0, 8).Reverse().FirstOrDefault(i =>
            !Path.Exists(Path.Combine(filtersPath, i.ToString())), -1);
        if (slot < 0)
            throw new InvalidOperationException("Les huit emplacements Dirac existent déjà. Le convertisseur refuse d’écraser un preset. Libérez un emplacement dans Dirac avant de réessayer.");
        EnsureAppsClosed();
        var existed = new Dictionary<string, bool>();
        var backupHashes = new Dictionary<string, string>();
        foreach (string name in SettingsNames)
        {
            string settings = Path.Combine(DiracRoot, name);
            SafeFileIO.RejectReparsePoints(settings);
            if (Directory.Exists(settings)) throw new IOException("Un réglage Dirac est un dossier : " + settings);
            existed[name] = File.Exists(settings);
            if (existed[name])
            {
                string backup = Path.Combine(job, "backups", name);
                SafeFileIO.CopyDurable(settings, backup);
                backupHashes[name] = Hash(backup);
            }
        }
        // Complete ownership evidence and content BEFORE atomically claiming a profile slot.
        // A cross-volume move fails safely; never fall back to merging/copying into a live slot.
        string prepared = Path.Combine(job, "prepared-slot");
        Directory.CreateDirectory(prepared);
        SafeFileIO.CopyDurable(inputSnapshot, Path.Combine(prepared, "filter.bin"));
        SafeFileIO.WriteNewText(Path.Combine(prepared, OwnerMarker), jobId);
        var journal = new Journal(jobId, slot, filter.Sha256, existed, backupHashes);
        WriteJournal(journal);
        WorkerResult? workerResult = null;
        Exception? failure = null;
        Process? process = null;
        bool processStarted = false;
        try
        {
            ct.ThrowIfCancellationRequested();
            EnsureAppsClosed();
            string slotPath = Path.Combine(filtersPath, slot.ToString());
            SafeFileIO.RejectReparsePoints(slotPath);
            Directory.CreateDirectory(filtersPath);
            Directory.Move(prepared, slotPath); // Must fail if any other program claimed this slot.
            var workerRequest = new WorkerRequest(Path.GetFullPath(request.PluginPath), job, request.SampleRate, slot);
            string requestPath = Path.Combine(job, "request.json");
            SafeFileIO.WriteNewText(requestPath, JsonSerializer.Serialize(workerRequest, JsonOptions));
            Report(progress, 15, "Chargement du plugin en mémoire — aucune sortie audio matérielle.");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMinutes(3));
            ct.ThrowIfCancellationRequested();
            EnsureAppsClosed();
            if (!ValidateOwnedSlot(slotPath, journal)) throw new IOException("L’emplacement temporaire a changé avant le lancement.");
            foreach (string name in SettingsNames)
            {
                string settings = Path.Combine(DiracRoot, name);
                SafeFileIO.RejectReparsePoints(settings);
                if (File.Exists(settings) != existed[name] ||
                    (existed[name] && !Hash(settings).Equals(backupHashes[name], StringComparison.OrdinalIgnoreCase)))
                    throw new IOException("Les réglages Dirac ont changé avant le lancement ; conversion refusée sans les remplacer.");
            }
            process = new Process { StartInfo = WorkerStartInfo(requestPath) };
            processStarted = process.Start();
            if (!processStarted) throw new IOException("Le processus de conversion n’a pas pu démarrer.");
            Task<string> stdout = process.StandardOutput.ReadToEndAsync();
            Task<string> stderr = process.StandardError.ReadToEndAsync();
            // Without this durable journal, the child has no permission to load native code.
            journal = journal with { WorkerPid = process.Id, WorkerStartTicks = process.StartTime.ToUniversalTime().Ticks, WorkerMayHaveWritten = true };
            WriteJournal(journal);
            ct.ThrowIfCancellationRequested();
            WorkerHandshake.GrantPermission(job, journal.WorkerPid, journal.WorkerStartTicks);
            Task exit = process.WaitForExitAsync(timeout.Token);
            string lastProgress = "";
            while (!exit.IsCompleted)
            {
                await Task.WhenAny(exit, Task.Delay(350, timeout.Token)).ConfigureAwait(false);
                timeout.Token.ThrowIfCancellationRequested();
                EnsureAppsClosed();
                string progressPath = Path.Combine(job, "progress.txt");
                try
                {
                    if (File.Exists(progressPath))
                    {
                        string text = File.ReadAllText(progressPath).Trim();
                        if (text.Length > 0 && text != lastProgress)
                        {
                            lastProgress = text;
                            Report(progress, 40, text.Length > 300 ? text[^300..] : text);
                        }
                    }
                }
                catch (IOException) { }
            }
            await exit.ConfigureAwait(false);
            File.WriteAllText(Path.Combine(job, "worker-stdout.txt"), await stdout.ConfigureAwait(false));
            File.WriteAllText(Path.Combine(job, "worker-stderr.txt"), await stderr.ConfigureAwait(false));
            if (process.ExitCode != 0)
            {
                string errorPath = Path.Combine(job, "failure.txt");
                string detail = File.Exists(errorPath) ? File.ReadAllText(errorPath) : "Le plugin n’a pas terminé son traitement. Vérifiez qu’il est activé pour votre compte Windows.";
                throw new InvalidOperationException(detail.Length > 1800 ? detail[..1800] : detail);
            }
            workerResult = JsonSerializer.Deserialize<WorkerResult>(File.ReadAllText(Path.Combine(job, "result.json")))
                ?? throw new InvalidDataException("Le plugin n’a pas produit de résultat exploitable.");
            if (workerResult.SampleRate != request.SampleRate)
                throw new InvalidDataException("La fréquence réellement traitée ne correspond pas à la demande.");
            ct.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        { failure = new TimeoutException("Le plugin n’a pas terminé dans le délai prévu. Aucun filtre n’a été exporté.", ex); }
        catch (Exception ex) { failure = ex; }
        finally
        {
            try
            {
                // Covers ALL failures after Start, including journal writes and permission publication.
                // Never restore while process termination is uncertain; retain journal and backups.
                if (processStarted && process is not null) await StopWorkerAsync(process).ConfigureAwait(false);
                Report(progress, 70, "Restauration des réglages Dirac et retrait du filtre temporaire…");
                Restore(journal);
            }
            catch (Exception ex)
            {
                failure = new IOException("Le nettoyage automatique n’a pas pu se terminer. Les sauvegardes sont conservées dans " + job + ". Fermez les logiciels Dirac et relancez une conversion pour tenter la récupération. " + ex.Message, failure ?? ex);
            }
            finally { process?.Dispose(); }
        }
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        ct.ThrowIfCancellationRequested();
        Report(progress, 75, "Comparaison de la convolution avec le traitement du plugin…");
        var result = ImpulseExporter.Export(workerResult!, job, outputDirectory, inputSnapshot, new SafeProgress(progress), ct);
        Report(progress, 100, "Conversion vérifiée. Le WAV est utilisable sans Dirac ni VST.");
        return result;
    }

    private sealed class SafeProgress(IProgress<ConversionProgress> inner) : IProgress<ConversionProgress>
    {
        public void Report(ConversionProgress value) => ConversionService.Report(inner, value.Percent, value.Message);
    }

    private static async Task StopWorkerAsync(Process process)
    {
        if (process.HasExited) return;
        try { process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) when (process.HasExited) { }
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
        if (!process.HasExited) throw new IOException("Le worker est encore actif ; restauration interdite.");
    }

    private static FileStream AcquireLock()
    {
        SafeFileIO.RejectReparsePoints(Path.Combine(StateRoot, "conversion.lock"));
        try { return new FileStream(Path.Combine(StateRoot, "conversion.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException ex) { throw new InvalidOperationException("Une autre conversion est déjà en cours. Attendez sa fin.", ex); }
    }

    private static void EnsureAppsClosed()
    {
        Process[] processes = Process.GetProcesses();
        try
        {
            foreach (var process in processes)
            {
                string name;
                try { name = process.ProcessName; } catch (InvalidOperationException) { continue; }
                if (name.Equals("DiracLiveProcessor", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("DiracLive", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("Dirac Live", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Fermez Dirac Live et Dirac Live Processor avant de convertir. Ne les rouvrez pas pendant la conversion.");
                if (name.Equals("Editor", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Fermez tous les processus Editor, notamment l’éditeur Equalizer APO, avant de convertir. Ne les rouvrez pas pendant la conversion.");
            }
        }
        finally { foreach (var process in processes) process.Dispose(); }
    }

    private static void ValidatePlugin(string path)
    {
        if (!File.Exists(path) || !Path.GetFileName(path).Equals("DiracLiveProcessor.dll", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Sélectionnez le plugin VST2 DiracLiveProcessor.dll installé sur ce PC. Le VST3 n’est pas pris en charge.");
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream);
        if (stream.Length < 64 || reader.ReadUInt16() != 0x5a4d) throw new InvalidDataException("Plugin Windows invalide.");
        stream.Position = 60;
        int offset = reader.ReadInt32();
        if (offset < 64 || offset > stream.Length - 6) throw new InvalidDataException("En-tête de plugin invalide.");
        stream.Position = offset;
        if (reader.ReadUInt32() != 0x4550 || reader.ReadUInt16() != 0x8664)
            throw new InvalidOperationException("Le plugin doit être une DLL Windows x64.");
    }

    private static ProcessStartInfo WorkerStartInfo(string requestPath)
    {
        string exe = Environment.ProcessPath ?? throw new InvalidOperationException("Chemin du programme introuvable.");
        var start = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        if (Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            string dllPath = Path.Combine(AppContext.BaseDirectory, "DiracToApo.dll");
            if (!File.Exists(dllPath)) throw new FileNotFoundException("Assembly du convertisseur introuvable.", dllPath);
            start.ArgumentList.Add(dllPath);
        }
        start.ArgumentList.Add("--worker"); start.ArgumentList.Add(requestPath);
        return start;
    }

    private static string Hash(string path) => SafeFileIO.Hash(path);

    private static void WriteJournal(Journal journal)
    {
        string path = Path.Combine(StateRoot, "pending.json");
        SafeFileIO.RejectReparsePoints(path);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        SafeFileIO.WriteNewText(temporary, JsonSerializer.Serialize(journal, JsonOptions));
        File.Move(temporary, path, true);
    }

    private static void RecoverPending()
    {
        string pending = Path.Combine(StateRoot, "pending.json");
        SafeFileIO.RejectReparsePoints(pending);
        if (!File.Exists(pending)) return;
        Journal journal;
        try
        {
            using var stream = File.OpenRead(pending);
            if (stream.Length > 65536) throw new InvalidDataException("Journal trop volumineux.");
            journal = JsonSerializer.Deserialize<Journal>(stream) ?? throw new InvalidDataException();
        }
        catch (Exception ex) { throw new IOException("Le journal de récupération est illisible. Ne supprimez pas les sauvegardes : " + pending, ex); }
        Restore(journal);
    }

    private static bool IsSha256(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);

    private static void ValidateJournal(Journal journal)
    {
        if (!Guid.TryParseExact(journal.JobId, "N", out _) || journal.Slot is < 0 or > 7 || !IsSha256(journal.FilterSha256) ||
            journal.SettingsExisted is null || journal.SettingsExisted.Count != SettingsNames.Length ||
            SettingsNames.Any(n => !journal.SettingsExisted.ContainsKey(n)) || journal.BackupHashes is null ||
            journal.BackupHashes.Keys.Any(n => !SettingsNames.Contains(n)) ||
            SettingsNames.Any(n => journal.SettingsExisted[n] &&
                (!journal.BackupHashes.TryGetValue(n, out string? hash) || !IsSha256(hash))) ||
            journal.WorkerPid < 0 || journal.WorkerStartTicks < 0 ||
            (journal.WorkerPid == 0) != (journal.WorkerStartTicks == 0) ||
            journal.WorkerStartTicks > DateTime.MaxValue.Ticks ||
            journal.WorkerMayHaveWritten != (journal.WorkerPid > 0))
            throw new InvalidDataException("Journal de récupération invalide ou ancien format. Les sauvegardes sont conservées ; une restauration manuelle peut être nécessaire.");
    }

    private static void RequireWorkerStopped(Journal journal)
    {
        if (journal.WorkerPid == 0) return;
        try
        {
            using var worker = Process.GetProcessById(journal.WorkerPid);
            if (!worker.HasExited && worker.StartTime.ToUniversalTime().Ticks == journal.WorkerStartTicks)
                throw new IOException("Le processus de conversion est encore actif. Attendez quelques instants avant de réessayer.");
        }
        catch (ArgumentException) { } // PID no longer exists. Other uncertainties must fail closed.
    }

    private static bool ValidateOwnedSlot(string slot, Journal journal)
    {
        SafeFileIO.RejectReparsePoints(slot);
        if (!Path.Exists(slot)) return false;
        if (!Directory.Exists(slot))
        {
            if (journal.WorkerMayHaveWritten) throw new IOException("L’emplacement temporaire est devenu un fichier ; il a été conservé.");
            return false;
        }
        string marker = Path.Combine(slot, OwnerMarker);
        SafeFileIO.RejectReparsePoints(marker);
        bool owned = false;
        if (File.Exists(marker))
        {
            using var stream = File.OpenRead(marker);
            if (stream.Length == 32)
            {
                using var reader = new StreamReader(stream);
                owned = reader.ReadToEnd() == journal.JobId;
            }
        }
        if (!owned)
        {
            if (journal.WorkerMayHaveWritten)
                throw new IOException("La propriété de l’emplacement temporaire ne peut pas être prouvée ; il a été conservé.");
            return false; // Failed claim: never change a third-party slot or its settings.
        }
        foreach (string entry in Directory.EnumerateFileSystemEntries(slot))
        {
            SafeFileIO.RejectReparsePoints(entry);
            string name = Path.GetFileName(entry);
            if (Directory.Exists(entry) || (name != OwnerMarker && !name.Equals("filter.bin", StringComparison.OrdinalIgnoreCase)))
                throw new IOException("L’emplacement temporaire contient des fichiers inattendus ; il a été conservé.");
        }
        string staged = Path.Combine(slot, "filter.bin");
        if (File.Exists(staged) && !Hash(staged).Equals(journal.FilterSha256, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Le filtre temporaire a été modifié par un autre programme ; il a été conservé.");
        return true;
    }

    private static void Restore(Journal journal)
    {
        ValidateJournal(journal);
        RequireWorkerStopped(journal);
        EnsureAppsClosed();
        RestoreFiles(journal, StateRoot, DiracRoot, EnsureAppsClosed);
    }

    // Separate transaction for tests: scratch roots only, never the real user profile.
    private static void RestoreFiles(Journal journal, string stateRoot, string diracRoot, Action ensureAppsClosed)
    {
        ValidateJournal(journal);
        ensureAppsClosed();
        SafeFileIO.RejectReparsePoints(stateRoot);
        SafeFileIO.RejectReparsePoints(diracRoot);
        string pending = Path.Combine(stateRoot, "pending.json");
        SafeFileIO.RejectReparsePoints(pending);
        string job = Path.Combine(stateRoot, "jobs", journal.JobId);
        SafeFileIO.RejectReparsePoints(job);
        string slot = Path.Combine(diracRoot, "filters", journal.Slot.ToString());
        bool ownsSlot = ValidateOwnedSlot(slot, journal);
        // Preflight EVERY backup and destination before changing even one settings file.
        foreach (string name in SettingsNames)
        {
            string settings = Path.Combine(diracRoot, name);
            SafeFileIO.RejectReparsePoints(settings);
            if (Directory.Exists(settings)) throw new IOException("Un réglage Dirac est devenu un dossier : " + settings);
            if (journal.SettingsExisted[name])
            {
                string backup = Path.Combine(job, "backups", name);
                if (!File.Exists(backup) || !Hash(backup).Equals(journal.BackupHashes![name], StringComparison.OrdinalIgnoreCase))
                    throw new IOException("Sauvegarde de réglages manquante ou altérée : " + backup);
            }
        }
        if (journal.WorkerMayHaveWritten)
        {
            // Preserve every current version before replacing/deleting anything.
            string conflicts = Path.Combine(job, "settings-before-restore", Guid.NewGuid().ToString("N"));
            SafeFileIO.RejectReparsePoints(conflicts);
            Directory.CreateDirectory(conflicts);
            foreach (string name in SettingsNames)
            {
                string settings = Path.Combine(diracRoot, name);
                if (File.Exists(settings)) SafeFileIO.CopyDurable(settings, Path.Combine(conflicts, name));
            }
            foreach (string name in SettingsNames)
            {
                ensureAppsClosed();
                string settings = Path.Combine(diracRoot, name);
                SafeFileIO.RejectReparsePoints(settings);
                if (journal.SettingsExisted[name])
                {
                    string backup = Path.Combine(job, "backups", name);
                    string expected = journal.BackupHashes![name];
                    if (!File.Exists(settings) || !Hash(settings).Equals(expected, StringComparison.OrdinalIgnoreCase))
                    {
                        string temporary = settings + ".diractoapo-restore-" + Guid.NewGuid().ToString("N");
                        SafeFileIO.CopyDurable(backup, temporary);
                        if (!Hash(temporary).Equals(expected, StringComparison.OrdinalIgnoreCase))
                            throw new IOException("La sauvegarde a changé pendant la restauration ; remplacement refusé.");
                        File.Move(temporary, settings, true);
                    }
                }
                else if (File.Exists(settings)) File.Delete(settings);
            }
        }
        ensureAppsClosed();
        if (ownsSlot)
        {
            // Keep ownership evidence during removal. A crash never leaves an unmarked live slot.
            if (!ValidateOwnedSlot(slot, journal)) throw new IOException("L’emplacement temporaire a changé ; retrait refusé.");
            string retired = Path.Combine(job, "retired-slot");
            SafeFileIO.RejectReparsePoints(retired);
            Directory.Move(slot, retired);
        }
        File.Delete(pending);
    }
}
