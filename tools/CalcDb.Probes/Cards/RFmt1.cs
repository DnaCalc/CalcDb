namespace CalcDb.Probes.Cards;

/// <summary>
/// R-FMT-1: format families, descriptors and minimal valid files (A-02). Fixtures are created through ACE
/// DAO, one feature at a time, in both the Jet 4 MDB and ACE ACCDB formats; each creation keeps the complete
/// output file (bytes for the later W-04 inspector) and each fixture is then reopened read-only and its
/// public metadata and rows are enumerated. Reopening through Access is a tier 2 route and is not here.
/// </summary>
public sealed class RFmt1 : ICard
{
    private const string Card = "r-fmt-1";
    private static readonly string[] Refs = ["R-FMT-1", "D-FMT-1", "II-03", "II-04", "T-FMT-01", "T-FMT-02"];

    /// <summary>A scalar field family: DDL type and typed values to store (a Null row is always added).</summary>
    private sealed record Family(string Name, string Ddl, Arg[] Values);

    private static readonly Family[] Families =
    [
        new("byte", "BYTE", [A.UI1(0), A.UI1(255)]),
        new("integer", "SHORT", [A.I2(short.MinValue), A.I2(short.MaxValue)]),
        new("long", "LONG", [A.I4(int.MinValue), A.I4(int.MaxValue)]),
        new("single", "SINGLE", [A.R4(1.5f), A.R4(-0.0f), A.R4(float.Epsilon), A.R4(float.MaxValue)]),
        new("double", "DOUBLE", [A.R8(0.1), A.R8(-0.0), A.R8(double.Epsilon), A.R8(double.MaxValue)]),
        new("currency", "CURRENCY", [A.Cy(-922337203685477.5808m), A.Cy(922337203685477.5807m), A.Cy(1.2345m)]),
        new("datetime", "DATETIME", [A.Date(0.0), A.Date(-657434.0), A.Date(2958465.99998843), A.Date(1.5), A.Date(-1.5)]),
        new("text", "TEXT(50)", [A.Str(""), A.Str("alpha"), A.Str("Bä€"), A.Str("\U0001D11E surrogate pair"), A.Str(new string('x', 50))]),
        new("memo", "MEMO", [A.Str(""), A.Str("short memo"), A.Str(new string('m', 5000)), A.Str(string.Concat(Enumerable.Repeat("Bä€", 3000)))]),
        new("yesno", "YESNO", [A.Bool(true), A.Bool(false)]),
        new("binary", "BINARY(50)", [A.Bytes(), A.Bytes(0x00, 0x01, 0xff), A.Bytes([.. Enumerable.Range(0, 50).Select(i => (byte)i)])]),
        new("longbinary", "LONGBINARY", [A.Bytes(0x00, 0x01, 0xff), A.Bytes([.. Enumerable.Range(0, 5000).Select(i => (byte)(i * 7))])]),
        new("guid", "GUID", [A.Str("{00112233-4455-6677-8899-AABBCCDDEEFF}"), A.Str("{FFFFFFFF-FFFF-FFFF-FFFF-FFFFFFFFFFFF}")]),
        new("decimal", "DECIMAL(18,4)", [A.Str("12345678901234.5678"), A.Str("-0.0001"), A.Cy(1.5m), A.I4(7)]),
        new("bigint", "BIGINT", [A.Str("9223372036854775807"), A.I4(-1)]),
        new("datetime2", "DATETIME2", [A.Date(1.5)]),
    ];

    private static readonly (string Ext, int Version)[] Formats = [("accdb", Dao.Version120), ("mdb", Dao.Version40)];

    public IEnumerable<Probe> Probes()
    {
        List<Probe> creations = Creations().ToList();
        // Read every created fixture back: metadata and rows through a fresh ACE engine, read-only.
        IEnumerable<Probe> reads = creations.Select(c => FixtureName(c.Name)).Select(fixture => new Probe(Card, "read-" + fixture[(Card.Length + 1)..], Refs,
            $"Reopen fixture {fixture} read-only through ACE DAO and enumerate its version, collating order, properties, tables, fields, indexes and rows.",
            p => ReadBack(p)) { Fixtures = [("main", fixture)] });
        return creations.Concat(reads);
    }

