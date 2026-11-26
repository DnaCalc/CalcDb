using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using CalcDb.Com.Com;
using CalcDb.Foundation;
using CalcDb.Values;

namespace CalcDb.Com.Ado;

/// <summary>Schema metadata for one persisted ADO Recordset column (W-09/events-persistence-inventory, II-12 §24).</summary>
internal sealed record PersistedColumn(
    string Name,
    int AdoType,
    int DefinedSize,
    byte Precision,
    byte NumericScale,
    int Attributes,
    string? BaseTableName = null,
    string? BaseColumnName = null,
    bool IsKeyColumn = false)
{
    public AdoFieldDescriptor ToFieldDescriptor() =>
        new(
            Name,
            AdoType,
            DefinedSize,
            Precision,
            NumericScale,
            Attributes,
            IsBaseTableColumn: !string.IsNullOrEmpty(BaseTableName),
            BaseColumn: null,
            BaseSourceIndex: -1,
            DecimalActualSize19OnServer: AdoType == 131);
}

/// <summary>One persisted row with its lifecycle state and Current/Original value channels.</summary>
internal sealed class PersistedRow : IDisposable
{
    public PersistedRow(
        ClientRowState state,
        OwnedVariant[] current,
        OwnedVariant[] original,
        bool[] modifiedColumns,
        long stagingOrder)
    {
        State = state;
        Current = current;
        Original = original;
        ModifiedColumns = modifiedColumns;
        StagingOrder = stagingOrder;
    }

    public ClientRowState State { get; }

    public OwnedVariant[] Current { get; }

    public OwnedVariant[] Original { get; }

    public bool[] ModifiedColumns { get; }

    public long StagingOrder { get; }

    public void Dispose()
    {
        for (int i = 0; i < Current.Length; i++)
        {
            Current[i]?.Dispose();
            Original[i]?.Dispose();
        }
    }
}

/// <summary>Complete persisted Recordset snapshot (schema, base-table provenance, and rows with pending changes).</summary>
internal sealed class PersistedRecordsetData : IDisposable
{
    public PersistedRecordsetData(
        PersistedColumn[] columns,
        List<PersistedRow> rows,
        string? baseTableName,
        bool isUpdatable)
    {
        Columns = columns;
        Rows = rows;
        BaseTableName = baseTableName;
        IsUpdatable = isUpdatable;
    }

    public PersistedColumn[] Columns { get; }

    public List<PersistedRow> Rows { get; }

    public string? BaseTableName { get; }

    public bool IsUpdatable { get; }

    public void Dispose()
    {
        foreach (PersistedRow row in Rows)
        {
            row.Dispose();
        }
        Rows.Clear();
    }
}

/// <summary>
/// Shared entry point for ADO Recordset persistence (<c>adPersistADTG = 0</c>, <c>adPersistXML = 1</c>,
/// <c>adCmdFile = 256</c>, <c>Provider=MSPersist</c>; R-ADO-1 ADO-PERSIST, II-12 §24).
/// Dispatches the recorded malformed-source errors from <c>evidence/r-ado-1b/persist-malformed-files</c>.
/// </summary>
internal static class AdoPersistence
{
    public const int AdPersistAdtg = 0;
    public const int AdPersistXml = 1;

    internal const string EmptyOrEosMessage =
        "Stream object cannot be read because it is empty, or the current position is at the end of the stream. For non-empty streams, set the current position with the Position property. To determine if a stream is empty, check the Size property.";

    internal const string InvalidSourceFormatMessage =
        "Recordset cannot be created from the specified source. The source file or stream must contain Recordset data in XML or ADTG format.";

    internal const string InvalidXmlMessage =
        "Recordset cannot be created. Source XML is incomplete or invalid.";

    public static bool TryLoadFile(string path, out PersistedRecordsetData? data, out Failure? error)
    {
        data = null;
        error = null;
        if (!File.Exists(path))
        {
            // r-ado-1b/persist-malformed-files #86: 0x80030002 "File <path> cannot be found."
            error = AdoObject.PersistenceFailure(AdoObject.HResultFileNotFound, $"File {path} cannot be found.");
            return false;
        }

        byte[] raw;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (fs.Length > int.MaxValue)
            {
                error = Failure.NotModelled("NotModelled.AdoPersistLargeFile", "Persisted Recordset file above 2 GB is not recorded");
                return false;
            }
            raw = new byte[(int)fs.Length];
            fs.ReadExactly(raw);
        }
        catch (FileNotFoundException)
        {
            error = AdoObject.PersistenceFailure(AdoObject.HResultFileNotFound, $"File {path} cannot be found.");
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            error = AdoObject.PersistenceFailure(AdoObject.HResultFail, InvalidSourceFormatMessage);
            return false;
        }

        return TryDecodeBytes(raw, out data, out error);
    }

    public static bool TryDecodeBytes(ReadOnlySpan<byte> raw, out PersistedRecordsetData? data, out Failure? error)
    {
        data = null;
        error = null;

        // r-ado-1b/persist-malformed-files #6 (0B), #15 (1B), #24 (3B), #42 (4B "<xml"), #51 (3B UTF-8 BOM):
        // inputs of <= 4 bytes fail with the empty/EOS persistence error.
        if (raw.Length <= 4)
        {
            error = AdoObject.PersistenceFailure(AdoObject.HResultFail, EmptyOrEosMessage);
            return false;
        }

        if (raw[0] == 0x01 && raw[1] == 0x07 && raw[2] == (byte)'T' && raw[3] == (byte)'G')
        {
            return AdoAdtgCodec.TryDecode(raw, out data, out error);
        }

        if (LooksLikeAdoXmlRoot(raw))
        {
            return AdoXmlPersistCodec.TryDecode(raw, out data, out error);
        }

        // r-ado-1b/persist-malformed-files #33 (512 zero bytes), #69 ("<?xml version=\"1.0\"?>"):
        // non-ADTG, non-"<xml" inputs fail with the source-format error.
        error = AdoObject.PersistenceFailure(AdoObject.HResultFail, InvalidSourceFormatMessage);
        return false;
    }

    private static bool LooksLikeAdoXmlRoot(ReadOnlySpan<byte> raw)
    {
        // UTF-16LE BOM + "<xml"
        if (raw.Length >= 10 && raw[0] == 0xFF && raw[1] == 0xFE)
        {
            int pos = 2;
            while (pos + 1 < raw.Length && IsAsciiWhitespace((byte)raw[pos]) && raw[pos + 1] == 0)
            {
                pos += 2;
            }
            if (pos + 8 <= raw.Length)
            {
                return raw[pos] == (byte)'<' && raw[pos + 1] == 0
                    && (raw[pos + 2] | 0x20) == (byte)'x' && raw[pos + 3] == 0
                    && (raw[pos + 4] | 0x20) == (byte)'m' && raw[pos + 5] == 0
                    && (raw[pos + 6] | 0x20) == (byte)'l' && raw[pos + 7] == 0;
            }
            return false;
        }

        int offset = raw.Length >= 3 && raw[0] == 0xEF && raw[1] == 0xBB && raw[2] == 0xBF ? 3 : 0;
        while (offset < raw.Length && IsAsciiWhitespace(raw[offset]))
        {
            offset++;
        }
        return offset + 4 <= raw.Length
            && raw[offset] == (byte)'<'
            && (raw[offset + 1] | 0x20) == (byte)'x'
            && (raw[offset + 2] | 0x20) == (byte)'m'
            && (raw[offset + 3] | 0x20) == (byte)'l';
    }

    private static bool IsAsciiWhitespace(byte b) => b is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n';
}

/// <summary>
/// Independent MS-ADTG TableGram codec ([MS-ADTG] §2.2.3.14; II-12 §24; W-09/events-persistence-inventory).
/// Verified byte-for-byte against the 568-byte read-only <c>T</c> TableGram in <c>evidence/r-ado-1b/persist-stream-roundtrip</c>,
/// <c>evidence/r-ado-1b/persist-overwrite-flags</c>, <c>evidence/r-fix-wire-ado/persist-save-two-parameter</c>,
/// and <c>evidence/r-q45-querydef-stream-rollback/stream-save-into-open</c> (<c>sha256 03cdca6d816d...</c>).
/// Pending-change (<c>New</c>/<c>Modified</c>/<c>Deleted</c>) and multi-type shapes use [MS-ADTG] §2.2.3.14.3 row tokens
/// as an interim roundtrip encoding pending byte-kept captures (W-04).
/// </summary>
internal static unsafe class AdoAdtgCodec
{
    private const byte TokenHeader = 0x01;
    private const byte TokenColumnDescriptor = 0x06;
    private const byte TokenUnchangedRow = 0x07;
    private const byte TokenInsertRow = 0x08;
    private const byte TokenDeleteRow = 0x09;
    private const byte TokenChangeRow = 0x0A;
    private const byte TokenDone = 0x0F;
    private const byte TokenRowsetProperties = 0x10;

