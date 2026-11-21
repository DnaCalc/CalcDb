using System.Buffers.Binary;

namespace CalcDb.Storage.Tests;

/// <summary>W-03/private-images-coherence, slice "Private overlay" (II-03 §5.1, §8; II-09 §23).</summary>
public sealed class PrivateOverlayTests
{
    private const int PageSize = 4096;
    private static readonly LockerId A = new(1);
    private static readonly LockerId B = new(2);

    [Fact]
    public void The_first_edit_copies_the_complete_page_and_unknown_bytes_survive()
    {
        var file = new ControlledFile(PageSize);
        byte[] page = new byte[PageSize];
        Random.Shared.NextBytes(page); // bytes the pager does not understand
        file.Set(17, page);
        var cache = new PageCache(file, 1, PageSize, 8);
        using (PrivateOverlay a = cache.CreateOverlay(A))
        {
            EditResult edit = a.Edit(17, new BytePatch(0x0FFB, [9, 0, 0, 0]));
            Assert.True(edit.Succeeded);
            using (PagePin mine = a.Pin(17).Pin!)
            {
                byte[] expected = page.ToArray();
                new byte[] { 9, 0, 0, 0 }.CopyTo(expected, 0x0FFB);
                Assert.True(mine.Bytes.SequenceEqual(expected));
                Assert.NotEqual(cache.CurrentImage(17), mine.Image);
            }
            using (PagePin committed = cache.Pin(17).Pin!)
            {
                Assert.True(committed.Bytes.SequenceEqual(page)); // the committed image is untouched
            }
            Conservation.Quiescent(cache);
        }
        Assert.Equal(0, file.Writes);
        Conservation.Settled(cache);
    }

    [Fact]
    public void A_pinned_revision_is_sealed_and_the_next_edit_makes_a_new_revision()
    {
        // II-03 §5.1: a private image exposed to a reader becomes immutable.
        var file = new ControlledFile(PageSize);
        var cache = new PageCache(file, 1, PageSize, 8);
        using PrivateOverlay a = cache.CreateOverlay(A);
        ImageId first = a.Edit(17, new BytePatch(10, [1])).Image!.Value;
        Assert.Equal(first, a.Edit(17, new BytePatch(11, [2])).Image); // unsealed and unshared: mutated in place
        using PagePin reader = a.Pin(17).Pin!;
        ImageId second = a.Edit(17, new BytePatch(12, [3])).Image!.Value;
        Assert.NotEqual(first, second);
        Assert.Equal(ControlledFile.Marker(17), reader.Bytes[12]); // the reader's sealed bytes did not change
        Assert.Equal(1, reader.Bytes[10]);
        reader.Dispose();
        a.Rollback();
        Conservation.Settled(cache);
    }

    [Fact]
    public void Rolling_back_a_scope_restores_its_before_images_and_keeps_the_outer_scope()
    {
        // II-03 §5.1 scope-before images; nested scopes commit into their parent.
        var file = new ControlledFile(PageSize);
        var cache = new PageCache(file, 1, PageSize, 12);
        using PrivateOverlay a = cache.CreateOverlay(A);
        Assert.True(a.Edit(17, new BytePatch(100, [0x11])).Succeeded);
        Assert.Equal(1, a.BeginScope());
        Assert.True(a.Edit(17, new BytePatch(101, [0x22])).Succeeded);
        Assert.True(a.Edit(18, new BytePatch(100, [0x33])).Succeeded); // first touched in the inner scope
        Conservation.Conserved(cache);
        a.RollbackScope();
        Assert.Equal(0, a.Depth);
        using (PagePin p17 = a.Pin(17).Pin!)
        {
            Assert.Equal(0x11, p17.Bytes[100]);
            Assert.Equal(ControlledFile.Marker(17), p17.Bytes[101]);
        }
        Assert.Null(a.PrivateImage(18));
        using (PagePin p18 = a.Pin(18).Pin!)
        {
            Assert.Equal(ControlledFile.Marker(18), p18.Bytes[100]);
        }

        a.BeginScope();
        a.Edit(17, new BytePatch(101, [0x44]));
        a.BeginScope();
        a.Edit(17, new BytePatch(102, [0x55]));
        a.CommitScope(); // depth 2 into depth 1: its before image is not needed by depth 1, which has its own
        a.RollbackScope(); // depth 1 removes both
        using (PagePin p17 = a.Pin(17).Pin!)
        {
            Assert.Equal(0x11, p17.Bytes[100]);
            Assert.Equal(ControlledFile.Marker(17), p17.Bytes[101]);
            Assert.Equal(ControlledFile.Marker(17), p17.Bytes[102]);
        }
        a.BeginScope();
        a.Edit(17, new BytePatch(103, [0x66]));
        a.CommitScope(); // into the transaction scope
        Assert.True(cache.Publish(a, file).Succeeded);
        byte[] written = file.Page(17);
        Assert.Equal(new byte[] { 0x11, ControlledFile.Marker(17), ControlledFile.Marker(17), 0x66 }, written[100..104]);
        Assert.Equal(1, file.Writes);
        Conservation.Settled(cache);
    }

