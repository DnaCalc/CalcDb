namespace CalcDb.Probes.Cards;

/// <summary>
/// R-SQL-1 and R-SQL-3, second batch (A-02): more relational semantics through ACE DAO recordsets. One ACCDB
/// fixture (r-sql-13b-main) with small tables built to ask new questions: outer and cross joins over Null keys and
/// nested join parentheses, IN/EXISTS/ANY/ALL subqueries, correlated and derived subqueries, GROUP BY and HAVING
/// forms, aggregate arguments and overflow boundaries, TOP forms, Memo comparison scope, ORDER BY forms, UNION
/// type promotion and syntax, crosstab heading lists and clauses, PARAMETERS in every declared type and position,
/// BETWEEN and LIKE. Probes ask; nothing here states an expected answer. A refused form is an observation: its
/// error number comes from DBEngine.Errors and the failing call's own error channels.
/// </summary>
public sealed class RSql13B : ICard
{
    private const string Card = "r-sql-13b";
    private const string Fixture = "r-sql-13b-main";
    private const int Fail = Dao.FailOnError;

    private const string StmtTail =
        " Runs on a fresh copy of r-sql-13b-main (ACCDB) opened read-only through ACE DAO; each text is opened as a snapshot with Database.OpenRecordset, " +
        "Fields Name/Type/Size are read before the first row, then every row's Field values to EOF (each MoveNext captured), then Close and DBEngine.Errors.";

    private const string ParamTail =
        " Runs on a fresh copy of r-sql-13b-main (ACCDB) opened read-only through ACE DAO; each text goes into a temporary QueryDef (CreateQueryDef with an empty name, then DBEngine.Errors).";

    private static string[] R1(params string[] branches) => ["R-SQL-1", .. branches];

    private static string[] R3(params string[] branches) => ["R-SQL-3", .. branches];

    public IEnumerable<Probe> Probes() =>
        new[] { Creation() }
            .Concat(Joins()).Concat(Subqueries()).Concat(Groups()).Concat(TopAndMemo()).Concat(OrderAndUnion())
            .Concat(Pivots()).Concat(ParameterProbes()).Concat(Predicates());

    // Fixture ---------------------------------------------------------------------------------------------------

    private static Probe Creation() =>
        new(Card, "create-main", ["R-SQL-1", "R-SQL-3", "SQL-REL"],
            "R-SQL-1/R-SQL-3 fixture r-sql-13b-main: tables JA, JB, JC, JD (Long keys with Null rows), CX, CY, CZ (empty), ONE (one row), GA (groups with Nulls), " +
            "SI (typed boundary values in four groups), TB (Yes/No, Memo, Text), TG (GUID), MMP (Memo values that differ at chosen positions), PV (crosstab source), BT (BETWEEN and LIKE operand types) and LK (LIKE text rows). " +
            "Created in ACCDB by DBEngine.CreateDatabase(path, dbLangGeneral, 128); DDL and literal rows go through Database.Execute with dbFailOnError, " +
            "exact typed values (Single, Currency, Date, long Memo, GUID) through table-type AddNew/Value/Update. Every call's outcome is captured and the closed file is kept.",
            p =>
            {
                p.Create().Modules("after-create").Get("eng", "Version")
                    .CallBind("db", "eng", "CreateDatabase", A.Out($"{Fixture}.accdb"), A.Str(Dao.LangGeneral), A.I4(Dao.Version120))
                    .Get("db", "Version");
                Build(p);
                p.Call("db", "Close").Release("db").Release("eng").Files("after-close");
            })
        { CreatesFixture = true };

    private static void Ddl(P p, string sql) => p.Call("db", "Execute", A.Str(sql), A.I4(Fail));

    private static void Fill(P p, string table, string columns, params string[] rows)
    {
        foreach (string row in rows)
        {
            Ddl(p, $"INSERT INTO {table} ({columns}) VALUES ({row})");
        }
    }

    private static void Build(P p)
    {
        Ddl(p, "CREATE TABLE JA (a LONG, g TEXT(5))");
        Fill(p, "JA", "a, g", "1, 'x'", "2, 'y'", "Null, 'z'", "4, 'x'");
        Ddl(p, "CREATE TABLE JB (b LONG, h TEXT(5))");
        Fill(p, "JB", "b, h", "1, 'p'", "1, 'q'", "Null, 'r'", "5, 's'");
        Ddl(p, "CREATE TABLE JC (c LONG, k TEXT(5))");
        Fill(p, "JC", "c, k", "1, 'cc'", "4, 'dd'", "Null, 'ee'");
        Ddl(p, "CREATE TABLE JD (d LONG, e TEXT(5))");
        Fill(p, "JD", "d, e", "1, 'd1'", "5, 'd5'");
        Ddl(p, "CREATE TABLE CX (x LONG)");
        Fill(p, "CX", "x", "1", "2");
        Ddl(p, "CREATE TABLE CY (y TEXT(2))");
        Fill(p, "CY", "y", "'a'", "'b'", "'c'");
        Ddl(p, "CREATE TABLE CZ (z LONG)");
        Ddl(p, "CREATE TABLE ONE (x LONG)");
        Fill(p, "ONE", "x", "1");

        // GA: groups with Null keys, Null values, duplicate values (v = 5 twice) and a text column with a non-number.
        Ddl(p, "CREATE TABLE GA (id LONG CONSTRAINT PK_GA PRIMARY KEY, g LONG, h TEXT(5), v LONG, d DOUBLE, tx TEXT(10))");
        Fill(p, "GA", "id, g, h, v, d, tx",
            "1, 1, 'x', 10, 1.5, '1'", "2, 1, 'x', 20, 2.5, '2'", "3, 1, 'y', Null, Null, '3.5'", "4, 2, 'x', Null, 4, 'x'",
            "5, 2, Null, 5, 5, '10'", "6, Null, 'x', 6, 6, '1'", "7, Null, 'x', 7, 7, Null", "8, Null, Null, Null, Null, ''",
            "9, 3, 'z', 5, 5, '2'");

        // SI: four groups. g1 = type maximum + 1 (Currency: maximum + 0.0001); g2 = two maxima; g3 = two halves that
        // sum to one past the maximum (Single and Double: twice a value above half the maximum); g4 = minimum + (-1).
        Ddl(p, "CREATE TABLE SI (id LONG CONSTRAINT PK_SI PRIMARY KEY, g LONG, i SHORT, b BYTE, l LONG, s SINGLE, c CURRENCY, d DOUBLE, dt DATETIME, yn YESNO)");
        static Arg[] Si(int id, int g, short i, byte b, int l, float s, decimal c, double d, double dt, bool yn) =>
            [A.I4(id), A.I4(g), A.I2(i), A.UI1(b), A.I4(l), A.R4(s), A.Cy(c), A.R8(d), A.Date(dt), A.Bool(yn)];
        const decimal cyMax = 922337203685477.5807m;
        Rows(p, "SI", ["id", "g", "i", "b", "l", "s", "c", "d", "dt", "yn"],
            Si(1, 1, short.MaxValue, 255, int.MaxValue, float.MaxValue, cyMax, 1e308, 2958465.0, true),
            Si(2, 1, 1, 1, 1, 1f, 0.0001m, 1.0, 1.0, false),
            Si(3, 2, short.MaxValue, 255, int.MaxValue, float.MaxValue, cyMax, 1e308, 2958465.0, true),
            Si(4, 2, short.MaxValue, 255, int.MaxValue, float.MaxValue, cyMax, 1e308, 2958465.0, true),
            Si(5, 3, 16384, 128, 1073741824, 2e38f, 461168601842738.7904m, 9e307, 1479232.0, true),
            Si(6, 3, 16384, 128, 1073741824, 2e38f, 461168601842738.7904m, 9e307, 1479232.0, true),
            Si(7, 4, short.MinValue, 0, int.MinValue, -float.MaxValue, -922337203685477.5808m, -1e308, -100000.0, false),
            Si(8, 4, -1, 0, -1, -1f, -0.0001m, -1.0, -100001.0, false));

        // TB: Yes/No, Memo and Text with case, accent, space, empty and Null variants.
        Ddl(p, "CREATE TABLE TB (id LONG CONSTRAINT PK_TB PRIMARY KEY, yn YESNO, m MEMO, t TEXT(10))");
        Rows(p, "TB", ["id", "yn", "m", "t"],
            [A.I4(1), A.Bool(true), A.Str("mb"), A.Str("b")], [A.I4(2), A.Bool(false), A.Str("ma"), A.Str("B")],
            [A.I4(3), A.Null, A.Null, A.Null], [A.I4(4), A.Bool(true), A.Str("ma"), A.Str("a")],
            [A.I4(5), A.Bool(false), A.Str(""), A.Str("")], [A.I4(6), A.Bool(true), A.Str("m\u00E4"), A.Str("\u00E4")],
            [A.I4(7), A.Bool(false), A.Str(" mb"), A.Str(" ")], [A.I4(8), A.Bool(true), A.Str("mA"), A.Str("A")]);

        Ddl(p, "CREATE TABLE TG (id LONG CONSTRAINT PK_TG PRIMARY KEY, gd GUID)");
        Rows(p, "TG", ["id", "gd"],
            [A.I4(1), A.Str("{11111111-1111-1111-1111-111111111111}")], [A.I4(2), A.Str("{22222222-2222-2222-2222-222222222222}")],
            [A.I4(3), A.Null], [A.I4(4), A.Str("{11111111-1111-1111-1111-111111111111}")]);

        // MMP: pairs of Memo values equal up to a position p and different at p ('a' versus 'b'); row 15 repeats row 1.
        Ddl(p, "CREATE TABLE MMP (id LONG CONSTRAINT PK_MMP PRIMARY KEY, p LONG, m MEMO)");
        List<Arg[]> mmp = [];
        foreach (int pos in MemoPositions)
        {
            int id = mmp.Count + 1;
            mmp.Add([A.I4(id), A.I4(pos), A.Str(new string('q', pos - 1) + "a")]);
            mmp.Add([A.I4(id + 1), A.I4(pos), A.Str(new string('q', pos - 1) + "b")]);
        }
        mmp.Add([A.I4(mmp.Count + 1), A.I4(MemoPositions[0]), A.Str(new string('q', MemoPositions[0] - 1) + "a")]);
        Rows(p, "MMP", ["id", "p", "m"], [.. mmp]);

        // PV: crosstab source with Null row heading, Null pivot values and Long/Currency/Yes-No pivot candidates.
        Ddl(p, "CREATE TABLE PV (id LONG CONSTRAINT PK_PV PRIMARY KEY, g TEXT(5), g2 TEXT(5), p TEXT(5), pl LONG, amt LONG, pc CURRENCY, pb YESNO)");
        Fill(p, "PV", "id, g, g2, p, pl, amt, pc, pb",
            "1, 'g1', 'h1', 'a', 1, 10, 0.5, True", "2, 'g1', 'h2', 'b', 2, 20, 0.25, False", "3, 'g2', 'h1', 'a', 9, 30, 1.5, True",
            "4, 'g2', 'h2', 'c', 10, 40, 10, False", "5, 'g3', 'h1', 'b', 100, 50, 2.5, True", "6, 'g3', Null, Null, -5, 60, Null, Null",
            "7, Null, 'h1', 'a', 2, 70, 0.5, False", "8, 'g1', 'h1', 'd', 9, Null, 2.5, True");

        // BT: operand types for BETWEEN and LIKE (Long, Text, Date, Double, Memo, Yes/No), one all-Null row.
        Ddl(p, "CREATE TABLE BT (id LONG CONSTRAINT PK_BT PRIMARY KEY, n LONG, t TEXT(5), dt DATETIME, f DOUBLE, m MEMO, yn YESNO)");
        Fill(p, "BT", "id, n, t, dt, f, m, yn",
            "1, 1, 'a', #2024-01-01#, 0.5, 'abc', True", "2, 5, 'B', #2024-01-05#, 1.5, 'xyz', False", "3, 10, 'c', #2024-01-10#, 2.5, 'abd', True",
            "4, Null, Null, Null, Null, Null, Null", "5, 7, 'D', #2024-01-07#, 3, '', False", "6, -3, '', #2023-12-31#, -0.5, 'b', True",
            "7, 5, 'b', #2024-01-05 12:00:00#, 1.5, 'B', False");

        Ddl(p, "CREATE TABLE LK (id LONG CONSTRAINT PK_LK PRIMARY KEY, t TEXT(20))");
        for (int i = 0; i < LkRows.Length; i++)
        {
            Fill(p, "LK", "id, t", LkRows[i] is string s ? $"{i + 1}, '{s}'" : $"{i + 1}, Null");
        }
    }

    private static readonly int[] MemoPositions = [250, 254, 255, 256, 257, 300, 1000];

    /// <summary>LK text rows: letters, case and accent variants, wildcard and bracket characters, spaces, empty, Null.</summary>
    private static readonly string?[] LkRows =
    [
        "a", "b", "c", "d", "A", "B", "ab", "abc", "a-c", "a*c", "a?c", "a#c", "a1c", "a[c", "a]c", "a!c", "a^c", "abc ", " abc", "", null,
        "\u00E4bc", "a%c", "a_c", "\u00E6", "ae", "1", "5", "#", "*", "?", "[", "]", "-", "!", "Z", "z", "\u00E1", "\u00DF", "\u00C4", "ss", "a\u00E4c",
    ];

