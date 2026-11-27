using System.Globalization;
using CalcDb.Foundation;

namespace CalcDb.Values.Scalar;

/// <summary>
/// OLE Automation formatting, date decomposition/parsing, and locale string conversion helpers backed directly by
/// <see cref="OleAut"/> (oleaut32.dll + kernel32.dll; docs/work/oleaut32-usage.md).
/// Covers R-EXPR-1 Part C / EXPR-LOCALE: <c>Format</c>/<c>Format$</c>, <c>FormatNumber</c>/<c>FormatCurrency</c>/
/// <c>FormatPercent</c>/<c>FormatDateTime</c>, <c>WeekdayName</c>/<c>MonthName</c>, <c>VarUdateFromDate</c>/
/// <c>VarDateFromUdate</c>/<c>VarDateFromStr</c>, and <c>StrConv</c>.
/// </summary>
public static unsafe class ScalarFormat
{
    private const uint LocaleIDefaultAnsiCodePage = 0x00001004;
    private const uint LocaleIFirstDayOfWeek = 0x0000100C;
    private const uint LocaleIFirstWeekOfYear = 0x0000100D;
    private const uint LcidInstalled = 0x00000001;
    private const uint LcMapLowercase = 0x00000100;
    private const uint LcMapUppercase = 0x00000200;

    public const uint VarTimeValueOnly = 0x00000001;
    public const uint VarDateValueOnly = 0x00000002;

    /// <summary>Whether an HRESULT returned by <see cref="TryDateToUDate"/>/<see cref="TryUDateToDate"/> is DISP_E_OVERFLOW.</summary>
    public static bool IsOverflowHResult(int hr) => hr == OleAut.DISP_E_OVERFLOW;

    /// <summary>Map an oleaut32 VarFormat* HRESULT to a <see cref="ScalarOutcome"/> BSTR or ACE failure.</summary>
    private static ScalarOutcome MapFormatResult(in ScalarDescriptor d, int hr, nint bstr)
    {
        if (hr == OleAut.S_OK)
        {
            return ScalarOutcome.Of(new NativeVariant
            {
                Vt = (ushort)VarType.Bstr,
                Pointer = bstr,
            });
        }
        if (bstr != 0)
        {
            OleAut.SysFreeString(bstr);
        }
        return MapHResult(d, hr);
    }

    private static ScalarOutcome Fail(in ScalarDescriptor d, int number) =>
        ScalarOutcome.Fail(ScalarFailure.Ace(number, d.Site, d.Operation));

    private static ScalarOutcome Open(in ScalarDescriptor d, NotModelledCase @case) =>
        ScalarOutcome.Fail(ScalarFailure.Open(@case, d.Site, d.Operation));

    private static ScalarOutcome MapHResult(in ScalarDescriptor d, int hr) => hr switch
    {
        OleAut.NotAvailableOnPlatform => Open(d, NotModelledCase.UnmappedAutomationFailure),
        OleAut.DISP_E_OVERFLOW => Fail(d, AceError.Overflow),
        OleAut.DISP_E_TYPEMISMATCH => Fail(d, AceError.TypeMismatch),
        OleAut.DISP_E_DIVBYZERO => Fail(d, AceError.DivisionByZero),
        OleAut.E_INVALIDARG => Fail(d, AceError.InvalidProcedureCall),
        _ => Open(d, NotModelledCase.UnmappedAutomationFailure),
    };

    /// <summary>
    /// <c>Format</c> / <c>Format$</c> via oleaut32 <c>VarFormat</c> (r-expr-1b/fmt-date-arguments, fmt-date-patterns,
    /// fmt-named-and-text-formats, fmt-numeric-patterns; r-expr-locale; r-q47-bigint-expr-edges/locale-*, meta-*).
    /// </summary>
    public static ScalarOutcome Format(in ScalarDescriptor d, in NativeVariant input, string? format, int firstDay, int firstWeek)
    {
        NativeVariant copy = input;
        if (format is null)
        {
            int hr = OleAut.VarFormat(&copy, null, firstDay, firstWeek, 0, out nint bstr);
            return MapFormatResult(d, hr, bstr);
        }
        fixed (char* fmtPtr = format)
        {
            int hr = OleAut.VarFormat(&copy, fmtPtr, firstDay, firstWeek, 0, out nint bstr);
            return MapFormatResult(d, hr, bstr);
        }
    }

