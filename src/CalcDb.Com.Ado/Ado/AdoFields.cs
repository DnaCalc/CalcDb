using System.Runtime.InteropServices;
using CalcDb.Com.Com;
using CalcDb.Com.Declarations;
using CalcDb.Expressions;
using CalcDb.Format;
using CalcDb.Foundation;
using CalcDb.Isam;
using CalcDb.Sql;
using CalcDb.Values;
using CalcDb.Values.Scalar;

namespace CalcDb.Com.Ado;

/// <summary>
/// ADO Field metadata for one column of an open (or recently closed) Recordset
/// (r-ado-1b/fields-meta-mdb, fields-meta-expressions, r-fix-wire-ado/fields-meta-decimal-expressions,
/// r-oledb-odbc/buffer-typed-server).
/// </summary>
internal sealed record AdoFieldDescriptor(
    string Name,
    int AdoType,
    int DefinedSize,
    byte Precision,
    byte NumericScale,
    int Attributes,
    bool IsBaseTableColumn,
    ColumnDescriptor? BaseColumn,
    int BaseSourceIndex,
    bool DecimalActualSize19OnServer = false,
    Failure? MetadataRefusal = null)
{
    // FieldAttributeEnum bit combinations observed in r-ado-1b/fields-meta-mdb & fields-meta-expressions:
    //   2 (adFldMayDefer) | 8 (adFldUnknownUpdatable) | 16 (adFldFixed) | 32 (adFldIsNullable) | 64 (adFldMayBeNull) | 128 (adFldLong)
    private const int AttrBaseFixedNotNull = 90;  // 2 | 8 | 16 | 64 (AutoNumber, Boolean)
    private const int AttrBaseFixedNullable = 122; // 2 | 8 | 16 | 32 | 64
    private const int AttrBaseVarNullable = 106;   // 2 | 8 | 32 | 64 (Text, Binary)
    private const int AttrBaseLongNullable = 234;  // 2 | 8 | 32 | 64 | 128 (Memo, Ole)

    private const int AttrExprFixed = 114;         // 2 | 16 | 32 | 64 (no adFldUnknownUpdatable)
    private const int AttrExprVar = 98;            // 2 | 32 | 64 (Null literal / binary expr)
    private const int AttrExprLong = 226;          // 2 | 32 | 64 | 128 (string / long expr)

    /// <summary>Build ADO metadata for a base table column (direct table open or base-column SELECT projection).</summary>
    public static AdoFieldDescriptor FromTableColumn(ColumnDescriptor col, string? aliasName = null, int sourceIndex = 0)
    {
        string name = aliasName ?? col.Name;
        byte typeCode = col.CalculatedResultType ?? col.TypeCode;
        return typeCode switch
        {
            NativeTypes.Boolean => new(name, 11, 2, 255, 255, AttrBaseFixedNotNull, true, col, sourceIndex),
            NativeTypes.Byte => new(name, 17, 1, 3, 255, AttrBaseFixedNullable, true, col, sourceIndex),
            NativeTypes.Integer => new(name, 2, 2, 5, 255, AttrBaseFixedNullable, true, col, sourceIndex),
            NativeTypes.Long => new(name, 3, 4, 10, 255, col.IsAutoNumber ? AttrBaseFixedNotNull : AttrBaseFixedNullable, true, col, sourceIndex),
            NativeTypes.Currency => new(name, 6, 8, 19, 255, AttrBaseFixedNullable, true, col, sourceIndex),
            NativeTypes.Single => new(name, 4, 4, 7, 255, AttrBaseFixedNullable, true, col, sourceIndex),
            NativeTypes.Double => new(name, 5, 8, 15, 255, AttrBaseFixedNullable, true, col, sourceIndex),
            NativeTypes.DateTime => new(name, 7, 8, 255, 255, AttrBaseFixedNullable, true, col, sourceIndex),
            NativeTypes.Binary => new(name, 204, col.Length, 255, 255, AttrBaseVarNullable, true, col, sourceIndex),
            NativeTypes.Text => new(name, 202, col.Length / 2, 255, 255, AttrBaseVarNullable, true, col, sourceIndex),
            NativeTypes.Ole => new(name, 205, 1073741823, 255, 255, AttrBaseLongNullable, true, col, sourceIndex),
            NativeTypes.Memo => new(name, 203, 536870910, 255, 255, AttrBaseLongNullable, true, col, sourceIndex),
            NativeTypes.Guid => new(name, 72, 16, 255, 255, AttrBaseFixedNullable, true, col, sourceIndex),
            NativeTypes.Numeric => new(
                name,
                131,
                19,
                col.CalculatedResultType is not null ? (byte)28 : col.NumericPrecision,
                col.CalculatedResultType is not null ? (byte)0 : col.NumericScale,
                AttrBaseFixedNullable,
                true,
                col,
                sourceIndex,
                DecimalActualSize19OnServer: true),
            _ => Refused(name, "NotModelled.AdoFieldType", $"ADO Field metadata for native column type code {typeCode} ('{name}') is not recorded"),
        };
    }

    /// <summary>
    /// Build ADO metadata for one projected column of a <see cref="SelectCursor"/> from bound SQL metadata only
    /// (r-ado-1b/fields-meta-expressions, r-fix-wire-ado/fields-meta-decimal-expressions). Unrecorded column types,
    /// aggregate shapes, or Decimal expression metadata carry an explicit <see cref="MetadataRefusal"/> rather than
    /// falling back to a guessed type or inspecting row values.
    /// </summary>
    public static AdoFieldDescriptor FromSelectColumn(
        ResultColumn col,
        int? daoType,
        string? typeOpen,
        SyntaxNode? expressionRoot = null,
        IReadOnlyList<SourceSlot>? sources = null)
    {
        if (col.Origin == ColumnOrigin.BaseColumn && col.SourceColumn is { } src)
        {
            return FromTableColumn(src.Column, col.Name, src.SourceIndex);
        }

        if (col.Origin == ColumnOrigin.Aggregate)
        {
            return FromAggregateColumn(col, daoType, typeOpen);
        }

        if (daoType is null)
        {
            if (expressionRoot is not null && sources is not null
                && TryResolveDecimalExpression(col.Name, expressionRoot, sources) is { } decimalDesc)
            {
                return decimalDesc;
            }
            return Refused(
                col.Name,
                typeOpen ?? "NotModelled.ExpressionFieldType",
                $"ADO Field metadata for column '{col.Name}', whose static type is not identified");
        }

        if (daoType == 20)
        {
            if (col.DecimalPrecision is byte prec && col.DecimalScale is byte scale)
            {
                // r-fix-wire-ado/fields-meta-decimal-expressions (x1/x3/x4/x5), r-ado-1b/fields-meta-expressions (e2/e14):
                // expression Decimal reports adNumeric (131), DefinedSize=19, Attr=114, server ActualSize=16 / client ActualSize=19.
                return new(col.Name, 131, 19, prec, scale, AttrExprFixed, false, null, -1, DecimalActualSize19OnServer: false);
            }
            return Refused(
                col.Name,
                "NotModelled.AdoDecimalExpressionMetadata",
                $"ADO Field Precision/NumericScale for Decimal expression '{col.Name}' is not recorded");
        }

        return daoType switch
        {
            // r-ado-1b/fields-meta-expressions: boolean expressions report adSmallInt (2), Prec=5, Attr=114, VT_I2
            1 => new(col.Name, 2, 2, 5, 255, AttrExprFixed, false, null, -1),
            2 => new(col.Name, 17, 1, 3, 255, AttrExprFixed, false, null, -1),
            3 => new(col.Name, 2, 2, 5, 255, AttrExprFixed, false, null, -1),
            4 => new(col.Name, 3, 4, 10, 255, AttrExprFixed, false, null, -1),
            5 => new(col.Name, 6, 8, 19, 255, AttrExprFixed, false, null, -1),
            6 => new(col.Name, 4, 4, 7, 255, AttrExprFixed, false, null, -1),
            7 => new(col.Name, 5, 8, 15, 255, AttrExprFixed, false, null, -1),
            8 => new(col.Name, 7, 8, 255, 255, AttrExprFixed, false, null, -1),
            // r-ado-1b/fields-meta-expressions: Null literal / binary expression -> adVarBinary (204), DefinedSize=510, Attr=98
            9 => new(col.Name, 204, 510, 255, 255, AttrExprVar, false, null, -1),
            // r-ado-1b/fields-meta-expressions: text/memo expressions -> adLongVarWChar (203), DefinedSize=536870910, Attr=226
            10 or 12 => new(col.Name, 203, 536870910, 255, 255, AttrExprLong, false, null, -1),
            11 => new(col.Name, 205, 1073741823, 255, 255, AttrExprLong, false, null, -1),
            15 => new(col.Name, 72, 16, 255, 255, AttrExprFixed, false, null, -1),
            _ => Refused(col.Name, "NotModelled.AdoFieldType", $"ADO Field metadata for DAO Field.Type {daoType} ('{col.Name}') is not recorded"),
        };
    }

    /// <summary>
    /// Exact recorded ADO metadata for bare SQL aggregates (r-ado-1b/fields-meta-expressions #495-567,
    /// r-fix-wire-ado/fields-meta-decimal-expressions #436-670):
    /// Count/Sum/Min/Max/First carry adFldUnknownUpdatable (122/106/234), Avg carries 114, Min(Text) is adVarWChar(255)
    /// with Attr=106, and First(Memo) is adLongVarWChar with Attr=234.
    /// </summary>
    private static AdoFieldDescriptor FromAggregateColumn(ResultColumn col, int? daoType, string? typeOpen)
    {
        if (daoType is null)
        {
            return Refused(
                col.Name,
                typeOpen ?? "NotModelled.AdoAggregateFieldMetadata",
                $"ADO Field metadata for aggregate column '{col.Name}', whose type is not identified");
        }
        return (col.Aggregate, daoType.Value) switch
        {
            (ResultAggregateKind.CountRows or ResultAggregateKind.Count, 4) =>
                new(col.Name, 3, 4, 10, 255, AttrBaseFixedNullable, false, null, -1),
            (ResultAggregateKind.Sum, 7) =>
                new(col.Name, 5, 8, 15, 255, AttrBaseFixedNullable, false, null, -1),
            (ResultAggregateKind.Sum, 5) =>
                new(col.Name, 6, 8, 19, 255, AttrBaseFixedNullable, false, null, -1),
            (ResultAggregateKind.Avg, 7) =>
                new(col.Name, 5, 8, 15, 255, AttrExprFixed, false, null, -1),
            (ResultAggregateKind.Min or ResultAggregateKind.Max, 10) =>
                new(col.Name, 202, 255, 255, 255, AttrBaseVarNullable, false, null, -1),
            (ResultAggregateKind.Min or ResultAggregateKind.Max, 8) =>
                new(col.Name, 7, 8, 255, 255, AttrBaseFixedNullable, false, null, -1),
            (ResultAggregateKind.First or ResultAggregateKind.Last, 12) =>
                new(col.Name, 203, 536870910, 255, 255, AttrBaseLongNullable, false, null, -1),
            _ => Refused(
                col.Name,
                "NotModelled.AdoAggregateFieldMetadata",
                $"ADO Field metadata for aggregate {col.Aggregate} of DAO type {daoType} ('{col.Name}') is not recorded"),
        };
    }

    /// <summary>
    /// Resolve recorded ADO metadata for unary negation of a stored Decimal column (<c>-N</c>, which CalcDb.Sql
    /// evaluates end-to-end; evidence/r-fix-wire-ado/fields-meta-decimal-expressions #151-168):
    /// <c>adNumeric(131)</c> (DefinedSize=19, Precision=col.Precision, NumericScale=col.Scale, Attr=114, server ActualSize=16).
    /// Binary Decimal arithmetic and Decimal aggregates are currently refused by CalcDb.Sql and are not resolved here.
    /// </summary>
    private static AdoFieldDescriptor? TryResolveDecimalExpression(
        string name,
        SyntaxNode root,
        IReadOnlyList<SourceSlot> sources)
    {
        if (root.Kind == SyntaxKind.Unary && root.Operation == ScalarOperation.Negate && root.Left is not null
            && TryResolveStoredDecimalColumn(root.Left, sources) is { } negCol)
        {
            return new(name, 131, 19, negCol.NumericPrecision, negCol.NumericScale, AttrExprFixed, false, null, -1, DecimalActualSize19OnServer: false);
        }
        return null;
    }

    private static ColumnDescriptor? TryResolveStoredDecimalColumn(SyntaxNode node, IReadOnlyList<SourceSlot> sources)
    {
        if (node.Kind != SyntaxKind.Name || string.IsNullOrEmpty(node.Text))
        {
            return null;
        }
        string raw = node.Text;
        int dot = raw.IndexOf('.');
        ColumnDescriptor? found = null;
        if (dot >= 0)
        {
            string qualifier = raw[..dot];
            string colName = raw[(dot + 1)..];
            foreach (SourceSlot slot in sources)
            {
                if (string.Equals(slot.ScopeName, qualifier, StringComparison.OrdinalIgnoreCase)
                    && slot.Column(colName) is { } col)
                {
                    if (found is not null)
                    {
                        return null;
                    }
                    found = col;
                }
            }
        }
        else
        {
            foreach (SourceSlot slot in sources)
            {
                if (slot.Column(raw) is { } col)
                {
                    if (found is not null)
                    {
                        return null;
                    }
                    found = col;
                }
            }
        }
        return found is { TypeCode: NativeTypes.Numeric, CalculatedResultType: null } ? found : null;
    }

    private static AdoFieldDescriptor Refused(string name, string predicate, string detail) =>
        new(name, 0, 0, 255, 255, 0, false, null, -1, MetadataRefusal: Failure.NotModelled(predicate, detail));

    /// <summary>Descriptor for the single column returned by <c>SELECT @@IDENTITY</c> (r-str-ado-act/ado-execute-action #25-30).</summary>
    public static AdoFieldDescriptor IdentityColumn() =>
        new("Expr1000", 3, 4, 10, 255, AttrExprFixed, false, null, -1);
}

