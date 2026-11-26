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

/// <summary>
/// One column's encoded value for <see cref="RowEncoder.TryEncode"/>: Null (optionally carrying preserved fixed-slot
/// bytes across a rewrite; R-FMT-2 r-q36), a Yes/No mask bit, or its exact payload bytes.
/// </summary>
public readonly record struct EncodedField(bool IsNull, bool Boolean = false, byte[]? Bytes = null)
{
    public static EncodedField Null { get; } = new(true);

    /// <summary>A Null fixed field whose existing fixed-area bytes stay in place across a rewrite (R-FMT-2 r-q36).</summary>
    public static EncodedField PreservedNull(byte[] fixedBytes) => new(true, Bytes: fixedBytes);

    public static EncodedField OfBoolean(bool value) => new(false, Boolean: value);

    public static EncodedField OfBytes(byte[] bytes) => new(false, Bytes: bytes);
}

/// <summary>
/// Ordinary row encoder (II-04 §2; verified byte-for-byte across live rows in retained ACE fixtures, R-FMT-2 r-q36
/// <c>evidence/r-q36-publication-bytes/zf-*</c>, <c>nullfix-*</c>, and <c>evidence/r-q44-ddl-alter-types</c>
/// <c>dropcol-unindexed-rows-*</c>, <c>alt-long-double-text-rows-*</c>, <c>alt-text-widen-narrow-rows-*</c>,
/// <c>alt-long-text-rows-mdb-dao</c>, <c>evidence/r-q49-dt2-draftprops/ddl-default-alter-ado-*</c>).
/// Writes <c>C = MaxColumns</c>, fixed area (zeros for newly inserted Null fixed fields and dropped fixed slots,
/// preserved old bytes when supplied via <see cref="EncodedField.PreservedNull"/>), when <c>V == 0</c> pads with
/// <c>max(0, 2 - fixedSize)</c> zero bytes so at least 4 bytes precede the null mask (no pad when <c>V &gt; 0</c>),
/// contiguous variable payloads in <see cref="ColumnDescriptor.VariableIndex"/> order (empty slices for abandoned
/// variable indexes after <c>DROP COLUMN</c> or <c>ALTER COLUMN</c>) with immediately adjacent trailer, and the
/// <c>ceil(C/8)</c>-byte mask with zero bits for dropped/abandoned physical numbers and unused high bits.
/// Refuses rows past 4,060 bytes.
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
        if (c <= 0 || columns.Count == 0 || columns.Count > c)
        {
            failure = $"table has ColumnCount={columns.Count} and MaxColumns={c}: invalid column counts for row encoding";
            return false;
        }
        int fixedEnd = table.KnownFixedEnd(c, d);
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
                if (field.IsNull && field.Bytes is null)
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
        if (v == 0 && d.Row.FixedBase != 2)
        {
            failure = "encoding a row with no variable columns in a non-Jet4/ACE12 dialect (Jet 3) is not recorded (R-FMT-2 r-q36)";
            return false;
        }
        // R-FMT-2 r-q36 (evidence/r-q36-publication-bytes/zf-*): when V == 0, at least 4 bytes precede the null mask
        // (max(0, 2 - fixedSize) zero pad bytes); when V > 0, no pad sits before the variable area.
        long eod = v == 0 ? Math.Max(fixedEnd, d.Row.FixedBase + 2) : fixedEnd + variableBytes;
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
            if (col.IsFixed && !col.IsMaskBoolean && field.Bytes is { } fixedBytes)
            {
                fixedBytes.CopyTo(row, d.Row.FixedBase + col.FixedOffset);
            }
        }
        if (v > 0 && variableSlots is not null)
        {
            Span<int> starts = stackalloc int[v];
            int pos = fixedEnd;
            for (int j = 0; j < v; j++)
            {
                starts[j] = pos;
                // Abandoned variable indexes (from DROP COLUMN or ALTER COLUMN) write an empty [pos, pos) slice
                // (evidence/r-q44-ddl-alter-types/dropcol-unindexed-rows-*, r-q49-dt2-draftprops/ddl-default-alter-ado-*).
                if (variableSlots[j] is { } entry && !entry.Field.IsNull && entry.Field.Bytes is { Length: > 0 } bytes)
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

    /// <summary>
    /// Rewrite one existing ordinary row in place for <c>ALTER TABLE ... ALTER COLUMN</c> type or width change
    /// (<c>evidence/r-q44-ddl-alter-types/alt-long-double-text-rows-accdb-dao</c>,
    /// <c>alt-long-text-rows-mdb-dao</c>, <c>alt-text-widen-narrow-rows-accdb-dao</c>,
    /// <c>evidence/r-q49-dt2-draftprops/ddl-default-alter-ado-*</c>): copies <paramref name="oldBody"/> into the
    /// scan's reused zero-initialized <paramref name="workBuffer"/> without clearing bytes past
    /// <paramref name="oldBody"/> (reproducing ACE's reused row-rewrite scratch buffer on Null fixed slots and old
    /// mask bytes in newly extended fixed gaps), raises <c>C</c> to <see cref="TableDefinition.MaxColumns"/>, keeps
    /// abandoned fixed/variable slots and old null-mask bits intact, writes <paramref name="newValue"/> at
    /// <paramref name="newColumn"/>'s new physical/variable slot, and writes the updated variable trailer and mask.
    /// </summary>
    public static bool TryRewriteAlteredColumn(
        ReadOnlySpan<byte> oldBody,
        TableDefinition oldTable,
        TableDefinition newTable,
        ColumnDescriptor newColumn,
        EncodedField newValue,
        FormatDescriptor d,
        Span<byte> workBuffer,
        out int writtenLength,
        out string? failure)
    {
        writtenLength = 0;
        failure = null;
        if (d.Row.FixedBase != 2)
        {
            failure = "ALTER COLUMN row rewrite in a non-Jet4/ACE12 dialect is not recorded";
            return false;
        }
        if (newColumn.IsMaskBoolean)
        {
            failure = $"ALTER COLUMN to Yes/No ({newColumn.Name}) row rewrite is not recorded";
            return false;
        }
        if (workBuffer.Length < MaxOrdinaryRowBytes)
        {
            failure = $"work buffer of {workBuffer.Length} bytes is smaller than {MaxOrdinaryRowBytes}";
            return false;
        }
        if (oldBody.Length > workBuffer.Length)
        {
            failure = $"existing row body ({oldBody.Length} bytes) exceeds work buffer ({workBuffer.Length} bytes)";
            return false;
        }
        if (!RowEnvelope.TryOpen(oldBody, d, out RowEnvelope oldEnvelope, out failure))
        {
            return false;
        }
        int newC = newTable.MaxColumns;
        int newV = newTable.VariableColumns;
        if (newC <= 0 || oldEnvelope.ColumnCount > newC || newColumn.PhysicalNumber < 0 || newColumn.PhysicalNumber >= newC)
        {
            failure = $"invalid ALTER COLUMN physical layout (oldC={oldEnvelope.ColumnCount}, newC={newC}, newPhys={newColumn.PhysicalNumber})";
            return false;
        }
        int oldColCount = oldEnvelope.ColumnCount;
        int oldKnownFixedEnd = oldTable.KnownFixedEnd(oldColCount, d);
        int oldV = 0;
        int oldFixedAreaEnd = oldEnvelope.MaskStart;
        int oldVarEnd = oldEnvelope.MaskStart;
        bool mayHaveVarTrailer = oldTable.VariableColumns > 0
            && (oldTable.Columns.Count(c => !c.IsFixed) < oldTable.VariableColumns
                || oldTable.Columns.Any(c => !c.IsFixed && c.PhysicalNumber < oldColCount));
        if (mayHaveVarTrailer && oldEnvelope.MaskStart >= oldKnownFixedEnd + 4)
        {
            int candV = oldBody[oldEnvelope.MaskStart - 2] | (oldBody[oldEnvelope.MaskStart - 1] << 8);
            long trailerBytes = 4L + (2L * candV);
            if (candV > 0 && candV <= oldTable.VariableColumns && oldEnvelope.MaskStart - trailerBytes >= oldKnownFixedEnd)
            {
                int dOffset = oldEnvelope.MaskStart - (int)trailerBytes;
                int eod = oldBody[dOffset] | (oldBody[dOffset + 1] << 8);
                int start0 = oldBody[oldEnvelope.MaskStart - 4] | (oldBody[oldEnvelope.MaskStart - 3] << 8);
                if (start0 >= oldKnownFixedEnd && start0 <= eod && eod <= dOffset)
                {
                    oldV = candV;
                    oldFixedAreaEnd = start0;
                    oldVarEnd = eod;
                }
            }
        }
        if (oldV > newV)
        {
            failure = $"existing row has {oldV} variable columns, exceeding new table VariableColumns={newV}";
            return false;
        }
        int newFixedEnd = Math.Max(oldFixedAreaEnd, newTable.KnownFixedEnd(newC, d));
        if (oldV > 0 && newFixedEnd > oldFixedAreaEnd)
        {
            failure = $"ALTER COLUMN row rewrite extending fixed storage ({oldFixedAreaEnd} -> {newFixedEnd}) on a row with {oldV} existing variable column(s) is not recorded";
            return false;
        }

        // Preserve ACE's reused scratch buffer: overwrite only [0..oldBody.Length), leaving bytes past oldBody.Length
        // from earlier rows in the same scan intact (alt-long-double-text-rows-accdb-dao after-ddl-1 slot 1).
        oldBody.CopyTo(workBuffer);
        d.Row.ColumnCount.Write(workBuffer, (ulong)newC);

        if (newColumn.IsFixed)
        {
            int width = NativeTypes.FixedWidth(newColumn.TypeCode) ?? newColumn.Length;
            int destOffset = d.Row.FixedBase + newColumn.FixedOffset;
            if (destOffset + width > newFixedEnd)
            {
                failure = $"altered fixed column {newColumn.Name} [{destOffset}, {destOffset + width}) runs past fixedEnd {newFixedEnd}";
                return false;
            }
            if (!newValue.IsNull)
            {
                if (newValue.Bytes is not { } fixedBytes || fixedBytes.Length != width)
                {
                    failure = $"altered fixed column {newColumn.Name} expects {width} bytes, got {newValue.Bytes?.Length ?? 0}";
                    return false;
                }
                fixedBytes.CopyTo(workBuffer.Slice(destOffset, width));
            }
        }
        else if (newColumn.VariableIndex < oldV || newColumn.VariableIndex >= newV)
        {
            failure = $"altered variable column {newColumn.Name} has VariableIndex={newColumn.VariableIndex}, outside [{oldV}, {newV})";
            return false;
        }

        int maskBytes = (newC + 7) / 8;
        int maskStart;
        if (newV == 0)
        {
            long eod = Math.Max(newFixedEnd, d.Row.FixedBase + 2);
            long total = eod + maskBytes;
            if (total > MaxOrdinaryRowBytes || eod > ushort.MaxValue)
            {
                failure = $"altered row of {total} bytes exceeds the {MaxOrdinaryRowBytes}-byte ordinary row limit";
                return false;
            }
            maskStart = (int)eod;
        }
        else
        {
            Span<int> starts = stackalloc int[newV];
            for (int j = 0; j < oldV; j++)
            {
                int off = oldEnvelope.MaskStart - 4 - (2 * j);
                starts[j] = oldBody[off] | (oldBody[off + 1] << 8);
            }
            int pos = oldV > 0 ? oldVarEnd : newFixedEnd;
            int newVarBytes = !newColumn.IsFixed && !newValue.IsNull ? (newValue.Bytes?.Length ?? 0) : 0;
            long eod = (long)pos + newVarBytes;
            long trailerLen = 4L + (2L * newV);
            long total = eod + trailerLen + maskBytes;
            if (total > MaxOrdinaryRowBytes || eod > ushort.MaxValue)
            {
                failure = $"altered row of {total} bytes exceeds the {MaxOrdinaryRowBytes}-byte ordinary row limit";
                return false;
            }
            for (int j = oldV; j < newV; j++)
            {
                starts[j] = pos;
                if (!newColumn.IsFixed && j == newColumn.VariableIndex && !newValue.IsNull && newValue.Bytes is { Length: > 0 } varBytes)
                {
                    varBytes.CopyTo(workBuffer.Slice(pos, varBytes.Length));
                    pos += varBytes.Length;
                }
            }
            int trailerAt = pos;
            WriteWord(workBuffer, trailerAt, pos); // EOD
            trailerAt += 2;
            for (int j = newV - 1; j >= 0; j--)
            {
                WriteWord(workBuffer, trailerAt, starts[j]);
                trailerAt += 2;
            }
            WriteWord(workBuffer, trailerAt, newV);
            maskStart = trailerAt + 2;
        }

        workBuffer.Slice(maskStart, maskBytes).Clear();
        for (int bit = 0; bit < oldEnvelope.ColumnCount; bit++)
        {
            if (oldEnvelope.MaskBit(bit))
            {
                workBuffer[maskStart + (bit / 8)] |= (byte)(1 << (bit % 8));
            }
        }
        if (!newValue.IsNull)
        {
            workBuffer[maskStart + (newColumn.PhysicalNumber / 8)] |= (byte)(1 << (newColumn.PhysicalNumber % 8));
        }
        writtenLength = maskStart + maskBytes;
        return true;
    }

    private static void WriteWord(Span<byte> buffer, int offset, int value)
    {
        buffer[offset] = (byte)value;
        buffer[offset + 1] = (byte)(value >> 8);
    }
}
