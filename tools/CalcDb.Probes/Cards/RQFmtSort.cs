namespace CalcDb.Probes.Cards;

/// <summary>
/// Follow-up probes for the qualification round (The Plan 8.1) of R-FMT-1, R-FMT-2, R-PRESERVE-1 and R-SORT-3. Each
/// probe's description names the branch and the deciding variant it asks; probes ask, they never assert. All of them
/// use the raw IDispatch lane on the reference build and only DAO (plus ADO DDL for Decimal columns, whose DAO DDL is
/// refused). Conventions, from the defects found in r-fix-fmt-sort: a statement whose refusal matters runs with
/// dbFailOnError, and a marker (DROP TABLE of a table that does not exist, which always fails) comes first, so the
/// DBEngine.Errors read after the statement shows that statement's own error or the marker's, never an older one;
/// TableDefs and Indexes are refreshed before they are read after DDL; Decimal columns are made with ADO DDL with
/// explicit precision and scale; fractional values are written as SQL literals, VT_R8, VT_CY or VT_I4, never as BSTR
/// text, because BSTR conversion depends on the user locale (7177). Fixtures are named <c>r-q-fmt-sort-*</c>; scratch
/// outputs start with <c>scratch-qfs-</c>, so they are never taken for fixtures. Every file is named explicitly (no
/// OpenDatabase, CreateDatabase, CompactDatabase or Connect with an empty name, no ODBC, no Requery with an argument).
/// </summary>
public sealed class RQFmtSort : ICard
{
    private const string Card = "r-q-fmt-sort";
    private const string Ace = "Microsoft.ACE.OLEDB.12.0";
    private const int Fail = Dao.FailOnError;
    private const string MarkerTable = "RqNoSuchMarker";

    private static readonly (string Ext, int Version)[] Formats = [("accdb", Dao.Version120), ("mdb", Dao.Version40)];

    private static string[] F1(params string[] b) => ["R-FMT-1", .. b];

    private static string[] F2(params string[] b) => ["R-FMT-2", .. b];

    private static string[] Pr(params string[] b) => ["R-PRESERVE-1", .. b];

    private static string[] S3(params string[] b) => ["R-SORT-3", "SORT-CHECK", .. b];

    private static string Fx(string name) => $"{Card}-{name}";

    private static string Scratch(string name, string ext) => $"scratch-qfs-{name}.{ext}";

    private static string Fmt(string ext) => ext.ToUpperInvariant();

    public IEnumerable<Probe> Probes()
    {
        // Fixtures first.
        foreach ((string ext, int version) in Formats)
        {
            yield return FxNoop(ext, version);
        }
        yield return FxFail();

        // R-FMT-1 and R-PRESERVE-1: opening leaves the file alone.
        foreach ((string ext, int version) in Formats)
        {
            yield return NoopRw(ext);
        }

        // R-FMT-1: width, row coordinates, calculated types, index enumeration.
        yield return Wide();
        foreach ((string ext, int version) in Formats)
        {
            yield return MaskBoundary(ext, version);
        }
        yield return CalcTypes();
        yield return CalcDependency();
        yield return IndexEnumOrder();

        // R-FMT-2: scalar boundaries, NUMERIC, extended date, text, binary, generated values, statistics.
        yield return MoneyTies();
        foreach ((string ext, int version) in Formats)
        {
            yield return DecimalGrid(ext, version);
            yield return ScalarBounds(ext, version);
            yield return TextUnits(ext, version);
        }
        yield return Dt2Ticks();
        yield return BinaryBytes();
        yield return AutoCompact();
        yield return AutoCancelFail();
        yield return AutoGuidRandom();
        foreach ((string ext, int version) in Formats)
        {
            yield return DcRules(ext, version);
        }

        // R-PRESERVE-1.
        foreach (string variant in new[] { "unique", "fk", "validation" })
        {
            yield return FailHash(variant);
        }
        yield return RollbackSize();
        yield return QueryProps();
        yield return RenameVariants();

        // R-SORT-3.
        yield return VCorpus("v64", "mdb", Dao.Version40);
        yield return VCorpus("v128", "accdb", Dao.Version120);
        yield return VCorpus("v256", "accdb", 256);
        yield return Controls();
        foreach ((string tag, string unit, string ext, int version) in new[]
        {
            ("a", "a", "accdb", Dao.Version120),
            ("e-acute", X(0xE9), "accdb", Dao.Version120),
            ("sharp-s", X(0xDF), "accdb", Dao.Version120),
            ("han", X(0x6F22), "accdb", Dao.Version120),
            ("a-mdb", "a", "mdb", Dao.Version40),
        })
        {
            yield return LenSeries(tag, unit, ext, version);
        }
        yield return MemoPrefix();
        foreach ((string ext, int version) in Formats)
        {
            yield return TieOrder(ext, version);
        }
        yield return LikeIndex();
        yield return NegZero();
        yield return GuidOrder();
        yield return LocaleExtra();
        yield return DaoIndexPrimary();
    }

    // ================================================================================================
    // Fixtures

    private static Probe FxNoop(string ext, int version)
    {
        string file = Fx($"noop-{ext}");
        return new Probe(Card, $"fx-noop-{ext}", F1("Fixtures and operations", "reopen"),
            $"Fixture ({Fmt(ext)}) for the no-op open probes: CreateDatabase(path, dbLangGeneral, {version}); T(ID Long primary key, A Text(20), M Memo, N Long) with index IX_A on A and 30 rows; saved query QSel. Closed and kept.",
            p =>
            {
                Begin(p, file + "." + ext, version);
                Run(p, "CREATE TABLE T (ID LONG CONSTRAINT PK_T PRIMARY KEY, A TEXT(20), M MEMO, N LONG)");
                Run(p, "CREATE INDEX IX_A ON T (A)");
                for (int i = 1; i <= 30; i++)
                {
                    Run(p, $"INSERT INTO T (ID, A, M, N) VALUES ({i}, 'row{i}', 'memo{i}', {i * 7})");
                }
                p.CallBind("qd", "db", "CreateQueryDef", A.Str("QSel"), A.Str("SELECT ID, A FROM T ORDER BY ID;")).Call("qd", "Close").Release("qd");
                p.FinishLite("db").Files("fixture-closed", keep: true);
            })
        { CreatesFixture = true };
    }

    private static Probe FxFail()
    {
        string file = Fx("fail-accdb");
        return new Probe(Card, "fx-fail-accdb", Pr("Capture and comparison", "failed preflight"),
            "Fixture (ACCDB) for the refused-statement and rollback probes: Nums (ID 1..60); P (ID 1..5); C (ID, PID, foreign key to P) with 3 rows; U (ID, U unique, N Text NOT NULL) with 5 rows; V (ID, P with ValidationRule > 0 and ValidationText) with 3 rows; Big (ID, T Text(255)) empty. Closed and kept.",
            p =>
            {
                Begin(p, file + ".accdb", Dao.Version120);
                Run(p, "CREATE TABLE Nums (ID LONG CONSTRAINT PK_Nums PRIMARY KEY)");
                for (int i = 1; i <= 60; i++)
                {
                    Run(p, $"INSERT INTO Nums (ID) VALUES ({i})");
                }
                Run(p, "CREATE TABLE P (ID LONG CONSTRAINT PK_P PRIMARY KEY)");
                for (int i = 1; i <= 5; i++)
                {
                    Run(p, $"INSERT INTO P (ID) VALUES ({i})");
                }
                Run(p, "CREATE TABLE C (ID LONG CONSTRAINT PK_C PRIMARY KEY, PID LONG, CONSTRAINT FK_C FOREIGN KEY (PID) REFERENCES P (ID))");
                Run(p, "INSERT INTO C (ID, PID) VALUES (1, 1)");
                Run(p, "INSERT INTO C (ID, PID) VALUES (2, 2)");
                Run(p, "INSERT INTO C (ID, PID) VALUES (3, 3)");
                Run(p, "CREATE TABLE U (ID LONG CONSTRAINT PK_U PRIMARY KEY, U LONG, N TEXT(10) NOT NULL)");
                Run(p, "CREATE UNIQUE INDEX UX_U ON U (U)");
                for (int i = 1; i <= 5; i++)
                {
                    Run(p, $"INSERT INTO U (ID, U, N) VALUES ({i}, {i * 10}, 'n{i}')");
                }
                Run(p, "CREATE TABLE V (ID LONG CONSTRAINT PK_V PRIMARY KEY, P LONG)");
                FieldPut(p, "V", "P", "ValidationRule", A.Str("> 0"));
                FieldPut(p, "V", "P", "ValidationText", A.Str("P must be positive"));
                for (int i = 1; i <= 3; i++)
                {
                    Run(p, $"INSERT INTO V (ID, P) VALUES ({i}, {i})");
                }
                Run(p, "CREATE TABLE Big (ID LONG CONSTRAINT PK_Big PRIMARY KEY, T TEXT(255))");
                p.FinishLite("db").Files("fixture-closed", keep: true);
            })
        { CreatesFixture = true };
    }

    // ================================================================================================
    // R-FMT-1 and R-PRESERVE-1: no-op opens

    private static Probe NoopRw(string ext)
    {
        string fixture = Fx($"noop-{ext}");
        return new Probe(Card, $"noop-rw-{ext}", [.. F1("reopen"), "R-PRESERVE-1", "no-op open"],
            $"Branch R-PRESERVE-1 no-op open / R-FMT-1 reopen ({Fmt(ext)}). A copy of the fixture is opened read/write, one element at a time, closed, and its whole-file hash and size kept after each step: (1) open and close; (2) the same again, as a repeat; (3) open, enumerate TableDefs, fields and QueryDefs, close; (4) open, read all rows of T through a dynaset, close; (5) open, open T as a table, MoveFirst, Edit and CancelUpdate, close. Asks which of these leave the file bytes unchanged, and whether the answer repeats.",
            p =>
            {
                p.EngineLite().Files("start", keep: true);
                Step(p, "open-close", _ => { });
                Step(p, "open-close-again", _ => { });
                Step(p, "enumerate", b =>
                {
                    b.GetBind("tds", "db", "TableDefs").Call("tds", "Refresh")
                        .Each("tds", "t", 24, t => t.Get("t", "Name").GetBind("tf", "t", "Fields").Get("tf", "Count").Release("tf"))
                        .Release("tds")
                        .GetBind("qds", "db", "QueryDefs").Call("qds", "Refresh")
                        .Each("qds", "q", 8, q => q.Get("q", "Name").Get("q", "SQL")).Release("qds");
                });
                Step(p, "read-dynaset", b => ReadSql(b, "SELECT * FROM T ORDER BY ID", 40, 6));
                Step(p, "edit-cancel", b =>
                {
                    b.OpenRs("rs", "db", "T", Dao.OpenTable).Call("rs", "MoveFirst").Call("rs", "Edit").Call("rs", "CancelUpdate").Call("rs", "Close").Release("rs");
                });
                p.Release("eng").FinishLite();
            })
        { Fixtures = [("main", fixture)] };
    }

    private static void Step(P p, string label, Action<P> body)
    {
        p.OpenDb(readOnly: false);
        body(p);
        p.Call("db", "Close").Release("db").Files("after-" + label, keep: true);
    }

    // ================================================================================================
    // R-FMT-1: width, historical row coordinates (public side)

    private static Probe Wide()
    {
        return new Probe(Card, "wide-accdb", F1("II-04 structural discrimination", "TableDef continuation"),
            "Branch R-FMT-1 II-04 (TableDef continuation, public side) on an ACCDB. Tables of Long columns with 254, 255 and 256 columns in total (the key included): each created by its own statement with dbFailOnError, then its Fields.Count, one row written to its first and last column, and read back. Then a 255-column Yes/No table (a row with only the key, then one column set True), and Text(255) tables with 4, 8, 16 and 32 columns, each with one row of 255-character values. Asks the column-count limit, the row-size refusal, and whether values and the Yes/No bitmap survive at the limit.",
            p =>
            {
                Begin(p, Scratch("wide", "accdb"), Dao.Version120);
                foreach (int n in new[] { 254, 255, 256 })
                {
                    string cols = string.Join(", ", Enumerable.Range(1, n - 1).Select(i => $"C{i} LONG"));
                    Act(p, $"CREATE TABLE W{n} (ID LONG CONSTRAINT PK_W{n} PRIMARY KEY, {cols})");
                }
                foreach (int n in new[] { 254, 255, 256 })
                {
                    FieldCount(p, $"W{n}", $"wc{n}");
                    Act(p, $"INSERT INTO W{n} (ID, C1, C{n - 1}) VALUES (1, 11, 22)");
                    ReadSql(p, $"SELECT ID, C1, C{n - 1} FROM W{n}", 4, 4);
                }
                string ycols = string.Join(", ", Enumerable.Range(1, 254).Select(i => $"Y{i} YESNO"));
                Act(p, $"CREATE TABLE WB (ID LONG CONSTRAINT PK_WB PRIMARY KEY, {ycols})");
                FieldCount(p, "WB", "wbc");
                Act(p, "INSERT INTO WB (ID) VALUES (1)");
                ReadSql(p, "SELECT ID, Y1, Y100, Y254 FROM WB", 4, 4);
                Act(p, "UPDATE WB SET Y100 = True WHERE ID = 1");
                ReadSql(p, "SELECT ID, Y1, Y99, Y100, Y101, Y254 FROM WB", 4, 8);
                string text255 = new('x', 255);
                foreach (int k in new[] { 4, 8, 16, 32 })
                {
                    string cols = string.Join(", ", Enumerable.Range(1, k).Select(i => $"T{i} TEXT(255)"));
                    Act(p, $"CREATE TABLE WT{k} (ID LONG CONSTRAINT PK_WT{k} PRIMARY KEY, {cols})");
                    string names = string.Join(", ", Enumerable.Range(1, k).Select(i => $"T{i}"));
                    string vals = string.Join(", ", Enumerable.Range(1, k).Select(_ => $"'{text255}'"));
                    Act(p, $"INSERT INTO WT{k} (ID, {names}) VALUES (1, {vals})");
                    ReadSql(p, $"SELECT ID, LEN(T1), LEN(T{k}) FROM WT{k}", 4, 4);
                }
                p.FinishLite("db");
            });
    }

