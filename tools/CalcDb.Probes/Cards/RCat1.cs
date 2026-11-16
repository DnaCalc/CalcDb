namespace CalcDb.Probes.Cards;

/// <summary>
/// R-CAT-1: native QueryDefs, catalog round trips and invalidation (A-02; II-06). Only the public ACE surface is
/// asked here, through DAO.DBEngine.120 on one STA plus one ADODB comparison: creation arguments and outcomes
/// (CAT-NEW), MSysObjects and the other system tables read through SQL as the default Admin (CAT-B), property
/// creation, edits and read-back (CAT-P), saved-query metadata, SQL text and system rows (CAT-Q), temporary and
/// held QueryDef transitions (CAT-V), and the public effect of schema changes on held and saved queries (CAT-I).
/// Created files are kept whole for the W-04 inspector; their bytes are not interpreted here.
/// </summary>
public sealed class RCat1 : ICard
{
    private const string Card = "r-cat-1";
    private const string Lang = Dao.LangGeneral;

    // DAO DataTypeEnum codes used as Property types (values from the DAO type library, also captured by R-DAO-1).
    private const int TBoolean = 1, TByte = 2, TInteger = 3, TLong = 4, TCurrency = 5, TSingle = 6, TDouble = 7, TDate = 8,
        TBinary = 9, TText = 10, TLongBinary = 11, TMemo = 12, TGuid = 15;

    // DAO TableDefAttributeEnum.dbHiddenObject and DatabaseTypeEnum.dbVersion30.
    private const int DbHiddenObject = 1, DbVersion30 = 32;

    private static string[] R(params string[] branches) => ["R-CAT-1", "II-06", .. branches];

    private static string Fx(string name) => $"{Card}-{name}";

    private static string File(string name, string ext) => $"{Card}-{name}.{ext}";

    /// <summary>Saved queries of the CAT-Q fixture: one per query shape, plus SQL-only and declaration-only variants.</summary>
    private static readonly (string Name, string Sql)[] SavedQueries =
    [
        ("QOne", "SELECT A FROM T;"),
        ("QOneOrdered", "SELECT A FROM T ORDER BY A;"),
        ("QAlias", "SELECT T.ID AS Key1, T.A AS [Label], B * 2 AS Twice FROM T;"),
        ("QParam", "PARAMETERS pId Long, pName Text ( 20 ); SELECT ID, A FROM T WHERE ID = pId OR A = pName;"),
        ("QParamTypes", "PARAMETERS pId Short, pName Text ( 10 ); SELECT ID, A FROM T WHERE ID = pId OR A = pName;"),
        ("QImplicit", "SELECT ID FROM T WHERE B > pMin;"),
        ("QOuter", "SELECT T.ID, U.V FROM T LEFT JOIN U ON T.ID = U.TID;"),
        ("QGroup", "SELECT A, Count(*) AS N, Sum(B) AS SB FROM T GROUP BY A HAVING Count(*) > 0;"),
        ("QOrder", "SELECT ID, A FROM T ORDER BY A DESC, ID ASC;"),
        ("QTop", "SELECT TOP 2 ID FROM T ORDER BY ID;"),
        ("QTopPercent", "SELECT TOP 50 PERCENT ID FROM T ORDER BY ID;"),
        ("QDistinct", "SELECT DISTINCT A FROM T;"),
        ("QDistinctRow", "SELECT DISTINCTROW T.A FROM T INNER JOIN U ON T.ID = U.TID;"),
        ("QCrosstab", "TRANSFORM Sum(B) AS SB SELECT A FROM T GROUP BY A PIVOT ID;"),
        ("QUnion3", "SELECT ID, A FROM T UNION SELECT ID, A FROM W UNION ALL SELECT ID, V FROM U;"),
        ("QAppend", "INSERT INTO W (ID, A) SELECT ID + 100, A FROM T;"),
        ("QUpdate", "UPDATE T SET B = B + 1 WHERE ID = 1;"),
        ("QDelete", "DELETE FROM W WHERE ID > 100;"),
        ("QMakeTable", "SELECT ID, A INTO TNew FROM T;"),
        ("QDdl", "CREATE TABLE Z (ID LONG);"),
        ("QText", "SELECT  ID ,A\r\nFROM T\r\nWHERE A = 'it''s' OR A = \"q\"\r\n"),
        ("QUnicode", "SELECT ID AS [Bä€] FROM T WHERE A <> 'Bä€ \U0001D11E';"),
        ("QLike89", "SELECT ID FROM T WHERE A LIKE 'a*';"),
        ("QLike92", "SELECT ID FROM T WHERE A LIKE 'a%';"),
        ("QBase", "SELECT ID, B FROM T;"),
        ("QNested", "SELECT B FROM QBase;"),
    ];

    /// <summary>Database-level properties of the CAT-P fixture: one per property type, plus length and naming cases.</summary>
    private static readonly (string Name, int Type, Arg Value)[] DbProps =
    [
        ("PBoolean", TBoolean, A.Bool(true)),
        ("PByte", TByte, A.UI1(200)),
        ("PInteger", TInteger, A.I2(-3)),
        ("PLong", TLong, A.I4(123456)),
        ("PCurrency", TCurrency, A.Cy(1.2345m)),
        ("PSingle", TSingle, A.R4(1.5f)),
        ("PDouble", TDouble, A.R8(0.1)),
        ("PDate", TDate, A.Date(45000.5)),
        ("PText", TText, A.Str("text")),
        ("PTextUnicode", TText, A.Str("Bä€ \U0001D11E")),
        ("PText255", TText, A.Str(new string('t', 255))),
        ("PText300", TText, A.Str(new string('u', 300))),
        ("PMemo", TMemo, A.Str(new string('p', 5000))),
        ("PMemoUnicode", TMemo, A.Str(string.Concat(Enumerable.Repeat("Bä€", 2000)))),
        ("PBinary16", TBinary, A.Bytes([.. Enumerable.Range(0, 16).Select(i => (byte)i)])),
        ("GUID", TBinary, A.Bytes([.. Enumerable.Range(0, 16).Select(i => (byte)(0xF0 - i))])),
        ("PGuidType", TGuid, A.Str("{00112233-4455-6677-8899-AABBCCDDEEFF}")),
        ("PLongBinary", TLongBinary, A.Bytes([.. Enumerable.Range(0, 3000).Select(i => (byte)(i * 7))])),
    ];

    public IEnumerable<Probe> Probes() =>
        Creations().Concat(Bootstrap()).Concat(QueryRows()).Concat(PropertyProbes()).Concat(Transitions()).Concat(Invalidation());

    // CAT-NEW and fixtures ---------------------------------------------------------------------------------