/// <summary>
/// Converts native ISAM / SQL values to ADO VARIANT cells and computes <c>Field.ActualSize</c>
/// (r-ado-1b/fields-meta-mdb, fields-meta-expressions, r-oledb-odbc/buffer-typed-server, qualified.md).
/// </summary>
internal static unsafe class AdoFieldValues
{
    /// <summary>
    /// Project a raw <see cref="NativeValue"/> (from <see cref="IRecordCursor"/>) into an ADO <see cref="OwnedVariant"/>.
    /// Applies ACE's recorded ADO rules:
    /// - Byte: <c>VT_I2</c> on server cursor, <c>VT_UI1</c> on client cursor (r-ado-1b/fields-meta-mdb, qualified.md).
    /// - Guid: uppercase braced <c>"{XXXXXXXX-XXXX-XXXX-XXXX-XXXXXXXXXXXX}"</c> (r-oledb-odbc/buffer-typed-server #415-416).
    /// - Binary/OLE: <c>VT_ARRAY|VT_UI1</c>, or <c>Null</c> when empty (r-ado-1b/fields-meta-mdb).
    /// - Decimal Precision 38 Scale 0: ACE's Lo32-&gt;Mid32 shift quirk (r-ado-1b/fields-meta-mdb #202, qualified.md).
    /// </summary>
    public static OwnedVariant? FromNative(NativeValue v, AdoFieldDescriptor desc, bool isClientCursor, out Failure? refusal)
    {
        refusal = null;
        switch (v.Kind)
        {
            case NativeKind.Null:
                return OwnedVariant.Null();
            case NativeKind.Boolean:
                return desc.AdoType == 2
                    ? OwnedVariant.FromInt16(v.Boolean ? (short)-1 : (short)0)
                    : OwnedVariant.FromBoolean(v.Boolean);
            case NativeKind.Byte:
                return isClientCursor
                    ? OwnedVariant.FromByte((byte)v.Integer)
                    : OwnedVariant.FromInt16((short)(byte)v.Integer);
            case NativeKind.Integer:
                return OwnedVariant.FromInt16((short)v.Integer);
            case NativeKind.Long:
                return OwnedVariant.FromInt32((int)v.Integer);
            case NativeKind.Currency:
                return OwnedVariant.FromCurrency(v.Integer);
            case NativeKind.Single:
                return OwnedVariant.FromSingle(BitConverter.Int32BitsToSingle((int)v.Bits));
            case NativeKind.Double:
                return OwnedVariant.FromDouble(BitConverter.Int64BitsToDouble((long)v.Bits));
            case NativeKind.DateTime:
                return OwnedVariant.FromDate(BitConverter.Int64BitsToDouble((long)v.Bits));
            case NativeKind.Text:
                return OwnedVariant.FromText(v.Text ?? "");
            case NativeKind.Binary:
            case NativeKind.LongBinary:
                return v.Bytes is { Length: > 0 } bytes ? ByteArray(bytes) : OwnedVariant.Null();
            case NativeKind.Guid:
                return OwnedVariant.FromText(new Guid(v.Bytes!).ToString("B").ToUpperInvariant());
            case NativeKind.Decimal:
                return FromNativeDecimal(v.Decimal, desc, out refusal);
            default:
                refusal = Failure.NotImplemented($"ADO Field.Value of native kind {v.Kind}");
                return null;
        }
    }