    private static IEnumerable<Probe> Creations()
    {
        foreach ((string ext, int version) in Formats)
        {
            // The empty file and one empty table: the smallest practical native files.
            yield return Creation($"{ext}-empty", ext, version, "an empty database created by DBEngine.CreateDatabase alone", _ => { });
            yield return Creation($"{ext}-table-empty", ext, version, "one empty table with a Long primary key",
                p => p.Call("db", "Execute", A.Str("CREATE TABLE T (ID LONG CONSTRAINT PK_T PRIMARY KEY)")));

            foreach (Family f in Families)
            {
                yield return Creation($"{ext}-{f.Name}", ext, version,
                    $"table T(ID LONG primary key, V {f.Ddl}) with a Null row and the typed values given, stored through a table-type recordset AddNew/Value/Update",
                    p => ScalarTable(p, f));
            }

            yield return Creation($"{ext}-counter", ext, version, "an AutoNumber (COUNTER) primary key with three rows added by AddNew and one deleted",
                p => p.Call("db", "Execute", A.Str("CREATE TABLE T (ID COUNTER CONSTRAINT PK_T PRIMARY KEY, V TEXT(10))"))
                    .Call("db", "Execute", A.Str("INSERT INTO T (V) VALUES ('a')"))
                    .Call("db", "Execute", A.Str("INSERT INTO T (V) VALUES ('b')"))
                    .Call("db", "Execute", A.Str("INSERT INTO T (V) VALUES ('c')"))
                    .Call("db", "Execute", A.Str("DELETE FROM T WHERE V = 'b'")));

            yield return Creation($"{ext}-index", ext, version, "a primary key plus a nonprimary unique index and a nonunique two-column index",
                p => p.Call("db", "Execute", A.Str("CREATE TABLE T (ID LONG CONSTRAINT PK_T PRIMARY KEY, A TEXT(20), B LONG)"))
                    .Call("db", "Execute", A.Str("CREATE UNIQUE INDEX UX_A ON T (A)"))
                    .Call("db", "Execute", A.Str("CREATE INDEX IX_BA ON T (B DESC, A)"))
                    .Call("db", "Execute", A.Str("INSERT INTO T VALUES (1, 'one', 10)"))
                    .Call("db", "Execute", A.Str("INSERT INTO T VALUES (2, 'two', 10)")));

            yield return Creation($"{ext}-properties", ext, version, "a user-defined database property and a table Description property created with CreateProperty/Append",
                p => p.Call("db", "Execute", A.Str("CREATE TABLE T (ID LONG CONSTRAINT PK_T PRIMARY KEY)"))
                    .CallBind("prop", "db", "CreateProperty", A.Str("CalcDbProbe"), A.I4(10), A.Str("value of a user property"))
                    .GetBind("props", "db", "Properties").Call("props", "Append", A.Obj("prop")).Release("prop").Release("props")
                    .GetBind("tdfs", "db", "TableDefs").Item("tdf", "tdfs", A.Str("T"))
                    .CallBind("tprop", "tdf", "CreateProperty", A.Str("Description"), A.I4(10), A.Str("table description"))
                    .GetBind("tprops", "tdf", "Properties").Call("tprops", "Append", A.Obj("tprop"))
                    .Release("tprop").Release("tprops").Release("tdf").Release("tdfs"));

            yield return Creation($"{ext}-querydef", ext, version, "a saved select query and a saved parameter query created with CreateQueryDef",
                p => p.Call("db", "Execute", A.Str("CREATE TABLE T (ID LONG CONSTRAINT PK_T PRIMARY KEY, V TEXT(20))"))
                    .Call("db", "Execute", A.Str("INSERT INTO T VALUES (1, 'a')"))
                    .CallBind("q1", "db", "CreateQueryDef", A.Str("QSelect"), A.Str("SELECT ID, V FROM T ORDER BY ID;"))
                    .CallBind("q2", "db", "CreateQueryDef", A.Str("QParam"), A.Str("PARAMETERS pId Long; SELECT * FROM T WHERE ID = pId;"))
                    .Release("q1").Release("q2"));

            yield return Creation($"{ext}-renamed-dropped", ext, version, "a table whose column was renamed through Field.Name and another dropped through ALTER TABLE, with rows written before and after",
                p => p.Call("db", "Execute", A.Str("CREATE TABLE T (ID LONG CONSTRAINT PK_T PRIMARY KEY, A TEXT(10), B LONG, C TEXT(10))"))
                    .Call("db", "Execute", A.Str("INSERT INTO T VALUES (1, 'a1', 11, 'c1')"))
                    .GetBind("tdfs", "db", "TableDefs").Item("tdf", "tdfs", A.Str("T")).GetBind("fl", "tdf", "Fields").Item("fa", "fl", A.Str("A"))
                    .Put("fa", "Name", A.Str("A_renamed")).Release("fa").Release("fl").Release("tdf").Release("tdfs")
                    .Call("db", "Execute", A.Str("ALTER TABLE T DROP COLUMN B"))
                    .Call("db", "Execute", A.Str("ALTER TABLE T ADD COLUMN D DOUBLE"))
                    .Call("db", "Execute", A.Str("INSERT INTO T (ID, A_renamed, C, D) VALUES (2, 'a2', 'c2', 2.5)")));

            yield return Creation($"{ext}-ordinal", ext, version, "visible column order changed through Field.OrdinalPosition, different from creation order",
                p => p.Call("db", "Execute", A.Str("CREATE TABLE T (ID LONG CONSTRAINT PK_T PRIMARY KEY, A TEXT(10), B TEXT(10))"))
                    .GetBind("tdfs", "db", "TableDefs").Item("tdf", "tdfs", A.Str("T")).GetBind("fl", "tdf", "Fields")
                    .Item("fb", "fl", A.Str("B")).Put("fb", "OrdinalPosition", A.I2(0)).Release("fb")
                    .Item("fid", "fl", A.Str("ID")).Put("fid", "OrdinalPosition", A.I2(2)).Release("fid")
                    .Release("fl").Release("tdf").Release("tdfs")
                    .Call("db", "Execute", A.Str("INSERT INTO T (ID, A, B) VALUES (1, 'a', 'b')")));

            yield return Creation($"{ext}-wide", ext, version, "a 120-column table, to force a TableDef continuation (II-04)",
                p => p.Call("db", "Execute", A.Str("CREATE TABLE T (ID LONG CONSTRAINT PK_T PRIMARY KEY, " +
                    string.Join(", ", Enumerable.Range(1, 119).Select(i => $"C{i:D3}_with_a_long_column_name TEXT(10)")) + ")"))
                    .Call("db", "Execute", A.Str("INSERT INTO T (ID, C001_with_a_long_column_name, C119_with_a_long_column_name) VALUES (1, 'first', 'last')")));
        }

        // ACCDB only: a calculated field through the DAO Field2.Expression property (II-04 calculated dispatch).
        yield return Creation("accdb-calculated", "accdb", Dao.Version120, "a calculated Text field defined by Field.Expression before TableDefs.Append",
            p => p.CallBind("tdf", "db", "CreateTableDef", A.Str("T"))
                .CallBind("fid", "tdf", "CreateField", A.Str("ID"), A.I4(4)).GetBind("fl", "tdf", "Fields").Call("fl", "Append", A.Obj("fid"))
                .CallBind("fa", "tdf", "CreateField", A.Str("A"), A.I4(10), A.I4(20)).Call("fl", "Append", A.Obj("fa"))
                .CallBind("fc", "tdf", "CreateField", A.Str("C"), A.I4(10), A.I4(40)).Put("fc", "Expression", A.Str("[A] & \"-calc\""))
                .Call("fl", "Append", A.Obj("fc"))
                .GetBind("tdfs", "db", "TableDefs").Call("tdfs", "Append", A.Obj("tdf"))
                .Release("fl").Release("fid").Release("fa").Release("fc").Release("tdf").Release("tdfs")
                .Call("db", "Execute", A.Str("INSERT INTO T (ID, A) VALUES (1, 'x')")));

        // The legacy sort order: CreateDatabase with the General sort order locale versus Database.CollatingOrder.
        yield return Creation("accdb-collating-general", "accdb", Dao.Version120, "CreateDatabase with dbLangGeneral; CollatingOrder is read back",
            p => p.Get("db", "CollatingOrder").Call("db", "Execute", A.Str("CREATE TABLE T (ID LONG CONSTRAINT PK_T PRIMARY KEY, S TEXT(20))"))
                .Call("db", "Execute", A.Str("CREATE INDEX IX_S ON T (S)")));

    }