    private static IEnumerable<Probe> Creations()
    {
        yield return Creation("accdb-empty", ["CAT-NEW", "CAT-B"],
            "an empty ACCDB from DBEngine.CreateDatabase(path, dbLangGeneral, dbVersion120); MSysObjects (fields and all rows) is then read through SQL on the still-open new Database",
            Std("accdb-empty", "accdb", A.Str(Lang), A.I4(Dao.Version120)), p => SysRead(p, "new", "SELECT * FROM MSysObjects"));
        yield return Creation("mdb-empty", ["CAT-NEW", "CAT-B"],
            "an empty Jet 4 MDB from DBEngine.CreateDatabase(path, dbLangGeneral, dbVersion40); MSysObjects (fields and all rows) is then read through SQL on the still-open new Database",
            Std("mdb-empty", "mdb", A.Str(Lang), A.I4(Dao.Version40)), p => SysRead(p, "new", "SELECT * FROM MSysObjects"));
        yield return Creation("accdb-option-omitted", ["CAT-NEW"],
            "DBEngine.CreateDatabase(path, dbLangGeneral) with the Option argument omitted, to a path ending in .accdb; which Version the returned Database reports",
            Std("accdb-option-omitted", "accdb", A.Str(Lang)), _ => { });
        yield return Creation("accdb-ext-v40", ["CAT-NEW"],
            "DBEngine.CreateDatabase(path, dbLangGeneral, dbVersion40) to a path ending in .accdb (extension disagrees with the requested version); whether the call succeeds and which Version is reported",
            Std("accdb-ext-v40", "accdb", A.Str(Lang), A.I4(Dao.Version40)), _ => { });
        yield return Creation("mdb-version30", ["CAT-NEW"],
            "DBEngine.CreateDatabase(path, dbLangGeneral, dbVersion30 = 32), an older-format request, to a path ending in .mdb; the outcome of the call and, if it succeeds, the reported Version",
            Std("mdb-version30", "mdb", A.Str(Lang), A.I4(DbVersion30)), _ => { });
        yield return Creation("accdb-workspace-route", ["CAT-NEW"],
            "Workspaces(0).CreateDatabase(path, dbLangGeneral, dbVersion120), the Workspace route rather than DBEngine.CreateDatabase",
            p => p.GetBind("wss", "eng", "Workspaces").Item("ws", "wss", A.I4(0))
                .CallBind("db", "ws", "CreateDatabase", A.Out(File("accdb-workspace-route", "accdb")), A.Str(Lang), A.I4(Dao.Version120)),
            p => p.Release("ws").Release("wss"));

        string existing = File("accdb-existing-destination", "accdb");
        yield return new Probe(Card, "create-accdb-existing-destination", R("CAT-NEW"),
            "CAT-NEW failure: DBEngine.CreateDatabase(path, dbLangGeneral, dbVersion120) is called, the Database closed, and the identical call repeated on the now-existing path. " +
            "Asks for the second call's result and error channels, DBEngine.Errors, Workspaces(0).Databases.Count and the file state after each step.",
            p => p.Modules("before-create").Create().Modules("after-create")
                .CallBind("db", "eng", "CreateDatabase", A.Out(existing), A.Str(Lang), A.I4(Dao.Version120))
                .Call("db", "Close").Release("db").Files("after-first")
                .CallBind("db2", "eng", "CreateDatabase", A.Out(existing), A.Str(Lang), A.I4(Dao.Version120)).Errors()
                .GetBind("wss", "eng", "Workspaces").Item("ws", "wss", A.I4(0)).GetBind("dbs", "ws", "Databases").Get("dbs", "Count")
                .Release("dbs").Release("ws").Release("wss").Files("after-second")
                .Call("db2", "Close").Release("db2").Release("eng").Modules("after-close").Files("after-close"))
        { CreatesFixture = true };

        yield return new Probe(Card, "create-accdb-in-transaction", R("CAT-NEW"),
            "CAT-NEW transaction case: Workspaces(0).BeginTrans, then Workspace.CreateDatabase(path, dbLangGeneral, dbVersion120) and CREATE TABLE T in the new Database, then Workspace.Rollback. " +
            "Asks for the TableDefs (after Refresh), Workspace.Databases.Count and file state before and after the Rollback.",
            p => p.Modules("before-create").Create().Modules("after-create")
                .GetBind("wss", "eng", "Workspaces").Item("ws", "wss", A.I4(0)).Call("ws", "BeginTrans")
                .CallBind("db", "ws", "CreateDatabase", A.Out(File("accdb-in-transaction", "accdb")), A.Str(Lang), A.I4(Dao.Version120))
                .Call("db", "Execute", A.Str("CREATE TABLE T (ID LONG)")).Files("in-transaction")
                .Call("ws", "Rollback")
                .GetBind("tdfs", "db", "TableDefs").Call("tdfs", "Refresh").Get("tdfs", "Count")
                .Each("tdfs", "tdf", 64, b => b.Get("tdf", "Name")).Release("tdfs")
                .GetBind("dbs", "ws", "Databases").Get("dbs", "Count").Release("dbs").Files("after-rollback")
                .Call("db", "Close").Release("db").Release("ws").Release("wss").Release("eng").Modules("after-close").Files("after-close"))
        { CreatesFixture = true };

        yield return Creation("accdb-catalog", ["CAT-B"],
            "catalog fixture: tables T, P2 (two-column primary key) and U, a two-column relationship FK_U_P2 by ALTER TABLE ADD CONSTRAINT, table H marked dbHiddenObject through TableDef.Attributes, a few rows and saved query QSel",
            Std("accdb-catalog", "accdb", A.Str(Lang), A.I4(Dao.Version120)), CatalogBody);
        yield return Creation("mdb-catalog", ["CAT-B"],
            "the same catalog fixture as accdb-catalog in Jet 4 MDB",
            Std("mdb-catalog", "mdb", A.Str(Lang), A.I4(Dao.Version40)), CatalogBody);
        yield return Creation("accdb-queries", ["CAT-Q"],
            "query fixture: tables T, U, W with rows, one saved QueryDef per shape (select, aliases, explicit and implicit parameters, outer join, GROUP BY/HAVING, ORDER BY both directions, TOP, TOP PERCENT, DISTINCT, DISTINCTROW, crosstab, three-arm UNION, append, update, delete, make-table, DDL, CR/LF and quoting text, Unicode, LIKE patterns, nested), SQL-only and declaration-only variants, a pass-through QueryDef (Connect, ReturnsRecords) and one with only a Description property",
            Std("accdb-queries", "accdb", A.Str(Lang), A.I4(Dao.Version120)), QueriesBody);
        yield return Creation("accdb-properties", ["CAT-P"],
            "property fixture: one user-defined Database property per DAO type with length, Unicode, GUID-named and 16-byte binary cases; Description on table T; Description, Caption and CalcDbProp on field K and CalcDbProp on index K (same name as the column); a Memo-typed property on field M; Description on QueryDef QP",
            Std("accdb-properties", "accdb", A.Str(Lang), A.I4(Dao.Version120)), PropertiesBody);

        yield return new Probe(Card, "create-accdb-queries-compacted", R("CAT-Q"),
            "CAT-Q compaction: DBEngine.CompactDatabase(accdb-queries copy, new .accdb path). The compacted file is kept, so that probe msys-queries-compacted can read its query rows against the uncompacted copy's rows.",
            p => p.Modules("before-create").Create().Modules("after-create").Files("before-compact")
                .Call("eng", "CompactDatabase", A.In(), A.Out(File("accdb-queries-compacted", "accdb"))).Errors()
                .Release("eng").Modules("after-close").Files("after-close"))
        { CreatesFixture = true, Fixtures = [("main", Fx("accdb-queries"))] };
    }

    private static Action<P> Std(string name, string ext, params Arg[] options) =>
        p => p.CallBind("db", "eng", "CreateDatabase", [A.Out(File(name, ext)), .. options]);

    private static Probe Creation(string name, string[] branches, string what, Action<P> create, Action<P> body) =>
        new(Card, "create-" + name, R(branches),
            $"R-CAT-1 creation: {what}. Right after creation the returned Database's Version, CollatingOrder, Properties and TableDefs are read; the closed file is kept.",
            p =>
            {
                p.Modules("before-create").Create().Modules("after-create").Get("eng", "Version");
                create(p);
                CreatedView(p);
                body(p);
                p.Call("db", "Close").Release("db").Release("eng").Modules("after-close").Files("after-close");
            })
        { CreatesFixture = true };

    private static void CreatedView(P p) =>
        p.Get("db", "Version").Get("db", "CollatingOrder")
            .GetBind("cprops", "db", "Properties").Each("cprops", "cp", 64, b => b.Get("cp", "Name").Get("cp", "Type").Get("cp", "Value")).Release("cprops")
            .GetBind("ctdfs", "db", "TableDefs").Get("ctdfs", "Count")
            .Each("ctdfs", "ctd", 64, b => b.Get("ctd", "Name").Get("ctd", "Attributes")).Release("ctdfs");

    private static void CatalogBody(P p)
    {
        Exec(p, "CREATE TABLE T (ID LONG CONSTRAINT PK_T PRIMARY KEY, A TEXT(20), B LONG)");
        Exec(p, "CREATE TABLE P2 (K1 LONG, K2 LONG, CONSTRAINT PK_P2 PRIMARY KEY (K1, K2))");
        Exec(p, "CREATE TABLE U (ID LONG CONSTRAINT PK_U PRIMARY KEY, K1 LONG, K2 LONG)");
        Exec(p, "ALTER TABLE U ADD CONSTRAINT FK_U_P2 FOREIGN KEY (K1, K2) REFERENCES P2 (K1, K2)");
        Exec(p, "CREATE TABLE H (ID LONG)");
        Exec(p, "INSERT INTO T VALUES (1, 'a', 10)");
        Exec(p, "INSERT INTO T VALUES (2, 'b', NULL)");
        Exec(p, "INSERT INTO P2 VALUES (1, 1)");
        Exec(p, "INSERT INTO U VALUES (1, 1, 1)");
        p.CallBind("qsel", "db", "CreateQueryDef", A.Str("QSel"), A.Str("SELECT ID, A FROM T;")).Release("qsel")
            .GetBind("tdfs", "db", "TableDefs").Item("th", "tdfs", A.Str("H"))
            .Put("th", "Attributes", A.I4(DbHiddenObject)).Get("th", "Attributes").Release("th").Release("tdfs");
    }

    private static void QueriesBody(P p)
    {
        Exec(p, "CREATE TABLE T (ID LONG CONSTRAINT PK_T PRIMARY KEY, A TEXT(20), B LONG, C DOUBLE)");
        Exec(p, "CREATE TABLE U (ID LONG CONSTRAINT PK_U PRIMARY KEY, TID LONG, V TEXT(20))");
        Exec(p, "CREATE TABLE W (ID LONG CONSTRAINT PK_W PRIMARY KEY, A TEXT(20))");
        Exec(p, "INSERT INTO T VALUES (1, 'a', 10, 1.5)");
        Exec(p, "INSERT INTO T VALUES (2, 'ab', 20, NULL)");
        Exec(p, "INSERT INTO T VALUES (3, 'it''s', NULL, 3)");
        Exec(p, "INSERT INTO T VALUES (4, 'a%x', 40, 4)");
        Exec(p, "INSERT INTO U VALUES (1, 1, 'u1')");
        Exec(p, "INSERT INTO U VALUES (2, 9, 'u9')");
        Exec(p, "INSERT INTO W VALUES (1, 'w1')");
        for (int i = 0; i < SavedQueries.Length; i++)
        {
            p.CallBind($"q{i}", "db", "CreateQueryDef", A.Str(SavedQueries[i].Name), A.Str(SavedQueries[i].Sql)).Release($"q{i}");
        }

        // Pass-through: an unnamed QueryDef given Name, Connect, SQL and ReturnsRecords, then appended.
        p.CallBind("qpass", "db", "CreateQueryDef").Put("qpass", "Name", A.Str("QPass"))
            .Put("qpass", "Connect", A.Str("ODBC;DSN=CalcDbProbeNoSuchDsn;")).Put("qpass", "SQL", A.Str("SELECT 1 AS One"))
            .Put("qpass", "ReturnsRecords", A.Bool(true))
            .GetBind("qdfs", "db", "QueryDefs").Call("qdfs", "Append", A.Obj("qpass")).Get("qpass", "Type").Release("qpass").Release("qdfs");

        // Persistent-property-only variant of QOne's text.
        p.CallBind("qprop", "db", "CreateQueryDef", A.Str("QProp"), A.Str("SELECT A FROM T;"));
        AppendProp(p, "qprop", "qpd", A.Str("Description"), A.I4(TText), A.Str("query description"));
        p.Release("qprop");
    }