    private const ushort ColPropBaseTable = 0x0001;
    private const ushort ColPropBaseColumn = 0x0002;
    private const ushort ColPropIsKeyColumn = 0x0003;

    // Published MS-ADTG / OLE DB GUIDs confirmed in r-ado-1b/persist-stream-roundtrip #25:
    private static readonly byte[] HandlerGuid = new Guid("3FF292B6-B204-11CF-8D23-00AA005FFE58").ToByteArray();
    private static readonly byte[] ResultDescriptorGuid = new Guid("F663ADD2-EB02-11CF-B0E3-00AA003F000F").ToByteArray();
    private static readonly byte[] PropSetAdcGuid = new Guid("B68E3CC1-6DEB-11D0-8DF6-00AA005FFE58").ToByteArray();
    private static readonly byte[] PropSetRowsetGuid = new Guid("C8B522BE-5CF3-11CE-ADE5-00AA0044773D").ToByteArray();

    public static byte[] Encode(PersistedRecordsetData data, out Failure? refusal)
    {
        refusal = null;
        using var ms = new MemoryStream(512);
        using var w = new BinaryWriter(ms, Encoding.Unicode, leaveOpen: true);

        // 1. adtgTokenHeader: 01 07 "TG" 21 00 00 00 00 (9 bytes)
        w.Write(TokenHeader);
        w.Write((byte)0x07);
        w.Write((byte)'T');
        w.Write((byte)'G');
        w.Write(0x00000021u);
        w.Write((byte)0x00);

        // 2. adtgTokenHandlerOptions (02, len 25)
        w.Write((byte)0x02);
        w.Write((ushort)25);
        w.Write(HandlerGuid);
        w.Write(1u); // version
        w.Write(0u); // options
        w.Write((byte)0); // cHandlerOptions

        // 3. adtgTokenResultDescriptor (03, len 103)
        w.Write((byte)0x03);
        w.Write((ushort)103);
        w.Write(ResultDescriptorGuid);
        w.Write((byte)0);
        w.Write((byte)0);
        w.Write((byte)0);
        w.Write((ushort)data.Columns.Length);
        w.Write((ushort)data.Columns.Length);
        w.Write((ushort)0);
        w.Write(0u);
        w.Write((uint)data.Rows.Count);
        w.Write((ushort)1); // 1 property set (DBPROPSET_ADC)
        w.Write(PropSetAdcGuid);
        w.Write((ushort)7);
        WritePropI4(w, 0x0B, 1);
        WritePropI4(w, 0x13, 1);
        WritePropEmpty(w, 0x0D);
        WritePropEmpty(w, 0x0E);
        WritePropEmpty(w, 0x0F);
        WritePropEmpty(w, 0x10);
        WritePropEmpty(w, 0x12);

        // 4. adtgTokenRowsetProperties (10, len 124)
        w.Write(TokenRowsetProperties);
        w.Write((ushort)124);
        w.Write((ushort)2); // 2 property sets: DBPROPSET_ROWSET + DBPROPSET_ADC
        w.Write(PropSetRowsetGuid);
        w.Write((ushort)4);
        WritePropI2(w, 0x7F, 0);
        WritePropI2(w, 0x86, 0);
        WritePropI4(w, 0x22, 0);
        WritePropI4(w, 0x49, 0);
        w.Write(PropSetAdcGuid);
        w.Write((ushort)5);
        WritePropI4(w, 4, 15);
        WritePropI4(w, 5, 2);
        WritePropI4(w, 3, 15);
        WritePropI4(w, 7, 50);
        WritePropI4(w, 8, 3);

        // 5. adtgTokenColumnDescriptor (06) for each column
        for (int i = 0; i < data.Columns.Length; i++)
        {
            PersistedColumn col = data.Columns[i];
            if (!TryMapAdoTypeToDbType(col.AdoType, out ushort dbType))
            {
                refusal = Failure.NotModelled("NotModelled.AdoPersistAdtgType", $"ADTG persistence for ADO field type {col.AdoType} ('{col.Name}') is not recorded");
                return [];
            }

            byte[] colBody = BuildColumnDescriptorBody(col, (ushort)(i + 1), dbType, data.IsUpdatable);
            w.Write(TokenColumnDescriptor);
            w.Write((ushort)colBody.Length);
            w.Write(colBody);
        }

        // 6. Row operations
        foreach (PersistedRow row in data.Rows)
        {
            switch (row.State)
            {
                case ClientRowState.Clean:
                    w.Write(TokenUnchangedRow);
                    if (!TryWriteRowValues(w, data.Columns, row.Current, out refusal))
                    {
                        return [];
                    }
                    break;
                case ClientRowState.Modified:
                    w.Write(TokenChangeRow);
                    if (!TryWriteRowValues(w, data.Columns, row.Original, out refusal))
                    {
                        return [];
                    }
                    WritePresenceMask(w, row.ModifiedColumns);
                    if (!TryWriteMaskedRowValues(w, data.Columns, row.Current, row.ModifiedColumns, out refusal))
                    {
                        return [];
                    }
                    break;
                case ClientRowState.New:
                    w.Write(TokenInsertRow);
                    if (!TryWriteRowValues(w, data.Columns, row.Current, out refusal))
                    {
                        return [];
                    }
                    break;
                case ClientRowState.Deleted:
                    w.Write(TokenDeleteRow);
                    if (!TryWriteRowValues(w, data.Columns, row.Original, out refusal))
                    {
                        return [];
                    }
                    break;
            }
        }

        // 7. adtgTokenDone
        w.Write(TokenDone);
        w.Flush();
        return ms.ToArray();
    }

