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
/// One native field value with its exact payload: fixed scalars as their exact bits, Text as exact UTF-16 units, Binary
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

    public static NativeValue Read(RowView row, TableDefinition table, ColumnDescriptor column, PageCache cache, FormatDescriptor d, long longValueBudget = DefaultLongValueBudget)
    {
        if (!RowEnvelope.TryOpen(row.Body, d, out RowEnvelope envelope, out string? failure))
        {
            return NativeValue.Fail(failure!);
        }
        FieldRead read = envelope.ReadField(column, table.KnownFixedEnd(envelope.ColumnCount, d));
        switch (read.Outcome)
        {
            case FieldOutcome.Malformed:
                return NativeValue.Fail(read.Failure!);
            case FieldOutcome.Null:
                return NativeValue.Null;
            case FieldOutcome.Boolean:
                return new NativeValue(NativeKind.Boolean) { Boolean = read.Value };
        }
        ReadOnlySpan<byte> bytes = row.Body[read.Start..read.End];
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
                return text.Succeeded ? new NativeValue(NativeKind.Text) { Text = text.Text, Bytes = bytes.ToArray() } : NativeValue.Fail(text.Failure!);
            }
            case NativeTypes.Binary:
                return new NativeValue(NativeKind.Binary) { Bytes = bytes.ToArray() };
            case NativeTypes.Memo or NativeTypes.Ole:
            {
                LongValuePlan? plan = LongValuePlan.Inspect(bytes, out string? planFailure);
                if (plan is null)
                {
                    return NativeValue.Fail(planFailure!);
                }
                byte[]? payload = LongValueReader.Read(plan, bytes, cache, d, longValueBudget, out TraversalFailure? lvFailure);
                if (payload is null)
                {
                    return NativeValue.Fail(lvFailure!.Predicate);
                }
                CostCounters.Current.LongValuePayloadBytes += payload.Length;
                if (column.TypeCode == NativeTypes.Memo)
                {
                    TextDecode memo = TextCodec.Decode(payload);
                    CostCounters.Current.TextDecodedUnits += memo.Text?.Length ?? 0;
                    return memo.Succeeded ? new NativeValue(NativeKind.Text) { Text = memo.Text, Bytes = payload } : NativeValue.Fail(memo.Failure!);
                }
                return new NativeValue(NativeKind.LongBinary) { Bytes = payload };
            }
            default:
                return NativeValue.Fail($"native type {column.TypeCode} has no qualified codec yet");
        }
    }
}
