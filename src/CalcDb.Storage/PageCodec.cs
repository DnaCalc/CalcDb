using System.Security.Cryptography;

namespace CalcDb.Storage;

/// <summary>
/// The representation of a page in the file (II-03 §1.2, §6.1; T-PAGER-14). Frames, spill records, fingerprints and
/// native writes always carry the <b>stored</b> bytes; accepted deltas apply to the <b>decoded</b> bytes. For an
/// identity page the two are the same; for the masked page 0 they differ, and decoding always writes a separate owned
/// buffer: a pinned shared frame is never masked in place.
/// </summary>
public interface IPageCodec
{
    /// <summary>Whether <paramref name="pageNumber"/> has a decoded domain distinct from its stored bytes.</summary>
    bool Transforms(ulong pageNumber);

    void Decode(ulong pageNumber, ReadOnlySpan<byte> stored, Span<byte> decoded);

    void Encode(ulong pageNumber, ReadOnlySpan<byte> decoded, Span<byte> stored);
}

/// <summary>Every page is stored as decoded.</summary>
public sealed class IdentityPageCodec : IPageCodec
{
    public static IdentityPageCodec Instance { get; } = new();

    public bool Transforms(ulong pageNumber) => false;

    public void Decode(ulong pageNumber, ReadOnlySpan<byte> stored, Span<byte> decoded) => stored.CopyTo(decoded);

    public void Encode(ulong pageNumber, ReadOnlySpan<byte> decoded, Span<byte> stored) => decoded.CopyTo(stored);
}

/// <summary>
/// An XOR mask over <c>[start, start + mask.Length)</c> of one page: the page-0 header representation (II-03 §1.2).
/// XOR is its own inverse, so decode and encode are the same proven reversible transformation into a separate output.
/// The mask bytes come from the format profile (CalcDb.Format's <c>HeaderMask</c>); this type only applies them.
/// </summary>
public sealed class MaskedRangeCodec : IPageCodec
{
    private readonly ulong _page;
    private readonly int _start;
    private readonly byte[] _mask;

    public MaskedRangeCodec(ulong pageNumber, int start, ReadOnlySpan<byte> mask)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(start);
        _page = pageNumber;
        _start = start;
        _mask = mask.ToArray();
    }

    public bool Transforms(ulong pageNumber) => pageNumber == _page;

    public void Decode(ulong pageNumber, ReadOnlySpan<byte> stored, Span<byte> decoded) => Apply(pageNumber, stored, decoded);

    public void Encode(ulong pageNumber, ReadOnlySpan<byte> decoded, Span<byte> stored) => Apply(pageNumber, decoded, stored);

    private void Apply(ulong pageNumber, ReadOnlySpan<byte> input, Span<byte> output)
    {
        if (input.Overlaps(output))
        {
            throw new ArgumentException("A page is never transformed in place (II-03 §1.2).", nameof(output));
        }
        input.CopyTo(output);
        if (pageNumber != _page)
        {
            return;
        }
        int end = Math.Min(output.Length, _start + _mask.Length);
        for (int i = _start; i < end; i++)
        {
            output[i] ^= _mask[i - _start];
        }
    }
}

/// <summary>A fingerprint of a page image. It covers the stored bytes, never a decoded view (II-03 §1.2, T-PAGER-14).</summary>
public static class PageFingerprint
{
    public static string OfStored(ReadOnlySpan<byte> stored) => Convert.ToHexStringLower(SHA256.HashData(stored));
}