    private static Probe MaskBoundary(string ext, int version)
    {
        return new Probe(Card, $"mask-boundary-{ext}", F2("Historical row coordinates", "Ordinary Boolean / Null"),
            $"Branch R-FMT-2 Historical row coordinates (public side) on a {Fmt(ext)}. Tables M7, M8, M9, M15, M16, M17 (key plus Long columns, so the column count is the number in the name, around the 8-bit mask boundaries): a row with every column set, and one row per column with only that column Null; all rows read back. Then on M8: add Yes/No, add Long, add Text(10), drop the middle column C3, set the Yes/No True, add a second Yes/No; and on M16: add a Long (17 columns), add a Yes/No; reading all rows after every step. Asks whether Null patterns, appended columns and a dropped column keep every old value in its place, and what an appended Yes/No reads for old rows.",
            p =>
            {
                Begin(p, Scratch("mask", ext), version);
                foreach (int n in new[] { 7, 8, 9, 15, 16, 17 })
                {
                    string cols = string.Join(", ", Enumerable.Range(1, n - 1).Select(i => $"C{i} LONG"));
                    Run(p, $"CREATE TABLE M{n} (ID LONG CONSTRAINT PK_M{n} PRIMARY KEY, {cols})");
                    string names = string.Join(", ", Enumerable.Range(1, n - 1).Select(i => $"C{i}"));
                    Run(p, $"INSERT INTO M{n} (ID, {names}) VALUES (1, {string.Join(", ", Enumerable.Range(1, n - 1).Select(i => i * 10))})");
                    for (int k = 1; k <= n - 1; k++)
                    {
                        Run(p, $"INSERT INTO M{n} (ID, {names}) VALUES ({100 + k}, {string.Join(", ", Enumerable.Range(1, n - 1).Select(i => i == k ? "Null" : (i * 10).ToString()))})");
                    }
                    ReadSql(p, $"SELECT * FROM M{n} ORDER BY ID", 24, n + 1);
                }
                foreach ((string table, string sql) in new[]
                {
                    ("M8", "ALTER TABLE M8 ADD COLUMN YB YESNO"),
                    ("M8", "ALTER TABLE M8 ADD COLUMN LL LONG"),
                    ("M8", "ALTER TABLE M8 ADD COLUMN TT TEXT(10)"),
                    ("M8", "ALTER TABLE M8 DROP COLUMN C3"),
                    ("M8", "UPDATE M8 SET YB = True WHERE ID = 1"),
                    ("M8", "ALTER TABLE M8 ADD COLUMN YC YESNO"),
                    ("M16", "ALTER TABLE M16 ADD COLUMN Z1 LONG"),
                    ("M16", "ALTER TABLE M16 ADD COLUMN ZY YESNO"),
                })
                {
                    Act(p, sql);
                    ReadSql(p, $"SELECT * FROM {table} ORDER BY ID", 24, 20);
                }
                p.FinishLite("db");
            });
    }

    // ================================================================================================
    // R-FMT-1: calculated columns (II-04 calculated dispatch)

    private static Probe CalcTypes()
    {
        (string Name, int Type, int Size, string Expr)[] specs =
        [
            ("Bool", 1, 0, "[A] > 3"),
            ("Byte", 2, 0, "[A]"),
            ("Int", 3, 0, "[A] * 2"),
            ("Long", 4, 0, "[A] * 100000"),
            ("Cur", 5, 0, "[A] * 1.5"),
            ("Sgl", 6, 0, "[A] / 3"),
            ("Dbl", 7, 0, "[A] / 3"),
            ("Date", 8, 0, "DateAdd('d', [A], #1/1/2000#)"),
            ("Text", 10, 255, "CStr([A]) & '-x'"),
            ("Memo", 12, 0, "String(300, 'm')"),
            ("Dec", 20, 0, "[A] / 3"),
        ];
        return new Probe(Card, "calc-types-accdb", F1("II-04 structural discrimination", "calculated Boolean/Numeric/Text/Memo"),
            "Branch R-FMT-1 II-04 calculated types (public side) on an ACCDB. For each result type (Yes/No, Byte, Integer, Long, Currency, Single, Double, Date, Text(255), Memo, Decimal) its own table CT<type>(ID Long, A Long, C calculated) is made through CreateTableDef, with Field.Expression put before the field is appended; each step's DBEngine.Errors follows a marker. Rows A = 5, Null and 0 are inserted and read back; C's Type, Size, Attributes and property list (Expression, ResultType where present) are read. Asks which result types ACE admits as calculated columns, what each reads back, and what the field reports.",
            p =>
            {
                Begin(p, Scratch("calc", "accdb"), Dao.Version120);
                foreach ((string name, int type, int size, string expr) in specs)
                {
                    string t = "CT" + name, b = "c" + name;
                    p.CallBind(b + "t", "db", "CreateTableDef", A.Str(t))
                        .CallBind(b + "i", b + "t", "CreateField", A.Str("ID"), A.I4(4)).GetBind(b + "l", b + "t", "Fields").Call(b + "l", "Append", A.Obj(b + "i"))
                        .CallBind(b + "a", b + "t", "CreateField", A.Str("A"), A.I4(4)).Call(b + "l", "Append", A.Obj(b + "a"));
                    if (size > 0)
                    {
                        p.CallBind(b + "c", b + "t", "CreateField", A.Str("C"), A.I4(type), A.I4(size));
                    }
                    else
                    {
                        p.CallBind(b + "c", b + "t", "CreateField", A.Str("C"), A.I4(type));
                    }
                    Mark(p);
                    p.Put(b + "c", "Expression", A.Str(expr)).Errors();
                    Mark(p);
                    p.Call(b + "l", "Append", A.Obj(b + "c")).Errors();
                    Mark(p);
                    p.GetBind("cts", "db", "TableDefs").Call("cts", "Append", A.Obj(b + "t")).Errors().Release("cts");
                    p.Release(b + "c").Release(b + "a").Release(b + "i").Release(b + "l").Release(b + "t");
                    Act(p, $"INSERT INTO {t} (ID, A) VALUES (1, 5)");
                    Act(p, $"INSERT INTO {t} (ID, A) VALUES (2, Null)");
                    Act(p, $"INSERT INTO {t} (ID, A) VALUES (3, 0)");
                    ReadSql(p, $"SELECT * FROM {t} ORDER BY ID", 8, 4);
                    FieldProps(p, "db", t, "C", "cp" + name);
                }
                p.FinishLite("db");
            });
    }

    private static Probe CalcDependency()
    {
        return new Probe(Card, "calc-dependency-accdb", F1("II-04 structural discrimination", "calculated dependency"),
            "Branch R-FMT-1 II-04 / R-FMT-2 Calculated wrappers (public side) on an ACCDB. CD(ID Long, A Long, B Text(20), C Text(255) calculated as [B] & '-' & CStr([A])) is made through CreateTableDef; row (1, 5, 'x') is inserted and read. Then one change at a time, each read back after it: A = 6; B = Null; A = Null; an UPDATE that sets C directly; ALTER TABLE DROP COLUMN A (a column C depends on); the field A renamed through Field.Name; Field C's Expression put to another text after creation. Asks when the stored result is recomputed, whether C can be written, and what is refused.",
            p =>
            {
                Begin(p, Scratch("calcdep", "accdb"), Dao.Version120);
                p.CallBind("t", "db", "CreateTableDef", A.Str("CD"))
                    .CallBind("fi", "t", "CreateField", A.Str("ID"), A.I4(4)).GetBind("fl", "t", "Fields").Call("fl", "Append", A.Obj("fi"))
                    .CallBind("fa", "t", "CreateField", A.Str("A"), A.I4(4)).Call("fl", "Append", A.Obj("fa"))
                    .CallBind("fb", "t", "CreateField", A.Str("B"), A.I4(10), A.I4(20)).Call("fl", "Append", A.Obj("fb"))
                    .CallBind("fc", "t", "CreateField", A.Str("C"), A.I4(10), A.I4(255)).Put("fc", "Expression", A.Str("[B] & '-' & CStr([A])"));
                Mark(p);
                p.Call("fl", "Append", A.Obj("fc")).Errors();
                Mark(p);
                p.GetBind("cts", "db", "TableDefs").Call("cts", "Append", A.Obj("t")).Errors().Release("cts")
                    .Release("fc").Release("fb").Release("fa").Release("fi").Release("fl").Release("t");
                Act(p, "INSERT INTO CD (ID, A, B) VALUES (1, 5, 'x')");
                ReadSql(p, "SELECT * FROM CD ORDER BY ID", 4, 4);
                foreach (string sql in new[]
                {
                    "UPDATE CD SET A = 6 WHERE ID = 1",
                    "UPDATE CD SET B = Null WHERE ID = 1",
                    "UPDATE CD SET A = Null WHERE ID = 1",
                    "UPDATE CD SET C = 'z' WHERE ID = 1",
                    "ALTER TABLE CD DROP COLUMN A",
                })
                {
                    Act(p, sql);
                    ReadSql(p, "SELECT * FROM CD ORDER BY ID", 4, 4);
                }
                TdOpen(p, "db", "CD", "cdt");
                p.GetBind("cdf", "cdt", "Fields").Call("cdf", "Refresh");
                Mark(p);
                p.Item("cdx", "cdf", A.Str("A")).Put("cdx", "Name", A.Str("A2")).Errors().Release("cdx");
                Mark(p);
                p.Item("cdy", "cdf", A.Str("C")).Put("cdy", "Expression", A.Str("[B] & '!'")).Errors().Release("cdy").Release("cdf").Release("cdt");
                ReadSql(p, "SELECT * FROM CD ORDER BY ID", 4, 4);
                FieldProps(p, "db", "CD", "C", "cdp");
                p.FinishLite("db");
            });
    }

    // ================================================================================================
    // R-FMT-1: index enumeration order

    private static Probe IndexEnumOrder()
    {
        return new Probe(Card, "index-enum-order-accdb", F1("Fixtures and operations", "index enumeration"),
            "Branch R-FMT-1 indexes (public side) on an ACCDB. T(ID Long primary key, A, B, C, D, E Long): indexes Z_ix, M_ix, a_ix, B_ix, _first and 0_ix are created in that order, each by its own statement, and the index list (names, Primary, Unique, Foreign, fields) is read after every creation, again after Indexes.Refresh, and after a read-only reopen. Asks whether TableDef.Indexes enumerates in name order or creation order, and how case, an underscore and a digit sort.",
            p =>
            {
                string file = Scratch("idxenum", "accdb");
                Begin(p, file, Dao.Version120);
                Run(p, "CREATE TABLE T (ID LONG CONSTRAINT PK_T PRIMARY KEY, A LONG, B LONG, C LONG, D LONG, E LONG)");
                IndexMeta(p, "T");
                foreach ((string name, string col) in new[] { ("Z_ix", "A"), ("M_ix", "B"), ("a_ix", "C"), ("B_ix", "D"), ("_first", "E"), ("0_ix", "A") })
                {
                    Act(p, $"CREATE INDEX {name} ON T ({col})");
                    IndexMeta(p, "T");
                }
                p.Call("db", "Close").Release("db");
                p.CallBind("db", "eng", "OpenDatabase", A.Out(file), A.Bool(false), A.Bool(true));
                IndexMeta(p, "T");
                p.FinishLite("db");
            });
    }

    // ================================================================================================
    // R-FMT-2: scalar boundaries

