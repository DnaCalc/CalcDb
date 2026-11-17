namespace CalcDb.Probes.Cards;

/// <summary>
/// Repairs of probe and fixture defects found in the retained records of R-FMT-1 (r-fmt-1), R-FMT-2 and
/// R-PRESERVE-1 (r-fmt-2-pres-1, r-fmt-2b) and R-SORT-3 (r-sort-3, r-sort-3b). Each probe asks the same question as
/// the original, built so that the defect cannot recur: every fault runs on its own fresh object or database; each
/// statement whose refusal matters uses dbFailOnError and DBEngine.Errors is read right after it; a collection is
/// refreshed before it is read after DDL; the state is read back after each step; Decimal columns are made through ADO
/// DDL with explicit precision and scale (a DAO CreateField(name, 20) Decimal has Precision 38 and DAO cannot read it)
/// and Decimal or fractional values are written as SQL literals, VT_CY or VT_I4, never as BSTR text, because BSTR
/// conversion depends on the user locale (7177). Fixtures written here are named <c>r-fix-fmt-sort-*</c>; scratch files
/// start with <c>scratch-fixfs-</c>, so they are never taken for fixtures. Every file is named explicitly (no
/// DBEngine.OpenDatabase with an empty name, no ODBC).
/// </summary>
public sealed class RFixFmtSort : ICard
{
    private const string Card = "r-fix-fmt-sort";
    private const string Ace = "Microsoft.ACE.OLEDB.12.0";
    private const int Wait = 30000;
    private const char Wide = (char)0x20AC;
    private const int PgChars = 61;
    private const int SafeRows = 203, RiskyRows = 37, PairRows = 54, LkNonNullRows = 36;

    private const string FxFixture = Card + "-fx-accdb";
    private const string World21 = "r-fmt-2-pres-1-world-accdb";
    private const string World2B = "r-fmt-2b-w-accdb";

    private const string Images = " Images: 'before' (the fixture copy as given), then one kept image of the closed file after each step; every step opens the database read/write, makes its mutation and closes it, with the DBEngine still alive.";

    private static readonly (string Ext, int Version)[] Formats = [("accdb", Dao.Version120), ("mdb", Dao.Version40)];

    private static readonly string[] AllOps = ["=", ">=", ">", "<=", "<"];

    private static readonly string[] F1 = ["R-FMT-1", "D-FMT-1", "II-03", "II-04", "T-FMT-01", "T-FMT-02"];

    private static string[] F2(params string[] branches) => ["R-FMT-2", .. branches];

    private static string[] S3(params string[] branches) => ["R-SORT-3", "SORT-CHECK", .. branches];

    private static string Fx(string name) => $"{Card}-{name}";

    private static string Fmt(string ext) => ext.ToUpperInvariant();

    private sealed record Gated(string Name, string Type, Arg[] Values, string Shown);

    public IEnumerable<Probe> Probes()
    {
        foreach (Probe p in Fmt1())
        {
            yield return p;
        }
        foreach (Probe p in Fmt2())
        {
            yield return p;
        }
        foreach (Probe p in Sort3())
        {
            yield return p;
        }
        foreach (Probe p in Sort3B())
        {
            yield return p;
        }
    }

    // ================================================================================================
    // R-FMT-1

    private static IEnumerable<Probe> Fmt1()
    {
        foreach ((string ext, int version) in Formats)
        {
            string file = Fx($"fmt1-{ext}-decimal") + "." + ext;
            yield return new Probe(Card, $"fmt1-{ext}-decimal", F1,
                $"Repairs r-fmt-1/create-{ext}-decimal and r-fmt-1/read-{ext}-decimal: DAO DDL refused DECIMAL(18,4) with 3292, so T was never created and both records hold only system tables. " +
                $"Asks, on a new {Fmt(ext)} database from DBEngine.CreateDatabase(path, dbLangGeneral, {version}) kept as a fixture: ADO (Provider {Ace}) creates T(ID INTEGER primary key, V DECIMAL(18,4)) in the closed file; DAO then writes a Null row (ID 1) and the values 12345678901234.5678, -0.0001, 1.5 and 7 as SQL numeric literals (IDs 2 to 5), and through a table-type recordset AddNew/Value/Update VT_CY 1.5, VT_I4 7 and the BSTR integer text 7 (IDs 6 to 8), with DBEngine.Errors read after each put and update. " +
                "Reads V's Type, Size, Attributes and property list (Precision and Scale where present), the rows through a TableDef snapshot and through SQL with CStr(V); closes, keeps the file, reopens it read-only and reads the same user-table view again. No system table is opened.",
                p =>
                {
                    p.EngineLite().CallBind("db", "eng", "CreateDatabase", A.Out(file), A.Str(Dao.LangGeneral), A.I4(version)).Get("db", "Version");
                    p.Call("db", "Close").Release("db");
                    AdoDdl(p, A.Out(file), "CREATE TABLE T (ID INTEGER CONSTRAINT PK_T PRIMARY KEY, V DECIMAL(18,4))");
                    p.Files("after-ado-ddl", keep: true);
                    OpenOut(p, file, false);
                    foreach ((int id, string literal) in new[] { (1, "Null"), (2, "12345678901234.5678"), (3, "-0.0001"), (4, "1.5"), (5, "7") })
                    {
                        Tr(p, $"INSERT INTO T (ID, V) VALUES ({id}, {literal})");
                    }
                    PutRows(p, "T", "ID", "V", [(6, A.Cy(1.5m)), (7, A.I4(7)), (8, A.Str("7"))]);
                    FieldProps(p, "db", "T", "V", "dv");
                    TableView(p, "db", "T");
                    ReadSql(p, "SELECT ID, V, CStr(V) AS TV FROM T ORDER BY ID", 16, 4);
                    CloseKeep(p, "closed-after-write");
                    OpenOut(p, file, true);
                    p.Get("db", "Version").Get("db", "CollatingOrder");
                    FieldProps(p, "db", "T", "V", "dr");
                    TableView(p, "db", "T");
                    p.FinishLite("db");
                })
            { CreatesFixture = true };
        }

        Gated[] gated =
        [
            new("bigint", "BIGINT", [A.Str("9223372036854775807"), A.I4(-1)], "the BSTR integer text 9223372036854775807 and VT_I4 -1"),
            new("datetime2", "DATETIME2", [A.Date(1.5)], "VT_DATE 1.5"),
        ];
        foreach (Gated g in gated)
        {
            string file = Fx($"fmt1-mdb-{g.Name}") + ".mdb";
            yield return new Probe(Card, $"fmt1-mdb-{g.Name}", F1,
                $"Repairs r-fmt-1/create-mdb-{g.Name} and r-fmt-1/read-mdb-{g.Name}: CREATE TABLE T with the {g.Type} column was refused in the Jet 4 MDB (3001), so T was never created and the follow-up open failed with 3011. " +
                $"Asks, on a new MDB database from CreateDatabase(path, dbLangGeneral, {Dao.Version40}) kept as a fixture: T(ID Long primary key) is created on its own; the field list is read; ALTER TABLE T ADD COLUMN V {g.Type} runs as its own statement with dbFailOnError and DBEngine.Errors; the (refreshed) field list is read again; a Null row (ID 1) is inserted by SQL and {g.Shown} is put through a table-type recordset (IDs 2 and up) with DBEngine.Errors read after each put and update; then the user-table view is read, the file is closed and kept, and the view is read again after a read-only reopen.",
                p =>
                {
                    p.EngineLite().CallBind("db", "eng", "CreateDatabase", A.Out(file), A.Str(Dao.LangGeneral), A.I4(Dao.Version40)).Get("db", "Version");
                    Tr(p, "CREATE TABLE T (ID LONG CONSTRAINT PK_T PRIMARY KEY)");
                    FieldsOf(p, "db", "T", "fa");
                    Tr(p, $"ALTER TABLE T ADD COLUMN V {g.Type}");
                    FieldsOf(p, "db", "T", "fb");
                    Tr(p, "INSERT INTO T (ID) VALUES (1)");
                    PutRows(p, "T", "ID", "V", g.Values.Select((v, i) => (i + 2, v)));
                    TableView(p, "db", "T");
                    CloseKeep(p, "closed-after-write");
                    OpenOut(p, file, true);
                    p.Get("db", "Version");
                    TableView(p, "db", "T");
                    p.FinishLite("db");
                })
            { CreatesFixture = true };
        }

        (string Tag, string Locale)[] locales =
        [
            ("general", Dao.LangGeneral),
            ("german", ";LANGID=0x0407;CP=1252;COUNTRY=0"),
            ("uk", ";LANGID=0x0809;CP=1252;COUNTRY=0"),
        ];
        yield return new Probe(Card, "fmt1-collating-contrast", F1,
            "Repairs r-fmt-1/create-accdb-collating-general: every creation passed the same LANGID 0x0409, so reading CollatingOrder back could only show 1033 and gave no contrast. " +
            "Asks, for three new ACCDB databases made by CreateDatabase(path, locale, 128) with dbLangGeneral (LANGID 0x0409), ;LANGID=0x0407;CP=1252;COUNTRY=0 and ;LANGID=0x0809;CP=1252;COUNTRY=0: Database.CollatingOrder at creation; T(ID Long primary key, S TEXT(20)) with index IX_S; the refreshed field list of T (S's CollatingOrder); and, after close and a read-only reopen, CollatingOrder and the field list again.",
            p =>
            {
                p.EngineLite();
                foreach ((string tag, string locale) in locales)
                {
                    string file = $"scratch-fixfs-coll-{tag}.accdb";
                    string db = "d" + tag;
                    p.CallBind(db, "eng", "CreateDatabase", A.Out(file), A.Str(locale), A.I4(Dao.Version120)).Get(db, "Version").Get(db, "CollatingOrder");
                    Tr(p, "CREATE TABLE T (ID LONG CONSTRAINT PK_T PRIMARY KEY, S TEXT(20))", db);
                    Tr(p, "CREATE INDEX IX_S ON T (S)", db);
                    FieldsOf(p, db, "T", "c" + tag);
                    p.Call(db, "Close").Release(db).Files("after-close-" + tag);
                    p.CallBind(db, "eng", "OpenDatabase", A.Out(file), A.Bool(false), A.Bool(true)).Get(db, "CollatingOrder");
                    FieldsOf(p, db, "T", "r" + tag);
                    p.Call(db, "Close").Release(db);
                }
                p.FinishLite();
            });

        foreach ((string ext, int version) in Formats)
        {
            string file = Fx($"fmt1-{ext}-properties") + "." + ext;
            yield return new Probe(Card, $"fmt1-{ext}-properties", F1,
                $"Repairs r-fmt-1/create-{ext}-properties and r-fmt-1/read-{ext}-properties: the TableDef Description property was written (CreateProperty/Append) but never read back, because the read-back enumerated only database properties. " +
                $"Asks, on a new {Fmt(ext)} database from CreateDatabase(path, dbLangGeneral, {version}) kept as a fixture: T(ID Long primary key); the database property CalcDbProbe (dbText) and the TableDef property Description (dbText, 'table description') are created and appended, DBEngine.Errors read after each Append; then, in the same session and again after close and a read-only reopen, the database property CalcDbProbe (Type, Value) and the names and types of T's refreshed Properties collection plus Description's Type and Value are read.",
                p =>
                {
                    p.EngineLite().CallBind("db", "eng", "CreateDatabase", A.Out(file), A.Str(Dao.LangGeneral), A.I4(version)).Get("db", "Version");
                    Tr(p, "CREATE TABLE T (ID LONG CONSTRAINT PK_T PRIMARY KEY)");
                    p.CallBind("prop", "db", "CreateProperty", A.Str("CalcDbProbe"), A.I4(10), A.Str("value of a user property"))
                        .GetBind("props", "db", "Properties").Call("props", "Append", A.Obj("prop")).Errors().Release("prop").Release("props");
                    TdOpen(p, "db", "T", "pt");
                    p.CallBind("tprop", "pt", "CreateProperty", A.Str("Description"), A.I4(10), A.Str("table description"))
                        .GetBind("tprops", "pt", "Properties").Call("tprops", "Append", A.Obj("tprop")).Errors().Release("tprop").Release("tprops").Release("pt");
                    ReadProps(p, "db");
                    CloseKeep(p, "closed-after-write");
                    OpenOut(p, file, true);
                    ReadProps(p, "db");
                    p.FinishLite("db");
                })
            { CreatesFixture = true };
        }
    }

    private static void ReadProps(P p, string db)
    {
        p.GetBind("rdp", db, "Properties").Call("rdp", "Refresh").Item("rdu", "rdp", A.Str("CalcDbProbe")).Get("rdu", "Type").Get("rdu", "Value").Release("rdu").Release("rdp");
        TdOpen(p, db, "T", "rt");
        p.GetBind("rtp", "rt", "Properties").Call("rtp", "Refresh").Each("rtp", "rte", 64, b => b.Get("rte", "Name").Get("rte", "Type"))
            .Item("rtd", "rtp", A.Str("Description")).Get("rtd", "Type").Get("rtd", "Value").Release("rtd").Release("rtp").Release("rt");
    }