    private static void Rows(P p, string table, string[] cols, params Arg[][] rows)
    {
        string rs = "ins_" + table, fl = rs + "_f";
        p.OpenRs(rs, "db", table, Dao.OpenTable).GetBind(fl, rs, "Fields");
        for (int c = 0; c < cols.Length; c++)
        {
            p.Item($"{rs}_c{c}", fl, A.Str(cols[c]));
        }
        foreach (Arg[] row in rows)
        {
            p.Call(rs, "AddNew");
            for (int c = 0; c < cols.Length; c++)
            {
                p.Put($"{rs}_c{c}", "Value", row[c]);
            }
            p.Call(rs, "Update");
        }
        for (int c = 0; c < cols.Length; c++)
        {
            p.Release($"{rs}_c{c}");
        }
        p.Release(fl).Call(rs, "Close").Release(rs).Errors();
    }

    // Program parts ---------------------------------------------------------------------------------------------

    private static Probe Read(string name, string[] refs, string description, Action<P> body) =>
        new(Card, name, refs, description, p =>
        {
            p.EngineLite().OpenDb().Get("db", "Version");
            body(p);
            p.FinishLite("db");
        })
        { Fixtures = [("main", Fixture)] };

    private static Probe Stmts(string name, string[] refs, string what, params string[] sqls) =>
        Read(name, refs, what + StmtTail, p => Qs(p, "s", sqls));

    private static P Meta(P p, string rs, int maxFields)
    {
        string fl = rs + "_mf", f = rs + "_mfi";
        return p.GetBind(fl, rs, "Fields").Each(fl, f, maxFields, b => b.Get(f, "Name").Get(f, "Type").Get(f, "Size")).Release(fl);
    }

    /// <summary>One statement through Database.OpenRecordset: metadata at open, rows to EOF, close, errors.</summary>
    private static void Q(P p, string id, string sql, int maxRows = 48, int maxFields = 32)
    {
        string rs = "q" + id;
        p.OpenRs(rs, "db", sql, Dao.OpenSnapshot);
        Meta(p, rs, maxFields);
        p.ReadAll(rs, maxRows, maxFields).Call(rs, "Close").Release(rs).Errors();
    }

    private static void Qs(P p, string prefix, IEnumerable<string> sqls, int maxRows = 48, int maxFields = 32)
    {
        int i = 0;
        foreach (string sql in sqls)
        {
            Q(p, $"{prefix}{++i}", sql, maxRows, maxFields);
        }
    }

    private static P TempQd(P p, string qd, string sql) =>
        p.CallBind(qd, "db", "CreateQueryDef", A.Str(""), A.Str(sql)).Errors();

    private static P QdParams(P p, string qd, bool withValue = false)
    {
        string ps = qd + "_ps", pr = qd + "_pi";
        return p.GetBind(ps, qd, "Parameters").Each(ps, pr, 16, b =>
        {
            b.Get(pr, "Name").Get(pr, "Type").Get(pr, "Direction");
            if (withValue)
            {
                b.Get(pr, "Value");
            }
        }).Release(ps);
    }

    private static P SetParam(P p, string qd, string name, Arg value, bool readBack = false)
    {
        string ps = qd + "_pset", pv = qd + "_pv_" + name.Replace(' ', '_');
        p.GetBind(ps, qd, "Parameters").Item(pv, ps, A.Str(name)).Put(pv, "Value", value);
        if (readBack)
        {
            p.Get(pv, "Value");
        }
        return p.Release(pv).Release(ps);
    }

    private static P QdOpen(P p, string qd, string rs, int maxRows = 16, int maxFields = 64)
    {
        p.CallBind(rs, qd, "OpenRecordset", A.I4(Dao.OpenSnapshot));
        Meta(p, rs, maxFields);
        return p.ReadAll(rs, maxRows, maxFields).Call(rs, "Close").Release(rs).Errors();
    }

    // Joins -----------------------------------------------------------------------------------------------------

    private static IEnumerable<Probe> Joins()
    {
        yield return Stmts("join-outer-null-keys", R1("SQL-J"),
            "JA.a is 1, 2, Null, 4 and JB.b is 1, 1, Null, 5. Which rows, in which order, come from LEFT JOIN and RIGHT JOIN on a = b (Null key on each side), the same join with the sides swapped, " +
            "a WHERE on the null-extended side's key in both directions, the INNER JOIN control, an aliased self LEFT JOIN, and Count(*) against Count(JB.b) over the LEFT JOIN?",
            "SELECT JA.a, JA.g, JB.b, JB.h FROM JA LEFT JOIN JB ON JA.a = JB.b",
            "SELECT JA.a, JA.g, JB.b, JB.h FROM JA RIGHT JOIN JB ON JA.a = JB.b",
            "SELECT JA.a, JB.b FROM JB LEFT JOIN JA ON JA.a = JB.b",
            "SELECT JA.a, JB.b FROM JA LEFT JOIN JB ON JA.a = JB.b WHERE JB.b Is Null",
            "SELECT JA.a, JB.b FROM JA RIGHT JOIN JB ON JA.a = JB.b WHERE JA.a Is Null",
            "SELECT JA.a, JB.b FROM JA INNER JOIN JB ON JA.a = JB.b",
            "SELECT x.a, y.a FROM JA AS x LEFT JOIN JA AS y ON x.a = y.a",
            "SELECT Count(*) AS n, Count(JB.b) AS nb FROM JA LEFT JOIN JB ON JA.a = JB.b");

        yield return Stmts("join-keyword-forms", R1("SQL-B", "SQL-J"),
            "Which join spellings are accepted or refused, and with which error number: FULL OUTER JOIN, FULL JOIN, CROSS JOIN, NATURAL JOIN, a bare JOIN, INNER JOIN ... USING, LEFT OUTER and RIGHT OUTER JOIN, " +
            "INNER JOIN with no ON, two ON clauses for one join, and LEFT JOIN with WHERE in the place of ON?",
            "SELECT JA.a, JB.b FROM JA FULL OUTER JOIN JB ON JA.a = JB.b",
            "SELECT JA.a, JB.b FROM JA FULL JOIN JB ON JA.a = JB.b",
            "SELECT JA.a, JB.b FROM JA CROSS JOIN JB",
            "SELECT JA.a, JB.b FROM JA NATURAL JOIN JB",
            "SELECT JA.a, JB.b FROM JA JOIN JB ON JA.a = JB.b",
            "SELECT JA.a, JB.b FROM JA INNER JOIN JB USING (a)",
            "SELECT JA.a, JB.b FROM JA LEFT OUTER JOIN JB ON JA.a = JB.b",
            "SELECT JA.a, JB.b FROM JA RIGHT OUTER JOIN JB ON JA.a = JB.b",
            "SELECT JA.a, JB.b FROM JA INNER JOIN JB",
            "SELECT JA.a, JB.b FROM JA LEFT JOIN JB ON JA.a = JB.b ON JA.a = JB.b",
            "SELECT JA.a, JB.b FROM JA LEFT JOIN JB WHERE JA.a = JB.b");

        yield return Stmts("join-nesting-four-tables", R1("SQL-B", "SQL-J"),
            "Which nestings of INNER, LEFT and RIGHT joins over JA, JB, JC, JD (keys with Nulls) are accepted, with which rows and error numbers: left-nested and right-nested parentheses, three joins written without parentheses in several type orders, " +
            "a RIGHT JOIN inside an INNER JOIN, two parenthesized pairs joined together, a chain of three LEFT JOINs on the first table, an ON that names only the first table, and an ON that names a table not yet joined?",
            "SELECT JA.a, JB.h, JC.k FROM (JA LEFT JOIN JB ON JA.a = JB.b) LEFT JOIN JC ON JB.b = JC.c",
            "SELECT JA.a, JB.h, JC.k FROM JA LEFT JOIN (JB LEFT JOIN JC ON JB.b = JC.c) ON JA.a = JB.b",
            "SELECT JA.a, JB.h, JC.k FROM JA LEFT JOIN JB ON JA.a = JB.b LEFT JOIN JC ON JB.b = JC.c",
            "SELECT JA.a, JB.h, JC.k FROM JA LEFT JOIN JB ON JA.a = JB.b INNER JOIN JC ON JB.b = JC.c",
            "SELECT JA.a, JB.h, JC.k FROM JA INNER JOIN JB ON JA.a = JB.b LEFT JOIN JC ON JB.b = JC.c",
            "SELECT JA.a, JB.h, JC.k FROM JA RIGHT JOIN JB ON JA.a = JB.b LEFT JOIN JC ON JB.b = JC.c",
            "SELECT JA.a, JB.h, JC.k FROM JA INNER JOIN (JB RIGHT JOIN JC ON JB.b = JC.c) ON JA.a = JC.c",
            "SELECT JA.a, JB.h, JC.k FROM JA LEFT JOIN (JB INNER JOIN JC ON JB.b = JC.c) ON JA.a = JB.b",
            "SELECT JA.a, JB.h, JC.k, JD.e FROM (JA INNER JOIN JB ON JA.a = JB.b) INNER JOIN (JC INNER JOIN JD ON JC.c = JD.d) ON JB.b = JC.c",
            "SELECT JA.a, JB.h, JC.k, JD.e FROM JA LEFT JOIN JB ON JA.a = JB.b LEFT JOIN JC ON JA.a = JC.c LEFT JOIN JD ON JA.a = JD.d",
            "SELECT JA.a, JB.h, JC.k FROM (JA LEFT JOIN JB ON JA.a = JB.b) LEFT JOIN JC ON JA.a = JC.c",
            "SELECT JA.a, JB.h, JC.k FROM JA LEFT JOIN JB ON JA.a = JC.c LEFT JOIN JC ON JA.a = JC.c");

        yield return Stmts("join-cross-forms", R1("SQL-B", "SQL-J"),
            "CX holds 1, 2, CY holds a, b, c, CZ is empty and ONE has one row. Which rows, in which order, come from the comma cross join in both table orders, with ORDER BY, with an empty table, as Count(*) over three tables, " +
            "from INNER JOIN and LEFT JOIN ON True / 1 = 1 / 1 = 0, with a WHERE on one side, a self cross join with and without an alias (with the error number where refused), and SELECT * over a cross join?",
            "SELECT x, y FROM CX, CY",
            "SELECT x, y FROM CX, CY ORDER BY y, x",
            "SELECT x, y FROM CY, CX",
            "SELECT x, z FROM CX, CZ",
            "SELECT Count(*) AS n FROM CX, CY, CZ",
            "SELECT x, y FROM CX INNER JOIN CY ON True",
            "SELECT x, y FROM CX INNER JOIN CY ON 1 = 1",
            "SELECT x, y FROM CX LEFT JOIN CY ON 1 = 0",
            "SELECT x, y FROM CX LEFT JOIN CY ON True",
            "SELECT x, y FROM CX, CY WHERE x = 1",
            "SELECT CX.x, c2.x FROM CX, CX AS c2",
            "SELECT x FROM CX, CX",
            "SELECT * FROM CX, CY");

        yield return Stmts("join-on-predicate-forms", R1("SQL-J", "SQL-B"),
            "Which ON predicates are accepted and with which rows (error numbers where refused) for INNER and LEFT joins of JA to JB: <, <>, OR across two columns, a Null-safe OR, a conjunct on the inner side, " +
            "an expression on one side (JA.a + 1), Nz on both sides, a constant comparison on the outer side only and on the inner side only, BETWEEN, and IN with a column in the list?",
            "SELECT JA.a, JB.b FROM JA INNER JOIN JB ON JA.a < JB.b",
            "SELECT JA.a, JB.b FROM JA LEFT JOIN JB ON JA.a < JB.b",
            "SELECT JA.a, JB.b FROM JA LEFT JOIN JB ON JA.a <> JB.b",
            "SELECT JA.a, JB.b FROM JA LEFT JOIN JB ON JA.a = JB.b OR JA.g = JB.h",
            "SELECT JA.a, JB.b FROM JA INNER JOIN JB ON JA.a = JB.b OR JA.g = JB.h",
            "SELECT JA.a, JB.b FROM JA INNER JOIN JB ON (JA.a = JB.b) OR (JA.a Is Null AND JB.b Is Null)",
            "SELECT JA.a, JB.b FROM JA LEFT JOIN JB ON JA.a = JB.b AND JB.h = 'p'",
            "SELECT JA.a, JB.b FROM JA LEFT JOIN JB ON JA.a = JB.b AND JA.g = 'x'",
            "SELECT JA.a, JB.b FROM JA LEFT JOIN JB ON JA.a + 1 = JB.b",
            "SELECT JA.a, JB.b FROM JA LEFT JOIN JB ON Nz(JA.a, 0) = Nz(JB.b, 0)",
            "SELECT JA.a, JB.b FROM JA LEFT JOIN JB ON JA.a = 1",
            "SELECT JA.a, JB.b FROM JA LEFT JOIN JB ON JB.b = 1",
            "SELECT JA.a, JB.b FROM JA INNER JOIN JB ON JA.a BETWEEN JB.b AND 4",
            "SELECT JA.a, JB.b FROM JA LEFT JOIN JB ON JA.a IN (JB.b, 2)");

        int[] tables = [16, 32, 33, 34, 50];
        yield return Read("join-limit-tables", R1("SQL-B"),
            $"Where does the number of table occurrences in one query stop being accepted, and at which phase? Only the count varies (n = {string.Join(", ", tables)}) over aliases of the one-row table ONE: " +
            "a comma cross join, and a nest of parenthesized INNER JOINs on x. Each text is opened directly with the standard capture." + StmtTail,
            p =>
            {
                foreach (int n in tables)
                {
                    Q(p, $"c{n}", "SELECT t1.x FROM " + string.Join(", ", Enumerable.Range(1, n).Select(i => $"ONE AS t{i}")));
                    string nest = "ONE AS t1";
                    for (int i = 2; i <= n; i++)
                    {
                        string join = $"{nest} INNER JOIN ONE AS t{i} ON t1.x = t{i}.x";
                        nest = i == n ? join : $"({join})";
                    }
                    Q(p, $"n{n}", "SELECT t1.x FROM " + nest);
                }
            });

        int[] columns = [255, 256, 257];
        yield return Read("select-limit-columns", R1("SQL-B"),
            $"Where does the number of output columns stop being accepted, and at which phase? Only the count varies (n = {string.Join(", ", columns)}): SELECT x AS c1, ..., x AS cn FROM ONE. " +
            "Each text is opened as a snapshot; Fields.Count is read before the first row, then the single row's values, then Close and DBEngine.Errors.",
            p =>
            {
                foreach (int n in columns)
                {
                    string rs = $"cl{n}";
                    p.OpenRs(rs, "db", "SELECT " + string.Join(", ", Enumerable.Range(1, n).Select(i => $"x AS c{i}")) + " FROM ONE", Dao.OpenSnapshot)
                        .GetBind(rs + "_f", rs, "Fields").Get(rs + "_f", "Count").Release(rs + "_f")
                        .ReadAll(rs, 2, 300).Call(rs, "Close").Release(rs).Errors();
                }
            });
    }

