namespace CalcDb.Probes.Cards;

/// <summary>
/// R-CURSOR-1, second set: questions the first set (<see cref="RCursor1"/>) does not ask, public DAO surface only.
/// Two ACCDB fixtures are created through ACE DAO: main (F with dates, quoted text, Null and duplicates and four
/// indexes, C3 with three-column and leading-descending indexes, E empty, S1 singleton, P2/C2 parent and child
/// with a declared relationship, parameter query QP) and bulk (B 20 rows, E empty, W of every scalar type with a
/// Null row and a large Memo, WD with a Decimal field). The other probes open a fresh copy and ask one family of
/// questions each: Filter/Sort and the derived recordset (CUR-LIFE), Find criteria forms (CUR-POS), Seek (CUR-SEEK),
/// PercentPosition/AbsolutePosition assignment and Move arguments (CUR-POS), GetRows and the provider cache
/// (CUR-BULK), Requery and Clone (CUR-LIFE), EditMode and LastModified (CUR-EDIT, CUR-ID) and the OpenRecordset
/// option flags (CUR-PROV, CUR-EDIT). Every read is itself an operation of the sequence. Probes ask; they hold no
/// expected answers. A bookmark argument is a constant here (arguments are constants only); the byte pattern
/// (row index, 2 or 3, 0, 0) is the pattern of the first rows' bookmarks in the retained r-cursor-1
/// id-bookmark-shape capture, applied as input and never read as a specification.
/// </summary>
public sealed class RCursor1B : ICard
{
    private const string Card = "r-cursor-1b";
    private const string CardId = "R-CURSOR-1";

    private const int DenyWrite = 1, DenyRead = 2, Inconsistent = 16, Consistent = 32;

    private static readonly string Memo3000 = new('m', 3000);

    private static readonly (string Name, int Type)[] Types =
        [("table", Dao.OpenTable), ("dynaset", Dao.OpenDynaset), ("snapshot", Dao.OpenSnapshot), ("forwardonly", Dao.OpenForwardOnly)];

    private static readonly (string Name, int Type)[] PosTypes =
        [("table", Dao.OpenTable), ("dynaset", Dao.OpenDynaset), ("snapshot", Dao.OpenSnapshot)];

    private static readonly string[] SeekOps6 = ["<", "<=", "=", ">=", ">", "<>"];
    private static readonly string[] SeekOps5 = ["=", ">=", ">", "<=", "<"];

    private static readonly string[] FRows =
    [
        "1, 10, 'alpha', #1/15/2020#, 'x'",
        "2, 20, 'beta', #2/20/2020 13:30:00#, Null",
        "3, 30, 'O''Brien', #3/25/2020#, 'x'",
        "4, 40, 'a\"b', #1/5/2021#, Null",
        "5, 40, 'abc', #1/15/2020#, 'y'",
        "6, 50, 'ABC', Null, Null",
        "7, 60, 'abd', #12/31/2020#, 'x'",
        "8, 70, Null, Null, 'y'",
    ];

    public IEnumerable<Probe> Probes() =>
        [.. Fixtures(), .. FilterSort(), .. Finds(), .. Seeks(), .. Positions(), .. Bulk(), .. Requeries(), .. Clones(), .. Edits(), .. Caches(), .. Options()];

    // Fixtures ----------------------------------------------------------------------------------------------

    private static IEnumerable<Probe> Fixtures()
    {
        yield return Creation("main", ["CUR-POS", "CUR-SEEK", "CUR-LIFE", "CUR-EDIT", "CUR-ID", "CUR-PROV"],
            "F(ID LONG primary key PK_F, N LONG, S TEXT(20), D DATETIME, Q TEXT(20)) with eight rows (N 10,20,30,40,40,50,60,70; S alpha, beta, " +
            "O'Brien, a\"b, abc, ABC, abd, Null; D with a time part, repeated and Null values; Q x, Null, y) and indexes IX_N (N), IX_NDESC (N DESC), " +
            "IX_S (S), IX_D (D); C3(A, B, C LONG NOT NULL, V) with unique IX_ABC (A ASC, B DESC, C ASC), IX_AD (A DESC, B ASC) and seven rows; " +
            "E(ID primary key, V) empty; S1(ID primary key, V) with one row; P2(ID primary key, N) with 2 rows and C2(ID primary key, PID, V) with 3 rows " +
            "and FOREIGN KEY FK_C2P (PID) REFERENCES P2 (ID); stored parameter query QP (PARAMETERS pN LONG; SELECT ID, N FROM F WHERE N >= pN ORDER BY ID)",
            p =>
            {
                Sql(p, "CREATE TABLE F (ID LONG CONSTRAINT PK_F PRIMARY KEY, N LONG, S TEXT(20), D DATETIME, Q TEXT(20))");
                foreach (string row in FRows)
                {
                    Sql(p, "INSERT INTO F VALUES (" + row + ")");
                }
                Sql(p, "CREATE INDEX IX_N ON F (N)");
                Sql(p, "CREATE INDEX IX_NDESC ON F (N DESC)");
                Sql(p, "CREATE INDEX IX_S ON F (S)");
                Sql(p, "CREATE INDEX IX_D ON F (D)");
                Sql(p, "CREATE TABLE C3 (A LONG NOT NULL, B LONG NOT NULL, C LONG NOT NULL, V TEXT(10))");
                Sql(p, "CREATE UNIQUE INDEX IX_ABC ON C3 (A ASC, B DESC, C ASC)");
                Sql(p, "CREATE INDEX IX_AD ON C3 (A DESC, B ASC)");
                foreach (string row in new[]
                {
                    "1, 1, 1, 'a'", "1, 2, 1, 'b'", "1, 2, 2, 'c'", "2, 1, 1, 'd'", "2, 1, 2, 'e'", "2, 2, 1, 'f'", "3, 1, 1, 'g'",
                })
                {
                    Sql(p, "INSERT INTO C3 VALUES (" + row + ")");
                }
                Sql(p, "CREATE TABLE E (ID LONG CONSTRAINT PK_E PRIMARY KEY, V TEXT(20))");
                Sql(p, "CREATE TABLE S1 (ID LONG CONSTRAINT PK_S1 PRIMARY KEY, V TEXT(20))");
                Sql(p, "INSERT INTO S1 VALUES (1, 'only')");
                Sql(p, "CREATE TABLE P2 (ID LONG CONSTRAINT PK_P2 PRIMARY KEY, N LONG)");
                Sql(p, "CREATE TABLE C2 (ID LONG CONSTRAINT PK_C2 PRIMARY KEY, PID LONG, V TEXT(10))");
                Sql(p, "INSERT INTO P2 VALUES (1, 10)");
                Sql(p, "INSERT INTO P2 VALUES (2, 20)");
                Sql(p, "ALTER TABLE C2 ADD CONSTRAINT FK_C2P FOREIGN KEY (PID) REFERENCES P2 (ID)");
                Sql(p, "INSERT INTO C2 VALUES (1, 1, 'a')");
                Sql(p, "INSERT INTO C2 VALUES (2, 1, 'b')");
                Sql(p, "INSERT INTO C2 VALUES (3, 2, 'c')");
                p.CallBind("qd", "db", "CreateQueryDef", A.Str("QP"), A.Str("PARAMETERS pN LONG; SELECT ID, N FROM F WHERE N >= pN ORDER BY ID;")).Release("qd");
            });

        yield return Creation("bulk", ["CUR-BULK", "CUR-POS", "CUR-LIFE"],
            "B(ID LONG primary key PK_B, N LONG, S TEXT(20)) with IDs 1-20, N = 10*ID, S 's'+ID; E(ID primary key, V) empty; W(ID LONG primary key PK_W, " +
            "Bo YESNO, Bt BYTE, Si SHORT, Cu CURRENCY, Db DOUBLE, Sg SINGLE, Dt DATETIME, Tx TEXT(20), Mm MEMO) with row 1 all set, row 2 all Null but ID " +
            "and row 3 with a 3000-character Memo; WD(ID LONG primary key PK_WD) with a Decimal field Dc added through TableDef.CreateField and two rows",
            p =>
            {
                Sql(p, "CREATE TABLE B (ID LONG CONSTRAINT PK_B PRIMARY KEY, N LONG, S TEXT(20))");
                for (int i = 1; i <= 20; i++)
                {
                    Sql(p, $"INSERT INTO B VALUES ({i}, {i * 10}, 's{i}')");
                }
                Sql(p, "CREATE TABLE E (ID LONG CONSTRAINT PK_E PRIMARY KEY, V TEXT(20))");
                Sql(p, "CREATE TABLE W (ID LONG CONSTRAINT PK_W PRIMARY KEY, Bo YESNO, Bt BYTE, Si SHORT, Cu CURRENCY, Db DOUBLE, Sg SINGLE, Dt DATETIME, Tx TEXT(20), Mm MEMO)");
                Sql(p, "INSERT INTO W VALUES (1, True, 200, -5, 12.3456, 1.5, 2.25, #1/15/2020 08:30:00#, 'one', 'memo1')");
                Sql(p, "INSERT INTO W (ID) VALUES (2)");
                Sql(p, $"INSERT INTO W VALUES (3, False, 0, 32767, 0, -0.5, 3.5, #1/1/2000#, 'three', '{Memo3000}')");
                Sql(p, "CREATE TABLE WD (ID LONG CONSTRAINT PK_WD PRIMARY KEY)");
                p.AddDecimalField("WD", "Dc");
                Sql(p, "INSERT INTO WD (ID, Dc) VALUES (1, 1.5)");
                Sql(p, "INSERT INTO WD (ID) VALUES (2)");
            });
    }

    // Filter and Sort (CUR-LIFE) ----------------------------------------------------------------------------

    private static IEnumerable<Probe> FilterSort()
    {
        int?[] derivedTypes = [null, Dao.OpenSnapshot, Dao.OpenDynaset, Dao.OpenForwardOnly, Dao.OpenTable];
        yield return On("main", "filter-derived-types", ["CUR-LIFE"],
            "For a source dynaset and then a source snapshot over F: open it, set Filter \"N > 20\" and Sort \"N DESC\", call OpenRecordset with the type " +
            "argument omitted, dbOpenSnapshot, dbOpenDynaset, dbOpenForwardOnly and dbOpenTable in turn; on each derived recordset read Type, Updatable, " +
            "Filter, Sort, walk ID and N by MoveNext to EOF, read RecordCount; close it and the source. Asks the type, updatability, inherited " +
            "properties and rows of the derived recordset per source type and requested type, and which requests are refused.",
            p =>
            {
                foreach (int sourceType in new[] { Dao.OpenDynaset, Dao.OpenSnapshot })
                {
                    foreach (int? derived in derivedTypes)
                    {
                        p.OpenRs("rs", "db", "F", sourceType).Put("rs", "Filter", A.Str("N > 20")).Put("rs", "Sort", A.Str("N DESC"));
                        if (derived is int t)
                        {
                            p.CallBind("rd", "rs", "OpenRecordset", A.I4(t));
                        }
                        else
                        {
                            p.CallBind("rd", "rs", "OpenRecordset");
                        }
                        p.Get("rd", "Type").Get("rd", "Updatable").Get("rd", "Filter").Get("rd", "Sort");
                        Forward(p, "rd", 16, "ID", "N");
                        p.Get("rd", "RecordCount");
                        Close(p, "rd");
                        Close(p, "rs");
                    }
                }
            });

        (string Label, Arg Value)[] filters = Labeled(
        [
            "N > 20", "S Like 'a*'", "D > #1/1/2020#", "D >= #2/20/2020 1:30:00 PM#", "Q Is Null", "Q Is Not Null", "S = 'O''Brien'",
            "N = 40 AND S = 'abc'", "N IN (10, 70)", "N BETWEEN 20 AND 40", "NOT N = 40", "1 = 0", "", "N >>", "NoSuchField = 1", "WHERE N > 20",
            "N > 20 ORDER BY N", "S > 'b'", "ID = '3'", "N = Null", "IsNull(S)", "[N] > 20", "F.N > 20",
        ], ("Null", A.Null), ("Empty", A.Empty), ("Long 1", A.I4(1)));
        yield return On("main", "filter-expression-forms", ["CUR-LIFE", "CUR-POS"],
            $"For each Filter value in turn [{Join(filters)}]: open a fresh dynaset over F, assign Filter, read Filter back, call OpenRecordset() with no arguments " +
            "into a derived recordset and walk ID to EOF from its first row; close both. Asks which Filter texts the property accepts at assignment, " +
            "which are refused at the derived OpenRecordset, and the rows each derived recordset holds.",
            p =>
            {
                foreach ((_, Arg value) in filters)
                {
                    p.OpenRs("rs", "db", "F", Dao.OpenDynaset).Put("rs", "Filter", value).Get("rs", "Filter").CallBind("rd", "rs", "OpenRecordset");
                    Forward(p, "rd", 16, "ID");
                    Close(p, "rd");
                    Close(p, "rs");
                }
            });

        (string Label, Arg Value)[] sorts = Labeled(
        [
            "N DESC", "S", "S DESC, N", "D", "D DESC", "ID DESC", "[S] ASC", "N DESC, ID DESC", "Q", "F.N", "N + 1", "NoSuchField", "",
            "N ASC, N DESC", "N DESCENDING", "ORDER BY N",
        ], ("Null", A.Null), ("Long 1", A.I4(1)));
        yield return On("main", "sort-expression-forms", ["CUR-LIFE", "CUR-POS"],
            $"For each Sort value in turn [{Join(sorts)}]: open a fresh dynaset over F, assign Sort, read Sort back, call OpenRecordset() with no arguments " +
            "into a derived recordset and walk ID to EOF; close both. Asks which Sort texts are accepted at assignment and at the derived OpenRecordset, " +
            "the order of the derived rows (text collation, Null placement, ties) and the Sort the derived recordset reports.",
            p =>
            {
                foreach ((_, Arg value) in sorts)
                {
                    p.OpenRs("rs", "db", "F", Dao.OpenDynaset).Put("rs", "Sort", value).Get("rs", "Sort").CallBind("rd", "rs", "OpenRecordset");
                    Forward(p, "rd", 16, "ID");
                    Close(p, "rd");
                    Close(p, "rs");
                }
            });

        yield return On("main", "filter-sort-clone-chain", ["CUR-LIFE", "CUR-POS"],
            "Dynaset R over F with Filter \"N > 20\" and Sort \"N DESC\": Clone into C, read C's Filter and Sort, BOF, EOF, AbsolutePosition and ID, walk ID to " +
            "EOF, MoveFirst and walk again. Then R.OpenRecordset() into R2, set R2 Filter \"N < 60\", R2.OpenRecordset() into R3, read R3's Filter and Sort, " +
            "walk ID, FindFirst \"N = 40\", FindNext \"N = 40\", reading NoMatch, BOF, EOF and ID. Close R3, set R2 Sort \"ID\", R2.OpenRecordset() into R4, " +
            "read Sort, walk ID. Then C.OpenRecordset() into R5, read its Filter and Sort and walk ID. Asks whether clones and derived recordsets inherit " +
            "or compose Filter and Sort, and the order of Find in a derived recordset.",
            p =>
            {
                p.OpenRs("rs", "db", "F", Dao.OpenDynaset).Put("rs", "Filter", A.Str("N > 20")).Put("rs", "Sort", A.Str("N DESC"));
                p.CallBind("rc", "rs", "Clone").Get("rc", "Filter").Get("rc", "Sort");
                State(p, "rc", true);
                Forward(p, "rc", 16, "ID");
                p.Call("rc", "MoveFirst");
                Forward(p, "rc", 16, "ID");
                p.CallBind("r2", "rs", "OpenRecordset").Put("r2", "Filter", A.Str("N < 60")).CallBind("r3", "r2", "OpenRecordset").Get("r3", "Filter").Get("r3", "Sort");
                Forward(p, "r3", 16, "ID");
                FindRead(p, "r3", "FindFirst", A.Str("N = 40"));
                FindRead(p, "r3", "FindNext", A.Str("N = 40"));
                Close(p, "r3");
                p.Put("r2", "Sort", A.Str("ID")).CallBind("r4", "r2", "OpenRecordset").Get("r4", "Sort");
                Forward(p, "r4", 16, "ID");
                Close(p, "r4");
                p.CallBind("r5", "rc", "OpenRecordset").Get("r5", "Filter").Get("r5", "Sort");
                Forward(p, "r5", 16, "ID");
                Close(p, "r5");
                Close(p, "r2");
                Close(p, "rc");
                Close(p, "rs");
            });

        (string Sql, string Filter, string Sort, string[] Fields)[] sources =
        [
            ("SELECT ID, N, S FROM F WHERE N < 60 ORDER BY N DESC", "N > 20", "ID", ["ID", "N"]),
            ("SELECT ID, N FROM F ORDER BY S", "N > 20", "N DESC", ["ID", "N"]),
            ("SELECT TOP 3 ID, N FROM F ORDER BY N DESC", "N < 70", "ID", ["ID", "N"]),
            ("SELECT N, COUNT(*) AS Cnt FROM F GROUP BY N", "Cnt > 1", "N DESC", ["N", "Cnt"]),
        ];
        yield return On("main", "filter-sort-query-source", ["CUR-LIFE", "CUR-POP"],
            $"For each SQL source [{string.Join(" ;; ", sources.Select(s => $"{s.Sql} | Filter {s.Filter} | Sort {s.Sort}"))}]: open a dynaset, read Type, Filter and Sort, walk the " +
            "listed fields to EOF; set Filter and Sort; call OpenRecordset() with no arguments; read the derived Type, Filter and Sort and walk the fields. " +
            "Asks how Filter and Sort compose with the source's own WHERE, ORDER BY, TOP and GROUP BY in the derived recordset and whether the source recordset's rows change.",
            p =>
            {
                foreach ((string sql, string filter, string sort, string[] fields) in sources)
                {
                    p.OpenRs("rs", "db", sql, Dao.OpenDynaset).Get("rs", "Type").Get("rs", "Filter").Get("rs", "Sort");
                    Forward(p, "rs", 16, fields);
                    p.Put("rs", "Filter", A.Str(filter)).Put("rs", "Sort", A.Str(sort)).CallBind("rd", "rs", "OpenRecordset").Get("rd", "Type").Get("rd", "Filter").Get("rd", "Sort");
                    Forward(p, "rd", 16, fields);
                    p.Call("rs", "MoveFirst");
                    Forward(p, "rs", 16, fields);
                    Close(p, "rd");
                    Close(p, "rs");
                }
            });

        yield return On("main", "filter-sort-other-types", ["CUR-LIFE"],
            "For a snapshot, a forward-only and a table-type recordset over F in turn: assign Filter \"N > 20\", assign Sort \"N DESC\", read both, call " +
            "OpenRecordset() with no arguments into a derived recordset, read its Type, Filter and Sort and walk ID to EOF; walk ID on the source from its " +
            "first row. Asks which types accept Filter and Sort and what the derived recordset of each holds.",
            p =>
            {
                foreach ((_, int type) in Types.Where(t => t.Type != Dao.OpenDynaset))
                {
                    p.OpenRs("rs", "db", "F", type).Put("rs", "Filter", A.Str("N > 20")).Put("rs", "Sort", A.Str("N DESC")).Get("rs", "Filter").Get("rs", "Sort");
                    p.CallBind("rd", "rs", "OpenRecordset").Get("rd", "Type").Get("rd", "Filter").Get("rd", "Sort");
                    Forward(p, "rd", 16, "ID");
                    Close(p, "rd");
                    Forward(p, "rs", 16, "ID");
                    Close(p, "rs");
                }
            });
    }