    [Fact]
    public void Two_lockers_have_separate_overlays_of_the_same_page()
    {
        // II-03 §5.1: the overlay belongs to a LockerId, not to the file session.
        var file = new ControlledFile(PageSize);
        var cache = new PageCache(file, 1, PageSize, 8);
        using PrivateOverlay a = cache.CreateOverlay(A);
        using PrivateOverlay b = cache.CreateOverlay(B);
        Assert.Throws<InvalidOperationException>(() => cache.CreateOverlay(A));
        a.Edit(17, new BytePatch(0, [0xAA]));
        b.Edit(17, new BytePatch(1, [0xBB]));
        Assert.NotEqual(a.PrivateImage(17), b.PrivateImage(17));
        using (PagePin pa = a.Pin(17).Pin!)
        using (PagePin pb = b.Pin(17).Pin!)
        {
            Assert.Equal(new byte[] { 0xAA, ControlledFile.Marker(17) }, pa.Bytes[..2].ToArray());
            Assert.Equal(new byte[] { ControlledFile.Marker(17), 0xBB }, pb.Bytes[..2].ToArray());
        }
        b.Rollback(); // removes only B's scope
        using (PagePin pa = a.Pin(17).Pin!)
        {
            Assert.Equal(0xAA, pa.Bytes[0]);
        }
        a.Dispose();
        b.Dispose();
        Assert.Equal(0, file.Writes);
        Conservation.Settled(cache);
    }

