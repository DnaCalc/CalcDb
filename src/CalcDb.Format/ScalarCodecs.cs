using System.Buffers.Binary;

namespace CalcDb.Format;

/// <summary>
/// The payload rules of the scalar kinds that are not one little-endian word (II-04 §§5.3–5.6): NUMERIC, GUID,
/// Extended Date/Time and the calculated-result wrapper. One definition for the row decoder, the key codec and the
/// inspector (T-FMT-23).
/// </summary>
public sealed record ScalarPayloadRules
{
    public required int NumericBytes { get; init; }
    public required FieldRule NumericSign { get; init; }

    /// <summary>Four 32-bit magnitude words, most significant first, each little-endian.</summary>
    public required FieldRule[] NumericWords { get; init; }

    public required byte NumericNegative { get; init; }

    public required int GuidBytes { get; init; }

    /// <summary>
    /// The Extended Date/Time payload width. <b>ACE's digit layout is not the pinned reference's.</b> II-04 §5.5 gives
    /// 19 day digits, a colon, 12 second digits, 7 fraction digits, then ":7" and one NUL; the values in
    /// r-sort-3/create-nums-accdb (NU.DX) are 15 day digits, a colon at 15, 12 second and 7 fraction digits, ":7" at 35
    /// and five NULs ("000000000693594:0000000432000000000:7" for 1899-12-30 12:00). The value's meaning is therefore
    /// not modelled (reported to the book, W-04/private-codecs); the exact bytes are kept and keyed.
    /// </summary>
    public required int ExtendedBytes { get; init; }

    /// <summary>Extended-flag bits that mark a calculated column (II-04 §5.6).</summary>
    public required byte CalculatedFlags { get; init; }

    public required int CalculatedPrefixBytes { get; init; }
    public required FieldRule CalculatedLength { get; init; }
    public required int CalculatedTrailerBytes { get; init; }

    public int CalculatedPayloadOffset => CalculatedLength.Offset + CalculatedLength.Width;

    /// <summary>
    /// A calculated NUMERIC result (II-04 §5.6): a length, its own scale and sign, then a magnitude of 4 to 16 bytes:
    /// a leading little-endian 32-bit group when its length is not a multiple of 8, then little-endian 64-bit groups,
    /// most significant first.
    /// </summary>
    public required FieldRule CalculatedNumericLength { get; init; }
    public required FieldRule CalculatedNumericScale { get; init; }
    public required FieldRule CalculatedNumericSign { get; init; }

    public int CalculatedNumericMagnitudeOffset => CalculatedNumericSign.Offset + 1;

    public IEnumerable<FieldRule> Rules => [NumericSign, .. NumericWords, CalculatedLength, CalculatedNumericLength, CalculatedNumericScale, CalculatedNumericSign];
}

/// <summary>
/// An exact NUMERIC value: a 128-bit magnitude, its sign and the column's scale (II-04 §5.3). The native value can hold
/// more than an Automation DECIMAL's 96 bits; <see cref="FitsAutomation"/> says whether it projects.
/// </summary>
public readonly record struct NativeDecimal(bool Negative, UInt128 Magnitude, byte Scale)
{
    public bool FitsAutomation => Magnitude >> 96 == UInt128.Zero && Scale <= 28;

    public uint High32 => (uint)(Magnitude >> 64);

    public ulong Low64 => (ulong)Magnitude;
}

/// <summary>Decoders of the payloads <see cref="ScalarPayloadRules"/> describes. Each checks its whole grammar and names what it refuses.</summary>
public static class ScalarCodecs
{
    /// <summary>
    /// A 17-byte NUMERIC payload at the column's scale. A sign byte other than 00 or 80 is NotModelled.NumericSign: the
    /// pinned reader treats every nonzero byte as negative, and no record shows what ACE does with one (II-04 §5.3).
    /// </summary>
    public static bool TryDecodeNumeric(ReadOnlySpan<byte> bytes, ScalarPayloadRules p, byte scale, out NativeDecimal value, out string? failure)
    {
        value = default;
        if (bytes.Length != p.NumericBytes)
        {
            failure = $"NUMERIC payload of {bytes.Length} bytes, not {p.NumericBytes}";
            return false;
        }
        byte sign = (byte)p.NumericSign.Read(bytes);
        if (sign != 0 && sign != p.NumericNegative)
        {
            failure = $"NotModelled.NumericSign: sign byte {sign:X2}";
            return false;
        }
        UInt128 magnitude = UInt128.Zero;
        foreach (FieldRule word in p.NumericWords)
        {
            magnitude = (magnitude << 32) | word.Read(bytes);
        }
        value = new NativeDecimal(sign != 0, magnitude, scale);
        failure = null;
        return true;
    }

