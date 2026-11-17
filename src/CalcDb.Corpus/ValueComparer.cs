using System.Globalization;
using System.Text;
using CalcDb.Evidence;

namespace CalcDb.Corpus;

/// <summary>
/// Exact typed comparison of captured values (II-15 §12 F-192, T-EVID-12/19/33). Equality uses the authoritative
/// fields only: tag, exact integer text, raw bits, exact bytes and length, Decimal fields, array shape and elements.
/// The <c>Text</c> and <c>Display</c> conveniences never decide. Object labels are capture-local, so they are compared
/// through one consistent bijection per comparison: independent labels do not differ, and a shared alias in one capture
/// that is two objects in the other is detected.
/// </summary>
public sealed class ValueComparer
{
    private readonly Dictionary<string, string> _forward = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _backward = new(StringComparer.Ordinal);

    /// <summary>The first difference between two values at <paramref name="path"/>, or null when they are equal.</summary>
    public Difference? Compare(CapturedValue? expected, CapturedValue? actual, string path)
    {
        if (expected is null || actual is null)
        {
            return expected is null && actual is null ? null : new Difference(path, Render(expected), Render(actual), "presence");
        }
        if (expected.Unsupported is not null || actual.Unsupported is not null)
        {
            return new Difference(path, Render(expected), Render(actual), "unsupported-capture");
        }
        if (expected.Vt != actual.Vt)
        {
            return new Difference(path, Render(expected), Render(actual), "tag");
        }
        if (!string.Equals(expected.Integer, actual.Integer, StringComparison.Ordinal))
        {
            return new Difference(path, Render(expected), Render(actual), "integer");
        }
        if (!string.Equals(expected.Bits, actual.Bits, StringComparison.Ordinal))
        {
            return new Difference(path, Render(expected), Render(actual), "bits");
        }
        if (expected.ByteLength != actual.ByteLength || !string.Equals(expected.Bytes, actual.Bytes, StringComparison.Ordinal))
        {
            return new Difference(path, Render(expected), Render(actual), "bytes");
        }
        if (expected.Decimal != actual.Decimal)
        {
            return new Difference(path, Render(expected), Render(actual), "decimal");
        }
        if (expected.Array is not null || actual.Array is not null)
        {
            Difference? array = CompareArray(expected.Array, actual.Array, path);
            if (array is not null)
            {
                return array;
            }
        }
        if (expected.Identity is not null || actual.Identity is not null)
        {
            return CompareIdentity(expected, actual, path);
        }
        return null;
    }

    private Difference? CompareArray(ArrayValue? e, ArrayValue? a, string path)
    {
        if (e is null || a is null)
        {
            return new Difference(path, e is null ? "no array" : "array", a is null ? "no array" : "array", "array-presence");
        }
        if (e.ElementVt != a.ElementVt)
        {
            return new Difference(path + ".elementVt", e.ElementVt.ToString(CultureInfo.InvariantCulture), a.ElementVt.ToString(CultureInfo.InvariantCulture), "array-type");
        }
        if (!e.Bounds.SequenceEqual(a.Bounds))
        {
            return new Difference(path + ".bounds", Bounds(e), Bounds(a), "array-bounds");
        }
        if (!string.Equals(e.Bytes, a.Bytes, StringComparison.Ordinal))
        {
            return new Difference(path + ".bytes", e.Bytes ?? "-", a.Bytes ?? "-", "array-bytes");
        }
        IReadOnlyList<CapturedValue> ee = e.Elements ?? [];
        IReadOnlyList<CapturedValue> ae = a.Elements ?? [];
        if (ee.Count != ae.Count)
        {
            return new Difference(path + ".elements", ee.Count.ToString(CultureInfo.InvariantCulture), ae.Count.ToString(CultureInfo.InvariantCulture), "array-count");
        }
        for (int i = 0; i < ee.Count; i++)
        {
            Difference? d = Compare(ee[i], ae[i], $"{path}[{i}]");
            if (d is not null)
            {
                return d;
            }
        }
        return null;
    }

    private Difference? CompareIdentity(CapturedValue e, CapturedValue a, string path)
    {
        if (e.Identity is null || a.Identity is null)
        {
            return new Difference(path, Render(e), Render(a), "identity-presence");
        }
        if (!string.Equals(e.TypeIid, a.TypeIid, StringComparison.Ordinal))
        {
            return new Difference(path + ".iid", e.TypeIid ?? "-", a.TypeIid ?? "-", "interface");
        }
        bool knownForward = _forward.TryGetValue(e.Identity, out string? mapped);
        bool knownBackward = _backward.TryGetValue(a.Identity, out string? mappedBack);
        if (!knownForward && !knownBackward)
        {
            _forward[e.Identity] = a.Identity;
            _backward[a.Identity] = e.Identity;
            return null;
        }
        if (knownForward && knownBackward && mapped == a.Identity && mappedBack == e.Identity)
        {
            return null;
        }
        return new Difference(path, $"object {e.Identity}", $"object {a.Identity}", "identity-graph");
    }

    private static string Bounds(ArrayValue a) => string.Join(",", a.Bounds.Select(b => $"{b.LowerBound}:{b.Count}"));

    /// <summary>A diagnostic rendering: tag plus the exact payload in hex or bits; never used for equality.</summary>
    public static string Render(CapturedValue? v)
    {
        if (v is null)
        {
            return "(none)";
        }
        var sb = new StringBuilder(v.VtName);
        if (v.Integer is not null) { sb.Append(' ').Append(v.Integer); }
        if (v.Bits is not null) { sb.Append(" bits=").Append(v.Bits); }
        if (v.Bytes is not null) { sb.Append(" len=").Append(v.ByteLength).Append(" bytes=").Append(v.Bytes.Length > 64 ? v.Bytes[..64] + "…" : v.Bytes); }
        if (v.Decimal is { } d) { sb.Append($" dec(scale={d.Scale},sign={d.Sign},hi={d.Hi32},lo={d.Lo64})"); }
        if (v.Identity is not null) { sb.Append(" obj ").Append(v.Identity); }
        if (v.Unsupported is not null) { sb.Append(" unsupported: ").Append(v.Unsupported); }
        return sb.ToString();
    }
}
