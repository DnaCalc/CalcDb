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

/// <summary>One column's encoded value for <see cref="RowEncoder.TryEncode"/>: Null, a Yes/No mask bit, or its exact payload bytes.</summary>
public readonly record struct EncodedField(bool IsNull, bool Boolean = false, byte[]? Bytes = null)
{
    public static EncodedField Null { get; } = new(true);

    public static EncodedField OfBoolean(bool value) => new(false, Boolean: value);

    public static EncodedField OfBytes(byte[] bytes) => new(false, Bytes: bytes);
}

/// <summary>
/// Ordinary row encoder (II-04 §2; verified byte-for-byte across 382,187 live rows in 269 retained ACE fixtures).
/// Writes <c>C = MaxColumns</c>, zero-initialized fixed area (5,140/5,140 newly inserted Null fixed fields are zero),
/// contiguous variable payloads in <see cref="ColumnDescriptor.VariableIndex"/> order with immediately adjacent trailer
/// (120,238/120,238 rows have zero padding), and the <c>ceil(C/8)</c>-byte mask with zero unused high bits. Refuses
/// schemas or values whose ACE byte rules are not established (dropped columns, 0-fixed-width tables, or rows past
/// 4,060 bytes).
/// </summary>
public static class RowEncoder
{
    /// <summary>Maximum ordinary in-page row body length (II-04 §2).</summary>
    public const int MaxOrdinaryRowBytes = 4060;

    public static bool TryEncode(TableDefinition table, IReadOnlyList<EncodedField> fields, FormatDescriptor d, out byte[]? body, out string? failure)
    {
        body = null;
        failure = null;
        IReadOnlyList<ColumnDescriptor> columns = table.Columns;
        if (fields.Count != columns.Count)
        {
            failure = $"expected {columns.Count} fields, got {fields.Count}";
            return false;
        }
        int c = table.MaxColumns;
        if (c <= 0 || columns.Count != c)
        {
            failure = $"table has ColumnCount={columns.Count} and MaxColumns={c}: dropped-column row layout is not established";
            return false;
        }
        int fixedEnd = table.KnownFixedEnd(c, d);
        if (fixedEnd <= d.Row.FixedBase)
        {
            failure = "table has no non-Boolean fixed storage: ACE's 2-byte prefix padding on zero-fixed tables is not established";
            return false;
        }
        int v = table.VariableColumns;
        var variableSlots = v > 0 ? new (ColumnDescriptor Column, EncodedField Field)?[v] : null;
        var seenPhysical = new bool[c];
        long variableBytes = 0;
        for (int i = 0; i < columns.Count; i++)
        {
            ColumnDescriptor col = columns[i];
            EncodedField field = fields[i];
            if (col.PhysicalNumber < 0 || col.PhysicalNumber >= c || seenPhysical[col.PhysicalNumber])
            {
                failure = $"column {col.Name} has invalid or duplicate physical number {col.PhysicalNumber}";
                return false;
            }
            seenPhysical[col.PhysicalNumber] = true;
            if (col.IsMaskBoolean)
            {
                if (field.IsNull)
                {
                    failure = $"Yes/No column {col.Name} cannot be Null";
                    return false;
                }
                continue;
            }
            if (col.IsFixed)
            {
                if (field.IsNull)
                {
                    continue;
                }
                int width = NativeTypes.FixedWidth(col.TypeCode) ?? col.Length;
                if (field.Bytes is not { } bytes || bytes.Length != width)
                {
                    failure = $"fixed column {col.Name} expects {width} bytes, got {field.Bytes?.Length ?? 0}";
                    return false;
                }
                if (d.Row.FixedBase + col.FixedOffset + width > fixedEnd)
                {
                    failure = $"fixed column {col.Name} extends past fixedEnd {fixedEnd}";
                    return false;
                }
            }
            else
            {
                if (variableSlots is null || col.VariableIndex < 0 || col.VariableIndex >= v || variableSlots[col.VariableIndex] is not null)
                {
                    failure = $"variable column {col.Name} has invalid or duplicate variable index {col.VariableIndex} of {v}";
                    return false;
                }
                variableSlots[col.VariableIndex] = (col, field);
                if (!field.IsNull)
                {
                    variableBytes += field.Bytes?.Length ?? 0;
                }
            }
        }
        int maskBytes = (c + 7) / 8;
        long eod = fixedEnd + variableBytes;
        long trailerBytes = v > 0 ? 4L + (2L * v) : 0L;
        long total = eod + trailerBytes + maskBytes;
        if (total > MaxOrdinaryRowBytes || eod > ushort.MaxValue)
        {
            failure = $"encoded row of {total} bytes exceeds the {MaxOrdinaryRowBytes}-byte ordinary row limit (overflow row)";
            return false;
        }
        byte[] row = new byte[(int)total];
        d.Row.ColumnCount.Write(row, (ulong)c);
        for (int i = 0; i < columns.Count; i++)
        {
            ColumnDescriptor col = columns[i];
            EncodedField field = fields[i];
            if (col.IsFixed && !col.IsMaskBoolean && !field.IsNull)
            {
                field.Bytes!.CopyTo(row, d.Row.FixedBase + col.FixedOffset);
            }
        }
        if (v > 0 && variableSlots is not null)
        {
            Span<int> starts = stackalloc int[v];
            int pos = fixedEnd;
            for (int j = 0; j < v; j++)
            {
                if (variableSlots[j] is not { } entry)
                {
                    failure = $"missing variable column at index {j} of {v}";
                    return false;
                }
                starts[j] = pos;
                if (!entry.Field.IsNull && entry.Field.Bytes is { Length: > 0 } bytes)
                {
                    bytes.CopyTo(row, pos);
                    pos += bytes.Length;
                }
            }
            int trailerAt = pos;
            WriteWord(row, trailerAt, pos); // EOD
            trailerAt += 2;
            for (int j = v - 1; j >= 0; j--)
            {
                WriteWord(row, trailerAt, starts[j]);
                trailerAt += 2;
            }
            WriteWord(row, trailerAt, v);
        }
        int maskStart = (int)total - maskBytes;
        for (int i = 0; i < columns.Count; i++)
        {
            ColumnDescriptor col = columns[i];
            EncodedField field = fields[i];
            bool bit = col.IsMaskBoolean ? field.Boolean : !field.IsNull;
            if (bit)
            {
                row[maskStart + (col.PhysicalNumber / 8)] |= (byte)(1 << (col.PhysicalNumber % 8));
            }
        }
        body = row;
        return true;
    }

    private static void WriteWord(byte[] buffer, int offset, int value)
    {
        buffer[offset] = (byte)value;
        buffer[offset + 1] = (byte)(value >> 8);
    }
}
