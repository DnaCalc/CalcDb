using CalcDb.Storage.Windows;

namespace CalcDb.Storage.Tests;

/// <summary>W-03/private-images-coherence, live spill (II-03 §5.2, T-PAGER-11).</summary>
public sealed class SpillTests
{
    private const int PageSize = 4096;
    private static readonly LockerId A = new(1);

    [Fact]
    public void T_PAGER_11_a_spill_that_fills_the_disk_keeps_the_resident_image_authoritative()
    {
        var disk = new MemoryFile { Capacity = SpillRecordFormat.HeaderSize + 1000 }; // the payload fails after a prefix
        var spill = new PositionedSpillStore(disk, PageSize);
        var file = new ControlledFile(PageSize);
        var cache = new PageCache(file, 1, PageSize, 3, spill: spill);
        PrivateOverlay a = cache.CreateOverlay(A);
        ImageId revision = a.Edit(1, new BytePatch(10, [0xEE])).Image!.Value;

        SpillWriteResult failed = cache.Spill(revision);
        Assert.False(failed.Succeeded);
        Assert.Contains("Failed", failed.Failure);
        Assert.True(cache.IsResident(revision));
        Assert.False(cache.IsSpilled(revision));
        Assert.Equal(0, spill.LiveRecords);
        Conservation.Conserved(cache);

        // Under pressure the failed spill is an explicit outcome; the private image is never freed or written.
        Assert.True(a.Edit(2, new BytePatch(10, [0xDD])).Succeeded); // frames: revision 1, revision 2, base 2 reclaimable
        PinResult blocked = cache.Pin(9);
        PinResult second = cache.Pin(10);
        Assert.True(blocked.Succeeded); // the base of page 2 was reclaimable
        Assert.Equal(PinFailureKind.NoFrame, second.Failure);
        Assert.Contains("could not be spilled", second.Detail);
        blocked.Pin!.Dispose();
        using (PagePin mine = a.Pin(1).Pin!)
        {
            Assert.Equal(revision, mine.Image);
            Assert.Equal(0xEE, mine.Bytes[10]);
            Assert.Equal(ControlledFile.Marker(1), mine.Bytes[11]);
        }
        Assert.Equal(0, file.Writes);
        Assert.Equal(2, cache.Counters.SpillFailures);
        a.Dispose();
        Conservation.Settled(cache, spill);
    }

    [Fact]
    public void T_PAGER_11_a_damaged_spill_record_never_supplies_a_pin()
    {
        var disk = new MemoryFile();
        var spill = new PositionedSpillStore(disk, PageSize);
        var file = new ControlledFile(PageSize);
        var cache = new PageCache(file, 1, PageSize, 2, spill: spill);
        PrivateOverlay a = cache.CreateOverlay(A);
        ImageId revision = a.Edit(1, new BytePatch(10, [0xEE])).Image!.Value;
        Assert.True(cache.Spill(revision).Succeeded);
        Assert.True(cache.IsSpilled(revision));
        PagePin five = cache.Pin(5).Pin!;
        PagePin six = cache.Pin(6).Pin!; // with 5 held, the spilled revision's frame is reclaimed: it has a reload source
        five.Dispose();
        six.Dispose();
        Assert.False(cache.IsResident(revision));

        disk.Damage(SpillRecordFormat.HeaderSize + 10); // the record's payload, not its header
        PinResult damaged = a.Pin(1);
        Assert.Equal(PinFailureKind.LoadFailed, damaged.Failure);
        Assert.Contains("Damaged", damaged.Detail);
        Assert.False(cache.IsResident(revision));
        Assert.Equal(1, cache.Counters.SpillRejects);
        Conservation.Quiescent(cache);

        disk.Damage(SpillRecordFormat.HeaderSize + 10); // restored bytes validate again
        using (PagePin restored = a.Pin(1).Pin!)
        {
            Assert.Equal(0xEE, restored.Bytes[10]);
        }
        a.Dispose(); // releases the spill record
        Assert.Equal(0, file.Writes);
        Conservation.Settled(cache, spill);
    }

