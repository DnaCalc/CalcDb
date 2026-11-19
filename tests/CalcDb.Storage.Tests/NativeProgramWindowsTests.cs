using CalcDb.Storage.Windows;

namespace CalcDb.Storage.Tests;

/// <summary>
/// W-03/native-program-executor on the real positioned adapter (II-03 §§2.4, 6.2; W-03/positioned-native, exclusive
/// admission as selected by R-LOCK-1 EX-PROBE): a publication through PageCache and WindowsFile, a short write then
/// failure injected over the real adapter, and the adapter's own write and flush failures on a read-only handle.
/// A controlled-effect fixture cannot qualify real transfer or backing lifetime (II-16 §12); these tests do.
/// </summary>
public sealed class NativeProgramWindowsTests : IDisposable
{
    private const int PageSize = 4096;
    private readonly string _dir = Directory.CreateTempSubdirectory("calcdb-w03-exec-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void A_publication_runs_its_program_on_a_real_file_with_growth_and_exact_receipts()
    {
        string path = NewFile("publish.bin", 4);
        byte[] before = File.ReadAllBytes(path);
        using (WindowsFile file = Open(path, Admission.Exclusive))
        {
            var cache = new PageCache(new FilePageSource(file, PageSize, 1024, 1024UL * PageSize), file.Incarnation, PageSize, 8);
            PrivateOverlay a = cache.CreateOverlay(new LockerId(1));
            Assert.True(a.Edit(1, new BytePatch(100, [0xAA, 0xBB])).Succeeded);
            Assert.True(a.Edit(5, new WholePage(Filled(0x55))).Succeeded); // beyond the extent: an Append target
            PublicationStart start = cache.BeginPublication(a);
            PublicationAttempt attempt = start.Attempt!;
            Assert.True(attempt.Run(file).Succeeded);

            NativeProgram program = attempt.Program!;
            Assert.Equal([EffectKind.ResizeExtent, EffectKind.Transfer, EffectKind.Transfer], program.Effects.Select(e => e.Kind));
            Assert.Equal([5UL, 1UL], program.Effects.Skip(1).Select(e => e.PageNumber!.Value)); // the new target first
            Assert.Equal(6UL * PageSize, file.GetExtent());
            Assert.All(program.Progress.Capture, r =>
            {
                Assert.Equal(file.Incarnation, r.FileIncarnation);
                Assert.Equal(TransferStatus.Complete, r.Status);
            });
            Assert.Equal(2, program.Progress.Capture.Count);
            Assert.Empty(program.Progress.CompletedFlushes); // the ordinary path adds no flush (II-08 §18)
            ProgramConservation.Settled(program);
            using (PagePin now = cache.Pin(1).Pin!)
            {
                Assert.Equal(0xAA, now.Bytes[100]);
            }
            attempt.Dispose();
            a.Dispose();
            Conservation.Settled(cache);
        }
        byte[] after = File.ReadAllBytes(path);
        Assert.Equal(6 * PageSize, after.Length);
        Assert.Equal([0xAA, 0xBB], after[(PageSize + 100)..(PageSize + 102)]);
        Assert.True(after.AsSpan(0, PageSize).SequenceEqual(before.AsSpan(0, PageSize)));
        Assert.True(after.AsSpan(5 * PageSize, PageSize).SequenceEqual(Filled(0x55)));
    }

    [Fact]
    public void T_PAGER_13_a_short_write_then_failure_over_the_real_adapter_leaves_the_exact_third_image()
    {
        string path = NewFile("short.bin", 4);
        byte[] before = File.ReadAllBytes(path);
        NativeProgram program;
        using (WindowsFile real = Open(path, Admission.Exclusive))
        {
            var file = new ShortThenFail(real, 1536);
            program = NativeProgram.Compile(Unit((2, Filled(0xAB)), (3, Filled(0xCD))), ProgramPolicy.CrashOrderData, real.Incarnation, real.GetExtent()).Program!;
            NativeOutcome outcome = NativeProgramExecutor.Run(program, file);
            Assert.Equal(NativeOutcomeKind.Uncertain, outcome.Kind);
            ProgramConservation.Settled(program);
        }
        EffectReceipt receipt = program.Progress[0];
        Assert.Equal(1536UL, receipt.ProvenBytes);
        Assert.Equal(new ByteRange((2 * PageSize) + 1536, 3 * PageSize), receipt.PossibleEffect);
        Assert.Equal(EffectState.Pending, program.Progress[1].State);
        Assert.Equal(TransferStatus.Complete, program.Progress.Capture[0].Status); // the real adapter's short completion

        byte[] after = File.ReadAllBytes(path);
        Assert.True(after.AsSpan(2 * PageSize, 1536).SequenceEqual(Filled(0xAB).AsSpan(0, 1536)));
        Assert.True(after.AsSpan((2 * PageSize) + 1536).SequenceEqual(before.AsSpan((2 * PageSize) + 1536)));
    }

    [Fact]
    public void T_PAGER_13_the_real_adapters_write_and_flush_failures_stop_the_program()
    {
        string path = NewFile("readonly.bin", 4);
        byte[] before = File.ReadAllBytes(path);
        using (WindowsFile file = Open(path, Admission.ReaderOnly))
        {
            // A failed WriteFile proves nothing and may have changed the request (II-03 §2.2): uncertain, not retried.
            NativeProgram write = NativeProgram.Compile(Unit((1, Filled(0x11)), (2, Filled(0x22))), ProgramPolicy.CrashOrderData, file.Incarnation, file.GetExtent()).Program!;
            NativeOutcome outcome = NativeProgramExecutor.Run(write, file);
            Assert.Equal(NativeOutcomeKind.Uncertain, outcome.Kind);
            Assert.Equal(5, write.Progress[0].NativeError); // ERROR_ACCESS_DENIED
            Assert.Equal(new ByteRange(PageSize, 2 * PageSize), write.Progress[0].PossibleEffect);
            Assert.Equal(EffectState.Pending, write.Progress[1].State);
            ProgramConservation.Settled(write);

            // A separate flush barrier fails on its own (T-TXN-33): a known failure that establishes no durability.
            NativeProgram flush = NativeProgram.Compile(new PreparedUnit(PageSize, []), ProgramPolicy.CrashOrderData with { FinalFlush = true }, file.Incarnation, file.GetExtent()).Program!;
            NativeOutcome flushed = NativeProgramExecutor.Run(flush, file);
            Assert.Equal(NativeOutcomeKind.KnownFailure, flushed.Kind);
            Assert.Equal(5, flush.Progress[0].NativeError);
            Assert.Empty(flush.Progress.CompletedFlushes);
            ProgramConservation.Settled(flush);
        }
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public void Last_byte_growth_and_a_final_flush_run_on_a_real_file()
    {
        string path = NewFile("grow.bin", 2);
        using (WindowsFile file = Open(path, Admission.Exclusive))
        {
            ProgramPolicy policy = ProgramPolicy.CrashOrderData with { Name = "last-byte growth, forced", Growth = ExtentGrowth.WriteLastByte, FinalFlush = true };
            NativeProgram program = NativeProgram.Compile(Unit((3, Filled(0x33))), policy, file.Incarnation, file.GetExtent()).Program!;
            Assert.True(NativeProgramExecutor.Run(program, file).Completed);
            Assert.Equal(new ByteRange((4 * PageSize) - 1, 4 * PageSize), program.Progress.Capture[0].Attempted);
            Assert.Equal([2], program.Progress.CompletedFlushes);
            ProgramConservation.Settled(program);
        }
        byte[] after = File.ReadAllBytes(path);
        Assert.Equal(4 * PageSize, after.Length);
        Assert.True(after.AsSpan(3 * PageSize).SequenceEqual(Filled(0x33)));
    }

    /// <summary>
    /// A fault injector over the real adapter: the first write reaches the file truncated to a prefix (the adapter's
    /// own short completion), the next is failed with its range possibly changed and never reaches the file.
    /// </summary>
    private sealed class ShortThenFail(WindowsFile inner, int prefix) : IPositionedFile
    {
        private int _writes;

        public ulong Incarnation => inner.Incarnation;

        public FileIdentity Identity => inner.Identity;

        public TransferReceipt ReadAt(ulong offset, Memory<byte> buffer, CancellationToken cancellation = default) => inner.ReadAt(offset, buffer, cancellation);

        public TransferReceipt WriteAt(ulong offset, ReadOnlyMemory<byte> buffer, CancellationToken cancellation = default)
        {
            if (_writes++ == 0)
            {
                return inner.WriteAt(offset, buffer[..prefix], cancellation) with { Attempted = new ByteRange(offset, offset + (ulong)buffer.Length) };
            }
            var attempted = new ByteRange(offset, offset + (ulong)buffer.Length);
            return new TransferReceipt
            {
                FileIncarnation = inner.Incarnation,
                RequestId = 0,
                RequestGeneration = 0,
                Attempted = attempted,
                Status = TransferStatus.Failed,
                ProvenPrefixBytes = 0,
                PossibleEffect = attempted,
                NativeError = 1117,
            };
        }

        public FileOperationResult Flush() => inner.Flush();

        public ulong GetExtent() => inner.GetExtent();

        public FileOperationResult SetExtent(ulong extent) => inner.SetExtent(extent);

        public void Dispose()
        {
        }
    }

    private string NewFile(string name, int pages)
    {
        string path = Path.Combine(_dir, name);
        byte[] content = new byte[pages * PageSize];
        for (int i = 0; i < content.Length; i++)
        {
            content[i] = (byte)(i * 31);
        }
        File.WriteAllBytes(path, content);
        return path;
    }

    private static WindowsFile Open(string path, Admission admission)
    {
        OpenResult r = WindowsFile.Open(path, admission);
        Assert.True(r.Succeeded, $"open failed with {r.NativeError}");
        return r.File!;
    }

    private static PreparedUnit Unit(params (ulong Page, byte[] Image)[] pages) =>
        new(PageSize, [.. pages.Select((p, i) => new PreparedRevision(i, p.Page, p.Image))]);

    private static byte[] Filled(byte value)
    {
        byte[] bytes = new byte[PageSize];
        bytes.AsSpan().Fill(value);
        return bytes;
    }
}
