using CalcDb.Storage.Windows;

namespace CalcDb.Storage.Tests;

/// <summary>W-03/positioned-native on real files (II-03 §2.4; T-PAGER-06/07, T-LOCK-01/04/05/13).</summary>
public sealed class WindowsFileTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("calcdb-w03-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string NewFile(string name, int bytes)
    {
        string path = Path.Combine(_dir, name);
        byte[] content = new byte[bytes];
        for (int i = 0; i < bytes; i++)
        {
            content[i] = (byte)(i * 31);
        }
        File.WriteAllBytes(path, content);
        return path;
    }

    private static WindowsFile OpenOk(string path, Admission admission)
    {
        OpenResult r = WindowsFile.Open(path, admission);
        Assert.True(r.Succeeded, $"open failed with {r.NativeError}");
        return r.File!;
    }

    [Fact]
    public void Positioned_reads_and_writes_round_trip_without_a_file_pointer()
    {
        string path = NewFile("rw.bin", 3 * 4096);
        using WindowsFile f = OpenOk(path, Admission.Exclusive);
        byte[] page = new byte[4096];
        Array.Fill(page, (byte)0x5A);
        TransferReceipt w = f.WriteAt(4096, page);
        Assert.Equal(TransferStatus.Complete, w.Status);
        Assert.Equal(4096UL, w.ProvenPrefixBytes);
        byte[] back = new byte[4096];
        TransferReceipt r = f.ReadAt(4096, back);
        Assert.Equal(TransferStatus.Complete, r.Status);
        Assert.Equal(page, back);
        byte[] first = new byte[16];
        Assert.Equal(TransferStatus.Complete, f.ReadAt(0, first).Status);
        Assert.Equal((byte)31, first[1]);
        Assert.Equal(f.Incarnation, r.FileIncarnation);
        Assert.NotEqual(w.RequestId, r.RequestId);
    }

    [Fact]
    public void Reads_at_and_across_end_of_file_are_typed()
    {
        string path = NewFile("eof.bin", 4096 + 1000);
        using WindowsFile f = OpenOk(path, Admission.ReaderOnly);
        TransferReceipt past = f.ReadAt(8192, new byte[4096]);
        Assert.Equal(TransferStatus.EndOfFile, past.Status);
        Assert.Equal(0UL, past.ProvenPrefixBytes);
        TransferReceipt straddle = f.ReadAt(4096, new byte[4096]);
        Assert.Equal(TransferStatus.Complete, straddle.Status);
        Assert.Equal(1000UL, straddle.ProvenPrefixBytes);
        // The full-page read never publishes a short page.
        PageReadResult page = PageReads.ReadFullPage(f, new ByteRange(4096, 8192), new byte[4096]);
        Assert.Equal(PageReadOutcome.Truncated, page.Outcome);
        Assert.Equal(1000UL, page.BytesReceived);
    }

    [Fact]
    public void Cancellation_can_lose_to_an_immediate_success()
    {
        string path = NewFile("cancel.bin", 4096);
        using WindowsFile f = OpenOk(path, Admission.ReaderOnly);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        TransferReceipt r = f.ReadAt(0, new byte[4096], cancelled.Token);
        Assert.Equal(TransferStatus.Complete, r.Status);
        Assert.Equal(4096UL, r.ProvenPrefixBytes);
    }

    [Theory]
    [InlineData(Admission.Exclusive, Admission.ReaderOnly, false)]
    [InlineData(Admission.ReaderOnly, Admission.Exclusive, false)]
    [InlineData(Admission.SingleWriterShareRead, Admission.ReaderOnly, false)]
    [InlineData(Admission.ReaderOnly, Admission.SingleWriterShareRead, false)]
    [InlineData(Admission.SingleWriterShareRead, Admission.Exclusive, false)]
    [InlineData(Admission.Exclusive, Admission.SingleWriterShareRead, false)]
    [InlineData(Admission.SingleWriterShareRead, Admission.SingleWriterShareRead, false)]
    [InlineData(Admission.SingleWriterShareRead, Admission.SharedReadWrite, false)]
    [InlineData(Admission.SharedReadWrite, Admission.SingleWriterShareRead, false)]
    [InlineData(Admission.ReaderOnly, Admission.ReaderOnly, true)]
    [InlineData(Admission.ReaderOnly, Admission.SharedReadWrite, false)]
    [InlineData(Admission.SharedReadWrite, Admission.ReaderOnly, false)]
    [InlineData(Admission.Exclusive, Admission.Exclusive, false)]
    [InlineData(Admission.SharedReadWrite, Admission.SharedReadWrite, true)]
    public void Admission_pairs_are_decided_by_share_flags_in_both_orders(Admission first, Admission second, bool coexist)
    {
        // T-LOCK-01: the second open of each ordered pair; SingleWriterShareRead still denies ReaderOnly and any writer.
        string path = NewFile($"admit-{first}-{second}.bin", 4096);
        using WindowsFile a = OpenOk(path, first);
        OpenResult b = WindowsFile.Open(path, second);
        Assert.Equal(coexist, b.Succeeded);
        if (!coexist)
        {
            Assert.True(b.SharingViolation);
        }
        b.File?.Dispose();
    }

    [Fact]
    public void SingleWriterShareRead_admits_read_observer_declaring_read_and_write_share_and_denies_writers()
    {
        string path = NewFile("single-writer-share-read.bin", 4096);
        using WindowsFile writer = OpenOk(path, Admission.SingleWriterShareRead);

        // Read-only observer declaring FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE (gp86 nativeOpen).
        using (var observer = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            Assert.Equal(4096, observer.Length);
        }

        // External writer fails with sharing violation.
        Assert.Throws<IOException>(() => new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite));
    }

    [Fact]
    public void Path_aliases_share_one_identity_and_equal_content_does_not()
    {
        // T-LOCK-04 (EX-ALIAS): identity comes from the open handle, not from the name.
        string path = NewFile("alias.bin", 4096);
        string twin = NewFile("twin.bin", 4096);
        using WindowsFile a = OpenOk(path, Admission.ReaderOnly);
        using WindowsFile upper = OpenOk(path.ToUpperInvariant(), Admission.ReaderOnly);
        using WindowsFile extended = OpenOk(@"\\?\" + path, Admission.ReaderOnly);
        using WindowsFile other = OpenOk(twin, Admission.ReaderOnly);
        Assert.Equal("FileIdInfo (128-bit)", a.Identity.IdentityKind);
        Assert.Equal(a.Identity, upper.Identity);
        Assert.Equal(a.Identity, extended.Identity);
        Assert.NotEqual(a.Identity, other.Identity);
        Assert.NotEqual(a.Incarnation, upper.Incarnation);
    }

    [Fact]
    public void A_held_handle_prevents_replacement_and_a_replaced_path_shows_a_new_identity()
    {
        // T-LOCK-05 (EX-RACE): the handle denies delete sharing, so the path cannot be replaced while admitted;
        // a replacement between a probe and the real open is visible as a different identity.
        string path = NewFile("race.bin", 4096);
        FileIdentity probed;
        using (WindowsFile a = OpenOk(path, Admission.ReaderOnly))
        {
            probed = a.Identity;
            Assert.ThrowsAny<IOException>(() => File.Move(path, path + ".old"));
        }
        File.Move(path, path + ".old");
        NewFile("race.bin", 4096);
        using WindowsFile reopened = OpenOk(path, Admission.ReaderOnly);
        Assert.NotEqual(probed, reopened.Identity);
    }

    [Fact]
    public async Task Byte_range_locks_grant_deny_cancel_and_release()
    {
        // T-LOCK-13 (EX-RANGE)
        string path = NewFile("locks.bin", 4096);
        using WindowsFile a = OpenOk(path, Admission.SharedReadWrite);
        using WindowsFile b = OpenOk(path, Admission.SharedReadWrite);
        LockReceipt held = a.Lock(100, 10, exclusive: true, failImmediately: true);
        Assert.Equal(LockOutcome.Granted, held.Outcome);
        Assert.Equal(LockOutcome.Denied, b.Lock(105, 1, exclusive: true, failImmediately: true).Outcome);

        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        LockReceipt waited = await Task.Run(() => b.Lock(100, 10, exclusive: true, failImmediately: false, cancel.Token));
        Assert.Equal(LockOutcome.Cancelled, waited.Outcome);

        Assert.True(a.Unlock(held));
        LockReceipt granted = b.Lock(100, 10, exclusive: true, failImmediately: true);
        Assert.Equal(LockOutcome.Granted, granted.Outcome);
        Assert.True(b.Unlock(granted));
    }

    [Fact]
    public async Task Close_waits_for_outstanding_requests_and_then_refuses_new_ones()
    {
        string path = NewFile("close.bin", 64 * 4096);
        WindowsFile f = OpenOk(path, Admission.ReaderOnly);
        var receipts = new List<TransferReceipt>();
        var reader = Task.Run(() =>
        {
            var buffer = new byte[4096];
            for (int i = 0; i < 2000; i++)
            {
                TransferReceipt r = f.ReadAt((ulong)(i % 64) * 4096, buffer);
                lock (receipts)
                {
                    receipts.Add(r);
                }
                if (r.Status == TransferStatus.Failed)
                {
                    break;
                }
            }
        });
        await Task.Delay(5);
        f.Dispose();
        await reader.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.All(receipts, r => Assert.True(r.Status is TransferStatus.Complete or TransferStatus.Failed));
        Assert.Equal(TransferStatus.Failed, f.ReadAt(0, new byte[16]).Status);
    }

    [Fact]
    public void Extent_and_flush_are_explicit_operations()
    {
        string path = NewFile("extent.bin", 4096);
        using WindowsFile f = OpenOk(path, Admission.Exclusive);
        Assert.True(f.SetExtent(3 * 4096).Succeeded);
        Assert.Equal(3UL * 4096, f.GetExtent());
        Assert.True(f.Flush().Succeeded);
        using WindowsFile reader = OpenOk(NewFile("ro.bin", 4096), Admission.ReaderOnly);
        FileOperationResult denied = reader.SetExtent(0);
        Assert.False(denied.Succeeded);
        Assert.NotNull(denied.NativeError);
    }

    [Fact]
    public void CreateFresh_creates_exclusively_and_refuses_existing_destination_untouched()
    {
        // T-LOCK-67 / R-LOCK-1 EX-CREATE: CREATE_NEW refuses an existing file (Win32 80) without touching its bytes,
        // and a freshly created Exclusive handle denies concurrent opens.
        string existing = NewFile("existing.accdb", 128);
        byte[] before = File.ReadAllBytes(existing);

        OpenResult collision = WindowsFile.CreateFresh(existing, Admission.Exclusive);
        Assert.False(collision.Succeeded);
        Assert.True(collision.AlreadyExists);
        Assert.Equal(before, File.ReadAllBytes(existing));

        string fresh = Path.Combine(_dir, "fresh.accdb");
        OpenResult created = WindowsFile.CreateFresh(fresh, Admission.Exclusive);
        Assert.True(created.Succeeded, $"CreateFresh failed with {created.NativeError}");
        using (WindowsFile handle = created.File!)
        {
            Assert.Equal(Admission.Exclusive, handle.Admission);
            OpenResult concurrentReader = WindowsFile.Open(fresh, Admission.ReaderOnly);
            Assert.False(concurrentReader.Succeeded);
            Assert.True(concurrentReader.SharingViolation);
        }

        string missingDir = Path.Combine(_dir, "no-such-dir", "child.accdb");
        OpenResult badPath = WindowsFile.CreateFresh(missingDir, Admission.Exclusive);
        Assert.False(badPath.Succeeded);
        Assert.True(badPath.PathNotFound);
    }
}