    public static bool TryDecode(ReadOnlySpan<byte> bytes, out PersistedRecordsetData? data, out Failure? error)
    {
        data = null;
        error = null;
        int pos = 0;

        // Header: 01 07 'T' 'G' <u32 version> 00
        if (bytes.Length < 10 || bytes[0] != TokenHeader || bytes[1] != 0x07 || bytes[2] != (byte)'T' || bytes[3] != (byte)'G')
        {
            error = AdoObject.PersistenceFailure(AdoObject.HResultFail, AdoPersistence.InvalidSourceFormatMessage);
            return false;
        }
        pos = 9;

        var columns = new List<PersistedColumn>();
        var rows = new List<PersistedRow>();
        bool sawResultDescriptor = false;
        bool isUpdatable = false;
        string? baseTableName = null;
        long stagingOrder = 0;

        try
        {
            while (pos < bytes.Length)
            {
                byte token = bytes[pos++];
                if (token == TokenDone)
                {
                    if (!sawResultDescriptor)
                    {
                        DisposeRows(rows);
                        error = AdoObject.PersistenceFailure(AdoObject.HResultFail, AdoPersistence.InvalidSourceFormatMessage);
                        return false;
                    }
                    data = new PersistedRecordsetData([.. columns], rows, baseTableName, isUpdatable);
                    return true;
                }

                if (token is 0x02 or 0x03 or TokenRowsetProperties)
                {
                    if (pos + 2 > bytes.Length)
                    {
                        goto Invalid;
                    }
                    ushort len = BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(pos, 2));
                    pos += 2;
                    if (pos + len > bytes.Length)
                    {
                        goto Invalid;
                    }
                    if (token == 0x03)
                    {
                        sawResultDescriptor = true;
                    }
                    pos += len;
                    continue;
                }

                if (token == TokenColumnDescriptor)
                {
                    if (pos + 2 > bytes.Length)
                    {
                        goto Invalid;
                    }
                    ushort len = BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(pos, 2));
                    pos += 2;
                    if (pos + len > bytes.Length)
                    {
                        goto Invalid;
                    }
                    if (!TryParseColumnDescriptor(bytes.Slice(pos, len), out PersistedColumn? col))
                    {
                        goto Invalid;
                    }
                    columns.Add(col!);
                    if (!string.IsNullOrEmpty(col!.BaseTableName))
                    {
                        baseTableName ??= col.BaseTableName;
                        isUpdatable = true;
                    }
                    pos += len;
                    continue;
                }

                if (token is TokenUnchangedRow or TokenInsertRow or TokenDeleteRow)
                {
                    if (!TryReadRowValues(bytes, ref pos, columns, out OwnedVariant[]? vals))
                    {
                        goto Invalid;
                    }
                    ClientRowState state = token switch
                    {
                        TokenInsertRow => ClientRowState.New,
                        TokenDeleteRow => ClientRowState.Deleted,
                        _ => ClientRowState.Clean,
                    };
                    OwnedVariant[] orig = state == ClientRowState.New
                        ? CreateNullVariants(columns.Count)
                        : CloneVariants(vals!);
                    if (state != ClientRowState.Clean)
                    {
                        isUpdatable = true;
                    }
                    rows.Add(new PersistedRow(
                        state,
                        vals!,
                        orig,
                        new bool[columns.Count],
                        state == ClientRowState.Clean ? 0 : ++stagingOrder));
                    continue;
                }

                if (token == TokenChangeRow)
                {
                    if (!TryReadRowValues(bytes, ref pos, columns, out OwnedVariant[]? orig))
                    {
                        goto Invalid;
                    }
                    if (!TryReadPresenceMask(bytes, ref pos, columns.Count, out bool[]? modMask))
                    {
                        DisposeVariants(orig!);
                        goto Invalid;
                    }
                    OwnedVariant[] current = CloneVariants(orig!);
                    if (!TryReadMaskedRowValues(bytes, ref pos, columns, modMask!, current))
                    {
                        DisposeVariants(orig!);
                        DisposeVariants(current);
                        goto Invalid;
                    }
                    isUpdatable = true;
                    rows.Add(new PersistedRow(ClientRowState.Modified, current, orig!, modMask!, ++stagingOrder));
                    continue;
                }

                goto Invalid;
            }
        }
        catch
        {
            DisposeRows(rows);
            error = AdoObject.PersistenceFailure(AdoObject.HResultFail, AdoPersistence.InvalidSourceFormatMessage);
            return false;
        }

    Invalid:
        DisposeRows(rows);
        error = AdoObject.PersistenceFailure(AdoObject.HResultFail, AdoPersistence.InvalidSourceFormatMessage);
        return false;
    }

    private static byte[] BuildColumnDescriptorBody(PersistedColumn col, ushort ordinal, ushort dbType, bool includeBaseMetadata)
    {
        using var ms = new MemoryStream(64);
        using var w = new BinaryWriter(ms, Encoding.Unicode, leaveOpen: true);

        w.Write((byte)0x80);
        w.Write((ushort)1);
        w.Write(ordinal);
        w.Write((ushort)col.Name.Length);
        w.Write(Encoding.Unicode.GetBytes(col.Name));
        w.Write(dbType);
        w.Write((uint)col.DefinedSize);
        w.Write((uint)col.Precision);
        w.Write((uint)col.NumericScale);
        w.Write((uint)col.Attributes);

        if (includeBaseMetadata && (!string.IsNullOrEmpty(col.BaseTableName) || !string.IsNullOrEmpty(col.BaseColumnName) || col.IsKeyColumn))
        {
            ushort propCount = 0;
            if (!string.IsNullOrEmpty(col.BaseTableName)) propCount++;
            if (!string.IsNullOrEmpty(col.BaseColumnName)) propCount++;
            if (col.IsKeyColumn) propCount++;
            w.Write(propCount);
            if (!string.IsNullOrEmpty(col.BaseTableName))
            {
                byte[] tb = Encoding.Unicode.GetBytes(col.BaseTableName);
                w.Write(ColPropBaseTable);
                w.Write((ushort)tb.Length);
                w.Write(tb);
            }
            if (!string.IsNullOrEmpty(col.BaseColumnName))
            {
                byte[] cb = Encoding.Unicode.GetBytes(col.BaseColumnName);
                w.Write(ColPropBaseColumn);
                w.Write((ushort)cb.Length);
                w.Write(cb);
            }
            if (col.IsKeyColumn)
            {
                w.Write(ColPropIsKeyColumn);
                w.Write((ushort)1);
                w.Write((byte)1);
            }
        }
        else
        {
            w.Write((ushort)0);
        }

        w.Write((ushort)0xFFFF);
        w.Flush();
        return ms.ToArray();
    }

    private static bool TryParseColumnDescriptor(ReadOnlySpan<byte> body, out PersistedColumn? column)
    {
        column = null;
        if (body.Length < 29)
        {
            return false;
        }
        int pos = 5; // skip presence byte (0x80), flags (u16), ordinal (u16)
        ushort nameChars = BinaryPrimitives.ReadUInt16LittleEndian(body.Slice(pos, 2));
        pos += 2;
        int nameBytes = nameChars * 2;
        if (pos + nameBytes + 22 > body.Length)
        {
            return false;
        }
        string name = Encoding.Unicode.GetString(body.Slice(pos, nameBytes));
        pos += nameBytes;
        ushort dbType = BinaryPrimitives.ReadUInt16LittleEndian(body.Slice(pos, 2));
        pos += 2;
        int definedSize = BinaryPrimitives.ReadInt32LittleEndian(body.Slice(pos, 4));
        pos += 4;
        byte precision = (byte)BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(pos, 4));
        pos += 4;
        byte scale = (byte)BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(pos, 4));
        pos += 4;
        int attributes = BinaryPrimitives.ReadInt32LittleEndian(body.Slice(pos, 4));
        pos += 4;
        ushort propCount = BinaryPrimitives.ReadUInt16LittleEndian(body.Slice(pos, 2));
        pos += 2;

        string? baseTable = null;
        string? baseColumn = null;
        bool isKey = false;
        for (int p = 0; p < propCount; p++)
        {
            if (pos + 4 > body.Length)
            {
                return false;
            }
            ushort propId = BinaryPrimitives.ReadUInt16LittleEndian(body.Slice(pos, 2));
            ushort propLen = BinaryPrimitives.ReadUInt16LittleEndian(body.Slice(pos + 2, 2));
            pos += 4;
            if (pos + propLen > body.Length)
            {
                return false;
            }
            ReadOnlySpan<byte> propBytes = body.Slice(pos, propLen);
            pos += propLen;
            if (propId == ColPropBaseTable)
            {
                baseTable = Encoding.Unicode.GetString(propBytes);
            }
            else if (propId == ColPropBaseColumn)
            {
                baseColumn = Encoding.Unicode.GetString(propBytes);
            }
            else if (propId == ColPropIsKeyColumn && propLen >= 1)
            {
                isKey = propBytes[0] != 0;
            }
        }

        if (!TryMapDbTypeToAdoType(dbType, attributes, out int adoType))
        {
            return false;
        }

        column = new PersistedColumn(name, adoType, definedSize, precision, scale, attributes, baseTable, baseColumn, isKey);
        return true;
    }

    private static bool TryWriteRowValues(BinaryWriter w, PersistedColumn[] columns, OwnedVariant[] values, out Failure? refusal)
    {
        refusal = null;
        bool[] present = new bool[columns.Length];
        for (int i = 0; i < columns.Length; i++)
        {
            present[i] = values[i].Type is not (VarType.Null or VarType.Empty);
        }
        WritePresenceMask(w, present);
        for (int i = 0; i < columns.Length; i++)
        {
            if (!present[i])
            {
                continue;
            }
            if (!TryWriteValue(w, columns[i], values[i], out refusal))
            {
                return false;
            }
        }
        return true;
    }

    private static bool TryWriteMaskedRowValues(
        BinaryWriter w,
        PersistedColumn[] columns,
        OwnedVariant[] values,
        bool[] mask,
        out Failure? refusal)
    {
        refusal = null;
        for (int i = 0; i < columns.Length; i++)
        {
            if (!mask[i])
            {
                continue;
            }
            bool nonNull = values[i].Type is not (VarType.Null or VarType.Empty);
            w.Write(nonNull ? (byte)1 : (byte)0);
            if (nonNull && !TryWriteValue(w, columns[i], values[i], out refusal))
            {
                return false;
            }
        }
        return true;
    }

    private static bool TryWriteValue(BinaryWriter w, PersistedColumn col, OwnedVariant val, out Failure? refusal)
    {
        refusal = null;
        switch (col.AdoType)
        {
            case 3: // adInteger
                w.Write((int)val.Pointer->Int64);
                return true;
            case 2: // adSmallInt
                w.Write((short)val.Pointer->Int64);
                return true;
            case 17: // adUnsignedTinyInt
                w.Write((byte)val.Pointer->Int64);
                return true;
            case 11: // adBoolean
                w.Write((short)val.Pointer->Int64);
                return true;
            case 4: // adSingle
                w.Write((int)val.Pointer->Int64);
                return true;
            case 5: // adDouble
                w.Write(BitConverter.DoubleToInt64Bits(val.Pointer->Double));
                return true;
            case 6: // adCurrency
                w.Write(val.Pointer->Int64);
                return true;
            case 7: // adDate
                w.Write(BitConverter.DoubleToInt64Bits(val.Pointer->Double));
                return true;
            case 72: // adGUID
            {
                string s = val.Pointer->Pointer != 0 ? Bstrs.Chars(val.Pointer->Pointer).ToString() : "";
                w.Write(Guid.Parse(s).ToByteArray());
                return true;
            }
            case 131: // adNumeric (VT_DECIMAL)
            {
                DecimalValue d = ReadDecimalValue(val);
                // r-str-ado-act/ado-persist #41[3].1[9].0: ADTG normalizes zero Decimal sign to 0x00.
                bool neg = d.Negative && (d.High32 != 0 || d.Low64 != 0);
                w.Write(d.Scale);
                w.Write(neg ? (byte)0x80 : (byte)0x00);
                w.Write(d.High32);
                w.Write(d.Low64);
                return true;
            }
            case 202: // adVarWChar
            case 203: // adLongVarWChar
            {
                string s = val.Pointer->Pointer != 0 ? Bstrs.Chars(val.Pointer->Pointer).ToString() : "";
                byte[] utf16 = Encoding.Unicode.GetBytes(s);
                if (col.AdoType == 202 && col.DefinedSize <= 127)
                {
                    w.Write((byte)utf16.Length);
                }
                else
                {
                    w.Write(utf16.Length);
                }
                w.Write(utf16);
                return true;
            }
            case 204: // adVarBinary
            case 205: // adLongVarBinary
            {
                nint psa = val.Pointer->Pointer;
                int len = psa != 0 ? SafeArrays.Shape(psa)[0].Count : 0;
                if (col.AdoType == 204 && col.DefinedSize <= 255)
                {
                    w.Write((byte)len);
                }
                else
                {
                    w.Write(len);
                }
                if (len > 0)
                {
                    byte[] raw = new byte[len];
                    new ReadOnlySpan<byte>(SafeArrays.ElementPointer(psa, 0), len).CopyTo(raw);
                    w.Write(raw);
                }
                return true;
            }
            default:
                refusal = Failure.NotModelled("NotModelled.AdoPersistAdtgValue", $"ADTG value encoding for ADO type {col.AdoType} ('{col.Name}') is not recorded");
                return false;
        }
    }

    private static bool TryReadRowValues(ReadOnlySpan<byte> bytes, ref int pos, List<PersistedColumn> columns, out OwnedVariant[]? values)
    {
        values = null;
        if (!TryReadPresenceMask(bytes, ref pos, columns.Count, out bool[]? present))
        {
            return false;
        }
        var result = new OwnedVariant[columns.Count];
        for (int i = 0; i < columns.Count; i++)
        {
            if (!present![i])
            {
                result[i] = OwnedVariant.Null();
                continue;
            }
            if (!TryReadValue(bytes, ref pos, columns[i], out OwnedVariant? v))
            {
                DisposeVariants(result, i);
                return false;
            }
            result[i] = v!;
        }
        values = result;
        return true;
    }

    private static bool TryReadMaskedRowValues(
        ReadOnlySpan<byte> bytes,
        ref int pos,
        List<PersistedColumn> columns,
        bool[] mask,
        OwnedVariant[] target)
    {
        for (int i = 0; i < columns.Count; i++)
        {
            if (!mask[i])
            {
                continue;
            }
            if (pos >= bytes.Length)
            {
                return false;
            }
            byte nonNull = bytes[pos++];
            target[i].Dispose();
            if (nonNull == 0)
            {
                target[i] = OwnedVariant.Null();
                continue;
            }
            if (!TryReadValue(bytes, ref pos, columns[i], out OwnedVariant? v))
            {
                target[i] = OwnedVariant.Null();
                return false;
            }
            target[i] = v!;
        }
        return true;
    }

    private static bool TryReadValue(ReadOnlySpan<byte> bytes, ref int pos, PersistedColumn col, out OwnedVariant? value)
    {
        value = null;
        switch (col.AdoType)
        {
            case 3:
                if (pos + 4 > bytes.Length) return false;
                value = OwnedVariant.FromInt32(BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(pos, 4)));
                pos += 4;
                return true;
            case 2:
                if (pos + 2 > bytes.Length) return false;
                value = OwnedVariant.FromInt16(BinaryPrimitives.ReadInt16LittleEndian(bytes.Slice(pos, 2)));
                pos += 2;
                return true;
            case 17:
                if (pos + 1 > bytes.Length) return false;
                value = OwnedVariant.FromByte(bytes[pos++]);
                return true;
            case 11:
                if (pos + 2 > bytes.Length) return false;
                value = OwnedVariant.FromBoolean(BinaryPrimitives.ReadInt16LittleEndian(bytes.Slice(pos, 2)) != 0);
                pos += 2;
                return true;
            case 4:
                if (pos + 4 > bytes.Length) return false;
                value = OwnedVariant.FromSingle(BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(pos, 4))));
                pos += 4;
                return true;
            case 5:
                if (pos + 8 > bytes.Length) return false;
                value = OwnedVariant.FromDouble(BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(bytes.Slice(pos, 8))));
                pos += 8;
                return true;
            case 6:
                if (pos + 8 > bytes.Length) return false;
                value = OwnedVariant.FromCurrency(BinaryPrimitives.ReadInt64LittleEndian(bytes.Slice(pos, 8)));
                pos += 8;
                return true;
            case 7:
                if (pos + 8 > bytes.Length) return false;
                value = OwnedVariant.FromDate(BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(bytes.Slice(pos, 8))));
                pos += 8;
                return true;
            case 72:
                if (pos + 16 > bytes.Length) return false;
                value = OwnedVariant.FromText(new Guid(bytes.Slice(pos, 16)).ToString("B").ToUpperInvariant());
                pos += 16;
                return true;
            case 131:
                if (pos + 14 > bytes.Length) return false;
                byte scale = bytes[pos++];
                bool neg = bytes[pos++] != 0;
                uint hi = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(pos, 4));
                pos += 4;
                ulong lo = BinaryPrimitives.ReadUInt64LittleEndian(bytes.Slice(pos, 8));
                pos += 8;
                value = OwnedVariant.FromDecimal(new DecimalValue(scale, neg && (hi != 0 || lo != 0), hi, lo));
                return true;
            case 202:
            case 203:
            {
                int byteLen;
                if (col.AdoType == 202 && col.DefinedSize <= 127)
                {
                    if (pos + 1 > bytes.Length) return false;
                    byteLen = bytes[pos++];
                }
                else
                {
                    if (pos + 4 > bytes.Length) return false;
                    byteLen = BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(pos, 4));
                    pos += 4;
                }
                if (byteLen < 0 || pos + byteLen > bytes.Length) return false;
                string s = Encoding.Unicode.GetString(bytes.Slice(pos, byteLen));
                pos += byteLen;
                value = OwnedVariant.FromText(s);
                return true;
            }
            case 204:
            case 205:
            {
                int byteLen;
                if (col.AdoType == 204 && col.DefinedSize <= 255)
                {
                    if (pos + 1 > bytes.Length) return false;
                    byteLen = bytes[pos++];
                }
                else
                {
                    if (pos + 4 > bytes.Length) return false;
                    byteLen = BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(pos, 4));
                    pos += 4;
                }
                if (byteLen < 0 || pos + byteLen > bytes.Length) return false;
                nint psa = SafeArrays.Create(VarType.UI1, (0, byteLen));
                if (byteLen > 0)
                {
                    bytes.Slice(pos, byteLen).CopyTo(new Span<byte>(SafeArrays.ElementPointer(psa, 0), byteLen));
                }
                pos += byteLen;
                value = OwnedVariant.TakeArray(psa, VarType.UI1);
                return true;
            }
            default:
                return false;
        }
    }

    private static void WritePresenceMask(BinaryWriter w, bool[] bits)
    {
        int byteCount = (bits.Length + 7) / 8;
        for (int b = 0; b < byteCount; b++)
        {
            byte mask = 0;
            for (int bit = 0; bit < 8; bit++)
            {
                int colIdx = (b * 8) + bit;
                // Unused trailing bits are 1 (matching 0xFF and 0xCF in r-ado-1b/persist-stream-roundtrip).
                bool isSet = colIdx >= bits.Length || bits[colIdx];
                if (isSet)
                {
                    mask |= (byte)(1 << (7 - bit));
                }
            }
            w.Write(mask);
        }
    }

    private static bool TryReadPresenceMask(ReadOnlySpan<byte> bytes, ref int pos, int count, out bool[]? bits)
    {
        bits = null;
        int byteCount = (count + 7) / 8;
        if (pos + byteCount > bytes.Length)
        {
            return false;
        }
        bool[] result = new bool[count];
        for (int i = 0; i < count; i++)
        {
            byte b = bytes[pos + (i / 8)];
            result[i] = (b & (1 << (7 - (i % 8)))) != 0;
        }
        pos += byteCount;
        bits = result;
        return true;
    }

    private static void WritePropI2(BinaryWriter w, uint propId, short value)
    {
        w.Write(propId);
        w.Write((ushort)2);
        w.Write(value);
    }

    private static void WritePropI4(BinaryWriter w, uint propId, int value)
    {
        w.Write(propId);
        w.Write((ushort)4);
        w.Write(value);
    }

    private static void WritePropEmpty(BinaryWriter w, uint propId)
    {
        w.Write(propId);
        w.Write((ushort)0);
    }

    private static bool TryMapAdoTypeToDbType(int adoType, out ushort dbType)
    {
        dbType = adoType switch
        {
            2 => 0x0002,   // DBTYPE_I2
            3 => 0x0003,   // DBTYPE_I4
            4 => 0x0004,   // DBTYPE_R4
            5 => 0x0005,   // DBTYPE_R8
            6 => 0x0006,   // DBTYPE_CY
            7 => 0x0007,   // DBTYPE_DATE
            11 => 0x000B,  // DBTYPE_BOOL
            17 => 0x0011,  // DBTYPE_UI1
            72 => 0x0048,  // DBTYPE_GUID
            131 => 0x0083, // DBTYPE_NUMERIC
            202 or 203 => 0x0082, // DBTYPE_WSTR
            204 or 205 => 0x0080, // DBTYPE_BYTES
            _ => 0,
        };
        return dbType != 0;
    }

    private static bool TryMapDbTypeToAdoType(ushort dbType, int attributes, out int adoType)
    {
        bool isLong = (attributes & 128) != 0;
        adoType = dbType switch
        {
            0x0002 => 2,
            0x0003 => 3,
            0x0004 => 4,
            0x0005 => 5,
            0x0006 => 6,
            0x0007 => 7,
            0x000B => 11,
            0x0011 => 17,
            0x0048 => 72,
            0x0083 => 131,
            0x0082 => isLong ? 203 : 202,
            0x0080 => isLong ? 205 : 204,
            _ => 0,
        };
        return adoType != 0;
    }

    internal static DecimalValue ReadDecimalValue(OwnedVariant v)
    {
        byte* b = (byte*)v.Pointer;
        DecimalLayout d = TargetDeclarations.WindowsX64.Decimal;
        byte scale = b[d.ScaleOffset];
        bool neg = (b[d.SignOffset] & d.NegativeSign) != 0;
        uint hi = *(uint*)(b + d.High32Offset);
        ulong lo = *(ulong*)(b + d.Low64Offset);
        return new DecimalValue(scale, neg, hi, lo);
    }

    internal static OwnedVariant[] CreateNullVariants(int count)
    {
        var arr = new OwnedVariant[count];
        for (int i = 0; i < count; i++)
        {
            arr[i] = OwnedVariant.Null();
        }
        return arr;
    }

    internal static OwnedVariant[] CloneVariants(OwnedVariant[] src)
    {
        var copy = new OwnedVariant[src.Length];
        for (int i = 0; i < src.Length; i++)
        {
            copy[i] = CloneVariant(src[i]);
        }
        return copy;
    }

    internal static OwnedVariant CloneVariant(OwnedVariant v)
    {
        if (v.Type == (VarType.Array | VarType.UI1) && v.Pointer->Pointer != 0)
        {
            nint psa = v.Pointer->Pointer;
            int len = SafeArrays.Shape(psa)[0].Count;
            nint copy = SafeArrays.Create(VarType.UI1, (0, len));
            if (len > 0)
            {
                new ReadOnlySpan<byte>(SafeArrays.ElementPointer(psa, 0), len)
                    .CopyTo(new Span<byte>(SafeArrays.ElementPointer(copy, 0), len));
            }
            return OwnedVariant.TakeArray(copy, VarType.UI1);
        }
        return v.CopyPreservingReference();
    }

    private static void DisposeRows(List<PersistedRow> rows)
    {
        foreach (PersistedRow row in rows)
        {
            row.Dispose();
        }
        rows.Clear();
    }

    internal static void DisposeVariants(OwnedVariant[] arr, int? count = null)
    {
        int n = count ?? arr.Length;
        for (int i = 0; i < n; i++)
        {
            arr[i]?.Dispose();
        }
    }
}