    /// <summary><c>FormatNumber</c> via oleaut32 <c>VarFormatNumber</c> (r-expr-1b/sandbox-vba-library, r-q47-bigint-expr-edges/fmt-formatnumber-tristate-edges-accdb).</summary>
    public static ScalarOutcome FormatNumber(in ScalarDescriptor d, in NativeVariant input, int numDig, int incLead, int useParens, int group)
    {
        NativeVariant copy = input;
        int hr = OleAut.VarFormatNumber(&copy, numDig, incLead, useParens, group, 0, out nint bstr);
        return MapFormatResult(d, hr, bstr);
    }

    /// <summary><c>FormatCurrency</c> via oleaut32 <c>VarFormatCurrency</c> (r-expr-1b/sandbox-vba-library, r-q47-bigint-expr-edges/fmt-formatnumber-tristate-edges-accdb).</summary>
    public static ScalarOutcome FormatCurrency(in ScalarDescriptor d, in NativeVariant input, int numDig, int incLead, int useParens, int group)
    {
        NativeVariant copy = input;
        int hr = OleAut.VarFormatCurrency(&copy, numDig, incLead, useParens, group, 0, out nint bstr);
        return MapFormatResult(d, hr, bstr);
    }

    /// <summary><c>FormatPercent</c> via oleaut32 <c>VarFormatPercent</c> (r-expr-1b/sandbox-vba-library, r-q47-bigint-expr-edges/fmt-formatnumber-tristate-edges-accdb).</summary>
    public static ScalarOutcome FormatPercent(in ScalarDescriptor d, in NativeVariant input, int numDig, int incLead, int useParens, int group)
    {
        NativeVariant copy = input;
        int hr = OleAut.VarFormatPercent(&copy, numDig, incLead, useParens, group, 0, out nint bstr);
        return MapFormatResult(d, hr, bstr);
    }

    /// <summary><c>FormatDateTime</c> via oleaut32 <c>VarFormatDateTime</c> (r-expr-1b/sandbox-vba-library).</summary>
    public static ScalarOutcome FormatDateTime(in ScalarDescriptor d, in NativeVariant input, int namedFormat)
    {
        NativeVariant copy = input;
        int hr = OleAut.VarFormatDateTime(&copy, namedFormat, 0, out nint bstr);
        return MapFormatResult(d, hr, bstr);
    }

    /// <summary><c>WeekdayName</c> via oleaut32 <c>VarWeekdayName</c> (r-expr-1b/date-names, r-q47-bigint-expr-edges/meta-*).</summary>
    public static ScalarOutcome WeekdayName(in ScalarDescriptor d, int weekday, bool abbrev, int firstDay)
    {
        int hr = OleAut.VarWeekdayName(weekday, abbrev ? 1 : 0, firstDay, 0, out nint bstr);
        return MapFormatResult(d, hr, bstr);
    }

    /// <summary><c>MonthName</c> via oleaut32 <c>VarMonthName</c> (r-expr-1b/date-names).</summary>
    public static ScalarOutcome MonthName(in ScalarDescriptor d, int month, bool abbrev)
    {
        int hr = OleAut.VarMonthName(month, abbrev ? 1 : 0, 0, out nint bstr);
        return MapFormatResult(d, hr, bstr);
    }

    /// <summary>Decompose an OLE Automation Date via oleaut32 <c>VarUdateFromDate</c> (r-expr-1b/date-*).</summary>
    public static bool TryDateToUDate(double oaDate, out OleUDate udate, out int hr)
    {
        hr = OleAut.VarUdateFromDate(oaDate, 0, out udate);
        return hr == OleAut.S_OK;
    }