    private static Probe MoneyTies()
    {
        string[] literals = ["0.00005", "0.00015", "0.00025", "0.00035", "0.00045", "0.00055", "-0.00005", "-0.00015", "-0.00025", "0.00004999", "0.00005001", "922337203685477.58075", "-922337203685477.58085"];
        return new Probe(Card, "money-ties-accdb", F2("Scalar boundaries", "Money ties/overflow"),
            "Branch R-FMT-2 Scalar boundaries: Money ties and overflow, on an ACCDB. MT(ID Long, Fc Currency). The same 13 amounts (ties at the fifth decimal in both signs, values just either side of a tie, and the half-unit beyond both Currency limits) are written by three routes, each ID range separate: SQL numeric literals (IDs 1..), a table-recordset Field.Value put of VT_R8 (IDs 101..), and the SQL conversion CCur(literal) (IDs 201..). Each write has dbFailOnError or a marked Errors read. Asks whether the fifth decimal rounds half to even or half away, whether the route matters, and what overflows.",
            p =>
            {
                Begin(p, Scratch("money", "accdb"), Dao.Version120);
                Run(p, "CREATE TABLE MT (ID LONG CONSTRAINT PK_MT PRIMARY KEY, Fc CURRENCY)");
                for (int i = 0; i < literals.Length; i++)
                {
                    Act(p, $"INSERT INTO MT (ID, Fc) VALUES ({i + 1}, {literals[i]})");
                }
                p.OpenRs("pr", "db", "MT", Dao.OpenTable).GetBind("pfl", "pr", "Fields").Item("pfi", "pfl", A.Str("ID")).Item("pfv", "pfl", A.Str("Fc"));
                for (int i = 0; i < literals.Length; i++)
                {
                    Mark(p);
                    p.Call("pr", "AddNew").Put("pfi", "Value", A.I4(101 + i)).Put("pfv", "Value", A.R8(double.Parse(literals[i], System.Globalization.CultureInfo.InvariantCulture))).Errors().Call("pr", "Update").Errors();
                }
                p.Release("pfi").Release("pfv").Release("pfl").Call("pr", "Close").Release("pr");
                for (int i = 0; i < literals.Length; i++)
                {
                    Act(p, $"INSERT INTO MT (ID, Fc) VALUES ({201 + i}, CCur({literals[i]}))");
                }
                ReadSql(p, "SELECT ID, Fc, CStr(Fc) FROM MT ORDER BY ID", 48, 4);
                p.FinishLite("db");
            });
    }

    private static Probe DecimalGrid(string ext, int version)
    {
        (int P, int S)[] specs = [(1, 0), (2, 1), (5, 2), (18, 4), (28, 0), (28, 28), (28, 10), (29, 0), (38, 0), (38, 10)];
        string file = Scratch("decgrid", ext);
        string[] statements = [.. specs.Select(s => $"CREATE TABLE D{s.P}_{s.S} (ID INTEGER CONSTRAINT PK_D{s.P}_{s.S} PRIMARY KEY, V DECIMAL({s.P},{s.S}))")];
        (string Table, string[] Values)[] inserts =
        [
            ("D1_0", ["9", "10", "0.5", "1.5", "2.5", "-0.4"]),
            ("D2_1", ["9.9", "10.0", "0.25", "0.35", "0.05", "0.15"]),
            ("D5_2", ["999.99", "1000", "12.345", "12.355", "12.365", "0.005", "0.015", "0.025", "-0.00", "0.001"]),
            ("D18_4", ["-12345678901234.5678", "99999999999999.9999", "100000000000000", "0.00005", "0.00015"]),
            ("D28_28", ["0.5", "1", "0.1234567890123", "-0.5"]),
            ("D28_10", ["123456789.0123456789", "1234567890123456789", "0.00000000005"]),
        ];
        (string Table, string Text)[] integers =
        [
            ("D28_0", "9999999999999999999999999999"),
            ("D28_0", "10000000000000000000000000000"),
            ("D28_0", "79228162514264337593543950335"),
            ("D29_0", "79228162514264337593543950335"),
            ("D38_0", "12345678901234567890123456789012345678"),
        ];
        return new Probe(Card, $"decimal-grid-{ext}", F2("NUMERIC", "Scalar boundaries"),
            $"Branch R-FMT-2 NUMERIC (precision/scale endpoints, public side) on a {Fmt(ext)}. After CreateDatabase and close, ADO DDL (Provider {Ace}) creates one table per precision and scale: DECIMAL(1,0), (2,1), (5,2), (18,4), (28,0), (28,28), (28,10), (29,0), (38,0), (38,10), each statement on its own so an ACE refusal of a form is an observation. Then DAO reads each field's Type, Size, Attributes and property list (Precision, Scale). Values are written as SQL numeric literals of at most 15 significant digits (ties at the next digit, one unit over the precision, negative zero as -0.00, a value needing more scale) and the long integers as BSTR integer text, each with dbFailOnError and a marked Errors read; rows are read through SQL CStr. Asks which forms ACE creates, and whether an over-scale value rounds (which way at a tie) or is refused.",
            p =>
            {
                Begin(p, file, version);
                p.Call("db", "Close").Release("db");
                AdoDdl(p, A.Out(file), statements);
                p.CallBind("db", "eng", "OpenDatabase", A.Out(file), A.Bool(false), A.Bool(false));
                foreach ((int pr, int sc) in specs)
                {
                    FieldProps(p, "db", $"D{pr}_{sc}", "V", $"gp{pr}x{sc}");
                }
                foreach ((string table, string[] values) in inserts)
                {
                    for (int i = 0; i < values.Length; i++)
                    {
                        Act(p, $"INSERT INTO {table} (ID, V) VALUES ({i + 1}, {values[i]})");
                    }
                    ReadSql(p, $"SELECT ID, CStr(V) FROM {table} ORDER BY ID", 16, 2);
                }
                int next = 1;
                foreach ((string table, string text) in integers)
                {
                    p.OpenRs("ir", "db", table, Dao.OpenTable).GetBind("ifl", "ir", "Fields").Item("iid", "ifl", A.Str("ID")).Item("iv", "ifl", A.Str("V"));
                    Mark(p);
                    p.Call("ir", "AddNew").Put("iid", "Value", A.I4(next++)).Put("iv", "Value", A.Str(text)).Errors().Call("ir", "Update").Errors()
                        .Release("iid").Release("iv").Release("ifl").Call("ir", "Close").Release("ir");
                }
                foreach (string table in new[] { "D28_0", "D29_0", "D38_0" })
                {
                    ReadSql(p, $"SELECT ID, CStr(V) FROM {table} ORDER BY ID", 8, 2);
                }
                p.FinishLite("db");
            });
    }

    private static Probe ScalarBounds(string ext, int version)
    {
        bool big = ext == "accdb";
        return new Probe(Card, $"scalar-bounds-{ext}", F2("Scalar boundaries"),
            $"Branch R-FMT-2 Scalar boundaries (admission, coercion, rounding) on a {Fmt(ext)}. SB(ID Long, Fb Byte, Fi Integer, Fl Long, Fs Single, Fd Double{(big ? ", Fbig BigInt" : string.Empty)}), one value per row through a table recordset, each write with a marked Errors read. Fractions and out-of-range values: Byte 0.5, 1.5, 2.5, 255.4, 255.5, -0.4, -0.5; Integer -32769, 32768, 32767.4, 32767.5, -32768.5, -32768.6; Long 2147483647.4, 2147483647.5, 2147483648, -2147483648.5, -2147483648.6, 1.5, 2.5, 3.5 (all VT_R8); Single 3.4028235E38, 3.5E38, 1E-46, 1E-45, -3.5E38; Double 1.7976931348623157E308 and the BSTR text 1e309, -1e309, 5e-325{(big ? "; BigInt -1, and the BSTR integer text 9223372036854775807, 9223372036854775808, -9223372036854775808, -9223372036854775809" : string.Empty)}. Asks the rounding rule of fractional input (half to even or half away), which side of each range is refused, and what an overflow stores.",
            p =>
            {
                Begin(p, Scratch("sb", ext), version);
                Run(p, "CREATE TABLE SB (ID LONG CONSTRAINT PK_SB PRIMARY KEY, Fb BYTE, Fi SHORT, Fl LONG, Fs SINGLE, Fd DOUBLE)");
                if (big)
                {
                    Run(p, "ALTER TABLE SB ADD COLUMN Fbig BIGINT");
                }
                var rows = new List<(string Field, Arg Value)>();
                foreach (double v in new[] { 0.5, 1.5, 2.5, 255.4, 255.5, -0.4, -0.5 })
                {
                    rows.Add(("Fb", A.R8(v)));
                }
                foreach (double v in new[] { 32767.4, 32767.5, -32768.5, -32768.6 })
                {
                    rows.Add(("Fi", A.R8(v)));
                }
                rows.Add(("Fi", A.I4(-32769)));
                rows.Add(("Fi", A.I4(32768)));
                foreach (double v in new[] { 2147483647.4, 2147483647.5, 2147483648.0, -2147483648.5, -2147483648.6, 1.5, 2.5, 3.5 })
                {
                    rows.Add(("Fl", A.R8(v)));
                }
                foreach (double v in new[] { 3.4028235E38, 3.5E38, 1E-46, 1E-45, -3.5E38 })
                {
                    rows.Add(("Fs", A.R8(v)));
                }
                rows.Add(("Fd", A.R8(1.7976931348623157E308)));
                foreach (string s in new[] { "1e309", "-1e309", "5e-325" })
                {
                    rows.Add(("Fd", A.Str(s)));
                }
                if (big)
                {
                    rows.Add(("Fbig", A.I4(-1)));
                    foreach (string s in new[] { "9223372036854775807", "9223372036854775808", "-9223372036854775808", "-9223372036854775809" })
                    {
                        rows.Add(("Fbig", A.Str(s)));
                    }
                }
                p.OpenRs("pr", "db", "SB", Dao.OpenTable).GetBind("pfl", "pr", "Fields").Item("pfi", "pfl", A.Str("ID"));
                foreach (string field in rows.Select(r => r.Field).Distinct())
                {
                    p.Item("pf" + field, "pfl", A.Str(field));
                }
                int id = 1;
                foreach ((string field, Arg value) in rows)
                {
                    Mark(p);
                    p.Call("pr", "AddNew").Put("pfi", "Value", A.I4(id++)).Put("pf" + field, "Value", value).Errors().Call("pr", "Update").Errors();
                }
                foreach (string field in rows.Select(r => r.Field).Distinct())
                {
                    p.Release("pf" + field);
                }
                p.Release("pfi").Release("pfl").Call("pr", "Close").Release("pr");
                ReadSql(p, big ? "SELECT * FROM SB ORDER BY ID" : "SELECT * FROM SB ORDER BY ID", 64, 8);
                p.FinishLite("db");
            });
    }

    private static Probe Dt2Ticks()
    {
        string[] values =
        [
            "0001-01-01 00:00:00.0000000", "0001-01-01 00:00:00.0000001", "0001-01-01 00:00:00.00000005",
            "2024-01-02 03:04:05.1234567", "2024-01-02 03:04:05.12345675", "2024-01-02 03:04:05.12345665", "2024-01-02 03:04:05.12345674", "2024-01-02 03:04:05.12345676",
            "2024-01-02 03:04:05.99999995", "2024-01-02 03:04:05.99999994",
            "9999-12-31 23:59:59.9999999", "9999-12-31 23:59:59.99999995", "9999-12-31 23:59:59.99999999",
            "0000-12-31 23:59:59.9999999", "10000-01-01 00:00:00.0000000",
        ];
        return new Probe(Card, "dt2-ticks-accdb", F2("Extended Date/Time", "NUMERIC / GUID / extended date"),
            "Branch R-FMT-2 Extended Date/Time and R-SORT-3 extended date key (public side) on an ACCDB. DX(ID Long, V DATETIME2). Fifteen ISO strings (year 1 and 9999 endpoints, one tick, the final tick, eighth-digit ties and carries into the seconds, a value before year 1 and one after year 9999) are written by two routes, each its own ID range: a table-recordset Field.Value BSTR put (IDs 1..) and a SQL INSERT with the string literal (IDs 101..), each with a marked Errors read. Reads V as stored and through CStr, then CREATE INDEX IX_V and walks the index and SQL ORDER BY V. Asks the stored tick, the rounding at the eighth digit (and whether a refused put leaves Null), the range, and the key order.",
            p =>
            {
                Begin(p, Scratch("dt2", "accdb"), Dao.Version120);
                Run(p, "CREATE TABLE DX (ID LONG CONSTRAINT PK_DX PRIMARY KEY)");
                Run(p, "ALTER TABLE DX ADD COLUMN V DATETIME2");
                p.OpenRs("pr", "db", "DX", Dao.OpenTable).GetBind("pfl", "pr", "Fields").Item("pfi", "pfl", A.Str("ID")).Item("pfv", "pfl", A.Str("V"));
                for (int i = 0; i < values.Length; i++)
                {
                    Mark(p);
                    p.Call("pr", "AddNew").Put("pfi", "Value", A.I4(i + 1)).Put("pfv", "Value", A.Str(values[i])).Errors().Call("pr", "Update").Errors();
                }
                p.Release("pfi").Release("pfv").Release("pfl").Call("pr", "Close").Release("pr");
                for (int i = 0; i < values.Length; i++)
                {
                    Act(p, $"INSERT INTO DX (ID, V) VALUES ({101 + i}, '{values[i]}')");
                }
                ReadSql(p, "SELECT ID, V, CStr(V) FROM DX ORDER BY ID", 40, 3);
                Act(p, "CREATE INDEX IX_V ON DX (V)");
                Walk(p, "db", "DX", "IX_V", false, 40);
                ReadSql(p, "SELECT ID FROM DX ORDER BY V, ID", 40, 1);
                p.FinishLite("db");
            });
    }