/// <summary>
/// Independent MS-Persist XML RowsetSchema codec (II-12 §24; W-09/events-persistence-inventory).
/// Verified character-for-character against <c>sx.ReadText(-1)</c> (1,460 chars / 2,922 bytes UTF-16LE with BOM)
/// in <c>evidence/r-ado-1b/persist-stream-roundtrip</c> and <c>evidence/r-q45-querydef-stream-rollback/stream-save-into-open</c>.
/// </summary>
internal static unsafe class AdoXmlPersistCodec
{
    private const string NsSchema = "uuid:BDC6E3F0-6DA3-11d1-A2A3-00AA00C14882";
    private const string NsDataType = "uuid:C2F41010-65B3-11d1-A29F-00AA00C14882";
    private const string NsRowset = "urn:schemas-microsoft-com:rowset";
    private const string NsRow = "#RowsetSchema";

    public static string Encode(PersistedRecordsetData data, out Failure? refusal)
    {
        refusal = null;
        var sb = new StringBuilder(1536);
        sb.Append("<xml xmlns:s='").Append(NsSchema).Append("'\r\n");
        sb.Append("\txmlns:dt='").Append(NsDataType).Append("'\r\n");
        sb.Append("\txmlns:rs='").Append(NsRowset).Append("'\r\n");
        sb.Append("\txmlns:z='").Append(NsRow).Append("'>\r\n");
        sb.Append("<s:Schema id='RowsetSchema'>\r\n");
        sb.Append("\t<s:ElementType name='row' content='eltOnly'");
        if (data.IsUpdatable)
        {
            sb.Append(" rs:updatable='true'");
        }
        sb.Append(">\r\n");

        for (int i = 0; i < data.Columns.Length; i++)
        {
            PersistedColumn col = data.Columns[i];
            try
            {
                XmlConvert.VerifyNCName(col.Name);
            }
            catch (XmlException)
            {
                refusal = Failure.NotModelled(
                    "NotModelled.AdoPersistXmlColumnName",
                    $"XML persistence for non-NCName column '{col.Name}' is not recorded");
                return "";
            }
            bool nullable = (col.Attributes & 32) != 0;
            sb.Append("\t\t<s:AttributeType name='").Append(EscapeAttr(col.Name))
              .Append("' rs:number='").Append(i + 1).Append('\'');
            if (nullable)
            {
                sb.Append(" rs:nullable='true'\r\n\t\t\t rs:maydefer='true' rs:writeunknown='true'");
            }
            else
            {
                sb.Append(" rs:maydefer='true'\r\n\t\t\t rs:writeunknown='true'");
            }
            if (data.IsUpdatable)
            {
                if (!string.IsNullOrEmpty(col.BaseTableName))
                {
                    sb.Append(" rs:basetable='").Append(EscapeAttr(col.BaseTableName)).Append('\'');
                }
                if (!string.IsNullOrEmpty(col.BaseColumnName))
                {
                    sb.Append(" rs:basecolumn='").Append(EscapeAttr(col.BaseColumnName)).Append('\'');
                }
                if (col.IsKeyColumn)
                {
                    sb.Append(" rs:keycolumn='true'");
                }
            }
            sb.Append(">\r\n");

            if (!TryAppendDataTypeElement(sb, col, out refusal))
            {
                return "";
            }
            sb.Append("\t\t</s:AttributeType>\r\n");
        }

        sb.Append("\t\t<s:extends type='rs:rowbase'/>\r\n");
        sb.Append("\t</s:ElementType>\r\n");
        sb.Append("</s:Schema>\r\n");
        sb.Append("<rs:data>\r\n");

        foreach (PersistedRow row in data.Rows)
        {
            switch (row.State)
            {
                case ClientRowState.Clean:
                    sb.Append('\t');
                    if (!TryAppendRowElement(sb, data.Columns, row.Current, out refusal)) return "";
                    sb.Append("\r\n");
                    break;
                case ClientRowState.Modified:
                    sb.Append("\t<rs:update>\r\n\t\t<rs:original>\r\n\t\t\t");
                    if (!TryAppendRowElement(sb, data.Columns, row.Original, out refusal)) return "";
                    sb.Append("\r\n\t\t</rs:original>\r\n\t\t");
                    if (!TryAppendRowElement(sb, data.Columns, row.Current, out refusal)) return "";
                    sb.Append("\r\n\t</rs:update>\r\n");
                    break;
                case ClientRowState.New:
                    sb.Append("\t<rs:insert>\r\n\t\t");
                    if (!TryAppendRowElement(sb, data.Columns, row.Current, out refusal)) return "";
                    sb.Append("\r\n\t</rs:insert>\r\n");
                    break;
                case ClientRowState.Deleted:
                    sb.Append("\t<rs:delete>\r\n\t\t");
                    if (!TryAppendRowElement(sb, data.Columns, row.Original, out refusal)) return "";
                    sb.Append("\r\n\t</rs:delete>\r\n");
                    break;
            }
        }

        sb.Append("</rs:data>\r\n</xml>");
        return sb.ToString();
    }

