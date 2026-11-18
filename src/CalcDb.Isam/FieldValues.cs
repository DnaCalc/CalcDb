using System.Buffers.Binary;
using CalcDb.Format;
using CalcDb.Foundation.Diagnostics;
using CalcDb.Storage;

namespace CalcDb.Isam;

/// <summary>The kinds of native field values the ISAM yields (II-04 §5.1); the public VARIANT projection is the object layer's.</summary>
public enum NativeKind
{
    Null,
    Boolean,
    Byte,
    Integer,
    Long,
    Currency,
    Single,
    Double,
    DateTime,
    Guid,
    Text,
    Binary,

    /// <summary>A long-value field (Memo, OLE) whose payload is held as exact bytes; Memo text is in <see cref="NativeValue.Text"/>.</summary>
    LongBinary,

    /// <summary>A Decimal/NUMERIC field kept as its exact native bytes until the scalar codec is qualified (II-04 §5.3).</summary>
    NumericRaw,

    /// <summary>A failure to read this field: the value is never a partial or lossy one.</summary>
    Failed,
}

/// <summary>
/// One native field value with its exact payload: fixed scalars as their exact bits, Text and Memo as exact UTF-16 units
/// (in <see cref="Text"/> only; the encoded bytes are not kept, task n7mw), Binary
/// and long values as exact bytes. Dates and Currency keep their raw representation. A value type: a fixed scalar read
/// allocates nothing (task 6p9q).
/// </summary>
public readonly record struct NativeValue(NativeKind Kind)
{
    public long Integer { get; init; }
    public ulong Bits { get; init; }
    public bool Boolean { get; init; }
    public string? Text { get; init; }
    public byte[]? Bytes { get; init; }
    public string? Failure { get; init; }

    public static NativeValue Null { get; } = new(NativeKind.Null);

    public static NativeValue Fail(string why) => new(NativeKind.Failed) { Failure = why };

    public override string ToString() => Kind switch
    {
        NativeKind.Null => "Null",
        NativeKind.Boolean => Boolean ? "True" : "False",
        NativeKind.Byte or NativeKind.Integer or NativeKind.Long => Integer.ToString(System.Globalization.CultureInfo.InvariantCulture),
        NativeKind.Text => $"\"{Text}\"",
        NativeKind.Failed => "failed: " + Failure,
        _ => $"{Kind} bits={Bits:x16}{(Bytes is null ? "" : " bytes=" + Convert.ToHexStringLower(Bytes))}",
    };
}

/// <summary>Reads one field of a row view as a <see cref="NativeValue"/> through the format description and the cache.</summary>
public static class FieldDecoder
{
    public const long DefaultLongValueBudget = 64L << 20;

    /// <summary>
    /// Locates the field's bytes in the row; false with the whole value when there are none to decode (a malformed
    /// row or field, Null, a Yes/No bit).
    /// </summary>
    private static bool Locate(RowView row, TableDefinition table, ColumnDescriptor column, FormatDescriptor d, out ReadOnlySpan<byte> bytes, out NativeValue value) =>
        Locate(row, table, column, d, out bytes, out value, out _);

    private static bool Locate(RowView row, TableDefinition table, ColumnDescriptor column, FormatDescriptor d, out ReadOnlySpan<byte> bytes, out NativeValue value, out Range at)
    {
        bytes = default;
        at = default;
        if (!RowEnvelope.TryOpen(row.Body, d, out RowEnvelope envelope, out string? failure))
        {
            value = NativeValue.Fail(failure!);
            return false;
        }
        FieldRead read = envelope.ReadField(column, table.KnownFixedEnd(envelope.ColumnCount, d));
        switch (read.Outcome)
        {
            case FieldOutcome.Malformed:
                value = NativeValue.Fail(read.Failure!);
                return false;
            case FieldOutcome.Null:
                value = NativeValue.Null;
                return false;
            case FieldOutcome.Boolean:
                value = new NativeValue(NativeKind.Boolean) { Boolean = read.Value };
                return false;
        }
        at = read.Start..read.End;
        bytes = row.Body[at];
        value = default;
        return true;
    }

    /// <summary>The exact payload of a Memo or OLE field's long value, or null with the failure.</summary>
    private static byte[]? LongValue(ReadOnlySpan<byte> bytes, PageCache cache, FormatDescriptor d, long longValueBudget, out string? failure)
    {
        LongValuePlan? plan = LongValuePlan.Inspect(bytes, out string? planFailure);
        if (plan is null)
        {
            failure = planFailure!;
            return null;
        }
        byte[]? payload = LongValueReader.Read(plan, bytes, cache, d, longValueBudget, out TraversalFailure? lvFailure);
        if (payload is null)
        {
            failure = lvFailure!.Predicate;
            return null;
        }
        CostCounters.Current.LongValuePayloadBytes += payload.Length;
        failure = null;
        return payload;
    }

    /// <summary>
    /// A Text or Memo field for direct construction (D-STRING-1, task n7mw): its encoded bytes, borrowed from the row
    /// view's page (Text) or the assembled long value (Memo), checked and measured but not decoded. Null and failures
    /// are those of <see cref="Read"/>; any other column reads as <see cref="Read"/>.
    /// </summary>
    public static TextFieldRead ReadText(RowView row, TableDefinition table, ColumnDescriptor column, PageCache cache, FormatDescriptor d, long longValueBudget = DefaultLongValueBudget) =>
        ReadText(row, table, column, cache, d, out _, longValueBudget);