    // Subqueries ------------------------------------------------------------------------------------------------

    private static IEnumerable<Probe> Subqueries()
    {
        yield return Stmts("sub-in-list-null-forms", R1("SQL-SUB-02", "SQL-B"),
            "Which rows of JA (a = 1, 2, Null, 4; g = x, y, z, x) pass IN and NOT IN with a value list containing Null, an empty list, mixed Long/Text and fractional members, a Null left side, a duplicate member, a column inside the list, " +
            "a Text list over case-different members, and a row-value form; what do the projected IN and NOT IN give with a Null in the list; and the same IN and NOT IN over a subquery on JB.b (with a Null), with and without its Null removed and over a UNION child?",
            "SELECT a FROM JA WHERE a IN (1, Null)",
            "SELECT a FROM JA WHERE a NOT IN (1, Null)",
            "SELECT a FROM JA WHERE a NOT IN (1, 2)",
            "SELECT a FROM JA WHERE a IN ()",
            "SELECT a FROM JA WHERE a IN (1, '2')",
            "SELECT a FROM JA WHERE a IN (1.0, 4.5)",
            "SELECT a FROM JA WHERE Null IN (1, 2)",
            "SELECT a FROM JA WHERE a IN (a, 5)",
            "SELECT a FROM JA WHERE g IN ('x', 'X')",
            "SELECT a FROM JA WHERE a IN (1, 1)",
            "SELECT a FROM JA WHERE (a, g) IN ((1, 'x'))",
            "SELECT a, (a IN (1, Null)) AS r1, (a NOT IN (1, Null)) AS r2 FROM JA",
            "SELECT a FROM JA WHERE a IN (SELECT b FROM JB)",
            "SELECT a FROM JA WHERE a NOT IN (SELECT b FROM JB)",
            "SELECT a FROM JA WHERE a NOT IN (SELECT b FROM JB WHERE b Is Not Null)",
            "SELECT a FROM JA WHERE a IN (SELECT b FROM JB UNION SELECT c FROM JC)");

        yield return Stmts("sub-exists-vs-in-null", R1("SQL-SUB-02", "SQL-SUB-04"),
            "With Null keys on both sides (JA.a, JB.b), do EXISTS, NOT EXISTS, IN and NOT IN over the same correlated test give the same JA rows; what does the projected EXISTS and NOT EXISTS give; " +
            "and what do EXISTS and NOT EXISTS give over an empty table (CZ), over a child selecting only Null, over a multi-column child, with a trailing OR, and when the subquery has no parentheses?",
            "SELECT a FROM JA WHERE EXISTS (SELECT * FROM JB WHERE JB.b = JA.a)",
            "SELECT a FROM JA WHERE NOT EXISTS (SELECT * FROM JB WHERE JB.b = JA.a)",
            "SELECT a FROM JA WHERE a IN (SELECT b FROM JB)",
            "SELECT a FROM JA WHERE a NOT IN (SELECT b FROM JB)",
            "SELECT a FROM JA WHERE NOT EXISTS (SELECT 1 FROM JB WHERE JB.b = JA.a OR (JB.b Is Null AND JA.a Is Null))",
            "SELECT a, (EXISTS (SELECT * FROM JB WHERE JB.b = JA.a)) AS r FROM JA",
            "SELECT a, (NOT EXISTS (SELECT * FROM JB WHERE JB.b = JA.a)) AS r FROM JA",
            "SELECT a FROM JA WHERE EXISTS (SELECT * FROM CZ)",
            "SELECT a FROM JA WHERE NOT EXISTS (SELECT * FROM CZ)",
            "SELECT a FROM JA WHERE EXISTS (SELECT Null FROM CX)",
            "SELECT a FROM JA WHERE EXISTS (SELECT x, y FROM CX, CY)",
            "SELECT a FROM JA WHERE EXISTS (SELECT * FROM JB WHERE JB.b = JA.a) OR a Is Null",
            "SELECT a FROM JA WHERE EXISTS SELECT * FROM CX");

        List<string> quantified = [];
        foreach (string op in new[] { "=", "<>", "<", "<=", ">", ">=" })
        {
            quantified.Add($"SELECT a FROM JA WHERE a {op} ANY (SELECT b FROM JB)");
            quantified.Add($"SELECT a FROM JA WHERE a {op} ALL (SELECT b FROM JB)");
        }
        quantified.AddRange(
        [
            "SELECT a FROM JA WHERE a = ANY (SELECT z FROM CZ)",
            "SELECT a FROM JA WHERE a = ALL (SELECT z FROM CZ)",
            "SELECT a FROM JA WHERE a <> ANY (SELECT z FROM CZ)",
            "SELECT a FROM JA WHERE a <> ALL (SELECT z FROM CZ)",
            "SELECT a FROM JA WHERE a = ALL (SELECT x FROM CX)",
            "SELECT a FROM JA WHERE a < ALL (SELECT x FROM CX)",
            "SELECT a FROM JA WHERE a = ANY (1, 2)",
            "SELECT a FROM JA WHERE a > ALL (SELECT b, h FROM JB)",
        ]);
        yield return Stmts("sub-quantified-operators", R1("SQL-SUB-02"),
            "Which JA.a rows (a = 1, 2, Null, 4) pass each of =, <>, <, <=, >, >= with ANY and with ALL over JB.b (1, 1, Null, 5); and over an empty child (CZ) with = and <> for ANY and ALL, over CX (1, 2) with = ALL and < ALL, " +
            "with a value list after ANY, and with a two-column child (error numbers where refused)?",
            [.. quantified]);

        yield return Stmts("sub-correlated-select-list", R1("SQL-SUB-01", "SQL-SUB-03"),
            "What does a correlated scalar subquery in the select list give over JA (Null key row, key 1 with two JB matches) for Count(*), Count(column), Max over Text, Sum, TOP 1 with ORDER BY both ways, First and Last, a child returning two rows, " +
            "a child correlated through a second nesting level, an uncorrelated child, the same child in ORDER BY, WHERE, GROUP BY (grouped column only) and HAVING?",
            "SELECT a, (SELECT Count(*) FROM JB WHERE JB.b = JA.a) AS n FROM JA",
            "SELECT a, (SELECT Count(JB.b) FROM JB WHERE JB.b = JA.a) AS n FROM JA",
            "SELECT a, (SELECT Max(h) FROM JB WHERE JB.b = JA.a) AS mx FROM JA",
            "SELECT a, (SELECT Sum(b) FROM JB WHERE JB.b = JA.a) AS s FROM JA",
            "SELECT a, (SELECT TOP 1 h FROM JB WHERE JB.b = JA.a ORDER BY h DESC) AS t FROM JA",
            "SELECT a, (SELECT TOP 1 h FROM JB WHERE JB.b = JA.a ORDER BY h) AS t FROM JA",
            "SELECT a, (SELECT First(h) FROM JB WHERE JB.b = JA.a) AS f, (SELECT Last(h) FROM JB WHERE JB.b = JA.a) AS l FROM JA",
            "SELECT a, (SELECT h FROM JB WHERE JB.b = JA.a) AS t FROM JA",
            "SELECT a, (SELECT k FROM JC WHERE JC.c = (SELECT Min(b) FROM JB WHERE JB.b >= JA.a)) AS k FROM JA",
            "SELECT a, (SELECT Count(*) FROM JB WHERE JB.b = JA.a AND JB.h IN (SELECT j2.h FROM JB AS j2 WHERE j2.b = JA.a)) AS n FROM JA",
            "SELECT a, (SELECT Count(*) FROM JB) + 1 AS n FROM JA",
            "SELECT a, (SELECT Count(*) FROM JB WHERE JB.b = JA.a) AS n FROM JA ORDER BY (SELECT Count(*) FROM JB WHERE JB.b = JA.a) DESC, a",
            "SELECT a FROM JA WHERE (SELECT Count(*) FROM JB WHERE JB.b = JA.a) > 0",
            "SELECT g, (SELECT Count(*) FROM JB WHERE JB.h = JA.g) AS n FROM JA GROUP BY g",
            "SELECT g, Count(*) AS c FROM JA GROUP BY g HAVING Count(*) > (SELECT Count(*) FROM JC WHERE JC.c = 4)");

        yield return Stmts("sub-derived-table-forms", R1("SQL-B"),
            "Which derived-table spellings are accepted, with which output names and error numbers: no alias, AS alias, alias without AS, the legacy [SELECT ...]. form, ORDER BY and TOP inside the derived table, a parenthesized derived table with an alias, " +
            "a LEFT JOIN of two derived tables, a derived table with a repeated column name and with an unnamed expression (read as Expr1), over a UNION, nested three deep, a star qualified by the alias, and an inner alias equal to the outer alias?",
            "SELECT a FROM (SELECT a FROM JA)",
            "SELECT d.a FROM (SELECT a FROM JA) AS d",
            "SELECT d.a FROM (SELECT a FROM JA) d",
            "SELECT d.a FROM [SELECT a FROM JA]. AS d",
            "SELECT d.a FROM (SELECT a FROM JA ORDER BY a DESC) AS d",
            "SELECT TOP 2 d.a FROM (SELECT a FROM JA ORDER BY a DESC) AS d",
            "SELECT d.a FROM ((SELECT a FROM JA) AS d)",
            "SELECT d.a, e.b FROM (SELECT a FROM JA) AS d LEFT JOIN (SELECT b FROM JB) AS e ON d.a = e.b",
            "SELECT d.a FROM (SELECT a, a FROM JA) AS d",
            "SELECT d.a FROM (SELECT a + 1 FROM JA) AS d",
            "SELECT d.Expr1 FROM (SELECT a + 1 FROM JA) AS d",
            "SELECT d.a FROM (SELECT a FROM JA UNION SELECT b FROM JB) AS d",
            "SELECT d.a FROM (SELECT a FROM (SELECT a FROM (SELECT a FROM JA) AS e1) AS e2) AS d",
            "SELECT d.* FROM (SELECT * FROM JA, JB) AS d",
            "SELECT d.a FROM (SELECT a FROM JA AS d) AS d");

        int[] depths = [10, 32, 49, 50, 51, 64];
        yield return Read("sub-nesting-depth", R1("SQL-B"),
            $"Where does subquery nesting depth stop being accepted, and at which phase? Only the depth varies (n = {string.Join(", ", depths)}) over the one-row table ONE: n scalar subqueries nested in the select list, " +
            "and n IN-subqueries nested in WHERE. Each text is opened directly with the standard capture." + StmtTail,
            p =>
            {
                foreach (int n in depths)
                {
                    string scalar = "SELECT x FROM ONE", inList = "SELECT x FROM ONE";
                    for (int i = 0; i < n; i++)
                    {
                        scalar = $"SELECT ({scalar}) AS v FROM ONE";
                        inList = $"SELECT x FROM ONE WHERE x IN ({inList})";
                    }
                    Q(p, $"s{n}", scalar);
                    Q(p, $"i{n}", inList);
                }
            });
    }

    // Grouping and aggregates -----------------------------------------------------------------------------------