    /// <summary>
    /// A calculated NUMERIC result payload (II-04 §5.6). r-cplx-rebuild/calc-schema-change reads C_dec ([A]/7 of 1) as
    /// VT_DECIMAL scale 14, words 0, 0x81ED, 0x8540EDB7: its 16-byte payload decodes to exactly that here. The length
    /// must equal the payload less 2, and the magnitude be 4, 8, 12 or 16 bytes.
    /// </summary>
    public static bool TryDecodeCalculatedNumeric(ReadOnlySpan<byte> payload, ScalarPayloadRules p, out NativeDecimal value, out string? failure)
    {
        value = default;
        int start = p.CalculatedNumericMagnitudeOffset;
        int width = payload.Length - start;
        if (!p.CalculatedNumericLength.TryRead(payload, 0, out ulong length) || length != (ulong)(payload.Length - p.CalculatedNumericLength.Width)
            || width is not (4 or 8 or 12 or 16))
        {
            failure = $"NotModelled.CalculatedNumeric: a {payload.Length}-byte result payload";
            return false;
        }
        byte sign = (byte)p.CalculatedNumericSign.Read(payload);
        if (sign != 0 && sign != p.NumericNegative)
        {
            failure = $"NotModelled.NumericSign: calculated sign byte {sign:X2}";
            return false;
        }
        ReadOnlySpan<byte> m = payload[start..];
        UInt128 magnitude = UInt128.Zero;
        if (width % 8 == 4)
        {
            magnitude = BinaryPrimitives.ReadUInt32LittleEndian(m);
            m = m[4..];
        }
        for (; !m.IsEmpty; m = m[8..])
        {
            magnitude = (magnitude << 64) | BinaryPrimitives.ReadUInt64LittleEndian(m);
        }
        value = new NativeDecimal(sign != 0, magnitude, (byte)p.CalculatedNumericScale.Read(payload));
        failure = null;
        return true;
    }

    /// <summary>
    /// The result type of a calculated column: its ResultType property (CAT-P), a Byte holding the native type code
    /// (r-cplx-rebuild-cx-calc.accdb: C_bool 01, C_dec 10, C_text 0A), bound by <see cref="BindCalculatedResults"/>. The
    /// column's own type code is not the result type: that fixture stores C_bool under code 3 and C_dec under code 7.
    /// </summary>
    public static byte? CalculatedResultType(long stored) => stored is >= 1 and <= 12 or 15 or NativeTypes.Numeric ? (byte)stored : null;

    /// <summary>
    /// The table with each calculated column's result type bound from its column map's ResultType property; a column
    /// without one, or with a type no codec reads, keeps no result type and reads as NotModelled.CalculatedResultType.
    /// </summary>
    public static TableDefinition BindCalculatedResults(TableDefinition table, PropertyBlob? blob, ScalarPayloadRules p)
    {
        if (blob is null || !table.Columns.Any(c => IsCalculated(c, p)))
        {
            return table;
        }
        return table with
        {
            Columns = [.. table.Columns.Select(c =>
            {
                if (!IsCalculated(c, p) || blob.Map(PropertySectionKind.ColumnValues, c.Name)?.Properties.FirstOrDefault(s => s.Name == "ResultType") is not { } stored)
                {
                    return c;
                }
                PropertyValue v = PropertyBlob.Decode(stored);
                return v.Failure is null && CalculatedResultType(v.Integer) is byte type ? c with { CalculatedResultType = type } : c;
            })],
        };
    }