    /// <summary>Compose an OLE Automation Date via oleaut32 <c>VarDateFromUdate</c> (r-expr-1b/date-*).</summary>
    public static bool TryUDateToDate(in OleUDate udate, out double oaDate, out int hr)
    {
        hr = OleAut.VarDateFromUdate(in udate, 0, out oaDate);
        return hr == OleAut.S_OK;
    }

    /// <summary>Parse a date/time string via oleaut32 <c>VarDateFromStr</c> under <paramref name="lcid"/> (r-expr-1b/date-component-functions).</summary>
    public static bool TryParseDateString(string text, uint lcid, uint flags, out double oaDate, out int hr)
    {
        fixed (char* ptr = text)
        {
            hr = OleAut.VarDateFromStr(ptr, lcid, flags, out oaDate);
            return hr == OleAut.S_OK;
        }
    }

    /// <summary>
    /// Resolve <c>vbUseSystemDayOfWeek</c> (0) to VBA FirstDayOfWeek (1=Sun..7=Sat) using <c>GetLocaleInfoW(LOCALE_IFIRSTDAYOFWEEK)</c>.
    /// Windows reports 0=Monday..6=Sunday; VBA maps 1=Sunday, 2=Monday..7=Saturday.
    /// </summary>
    public static int GetSystemFirstDayOfWeek(uint lcid)
    {
        uint effective = lcid != 0 ? lcid : OleAut.GetUserDefaultLCID();
        char* buf = stackalloc char[16];
        int len = OleAut.GetLocaleInfoW(effective, LocaleIFirstDayOfWeek, buf, 16);
        if (len > 1 && int.TryParse(new ReadOnlySpan<char>(buf, len - 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out int winDay) && winDay is >= 0 and <= 6)
        {
            return winDay == 6 ? 1 : winDay + 2;
        }
        return 1; // vbSunday fallback
    }

    /// <summary>
    /// Resolve <c>vbUseSystem</c> (0) to VBA FirstWeekOfYear (1=Jan1, 2=FirstFourDays, 3=FirstFullWeek) using <c>GetLocaleInfoW(LOCALE_IFIRSTWEEKOFYEAR)</c>.
    /// Windows reports 0=Jan1, 1=FirstFullWeek, 2=FirstFourDays.
    /// </summary>
    public static int GetSystemFirstWeekOfYear(uint lcid)
    {
        uint effective = lcid != 0 ? lcid : OleAut.GetUserDefaultLCID();
        char* buf = stackalloc char[16];
        int len = OleAut.GetLocaleInfoW(effective, LocaleIFirstWeekOfYear, buf, 16);
        if (len > 1 && int.TryParse(new ReadOnlySpan<char>(buf, len - 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out int winWeek))
        {
            return winWeek switch
            {
                0 => 1, // vbFirstJan1
                1 => 3, // vbFirstFullWeek
                2 => 2, // vbFirstFourDays
                _ => 1,
            };
        }
        return 1;
    }

    private static uint GetAnsiCodePage(uint lcid)
    {
        char* buf = stackalloc char[16];
        int len = OleAut.GetLocaleInfoW(lcid, LocaleIDefaultAnsiCodePage, buf, 16);
        if (len > 1 && uint.TryParse(new ReadOnlySpan<char>(buf, len - 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out uint cp) && cp != 0)
        {
            return cp;
        }
        return 0; // CP_ACP
    }

    /// <summary>
    /// <c>StrConv(string, conversion[, LCID])</c> (r-expr-1b/str-strconv-modes, r-def-expr-sql/str-concat-update-roundtrip,
    /// r-q47-bigint-expr-edges/locale-strconv-lcid-matrix-accdb).
    /// Admits modes 0 (none), 1 (vbUpperCase), 2 (vbLowerCase), 3 (vbProperCase), 64 (vbUnicode), and 128 (vbFromUnicode).
    /// Combined flags (5, 6, 7, 9, 10, 17, 20, 40, 65, 129, 192), DBCS/Kana flags (4, 8, 16, 32 on Western locale),
    /// negative/out-of-range flags, and invalid LCIDs fail error 5 (Invalid procedure call).
    /// </summary>
    public static ScalarOutcome StrConv(in ScalarDescriptor d, nint bstrInput, int conversion, uint requestedLcid, uint defaultLcid)
    {
        if (OleAut.Binding != OleAutBinding.Platform)
        {
            return Open(d, NotModelledCase.UnmappedAutomationFailure);
        }
        uint lcid = requestedLcid == 0 ? defaultLcid : requestedLcid;
        if (requestedLcid != 0 && !OleAut.IsValidLocale(requestedLcid, LcidInstalled))
        {
            return Fail(d, AceError.InvalidProcedureCall);
        }
        if (conversion is not (0 or 1 or 2 or 3 or 64 or 128))
        {
            return Fail(d, AceError.InvalidProcedureCall);
        }
        if (conversion == 0)
        {
            uint rawLen = OleAut.SysStringByteLen(bstrInput);
            nint copy = OleAut.SysAllocStringByteLen((byte*)bstrInput, rawLen);
            return ScalarOutcome.Of(new NativeVariant { Vt = (ushort)VarType.Bstr, Pointer = copy });
        }

        uint cp = GetAnsiCodePage(lcid);

        if (conversion == 64) // vbUnicode: raw BSTR bytes -> UTF-16 via ANSI code page
        {
            uint byteLen = OleAut.SysStringByteLen(bstrInput);
            if (byteLen == 0)
            {
                return ScalarOutcome.Of(new NativeVariant { Vt = (ushort)VarType.Bstr, Pointer = OleAut.SysAllocStringByteLen(null, 0) });
            }
            int wideLen = OleAut.MultiByteToWideChar(cp, 0, (byte*)bstrInput, (int)byteLen, null, 0);
            if (wideLen <= 0)
            {
                return Fail(d, AceError.InvalidProcedureCall);
            }
            nint result = OleAut.SysAllocStringByteLen(null, (uint)(wideLen * 2));
            _ = OleAut.MultiByteToWideChar(cp, 0, (byte*)bstrInput, (int)byteLen, (char*)result, wideLen);
            return ScalarOutcome.Of(new NativeVariant { Vt = (ushort)VarType.Bstr, Pointer = result });
        }

        ReadOnlySpan<char> chars = Bstrs.Chars(bstrInput);
        if (chars.Length == 0)
        {
            return ScalarOutcome.Of(new NativeVariant { Vt = (ushort)VarType.Bstr, Pointer = OleAut.SysAllocStringByteLen(null, 0) });
        }

        if (conversion == 128) // vbFromUnicode: UTF-16 -> ANSI bytes stored directly in BSTR (preserving odd byte length)
        {
            fixed (char* widePtr = chars)
            {
                int byteLen = OleAut.WideCharToMultiByte(cp, 0, widePtr, chars.Length, null, 0);
                if (byteLen <= 0)
                {
                    return Fail(d, AceError.InvalidProcedureCall);
                }
                nint result = OleAut.SysAllocStringByteLen(null, (uint)byteLen);
                _ = OleAut.WideCharToMultiByte(cp, 0, widePtr, chars.Length, (byte*)result, byteLen);
                return ScalarOutcome.Of(new NativeVariant { Vt = (ushort)VarType.Bstr, Pointer = result });
            }
        }

        // Modes 1 (Upper), 2 (Lower), 3 (ProperCase): match ACE's ANSI LCMapStringA pipeline (r-q47/locale-strconv-lcid-matrix-accdb:
        // LCID 1033 maps 'iIİı' -> 'IIII'/'iiii'; LCID 1055 maps 'iIİı' -> 'IIİı'/'iiİı'; LCID 1031 keeps 'ß' -> 'STRAßE').
        fixed (char* widePtr = chars)
        {
            int byteLen = OleAut.WideCharToMultiByte(cp, 0, widePtr, chars.Length, null, 0);
            if (byteLen <= 0)
            {
                return Fail(d, AceError.InvalidProcedureCall);
            }
            byte[] ansi = new byte[byteLen];
            fixed (byte* ansiPtr = ansi)
            {
                _ = OleAut.WideCharToMultiByte(cp, 0, widePtr, chars.Length, ansiPtr, byteLen);
                byte[] mapped;
                if (conversion is 1 or 2)
                {
                    uint mapFlags = conversion == 1 ? LcMapUppercase : LcMapLowercase;
                    int mappedLen = OleAut.LCMapStringA(lcid, mapFlags, ansiPtr, byteLen, null, 0);
                    if (mappedLen <= 0)
                    {
                        return Fail(d, AceError.InvalidProcedureCall);
                    }
                    mapped = new byte[mappedLen];
                    fixed (byte* outPtr = mapped)
                    {
                        _ = OleAut.LCMapStringA(lcid, mapFlags, ansiPtr, byteLen, outPtr, mappedLen);
                    }
                }
                else
                {
                    // vbProperCase (3): lowercase all bytes, then uppercase start of string and any byte after whitespace (0x00, 0x09..0x0D, 0x20).
                    int lowerLen = OleAut.LCMapStringA(lcid, LcMapLowercase, ansiPtr, byteLen, null, 0);
                    if (lowerLen <= 0)
                    {
                        return Fail(d, AceError.InvalidProcedureCall);
                    }
                    mapped = new byte[lowerLen];
                    fixed (byte* outPtr = mapped)
                    {
                        _ = OleAut.LCMapStringA(lcid, LcMapLowercase, ansiPtr, byteLen, outPtr, lowerLen);
                        bool atWordStart = true;
                        for (int i = 0; i < lowerLen; i++)
                        {
                            byte b = outPtr[i];
                            if (b is 0x00 or 0x09 or 0x0A or 0x0B or 0x0C or 0x0D or 0x20)
                            {
                                atWordStart = true;
                            }
                            else if (atWordStart)
                            {
                                byte upper = b;
                                _ = OleAut.LCMapStringA(lcid, LcMapUppercase, &b, 1, &upper, 1);
                                outPtr[i] = upper;
                                atWordStart = false;
                            }
                        }
                    }
                }

                fixed (byte* mappedPtr = mapped)
                {
                    int wideOutLen = OleAut.MultiByteToWideChar(cp, 0, mappedPtr, mapped.Length, null, 0);
                    if (wideOutLen <= 0)
                    {
                        return Fail(d, AceError.InvalidProcedureCall);
                    }
                    nint result = OleAut.SysAllocStringByteLen(null, (uint)(wideOutLen * 2));
                    _ = OleAut.MultiByteToWideChar(cp, 0, mappedPtr, mapped.Length, (char*)result, wideOutLen);
                    return ScalarOutcome.Of(new NativeVariant { Vt = (ushort)VarType.Bstr, Pointer = result });
                }
            }
        }
    }

    // Windows-1252 (ACP 1252) mapping for 0x80..0x9F (0xA0..0xFF is identity U+00A0..U+00FF).
    // Recorded in r-expr-1b/str-chr-asc-range (acp=1252: Chr(128) = '€', Chr(129) = U+0081, Chr(255) = 'ÿ', Asc('é') = 233).
    private static readonly char[] Cp1252High =
    [
        '\u20AC', '\u0081', '\u201A', '\u0192', '\u201E', '\u2026', '\u2020', '\u2021',
        '\u02C6', '\u2030', '\u0160', '\u2039', '\u0152', '\u008D', '\u017D', '\u008F',
        '\u0090', '\u2018', '\u2019', '\u201C', '\u201D', '\u2022', '\u2013', '\u2014',
        '\u02DC', '\u2122', '\u0161', '\u203A', '\u0153', '\u009D', '\u017E', '\u0178',
    ];

    /// <summary>
    /// Maps non-ASCII text for <c>UCase</c>/<c>LCase</c> via Windows <c>LCMapStringW</c> (<c>LCMAP_UPPERCASE</c> /
    /// <c>LCMAP_LOWERCASE</c>, without <c>LCMAP_LINGUISTIC_CASING</c>), matching <c>r-expr-1b/str-case-folding</c>
    /// (preserves embedded NULs, lone surrogates, 'ı', 'ς', 'µ', 'ſ', 'ﬁ', 'K', 'ẞ'; folds Deseret surrogates).
    /// Returns false when the platform library is unavailable so callers refuse as <see cref="NotModelledCase.NonAsciiCase"/>.
    /// </summary>
    public static bool TryMapCase(ReadOnlySpan<char> units, bool upper, uint lcid, out nint bstr)
    {
        bstr = 0;
        if (OleAut.Binding != OleAutBinding.Platform)
        {
            return false;
        }
        if (units.IsEmpty)
        {
            bstr = OleAut.SysAllocStringByteLen(null, 0);
            return true;
        }
        uint flags = upper ? LcMapUppercase : LcMapLowercase;
        fixed (char* src = units)
        {
            int outLen = OleAut.LCMapStringW(lcid, flags, src, units.Length, null, 0);
            if (outLen <= 0)
            {
                return false;
            }
            nint result = OleAut.SysAllocStringByteLen(null, (uint)(outLen * 2));
            int written = OleAut.LCMapStringW(lcid, flags, src, units.Length, (char*)result, outLen);
            if (written <= 0)
            {
                OleAut.SysFreeString(result);
                return false;
            }
            bstr = result;
            return true;
        }
    }

    /// <summary>
    /// Maps <paramref name="code"/> (128..255) to its Windows-1252 UTF-16 character when the locale's ANSI code page is 1252
    /// (<c>r-expr-1b/str-chr-asc-range</c>, acp=1252). Returns false on other code pages so callers refuse as
    /// <see cref="NotModelledCase.ChrCodePage"/>.
    /// </summary>
    public static bool TryChrCp1252(int code, uint lcid, out char ch)
    {
        ch = '\0';
        if (code is < 128 or > 255)
        {
            return false;
        }
        if (OleAut.Binding == OleAutBinding.Platform && GetAnsiCodePage(lcid) != 1252)
        {
            return false;
        }
        ch = code < 0xA0 ? Cp1252High[code - 0x80] : (char)code;
        return true;
    }

    /// <summary>
    /// Maps a non-ASCII UTF-16 character to its Windows-1252 byte (128..255) when the locale's ANSI code page is 1252
    /// (<c>r-expr-1b/str-chr-asc-range</c>, acp=1252: <c>Asc('é') = 233</c>, <c>Asc(Chr(128)) = 128</c>, <c>Asc(Chr(255)) = 255</c>).
    /// Returns false for unrecorded code pages or characters outside CP1252 so callers refuse as <c>NotModelled.AscCodePage</c>.
    /// </summary>
    public static bool TryAscCp1252(char ch, uint lcid, out byte code)
    {
        code = 0;
        if (ch <= 0x7F)
        {
            return false;
        }
        if (OleAut.Binding == OleAutBinding.Platform && GetAnsiCodePage(lcid) != 1252)
        {
            return false;
        }
        if (ch is >= '\u00A0' and <= '\u00FF')
        {
            code = (byte)ch;
            return true;
        }
        for (int i = 0; i < Cp1252High.Length; i++)
        {
            if (Cp1252High[i] == ch)
            {
                code = (byte)(0x80 + i);
                return true;
            }
        }
        return false;
    }
}
