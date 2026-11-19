using CalcDb.Format;

namespace CalcDb.Storage.Tests;

/// <summary>T-PAGER-14 (II-03 §1.2, §6.1): the stored and decoded domains of the masked page 0 stay distinct.</summary>
public sealed class HeaderDomainTests
{
    private const int PageSize = 4096;
    private static readonly int MaskStart = FormatDescriptor.Jet4Family.Header.MaskStart;
    private static readonly byte[] Mask = HeaderMask.Bytes(128);

    [Fact]
    public void T_PAGER_14_stored_A5_decodes_to_10_and_fingerprints_and_writes_use_the_stored_bytes()
    {
        Assert.Equal(0x18, MaskStart);
        Assert.Equal(0xB5, Mask[0]);
        byte[] stored = new byte[PageSize];
        Random.Shared.NextBytes(stored);
        stored[MaskStart] = 0xA5;
        var file = new ControlledFile(PageSize);
        file.Set(0, stored);
        var cache = new PageCache(file, 1, PageSize, 6, codec: new MaskedRangeCodec(0, MaskStart, Mask));

        byte[] decoded = new byte[PageSize];
        using (PagePin pin = cache.Pin(0).Pin!)
        {
            cache.Decode(pin, decoded);
            Assert.Equal(0xA5, pin.Bytes[MaskStart]); // the shared frame is never masked in place
            Assert.Equal(0x10, decoded[MaskStart]);
            Assert.Equal(PageFingerprint.OfStored(stored), PageFingerprint.OfStored(pin.Bytes));
            Assert.NotEqual(PageFingerprint.OfStored(decoded), PageFingerprint.OfStored(pin.Bytes));
        }

        // A delta is accepted in the decoded domain: decoded byte 0x40 becomes 0x77, and a map bit is set at 0x41.
        PrivateOverlay a = cache.CreateOverlay(new LockerId(1));
        Assert.True(a.Edit(0, new BytePatch(0x40, [0x77])).Succeeded);
        Assert.True(a.Edit(0, new BitDelta(0x41, 0x80, 0)).Succeeded);
        byte expected41 = (byte)(decoded[0x41] | 0x80);
        using (PagePin mine = a.Pin(0).Pin!)
        {
            Assert.Equal(0xA5, mine.Bytes[MaskStart]);
            Assert.Equal((byte)(0x77 ^ Mask[0x40 - MaskStart]), mine.Bytes[0x40]);
        }

        Assert.True(cache.Publish(a, file).Succeeded);
        byte[] written = file.Page(0);
        // The write encoder submitted stored bytes: the unchanged A5, the masked new values, every other byte as stored.
        Assert.Equal(0xA5, written[MaskStart]);
        Assert.Equal((byte)(0x77 ^ Mask[0x40 - MaskStart]), written[0x40]);
        Assert.Equal((byte)(expected41 ^ Mask[0x41 - MaskStart]), written[0x41]);
        for (int i = 0; i < PageSize; i++)
        {
            if (i is not 0x40 and not 0x41)
            {
                Assert.Equal(stored[i], written[i]);
            }
        }
        using (PagePin now = cache.Pin(0).Pin!)
        {
            Assert.Equal(PageFingerprint.OfStored(written), PageFingerprint.OfStored(now.Bytes));
            byte[] nowDecoded = new byte[PageSize];
            cache.Decode(now, nowDecoded);
            Assert.Equal(0x10, nowDecoded[MaskStart]);
            Assert.Equal(0x77, nowDecoded[0x40]);
            Assert.Equal(expected41, nowDecoded[0x41]);
        }
        a.Dispose();
        Conservation.Settled(cache);
    }

    [Fact]
    public void An_identity_page_has_one_domain()
    {
        var codec = new MaskedRangeCodec(0, MaskStart, Mask);
        byte[] page = new byte[PageSize];
        page[MaskStart] = 0xA5;
        byte[] decoded = new byte[PageSize];
        codec.Decode(17, page, decoded);
        Assert.Equal(page, decoded);
        Assert.False(codec.Transforms(17));
        Assert.Throws<ArgumentException>(() => codec.Decode(0, page, page));
    }
}
