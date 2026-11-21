namespace CalcDb.Evidence.Tests;

/// <summary>
/// Concurrent cold-cache reads of one stored artifact through <see cref="ArtifactStore"/> (task 6dqh):
/// multiple threads decompressing and reading the same hash into an empty cache directory must never collide on
/// Windows sharing modes during the atomic publish rename.
/// </summary>
public sealed class ArtifactStoreConcurrencyTests
{
    [Fact]
    public void Parallel_cold_cache_ReadAll_of_one_artifact_never_fails_with_a_sharing_violation()
    {
        string dir = Path.Combine(Path.GetTempPath(), "calcdb-cas-race-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string artifactsDir = Path.Combine(dir, "artifacts");
            string source = Path.Combine(dir, "fixture.bin");
            byte[] payload = [.. Enumerable.Range(0, 65_536).Select(i => (byte)(i * 31 + 17))];
            File.WriteAllBytes(source, payload);
            string sha = new ArtifactStore(artifactsDir, Path.Combine(dir, "seed-cas")).Put(source);

            const int threads = 8;
            const int rounds = 80;
            for (int round = 0; round < rounds; round++)
            {
                string casDir = Path.Combine(dir, $"cas-{round}");
                var store = new ArtifactStore(artifactsDir, casDir);
                using var gate = new Barrier(threads);
                byte[][] results = new byte[threads][];
                Parallel.For(0, threads, new ParallelOptions { MaxDegreeOfParallelism = threads }, i =>
                {
                    gate.SignalAndWait();
                    results[i] = store.ReadAll(sha);
                });
                Assert.All(results, r => Assert.Equal(payload, r));
                Assert.Empty(Directory.EnumerateFiles(casDir, "*.tmp-*"));
            }
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Parallel_cold_cache_Locate_and_File_OpenRead_never_fails_with_a_sharing_violation()
    {
        string dir = Path.Combine(Path.GetTempPath(), "calcdb-cas-locate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string artifactsDir = Path.Combine(dir, "artifacts");
            string source = Path.Combine(dir, "fixture.bin");
            byte[] payload = [.. Enumerable.Range(0, 65_536).Select(i => (byte)(i * 19 + 5))];
            File.WriteAllBytes(source, payload);
            string sha = new ArtifactStore(artifactsDir, Path.Combine(dir, "seed-cas")).Put(source);

            const int threads = 8;
            const int rounds = 60;
            for (int round = 0; round < rounds; round++)
            {
                string casDir = Path.Combine(dir, $"cas-{round}");
                var store = new ArtifactStore(artifactsDir, casDir);
                using var gate = new Barrier(threads);
                byte[][] results = new byte[threads][];
                Parallel.For(0, threads, new ParallelOptions { MaxDegreeOfParallelism = threads }, i =>
                {
                    gate.SignalAndWait();
                    string path = store.Locate(sha) ?? throw new InvalidOperationException("Artifact not found.");
                    // Even a caller using raw File.OpenRead (FileShare.Read only) never sees a rename handle in flight.
                    using FileStream f = File.OpenRead(path);
                    byte[] head = new byte[64];
                    f.ReadExactly(head);
                    results[i] = head;
                });
                Assert.All(results, r => Assert.Equal(payload[..64], r));
            }
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void OpenRead_and_ReadAll_allow_concurrent_readers_while_another_handle_holds_shared_access()
    {
        string dir = Path.Combine(Path.GetTempPath(), "calcdb-cas-share-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string artifactsDir = Path.Combine(dir, "artifacts");
            string casDir = Path.Combine(dir, "cas");
            string source = Path.Combine(dir, "fixture.bin");
            byte[] payload = [.. Enumerable.Range(0, 16_384).Select(i => (byte)(i * 13 + 3))];
            File.WriteAllBytes(source, payload);
            var store = new ArtifactStore(artifactsDir, casDir);
            string sha = store.Put(source);

            string cached = store.Locate(sha)!;
            using FileStream holder = new(cached, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
            using FileStream? opened = store.OpenRead(sha);
            Assert.NotNull(opened);
            Assert.Equal(payload.Length, opened.Length);
            Assert.Equal(payload, store.ReadAll(sha));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