    private static void PropertiesBody(P p)
    {
        Exec(p, "CREATE TABLE T (ID LONG CONSTRAINT PK_T PRIMARY KEY, K TEXT(10), M MEMO)");
        Exec(p, "CREATE INDEX K ON T (K)");
        Exec(p, "INSERT INTO T VALUES (1, 'k', 'memo')");
        p.CallBind("qp", "db", "CreateQueryDef", A.Str("QP"), A.Str("SELECT ID, K FROM T;"));
        for (int i = 0; i < DbProps.Length; i++)
        {
            AppendProp(p, "db", $"dp{i}", A.Str(DbProps[i].Name), A.I4(DbProps[i].Type), DbProps[i].Value);
        }
        p.GetBind("tdfs", "db", "TableDefs").Item("tdf", "tdfs", A.Str("T"));
        AppendProp(p, "tdf", "tp", A.Str("Description"), A.I4(TText), A.Str("table description"));
        p.GetBind("fl", "tdf", "Fields").Item("fk", "fl", A.Str("K")).Item("fm", "fl", A.Str("M"));
        AppendProp(p, "fk", "fkd", A.Str("Description"), A.I4(TText), A.Str("column description"));
        AppendProp(p, "fk", "fkc", A.Str("Caption"), A.I4(TText), A.Str("Column K"));
        AppendProp(p, "fk", "fkp", A.Str("CalcDbProp"), A.I4(TLong), A.I4(1));
        AppendProp(p, "fm", "fmp", A.Str("CalcDbMemo"), A.I4(TMemo), A.Str(new string('d', 3000)));
        p.GetBind("ixs", "tdf", "Indexes").Item("ixk", "ixs", A.Str("K"));
        AppendProp(p, "ixk", "ixp", A.Str("CalcDbProp"), A.I4(TLong), A.I4(2));
        AppendProp(p, "qp", "qpd", A.Str("Description"), A.I4(TText), A.Str("query description"));
        p.Release("ixk").Release("ixs").Release("fm").Release("fk").Release("fl").Release("tdf").Release("tdfs").Release("qp");
    }

    // CAT-B ------------------------------------------------------------------------------------------------

    private static readonly string[] SystemTables =
        ["MSysACEs", "MSysQueries", "MSysRelationships", "MSysAccessStorage", "MSysComplexColumns", "MSysResources", "MSysNavPaneGroups", "MSysAccessObjects"];

    private static IEnumerable<Probe> Bootstrap()
    {
        foreach (string ext in new[] { "accdb", "mdb" })
        {
            yield return Use($"catalog-{ext}", ["CAT-B"], $"{ext}-catalog",
                $"Open the {ext.ToUpperInvariant()} catalog fixture read-only as the default Admin. Read MSysObjects through SELECT * (field metadata and every row), every TableDef's Name and Attributes, " +
                "the MSysObjects TableDef's Fields (Name, Type, Size, Attributes, OrdinalPosition) and Indexes (Name, Primary, Unique, Clustered, IgnoreNulls, index fields), and then try SELECT * on each of " +
                string.Join(", ", SystemTables) + " (each open's outcome, field metadata and rows).",
                CatalogRead);
        }

        yield return Use("msys-add-objects", ["CAT-B"], "accdb-empty",
            "On a writable copy of the empty ACCDB, read MSysObjects (SELECT *) three times: before any change, after CREATE TABLE T, and after creating P2 and U with a two-column FOREIGN KEY constraint between them; MSysRelationships is read after the last step. Asks which MSysObjects rows each object adds and with which field values.",
            p =>
            {
                p.Engine().OpenDb(readOnly: false);
                SysRead(p, "r0", "SELECT * FROM MSysObjects");
                Exec(p, "CREATE TABLE T (ID LONG CONSTRAINT PK_T PRIMARY KEY, A TEXT(20))");
                SysRead(p, "r1", "SELECT * FROM MSysObjects");
                Exec(p, "CREATE TABLE P2 (K1 LONG, K2 LONG, CONSTRAINT PK_P2 PRIMARY KEY (K1, K2))");
                Exec(p, "CREATE TABLE U (ID LONG CONSTRAINT PK_U PRIMARY KEY, K1 LONG, K2 LONG, CONSTRAINT FK_U_P2 FOREIGN KEY (K1, K2) REFERENCES P2 (K1, K2))");
                SysRead(p, "r2", "SELECT * FROM MSysObjects");
                SysRead(p, "rel", "SELECT * FROM MSysRelationships");
                p.Finish("db");
            });

        string[] names =
        [
            "Tbl", "TBL", "Tä", "T\u00E9", "Te\u0301", "T\u00C9", "Trail ", " Lead", "a.b", "a!b", "[br]", "a`b", "a'b",
            new string('x', 64), new string('y', 65),
        ];
        string[] lookups = ["tbl", "TÄ", "Te\u0301", "t\u00C9", "Trail", "TRAIL ", "Lead"];
        yield return Use("names", ["CAT-B"], "accdb-empty",
            "Name rules on a writable copy of the empty ACCDB. For each of a fixed list of table names (ASCII case pair Tbl/TBL, accented, precomposed and decomposed e-acute, upper-case E-acute, trailing and leading space, '.', '!', brackets, backquote, apostrophe, 64 and 65 characters) " +
            "CreateTableDef(name) + one Long field + TableDefs.Append is called and the appended TableDef's Name read back. Then TableDefs (after Refresh) is enumerated, a fixed list of differently spelled names is looked up through TableDefs.Item, " +
            "a column-name case pair (a, A) and an index-name case pair (ix1, IX1) are tried through DDL, QueryDefs named Tbl (same as a table), qcase and QCASE are created, and MSysObjects is read. Each call's outcome and returned spelling are recorded.",
            p =>
            {
                p.Engine().OpenDb(readOnly: false).GetBind("tdfs", "db", "TableDefs");
                for (int i = 0; i < names.Length; i++)
                {
                    p.CallBind($"nt{i}", "db", "CreateTableDef", A.Str(names[i]))
                        .CallBind($"nf{i}", $"nt{i}", "CreateField", A.Str("ID"), A.I4(TLong))
                        .GetBind($"nfl{i}", $"nt{i}", "Fields").Call($"nfl{i}", "Append", A.Obj($"nf{i}"))
                        .Call("tdfs", "Append", A.Obj($"nt{i}")).Get($"nt{i}", "Name")
                        .Release($"nf{i}").Release($"nfl{i}").Release($"nt{i}");
                }
                p.Call("tdfs", "Refresh").Get("tdfs", "Count").Each("tdfs", "tn", 64, b => b.Get("tn", "Name"));
                for (int i = 0; i < lookups.Length; i++)
                {
                    p.Item($"lk{i}", "tdfs", A.Str(lookups[i])).Get($"lk{i}", "Name").Release($"lk{i}");
                }
                p.Release("tdfs");
                Exec(p, "CREATE TABLE C2 (a LONG, A LONG)");
                Exec(p, "CREATE TABLE C3 (ID LONG, Nm TEXT(5))");
                Exec(p, "CREATE INDEX ix1 ON C3 (ID)");
                Exec(p, "CREATE INDEX IX1 ON C3 (Nm)");
                p.CallBind("qt", "db", "CreateQueryDef", A.Str("Tbl"), A.Str("SELECT ID FROM Tbl;")).Get("qt", "Name").Release("qt")
                    .CallBind("qc1", "db", "CreateQueryDef", A.Str("qcase"), A.Str("SELECT ID FROM Tbl;")).Get("qc1", "Name").Release("qc1")
                    .CallBind("qc2", "db", "CreateQueryDef", A.Str("QCASE"), A.Str("SELECT ID FROM Tbl;")).Get("qc2", "Name").Release("qc2");
                SysRead(p, "names", "SELECT * FROM MSysObjects");
                p.Finish("db");
            });

        yield return Use("id-reuse", ["CAT-B"], "accdb-empty",
            "Object ID consumption on a writable copy of the empty ACCDB: MSysObjects (SELECT *) is read before any change, after CREATE TABLE X, after DROP TABLE X and CREATE TABLE X again, " +
            "after a failing CREATE TABLE Z (duplicate column) followed by CREATE TABLE Y, and after a CREATE TABLE R inside Workspaces(0).BeginTrans/Rollback followed by CREATE TABLE S and a saved QueryDef QX.",
            p =>
            {
                p.Engine().OpenDb(readOnly: false).GetBind("wss", "eng", "Workspaces").Item("ws", "wss", A.I4(0));
                SysRead(p, "i0", "SELECT * FROM MSysObjects");
                Exec(p, "CREATE TABLE X (ID LONG)");
                SysRead(p, "i1", "SELECT * FROM MSysObjects");
                Exec(p, "DROP TABLE X");
                Exec(p, "CREATE TABLE X (ID LONG)");
                SysRead(p, "i2", "SELECT * FROM MSysObjects");
                Exec(p, "CREATE TABLE Z (ID LONG, ID LONG)");
                p.Errors();
                Exec(p, "CREATE TABLE Y (ID LONG)");
                SysRead(p, "i3", "SELECT * FROM MSysObjects");
                p.Call("ws", "BeginTrans");
                Exec(p, "CREATE TABLE R (ID LONG)");
                p.Call("ws", "Rollback");
                Exec(p, "CREATE TABLE S (ID LONG)");
                p.CallBind("qx", "db", "CreateQueryDef", A.Str("QX"), A.Str("SELECT ID FROM S;")).Release("qx");
                SysRead(p, "i4", "SELECT * FROM MSysObjects");
                p.Release("ws").Release("wss").Finish("db");
            });
    }

