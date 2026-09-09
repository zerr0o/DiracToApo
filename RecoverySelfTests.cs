using System.Diagnostics;

namespace DiracToApo;

public sealed partial class ConversionService
{
    /// <summary>Only creates disposable scratch profiles; never loads VSTs or reads live Dirac settings.</summary>
    public static int RunRecoverySelfTests()
    {
        int passed = 0;
        void Check(bool condition, string description)
        {
            if (!condition) throw new InvalidOperationException("Recovery self-test failed: " + description);
        }
        void ExpectFailure(Action action)
        {
            try { action(); }
            catch (InvalidDataException) { return; }
            catch (IOException) { return; }
            throw new InvalidOperationException("Expected a safe refusal.");
        }
        void Test(string name, Action<RecoveryScratch> action)
        {
            using var scratch = new RecoveryScratch();
            action(scratch);
            Console.WriteLine("PASS recovery: " + name);
            passed++;
        }
        try
        {
            Test("restore originals and preserve changed settings", s =>
            {
                s.Restore();
                Check(File.ReadAllText(s.Settings(0)) == "original-0", "original settings restored");
                Check(!File.Exists(s.Settings(1)), "new settings removed");
                Check(!Directory.Exists(s.Slot) && !File.Exists(s.Pending), "slot and journal removed");
                string[] preserved = Directory.GetFiles(Path.Combine(s.Job, "settings-before-restore"), "*", SearchOption.AllDirectories);
                Check(preserved.Length == 2 && preserved.Any(p => File.ReadAllText(p) == "changed-0"), "changed versions saved");
                s.Restore(); // Simulates a crash after slot retirement but before pending deletion.
                Check(File.ReadAllText(s.Settings(0)) == "original-0", "recovery is repeatable");
            });
            Test("unowned same-filter slot survives a failed claim", s =>
            {
                s.JournalData = s.JournalData with { WorkerPid = 0, WorkerStartTicks = 0, WorkerMayHaveWritten = false };
                File.Delete(Path.Combine(s.Slot, OwnerMarker));
                s.Restore();
                Check(Directory.Exists(s.Slot) && File.Exists(Path.Combine(s.Slot, "filter.bin")), "unowned slot kept");
                Check(File.ReadAllText(s.Settings(0)) == "changed-0" && File.Exists(s.Settings(1)), "settings never touched before permission");
            });
            Test("unowned empty slot survives a failed claim", s =>
            {
                s.JournalData = s.JournalData with { WorkerPid = 0, WorkerStartTicks = 0, WorkerMayHaveWritten = false };
                File.Delete(Path.Combine(s.Slot, OwnerMarker));
                File.Delete(Path.Combine(s.Slot, "filter.bin"));
                s.Restore();
                Check(Directory.Exists(s.Slot), "third-party empty slot retained");
            });
            Test("missing backup prevents any settings replacement", s =>
            {
                s.JournalData.SettingsExisted[SettingsNames[1]] = true;
                s.JournalData.BackupHashes![SettingsNames[1]] = new string('A', 64);
                ExpectFailure(s.Restore);
                Check(File.ReadAllText(s.Settings(0)) == "changed-0" && File.Exists(s.Pending), "first setting and journal unchanged");
            });
            Test("altered backup is refused", s =>
            {
                File.WriteAllText(Path.Combine(s.Job, "backups", SettingsNames[0]), "damaged");
                ExpectFailure(s.Restore);
                Check(File.ReadAllText(s.Settings(0)) == "changed-0", "damaged backup not installed");
            });
            Test("unexpected slot data is retained", s =>
            {
                File.WriteAllText(Path.Combine(s.Slot, "user-preset.txt"), "keep");
                ExpectFailure(s.Restore);
                Check(File.ReadAllText(s.Settings(0)) == "changed-0" && File.Exists(s.Pending), "no partial restore");
            });
            Test("changed filter is retained", s =>
            {
                File.WriteAllText(Path.Combine(s.Slot, "filter.bin"), "third-party-filter");
                ExpectFailure(s.Restore);
                Check(File.ReadAllText(Path.Combine(s.Slot, "filter.bin")) == "third-party-filter", "changed preset retained");
            });
            Test("missing owner after permission is refused", s =>
            {
                File.Delete(Path.Combine(s.Slot, OwnerMarker));
                ExpectFailure(s.Restore);
                Check(File.Exists(s.Pending), "ambiguous state journal retained");
            });
            Test("application conflict blocks all profile writes", s =>
            {
                ExpectFailure(() => RestoreFiles(s.JournalData, s.State, s.Profile, () => throw new IOException("Editor reopened")));
                Check(File.ReadAllText(s.Settings(0)) == "changed-0" && Directory.Exists(s.Slot), "conflict preserves profile");
            });
            Test("malformed and legacy journals fail closed", s =>
            {
                ExpectFailure(() => ValidateJournal(s.JournalData with { BackupHashes = null }));
                ExpectFailure(() => ValidateJournal(s.JournalData with { SettingsExisted = null! }));
                ExpectFailure(() => ValidateJournal(s.JournalData with { FilterSha256 = null! }));
                ExpectFailure(() => ValidateJournal(s.JournalData with { WorkerPid = 0 }));
            });
            Test("live worker prevents recovery", s =>
            {
                using var self = Process.GetCurrentProcess();
                ExpectFailure(() => RequireWorkerStopped(s.JournalData with
                { WorkerPid = self.Id, WorkerStartTicks = self.StartTime.ToUniversalTime().Ticks }));
            });
            Test("permission gate must match current process", s =>
            {
                using var self = Process.GetCurrentProcess();
                WorkerHandshake.GrantPermission(s.Job, self.Id, self.StartTime.ToUniversalTime().Ticks);
                var request = new WorkerRequest("never-loaded.dll", s.Job, 48000, 7);
                WorkerHandshake.WaitForPermission(request);
                File.Delete(Path.Combine(s.Job, "worker-go.json"));
                WorkerHandshake.GrantPermission(s.Job, self.Id, self.StartTime.ToUniversalTime().Ticks + 1);
                ExpectFailure(() => WorkerHandshake.WaitForPermission(request));
            });
            Report(new ThrowingProgress(), 70, "must not throw");
            Console.WriteLine($"Recovery self-tests passed: {passed}; throwing progress safely ignored.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    private sealed class ThrowingProgress : IProgress<ConversionProgress>
    {
        public void Report(ConversionProgress value) => throw new InvalidOperationException("Broken observer");
    }

    private sealed class RecoveryScratch : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "DiracToApo-recovery-tests-" + Guid.NewGuid().ToString("N"));
        public string State => Path.Combine(root, "state");
        public string Profile => Path.Combine(root, "profile");
        public string Job => Path.Combine(State, "jobs", JournalData.JobId);
        public string Slot => Path.Combine(Profile, "filters", "7");
        public string Pending => Path.Combine(State, "pending.json");
        public Journal JournalData { get; set; }
        public string Settings(int index) => Path.Combine(Profile, SettingsNames[index]);

        public RecoveryScratch()
        {
            JournalData = new Journal(Guid.NewGuid().ToString("N"), 7, new string('A', 64),
                new Dictionary<string, bool> { [SettingsNames[0]] = true, [SettingsNames[1]] = false },
                new Dictionary<string, string>(), WorkerPid: 1, WorkerStartTicks: 1, WorkerMayHaveWritten: true);
            SafeFileIO.RejectReparsePoints(root);
            Directory.CreateDirectory(Path.Combine(Job, "backups"));
            Directory.CreateDirectory(Slot);
            File.WriteAllText(Path.Combine(Slot, "filter.bin"), "scratch-filter");
            SafeFileIO.WriteNewText(Path.Combine(Slot, OwnerMarker), JournalData.JobId);
            string backup = Path.Combine(Job, "backups", SettingsNames[0]);
            File.WriteAllText(backup, "original-0");
            JournalData.BackupHashes![SettingsNames[0]] = Hash(backup);
            JournalData = JournalData with { FilterSha256 = Hash(Path.Combine(Slot, "filter.bin")) };
            File.WriteAllText(Settings(0), "changed-0");
            File.WriteAllText(Settings(1), "created-by-worker");
            File.WriteAllText(Pending, "scratch-pending");
        }
        public void Restore() => RestoreFiles(JournalData, State, Profile, () => { });
        public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