    private static OwnedVariant? FromNativeDecimal(NativeDecimal d, AdoFieldDescriptor desc, out Failure? refusal)
    {
        refusal = null;
        if (desc.IsBaseTableColumn && desc.BaseColumn is { CalculatedResultType: null, NumericPrecision: 38, NumericScale: 0 })
        {
            // r-ado-1b/fields-meta-mdb #202, qualified.md: ACE reports precision-38/scale-0 Decimal with Lo32 shifted into Mid32.
            if (d.High32 == 0 && (d.Low64 >> 32) == 0)
            {
                return OwnedVariant.FromDecimal(new DecimalValue(0, d.Negative, 0, (d.Low64 & 0xFFFF_FFFFUL) << 32));
            }
            refusal = Failure.NotModelled(
                "NotModelled.AdoPrecision38Decimal",
                $"ADO Field.Value of Precision-38 Decimal with High32={d.High32} Mid32={d.Low64 >> 32} (only Lo32-only values are recorded)");
            return null;
        }
        if (desc.IsBaseTableColumn && desc.BaseColumn is { CalculatedResultType: null, NumericPrecision: > 28 })
        {
            refusal = Failure.NotModelled(
                "NotModelled.AdoHighPrecisionDecimal",
                $"ADO Field.Value of Decimal with Precision={desc.BaseColumn.NumericPrecision}, Scale={desc.BaseColumn.NumericScale} (not recorded)");
            return null;
        }
        if (!d.FitsAutomation)
        {
            refusal = Failure.NotImplemented($"ADO Field.Value of a Decimal beyond VT_DECIMAL ({d.Magnitude} at scale {d.Scale})");
            return null;
        }
        return OwnedVariant.FromDecimal(new DecimalValue(d.Scale, d.Negative, d.High32, d.Low64));
    }