    private static Probe TextUnits(string ext, int version)
    {
        string[] values =
        [
            "a" + X(0) + "b", X(0xD800), X(0xDC00), "a" + X(0xD800) + "b", X(0x10000), X(0xDC00, 0xD800), X(0xFEFF) + "x", X(1),
            Rep("A", 254) + X(0xD800), Rep("A", 253) + X(0x20000), "ab" + X(0x20000) + "cd",
        ];
        return new Probe(Card, $"text-units-{ext}", F2("Text grammar", "UTF-16 boundaries"),
            $"Branch R-FMT-2 Text grammar / R-SORT-3 UTF-16 boundaries (stored units) on a {Fmt(ext)}. TU(ID Long, K Text(255), M Memo): eleven values as counted BSTR units (embedded NUL, a lone high and a lone low surrogate, a lone surrogate between letters, a pair, a reversed pair, a leading U+FEFF, U+0001, 254 letters plus a lone high surrogate, 253 letters plus a pair, letters around a pair) written into both K and M through a table recordset. Reads back what the stored units are, which the display could not show: LEN, and AscW of units 1 to 3 through SQL, and the values through a recordset. Asks whether ACE stores each unit sequence as given (lone surrogates, NUL), and how LEN counts them.",
            p =>
            {
                Begin(p, Scratch("tu", ext), version);
                Run(p, "CREATE TABLE TU (ID LONG CONSTRAINT PK_TU PRIMARY KEY, K TEXT(255), M MEMO)");
                var rows = new List<Arg[]>();
                for (int i = 0; i < values.Length; i++)
                {
                    rows.Add([A.I4(i + 1), Units(values[i]), Units(values[i])]);
                }
                Insert(p, "db", "TU", ["ID", "K", "M"], [.. rows]);
                ReadSql(p, "SELECT ID, LEN(K), AscW(Mid(K, 1, 1)), IIf(LEN(K) >= 2, AscW(Mid(K, 2, 1)), Null), IIf(LEN(K) >= 3, AscW(Mid(K, 3, 1)), Null), LEN(M), AscW(Mid(M, 1, 1)) FROM TU ORDER BY ID", 16, 8);
                ReadSql(p, "SELECT ID, K, M FROM TU ORDER BY ID", 16, 4);
                p.FinishLite("db");
            });
    }

    private static Probe BinaryBytes()
    {
        byte[] all = [.. Enumerable.Range(0, 256).Select(i => (byte)i)];
        byte[] b255 = [.. Enumerable.Range(0, 255).Select(i => (byte)i)];
        byte[] pattern = [.. Enumerable.Range(0, 4000).Select(i => (byte)(i * 7))];
        return new Probe(Card, "binary-bytes-accdb", F2("Binary and OLE", "Ordinary Boolean / Null"),
            "Branch R-FMT-2 Binary and OLE (public side) on an ACCDB. BB(ID Long, B Binary(255), L LongBinary): row 1 B = bytes 0..254 and L = all 256 byte values; row 2 B = 255 zero bytes and L = one zero byte; row 3 B and L empty arrays; row 4 both Null; row 5 B = one 0xFF byte and L = a 4000-byte pattern; row 6 B = 256 bytes (over the field size, with a marked Errors read). Reads the arrays back through a recordset and the lengths through SQL. Asks whether every byte value round-trips, whether an empty array reads back as Null or empty, and what an over-long Binary does.",
            p =>
            {
                Begin(p, Scratch("bb", "accdb"), Dao.Version120);
                Run(p, "CREATE TABLE BB (ID LONG CONSTRAINT PK_BB PRIMARY KEY, B BINARY(255), L LONGBINARY)");
                p.OpenRs("pr", "db", "BB", Dao.OpenTable).GetBind("pfl", "pr", "Fields").Item("pfi", "pfl", A.Str("ID")).Item("pfb", "pfl", A.Str("B")).Item("pfl2", "pfl", A.Str("L"));
                (int Id, Arg? B, Arg? L)[] rows =
                [
                    (1, A.Bytes(b255), A.Bytes(all)),
                    (2, A.Bytes([.. new byte[255]]), A.Bytes(0)),
                    (3, A.Bytes(), A.Bytes()),
                    (4, null, null),
                    (5, A.Bytes(0xFF), A.Bytes(pattern)),
                    (6, A.Bytes([.. new byte[256]]), null),
                ];
                foreach ((int id, Arg? b, Arg? l) in rows)
                {
                    Mark(p);
                    p.Call("pr", "AddNew").Put("pfi", "Value", A.I4(id));
                    if (b is not null)
                    {
                        p.Put("pfb", "Value", b).Errors();
                    }
                    if (l is not null)
                    {
                        p.Put("pfl2", "Value", l).Errors();
                    }
                    p.Call("pr", "Update").Errors();
                }
                p.Release("pfi").Release("pfb").Release("pfl2").Release("pfl").Call("pr", "Close").Release("pr");
                ReadSql(p, "SELECT ID, B, L FROM BB ORDER BY ID", 8, 4);
                ReadSql(p, "SELECT ID, B IS NULL, L IS NULL FROM BB ORDER BY ID", 8, 3);
                p.FinishLite("db");
            });
    }

    // ================================================================================================
    // R-FMT-2: generated values

    private static Probe AutoCompact()
    {
        string file = Scratch("autocompact", "accdb");
        return new Probe(Card, "auto-compact-accdb", F2("Generated values"),
            "Branch R-FMT-2 Generated values (AutoNumber reuse and Compact & Repair) on an ACCDB. Three tables AuK, AuC, AuA (ID AutoNumber primary key, S Text) with ten rows each. AuK: rows 8..10 deleted, then an insert, @@IDENTITY read (control, before any compaction). AuC: rows 8..10 deleted, no insert. AuA: all rows deleted, no insert. The database is closed and DBEngine.CompactDatabase writes a work file; then in the compacted file one insert each into AuC and AuA (and AuK), @@IDENTITY read after each. Asks whether Compact & Repair resets the counter to the highest remaining key plus one, which is the competing explanation for the earlier result that deleting the tail did not reuse keys.",
            p =>
            {
                Begin(p, file, Dao.Version120);
                foreach (string t in new[] { "AuK", "AuC", "AuA" })
                {
                    Run(p, $"CREATE TABLE {t} (ID COUNTER CONSTRAINT PK_{t} PRIMARY KEY, S TEXT(10))");
                    for (int i = 0; i < 10; i++)
                    {
                        Run(p, $"INSERT INTO {t} (S) VALUES ('s{i + 1}')");
                    }
                }
                Act(p, "DELETE FROM AuK WHERE ID >= 8");
                Act(p, "INSERT INTO AuK (S) VALUES ('after-delete')");
                ReadSql(p, "SELECT @@IDENTITY", 2, 2);
                Act(p, "DELETE FROM AuC WHERE ID >= 8");
                Act(p, "DELETE FROM AuA");
                ReadSql(p, "SELECT COUNT(*), MAX(ID) FROM AuC", 2, 2);
                p.Call("db", "Close").Release("db");
                p.Call("eng", "CompactDatabase", A.Out(file), A.Out(Scratch("autocompact-out", "accdb"))).Files("after-compact", keep: true);
                p.CallBind("db", "eng", "OpenDatabase", A.Out(Scratch("autocompact-out", "accdb")), A.Bool(false), A.Bool(false));
                foreach (string t in new[] { "AuC", "AuA", "AuK" })
                {
                    Act(p, $"INSERT INTO {t} (S) VALUES ('after-compact')");
                    ReadSql(p, "SELECT @@IDENTITY", 2, 2);
                    ReadSql(p, $"SELECT ID FROM {t} ORDER BY ID", 16, 1);
                }
                p.FinishLite("db");
            });
    }

    private static Probe AutoCancelFail()
    {
        return new Probe(Card, "auto-cancel-fail-accdb", F2("Generated values", "R-TXN-1 generated-value"),
            "Branch R-FMT-2 Generated values (counter consumption) on an ACCDB. Au(ID AutoNumber primary key, U Long unique, S Text) with three rows. One variable at a time, @@IDENTITY read after each: (a) a recordset AddNew with the ID read before any put, then CancelUpdate, then an insert; (b) an INSERT refused for a duplicate U (dbFailOnError), then an insert; (c) a recordset AddNew whose Update is refused for a duplicate U, then the same row's U changed and Update repeated, with the ID read before and after; (d) Workspaces(0).BeginTrans, an insert, Rollback, then an insert; (e) an insert with an explicit existing ID (refused), then an insert. Asks when the next value is taken and whether cancel, refusal and rollback consume it.",
            p =>
            {
                Begin(p, Scratch("autocancel", "accdb"), Dao.Version120);
                Run(p, "CREATE TABLE Au (ID COUNTER CONSTRAINT PK_Au PRIMARY KEY, U LONG, S TEXT(10))");
                Run(p, "CREATE UNIQUE INDEX UX_U ON Au (U)");
                for (int i = 1; i <= 3; i++)
                {
                    Run(p, $"INSERT INTO Au (U, S) VALUES ({i}, 's{i}')");
                }
                p.GetBind("wss", "eng", "Workspaces").Item("ws", "wss", A.I4(0)).Release("wss");
                // (a) AddNew + CancelUpdate
                p.OpenRs("ra", "db", "Au", Dao.OpenDynaset).Call("ra", "AddNew").GetBind("rafl", "ra", "Fields").Item("raid", "rafl", A.Str("ID")).Get("raid", "Value")
                    .Item("rau", "rafl", A.Str("U")).Put("rau", "Value", A.I4(100)).Call("ra", "CancelUpdate")
                    .Release("rau").Release("raid").Release("rafl").Call("ra", "Close").Release("ra");
                Act(p, "INSERT INTO Au (U, S) VALUES (200, 'a')");
                ReadSql(p, "SELECT @@IDENTITY", 2, 2);
                // (b) refused INSERT
                Act(p, "INSERT INTO Au (U, S) VALUES (1, 'dup')");
                Act(p, "INSERT INTO Au (U, S) VALUES (300, 'b')");
                ReadSql(p, "SELECT @@IDENTITY", 2, 2);
                // (c) AddNew, refused Update, retry
                p.OpenRs("rc", "db", "Au", Dao.OpenDynaset).Call("rc", "AddNew").GetBind("rcfl", "rc", "Fields").Item("rcid", "rcfl", A.Str("ID")).Get("rcid", "Value")
                    .Item("rcu", "rcfl", A.Str("U")).Put("rcu", "Value", A.I4(1));
                Mark(p);
                p.Call("rc", "Update").Errors().Get("rcid", "Value").Put("rcu", "Value", A.I4(400));
                Mark(p);
                p.Call("rc", "Update").Errors().Get("rcid", "Value")
                    .Release("rcu").Release("rcid").Release("rcfl").Call("rc", "Close").Release("rc");
                ReadSql(p, "SELECT * FROM Au ORDER BY ID", 16, 4);
                // (d) rollback
                p.Call("ws", "BeginTrans");
                Act(p, "INSERT INTO Au (U, S) VALUES (500, 'in-txn')");
                ReadSql(p, "SELECT @@IDENTITY", 2, 2);
                p.Call("ws", "Rollback");
                Act(p, "INSERT INTO Au (U, S) VALUES (600, 'after-rollback')");
                ReadSql(p, "SELECT @@IDENTITY", 2, 2);
                // (e) explicit duplicate ID
                Act(p, "INSERT INTO Au (ID, U, S) VALUES (3, 700, 'dup-id')");
                Act(p, "INSERT INTO Au (U, S) VALUES (800, 'e')");
                ReadSql(p, "SELECT @@IDENTITY", 2, 2);
                ReadSql(p, "SELECT * FROM Au ORDER BY ID", 24, 4);
                p.Release("ws").FinishLite("db");
            });
    }