    // Find criteria forms (CUR-POS) -------------------------------------------------------------------------

    private static IEnumerable<Probe> Finds()
    {
        string[] dates =
        [
            "D = #1/15/2020#", "D = #15 Jan 2020#", "D = #2020-01-15#", "D = #1/15/20#", "D = #2/20/2020 1:30:00 PM#", "D = #2/20/2020#",
            "D = #2/20/2020 13:30#", "D >= #1/1/2021#", "D < #1/1/2020#", "D = 43845", "D = 43845.0", "D = '1/15/2020'", "D = #15/1/2020#",
            "D = #1/32/2020#", "D = CDate('1/15/2020')", "D = DateSerial(2020, 1, 15)", "D = #1/15/2020# AND N = 40",
            "D Between #1/1/2020# And #1/31/2020#", "D = #1/15/2020 12:00:00 AM#",
        ];
        yield return FindProbe("find-date-forms", dates.Select(d => (d, A.Str(d))).ToArray(), 1,
            "Asks which date literal spellings, serial numbers, string dates and date functions the criteria accept, how a time part takes part in equality, " +
            "and which of the two rows dated 1/15/2020 each FindFirst and FindNext reach.");

        string[] texts =
        [
            "S = 'alpha'", "S = 'ALPHA'", "S = 'alpha '", "S = 'O''Brien'", "S = \"O'Brien\"", "S = 'O'Brien'", "S = 'a\"b'", "S = \"a\"\"b\"",
            "S = \"a\"b\"", "S = '''", "S = ''", "S = \"\"", "S = 'abc'", "S > 'abc'", "S < 'b'", "S = [S]", "S = 'Zed'", "[S] = 'beta'", "S = 'beta",
            "S = beta", "S = 'be' & 'ta'", "S = 'a' + 'lpha'", "S = Left('alphabet', 5)",
        ];
        yield return FindProbe("find-text-quotes", texts.Select(t => (t, A.Str(t))).ToArray(), 1,
            "Asks quote and escape handling in text criteria, case and trailing-space comparison, comparison operators on text, field-to-field and " +
            "function-built text, and which malformed texts are refused, with the second match after FindNext.");

        string[] nulls =
        [
            "S Is Null", "S Is Not Null", "Q Is Null", "Q Is Not Null", "Q = Null", "Q <> Null", "Q = 'x'", "Q <> 'x'", "NOT Q = 'x'",
            "NOT (Q Is Null)", "IsNull(Q)", "Not IsNull(Q)", "Q Is Null OR N = 10", "Q Is Null AND S Is Null", "N Is Null", "D Is Null",
            "D Is Null AND N > 50", "Nz(Q, 'z') = 'z'", "Q = ''", "Len(Q) = 0", "Null",
        ];
        yield return FindProbe("find-null-forms", nulls.Select(t => (t, A.Str(t))).ToArray(), 2,
            "Asks how Is Null, comparison with Null, negation, IsNull, Nz and zero-length tests select rows holding Null, and the order of the successive " +
            "matches reached by FindNext.");

        string[] likes =
        [
            "S Like 'a*'", "S Like 'A*'", "S Like 'a?c'", "S Like 'a_c'", "S Like 'a%'", "S Like 'a#c'", "S Like '[ab]*'", "S Like '[!a]*'",
            "S Like '[a-b]*'", "S Like '*r*'", "S Like '*'", "S Like ''", "S Like 'a*' AND N > 40", "S Not Like 'a*'", "S Like \"a*\"",
            "S Like 'O''B*'", "S Like 'a[\"]b'", "S Like 'a?b'", "S Like '[*]'", "Q Like '*'", "S Like Null", "S Like 'alpha'", "S Like 'ALPHA'",
            "N Like '4*'", "D Like '*2020*'",
        ];
        yield return FindProbe("find-like-patterns", likes.Select(t => (t, A.Str(t))).ToArray(), 2,
            "Asks which Like wildcards (Jet and ANSI spellings, sets, ranges, negation, escapes), case rules, Null rows, and Like on number and date " +
            "fields the database accepts, and the rows reached in order.");

        string[] exprs =
        [
            "", " ", "N", "1=1", "1=0", "N = 40 N", "N = (40", "ID IN (1,3)", "N = ID * 10", "N > ID * 10", "UCase(S) = 'ALPHA'", "Len(S) = 3",
            "N Mod 20 = 0", "ID = '3'", "S = 3", "N = 'abc'", "N = 20.0", "N = 2E1", "N = &H14", "NoSuch = 1", "[S] = 'beta'", "F.N = 20", "N = 20;",
            "N = 20 -- c", "N = 20 /* c */", "N = DMax('N', 'F')", "N = Int(20.5)", "N = CInt('20')", "S = Chr(97) & 'lpha'", "N = Val('20')",
        ];
        (string Label, Arg Value)[] exprForms = Labeled(exprs, ("Missing", A.Missing), ("Empty", A.Empty), ("Long 1", A.I4(1)), ("Boolean True", A.Bool(true)), ("Double 40", A.R8(40)));
        yield return FindProbe("find-expression-forms", exprForms, 1,
            "Asks which expressions (arithmetic, functions, domain aggregates, comment markers, hexadecimal and exponent literals, coercions, qualified and " +
            "unknown names) the criteria engine evaluates, and which argument types (Missing, Empty, Long, Boolean, Double) it takes.");

        yield return On("main", "find-duplicates-sequence", ["CUR-POS"],
            "On a dynaset over F, with the criteria shown, call in order and read NoMatch, BOF, EOF and ID after each: FindFirst/FindNext x2/FindPrevious x3/" +
            "FindLast/FindPrevious x2/FindNext for \"N = 40\" (two rows share 40); FindFirst \"N = 40\", FindNext \"N >= 50\" x2, FindLast \"N <= 30\", " +
            "FindPrevious \"N >= 10\" x2; FindFirst \"S Like 'a*'\" with FindNext x5; then Move 0, MoveFirst, FindNext \"N = 40\", MoveLast, FindPrevious " +
            "\"N = 40\". Asks which duplicate each direction reaches, the effect of changing the criterion between calls, and the position after the last match.",
            p =>
            {
                p.OpenRs("rs", "db", "F", Dao.OpenDynaset);
                string n40 = "N = 40";
                foreach ((string member, string crit) in new[]
                {
                    ("FindFirst", n40), ("FindNext", n40), ("FindNext", n40), ("FindPrevious", n40), ("FindPrevious", n40), ("FindPrevious", n40),
                    ("FindLast", n40), ("FindPrevious", n40), ("FindPrevious", n40), ("FindNext", n40),
                    ("FindFirst", n40), ("FindNext", "N >= 50"), ("FindNext", "N >= 50"), ("FindLast", "N <= 30"), ("FindPrevious", "N >= 10"), ("FindPrevious", "N >= 10"),
                    ("FindFirst", "S Like 'a*'"), ("FindNext", "S Like 'a*'"), ("FindNext", "S Like 'a*'"), ("FindNext", "S Like 'a*'"), ("FindNext", "S Like 'a*'"), ("FindNext", "S Like 'a*'"),
                })
                {
                    FindRead(p, "rs", member, A.Str(crit));
                }
                p.Call("rs", "Move", A.I4(0));
                State(p, "rs", true).Call("rs", "MoveFirst");
                FindRead(p, "rs", "FindNext", A.Str(n40)).Call("rs", "MoveLast");
                FindRead(p, "rs", "FindPrevious", A.Str(n40));
                Close(p, "rs");
            });

        yield return On("main", "find-all-directions-other-types", ["CUR-POS", "CUR-SEEK"],
            "Over F in turn: a snapshot, a table-type recordset with no Index, a table-type recordset with Index IX_N, one with Index IX_NDESC, and a forward-only " +
            "recordset. Call FindFirst \"N = 40\", FindNext x2, FindLast, FindPrevious x3 with the same criterion, reading NoMatch, BOF, EOF and ID after each. " +
            "Asks which Find members each type accepts and, for table-type, whether the Index changes which duplicate is reached.",
            p =>
            {
                foreach ((int type, string? index) in new (int, string?)[]
                {
                    (Dao.OpenSnapshot, null), (Dao.OpenTable, null), (Dao.OpenTable, "IX_N"), (Dao.OpenTable, "IX_NDESC"), (Dao.OpenForwardOnly, null),
                })
                {
                    p.OpenRs("rs", "db", "F", type);
                    if (index is not null)
                    {
                        p.Put("rs", "Index", A.Str(index));
                    }
                    foreach (string member in new[] { "FindFirst", "FindNext", "FindNext", "FindLast", "FindPrevious", "FindPrevious", "FindPrevious" })
                    {
                        FindRead(p, "rs", member, A.Str("N = 40"));
                    }
                    Close(p, "rs");
                }
            });

        yield return On("main", "find-empty-and-ends", ["CUR-POS"],
            "On a dynaset and then a snapshot over empty table E: FindFirst, FindLast, FindNext and FindPrevious \"ID = 1\", reading NoMatch, BOF, EOF and ID. On a " +
            "dynaset over F: MoveLast, FindNext \"N > 0\"; MoveNext (to EOF), FindPrevious \"N = 70\", FindNext \"N = 70\"; FindLast \"N = 10\", FindNext \"N = 70\"; " +
            "MoveFirst, MovePrevious (to BOF), FindNext \"N = 10\"; MoveFirst, MovePrevious, FindPrevious \"N = 70\"; MoveFirst, FindPrevious \"N = 70\"; reading " +
            "NoMatch, BOF, EOF and ID after each. Asks Find on empty sets and from the BOF and EOF positions in both directions.",
            p =>
            {
                foreach (int type in new[] { Dao.OpenDynaset, Dao.OpenSnapshot })
                {
                    p.OpenRs("rs", "db", "E", type);
                    foreach (string member in new[] { "FindFirst", "FindLast", "FindNext", "FindPrevious" })
                    {
                        FindRead(p, "rs", member, A.Str("ID = 1"));
                    }
                    Close(p, "rs");
                }
                p.OpenRs("rs", "db", "F", Dao.OpenDynaset).Call("rs", "MoveLast");
                FindRead(p, "rs", "FindNext", A.Str("N > 0")).Call("rs", "MoveNext");
                State(p, "rs", false);
                FindRead(p, "rs", "FindPrevious", A.Str("N = 70"));
                FindRead(p, "rs", "FindNext", A.Str("N = 70"));
                FindRead(p, "rs", "FindLast", A.Str("N = 10"));
                FindRead(p, "rs", "FindNext", A.Str("N = 70")).Call("rs", "MoveFirst").Call("rs", "MovePrevious");
                State(p, "rs", false);
                FindRead(p, "rs", "FindNext", A.Str("N = 10")).Call("rs", "MoveFirst").Call("rs", "MovePrevious");
                FindRead(p, "rs", "FindPrevious", A.Str("N = 70")).Call("rs", "MoveFirst");
                FindRead(p, "rs", "FindPrevious", A.Str("N = 70"));
                Close(p, "rs");
            });
    }

    // Seek (CUR-SEEK) ---------------------------------------------------------------------------------------

