namespace CalcDb.Format;

/// <summary>The outcome of reading one field's bytes (II-04 §4.1).</summary>
public enum FieldOutcome
{
    /// <summary>The field's bytes are <see cref="FieldRead.Start"/> to <see cref="FieldRead.End"/> of the row body.</summary>
    Bytes,

    /// <summary>The mask says Null (or the column is absent from this older row).</summary>
    Null,

    /// <summary>An ordinary Boolean: its value is the mask bit.</summary>
    Boolean,

    /// <summary>A bounded format failure: <see cref="FieldRead.Failure"/> names the failed predicate.</summary>
    Malformed,
}

/// <summary>One field read: a borrowed range of the row body, a mask value, or a typed failure. No bytes are copied.</summary>
public readonly record struct FieldRead(FieldOutcome Outcome, int Start, int End, bool Value, string? Failure)
{
    public static FieldRead Malformed(string failure) => new(FieldOutcome.Malformed, 0, 0, false, failure);
}

/// <summary>
/// The ordinary row envelope over one checked row body (II-04 §§2, 4.1): C from the row itself, the ceil(C/8)-byte
/// mask at the row end, and the variable trailer loaded on demand. There is no allocation proportional to an
/// untrusted C or V; a field read is O(1) word and bit loads.
/// </summary>
public readonly ref struct RowEnvelope
{
    private readonly ReadOnlySpan<byte> _body;
    private readonly FormatDescriptor _d;

    private RowEnvelope(ReadOnlySpan<byte> body, int columnCount, int maskStart, FormatDescriptor d)
    {
        _body = body;
        ColumnCount = columnCount;
        MaskStart = maskStart;
        _d = d;
    }

    /// <summary>C: the physical columns this row records.</summary>
    public int ColumnCount { get; }

    /// <summary>T: where the mask starts.</summary>
    public int MaskStart { get; }

    public int MaskBytes => _body.Length - MaskStart;

    /// <summary>OpenRowEnvelope: C widened before arithmetic, the mask bounded by the body.</summary>
    public static bool TryOpen(ReadOnlySpan<byte> body, FormatDescriptor d, out RowEnvelope envelope, out string? failure)
    {
        envelope = default;
        failure = null;
        if (body.Length < d.Row.FixedBase)
        {
            failure = $"row of {body.Length} bytes has no column count";
            return false;
        }
        int c = (int)d.Row.ColumnCount.Read(body);
        int m = (c + 7) / 8;
        if (m > body.Length - d.Row.FixedBase)
        {
            failure = $"{d.Row.ColumnCount.Id}: C={c} needs a {m}-byte mask; the {body.Length}-byte row cannot hold it";
            return false;
        }
        envelope = new RowEnvelope(body, c, body.Length - m, d);
        return true;
    }

    public bool MaskBit(int physical) => physical < ColumnCount && (_body[MaskStart + (physical / 8)] & (1 << (physical % 8))) != 0;

    /// <summary>
    /// ReadFieldBytes (II-04 §4.1). <paramref name="knownFixedEnd"/> is the end of the active fixed storage below C, from
    /// the schema (bytes 0–1 count as fixed); variable spans must start at or after it.
    /// </summary>
    public FieldRead ReadField(ColumnDescriptor column, int knownFixedEnd)
    {
        int p = column.PhysicalNumber;
        if (p < 0)
        {
            return FieldRead.Malformed("negative physical column number");
        }
        bool present = MaskBit(p);
        if (column.IsMaskBoolean)
        {
            return new FieldRead(FieldOutcome.Boolean, 0, 0, present, null);
        }
        if (!present)
        {
            return new FieldRead(FieldOutcome.Null, 0, 0, false, null);
        }
        if (column.IsFixed)
        {
            int width = NativeTypes.FixedWidth(column.TypeCode) ?? column.Length;
            long begin = (long)_d.Row.FixedBase + column.FixedOffset;
            long end = begin + width;
            if (!(_d.Row.FixedBase <= begin && begin <= end && end <= MaskStart))
            {
                return FieldRead.Malformed($"fixed field {column.Name} [{begin}, {end}) runs past the mask at {MaskStart}");
            }
            return new FieldRead(FieldOutcome.Bytes, (int)begin, (int)end, false, null);
        }
        int t = MaskStart;
        int word = _d.Row.WordBytes;
        if (t < _d.Row.FixedBase + word)
        {
            return FieldRead.Malformed("no room for a variable trailer");
        }
        int v = Word(t - word);
        int j = column.VariableIndex;
        if (j < 0 || j >= v)
        {
            return FieldRead.Malformed($"variable index {j} is not below V={v}");
        }
        long trailer = 4L + (2L * v);
        if (trailer > t - _d.Row.FixedBase)
        {
            return FieldRead.Malformed($"V={v} needs a {trailer}-byte trailer; only {t - _d.Row.FixedBase} bytes precede the mask");
        }
        int dOffset = t - (int)trailer;
        int eod = Word(dOffset);
        if (!(_d.Row.FixedBase <= eod && eod <= dOffset))
        {
            return FieldRead.Malformed($"EOD {eod} is outside [{_d.Row.FixedBase}, {dOffset}]");
        }
        int a = Word(t - 4 - (2 * j));
        // end[j] is the next field's start; for the last field, T-6-2(V-1) is exactly the EOD word.
        int b = Word(t - 6 - (2 * j));
        if (!(knownFixedEnd <= a && a <= b && b <= eod))
        {
            return FieldRead.Malformed($"variable field {column.Name} [{a}, {b}) is not inside [{knownFixedEnd}, {eod}] or descends");
        }
        return new FieldRead(FieldOutcome.Bytes, a, b, false, null);
    }

    private int Word(int offset) => _body[offset] | (_body[offset + 1] << 8);

    /// <summary>The end of active fixed storage below C for this row: header bytes plus every fixed column present in the row.</summary>
    public static int KnownFixedEnd(IEnumerable<ColumnDescriptor> columns, int rowColumnCount, FormatDescriptor d)
    {
        int end = d.Row.FixedBase;
        foreach (ColumnDescriptor c in columns)
        {
            if (c.IsFixed && !c.IsMaskBoolean && c.PhysicalNumber < rowColumnCount)
            {
                end = Math.Max(end, d.Row.FixedBase + c.FixedOffset + (NativeTypes.FixedWidth(c.TypeCode) ?? c.Length));
            }
        }
        return end;
    }
}