    private static Probe AutoGuidRandom()
    {
        return new Probe(Card, "auto-guid-random-accdb", F2("Generated values", "GUID/AutoNumber behavior"),
            "Branch R-FMT-2 Generated values (random AutoNumber and GUID generation) on an ACCDB, observation only. (1) AG(ID Long, G GUID) made through CreateTableDef with G's Attributes put to 16 (dbAutoIncrField) before append; three rows inserted without G and read. (2) CREATE TABLE with G UNIQUEIDENTIFIER DEFAULT GenGUID() and, separately, G GUID DEFAULT GenGUID(), then three inserts each. (3) AR(ID AutoNumber primary key, S): the field's property NewValues created as Text 'Random' and, as a second try, as Long 1, each appended with a marked Errors read, then five inserts and the IDs read. Asks which routes ACE admits and what each generates (the same GUID twice? a random Long?).",
            p =>
            {
                Begin(p, Scratch("autoguid", "accdb"), Dao.Version120);
                p.CallBind("t", "db", "CreateTableDef", A.Str("AG"))
                    .CallBind("fi", "t", "CreateField", A.Str("ID"), A.I4(4)).GetBind("fl", "t", "Fields").Call("fl", "Append", A.Obj("fi"))
                    .CallBind("fg", "t", "CreateField", A.Str("G"), A.I4(15));
                Mark(p);
                p.Put("fg", "Attributes", A.I4(16)).Errors();
                Mark(p);
                p.Call("fl", "Append", A.Obj("fg")).Errors();
                Mark(p);
                p.GetBind("cts", "db", "TableDefs").Call("cts", "Append", A.Obj("t")).Errors().Release("cts").Release("fg").Release("fi").Release("fl").Release("t");
                for (int i = 1; i <= 3; i++)
                {
                    Act(p, $"INSERT INTO AG (ID) VALUES ({i})");
                }
                ReadSql(p, "SELECT * FROM AG ORDER BY ID", 8, 4);
                FieldProps(p, "db", "AG", "G", "agp");
                foreach ((string table, string type) in new[] { ("AG2", "UNIQUEIDENTIFIER"), ("AG3", "GUID") })
                {
                    Act(p, $"CREATE TABLE {table} (ID LONG CONSTRAINT PK_{table} PRIMARY KEY, G {type} DEFAULT GenGUID())");
                    for (int i = 1; i <= 3; i++)
                    {
                        Act(p, $"INSERT INTO {table} (ID) VALUES ({i})");
                    }
                    ReadSql(p, $"SELECT * FROM {table} ORDER BY ID", 8, 4);
                }
                Run(p, "CREATE TABLE AR (ID COUNTER CONSTRAINT PK_AR PRIMARY KEY, S TEXT(10))");
                TdOpen(p, "db", "AR", "art");
                p.GetBind("arfl", "art", "Fields").Item("arf", "arfl", A.Str("ID")).GetBind("arps", "arf", "Properties");
                Mark(p);
                p.CallBind("arp1", "arf", "CreateProperty", A.Str("NewValues"), A.I4(10), A.Str("Random")).Errors();
                Mark(p);
                p.Call("arps", "Append", A.Obj("arp1")).Errors().Release("arp1");
                Mark(p);
                p.CallBind("arp2", "arf", "CreateProperty", A.Str("NewValues"), A.I4(4), A.I4(1)).Errors();
                Mark(p);
                p.Call("arps", "Append", A.Obj("arp2")).Errors().Release("arp2").Release("arps").Release("arf").Release("arfl").Release("art");
                for (int i = 1; i <= 5; i++)
                {
                    Act(p, $"INSERT INTO AR (S) VALUES ('r{i}')");
                }
                ReadSql(p, "SELECT * FROM AR ORDER BY S", 8, 4);
                FieldProps(p, "db", "AR", "ID", "arq");
                p.FinishLite("db");
            });
    }

    // ================================================================================================
    // R-FMT-2: statistics word (public DistinctCount)

    private static Probe DcRules(string ext, int version)
    {
        string file = Scratch("dc", ext);
        return new Probe(Card, $"dc-rules-{ext}", F2("II-05 Statistics word"),
            $"Branch R-FMT-2 II-05 Statistics word (public DistinctCount rule) on a {Fmt(ext)}. D(ID Long primary key, K Long, S Text(10)) with non-unique IX_K on K and unique UX_S on S. After each of these steps, one at a time, every index's DistinctCount, Primary, Unique and fields are read (TableDefs and Indexes refreshed): insert (1,10,'a'); insert (2,20,'b'); insert a duplicate key (3,10,'c'); delete that duplicate; delete the last row with K = 10; reinsert K = 10; update K 20 to 30 and back to 20; an insert refused for a duplicate S; an insert with a duplicate K; AddNew then CancelUpdate; an UPDATE refused for a duplicate S. Then the file is closed and the indexes read after a read-only reopen, and again from a DBEngine.CompactDatabase copy. Asks the increment, decrement and refusal rule of the statistic, and whether reopen and compaction change it.",
            p =>
            {
                Begin(p, file, version);
                Run(p, "CREATE TABLE D (ID LONG CONSTRAINT PK_D PRIMARY KEY, K LONG, S TEXT(10))");
                Run(p, "CREATE INDEX IX_K ON D (K)");
                Run(p, "CREATE UNIQUE INDEX UX_S ON D (S)");
                IndexMeta(p, "D");
                foreach (string sql in new[]
                {
                    "INSERT INTO D (ID, K, S) VALUES (1, 10, 'a')",
                    "INSERT INTO D (ID, K, S) VALUES (2, 20, 'b')",
                    "INSERT INTO D (ID, K, S) VALUES (3, 10, 'c')",
                    "DELETE FROM D WHERE ID = 3",
                    "DELETE FROM D WHERE ID = 1",
                    "INSERT INTO D (ID, K, S) VALUES (4, 10, 'd')",
                    "UPDATE D SET K = 30 WHERE ID = 2",
                    "UPDATE D SET K = 20 WHERE ID = 2",
                    "INSERT INTO D (ID, K, S) VALUES (5, 40, 'b')",
                    "INSERT INTO D (ID, K, S) VALUES (6, 10, 'e')",
                })
                {
                    Act(p, sql);
                    IndexMeta(p, "D");
                }
                p.OpenRs("cr", "db", "D", Dao.OpenTable).Call("cr", "AddNew").GetBind("cfl", "cr", "Fields").Item("cid", "cfl", A.Str("ID")).Put("cid", "Value", A.I4(7))
                    .Item("ck", "cfl", A.Str("K")).Put("ck", "Value", A.I4(77)).Call("cr", "CancelUpdate")
                    .Release("ck").Release("cid").Release("cfl").Call("cr", "Close").Release("cr");
                IndexMeta(p, "D");
                Act(p, "UPDATE D SET S = 'b' WHERE ID = 4");
                IndexMeta(p, "D");
                ReadSql(p, "SELECT * FROM D ORDER BY ID", 16, 4);
                p.Call("db", "Close").Release("db");
                p.CallBind("db", "eng", "OpenDatabase", A.Out(file), A.Bool(false), A.Bool(true));
                IndexMeta(p, "D");
                p.Call("db", "Close").Release("db");
                p.Call("eng", "CompactDatabase", A.Out(file), A.Out(Scratch("dc-out", ext))).Files("after-compact", keep: true);
                p.CallBind("db", "eng", "OpenDatabase", A.Out(Scratch("dc-out", ext)), A.Bool(false), A.Bool(true));
                IndexMeta(p, "D");
                ReadSql(p, "SELECT * FROM D ORDER BY ID", 16, 4);
                p.FinishLite("db");
            });
    }

    // ================================================================================================
    // R-PRESERVE-1

    private static Probe FailHash(string variant)
    {
        (string Sql, string What) spec = variant switch
        {
            "unique" => ("INSERT INTO U (ID, U, N) VALUES (101, 10, 'x')", "a duplicate value in the unique index UX_U"),
            "fk" => ("INSERT INTO C (ID, PID) VALUES (101, 999)", "a missing parent row (foreign key)"),
            _ => ("INSERT INTO V (ID, P) VALUES (101, -5)", "a validation rule violation (P > 0)"),
        };
        return new Probe(Card, $"fail-hash-{variant}", Pr("Capture and comparison", "failed preflight", "rollback"),
            $"Branch R-PRESERVE-1 failed operations: why a refused statement changed the file hash. On a copy of the fail fixture, one kind of refusal per probe: {spec.Sql} ({spec.What}), with dbFailOnError and a marked Errors read. Steps, each a read/write open, the statement, close, and a kept whole-file hash: (1) the refused statement; (2) the same refused statement again; (3) an open and close with no statement. Asks whether a refused statement changes the bytes, whether the second does, and whether the answer differs between refusal kinds (the other two kinds are separate probes).",
            p =>
            {
                p.EngineLite().Files("start", keep: true);
                Step(p, "refusal-1", b => Act(b, spec.Sql));
                Step(p, "refusal-2", b => Act(b, spec.Sql));
                Step(p, "open-close", _ => { });
                p.Release("eng").FinishLite();
            })
        { Fixtures = [("main", Fx("fail-accdb"))] };
    }

    private static Probe RollbackSize()
    {
        const string Bulk = "INSERT INTO Big (ID, T) SELECT A.ID + 100 * B.ID, String(200, 'x') FROM Nums AS A, Nums AS B";
        return new Probe(Card, "rollback-size-accdb", Pr("Capture and comparison", "rollback"),
            "Branch R-PRESERVE-1 rollback (and R-TXN-1 write-behind, public side). On a copy of the fail fixture, each step a read/write open, Workspaces(0).BeginTrans, an insert into Big, Rollback or CommitTrans, a row count read, close, and a kept whole-file hash: (1) one row, Rollback; (2) 3,600 rows of about 200 characters (large enough to push dirty pages out of the cache), Rollback; (3) the same 3,600 rows, CommitTrans, as the control. Asks whether a rolled-back transaction leaves the bytes unchanged at both sizes, which would explain why a smaller rollback earlier left the hash equal, and what a commit changes.",
            p =>
            {
                p.EngineLite().Files("start", keep: true);
                Step(p, "one-row-rollback", b =>
                {
                    b.GetBind("wss", "eng", "Workspaces").Item("ws", "wss", A.I4(0)).Release("wss").Call("ws", "BeginTrans");
                    Act(b, "INSERT INTO Big (ID, T) VALUES (1, 'x')");
                    b.Call("ws", "Rollback").Release("ws");
                    ReadSql(b, "SELECT COUNT(*) FROM Big", 2, 2);
                });
                Step(p, "bulk-rollback", b =>
                {
                    b.GetBind("wss", "eng", "Workspaces").Item("ws", "wss", A.I4(0)).Release("wss").Call("ws", "BeginTrans");
                    Act(b, Bulk);
                    b.Call("ws", "Rollback").Release("ws");
                    ReadSql(b, "SELECT COUNT(*) FROM Big", 2, 2);
                });
                Step(p, "bulk-commit", b =>
                {
                    b.GetBind("wss", "eng", "Workspaces").Item("ws", "wss", A.I4(0)).Release("wss").Call("ws", "BeginTrans");
                    Act(b, Bulk);
                    b.Call("ws", "CommitTrans").Release("ws");
                    ReadSql(b, "SELECT COUNT(*) FROM Big", 2, 2);
                });
                p.Release("eng").FinishLite();
            })
        { Fixtures = [("main", Fx("fail-accdb"))] };
    }

    private static Probe QueryProps()
    {
        (string Name, string Sql)[] queries =
        [
            ("Q1", "SELECT * FROM T;"),
            ("Q2", "SELECT ID FROM T WHERE ID > 1;"),
            ("Q3", "SELECT T.ID, U.B FROM T INNER JOIN U ON T.ID = U.ID;"),
            ("Q4", "SELECT * FROM U;"),
            ("Q5", "UPDATE T SET A = 'x';"),
            ("Q6", "SELECT ID, A FROM T ORDER BY A;"),
            ("Q7", "SELECT DISTINCT A FROM T;"),
            ("Q8", "SELECT COUNT(*) AS N FROM T;"),
        ];
        return new Probe(Card, "query-props-accdb", Pr("II-06 catalog/property discrimination", "unknown properties"),
            "Branch R-PRESERVE-1 II-06 (public side) on an ACCDB. T(ID, A) carries two table properties created through CreateProperty, Description and an unknown one, CalcDbUnknown; U(ID, B) carries none. Eight saved queries are created one element apart: Q1 SELECT * FROM T; Q2 with a WHERE; Q3 a join of T and U; Q4 over U only; Q5 an UPDATE of T; Q6 with ORDER BY; Q7 DISTINCT; Q8 an aggregate. Each query's Properties (Name, Type, Value) are listed. Asks which queries list T's table properties among their own, which earlier showed Q-over-T lists and join and action queries did not, and what decides it (single table? SELECT list? ORDER BY? aggregate?).",
            p =>
            {
                Begin(p, Scratch("qprops", "accdb"), Dao.Version120);
                Run(p, "CREATE TABLE T (ID LONG CONSTRAINT PK_T PRIMARY KEY, A TEXT(20))");
                Run(p, "CREATE TABLE U (ID LONG CONSTRAINT PK_U PRIMARY KEY, B TEXT(20))");
                Run(p, "INSERT INTO T (ID, A) VALUES (1, 'a1')");
                Run(p, "INSERT INTO U (ID, B) VALUES (1, 'b1')");
                TdOpen(p, "db", "T", "qt");
                p.GetBind("qtp", "qt", "Properties");
                foreach ((string name, string value) in new[] { ("Description", "table T description"), ("CalcDbUnknown", "unknown property value") })
                {
                    Mark(p);
                    p.CallBind("qpp", "qt", "CreateProperty", A.Str(name), A.I4(10), A.Str(value)).Errors();
                    Mark(p);
                    p.Call("qtp", "Append", A.Obj("qpp")).Errors().Release("qpp");
                }
                p.Release("qtp").Release("qt");
                foreach ((string name, string sql) in queries)
                {
                    p.CallBind("qd", "db", "CreateQueryDef", A.Str(name), A.Str(sql)).Call("qd", "Close").Release("qd");
                }
                p.GetBind("qds", "db", "QueryDefs").Call("qds", "Refresh")
                    .Each("qds", "q", 12, q => q.Get("q", "Name").Get("q", "Type").GetBind("qp", "q", "Properties").Call("qp", "Refresh").Get("qp", "Count")
                        .Each("qp", "pp", 40, b => b.Get("pp", "Name").Get("pp", "Type").Get("pp", "Value")).Release("qp"))
                    .Release("qds");
                p.FinishLite("db");
            });
    }