    /// <summary>Project a <see cref="NativeVariant"/> produced by <see cref="SelectCursor.Read"/> into an ADO <see cref="OwnedVariant"/>.</summary>
    public static OwnedVariant? FromSqlVariant(in NativeVariant v, AdoFieldDescriptor desc, bool isClientCursor, out Failure? refusal)
    {
        refusal = null;
        switch ((VarType)v.Vt)
        {
            case VarType.Empty:
                return OwnedVariant.Empty();
            case VarType.Null:
                return OwnedVariant.Null();
            case VarType.Bool:
            {
                bool b = (short)v.Int64 != 0;
                return desc.AdoType == 2
                    ? OwnedVariant.FromInt16(b ? (short)-1 : (short)0)
                    : OwnedVariant.FromBoolean(b);
            }
            case VarType.UI1:
            {
                byte val = (byte)v.Int64;
                return isClientCursor ? OwnedVariant.FromByte(val) : OwnedVariant.FromInt16(val);
            }
            case VarType.I2:
                return OwnedVariant.FromInt16((short)v.Int64);
            case VarType.I4:
                return OwnedVariant.FromInt32((int)v.Int64);
            case VarType.Currency:
                return OwnedVariant.FromCurrency(v.Int64);
            case VarType.R4:
                return OwnedVariant.FromSingle(BitConverter.Int32BitsToSingle((int)v.Int64));
            case VarType.R8:
                return OwnedVariant.FromDouble(v.Double);
            case VarType.Date:
                return OwnedVariant.FromDate(v.Double);
            case VarType.Bstr:
                return v.Pointer == 0 ? OwnedVariant.FromText("") : OwnedVariant.FromText(Bstrs.Chars(v.Pointer));
            case VarType.Decimal:
                return OwnedVariant.Adopt(v);
            case VarType.Array | VarType.UI1:
            {
                if (desc.AdoType is not (204 or 205))
                {
                    refusal = Failure.NotModelled(
                        "NotModelled.AdoSqlBinaryFieldType",
                        $"ADO Field.Value of SQL VT_ARRAY|VT_UI1 for ADO field type {desc.AdoType} ('{desc.Name}') is not recorded");
                    return null;
                }
                nint psa = v.Pointer;
                if (psa == 0)
                {
                    refusal = Failure.NotModelled(
                        "NotModelled.AdoSqlBinaryNullArray",
                        $"ADO Field.Value of null SQL VT_ARRAY|VT_UI1 SAFEARRAY for '{desc.Name}' is not recorded");
                    return null;
                }
                (int Lower, int Count)[] shape = SafeArrays.Shape(psa);
                if (shape.Length != 1 || shape[0].Lower != 0 || shape[0].Count <= 0)
                {
                    refusal = Failure.NotModelled(
                        "NotModelled.AdoSqlBinaryArrayShape",
                        $"ADO Field.Value of SQL VT_ARRAY|VT_UI1 with shape dims={shape.Length}, lower={(shape.Length > 0 ? shape[0].Lower : 0)}, count={(shape.Length > 0 ? shape[0].Count : 0)} for '{desc.Name}' is not recorded");
                    return null;
                }
                int count = shape[0].Count;
                nint copy = SafeArrays.Create(VarType.UI1, (0, count));
                new ReadOnlySpan<byte>(SafeArrays.ElementPointer(psa, 0), count)
                    .CopyTo(new Span<byte>(SafeArrays.ElementPointer(copy, 0), count));
                return OwnedVariant.TakeArray(copy, VarType.UI1);
            }
            default:
                refusal = Failure.NotImplemented($"ADO Field.Value of SQL variant type {(VarType)v.Vt}");
                return null;
        }
    }