    [Fact]
    public void A_record_that_names_another_image_is_refused()
    {
        var spill = new PositionedSpillStore(new MemoryFile(), PageSize);
        var image = new ImageId(new PageObject(1, 17, 0), 5);
        byte[] payload = new byte[PageSize];
        SpillLocator locator = spill.Write(new SpillRecordIdentity(1, image, SpillRecordIdentity.StoredPage), payload).Locator!.Value;
        byte[] into = new byte[PageSize];
        Assert.Equal(SpillReadStatus.Valid, spill.Read(locator, new SpillRecordIdentity(1, image, SpillRecordIdentity.StoredPage), into).Status);
        Assert.Equal(SpillReadStatus.IdentityMismatch, spill.Read(locator, new SpillRecordIdentity(1, image with { Serial = 6 }, SpillRecordIdentity.StoredPage), into).Status);
        Assert.Equal(SpillReadStatus.IdentityMismatch, spill.Read(locator, new SpillRecordIdentity(2, image, SpillRecordIdentity.StoredPage), into).Status);
        spill.Release(locator);
        Assert.Equal(SpillReadStatus.UnknownLocator, spill.Read(locator, new SpillRecordIdentity(1, image, SpillRecordIdentity.StoredPage), into).Status);
    }

    [Fact]
    public void Private_edits_survive_pressure_through_spill_with_no_native_write()
    {
        // Slice "Private overlay" (II-03 §8): edits survive pressure and rollback; eviction performs no native write.
        var spill = new PositionedSpillStore(new MemoryFile(), PageSize);
        var file = new ControlledFile(PageSize);
        var cache = new PageCache(file, 1, PageSize, 3, spill: spill);
        PrivateOverlay a = cache.CreateOverlay(A);
        for (ulong page = 1; page <= 6; page++)
        {
            Assert.True(a.Edit(page, new BytePatch(0, [(byte)(0xA0 + page)])).Succeeded);
        }
        Assert.True(cache.Counters.SpillWrites >= 3);
        a.BeginScope();
        Assert.True(a.Edit(1, new BytePatch(1, [0x99])).Succeeded); // the scope-before image may itself be spilled
        Conservation.Quiescent(cache);
        for (ulong page = 1; page <= 6; page++)
        {
            using PagePin pin = a.Pin(page).Pin!;
            Assert.Equal((byte)(0xA0 + page), pin.Bytes[0]);
            Assert.Equal(page == 1 ? 0x99 : ControlledFile.Marker(page), pin.Bytes[1]);
        }
        a.RollbackScope();
        using (PagePin pin = a.Pin(1).Pin!)
        {
            Assert.Equal(0xA1, pin.Bytes[0]);
            Assert.Equal(ControlledFile.Marker(1), pin.Bytes[1]);
        }
        Assert.True(cache.Counters.SpillLoads > 0);
        Assert.Equal(0, file.Writes);
        a.Dispose();
        Conservation.Settled(cache, spill);
    }

    [Fact]
    public void A_native_temporary_file_holds_spilled_images()
    {
        using TemporarySpillFile temp = TemporarySpillFile.Create(PageSize);
        var file = new ControlledFile(PageSize);
        var cache = new PageCache(file, 1, PageSize, 2, spill: temp.Store);
        PrivateOverlay a = cache.CreateOverlay(A);
        ImageId revision = a.Edit(3, new BytePatch(4095, [0x5A])).Image!.Value;
        Assert.True(cache.Spill(revision).Succeeded);
        PagePin seven = cache.Pin(7).Pin!;
        PagePin eight = cache.Pin(8).Pin!;
        seven.Dispose();
        eight.Dispose();
        Assert.False(cache.IsResident(revision));
        using (PagePin back = a.Pin(3).Pin!)
        {
            Assert.Equal(0x5A, back.Bytes[4095]);
            Assert.Equal(ControlledFile.Marker(3), back.Bytes[0]);
        }
        a.Dispose();
        Conservation.Settled(cache, temp.Store);
        Assert.True(File.Exists(temp.Path));
    }
}