    public static bool TryDecode(ReadOnlySpan<byte> rawBytes, out PersistedRecordsetData? data, out Failure? error)
    {
        data = null;
        error = null;
        string xmlText;
        try
        {
            if (rawBytes.Length >= 2 && rawBytes[0] == 0xFF && rawBytes[1] == 0xFE)
            {
                xmlText = Encoding.Unicode.GetString(rawBytes[2..]);
            }
            else if (rawBytes.Length >= 3 && rawBytes[0] == 0xEF && rawBytes[1] == 0xBB && rawBytes[2] == 0xBF)
            {
                xmlText = Encoding.UTF8.GetString(rawBytes[3..]);
            }
            else
            {
                xmlText = Encoding.UTF8.GetString(rawBytes);
            }
        }
        catch
        {
            error = AdoObject.PersistenceFailure(AdoObject.HResultFail, AdoPersistence.InvalidXmlMessage);
            return false;
        }

        XDocument doc;
        try
        {
            doc = XDocument.Parse(xmlText, LoadOptions.PreserveWhitespace);
        }
        catch (XmlException)
        {
            error = AdoObject.PersistenceFailure(AdoObject.HResultFail, AdoPersistence.InvalidXmlMessage);
            return false;
        }

        XElement? root = doc.Root;
        if (root is null || !string.Equals(root.Name.LocalName, "xml", StringComparison.Ordinal))
        {
            error = AdoObject.PersistenceFailure(AdoObject.HResultFail, AdoPersistence.InvalidXmlMessage);
            return false;
        }

        XNamespace sNs = NsSchema;
        XNamespace dtNs = NsDataType;
        XNamespace rsNs = NsRowset;
        XNamespace zNs = NsRow;

        XElement? schemaEl = root.Element(sNs + "Schema");
        XElement? elemTypeEl = schemaEl?.Element(sNs + "ElementType");
        XElement? dataEl = root.Element(rsNs + "data");
        if (schemaEl is null || elemTypeEl is null || dataEl is null)
        {
            // r-ado-1b/persist-malformed-files #60 ("<xml/>"), #78 ("<xml xmlns:s=... xmlns:z=...></xml>"):
            // missing Schema/ElementType/rs:data fails with InvalidXmlMessage.
            error = AdoObject.PersistenceFailure(AdoObject.HResultFail, AdoPersistence.InvalidXmlMessage);
            return false;
        }

        bool isUpdatable = string.Equals((string?)elemTypeEl.Attribute(rsNs + "updatable"), "true", StringComparison.OrdinalIgnoreCase);
        string? baseTableName = null;
        var columns = new List<PersistedColumn>();
        foreach (XElement attrEl in elemTypeEl.Elements(sNs + "AttributeType"))
        {
            string? colName = (string?)attrEl.Attribute("name");
            XElement? dtEl = attrEl.Element(sNs + "datatype");
            if (string.IsNullOrEmpty(colName) || dtEl is null)
            {
                error = AdoObject.PersistenceFailure(AdoObject.HResultFail, AdoPersistence.InvalidXmlMessage);
                return false;
            }
            if (!TryParseXmlColumn(attrEl, dtEl, colName, dtNs, rsNs, out PersistedColumn? col))
            {
                error = AdoObject.PersistenceFailure(AdoObject.HResultFail, AdoPersistence.InvalidXmlMessage);
                return false;
            }
            columns.Add(col!);
            if (!string.IsNullOrEmpty(col!.BaseTableName))
            {
                baseTableName ??= col.BaseTableName;
                isUpdatable = true;
            }
        }

        if (columns.Count == 0)
        {
            error = AdoObject.PersistenceFailure(AdoObject.HResultFail, AdoPersistence.InvalidXmlMessage);
            return false;
        }

        var rows = new List<PersistedRow>();
        long stagingOrder = 0;
        try
        {
            foreach (XElement child in dataEl.Elements())
            {
                if (child.Name == zNs + "row")
                {
                    OwnedVariant[] vals = ParseXmlRowElement(child, columns);
                    rows.Add(new PersistedRow(ClientRowState.Clean, vals, AdoAdtgCodec.CloneVariants(vals), new bool[columns.Count], 0));
                }
                else if (child.Name == rsNs + "update")
                {
                    XElement? origEl = child.Element(rsNs + "original")?.Element(zNs + "row");
                    XElement? curEl = child.Element(zNs + "row");
                    if (origEl is null || curEl is null)
                    {
                        goto InvalidXml;
                    }
                    OwnedVariant[] origVals = ParseXmlRowElement(origEl, columns);
                    OwnedVariant[] curVals;
                    try
                    {
                        curVals = ParseXmlRowElement(curEl, columns);
                    }
                    catch
                    {
                        AdoAdtgCodec.DisposeVariants(origVals);
                        throw;
                    }
                    bool[] modified = new bool[columns.Count];
                    for (int i = 0; i < columns.Count; i++)
                    {
                        modified[i] = !ArePersistedValuesEqual(origVals[i], curVals[i]);
                    }
                    isUpdatable = true;
                    rows.Add(new PersistedRow(ClientRowState.Modified, curVals, origVals, modified, ++stagingOrder));
                }
                else if (child.Name == rsNs + "insert")
                {
                    foreach (XElement insRow in child.Elements(zNs + "row"))
                    {
                        OwnedVariant[] vals = ParseXmlRowElement(insRow, columns);
                        isUpdatable = true;
                        rows.Add(new PersistedRow(ClientRowState.New, vals, AdoAdtgCodec.CreateNullVariants(columns.Count), new bool[columns.Count], ++stagingOrder));
                    }
                }
                else if (child.Name == rsNs + "delete")
                {
                    foreach (XElement delRow in child.Elements(zNs + "row"))
                    {
                        OwnedVariant[] origVals = ParseXmlRowElement(delRow, columns);
                        isUpdatable = true;
                        rows.Add(new PersistedRow(ClientRowState.Deleted, AdoAdtgCodec.CloneVariants(origVals), origVals, new bool[columns.Count], ++stagingOrder));
                    }
                }
                else
                {
                    goto InvalidXml;
                }
            }

            data = new PersistedRecordsetData([.. columns], rows, baseTableName, isUpdatable);
            return true;
        }
        catch
        {
            goto InvalidXml;
        }

    InvalidXml:
        foreach (PersistedRow r in rows)
        {
            r.Dispose();
        }
        error = AdoObject.PersistenceFailure(AdoObject.HResultFail, AdoPersistence.InvalidXmlMessage);
        return false;
    }

