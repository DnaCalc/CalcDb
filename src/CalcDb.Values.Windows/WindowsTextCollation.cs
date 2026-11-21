using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

using CalcDb.Values.Scalar;

namespace CalcDb.Values.Windows;

/// <summary>
/// Windows host implementation of <see cref="TextCollation"/> for <see cref="TextCompareMode.Text"/> (VBA
/// <c>Option Compare Text</c>, <c>InStr</c>/<c>StrComp</c> compare=1), backed by <c>kernel32!CompareStringW</c> with
/// <c>NORM_IGNORECASE | NORM_IGNOREKANATYPE | NORM_IGNOREWIDTH</c>. Splits on embedded <c>U+0000</c> with a
/// span-based loop so embedded NUL sorts after end-of-string (matching ACE's <c>VarBstrCmp</c>-based text compare on
/// <c>r-expr-1b/str-strcomp-and-text-operators</c>, <c>r-expr-1b/str-instr-compare-modes</c>,
/// <c>r-sort-3b/lg-compare-ops</c>, <c>r-sort-3b/memo-compare-ops</c>, and <c>r-def-expr-sql/str-stored-instr-strcomp</c>)
/// without allocating per call.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsTextCollation : TextCollation
{
    private const uint NormIgnoreCase = 0x00000001u;
    private const uint NormIgnoreKanaType = 0x00010000u;
    private const uint NormIgnoreWidth = 0x00020000u;
    private const uint TextFlags = NormIgnoreCase | NormIgnoreKanaType | NormIgnoreWidth;

    private static readonly ConcurrentDictionary<uint, WindowsTextCollation> Cache = new();

    private WindowsTextCollation(uint lcid)
    {
        Lcid = lcid;
        Identity = "win-text/" + lcid.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>The Windows LCID this collation compares under.</summary>
    public uint Lcid { get; }

    public override string Identity { get; }

    /// <summary>The cached Windows Text collation for <paramref name="lcid"/>.</summary>
    public static WindowsTextCollation For(uint lcid) => Cache.GetOrAdd(lcid, static id => new WindowsTextCollation(id));

    public override unsafe int Compare(ReadOnlySpan<char> left, ReadOnlySpan<char> right)
    {
        char empty = '\0';
        while (true)
        {
            int nl = left.IndexOf('\0');
            int nr = right.IndexOf('\0');
            ReadOnlySpan<char> segL = nl < 0 ? left : left[..nl];
            ReadOnlySpan<char> segR = nr < 0 ? right : right[..nr];
            int cmp;
            if (segL.IsEmpty && segR.IsEmpty)
            {
                cmp = 0;
            }
            else
            {
                int raw;
                fixed (char* pl = segL)
                fixed (char* pr = segR)
                {
                    raw = Kernel32.CompareStringW(
                        Lcid,
                        TextFlags,
                        segL.IsEmpty ? &empty : pl,
                        segL.Length,
                        segR.IsEmpty ? &empty : pr,
                        segR.Length);
                }
                if (raw == 0)
                {
                    throw new InvalidOperationException($"CompareStringW failed for LCID {Lcid}.");
                }
                cmp = raw - 2; // CSTR_LESS_THAN=1, CSTR_EQUAL=2, CSTR_GREATER_THAN=3
            }
            if (cmp != 0)
            {
                return cmp < 0 ? -1 : 1;
            }
            if (nl < 0 && nr < 0)
            {
                return 0;
            }
            if (nl < 0)
            {
                return -1;
            }
            if (nr < 0)
            {
                return 1;
            }
            left = left[(nl + 1)..];
            right = right[(nr + 1)..];
        }
    }
}

[SupportedOSPlatform("windows")]
internal static unsafe partial class Kernel32
{
    [LibraryImport("kernel32.dll", EntryPoint = "CompareStringW")]
    public static partial int CompareStringW(
        uint locale,
        uint dwCmpFlags,
        char* lpString1,
        int cchCount1,
        char* lpString2,
        int cchCount2);
}