    /// <summary>Deep-copy an <see cref="OwnedVariant"/> so a materialized client snapshot or returned Value owns its own payload.</summary>
    public static OwnedVariant Clone(OwnedVariant source)
    {
        NativeVariant* cell = source.Pointer;
        switch ((VarType)cell->Vt)
        {
            case VarType.Empty:
                return OwnedVariant.Empty();
            case VarType.Null:
                return OwnedVariant.Null();
            case VarType.Bool:
                return OwnedVariant.FromBoolean((short)cell->Int64 != 0);
            case VarType.UI1:
                return OwnedVariant.FromByte((byte)cell->Int64);
            case VarType.I2:
                return OwnedVariant.FromInt16((short)cell->Int64);
            case VarType.I4:
                return OwnedVariant.FromInt32((int)cell->Int64);
            case VarType.I8:
                return OwnedVariant.FromInt64(cell->Int64);
            case VarType.Currency:
                return OwnedVariant.FromCurrency(cell->Int64);
            case VarType.R4:
                return OwnedVariant.FromSingle(BitConverter.Int32BitsToSingle((int)cell->Int64));
            case VarType.R8:
                return OwnedVariant.FromDouble(cell->Double);
            case VarType.Date:
                return OwnedVariant.FromDate(cell->Double);
            case VarType.Decimal:
                return OwnedVariant.FromDecimal(source.AsDecimal());
            case VarType.Bstr:
                return cell->Pointer == 0 ? OwnedVariant.NullBstr() : OwnedVariant.FromText(Bstrs.Chars(cell->Pointer));
            case VarType.Array | VarType.UI1:
            {
                nint psa = cell->Pointer;
                if (psa == 0)
                {
                    return OwnedVariant.Null();
                }
                (int lower, int count) = SafeArrays.Shape(psa)[0];
                if (count <= 0)
                {
                    return OwnedVariant.Null();
                }
                byte[] copy = new Span<byte>(SafeArrays.ElementPointer(psa, lower), count).ToArray();
                return ByteArray(copy);
            }
            default:
                throw new InvalidOperationException($"Unsupported cloned ADO variant type {(VarType)cell->Vt}.");
        }
    }

    /// <summary>
    /// Compute <c>Field.ActualSize</c> for <paramref name="value"/> (r-ado-1b/fields-meta-mdb, fields-meta-expressions,
    /// r-fix-wire-ado/fields-meta-decimal-expressions):
    /// 0 for Null or empty string, character byte length (<c>chars * 2</c>) for text/memo, byte length for binary/OLE,
    /// 19 for client-cursor Decimal and server-cursor base-table / Sum / Min / Max Decimal, 16 for other server-cursor
    /// Decimal expressions, and fixed byte width for fixed types.
    /// </summary>
    public static int ActualSizeOf(OwnedVariant value, AdoFieldDescriptor desc, bool isClientCursor)
    {
        VarType vt = value.Type;
        if (vt is VarType.Null or VarType.Empty)
        {
            return 0;
        }
        return desc.AdoType switch
        {
            11 => 2,  // adBoolean
            17 => 1,  // adUnsignedTinyInt
            2 => 2,   // adSmallInt
            3 => 4,   // adInteger
            4 => 4,   // adSingle
            5 => 8,   // adDouble
            6 => 8,   // adCurrency
            7 => 8,   // adDate
            72 => 16, // adGUID
            131 => (isClientCursor || desc.DecimalActualSize19OnServer) ? 19 : 16, // adNumeric (fields-meta-expressions, fields-meta-decimal-expressions)
            202 or 203 => vt == VarType.Bstr && value.Pointer->Pointer != 0 ? checked((int)OleAut.SysStringByteLen(value.Pointer->Pointer)) : 0,
            204 or 205 => vt == (VarType.Array | VarType.UI1) && value.Pointer->Pointer != 0 ? SafeArrays.Shape(value.Pointer->Pointer)[0].Count : 0,
            _ => 0,
        };
    }

    private static OwnedVariant ByteArray(byte[] bytes)
    {
        nint array = SafeArrays.Create(VarType.UI1, (0, bytes.Length));
        bytes.CopyTo(new Span<byte>(SafeArrays.ElementPointer(array, 0), bytes.Length));
        return OwnedVariant.TakeArray(array, VarType.UI1);
    }
}

/// <summary>
/// ADODB.Fields (<c>Fields</c>, dual): one live collection per <see cref="RecordsetObject"/>.
/// Column descriptors survive <c>Recordset.Close()</c> while the Connection stays open
/// (r-str-ado-act/ado-rs-read-close #283, r-q-wire/retain-ado-field-after-recordset-and-connection-close #29,
/// r-q2-wire/retain-enum-ado-after-close #24-31), and detach (Count -&gt; 0) when <c>Connection.Close()</c> runs.
/// Previously handed-out <see cref="AdoFieldObject"/> wrappers are permanently invalidated on <c>Recordset.Close()</c>
/// (0x800A0D5C, r-str-ado-act/ado-rs-read-close #279-288).
/// </summary>
internal sealed unsafe class AdoFieldsObject : AdoObject
{
    private static readonly InterfaceDecl Decl = AdoTypes.Fields;
    private static readonly nint Vtable = DualEntries.Build(Decl);
    private static readonly OperationTable Operations = new OperationTable(Decl)
        .Get("Count", (self, args) => ((AdoFieldsObject)self).Count())
        .Get("Item", (self, args) => ((AdoFieldsObject)self).Item(args[0]))
        .Method("Refresh", (self, args) => CallResult.Nothing)
        .Method("_NewEnum", (self, args) => ((AdoFieldsObject)self).NewEnum());

    private readonly RecordsetObject _recordset;
    private AdoFieldDescriptor[] _columns = [];
    private AdoFieldObject?[] _wrappers = [];
    private int _fieldGeneration;
    private MappedProviderError? _pendingPrepareError;

    public AdoFieldsObject(RecordsetObject recordset) : base(Decl, Vtable, "ADODB.Fields")
    {
        _recordset = recordset;
    }

    protected override OperationTable Table => Operations;

    public RecordsetObject Recordset => _recordset;

    public int ColumnCount
    {
        get
        {
            DetachIfConnectionClosed();
            return _columns.Length;
        }
    }

    public int FieldGeneration => _fieldGeneration;

    public AdoFieldDescriptor Descriptor(int ordinal) => _columns[ordinal];

    private void DetachIfConnectionClosed()
    {
        if (!_recordset.IsOpen && !_recordset.IsActiveConnectionOpen && _columns.Length > 0)
        {
            Detach();
        }
    }

    /// <summary>Install new columns on a successful <c>Recordset.Open</c>, invalidating any previous wrappers.</summary>
    public void Bind(AdoFieldDescriptor[] columns)
    {
        InvalidateWrappers();
        _pendingPrepareError = null;
        _columns = columns;
        _wrappers = columns.Length == 0 ? [] : new AdoFieldObject?[columns.Length];
    }