    /// <summary>Whether a column is calculated (II-04 §5.6: extended flags C0).</summary>
    public static bool IsCalculated(ColumnDescriptor column, ScalarPayloadRules p) => (column.ExtendedFlags & p.CalculatedFlags) != 0;

    /// <summary>
    /// The result payload inside a calculated wrapper: 16 prefix bytes, the length N at 16, N payload bytes at 20, then
    /// three trailer bytes. Only that exact total is admitted: the pinned reader's pass-through of short values and its
    /// clamping of N are not adopted (NotModelled.CalculatedWrapper).
    /// </summary>
    public static bool TryUnwrapCalculated(ReadOnlySpan<byte> bytes, ScalarPayloadRules p, out Range payload, out string? failure)
    {
        payload = default;
        if (!p.CalculatedLength.TryRead(bytes, 0, out ulong n) || (ulong)bytes.Length != (ulong)p.CalculatedPayloadOffset + n + (ulong)p.CalculatedTrailerBytes)
        {
            failure = $"NotModelled.CalculatedWrapper: {bytes.Length} bytes{(bytes.Length >= p.CalculatedPayloadOffset ? $" declaring {n}" : "")}";
            return false;
        }
        payload = p.CalculatedPayloadOffset..(p.CalculatedPayloadOffset + (int)n);
        failure = null;
        return true;
    }

    /// <summary>The canonical-text byte order of a GUID's row bytes (II-05 §4.4): the first three components reversed.</summary>
    public static void GuidTextOrder(ReadOnlySpan<byte> row, Span<byte> text)
    {
        BinaryPrimitives.WriteUInt32BigEndian(text, BinaryPrimitives.ReadUInt32LittleEndian(row));
        BinaryPrimitives.WriteUInt16BigEndian(text[4..], BinaryPrimitives.ReadUInt16LittleEndian(row[4..]));
        BinaryPrimitives.WriteUInt16BigEndian(text[6..], BinaryPrimitives.ReadUInt16LittleEndian(row[6..]));
        row[8..16].CopyTo(text[8..]);
    }
}

/// <summary>
/// Which ACE write route formats a 42-byte <c>DATETIME2</c> (<c>0x14</c>) payload
/// (<c>evidence/r-q49-dt2-draftprops/dt2-*</c>, <c>evidence/r-q-fmt-sort/dt2-ticks-accdb</c>):
/// <list type="bullet">
/// <item><see cref="SqlInsert"/>: 19 day digits + <c>:</c> + 19 100-ns tick digits + <c>:7\0</c> (42 bytes exact).</item>
/// <item><see cref="DaoTable"/>: 15 day digits + <c>:</c> + 19 100-ns tick digits + <c>:7\0</c> + 4 zero slack bytes
/// (42 bytes; ACE's byte 40 is non-deterministic stack memory and is written as 0x00 here).</item>
/// <item><see cref="SqlUpdateOrDynaset"/>: 19 day digits + <c>:</c> + 19 whole-second digits + <c>:0\0</c> (42 bytes exact,
/// truncating fractional seconds; <c>dt2-update-invalid-accdb</c> page 46).</item>
/// </list>
/// </summary>
public enum ExtendedDateTimeWriteMode : byte
{
    DaoTable,
    SqlInsert,
    SqlUpdateOrDynaset,
}

