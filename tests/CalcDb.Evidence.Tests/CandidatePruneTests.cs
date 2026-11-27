using CalcDb.Oracle.Runner;

namespace CalcDb.Evidence.Tests;

/// <summary>
/// Task w-store-prune: after candidate comparison finishes, disposable working directories
/// (<c>inputs/</c>, <c>work/</c>, <c>artifacts/</c>) inside a sealed candidate attempt are removed while preserving
/// sealed manifest/capture/receipt JSON and never touching ACE attempts, <c>compare-sets/</c>, <c>cas/</c>, or
/// retained <c>evidence/</c>.
/// </summary>
public sealed class CandidatePruneTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "calcdb-candidate-prune-" + Guid.NewGuid().ToString("N"));

    private static Question MakeQuestion(string probe) => new()
    {
        Probe = probe,
        BookRefs = ["W-01"],
        Description = "candidate prune test",
        CallerSurface = "native-idispatch/x64",
        InvokeLcid = 0x0409,
        ObservationRecipe = [],
        Program = [new ObserveModules("m")],
    };

    private static void PopulateWorkingFiles(string attemptDir, out long totalBytes)
    {
        totalBytes = 0;
        foreach ((string sub, string fileName, byte[] content, bool readOnly) in new (string, string, byte[], bool)[]
        {
            ("inputs", "fixture.accdb", new byte[4096], true),
            ("work", "temp.laccdb", new byte[128], false),
            ("artifacts", "out.accdb", new byte[8192], false),
        })
        {
            string dir = Path.Combine(attemptDir, sub);
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, fileName);
            File.WriteAllBytes(file, content);
            if (readOnly)
            {
                File.SetAttributes(file, FileAttributes.ReadOnly);
            }
            totalBytes += content.Length;
        }
    }

    private static AttemptRecord PrepareAndSeal(JobStore store, string probe, string environment, ImplementationSelection? implementation, bool seal = true)
    {
        Question q = MakeQuestion(probe);
        string token = Program.TokenOf(q, environment);
        store.SubmitOrQuery(token, q, environment);
        AttemptRecord attempt = store.PrepareAttempt(token, "rev-test", "book-v1.1", implementation);
        string dir = store.AttemptDirectory(attempt.AttemptId);
        Directory.CreateDirectory(Path.Combine(dir, "receipts"));
        File.WriteAllText(Path.Combine(dir, "receipts", "started.json"), "{}");
        File.WriteAllText(Path.Combine(dir, "capture.json"), "{}");
        if (seal)
        {
            store.Seal(attempt, [new OwnerReport { Participant = "a", Ownership = "test", ProcessId = null }]);
        }
        return attempt;
    }

    [Fact]
    public void PruneCandidateWorkingFiles_reclaims_working_dirs_and_preserves_manifest_capture_compare_sets_cas_and_evidence()
    {
        string storeRoot = Path.Combine(_root, "store");
        string evidenceDir = Path.Combine(_root, "evidence");
        string compareSetsDir = Path.Combine(storeRoot, "compare-sets");
        string casDir = Path.Combine(storeRoot, "cas");
        Directory.CreateDirectory(evidenceDir);
        Directory.CreateDirectory(compareSetsDir);
        Directory.CreateDirectory(casDir);

        string baselineFile = Path.Combine(compareSetsDir, "baseline.tsv");
        string casFile = Path.Combine(casDir, "deadbeef.bin");
        string evidenceFile = Path.Combine(evidenceDir, "ace-record.json");
        File.WriteAllText(baselineFile, "baseline-content");
        File.WriteAllBytes(casFile, [1, 2, 3, 4]);
        File.WriteAllText(evidenceFile, "{\"ace\":true}");

        var store = new JobStore(storeRoot);
        var impl = new ImplementationSelection("calcdb", @"C:\deploy", new string('a', 64));
        AttemptRecord attempt = PrepareAndSeal(store, "card/cand-prune", ImplementationSelection.CalcDbEnvironment, impl, seal: true);
        string attemptDir = store.AttemptDirectory(attempt.AttemptId);
        PopulateWorkingFiles(attemptDir, out long expectedBytes);

        Program.PruneAttemptResult result = Program.PruneCandidateWorkingFiles(store, attempt.AttemptId);

        Assert.True(result.Pruned);
        Assert.Equal(expectedBytes, result.BytesRemoved);
        Assert.Equal(0, result.FoldersFailed);

        // Disposable working directories are gone.
        Assert.False(Directory.Exists(Path.Combine(attemptDir, "inputs")));
        Assert.False(Directory.Exists(Path.Combine(attemptDir, "work")));
        Assert.False(Directory.Exists(Path.Combine(attemptDir, "artifacts")));

        // Sealed metadata, capture, and receipts remain intact.
        Assert.True(File.Exists(Path.Combine(attemptDir, "manifest.json")));
        Assert.True(File.Exists(Path.Combine(attemptDir, "attempt.json")));
        Assert.True(File.Exists(Path.Combine(attemptDir, "question.json")));
        Assert.True(File.Exists(Path.Combine(attemptDir, "capture.json")));
        Assert.True(File.Exists(Path.Combine(attemptDir, "receipts", "started.json")));

        // compare-sets/, cas/, and retained evidence/ are untouched.
        Assert.Equal("baseline-content", File.ReadAllText(baselineFile));
        Assert.Equal([1, 2, 3, 4], File.ReadAllBytes(casFile));
        Assert.Equal("{\"ace\":true}", File.ReadAllText(evidenceFile));

        // Second call is an idempotent no-op.
        Program.PruneAttemptResult second = Program.PruneCandidateWorkingFiles(store, attempt.AttemptId);
        Assert.False(second.Pruned);
        Assert.Equal(0, second.BytesRemoved);
        Assert.Equal(0, second.FoldersFailed);
    }

    [Fact]
    public void PruneCandidateWorkingFiles_never_touches_ace_reference_attempts_or_unsealed_attempts()
    {
        string storeRoot = Path.Combine(_root, "store-guard");
        var store = new JobStore(storeRoot);

        // (a) Sealed ACE reference attempt (Implementation is null) must never be pruned.
        AttemptRecord aceAttempt = PrepareAndSeal(store, "card/ace-ref", "windows-x64-ace-local", implementation: null, seal: true);
        string aceDir = store.AttemptDirectory(aceAttempt.AttemptId);
        PopulateWorkingFiles(aceDir, out _);

        Program.PruneAttemptResult aceResult = Program.PruneCandidateWorkingFiles(store, aceAttempt.AttemptId);
        Assert.False(aceResult.Pruned);
        Assert.Equal(0, aceResult.BytesRemoved);
        Assert.True(File.Exists(Path.Combine(aceDir, "inputs", "fixture.accdb")));
        Assert.True(File.Exists(Path.Combine(aceDir, "work", "temp.laccdb")));
        Assert.True(File.Exists(Path.Combine(aceDir, "artifacts", "out.accdb")));

        // (b) Unsealed candidate attempt (manifest.json absent) must never be pruned.
        var impl = new ImplementationSelection("calcdb", @"C:\deploy", new string('b', 64));
        AttemptRecord unsealed = PrepareAndSeal(store, "card/cand-unsealed", ImplementationSelection.CalcDbEnvironment, impl, seal: false);
        string unsealedDir = store.AttemptDirectory(unsealed.AttemptId);
        PopulateWorkingFiles(unsealedDir, out _);

        Program.PruneAttemptResult unsealedResult = Program.PruneCandidateWorkingFiles(store, unsealed.AttemptId);
        Assert.False(unsealedResult.Pruned);
        Assert.Equal(0, unsealedResult.BytesRemoved);
        Assert.True(File.Exists(Path.Combine(unsealedDir, "inputs", "fixture.accdb")));
        Assert.True(File.Exists(Path.Combine(unsealedDir, "work", "temp.laccdb")));
        Assert.True(File.Exists(Path.Combine(unsealedDir, "artifacts", "out.accdb")));
    }

    public void Dispose()
    {
        if (!Directory.Exists(_root))
        {
            return;
        }
        foreach (string file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }
        Directory.Delete(_root, recursive: true);
    }
}