    private static IEnumerable<Probe> Groups()
    {
        yield return Stmts("group-expression-forms", R1("SQL-B", "SQL-KEY-02"),
            "GA holds g (1, 1, 1, 2, 2, Null, Null, Null, 3), h, v and d with Nulls. Which GROUP BY forms are accepted, with which rows and error numbers: an expression (v Mod 2) written in full and in parentheses, by output alias, by position 1, " +
            "two keys with an expression, UCase, concatenation and IIf keys, a constant, 1 + 1 and Null as the key, a Boolean key, a select item that is not in the GROUP BY, an expression of a grouped column, a key repeated, and a grouped column absent from the select list?",
            "SELECT v Mod 2 AS r, Count(*) AS n FROM GA GROUP BY v Mod 2",
            "SELECT v Mod 2 AS r, Count(*) AS n FROM GA GROUP BY r",
            "SELECT v Mod 2 AS r, Count(*) AS n FROM GA GROUP BY 1",
            "SELECT v Mod 2 AS r, Count(*) AS n FROM GA GROUP BY (v Mod 2)",
            "SELECT g, v Mod 2 AS r, Count(*) AS n FROM GA GROUP BY g, v Mod 2",
            "SELECT UCase(h) AS u, Count(*) AS n FROM GA GROUP BY UCase(h)",
            "SELECT g & '' AS s, Count(*) AS n FROM GA GROUP BY g & ''",
            "SELECT IIf(v > 6, 'hi', 'lo') AS b, Count(*) AS n FROM GA GROUP BY IIf(v > 6, 'hi', 'lo')",
            "SELECT Count(*) AS n FROM GA GROUP BY 'k'",
            "SELECT Count(*) AS n FROM GA GROUP BY 1 + 1",
            "SELECT Count(*) AS n FROM GA GROUP BY Null",
            "SELECT v > 5 AS b, Count(*) AS n FROM GA GROUP BY v > 5",
            "SELECT g, Count(*) AS n FROM GA GROUP BY g + 0",
            "SELECT g + 0 AS q, Count(*) AS n FROM GA GROUP BY g",
            "SELECT g, Count(*) AS n FROM GA GROUP BY g, g",
            "SELECT Count(*) AS n FROM GA GROUP BY g");

        yield return Stmts("group-aggregate-placement", R1("SQL-AGG-OCCUR", "SQL-B"),
            "Which placements of aggregates and grouping are accepted, with which rows and error numbers: an aggregate in WHERE, a bare column beside Count(*) with no GROUP BY, a select column not in the GROUP BY, HAVING with no GROUP BY (aggregate and plain column), " +
            "HAVING on a non-grouped column, on a grouped column and on an output alias, an aggregate of an aggregate, an aggregate as a GROUP BY key, ORDER BY an aggregate absent from the select list and an alias, HAVING with a subquery and with Sum(v) Is Null, and WHERE plus HAVING together?",
            "SELECT g FROM GA WHERE Sum(v) > 5 GROUP BY g",
            "SELECT g, Count(*) AS n FROM GA",
            "SELECT g, Count(*) AS n FROM GA GROUP BY h",
            "SELECT Count(*) AS n FROM GA HAVING Count(*) > 3",
            "SELECT Count(*) AS n FROM GA HAVING v > 3",
            "SELECT g, Count(*) AS n FROM GA GROUP BY g HAVING h = 'x'",
            "SELECT g, Count(*) AS n FROM GA GROUP BY g HAVING g > 1",
            "SELECT g, Count(*) AS n FROM GA GROUP BY g HAVING n > 1",
            "SELECT g, Sum(v) AS s FROM GA GROUP BY g HAVING s > 5",
            "SELECT g, Sum(Count(*)) AS s FROM GA GROUP BY g",
            "SELECT g, Sum(v) AS s FROM GA GROUP BY g, Sum(v)",
            "SELECT g, Count(*) AS n FROM GA GROUP BY g ORDER BY Sum(v)",
            "SELECT g, Count(*) AS n FROM GA GROUP BY g ORDER BY n DESC, g",
            "SELECT g, Count(*) AS n FROM GA GROUP BY g HAVING Count(*) > (SELECT Count(*) FROM CX)",
            "SELECT g, Count(*) AS n FROM GA GROUP BY g HAVING Sum(v) Is Null",
            "SELECT g, Count(*) AS n FROM GA WHERE v > 5 GROUP BY g HAVING Count(*) = 1");

        yield return Stmts("group-null-keys", R1("SQL-KEY-02", "SQL-EQ-01"),
            "How do Null keys group and order: GROUP BY g, GROUP BY h and GROUP BY g, h over GA (Null in each) with Count(*), Count of a Null-bearing column and Sum; the same keys listed in the other order with ORDER BY; DISTINCT over two keys and over one with ORDER BY DESC; " +
            "ORDER BY an aggregate; Min/Max/First/Last per group; and GROUP BY over JA.a and over a LEFT JOIN with Count(JB.b)?",
            "SELECT g, Count(*) AS n, Count(v) AS nv, Sum(v) AS s FROM GA GROUP BY g",
            "SELECT h, Count(*) AS n, Count(v) AS nv FROM GA GROUP BY h",
            "SELECT g, h, Count(*) AS n FROM GA GROUP BY g, h",
            "SELECT g, h, Count(*) AS n FROM GA GROUP BY h, g ORDER BY h, g",
            "SELECT DISTINCT g, h FROM GA",
            "SELECT DISTINCT g FROM GA ORDER BY g DESC",
            "SELECT g, Count(*) AS n FROM GA GROUP BY g ORDER BY Count(*) DESC, g",
            "SELECT g, Min(v) AS mn, Max(v) AS mx, First(v) AS f, Last(v) AS l FROM GA GROUP BY g",
            "SELECT a, Count(*) AS n FROM JA GROUP BY a",
            "SELECT JA.a, Count(JB.b) AS n FROM JA LEFT JOIN JB ON JA.a = JB.b GROUP BY JA.a",
            "SELECT JB.b, Count(*) AS n FROM JB GROUP BY JB.b");

        List<string> sums = [];
        string[] cols = ["i", "b", "l", "s", "c", "d"];
        foreach (string c in cols)
        {
            sums.Add($"SELECT g, Sum({c}) AS v FROM SI GROUP BY g ORDER BY g");
            sums.Add($"SELECT g, Avg({c}) AS v FROM SI GROUP BY g ORDER BY g");
        }
        foreach (string c in new[] { "i", "b", "l", "c" })
        {
            for (int g = 1; g <= 4; g++)
            {
                sums.Add($"SELECT Sum({c}) AS v FROM SI WHERE g = {g}");
            }
        }
        yield return Stmts("agg-sum-boundary", R1("SQL-AGG-TYPE"),
            "SI (Integer i, Byte b, Long l, Single s, Currency c, Double d) has four groups: g1 = the type maximum and 1 (Currency: maximum and 0.0001), g2 = two maxima, g3 = two halves that add to one past the maximum (Single and Double: two large values), " +
            "g4 = the minimum and -1. What Field type and value, or error and its call, do Sum and Avg give per group in one grouped statement for each column, and Sum of i, b, l and c for each group alone?",
            [.. sums]);

        yield return Stmts("agg-yesno-memo-guid", R1("SQL-AGG-TYPE", "SQL-AGG-FIRST"),
            "What do Sum, Avg, Min, Max, Count, First and Last give, or refuse with which error number, over a Yes/No column (TB.yn: True, False, Null, ...), over a Memo column (TB.m) and over a GUID column (TG.gd with Null and a repeated value); " +
            "with the result Field types?",
            "SELECT Sum(yn) AS s, Avg(yn) AS a, Min(yn) AS mn, Max(yn) AS mx, Count(yn) AS c FROM TB",
            "SELECT First(yn) AS f, Last(yn) AS l FROM TB",
            "SELECT Min(m) AS mn FROM TB",
            "SELECT Max(m) AS mx FROM TB",
            "SELECT First(m) AS f, Last(m) AS l FROM TB",
            "SELECT Count(m) AS c FROM TB",
            "SELECT Sum(m) AS s FROM TB",
            "SELECT Min(gd) AS mn, Max(gd) AS mx, First(gd) AS f, Count(gd) AS c FROM TG",
            "SELECT Sum(gd) AS s FROM TG");

        yield return Stmts("agg-min-max-text", R1("SQL-AGG-TYPE", "SQL-KEY-03"),
            "Which values do Min, Max, First and Last pick over TB.t (b, B, Null, a, empty, a-umlaut, space, A) and which representative does GROUP BY t return for case variants; what changes with a WHERE excluding the empty string, UCase, a concatenation, " +
            "an all-Null selection, and GROUP BY / ORDER BY on the Yes/No column?",
            "SELECT Min(t) AS mn, Max(t) AS mx FROM TB",
            "SELECT Min(t) AS mn, Max(t) AS mx FROM TB WHERE t <> ''",
            "SELECT Min(UCase(t)) AS mn, Max(UCase(t)) AS mx FROM TB",
            "SELECT Min(t & '') AS mn, Max(t & '') AS mx FROM TB",
            "SELECT First(t) AS f, Last(t) AS l FROM TB",
            "SELECT t, Count(*) AS n FROM TB GROUP BY t",
            "SELECT t, Count(*) AS n FROM TB GROUP BY t ORDER BY t DESC",
            "SELECT Min(t) AS mn FROM TB WHERE t Is Null",
            "SELECT yn, Count(*) AS n FROM TB GROUP BY yn",
            "SELECT yn, Count(*) AS n FROM TB GROUP BY yn ORDER BY yn");

        static string Stat(string col, string from) =>
            $"SELECT Var({col}) AS a, VarP({col}) AS b, StDev({col}) AS c, StDevP({col}) AS d FROM {from}";
        yield return Stmts("agg-stat-argument-types", R1("SQL-AGG-SINGLE", "SQL-AGG-TYPE"),
            "What do Var, VarP, StDev and StDevP give, with which Field type, over one row and over two rows of a Long column (GA.v), over two rows of each of Integer, Byte, Long, Single, Currency, Double and Date (SI rows 2 and 8), " +
            "over Yes/No (True and False), over Text holding numbers (GA.tx '1', '2', '3.5') and over Text holding a non-number ('1', 'x')?",
            Stat("v", "GA WHERE id = 1"),
            Stat("v", "GA WHERE id IN (1, 2)"),
            Stat("i", "SI WHERE id IN (2, 8)"),
            Stat("b", "SI WHERE id IN (2, 8)"),
            Stat("l", "SI WHERE id IN (2, 8)"),
            Stat("s", "SI WHERE id IN (2, 8)"),
            Stat("c", "SI WHERE id IN (2, 8)"),
            Stat("d", "SI WHERE id IN (2, 8)"),
            Stat("dt", "SI WHERE id IN (2, 8)"),
            Stat("yn", "TB WHERE id IN (1, 2)"),
            Stat("tx", "GA WHERE id IN (1, 2, 3)"),
            Stat("tx", "GA WHERE id IN (1, 4)"));

        yield return Stmts("agg-distinct-forms", R1("SQL-AGG-COUNT", "SQL-EQ-01"),
            "Which DISTINCT and ALL forms around aggregates are accepted, with which values and error numbers, over GA.v (10, 20, Null, Null, 5, 6, 7, Null, 5): Sum, Avg, Min and Count with DISTINCT, Count and Sum with ALL, Count(DISTINCT *), " +
            "Count(DISTINCT g, h), Count over a derived DISTINCT and DISTINCTROW table (Count(*) and Count(v)), Count(DISTINCT v) with GROUP BY, and a DISTINCT over an aggregate select list?",
            "SELECT Sum(DISTINCT v) AS s FROM GA",
            "SELECT Avg(DISTINCT v) AS a FROM GA",
            "SELECT Min(DISTINCT v) AS m FROM GA",
            "SELECT Count(DISTINCT v) AS c FROM GA",
            "SELECT Count(ALL v) AS c FROM GA",
            "SELECT Sum(ALL v) AS s FROM GA",
            "SELECT Count(DISTINCT *) AS c FROM GA",
            "SELECT Count(DISTINCT g, h) AS c FROM GA",
            "SELECT Count(*) AS c FROM (SELECT DISTINCT v FROM GA)",
            "SELECT Count(v) AS c FROM (SELECT DISTINCT v FROM GA)",
            "SELECT Count(*) AS c FROM (SELECT DISTINCTROW v FROM GA)",
            "SELECT Count(DISTINCT v) AS c FROM GA GROUP BY g",
            "SELECT DISTINCT Count(*) AS c FROM GA GROUP BY g",
            "SELECT DISTINCT g, Count(*) AS c FROM GA GROUP BY g");

        yield return Stmts("agg-expression-composition", R1("SQL-AGG-OCCUR", "SQL-AGG-TYPE"),
            "Which expressions built around aggregates over GA are accepted, with which result type and value or error number: Sum/Count and Sum\\Count, Sum(v + 1) beside Sum(v) + 1, Sum of an IIf, IIf over a Sum, Sum & '', unary minus and Abs of a Sum, " +
            "Sum times a fraction, Sum Mod 2, the same aggregate twice (distinct aliases and the same alias), Sum(Sum(v)), Max(Count(*)) with GROUP BY, the same through a derived table, Sum(v) / Sum(d), Avg(v * 2), Sum(CDbl(v)) and Sum(Nz(v, 0))?",
            "SELECT Sum(v) / Count(*) AS r, Avg(v) AS a FROM GA",
            "SELECT Sum(v) \\ Count(*) AS r FROM GA",
            "SELECT Sum(v + 1) AS s, Sum(v) + 1 AS s2 FROM GA",
            "SELECT Sum(IIf(v > 6, 1, 0)) AS s FROM GA",
            "SELECT IIf(Sum(v) > 5, 'a', 'b') AS r FROM GA",
            "SELECT Sum(v) & '' AS r FROM GA",
            "SELECT -Sum(v) AS r, Abs(Sum(v)) AS a FROM GA",
            "SELECT Sum(v) * 1.5 AS r FROM GA",
            "SELECT Sum(v) Mod 2 AS r FROM GA",
            "SELECT Sum(v) AS a, Sum(v) AS b FROM GA",
            "SELECT Count(*) AS n, Count(*) AS n FROM GA",
            "SELECT Sum(Sum(v)) AS r FROM GA",
            "SELECT Max(Count(*)) AS r FROM GA GROUP BY g",
            "SELECT Max(n) AS r FROM (SELECT g, Count(*) AS n FROM GA GROUP BY g)",
            "SELECT Sum(v) / Sum(d) AS r FROM GA",
            "SELECT Avg(v * 2) AS a FROM GA",
            "SELECT Sum(CDbl(v)) AS s FROM GA",
            "SELECT Sum(Nz(v, 0)) AS s FROM GA");
    }