    /// <summary>
    /// T-PAGER-17 over F-76 (II-09 §23): two local lockers own separate overlays of page 17. A changes row 0 (7→9) and
    /// allocates page 80 (clears 01 at 0x0FCA); B changes row 1 (100→101) and allocates page 81 (clears 02). Every
    /// commit/rollback combination in both orders: the fresh-base rebase preserves both admitted deltas, and rollback
    /// removes only its own scope with no native write. Page/record conflict policy (TXN-LOCAL) is not the pager's.
    /// </summary>
    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, true)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public void T_PAGER_17_fresh_base_rebase_preserves_both_lockers_deltas(bool aCommits, bool bCommits, bool aFirst)
    {
        var file = new ControlledFile(PageSize);
        file.Set(17, F76Page());
        var cache = new PageCache(file, 1, PageSize, 12);
        PrivateOverlay a = cache.CreateOverlay(A);
        PrivateOverlay b = cache.CreateOverlay(B);
        Assert.True(a.Edit(17, new BytePatch(0x0FFB, Long(9))).Succeeded);
        Assert.True(a.Edit(17, new BitDelta(0x0FCA, 0, 0x01)).Succeeded);
        Assert.True(b.Edit(17, new BytePatch(0x0FF4, Long(101))).Succeeded);
        Assert.True(b.Edit(17, new BitDelta(0x0FCA, 0, 0x02)).Succeeded);

        // A's private candidate: only its own row changed (F-76 row 2).
        using (PagePin candidate = a.Pin(17).Pin!)
        {
            Assert.Equal(9, Row(candidate.Bytes, 0x0FFB));
            Assert.Equal(100, Row(candidate.Bytes, 0x0FF4));
            Assert.Equal(0xFE, candidate.Bytes[0x0FCA]);
        }

        (PrivateOverlay first, bool firstCommits, PrivateOverlay second, bool secondCommits) =
            aFirst ? (a, aCommits, b, bCommits) : (b, bCommits, a, aCommits);
        Finish(first, firstCommits);
        if (firstCommits)
        {
            // The second locker's private image is still built from P0; its view of the sibling row is unchanged.
            using PagePin stale = second.Pin(17).Pin!;
            Assert.Equal(aFirst ? 7 : 100, Row(stale.Bytes, aFirst ? 0x0FFB : 0x0FF4));
        }
        Finish(second, secondCommits);

        byte[] final = file.Page(17);
        Assert.Equal(aCommits ? 9 : 7, Row(final, 0x0FFB));
        Assert.Equal(bCommits ? 101 : 100, Row(final, 0x0FF4));
        Assert.Equal((byte)(0xFF & ~(aCommits ? 0x01 : 0) & ~(bCommits ? 0x02 : 0)), final[0x0FCA]);
        Assert.Equal((aCommits ? 1 : 0) + (bCommits ? 1 : 0), file.Writes);
        byte[] p0 = F76Page();
        for (int i = 0; i < PageSize; i++)
        {
            if (i is not (>= 0x0FF4 and < 0x0FF8) and not (>= 0x0FFB and < 0x0FFF) and not 0x0FCA)
            {
                Assert.Equal(p0[i], final[i]); // every other byte, known or not, is P0's
            }
        }
        using (PagePin current = cache.Pin(17).Pin!)
        {
            Assert.True(current.Bytes.SequenceEqual(final));
        }
        a.Dispose();
        b.Dispose();
        Conservation.Settled(cache);

        void Finish(PrivateOverlay overlay, bool commit)
        {
            if (commit)
            {
                Assert.True(cache.Publish(overlay, file).Succeeded);
            }
            else
            {
                overlay.Rollback();
            }
            Conservation.Quiescent(cache);
        }
    }

    [Fact]
    public void A_whole_page_edit_does_not_read_the_base()
    {
        var file = new ControlledFile(PageSize);
        var cache = new PageCache(file, 1, PageSize, 4);
        using PrivateOverlay a = cache.CreateOverlay(A);
        byte[] index = new byte[PageSize];
        index[0] = 0x04;
        Assert.True(a.Edit(17, new WholePage(index)).Succeeded);
        Assert.True(a.Edit(17, new BytePatch(1, [0x01])).Succeeded);
        Assert.Equal(0, file.Loads(17));
        Assert.True(cache.Publish(a, file).Succeeded);
        Assert.Equal(0, file.Loads(17)); // nobody selected the old image: nothing to preserve
        Assert.Equal(new byte[] { 0x04, 0x01, 0x00 }, file.Page(17)[..3]);
        Conservation.Settled(cache);
    }

    [Fact]
    public void Nested_scope_promotion_and_rollback_preserve_delta_boundaries_and_conservation()
    {
        var file = new ControlledFile(PageSize);
        var cache = new PageCache(file, 1, PageSize, 16);
        using PrivateOverlay a = cache.CreateOverlay(A);

        Assert.True(a.Edit(17, new BytePatch(100, [0x11])).Succeeded);

        // Depth 1 touches page 20, skips page 17 and page 19.
        Assert.Equal(1, a.BeginScope());
        Assert.True(a.Edit(20, new BytePatch(0, [0xA1])).Succeeded);

        // Depth 2 edits existing page 17, creates page 19, and edits depth-1 page 20.
        Assert.Equal(2, a.BeginScope());
        Assert.True(a.Edit(17, new BytePatch(101, [0x22])).Succeeded);
        Assert.True(a.Edit(17, new BytePatch(102, [0x23])).Succeeded);
        Assert.True(a.Edit(19, new BytePatch(10, [0x91])).Succeeded);
        Assert.True(a.Edit(20, new BytePatch(1, [0xA2])).Succeeded);

        // Empty nested scopes commit and roll back cleanly.
        Assert.Equal(3, a.BeginScope());
        a.CommitScope();
        Assert.Equal(3, a.BeginScope());
        a.RollbackScope();
        Conservation.Conserved(cache);

        // Commit depth 2 -> depth 1: promotes pages 17 and 19 into depth 1, drops depth 2's before for page 20.
        a.CommitScope();
        Assert.Equal(1, a.Depth);
        Conservation.Conserved(cache);

        // Further edits in depth 1 reuse the promoted ScopeBefore entries without duplicating them.
        Assert.True(a.Edit(17, new BytePatch(103, [0x34])).Succeeded);
        Assert.True(a.Edit(19, new BytePatch(11, [0x92])).Succeeded);
        Conservation.Conserved(cache);

        // Second depth-2 scope edits already-tracked pages 17 and 19, then commits into depth 1.
        Assert.Equal(2, a.BeginScope());
        Assert.True(a.Edit(17, new BytePatch(104, [0x45])).Succeeded);
        Assert.True(a.Edit(19, new BytePatch(12, [0x93])).Succeeded);
        a.CommitScope();
        Conservation.Conserved(cache);

        // Rolling back depth 1 must drop all depth-1 and promoted depth-2 deltas on page 17, and remove pages 19 and 20.
        a.RollbackScope();
        Assert.Equal(0, a.Depth);
        Conservation.Conserved(cache);
        Assert.Null(a.PrivateImage(19));
        Assert.Null(a.PrivateImage(20));

        // Page 20 created in depth 1, edited in depth 2, depth 2 rolled back, depth 1 committed to transaction scope.
        Assert.Equal(1, a.BeginScope());
        Assert.True(a.Edit(20, new BytePatch(0, [0xB1])).Succeeded);
        Assert.Equal(2, a.BeginScope());
        Assert.True(a.Edit(20, new BytePatch(1, [0xB2])).Succeeded);
        a.RollbackScope();
        Conservation.Conserved(cache);
        a.CommitScope();
        Conservation.Conserved(cache);

        Assert.True(a.Edit(17, new BytePatch(105, [0x56])).Succeeded);
        Assert.True(cache.Publish(a, file).Succeeded);

        byte m17 = ControlledFile.Marker(17);
        Assert.Equal(new byte[] { 0x11, m17, m17, m17, m17, 0x56 }, file.Page(17)[100..106]);
        Assert.Equal(ControlledFile.Marker(19), file.Page(19)[10]);
        Assert.Equal(new byte[] { 0xB1, ControlledFile.Marker(20) }, file.Page(20)[..2]);
        Conservation.Settled(cache);
    }

    [Fact]
    public void Failed_edit_inside_nested_scope_and_overlay_rollback_conserve_owners()
    {
        var file = new ControlledFile(PageSize);
        var cache = new PageCache(file, 1, PageSize, 2);
        using PrivateOverlay a = cache.CreateOverlay(A);

        Assert.True(a.Edit(17, new BytePatch(0, [0xAA])).Succeeded);

        // Path A: NoFrame inside depth 1 (twice on the same page), then RollbackScope.
        a.BeginScope();
        using (PagePin blocker = cache.Pin(18).Pin!)
        {
            EditResult fail1 = a.Edit(17, new BytePatch(1, [0xBB]));
            Assert.False(fail1.Succeeded);
            Assert.Equal(PinFailureKind.NoFrame, fail1.Failure);
            Conservation.Conserved(cache);

            EditResult fail2 = a.Edit(17, new BytePatch(2, [0xCC]));
            Assert.False(fail2.Succeeded);
            Assert.Equal(PinFailureKind.NoFrame, fail2.Failure);
            Conservation.Conserved(cache);
        }
        a.RollbackScope();
        Conservation.Conserved(cache);

        // Path B: NoFrame inside depth 1, then CommitScope into transaction scope.
        a.BeginScope();
        using (PagePin blocker = cache.Pin(18).Pin!)
        {
            Assert.Equal(PinFailureKind.NoFrame, a.Edit(17, new BytePatch(1, [0xBB])).Failure);
            Conservation.Conserved(cache);
        }
        a.CommitScope();
        Conservation.Conserved(cache);

        // Path C: NoFrame inside depth 2, promoted to depth 1, then full overlay.Rollback() with open scope.
        a.BeginScope();
        a.BeginScope();
        using (PagePin blocker = cache.Pin(18).Pin!)
        {
            Assert.Equal(PinFailureKind.NoFrame, a.Edit(17, new BytePatch(1, [0xBB])).Failure);
            Conservation.Conserved(cache);
        }
        a.CommitScope();
        Assert.Equal(1, a.Depth);
        Conservation.Conserved(cache);

        a.Rollback();
        Assert.Equal(0, a.Depth);
        Conservation.Settled(cache);
    }

    /// <summary>
    /// F-76's constructed 4096-byte data page: owner 0x20, two slots naming 0x0FF9 and 0x0FF2, free space 4064, each row
    /// one non-null Long (7 and 100); a global declaration byte at 0x0FCA starts at FF.
    /// </summary>
    internal static byte[] F76Page()
    {
        byte[] page = new byte[PageSize];
        page[0] = 0x01;
        page[1] = 0x01;
        BinaryPrimitives.WriteUInt16LittleEndian(page.AsSpan(2), 4064);
        BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(4), 0x20);
        BinaryPrimitives.WriteUInt16LittleEndian(page.AsSpan(12), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(page.AsSpan(14), 0x0FF9);
        BinaryPrimitives.WriteUInt16LittleEndian(page.AsSpan(16), 0x0FF2);
        byte[] row0 = [0x01, 0x00, 0x07, 0x00, 0x00, 0x00, 0x01];
        byte[] row1 = [0x01, 0x00, 0x64, 0x00, 0x00, 0x00, 0x01];
        row0.CopyTo(page, 0x0FF9);
        row1.CopyTo(page, 0x0FF2);
        page[0x0FCA] = 0xFF;
        return page;
    }

    private static byte[] Long(int value)
    {
        byte[] bytes = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        return bytes;
    }

    private static int Row(ReadOnlySpan<byte> page, int payload) => BinaryPrimitives.ReadInt32LittleEndian(page[payload..]);
}