    private static void CatalogRead(P p)
    {
        p.Engine().OpenDb().Get("db", "Version").Files("after-open");
        SysRead(p, "mso", "SELECT * FROM MSysObjects");
        p.GetBind("tdfs", "db", "TableDefs").Each("tdfs", "tdf", 64, t => t.Get("tdf", "Name").Get("tdf", "Attributes"))
            .Item("mso", "tdfs", A.Str("MSysObjects"))
            .GetBind("msf", "mso", "Fields")
            .Each("msf", "fd", 64, f => f.Get("fd", "Name").Get("fd", "Type").Get("fd", "Size").Get("fd", "Attributes").Get("fd", "OrdinalPosition"))
            .Release("msf")
            .GetBind("msi", "mso", "Indexes")
            .Each("msi", "ix", 16, i => i.Get("ix", "Name").Get("ix", "Primary").Get("ix", "Unique").Get("ix", "Clustered").Get("ix", "IgnoreNulls")
                .GetBind("ixf", "ix", "Fields").Each("ixf", "xf", 16, x => x.Get("xf", "Name").Get("xf", "Attributes")).Release("ixf"))
            .Release("msi").Release("mso").Release("tdfs");
        foreach (string t in SystemTables)
        {
            SysRead(p, t, $"SELECT * FROM [{t}]");
        }
        p.Finish("db");
    }

    // CAT-Q ------------------------------------------------------------------------------------------------

    private static IEnumerable<Probe> QueryRows()
    {
        yield return Use("querydefs", ["CAT-Q"], "accdb-queries",
            "Open the query fixture read-only and enumerate QueryDefs: for each, Name, Type, SQL (exact BSTR), Connect, ReturnsRecords, Updatable, Parameters (Name, Type), Fields (Name, Type, Size, SourceTable, SourceField) and Properties (Name, Type, Value, Inherited).",
            p => { p.Engine().OpenDb(); QueryMeta(p); p.Finish("db"); });

        yield return Use("msys-queries", ["CAT-Q"], "accdb-queries",
            "Open the query fixture read-only and read MSysObjects and MSysQueries through SELECT * (field metadata and every row in returned order), and MSysQueries again through a table-type recordset with no index set (traversal order).",
            QueryRowsRead);

        yield return Use("msys-queries-compacted", ["CAT-Q"], "accdb-queries-compacted",
            "The same reads as msys-queries (MSysObjects and MSysQueries through SELECT *, MSysQueries through a table-type recordset) on the CompactDatabase output of the query fixture, for comparison with the uncompacted rows.",
            QueryRowsRead);

        string[] roundTrip = ["QText", "QUnicode", "QParam", "QOuter", "QCrosstab"];
        yield return Use("sql-round-trip", ["CAT-Q"], "accdb-queries",
            "SQL getter/setter round trip on a writable copy of the query fixture. For " + string.Join(", ", roundTrip) +
            " the SQL is read, set to the exact text the fixture was created with, and read again. QNul (text ending in U+0000) and QNoSemi (no trailing semicolon) are created and their SQL read. " +
            "The Database is closed and reopened and every one of these SQL values is read again.",
            p =>
            {
                p.Engine().OpenDb(readOnly: false).GetBind("qdfs", "db", "QueryDefs");
                foreach (string name in roundTrip)
                {
                    string sql = SavedQueries.Single(q => q.Name == name).Sql;
                    p.Item("rq", "qdfs", A.Str(name)).Get("rq", "SQL").Put("rq", "SQL", A.Str(sql)).Get("rq", "SQL").Release("rq");
                }
                p.CallBind("qnul", "db", "CreateQueryDef", A.Str("QNul"), A.Str("SELECT ID FROM T;\0")).Get("qnul", "SQL").Release("qnul")
                    .CallBind("qns", "db", "CreateQueryDef", A.Str("QNoSemi"), A.Str("SELECT ID FROM T")).Get("qns", "SQL").Release("qns")
                    .Release("qdfs").Call("db", "Close").Release("db")
                    .OpenDb("db2").GetBind("qdfs2", "db2", "QueryDefs");
                foreach (string name in roundTrip.Concat(["QNul", "QNoSemi"]))
                {
                    p.Item("rq2", "qdfs2", A.Str(name)).Get("rq2", "SQL").Release("rq2");
                }
                p.Release("qdfs2").Finish("db2");
            });

        const string orderRows = "SELECT * FROM MSysQueries WHERE ObjectId IN (SELECT Id FROM MSysObjects WHERE Name IN ('QOrdA', 'QOrdB'))";
        yield return Use("order-variants", ["CAT-Q"], "accdb-queries",
            "Ordering on a writable copy of the query fixture: QOrdA and QOrdB are created with the same expressions but reversed parameter, projected-field and sort-term order. " +
            "Asks for each QueryDef's Parameters and Fields (Name, Type) and their MSysQueries rows; then QOrdA is deleted and recreated with identical SQL and its rows are read again.",
            p =>
            {
                const string a = "PARAMETERS p1 Long, p2 Text ( 10 ); SELECT A, B FROM T WHERE B = p1 AND A = p2 ORDER BY A, B;";
                const string b = "PARAMETERS p2 Text ( 10 ), p1 Long; SELECT B, A FROM T WHERE B = p1 AND A = p2 ORDER BY B, A;";
                p.Engine().OpenDb(readOnly: false)
                    .CallBind("qa", "db", "CreateQueryDef", A.Str("QOrdA"), A.Str(a))
                    .CallBind("qb", "db", "CreateQueryDef", A.Str("QOrdB"), A.Str(b));
                ParamNames(p, "qa", "qap");
                FieldNames(p, "qa", "qaf");
                ParamNames(p, "qb", "qbp");
                FieldNames(p, "qb", "qbf");
                SysRead(p, "o1", orderRows);
                p.Release("qa").GetBind("qdfs", "db", "QueryDefs").Call("qdfs", "Delete", A.Str("QOrdA")).Release("qdfs")
                    .CallBind("qa2", "db", "CreateQueryDef", A.Str("QOrdA"), A.Str(a));
                ParamNames(p, "qa2", "qa2p");
                SysRead(p, "o2", orderRows);
                p.Release("qa2").Release("qb").Finish("db");
            });
    }

    private static void QueryRowsRead(P p)
    {
        p.Engine().OpenDb();
        SysRead(p, "obj", "SELECT * FROM MSysObjects");
        SysRead(p, "qry", "SELECT * FROM MSysQueries", maxRows: 512);
        SysRead(p, "qrytab", "MSysQueries", maxRows: 512, type: Dao.OpenTable);
        p.Finish("db");
    }

    // CAT-P ------------------------------------------------------------------------------------------------