    private static Probe RenameVariants()
    {
        return new Probe(Card, "rename-variants-accdb", Pr("II-06 catalog/property discrimination", "Name AutoCorrect (engine-only)"),
            "Branch R-PRESERVE-1 II-06 engine-only rename (separate from Access Name AutoCorrect) on an ACCDB. R1 is referenced by a relationship only (R2 holds the foreign key), R3 by a saved query QR3 only, R4 by nothing, R5 by both a relationship (R6 holds the foreign key) and a saved query QR5. Each is renamed in its own step through TableDef.Name (Rx to Rxx), with a marked Errors read, and after each step the relationships (Name, Table, ForeignTable), the saved queries' SQL, the table names and an attempt to open each saved query as a recordset are read. Asks what a table rename rewrites (relation, query SQL) and what it leaves dangling, one referencing kind at a time.",
            p =>
            {
                Begin(p, Scratch("rename", "accdb"), Dao.Version120);
                Run(p, "CREATE TABLE R1 (ID LONG CONSTRAINT PK_R1 PRIMARY KEY)");
                Run(p, "CREATE TABLE R2 (ID LONG CONSTRAINT PK_R2 PRIMARY KEY, PID LONG, CONSTRAINT FK_R2 FOREIGN KEY (PID) REFERENCES R1 (ID))");
                Run(p, "CREATE TABLE R3 (ID LONG CONSTRAINT PK_R3 PRIMARY KEY)");
                Run(p, "CREATE TABLE R4 (ID LONG CONSTRAINT PK_R4 PRIMARY KEY)");
                Run(p, "CREATE TABLE R5 (ID LONG CONSTRAINT PK_R5 PRIMARY KEY)");
                Run(p, "CREATE TABLE R6 (ID LONG CONSTRAINT PK_R6 PRIMARY KEY, PID LONG, CONSTRAINT FK_R6 FOREIGN KEY (PID) REFERENCES R5 (ID))");
                p.CallBind("q1", "db", "CreateQueryDef", A.Str("QR3"), A.Str("SELECT * FROM R3;")).Call("q1", "Close").Release("q1");
                p.CallBind("q2", "db", "CreateQueryDef", A.Str("QR5"), A.Str("SELECT * FROM R5;")).Call("q2", "Close").Release("q2");
                RenameView(p, "v0");
                foreach (string t in new[] { "R1", "R3", "R4", "R5" })
                {
                    TdOpen(p, "db", t, "rt");
                    Mark(p);
                    p.Put("rt", "Name", A.Str(t + "x")).Errors().Release("rt");
                    RenameView(p, "after-" + t);
                }
                p.FinishLite("db");
            });
    }

    private static void RenameView(P p, string tag)
    {
        p.GetBind("rls", "db", "Relations").Call("rls", "Refresh")
            .Each("rls", "r", 12, r => r.Get("r", "Name").Get("r", "Table").Get("r", "ForeignTable")).Release("rls")
            .GetBind("rqs", "db", "QueryDefs").Call("rqs", "Refresh")
            .Each("rqs", "q", 12, q => q.Get("q", "Name").Get("q", "SQL")).Release("rqs")
            .GetBind("rts", "db", "TableDefs").Call("rts", "Refresh")
            .Each("rts", "t", 40, t => t.Get("t", "Name")).Release("rts");
        foreach (string q in new[] { "QR3", "QR5" })
        {
            string rs = "rq" + tag.Replace("-", string.Empty) + q;
            p.OpenRs(rs, "db", q, Dao.OpenSnapshot).Call(rs, "Close").Release(rs);
        }
    }

    // ================================================================================================
    // R-SORT-3

    private static readonly string?[] CorpusV =
    [
        string.Empty, " ", "a", "A", "a ", X(0x1D11E), X(0x1D400), X(0x1F600), X(0x20000), X(0x20001), X(0x20000, 0x20000), "a" + X(0x20000), X(0x20000) + "a",
        X(0x3400), X(0x9FFF), X(0xFFFD), X(0xFFFF), X(0xD7FF), X(0xE000), X(0x1E9E), X(0xD800), X(0xDC00), X(0xE9), "e" + X(0x301), X(0xDF), "ss",
        X(0x4E00), X(0x6F22), X(0x56FD), X(0x3042), X(0x30A2), Rep("A", 253) + X(0x20000), Rep("A", 253), X(0x10000), X(0x10FFFF), X(1), X(0xA0), X(9), "b", "z", null,
    ];

    private static readonly string?[] CorpusControls =
    [
        "a", "a ", "a  ", "a\t", "a" + X(0xA0), "a" + X(0x200B), "a" + X(0xAD), "a" + X(0x301), "a" + X(0x301, 0x301), "a" + X(0x301, 0x302), "a" + X(0x302, 0x301),
        X(0x301) + "a", "a" + X(1), "a" + X(0x1F), "a" + X(0x7F), "a\r", "a\n", "a\r\n", "a" + X(0x2028), " a", "\ta", "a b", "a\tb", "ab", "a" + X(0) + "b", "a" + X(0),
        X(0) + "a", "a" + X(0x3000), "A" + X(0x301), X(0xE1), "a" + X(0xA0) + "b", null,
    ];

    private static Probe VCorpus(string tag, string ext, int version)
    {
        return new Probe(Card, $"{tag}-corpus-{ext}", S3("Descriptor dispatch", "Text levels", "UTF-16 boundaries"),
            $"Branch R-SORT-3 Descriptor dispatch / Text levels: one element varied, the file version. The same 41-value corpus (supplementary-plane characters alone and beside letters, U+FFFD, U+FFFF, a lone surrogate pair of each kind, ligature and accent forms, Han and kana, 253-letter boundary values, controls, a Null) is written into T (K Text(255)) with the same locale in a file made with dbVersion {version} ({Fmt(ext)}), with a non-unique index IX_K built after the rows; T0 is an unindexed copy. Reads GROUP BY K (first ID, count, last ID) in key order on T and on T0, ORDER BY K, ID on both, and the IX_K forward and backward walks. Asks whether the equality classes and the order (in particular supplementary-plane characters equal to the empty string, which was seen in dbVersion 128 and not in 256) follow the file version, the index, or neither.",
            p =>
            {
                Corpus(p, Scratch(tag, ext), version, CorpusV);
            });
    }

    private static Probe Controls()
    {
        return new Probe(Card, "controls-accdb", S3("Positions and controls"),
            "Branch R-SORT-3 Positions and controls on an ACCDB. T(K Text(255)) with 32 values built around the letter a: trailing spaces, tab, no-break space, zero-width space, soft hyphen, one and two combining marks in both orders, a leading combining mark, controls U+0001, U+001F and U+007F, CR, LF, CR LF, U+2028, U+3000, a leading space and tab, a space or tab between letters, embedded NUL before and after the letter, and a Null; an index IX_K built after the rows and an unindexed copy T0. Reads GROUP BY K (first ID, count) in key order, ORDER BY K, ID, and the IX_K walks on both. Asks which of these units are ignored, which are weighted, and where trailing and leading whitespace is trimmed.",
            p =>
            {
                Corpus(p, Scratch("controls", "accdb"), Dao.Version120, CorpusControls);
            });
    }

    private static void Corpus(P p, string file, int version, string?[] values)
    {
        Begin(p, file, version);
        Run(p, "CREATE TABLE T (ID LONG CONSTRAINT PK_T PRIMARY KEY, K TEXT(255))");
        var rows = new List<Arg[]>();
        for (int i = 0; i < values.Length; i++)
        {
            rows.Add([A.I4(i + 1), Units(values[i])]);
        }
        Insert(p, "db", "T", ["ID", "K"], [.. rows]);
        Run(p, "CREATE TABLE T0 (ID LONG CONSTRAINT PK_T0 PRIMARY KEY, K TEXT(255))");
        Run(p, "INSERT INTO T0 (ID, K) SELECT ID, K FROM T");
        Act(p, "CREATE INDEX IX_K ON T (K)");
        foreach (string t in new[] { "T", "T0" })
        {
            ReadSql(p, $"SELECT MIN(ID), COUNT(*), MAX(ID) FROM {t} GROUP BY K ORDER BY K", 64, 3);
            ReadSql(p, $"SELECT ID FROM {t} ORDER BY K, ID", 64, 1);
        }
        Walk(p, "db", "T", "IX_K", false, 64);
        Walk(p, "db", "T", "IX_K", true, 64);
        p.FinishLite("db");
    }

    private static Probe LenSeries(string tag, string unit, string ext, int version)
    {
        int[] lengths = [100, 127, 128, 200, 250, 253, 254, 255];
        return new Probe(Card, $"len-series-{tag}-{ext}", S3("Length510 boundary", "Shortened-key collisions", "Compound framing"),
            $"Branch R-SORT-3 Length510 boundary and Shortened-key collisions on a {Fmt(ext)}, public side, one character kind per probe (this one: {Describe(unit)}), eight lengths from 100 to 255 units. LA(K Text(255), N Long) with non-unique IX_KN on (K, N) built before the rows: per length the row N = 2 is inserted before N = 1 (IDs base+1 and base+2), so a key that keeps N orders base+2 first and a key cut before N orders by row position. LB has unique UX_KN on (K, N): N = 2, N = 1, then a true duplicate of N = 1 (the control that must be refused). LC has unique UX_K on K: K1 = unit x (length-1) + 'x', K2 = the same + 'y', K3 = 'y' + unit x (length-1) (a change at the first position), and a true duplicate of K1; every refusal read with a marked Errors. LD has the same rows as LC under a non-unique index, and SQL equality pairs are read. The IX_KN, UX_KN and UX_K walks and SQL ORDER BY K, N are read. Asks the length at which N stops deciding order, whether a changed last character still gives a distinct key, and whether SQL equality still separates the rows.",
            p =>
            {
                Begin(p, Scratch("len-" + tag, ext), version);
                Run(p, "CREATE TABLE LA (ID LONG CONSTRAINT PK_LA PRIMARY KEY, K TEXT(255), N LONG)");
                Run(p, "CREATE INDEX IX_KN ON LA (K, N)");
                Run(p, "CREATE TABLE LB (ID LONG CONSTRAINT PK_LB PRIMARY KEY, K TEXT(255), N LONG)");
                Run(p, "CREATE UNIQUE INDEX UX_KN ON LB (K, N)");
                Run(p, "CREATE TABLE LC (ID LONG CONSTRAINT PK_LC PRIMARY KEY, K TEXT(255))");
                Run(p, "CREATE UNIQUE INDEX UX_K ON LC (K)");
                Run(p, "CREATE TABLE LD (ID LONG CONSTRAINT PK_LD PRIMARY KEY, K TEXT(255))");
                Run(p, "CREATE INDEX IX_KD ON LD (K)");
                var la = new List<Arg[]>();
                var lb = new List<Arg[]>();
                var lc = new List<Arg[]>();
                for (int g = 0; g < lengths.Length; g++)
                {
                    int b = 10 * (g + 1), len = lengths[g];
                    string k = Rep(unit, len);
                    la.Add([A.I4(b + 1), Units(k), A.I4(2)]);
                    la.Add([A.I4(b + 2), Units(k), A.I4(1)]);
                    lb.Add([A.I4(b + 1), Units(k), A.I4(2)]);
                    lb.Add([A.I4(b + 2), Units(k), A.I4(1)]);
                    lb.Add([A.I4(b + 3), Units(k), A.I4(1)]);
                    string head = Rep(unit, len - 1);
                    lc.Add([A.I4(b + 1), Units(head + "x")]);
                    lc.Add([A.I4(b + 2), Units(head + "y")]);
                    lc.Add([A.I4(b + 3), Units("y" + head)]);
                    lc.Add([A.I4(b + 4), Units(head + "x")]);
                }
                Insert(p, "db", "LA", ["ID", "K", "N"], [.. la]);
                RowsE(p, "LB", ["ID", "K", "N"], lb);
                RowsE(p, "LC", ["ID", "K"], lc);
                Insert(p, "db", "LD", ["ID", "K"], [.. lc.Where((_, i) => i % 4 != 3)]);
                Walk(p, "db", "LA", "IX_KN", false, 40);
                Walk(p, "db", "LB", "UX_KN", false, 40);
                Walk(p, "db", "LC", "UX_K", false, 40);
                Walk(p, "db", "LD", "IX_KD", false, 40);
                ReadSql(p, "SELECT ID FROM LA ORDER BY K, N", 40, 1);
                ReadSql(p, "SELECT A.ID, B.ID FROM LD AS A INNER JOIN LD AS B ON A.K = B.K WHERE A.ID < B.ID", 40, 2);
                ReadSql(p, "SELECT A.ID, B.ID FROM LD AS A INNER JOIN LD AS B ON A.K < B.K WHERE A.ID + 1 = B.ID", 40, 2);
                p.FinishLite("db");
            });
    }