    // ================================================================================================
    // R-FMT-2 and R-PRESERVE-1

    private const string FxText = "Fx: ten rows on one small table (ID Long key; Byte Fb, Long Fl, Currency Fc, Date Ft, GUID Fg, a Text Vt and Decimal Fm made by ADO DDL as DECIMAL(28,0), so DAO can read it); row 8 is Null in Fb, Fl, Fc, Ft and Fm; elsewhere Fm = ID * 100.";

    private const string PgText = "Pg: 12 rows, ID and eight Text(255) fields T1..T8, each holding 61 repetitions of U+20AC (122 UTF-16 bytes of value; the character lies outside Latin-1).";

    private const string DrText = "Dr: 20 small rows (ID Long key, A Long, Vt Text(60) 'original text of row <ID>'), ID 1..20.";

    private static readonly string PgCols = string.Join(", ", Enumerable.Range(1, 8).Select(i => $"T{i}"));

    private static void ReadFx(P p)
    {
        ReadSql(p, "SELECT * FROM Fx ORDER BY ID", 16, 12);
        ReadSql(p, "SELECT ID, CStr(Fm) AS TFm FROM Fx ORDER BY ID", 16, 4);
        IndexStats(p, "Fx");
    }

    private static void ReadPg(P p) => ReadSql(p, "SELECT ID, LEN(T1), LEN(T2), LEN(T8) FROM Pg ORDER BY ID", 16, 4);

    private static void ReadDr(P p)
    {
        ReadSql(p, "SELECT * FROM Dr ORDER BY ID", 32, 4);
        IndexStats(p, "Dr");
    }

    private static void ReadIx(P p, string table)
    {
        ReadSql(p, $"SELECT COUNT(*), MIN(ID), MAX(ID) FROM {table}", 4, 4);
        IndexStats(p, table);
    }

    private static void ReadAu(P p, string table)
    {
        ReadSql(p, $"SELECT * FROM {table} ORDER BY ID", 32, 4);
        IndexStats(p, table);
    }

    private static (string Label, Action<P> Body) InsertAuto(string label, string table, string text) =>
        D(label, p =>
        {
            Sql(p, $"INSERT INTO {table} (S) VALUES ('{text}')");
            ReadSql(p, "SELECT @@IDENTITY", 2, 2);
        });

    private static IEnumerable<Probe> Fmt2()
    {
        yield return new Probe(Card, "fmt2-fx-fixture", ["R-FMT-2", "R-PRESERVE-1", "II-03 concrete mutation probes", "Scalar boundaries", "NUMERIC"],
            "Repairs r-fmt-2b/create-w-accdb and r-fmt-2-pres-1/create-world-accdb: the Decimal columns Fx.Fm and Sc.Fm were appended with DAO TableDef.CreateField(name, 20), which gives Precision 38 and Scale 0; DAO Field.Value then fails on every non-Null value (3000, -1038) and the writes of the scalar-admission probe were refused (0x800A0EAF). " +
            "Fixture (ACCDB), CreateDatabase(path, dbLangGeneral, 128): the Nums helper (1..1024); Fx exactly as in r-fmt-2b (ten rows, ID Long key, Byte Fb, Long Fl, Currency Fc, Date Ft, GUID Fg, Text Vt, row 8 Null in all fixed fields, GUIDs written through a table recordset); Sc2(ID Long key) and Dt(ID Long key, Ft Date). " +
            "On the closed file ADO (Provider " + Ace + ") adds Fx.Fm as DECIMAL(28,0) and Sc2.Fm as DECIMAL(18,4); DAO then sets Fx.Fm = ID * 100 with SQL integer literals for every row except 8. Asks what DAO reports for both Decimal fields (Type, Size, Attributes, Precision and Scale) and what DAO and SQL CStr(Fm) read back. The closed file is kept.",
            p =>
            {
                string file = FxFixture + ".accdb";
                p.EngineLite().CallBind("db", "eng", "CreateDatabase", A.Out(file), A.Str(Dao.LangGeneral), A.I4(Dao.Version120)).Get("db", "Version");
                Tr(p, "CREATE TABLE Nums (K LONG CONSTRAINT PK_Nums PRIMARY KEY)");
                Tr(p, "INSERT INTO Nums (K) VALUES (1)");
                for (int m = 1; m <= 512; m *= 2)
                {
                    Tr(p, $"INSERT INTO Nums (K) SELECT K + {m} FROM Nums");
                }
                Tr(p, "CREATE TABLE Fx (ID LONG CONSTRAINT PK_Fx PRIMARY KEY, Fb BYTE, Fl LONG, Fc CURRENCY, Ft DATETIME, Fg GUID, Vt TEXT(30))");
                Tr(p, "INSERT INTO Fx (ID, Fb, Fl, Fc, Ft, Vt) SELECT K, K * 10, K * 1000, CCur(K * 1.25), #1/1/2000#, 'v' & K FROM Nums WHERE K <= 10");
                Tr(p, "UPDATE Fx SET Fb = Null, Fl = Null, Fc = Null, Ft = Null WHERE ID = 8");
                p.OpenRs("rs", "db", "Fx", Dao.OpenTable).GetBind("fl", "rs", "Fields").Item("fg", "fl", A.Str("Fg"));
                for (int id = 1; id <= 10; id++)
                {
                    if (id != 8)
                    {
                        p.Call("rs", "Edit").Put("fg", "Value", A.Str($"{{00112233-4455-6677-8899-AABBCCDDEE{id:X2}}}")).Call("rs", "Update").Errors();
                    }
                    p.Call("rs", "MoveNext");
                }
                p.Release("fg").Release("fl").Call("rs", "Close").Release("rs");
                Tr(p, "CREATE TABLE Sc2 (ID LONG CONSTRAINT PK_Sc2 PRIMARY KEY)");
                Tr(p, "CREATE TABLE Dt (ID LONG CONSTRAINT PK_Dt PRIMARY KEY, Ft DATETIME)");
                p.Call("db", "Close").Release("db");
                AdoDdl(p, A.Out(file), "ALTER TABLE Fx ADD COLUMN Fm DECIMAL(28,0)", "ALTER TABLE Sc2 ADD COLUMN Fm DECIMAL(18,4)");
                p.Files("after-ado-ddl", keep: true);
                OpenOut(p, file, false);
                foreach (int id in new[] { 1, 2, 3, 4, 5, 6, 7, 9, 10 })
                {
                    Tr(p, $"UPDATE Fx SET Fm = {id * 100} WHERE ID = {id}");
                }
                FieldProps(p, "db", "Fx", "Fm", "fm");
                FieldProps(p, "db", "Sc2", "Fm", "sm");
                ReadFx(p);
                p.FinishLite("db").Files("fixture-closed", keep: true);
            })
        { CreatesFixture = true };

        // Fm of r-fmt-2-pres-1/scalar-admission: three Decimal writes onto a column of explicit precision.
        string[] scalar = F2("Scalar boundaries", "NUMERIC", "Generated values");
        yield return Mut("fmt2-scalar-admission-decimal", scalar, FxFixture,
            "Repairs r-fmt-2-pres-1/scalar-admission: the three Decimal writes (12345678901234.5678, -0.0001, VT_CY 1.5) went onto Sc.Fm made by CreateField(name, 20) (Precision 38, Scale 0), so every refusal (0x800A0EAF) describes that column and maps no precision or scale. " +
            "Asks the same three admissions on Sc2.Fm, a DECIMAL(18,4) column made by ADO DDL: 12345678901234.5678 and -0.0001 as SQL INSERT literals (IDs 1 and 2, dbFailOnError, DBEngine.Errors after each), and VT_CY 1.5 through a table-type recordset AddNew/Value/Update (ID 3, DBEngine.Errors after the put and the update). The field's properties and the stored values (DAO, SQL CStr) are read back before and after.",
            p =>
            {
                ReadSql(p, "SELECT ID, Fm, CStr(Fm) AS TFm FROM Sc2 ORDER BY ID", 8, 4);
                FieldProps(p, "db", "Sc2", "Fm", "sf");
            },
            D("write-attempts", p =>
            {
                Tr(p, "INSERT INTO Sc2 (ID, Fm) VALUES (1, 12345678901234.5678)");
                Tr(p, "INSERT INTO Sc2 (ID, Fm) VALUES (2, -0.0001)");
                PutRows(p, "Sc2", "ID", "Fm", [(3, A.Cy(1.5m))]);
            }));

        // The out-of-range label of r-fmt-2-pres-1/scalar-admission: both ends of the Date range.
        yield return Mut("fmt2-scalar-admission-date-range", F2("Scalar boundaries", "Dates / GUID"), FxFixture,
            "Repairs r-fmt-2-pres-1/scalar-admission: the date 2958466 was labelled out-of-range but was accepted as the upper endpoint, and the overflow side was never probed. " +
            "Asks the admission of VT_DATE values through a table-type recordset into Dt.Ft, one row per value (DBEngine.Errors after the put and the update): 2958465.99998843, 2958466, 2958466.5, 2958467, 10000000 at the upper end, and -657434, -657435, -700000 at the lower end. Typed values are read back; one image after all of them.",
            p => ReadSql(p, "SELECT * FROM Dt ORDER BY ID", 16, 4),
            D("write-attempts", p => PutRows(p, "Dt", "ID", "Ft",
            [
                (1, A.Date(2958465.99998843)), (2, A.Date(2958466.0)), (3, A.Date(2958466.5)), (4, A.Date(2958467.0)), (5, A.Date(10000000.0)),
                (6, A.Date(-657434.0)), (7, A.Date(-657435.0)), (8, A.Date(-700000.0)),
            ])));

        yield return Ladder("fmt2-var-null-empty-ladder", F2("Ordinary Boolean / Null", "Text grammar", "Historical row coordinates"), World21,
            "Repairs r-fmt-2-pres-1/var-null-empty-ladder: only the end state was read back (the intermediate states of V were known from file hashes only), and its description said one attempt is refused although none was. " +
            "Null versus empty Text on one variable field. Row 7 has T.N Null and T.V = 'v7' (V allows zero length, N is a DDL default). Steps, each with dbFailOnError: V to the empty string; V to Null; V to a value; an attempt to store the empty string in N. After every step the closed file is kept and row 7 (N, V) is read back through a read-only open, so the Null/empty state is typed after each step.",
            p => ReadSql(p, "SELECT ID, N, V FROM T WHERE ID = 7", 2, 4),
            Q("v-to-empty", "UPDATE T SET V = '' WHERE ID = 7"), Q("v-to-null", "UPDATE T SET V = Null WHERE ID = 7"),
            Q("v-to-value", "UPDATE T SET V = 'filled' WHERE ID = 7"), Q("n-empty-attempt", "UPDATE T SET N = '' WHERE ID = 7"));

        // r-fmt-2b: the ten probes whose reads failed on the unreadable Decimal Fm.
        string[] fxr = F2("II-03 concrete mutation probes", "Scalar boundaries");
        yield return Mut("fmt2b-fx-byte-update", fxr, FxFixture,
            $"Repairs r-fmt-2b/fx-byte-update: the read of every row failed on the unreadable Decimal Fm (hr 0x80020009, -1038), so the record is ReturnedFailure. Which bytes of the closed file change when one one-byte fixed field (Fx.Fb, Byte) of row 5 is updated to a different value by UPDATE with dbFailOnError? {FxText}",
            ReadFx, Q("update-fb", "UPDATE Fx SET Fb = 200 WHERE ID = 5"));
        yield return Mut("fmt2b-fx-currency-update", fxr, FxFixture,
            $"Repairs r-fmt-2b/fx-currency-update: the read of every row failed on the unreadable Decimal Fm. Which bytes change when one eight-byte fixed field (Fx.Fc, Currency) of row 5 is updated to a different value? {FxText}",
            ReadFx, Q("update-fc", "UPDATE Fx SET Fc = CCur(1234.5678) WHERE ID = 5"));
        yield return Mut("fmt2b-fx-guid-update", F2("II-03 concrete mutation probes", "Dates / GUID"), FxFixture,
            $"Repairs r-fmt-2b/fx-guid-update: the read of every row failed on the unreadable Decimal Fm. Which bytes change when one sixteen-byte fixed field (Fx.Fg, GUID) of row 5 is set through a dynaset Edit/Update to a different braced GUID string? {FxText}",
            ReadFx, D("edit-fg", p => EditRs(p, "Fx", 5, "Fg", A.Str("{FFEEDDCC-BBAA-9988-7766-554433221100}"))));
        yield return Mut("fmt2b-fx-decimal-update", F2("II-03 concrete mutation probes", "NUMERIC"), FxFixture,
            $"Repairs r-fmt-2b/fx-decimal-update: the Decimal Fm was a precision-38 column that DAO cannot read, so UPDATE Fx SET Fm = 12345 (RecordsAffected 1) had no public confirmation. Which bytes change when the Decimal field (Fx.Fm, DECIMAL(28,0)) of row 5 is updated to the integer 12345 by UPDATE; the value is read back through DAO Field.Value and through SQL CStr(Fm). {FxText}",
            ReadFx, Q("update-fm", "UPDATE Fx SET Fm = 12345 WHERE ID = 5"));
        yield return Mut("fmt2b-fx-null-to-value", F2("Ordinary Boolean / Null", "II-03 concrete mutation probes"), FxFixture,
            $"Repairs r-fmt-2b/fx-null-to-value: the read of every row failed on the unreadable Decimal Fm. Which bytes change when a Null fixed field (Fx.Fl of row 8, whose other fixed fields stay Null) is updated to a value? {FxText}",
            ReadFx, Q("null-to-value", "UPDATE Fx SET Fl = 777 WHERE ID = 8"));
        yield return Mut("fmt2b-fx-value-to-null", F2("Ordinary Boolean / Null", "II-03 concrete mutation probes"), FxFixture,
            $"Repairs r-fmt-2b/fx-value-to-null: the read of every row failed on the unreadable Decimal Fm. Which bytes change when a fixed field with a value (Fx.Fl of row 5) is updated to Null? {FxText}",
            ReadFx, Q("value-to-null", "UPDATE Fx SET Fl = Null WHERE ID = 5"));
        yield return Mut("fmt2b-fx-same-value-update", F2("II-03 concrete mutation probes"), FxFixture,
            $"Repairs r-fmt-2b/fx-same-value-update: the read of every row failed on the unreadable Decimal Fm. Which bytes change when a fixed field is updated to the value it already holds (UPDATE Fx SET Fl = 5000 WHERE ID = 5; Fl is 5000)? {FxText}",
            ReadFx, Q("update-fl-same", "UPDATE Fx SET Fl = 5000 WHERE ID = 5"));

        yield return new Probe(Card, "fmt2b-compact-after-fixed-update", ["R-FMT-2", "R-PRESERVE-1", "II-03 concrete mutation probes", "Compact & Repair diagnostic", "II-04 preservation cases", "Capture and comparison"],
            $"Repairs r-fmt-2b/compact-after-fixed-update: the read of every row failed on the unreadable Decimal Fm. Compact after a fixed-width edit: Fx.Fb of row 5 is updated by UPDATE with dbFailOnError; then DBEngine.CompactDatabase of the mutated file into a work file (DBEngine.Errors read after it; the mutated file is unchanged). The public state is read before the mutation, after it and from the compacted file.{Images} The compacted file is kept as a separate derivative (after-compact). {FxText}",
            p =>
            {
                Begin(p);
                Read(p, "before", ReadFx);
                RunStep(p, "update-fb", s => Sql(s, "UPDATE Fx SET Fb = 200 WHERE ID = 5"));
                Read(p, "mutated", ReadFx);
                p.Call("eng", "CompactDatabase", A.In(), A.Out("scratch-fixfs-compacted.accdb")).Errors().Files("after-compact", keep: true)
                    .CallBind("db", "eng", "OpenDatabase", A.Out("scratch-fixfs-compacted.accdb"), A.Bool(false), A.Bool(true));
                ReadFx(p);
                p.Call("db", "Close").Release("db").Files("read-compacted");
                End(p);
            })
        { Fixtures = [("main", FxFixture)] };

        yield return new Probe(Card, "fmt2b-settle-fixed-update", F2("II-03 concrete mutation probes", "Capture and comparison", "R-TXN-1 flush window"),
            $"Repairs r-fmt-2b/settle-fixed-update: the read of every row failed on the unreadable Decimal Fm. One fixed-width edit (UPDATE Fx SET Fb = 200 WHERE ID = 5, dbFailOnError). One kept image after the database Close (DBEngine alive), a second after a 1500 ms pause, a third after the DBEngine is released. Asks whether the three images are the same bytes. {FxText}",
            p =>
            {
                Begin(p);
                Read(p, "before", ReadFx);
                RunStep(p, "update-fb", s => Sql(s, "UPDATE Fx SET Fb = 200 WHERE ID = 5"));
                p.Pause(1500).Files("after-1500ms", keep: true).Release("eng").Files("after-engine-release", keep: true).FinishLite();
            })
        { Fixtures = [("main", FxFixture)] };

        yield return new Probe(Card, "fmt2b-kill-after-close-fixed-update", F2("II-03 concrete mutation probes", "Capture and comparison", "R-TXN-1 flush window", "R-CRASH-1"),
            $"Repairs r-fmt-2b/kill-after-close-fixed-update: the read of every row failed on the unreadable Decimal Fm. One fixed-width edit (UPDATE Fx SET Fb = 200 WHERE ID = 5, dbFailOnError). Writer A opens the database read/write, makes the mutation, closes the database (DBEngine alive) and signals; process B kills A at once (TerminateProcess, verified own worker), waits 0.5 s, keeps the frozen file, opens it read-only with DAO and reads the public state. Process-termination model only. {FxText}",
            a =>
            {
                a.EngineLite().Files("before", keep: true).CallBind("db", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false));
                Sql(a, "UPDATE Fx SET Fb = 200 WHERE ID = 5");
                a.Call("db", "Close").Release("db").Signal("a-closed").Await("never", Wait).FinishLite();
            })
        {
            Fixtures = [("main", FxFixture)],
            Participants =
            [
                ("b", b =>
                {
                    b.Await("a-closed", Wait).KillParticipant("a", 0).Pause(500).Files("frozen", keep: true).EngineLite().OpenDb();
                    ReadFx(b);
                    b.Call("db", "Close").Release("db").Files("after-read", keep: true).FinishLite();
                }),
            ],
        };