/// <summary>
/// Decoded <c>DATETIME2</c> (<c>0x14</c>) value: days since <c>0001-01-01</c> (<c>0..3652058</c>) and 100-ns ticks since
/// midnight (<c>0..864000000000</c>, where <c>864000000000</c> represents <c>9999-12-31 24:00:00.0000000</c> produced by
/// half-up rounding at the 8th fractional digit; <c>evidence/r-q-fmt-sort/dt2-ticks-accdb</c>).
/// </summary>
public readonly record struct ExtendedDateTimeValue(long Days, long Ticks100ns) : IComparable<ExtendedDateTimeValue>
{
    public const long MinDays = 0;
    public const long MaxDays = 3_652_058;
    public const long OleEpochDays = 693_593;
    public const long MinOleDays = 36_159; // 0100-01-01
    public const long TicksPerSecond = 10_000_000L;
    public const long TicksPerDay = 86_400L * TicksPerSecond;

    public int CompareTo(ExtendedDateTimeValue other)
    {
        int d = Days.CompareTo(other.Days);
        return d != 0 ? d : Ticks100ns.CompareTo(other.Ticks100ns);
    }

    public static bool operator <(ExtendedDateTimeValue left, ExtendedDateTimeValue right) => left.CompareTo(right) < 0;
    public static bool operator <=(ExtendedDateTimeValue left, ExtendedDateTimeValue right) => left.CompareTo(right) <= 0;
    public static bool operator >(ExtendedDateTimeValue left, ExtendedDateTimeValue right) => left.CompareTo(right) > 0;
    public static bool operator >=(ExtendedDateTimeValue left, ExtendedDateTimeValue right) => left.CompareTo(right) >= 0;

    public void ToComponents(out int year, out int month, out int day, out int hour, out int minute, out int second, out int frac7)
    {
        DateTime date = new(Days * TicksPerDay);
        year = date.Year;
        month = date.Month;
        day = date.Day;
        if (Ticks100ns == TicksPerDay)
        {
            hour = 24;
            minute = 0;
            second = 0;
            frac7 = 0;
            return;
        }
        long totalSec = Ticks100ns / TicksPerSecond;
        frac7 = (int)(Ticks100ns % TicksPerSecond);
        hour = (int)(totalSec / 3600);
        minute = (int)((totalSec % 3600) / 60);
        second = (int)(totalSec % 60);
    }

    /// <summary>DAO <c>Field.Value</c> BSTR format: <c>"MM/DD/YYYY HH:MM:SS.fffffff"</c>.</summary>
    public string FormatDao()
    {
        ToComponents(out int y, out int m, out int d, out int h, out int min, out int s, out int f7);
        return string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{m:D2}/{d:D2}/{y:D4} {h:D2}:{min:D2}:{s:D2}.{f7:D7}");
    }

    /// <summary>
    /// Convert to OLE Automation <c>DATE</c> (truncating sub-second ticks to whole seconds; <c>dt2-select-funcs-convert-dao-ado-accdb</c>
    /// <c>DateAdd('d', 1, D)</c>). Fails outside OLE's <c>0100-01-01..9999-12-31</c> range or at <c>24:00:00</c>.
    /// </summary>
    public bool TryToOleDate(out double oaDate)
    {
        if (Days < MinOleDays || Days > MaxDays || Ticks100ns >= TicksPerDay)
        {
            oaDate = 0;
            return false;
        }
        long wholeDays = Days - OleEpochDays;
        double frac = (Ticks100ns / TicksPerSecond) / 86_400.0;
        oaDate = wholeDays >= 0 ? wholeDays + frac : wholeDays - frac;
        return true;
    }
}

/// <summary>
/// Parser, encoder, and decoder for ACE's 42-byte <c>DATETIME2</c> (<c>0x14</c>) ASCII payload
/// (<c>evidence/r-q49-dt2-draftprops/dt2-*</c>, <c>evidence/r-q-fmt-sort/dt2-ticks-accdb</c>).
/// </summary>
public static class ExtendedDateTimeCodec
{
    public const int PayloadBytes = 42;

    private static readonly long[] ScaleMultipliers =
    [
        10_000_000L,
        1_000_000L,
        100_000L,
        10_000L,
        1_000L,
        100L,
        10L,
        1L,
    ];

