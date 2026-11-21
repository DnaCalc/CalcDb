using System.Collections.Concurrent;

namespace CalcDb.Evidence.Tests;

public sealed class LedgerAppendTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "calcdb-ledger-test-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private sealed record LedgerEntry(int Writer, int Sequence, string Payload);

    [Fact]
    public void ConcurrentWritersLoseAndSplitNoLine()
    {
        Directory.CreateDirectory(_root);
        string ledger = Path.Combine(_root, "comparisons.jsonl");
        const int writers = 4;
        const int perWriter = 100;
        // Payloads exceed 8 KB (well above FileStream's default 4 KB buffer) so torn or interleaved multi-chunk writes would surface.
        string[] payloads = [.. Enumerable.Range(0, writers).Select(w => new string((char)('a' + w), 8500 + w * 137))];

        using var start = new Barrier(writers + 1);
        var errors = new ConcurrentQueue<Exception>();
        int writersDone = 0;

        Thread[] writerThreads = [.. Enumerable.Range(0, writers).Select(writerId => new Thread(() =>
        {
            try
            {
                start.SignalAndWait();
                for (int seq = 0; seq < perWriter; seq++)
                {
                    EvidenceJson.AppendLine(ledger, new LedgerEntry(writerId, seq, payloads[writerId]));
                }
            }
            catch (Exception ex)
            {
                errors.Enqueue(ex);
            }
            finally
            {
                Interlocked.Increment(ref writersDone);
            }
        }))];

        // A concurrent reader exercising EvidenceJson.ReadLines while all four writers append.
        var readerThread = new Thread(() =>
        {
            try
            {
                start.SignalAndWait();
                while (Volatile.Read(ref writersDone) < writers)
                {
                    if (File.Exists(ledger))
                    {
                        foreach (string line in EvidenceJson.ReadLines(ledger))
                        {
                            _ = EvidenceJson.Deserialize<LedgerEntry>(line);
                        }
                    }
                    Thread.Yield();
                }
            }
            catch (Exception ex)
            {
                errors.Enqueue(ex);
            }
        });

        foreach (Thread t in writerThreads)
        {
            t.Start();
        }
        readerThread.Start();
        foreach (Thread t in writerThreads)
        {
            t.Join();
        }
        readerThread.Join();

        Assert.Empty(errors);

        string[] rawLines = [.. EvidenceJson.ReadLines(ledger)];
        Assert.Equal(writers * perWriter, rawLines.Length);

        var lastSeq = new int[writers];
        Array.Fill(lastSeq, -1);
        var counts = new int[writers];
        foreach (string raw in rawLines)
        {
            LedgerEntry entry = EvidenceJson.Deserialize<LedgerEntry>(raw);
            Assert.InRange(entry.Writer, 0, writers - 1);
            Assert.Equal(payloads[entry.Writer], entry.Payload);
            Assert.Equal(lastSeq[entry.Writer] + 1, entry.Sequence);
            lastSeq[entry.Writer] = entry.Sequence;
            counts[entry.Writer]++;
        }
        Assert.All(counts, c => Assert.Equal(perWriter, c));
    }

    [Fact]
    public void TransientSharingViolationRetriesAndAppends()
    {
        Directory.CreateDirectory(_root);
        string ledger = Path.Combine(_root, "comparisons.jsonl");
        EvidenceJson.AppendLine(ledger, new LedgerEntry(0, 0, "seed"));

        // Hold the file open without write sharing (as an external reader or scanner does) briefly, then release.
        using var held = new ManualResetEventSlim(false);
        var holder = new Thread(() =>
        {
            using var stream = new FileStream(ledger, FileMode.Open, FileAccess.Read, FileShare.Read);
            held.Set();
            Thread.Sleep(120);
        });
        holder.Start();
        held.Wait();

        EvidenceJson.AppendLine(ledger, new LedgerEntry(1, 1, "after-lock"));
        holder.Join();

        LedgerEntry[] entries = [.. EvidenceJson.ReadLines(ledger).Select(EvidenceJson.Deserialize<LedgerEntry>)];
        Assert.Equal(2, entries.Length);
        Assert.Equal("seed", entries[0].Payload);
        Assert.Equal("after-lock", entries[1].Payload);
    }

    [Fact]
    public void PersistentSharingViolationThrowsNamedFailureAfterRetries()
    {
        Directory.CreateDirectory(_root);
        string ledger = Path.Combine(_root, "comparisons.jsonl");
        File.WriteAllText(ledger, "");

        using var lockStream = new FileStream(ledger, FileMode.Open, FileAccess.Read, FileShare.Read);
        IOException ex = Assert.Throws<IOException>(() =>
            EvidenceJson.AppendLineText(ledger, "{\"ok\":true}", maxAttempts: 3, retryDelayMs: 10));

        Assert.Contains(Path.GetFullPath(ledger), ex.Message, StringComparison.Ordinal);
        Assert.Contains("3 attempts", ex.Message, StringComparison.Ordinal);
        Assert.Contains("sharing violation", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(ex.InnerException);
    }
}