        // r-fmt-2b: ladders that had no public read between steps.
        string[] del = F2("Overflow / deletion", "II-03 concrete mutation probes", "II-05 Deletion");
        const string NoRead = " no public read between steps (only the whole-file hash existed for the middle steps).";
        yield return Ladder("fmt2b-dl-pg-empty-ladder", [.. del, "Map ownership and growth"], World2B,
            $"Repairs r-fmt-2b/dl-pg-empty-ladder:{NoRead} Deletes until a page empties: Pg ID 1, 2, 3 and 4 are deleted by four single statements, then one full-size row (ID 21, eight fields of 61 U+20AC) is inserted. Asks which delete empties the page, what each earlier delete changes, and whether the insert reuses the emptied page; after every step the closed file is kept and the lengths of Pg are read back through a read-only open. {PgText}",
            ReadPg, Q("delete-id-1", "DELETE FROM Pg WHERE ID = 1"), Q("delete-id-2", "DELETE FROM Pg WHERE ID = 2"), Q("delete-id-3", "DELETE FROM Pg WHERE ID = 3"),
            Q("delete-id-4", "DELETE FROM Pg WHERE ID = 4"),
            Q("insert-id-21", $"INSERT INTO Pg (ID, {PgCols}) VALUES (21, {string.Join(", ", Enumerable.Repeat($"'{new string(Wide, PgChars)}'", 8))})"));
        yield return Ladder("fmt2b-dr-reinsert-same-key-same-values", del, World2B,
            $"Repairs r-fmt-2b/dr-reinsert-same-key-same-values:{NoRead} Delete then reinsert of one key: Dr row 8 is deleted, then ID 8 is inserted again with the values it had (A 80, Vt 'original text of row 8'). Asks whether the new row takes the old slot and body, and how the primary-key index entry is retired and re-added; the rows and index statistics are read back after each step. {DrText}",
            ReadDr, Q("delete-id-8", "DELETE FROM Dr WHERE ID = 8"), Q("reinsert-id-8-same", "INSERT INTO Dr (ID, A, Vt) VALUES (8, 80, 'original text of row 8')"));
        yield return Ladder("fmt2b-dr-reinsert-same-key-longer-value", del, World2B,
            $"Repairs r-fmt-2b/dr-reinsert-same-key-longer-value:{NoRead} Delete then reinsert of one key with a longer value: Dr row 8 is deleted, then ID 8 is inserted again with A 80 and a 60-character Vt. Asks whether the longer row can reuse the freed space; the rows and index statistics are read back after each step. {DrText}",
            ReadDr, Q("delete-id-8", "DELETE FROM Dr WHERE ID = 8"), Q("reinsert-id-8-longer", $"INSERT INTO Dr (ID, A, Vt) VALUES (8, 80, '{new string('L', 60)}')"));
        const string Ixm = "IxM: 602 Long primary-key entries (1..301 and 303..603), no other column.";
        yield return Ladder("fmt2b-ix-delete-reinsert-ladder", F2("II-05 Deletion", "II-05 Statistics word"), World2B,
            $"Repairs r-fmt-2b/ix-delete-reinsert-ladder:{NoRead} Delete then reinsert of one index key: key 100 is deleted, then inserted again. Asks how the entry is retired and re-added and whether the statistic follows; count, minimum, maximum and the index statistics are read back after each step. {Ixm}",
            p => ReadIx(p, "IxM"), Q("delete-100", "DELETE FROM IxM WHERE ID = 100"), Q("reinsert-100", "INSERT INTO IxM (ID) VALUES (100)"));
        yield return Ladder("fmt2b-ix-dup-ladder", F2("II-05 Splits / stable root", "II-05 Statistics word", "II-03 concrete mutation probes"), World2B,
            $"Repairs r-fmt-2b/ix-dup-ladder:{NoRead} Inserts into a non-unique index that holds one value many times: IxD has 300 rows (ID 1..300) all with G = 7 and index IX_IxD_G on G. Four single statements add 100, 100, 200 and 400 more rows with G = 7. Asks at which statement the duplicate-key index gains pages and how the primary-key index and the statistic change; count and the index statistics are read back after each statement.",
            p => ReadIx(p, "IxD"),
            Q("add-100", "INSERT INTO IxD (ID, G) SELECT K + 300, 7 FROM Nums WHERE K <= 100"), Q("add-100-more", "INSERT INTO IxD (ID, G) SELECT K + 400, 7 FROM Nums WHERE K <= 100"),
            Q("add-200", "INSERT INTO IxD (ID, G) SELECT K + 500, 7 FROM Nums WHERE K <= 200"), Q("add-400", "INSERT INTO IxD (ID, G) SELECT K + 700, 7 FROM Nums WHERE K <= 400"));
        string pad = new('p', 40);
        yield return Ladder("fmt2b-ix-text-ladder", F2("II-05 Splits / stable root", "II-05 Statistics word", "II-03 concrete mutation probes"), World2B,
            $"Repairs r-fmt-2b/ix-text-ladder:{NoRead} Ascending key inserts into a unique Text index of long keys: IxT has 60 rows (ID 1..60, W 'k' + a four-digit number + 40 'p' characters) and unique index UX_IxT_W. Four statements add 20 rows each with higher keys. Asks at which statement the index leaf is split and where the new keys land; count and the index statistics are read back after each statement.",
            p => ReadIx(p, "IxT"),
            [.. Enumerable.Range(1, 4).Select(n => Q($"add-20-rows-{n}", $"INSERT INTO IxT (ID, W) SELECT K + {40 + 20 * n}, 'k' & Format((K + {40 + 20 * n}) * 10, '0000') & '{pad}' FROM Nums WHERE K <= 20"))]);
        string[] gen = F2("Generated values", "II-03 concrete mutation probes");
        yield return Ladder("fmt2b-au-explicit-high-ladder", gen, World2B,
            $"Repairs r-fmt-2b/au-explicit-high-ladder:{NoRead} AutoNumber and an explicit key: Au1 (ten rows, IDs 1..10) first receives INSERT with ID 100 given, then an insert without an ID whose new ID is read by @@IDENTITY. Asks how the explicit value changes the counter and what each step changes in the file; the table and index statistics are read back after each step.",
            p => ReadAu(p, "Au1"), Q("insert-explicit-100", "INSERT INTO Au1 (ID, S) VALUES (100, 'explicit')"), InsertAuto("insert-auto", "Au1", "after explicit"));
        yield return Ladder("fmt2b-au-reseed-ladder", gen, World2B,
            $"Repairs r-fmt-2b/au-reseed-ladder: the increment 5 was not observed (only one insert followed the reseed) and{NoRead} AutoNumber reseed: Au1 (ten rows) receives ALTER TABLE Au1 ALTER COLUMN ID COUNTER(200, 5) with dbFailOnError (the key column is the primary key; a refusal is an observation); then two inserts without an ID, each as its own step, whose IDs are read by @@IDENTITY. Asks what the file shows after each step, which ID the first insert gets and how far the second one advances; the table is read back after every step.",
            p => ReadAu(p, "Au1"), Q("alter-counter-200-5", "ALTER TABLE Au1 ALTER COLUMN ID COUNTER(200, 5)"),
            InsertAuto("insert-auto-1", "Au1", "after reseed 1"), InsertAuto("insert-auto-2", "Au1", "after reseed 2"));
    }

    // ------------------------------------------------------------------------------------------------
    // Probe shapes for R-FMT-2

    private static (string Label, Action<P> Body) Q(string label, params string[] sqls) => (label, p => Sql(p, sqls));

    private static (string Label, Action<P> Body) D(string label, Action<P> body) => (label, body);

    private static Probe Mut(string name, string[] refs, string fixture, string what, Action<P> read, params (string Label, Action<P> Body)[] steps) =>
        new(Card, name, refs, what + Images, p =>
        {
            Begin(p);
            Read(p, "before", read);
            foreach ((string label, Action<P> body) in steps)
            {
                RunStep(p, label, body);
            }
            Read(p, "after", read);
            End(p);
        })
        { Fixtures = [("main", fixture)] };

    /// <summary>Like <see cref="Mut"/>, with the same public read after every step (a read-only open of the closed file).</summary>
    private static Probe Ladder(string name, string[] refs, string fixture, string what, Action<P> read, params (string Label, Action<P> Body)[] steps) =>
        new(Card, name, refs, what + Images, p =>
        {
            Begin(p);
            Read(p, "before", read);
            foreach ((string label, Action<P> body) in steps)
            {
                RunStep(p, label, body);
                Read(p, "after-" + label, read);
            }
            End(p);
        })
        { Fixtures = [("main", fixture)] };

    private static void Begin(P p) => p.EngineLite().Files("before", keep: true);

    private static void End(P p) => p.Release("eng").FinishLite();

    /// <summary>Open read/write, run the body (one mutation), close, and keep the closed file's image.</summary>
    private static void RunStep(P p, string label, Action<P> body)
    {
        p.CallBind("db", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false));
        body(p);
        p.Call("db", "Close").Release("db").Files("after-" + label, keep: true);
    }

    /// <summary>Open read-only, read the public projection, close; the file state after is recorded.</summary>
    private static void Read(P p, string tag, Action<P> body)
    {
        p.OpenDb();
        body(p);
        p.Call("db", "Close").Release("db").Files("read-" + tag);
    }

    /// <summary>Edit one field of the row with this ID through a dynaset (Edit/Update).</summary>
    private static void EditRs(P p, string table, int id, string field, Arg value) =>
        p.OpenRs("rs", "db", $"SELECT * FROM {table} WHERE ID = {id}", Dao.OpenDynaset).GetBind("fl", "rs", "Fields").Item("ff", "fl", A.Str(field))
            .Call("rs", "Edit").Put("ff", "Value", value).Call("rs", "Update").Errors().Release("ff").Release("fl").Call("rs", "Close").Release("rs");

    // ================================================================================================
    // R-SORT-3 (r-sort-3)

    private static IEnumerable<Probe> Sort3()
    {
        foreach ((string ext, int version) in Formats)
        {
            string corpus = $"r-sort-3-corpus-{ext}";
            string keys = $"r-sort-3-keys-{ext}";
            yield return Comp(ext, corpus);
            yield return UniqueCorpus(ext, corpus);
            yield return Stages(ext, corpus);
            yield return UniqueLu(ext, keys);
            yield return NpTables(ext, version);
            yield return FrTable(ext, version);
            yield return NumsDecimal(ext, version);
        }
    }

    private static Probe Comp(string ext, string corpus) =>
        new(Card, $"sort3-comp-{ext}", S3("Text levels", "Positions and controls", "Traversal consequences"),
            $"Repairs r-sort-3/create-corpus-{ext}: CREATE TABLE C ... TEXT(255) WITH COMPRESSION is refused by DAO Database.Execute (3290), so table C was never created and order-sql-{ext} and order-index-{ext} ended in failures on C. " +
            $"Asks, on a copy of fixture {corpus} ({Fmt(ext)}) opened read/write: ADO (Provider {Ace}, Data Source set through Connection.Properties) creates C(ID INTEGER primary key, KC TEXT(255) WITH COMPRESSION, KN TEXT(255)) in the closed copy; DAO then fills it from T with INSERT INTO C (ID, KC, KN) SELECT ID, K, K WHERE G is ascii, latin1, han, width or pair (the compressed and the uncompressed copy of one subset) with dbFailOnError, builds IX_KC and IX_KN after the rows, and reads C's fields, the ID sequences of SQL ORDER BY KC and ORDER BY KN, and the IX_KC and IX_KN table-type walks.",
            p =>
            {
                p.EngineLite();
                AdoDdl(p, A.In(), "CREATE TABLE C (ID INTEGER CONSTRAINT PK_C PRIMARY KEY, KC TEXT(255) WITH COMPRESSION, KN TEXT(255))");
                p.Files("after-ado-ddl", keep: true);
                p.CallBind("db", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false)).Get("db", "Version").Get("db", "CollatingOrder");
                FieldsOf(p, "db", "C", "cf");
                Tr(p, "INSERT INTO C (ID, KC, KN) SELECT ID, K, K FROM T WHERE G IN ('ascii', 'latin1', 'han', 'width', 'pair')");
                p.Get("db", "RecordsAffected");
                Tr(p, "CREATE INDEX IX_KC ON C (KC)");
                Tr(p, "CREATE INDEX IX_KN ON C (KN)");
                IndexMeta(p, "C");
                ReadSql(p, "SELECT ID FROM C ORDER BY KC", 140, 1);
                ReadSql(p, "SELECT ID FROM C ORDER BY KN", 140, 1);
                Walk(p, "db", "C", "IX_KC", false, 140);
                Walk(p, "db", "C", "IX_KN", false, 140);
                CloseKeep(p, "closed");
                p.FinishLite();
            })
        { Fixtures = [("main", corpus)] };

    private static Probe UniqueCorpus(string ext, string corpus) =>
        new(Card, $"sort3-unique-corpus-{ext}", S3("Text levels", "Positions and controls", "UTF-16 boundaries", "Shortened-key collisions", "Null policy / aliases"),
            $"Repairs r-sort-3/create-corpus-{ext}: the one-INSERT-per-row loads into the unique-indexed tables W (from T) and W2 (from U) used Database.Execute without options, so a refusal returned hr 0 and neither the refusal nor its error number was recorded. " +
            $"Asks, on a copy of fixture {corpus} ({Fmt(ext)}) opened read/write: new tables WR(ID Long primary key, K TEXT(255)) and W2R, each with a unique index UX_K on K, filled by one INSERT INTO ... SELECT ID, K FROM T (IDs 1 to {SafeRows}) or FROM U (IDs 1 to {RiskyRows}) per statement with dbFailOnError, DBEngine.Errors read after each; then the IDs that survive in each and each table's index metadata.",
            p =>
            {
                p.EngineLite().CallBind("db", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false)).Get("db", "Version").Get("db", "CollatingOrder");
                foreach ((string table, string source, int rows) in new[] { ("WR", "T", SafeRows), ("W2R", "U", RiskyRows) })
                {
                    Tr(p, $"CREATE TABLE {table} (ID LONG CONSTRAINT PK_{table} PRIMARY KEY, K TEXT(255))");
                    Tr(p, $"CREATE UNIQUE INDEX UX_K ON {table} (K)");
                    for (int id = 1; id <= rows; id++)
                    {
                        Tr(p, $"INSERT INTO {table} (ID, K) SELECT ID, K FROM {source} WHERE ID = {id}");
                    }
                    ReadSql(p, $"SELECT ID FROM {table} ORDER BY ID", rows + 8, 1);
                    IndexMeta(p, table);
                }
                CloseKeep(p, "closed");
                p.FinishLite();
            })
        { Fixtures = [("main", corpus)] };

    private static Probe Stages(string ext, string corpus) =>
        new(Card, $"sort3-stages-{ext}", S3("Text levels", "Traversal consequences", "SORT-CHECK"),
            $"Repairs r-sort-3/create-corpus-{ext}: the intermediate whole-file images of the build stages were size-only (unread: IOException 0x80070020, file in use), so the fresh-versus-incremental builds of T and T2 were not retained as images. " +
            $"Asks, on a copy of fixture {corpus} ({Fmt(ext)}), with the database closed and its image kept after each step (every step is its own read/write open): TS(ID, K, G) filled from T in ID order, no index; IX_K on TS (fresh build); IX_KD on TS; T2S with IX_K and IX_KD created empty; T2S filled by INSERT..SELECT from TS ORDER BY ID (incremental build). Then, through a read-only open, SQL ORDER BY K and the IX_K and IX_KD walks of TS and T2S.",
            p =>
            {
                Begin(p);
                RunStep(p, "ts-rows-no-index", s =>
                {
                    Tr(s, "CREATE TABLE TS (ID LONG CONSTRAINT PK_TS PRIMARY KEY, K TEXT(255), G TEXT(16))");
                    Tr(s, "INSERT INTO TS (ID, K, G) SELECT ID, K, G FROM T ORDER BY ID");
                });
                RunStep(p, "ts-fresh-ix-k", s => Tr(s, "CREATE INDEX IX_K ON TS (K)"));
                RunStep(p, "ts-fresh-ix-kd", s => Tr(s, "CREATE INDEX IX_KD ON TS (K DESC)"));
                RunStep(p, "t2s-empty-indexes", s =>
                {
                    Tr(s, "CREATE TABLE T2S (ID LONG CONSTRAINT PK_T2S PRIMARY KEY, K TEXT(255), G TEXT(16))");
                    Tr(s, "CREATE INDEX IX_K ON T2S (K)");
                    Tr(s, "CREATE INDEX IX_KD ON T2S (K DESC)");
                });
                RunStep(p, "t2s-incremental", s => Tr(s, "INSERT INTO T2S (ID, K, G) SELECT ID, K, G FROM TS ORDER BY ID"));
                Read(p, "final", s =>
                {
                    foreach (string table in new[] { "TS", "T2S" })
                    {
                        ReadSql(s, $"SELECT ID FROM {table} ORDER BY K", SafeRows + 8, 1);
                        Walk(s, "db", table, "IX_K", false, SafeRows + 8);
                        Walk(s, "db", table, "IX_KD", false, SafeRows + 8);
                    }
                });
                End(p);
            })
        { Fixtures = [("main", corpus)] };

    private static Probe UniqueLu(string ext, string keys) =>
        new(Card, $"sort3-unique-lu-{ext}", S3("Compound framing", "Shortened-key collisions", "Null policy / aliases"),
            $"Repairs r-sort-3/create-keys-{ext}: the one-INSERT-per-row loads into the unique-indexed LU, LU2 and LU3 used Database.Execute without options, so a refused row returned hr 0 and no error number was recorded. " +
            $"Asks, on a copy of fixture {keys} ({Fmt(ext)}) opened read/write: new tables LUR (unique index on (K, N)), LU2R ((K, N DESC)) and LU3R ((K)), each (S Counter primary key, ID, K, N), filled by one INSERT..SELECT per LK row ID 1 to {LkNonNullRows} and then the repeated IDs 1, 5, 6 and 19, with dbFailOnError and DBEngine.Errors read after each; then the (S, ID) rows that survive and each table's index metadata.",
            p =>
            {
                p.EngineLite().CallBind("db", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false)).Get("db", "Version").Get("db", "CollatingOrder");
                foreach ((string table, string index) in new[] { ("LUR", "(K, N)"), ("LU2R", "(K, N DESC)"), ("LU3R", "(K)") })
                {
                    Tr(p, $"CREATE TABLE {table} (S COUNTER CONSTRAINT PK_{table} PRIMARY KEY, ID LONG, K TEXT(255), N LONG)");
                    Tr(p, $"CREATE UNIQUE INDEX UX ON {table} {index}");
                    foreach (int id in Enumerable.Range(1, LkNonNullRows).Concat([1, 5, 6, 19]))
                    {
                        Tr(p, $"INSERT INTO {table} (ID, K, N) SELECT ID, K, N FROM LK WHERE ID = {id}");
                    }
                    ReadSql(p, $"SELECT S, ID FROM {table} ORDER BY S", LkNonNullRows + 16, 2);
                    IndexMeta(p, table);
                }
                CloseKeep(p, "closed");
                p.FinishLite();
            })
        { Fixtures = [("main", keys)] };

    private static Probe NpTables(string ext, int version)
    {
        string file = Fx($"sort3-np-{ext}") + "." + ext;
        (string Name, string Create, string? Index)[] nps =
        [
            ("NP1", "CREATE TABLE NP1 (K TEXT(20), N LONG)", "CREATE UNIQUE INDEX UX ON NP1 (K, N)"),
            ("NP2", "CREATE TABLE NP2 (K TEXT(20), N LONG)", "CREATE UNIQUE INDEX UX ON NP2 (K, N) WITH IGNORE NULL"),
            ("NP3", "CREATE TABLE NP3 (K TEXT(20), N LONG, CONSTRAINT PK_NP3 PRIMARY KEY (K, N))", null),
            ("NP4", "CREATE TABLE NP4 (K TEXT(20), N LONG)", "CREATE UNIQUE INDEX UX ON NP4 (K, N) WITH DISALLOW NULL"),
            ("NP5", "CREATE TABLE NP5 (K TEXT(20) NOT NULL, N LONG)", "CREATE UNIQUE INDEX UX ON NP5 (K, N)"),
            ("NP6", "CREATE TABLE NP6 (K TEXT(20), N LONG)", "CREATE UNIQUE INDEX UX ON NP6 (K) WITH IGNORE NULL"),
            ("NP7", "CREATE TABLE NP7 (K TEXT(20), N LONG)", "CREATE UNIQUE INDEX UX ON NP7 (K)"),
            ("NP8", "CREATE TABLE NP8 (K TEXT(20), N LONG)", "CREATE INDEX IX ON NP8 (K, N) WITH IGNORE NULL"),
            ("NP9", "CREATE TABLE NP9 (K TEXT(20), N LONG)", null),
            ("NP10", "CREATE TABLE NP10 (K TEXT(20), N LONG)", null),
        ];
        string[] tuples = ["NULL, NULL", "NULL, NULL", "NULL, 1", "NULL, 1", "'a', NULL", "'a', NULL", "'a', 1", "'a', 1", "'', NULL", "'', NULL", "'', 1", "'', 1"];
        return new Probe(Card, $"sort3-np-{ext}", S3("Null policy / aliases", "Compound framing", "Traversal consequences"),
            $"Repairs r-sort-3/create-keys-{ext}: the NP1..NP8 loads used Database.Execute without options (refusals returned hr 0 with no error number), and NP10's DAO index was never created: TableDefs.Item(\"NP10\") failed with 0x800A0CC1 because the collection was not refreshed after CREATE TABLE, so its 12 inserts ran on a table with no index. " +
            $"Fixture ({Fmt(ext)}), CreateDatabase(path, dbLangGeneral, {version}). NP1..NP10 (K TEXT(20), N Long) as in the original (unique, IGNORE NULL, DISALLOW NULL, primary key, NOT NULL and non-unique variants; NP9 and NP10 get a DAO index on K with Unique and Required, IgnoreNulls True for NP9 and False for NP10), each built with dbFailOnError and DBEngine.Errors after every statement. The DAO route refreshes TableDefs before Item, reads DBEngine.Errors after Indexes.Append and refreshes Indexes. Then 12 INSERTs per table over the tuples (Null,Null) x2, (Null,1) x2, (a,Null) x2, (a,1) x2, (empty,Null) x2, (empty,1) x2, one per statement with DBEngine.Errors after each; then the surviving (K, N) of each table and every index (Name, Primary, Unique, Foreign, Required, IgnoreNulls, DistinctCount, fields).",
            p =>
            {
                CreateOut(p, file, Dao.LangGeneral, version);
                foreach ((string name, string create, string? index) in nps)
                {
                    Tr(p, create);
                    if (index is not null)
                    {
                        Tr(p, index);
                    }
                    else if (name is "NP9" or "NP10")
                    {
                        DaoIndex(p, name, "UXD", ["K"], primary: false, unique: true, required: true, ignoreNulls: name == "NP9", tag: "np");
                    }
                    foreach (string t in tuples)
                    {
                        Tr(p, $"INSERT INTO {name} (K, N) VALUES ({t})");
                    }
                }
                foreach ((string name, _, _) in nps)
                {
                    ReadSql(p, $"SELECT K, N FROM {name}", 16, 2);
                    IndexMeta(p, name);
                }
                CloseKeep(p, "closed");
                p.FinishLite();
            })
        { CreatesFixture = true };
    }

    private static readonly string?[] FrK = [null, "", "a", "ab", X(1), X(2), "\0", X(0xFF), X(0x100), X("a", 1)];
    private static readonly int?[] FrN = [null, 0, 1, 256, -1, -256];
    private static readonly double?[] FrD = [null, 0.0, 1.5, 256.5, -1.5, -256.5];

    private static Probe FrTable(string ext, int version)
    {
        string file = Fx($"sort3-fr-{ext}") + "." + ext;
        int fr = FrK.Length * FrN.Length + 8;
        List<Arg[]> rows = [];
        int id = 1;
        foreach (string? k in FrK)
        {
            for (int i = 0; i < FrN.Length; i++)
            {
                rows.Add([A.I4(id++), Units(k), FrN[i] is int nv ? A.I4(nv) : A.Null, FrD[i] is double dv ? A.R8(dv) : A.Null]);
            }
        }
        return new Probe(Card, $"sort3-fr-{ext}", S3("Compound framing", "Length510 boundary", "Traversal consequences"),
            $"Repairs r-sort-3/create-keys-{ext}: CREATE INDEX IX_FKND ON FR returned 0x800A0D2F because IX_FKNd already exists (index names are case-insensitive), so walking \"IX_FKND\" in keys-order-index-{ext} walked IX_FKNd again and the (K, N, D) index was untested. " +
            $"Fixture ({Fmt(ext)}), CreateDatabase(path, dbLangGeneral, {version}). FR(ID Long primary key, K TEXT(20), N Long, D Double) with the original 60 rows (K over Null, empty, marker-like and embedded-null values; N over Null, 0, 1, 256, -1, -256; D paired with N), stored through a table-type recordset; then the seven compound indexes with distinct names: IX_FKN (K, N), IX_FNK (N, K), IX_FKdN (K DESC, N), IX_FKNd (K, N DESC), IX_FNdKd (N DESC, K DESC), IX_FKN3 (K, N, D) and IX_FDK (D, K), each with dbFailOnError and DBEngine.Errors. Reads the refreshed index metadata of FR, the IX_FKN3 walk forward and backward, the IX_FKNd walk (the one the original walked by mistake), and SQL ORDER BY K, N, D.",
            p =>
            {
                CreateOut(p, file, Dao.LangGeneral, version);
                Tr(p, "CREATE TABLE FR (ID LONG CONSTRAINT PK_FR PRIMARY KEY, K TEXT(20), N LONG, D DOUBLE)");
                Insert(p, "db", "FR", ["ID", "K", "N", "D"], [.. rows]);
                foreach ((string name, string cols) in new[]
                {
                    ("IX_FKN", "(K, N)"), ("IX_FNK", "(N, K)"), ("IX_FKdN", "(K DESC, N)"), ("IX_FKNd", "(K, N DESC)"),
                    ("IX_FNdKd", "(N DESC, K DESC)"), ("IX_FKN3", "(K, N, D)"), ("IX_FDK", "(D, K)"),
                })
                {
                    Tr(p, $"CREATE INDEX {name} ON FR {cols}");
                }
                IndexMeta(p, "FR");
                Walk(p, "db", "FR", "IX_FKN3", false, fr);
                Walk(p, "db", "FR", "IX_FKN3", true, fr);
                Walk(p, "db", "FR", "IX_FKNd", false, fr);
                ReadSql(p, "SELECT ID FROM FR ORDER BY K, N, D", fr, 1);
                CloseKeep(p, "closed");
                p.FinishLite();
            })
        { CreatesFixture = true };
    }

    private static Probe NumsDecimal(string ext, int version)
    {
        string file = Fx($"sort3-nums-decimal-{ext}") + "." + ext;
        (int Id, string Literal)[] literals =
        [
            (1, "-12345678901234.5678"), (2, "-1"), (3, "-0.0001"), (4, "0"), (5, "0.0001"), (6, "1"), (7, "1.5"), (8, "12345678901234.5678"),
        ];
        return new Probe(Card, $"sort3-nums-decimal-{ext}", S3("NUMERIC / GUID / extended date", "Integer and Money"),
            $"Repairs r-sort-3/create-nums-{ext}: the Decimal key column X was made by CreateField(\"X\", 20) (Precision 38, Scale 0), so six of the ten values failed with 0x800A0EAF and DAO could not read the four stored ones (0x800A0BB8); Decimal keys were not observed. " +
            $"Fixture ({Fmt(ext)}), CreateDatabase(path, dbLangGeneral, {version}). ND(ID Long primary key) is made by DAO; ADO (Provider {Ace}) adds X as DECIMAL(18,4) on the closed file; DAO then stores each value in its own row (other columns Null): the SQL literals -12345678901234.5678, -1, -0.0001, 0, 0.0001, 1, 1.5 and 12345678901234.5678 (IDs 1 to 8, dbFailOnError, DBEngine.Errors after each), and through a table-type recordset VT_I4 7 and VT_CY 1.5 (IDs 9 and 10). Index IX_X is built after the rows. Asks X's Precision and Scale, the stored values (DAO and SQL CStr) in ID order, and the IX_X walk forward and backward; the file is closed and kept, and the walk and rows are read again after a read-only reopen.",
            p =>
            {
                p.EngineLite().CallBind("db", "eng", "CreateDatabase", A.Out(file), A.Str(Dao.LangGeneral), A.I4(version)).Get("db", "Version").Get("db", "CollatingOrder");
                Tr(p, "CREATE TABLE ND (ID LONG CONSTRAINT PK_ND PRIMARY KEY)");
                p.Call("db", "Close").Release("db");
                AdoDdl(p, A.Out(file), "ALTER TABLE ND ADD COLUMN X DECIMAL(18,4)");
                p.Files("after-ado-ddl", keep: true);
                OpenOut(p, file, false);
                foreach ((int nid, string literal) in literals)
                {
                    Tr(p, $"INSERT INTO ND (ID, X) VALUES ({nid}, {literal})");
                }
                PutRows(p, "ND", "ID", "X", [(9, A.I4(7)), (10, A.Cy(1.5m))]);
                Tr(p, "CREATE INDEX IX_X ON ND (X)");
                FieldProps(p, "db", "ND", "X", "nx");
                ReadSql(p, "SELECT ID, X, CStr(X) AS TX FROM ND ORDER BY ID", 16, 4);
                Walk(p, "db", "ND", "IX_X", false, 16);
                Walk(p, "db", "ND", "IX_X", true, 16);
                CloseKeep(p, "closed-after-write");
                OpenOut(p, file, true);
                ReadSql(p, "SELECT ID, X, CStr(X) AS TX FROM ND ORDER BY ID", 16, 4);
                Walk(p, "db", "ND", "IX_X", false, 16);
                p.FinishLite("db");
            })
        { CreatesFixture = true };
    }

    // ================================================================================================
    // R-SORT-3 second wave (r-sort-3b)

    private static readonly string?[] Pair24 =
    [
        "a", "A", "b", "B", "e", "E", X(0xE9), X(0xC9), X(0xE8), X("e", 0x301),
        "ss", "SS", X(0xDF), "ae", X(0xE6), X(0xC6),
        "o", X(0xF6), X(0xF8), "n", X(0xF1), "u", X(0xFC), "ue",
    ];

    private static string NullKn(int id)
    {
        int slot = (id - 1) % 9;
        string k = (slot / 3) switch { 0 => "NULL", 1 => "''", _ => "'a'" };
        string n = (slot % 3) switch { 0 => "NULL", 1 => "1", _ => "2" };
        return $"{k}, {n}";
    }

    private static IEnumerable<Probe> Sort3B()
    {
        foreach (string tag in new[] { "lg", "lu" })
        {
            yield return UniqueRefusals(tag);
        }
        yield return NullsRefusals();
        yield return ImageBuild("a", ["IX1", "IX2", "IX3"]);
        yield return ImageBuild("b", ["IX3", "IX2", "IX1"]);
        yield return ImageFailedUnique();
        yield return ImageDropRebuild();
        yield return ExistingNulls();
        yield return DaoIndexFlags();
        yield return MemoIndexDdl();
    }

    private static Probe UniqueRefusals(string tag)
    {
        string fx = $"r-sort-3b-{tag}";
        return new Probe(Card, $"sort3b-unique-refusals-{tag}", S3("Text levels", "Null policy / aliases", "Traversal consequences"),
            $"Repairs r-sort-3b/create-{tag}: the one-INSERT-per-row loads into UQ and UQD used Database.Execute without options, so a refused row returned hr 0 (INSERT ... WHERE ID = 2 returned hr 0 while 2 is missing from the survivors) and only the absence of rows was visible. " +
            $"Asks, on a copy of fixture {fx} opened read/write: new tables UQ2 (unique index UX_K on K) and UQD2 (unique index UX_KD on K DESC), each (ID Long primary key, K TEXT(40)), filled by one INSERT..SELECT per ID of T (1 to {PairRows}) with dbFailOnError and DBEngine.Errors read after each; then the IDs that survive in each table and the refreshed index metadata of both.",
            p =>
            {
                p.EngineLite().CallBind("db", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false)).Get("db", "Version").Get("db", "CollatingOrder");
                foreach ((string table, string index, string cols) in new[] { ("UQ2", "UX_K", "(K)"), ("UQD2", "UX_KD", "(K DESC)") })
                {
                    Tr(p, $"CREATE TABLE {table} (ID LONG CONSTRAINT PK_{table} PRIMARY KEY, K TEXT(40))");
                    Tr(p, $"CREATE UNIQUE INDEX {index} ON {table} {cols}");
                    for (int id = 1; id <= PairRows; id++)
                    {
                        Tr(p, $"INSERT INTO {table} (ID, K) SELECT ID, K FROM T WHERE ID = {id}");
                    }
                    ReadSql(p, $"SELECT ID FROM {table} ORDER BY ID", PairRows + 8, 1);
                    IndexMeta(p, table);
                }
                CloseKeep(p, "closed");
                p.FinishLite();
            })
        { Fixtures = [("main", fx)] };
    }

    private static Probe NullsRefusals()
    {
        string file = Fx("sort3b-nulls-refusals") + ".accdb";
        (string Name, string Index)[] tables =
        [
            ("NI8", "CREATE INDEX IX ON NI8 (K, N) WITH DISALLOW NULL"),
            ("NU1", "CREATE UNIQUE INDEX IX ON NU1 (K, N) WITH IGNORE NULL"),
            ("NU2", "CREATE UNIQUE INDEX IX ON NU2 (K, N) WITH DISALLOW NULL"),
            ("NU3", "CREATE UNIQUE INDEX IX ON NU3 (K, N)"),
            ("NU4", "CREATE UNIQUE INDEX IX ON NU4 (K DESC) WITH IGNORE NULL"),
        ];
        return new Probe(Card, "sort3b-nulls-refusals", S3("Null policy / aliases", "Compound framing", "Traversal consequences"),
            "Repairs r-sort-3b/create-nulls: every INSERT into NI8 and NU1..NU4 was Database.Execute with no options, so refused rows returned hr 0 and the error numbers were never captured. " +
            $"Fixture (General ACCDB, dbVersion120). The five tables (ID Long primary key, K TEXT(20), N Long), each with its index built first (NI8 non-unique WITH DISALLOW NULL on (K, N); NU1 unique (K, N) IGNORE NULL; NU2 unique (K, N) DISALLOW NULL; NU3 unique (K, N); NU4 unique (K DESC) IGNORE NULL), then 18 single-row INSERTs over K in (Null, empty, a) x N in (Null, 1, 2) twice, each with dbFailOnError and DBEngine.Errors read after it. Reads the surviving IDs, the refreshed index metadata and the forward walk of IX of each table.",
            p =>
            {
                CreateOut(p, file, Dao.LangGeneral, Dao.Version120);
                foreach ((string name, string index) in tables)
                {
                    Tr(p, $"CREATE TABLE {name} (ID LONG CONSTRAINT PK_{name} PRIMARY KEY, K TEXT(20), N LONG)");
                    Tr(p, index);
                    for (int id = 1; id <= 18; id++)
                    {
                        Tr(p, $"INSERT INTO {name} (ID, K, N) VALUES ({id}, {NullKn(id)})");
                    }
                    ReadSql(p, $"SELECT ID FROM {name} ORDER BY ID", 26, 1);
                    IndexMeta(p, name);
                    Walk(p, "db", name, "IX", false, 26);
                }
                CloseKeep(p, "closed");
                p.FinishLite();
            })
        { CreatesFixture = true };
    }

    private static Probe ImageBuild(string suffix, string[] order)
    {
        string file = $"scratch-fixfs-image-order-{suffix}.accdb";
        Dictionary<string, string> defs = new() { ["IX1"] = "(K)", ["IX2"] = "(K DESC)", ["IX3"] = "(K, ID)" };
        string?[] values = [.. Pair24];
        int n = values.Length + 8;
        return new Probe(Card, $"sort3b-image-build-order-{suffix}", S3("Text levels", "Compound framing", "Traversal consequences"),
            $"Repairs r-sort-3b/image-build-order-{suffix}: the intermediate files[...] captures were size-only (unread: IOException 0x80070020, file in use), so the states between index builds could not be compared although the final sizes were equal and the hashes differed. " +
            $"New General ACCDB. T(ID Long primary key, K TEXT(40)) with the first {values.Length} pair-corpus values in ID order; then the indexes {string.Join(", ", order.Select(o => $"{o} {defs[o]}"))} are built one after another, each in its own read/write open of the closed file, with a whole-file image kept after each close. Reads the IX1, IX2 and IX3 walks and every index's refreshed metadata through a read-only open. Asks what each index build changes in the file and whether the order of builds changes the final image, the walks or the reported index order (the build order reversed is sort3b-image-build-order-{(suffix == "a" ? "b" : "a")}).",
            p =>
            {
                CreateOut(p, file, Dao.LangGeneral, Dao.Version120);
                Tr(p, "CREATE TABLE T (ID LONG CONSTRAINT PK_T PRIMARY KEY, K TEXT(40))");
                Insert(p, "db", "T", ["ID", "K"], [.. values.Select((v, i) => new[] { A.I4(i + 1), Units(v) })]);
                CloseKeep(p, "rows-no-index");
                foreach (string ix in order)
                {
                    StepOut(p, file, ix.ToLowerInvariant(), s => Tr(s, $"CREATE INDEX {ix} ON T {defs[ix]}"));
                }
                OpenOut(p, file, true);
                foreach (string ix in new[] { "IX1", "IX2", "IX3" })
                {
                    Walk(p, "db", "T", ix, false, n);
                }
                IndexMeta(p, "T");
                p.FinishLite("db");
            });
    }

    private static Probe ImageFailedUnique()
    {
        string file = "scratch-fixfs-image-failed-unique.accdb";
        string?[] first10 = [.. Pair24.Take(10)];
        return new Probe(Card, "sort3b-image-failed-unique", S3("Text levels", "Null policy / aliases", "Traversal consequences"),
            "Repairs r-sort-3b/image-failed-unique: the TableDefs collection was not refreshed after CREATE TABLE, so TableDefs.Item(\"TU\") failed (0x800A0CC1) and the metadata of TU was never read; refused unique builds, inserts and updates ran with no options so their error numbers were not captured; and the intermediate images were size-only (file in use). " +
            "New General ACCDB. T(ID Long primary key, K TEXT(40)) with the first 10 pair-corpus values (a, A, b, B, e, E, e-acute, E-acute, e-grave, e + combining acute), image kept. Each of these steps is its own read/write open of the closed file with dbFailOnError, DBEngine.Errors after the statement and an image kept after the close: CREATE UNIQUE INDEX UX_K ON T (K) (equal-comparing values are present); CREATE UNIQUE INDEX UX_KID ON T (K, ID); CREATE TABLE TU with unique index UX on K and the rows a and b; INSERT of A; UPDATE of b to A; INSERT of a Null; INSERT of a second Null. Then, through a read-only open: the refreshed index metadata of T and TU, a UX_K walk of T (does a refused build leave a trace), the IDs of TU, and the UX walk of TU. Asks what a refused unique build, insert and update leave in the file and in the index list, and which rows the unique index admits.",
            p =>
            {
                CreateOut(p, file, Dao.LangGeneral, Dao.Version120);
                Tr(p, "CREATE TABLE T (ID LONG CONSTRAINT PK_T PRIMARY KEY, K TEXT(40))");
                Insert(p, "db", "T", ["ID", "K"], [.. first10.Select((v, i) => new[] { A.I4(i + 1), Units(v) })]);
                CloseKeep(p, "rows-no-index");
                StepOut(p, file, "failed-unique-k", s => Tr(s, "CREATE UNIQUE INDEX UX_K ON T (K)"));
                StepOut(p, file, "unique-k-id", s => Tr(s, "CREATE UNIQUE INDEX UX_KID ON T (K, ID)"));
                StepOut(p, file, "tu-two-rows", s =>
                {
                    Tr(s, "CREATE TABLE TU (ID LONG CONSTRAINT PK_TU PRIMARY KEY, K TEXT(40))");
                    Tr(s, "CREATE UNIQUE INDEX UX ON TU (K)");
                    Tr(s, "INSERT INTO TU (ID, K) VALUES (1, 'a')");
                    Tr(s, "INSERT INTO TU (ID, K) VALUES (2, 'b')");
                });
                StepOut(p, file, "tu-refused-insert", s => Tr(s, "INSERT INTO TU (ID, K) VALUES (3, 'A')"));
                StepOut(p, file, "tu-refused-update", s => Tr(s, "UPDATE TU SET K = 'A' WHERE ID = 2"));
                StepOut(p, file, "tu-null-1", s => Tr(s, "INSERT INTO TU (ID, K) VALUES (4, NULL)"));
                StepOut(p, file, "tu-null-2", s => Tr(s, "INSERT INTO TU (ID, K) VALUES (5, NULL)"));
                OpenOut(p, file, true);
                IndexMeta(p, "T");
                IndexMeta(p, "TU");
                Walk(p, "db", "T", "UX_K", false, 24);
                ReadSql(p, "SELECT ID FROM TU ORDER BY ID", 16, 1);
                Walk(p, "db", "TU", "UX", false, 16);
                p.FinishLite("db");
            });
    }

    private static Probe ImageDropRebuild()
    {
        string file = "scratch-fixfs-image-drop-rebuild.accdb";
        string?[] values = [.. Pair24];
        int n = values.Length + 8;
        return new Probe(Card, "sort3b-image-drop-rebuild", S3("Text levels", "Traversal consequences"),
            "Repairs r-sort-3b/image-drop-rebuild: the intermediate files[...] captures were size-only (unread: IOException 0x80070020, file in use), so the images after drop, rebuild, delete and reinsert were not retained; the statements ran with no options. " +
            $"New General ACCDB. T(ID Long primary key, K TEXT(40)) with the first {values.Length} pair-corpus values. Each of these steps is its own read/write open of the closed file with dbFailOnError, DBEngine.Errors after each statement and an image kept after the close: CREATE INDEX IX_K ON T (K); DROP INDEX IX_K; CREATE INDEX IX_K again; DROP IX_K and CREATE as IX_K2; copy the even IDs to TB and DELETE them from T; INSERT the even IDs again in descending ID order; CREATE INDEX IX_KD ON T (K DESC). The IX_K walk is read after the first build and after the rebuild, the IX_K2 walk after the reinsert and the IX_KD walk after the last step (each in the same open, before the close), with the refreshed index metadata at the end. Asks what drop, rebuild, delete and reinsert change in the file and whether a rebuilt or maintained index lists ties in the same order as the original build.",
            p =>
            {
                CreateOut(p, file, Dao.LangGeneral, Dao.Version120);
                Tr(p, "CREATE TABLE T (ID LONG CONSTRAINT PK_T PRIMARY KEY, K TEXT(40))");
                Insert(p, "db", "T", ["ID", "K"], [.. values.Select((v, i) => new[] { A.I4(i + 1), Units(v) })]);
                CloseKeep(p, "rows-no-index");
                StepOut(p, file, "create-ix-k", s =>
                {
                    Tr(s, "CREATE INDEX IX_K ON T (K)");
                    Walk(s, "db", "T", "IX_K", false, n);
                });
                StepOut(p, file, "drop-ix-k", s => Tr(s, "DROP INDEX IX_K ON T"));
                StepOut(p, file, "recreate-ix-k", s =>
                {
                    Tr(s, "CREATE INDEX IX_K ON T (K)");
                    Walk(s, "db", "T", "IX_K", false, n);
                });
                StepOut(p, file, "rename-by-rebuild", s =>
                {
                    Tr(s, "DROP INDEX IX_K ON T");
                    Tr(s, "CREATE INDEX IX_K2 ON T (K)");
                });
                StepOut(p, file, "delete-even", s =>
                {
                    Tr(s, "CREATE TABLE TB (ID LONG CONSTRAINT PK_TB PRIMARY KEY, K TEXT(40))");
                    Tr(s, "INSERT INTO TB (ID, K) SELECT ID, K FROM T WHERE ID MOD 2 = 0");
                    Tr(s, "DELETE FROM T WHERE ID MOD 2 = 0");
                });
                StepOut(p, file, "reinsert-even-desc", s =>
                {
                    Tr(s, "INSERT INTO T (ID, K) SELECT ID, K FROM TB ORDER BY ID DESC");
                    Walk(s, "db", "T", "IX_K2", false, n);
                });
                StepOut(p, file, "ix-kd", s =>
                {
                    Tr(s, "CREATE INDEX IX_KD ON T (K DESC)");
                    Walk(s, "db", "T", "IX_KD", false, n);
                    IndexMeta(s, "T");
                });
                p.FinishLite();
            });
    }

    private static Probe ExistingNulls()
    {
        string file = Fx("sort3b-existing-nulls") + ".accdb";
        // Base tables: no value duplicates, only Null-bearing repeats, so a refusal cannot come from real duplicates.
        (string Base, string[] Rows)[] bases =
        [
            ("BK", ["Null, 1", "Null, 2", "Null, 3", "'a', 4", "'b', 5", "'', 6", "'c', 7"]),
            ("BKN", ["Null, Null", "Null, Null", "Null, 1", "Null, 1", "'a', Null", "'a', Null", "'', Null", "'', Null", "'a', 1", "'b', 2"]),
            ("BN", ["'x1', Null", "'x2', Null", "'x3', Null", "'a', 4", "'b', 5", "'', 6", "'c', 7"]),
        ];
        const string Alter = "ALTER TABLE {T} ALTER COLUMN K TEXT(20) NOT NULL";
        (string Tag, string Base, string? Pre, string? Ddl, string? WalkIx)[] variants =
        [
            ("uk", "BK", null, "CREATE UNIQUE INDEX UX ON {T} (K)", null),
            ("uki", "BK", null, "CREATE UNIQUE INDEX UX ON {T} (K) WITH IGNORE NULL", null),
            ("dk", "BK", null, "CREATE INDEX IX ON {T} (K) WITH DISALLOW NULL", null),
            ("pk", "BK", null, "CREATE INDEX IX ON {T} (K) WITH PRIMARY", null),
            ("pkc", "BK", null, "ALTER TABLE {T} ADD CONSTRAINT PK_X PRIMARY KEY (K)", null),
            ("ukni", "BKN", null, "CREATE UNIQUE INDEX UX ON {T} (K, N) WITH IGNORE NULL", null),
            ("kni", "BKN", null, "CREATE INDEX IX ON {T} (K, N) WITH IGNORE NULL", "IX"),
            ("dkn", "BKN", null, "CREATE INDEX IX ON {T} (K, N) WITH DISALLOW NULL", null),
            ("ukn", "BKN", null, "CREATE UNIQUE INDEX UX ON {T} (K, N)", null),
            ("uni", "BN", null, "CREATE UNIQUE INDEX UX ON {T} (N) WITH IGNORE NULL", null),
            ("alt", "BKN", Alter, null, null),
            ("alt-ukd", "BKN", Alter, "CREATE UNIQUE INDEX UX ON {T} (K, N) WITH DISALLOW NULL", null),
            ("alt-pk", "BKN", Alter, "CREATE INDEX IX ON {T} (K) WITH PRIMARY", null),
        ];
        return new Probe(Card, "sort3b-existing-nulls-isolated", S3("Null policy / aliases", "Compound framing"),
            "Repairs r-sort-3b/index-on-existing-nulls: UX1, UX2, UX3 and UX6 failed with 3022 because the data held real value duplicates (empty x6, a x6, N = 1 x6), so they did not isolate Null handling; IX7 WITH PRIMARY failed (3283) because X already had a primary key; the statements shared two tables, so a refusal could mask the next; after ALTER COLUMN K NOT NULL returned hr 0 nothing read K's Required state; and the walks of IX4 and UX6 walked indexes whose build had failed (0x800A0ED8, a storage-order walk). " +
            "Fixture (General ACCDB). Base tables with no value duplicates, only Null-bearing repeats: BK(K, N) with K Null x3 and a, b, empty, c; BKN(K, N) with (Null,Null) x2, (Null,1) x2, (a,Null) x2, (empty,Null) x2, (a,1), (b,2); BN(K, N) with N Null x3 and 4, 5, 6, 7. Every statement below runs on its own fresh key-less copy (CREATE TABLE E_<tag> (K TEXT(20), N Long) then INSERT..SELECT from its base), in its own read/write open with dbFailOnError, DBEngine.Errors after it and an image kept after the close: unique (K), unique (K) IGNORE NULL, non-unique (K) DISALLOW NULL and (K) WITH PRIMARY, ADD CONSTRAINT PRIMARY KEY (K) on BK; unique (K, N) IGNORE NULL, non-unique (K, N) IGNORE NULL (walked after the build), non-unique (K, N) DISALLOW NULL, plain unique (K, N) on BKN; unique (N) IGNORE NULL on BN; and on BKN ALTER COLUMN K NOT NULL alone, then followed by unique (K, N) DISALLOW NULL and by (K) WITH PRIMARY. After each the refreshed field list (Required), the number of Null K rows, the row count and the refreshed index metadata are read; no index whose build was refused is walked.",
            p =>
            {
                CreateOut(p, file, Dao.LangGeneral, Dao.Version120);
                foreach ((string b, string[] rows) in bases)
                {
                    Tr(p, $"CREATE TABLE {b} (K TEXT(20), N LONG)");
                    foreach (string row in rows)
                    {
                        Tr(p, $"INSERT INTO {b} (K, N) VALUES ({row})");
                    }
                }
                CloseKeep(p, "base-tables");
                foreach ((string tag, string b, string? pre, string? ddl, string? walkIx) in variants)
                {
                    string t = "E_" + tag.Replace("-", "_", StringComparison.Ordinal);
                    StepOut(p, file, "variant-" + tag, s =>
                    {
                        Tr(s, $"CREATE TABLE {t} (K TEXT(20), N LONG)");
                        Tr(s, $"INSERT INTO {t} (K, N) SELECT K, N FROM {b}");
                        if (pre is not null)
                        {
                            Tr(s, pre.Replace("{T}", t, StringComparison.Ordinal));
                            FieldsOf(s, "db", t, "af" + t);
                            ReadSql(s, $"SELECT COUNT(*) FROM {t} WHERE K IS NULL", 2, 1);
                        }
                        if (ddl is not null)
                        {
                            Tr(s, ddl.Replace("{T}", t, StringComparison.Ordinal));
                        }
                        FieldsOf(s, "db", t, "bf" + t);
                        ReadSql(s, $"SELECT COUNT(*) FROM {t}", 2, 1);
                        IndexMeta(s, t);
                        if (walkIx is not null)
                        {
                            Walk(s, "db", t, walkIx, false, 20);
                        }
                    });
                }
                p.FinishLite();
            })
        { CreatesFixture = true };
    }

    private static Probe DaoIndexFlags()
    {
        string file = Fx("sort3b-dao-index-flags") + ".accdb";
        (string Table, string[] Fields, bool Primary, bool Unique, bool Required, bool Ignore)[] defs =
        [
            ("DX1", ["K"], true, false, false, true),
            ("DX2", ["K", "N"], true, false, false, false),
            ("DX3", ["K", "N"], false, true, false, true),
            ("DX4", ["K", "N"], false, true, true, false),
            ("DX5", ["K"], false, false, false, true),
            ("DX6", ["K"], false, false, true, false),
            ("DX7", ["K", "N"], false, false, true, true),
        ];
        string[] tuples = ["NULL, NULL", "'a', NULL", "'a', 1", "NULL, 1", "'a', 1", "NULL, NULL"];
        return new Probe(Card, "sort3b-dao-index-flags", S3("Null policy / aliases", "Compound framing"),
            "Repairs r-sort-3b/dao-index-flags: after CREATE TABLE through Execute, TableDefs.Item(\"DX2\" to \"DX7\") failed with 0x800A0CC1 because TableDefs was not refreshed, so only DX1 got an index and 6 of the 7 flag variants were never exercised; DX1's Primary read back as 0; the inserts ran with no options. " +
            $"Fixture (General ACCDB). {defs.Length} tables (S Counter, K TEXT(20), N Long) with no key, each variant in its own read/write open of the closed file with an image kept after it: the table is created (dbFailOnError), TableDefs is refreshed before Item, DAO index IX is created with TableDef.CreateIndex, one CreateField per column and the properties (Primary, Unique, Required, IgnoreNulls): (K) primary+IgnoreNulls; (K, N) primary; (K, N) unique+IgnoreNulls; (K, N) unique+Required; (K) IgnoreNulls; (K) Required; (K, N) Required+IgnoreNulls. DBEngine.Errors is read after Indexes.Append, Indexes is refreshed, then six INSERTs per table over the tuples (Null, Null), (a, Null), (a, 1), (Null, 1), (a, 1), (Null, Null), one per statement with dbFailOnError and DBEngine.Errors after each; then the surviving S of the table and its refreshed index metadata (Primary, Unique, Required, IgnoreNulls, DistinctCount, fields) are read. Asks which property combinations DAO admits on an empty table, what each reads back as, and which inserts each combination refuses.",
            p =>
            {
                CreateOut(p, file, Dao.LangGeneral, Dao.Version120);
                CloseKeep(p, "created");
                foreach ((string table, string[] fields, bool primary, bool unique, bool required, bool ignore) in defs)
                {
                    StepOut(p, file, table.ToLowerInvariant(), s =>
                    {
                        Tr(s, $"CREATE TABLE {table} (S COUNTER, K TEXT(20), N LONG)");
                        DaoIndex(s, table, "IX", fields, primary, unique, required, ignore, "df" + table);
                        foreach (string t in tuples)
                        {
                            Tr(s, $"INSERT INTO {table} (K, N) VALUES ({t})");
                        }
                        ReadSql(s, $"SELECT S FROM {table} ORDER BY S", 16, 1);
                        IndexMeta(s, table);
                    });
                }
                p.FinishLite();
            })
        { CreatesFixture = true };
    }

    private static Probe MemoIndexDdl()
    {
        string file = "scratch-fixfs-memo-index-ddl.accdb";
        // No two values are equal under General compare; groups that share their first 255 units (A x255 + x, + y, A x256 + x;
        // e-acute x256 + p and + q) collide only if the key is cut at 255 units. A x254 + B and + C differ inside 255 units.
        string[] memo =
        [
            "a", "b", Rep("A", 255), Rep("A", 255) + "x", Rep("A", 255) + "y", Rep("A", 256) + "x", Rep("A", 254) + "B", Rep("A", 254) + "C",
            Rep(X(0xE9), 256) + "p", Rep(X(0xE9), 256) + "q",
        ];
        (string Tag, string Ddl)[] ddl =
        [
            ("ix-mk", "CREATE INDEX IX ON {T} (MK)"),
            ("ux-mk", "CREATE UNIQUE INDEX UX ON {T} (MK)"),
            ("ix-mkd", "CREATE INDEX IX ON {T} (MK DESC)"),
            ("ix-tkmk", "CREATE INDEX IX ON {T} (TK, MK)"),
            ("ix-mktk", "CREATE INDEX IX ON {T} (MK, TK)"),
            ("uq-mk", "ALTER TABLE {T} ADD CONSTRAINT UQ_MK UNIQUE (MK)"),
            ("ix-tk", "CREATE INDEX IX ON {T} (TK)"),
        ];
        (string Tag, bool Primary, bool Unique)[] dao = [("dao-nonunique", false, false), ("dao-unique", false, true), ("dao-primary", true, false)];
        (string Name, string Create)[] own =
        [
            ("MI3", "CREATE TABLE MI3 (MK MEMO CONSTRAINT PK_MI3 PRIMARY KEY)"),
            ("MI4", "CREATE TABLE MI4 (MK MEMO, CONSTRAINT UQ_MI4 UNIQUE (MK))"),
            ("MI5", "CREATE TABLE MI5 (MK MEMO, CONSTRAINT PK_MI5 PRIMARY KEY (MK))"),
        ];
        return new Probe(Card, "sort3b-memo-index-ddl", S3("Length510 boundary", "Null policy / aliases"),
            "Repairs r-sort-3b/memo-index-ddl: the memo set could not show 255-unit truncation: it holds an exact duplicate (A x255 + x at IDs 7 and 15) and the eight rows MI took include the case-equal pair a and A, so the unique-index refusals (3022) came from real duplicates; and every statement ran on one shared table, so an earlier index or refusal could affect the next. " +
            $"New General ACCDB. MI(ID Long primary key, MK MEMO, TK TEXT(255)) holds {memo.Length} values that are all different under General compare: a, b, A x255, A x255 + x, A x255 + y, A x256 + x, A x254 + B, A x254 + C, e-acute x256 + p and + q (TK is MK cut to its first 255 units). Each of {ddl.Length} statements runs on its own fresh copy of MI (CREATE TABLE and INSERT..SELECT), in its own read/write open with dbFailOnError, DBEngine.Errors after it and an image kept after the close: CREATE INDEX and CREATE UNIQUE INDEX on MK, MK DESC, (TK, MK) and (MK, TK); ADD CONSTRAINT UNIQUE (MK); and CREATE INDEX on TK as the control. Then, each on its own copy, DAO TableDef.CreateIndex with a field MK appended to a non-unique, to a unique and to a primary index (TableDefs refreshed before Item, DBEngine.Errors after Indexes.Append), and the three tables MI3, MI4 and MI5 whose MEMO column is a primary key or has a unique constraint (column and table form). The refreshed index metadata of every table is read after its step. Asks whether a memo column can be a native key in any route, which refusal each route gives, and whether the key is cut at 255 units.",
            p =>
            {
                CreateOut(p, file, Dao.LangGeneral, Dao.Version120);
                Tr(p, "CREATE TABLE MI (ID LONG CONSTRAINT PK_MI PRIMARY KEY, MK MEMO, TK TEXT(255))");
                Insert(p, "db", "MI", ["ID", "MK", "TK"], [.. memo.Select((v, i) => new[] { A.I4(i + 1), Units(v), Units(v.Length > 255 ? v[..255] : v) })]);
                CloseKeep(p, "mi-rows-no-index");
                foreach ((string tag, string sql) in ddl)
                {
                    string t = "M_" + tag.Replace("-", "_", StringComparison.Ordinal);
                    StepOut(p, file, tag, s =>
                    {
                        CopyMi(s, t);
                        Tr(s, sql.Replace("{T}", t, StringComparison.Ordinal));
                        IndexMeta(s, t);
                    });
                }
                foreach ((string tag, bool primary, bool unique) in dao)
                {
                    string t = "M_" + tag.Replace("-", "_", StringComparison.Ordinal);
                    StepOut(p, file, tag, s =>
                    {
                        CopyMi(s, t);
                        DaoIndex(s, t, "DX_MK", ["MK"], primary, unique, required: false, ignoreNulls: false, tag: "dm");
                        IndexMeta(s, t);
                    });
                }
                foreach ((string name, string create) in own)
                {
                    StepOut(p, file, name.ToLowerInvariant(), s =>
                    {
                        Tr(s, create);
                        IndexMeta(s, name);
                    });
                }
                p.FinishLite();
            });
    }

    private static void CopyMi(P p, string table)
    {
        Tr(p, $"CREATE TABLE {table} (ID LONG CONSTRAINT PK_{table} PRIMARY KEY, MK MEMO, TK TEXT(255))");
        Tr(p, $"INSERT INTO {table} (ID, MK, TK) SELECT ID, MK, TK FROM MI");
    }

    // ================================================================================================
    // Shared helpers

    private static string X(params object[] parts)
    {
        var sb = new System.Text.StringBuilder();
        foreach (object part in parts)
        {
            if (part is int cp)
            {
                sb.Append(cp < 0x10000 ? ((char)cp).ToString() : char.ConvertFromUtf32(cp));
            }
            else
            {
                sb.Append((string)part);
            }
        }
        return sb.ToString();
    }

    private static string Rep(string unit, int count) => string.Concat(Enumerable.Repeat(unit, count));

    private static Arg Units(string? units) => units is null ? A.Null : A.BstrUnits(units);

    /// <summary>Execute with dbFailOnError, then DBEngine.Errors, so a refusal is captured with its number on this statement.</summary>
    private static void Tr(P p, string sql, string db = "db") =>
        p.Call(db, "Execute", A.Str(sql), A.I4(Dao.FailOnError)).Errors();

    /// <summary>A mutation statement of a step: dbFailOnError, RecordsAffected, then DBEngine.Errors.</summary>
    private static void Sql(P p, params string[] sqls)
    {
        foreach (string sql in sqls)
        {
            p.Call("db", "Execute", A.Str(sql), A.I4(Dao.FailOnError)).Get("db", "RecordsAffected").Errors();
        }
    }

    /// <summary>Snapshot recordset read to EOF; the bind name carries the operation count so every query is distinct.</summary>
    private static void ReadSql(P p, string sql, int maxRows, int maxFields, string db = "db")
    {
        string tag = "q" + p.Ops.Count;
        p.OpenRs(tag, db, sql, Dao.OpenSnapshot).ReadAll(tag, maxRows, maxFields).Call(tag, "Close").Release(tag);
    }

    /// <summary>TableDefs is refreshed before Item, so a table made by DDL in this session is found.</summary>
    private static void TdOpen(P p, string db, string table, string bind)
    {
        string tds = bind + "_tds";
        p.GetBind(tds, db, "TableDefs").Call(tds, "Refresh").Item(bind, tds, A.Str(table)).Release(tds);
    }

    private static void FieldsOf(P p, string db, string table, string bind)
    {
        string fl = bind + "_fl", fd = bind + "_fd";
        TdOpen(p, db, table, bind);
        p.GetBind(fl, bind, "Fields").Call(fl, "Refresh").Get(fl, "Count")
            .Each(fl, fd, 64, f => f.Get(fd, "Name").Get(fd, "Type").Get(fd, "Size").Get(fd, "Attributes").Get(fd, "OrdinalPosition").Get(fd, "Required").Get(fd, "CollatingOrder"))
            .Release(fl).Release(bind);
    }

    /// <summary>One field's Type, Size, Attributes and every property (Precision and Scale for a Decimal).</summary>
    private static void FieldProps(P p, string db, string table, string field, string tag)
    {
        TdOpen(p, db, table, tag + "t");
        p.GetBind(tag + "f", tag + "t", "Fields").Item(tag + "d", tag + "f", A.Str(field)).Get(tag + "d", "Type").Get(tag + "d", "Size").Get(tag + "d", "Attributes")
            .GetBind(tag + "p", tag + "d", "Properties").Each(tag + "p", tag + "q", 48, b => b.Get(tag + "q", "Name").Get(tag + "q", "Value"))
            .Release(tag + "p").Release(tag + "d").Release(tag + "f").Release(tag + "t");
    }

    /// <summary>The user-table view: attributes, fields, indexes and the rows of one named table (no system table).</summary>
    private static void TableView(P p, string db, string table)
    {
        TdOpen(p, db, table, "tv");
        p.Get("tv", "Name").Get("tv", "Attributes").Get("tv", "RecordCount")
            .GetBind("tvf", "tv", "Fields").Call("tvf", "Refresh")
            .Each("tvf", "tvd", 64, f => f.Get("tvd", "Name").Get("tvd", "Type").Get("tvd", "Size").Get("tvd", "Attributes").Get("tvd", "OrdinalPosition").Get("tvd", "Required").Get("tvd", "CollatingOrder"))
            .Release("tvf")
            .GetBind("tvx", "tv", "Indexes").Call("tvx", "Refresh")
            .Each("tvx", "tvi", 16, i => i.Get("tvi", "Name").Get("tvi", "Primary").Get("tvi", "Unique").Get("tvi", "Foreign")
                .GetBind("tvg", "tvi", "Fields").Each("tvg", "tvh", 8, x => x.Get("tvh", "Name").Get("tvh", "Attributes")).Release("tvg"))
            .Release("tvx")
            .CallBind("tvr", "tv", "OpenRecordset", A.I4(Dao.OpenSnapshot)).ReadAll("tvr", 64, 32).Call("tvr", "Close").Release("tvr")
            .Release("tv");
    }

    private static void IndexStats(P p, string table, string db = "db")
    {
        TdOpen(p, db, table, "is_t");
        p.GetBind("is_x", "is_t", "Indexes").Call("is_x", "Refresh")
            .Each("is_x", "is_i", 8, b => b.Get("is_i", "Name").Get("is_i", "Primary").Get("is_i", "Unique").Get("is_i", "DistinctCount"))
            .Release("is_x").Release("is_t");
    }

    /// <summary>Every index of a table, TableDefs and Indexes refreshed first: flags, DistinctCount and fields.</summary>
    private static void IndexMeta(P p, string table, string db = "db")
    {
        TdOpen(p, db, table, "im_t");
        p.GetBind("im_x", "im_t", "Indexes").Call("im_x", "Refresh").Get("im_x", "Count")
            .Each("im_x", "im_i", 16, ib => ib.Get("im_i", "Name").Get("im_i", "Primary").Get("im_i", "Unique").Get("im_i", "Foreign")
                .Get("im_i", "Required").Get("im_i", "IgnoreNulls").Get("im_i", "DistinctCount")
                .GetBind("im_f", "im_i", "Fields").Each("im_f", "im_g", 8, xb => xb.Get("im_g", "Name").Get("im_g", "Attributes")).Release("im_f"))
            .Release("im_x").Release("im_t");
    }

    /// <summary>
    /// DAO route to an index: TableDefs refreshed, CreateIndex, one CreateField per column, the properties, Indexes.Append
    /// with DBEngine.Errors read right after it, then Indexes refreshed.
    /// </summary>
    private static void DaoIndex(P p, string table, string name, string[] fields, bool primary, bool unique, bool required, bool ignoreNulls, string tag)
    {
        TdOpen(p, "db", table, tag + "t");
        p.CallBind(tag + "x", tag + "t", "CreateIndex", A.Str(name)).GetBind(tag + "xf", tag + "x", "Fields");
        for (int i = 0; i < fields.Length; i++)
        {
            p.CallBind(tag + "f" + i, tag + "x", "CreateField", A.Str(fields[i])).Call(tag + "xf", "Append", A.Obj(tag + "f" + i)).Release(tag + "f" + i);
        }
        p.Put(tag + "x", "Primary", A.Bool(primary)).Put(tag + "x", "Unique", A.Bool(unique)).Put(tag + "x", "Required", A.Bool(required))
            .Put(tag + "x", "IgnoreNulls", A.Bool(ignoreNulls))
            .GetBind(tag + "xs", tag + "t", "Indexes").Call(tag + "xs", "Append", A.Obj(tag + "x")).Errors()
            .Call(tag + "xs", "Refresh").Get(tag + "xs", "Count")
            .Release(tag + "xs").Release(tag + "xf").Release(tag + "x").Release(tag + "t");
    }

    /// <summary>
    /// AddNew / Put ID / Put value / Update per row through a table-type recordset, DBEngine.Errors read after the value
    /// put and after the update (a refused put still lets Update store the ID-only row).
    /// </summary>
    private static void PutRows(P p, string table, string idField, string valField, IEnumerable<(int Id, Arg Value)> rows, string db = "db")
    {
        p.OpenRs("pr", db, table, Dao.OpenTable).GetBind("pfl", "pr", "Fields").Item("pfi", "pfl", A.Str(idField)).Item("pfv", "pfl", A.Str(valField));
        foreach ((int id, Arg value) in rows)
        {
            p.Call("pr", "AddNew").Put("pfi", "Value", A.I4(id)).Put("pfv", "Value", value).Errors().Call("pr", "Update").Errors();
        }
        p.Release("pfi").Release("pfv").Release("pfl").Call("pr", "Close").Release("pr");
    }

    /// <summary>Table-type traversal of one index to the end (or, backward, to the start), reading ID only.</summary>
    private static void Walk(P p, string db, string table, string? index, bool backward, int max)
    {
        p.OpenRs("w", db, table, Dao.OpenTable);
        if (index is not null)
        {
            p.Put("w", "Index", A.Str(index));
        }
        p.GetBind("wfl", "w", "Fields").Item("wid", "wfl", A.Str("ID"));
        if (backward)
        {
            p.Call("w", "MoveLast").Until("w", "BOF", max, b => b.Get("wid", "Value").Call("w", "MovePrevious"));
        }
        else
        {
            p.Call("w", "MoveFirst").Until("w", "EOF", max, b => b.Get("wid", "Value").Call("w", "MoveNext"));
        }
        p.Release("wid").Release("wfl").Call("w", "Close").Release("w");
    }

    /// <summary>AddNew / Put / Update per row through a table-type recordset; the first column is the Long ID.</summary>
    private static void Insert(P p, string db, string table, string[] cols, Arg[][] rows)
    {
        p.OpenRs("rs", db, table, Dao.OpenTable).GetBind("fl", "rs", "Fields");
        for (int c = 0; c < cols.Length; c++)
        {
            p.Item("f" + c, "fl", A.Str(cols[c]));
        }
        foreach (Arg[] row in rows)
        {
            p.Call("rs", "AddNew");
            for (int c = 0; c < cols.Length; c++)
            {
                p.Put("f" + c, "Value", row[c]);
            }
            p.Call("rs", "Update");
        }
        for (int c = 0; c < cols.Length; c++)
        {
            p.Release("f" + c);
        }
        p.Release("fl").Call("rs", "Close").Release("rs");
    }

    private static void OpenOut(P p, string file, bool readOnly) =>
        p.CallBind("db", "eng", "OpenDatabase", A.Out(file), A.Bool(false), A.Bool(readOnly));

    /// <summary>EngineLite, CreateDatabase of a named output file; the caller closes and keeps it.</summary>
    private static void CreateOut(P p, string file, string locale, int version) =>
        p.EngineLite().Get("eng", "Version").CallBind("db", "eng", "CreateDatabase", A.Out(file), A.Str(locale), A.I4(version)).Get("db", "Version").Get("db", "CollatingOrder");

    private static void CloseKeep(P p, string label) => p.Call("db", "Close").Release("db").Files(label, keep: true);

    /// <summary>One step on a named output file: open read/write, run the body, close, and keep the closed file's image.</summary>
    private static void StepOut(P p, string file, string label, Action<P> body)
    {
        OpenOut(p, file, false);
        body(p);
        CloseKeep(p, "after-" + label);
    }

    /// <summary>
    /// ADO DDL on a closed file (Provider ACE.OLEDB.12.0, Data Source set through Connection.Properties): the route
    /// for DECIMAL with explicit precision and scale and for WITH COMPRESSION, which DAO DDL refuses.
    /// </summary>
    private static void AdoDdl(P p, Arg path, params string[] sqls)
    {
        p.Create("ADODB.Connection", "cn").Put("cn", "Provider", A.Str(Ace))
            .GetBind("cnp", "cn", "Properties").Item("cds", "cnp", A.Str("Data Source")).Put("cds", "Value", path)
            .Release("cds").Release("cnp").Call("cn", "Open");
        foreach (string sql in sqls)
        {
            p.Call("cn", "Execute", A.Str(sql));
        }
        p.Call("cn", "Close").Release("cn");
    }
}