    /// <summary>Decode any valid 42-byte <c>DATETIME2</c> payload (15- or 19-digit days, scale 0..7).</summary>
    public static bool TryDecode(ReadOnlySpan<byte> bytes, out ExtendedDateTimeValue value)
    {
        value = default;
        if (bytes.Length != PayloadBytes)
        {
            return false;
        }
        int nul = bytes.IndexOf((byte)0);
        ReadOnlySpan<byte> text = nul >= 0 ? bytes[..nul] : bytes;
        int colon1 = text.IndexOf((byte)':');
        if (colon1 <= 0)
        {
            return false;
        }
        ReadOnlySpan<byte> rest = text[(colon1 + 1)..];
        int colon2 = rest.IndexOf((byte)':');
        if (colon2 <= 0 || colon2 + 1 >= rest.Length)
        {
            return false;
        }
        if (!TryParseAsciiUInt64(text[..colon1], out ulong days)
            || !TryParseAsciiUInt64(rest[..colon2], out ulong timeUnits)
            || !TryParseAsciiUInt64(rest[(colon2 + 1)..], out ulong scale)
            || scale > 7
            || days > (ulong)ExtendedDateTimeValue.MaxDays)
        {
            return false;
        }
        long mult = ScaleMultipliers[(int)scale];
        if (timeUnits > (ulong)(ExtendedDateTimeValue.TicksPerDay / mult))
        {
            return false;
        }
        long ticks = (long)timeUnits * mult;
        value = new ExtendedDateTimeValue((long)days, ticks);
        return true;
    }

    /// <summary>Encode <paramref name="value"/> into 42 row bytes for <paramref name="mode"/>.</summary>
    public static void Encode(ExtendedDateTimeValue value, ExtendedDateTimeWriteMode mode, Span<byte> destination)
    {
        if (destination.Length < PayloadBytes)
        {
            throw new ArgumentException($"Destination must be at least {PayloadBytes} bytes.", nameof(destination));
        }
        destination[..PayloadBytes].Clear();
        int dayDigits = mode == ExtendedDateTimeWriteMode.DaoTable ? 15 : 19;
        long timeUnits = mode == ExtendedDateTimeWriteMode.SqlUpdateOrDynaset
            ? value.Ticks100ns / ExtendedDateTimeValue.TicksPerSecond
            : value.Ticks100ns;
        byte scaleChar = mode == ExtendedDateTimeWriteMode.SqlUpdateOrDynaset ? (byte)'0' : (byte)'7';
        WriteZeroPaddedAscii(value.Days, destination[..dayDigits]);
        destination[dayDigits] = (byte)':';
        WriteZeroPaddedAscii(timeUnits, destination.Slice(dayDigits + 1, 19));
        destination[dayDigits + 20] = (byte)':';
        destination[dayDigits + 21] = scaleChar;
        destination[dayDigits + 22] = 0;
    }

    /// <summary>Encode <paramref name="value"/> into a fresh 42-byte array for <paramref name="mode"/>.</summary>
    public static byte[] Encode(ExtendedDateTimeValue value, ExtendedDateTimeWriteMode mode)
    {
        byte[] bytes = new byte[PayloadBytes];
        Encode(value, mode, bytes);
        return bytes;
    }

    /// <summary>Convert an OLE Automation <c>DATE</c> (<c>VT_DATE</c>) to <see cref="ExtendedDateTimeValue"/>.</summary>
    public static bool TryFromOleDate(double oaDate, out ExtendedDateTimeValue value)
    {
        value = default;
        if (!double.IsFinite(oaDate) || oaDate < -657_434.99999999 || oaDate >= 2_958_466.0)
        {
            return false;
        }
        double wholeDays = Math.Truncate(oaDate);
        long days = ExtendedDateTimeValue.OleEpochDays + (long)wholeDays;
        double frac = Math.Abs(oaDate - wholeDays);
        long totalSeconds = (long)Math.Round(frac * 86_400.0, MidpointRounding.AwayFromZero);
        if (totalSeconds >= 86_400L)
        {
            days += 1;
            totalSeconds -= 86_400L;
        }
        if (days < ExtendedDateTimeValue.MinDays || days > ExtendedDateTimeValue.MaxDays)
        {
            return false;
        }
        value = new ExtendedDateTimeValue(days, totalSeconds * ExtendedDateTimeValue.TicksPerSecond);
        return true;
    }