    // TOP, Memo ------------------------------------------------------------------------------------------------

    private static IEnumerable<Probe> TopAndMemo()
    {
        yield return Stmts("top-expression-forms", R3("SQL-TOP-02"),
            "Which TOP arguments are accepted, with which rows and error numbers, over GA ordered by id (9 rows): a parenthesized number, 1+1, CLng('2'), '2', &H2, 2E0, 1E1, 1000000000000, 2147483647, 2147483648, -0, a scalar subquery, " +
            "an undeclared name, Null, True, a column name, no argument, and 2.0?",
            "SELECT TOP (2) id FROM GA ORDER BY id",
            "SELECT TOP 1+1 id FROM GA ORDER BY id",
            "SELECT TOP CLng('2') id FROM GA ORDER BY id",
            "SELECT TOP '2' id FROM GA ORDER BY id",
            "SELECT TOP &H2 id FROM GA ORDER BY id",
            "SELECT TOP 2E0 id FROM GA ORDER BY id",
            "SELECT TOP 1E1 id FROM GA ORDER BY id",
            "SELECT TOP 1000000000000 id FROM GA ORDER BY id",
            "SELECT TOP 2147483647 id FROM GA ORDER BY id",
            "SELECT TOP 2147483648 id FROM GA ORDER BY id",
            "SELECT TOP -0 id FROM GA ORDER BY id",
            "SELECT TOP (SELECT 2) id FROM GA ORDER BY id",
            "SELECT TOP [nosuch] id FROM GA ORDER BY id",
            "SELECT TOP Null id FROM GA ORDER BY id",
            "SELECT TOP True id FROM GA ORDER BY id",
            "SELECT TOP v id FROM GA ORDER BY id",
            "SELECT TOP id FROM GA ORDER BY id",
            "SELECT TOP 2.0 id FROM GA ORDER BY id");

        yield return Read("top-parameter-forms", R3("SQL-TOP-02", "SQL-PIVOT-05"),
            "Does a declared PARAMETERS name work as the TOP argument: TOP pn, TOP [pn], TOP (pn) with pn Long = 2, TOP pn PERCENT with pn Double = 30, and TOP pn with pn Text = '2'? " +
            "For each temporary QueryDef the Parameters (Name, Type, Direction) are read, the value is assigned and read back, then the QueryDef is opened as a snapshot with Fields Name/Type/Size, every row, Close and DBEngine.Errors." + ParamTail,
            p =>
            {
                (string Sql, Arg Value)[] items =
                [
                    ("PARAMETERS pn Long; SELECT TOP pn id FROM GA ORDER BY id;", A.I4(2)),
                    ("PARAMETERS pn Long; SELECT TOP [pn] id FROM GA ORDER BY id;", A.I4(2)),
                    ("PARAMETERS pn Long; SELECT TOP (pn) id FROM GA ORDER BY id;", A.I4(2)),
                    ("PARAMETERS pn Double; SELECT TOP pn PERCENT id FROM GA ORDER BY id;", A.R8(30)),
                    ("PARAMETERS pn Text ( 5 ); SELECT TOP pn id FROM GA ORDER BY id;", A.Str("2")),
                ];
                for (int i = 0; i < items.Length; i++)
                {
                    string qd = $"tp{i}";
                    TempQd(p, qd, items[i].Sql);
                    QdParams(p, qd);
                    SetParam(p, qd, "pn", items[i].Value, readBack: true);
                    QdOpen(p, qd, qd + "r");
                    p.Release(qd);
                }
            });

        List<string> percents = [];
        foreach (int pct in new[] { 1, 10, 11, 12, 22, 23, 33, 34, 50, 67, 99 })
        {
            percents.Add($"SELECT TOP {pct} PERCENT id FROM GA ORDER BY id");
        }
        percents.AddRange(
        [
            "SELECT TOP 20 PERCENT id, v FROM GA ORDER BY v",
            "SELECT TOP 50 PERCENT g FROM GA ORDER BY g",
            "SELECT TOP 1 PERCENT x FROM CX ORDER BY x",
            "SELECT TOP 50 PERCENT y FROM CY ORDER BY y",
            "SELECT TOP 50 PERCENT z FROM CZ",
            "SELECT TOP 100 PERCENT id FROM GA WHERE 1 = 0",
        ]);
        yield return Stmts("top-percent-counts", R3("SQL-TOP-02", "SQL-TOP-01"),
            "How many rows does TOP n PERCENT return from GA (9 rows) ordered by id for n = 1, 10, 11, 12, 22, 23, 33, 34, 50, 67, 99; with a tie group and a Null in the order key (v ordered, 20 PERCENT; g ordered, 50 PERCENT); " +
            "over CX (2 rows, 1 PERCENT), CY (3 rows, 50 PERCENT), an empty table, and a WHERE that removes all rows?",
            [.. percents]);

        yield return Stmts("memo-distinct-scope", R1("SQL-EQ-01", "SQL-KEY-03"),
            $"MMP holds pairs of Memo values equal up to position p and different at p ('a' versus 'b'), for p = {string.Join(", ", MemoPositions)}, plus a repeat of row 1. " +
            "How many value classes does DISTINCT over Memo give overall and per p, what does GROUP BY on the Memo column give (error number if refused), and which lengths do the rows have (control)?",
            "SELECT Count(*) AS n FROM (SELECT DISTINCT m FROM MMP)",
            "SELECT p, Count(*) AS n FROM (SELECT DISTINCT p, m FROM MMP) GROUP BY p ORDER BY p",
            "SELECT p, Count(*) AS n FROM MMP GROUP BY p, m ORDER BY p",
            "SELECT DISTINCT Len(m) AS l FROM MMP ORDER BY Len(m)");

        yield return Stmts("memo-compare-scope", R1("SQL-EQ-01", "SQL-KEY-03", "SQL-B"),
            "Over the MMP Memo pairs (equal up to position p, different at p), which row pairs (a.id < b.id) does Memo equality match when written as an INNER JOIN ON, as a comma join WHERE, " +
            "which pairs does Memo < order, and which rows does a Memo IN-subquery and a Memo = scalar-subquery against row 1 select?",
            "SELECT a.id, b.id FROM MMP AS a INNER JOIN MMP AS b ON a.m = b.m WHERE a.id < b.id",
            "SELECT a.id, b.id FROM MMP AS a, MMP AS b WHERE a.m = b.m AND a.id < b.id",
            "SELECT a.id, b.id FROM MMP AS a, MMP AS b WHERE a.m < b.m AND a.id < b.id",
            "SELECT id FROM MMP WHERE m IN (SELECT m FROM MMP WHERE id = 1)",
            "SELECT id FROM MMP WHERE m = (SELECT m FROM MMP WHERE id = 1)");

        yield return Stmts("memo-order-scope", R1("SQL-B", "SQL-KEY-03"),
            "In what order do the MMP rows (Memo equal up to p, different at p) come from ORDER BY the Memo column with id as the second key, ascending and descending, " +
            "from ORDER BY Left(m, 255) and ORDER BY m & '' (a Text-converted key) with the same second key, and from ORDER BY the Memo column alone?",
            "SELECT id, p FROM MMP ORDER BY m, id",
            "SELECT id, p FROM MMP ORDER BY m DESC, id",
            "SELECT id, p FROM MMP ORDER BY Left(m, 255), id",
            "SELECT id, p FROM MMP ORDER BY m & '', id",
            "SELECT id, p FROM MMP ORDER BY m");
    }

    // ORDER BY and UNION ---------------------------------------------------------------------------------------