    private static IEnumerable<Probe> Seeks()
    {
        (string Label, string Op, Arg Key)[] textCases =
        [
            ("= abc", "=", A.Str("abc")), ("= ABC", "=", A.Str("ABC")), ("= alpha", "=", A.Str("alpha")), ("= O'Brien", "=", A.Str("O'Brien")),
            ("= ab", "=", A.Str("ab")), ("= \"abc \"", "=", A.Str("abc ")), ("= \"\"", "=", A.Str("")), ("= Null", "=", A.Null), ("= a\"b", "=", A.Str("a\"b")),
            ("= Zed", "=", A.Str("Zed")), (">= ab", ">=", A.Str("ab")), ("> abc", ">", A.Str("abc")), ("< abc", "<", A.Str("abc")), ("<= abc", "<=", A.Str("abc")),
            ("< a", "<", A.Str("a")), ("= Long 5", "=", A.I4(5)), ("= *", "=", A.Str("*")),
        ];
        yield return On("main", "seek-text-index", ["CUR-SEEK"],
            $"Index IX_S (S) over F holds alpha, beta, O'Brien, a\"b, abc, ABC, abd and a Null. For each case [{string.Join(" ;; ", textCases.Select(c => c.Label))}]: fresh " +
            "table-type recordset, Index IX_S, Seek, read NoMatch, BOF, EOF, ID and S; MoveNext and read ID and S; MovePrevious twice and read ID and S. Asks text " +
            "key equality (case, trailing space, Null, type), the index collation order, and the row each operator selects with its neighbours.",
            p =>
            {
                foreach ((_, string op, Arg key) in textCases)
                {
                    SeekCase(p, "F", "IX_S", [A.Str(op), key], "ID", "S");
                }
            });

        (string Label, string Op, Arg Key)[] dateCases =
        [
            ("= 2020-01-15", "=", A.Date(new DateTime(2020, 1, 15))), ("= 2020-02-20 13:30", "=", A.Date(new DateTime(2020, 2, 20, 13, 30, 0))),
            ("= 2020-02-20", "=", A.Date(new DateTime(2020, 2, 20))), ("= Double 43845", "=", A.R8(43845)), ("= Long 43845", "=", A.I4(43845)),
            ("= \"1/15/2020\"", "=", A.Str("1/15/2020")), ("= \"2020-01-15\"", "=", A.Str("2020-01-15")), ("= Null", "=", A.Null),
            ("= 1899-12-30", "=", A.Date(0)), (">= 2020-02-01", ">=", A.Date(new DateTime(2020, 2, 1))), ("> 2020-12-31", ">", A.Date(new DateTime(2020, 12, 31))),
            ("< 2020-02-01", "<", A.Date(new DateTime(2020, 2, 1))), ("<= 2020-01-15", "<=", A.Date(new DateTime(2020, 1, 15))),
        ];
        yield return On("main", "seek-date-index", ["CUR-SEEK"],
            $"Index IX_D (D) over F holds a time part, a repeated date and two Nulls. For each case [{string.Join(" ;; ", dateCases.Select(c => c.Label))}] (Date VARIANTs for the " +
            "dates): fresh table-type recordset, Index IX_D, Seek, read NoMatch, BOF, EOF, ID and D; MoveNext and read; MovePrevious twice and read. Asks how date, " +
            "serial-number and string keys are coerced, how a time part and a duplicate date take part, and where Null sorts.",
            p =>
            {
                foreach ((_, string op, Arg key) in dateCases)
                {
                    SeekCase(p, "F", "IX_D", [A.Str(op), key], "ID", "D");
                }
            });

        int[] gapKeys = [5, 35, 40, 80];
        yield return On("main", "seek-gap-keys", ["CUR-SEEK"],
            $"Index IX_N (N ASC) and IX_NDESC (N DESC) over F hold N 10,20,30,40,40,50,60,70 (40 twice). For each index and each operator {string.Join(" ", SeekOps6)} and each key " +
            $"{string.Join(", ", gapKeys)} (below the range, in a gap, a duplicate, above the range): fresh table-type recordset, set the Index, Seek, read NoMatch, BOF, " +
            "EOF, ID and N; MoveNext and read; MovePrevious twice and read. Asks which row each operator selects when the key is absent or beyond either end of " +
            "an ascending and a descending index, and the following moves in both directions.",
            p =>
            {
                foreach (string index in new[] { "IX_N", "IX_NDESC" })
                {
                    foreach (string op in SeekOps6)
                    {
                        foreach (int key in gapKeys)
                        {
                            SeekCase(p, "F", index, [A.Str(op), A.I4(key)], "ID", "N");
                        }
                    }
                }
            });

        (string Index, int[] Keys)[] shapes =
        [
            ("IX_ABC", [1, 2, 1]), ("IX_ABC", [1, 2]), ("IX_ABC", [1]), ("IX_ABC", [2, 1]), ("IX_AD", [2, 1]), ("IX_AD", [2]),
        ];
        yield return On("main", "seek-three-column-mixed", ["CUR-SEEK"],
            "C3 rows (A,B,C): (1,1,1),(1,2,1),(1,2,2),(2,1,1),(2,1,2),(2,2,1),(3,1,1). Index IX_ABC is (A ASC, B DESC, C ASC), IX_AD is (A DESC, B ASC). For each of " +
            $"{string.Join("; ", shapes.Select(s => $"{s.Index} with key ({string.Join(",", s.Keys)})"))} and each operator {string.Join(" ", SeekOps5)}: fresh table-type recordset, set the " +
            "Index, Seek, read NoMatch, BOF, EOF, A, B and C; MoveNext and read; MovePrevious twice and read. Asks the value-order versus index-order meaning of each " +
            "operator on a partial key over mixed and leading-descending directions, and the neighbours of the selected row.",
            p =>
            {
                foreach ((string index, int[] keys) in shapes)
                {
                    foreach (string op in SeekOps5)
                    {
                        SeekCase(p, "C3", index, [A.Str(op), .. keys.Select(k => A.I4(k))], "A", "B", "C");
                    }
                }
            });

        (string Label, Arg Op)[] spellings =
        [
            ("=>", A.Str("=>")), ("=<", A.Str("=<")), ("!=", A.Str("!=")), ("<=>", A.Str("<=>")), (" = ", A.Str(" = ")), (">= ", A.Str(">= ")), ("\"\"", A.Str("")),
            ("Like", A.Str("Like")), ("EQ", A.Str("EQ")), ("~", A.Str("~")), ("Null", A.Null), ("Empty", A.Empty), ("Missing", A.Missing),
            ("Long 0", A.I4(0)), ("Long 61", A.I4(61)), ("Boolean True", A.Bool(true)),
        ];
        yield return On("main", "seek-operator-spelling", ["CUR-SEEK"],
            $"On a fresh table-type recordset over F with Index PK_F for each comparison argument [{string.Join(" ;; ", spellings.Select(s => s.Label))}]: Seek with that argument and the key " +
            "Long 4, read NoMatch, BOF, EOF and ID. Asks which operator spellings and argument types the method accepts and which row each selects.",
            p =>
            {
                foreach ((_, Arg op) in spellings)
                {
                    p.OpenRs("rt", "db", "F", Dao.OpenTable).Put("rt", "Index", A.Str("PK_F")).Call("rt", "Seek", op, A.I4(4)).Get("rt", "NoMatch");
                    State(p, "rt", false);
                    Close(p, "rt");
                }
            });

        yield return On("main", "seek-repeat-sequence", ["CUR-SEEK", "CUR-POS"],
            "On one table-type recordset over F with held Fields ID and N, reading NoMatch, BOF, EOF, ID and N after each call: Index IX_N; Seek \"=\" 40; Seek \"=\" 999; " +
            "MoveNext; Seek \">=\" 35; Seek \"<\" 10; MoveNext; Seek \">\" 70; MovePrevious; Seek \"<=\" 5; MoveNext; Seek \"=\" 40, MoveLast, Seek \"=\" 40, Seek " +
            "\">=\" 40; Index IX_NDESC, Seek \"=\" 40, Seek \">\" 40, Seek \"<\" 40, Seek \"=\" 999; Index PK_F, Seek \"<\" 3, Seek \"=\" 8, MoveNext. Asks the BOF/EOF " +
            "state each failed operator leaves in each direction and the recovery by the next Seek or move, across an index switch.",
            p =>
            {
                p.OpenRs("rt", "db", "F", Dao.OpenTable);
                Hold(p, "rt", "h_", "ID", "N");
                void Step(string member, params Arg[] args)
                {
                    p.Call("rt", member, args);
                    if (member == "Seek")
                    {
                        p.Get("rt", "NoMatch");
                    }
                    p.Get("rt", "BOF").Get("rt", "EOF");
                    ReadHeld(p, "h_", "ID", "N");
                }

                p.Put("rt", "Index", A.Str("IX_N"));
                Step("Seek", A.Str("="), A.I4(40));
                Step("Seek", A.Str("="), A.I4(999));
                Step("MoveNext");
                Step("Seek", A.Str(">="), A.I4(35));
                Step("Seek", A.Str("<"), A.I4(10));
                Step("MoveNext");
                Step("Seek", A.Str(">"), A.I4(70));
                Step("MovePrevious");
                Step("Seek", A.Str("<="), A.I4(5));
                Step("MoveNext");
                Step("Seek", A.Str("="), A.I4(40));
                Step("MoveLast");
                Step("Seek", A.Str("="), A.I4(40));
                Step("Seek", A.Str(">="), A.I4(40));
                p.Put("rt", "Index", A.Str("IX_NDESC"));
                Step("Seek", A.Str("="), A.I4(40));
                Step("Seek", A.Str(">"), A.I4(40));
                Step("Seek", A.Str("<"), A.I4(40));
                Step("Seek", A.Str("="), A.I4(999));
                p.Put("rt", "Index", A.Str("PK_F"));
                Step("Seek", A.Str("<"), A.I4(3));
                Step("Seek", A.Str("="), A.I4(8));
                Step("MoveNext");
                Unhold(p, "h_", "ID", "N");
                Close(p, "rt");
            });

        Arg[] indexForms =
        [
            A.Str("IX_N"), A.Str("ix_n"), A.Str(" IX_N"), A.Str("IX_N "), A.Str("PrimaryKey"), A.Str("PK_F"), A.Str(""), A.Null, A.I4(0), A.Str("NoSuch"), A.Str("F"),
        ];
        yield return On("main", "index-property-forms", ["CUR-SEEK", "CUR-POS"],
            "On a table-type recordset over F: read Index; assign Index in turn IX_N, ix_n, \" IX_N\", \"IX_N \", PrimaryKey, PK_F, \"\", Null, Long 0, NoSuch and F, reading " +
            "Index back, then MoveFirst and reading ID and N after each. Then on a dynaset, a snapshot and a forward-only recordset over F: read Index, assign PK_F, read " +
            "Index. Asks the Index spellings accepted (case, spaces, the default primary-key name), the value read back, the order that results, and which recordset " +
            "types have the property.",
            p =>
            {
                p.OpenRs("rt", "db", "F", Dao.OpenTable).Get("rt", "Index");
                Hold(p, "rt", "h_", "ID", "N");
                foreach (Arg form in indexForms)
                {
                    p.Put("rt", "Index", form).Get("rt", "Index").Call("rt", "MoveFirst");
                    ReadHeld(p, "h_", "ID", "N");
                }
                Unhold(p, "h_", "ID", "N");
                Close(p, "rt");
                foreach ((_, int type) in Types.Where(t => t.Type != Dao.OpenTable))
                {
                    p.OpenRs("rs", "db", "F", type).Get("rs", "Index").Put("rs", "Index", A.Str("PK_F")).Get("rs", "Index");
                    Close(p, "rs");
                }
            });
    }

    // PercentPosition, AbsolutePosition and Move (CUR-POS) --------------------------------------------------

