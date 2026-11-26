using CalcDb.Catalog;
using CalcDb.Com.Com;
using CalcDb.Format;
using CalcDb.Foundation;
using CalcDb.Sql;
using CalcDb.Values;
using CalcDb.Values.Scalar;

namespace CalcDb.Com.Ado;

/// <summary>
/// <c>Connection.OpenSchema</c> rowset builders for <c>adSchemaTables(20)</c>, <c>adSchemaColumns(4)</c>,
/// <c>adSchemaIndexes(12)</c>, <c>adSchemaPrimaryKeys(28)</c>, and <c>adSchemaForeignKeys(27)</c>
/// (W-09; R-ADO-1 ADO-SCHEMA, R-OLEDB-1 OLE-BUFFER; evidence/r-q54-excl-ddl-ix-cmd/ado-openschema-accdb,
/// evidence/r-q54-excl-ddl-ix-cmd/ado-openschema-mdb, evidence/r-oledb-odbc/schema-tables,
/// evidence/r-oledb-odbc/schema-columns, evidence/r-oledb-odbc/schema-keys-indexes,
/// evidence/r-oledb-odbc/schema-others, evidence/r-q-ado-misc/schema-ids-no-restrictions).
/// </summary>
internal static class AdoSchema
{
    public const int AdSchemaColumns = 4;
    public const int AdSchemaIndexes = 12;
    public const int AdSchemaTables = 20;
    public const int AdSchemaForeignKeys = 27;
    public const int AdSchemaPrimaryKeys = 28;

    /// <summary>
    /// SchemaEnum values ACE refuses with 0x800A0CB3 when called with no restrictions
    /// (evidence/r-q-ado-misc/schema-ids-no-restrictions, r-q54-excl-ddl-ix-cmd/ado-openschema-accdb #136, r-q54-excl-ddl-ix-cmd/ado-openschema-mdb #136).
    /// </summary>
    public static bool IsAceUnsupportedSchema(int schemaId) => schemaId is
        -1 or 0 or 1 or 2 or 3 or 7 or 11 or 13 or 14 or 15 or 17 or 18 or 21 or 24 or 25 or 26 or 29
        or (>= 32 and <= 41);

    /// <summary>
    /// Build a materialized <see cref="MemoryRowSource"/> for one of the five implemented schema rowsets,
    /// or return null with a named refusal for other ACE-supported schemas (5, 6, 8, 9, 10, 16, 19, 22, 23, 30, 31).
    /// </summary>
    public static MemoryRowSource? TryBuild(AdoOpenSession session, int schemaId, out Failure? refusal)
    {
        refusal = null;
        return schemaId switch
        {
            AdSchemaTables => BuildTables(session),
            AdSchemaColumns => BuildColumns(session),
            AdSchemaIndexes => BuildIndexes(session),
            AdSchemaPrimaryKeys => BuildPrimaryKeys(session),
            AdSchemaForeignKeys => BuildForeignKeys(session),
            _ => FailUnbuiltSchema(schemaId, out refusal),
        };
    }

    private static MemoryRowSource? FailUnbuiltSchema(int schemaId, out Failure refusal)
    {
        refusal = Failure.NotModelled(
            "NotModelled.AdoOpenSchemaType",
            $"Connection.OpenSchema({schemaId}) is not implemented in this slice (only 4, 12, 20, 27, 28 are built)");
        return null;
    }

    // -------------------------------------------------------------------------
    // Column descriptor helpers (exact Type/DefinedSize/Precision/Scale/Attributes from ado-openschema-{accdb,mdb})
    // -------------------------------------------------------------------------

    private static AdoFieldDescriptor ColWStr(string name) => new(name, 202, 255, 255, 255, 70, false, null, -1);
    private static AdoFieldDescriptor ColMemo(string name) => new(name, 203, 536870910, 255, 255, 198, false, null, -1);
    private static AdoFieldDescriptor ColGuid(string name) => new(name, 72, 16, 255, 255, 86, false, null, -1);
    private static AdoFieldDescriptor ColUInt(string name) => new(name, 19, 4, 10, 255, 86, false, null, -1);
    private static AdoFieldDescriptor ColUSmall(string name) => new(name, 18, 2, 5, 255, 86, false, null, -1);
    private static AdoFieldDescriptor ColSmall(string name) => new(name, 2, 2, 5, 255, 86, false, null, -1);
    private static AdoFieldDescriptor ColInt(string name) => new(name, 3, 4, 10, 255, 86, false, null, -1);
    private static AdoFieldDescriptor ColUBig(string name) => new(name, 21, 8, 255, 255, 86, false, null, -1);
    private static AdoFieldDescriptor ColBool(string name) => new(name, 11, 2, 255, 255, 86, false, null, -1);
    private static AdoFieldDescriptor ColDate(string name) => new(name, 7, 8, 255, 255, 86, false, null, -1);