    /// <summary>
    /// Parse an ACE <c>DATETIME2</c> text value: ISO <c>YYYY-MM-DD[ HH:MM:SS[.f...]]</c>, time-only
    /// <c>HH:MM:SS[.f...]</c> (defaulting to <c>1899-12-30</c>), or US <c>MM/DD/YYYY HH:MM:SS[.f...]</c>.
    /// Rounds half-up at the 8th fractional digit (carrying into the next second/day, or holding
    /// <c>9999-12-31 24:00:00.0000000</c> at max day; <c>dt2-ticks-accdb</c>, <c>dt2-ddl-insert-bounds-accdb</c>,
    /// <c>dt2-update-invalid-accdb</c>).
    /// </summary>
    public static bool TryParseText(ReadOnlySpan<char> input, out ExtendedDateTimeValue value)
    {
        value = default;
        ReadOnlySpan<char> s = input.Trim();
        if (s.Length == 0)
        {
            return false;
        }
        int sep = s.IndexOfAny(' ', 'T', 't');
        if (sep < 0)
        {
            if (s.Contains('-'))
            {
                if (!TryParseIsoDate(s, out long days))
                {
                    return false;
                }
                value = new ExtendedDateTimeValue(days, 0);
                return true;
            }
            if (s.Contains(':'))
            {
                if (!TryParseTime(s, out long ticks, out bool carry))
                {
                    return false;
                }
                long days = ExtendedDateTimeValue.OleEpochDays + (carry ? 1 : 0);
                value = new ExtendedDateTimeValue(days, carry ? 0 : ticks);
                return true;
            }
            return false;
        }
        ReadOnlySpan<char> datePart = s[..sep];
        ReadOnlySpan<char> timePart = s[(sep + 1)..].TrimStart();
        if (timePart.Length == 0)
        {
            return false;
        }
        long parsedDays;
        if (datePart.Contains('-'))
        {
            if (!TryParseIsoDate(datePart, out parsedDays))
            {
                return false;
            }
        }
        else if (datePart.Contains('/'))
        {
            if (!TryParseUsDate(datePart, out parsedDays))
            {
                return false;
            }
        }
        else
        {
            return false;
        }
        if (!TryParseTime(timePart, out long parsedTicks, out bool dayCarry))
        {
            return false;
        }
        if (dayCarry)
        {
            if (parsedDays < ExtendedDateTimeValue.MaxDays)
            {
                parsedDays++;
                parsedTicks = 0;
            }
            else
            {
                // 9999-12-31 23:59:59.99999995..99999999 -> 9999-12-31 24:00:00.0000000 (dt2-ticks-accdb).
                parsedTicks = ExtendedDateTimeValue.TicksPerDay;
            }
        }
        value = new ExtendedDateTimeValue(parsedDays, parsedTicks);
        return true;
    }

    private static bool TryParseIsoDate(ReadOnlySpan<char> s, out long days)
    {
        days = 0;
        int d1 = s.IndexOf('-');
        if (d1 != 4)
        {
            return false;
        }
        ReadOnlySpan<char> rest = s[(d1 + 1)..];
        int d2 = rest.IndexOf('-');
        if (d2 is < 1 or > 2)
        {
            return false;
        }
        ReadOnlySpan<char> daySpan = rest[(d2 + 1)..];
        if (daySpan.Length is < 1 or > 2)
        {
            return false;
        }
        if (!TryParseDigits(s[..d1], out int year)
            || !TryParseDigits(rest[..d2], out int month)
            || !TryParseDigits(daySpan, out int day))
        {
            return false;
        }
        return TryCalendarDays(year, month, day, out days);
    }

    private static bool TryParseUsDate(ReadOnlySpan<char> s, out long days)
    {
        days = 0;
        int d1 = s.IndexOf('/');
        if (d1 is < 1 or > 2)
        {
            return false;
        }
        ReadOnlySpan<char> rest = s[(d1 + 1)..];
        int d2 = rest.IndexOf('/');
        if (d2 is < 1 or > 2)
        {
            return false;
        }
        ReadOnlySpan<char> yearSpan = rest[(d2 + 1)..];
        if (yearSpan.Length != 4)
        {
            return false;
        }
        if (!TryParseDigits(s[..d1], out int month)
            || !TryParseDigits(rest[..d2], out int day)
            || !TryParseDigits(yearSpan, out int year))
        {
            return false;
        }
        return TryCalendarDays(year, month, day, out days);
    }