    private static bool TryAppendDataTypeElement(StringBuilder sb, PersistedColumn col, out Failure? refusal)
    {
        refusal = null;
        switch (col.AdoType)
        {
            case 3: // adInteger
                sb.Append("\t\t\t<s:datatype dt:type='int' dt:maxLength='4' rs:precision='10'\r\n\t\t\t rs:fixedlength='true'/>\r\n");
                return true;
            case 2: // adSmallInt
                sb.Append("\t\t\t<s:datatype dt:type='i2' dt:maxLength='2' rs:precision='5'\r\n\t\t\t rs:fixedlength='true'/>\r\n");
                return true;
            case 17: // adUnsignedTinyInt
                sb.Append("\t\t\t<s:datatype dt:type='ui1' dt:maxLength='1' rs:precision='3'\r\n\t\t\t rs:fixedlength='true'/>\r\n");
                return true;
            case 4: // adSingle
                sb.Append("\t\t\t<s:datatype dt:type='r4' dt:maxLength='4' rs:precision='7'\r\n\t\t\t rs:fixedlength='true'/>\r\n");
                return true;
            case 5: // adDouble
                sb.Append("\t\t\t<s:datatype dt:type='float' dt:maxLength='8' rs:precision='15'\r\n\t\t\t rs:fixedlength='true'/>\r\n");
                return true;
            case 6: // adCurrency
                sb.Append("\t\t\t<s:datatype dt:type='number' rs:dbtype='currency' dt:maxLength='8' rs:precision='19'\r\n\t\t\t rs:fixedlength='true'/>\r\n");
                return true;
            case 7: // adDate
                sb.Append("\t\t\t<s:datatype dt:type='dateTime' rs:dbtype='variantdate' dt:maxLength='16'\r\n\t\t\t rs:fixedlength='true'/>\r\n");
                return true;
            case 11: // adBoolean
                sb.Append("\t\t\t<s:datatype dt:type='boolean' dt:maxLength='2'\r\n\t\t\t rs:fixedlength='true'/>\r\n");
                return true;
            case 72: // adGUID
                sb.Append("\t\t\t<s:datatype dt:type='uuid' dt:maxLength='16'\r\n\t\t\t rs:fixedlength='true'/>\r\n");
                return true;
            case 131: // adNumeric
                sb.Append("\t\t\t<s:datatype dt:type='number' rs:dbtype='numeric' dt:maxLength='19' rs:scale='")
                  .Append(col.NumericScale).Append("' rs:precision='").Append(col.Precision)
                  .Append("'\r\n\t\t\t rs:fixedlength='true'/>\r\n");
                return true;
            case 202: // adVarWChar
                sb.Append("\t\t\t<s:datatype dt:type='string' dt:maxLength='").Append(col.DefinedSize).Append("'/>\r\n");
                return true;
            case 203: // adLongVarWChar
                sb.Append("\t\t\t<s:datatype dt:type='string' dt:maxLength='").Append(col.DefinedSize).Append("' rs:long='true'/>\r\n");
                return true;
            case 204: // adVarBinary
                sb.Append("\t\t\t<s:datatype dt:type='bin.hex' dt:maxLength='").Append(col.DefinedSize).Append("'/>\r\n");
                return true;
            case 205: // adLongVarBinary
                sb.Append("\t\t\t<s:datatype dt:type='bin.hex' dt:maxLength='").Append(col.DefinedSize).Append("' rs:long='true'/>\r\n");
                return true;
            default:
                refusal = Failure.NotModelled("NotModelled.AdoPersistXmlType", $"XML persistence for ADO field type {col.AdoType} ('{col.Name}') is not recorded");
                return false;
        }
    }