    private static MemoryCell CellNull() => new(OwnedVariant.Null(), null);
    private static MemoryCell CellText(string? s) => s is null ? CellNull() : new(OwnedVariant.FromText(s), null);
    private static MemoryCell CellBool(bool b) => new(OwnedVariant.FromBoolean(b), null);
    private static MemoryCell CellInt32(int v) => new(OwnedVariant.FromInt32(v), null);
    private static MemoryCell CellInt16(short v) => new(OwnedVariant.FromInt16(v), null);
    private static MemoryCell CellDouble(double v) => new(OwnedVariant.FromDouble(v), null);
    private static MemoryCell CellDateBits(ulong? bits) => bits is ulong b
        ? new(OwnedVariant.FromDate(BitConverter.Int64BitsToDouble((long)b)), null)
        : CellNull();
    private static MemoryCell CellDecimalUInt(uint v) =>
        new(OwnedVariant.FromDecimal(new DecimalValue(0, false, 0, v)), null);

    // -------------------------------------------------------------------------
    // Catalog classification helpers
    // -------------------------------------------------------------------------

    private static bool IsSystemTable(CatalogEntry entry) =>
        (entry.Flags & unchecked((int)0x80000002)) != 0
        || entry.Name.StartsWith("MSys", StringComparison.OrdinalIgnoreCase);

    private static bool IsZeroParamView(AdoOpenSession session, SavedQuery query)
    {
        if (query.Family is not (QueryFamily.Select or QueryFamily.Union))
        {
            return false;
        }
        RenderedSql rendered = QuerySql.Render(query);
        if (rendered.Text is not { } sql)
        {
            return false;
        }
        using BoundSelect? plan = BoundSelect.Prepare(sql, session.Sql, out _);
        return plan is not null && plan.Parameters.Count == 0;
    }

    private static string? ReadObjectDescription(AdoOpenSession session, CatalogEntry entry)
    {
        PropertyBlob? blob = session.Blob(entry, out _);
        if (blob?.ObjectMap is not { } map)
        {
            return null;
        }
        StoredProperty? prop = map.Properties.FirstOrDefault(p => string.Equals(p.Name, "Description", StringComparison.OrdinalIgnoreCase));
        if (prop is null)
        {
            return null;
        }
        PropertyValue val = PropertyBlob.Decode(prop);
        return val.Failure is null ? val.Text : null;
    }

    private static (bool HasDefault, string? DefaultText, bool Required, string? Description) ReadColumnProps(
        PropertyBlob? blob,
        string columnName)
    {
        if (blob?.Map(PropertySectionKind.ColumnValues, columnName) is not { } map)
        {
            return (false, null, false, null);
        }
        bool hasDefault = false;
        string? defaultText = null;
        bool required = false;
        string? description = null;
        foreach (StoredProperty prop in map.Properties)
        {
            if (string.Equals(prop.Name, "DefaultValue", StringComparison.OrdinalIgnoreCase))
            {
                PropertyValue v = PropertyBlob.Decode(prop);
                if (v.Failure is null && !string.IsNullOrEmpty(v.Text))
                {
                    hasDefault = true;
                    defaultText = v.Text;
                }
            }
            else if (string.Equals(prop.Name, "Required", StringComparison.OrdinalIgnoreCase))
            {
                PropertyValue v = PropertyBlob.Decode(prop);
                if (v.Failure is null && v.Boolean)
                {
                    required = true;
                }
            }
            else if (string.Equals(prop.Name, "Description", StringComparison.OrdinalIgnoreCase))
            {
                PropertyValue v = PropertyBlob.Decode(prop);
                if (v.Failure is null && v.Text is not null)
                {
                    description = v.Text;
                }
            }
        }
        return (hasDefault, defaultText, required, description);
    }