    private static IEnumerable<Probe> OrderAndUnion()
    {
        yield return Stmts("order-position-forms", R1("SQL-B"),
            "Which ORDER BY position forms are accepted, with which order and error numbers, over GA: 2, 2 DESC with a second key, 0, 3 (out of range for a two-column select), -1, 1.5, '2', 1 + 1, a position over SELECT *, over an expression column, " +
            "when aliases swap the names (id AS v, v AS id), with DISTINCT, with GROUP BY and a second position, over a UNION, with TOP, and a repeated position?",
            "SELECT id, v FROM GA ORDER BY 2",
            "SELECT id, v FROM GA ORDER BY 2 DESC, 1",
            "SELECT id, v FROM GA ORDER BY 0",
            "SELECT id, v FROM GA ORDER BY 3",
            "SELECT id, v FROM GA ORDER BY -1",
            "SELECT id, v FROM GA ORDER BY 1.5",
            "SELECT id, v FROM GA ORDER BY '2'",
            "SELECT id, v FROM GA ORDER BY 1 + 1",
            "SELECT * FROM GA ORDER BY 3",
            "SELECT id, v + 1 FROM GA ORDER BY 2",
            "SELECT id AS v, v AS id FROM GA ORDER BY 1",
            "SELECT DISTINCT g FROM GA ORDER BY 1 DESC",
            "SELECT g, Count(*) AS n FROM GA GROUP BY g ORDER BY 2 DESC, 1",
            "SELECT a FROM JA UNION SELECT b FROM JB ORDER BY 1 DESC",
            "SELECT TOP 3 id FROM GA ORDER BY 1 DESC",
            "SELECT id FROM GA ORDER BY 1, 1");

        yield return Stmts("order-expression-forms", R1("SQL-B"),
            "Which ORDER BY keys are accepted, with which order and error numbers: a non-selected column under DISTINCT (two cases), a non-grouped column under GROUP BY, Null and a text constant as the key, a Boolean, an IIf placing Nulls, Nz, an IIf mixing Long and Text, " +
            "text numbers ordered as Text and as Val, a correlated subquery, an alias equal to a source column (alias form and qualified form), several DESC keys, ASC DESC together, a repeated key, and a key from the null-extended side of an outer join?",
            "SELECT DISTINCT g FROM GA ORDER BY v",
            "SELECT DISTINCT g FROM GA ORDER BY h",
            "SELECT g, Count(*) AS n FROM GA GROUP BY g ORDER BY v",
            "SELECT id FROM GA ORDER BY Null",
            "SELECT id FROM GA ORDER BY 'x'",
            "SELECT id FROM GA ORDER BY v > 5, id",
            "SELECT id FROM GA ORDER BY IIf(v Is Null, 1, 0), v",
            "SELECT id FROM GA ORDER BY Nz(v, 0) DESC, id",
            "SELECT id FROM GA ORDER BY IIf(id = 1, 'a', 1), id",
            "SELECT id FROM GA ORDER BY tx, id",
            "SELECT id FROM GA ORDER BY Val(tx), id",
            "SELECT id FROM GA ORDER BY (SELECT Count(*) FROM JB WHERE JB.b = GA.v), id",
            "SELECT id AS g FROM GA ORDER BY g, id",
            "SELECT id AS g FROM GA ORDER BY GA.g, id",
            "SELECT id, g FROM GA ORDER BY g DESC, h DESC, id",
            "SELECT id FROM GA ORDER BY id ASC DESC",
            "SELECT id FROM GA ORDER BY id, id",
            "SELECT JA.a FROM JA LEFT JOIN JB ON JA.a = JB.b ORDER BY JB.h, JA.a");

        List<string> ladder = [];
        foreach ((string a, string b) in new[]
        {
            ("b", "i"), ("i", "b"), ("i", "l"), ("l", "i"), ("l", "s"), ("s", "l"), ("s", "d"), ("d", "s"), ("l", "d"), ("b", "c"), ("i", "c"), ("l", "c"), ("c", "l"),
            ("c", "d"), ("d", "c"), ("yn", "b"), ("yn", "i"), ("yn", "l"), ("l", "yn"), ("yn", "c"), ("yn", "d"),
        })
        {
            ladder.Add($"SELECT {a} AS v FROM SI WHERE id = 1 UNION ALL SELECT {b} FROM SI WHERE id = 1");
        }
        ladder.AddRange(
        [
            "SELECT b AS v FROM SI WHERE id = 1 UNION ALL SELECT i FROM SI WHERE id = 1 UNION ALL SELECT l FROM SI WHERE id = 1",
            "SELECT l AS v FROM SI WHERE id = 1 UNION ALL SELECT i FROM SI WHERE id = 1 UNION ALL SELECT b FROM SI WHERE id = 1",
            "SELECT i AS v FROM SI WHERE id = 1 UNION ALL SELECT d FROM SI WHERE id = 1 UNION ALL SELECT b FROM SI WHERE id = 1",
            "SELECT i AS v FROM SI WHERE id = 1 UNION ALL SELECT t FROM TB WHERE id = 1",
            "SELECT yn AS v FROM TB WHERE id = 1 UNION ALL SELECT t FROM TB WHERE id = 1",
            "SELECT yn AS v FROM TB WHERE id = 1 UNION ALL SELECT m FROM TB WHERE id = 1",
            "SELECT l AS v FROM SI WHERE id = 1 UNION ALL SELECT m FROM TB WHERE id = 1",
            "SELECT dt AS v FROM SI WHERE id = 1 UNION ALL SELECT t FROM TB WHERE id = 1",
            "SELECT t AS v FROM TB WHERE id = 1 UNION ALL SELECT dt FROM SI WHERE id = 1",
        ]);
        yield return Stmts("union-all-numeric-ladder", R1("SQL-UNION-02"),
            "What common Field type and value types does UNION ALL give for each ordered pair among Byte, Integer, Long, Single, Double, Currency and Yes/No (both orders for most pairs; SI row 1 holds the type maxima), " +
            "for three-arm chains in several orders, and for Integer, Yes/No, Long or Date against Text and Memo (TB row 1)?",
            [.. ladder]);

        yield return Stmts("union-dedup-cross-type", R1("SQL-UNION-02", "SQL-EQ-03"),
            "Which arms collapse under UNION (not UNION ALL) when the same value arrives with another type or spelling, and with which output type and representative: Long 1 against 1.0, '1', CDbl(1), CCur(1); Single against Double; True against -1; " +
            "1 against Null; '' against Null; 'a' against 'A' and against 'a '; a Date against its serial number; CDbl(0.1) against CCur(0.1) and CSng(0.1); and UNION ALL of 1 and 1.0 as the control?",
            "SELECT x AS v FROM ONE UNION SELECT 1.0 FROM ONE",
            "SELECT x AS v FROM ONE UNION SELECT '1' FROM ONE",
            "SELECT x AS v FROM ONE UNION SELECT CDbl(1) FROM ONE",
            "SELECT x AS v FROM ONE UNION SELECT CCur(1) FROM ONE",
            "SELECT CSng(1) AS v FROM ONE UNION SELECT CDbl(1) FROM ONE",
            "SELECT True AS v FROM ONE UNION SELECT -1 FROM ONE",
            "SELECT 1 AS v FROM ONE UNION SELECT Null FROM ONE",
            "SELECT '' AS v FROM ONE UNION SELECT Null FROM ONE",
            "SELECT 'a' AS v FROM ONE UNION SELECT 'A' FROM ONE",
            "SELECT 'a' AS v FROM ONE UNION SELECT 'a ' FROM ONE",
            "SELECT #2024-01-01# AS v FROM ONE UNION SELECT 45292 FROM ONE",
            "SELECT 1 AS v FROM ONE UNION ALL SELECT 1.0 FROM ONE",
            "SELECT CDbl(0.1) AS v FROM ONE UNION SELECT CCur(0.1) FROM ONE",
            "SELECT CSng(0.1) AS v FROM ONE UNION SELECT CDbl(0.1) FROM ONE");

        yield return Stmts("union-order-by-forms", R1("SQL-UNION-01"),
            "Which ORDER BY keys over a UNION of JA.a and JB.b (with Nulls) are accepted, with which order and error numbers: the first arm's alias, the first arm's column name, the second arm's column name and alias, a position, a position out of range, " +
            "an expression of the alias, a table-qualified name, ORDER BY inside the first arm, a repeated key, two positions over two columns, WHERE belonging to the second arm, TOP in an arm, GROUP BY on the second arm, and an IIf over the alias?",
            "SELECT a AS v FROM JA UNION SELECT b FROM JB ORDER BY v",
            "SELECT a AS v FROM JA UNION SELECT b FROM JB ORDER BY a",
            "SELECT a AS v FROM JA UNION SELECT b FROM JB ORDER BY b",
            "SELECT a AS v FROM JA UNION SELECT b AS w FROM JB ORDER BY w",
            "SELECT a AS v FROM JA UNION SELECT b FROM JB ORDER BY 1 DESC",
            "SELECT a AS v FROM JA UNION SELECT b FROM JB ORDER BY 2",
            "SELECT a AS v FROM JA UNION SELECT b FROM JB ORDER BY v + 1",
            "SELECT a AS v FROM JA UNION SELECT b FROM JB ORDER BY JA.a",
            "SELECT a AS v FROM JA ORDER BY a UNION SELECT b FROM JB",
            "SELECT a AS v FROM JA UNION SELECT b FROM JB ORDER BY v, v",
            "SELECT a, g FROM JA UNION SELECT b, h FROM JB ORDER BY 2, 1",
            "SELECT a AS v FROM JA UNION SELECT b FROM JB WHERE b > 1 ORDER BY v",
            "SELECT TOP 2 a AS v FROM JA UNION SELECT b FROM JB ORDER BY v",
            "SELECT a AS v FROM JA UNION SELECT b FROM JB GROUP BY b",
            "SELECT a AS v FROM JA UNION SELECT b FROM JB ORDER BY IIf(v Is Null, 0, 1), v");

        yield return Stmts("union-syntax-forms", R1("SQL-UNION-01"),
            "Which set-operation spellings are accepted, with which rows and error numbers: parenthesized arms (both, and the second only), UNION DISTINCT, INTERSECT, EXCEPT, MINUS, UNION CORRESPONDING, a trailing semicolon after three UNION ALL arms, " +
            "TABLE as an arm after UNION and in both arms of UNION ALL, a dangling UNION (with and without a following arm), a UNION as a derived table, and a UNION as the child of IN and of EXISTS?",
            "(SELECT a FROM JA) UNION (SELECT b FROM JB)",
            "SELECT a FROM JA UNION (SELECT b FROM JB)",
            "SELECT a FROM JA UNION DISTINCT SELECT b FROM JB",
            "SELECT a FROM JA INTERSECT SELECT b FROM JB",
            "SELECT a FROM JA EXCEPT SELECT b FROM JB",
            "SELECT a FROM JA MINUS SELECT b FROM JB",
            "SELECT a FROM JA UNION CORRESPONDING SELECT b FROM JB",
            "SELECT a FROM JA UNION ALL SELECT b FROM JB UNION ALL SELECT c FROM JC;",
            "SELECT d, e FROM JD UNION TABLE JD",
            "TABLE JD UNION ALL TABLE JD",
            "SELECT a FROM JA UNION SELECT b FROM JB UNION",
            "SELECT a FROM JA UNION",
            "SELECT * FROM (SELECT a FROM JA UNION SELECT b FROM JB) AS u WHERE u.a > 1",
            "SELECT a FROM JA WHERE a IN (SELECT b FROM JB UNION ALL SELECT c FROM JC)",
            "SELECT a FROM JA WHERE EXISTS (SELECT 1 FROM JB UNION SELECT 1 FROM JC)");

        int[] arms = [2, 16, 32, 33, 50, 51, 64];
        yield return Read("union-arm-count", R1("SQL-UNION-01", "SQL-B"),
            $"Where does the number of UNION arms stop being accepted, and at which phase? Only the count varies (n = {string.Join(", ", arms)}) over the one-row table ONE: n arms joined by UNION (rows read), " +
            "and the same n arms joined by UNION ALL inside SELECT Count(*) AS n FROM (...)." + StmtTail,
            p =>
            {
                foreach (int n in arms)
                {
                    string[] all = [.. Enumerable.Repeat("SELECT x FROM ONE", n)];
                    Q(p, $"u{n}", string.Join(" UNION ", all));
                    Q(p, $"a{n}", $"SELECT Count(*) AS n FROM ({string.Join(" UNION ALL ", all)})");
                }
            });
    }

    // Crosstabs -------------------------------------------------------------------------------------------------

    private static IEnumerable<Probe> Pivots()
    {
        const string head = "TRANSFORM Sum(amt) AS s SELECT g FROM PV GROUP BY g PIVOT ";
        string[] lists =
        [
            "p IN ('a', Null)", "p IN ()", "p IN ('a' & '', 'b')", "p IN (UCase('a'), 'b')", "p IN ('a', 1)", "p IN (1, 2)",
            "p IN ('a', 'b', 'c', 'd', 'e', 'f', 'g', 'h')", "p IN ([p1])", "p IN (SELECT p FROM PV)", "p IN ('')", "p IN (Null)",
            "pl IN (1, 9, 10, 100, -5, 2)", "pl IN (10, 9, 2, 1)", "pl IN (1.0, 1)", "pl IN (1.5)",
            "pb IN (True, False)", "pb IN (0, -1)", "pc IN (0.5, 0.25, 10)", "p IN ('a' 'b')", "p IN 'a', 'b'",
        ];
        yield return Stmts("pivot-in-list-forms", R3("SQL-PIVOT-01", "SQL-PIVOT-04"),
            "PV holds row headings g (g1, g2, g3, Null), pivot candidates p (Text with Null), pl (Long), pc (Currency) and pb (Yes/No), and Amt with a Null. Which fixed PIVOT ... IN forms are accepted, with which column names, Types and order, and with which cells " +
            "(error numbers where refused): a Null member, an empty list, expression members, mixed Text/Long members, Long members against a Text pivot, eight members of which five are absent, an undeclared name, a subquery, the empty string, only Null, " +
            "Long lists in several orders with a duplicate-valued pair (1.0, 1) and a fraction, Yes/No lists spelled True/False and 0/-1, Currency members, and two malformed spellings?",
            [.. lists.Select(l => head + l)]);

        string[] clauses =
        [
            "TRANSFORM Sum(amt) AS s, Count(*) AS c SELECT g FROM PV GROUP BY g PIVOT p",
            "TRANSFORM amt AS s SELECT g FROM PV GROUP BY g PIVOT p",
            "TRANSFORM Sum(amt) AS s SELECT g FROM PV PIVOT p",
            "TRANSFORM Sum(amt) SELECT g FROM PV GROUP BY g PIVOT p",
            "TRANSFORM Sum(amt) + 1 AS s SELECT g FROM PV GROUP BY g PIVOT p",
            "TRANSFORM Sum(amt) AS s SELECT g, g2 FROM PV GROUP BY g, g2 PIVOT p",
            "TRANSFORM Sum(amt) AS s SELECT g FROM PV GROUP BY g PIVOT p, pl",
            "TRANSFORM Sum(amt) AS s SELECT g FROM PV GROUP BY g ORDER BY g DESC PIVOT p",
            "TRANSFORM Sum(amt) AS s SELECT g FROM PV GROUP BY g PIVOT p ORDER BY g",
            "TRANSFORM Sum(amt) AS s SELECT g FROM PV WHERE amt > 15 GROUP BY g PIVOT p",
            "TRANSFORM Sum(amt) AS s SELECT g FROM PV GROUP BY g HAVING Count(*) > 1 PIVOT p",
            "TRANSFORM Sum(amt) AS s SELECT g & '' AS gg FROM PV GROUP BY g & '' PIVOT p",
            "TRANSFORM Sum(amt) AS s SELECT g FROM PV GROUP BY g PIVOT UCase(p)",
            "TRANSFORM Sum(amt) AS s SELECT g FROM PV GROUP BY g PIVOT g",
            "SELECT * FROM (TRANSFORM Sum(amt) AS s SELECT g FROM PV GROUP BY g PIVOT p) AS x",
            "TRANSFORM Sum(amt) AS s SELECT g FROM (SELECT * FROM PV WHERE id < 6) AS d GROUP BY g PIVOT p",
            "TRANSFORM Sum(PV.amt) AS s SELECT PV.g FROM PV LEFT JOIN CX ON PV.id = CX.x GROUP BY PV.g PIVOT PV.p",
        ];
        yield return Stmts("pivot-clause-forms", R3("SQL-PIVOT-02", "SQL-PIVOT-01"),
            "Which crosstab clause shapes over PV are accepted, with which Fields (Name, Type, Size) and cells, and with which error numbers where refused: two aggregates in TRANSFORM, a non-aggregate, no GROUP BY, no alias for the aggregate, an expression over the aggregate, " +
            "two row-heading columns, two pivot expressions, ORDER BY before and after PIVOT, WHERE, HAVING, an expression row heading, an expression pivot, the pivot equal to the row heading, a crosstab as a derived table, a derived-table source, and a LEFT JOIN source?",
            clauses);

        yield return Read("pivot-parameter-positions", R3("SQL-PIVOT-05", "SQL-B"),
            "Where may a parameter appear in a crosstab, and when is the schema available: a declared Text parameter as a PIVOT IN member, a declared Long in the WHERE, in the TRANSFORM expression, and in an expression PIVOT heading (p & pa), and an undeclared [which g] in the WHERE. " +
            "For each temporary QueryDef the Parameters (Name, Type, Direction, Value) are read, the QueryDef is opened with nothing assigned (Fields Name/Type/Size, rows, Errors), then the parameter is assigned and the QueryDef is opened again the same way." + ParamTail,
            p =>
            {
                (string Sql, string Name, Arg Value)[] items =
                [
                    ("PARAMETERS pa Text ( 5 ); TRANSFORM Sum(amt) AS s SELECT g FROM PV GROUP BY g PIVOT p IN (pa, 'b');", "pa", A.Str("a")),
                    ("PARAMETERS pa Long; TRANSFORM Sum(amt) AS s SELECT g FROM PV WHERE amt > pa GROUP BY g PIVOT p;", "pa", A.I4(25)),
                    ("PARAMETERS pa Long; TRANSFORM Sum(amt) + pa AS s SELECT g FROM PV GROUP BY g PIVOT p;", "pa", A.I4(5)),
                    ("PARAMETERS pa Text ( 5 ); TRANSFORM Count(*) AS n SELECT g FROM PV GROUP BY g PIVOT p & pa;", "pa", A.Str("q")),
                    ("TRANSFORM Sum(amt) AS s SELECT g FROM PV WHERE g = [which g] GROUP BY g PIVOT p;", "which g", A.Str("g1")),
                ];
                for (int i = 0; i < items.Length; i++)
                {
                    string qd = $"pp{i}";
                    TempQd(p, qd, items[i].Sql);
                    QdParams(p, qd, withValue: true);
                    QdOpen(p, qd, qd + "a");
                    SetParam(p, qd, items[i].Name, items[i].Value);
                    QdOpen(p, qd, qd + "b");
                    p.Release(qd);
                }
            });

        const string dyn = "TRANSFORM Count(*) AS n SELECT g FROM PV GROUP BY g PIVOT ";
        yield return Stmts("pivot-heading-sort-order", R3("SQL-PIVOT-01", "SQL-PIVOT-04"),
            "In which order and with which names and Types do dynamic crosstab headings come over PV when the pivot is: pl (1, 2, 9, 10, 100, -5), CStr(pl), pl & '', pc (with Null), pb (with Null), p, g2 (with Null), -pl, pl * 0.5 and Format(pl, '000')? " +
            "Row headings include a Null group.",
            dyn + "pl", dyn + "CStr(pl)", dyn + "pl & ''", dyn + "pc", dyn + "pb", dyn + "p", dyn + "g2", dyn + "-pl", dyn + "pl * 0.5", dyn + "Format(pl, '000')");
    }