    private static bool TryCalendarDays(int year, int month, int day, out long days)
    {
        days = 0;
        if (year is < 1 or > 9999 || month is < 1 or > 12)
        {
            return false;
        }
        if (day < 1 || day > DateTime.DaysInMonth(year, month))
        {
            return false;
        }
        days = new DateTime(year, month, day).Ticks / ExtendedDateTimeValue.TicksPerDay;
        return true;
    }

    private static bool TryParseTime(ReadOnlySpan<char> s, out long ticks, out bool carryDay)
    {
        ticks = 0;
        carryDay = false;
        int c1 = s.IndexOf(':');
        if (c1 is < 1 or > 2)
        {
            return false;
        }
        ReadOnlySpan<char> rest = s[(c1 + 1)..];
        int c2 = rest.IndexOf(':');
        if (c2 is < 1 or > 2)
        {
            return false;
        }
        ReadOnlySpan<char> secAndFrac = rest[(c2 + 1)..];
        int dot = secAndFrac.IndexOf('.');
        ReadOnlySpan<char> secSpan = dot >= 0 ? secAndFrac[..dot] : secAndFrac;
        ReadOnlySpan<char> fracSpan = dot >= 0 ? secAndFrac[(dot + 1)..] : default;
        if (secSpan.Length is < 1 or > 2 || (dot >= 0 && fracSpan.Length == 0))
        {
            return false;
        }
        if (!TryParseDigits(s[..c1], out int hour)
            || !TryParseDigits(rest[..c2], out int minute)
            || !TryParseDigits(secSpan, out int second))
        {
            return false;
        }
        if (hour is < 0 or > 23 || minute is < 0 or > 59 || second is < 0 or > 59)
        {
            return false;
        }
        long frac7 = 0;
        bool roundUp = false;
        for (int i = 0; i < fracSpan.Length; i++)
        {
            char ch = fracSpan[i];
            if (!char.IsAsciiDigit(ch))
            {
                return false;
            }
            if (i < 7)
            {
                frac7 = (frac7 * 10) + (ch - '0');
            }
            else if (i == 7)
            {
                roundUp = ch >= '5';
            }
        }
        for (int i = fracSpan.Length; i < 7; i++)
        {
            frac7 *= 10;
        }
        long total = (((hour * 3600L) + (minute * 60L) + second) * ExtendedDateTimeValue.TicksPerSecond) + frac7 + (roundUp ? 1 : 0);
        if (total == ExtendedDateTimeValue.TicksPerDay)
        {
            carryDay = true;
            ticks = 0;
        }
        else
        {
            ticks = total;
        }
        return true;
    }

    private static bool TryParseDigits(ReadOnlySpan<char> s, out int value)
    {
        value = 0;
        if (s.Length == 0)
        {
            return false;
        }
        foreach (char c in s)
        {
            if (!char.IsAsciiDigit(c))
            {
                return false;
            }
            value = (value * 10) + (c - '0');
        }
        return true;
    }

    private static bool TryParseAsciiUInt64(ReadOnlySpan<byte> s, out ulong value)
    {
        value = 0;
        if (s.Length == 0)
        {
            return false;
        }
        foreach (byte b in s)
        {
            if (b is < (byte)'0' or > (byte)'9')
            {
                return false;
            }
            ulong digit = (ulong)(b - (byte)'0');
            if (value > (ulong.MaxValue - digit) / 10)
            {
                return false;
            }
            value = (value * 10) + digit;
        }
        return true;
    }

    private static void WriteZeroPaddedAscii(long value, Span<byte> dest)
    {
        ulong v = (ulong)value;
        for (int i = dest.Length - 1; i >= 0; i--)
        {
            dest[i] = (byte)('0' + (v % 10));
            v /= 10;
        }
    }
}