    private static IEnumerable<Probe> PropertyProbes()
    {
        yield return Use("properties", ["CAT-P"], "accdb-properties",
            "Open the property fixture read-only and enumerate Properties (Name, Type, Value, Inherited) of the Database, of TableDef T, of each of its Fields, of each of its Indexes, and of QueryDef QP. This includes built-in properties with no stored record.",
            p =>
            {
                p.Engine().OpenDb();
                PropList(p, "db", "dbp");
                p.GetBind("tdfs", "db", "TableDefs").Item("tdf", "tdfs", A.Str("T"));
                PropList(p, "tdf", "tdp");
                p.GetBind("fl", "tdf", "Fields").Each("fl", "fd", 16, b => { b.Get("fd", "Name"); PropList(b, "fd", "fdp"); }).Release("fl")
                    .GetBind("ixs", "tdf", "Indexes").Each("ixs", "ix", 16, b => { b.Get("ix", "Name"); PropList(b, "ix", "ixp"); }).Release("ixs")
                    .GetBind("qdfs", "db", "QueryDefs").Item("qp", "qdfs", A.Str("QP"));
                PropList(p, "qp", "qpp");
                p.Release("qp").Release("qdfs").Release("tdf").Release("tdfs").Finish("db");
            });

        (string Name, int Type, Arg? Value)[] variants =
        [
            ("VNullText", TText, A.Null), ("VEmptyText", TText, A.Empty), ("VEmptyBstr", TText, A.Str("")),
            ("VEmptyBinary", TLongBinary, A.Bytes()), ("VZeroLong", TLong, A.I4(0)), ("VFalse", TBoolean, A.Bool(false)),
            ("VBoolOneI2", TBoolean, A.I2(1)), ("VBoolMinusTwo", TBoolean, A.I4(-2)), ("VBoolText", TBoolean, A.Str("yes")),
            ("VLongFromText", TLong, A.Str("abc")), ("VTextFromLong", TText, A.I4(5)), ("VMissingValue", TText, A.Missing),
            ("VOmittedValue", TText, null), ("", TText, A.Str("empty name")), ("VZeroLong", TLong, A.I4(1)), ("VBadType", 99, A.Str("x")),
            (new string('n', 64), TText, A.Str("64")), (new string('m', 65), TText, A.Str("65")),
        ];
        yield return Use("property-inputs", ["CAT-P"], "accdb-empty",
            "Property inputs on a writable copy of the empty ACCDB. Database.CreateProperty + Properties.Append is called with Null, Empty, empty BSTR, empty byte array, zero, False, noncanonical Boolean inputs (I2 1, I4 -2, text), " +
            "cross-type values, a Missing and an omitted Value, an empty name, a duplicate name, an undefined type code 99, 64- and 65-character names, and once with only a name. " +
            "Properties are then enumerated (Name, Type, Value), and enumerated again after Close and reopen.",
            p =>
            {
                p.Engine().OpenDb(readOnly: false)
                    .CallBind("vonly", "db", "CreateProperty", A.Str("VNameOnly"))
                    .GetBind("vonlyc", "db", "Properties").Call("vonlyc", "Append", A.Obj("vonly")).Release("vonly").Release("vonlyc");
                for (int i = 0; i < variants.Length; i++)
                {
                    (string name, int type, Arg? value) = variants[i];
                    if (value is null)
                    {
                        AppendProp(p, "db", $"v{i}", A.Str(name), A.I4(type));
                    }
                    else
                    {
                        AppendProp(p, "db", $"v{i}", A.Str(name), A.I4(type), value);
                    }
                }
                PropList(p, "db", "vp1");
                p.Call("db", "Close").Release("db").OpenDb("db2");
                PropList(p, "db2", "vp2");
                p.Finish("db2");
            });

        yield return Use("property-edits", ["CAT-P"], "accdb-properties",
            "Property edits on a writable copy of the property fixture, with the Property object held throughout. PText.Value is read and set in turn to its same value, a same-length value, a longer value, a shorter value and Null (each read back), and PText.Type is set; PMemo is shrunk; " +
            "then PText is deleted (held object's Name/Value read, Count before and after Refresh), deletion of the built-in Version property is attempted, and inside Workspaces(0).BeginTrans/Rollback PLong.Value is changed and PInTransaction appended. " +
            "After Rollback PLong and PInTransaction are read; after Close and reopen all Database properties are enumerated.",
            p =>
            {
                p.Engine().OpenDb(readOnly: false).GetBind("wss", "eng", "Workspaces").Item("ws", "wss", A.I4(0))
                    .GetBind("dprops", "db", "Properties").Item("pt", "dprops", A.Str("PText")).Get("pt", "Value")
                    .Put("pt", "Value", A.Str("text")).Get("pt", "Value")
                    .Put("pt", "Value", A.Str("TEXT")).Get("pt", "Value")
                    .Put("pt", "Value", A.Str("a longer text value than before")).Get("pt", "Value")
                    .Put("pt", "Value", A.Str("s")).Get("pt", "Value")
                    .Put("pt", "Value", A.Null).Get("pt", "Value")
                    .Put("pt", "Type", A.I4(TLong)).Get("pt", "Type").Get("pt", "Value")
                    .Item("pm", "dprops", A.Str("PMemo")).Put("pm", "Value", A.Str("short")).Get("pm", "Value")
                    .Call("dprops", "Delete", A.Str("PText")).Get("pt", "Name").Get("pt", "Value")
                    .Get("dprops", "Count").Call("dprops", "Refresh").Get("dprops", "Count")
                    .Call("dprops", "Delete", A.Str("Version")).Errors()
                    .Item("pl", "dprops", A.Str("PLong"))
                    .Call("ws", "BeginTrans").Put("pl", "Value", A.I4(999));
                AppendProp(p, "db", "ptx", A.Str("PInTransaction"), A.I4(TText), A.Str("in transaction"));
                p.Call("ws", "Rollback").Get("pl", "Value").Item("ptx2", "dprops", A.Str("PInTransaction")).Get("ptx2", "Value")
                    .Release("ptx2").Release("pl").Release("pm").Release("pt").Release("dprops").Release("ws").Release("wss")
                    .Call("db", "Close").Release("db").OpenDb("db2");
                PropList(p, "db2", "ep");
                p.Finish("db2");
            });

        yield return Use("field-rules", ["CAT-P"], "accdb-catalog",
            "Rules against existing rows on a writable copy of the catalog fixture (T has a row with B Null). On field T.B, Required is set True, ValidationRule set to a syntax error '[B] >' and then '[B] > 100', ValidationText set, " +
            "DefaultValue set to '1 +' and then 'NoSuchFunction()' (each read back), and the table ValidationRule set to '[B] <> [ID]'. Then three INSERTs with dbFailOnError (B omitted, B = 5, B = 500) and DBEngine.Errors after each. " +
            "After Close and reopen, B's Required, ValidationRule and DefaultValue and T's rows are read.",
            p =>
            {
                p.Engine().OpenDb(readOnly: false)
                    .GetBind("tdfs", "db", "TableDefs").Item("tdf", "tdfs", A.Str("T")).GetBind("fl", "tdf", "Fields").Item("fb", "fl", A.Str("B"))
                    .Put("fb", "Required", A.Bool(true)).Get("fb", "Required").Errors()
                    .Put("fb", "ValidationRule", A.Str("[B] >")).Get("fb", "ValidationRule").Errors()
                    .Put("fb", "ValidationRule", A.Str("[B] > 100")).Get("fb", "ValidationRule").Errors()
                    .Put("fb", "ValidationText", A.Str("B too small")).Get("fb", "ValidationText")
                    .Put("fb", "DefaultValue", A.Str("1 +")).Get("fb", "DefaultValue").Errors()
                    .Put("fb", "DefaultValue", A.Str("NoSuchFunction()")).Get("fb", "DefaultValue").Errors()
                    .Put("tdf", "ValidationRule", A.Str("[B] <> [ID]")).Get("tdf", "ValidationRule").Errors()
                    .Release("fb").Release("fl").Release("tdf").Release("tdfs");
                foreach (string sql in new[] { "INSERT INTO T (ID, A) VALUES (9, 'z')", "INSERT INTO T (ID, A, B) VALUES (10, 'y', 5)", "INSERT INTO T (ID, A, B) VALUES (11, 'x', 500)" })
                {
                    p.Call("db", "Execute", A.Str(sql), A.I4(Dao.FailOnError)).Errors();
                }
                p.Call("db", "Close").Release("db").OpenDb("db2")
                    .GetBind("tdfs2", "db2", "TableDefs").Item("tdf2", "tdfs2", A.Str("T")).GetBind("fl2", "tdf2", "Fields").Item("fb2", "fl2", A.Str("B"))
                    .Get("fb2", "Required").Get("fb2", "ValidationRule").Get("fb2", "DefaultValue").Get("tdf2", "ValidationRule")
                    .Release("fb2").Release("fl2").Release("tdf2").Release("tdfs2");
                SysRead(p, "t", "SELECT * FROM T", db: "db2");
                p.Finish("db2");
            });
    }

    // CAT-V ------------------------------------------------------------------------------------------------