    private static IEnumerable<Probe> Positions()
    {
        (string Label, Arg Value)[] percents =
        [
            ("Long 0", A.I4(0)), ("Long 50", A.I4(50)), ("Long 100", A.I4(100)), ("Double 37.5", A.R8(37.5)), ("Double 99.99", A.R8(99.99)),
            ("Double 100.5", A.R8(100.5)), ("Long -1", A.I4(-1)), ("Long 101", A.I4(101)), ("\"50\"", A.Str("50")), ("Null", A.Null), ("Empty", A.Empty),
        ];
        yield return On("bulk", "percent-assign", ["CUR-POS", "CUR-POP"],
            $"B holds 20 rows (IDs 1-20). For a table-type, a dynaset and a snapshot over B and each value [{string.Join(" ;; ", percents.Select(v => v.Label))}]: " +
            "(a) a fresh recordset with the value assigned to PercentPosition at once; read BOF, EOF, PercentPosition, AbsolutePosition (not table-type), ID and RecordCount; " +
            "(b) one recordset after MoveLast with RecordCount read, assigning each value in turn and reading the same. Asks how an assigned percentage maps to a row, " +
            "its rounding and out-of-range refusals, and how the unpopulated and populated states differ.",
            p =>
            {
                foreach ((_, int type) in PosTypes)
                {
                    foreach ((_, Arg value) in percents)
                    {
                        p.OpenRs("rs", "db", "B", type).Put("rs", "PercentPosition", value);
                        PosState(p, "rs", type != Dao.OpenTable).Get("rs", "RecordCount");
                        Close(p, "rs");
                    }
                    p.OpenRs("rs", "db", "B", type).Call("rs", "MoveLast").Get("rs", "RecordCount");
                    foreach ((_, Arg value) in percents)
                    {
                        p.Put("rs", "PercentPosition", value);
                        PosState(p, "rs", type != Dao.OpenTable);
                    }
                    Close(p, "rs");
                }
            });

        (string Label, Arg Value)[] absolutes =
        [
            ("Long 1", A.I4(1)), ("Long 2", A.I4(2)), ("Long 10", A.I4(10)), ("Long 20", A.I4(20)), ("Long 21", A.I4(21)), ("Long 0", A.I4(0)),
            ("Long -1", A.I4(-1)), ("Long 1000", A.I4(1000)), ("\"3\"", A.Str("3")), ("Double 2.5", A.R8(2.5)), ("Null", A.Null), ("Empty", A.Empty),
        ];
        yield return On("bulk", "abspos-assign", ["CUR-POS", "CUR-POP"],
            $"B holds 20 rows. For a table-type, a dynaset, a snapshot and a forward-only recordset over B and each value [{string.Join(" ;; ", absolutes.Select(v => v.Label))}]: " +
            "(a) a fresh recordset with the value assigned to AbsolutePosition at once; read BOF, EOF, AbsolutePosition, PercentPosition, ID and RecordCount; " +
            "(b) one recordset after MoveLast (reads are RecordCount first), assigning each value in turn and reading the same. Asks the base of the index, the " +
            "out-of-range and type-coercion policy, the state after a refused assignment, and which types accept it.",
            p =>
            {
                foreach ((_, int type) in Types)
                {
                    foreach ((_, Arg value) in absolutes)
                    {
                        p.OpenRs("rs", "db", "B", type).Put("rs", "AbsolutePosition", value);
                        PosState(p, "rs", true).Get("rs", "RecordCount");
                        Close(p, "rs");
                    }
                    p.OpenRs("rs", "db", "B", type).Call("rs", "MoveLast").Get("rs", "RecordCount");
                    foreach ((_, Arg value) in absolutes)
                    {
                        p.Put("rs", "AbsolutePosition", value);
                        PosState(p, "rs", true);
                    }
                    Close(p, "rs");
                }
            });

        yield return On("main", "position-assign-empty-single", ["CUR-POS"],
            "For empty table E and singleton S1 and, in turn, a dynaset, a snapshot and a table-type recordset: read PercentPosition; assign PercentPosition 50 and read " +
            "BOF, EOF and ID; assign AbsolutePosition 0 (not table-type) and read; assign AbsolutePosition 1 and read; assign PercentPosition 100 and read; MoveLast and read " +
            "PercentPosition and AbsolutePosition. Asks position assignment and reads on empty and singleton sets.",
            p =>
            {
                foreach (string table in new[] { "E", "S1" })
                {
                    foreach ((_, int type) in PosTypes)
                    {
                        bool abs = type != Dao.OpenTable;
                        p.OpenRs("rs", "db", table, type).Get("rs", "PercentPosition");
                        p.Put("rs", "PercentPosition", A.I4(50));
                        State(p, "rs", false);
                        if (abs)
                        {
                            p.Put("rs", "AbsolutePosition", A.I4(0));
                            State(p, "rs", false);
                            p.Put("rs", "AbsolutePosition", A.I4(1));
                            State(p, "rs", false);
                        }
                        p.Put("rs", "PercentPosition", A.I4(100));
                        State(p, "rs", false).Call("rs", "MoveLast").Get("rs", "PercentPosition");
                        if (abs)
                        {
                            p.Get("rs", "AbsolutePosition");
                        }
                        Close(p, "rs");
                    }
                }
            });

        (string Label, Arg[] Args)[] moves =
        [
            ("(no argument)", []), ("Integer 2", [A.I2(2)]), ("Long 2", [A.I4(2)]), ("Double 2.5", [A.R8(2.5)]), ("Double 1.5", [A.R8(1.5)]), ("Double -1.5", [A.R8(-1.5)]),
            ("Byte 2", [A.UI1(2)]), ("\"2\"", [A.Str("2")]), ("\"abc\"", [A.Str("abc")]), ("Null", [A.Null]), ("Empty", [A.Empty]), ("Missing", [A.Missing]),
            ("Boolean True", [A.Bool(true)]), ("Long max", [A.I4(int.MaxValue)]), ("Long min", [A.I4(int.MinValue)]), ("Long 7", [A.I4(7)]), ("Long 8", [A.I4(8)]),
        ];
        yield return On("main", "move-argument-forms", ["CUR-POS"],
            $"F holds eight rows. For a table-type, a dynaset, a snapshot and a forward-only recordset over F and each Move argument [{string.Join(" ;; ", moves.Select(m => m.Label))}]: " +
            "fresh recordset at its first row, call Move with the argument, read BOF, EOF, AbsolutePosition (not table-type) and ID. Asks the accepted argument types, rounding " +
            "of a fractional count, a count that lands exactly on the last row or one past it, and the refusals.",
            p =>
            {
                foreach ((_, int type) in Types)
                {
                    foreach ((_, Arg[] args) in moves)
                    {
                        p.OpenRs("rs", "db", "F", type).Call("rs", "Move", args);
                        State(p, "rs", type != Dao.OpenTable);
                        Close(p, "rs");
                    }
                }
            });

        yield return On("main", "move-start-bookmark", ["CUR-POS", "CUR-ID"],
            "On a dynaset and then a snapshot over F: walk ID and Bookmark of all rows to EOF; then for each of Move(0, b2), Move(1, b2), Move(-1, b2), Move(-3, b2), Move(20, b0), " +
            "Move(0, b7), Move(0, b8), and Move(1, X) for X = empty byte array, four zero bytes, empty string, Long 1, Null, Empty and Missing: MoveFirst, call Move with the " +
            "StartBookmark, read BOF, EOF, AbsolutePosition, ID and Bookmark. bi is the four-byte constant (i, 2, 0, 0) for the dynaset and (i, 3, 0, 0) for the snapshot (the " +
            "byte pattern of the first rows in the retained id-bookmark-shape capture; the bookmarks read by the walk show what this copy actually returns). Asks how a start " +
            "bookmark and a count combine, what is refused (empty, malformed, past-the-end bookmark), and whether a bookmark constant read elsewhere resolves.",
            p =>
            {
                foreach ((int type, byte tag) in new[] { (Dao.OpenDynaset, (byte)2), (Dao.OpenSnapshot, (byte)3) })
                {
                    Func<int, Arg> bm = i => A.Bytes((byte)i, tag, 0, 0);
                    p.OpenRs("rs", "db", "F", type);
                    Hold(p, "rs", "m_", "ID");
                    p.Until("rs", "EOF", 16, b => b.Get("m_ID", "Value").Get("rs", "Bookmark").Call("rs", "MoveNext"));
                    foreach ((int rows, Arg start) in new (int, Arg)[]
                    {
                        (0, bm(2)), (1, bm(2)), (-1, bm(2)), (-3, bm(2)), (20, bm(0)), (0, bm(7)), (0, bm(8)), (1, A.Bytes()), (1, A.Bytes(0, 0, 0, 0)),
                        (1, A.Str("")), (1, A.I4(1)), (1, A.Null), (1, A.Empty), (1, A.Missing),
                    })
                    {
                        p.Call("rs", "MoveFirst").Call("rs", "Move", A.I4(rows), start);
                        HState(p, "rs", "m_ID", true, true);
                    }
                    Unhold(p, "m_", "ID");
                    Close(p, "rs");
                }
            });

        yield return On("main", "bookmark-walk-types", ["CUR-ID", "CUR-POS"],
            "Over F in turn: a table-type recordset with no Index, with Index IX_N and with Index IX_NDESC, a dynaset and a snapshot. Hold Field ID, then repeat until EOF: " +
            "read ID, read Bookmark, MoveNext (at most 16). Then MoveLast and read Bookmark and ID, and MoveFirst and read Bookmark. Asks the exact bookmark VARIANT of every " +
            "row per type and index, and whether it depends on the visiting order or the Index.",
            p =>
            {
                foreach ((int type, string? index) in new (int, string?)[]
                {
                    (Dao.OpenTable, null), (Dao.OpenTable, "IX_N"), (Dao.OpenTable, "IX_NDESC"), (Dao.OpenDynaset, null), (Dao.OpenSnapshot, null),
                })
                {
                    p.OpenRs("rs", "db", "F", type);
                    if (index is not null)
                    {
                        p.Put("rs", "Index", A.Str(index)).Call("rs", "MoveFirst");
                    }
                    Hold(p, "rs", "m_", "ID");
                    p.Until("rs", "EOF", 16, b => b.Get("m_ID", "Value").Get("rs", "Bookmark").Call("rs", "MoveNext"));
                    p.Call("rs", "MoveLast").Get("rs", "Bookmark").Get("m_ID", "Value").Call("rs", "MoveFirst").Get("rs", "Bookmark");
                    Unhold(p, "m_", "ID");
                    Close(p, "rs");
                }
            });
    }

    // GetRows (CUR-BULK) ------------------------------------------------------------------------------------

    private static IEnumerable<Probe> Bulk()
    {
        (string Label, Arg[] Args)[] counts =
        [
            ("(no argument)", []), ("Missing", [A.Missing]), ("Long 0", [A.I4(0)]), ("Long 1", [A.I4(1)]), ("Long 3", [A.I4(3)]), ("Long -1", [A.I4(-1)]),
            ("Long 20", [A.I4(20)]), ("Long 21", [A.I4(21)]), ("Long 1000000", [A.I4(1000000)]), ("Null", [A.Null]), ("Empty", [A.Empty]), ("\"3\"", [A.Str("3")]),
            ("\"abc\"", [A.Str("abc")]), ("Double 2.5", [A.R8(2.5)]), ("Integer 3", [A.I2(3)]), ("Boolean True", [A.Bool(true)]),
        ];
        yield return On("bulk", "getrows-count-arguments", ["CUR-BULK"],
            $"B holds 20 rows of three fields. For a table-type, a dynaset, a snapshot and a forward-only recordset over B and each GetRows argument [{string.Join(" ;; ", counts.Select(c => c.Label))}]: " +
            "fresh recordset at its first row, call GetRows, read BOF, EOF and ID. The call's VARIANT is captured exactly. Asks the array VARIANT shape (dimensions, " +
            "bounds, element types) per requested count, the accepted argument types, the count beyond the row total and the position after the call.",
            p =>
            {
                foreach ((_, int type) in Types)
                {
                    foreach ((_, Arg[] args) in counts)
                    {
                        p.OpenRs("rs", "db", "B", type).Call("rs", "GetRows", args);
                        State(p, "rs", false);
                        Close(p, "rs");
                    }
                }
            });

        yield return On("bulk", "getrows-beyond-eof", ["CUR-BULK", "CUR-POS"],
            "For a table-type, a dynaset, a snapshot and a forward-only recordset over B (20 rows): (a) Move 17 (ID 18), read BOF, EOF and ID, GetRows(5), read, GetRows(5) again, " +
            "read, MoveFirst, read, MoveLast, GetRows(1) twice, read; (b) fresh: GetRows(20), read, GetRows(1), read; (c) fresh: GetRows(2) x2 with a read between, GetRows(0), read, " +
            "GetRows(2); (d) empty table E: GetRows(1), read. Asks the short return at the end, the array shape of a partial and of an empty result, the position and BOF/EOF after " +
            "each, and a second call at EOF.",
            p =>
            {
                foreach ((_, int type) in Types)
                {
                    p.OpenRs("rs", "db", "B", type).Call("rs", "Move", A.I4(17));
                    State(p, "rs", false).Call("rs", "GetRows", A.I4(5));
                    State(p, "rs", false).Call("rs", "GetRows", A.I4(5));
                    State(p, "rs", false).Call("rs", "MoveFirst");
                    State(p, "rs", false).Call("rs", "MoveLast").Call("rs", "GetRows", A.I4(1)).Call("rs", "GetRows", A.I4(1));
                    State(p, "rs", false);
                    Close(p, "rs");
                    p.OpenRs("rs", "db", "B", type).Call("rs", "GetRows", A.I4(20));
                    State(p, "rs", false).Call("rs", "GetRows", A.I4(1));
                    State(p, "rs", false);
                    Close(p, "rs");
                    p.OpenRs("rs", "db", "B", type).Call("rs", "GetRows", A.I4(2));
                    State(p, "rs", false).Call("rs", "GetRows", A.I4(2)).Call("rs", "GetRows", A.I4(0));
                    State(p, "rs", false).Call("rs", "GetRows", A.I4(2));
                    State(p, "rs", false);
                    Close(p, "rs");
                    p.OpenRs("rs", "db", "E", type).Call("rs", "GetRows", A.I4(1));
                    State(p, "rs", false);
                    Close(p, "rs");
                }
            });

        const string calc = "SELECT ID, Cu * 2 AS CuX, Dt + 1 AS DtX, Tx & 'x' AS TxX, Bo AND True AS BoX FROM W";
        yield return On("bulk", "getrows-shape-types", ["CUR-BULK"],
            "W holds row 1 with every scalar type set, row 2 with every field Null but ID, and row 3 with a 3000-character Memo. For a table-type, a dynaset, a snapshot and a " +
            "forward-only recordset over W: GetRows(3), then read BOF, EOF and ID. Then GetRows(3) on a dynaset, a snapshot and a forward-only recordset over \"" + calc + "\" and " +
            "GetRows(2) over WD (a Decimal field) in a table-type, a dynaset and a snapshot. Asks the VARIANT type of each cell per field type and source (Boolean, Byte, Integer, " +
            "Currency, Double, Single, Date, Text, Memo, Decimal, Null), the Memo length returned, and the array dimensions.",
            p =>
            {
                foreach ((_, int type) in Types)
                {
                    p.OpenRs("rs", "db", "W", type).Call("rs", "GetRows", A.I4(3));
                    State(p, "rs", false);
                    Close(p, "rs");
                }
                foreach (int type in new[] { Dao.OpenDynaset, Dao.OpenSnapshot, Dao.OpenForwardOnly })
                {
                    p.OpenRs("rs", "db", calc, type).Call("rs", "GetRows", A.I4(3));
                    State(p, "rs", false);
                    Close(p, "rs");
                }
                foreach ((_, int type) in PosTypes)
                {
                    p.OpenRs("rs", "db", "WD", type).Call("rs", "GetRows", A.I4(2));
                    State(p, "rs", false);
                    Close(p, "rs");
                }
            });

        yield return On("bulk", "getrows-edit-buffer", ["CUR-BULK", "CUR-EDIT"],
            "On a dynaset over W with held Fields ID, Db and Mm, at row 1: (a) Edit, assign Db 99.5 and Mm \"edited memo\" without Update, GetRows(0), read EditMode and ID, GetRows(1), " +
            "read EditMode, ID, Db and Mm, CancelUpdate, Errors, EditMode, MoveFirst, read Db and Mm. (b) AddNew, assign ID 7, Db 7.5 and Mm \"added\", GetRows(3), read EditMode, ID, Db, " +
            "GetRows(1), CancelUpdate, Errors, EditMode, RecordCount. (c) MoveFirst, Edit, assign Db 55.5 and Mm \"m3\", GetRows(3), CancelUpdate, MoveFirst, read Db and Mm. Asks whether " +
            "GetRows returns buffer or stored values for the row being edited, what it does to the pending edit and to the current row, and its result for an AddNew buffer.",
            p =>
            {
                p.OpenRs("rs", "db", "W", Dao.OpenDynaset);
                Hold(p, "rs", "w_", "ID", "Db", "Mm");
                p.Call("rs", "Edit").Put("w_Db", "Value", A.R8(99.5)).Put("w_Mm", "Value", A.Str("edited memo")).Call("rs", "GetRows", A.I4(0))
                    .Get("rs", "EditMode").Get("w_ID", "Value").Call("rs", "GetRows", A.I4(1)).Get("rs", "EditMode");
                ReadHeld(p, "w_", "ID", "Db", "Mm").Call("rs", "CancelUpdate").Errors().Get("rs", "EditMode").Call("rs", "MoveFirst");
                ReadHeld(p, "w_", "Db", "Mm");
                p.Call("rs", "AddNew").Put("w_ID", "Value", A.I4(7)).Put("w_Db", "Value", A.R8(7.5)).Put("w_Mm", "Value", A.Str("added")).Call("rs", "GetRows", A.I4(3))
                    .Get("rs", "EditMode");
                ReadHeld(p, "w_", "ID", "Db").Call("rs", "GetRows", A.I4(1)).Call("rs", "CancelUpdate").Errors().Get("rs", "EditMode").Get("rs", "RecordCount");
                p.Call("rs", "MoveFirst").Call("rs", "Edit").Put("w_Db", "Value", A.R8(55.5)).Put("w_Mm", "Value", A.Str("m3")).Call("rs", "GetRows", A.I4(3))
                    .Call("rs", "CancelUpdate").Call("rs", "MoveFirst");
                ReadHeld(p, "w_", "Db", "Mm");
                Unhold(p, "w_", "ID", "Db", "Mm");
                Close(p, "rs");
            }, write: true);

        yield return On("bulk", "getrows-deleted-row", ["CUR-BULK", "CUR-POP", "CUR-POS"],
            "B holds 20 rows. (a) Dynaset D1 at row 1 and nothing else read: db.Execute DELETE FROM B WHERE ID IN (3,4) (dbFailOnError); GetRows(6), read BOF, EOF and ID; GetRows(6) " +
            "again, read. (b) Dynaset D2 at row 1: db.Execute DELETE FROM B WHERE ID IN (9,10); Move 8 and read; MoveFirst; GetRows(6), read; GetRows(6), read; GetRows(6), read. " +
            "(c) Dynaset D3: Move 11 (ID 12 as listed before the deletions), read, db.Execute DELETE FROM B WHERE ID = 12, GetRows(2), read, GetRows(2), read. (d) Snapshot S: " +
            "db.Execute DELETE FROM B WHERE ID = 15 after opening, GetRows(20), read. Asks whether a deletion made elsewhere is skipped, returned or fatal inside GetRows when it was " +
            "undiscovered, discovered or on the current row, the short-return frontier and the dynaset against the snapshot.",
            p =>
            {
                p.OpenRs("d1", "db", "B", Dao.OpenDynaset);
                Exec(p, "DELETE FROM B WHERE ID IN (3, 4)");
                p.Call("d1", "GetRows", A.I4(6));
                State(p, "d1", false).Call("d1", "GetRows", A.I4(6));
                State(p, "d1", false);
                Close(p, "d1");
                p.OpenRs("d2", "db", "B", Dao.OpenDynaset);
                Exec(p, "DELETE FROM B WHERE ID IN (9, 10)");
                p.Call("d2", "Move", A.I4(8));
                State(p, "d2", false).Call("d2", "MoveFirst").Call("d2", "GetRows", A.I4(6));
                State(p, "d2", false).Call("d2", "GetRows", A.I4(6));
                State(p, "d2", false).Call("d2", "GetRows", A.I4(6));
                State(p, "d2", false);
                Close(p, "d2");
                p.OpenRs("d3", "db", "B", Dao.OpenDynaset).Call("d3", "Move", A.I4(11));
                State(p, "d3", false);
                Exec(p, "DELETE FROM B WHERE ID = 12");
                p.Call("d3", "GetRows", A.I4(2));
                State(p, "d3", false).Call("d3", "GetRows", A.I4(2));
                State(p, "d3", false);
                Close(p, "d3");
                p.OpenRs("sn", "db", "B", Dao.OpenSnapshot);
                Exec(p, "DELETE FROM B WHERE ID = 15");
                p.Call("sn", "GetRows", A.I4(20));
                State(p, "sn", false);
                Close(p, "sn");
            }, write: true);
    }