    private static string Describe(string unit) => unit.Length == 1 && unit[0] < 0x80 ? "ASCII letter a" : $"U+{(int)unit[0]:X4}";

    private static Probe MemoPrefix()
    {
        int[] lengths = [100, 254, 255, 256, 257, 300, 1000];
        return new Probe(Card, "memo-prefix-accdb", S3("Length510 boundary", "Shortened-key collisions", "UTF-16 boundaries"),
            "Branch R-SORT-3 Length510 boundary, Memo keys, on an ACCDB. MA(ID, M Memo) with non-unique IX_M built before the rows: per length n of 100, 254, 255, 256, 257, 300 and 1000 two values, m x n + 'x' and m x n + 'y' (a change only at the last unit), and for n = 300 a control with the change at the first unit. MB(ID, M Memo) gets CREATE UNIQUE INDEX UX_M on the empty table (its result is read) and then the same rows with a marked Errors read per row. Reads IX_M's DistinctCount, GROUP BY M (first ID, count), the SQL equality pairs, and the walk of IX_M. Asks the Memo key prefix length (the earlier unique-index refusals and the DistinctCount of 6 are consistent with 255 but did not measure it) and whether a unique index on Memo is admitted.",
            p =>
            {
                Begin(p, Scratch("memopfx", "accdb"), Dao.Version120);
                Run(p, "CREATE TABLE MA (ID LONG CONSTRAINT PK_MA PRIMARY KEY, M MEMO)");
                Run(p, "CREATE INDEX IX_M ON MA (M)");
                Run(p, "CREATE TABLE MB (ID LONG CONSTRAINT PK_MB PRIMARY KEY, M MEMO)");
                Act(p, "CREATE UNIQUE INDEX UX_M ON MB (M)");
                var rows = new List<Arg[]>();
                int id = 1;
                foreach (int n in lengths)
                {
                    rows.Add([A.I4(id++), Units(Rep("m", n) + "x")]);
                    rows.Add([A.I4(id++), Units(Rep("m", n) + "y")]);
                }
                rows.Add([A.I4(id++), Units("y" + Rep("m", 300))]);
                Insert(p, "db", "MA", ["ID", "M"], [.. rows]);
                RowsE(p, "MB", ["ID", "M"], rows);
                IndexMeta(p, "MA");
                IndexMeta(p, "MB");
                ReadSql(p, "SELECT MIN(ID), COUNT(*), MAX(ID) FROM MA GROUP BY M ORDER BY M", 32, 3);
                ReadSql(p, "SELECT A.ID, B.ID FROM MA AS A INNER JOIN MA AS B ON A.M = B.M WHERE A.ID < B.ID", 32, 2);
                Walk(p, "db", "MA", "IX_M", false, 32);
                ReadSql(p, "SELECT ID FROM MB ORDER BY ID", 32, 1);
                p.FinishLite("db");
            });
    }

    private static Probe TieOrder(string ext, int version)
    {
        (int Id, string K)[] rows = [(5, "a"), (8, "b"), (3, "a"), (2, "b"), (9, "a"), (4, "c"), (1, "a"), (6, "b"), (7, "a")];
        return new Probe(Card, $"tie-order-{ext}", S3("Traversal consequences", "Compound framing"),
            $"Branch R-SORT-3 Traversal consequences (row-locator tie break) on a {Fmt(ext)}. Equal keys are inserted with IDs in a scrambled order, (ID,K): " + string.Join(" ", rows.Select(r => $"({r.Id},{r.K})")) + ". TA has non-unique IX_K built before the rows, TB builds it after the rows, TD has no index. Reads the IX_K forward and backward walks of TA and TB, and SQL ORDER BY K, ORDER BY K, ID and ORDER BY K DESC on all three. Then IDs 3 and 7 are deleted and IDs 3 (K = a) and 11 (K = a) inserted, and everything is read again. Asks whether ties order by ID, by insertion order, or by row position, and how the SQL order and a delete and reinsert differ.",
            p =>
            {
                Begin(p, Scratch("tie", ext), version);
                Run(p, "CREATE TABLE TA (ID LONG CONSTRAINT PK_TA PRIMARY KEY, K TEXT(10))");
                Run(p, "CREATE INDEX IX_K ON TA (K)");
                Run(p, "CREATE TABLE TB (ID LONG CONSTRAINT PK_TB PRIMARY KEY, K TEXT(10))");
                Run(p, "CREATE TABLE TD (ID LONG CONSTRAINT PK_TD PRIMARY KEY, K TEXT(10))");
                foreach (string t in new[] { "TA", "TB", "TD" })
                {
                    foreach ((int id, string k) in rows)
                    {
                        Run(p, $"INSERT INTO {t} (ID, K) VALUES ({id}, '{k}')");
                    }
                }
                Run(p, "CREATE INDEX IX_K ON TB (K)");
                TieRead(p);
                foreach (string t in new[] { "TA", "TB", "TD" })
                {
                    Run(p, $"DELETE FROM {t} WHERE ID = 3 OR ID = 7");
                    Run(p, $"INSERT INTO {t} (ID, K) VALUES (3, 'a')");
                    Run(p, $"INSERT INTO {t} (ID, K) VALUES (11, 'a')");
                }
                TieRead(p);
                p.FinishLite("db");
            });
    }

    private static void TieRead(P p)
    {
        foreach (string t in new[] { "TA", "TB" })
        {
            Walk(p, "db", t, "IX_K", false, 16);
            Walk(p, "db", t, "IX_K", true, 16);
        }
        foreach (string t in new[] { "TA", "TB", "TD" })
        {
            ReadSql(p, $"SELECT ID FROM {t} ORDER BY K", 16, 1);
            ReadSql(p, $"SELECT ID FROM {t} ORDER BY K, ID", 16, 1);
            ReadSql(p, $"SELECT ID FROM {t} ORDER BY K DESC", 16, 1);
        }
    }

    private static Probe LikeIndex()
    {
        string?[] values =
        [
            "a", "A", "ab", "a b", "a-b", "ae", "AE", X(0xE6), "e", "E", X(0xE9), X(0xC9), "e" + X(0x301), "s", "ss", "SS", X(0xDF), "f", "fi", X(0xFB01), "oe", X(0x153),
            "o", X(0xF6), "n", X(0xF1), "b", "c", "ch", null,
        ];
        string[] likes = ["a*", "A*", "e*", X(0xE9) + "*", "s*", "f*", "o*", "a?", "?a", "*e*"];
        return new Probe(Card, "like-index-accdb", ["R-SORT-3", "SORT-CHECK", "LIKE-RANGE-01", "LIKE-RANGE-05", "Text levels"],
            "Branch R-SORT-3 LIKE-RANGE-01/05, one factor varied: the presence of an index on the same data. L(ID, K Text(50)) holds 30 values (a, A, ab, a b, a-b, ae, the ligature and expansion forms, accented and decomposed e, s and ss and the sharp s, f and the fi ligature, oe and its ligature, o and o-umlaut, n and n-tilde, b, c, ch, a Null). The same set of LIKE patterns (a*, A*, e*, e-acute*, s*, f*, o*, a?, ?a, *e*) plus K = 'a' and K >= 'a' AND K < 'b' is read (IDs, in ID order) in five states of one table: no index; after CREATE INDEX IX_K; after DROP INDEX; after CREATE INDEX IX_KD on K DESC; after DROP INDEX and CREATE INDEX IX_K again. Asks whether a LIKE result depends on the index, which patterns, and whether the answer repeats.",
            p =>
            {
                Begin(p, Scratch("like", "accdb"), Dao.Version120);
                Run(p, "CREATE TABLE L (ID LONG CONSTRAINT PK_L PRIMARY KEY, K TEXT(50))");
                var rows = new List<Arg[]>();
                for (int i = 0; i < values.Length; i++)
                {
                    rows.Add([A.I4(i + 1), Units(values[i])]);
                }
                Insert(p, "db", "L", ["ID", "K"], [.. rows]);
                LikeRead(p, likes);
                foreach (string ddl in new[] { "CREATE INDEX IX_K ON L (K)", "DROP INDEX IX_K ON L", "CREATE INDEX IX_KD ON L (K DESC)", "DROP INDEX IX_KD ON L", "CREATE INDEX IX_K ON L (K)" })
                {
                    Act(p, ddl);
                    LikeRead(p, likes);
                }
                p.FinishLite("db");
            });
    }

    private static void LikeRead(P p, string[] likes)
    {
        foreach (string like in likes)
        {
            ReadSql(p, $"SELECT ID FROM L WHERE K LIKE '{like}' ORDER BY ID", 40, 1);
        }
        ReadSql(p, "SELECT ID FROM L WHERE K = 'a' ORDER BY ID", 40, 1);
        ReadSql(p, "SELECT ID FROM L WHERE K >= 'a' AND K < 'b' ORDER BY ID", 40, 1);
    }

    private static Probe NegZero()
    {
        return new Probe(Card, "neg-zero-accdb", S3("Floating and DATE", "Integer and Money"),
            "Branch R-SORT-3 Floating and DATE (SQL consumers of negative zero) on an ACCDB. NZ(ID, D Double, S Single, T Date) with rows -0, +0, 1 and Null written as VT_R8, VT_R4 and VT_DATE bit patterns. The index walks earlier ordered -0 before +0 and Seek told them apart; this asks what SQL does: WHERE D = 0, D < 0, D > 0, S = 0, CDbl(T) = 0, ORDER BY D, ID, GROUP BY D, SELECT DISTINCT D, and an index IX_D built afterwards with the same reads. Asks whether SQL equality, grouping and ordering treat -0 and +0 as one value.",
            p =>
            {
                Begin(p, Scratch("negzero", "accdb"), Dao.Version120);
                Run(p, "CREATE TABLE NZ (ID LONG CONSTRAINT PK_NZ PRIMARY KEY, D DOUBLE, S SINGLE, T DATETIME)");
                Insert(p, "db", "NZ", ["ID", "D", "S", "T"],
                [
                    [A.I4(1), A.R8(-0.0), A.R4(-0.0f), A.Date(-0.0)],
                    [A.I4(2), A.R8(0.0), A.R4(0.0f), A.Date(0.0)],
                    [A.I4(3), A.R8(1.0), A.R4(1.0f), A.Date(1.0)],
                    [A.I4(4), A.Null, A.Null, A.Null],
                ]);
                NegZeroRead(p);
                Run(p, "CREATE INDEX IX_D ON NZ (D)");
                NegZeroRead(p);
                p.FinishLite("db");
            });
    }

    private static void NegZeroRead(P p)
    {
        foreach (string sql in new[]
        {
            "SELECT ID FROM NZ WHERE D = 0 ORDER BY ID",
            "SELECT ID FROM NZ WHERE D < 0 ORDER BY ID",
            "SELECT ID FROM NZ WHERE D > 0 ORDER BY ID",
            "SELECT ID FROM NZ WHERE S = 0 ORDER BY ID",
            "SELECT ID FROM NZ WHERE CDbl(T) = 0 ORDER BY ID",
            "SELECT ID FROM NZ ORDER BY D, ID",
            "SELECT COUNT(*), MIN(ID) FROM NZ GROUP BY D ORDER BY D",
            "SELECT DISTINCT D FROM NZ",
        })
        {
            ReadSql(p, sql, 8, 3);
        }
    }