    private static string FixtureName(string probeName) => Card + "-" + probeName["create-".Length..];

    private static Probe Creation(string name, string ext, int version, string what, Action<P> body) =>
        new(Card, "create-" + name, Refs, $"R-FMT-1 fixture: {what}. Created in {ext.ToUpperInvariant()} by DBEngine.CreateDatabase(path, dbLangGeneral, {version}); the closed file is kept.",
            p =>
            {
                p.Modules("before-create").Create().Modules("after-create").Get("eng", "Version")
                    .CallBind("db", "eng", "CreateDatabase", A.Out($"{Card}-{name}.{ext}"), A.Str(Dao.LangGeneral), A.I4(version))
                    .Get("db", "Version");
                body(p);
                p.Call("db", "Close").Release("db").Release("eng").Modules("after-close").Files("after-close");
            })
        { CreatesFixture = true };

    private static void ScalarTable(P p, Family f)
    {
        p.Call("db", "Execute", A.Str($"CREATE TABLE T (ID LONG CONSTRAINT PK_T PRIMARY KEY, V {f.Ddl})"))
            .OpenRs("rs", "db", "T", Dao.OpenTable).GetBind("fl", "rs", "Fields").Item("fid", "fl", A.Str("ID")).Item("fv", "fl", A.Str("V"));
        Arg[] values = [A.Null, .. f.Values];
        for (int i = 0; i < values.Length; i++)
        {
            p.Call("rs", "AddNew").Put("fid", "Value", A.I4(i + 1)).Put("fv", "Value", values[i]).Call("rs", "Update");
        }
        p.Release("fid").Release("fv").Release("fl").Call("rs", "Close").Release("rs");
    }