    // Requery (CUR-LIFE, CUR-POP) ---------------------------------------------------------------------------

    private static IEnumerable<Probe> Requeries()
    {
        yield return On("main", "requery-types-state", ["CUR-LIFE", "CUR-POS"],
            "For a table-type, a dynaset, a snapshot and a forward-only recordset over F, with no data change: Move 2, read BOF, EOF, AbsolutePosition (not table-type) and ID; Requery; " +
            "read the same, RecordCount, MoveNext and read, MovePrevious and read; MoveLast, MoveNext (EOF), Requery, read; MoveFirst, MovePrevious (BOF), Requery, read; Requery again; " +
            "Close, then Requery on the closed recordset. Asks the position and BOF/EOF state a plain Requery leaves per type, whether it is refused for a type, and the closed-recordset error.",
            p =>
            {
                foreach ((_, int type) in Types)
                {
                    bool abs = type != Dao.OpenTable;
                    p.OpenRs("rs", "db", "F", type).Call("rs", "Move", A.I4(2));
                    State(p, "rs", abs).Call("rs", "Requery");
                    State(p, "rs", abs).Get("rs", "RecordCount").Call("rs", "MoveNext");
                    State(p, "rs", abs).Call("rs", "MovePrevious");
                    State(p, "rs", abs).Call("rs", "MoveLast").Call("rs", "MoveNext").Call("rs", "Requery");
                    State(p, "rs", abs).Call("rs", "MoveFirst").Call("rs", "MovePrevious").Call("rs", "Requery");
                    State(p, "rs", abs).Call("rs", "Requery");
                    State(p, "rs", abs).Call("rs", "Close").Call("rs", "Requery").Release("rs");
                }
            });

        yield return On("main", "requery-empty-to-populated", ["CUR-LIFE", "CUR-POP"],
            "For a dynaset, a snapshot and a table-type recordset in turn over empty table E: read BOF, EOF and RecordCount; db.Execute INSERT INTO E VALUES (1,'a'); read; Requery; read " +
            "BOF, EOF, ID and RecordCount; MoveLast; db.Execute INSERT INTO E VALUES (2,'b'); MoveNext and read; Requery and read; db.Execute DELETE FROM E; read; Requery; read BOF, EOF, " +
            "ID and RecordCount. Asks how an empty recordset acquires, and after Requery loses, rows, and the position after Requery of a set that was exhausted.",
            p =>
            {
                foreach ((_, int type) in PosTypes)
                {
                    p.OpenRs("rs", "db", "E", type).Get("rs", "BOF").Get("rs", "EOF").Get("rs", "RecordCount");
                    Exec(p, "INSERT INTO E VALUES (1, 'a')");
                    State(p, "rs", false).Call("rs", "Requery");
                    State(p, "rs", false).Get("rs", "RecordCount").Call("rs", "MoveLast");
                    Exec(p, "INSERT INTO E VALUES (2, 'b')");
                    p.Call("rs", "MoveNext");
                    State(p, "rs", false).Call("rs", "Requery");
                    State(p, "rs", false).Get("rs", "RecordCount");
                    Exec(p, "DELETE FROM E");
                    State(p, "rs", false).Call("rs", "Requery");
                    State(p, "rs", false).Get("rs", "RecordCount");
                    Close(p, "rs");
                }
            }, write: true);

        yield return On("main", "requery-parameter-query", ["CUR-LIFE", "CUR-POP"],
            "Stored query QP (PARAMETERS pN LONG; SELECT ID, N FROM F WHERE N >= pN ORDER BY ID). Through QueryDef QP set Parameters pN 30; OpenRecordset dynaset; walk ID and N; set pN 55; " +
            "Requery with no argument, MoveFirst, walk; Requery(qd), MoveFirst, walk; set pN 15; Requery(qd), MoveFirst, walk; read Type; Requery with Missing, Null, Long 1 and \"QP\". " +
            "Then OpenRecordset snapshot from QP with pN 70, Requery, walk. Then a dynaset over \"SELECT ID, N FROM F\" with Requery(qd) after pN 45, walk. Asks whether Requery re-reads the " +
            "current parameter value, what the supplied QueryDef replaces, the accepted argument types and the recordset type afterwards.",
            p =>
            {
                p.GetBind("qds", "db", "QueryDefs").Item("qd", "qds", A.Str("QP")).GetBind("qps", "qd", "Parameters").Item("qp", "qps", A.Str("pN"));
                p.Put("qp", "Value", A.I4(30)).CallBind("rs", "qd", "OpenRecordset", A.I4(Dao.OpenDynaset));
                Forward(p, "rs", 16, "ID", "N");
                p.Put("qp", "Value", A.I4(55)).Call("rs", "Requery").Call("rs", "MoveFirst");
                Forward(p, "rs", 16, "ID", "N");
                p.Call("rs", "Requery", A.Obj("qd")).Call("rs", "MoveFirst");
                Forward(p, "rs", 16, "ID", "N");
                p.Put("qp", "Value", A.I4(15)).Call("rs", "Requery", A.Obj("qd")).Call("rs", "MoveFirst");
                Forward(p, "rs", 16, "ID", "N");
                p.Get("rs", "Type").Call("rs", "Requery", A.Missing).Call("rs", "Requery", A.Null).Call("rs", "Requery", A.I4(1)).Call("rs", "Requery", A.Str("QP"));
                Close(p, "rs");
                p.Put("qp", "Value", A.I4(70)).CallBind("rn", "qd", "OpenRecordset", A.I4(Dao.OpenSnapshot)).Call("rn", "Requery").Call("rn", "MoveFirst");
                Forward(p, "rn", 16, "ID", "N");
                Close(p, "rn");
                p.OpenRs("rq", "db", "SELECT ID, N FROM F", Dao.OpenDynaset).Put("qp", "Value", A.I4(45)).Call("rq", "Requery", A.Obj("qd")).Call("rq", "MoveFirst");
                Forward(p, "rq", 16, "ID", "N");
                Close(p, "rq");
                p.Release("qp").Release("qps").Release("qd").Release("qds");
            });

        yield return On("main", "requery-family", ["CUR-LIFE", "CUR-ID", "CUR-POP"],
            "Dynaset P over F at row 3 (Move 2), held Field N of P; Clone into C (held N of C) and C.Move 1; derived recordset D = P.OpenRecordset() with held N. P.Requery; then read C's " +
            "BOF, EOF, ID, Bookmark, RecordCount and held N; C.MoveNext and read ID; read D's ID and held N and RecordCount; read P's BOF, EOF, ID, held N and Bookmark. Then " +
            "db.Execute INSERT INTO F (ID, N) VALUES (9, 90); C.Requery; read P's RecordCount and ID, walk P's ID to EOF from its position; P.Requery; read C's RecordCount and ID, MoveLast and " +
            "read. Asks whether Requery on the parent affects the clone, the derived recordset and held Field objects, and what a clone's Requery makes visible to the parent.",
            p =>
            {
                p.OpenRs("rp", "db", "F", Dao.OpenDynaset).Call("rp", "Move", A.I4(2));
                Hold(p, "rp", "p_", "N");
                p.CallBind("rc", "rp", "Clone").Call("rc", "Move", A.I4(1));
                Hold(p, "rc", "c_", "N");
                p.CallBind("rd", "rp", "OpenRecordset");
                Hold(p, "rd", "d_", "N");
                p.Call("rp", "Requery");
                State(p, "rc", false).Get("rc", "Bookmark").Get("rc", "RecordCount").Get("c_N", "Value").Call("rc", "MoveNext");
                Val(p, "rc", "ID").Get("d_N", "Value").Get("rd", "RecordCount");
                Val(p, "rd", "ID");
                State(p, "rp", false).Get("p_N", "Value").Get("rp", "Bookmark");
                Exec(p, "INSERT INTO F (ID, N) VALUES (9, 90)");
                p.Call("rc", "Requery").Get("rp", "RecordCount");
                Val(p, "rp", "ID");
                Forward(p, "rp", 16, "ID");
                p.Call("rp", "Requery").Get("rc", "RecordCount");
                Val(p, "rc", "ID").Call("rc", "MoveLast");
                Val(p, "rc", "ID");
                Unhold(p, "p_", "N");
                Unhold(p, "c_", "N");
                Unhold(p, "d_", "N");
                Close(p, "rd");
                Close(p, "rc");
                Close(p, "rp");
            }, write: true);
    }

    // Clone and LastModified (CUR-LIFE, CUR-ID) -------------------------------------------------------------