    // Parameters ------------------------------------------------------------------------------------------------

    private static IEnumerable<Probe> ParameterProbes()
    {
        string[] typeNames =
        [
            "Bit", "Byte", "Short", "Long", "Currency", "IEEESingle", "IEEEDouble", "DateTime", "Text", "Text ( 10 )", "LongText", "LongBinary", "Binary", "Binary ( 4 )",
            "Guid", "Decimal", "Decimal ( 10, 2 )", "Value", "Integer", "Single", "Double", "Float", "Real", "Memo", "Counter", "Date", "Boolean", "YesNo", "Numeric", "Char",
            "VarChar ( 5 )", "BigInt", "Variant", "Object", "OLEObject", "TinyInt", "SmallInt", "Int", "Money",
        ];
        yield return Read("params-declared-type-names", R3("SQL-B"),
            $"Which type names does a PARAMETERS declaration accept, with which Parameters Type and QueryDef.Fields Type/Size of the output (SELECT pX AS v FROM ONE), and with which error number where refused? Names: {string.Join("; ", typeNames)}. " +
            "For each temporary QueryDef the Parameters (Name, Type, Direction) and Fields (Name, Type, Size) are read; no value is assigned and nothing is opened." + ParamTail,
            p =>
            {
                for (int i = 0; i < typeNames.Length; i++)
                {
                    string qd = $"pt{i}", fl = qd + "_f", fi = qd + "_fi";
                    TempQd(p, qd, $"PARAMETERS pX {typeNames[i]}; SELECT pX AS v FROM ONE;");
                    QdParams(p, qd);
                    p.GetBind(fl, qd, "Fields").Each(fl, fi, 8, b => b.Get(fi, "Name").Get(fi, "Type").Get(fi, "Size")).Release(fl).Release(qd);
                }
            });

        Arg[] values = [A.I4(70000), A.R8(2.5), A.Str("12"), A.Str("abc"), A.Null, A.Bool(true), A.Date(45292.5)];
        (string Name, string[] Types, string Which)[] groups =
        [
            ("params-values-integers", ["Byte", "Short", "Long"], "Byte, Short and Long"),
            ("params-values-fractional", ["Currency", "IEEESingle", "IEEEDouble"], "Currency, IEEESingle and IEEEDouble"),
            ("params-values-other", ["Bit", "DateTime", "Text ( 20 )"], "Bit, DateTime and Text(20)"),
        ];
        foreach ((string name, string[] types, string which) in groups)
        {
            yield return Read(name, R3("SQL-KEY-01", "SQL-B"),
                $"For a parameter declared {which} (PARAMETERS pX <type>; SELECT pX AS v FROM ONE;), what happens when it is assigned each of: Long 70000, Double 2.5, Text '12', Text 'abc', Null, Boolean True and Date 2024-01-01 12:00? " +
                "Per assignment: the Parameter's Value is read back, then the QueryDef is opened as a snapshot (Fields Name/Type/Size, the row's value and VARIANT tag, Close, DBEngine.Errors); the call at which a refusal appears is part of the observation." + ParamTail,
                p =>
                {
                    for (int t = 0; t < types.Length; t++)
                    {
                        string qd = $"pv{t}";
                        TempQd(p, qd, $"PARAMETERS pX {types[t]}; SELECT pX AS v FROM ONE;");
                        QdParams(p, qd, withValue: true);
                        for (int i = 0; i < values.Length; i++)
                        {
                            SetParam(p, qd, "pX", values[i], readBack: true);
                            QdOpen(p, qd, $"{qd}r{i}", 4, 8);
                        }
                        p.Release(qd);
                    }
                });
        }

        yield return Read("params-null-and-unset", R3("SQL-KEY-02", "SQL-SUB-02"),
            "How does a declared parameter behave when it is assigned Null, assigned Empty, assigned the empty string (Text only) or left unassigned: projected as itself, in p + 1, Nz, (x = p), (p Is Null), p & 'a', Len(p) and LIKE; " +
            "and for the WHERE forms x = p, x <> p, p Is Null, x NOT IN (p, 1) and x BETWEEN p AND 2 with p Null? Per temporary QueryDef: Parameters (Name, Type, Direction, Value), an open with nothing assigned, then an open after each assignment (Fields Name/Type/Size, rows, Errors)." + ParamTail,
            p =>
            {
                (string Sql, Arg[] Values)[] items =
                [
                    ("PARAMETERS p Long; SELECT x, p AS v, p + 1 AS w, Nz(p, 7) AS z, (x = p) AS e, (p Is Null) AS n FROM CX;", [A.Null, A.Empty]),
                    ("PARAMETERS p Text ( 5 ); SELECT y, p AS v, p & 'a' AS w, Len(p) AS l, (y = p) AS e, (y LIKE p) AS k FROM CY;", [A.Null, A.Empty, A.Str("")]),
                    ("PARAMETERS p Long; SELECT x FROM CX WHERE x = p;", [A.Null]),
                    ("PARAMETERS p Long; SELECT x FROM CX WHERE x <> p;", [A.Null]),
                    ("PARAMETERS p Long; SELECT x FROM CX WHERE p Is Null;", [A.Null]),
                    ("PARAMETERS p Long; SELECT x FROM CX WHERE x NOT IN (p, 1);", [A.Null]),
                    ("PARAMETERS p Long; SELECT x FROM CX WHERE x BETWEEN p AND 2;", [A.Null]),
                ];
                for (int i = 0; i < items.Length; i++)
                {
                    string qd = $"pn{i}";
                    TempQd(p, qd, items[i].Sql);
                    QdParams(p, qd, withValue: true);
                    QdOpen(p, qd, $"{qd}u");
                    for (int j = 0; j < items[i].Values.Length; j++)
                    {
                        SetParam(p, qd, "p", items[i].Values[j], readBack: true);
                        QdOpen(p, qd, $"{qd}r{j}");
                    }
                    p.Release(qd);
                }
            });

        string[] positions =
        [
            "SELECT x FROM CX WHERE x = p", "SELECT x, p AS v FROM CX", "SELECT x FROM CX ORDER BY p", "SELECT x FROM CX ORDER BY x * p DESC",
            "SELECT x FROM CX WHERE x IN (p, 99)", "SELECT x FROM CX WHERE x BETWEEN p AND 5", "SELECT x FROM CX GROUP BY x HAVING Count(*) >= p - 1",
            "SELECT Sum(x * p) AS s FROM CX", "SELECT x, y FROM CX INNER JOIN CY ON CX.x = p",
            "SELECT x FROM CX WHERE x = (SELECT Max(x) FROM CX WHERE x <= p)", "SELECT d.x FROM (SELECT x FROM CX WHERE x <= p) AS d",
            "SELECT x FROM CX UNION SELECT p FROM ONE", "SELECT x FROM CX WHERE x = p OR x = p + 1", "SELECT x FROM CX WHERE EXISTS (SELECT * FROM CY WHERE CX.x = p)",
            "SELECT IIf(x = p, 'a', 'b') AS r FROM CX", "SELECT x FROM CX WHERE x = CLng(p)",
        ];
        yield return Read("params-positions", R3("SQL-B"),
            "In which syntactic positions does a declared parameter (PARAMETERS p Long, assigned 2) work, with which Parameters list, rows and error number: WHERE, select list, ORDER BY as a bare key and inside an expression, an IN list, BETWEEN, HAVING, an aggregate argument, " +
            "a join ON, a scalar subquery, a derived table, a UNION arm, an OR, an EXISTS correlated test, IIf, and CLng? Per temporary QueryDef: Parameters (Name, Type, Direction), the assignment with read-back, then the open (Fields Name/Type/Size, rows, Errors)." + ParamTail,
            p =>
            {
                for (int i = 0; i < positions.Length; i++)
                {
                    string qd = $"ps{i}";
                    TempQd(p, qd, "PARAMETERS p Long; " + positions[i] + ";");
                    QdParams(p, qd);
                    SetParam(p, qd, "p", A.I4(2), readBack: true);
                    QdOpen(p, qd, qd + "r");
                    p.Release(qd);
                }
            });

        string[] syntax =
        [
            "PARAMETERS p Long SELECT x FROM CX WHERE x = p", "PARAMETERS p Long; PARAMETERS q Long; SELECT x FROM CX WHERE x = p AND x = q",
            "PARAMETERS p Long, ; SELECT x FROM CX WHERE x = p", "PARAMETERS p; SELECT x FROM CX WHERE x = p", "PARAMETERS ; SELECT x FROM CX",
            "PARAMETERS [p 1] Long; SELECT x FROM CX WHERE x = [p 1]", "PARAMETERS [p]] 2] Long; SELECT x FROM CX WHERE x = [p]] 2]",
            "PARAMETERS [Date] Long; SELECT x FROM CX WHERE x = [Date]", "PARAMETERS x Long; SELECT x FROM CX WHERE x = x", "PARAMETERS CX.x Long; SELECT x FROM CX WHERE x = 1",
            "PARAMETERS p Long; SELECT x FROM CX UNION SELECT p FROM ONE", "SELECT x FROM CX PARAMETERS p Long", "PARAMETERS P Long, p Long; SELECT x FROM CX WHERE x = p",
            "PARAMETERS p Long; SELECT x FROM CX WHERE x = P", "PARAMETERS p long; SELECT x FROM CX WHERE x = p",
            $"PARAMETERS {new string('n', 64)} Long; SELECT x FROM CX WHERE x = {new string('n', 64)}", $"PARAMETERS {new string('n', 65)} Long; SELECT x FROM CX WHERE x = {new string('n', 65)}",
        ];
        yield return Read("params-declaration-syntax", R3("SQL-B"),
            "Which PARAMETERS declaration spellings are accepted by CreateQueryDef, with which Parameters list (Name, Type, Direction, Value) and error number where refused: a missing semicolon, two declarations, a trailing comma, a missing type, an empty list, bracketed names " +
            "(with a space, with an escaped bracket, a reserved word), a name equal to a column, a qualified name, a UNION body, the declaration after the SELECT, two names that differ only in case, a use in another case, a lower-case type, and names of 64 and 65 characters?" + ParamTail,
            p =>
            {
                for (int i = 0; i < syntax.Length; i++)
                {
                    string qd = $"pd{i}";
                    TempQd(p, qd, syntax[i]);
                    QdParams(p, qd, withValue: true);
                    p.Release(qd);
                }
            });

        int[] counts = [50, 254, 255, 256, 257, 300];
        yield return Read("params-count-limit", R3("SQL-B"),
            $"Where does the number of declared parameters stop being accepted, and at which call? Only the count varies (n = {string.Join(", ", counts)}) with type Long: PARAMETERS p1 Long, ..., pn Long, used only as p1 in one text and in an OR chain of all n in another. " +
            "For each temporary QueryDef the error channels of CreateQueryDef, Parameters.Count and DBEngine.Errors are read; nothing is assigned or opened." + ParamTail,
            p =>
            {
                foreach (int n in counts)
                {
                    string decl = "PARAMETERS " + string.Join(", ", Enumerable.Range(1, n).Select(i => $"p{i} Long")) + "; ";
                    string one = $"{decl}SELECT x FROM CX WHERE x = p1;";
                    string all = $"{decl}SELECT x FROM CX WHERE " + string.Join(" OR ", Enumerable.Range(1, n).Select(i => $"x = p{i}")) + ";";
                    foreach ((string tag, string sql) in new[] { ("o", one), ("a", all) })
                    {
                        string qd = $"pc{tag}{n}";
                        TempQd(p, qd, sql);
                        p.GetBind(qd + "_ps", qd, "Parameters").Get(qd + "_ps", "Count").Release(qd + "_ps").Release(qd);
                    }
                }
            });

        string[] readTypes = ["Long", "Byte", "Bit", "DateTime", "Currency", "Text ( 5 )"];
        Arg[] readValues = [A.Str("5"), A.R8(2.5), A.Bool(true), A.Null, A.Empty, A.I4(-1), A.Str("abc")];
        yield return Read("params-value-readback", R3("SQL-KEY-01"),
            "Is a value coerced when it is assigned to Parameter.Value, or only at open? For parameters declared Long, Byte, Bit, DateTime, Currency and Text(5) (PARAMETERS pX <type>; SELECT pX AS v FROM ONE;), " +
            "the values Text '5', Double 2.5, Boolean True, Null, Empty, Long -1 and Text 'abc' are assigned in turn and Parameter.Value is read back after each (VARIANT tag and value); nothing is opened." + ParamTail,
            p =>
            {
                for (int t = 0; t < readTypes.Length; t++)
                {
                    string qd = $"pr{t}";
                    TempQd(p, qd, $"PARAMETERS pX {readTypes[t]}; SELECT pX AS v FROM ONE;");
                    QdParams(p, qd, withValue: true);
                    foreach (Arg v in readValues)
                    {
                        SetParam(p, qd, "pX", v, readBack: true);
                    }
                    p.Release(qd);
                }
            });
    }