    /// <summary>Record a prepare/syntax error from a failed <c>Recordset.Open</c> (r-ado-1b/rs-source-kinds #66/#226/#242, r-str-ado-act/ado-rs-read-close #16/#18).</summary>
    public void RecordFailedOpen(MappedProviderError? prepareError)
    {
        InvalidateWrappers();
        _columns = [];
        _wrappers = [];
        _pendingPrepareError = prepareError;
    }

    /// <summary>
    /// Called on <c>Recordset.Close()</c>: invalidates previously handed-out Field wrappers (0x800A0D5C)
    /// while keeping <see cref="_columns"/> for <c>Fields.Count</c> and enumerators while the Connection stays open.
    /// </summary>
    public void OnRecordsetClosed()
    {
        InvalidateWrappers();
        _wrappers = _columns.Length == 0 ? [] : new AdoFieldObject?[_columns.Length];
    }

    /// <summary>Called on <c>Connection.Close()</c>: detaches retained columns (Count -&gt; 0) and invalidates wrappers.</summary>
    public void Detach()
    {
        InvalidateWrappers();
        _pendingPrepareError = null;
        _columns = [];
        _wrappers = [];
    }

    private void InvalidateWrappers()
    {
        _fieldGeneration++;
        foreach (AdoFieldObject? wrapper in _wrappers)
        {
            wrapper?.Release();
        }
        _wrappers = [];
    }

    public AdoFieldObject GetOrCreateField(int ordinal) =>
        _wrappers[ordinal] ??= new AdoFieldObject(this, ordinal, _fieldGeneration);

    private CallResult Count()
    {
        DetachIfConnectionClosed();
        if (_pendingPrepareError is { } err)
        {
            _recordset.ReportProviderError(err);
            return CallResult.Fail(err.ComFailure);
        }
        return Int32(_columns.Length);
    }

    private CallResult Item(nint index)
    {
        DetachIfConnectionClosed();
        if (_pendingPrepareError is { } err)
        {
            _recordset.ReportProviderError(err);
            return CallResult.Fail(err.ComFailure);
        }
        if (index == 0)
        {
            return NotImplemented("Fields.Item with Missing index");
        }
        NativeVariant* v = Args.Cell(index);
        int ordinal = -1;
        switch ((VarType)v->Vt)
        {
            case VarType.I2:
            case VarType.I4:
            {
                long i = (VarType)v->Vt == VarType.I2 ? (short)v->Int64 : (int)v->Int64;
                if (i >= 0 && i < _columns.Length)
                {
                    ordinal = (int)i;
                }
                break;
            }
            case VarType.Bstr:
            {
                string? name = Args.Text(index);
                if (name is null)
                {
                    return NotImplemented("Fields.Item with null BSTR index");
                }
                for (int k = 0; k < _columns.Length; k++)
                {
                    if (string.Equals(_columns[k].Name, name, StringComparison.OrdinalIgnoreCase))
                    {
                        ordinal = k;
                        break;
                    }
                }
                break;
            }
            default:
                return NotImplemented($"Fields.Item with index type {(VarType)v->Vt}");
        }
        if (ordinal < 0)
        {
            _recordset.ClearConnectionErrors();
            return Fail(AdoErrors.ItemNotFound);
        }
        return Object(GetOrCreateField(ordinal));
    }

    private CallResult NewEnum()
    {
        DetachIfConnectionClosed();
        if (_pendingPrepareError is { } err)
        {
            _recordset.ReportProviderError(err);
            return CallResult.Fail(err.ComFailure);
        }
        var enumerator = new AdoEnumVariant(this);
        nint unknown;
        enumerator.QueryInterface(ComIids.IUnknown, &unknown);
        enumerator.Release();
        return CallResult.Of(OwnedVariant.TakeObject(unknown, VarType.Unknown));
    }

    protected override void OnFinalRelease() => InvalidateWrappers();
}

/// <summary>
/// ADODB.Field (<c>Field</c>, dual): one column wrapper of a <see cref="RecordsetObject"/>.
/// Tracks its owning <see cref="AdoFieldsObject"/> generation and permanently fails 0x800A0D5C
/// (<c>Object is no longer valid.</c>) after <c>Recordset.Close()</c> (r-str-ado-act/ado-rs-read-close #279-288).
/// </summary>
internal sealed unsafe class AdoFieldObject : AdoObject
{
    private static readonly InterfaceDecl Decl = AdoTypes.Field;
    private static delegate* unmanaged<nint, int, Guid*, uint, ushort, DispParams*, NativeVariant*, ExcepInfo*, uint*, int> _baseInvoke;
    private static readonly nint Vtable = BuildVtable();

    private static nint BuildVtable()
    {
        nint* table = (nint*)DualEntries.Build(Decl);
        _baseInvoke = (delegate* unmanaged<nint, int, Guid*, uint, ushort, DispParams*, NativeVariant*, ExcepInfo*, uint*, int>)table[6];
        table[6] = (nint)(delegate* unmanaged<nint, int, Guid*, uint, ushort, DispParams*, NativeVariant*, ExcepInfo*, uint*, int>)&InvokeField;
        return (nint)table;
    }

    [UnmanagedCallersOnly]
    private static int InvokeField(
        nint self,
        int dispId,
        Guid* iid,
        uint lcid,
        ushort flags,
        DispParams* parameters,
        NativeVariant* result,
        ExcepInfo* info,
        uint* argError)
    {
        // Inspect raw GetChunk(Length) argument BEFORE DispatchObject coerces non-I4 values to I4
        // (evidence/r-oledb-odbc/stream-getchunk records only 1 positional VT_I4 argument).
        if (dispId == 1108 && (flags & 1) != 0)
        {
            AdoFieldObject field = Recover<AdoFieldObject>(self);
            field._getChunkBadArgReason = CheckGetChunkRawArg(parameters);
            try
            {
                return _baseInvoke(self, dispId, iid, lcid, flags, parameters, result, info, argError);
            }
            finally
            {
                field._getChunkBadArgReason = null;
            }
        }
        return _baseInvoke(self, dispId, iid, lcid, flags, parameters, result, info, argError);
    }