    private static IEnumerable<Probe> Clones()
    {
        yield return On("main", "clone-by-type-and-properties", ["CUR-LIFE", "CUR-SEEK"],
            "Table-type: Index IX_N, MoveNext twice, Clone into C; read C's Type, Index, BOF, EOF, ID and Bookmark; C Seek \"=\" 40 and read NoMatch and ID; C MoveFirst, read P's ID; " +
            "assign P's Index IX_NDESC and read C's Index and ID. Snapshot: Move 2, Clone, read Type, BOF, EOF, AbsolutePosition and ID, C MoveLast, read P's ID, RecordCount of both. " +
            "Forward-only: MoveNext, Clone, read Type, BOF, EOF and ID. Dynaset with Filter \"N > 20\" and Sort \"N DESC\" set before the Clone: Move 1, Clone, read C's Filter, Sort, Type " +
            "and RecordCount after MoveLast, walk ID; assign P's Filter \"N > 50\"; read C's Filter; Clone again into C2, read its Filter and walk. Then on the dynaset Clone with the arguments " +
            "Long 4, Long 2 and Null. Asks the clone's type, inherited properties and starting position per type, which members are shared with the parent, and which types or arguments refuse Clone.",
            p =>
            {
                p.OpenRs("rt", "db", "F", Dao.OpenTable).Put("rt", "Index", A.Str("IX_N")).Call("rt", "MoveFirst").Call("rt", "MoveNext").Call("rt", "MoveNext");
                p.CallBind("rc", "rt", "Clone").Get("rc", "Type").Get("rc", "Index");
                State(p, "rc", false).Get("rc", "Bookmark").Call("rc", "Seek", A.Str("="), A.I4(40)).Get("rc", "NoMatch");
                Val(p, "rc", "ID").Call("rc", "MoveFirst");
                Val(p, "rt", "ID").Put("rt", "Index", A.Str("IX_NDESC")).Get("rc", "Index");
                Val(p, "rc", "ID");
                Close(p, "rc");
                Close(p, "rt");
                p.OpenRs("rn", "db", "F", Dao.OpenSnapshot).Call("rn", "Move", A.I4(2)).CallBind("rc", "rn", "Clone").Get("rc", "Type");
                State(p, "rc", true).Call("rc", "MoveLast");
                Val(p, "rn", "ID").Get("rn", "RecordCount").Get("rc", "RecordCount");
                Close(p, "rc");
                Close(p, "rn");
                p.OpenRs("rf", "db", "F", Dao.OpenForwardOnly).Call("rf", "MoveNext").CallBind("rc", "rf", "Clone").Get("rc", "Type");
                State(p, "rc", false);
                Close(p, "rc");
                Close(p, "rf");
                p.OpenRs("rd", "db", "F", Dao.OpenDynaset).Put("rd", "Filter", A.Str("N > 20")).Put("rd", "Sort", A.Str("N DESC")).Call("rd", "Move", A.I4(1));
                p.CallBind("rc", "rd", "Clone").Get("rc", "Filter").Get("rc", "Sort").Get("rc", "Type").Call("rc", "MoveLast").Get("rc", "RecordCount").Call("rc", "MoveFirst");
                Forward(p, "rc", 16, "ID");
                p.Put("rd", "Filter", A.Str("N > 50")).Get("rc", "Filter").CallBind("rc2", "rd", "Clone").Get("rc2", "Filter");
                Forward(p, "rc2", 16, "ID");
                Close(p, "rc2");
                Close(p, "rc");
                foreach (Arg arg in new[] { A.I4(4), A.I4(2), A.Null })
                {
                    p.CallBind("rx", "rd", "Clone", arg).Get("rx", "Type").Get("rx", "Updatable");
                    Close(p, "rx");
                }
                Close(p, "rd");
            });

        yield return On("main", "clone-bookmark-sharing", ["CUR-LIFE", "CUR-ID"],
            "Dynaset P over F, Clone C, independent dynaset Q, snapshot S and its clone SC. Read Bookmark of P, C and Q each at row 3 (Move 2; for C and Q MoveFirst first), and of S and SC at row 3. " +
            "Then, for each of P, C, Q, S and SC: MoveFirst, assign Bookmark the constant (2,2,0,0) (dynaset pattern) and then (2,3,0,0) (snapshot pattern), reading BOF, EOF and ID after each " +
            "assignment and the cursor's own Bookmark. Close P and assign (2,2,0,0) to C. Asks whether a bookmark constant is accepted by another cursor, a clone and a different recordset type, " +
            "and whether a clone survives the parent.",
            p =>
            {
                p.OpenRs("rp", "db", "F", Dao.OpenDynaset).CallBind("rc", "rp", "Clone").OpenRs("rq", "db", "F", Dao.OpenDynaset)
                    .OpenRs("rs", "db", "F", Dao.OpenSnapshot).CallBind("rsc", "rs", "Clone");
                p.Call("rp", "Move", A.I4(2)).Get("rp", "Bookmark");
                foreach (string rs in new[] { "rc", "rq", "rs", "rsc" })
                {
                    p.Call(rs, "MoveFirst").Call(rs, "Move", A.I4(2)).Get(rs, "Bookmark");
                }
                foreach (string rs in new[] { "rp", "rc", "rq", "rs", "rsc" })
                {
                    foreach (Arg bm in new[] { A.Bytes(2, 2, 0, 0), A.Bytes(2, 3, 0, 0) })
                    {
                        p.Call(rs, "MoveFirst").Put(rs, "Bookmark", bm);
                        State(p, rs, false).Get(rs, "Bookmark");
                    }
                }
                Close(p, "rp");
                p.Put("rc", "Bookmark", A.Bytes(2, 2, 0, 0));
                State(p, "rc", false);
                Close(p, "rc");
                Close(p, "rq");
                Close(p, "rsc");
                Close(p, "rs");
            });

        yield return On("main", "clone-chain-close-orders", ["CUR-LIFE"],
            "Dynaset P over F at row 2; Clone C1 of P; Clone C2 of C1; read BOF, EOF and ID of each. C1.Close; C2.MoveNext and read ID; read C1's EOF; P.MoveNext and read ID; P.Close; " +
            "C2.MoveNext, read ID and RecordCount; C2.Close. Then dynaset P2, Clones CA and CB: release CA without Close; read CB's EOF and ID; release P2 without Close; CB.MoveNext, read ID " +
            "and RecordCount; CB.Close. Asks which close or release orders of a parent, clone and clone-of-clone leave the others usable, and the errors of the closed ones.",
            p =>
            {
                p.OpenRs("rp", "db", "F", Dao.OpenDynaset).Call("rp", "Move", A.I4(1)).CallBind("c1", "rp", "Clone").CallBind("c2", "c1", "Clone");
                State(p, "rp", false);
                State(p, "c1", false);
                State(p, "c2", false).Call("c1", "Close").Call("c2", "MoveNext");
                State(p, "c2", false).Get("c1", "EOF").Call("rp", "MoveNext");
                State(p, "rp", false).Call("rp", "Close").Call("c2", "MoveNext");
                State(p, "c2", false).Get("c2", "RecordCount").Call("c2", "Close").Release("c2").Release("c1").Release("rp");
                p.OpenRs("p2", "db", "F", Dao.OpenDynaset).CallBind("ca", "p2", "Clone").CallBind("cb", "p2", "Clone").Release("ca").Get("cb", "EOF");
                Val(p, "cb", "ID").Release("p2").Call("cb", "MoveNext");
                State(p, "cb", false).Get("cb", "RecordCount").Call("cb", "Close").Release("cb");
            });

        yield return On("main", "clone-write-visibility", ["CUR-LIFE", "CUR-POP", "CUR-EDIT"],
            "Dynaset P at row 2 (Move 1), held Fields N and S; Clone C at the same row (held N and S). C: Edit, assign S \"clone\" and N 222, Update. Read P's held S and N, C's; P.Move 0 and read; " +
            "P.MoveNext, P.MovePrevious, read. C: AddNew, assign ID 9, N 99, S \"cn\", Update; read P's RecordCount, P.MoveLast and read ID, read C's RecordCount. C: Delete the current row; " +
            "read P's BOF, EOF, ID and held S, P.Move 0, read again. P: Delete its current row; read C's BOF, EOF and ID, C.Move 0, C.Requery, read. Asks which edits, additions and " +
            "deletions made through one family member the other sees without Requery, and the current state after each.",
            p =>
            {
                p.OpenRs("rp", "db", "F", Dao.OpenDynaset).Call("rp", "Move", A.I4(1));
                Hold(p, "rp", "p_", "N", "S");
                p.CallBind("rc", "rp", "Clone");
                Hold(p, "rc", "c_", "ID", "N", "S");
                p.Call("rc", "Edit").Put("c_S", "Value", A.Str("clone")).Put("c_N", "Value", A.I4(222)).Call("rc", "Update");
                ReadHeld(p, "p_", "S", "N");
                ReadHeld(p, "c_", "S", "N").Call("rp", "Move", A.I4(0));
                ReadHeld(p, "p_", "S", "N").Call("rp", "MoveNext").Call("rp", "MovePrevious");
                ReadHeld(p, "p_", "S", "N");
                p.Call("rc", "AddNew").Put("c_ID", "Value", A.I4(9)).Put("c_N", "Value", A.I4(99)).Put("c_S", "Value", A.Str("cn")).Call("rc", "Update");
                p.Get("rp", "RecordCount").Call("rp", "MoveLast");
                Val(p, "rp", "ID").Get("rc", "RecordCount").Call("rc", "Delete");
                State(p, "rp", false).Get("p_S", "Value").Call("rp", "Move", A.I4(0));
                State(p, "rp", false).Get("p_S", "Value").Call("rp", "Delete");
                State(p, "rc", false).Call("rc", "Move", A.I4(0)).Call("rc", "Requery");
                State(p, "rc", false);
                Unhold(p, "p_", "N", "S");
                Unhold(p, "c_", "ID", "N", "S");
                Close(p, "rc");
                Close(p, "rp");
            }, write: true);

        yield return On("main", "lastmodified-clone-family", ["CUR-LIFE", "CUR-ID", "CUR-EDIT"],
            "Dynaset P over F, held N: read P's LastModified before any change; Clone C, read C's LastModified; P Edit/Update N 111 on its first row and read LastModified of P and C; C Move 1, " +
            "Edit/Update N 222 and read LastModified of C and P; C.Requery, read both; P.Requery, read both; P AddNew (ID 9, N 90)/Update, read both; C Delete its current row, read both; " +
            "P Move 0 and read both. Asks whether LastModified is inherited by a clone, shared across the family, per-cursor, retained or cleared by Requery and deletion.",
            p =>
            {
                p.OpenRs("rp", "db", "F", Dao.OpenDynaset).Get("rp", "LastModified");
                Hold(p, "rp", "p_", "ID", "N");
                p.CallBind("rc", "rp", "Clone").Get("rc", "LastModified");
                p.Call("rp", "Edit").Put("p_N", "Value", A.I4(111)).Call("rp", "Update").Get("rp", "LastModified").Get("rc", "LastModified");
                p.Call("rc", "Move", A.I4(1));
                Hold(p, "rc", "c_", "N");
                p.Call("rc", "Edit").Put("c_N", "Value", A.I4(222)).Call("rc", "Update").Get("rc", "LastModified").Get("rp", "LastModified");
                p.Call("rc", "Requery").Get("rc", "LastModified").Get("rp", "LastModified");
                p.Call("rp", "Requery").Get("rc", "LastModified").Get("rp", "LastModified");
                p.Call("rp", "AddNew").Put("p_ID", "Value", A.I4(9)).Put("p_N", "Value", A.I4(90)).Call("rp", "Update").Get("rp", "LastModified").Get("rc", "LastModified");
                p.Call("rc", "Delete").Get("rc", "LastModified").Get("rp", "LastModified").Call("rp", "Move", A.I4(0)).Get("rc", "LastModified").Get("rp", "LastModified");
                Unhold(p, "p_", "ID", "N");
                Unhold(p, "c_", "N");
                Close(p, "rc");
                Close(p, "rp");
            }, write: true);

        yield return On("main", "lastmodified-addnew", ["CUR-EDIT", "CUR-ID"],
            "For a dynaset and a table-type recordset (Index PK_F) over F, and a snapshot and a forward-only recordset: read LastModified at open; AddNew, read LastModified; assign ID 20 " +
            "and N 200, read; Update, read LastModified, Bookmark and ID; MoveFirst, read LastModified; Edit, Update, read; AddNew, CancelUpdate, read; Delete, read. Asks when LastModified is " +
            "set (AddNew, assignment, Update), whether it survives moves, CancelUpdate and Delete, and which types answer.",
            p =>
            {
                int id = 20;
                foreach ((_, int type) in Types)
                {
                    p.OpenRs("rs", "db", "F", type);
                    if (type == Dao.OpenTable)
                    {
                        p.Put("rs", "Index", A.Str("PK_F")).Call("rs", "MoveFirst");
                    }
                    Hold(p, "rs", "a_", "ID", "N");
                    p.Get("rs", "LastModified").Call("rs", "AddNew").Get("rs", "LastModified").Put("a_ID", "Value", A.I4(id++)).Put("a_N", "Value", A.I4(200)).Get("rs", "LastModified")
                        .Call("rs", "Update").Get("rs", "LastModified").Get("rs", "Bookmark").Get("a_ID", "Value").Call("rs", "MoveFirst").Get("rs", "LastModified")
                        .Call("rs", "Edit").Call("rs", "Update").Get("rs", "LastModified").Call("rs", "AddNew").Call("rs", "CancelUpdate").Get("rs", "LastModified")
                        .Call("rs", "Delete").Get("rs", "LastModified");
                    Unhold(p, "a_", "ID", "N");
                    Close(p, "rs");
                }
            }, write: true);
    }

    // EditMode (CUR-EDIT) -----------------------------------------------------------------------------------

    private static IEnumerable<Probe> Edits()
    {
        yield return On("main", "editmode-by-type", ["CUR-EDIT"],
            "For a table-type (Index PK_F), a dynaset, a snapshot and a forward-only recordset over F, with held Field N: read EditMode and Updatable; Edit, EditMode; CancelUpdate, EditMode; " +
            "AddNew, EditMode; CancelUpdate, EditMode; Edit, assign N 5, Update, EditMode; Delete, EditMode, BOF, EOF; Errors after the sequence. Asks which transitions each type " +
            "accepts or refuses and the current-row state after the Delete.",
            p =>
            {
                foreach ((_, int type) in Types)
                {
                    p.OpenRs("rs", "db", "F", type);
                    if (type == Dao.OpenTable)
                    {
                        p.Put("rs", "Index", A.Str("PK_F")).Call("rs", "MoveFirst");
                    }
                    Hold(p, "rs", "e_", "N");
                    p.Get("rs", "EditMode").Get("rs", "Updatable").Call("rs", "Edit").Get("rs", "EditMode").Call("rs", "CancelUpdate").Get("rs", "EditMode")
                        .Call("rs", "AddNew").Get("rs", "EditMode").Call("rs", "CancelUpdate").Get("rs", "EditMode")
                        .Call("rs", "Edit").Put("e_N", "Value", A.I4(5)).Call("rs", "Update").Get("rs", "EditMode")
                        .Call("rs", "Delete").Get("rs", "EditMode").Get("rs", "BOF").Get("rs", "EOF").Errors();
                    Unhold(p, "e_", "N");
                    Close(p, "rs");
                }
            }, write: true);

        (string Label, Action<P> Steps)[] transitions =
        [
            ("Edit then AddNew", q => q.Call("rs", "Edit").Call("rs", "AddNew")),
            ("AddNew then Edit", q => q.Call("rs", "AddNew").Call("rs", "Edit")),
            ("Edit then Delete", q => q.Call("rs", "Edit").Call("rs", "Delete")),
            ("AddNew then Delete", q => q.Call("rs", "AddNew").Call("rs", "Delete")),
            ("Edit, Edit, Update, Update", q => q.Call("rs", "Edit").Call("rs", "Edit").Call("rs", "Update").Call("rs", "Update")),
            ("Edit then Update with no assignment", q => q.Call("rs", "Edit").Call("rs", "Update")),
            ("AddNew then Update with nothing assigned", q => q.Call("rs", "AddNew").Call("rs", "Update")),
            ("MoveLast, MoveNext, Edit", q => q.Call("rs", "MoveLast").Call("rs", "MoveNext").Call("rs", "Edit")),
            ("MoveLast, MoveNext, AddNew, assign ID 20, Update", q => q.Call("rs", "MoveLast").Call("rs", "MoveNext").Call("rs", "AddNew").Put("e_ID", "Value", A.I4(20)).Call("rs", "Update")),
            ("MovePrevious (BOF), Delete", q => q.Call("rs", "MovePrevious").Call("rs", "Delete")),
            ("MovePrevious (BOF), Edit", q => q.Call("rs", "MovePrevious").Call("rs", "Edit")),
            ("CancelUpdate with nothing pending", q => q.Call("rs", "CancelUpdate")),
            ("Update with nothing pending", q => q.Call("rs", "Update")),
        ];
        yield return On("main", "editmode-unusual-transitions", ["CUR-EDIT", "CUR-POS"],
            $"For each sequence [{string.Join(" ;; ", transitions.Select(t => t.Label))}], on a fresh dynaset over F with held Fields ID and N: run the calls, then read EditMode, Errors, BOF, EOF and N; " +
            "then CancelUpdate, EditMode and Errors; close. Asks the EditMode state, the error and the pending buffer after each call in an unusual order, at BOF and at EOF.",
            p =>
            {
                foreach ((_, Action<P> steps) in transitions)
                {
                    p.OpenRs("rs", "db", "F", Dao.OpenDynaset);
                    Hold(p, "rs", "e_", "ID", "N");
                    steps(p);
                    p.Get("rs", "EditMode").Errors().Get("rs", "BOF").Get("rs", "EOF").Get("e_N", "Value").Call("rs", "CancelUpdate").Get("rs", "EditMode").Errors();
                    Unhold(p, "e_", "ID", "N");
                    Close(p, "rs");
                }
            }, write: true);

        (string Label, Action<P> Do)[] operations =
        [
            ("Requery", q => q.Call("rs", "Requery")),
            ("Clone", q => q.CallBind("rc", "rs", "Clone")),
            ("assign Filter", q => q.Put("rs", "Filter", A.Str("N > 20"))),
            ("assign Sort", q => q.Put("rs", "Sort", A.Str("N DESC"))),
            ("OpenRecordset", q => q.CallBind("rd", "rs", "OpenRecordset")),
            ("FindFirst \"N = 40\"", q => q.Call("rs", "FindFirst", A.Str("N = 40"))),
            ("assign Bookmark (2,2,0,0)", q => q.Put("rs", "Bookmark", A.Bytes(2, 2, 0, 0))),
            ("Move 0", q => q.Call("rs", "Move", A.I4(0))),
            ("MoveNext", q => q.Call("rs", "MoveNext")),
            ("MoveLast", q => q.Call("rs", "MoveLast")),
            ("read RecordCount", q => q.Get("rs", "RecordCount")),
            ("GetRows(1)", q => q.Call("rs", "GetRows", A.I4(1))),
            ("Edit again", q => q.Call("rs", "Edit")),
            ("AddNew", q => q.Call("rs", "AddNew")),
            ("Delete", q => q.Call("rs", "Delete")),
            ("Update", q => q.Call("rs", "Update")),
            ("Close the recordset", q => q.Call("rs", "Close")),
            ("db.Execute UPDATE of the same row", q => q.Call("db", "Execute", A.Str("UPDATE F SET N = N + 1 WHERE ID = 1"), A.I4(Dao.FailOnError))),
            ("db.Execute INSERT of another row", q => q.Call("db", "Execute", A.Str("INSERT INTO F (ID, N) VALUES (9, 90)"), A.I4(Dao.FailOnError))),
            ("BeginTrans then Rollback", q => q.Call("eng", "BeginTrans").Call("eng", "Rollback")),
        ];
        yield return On("main", "edit-pending-ops", ["CUR-EDIT", "CUR-LIFE"],
            $"For each pending state (Edit with N assigned 999 on row 1; AddNew with ID 20 and N 999 assigned) and each operation [{string.Join(" ;; ", operations.Select(o => o.Label))}]: fresh " +
            "dynaset over F with held Fields ID and N, enter the pending state, run the operation, then read EditMode, Errors, N, then CancelUpdate, EditMode and Errors; release derived objects and " +
            "close. Asks which operations a pending Edit or AddNew accepts, refuses, posts or discards, and the buffer afterwards.",
            p =>
            {
                foreach (bool addNew in new[] { false, true })
                {
                    foreach ((_, Action<P> op) in operations)
                    {
                        p.OpenRs("rs", "db", "F", Dao.OpenDynaset);
                        Hold(p, "rs", "e_", "ID", "N");
                        if (addNew)
                        {
                            p.Call("rs", "AddNew").Put("e_ID", "Value", A.I4(20)).Put("e_N", "Value", A.I4(999));
                        }
                        else
                        {
                            p.Call("rs", "Edit").Put("e_N", "Value", A.I4(999));
                        }
                        op(p);
                        p.Get("rs", "EditMode").Errors().Get("e_N", "Value").Call("rs", "CancelUpdate").Get("rs", "EditMode").Errors();
                        p.Release("rc").Release("rd");
                        Unhold(p, "e_", "ID", "N");
                        Close(p, "rs");
                    }
                }
            }, write: true);

        (string Label, Arg[] Args)[] updates =
        [
            ("(no argument)", []), ("Long 1", [A.I4(1)]), ("Long 2", [A.I4(2)]), ("Long 3", [A.I4(3)]), ("Long 1, True", [A.I4(1), A.Bool(true)]),
            ("Missing, True", [A.Missing, A.Bool(true)]), ("Null", [A.Null]), ("\"1\"", [A.Str("1")]), ("Long 0", [A.I4(0)]), ("Long 4", [A.I4(4)]),
            ("Long 1, False", [A.I4(1), A.Bool(false)]),
        ];
        yield return On("main", "update-arguments", ["CUR-EDIT"],
            $"For each Update argument list [{string.Join(" ;; ", updates.Select(u => u.Label))}]: fresh dynaset over F with held Field N, Edit row 1, assign N 301+index, Update with the arguments, " +
            "read EditMode, Errors; Move 0 and read N; CancelUpdate if needed, close. Asks the accepted UpdateType and Force values and whether an Update with them posts the edit.",
            p =>
            {
                int i = 0;
                foreach ((_, Arg[] args) in updates)
                {
                    p.OpenRs("rs", "db", "F", Dao.OpenDynaset);
                    Hold(p, "rs", "e_", "N");
                    p.Call("rs", "Edit").Put("e_N", "Value", A.I4(301 + i++)).Call("rs", "Update", args).Get("rs", "EditMode").Errors().Call("rs", "Move", A.I4(0)).Get("e_N", "Value")
                        .Call("rs", "CancelUpdate");
                    Unhold(p, "e_", "N");
                    Close(p, "rs");
                }
            }, write: true);
    }