    private static List<CatalogEntry> GetUserTables(AdoOpenSession session) =>
        [.. session.Catalog.LocalTables(session.Format)
            .Where(e => !IsSystemTable(e))
            .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase)];

    // -------------------------------------------------------------------------
    // 1. adSchemaTables (20) — 9 columns
    // -------------------------------------------------------------------------

    private static readonly AdoFieldDescriptor[] TablesColumns =
    [
        ColWStr("TABLE_CATALOG"),
        ColWStr("TABLE_SCHEMA"),
        ColWStr("TABLE_NAME"),
        ColWStr("TABLE_TYPE"),
        ColGuid("TABLE_GUID"),
        ColMemo("DESCRIPTION"),
        ColUInt("TABLE_PROPID"),
        ColDate("DATE_CREATED"),
        ColDate("DATE_MODIFIED"),
    ];

    private static MemoryRowSource BuildTables(AdoOpenSession session)
    {
        var entries = new List<(string Name, string TableType, CatalogEntry Entry)>();
        if (session.Catalog.TablesContainer is int container)
        {
            foreach (CatalogEntry entry in session.Catalog.Entries)
            {
                if (entry.ParentId != container)
                {
                    continue;
                }
                if (entry.Type == session.Format.Catalog.LocalTableType)
                {
                    // MSysComplexType_* system tables carry ComplexTypeObjectFlag (0x00030000) and are omitted from
                    // adSchemaTables (r-q54-excl-ddl-ix-cmd/ado-openschema-accdb, r-oledb-odbc/schema-tables).
                    if ((entry.Flags & 0x00030000) == 0x00030000)
                    {
                        continue;
                    }
                    string tableType = IsSystemTable(entry) ? "SYSTEM TABLE" : "TABLE";
                    entries.Add((entry.Name, tableType, entry));
                }
                else if (entry.Type is 4 or 6)
                {
                    entries.Add((entry.Name, "LINK", entry));
                }
                else if (entry.Type == 5)
                {
                    if (session.FindSavedQuery(entry.Name) is { } sq && IsZeroParamView(session, sq))
                    {
                        entries.Add((entry.Name, "VIEW", entry));
                    }
                }
            }
        }

        entries.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name));

        var rows = new List<MemoryCell[]>(entries.Count);
        foreach ((string name, string tableType, CatalogEntry entry) in entries)
        {
            string? descr = ReadObjectDescription(session, entry);
            rows.Add(
            [
                CellNull(),
                CellNull(),
                CellText(name),
                CellText(tableType),
                CellNull(),
                CellText(descr),
                CellNull(),
                CellDateBits(entry.DateCreateBits),
                CellDateBits(entry.DateUpdateBits),
            ]);
        }
        return MemoryRowSource.FromRows(TablesColumns, rows);
    }

    // -------------------------------------------------------------------------
    // 2. adSchemaColumns (4) — 28 columns
    // -------------------------------------------------------------------------

    private static readonly AdoFieldDescriptor[] ColumnsSchemaColumns =
    [
        ColWStr("TABLE_CATALOG"),
        ColWStr("TABLE_SCHEMA"),
        ColWStr("TABLE_NAME"),
        ColWStr("COLUMN_NAME"),
        ColGuid("COLUMN_GUID"),
        ColUInt("COLUMN_PROPID"),
        ColUInt("ORDINAL_POSITION"),
        ColBool("COLUMN_HASDEFAULT"),
        ColMemo("COLUMN_DEFAULT"),
        ColUInt("COLUMN_FLAGS"),
        ColBool("IS_NULLABLE"),
        ColUSmall("DATA_TYPE"),
        ColGuid("TYPE_GUID"),
        ColUInt("CHARACTER_MAXIMUM_LENGTH"),
        ColUInt("CHARACTER_OCTET_LENGTH"),
        ColUSmall("NUMERIC_PRECISION"),
        ColSmall("NUMERIC_SCALE"),
        ColUInt("DATETIME_PRECISION"),
        ColWStr("CHARACTER_SET_CATALOG"),
        ColWStr("CHARACTER_SET_SCHEMA"),
        ColWStr("CHARACTER_SET_NAME"),
        ColWStr("COLLATION_CATALOG"),
        ColWStr("COLLATION_SCHEMA"),
        ColWStr("COLLATION_NAME"),
        ColWStr("DOMAIN_CATALOG"),
        ColWStr("DOMAIN_SCHEMA"),
        ColWStr("DOMAIN_NAME"),
        ColMemo("DESCRIPTION"),
    ];

    private static MemoryRowSource BuildColumns(AdoOpenSession session)
    {
        var items = new List<(string TableName, string ColumnName, MemoryCell[] Row)>();

        // Local user tables
        foreach (CatalogEntry entry in GetUserTables(session))
        {
            if (session.Definition(entry, out _) is not { } def)
            {
                continue;
            }
            PropertyBlob? blob = session.Blob(entry, out _);
            for (int i = 0; i < def.Columns.Count; i++)
            {
                ColumnDescriptor col = def.Columns[i];
                (bool hasDefault, string? defaultText, bool required, string? descr) = ReadColumnProps(blob, col.Name);
                MemoryCell[] row = BuildColumnRow(
                    entry.Name,
                    col.Name,
                    ordinalOneBased: i + 1,
                    col,
                    isView: false,
                    hasDefault,
                    defaultText,
                    required,
                    descr);
                items.Add((entry.Name, col.Name, row));
            }
        }

        // Views (saved zero-parameter SELECT/UNION queries)
        if (session.Queries is not null)
        {
            foreach (SavedQuery sq in session.Queries.Queries)
            {
                if (sq.Family is not (QueryFamily.Select or QueryFamily.Union))
                {
                    continue;
                }
                RenderedSql rendered = QuerySql.Render(sq);
                if (rendered.Text is not { } sql)
                {
                    continue;
                }
                using BoundSelect? plan = BoundSelect.Prepare(sql, session.Sql, out _);
                if (plan is null || plan.Parameters.Count > 0)
                {
                    continue;
                }
                for (int i = 0; i < plan.Columns.Count; i++)
                {
                    ResultColumn rc = plan.Columns[i];
                    if (rc.SourceColumn is { } src)
                    {
                        MemoryCell[] row = BuildColumnRow(
                            sq.Name,
                            rc.Name,
                            ordinalOneBased: i + 1,
                            src.Column,
                            isView: true,
                            hasDefault: false,
                            defaultText: null,
                            required: false,
                            description: null);
                        items.Add((sq.Name, rc.Name, row));
                    }
                    else
                    {
                        MemoryCell[] row = BuildViewExpressionColumnRow(sq.Name, rc, ordinalOneBased: i + 1);
                        items.Add((sq.Name, rc.Name, row));
                    }
                }
            }
        }

        items.Sort((a, b) =>
        {
            int cmp = StringComparer.OrdinalIgnoreCase.Compare(a.TableName, b.TableName);
            return cmp != 0 ? cmp : StringComparer.OrdinalIgnoreCase.Compare(a.ColumnName, b.ColumnName);
        });

        var rows = new List<MemoryCell[]>(items.Count);
        foreach (var item in items)
        {
            rows.Add(item.Row);
        }
        return MemoryRowSource.FromRows(ColumnsSchemaColumns, rows);
    }

    private static MemoryCell[] BuildColumnRow(
        string tableName,
        string columnName,
        int ordinalOneBased,
        ColumnDescriptor col,
        bool isView,
        bool hasDefault,
        string? defaultText,
        bool required,
        string? description)
    {
        byte typeCode = col.CalculatedResultType ?? col.TypeCode;
        bool isNullable = typeCode != NativeTypes.Boolean && !col.IsAutoNumber && (!required || isView);

        bool isLong = typeCode is NativeTypes.Memo or NativeTypes.Ole;
        bool isVar = typeCode is NativeTypes.Text or NativeTypes.Binary;
        // DBCOLUMNFLAGS:
        // Base table: Fixed nullable=122, Fixed non-null=90, Var nullable=106, Var non-null=74, Long nullable=234, Long non-null=202
        // View: Fixed nullable=118 (WRITE instead of WRITEUNKNOWN), Fixed non-null=90, Var nullable=102, Long nullable=230
        int flags;
        if (isLong)
        {
            flags = isNullable ? (isView ? 230 : 234) : 202;
        }
        else if (isVar)
        {
            flags = isNullable ? (isView ? 102 : 106) : 74;
        }
        else
        {
            flags = isNullable ? (isView ? 118 : 122) : 90;
        }

        int dataType;
        MemoryCell charMax = CellNull();
        MemoryCell charOctet = CellNull();
        MemoryCell numPrec = CellNull();
        MemoryCell numScale = CellNull();
        MemoryCell dtPrec = CellNull();

        switch (typeCode)
        {
            case NativeTypes.Boolean:
                dataType = 11;
                charMax = CellDouble(2);
                break;
            case NativeTypes.Byte:
                dataType = 17;
                numPrec = CellInt32(3);
                break;
            case NativeTypes.Integer:
                dataType = 2;
                numPrec = CellInt32(5);
                break;
            case NativeTypes.Long:
                dataType = 3;
                numPrec = CellInt32(10);
                break;
            case NativeTypes.Single:
                dataType = 4;
                numPrec = CellInt32(7);
                break;
            case NativeTypes.Double:
                dataType = 5;
                numPrec = CellInt32(15);
                break;
            case NativeTypes.Currency:
                dataType = 6;
                numPrec = CellInt32(19);
                break;
            case NativeTypes.DateTime:
                dataType = 7;
                dtPrec = CellDouble(0);
                break;
            case NativeTypes.Guid:
                dataType = 72;
                break;
            case NativeTypes.Numeric:
                dataType = 131;
                numPrec = CellInt32(col.NumericPrecision);
                numScale = CellInt16(col.NumericScale);
                break;
            case NativeTypes.Text:
                dataType = 130; // DBTYPE_WSTR
                charMax = CellDouble(col.Length / 2);
                charOctet = CellDouble(col.Length);
                break;
            case NativeTypes.Memo:
                dataType = 130;
                charMax = CellDouble(0);
                charOctet = CellDouble(0);
                break;
            case NativeTypes.Binary:
                dataType = 128; // DBTYPE_BYTES
                charMax = CellDouble(col.Length);
                charOctet = CellDouble(col.Length);
                break;
            case NativeTypes.Ole:
                dataType = 128;
                charMax = CellDouble(0);
                charOctet = CellDouble(0);
                break;
            case NativeTypes.BigInt:
                dataType = 20;
                numPrec = CellInt32(19);
                break;
            default:
                dataType = 130;
                break;
        }

        return
        [
            CellNull(),                          // 0 TABLE_CATALOG
            CellNull(),                          // 1 TABLE_SCHEMA
            CellText(tableName),                 // 2 TABLE_NAME
            CellText(columnName),                // 3 COLUMN_NAME
            CellNull(),                          // 4 COLUMN_GUID
            CellNull(),                          // 5 COLUMN_PROPID
            CellDouble(ordinalOneBased),         // 6 ORDINAL_POSITION (declared 19, runtime VT_R8)
            CellBool(hasDefault),                // 7 COLUMN_HASDEFAULT
            CellText(defaultText),               // 8 COLUMN_DEFAULT
            CellDouble(flags),                   // 9 COLUMN_FLAGS (declared 19, runtime VT_R8)
            CellBool(isNullable),                // 10 IS_NULLABLE
            CellInt32(dataType),                 // 11 DATA_TYPE (declared 18, runtime VT_I4)
            CellNull(),                          // 12 TYPE_GUID
            charMax,                             // 13 CHARACTER_MAXIMUM_LENGTH (declared 19, runtime VT_R8)
            charOctet,                           // 14 CHARACTER_OCTET_LENGTH (declared 19, runtime VT_R8)
            numPrec,                             // 15 NUMERIC_PRECISION (declared 18, runtime VT_I4)
            numScale,                            // 16 NUMERIC_SCALE (declared 2, runtime VT_I2)
            dtPrec,                              // 17 DATETIME_PRECISION (declared 19, runtime VT_R8)
            CellNull(),                          // 18 CHARACTER_SET_CATALOG
            CellNull(),                          // 19 CHARACTER_SET_SCHEMA
            CellNull(),                          // 20 CHARACTER_SET_NAME
            CellNull(),                          // 21 COLLATION_CATALOG
            CellNull(),                          // 22 COLLATION_SCHEMA
            CellNull(),                          // 23 COLLATION_NAME
            CellNull(),                          // 24 DOMAIN_CATALOG
            CellNull(),                          // 25 DOMAIN_SCHEMA
            CellNull(),                          // 26 DOMAIN_NAME
            CellText(description),               // 27 DESCRIPTION
        ];
    }

    private static MemoryCell[] BuildViewExpressionColumnRow(string viewName, ResultColumn rc, int ordinalOneBased)
    {
        int dataType = rc.FieldType switch
        {
            1 => 11,
            2 => 17,
            3 => 2,
            4 => 3,
            5 => 6,
            6 => 4,
            7 => 5,
            8 => 7,
            10 or 12 => 130,
            9 or 11 => 128,
            15 => 72,
            20 => 131,
            _ => 130,
        };
        int flags = dataType is 130 or 128 ? 102 : 118;
        return
        [
            CellNull(), CellNull(), CellText(viewName), CellText(rc.Name),
            CellNull(), CellNull(), CellDouble(ordinalOneBased), CellBool(false),
            CellNull(), CellDouble(flags), CellBool(true), CellInt32(dataType),
            CellNull(), CellNull(), CellNull(), CellNull(), CellNull(), CellNull(),
            CellNull(), CellNull(), CellNull(), CellNull(), CellNull(), CellNull(),
            CellNull(), CellNull(), CellNull(), CellNull(),
        ];
    }

    // -------------------------------------------------------------------------
    // 3. adSchemaIndexes (12) — 25 columns
    // -------------------------------------------------------------------------

    private static readonly AdoFieldDescriptor[] IndexesColumns =
    [
        ColWStr("TABLE_CATALOG"),
        ColWStr("TABLE_SCHEMA"),
        ColWStr("TABLE_NAME"),
        ColWStr("INDEX_CATALOG"),
        ColWStr("INDEX_SCHEMA"),
        ColWStr("INDEX_NAME"),
        ColBool("PRIMARY_KEY"),
        ColBool("UNIQUE"),
        ColBool("CLUSTERED"),
        ColUSmall("TYPE"),
        ColInt("FILL_FACTOR"),
        ColInt("INITIAL_SIZE"),
        ColInt("NULLS"),
        ColBool("SORT_BOOKMARKS"),
        ColBool("AUTO_UPDATE"),
        ColInt("NULL_COLLATION"),
        ColUInt("ORDINAL_POSITION"),
        ColWStr("COLUMN_NAME"),
        ColGuid("COLUMN_GUID"),
        ColUInt("COLUMN_PROPID"),
        ColSmall("COLLATION"),
        ColUBig("CARDINALITY"),
        ColInt("PAGES"),
        ColWStr("FILTER_CONDITION"),
        ColBool("INTEGRATED"),
    ];

    private static MemoryRowSource BuildIndexes(AdoOpenSession session)
    {
        var items = new List<(bool IsPk, bool IsUnique, string TableName, string IndexName, int Ordinal, MemoryCell[] Row)>();
        IndexDescriptorRules ixRules = session.Format.IndexDescriptor;

        foreach (CatalogEntry entry in GetUserTables(session))
        {
            if (session.Definition(entry, out _) is not { } def)
            {
                continue;
            }
            var physByNum = def.PhysicalIndexes.ToDictionary(static p => p.Number);
            var colByPhys = def.Columns.ToDictionary(static c => c.PhysicalNumber);

            foreach (LogicalIndex logical in def.LogicalIndexes)
            {
                // Skip internal parent-side FK back-reference indexes (e.g. ".rD"; r-q54-excl-ddl-ix-cmd/ado-openschema-mdb, r-oledb-odbc/schema-keys-indexes).
                if (logical.Name.StartsWith('.'))
                {
                    continue;
                }
                if (!physByNum.TryGetValue(logical.PhysicalNumber, out PhysicalIndex? phys))
                {
                    continue;
                }
                bool isPk = logical.IndexType == ixRules.PrimaryKeyType;
                bool isUnique = phys.Unique(ixRules);
                int nullsVal = phys.Required(ixRules) ? 1 : (phys.IgnoreNulls(ixRules) ? 4 : 0);

                for (int k = 0; k < phys.Columns.Count; k++)
                {
                    IndexColumn ixCol = phys.Columns[k];
                    string colName = colByPhys.TryGetValue(ixCol.PhysicalNumber, out ColumnDescriptor? cd) ? cd.Name : "";
                    short collation = ixCol.Ascending ? (short)1 : (short)2;
                    int ord = k + 1;

                    MemoryCell[] row =
                    [
                        CellNull(),                          // 0 TABLE_CATALOG
                        CellNull(),                          // 1 TABLE_SCHEMA
                        CellText(entry.Name),                // 2 TABLE_NAME
                        CellNull(),                          // 3 INDEX_CATALOG
                        CellNull(),                          // 4 INDEX_SCHEMA
                        CellText(logical.Name),              // 5 INDEX_NAME
                        CellBool(isPk),                      // 6 PRIMARY_KEY
                        CellBool(isUnique),                  // 7 UNIQUE
                        CellBool(false),                     // 8 CLUSTERED
                        CellInt32(1),                        // 9 TYPE (DBPROPVAL_IT_BTREE, declared 18, runtime VT_I4)
                        CellInt32(100),                      // 10 FILL_FACTOR
                        CellInt32(4096),                     // 11 INITIAL_SIZE
                        CellInt32(nullsVal),                 // 12 NULLS (1=DISALLOW on PK/Required, 0=ALLOW)
                        CellBool(false),                     // 13 SORT_BOOKMARKS
                        CellBool(true),                      // 14 AUTO_UPDATE
                        CellInt32(4),                        // 15 NULL_COLLATION (DBPROPVAL_NC_LOW)
                        CellDouble(ord),                     // 16 ORDINAL_POSITION (declared 19, runtime VT_R8)
                        CellText(colName),                   // 17 COLUMN_NAME
                        CellNull(),                          // 18 COLUMN_GUID
                        CellNull(),                          // 19 COLUMN_PROPID
                        CellInt16(collation),                // 20 COLLATION (1=ASC, 2=DESC)
                        CellDecimalUInt(phys.UniqueEntryCount), // 21 CARDINALITY (declared 21, runtime VT_DECIMAL scale 0)
                        CellInt32(1),                        // 22 PAGES
                        CellNull(),                          // 23 FILTER_CONDITION
                        CellBool(true),                      // 24 INTEGRATED
                    ];
                    items.Add((isPk, isUnique, entry.Name, logical.Name, ord, row));
                }
            }
        }

        // Ordering verified in ado-openschema-{accdb,mdb} and r-oledb-odbc/schema-keys-indexes:
        // PRIMARY_KEY desc, UNIQUE desc, TABLE_NAME asc, INDEX_NAME asc, ORDINAL_POSITION asc.
        items.Sort((a, b) =>
        {
            int cmp = b.IsPk.CompareTo(a.IsPk);
            if (cmp != 0) return cmp;
            cmp = b.IsUnique.CompareTo(a.IsUnique);
            if (cmp != 0) return cmp;
            cmp = StringComparer.OrdinalIgnoreCase.Compare(a.TableName, b.TableName);
            if (cmp != 0) return cmp;
            cmp = StringComparer.OrdinalIgnoreCase.Compare(a.IndexName, b.IndexName);
            if (cmp != 0) return cmp;
            return a.Ordinal.CompareTo(b.Ordinal);
        });

        var rows = new List<MemoryCell[]>(items.Count);
        foreach (var item in items)
        {
            rows.Add(item.Row);
        }
        return MemoryRowSource.FromRows(IndexesColumns, rows);
    }

    // -------------------------------------------------------------------------
    // 4. adSchemaPrimaryKeys (28) — 8 columns
    // -------------------------------------------------------------------------

    private static readonly AdoFieldDescriptor[] PrimaryKeysColumns =
    [
        ColWStr("TABLE_CATALOG"),
        ColWStr("TABLE_SCHEMA"),
        ColWStr("TABLE_NAME"),
        ColWStr("COLUMN_NAME"),
        ColGuid("COLUMN_GUID"),
        ColUInt("COLUMN_PROPID"),
        ColUInt("ORDINAL"),
        ColWStr("PK_NAME"),
    ];

    private static MemoryRowSource BuildPrimaryKeys(AdoOpenSession session)
    {
        var items = new List<(string TableName, int Ordinal, MemoryCell[] Row)>();
        byte pkType = session.Format.IndexDescriptor.PrimaryKeyType;

        foreach (CatalogEntry entry in GetUserTables(session))
        {
            if (session.Definition(entry, out _) is not { } def)
            {
                continue;
            }
            LogicalIndex? pk = def.LogicalIndexes.FirstOrDefault(ix => ix.IndexType == pkType);
            if (pk is null)
            {
                continue;
            }
            PhysicalIndex? phys = def.PhysicalIndexes.FirstOrDefault(p => p.Number == pk.PhysicalNumber);
            if (phys is null)
            {
                continue;
            }
            var colByPhys = def.Columns.ToDictionary(static c => c.PhysicalNumber);
            for (int k = 0; k < phys.Columns.Count; k++)
            {
                string colName = colByPhys.TryGetValue(phys.Columns[k].PhysicalNumber, out ColumnDescriptor? cd) ? cd.Name : "";
                int ord = k + 1;
                items.Add((entry.Name, ord,
                [
                    CellNull(),
                    CellNull(),
                    CellText(entry.Name),
                    CellText(colName),
                    CellNull(),
                    CellNull(),
                    CellDouble(ord),
                    CellText(pk.Name),
                ]));
            }
        }

        items.Sort((a, b) =>
        {
            int cmp = StringComparer.OrdinalIgnoreCase.Compare(a.TableName, b.TableName);
            return cmp != 0 ? cmp : a.Ordinal.CompareTo(b.Ordinal);
        });

        var rows = new List<MemoryCell[]>(items.Count);
        foreach (var item in items)
        {
            rows.Add(item.Row);
        }
        return MemoryRowSource.FromRows(PrimaryKeysColumns, rows);
    }

    // -------------------------------------------------------------------------
    // 5. adSchemaForeignKeys (27) — 18 columns
    // -------------------------------------------------------------------------

    private static readonly AdoFieldDescriptor[] ForeignKeysColumns =
    [
        ColWStr("PK_TABLE_CATALOG"),
        ColWStr("PK_TABLE_SCHEMA"),
        ColWStr("PK_TABLE_NAME"),
        ColWStr("PK_COLUMN_NAME"),
        ColGuid("PK_COLUMN_GUID"),
        ColUInt("PK_COLUMN_PROPID"),
        ColWStr("FK_TABLE_CATALOG"),
        ColWStr("FK_TABLE_SCHEMA"),
        ColWStr("FK_TABLE_NAME"),
        ColWStr("FK_COLUMN_NAME"),
        ColGuid("FK_COLUMN_GUID"),
        ColUInt("FK_COLUMN_PROPID"),
        ColUInt("ORDINAL"),
        ColWStr("UPDATE_RULE"),
        ColWStr("DELETE_RULE"),
        ColWStr("PK_NAME"),
        ColWStr("FK_NAME"),
        ColSmall("DEFERRABILITY"),
    ];

    private static MemoryRowSource BuildForeignKeys(AdoOpenSession session)
    {
        var items = new List<(string PkTable, string FkTable, string FkName, int Ordinal, MemoryCell[] Row)>();
        IReadOnlyList<Relationship>? rels = session.Relationships(out _);
        if (rels is not null)
        {
            byte pkType = session.Format.IndexDescriptor.PrimaryKeyType;
            var pkNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (CatalogEntry entry in GetUserTables(session))
            {
                if (session.Definition(entry, out _) is { } def
                    && def.LogicalIndexes.FirstOrDefault(ix => ix.IndexType == pkType) is { } pk)
                {
                    pkNames[entry.Name] = pk.Name;
                }
            }

            foreach (Relationship rel in rels)
            {
                string updateRule = (rel.Attributes & 256) != 0 ? "CASCADE" : "NO ACTION";
                string deleteRule = (rel.Attributes & 4096) != 0
                    ? "CASCADE"
                    : ((rel.Attributes & 0x2000) != 0 ? "SET NULL" : "NO ACTION");
                string? pkName = pkNames.GetValueOrDefault(rel.Table);

                for (int k = 0; k < rel.Columns.Count; k++)
                {
                    RelationshipColumn col = rel.Columns[k];
                    int ord = k + 1;
                    items.Add((rel.Table, rel.ForeignTable, rel.Name, ord,
                    [
                        CellNull(),                  // 0 PK_TABLE_CATALOG
                        CellNull(),                  // 1 PK_TABLE_SCHEMA
                        CellText(rel.Table),         // 2 PK_TABLE_NAME
                        CellText(col.Referenced),    // 3 PK_COLUMN_NAME
                        CellNull(),                  // 4 PK_COLUMN_GUID
                        CellNull(),                  // 5 PK_COLUMN_PROPID
                        CellNull(),                  // 6 FK_TABLE_CATALOG
                        CellNull(),                  // 7 FK_TABLE_SCHEMA
                        CellText(rel.ForeignTable),  // 8 FK_TABLE_NAME
                        CellText(col.Foreign),       // 9 FK_COLUMN_NAME
                        CellNull(),                  // 10 FK_COLUMN_GUID
                        CellNull(),                  // 11 FK_COLUMN_PROPID
                        CellDouble(ord),             // 12 ORDINAL (declared 19, runtime VT_R8)
                        CellText(updateRule),        // 13 UPDATE_RULE
                        CellText(deleteRule),        // 14 DELETE_RULE
                        CellText(pkName),            // 15 PK_NAME
                        CellText(rel.Name),          // 16 FK_NAME
                        CellNull(),                  // 17 DEFERRABILITY
                    ]));
                }
            }
        }

        items.Sort((a, b) =>
        {
            int cmp = StringComparer.OrdinalIgnoreCase.Compare(a.PkTable, b.PkTable);
            if (cmp != 0) return cmp;
            cmp = StringComparer.OrdinalIgnoreCase.Compare(a.FkTable, b.FkTable);
            if (cmp != 0) return cmp;
            cmp = StringComparer.OrdinalIgnoreCase.Compare(a.FkName, b.FkName);
            if (cmp != 0) return cmp;
            return a.Ordinal.CompareTo(b.Ordinal);
        });

        var rows = new List<MemoryCell[]>(items.Count);
        foreach (var item in items)
        {
            rows.Add(item.Row);
        }
        return MemoryRowSource.FromRows(ForeignKeysColumns, rows);
    }
}