    private static bool TryAppendRowElement(StringBuilder sb, PersistedColumn[] columns, OwnedVariant[] values, out Failure? refusal)
    {
        refusal = null;
        sb.Append("<z:row");
        for (int i = 0; i < columns.Length; i++)
        {
            OwnedVariant val = values[i];
            if (val.Type is VarType.Null or VarType.Empty)
            {
                continue;
            }
            if (!TryFormatXmlValue(columns[i], val, out string? formatted, out refusal))
            {
                return false;
            }
            sb.Append(' ').Append(columns[i].Name).Append("='").Append(EscapeAttr(formatted!)).Append('\'');
        }
        sb.Append("/>");
        return true;
    }

    private static bool TryFormatXmlValue(PersistedColumn col, OwnedVariant val, out string? formatted, out Failure? refusal)
    {
        refusal = null;
        formatted = null;
        switch (col.AdoType)
        {
            case 3:
                formatted = ((int)val.Pointer->Int64).ToString(CultureInfo.InvariantCulture);
                return true;
            case 2:
                formatted = ((short)val.Pointer->Int64).ToString(CultureInfo.InvariantCulture);
                return true;
            case 17:
                formatted = ((byte)val.Pointer->Int64).ToString(CultureInfo.InvariantCulture);
                return true;
            case 11:
                formatted = (short)val.Pointer->Int64 != 0 ? "True" : "False";
                return true;
            case 4:
                formatted = BitConverter.Int32BitsToSingle((int)val.Pointer->Int64).ToString("R", CultureInfo.InvariantCulture);
                return true;
            case 5:
                formatted = val.Pointer->Double.ToString("R", CultureInfo.InvariantCulture);
                return true;
            case 6:
                formatted = (val.Pointer->Int64 / 10000m).ToString(CultureInfo.InvariantCulture);
                return true;
            case 7:
            {
                double oa = val.Pointer->Double;
                DateTime dt = DateTime.FromOADate(oa);
                formatted = dt.ToString("yyyy-MM-ddTHH:mm:ss.fffffff", CultureInfo.InvariantCulture).TrimEnd('0').TrimEnd('.');
                return true;
            }
            case 72:
                formatted = val.Pointer->Pointer != 0 ? Bstrs.Chars(val.Pointer->Pointer).ToString() : "";
                return true;
            case 131:
                formatted = AdoAdtgCodec.ReadDecimalValue(val).ToDecimal().ToString(CultureInfo.InvariantCulture);
                return true;
            case 202:
            case 203:
                formatted = val.Pointer->Pointer != 0 ? Bstrs.Chars(val.Pointer->Pointer).ToString() : "";
                return true;
            case 204:
            case 205:
            {
                nint psa = val.Pointer->Pointer;
                int len = psa != 0 ? SafeArrays.Shape(psa)[0].Count : 0;
                if (len == 0)
                {
                    formatted = "";
                    return true;
                }
                ReadOnlySpan<byte> raw = new(SafeArrays.ElementPointer(psa, 0), len);
                formatted = Convert.ToHexString(raw).ToLowerInvariant();
                return true;
            }
            default:
                refusal = Failure.NotModelled("NotModelled.AdoPersistXmlValue", $"XML value encoding for ADO type {col.AdoType} ('{col.Name}') is not recorded");
                return false;
        }
    }