    // CacheSize, CacheStart, FillCache (CUR-BULK) -----------------------------------------------------------

    private static IEnumerable<Probe> Caches()
    {
        (string Label, Arg Value)[] sizes =
        [
            ("Long 0", A.I4(0)), ("Long 1", A.I4(1)), ("Long 4", A.I4(4)), ("Long 5", A.I4(5)), ("Long 6", A.I4(6)), ("Long 100", A.I4(100)), ("Long 1200", A.I4(1200)),
            ("Long 1201", A.I4(1201)), ("Long -1", A.I4(-1)), ("\"10\"", A.Str("10")), ("Double 10.5", A.R8(10.5)), ("Null", A.Null),
        ];
        yield return On("bulk", "cache-size-properties", ["CUR-BULK"],
            $"For a table-type, a dynaset, a snapshot and a forward-only recordset over B: read CacheSize and CacheStart, assign CacheSize 10 and read it. Then on one dynaset assign each CacheSize " +
            $"[{string.Join(" ;; ", sizes.Select(s => s.Label))}] and read CacheSize and CacheStart after each, and read ID. Asks which types have the properties, the default, and the " +
            "accepted range, coercion and refusals of CacheSize.",
            p =>
            {
                foreach ((_, int type) in Types)
                {
                    p.OpenRs("rs", "db", "B", type).Get("rs", "CacheSize").Get("rs", "CacheStart").Put("rs", "CacheSize", A.I4(10)).Get("rs", "CacheSize");
                    Close(p, "rs");
                }
                p.OpenRs("rs", "db", "B", Dao.OpenDynaset);
                foreach ((_, Arg value) in sizes)
                {
                    p.Put("rs", "CacheSize", value).Get("rs", "CacheSize").Get("rs", "CacheStart");
                    Val(p, "rs", "ID");
                }
                Close(p, "rs");
            });

        (string Label, Arg[] Args)[] fills =
        [
            ("(no argument)", []), ("Long 5", [A.I4(5)]), ("Long 0", [A.I4(0)]), ("Long -1", [A.I4(-1)]), ("Long 21", [A.I4(21)]), ("Long 1000", [A.I4(1000)]),
            ("Long 5, bookmark (0,2,0,0)", [A.I4(5), A.Bytes(0, 2, 0, 0)]), ("Missing, bookmark (2,2,0,0)", [A.Missing, A.Bytes(2, 2, 0, 0)]), ("\"a\"", [A.Str("a")]),
            ("Null", [A.Null]), ("Long 5, empty bytes", [A.I4(5), A.Bytes()]), ("Long 5, \"\"", [A.I4(5), A.Str("")]), ("Long 5, Null", [A.I4(5), A.Null]),
        ];
        Arg[] starts = [A.Bytes(), A.Bytes(0, 0, 0, 0), A.Bytes(2, 2, 0, 0), A.Str(""), A.I4(1), A.Null, A.Empty];
        yield return On("bulk", "cache-fill-and-start", ["CUR-BULK"],
            $"On a dynaset over B with CacheSize 5 and one with the default, and for each FillCache argument list [{string.Join(" ;; ", fills.Select(f => f.Label))}]: call FillCache, read CacheStart, " +
            "EOF, ID and RecordCount. Then assign CacheStart in turn the bookmark-shaped constants empty bytes, four zero bytes, (2,2,0,0), empty string, Long 1, Null and Empty, reading CacheStart " +
            "and ID after each. Then FillCache() on a snapshot, a table-type and a forward-only recordset, reading CacheStart and ID. Asks the accepted FillCache and CacheStart arguments and " +
            "whether the call moves the cursor or reports through RecordCount, on a recordset the engine does not cache by default.",
            p =>
            {
                foreach (bool sized in new[] { true, false })
                {
                    p.OpenRs("rs", "db", "B", Dao.OpenDynaset);
                    if (sized)
                    {
                        p.Put("rs", "CacheSize", A.I4(5));
                    }
                    foreach ((_, Arg[] args) in fills)
                    {
                        p.Call("rs", "FillCache", args).Get("rs", "CacheStart");
                        State(p, "rs", false).Get("rs", "RecordCount");
                    }
                    if (sized)
                    {
                        foreach (Arg start in starts)
                        {
                            p.Put("rs", "CacheStart", start).Get("rs", "CacheStart");
                            Val(p, "rs", "ID");
                        }
                    }
                    Close(p, "rs");
                }
                foreach (int type in new[] { Dao.OpenSnapshot, Dao.OpenTable, Dao.OpenForwardOnly })
                {
                    p.OpenRs("rs", "db", "B", type).Call("rs", "FillCache").Get("rs", "CacheStart");
                    Val(p, "rs", "ID");
                    Close(p, "rs");
                }
            });
    }

    // OpenRecordset option flags (CUR-PROV, CUR-EDIT) -------------------------------------------------------

    private static IEnumerable<Probe> Options()
    {
        int?[] flags = [null, 0, DenyWrite, DenyRead, Dao.ReadOnly, Dao.AppendOnly, Inconsistent, Consistent, 64, Dao.FailOnError, Dao.ForwardOnlyOpt, Dao.SeeChanges, 1024, 2048,
            DenyWrite | DenyRead, Dao.ReadOnly | Dao.AppendOnly, Inconsistent | Consistent, Dao.ReadOnly | Dao.ForwardOnlyOpt];
        yield return On("main", "option-flags-by-type", ["CUR-PROV", "CUR-EDIT"],
            $"For a table-type, a dynaset, a snapshot and a forward-only recordset over F and each Options value [omitted, {string.Join(", ", flags.Skip(1))}] (1 dbDenyWrite, 2 dbDenyRead, 4 dbReadOnly, " +
            "8 dbAppendOnly, 16 dbInconsistent, 32 dbConsistent, 64 dbSQLPassThrough, 128 dbFailOnError, 256 dbForwardOnly, 512 dbSeeChanges, 1024 dbRunAsync, 2048 dbExecDirect): OpenRecordset " +
            "(F, type, options); read Type, Updatable, Bookmarkable, EOF and RecordCount; close. Asks which flags each type accepts, the resulting Type and Updatable, and the refusals.",
            p =>
            {
                foreach ((_, int type) in Types)
                {
                    foreach (int? flag in flags)
                    {
                        if (flag is int f)
                        {
                            p.OpenRs("rs", "db", "F", type, A.I4(f));
                        }
                        else
                        {
                            p.OpenRs("rs", "db", "F", type);
                        }
                        p.Get("rs", "Type").Get("rs", "Updatable").Get("rs", "Bookmarkable").Get("rs", "EOF").Get("rs", "RecordCount");
                        Close(p, "rs");
                    }
                }
            });

        yield return On("main", "option-append-only", ["CUR-PROV", "CUR-EDIT"],
            "For a dynaset, a table-type, a snapshot and a forward-only recordset over F opened with dbAppendOnly (8): read Type, Updatable, BOF, EOF and RecordCount; AddNew, assign ID " +
            "(20, 21, 22, 23 in turn) and N 200, Update, read EditMode, BOF, EOF, RecordCount and ID; MoveFirst and read BOF, EOF and ID; Edit, Delete, Errors; close. Then a plain " +
            "dynaset over F: walk ID and N. Asks what an append-only recordset exposes, whether AddNew works, whether Edit and Delete are refused, and what the base table then holds.",
            p =>
            {
                int id = 20;
                foreach (int type in new[] { Dao.OpenDynaset, Dao.OpenTable, Dao.OpenSnapshot, Dao.OpenForwardOnly })
                {
                    p.OpenRs("rs", "db", "F", type, A.I4(Dao.AppendOnly)).Get("rs", "Type").Get("rs", "Updatable");
                    Hold(p, "rs", "e_", "ID", "N");
                    State(p, "rs", false).Get("rs", "RecordCount").Call("rs", "AddNew").Put("e_ID", "Value", A.I4(id++)).Put("e_N", "Value", A.I4(200)).Call("rs", "Update")
                        .Get("rs", "EditMode");
                    State(p, "rs", false).Get("rs", "RecordCount").Call("rs", "MoveFirst");
                    State(p, "rs", false).Call("rs", "Edit").Call("rs", "Delete").Errors();
                    Unhold(p, "e_", "ID", "N");
                    Close(p, "rs");
                }
                p.OpenRs("rs", "db", "F", Dao.OpenDynaset);
                Forward(p, "rs", 16, "ID", "N");
                Close(p, "rs");
            }, write: true);

        yield return On("main", "option-readonly-write", ["CUR-PROV", "CUR-EDIT"],
            "For a dynaset and a table-type recordset over F opened with Options omitted and then with dbReadOnly (4), with held Fields ID and N: read Updatable, Updatable-related Field " +
            "properties DataUpdatable and Attributes of ID and N; Edit, assign N 5, Update, Errors, EditMode; AddNew, assign ID 20, Update, Errors; Delete, Errors; CancelUpdate; close. " +
            "Asks whether dbReadOnly refuses each write member and at which call, and what Updatable and DataUpdatable report.",
            p =>
            {
                foreach (int type in new[] { Dao.OpenDynaset, Dao.OpenTable })
                {
                    foreach (int? flag in new int?[] { null, Dao.ReadOnly })
                    {
                        if (flag is int f)
                        {
                            p.OpenRs("rs", "db", "F", type, A.I4(f));
                        }
                        else
                        {
                            p.OpenRs("rs", "db", "F", type);
                        }
                        Hold(p, "rs", "e_", "ID", "N");
                        p.Get("rs", "Updatable").Get("e_ID", "DataUpdatable").Get("e_N", "DataUpdatable").Get("e_ID", "Attributes").Get("e_N", "Attributes")
                            .Call("rs", "Edit").Put("e_N", "Value", A.I4(5)).Call("rs", "Update").Errors().Get("rs", "EditMode")
                            .Call("rs", "AddNew").Put("e_ID", "Value", A.I4(20)).Call("rs", "Update").Errors().Call("rs", "Delete").Errors().Call("rs", "CancelUpdate");
                        Unhold(p, "e_", "ID", "N");
                        Close(p, "rs");
                    }
                }
            }, write: true);

        yield return On("main", "option-deny-open", ["CUR-PROV", "CUR-POP"],
            "Open a second Database db2 on the same file read-write. For each of dbDenyWrite (1) and dbDenyRead (2) with a table-type and then a dynaset recordset A over F in db: while A is " +
            "open, db2.OpenRecordset F as a dynaset, as a snapshot and as a table-type recordset reading EOF and RecordCount of each; db2.Execute UPDATE F SET N = N WHERE ID = 1 " +
            "(dbFailOnError); A Edit, assign N 5, Update, Errors; close A, then db2.OpenRecordset F dynaset and db2.Execute the same UPDATE. Asks whether a deny flag of one cursor affects " +
            "another Database of the same Workspace for each open type and for a statement, and what is released when A closes.",
            p =>
            {
                p.OpenDb(bind: "db2", readOnly: false);
                foreach (int flag in new[] { DenyWrite, DenyRead })
                {
                    foreach (int type in new[] { Dao.OpenTable, Dao.OpenDynaset })
                    {
                        p.OpenRs("ra", "db", "F", type, A.I4(flag));
                        Hold(p, "ra", "a_", "N");
                        foreach (int t2 in new[] { Dao.OpenDynaset, Dao.OpenSnapshot, Dao.OpenTable })
                        {
                            p.OpenRs("rb", "db2", "F", t2).Get("rb", "EOF").Get("rb", "RecordCount");
                            Close(p, "rb");
                        }
                        Exec(p, "UPDATE F SET N = N WHERE ID = 1", "db2");
                        p.Call("ra", "Edit").Put("a_N", "Value", A.I4(5)).Call("ra", "Update").Errors();
                        Unhold(p, "a_", "N");
                        Close(p, "ra");
                        p.OpenRs("rb", "db2", "F", Dao.OpenDynaset).Get("rb", "EOF").Get("rb", "RecordCount");
                        Close(p, "rb");
                        Exec(p, "UPDATE F SET N = N WHERE ID = 1", "db2");
                    }
                }
                p.Call("db2", "Close").Release("db2");
            }, write: true);

        (int? Options, int? Lock, int Row)[] seeCases = [(0, null, 2), (Dao.SeeChanges, null, 3), (0, 3, 4), (Dao.SeeChanges, 3, 5)];
        yield return On("main", "option-seechanges", ["CUR-PROV", "CUR-EDIT"],
            "Open a second Database db2 on the same file read-write. For each of (Options 0, LockEdits default, row 2), (dbSeeChanges 512, default, row 3), (0, dbOptimistic 3, row 4), " +
            "(512, 3, row 5): dynaset A over F with the Options and LockEdits arguments, Move to the row, hold N, read N; db2.Execute UPDATE F SET N = 900 + ID WHERE ID = row (dbFailOnError); " +
            "A Edit, Errors, EditMode, read N; assign N 5; Update; Errors; EditMode; Move 0; read N; close A. Asks whether Edit or Update detects a change made through another Database, " +
            "whether dbSeeChanges and the lock mode alter it, and the stored value afterwards.",
            p =>
            {
                p.OpenDb(bind: "db2", readOnly: false);
                foreach ((int? options, int? lockEdits, int row) in seeCases)
                {
                    p.OpenRs("ra", "db", "F", Dao.OpenDynaset, A.I4(options ?? 0), A.I4(lockEdits ?? 2)).Call("ra", "Move", A.I4(row - 1));
                    Hold(p, "ra", "a_", "N");
                    p.Get("a_N", "Value");
                    Exec(p, $"UPDATE F SET N = 900 + ID WHERE ID = {row}", "db2");
                    p.Call("ra", "Edit").Errors().Get("ra", "EditMode").Get("a_N", "Value").Put("a_N", "Value", A.I4(5)).Call("ra", "Update").Errors().Get("ra", "EditMode")
                        .Call("ra", "Move", A.I4(0)).Get("a_N", "Value");
                    Unhold(p, "a_", "N");
                    Close(p, "ra");
                }
                p.Call("db2", "Close").Release("db2");
            }, write: true);

        const string join = "SELECT P2.ID AS PID, P2.N AS PN, C2.ID AS CID, C2.V AS CV FROM P2 INNER JOIN C2 ON P2.ID = C2.PID ORDER BY C2.ID";
        int?[] joinOptions = [null, 0, Dao.ReadOnly, Inconsistent, Consistent, Dao.AppendOnly, Dao.ForwardOnlyOpt];
        yield return On("main", "option-join-updatability", ["CUR-PROV"],
            "P2 (1,10),(2,20) and C2 (1,1,'a'),(2,1,'b'),(3,2,'c') with a declared relationship C2.PID to P2.ID. For each Options value [omitted, 0, 4, 16, 32, 8, 256] and a dynaset over \"" + join +
            "\": read Updatable, and DataUpdatable, SourceTable and SourceField of PID, PN, CID and CV; Edit, assign PN 100+index, Update, Errors; MoveFirst; walk PID, PN, CID and CV; Move 1, " +
            "Edit, assign CV 'z'+index, Update, Errors; MoveFirst; walk again. Asks which source field each projected field maps to, whether the parent field of a one-to-many join is " +
            "updatable under each option, and whether the change shows in all matching occurrences.",
            p =>
            {
                int i = 0;
                foreach (int? flag in joinOptions)
                {
                    if (flag is int f)
                    {
                        p.OpenRs("rs", "db", join, Dao.OpenDynaset, A.I4(f));
                    }
                    else
                    {
                        p.OpenRs("rs", "db", join, Dao.OpenDynaset);
                    }
                    Hold(p, "rs", "j_", "PID", "PN", "CID", "CV");
                    p.Get("rs", "Updatable");
                    foreach (string field in new[] { "PID", "PN", "CID", "CV" })
                    {
                        p.Get("j_" + field, "DataUpdatable").Get("j_" + field, "SourceTable").Get("j_" + field, "SourceField");
                    }
                    p.Call("rs", "Edit").Put("j_PN", "Value", A.I4(100 + i)).Call("rs", "Update").Errors().Call("rs", "MoveFirst");
                    Forward(p, "rs", 8, "PID", "PN", "CID", "CV");
                    p.Call("rs", "MoveFirst").Call("rs", "Move", A.I4(1)).Call("rs", "Edit").Put("j_CV", "Value", A.Str("z" + i)).Call("rs", "Update").Errors().Call("rs", "MoveFirst");
                    Forward(p, "rs", 8, "PID", "PN", "CID", "CV");
                    Unhold(p, "j_", "PID", "PN", "CID", "CV");
                    Close(p, "rs");
                    i++;
                }
            }, write: true);

        yield return On("main", "option-join-addnew-delete", ["CUR-PROV"],
            "For each Options value [omitted, dbInconsistent 16, dbConsistent 32] and a fresh dynaset over \"" + join + "\" with held Fields PID, PN, CID and CV: (a) AddNew, assign PID 3 and PN 30, " +
            "Update, Errors; (b) AddNew, assign CID 10 and CV 'n' only, Update, Errors; (c) AddNew, assign PID 1, CID 11 and CV 'm', Update, Errors; (d) MoveFirst, Delete, Errors, EditMode, " +
            "BOF, EOF; then close and read the base tables P2 (ID, N) and C2 (ID, PID, V) through snapshots. Asks which base row an AddNew or Delete on a join touches, the effect of the " +
            "options and of the declared relationship, and the rows left in each base table.",
            p =>
            {
                foreach (int? flag in new int?[] { null, Inconsistent, Consistent })
                {
                    if (flag is int f)
                    {
                        p.OpenRs("rs", "db", join, Dao.OpenDynaset, A.I4(f));
                    }
                    else
                    {
                        p.OpenRs("rs", "db", join, Dao.OpenDynaset);
                    }
                    Hold(p, "rs", "j_", "PID", "PN", "CID", "CV");
                    p.Call("rs", "AddNew").Put("j_PID", "Value", A.I4(3)).Put("j_PN", "Value", A.I4(30)).Call("rs", "Update").Errors();
                    p.Call("rs", "AddNew").Put("j_CID", "Value", A.I4(10)).Put("j_CV", "Value", A.Str("n")).Call("rs", "Update").Errors();
                    p.Call("rs", "AddNew").Put("j_PID", "Value", A.I4(1)).Put("j_CID", "Value", A.I4(11)).Put("j_CV", "Value", A.Str("m")).Call("rs", "Update").Errors();
                    p.Call("rs", "MoveFirst").Call("rs", "Delete").Errors().Get("rs", "EditMode").Get("rs", "BOF").Get("rs", "EOF");
                    Unhold(p, "j_", "PID", "PN", "CID", "CV");
                    Close(p, "rs");
                    p.OpenRs("rn", "db", "P2", Dao.OpenSnapshot);
                    Forward(p, "rn", 8, "ID", "N");
                    Close(p, "rn");
                    p.OpenRs("rn", "db", "C2", Dao.OpenSnapshot);
                    Forward(p, "rn", 8, "ID", "PID", "V");
                    Close(p, "rn");
                }
            }, write: true);

        (string Label, Arg[] Args)[] opens =
        [
            ("\"F\"", [A.Str("F")]), ("SQL over F", [A.Str("SELECT * FROM F")]), ("\"QP\" (parameter query)", [A.Str("QP")]), ("F, Long 0", [A.Str("F"), A.I4(0)]),
            ("F, Long 1", [A.Str("F"), A.I4(1)]), ("SQL, Long 1", [A.Str("SELECT * FROM F"), A.I4(1)]), ("F, Integer 2", [A.Str("F"), A.I2(2)]), ("F, \"2\"", [A.Str("F"), A.Str("2")]),
            ("F, Null", [A.Str("F"), A.Null]), ("F, Empty", [A.Str("F"), A.Empty]), ("F, Long 3", [A.Str("F"), A.I4(3)]), ("F, Long 5", [A.Str("F"), A.I4(5)]),
            ("F, Long 16", [A.Str("F"), A.I4(16)]), ("F, Long 32", [A.Str("F"), A.I4(32)]), ("F, Missing, Long 4", [A.Str("F"), A.Missing, A.I4(4)]),
            ("F, Long 2, Missing, Long 3", [A.Str("F"), A.I4(2), A.Missing, A.I4(3)]), ("F, Long 2, Long 0, Long 0", [A.Str("F"), A.I4(2), A.I4(0), A.I4(0)]),
            ("F, Long 2, Long 0, Long 2, Long 0", [A.Str("F"), A.I4(2), A.I4(0), A.I4(2), A.I4(0)]), ("F, Long 8", [A.Str("F"), A.I4(8)]),
            ("F, Long 2, Long 256", [A.Str("F"), A.I4(2), A.I4(256)]), ("\"\", Long 2", [A.Str(""), A.I4(2)]), ("NoSuch, Long 2", [A.Str("NoSuch"), A.I4(2)]),
            ("Null, Long 2", [A.Null, A.I4(2)]), ("SQL WHERE 1 = 0, Long 4", [A.Str("SELECT * FROM F WHERE 1 = 0"), A.I4(4)]), ("F, Long 2, Long 8", [A.Str("F"), A.I4(2), A.I4(8)]),
        ];
        yield return On("main", "open-argument-shapes", ["CUR-PROV", "CUR-POS"],
            $"For each argument list of Database.OpenRecordset [{string.Join(" ;; ", opens.Select(o => o.Label))}]: call it, read Type, Updatable, Bookmarkable, EOF, RecordCount and Name; close. " +
            "Asks the default recordset type for a table name and for SQL, the refused type and option values, the accepted VARIANT types, omitted middle arguments and excess arguments.",
            p =>
            {
                foreach ((_, Arg[] args) in opens)
                {
                    p.CallBind("rs", "db", "OpenRecordset", args).Get("rs", "Type").Get("rs", "Updatable").Get("rs", "Bookmarkable").Get("rs", "EOF").Get("rs", "RecordCount").Get("rs", "Name");
                    Close(p, "rs");
                }
            });

        int[] locks = [0, 1, 2, 3, 4, 5, 6, -1];
        Arg[] lockArgs = [.. locks.Select(A.I4), A.Null, A.Str("2")];
        yield return On("main", "open-lockedits-values", ["CUR-EDIT", "CUR-PROV"],
            $"For a dynaset over F opened with LockEdits (the fourth argument, Options 0) in turn [{string.Join(", ", locks)}, Null, \"2\"]: read Updatable and the LockEdits property; Edit, " +
            "Errors, EditMode; assign N 5; Update; Errors; CancelUpdate; close. Asks the accepted lock-type values (1 dbOptimisticValue, 2 dbPessimistic, 3 dbOptimistic, 4 dbReadOnly, " +
            "5 dbOptimisticBatch), the LockEdits value each yields and whether writes are refused.",
            p =>
            {
                foreach (Arg lockArg in lockArgs)
                {
                    p.OpenRs("rs", "db", "F", Dao.OpenDynaset, A.I4(0), lockArg).Get("rs", "Updatable").Get("rs", "LockEdits");
                    Hold(p, "rs", "e_", "N");
                    p.Call("rs", "Edit").Errors().Get("rs", "EditMode").Put("e_N", "Value", A.I4(5)).Call("rs", "Update").Errors().Call("rs", "CancelUpdate");
                    Unhold(p, "e_", "N");
                    Close(p, "rs");
                }
            }, write: true);
    }