    private static Probe GuidOrder()
    {
        var guids = new List<string> { Guid(-1, 0) };
        for (int k = 0; k < 16; k++)
        {
            guids.Add(Guid(k, 0x01));
        }
        foreach (int k in new[] { 0, 3, 4, 7, 8, 15 })
        {
            guids.Add(Guid(k, 0x80));
        }
        guids.Add("{FFFFFFFF-FFFF-FFFF-FFFF-FFFFFFFFFFFF}");
        return new Probe(Card, "guid-order-accdb", S3("NUMERIC / GUID / extended date"),
            "Branch R-SORT-3 GUID component order (public side) on an ACCDB. GU(ID, G GUID) with 24 GUIDs: all zero; the byte 0x01 at each of the 16 byte positions of the textual form, one position per row; the byte 0x80 at six positions; all FF. Written as braced strings (ID in write order), then CREATE INDEX IX_G, and the IX_G walk and SQL ORDER BY G read. Asks the order in which byte positions and byte values rank (textual group order or a byte-swapped storage order), one byte at a time.",
            p =>
            {
                Begin(p, Scratch("guid", "accdb"), Dao.Version120);
                Run(p, "CREATE TABLE GU (ID LONG CONSTRAINT PK_GU PRIMARY KEY, G GUID)");
                var rows = new List<Arg[]>();
                for (int i = 0; i < guids.Count; i++)
                {
                    rows.Add([A.I4(i + 1), A.Str(guids[i])]);
                }
                Insert(p, "db", "GU", ["ID", "G"], [.. rows]);
                Act(p, "CREATE INDEX IX_G ON GU (G)");
                Walk(p, "db", "GU", "IX_G", false, 40);
                ReadSql(p, "SELECT ID FROM GU ORDER BY G", 40, 1);
                p.FinishLite("db");
            });
    }

    private static string Guid(int bytePosition, int value)
    {
        string hex = string.Concat(Enumerable.Range(0, 16).Select(i => i == bytePosition ? value.ToString("X2") : "00"));
        return $"{{{hex[..8]}-{hex[8..12]}-{hex[12..16]}-{hex[16..20]}-{hex[20..]}}}";
    }

    private static Probe LocaleExtra()
    {
        (string Tag, string Locale)[] locales =
        [
            ("general", Dao.LangGeneral),
            ("russian", ";LANGID=0x0419;CP=1252;COUNTRY=0"),
            ("greek", ";LANGID=0x0408;CP=1252;COUNTRY=0"),
            ("danish", ";LANGID=0x0406;CP=1252;COUNTRY=0"),
            ("german", ";LANGID=0x0407;CP=1252;COUNTRY=0"),
        ];
        string?[] values =
        [
            X(0x430), X(0x431), X(0x432), X(0x451), X(0x435), X(0x44F), X(0x42F), X(0x3B1), X(0x3B2), X(0x3AC), X(0x3C2), X(0x3C3),
            "a", X(0xE4), "ae", "b", "o", X(0xF6), "oe", X(0xDF), "ss", "u", X(0xFC), "ue", "aa", X(0xE5), X(0xF8), "z", null,
        ];
        return new Probe(Card, "locale-extra-accdb", S3("Descriptor dispatch", "Text levels"),
            "Branch R-SORT-3 Descriptor dispatch, locales beyond those retained, on ACCDB files made with the locale as the one varied element: 0x0409 (control), Russian 0x0419, Greek 0x0408, Danish 0x0406 and German 0x0407, each CreateDatabase(path, locale, 128) in its own file. The same 30 values (Cyrillic letters including the e-diaeresis and capital ya, Greek letters including final sigma and accented alpha, German umlaut and sharp-s forms with their expansions, Danish aa and a-ring and o-stroke) are written to T (K Text(255)) and read: CollatingOrder, GROUP BY K (first ID, count) in key order, and ORDER BY K, ID. Asks which of these locales sort differently from General on this corpus (the earlier corpora had no Cyrillic or Greek).",
            p =>
            {
                p.EngineLite();
                foreach ((string tag, string locale) in locales)
                {
                    p.CallBind("db", "eng", "CreateDatabase", A.Out(Scratch("loc-" + tag, "accdb")), A.Str(locale), A.I4(Dao.Version120)).Get("db", "CollatingOrder");
                    Run(p, "CREATE TABLE T (ID LONG CONSTRAINT PK_T PRIMARY KEY, K TEXT(255))");
                    var rows = new List<Arg[]>();
                    for (int i = 0; i < values.Length; i++)
                    {
                        rows.Add([A.I4(i + 1), Units(values[i])]);
                    }
                    Insert(p, "db", "T", ["ID", "K"], [.. rows]);
                    ReadSql(p, "SELECT MIN(ID), COUNT(*) FROM T GROUP BY K ORDER BY K", 40, 2);
                    ReadSql(p, "SELECT ID FROM T ORDER BY K, ID", 40, 1);
                    p.Call("db", "Close").Release("db");
                }
                p.Release("eng").FinishLite();
            });
    }

    private static Probe DaoIndexPrimary()
    {
        (string Name, bool Primary, bool Unique, bool Required, bool IgnoreNulls)[] specs =
        [
            ("X1", true, true, false, false),
            ("X2", true, false, false, false),
            ("X3", false, true, true, false),
            ("X4", false, true, false, true),
            ("X5", false, false, true, false),
            ("X6", false, true, true, true),
        ];
        return new Probe(Card, "dao-index-primary-accdb", S3("Null policy / aliases"),
            "Branch R-SORT-3 Null policy / aliases, DAO property combinations, repairing the earlier ordering defect (which put Unique False after Primary True and so lost the Primary flag): a property is put only when it is True. Six tables X1..X6 (ID, K Long, S Text) without a key each get one DAO index on K by CreateIndex, with Primary, Unique, Required and IgnoreNulls set as: Primary + Unique; Primary alone; Unique + Required; Unique + IgnoreNulls; Required alone; Unique + Required + IgnoreNulls. Then the rows (1,10), (2,10), (3,Null), (4,Null) are inserted one by one with dbFailOnError and a marked Errors read. X7 has a DDL primary key and then gets a DAO Primary index (refused?). Reads each index's flags as stored and the surviving rows. Asks which combinations ACE admits, what each refuses (duplicate, Null) and which flags read back.",
            p =>
            {
                Begin(p, Scratch("daoprimary", "accdb"), Dao.Version120);
                foreach ((string name, bool primary, bool unique, bool required, bool ignore) in specs)
                {
                    Run(p, $"CREATE TABLE {name} (ID LONG, K LONG, S TEXT(10))");
                    DaoIndex(p, name, "IX_" + name, "K", primary, unique, required, ignore, "di" + name);
                    IndexMeta(p, name);
                    foreach (string sql in new[]
                    {
                        $"INSERT INTO {name} (ID, K, S) VALUES (1, 10, 'a')",
                        $"INSERT INTO {name} (ID, K, S) VALUES (2, 10, 'b')",
                        $"INSERT INTO {name} (ID, K, S) VALUES (3, Null, 'c')",
                        $"INSERT INTO {name} (ID, K, S) VALUES (4, Null, 'd')",
                    })
                    {
                        Act(p, sql);
                    }
                    ReadSql(p, $"SELECT ID FROM {name} ORDER BY ID", 8, 1);
                }
                Run(p, "CREATE TABLE X7 (ID LONG CONSTRAINT PK_X7 PRIMARY KEY, K LONG)");
                DaoIndex(p, "X7", "IX_X7", "K", true, true, false, false, "diX7");
                IndexMeta(p, "X7");
                p.FinishLite("db");
            });
    }

    // ================================================================================================
    // Helpers

    private static string X(params int[] codePoints)
    {
        var sb = new System.Text.StringBuilder();
        foreach (int cp in codePoints)
        {
            sb.Append(cp < 0x10000 ? ((char)cp).ToString() : char.ConvertFromUtf32(cp));
        }
        return sb.ToString();
    }

    private static string Rep(string unit, int count) => string.Concat(Enumerable.Repeat(unit, count));

    private static Arg Units(string? units) => units is null ? A.Null : A.BstrUnits(units);

    /// <summary>EngineLite, then CreateDatabase of a named scratch or fixture file; the caller closes it.</summary>
    private static void Begin(P p, string file, int version) =>
        p.EngineLite().CallBind("db", "eng", "CreateDatabase", A.Out(file), A.Str(Dao.LangGeneral), A.I4(version)).Get("db", "Version").Get("db", "CollatingOrder");

    /// <summary>A statement that always fails, so the DBEngine.Errors read after the next step belongs to that step.</summary>
    private static void Mark(P p, string db = "db") => p.Call(db, "Execute", A.Str("DROP TABLE " + MarkerTable));

    /// <summary>The statement under test: marker, Execute with dbFailOnError, RecordsAffected, then DBEngine.Errors.</summary>
    private static void Act(P p, string sql, string db = "db")
    {
        Mark(p, db);
        p.Call(db, "Execute", A.Str(sql), A.I4(Fail)).Get(db, "RecordsAffected").Errors();
    }

    /// <summary>A setup statement: dbFailOnError, so a refusal is visible in its own HRESULT and EXCEPINFO.</summary>
    private static void Run(P p, string sql, string db = "db") => p.Call(db, "Execute", A.Str(sql), A.I4(Fail));

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

    private static void FieldCount(P p, string table, string bind)
    {
        TdOpen(p, "db", table, bind);
        p.GetBind(bind + "_fl", bind, "Fields").Call(bind + "_fl", "Refresh").Get(bind + "_fl", "Count").Release(bind + "_fl").Release(bind);
    }

    /// <summary>One field's Type, Size, Attributes and every property (Expression, ResultType, Precision, Scale where present).</summary>
    private static void FieldProps(P p, string db, string table, string field, string tag)
    {
        TdOpen(p, db, table, tag + "t");
        p.GetBind(tag + "f", tag + "t", "Fields").Item(tag + "d", tag + "f", A.Str(field)).Get(tag + "d", "Type").Get(tag + "d", "Size").Get(tag + "d", "Attributes")
            .GetBind(tag + "p", tag + "d", "Properties").Each(tag + "p", tag + "q", 48, b => b.Get(tag + "q", "Name").Get(tag + "q", "Value"))
            .Release(tag + "p").Release(tag + "d").Release(tag + "f").Release(tag + "t");
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
    /// DAO route to an index on one column: CreateIndex, CreateField, then each property put only when True (a False put
    /// after Primary True had cleared Primary in the earlier probes), Indexes.Append with Errors read after a marker.
    /// </summary>
    private static void DaoIndex(P p, string table, string name, string field, bool primary, bool unique, bool required, bool ignoreNulls, string tag)
    {
        TdOpen(p, "db", table, tag + "t");
        p.CallBind(tag + "x", tag + "t", "CreateIndex", A.Str(name)).GetBind(tag + "xf", tag + "x", "Fields")
            .CallBind(tag + "f", tag + "x", "CreateField", A.Str(field)).Call(tag + "xf", "Append", A.Obj(tag + "f")).Release(tag + "f");
        if (primary)
        {
            p.Put(tag + "x", "Primary", A.Bool(true));
        }
        if (unique)
        {
            p.Put(tag + "x", "Unique", A.Bool(true));
        }
        if (required)
        {
            p.Put(tag + "x", "Required", A.Bool(true));
        }
        if (ignoreNulls)
        {
            p.Put(tag + "x", "IgnoreNulls", A.Bool(true));
        }
        p.GetBind(tag + "xs", tag + "t", "Indexes");
        Mark(p);
        p.Call(tag + "xs", "Append", A.Obj(tag + "x")).Errors().Call(tag + "xs", "Refresh")
            .Release(tag + "xs").Release(tag + "xf").Release(tag + "x").Release(tag + "t");
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

    /// <summary>Like <see cref="Insert"/>, but each row's Update has a marked DBEngine.Errors read, so a refusal carries its own number.</summary>
    private static void RowsE(P p, string table, string[] cols, IEnumerable<Arg[]> rows)
    {
        p.OpenRs("re", "db", table, Dao.OpenTable).GetBind("refl", "re", "Fields");
        for (int c = 0; c < cols.Length; c++)
        {
            p.Item("rf" + c, "refl", A.Str(cols[c]));
        }
        foreach (Arg[] row in rows)
        {
            Mark(p);
            p.Call("re", "AddNew");
            for (int c = 0; c < cols.Length; c++)
            {
                p.Put("rf" + c, "Value", row[c]);
            }
            p.Call("re", "Update").Errors();
        }
        for (int c = 0; c < cols.Length; c++)
        {
            p.Release("rf" + c);
        }
        p.Release("refl").Call("re", "Close").Release("re");
    }

    private static void FieldPut(P p, string table, string field, string property, Arg value)
    {
        TdOpen(p, "db", table, "fpt");
        p.GetBind("fpl", "fpt", "Fields").Item("fpx", "fpl", A.Str(field)).Put("fpx", property, value).Release("fpx").Release("fpl").Release("fpt");
    }

    /// <summary>
    /// ADO DDL on a closed file (Provider ACE.OLEDB.12.0, Data Source set through Connection.Properties): the route for
    /// DECIMAL with explicit precision and scale, which DAO DDL refuses.
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