    private static string? CheckGetChunkRawArg(DispParams* parameters)
    {
        if (parameters is null || parameters->ArgCount != 1 || parameters->NamedArgCount != 0 || parameters->Args == 0)
        {
            return "Field.GetChunk requires exactly 1 positional I4 argument (not recorded)";
        }
        NativeVariant* cell = Args.Cell(parameters->Args);
        VarType vt = (VarType)cell->Vt & ~VarType.ByRef;
        return vt == VarType.I4
            ? null
            : $"Field.GetChunk with non-I4 argument (vt={(ushort)vt}) is not recorded";
    }

    private static readonly OperationTable Operations = new OperationTable(Decl)
        .Get("Name", (self, args) => Live(self, f => Text(f.Desc.Name)))
        .Get("Type", (self, args) => Meta(self, d => Int32(d.AdoType)))
        .Get("DefinedSize", (self, args) => Meta(self, d => Int32(d.DefinedSize)))
        .Get("Precision", (self, args) => Meta(self, d => CallResult.OfScalar(VarType.UI1, d.Precision)))
        .Get("NumericScale", (self, args) => Meta(self, d => CallResult.OfScalar(VarType.UI1, d.NumericScale)))
        .Get("Attributes", (self, args) => Meta(self, d => Int32(d.Attributes)))
        .Get("ActualSize", (self, args) => Live(self, f => f.ActualSize()))
        .Get("Value", (self, args) => Live(self, f => f.GetValue()))
        .Put("Value", (self, args) =>
        {
            nint val = args[0];
            return Live(self, f => f.PutValue(val));
        })
        .Method("GetChunk", (self, args) => ((AdoFieldObject)self).GetChunk(args[0]))
        .Get("OriginalValue", (self, args) => Live(self, f => f.GetOriginalValue()))
        .Get("UnderlyingValue", (self, args) => Live(self, f => f.GetUnderlyingValue()))
        .Get("Status", (self, args) => Live(self, f => f.GetStatus()))
        .Get("Properties", (self, args) => Live(self, _ => NotImplemented("Field.Properties (not recorded for ADO)")));

    private readonly AdoFieldsObject _fields;
    private readonly int _ordinal;
    private readonly int _generation;
    private string? _getChunkBadArgReason;

    public AdoFieldObject(AdoFieldsObject fields, int ordinal, int generation) : base(Decl, Vtable, "ADODB.Field")
    {
        _fields = fields;
        _ordinal = ordinal;
        _generation = generation;
        fields.AddRef();
    }

    protected override OperationTable Table => Operations;

    protected override Failure? ArgumentCountFailure(MemberDecl member) =>
        member.Name == "GetChunk"
            ? Failure.NotModelled("NotModelled.AdoGetChunkArg", "Field.GetChunk requires exactly 1 positional I4 argument (not recorded)")
            : null;

    protected override Failure? ArgumentConversionFailure(MemberDecl member, bool missing) =>
        member.Name == "GetChunk"
            ? Failure.NotModelled(
                "NotModelled.AdoGetChunkArg",
                missing ? "Field.GetChunk with Missing argument is not recorded" : "Field.GetChunk with non-I4 argument is not recorded")
            : null;

    private AdoFieldDescriptor Desc => _fields.Descriptor(_ordinal);

    private bool IsValid => _generation == _fields.FieldGeneration && _ordinal < _fields.ColumnCount;

    private static CallResult Live(ComObject self, Func<AdoFieldObject, CallResult> op)
    {
        var f = (AdoFieldObject)self;
        if (!f.IsValid)
        {
            f._fields.Recordset.ClearConnectionErrors();
            return f.Fail(AdoErrors.ObjectNoLongerValid);
        }
        return op(f);
    }

    private static CallResult Meta(ComObject self, Func<AdoFieldDescriptor, CallResult> op) =>
        Live(self, f => f.Desc.MetadataRefusal is { } refusal ? CallResult.Fail(refusal) : op(f.Desc));

    private CallResult GetChunk(nint lengthArg)
    {
        if (_getChunkBadArgReason is { } badArg)
        {
            return CallResult.Fail(Failure.NotModelled("NotModelled.AdoGetChunkArg", badArg));
        }
        if (!IsValid || !_fields.Recordset.IsOpen)
        {
            return CallResult.Fail(Failure.NotModelled(
                "NotModelled.AdoGetChunkClosed",
                "Field.GetChunk on a closed Recordset or invalidated Field wrapper is not recorded"));
        }
        if (lengthArg == 0 || ((VarType)Args.Cell(lengthArg)->Vt & ~VarType.ByRef) != VarType.I4)
        {
            return CallResult.Fail(Failure.NotModelled(
                "NotModelled.AdoGetChunkArg",
                "Field.GetChunk requires a single I4 Length argument (not recorded)"));
        }
        int length = (int)Args.Cell(lengthArg)->Int64;
        return _fields.Recordset.ReadFieldGetChunk(_ordinal, length, this);
    }

    private CallResult GetValue()
    {
        if (!_fields.Recordset.IsOpen)
        {
            _fields.Recordset.ClearConnectionErrors();
            return Fail(AdoErrors.NoCurrentRecord);
        }
        return _fields.Recordset.ReadFieldValue(_ordinal, this);
    }

    private CallResult GetOriginalValue()
    {
        if (!_fields.Recordset.IsOpen)
        {
            return NotImplemented("Field.OriginalValue on a closed Recordset (not recorded)");
        }
        return _fields.Recordset.ReadFieldOriginalValue(_ordinal, this);
    }

    private CallResult GetUnderlyingValue()
    {
        if (!_fields.Recordset.IsOpen)
        {
            return NotImplemented("Field.UnderlyingValue on a closed Recordset (not recorded)");
        }
        return _fields.Recordset.ReadFieldUnderlyingValue(_ordinal, this);
    }