    // Helpers -----------------------------------------------------------------------------------------------

    private static Probe Creation(string name, string[] branches, string what, Action<P> body) =>
        new(Card, "create-" + name, [CardId, "D-CURSOR-1", .. branches],
            $"R-CURSOR-1 fixture {Card}-{name}: {what}. Created in ACCDB by DBEngine.CreateDatabase(path, dbLangGeneral, dbVersion120) and " +
            "db.Execute DDL/DML; the closed file is kept.",
            p =>
            {
                p.EngineLite().Get("eng", "Version")
                    .CallBind("db", "eng", "CreateDatabase", A.Out($"{Card}-{name}.accdb"), A.Str(Dao.LangGeneral), A.I4(Dao.Version120))
                    .Get("db", "Version");
                body(p);
                p.Call("db", "Close").Release("db").Release("eng").Files("after-close");
            })
        { CreatesFixture = true };

    /// <summary>A probe on a fresh copy of a fixture: light opening, the body, light closing.</summary>
    private static Probe On(string fixture, string name, string[] branches, string what, Action<P> body, bool write = false) =>
        new(Card, name, [CardId, "D-CURSOR-1", .. branches],
            what + (write ? " The fixture copy is opened read-write." : " The fixture copy is opened read-only."),
            p =>
            {
                p.EngineLite().OpenDb(readOnly: !write).Files("after-open");
                body(p);
                p.FinishLite("db");
            })
        { Fixtures = [("main", $"{Card}-{fixture}")] };

    /// <summary>A Find probe: one dynaset over F; each criterion FindFirst, then <paramref name="nexts"/> FindNext, each read in full.</summary>
    private static Probe FindProbe(string name, (string Label, Arg Value)[] forms, int nexts, string asks) =>
        On("main", name, ["CUR-POS"],
            $"On one dynaset over F (IDs 1-8; N 10,20,30,40,40,50,60,70; S alpha, beta, O'Brien, a\"b, abc, ABC, abd, Null; D with a time part, a repeated date and Nulls; Q x, Null, y), for each " +
            $"criterion [{Join(forms)}]: FindFirst, then FindNext x{nexts}, with the same criterion; after each call read NoMatch, BOF, EOF and ID. " + asks,
            p =>
            {
                p.OpenRs("rs", "db", "F", Dao.OpenDynaset);
                foreach ((_, Arg value) in forms)
                {
                    FindRead(p, "rs", "FindFirst", value);
                    for (int i = 0; i < nexts; i++)
                    {
                        FindRead(p, "rs", "FindNext", value);
                    }
                }
                Close(p, "rs");
            });

    private static (string Label, Arg Value)[] Labeled(IEnumerable<string> texts, params (string Label, Arg Value)[] extra) =>
        [.. texts.Select(t => (Label: t, Value: A.Str(t))), .. extra];

    private static string Join((string Label, Arg Value)[] items) => string.Join(" ;; ", items.Select(i => i.Label));

    private static P Sql(P p, string sql) => p.Call("db", "Execute", A.Str(sql));

    private static P Exec(P p, string sql, string db = "db") => p.Call(db, "Execute", A.Str(sql), A.I4(Dao.FailOnError));

    /// <summary>Read one field of the current row through a fresh Fields collection.</summary>
    private static P Val(P p, string rs, string field) =>
        p.GetBind("sf", rs, "Fields").Item("sv", "sf", A.Str(field)).Get("sv", "Value").Release("sv").Release("sf");

    private static P State(P p, string rs, bool position, string field = "ID")
    {
        p.Get(rs, "BOF").Get(rs, "EOF");
        if (position)
        {
            p.Get(rs, "AbsolutePosition");
        }
        return Val(p, rs, field);
    }

    /// <summary>BOF, EOF, optional AbsolutePosition, the value of a held Field and optionally Bookmark.</summary>
    private static P HState(P p, string rs, string held, bool position, bool bookmark)
    {
        p.Get(rs, "BOF").Get(rs, "EOF");
        if (position)
        {
            p.Get(rs, "AbsolutePosition");
        }
        p.Get(held, "Value");
        return bookmark ? p.Get(rs, "Bookmark") : p;
    }

    private static P PosState(P p, string rs, bool absolute)
    {
        p.Get(rs, "BOF").Get(rs, "EOF").Get(rs, "PercentPosition");
        if (absolute)
        {
            p.Get(rs, "AbsolutePosition");
        }
        return Val(p, rs, "ID");
    }

    /// <summary>A Find call with the criterion, then NoMatch, BOF, EOF and ID.</summary>
    private static P FindRead(P p, string rs, string member, Arg criteria) =>
        State(p.Call(rs, member, criteria).Get(rs, "NoMatch"), rs, false);

    /// <summary>A fresh table-type recordset with Index, held fields; Seek, then the current row, MoveNext, and MovePrevious twice.</summary>
    private static void SeekCase(P p, string table, string index, Arg[] seekArgs, params string[] fields)
    {
        p.OpenRs("rt", "db", table, Dao.OpenTable).Put("rt", "Index", A.Str(index));
        Hold(p, "rt", "h_", fields);
        p.Call("rt", "Seek", seekArgs).Get("rt", "NoMatch").Get("rt", "BOF").Get("rt", "EOF");
        ReadHeld(p, "h_", fields).Call("rt", "MoveNext");
        ReadHeld(p, "h_", fields).Call("rt", "MovePrevious").Call("rt", "MovePrevious");
        ReadHeld(p, "h_", fields);
        Unhold(p, "h_", fields);
        Close(p, "rt");
    }

    /// <summary>Bind <c>prefix + "s"</c> to the Fields collection and <c>prefix + name</c> to each named Field.</summary>
    private static P Hold(P p, string rs, string prefix, params string[] fields)
    {
        p.GetBind(prefix + "s", rs, "Fields");
        foreach (string f in fields)
        {
            p.Item(prefix + f, prefix + "s", A.Str(f));
        }
        return p;
    }

    private static P ReadHeld(P p, string prefix, params string[] fields)
    {
        foreach (string f in fields)
        {
            p.Get(prefix + f, "Value");
        }
        return p;
    }

    private static P Unhold(P p, string prefix, params string[] fields)
    {
        foreach (string f in fields)
        {
            p.Release(prefix + f);
        }
        return p.Release(prefix + "s");
    }

    private static P Forward(P p, string rs, int max, params string[] fields)
    {
        Hold(p, rs, "w_", fields);
        p.Until(rs, "EOF", max, b =>
        {
            foreach (string f in fields)
            {
                b.Get("w_" + f, "Value");
            }
            b.Call(rs, "MoveNext");
        });
        return Unhold(p, "w_", fields);
    }

    private static P Close(P p, string rs) => p.Call(rs, "Close").Release(rs);
}