    // BETWEEN and LIKE ------------------------------------------------------------------------------------------

    private static IEnumerable<Probe> Predicates()
    {
        static string Bt(string where) => $"SELECT id FROM BT WHERE {where} ORDER BY id";
        yield return Stmts("between-bound-order", R1("SQL-B"),
            "BT holds n (1, 5, 10, Null, 7, -3, 5), t (a, B, c, Null, D, empty, b), dt (2024-01-01 ... with a time of day on one row), f and Memo m. Which ids pass BETWEEN with the bounds in order and reversed, NOT BETWEEN in both orders, equal bounds, " +
            "Text bounds in both orders and with bounds that differ only in case, Date bounds in both orders, Double bounds in both orders, a negative low bound against a high first bound, the empty string as a bound, and Memo bounds in both orders?",
            Bt("n BETWEEN 1 AND 5"), Bt("n BETWEEN 5 AND 1"), Bt("n NOT BETWEEN 5 AND 1"), Bt("n NOT BETWEEN 1 AND 5"), Bt("n BETWEEN 5 AND 5"),
            Bt("t BETWEEN 'a' AND 'c'"), Bt("t BETWEEN 'c' AND 'a'"), Bt("t BETWEEN 'B' AND 'b'"), Bt("t BETWEEN 'b' AND 'B'"),
            Bt("dt BETWEEN #2024-01-01# AND #2024-01-05#"), Bt("dt BETWEEN #2024-01-05# AND #2024-01-01#"),
            Bt("f BETWEEN 0.5 AND 1.5"), Bt("f BETWEEN 1.5 AND 0.5"), Bt("n BETWEEN -3 AND 1"), Bt("n BETWEEN 10 AND -3"), Bt("t BETWEEN '' AND 'a'"),
            Bt("m BETWEEN 'a' AND 'b'"), Bt("m BETWEEN 'b' AND 'a'"));

        yield return Stmts("between-null-mixed-types", R1("SQL-B", "SQL-KEY-02"),
            "Which BT ids (and which projected truth values) result from BETWEEN with a Null bound on each side and both, NOT BETWEEN and NOT (...) with a Null bound, a Null left operand, Long against Text bounds ('5', '1' and '5'), Text against Long bounds, " +
            "Date against Long serial bounds and against Text date bounds, expression bounds, the column itself as both bounds, a column as one bound, AND/OR following the BETWEEN, and subquery bounds?",
            Bt("n BETWEEN Null AND 5"), Bt("n BETWEEN 1 AND Null"), Bt("n BETWEEN Null AND Null"), Bt("NOT (n BETWEEN Null AND 5)"), Bt("n NOT BETWEEN Null AND 5"),
            Bt("Null BETWEEN 1 AND 5"), Bt("n BETWEEN 1 AND '5'"), Bt("n BETWEEN '1' AND '5'"), Bt("t BETWEEN 1 AND 5"),
            Bt("dt BETWEEN 45292 AND 45296"), Bt("dt BETWEEN '2024-01-01' AND '2024-01-05'"), Bt("n BETWEEN 2 * 3 AND 1 + 0"), Bt("n BETWEEN n AND n"),
            Bt("n BETWEEN id AND 10"), Bt("n BETWEEN 1 AND 5 AND id > 1"), Bt("n BETWEEN 1 AND 5 OR id = 3"),
            "SELECT id, (n BETWEEN 5 AND 1) AS r1, (n BETWEEN 1 AND 5) AS r2, (n NOT BETWEEN 1 AND 5) AS r3, (n BETWEEN Null AND 5) AS r4 FROM BT ORDER BY id",
            Bt("n BETWEEN (SELECT Min(x) FROM CX) AND (SELECT Max(x) FROM CX)"));

        static string Lk(string pattern) => $"SELECT id FROM LK WHERE t LIKE '{pattern}' ORDER BY id";
        string[] classes =
        [
            "[a-c]", "[!a-c]", "[^a-c]", "[ab]", "[!ab]", "[a-]", "[-a]", "[c-a]", "[a-c-e]", "[a-cA-C]", "a[b-c]c", "a[!b-c]c", "[*]", "[?]", "[#]", "[-]", "[!-]", "[[]", "[]]", "[!]]",
        ];
        yield return Stmts("like-bracket-classes", R1("SQL-B"),
            "LK holds 42 text rows (letters in both cases, a-umlaut, ae-ligature, digits, a*c, a?c, a#c, a[c, a]c, a-c, a!c, a^c, single wildcard characters, trailing and leading spaces, empty, Null). " +
            "Which ids match each bracket form (ascending id), with error numbers where refused: [a-c], [!a-c], [^a-c], [ab], [!ab], a trailing and a leading hyphen, a reversed range, two ranges and a hyphen, two ranges together, a class inside a pattern (positive and negated), " +
            "classes holding *, ?, #, -, a negated hyphen, an open bracket and a close bracket, and a negated close bracket?",
            [.. classes.Select(Lk)]);

        string[] malformed = ["[", "[a", "[a-", "[!", "[!]", "[]", "[z-a]", "[a-a]", "a[", "]", "a]c", "[[]]", "[[a]", "[a[]", "[*", "[#"];
        yield return Stmts("like-malformed-patterns", R1("SQL-B"),
            "Which malformed or edge bracket patterns are accepted, with which ids and error numbers (and at which call): an unmatched open bracket (alone, after a letter, with text), an open range, an open negation, [!], an empty class [], a reversed range, a one-letter range, " +
            "a lone close bracket, a close bracket after text, doubled brackets, and an open bracket before * or #?",
            [.. malformed.Select(Lk)]);

        string[] wildcards =
        [
            "*", "?", "#", "a*", "*c", "a?c", "a#c", "?*", "???", "#*", "*#*", "a[*]c", "a[?]c", "a[#]c", "a[[]c", "a[]]c", "%", "a%c", "a_c", "_", "", "**", "a**c",
        ];
        yield return Stmts("like-wildcard-forms", R1("SQL-B"),
            "Which LK ids match each wildcard pattern: *, ?, #, a*, *c, a?c, a#c, ?*, ???, #*, *#*, a bracketed *, ?, # or [ or ] inside a pattern, the ANSI-92 characters % and _ alone and inside a pattern (under DAO's ANSI-89 mode), the empty pattern, doubled * ?",
            [.. wildcards.Select(Lk)]);

        string longA(int n) => new('a', n);
        yield return Stmts("like-null-pattern-edges", R1("SQL-B", "SQL-KEY-02"),
            "What do LK rows with a Null text and an empty text do under: LIKE Null, NOT LIKE '*', Null LIKE '*', t LIKE '' , t LIKE '*' with Is Null, NOT (t LIKE 'a*'), t LIKE t, a pattern built from the row (Left(t, 1) & '*'), a computed pattern ('*' & ''), " +
            "the projected truth values of LIKE with Null; and what is the limit on a pattern's length (a pattern of 100, 255, 256 and 500 a's followed by *)?",
            "SELECT id FROM LK WHERE t LIKE Null ORDER BY id",
            "SELECT id FROM LK WHERE t NOT LIKE '*' ORDER BY id",
            "SELECT id FROM LK WHERE Null LIKE '*' ORDER BY id",
            "SELECT id FROM LK WHERE t LIKE '' ORDER BY id",
            "SELECT id FROM LK WHERE t LIKE '*' AND t Is Null ORDER BY id",
            "SELECT id FROM LK WHERE NOT (t LIKE 'a*') ORDER BY id",
            "SELECT id FROM LK WHERE t LIKE t ORDER BY id",
            "SELECT id FROM LK WHERE t LIKE Left(t, 1) & '*' ORDER BY id",
            "SELECT id FROM LK WHERE t LIKE '*' & '' ORDER BY id",
            "SELECT id, (t LIKE 'a*') AS m, (t NOT LIKE 'a*') AS nm, (t LIKE Null) AS mn FROM LK WHERE id > 18 ORDER BY id",
            Lk(longA(100) + "*"), Lk(longA(255) + "*"), Lk(longA(256) + "*"), Lk(longA(500) + "*"));

        yield return Stmts("like-operand-types", R1("SQL-B"),
            "Which BT ids does LIKE match when the operand is not Text: Long ('1*', and a numeric pattern 5), Double ('1.*'), Date (a leading month '1/*' and '2024*', CStr, Format), Yes/No ('-*' and 'T*'), Memo ('a*' and '*b*'), " +
            "and a Null pattern over a Long; with error numbers where refused?",
            "SELECT id FROM BT WHERE n LIKE '1*' ORDER BY id",
            "SELECT id FROM BT WHERE n LIKE 5 ORDER BY id",
            "SELECT id FROM BT WHERE n LIKE Null ORDER BY id",
            "SELECT id FROM BT WHERE f LIKE '1.*' ORDER BY id",
            "SELECT id FROM BT WHERE dt LIKE '1/*' ORDER BY id",
            "SELECT id FROM BT WHERE dt LIKE '2024*' ORDER BY id",
            "SELECT id FROM BT WHERE CStr(dt) LIKE '*2024*' ORDER BY id",
            "SELECT id FROM BT WHERE Format(dt, 'yyyy') LIKE '2024' ORDER BY id",
            "SELECT id FROM BT WHERE yn LIKE '-*' ORDER BY id",
            "SELECT id FROM BT WHERE yn LIKE 'T*' ORDER BY id",
            "SELECT id FROM BT WHERE m LIKE 'a*' ORDER BY id",
            "SELECT id FROM BT WHERE m LIKE '*b*' ORDER BY id");

        string[] collation =
        [
            "a", "A", "[a-c]", "[A-C]", "[a-z]", "[!a-z]", "[\u00E4]", "[a-\u00E4]", "[\u00E0-\u00E6]", "a[e]", "[\u00E6]", "ss", "\u00DF", "[s]s", "z", "Z", "[Z-a]", "a[\u00E4]c",
        ];
        yield return Stmts("like-collation-ranges", R1("SQL-B", "SQL-KEY-03"),
            "Which LK ids does LIKE match with case and accent variants (a, A, a-umlaut, A-umlaut, a-acute, ae-ligature, sharp s, ss, z, Z): single letters in both cases, ranges in both cases ([a-c], [A-C], [a-z], [!a-z]), classes holding accented letters, " +
            "a range ending at an accented letter, a range across case ([Z-a]), the ligature and sharp-s spellings, and a class between letters?",
            [.. collation.Select(Lk)]);
    }
}