    private static bool TryParseXmlColumn(
        XElement attrEl,
        XElement dtEl,
        string name,
        XNamespace dtNs,
        XNamespace rsNs,
        out PersistedColumn? col)
    {
        col = null;
        string? dtType = (string?)dtEl.Attribute(dtNs + "type");
        string? dbType = (string?)dtEl.Attribute(rsNs + "dbtype");
        bool isLong = string.Equals((string?)dtEl.Attribute(rsNs + "long"), "true", StringComparison.OrdinalIgnoreCase);
        bool fixedLen = string.Equals((string?)dtEl.Attribute(rsNs + "fixedlength"), "true", StringComparison.OrdinalIgnoreCase);
        bool nullable = string.Equals((string?)attrEl.Attribute(rsNs + "nullable"), "true", StringComparison.OrdinalIgnoreCase);
        bool mayDefer = string.Equals((string?)attrEl.Attribute(rsNs + "maydefer"), "true", StringComparison.OrdinalIgnoreCase);
        bool writeUnknown = string.Equals((string?)attrEl.Attribute(rsNs + "writeunknown"), "true", StringComparison.OrdinalIgnoreCase);

        int maxLength = int.TryParse((string?)dtEl.Attribute(dtNs + "maxLength"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int ml) ? ml : 0;
        byte precision = byte.TryParse((string?)dtEl.Attribute(rsNs + "precision"), NumberStyles.Integer, CultureInfo.InvariantCulture, out byte pr) ? pr : (byte)255;
        byte scale = byte.TryParse((string?)dtEl.Attribute(rsNs + "scale"), NumberStyles.Integer, CultureInfo.InvariantCulture, out byte sc) ? sc : (byte)255;

        int adoType;
        int definedSize;
        switch (dtType)
        {
            case "int":
                adoType = 3; definedSize = 4; break;
            case "i2":
                adoType = 2; definedSize = 2; break;
            case "ui1":
                adoType = 17; definedSize = 1; break;
            case "r4":
                adoType = 4; definedSize = 4; break;
            case "float":
                adoType = 5; definedSize = 8; break;
            case "boolean":
                adoType = 11; definedSize = 2; break;
            case "uuid":
                adoType = 72; definedSize = 16; break;
            case "dateTime":
                adoType = 7; definedSize = 8; break;
            case "number":
                if (string.Equals(dbType, "currency", StringComparison.OrdinalIgnoreCase))
                {
                    adoType = 6; definedSize = 8;
                }
                else
                {
                    adoType = 131; definedSize = 19;
                }
                break;
            case "string":
                adoType = isLong ? 203 : 202; definedSize = maxLength; break;
            case "bin.hex":
                adoType = isLong ? 205 : 204; definedSize = maxLength; break;
            default:
                return false;
        }

        int attrs = 64; // adFldMayBeNull
        if (mayDefer) attrs |= 2;
        if (writeUnknown) attrs |= 8;
        if (fixedLen) attrs |= 16;
        if (nullable) attrs |= 32;
        if (isLong) attrs |= 128;

        string? baseTable = (string?)attrEl.Attribute(rsNs + "basetable");
        string? baseColumn = (string?)attrEl.Attribute(rsNs + "basecolumn");
        bool isKey = string.Equals((string?)attrEl.Attribute(rsNs + "keycolumn"), "true", StringComparison.OrdinalIgnoreCase);

        col = new PersistedColumn(name, adoType, definedSize, precision, scale, attrs, baseTable, baseColumn, isKey);
        return true;
    }

    private static OwnedVariant[] ParseXmlRowElement(XElement rowEl, List<PersistedColumn> columns)
    {
        var vals = new OwnedVariant[columns.Count];
        int filled = 0;
        try
        {
            for (int i = 0; i < columns.Count; i++)
            {
                PersistedColumn col = columns[i];
                XAttribute? attr = rowEl.Attribute(col.Name);
                if (attr is null)
                {
                    vals[i] = OwnedVariant.Null();
                    filled = i + 1;
                    continue;
                }
                vals[i] = ParseXmlValue(col, attr.Value);
                filled = i + 1;
            }
            return vals;
        }
        catch
        {
            for (int i = 0; i < filled; i++)
            {
                vals[i].Dispose();
            }
            throw;
        }
    }

    private static OwnedVariant ParseXmlValue(PersistedColumn col, string text) => col.AdoType switch
    {
        3 => OwnedVariant.FromInt32(int.Parse(text, CultureInfo.InvariantCulture)),
        2 => OwnedVariant.FromInt16(short.Parse(text, CultureInfo.InvariantCulture)),
        17 => OwnedVariant.FromByte(byte.Parse(text, CultureInfo.InvariantCulture)),
        11 => ParseXmlBoolean(text),
        4 => OwnedVariant.FromSingle(float.Parse(text, CultureInfo.InvariantCulture)),
        5 => OwnedVariant.FromDouble(double.Parse(text, CultureInfo.InvariantCulture)),
        6 => OwnedVariant.FromCurrency(checked((long)(decimal.Parse(text, CultureInfo.InvariantCulture) * 10000m))),
        7 => OwnedVariant.FromDate(DateTime.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToOADate()),
        72 => OwnedVariant.FromText(Guid.Parse(text).ToString("B").ToUpperInvariant()),
        131 => OwnedVariant.FromDecimal(DecimalValue.FromDecimal(decimal.Parse(text, CultureInfo.InvariantCulture))),
        202 or 203 => OwnedVariant.FromText(text),
        204 or 205 => ParseHexBytes(text),
        _ => throw new FormatException($"Unsupported XML ADO type {col.AdoType}"),
    };

    private static OwnedVariant ParseXmlBoolean(string text)
    {
        if (string.Equals(text, "True", StringComparison.OrdinalIgnoreCase) || text == "1")
        {
            return OwnedVariant.FromBoolean(true);
        }
        if (string.Equals(text, "False", StringComparison.OrdinalIgnoreCase) || text == "0")
        {
            return OwnedVariant.FromBoolean(false);
        }
        throw new FormatException($"Invalid XML boolean value '{text}'");
    }

    private static OwnedVariant ParseHexBytes(string hex)
    {
        byte[] bytes = hex.Length == 0 ? [] : Convert.FromHexString(hex);
        nint psa = SafeArrays.Create(VarType.UI1, (0, bytes.Length));
        if (bytes.Length > 0)
        {
            bytes.CopyTo(new Span<byte>(SafeArrays.ElementPointer(psa, 0), bytes.Length));
        }
        return OwnedVariant.TakeArray(psa, VarType.UI1);
    }

    private static bool ArePersistedValuesEqual(OwnedVariant a, OwnedVariant b)
    {
        if (a.Type != b.Type)
        {
            return false;
        }
        return a.Type switch
        {
            VarType.Null or VarType.Empty => true,
            VarType.Bool or VarType.I2 => (short)a.Pointer->Int64 == (short)b.Pointer->Int64,
            VarType.UI1 => (byte)a.Pointer->Int64 == (byte)b.Pointer->Int64,
            VarType.I4 or VarType.R4 => (int)a.Pointer->Int64 == (int)b.Pointer->Int64,
            VarType.I8 or VarType.Currency => a.Pointer->Int64 == b.Pointer->Int64,
            VarType.Bstr => (a.Pointer->Pointer != 0 ? Bstrs.Chars(a.Pointer->Pointer) : ReadOnlySpan<char>.Empty)
                .SequenceEqual(b.Pointer->Pointer != 0 ? Bstrs.Chars(b.Pointer->Pointer) : ReadOnlySpan<char>.Empty),
            VarType.Decimal => AdoAdtgCodec.ReadDecimalValue(a) == AdoAdtgCodec.ReadDecimalValue(b),
            VarType.R8 or VarType.Date => BitConverter.DoubleToInt64Bits(a.Pointer->Double) == BitConverter.DoubleToInt64Bits(b.Pointer->Double),
            VarType.Array | VarType.UI1 => BytesEqual(a.Pointer->Pointer, b.Pointer->Pointer),
            _ => a.Pointer->Int64 == b.Pointer->Int64,
        };
    }

    private static bool BytesEqual(nint pa, nint pb)
    {
        int la = pa != 0 ? SafeArrays.Shape(pa)[0].Count : 0;
        int lb = pb != 0 ? SafeArrays.Shape(pb)[0].Count : 0;
        if (la != lb) return false;
        if (la == 0) return true;
        return new ReadOnlySpan<byte>(SafeArrays.ElementPointer(pa, 0), la)
            .SequenceEqual(new ReadOnlySpan<byte>(SafeArrays.ElementPointer(pb, 0), lb));
    }

    /// <summary>Escape single-quoted XML attribute values matching MS-Persist (<c>&amp;apos;</c> rendered as <c>&amp;#x27;</c> in <c>r-ado-1b/persist-stream-roundtrip</c> row 6 <c>it&amp;#x27;s</c>).</summary>
    private static string EscapeAttr(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (char c in value)
        {
            switch (c)
            {
                case '&': sb.Append("&amp;"); break;
                case '<': sb.Append("&lt;"); break;
                case '>': sb.Append("&gt;"); break;
                case '\'': sb.Append("&#x27;"); break;
                case '"': sb.Append("&quot;"); break;
                case '\r': sb.Append("&#xD;"); break;
                case '\n': sb.Append("&#xA;"); break;
                case '\t': sb.Append("&#x9;"); break;
                default: sb.Append(c); break;
            }
        }
        return sb.ToString();
    }
}