    private CallResult GetStatus()
    {
        if (!_fields.Recordset.IsOpen)
        {
            return NotImplemented("Field.Status on a closed Recordset (not recorded)");
        }
        return _fields.Recordset.ReadFieldStatus(_ordinal, this);
    }

    private CallResult PutValue(nint arg)
    {
        if (!_fields.Recordset.IsOpen)
        {
            // evidence/r-str-ado-act/ado-rs-read-close #284-285: closed Recordset operations fail 0x800A0E78.
            _fields.Recordset.ClearConnectionErrors();
            return Fail(AdoErrors.ObjectClosed);
        }
        return _fields.Recordset.WriteFieldValue(_ordinal, arg, this);
    }

    private CallResult ActualSize()
    {
        if (!_fields.Recordset.IsOpen)
        {
            _fields.Recordset.ClearConnectionErrors();
            return Fail(AdoErrors.NoCurrentRecord);
        }
        return _fields.Recordset.ReadFieldActualSize(_ordinal, this);
    }

    protected override void OnFinalRelease() => _fields.Release();
}

/// <summary>
/// COM <c>IEnumVARIANT</c> (<c>{00020404-0000-0000-C000-000000000046}</c>) over <see cref="AdoFieldsObject"/>
/// (r-q2-wire/retain-enum-ado-after-close: continues over retained columns after Recordset.Close while Connection stays
/// open, yields S_FALSE once Connection closes and detaches them).
/// </summary>
internal sealed unsafe class AdoEnumVariant : ComObject
{
    public static readonly Guid IidEnumVariant = new("00020404-0000-0000-C000-000000000046");

    private static readonly nint EnumVtable = BuildVtable();

    private readonly AdoFieldsObject _fields;
    private int _index;

    public AdoEnumVariant(AdoFieldsObject fields, int index = 0) : base(EnumVtable)
    {
        _fields = fields;
        _index = index;
        fields.AddRef();
    }

    protected override int HeadFor(in Guid iid) => iid == IidEnumVariant ? 0 : -1;

    private int Next(uint celt, NativeVariant* rgVar, uint* pceltFetched)
    {
        if (rgVar is null)
        {
            return HResults.E_POINTER;
        }
        if (pceltFetched is null && celt > 1)
        {
            return HResults.E_INVALIDARG;
        }
        uint fetched = 0;
        int count = _fields.ColumnCount;
        while (fetched < celt && _index < count)
        {
            AdoFieldObject field = _fields.GetOrCreateField(_index++);
            nint dispatch;
            field.QueryInterface(ComIids.IDispatch, &dispatch);
            OleAut.VariantInit(rgVar + fetched);
            rgVar[fetched].Vt = (ushort)VarType.Dispatch;
            rgVar[fetched].Pointer = dispatch;
            fetched++;
        }
        if (pceltFetched is not null)
        {
            *pceltFetched = fetched;
        }
        return fetched == celt ? HResults.S_OK : HResults.S_FALSE;
    }

    private int Skip(uint celt)
    {
        if (celt == 0)
        {
            return HResults.S_OK;
        }
        long next = _index + (long)celt;
        int count = _fields.ColumnCount;
        // r-q2-wire/retain-enum-ado-after-close #29-31: Skip(1) from index 1 of 2 returns S_FALSE.
        if (next >= count)
        {
            _index = count;
            return HResults.S_FALSE;
        }
        _index = (int)next;
        return HResults.S_OK;
    }

    private int Reset()
    {
        _index = 0;
        return HResults.S_OK;
    }

    private int Clone(nint* ppenum)
    {
        if (ppenum is null)
        {
            return HResults.E_POINTER;
        }
        var copy = new AdoEnumVariant(_fields, _index);
        int hr = copy.QueryInterface(IidEnumVariant, ppenum);
        copy.Release();
        return hr;
    }

    protected override void OnFinalRelease() => _fields.Release();

    private static nint BuildVtable()
    {
        nint* table = (nint*)NativeMemory.AllocZeroed(7, (nuint)sizeof(nint));
        table[0] = (nint)(delegate* unmanaged<nint, Guid*, nint*, int>)&QueryInterfaceEntry;
        table[1] = (nint)(delegate* unmanaged<nint, uint>)&AddRefEntry;
        table[2] = (nint)(delegate* unmanaged<nint, uint>)&ReleaseEntry;
        table[3] = (nint)(delegate* unmanaged<nint, uint, NativeVariant*, uint*, int>)&NextEntry;
        table[4] = (nint)(delegate* unmanaged<nint, uint, int>)&SkipEntry;
        table[5] = (nint)(delegate* unmanaged<nint, int>)&ResetEntry;
        table[6] = (nint)(delegate* unmanaged<nint, nint*, int>)&CloneEntry;
        return (nint)table;
    }

    [UnmanagedCallersOnly]
    private static int QueryInterfaceEntry(nint self, Guid* iid, nint* result) =>
        result is null ? HResults.E_POINTER : Recover<AdoEnumVariant>(self).QueryInterface(*iid, result);

    [UnmanagedCallersOnly]
    private static uint AddRefEntry(nint self) => Recover<AdoEnumVariant>(self).AddRef();

    [UnmanagedCallersOnly]
    private static uint ReleaseEntry(nint self) => Recover<AdoEnumVariant>(self).Release();

    [UnmanagedCallersOnly]
    private static int NextEntry(nint self, uint celt, NativeVariant* rgVar, uint* pceltFetched) =>
        Recover<AdoEnumVariant>(self).Next(celt, rgVar, pceltFetched);

    [UnmanagedCallersOnly]
    private static int SkipEntry(nint self, uint celt) => Recover<AdoEnumVariant>(self).Skip(celt);

    [UnmanagedCallersOnly]
    private static int ResetEntry(nint self) => Recover<AdoEnumVariant>(self).Reset();

    [UnmanagedCallersOnly]
    private static int CloneEntry(nint self, nint* ppenum) => Recover<AdoEnumVariant>(self).Clone(ppenum);
}