    /// <summary>The public view of a database: version, collating order, properties, and every table.</summary>
    internal static void ReadBack(P p)
    {
        p.Engine().OpenDb().Get("db", "Version").Get("db", "CollatingOrder").Files("after-open")
            .GetBind("dprops", "db", "Properties")
            .Each("dprops", "dp", 64, b => b.Get("dp", "Name").Get("dp", "Type").Get("dp", "Value")).Release("dprops")
            .GetBind("tdfs", "db", "TableDefs")
            .Each("tdfs", "tdf", 64, t => t
                .Get("tdf", "Name").Get("tdf", "Attributes").Get("tdf", "RecordCount")
                .GetBind("tfl", "tdf", "Fields")
                .Each("tfl", "fd", 160, f => f.Get("fd", "Name").Get("fd", "Type").Get("fd", "Size").Get("fd", "Attributes")
                    .Get("fd", "OrdinalPosition").Get("fd", "Required").Get("fd", "CollatingOrder"))
                .Release("tfl")
                .GetBind("idxs", "tdf", "Indexes")
                .Each("idxs", "ix", 16, i => i.Get("ix", "Name").Get("ix", "Primary").Get("ix", "Unique").Get("ix", "Foreign")
                    .GetBind("ixf", "ix", "Fields").Each("ixf", "xf", 16, x => x.Get("xf", "Name").Get("xf", "Attributes")).Release("ixf"))
                .Release("idxs")
                .CallBind("trs", "tdf", "OpenRecordset", A.I4(Dao.OpenSnapshot))
                .ReadAll("trs", maxRows: 16, maxFields: 160)
                .Call("trs", "Close").Release("trs"))
            .Release("tdfs")
            .GetBind("qdfs", "db", "QueryDefs")
            .Each("qdfs", "qd", 16, q => q.Get("qd", "Name").Get("qd", "Type").Get("qd", "SQL"))
            .Release("qdfs")
            .Finish("db");
    }
}