    /// <summary>
    /// As <see cref="ReadText(RowView, TableDefinition, ColumnDescriptor, PageCache, FormatDescriptor, long)"/>; for an
    /// in-row Text field, <paramref name="location"/> is where its checked bytes lie in the row body, so a holder of the
    /// row can borrow them again without locating them again.
    /// </summary>
    public static TextFieldRead ReadText(RowView row, TableDefinition table, ColumnDescriptor column, PageCache cache, FormatDescriptor d, out Range? location, long longValueBudget = DefaultLongValueBudget)
    {
        location = null;
        if (column.TypeCode is not (NativeTypes.Text or NativeTypes.Memo))
        {
            return new TextFieldRead(FieldResult.Of(Read(row, table, column, cache, d, longValueBudget)));
        }
        if (!Locate(row, table, column, d, out ReadOnlySpan<byte> bytes, out NativeValue value, out Range at))
        {
            return new TextFieldRead(FieldResult.Of(value));
        }
        if (column.TypeCode == NativeTypes.Memo)
        {
            byte[]? payload = LongValue(bytes, cache, d, longValueBudget, out string? failure);
            if (payload is null)
            {
                return new TextFieldRead(FieldResult.Of(NativeValue.Fail(failure!)));
            }
            bytes = payload;
        }
        TextShape shape = TextCodec.Measure(bytes);
        if (!shape.Succeeded)
        {
            return new TextFieldRead(FieldResult.Of(NativeValue.Fail(shape.Failure!)));
        }
        CostCounters.Current.TextDecodedUnits += shape.Units;
        location = column.TypeCode == NativeTypes.Text ? at : null;
        return new TextFieldRead(bytes, shape);
    }

    public static NativeValue Read(RowView row, TableDefinition table, ColumnDescriptor column, PageCache cache, FormatDescriptor d, long longValueBudget = DefaultLongValueBudget)
    {
        if (!Locate(row, table, column, d, out ReadOnlySpan<byte> bytes, out NativeValue located))
        {
            return located;
        }
        switch (column.TypeCode)
        {
            case NativeTypes.Byte:
                return new NativeValue(NativeKind.Byte) { Integer = bytes[0], Bits = bytes[0] };
            case NativeTypes.Integer:
                return new NativeValue(NativeKind.Integer) { Integer = BinaryPrimitives.ReadInt16LittleEndian(bytes), Bits = BinaryPrimitives.ReadUInt16LittleEndian(bytes) };
            case NativeTypes.Long:
                return new NativeValue(NativeKind.Long) { Integer = BinaryPrimitives.ReadInt32LittleEndian(bytes), Bits = BinaryPrimitives.ReadUInt32LittleEndian(bytes) };
            case NativeTypes.Currency:
                return new NativeValue(NativeKind.Currency) { Integer = BinaryPrimitives.ReadInt64LittleEndian(bytes), Bits = BinaryPrimitives.ReadUInt64LittleEndian(bytes) };
            case NativeTypes.Single:
                return new NativeValue(NativeKind.Single) { Bits = BinaryPrimitives.ReadUInt32LittleEndian(bytes) };
            case NativeTypes.Double:
                return new NativeValue(NativeKind.Double) { Bits = BinaryPrimitives.ReadUInt64LittleEndian(bytes) };
            case NativeTypes.DateTime:
                return new NativeValue(NativeKind.DateTime) { Bits = BinaryPrimitives.ReadUInt64LittleEndian(bytes) };
            case NativeTypes.Guid:
                return new NativeValue(NativeKind.Guid) { Bytes = bytes.ToArray() };
            case NativeTypes.Numeric:
                return new NativeValue(NativeKind.NumericRaw) { Bytes = bytes.ToArray() };
            case NativeTypes.Text:
            {
                TextDecode text = TextCodec.Decode(bytes);
                CostCounters.Current.TextDecodedUnits += text.Text?.Length ?? 0;
                return text.Succeeded ? new NativeValue(NativeKind.Text) { Text = text.Text } : NativeValue.Fail(text.Failure!);
            }
            case NativeTypes.Binary:
                return new NativeValue(NativeKind.Binary) { Bytes = bytes.ToArray() };
            case NativeTypes.Memo or NativeTypes.Ole:
            {
                byte[]? payload = LongValue(bytes, cache, d, longValueBudget, out string? failure);
                if (payload is null)
                {
                    return NativeValue.Fail(failure!);
                }
                if (column.TypeCode == NativeTypes.Memo)
                {
                    TextDecode memo = TextCodec.Decode(payload);
                    CostCounters.Current.TextDecodedUnits += memo.Text?.Length ?? 0;
                    return memo.Succeeded ? new NativeValue(NativeKind.Text) { Text = memo.Text } : NativeValue.Fail(memo.Failure!);
                }
                return new NativeValue(NativeKind.LongBinary) { Bytes = payload };
            }
            default:
                return NativeValue.Fail($"native type {column.TypeCode} has no qualified codec yet");
        }
    }
}