    private static IEnumerable<Probe> Transitions()
    {
        yield return Use("temporary-querydef", ["CAT-V"], "accdb-queries",
            "Temporary QueryDefs on a writable copy of the query fixture. CreateQueryDef(\"\", SQL): Name, Type, SQL, rows through its OpenRecordset, QueryDefs.Count before and after, and an attempted QueryDefs.Append of it. " +
            "CreateQueryDef() with no arguments: Name, then SQL and Name set to QFromTemp, appended, Count, looked up. A named CreateQueryDef(QNamedNow) as control. After Close and reopen the QueryDef names are enumerated.",
            p =>
            {
                p.Engine().OpenDb(readOnly: false).GetBind("qdfs", "db", "QueryDefs").Get("qdfs", "Count")
                    .CallBind("tq", "db", "CreateQueryDef", A.Str(""), A.Str("SELECT ID FROM T ORDER BY ID;"))
                    .Get("tq", "Name").Get("tq", "Type").Get("tq", "SQL")
                    .CallBind("trs", "tq", "OpenRecordset", A.I4(Dao.OpenSnapshot)).ReadAll("trs", 16).Call("trs", "Close").Release("trs")
                    .Get("qdfs", "Count").Call("qdfs", "Append", A.Obj("tq")).Get("qdfs", "Count")
                    .CallBind("tq2", "db", "CreateQueryDef").Get("tq2", "Name")
                    .Put("tq2", "SQL", A.Str("SELECT A FROM T;")).Put("tq2", "Name", A.Str("QFromTemp"))
                    .Call("qdfs", "Append", A.Obj("tq2")).Get("qdfs", "Count")
                    .Item("lk", "qdfs", A.Str("QFromTemp")).Get("lk", "SQL").Release("lk")
                    .CallBind("tq3", "db", "CreateQueryDef", A.Str("QNamedNow"), A.Str("SELECT ID FROM U;")).Get("qdfs", "Count")
                    .Release("tq").Release("tq2").Release("tq3").Release("qdfs")
                    .Call("db", "Close").Release("db").OpenDb("db2")
                    .GetBind("qdfs2", "db2", "QueryDefs").Get("qdfs2", "Count").Each("qdfs2", "qn", 48, b => b.Get("qn", "Name")).Release("qdfs2")
                    .Finish("db2");
            });

        yield return Use("held-sql-replace", ["CAT-V"], "accdb-queries",
            "Held Parameters across an SQL replacement on a writable copy of the query fixture. QParam's Parameters collection and Parameters(0) are held and Parameters(0).Value set to 1; " +
            "QParam.SQL is then replaced with one that declares different parameter types and order (pB Long, pId Text). Asks for the held Parameter's Name/Type/Value, the held collection's Count, a fresh Parameters enumeration, " +
            "a fresh QueryDefs lookup's SQL and Parameters, and after Close and reopen the stored SQL and Parameters.",
            p =>
            {
                p.Engine().OpenDb(readOnly: false).GetBind("qdfs", "db", "QueryDefs").Item("qd", "qdfs", A.Str("QParam"))
                    .GetBind("prms", "qd", "Parameters").Item("p0", "prms", A.I4(0)).Get("p0", "Name").Get("p0", "Type")
                    .Put("p0", "Value", A.I4(1)).Get("p0", "Value")
                    .Put("qd", "SQL", A.Str("PARAMETERS pB Long, pId Text ( 5 ); SELECT ID FROM T WHERE B = pB OR CStr(ID) = pId;"))
                    .Get("p0", "Name").Get("p0", "Type").Get("p0", "Value").Get("prms", "Count")
                    .GetBind("prms2", "qd", "Parameters").Each("prms2", "pp", 8, b => b.Get("pp", "Name").Get("pp", "Type").Get("pp", "Value")).Release("prms2")
                    .Item("qf", "qdfs", A.Str("QParam")).Get("qf", "SQL");
                ParamNames(p, "qf", "qfp");
                p.Release("qf").Release("p0").Release("prms").Release("qd").Release("qdfs")
                    .Call("db", "Close").Release("db").OpenDb("db2")
                    .GetBind("qdfs2", "db2", "QueryDefs").Item("qr", "qdfs2", A.Str("QParam")).Get("qr", "SQL");
                ParamNames(p, "qr", "qrp");
                p.Release("qr").Release("qdfs2").Finish("db2");
            });

        yield return Use("parameter-values", ["CAT-V"], "accdb-queries",
            "Parameter values and Requery on the query fixture (read-only). QParam pId = 1, pName = 'zzz', dynaset opened and read; pId = 2 then Requery() and read; Requery(QueryDef) and read; pId = 3 then Requery(QueryDef) and read. " +
            "Two separate QueryDefs.Item(\"QParam\") instances get pId 1 and 4: each instance's pId is read and both are opened and read. A third instance is opened with no values set, QImplicit is opened by name, and a temporary parameter query is opened before and after setting its value; DBEngine.Errors after each failure.",
            p =>
            {
                p.Engine().OpenDb().GetBind("qdfs", "db", "QueryDefs").Item("qd", "qdfs", A.Str("QParam"))
                    .GetBind("prms", "qd", "Parameters").Item("pid", "prms", A.Str("pId")).Item("pn", "prms", A.Str("pName"))
                    .Put("pid", "Value", A.I4(1)).Put("pn", "Value", A.Str("zzz"))
                    .CallBind("rs", "qd", "OpenRecordset", A.I4(Dao.OpenDynaset)).ReadAll("rs", 16)
                    .Put("pid", "Value", A.I4(2)).Call("rs", "Requery").ReadAll("rs", 16)
                    .Call("rs", "Requery", A.Obj("qd")).ReadAll("rs", 16)
                    .Put("pid", "Value", A.I4(3)).Call("rs", "Requery", A.Obj("qd")).ReadAll("rs", 16).Call("rs", "Close").Release("rs")
                    .Item("qa", "qdfs", A.Str("QParam")).Item("qb", "qdfs", A.Str("QParam"))
                    .GetBind("pa", "qa", "Parameters").Item("paid", "pa", A.Str("pId")).Put("paid", "Value", A.I4(1))
                    .GetBind("pb", "qb", "Parameters").Item("pbid", "pb", A.Str("pId")).Put("pbid", "Value", A.I4(4))
                    .Get("paid", "Value").Get("pbid", "Value")
                    .CallBind("rsa", "qa", "OpenRecordset", A.I4(Dao.OpenSnapshot)).ReadAll("rsa", 16).Call("rsa", "Close").Release("rsa")
                    .CallBind("rsb", "qb", "OpenRecordset", A.I4(Dao.OpenSnapshot)).ReadAll("rsb", 16).Call("rsb", "Close").Release("rsb")
                    .Item("qc", "qdfs", A.Str("QParam")).CallBind("rsc", "qc", "OpenRecordset", A.I4(Dao.OpenSnapshot)).Errors().ReadAll("rsc", 16)
                    .OpenRs("rsi", "db", "QImplicit", Dao.OpenSnapshot).Errors()
                    .CallBind("tq", "db", "CreateQueryDef", A.Str(""), A.Str("SELECT ID FROM T WHERE ID = pX;"))
                    .CallBind("rst", "tq", "OpenRecordset", A.I4(Dao.OpenSnapshot)).Errors()
                    .GetBind("tqp", "tq", "Parameters").Item("tpx", "tqp", A.Str("pX")).Put("tpx", "Value", A.I4(1))
                    .CallBind("rst2", "tq", "OpenRecordset", A.I4(Dao.OpenSnapshot)).ReadAll("rst2", 16).Call("rst2", "Close")
                    .Release("rst2").Release("tpx").Release("tqp").Release("tq").Release("qc").Release("paid").Release("pa").Release("pbid").Release("pb")
                    .Release("qa").Release("qb").Release("pid").Release("pn").Release("prms").Release("qd").Release("qdfs")
                    .Finish("db");
            });

        yield return Use("held-querydef-lifetime", ["CAT-V"], "accdb-queries",
            "Held QueryDef through rename, delete, recreation and Close on a writable copy of the query fixture. QOne and its Fields(0) are held; QOne.Name is set to QOneRenamed (old and new names then looked up); " +
            "QOneRenamed is deleted from QueryDefs (held Name, SQL, Field Name and OpenRecordset read); a new QOneRenamed is created; the Database is closed (held objects read again); Workspaces(0).Close is attempted.",
            p =>
            {
                p.Engine().OpenDb(readOnly: false).GetBind("qdfs", "db", "QueryDefs").Item("qd", "qdfs", A.Str("QOne"))
                    .GetBind("qfl", "qd", "Fields").Item("qf0", "qfl", A.I4(0)).Get("qf0", "Name")
                    .Put("qd", "Name", A.Str("QOneRenamed")).Get("qd", "Name")
                    .Item("lkold", "qdfs", A.Str("QOne")).Get("lkold", "SQL").Release("lkold")
                    .Item("lknew", "qdfs", A.Str("QOneRenamed")).Get("lknew", "SQL").Release("lknew")
                    .Call("qdfs", "Delete", A.Str("QOneRenamed")).Get("qdfs", "Count")
                    .Get("qd", "Name").Get("qd", "SQL").Get("qf0", "Name")
                    .CallBind("rs", "qd", "OpenRecordset", A.I4(Dao.OpenSnapshot)).Errors()
                    .CallBind("qn", "db", "CreateQueryDef", A.Str("QOneRenamed"), A.Str("SELECT ID FROM T;"))
                    .Get("qd", "Name").Get("qd", "SQL").Get("qn", "SQL")
                    .Call("db", "Close")
                    .Get("qd", "SQL").Get("qn", "Name").Get("qdfs", "Count")
                    .GetBind("wss", "eng", "Workspaces").Item("ws", "wss", A.I4(0)).Call("ws", "Close").Errors()
                    .Get("qn", "SQL").Get("wss", "Count")
                    .Release("ws").Release("wss").Release("qn").Release("qf0").Release("qfl").Release("qd").Release("qdfs").Release("db")
                    .Modules("after-close").Files("after-close");
            });

        yield return Use("collection-refresh", ["CAT-V"], "accdb-queries",
            "Collection membership and Refresh on a writable copy of the query fixture. A held QueryDefs collection's Count is read before and after CreateQueryDef and after Refresh; the same for a held TableDefs collection around CREATE TABLE. " +
            "A second Database on the same file in the same Workspace and a third through a new Workspace (DBEngine.CreateWorkspace) each hold QueryDefs; Count is read before and after the first Database creates a QueryDef and after Refresh, and the new QueryDef is looked up.",
            p =>
            {
                p.Engine().OpenDb(readOnly: false).GetBind("qdfs", "db", "QueryDefs").Get("qdfs", "Count")
                    .CallBind("q1", "db", "CreateQueryDef", A.Str("QRefresh1"), A.Str("SELECT ID FROM T;")).Release("q1")
                    .Get("qdfs", "Count").Call("qdfs", "Refresh").Get("qdfs", "Count")
                    .GetBind("tdfs", "db", "TableDefs").Get("tdfs", "Count");
                Exec(p, "CREATE TABLE TRefresh (ID LONG)");
                p.Get("tdfs", "Count").Call("tdfs", "Refresh").Get("tdfs", "Count")
                    .OpenDb("db2", readOnly: false).GetBind("qdfs2", "db2", "QueryDefs").Get("qdfs2", "Count")
                    .CallBind("ws2", "eng", "CreateWorkspace", A.Str("CalcDbProbe2"), A.Str("admin"), A.Str(""))
                    .CallBind("db3", "ws2", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false))
                    .GetBind("qdfs3", "db3", "QueryDefs").Get("qdfs3", "Count")
                    .CallBind("q2", "db", "CreateQueryDef", A.Str("QRefresh2"), A.Str("SELECT A FROM T;")).Release("q2")
                    .Get("qdfs2", "Count").Get("qdfs3", "Count")
                    .Call("qdfs2", "Refresh").Get("qdfs2", "Count").Call("qdfs3", "Refresh").Get("qdfs3", "Count")
                    .Item("l2", "qdfs2", A.Str("QRefresh2")).Get("l2", "SQL").Release("l2")
                    .Item("l3", "qdfs3", A.Str("QRefresh2")).Get("l3", "SQL").Release("l3")
                    .Release("qdfs3").Release("qdfs2").Release("tdfs").Release("qdfs")
                    .Finish("db3", "db2", "db");
            });
    }

    // CAT-I ------------------------------------------------------------------------------------------------

    private static IEnumerable<Probe> Invalidation()
    {
        yield return Use("drop-column", ["CAT-I"], "accdb-queries",
            "A query referring to a dropped column, on a writable copy of the query fixture. QDep (SELECT ID, B FROM T) is created; with a dynaset from it still open, ALTER TABLE T DROP COLUMN C is attempted; after Close, ALTER TABLE T DROP COLUMN B. " +
            "Asks for the held QDep's SQL and Fields, opens through the held QueryDef, a fresh lookup and the saved QGroup (which uses B), and DBEngine.Errors after each.",
            p =>
            {
                p.Engine().OpenDb(readOnly: false).CallBind("qd", "db", "CreateQueryDef", A.Str("QDep"), A.Str("SELECT ID, B FROM T;"))
                    .CallBind("rsh", "qd", "OpenRecordset", A.I4(Dao.OpenDynaset)).ReadRow("rsh");
                Exec(p, "ALTER TABLE T DROP COLUMN C");
                p.Errors().Call("rsh", "Close").Release("rsh");
                Exec(p, "ALTER TABLE T DROP COLUMN B");
                p.Errors().Get("qd", "SQL");
                FieldNames(p, "qd", "qdf");
                p.CallBind("rs2", "qd", "OpenRecordset", A.I4(Dao.OpenSnapshot)).Errors().ReadAll("rs2", 16)
                    .GetBind("qdfs", "db", "QueryDefs").Item("qf", "qdfs", A.Str("QDep")).CallBind("rs3", "qf", "OpenRecordset", A.I4(Dao.OpenSnapshot)).Errors()
                    .OpenRs("rs4", "db", "QGroup", Dao.OpenSnapshot).Errors()
                    .Release("qf").Release("qdfs").Release("qd").Finish("db");
            });

        yield return Use("rename-column", ["CAT-I"], "accdb-queries",
            "A query referring to a renamed column (engine-only, no Access Name AutoCorrect), on a writable copy of the query fixture. QRen (SELECT ID, A FROM T WHERE A <> 'q') is created and read; T.A is renamed to A2 through Field.Name. " +
            "Asks for the held QRen's SQL and Parameters, an open through it and its rows, an open of the saved QOne, and DBEngine.Errors after each.",
            p =>
            {
                p.Engine().OpenDb(readOnly: false).CallBind("qd", "db", "CreateQueryDef", A.Str("QRen"), A.Str("SELECT ID, A FROM T WHERE A <> 'q';"))
                    .CallBind("rs1", "qd", "OpenRecordset", A.I4(Dao.OpenSnapshot)).ReadAll("rs1", 16).Call("rs1", "Close").Release("rs1")
                    .GetBind("tdfs", "db", "TableDefs").Item("tdf", "tdfs", A.Str("T")).GetBind("fl", "tdf", "Fields").Item("fa", "fl", A.Str("A"))
                    .Put("fa", "Name", A.Str("A2")).Get("fa", "Name").Release("fa").Release("fl").Release("tdf").Release("tdfs")
                    .Get("qd", "SQL");
                ParamNames(p, "qd", "qdp");
                p.CallBind("rs2", "qd", "OpenRecordset", A.I4(Dao.OpenSnapshot)).Errors().ReadAll("rs2", 16)
                    .OpenRs("rs3", "db", "QOne", Dao.OpenSnapshot).Errors().ReadAll("rs3", 16)
                    .Release("qd").Finish("db");
            });

        yield return Use("select-star", ["CAT-I"], "accdb-queries",
            "SELECT * expansion after schema change, on a writable copy of the query fixture. QStar (SELECT * FROM T) is created; its QueryDef Fields and a recordset's Fields (Name, Type) are read, " +
            "then again through the held QueryDef and a fresh lookup after ALTER TABLE T ADD COLUMN E TEXT(5), and again after ALTER TABLE T DROP COLUMN C.",
            p =>
            {
                p.Engine().OpenDb(readOnly: false).CallBind("qd", "db", "CreateQueryDef", A.Str("QStar"), A.Str("SELECT * FROM T;"));
                StarView(p, "s0");
                Exec(p, "ALTER TABLE T ADD COLUMN E TEXT(5)");
                StarView(p, "s1");
                p.GetBind("qdfs", "db", "QueryDefs").Item("qf", "qdfs", A.Str("QStar"));
                FieldNames(p, "qf", "qff");
                p.Release("qf").Release("qdfs");
                Exec(p, "ALTER TABLE T DROP COLUMN C");
                StarView(p, "s2");
                p.Release("qd").Finish("db");
            });

        yield return Use("column-type-change", ["CAT-I"], "accdb-queries",
            "A query over a column whose type changes, on a writable copy of the query fixture. QType (SELECT ID, B + 1 AS X FROM T) is created and its recordset Fields and rows read; after ALTER TABLE T ALTER COLUMN B TEXT(10) the held QueryDef is opened and read again, with DBEngine.Errors.",
            p =>
            {
                p.Engine().OpenDb(readOnly: false).CallBind("qd", "db", "CreateQueryDef", A.Str("QType"), A.Str("SELECT ID, B + 1 AS X FROM T;"))
                    .CallBind("rs1", "qd", "OpenRecordset", A.I4(Dao.OpenSnapshot));
                FieldNames(p, "rs1", "r1f");
                p.ReadAll("rs1", 16).Call("rs1", "Close").Release("rs1");
                Exec(p, "ALTER TABLE T ALTER COLUMN B TEXT(10)");
                p.Errors().CallBind("rs2", "qd", "OpenRecordset", A.I4(Dao.OpenSnapshot)).Errors();
                FieldNames(p, "rs2", "r2f");
                p.ReadAll("rs2", 16).Release("qd").Finish("db");
            });

        yield return Use("unresolved-identifier", ["CAT-I"], "accdb-queries",
            "An unresolved identifier that later becomes a column, on a writable copy of the query fixture. QUnres (SELECT ID, Z FROM T) is created; its Parameters are enumerated and an open is attempted (DBEngine.Errors). " +
            "After ALTER TABLE T ADD COLUMN Z LONG, the held QueryDef's Parameters are enumerated again, it is opened and read, and a fresh lookup's Parameters are enumerated.",
            p =>
            {
                p.Engine().OpenDb(readOnly: false).CallBind("qd", "db", "CreateQueryDef", A.Str("QUnres"), A.Str("SELECT ID, Z FROM T;"));
                ParamNames(p, "qd", "u0");
                p.CallBind("rs1", "qd", "OpenRecordset", A.I4(Dao.OpenSnapshot)).Errors();
                Exec(p, "ALTER TABLE T ADD COLUMN Z LONG");
                ParamNames(p, "qd", "u1");
                p.CallBind("rs2", "qd", "OpenRecordset", A.I4(Dao.OpenSnapshot)).Errors();
                FieldNames(p, "rs2", "u2f");
                p.ReadAll("rs2", 16).GetBind("qdfs", "db", "QueryDefs").Item("qf", "qdfs", A.Str("QUnres"));
                ParamNames(p, "qf", "u3");
                p.Release("qf").Release("qdfs").Release("qd").Finish("db");
            });

        yield return Use("nested-querydef", ["CAT-I"], "accdb-queries",
            "A nested QueryDef whose inner definition changes, on a writable copy of the query fixture. QNested (SELECT B FROM QBase) is held and read; QBase.SQL is set to SELECT ID FROM T and the held QNested and a fresh lookup are opened (DBEngine.Errors, held Fields); " +
            "QBase.SQL is set back to its original text and the held QNested opened and read again.",
            p =>
            {
                p.Engine().OpenDb(readOnly: false).GetBind("qdfs", "db", "QueryDefs").Item("qb", "qdfs", A.Str("QBase")).Item("qn", "qdfs", A.Str("QNested"))
                    .CallBind("rs1", "qn", "OpenRecordset", A.I4(Dao.OpenSnapshot)).ReadAll("rs1", 16).Call("rs1", "Close").Release("rs1")
                    .Put("qb", "SQL", A.Str("SELECT ID FROM T;"))
                    .CallBind("rs2", "qn", "OpenRecordset", A.I4(Dao.OpenSnapshot)).Errors();
                FieldNames(p, "qn", "qnf");
                p.Item("qn2", "qdfs", A.Str("QNested")).CallBind("rs3", "qn2", "OpenRecordset", A.I4(Dao.OpenSnapshot)).Errors()
                    .Put("qb", "SQL", A.Str("SELECT ID, B FROM T;"))
                    .CallBind("rs4", "qn", "OpenRecordset", A.I4(Dao.OpenSnapshot)).Errors().ReadAll("rs4", 16)
                    .Release("qn2").Release("qn").Release("qb").Release("qdfs").Finish("db");
            });

        yield return Use("recreate-table", ["CAT-I"], "accdb-queries",
            "Drop and recreate a referenced table under the same name with different column types, on a writable copy of the query fixture. QW (SELECT ID, A FROM W) is created and read; MSysObjects rows named W are read; " +
            "DROP TABLE W, CREATE TABLE W (ID TEXT(5), A LONG) and one INSERT follow; MSysObjects rows named W are read again; the held QW and the saved QUnion3 are opened (Fields, rows, DBEngine.Errors).",
            p =>
            {
                p.Engine().OpenDb(readOnly: false);
                SysRead(p, "w0", "SELECT * FROM MSysObjects WHERE Name = 'W'");
                p.CallBind("qd", "db", "CreateQueryDef", A.Str("QW"), A.Str("SELECT ID, A FROM W;"))
                    .CallBind("rs1", "qd", "OpenRecordset", A.I4(Dao.OpenSnapshot)).ReadAll("rs1", 16).Call("rs1", "Close").Release("rs1");
                Exec(p, "DROP TABLE W");
                p.Errors();
                Exec(p, "CREATE TABLE W (ID TEXT(5), A LONG)");
                Exec(p, "INSERT INTO W VALUES ('x', 7)");
                SysRead(p, "w1", "SELECT * FROM MSysObjects WHERE Name = 'W'");
                p.CallBind("rs2", "qd", "OpenRecordset", A.I4(Dao.OpenSnapshot)).Errors();
                FieldNames(p, "rs2", "w2f");
                p.ReadAll("rs2", 16).OpenRs("rs3", "db", "QUnion3", Dao.OpenSnapshot).Errors();
                FieldNames(p, "rs3", "w3f");
                p.ReadAll("rs3", 16).Release("qd").Finish("db");
            });

        string[] likeSql =
        [
            "SELECT ID FROM QLike89", "SELECT ID FROM QLike92",
            "SELECT ID FROM T WHERE A LIKE 'a*' ORDER BY ID", "SELECT ID FROM T WHERE A LIKE 'a%' ORDER BY ID",
        ];
        yield return Use("wildcard-mode", ["CAT-I"], "accdb-queries",
            "Wildcard mode by client on the query fixture. Through ADODB.Connection (Provider Microsoft.ACE.OLEDB.12.0, Data Source set through the Properties collection, Mode adModeRead) and then through DAO read-only, the same four statements are run: " +
            "SELECT from saved QLike89 ('a*') and QLike92 ('a%'), and the same two LIKE predicates ad hoc. Asks for each statement's rows per client.",
            p =>
            {
                p.Files("before-open").Modules("before-create").Create("ADODB.Connection", "cn").Modules("after-create-ado")
                    .Put("cn", "Provider", A.Str("Microsoft.ACE.OLEDB.12.0")).Put("cn", "Mode", A.I4(1))
                    .GetBind("cprops", "cn", "Properties").Item("ds", "cprops", A.Str("Data Source")).Put("ds", "Value", A.In()).Release("ds").Release("cprops")
                    .Call("cn", "Open").Get("cn", "Version");
                for (int i = 0; i < likeSql.Length; i++)
                {
                    p.CallBind($"ars{i}", "cn", "Execute", A.Str(likeSql[i])).ReadAll($"ars{i}", 16).Call($"ars{i}", "Close").Release($"ars{i}");
                }
                p.Call("cn", "Close").Release("cn").Create().Modules("after-create").OpenDb();
                for (int i = 0; i < likeSql.Length; i++)
                {
                    p.OpenRs($"drs{i}", "db", likeSql[i], Dao.OpenSnapshot).ReadAll($"drs{i}", 16).Call($"drs{i}", "Close").Release($"drs{i}");
                }
                p.Finish("db");
            });
    }

    private static void StarView(P p, string tag)
    {
        FieldNames(p, "qd", tag + "q");
        p.CallBind(tag + "rs", "qd", "OpenRecordset", A.I4(Dao.OpenSnapshot)).Errors();
        FieldNames(p, tag + "rs", tag + "r");
        p.Call(tag + "rs", "Close").Release(tag + "rs");
    }

    // Shared helpers -----------------------------------------------------------------------------------------

    private static Probe Use(string name, string[] branches, string fixture, string description, Action<P> program) =>
        new(Card, name, R(branches), description, program) { Fixtures = [("main", Fx(fixture))] };

    private static void Exec(P p, string sql) => p.Call("db", "Execute", A.Str(sql));

    /// <summary><c>owner.CreateProperty(args)</c> then <c>owner.Properties.Append</c>, with binds unique to <paramref name="bind"/>.</summary>
    private static void AppendProp(P p, string owner, string bind, params Arg[] createArgs) =>
        p.CallBind(bind, owner, "CreateProperty", createArgs)
            .GetBind(bind + "c", owner, "Properties").Call(bind + "c", "Append", A.Obj(bind)).Release(bind).Release(bind + "c");

    private static void PropList(P p, string owner, string bind) =>
        p.GetBind(bind, owner, "Properties")
            .Each(bind, bind + "i", 64, b => b.Get(bind + "i", "Name").Get(bind + "i", "Type").Get(bind + "i", "Value").Get(bind + "i", "Inherited"))
            .Release(bind);

    private static void FieldNames(P p, string owner, string bind) =>
        p.GetBind(bind, owner, "Fields").Each(bind, bind + "i", 32, b => b.Get(bind + "i", "Name").Get(bind + "i", "Type")).Release(bind);

    private static void ParamNames(P p, string owner, string bind) =>
        p.GetBind(bind, owner, "Parameters").Get(bind, "Count")
            .Each(bind, bind + "i", 16, b => b.Get(bind + "i", "Name").Get(bind + "i", "Type")).Release(bind);

    /// <summary>Open <paramref name="sql"/> (or a table name with OpenTable), read field metadata and all rows, close.</summary>
    private static void SysRead(P p, string tag, string sql, int maxRows = 256, string db = "db", int type = Dao.OpenSnapshot)
    {
        string rs = "s_" + tag, fl = "sf_" + tag;
        p.OpenRs(rs, db, sql, type)
            .GetBind(fl, rs, "Fields").Each(fl, fl + "i", 64, b => b.Get(fl + "i", "Name").Get(fl + "i", "Type").Get(fl + "i", "Size")).Release(fl)
            .ReadAll(rs, maxRows, 64).Call(rs, "Close").Release(rs);
    }

    private static void QueryMeta(P p, string db = "db") =>
        p.GetBind("qdfs", db, "QueryDefs").Get("qdfs", "Count")
            .Each("qdfs", "qd", 48, q =>
            {
                q.Get("qd", "Name").Get("qd", "Type").Get("qd", "SQL").Get("qd", "Connect").Get("qd", "ReturnsRecords").Get("qd", "Updatable")
                    .GetBind("qpr", "qd", "Parameters").Each("qpr", "qp", 16, b => b.Get("qp", "Name").Get("qp", "Type")).Release("qpr")
                    .GetBind("qfl", "qd", "Fields")
                    .Each("qfl", "qf", 32, b => b.Get("qf", "Name").Get("qf", "Type").Get("qf", "Size").Get("qf", "SourceTable").Get("qf", "SourceField"))
                    .Release("qfl");
                PropList(q, "qd", "qpp");
            })
            .Release("qdfs");
}
