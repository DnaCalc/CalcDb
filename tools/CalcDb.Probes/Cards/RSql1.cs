namespace CalcDb.Probes.Cards;

/// <summary>
/// R-SQL-1: ordinary SELECT semantics through ACE DAO recordsets (A-02 SQL-B, SQL-J, SQL-EQ, SQL-KEYS, SQL-AGG,
/// SQL-UNION, SQL-SUB, SQL-DEMAND, SQL-REL). One fixture with known rows is created through ACE DAO in ACCDB and
/// Jet 4 MDB. Each read probe runs exact SQL text and captures Fields metadata at open (before row demand), then
/// every row's typed Field values to EOF, then DBEngine.Errors. Probes ask; nothing here states an expected answer.
/// </summary>
public sealed class RSql1 : ICard
{
    private const string Card = "r-sql-1";
    private const string CardId = "R-SQL-1";

    private static readonly (string Ext, int Version)[] Formats = [("accdb", Dao.Version120), ("mdb", Dao.Version40)];

    /// <summary>Field.Properties names read for type-focused queries (absence is itself an observation).</summary>
    private static readonly string[] TypeProps = ["Precision", "Scale", "DecimalPlaces"];

    private const double Two53 = 9007199254740992.0;

    private static string Fixture(string ext) => $"{Card}-{ext}-main";

    public IEnumerable<Probe> Probes()
    {
        List<Probe> all = [.. Formats.Select(f => Creation(f.Ext, f.Version))];
        all.AddRange(SyntaxScope());
        all.AddRange(Joins());
        all.AddRange(Equality());
        all.AddRange(Aggregates());
        all.AddRange(Unions());
        all.AddRange(Subqueries());
        all.AddRange(OrderAndDemand());
        return all;
    }

    // Fixture ---------------------------------------------------------------------------------------------------

    private static Probe Creation(string ext, int version) =>
        new(Card, $"create-{ext}-main", [CardId, "SQL-REL"],
            $"R-SQL-1 fixture {Fixture(ext)}: tables L, R, RX, LD, NS, E, TX, S, N, ND, NB, AG, AGI and [T Space], and saved queries QL, QL2, QU3A, QU3B, " +
            $"with the rows written in RSql1.Build. Created in {ext.ToUpperInvariant()} by DBEngine.CreateDatabase(path, dbLangGeneral, {version}); " +
            "DDL and literal rows go through Database.Execute, exact typed values (Double bits, Single, Currency, Date, embedded NUL) through table-type AddNew/Value/Update. " +
            "Every statement's outcome is captured (a refused type such as BIGINT in MDB is an observation) and the closed file is kept.",
            p =>
            {
                p.Modules("before-create").Create().Modules("after-create").Get("eng", "Version")
                    .CallBind("db", "eng", "CreateDatabase", A.Out($"{Fixture(ext)}.{ext}"), A.Str(Dao.LangGeneral), A.I4(version))
                    .Get("db", "Version");
                Build(p);
                p.Call("db", "Close").Release("db").Release("eng").Modules("after-close").Files("after-close");
            })
        { CreatesFixture = true };

    private static void Build(P p)
    {
        void X(string sql) => p.Call("db", "Execute", A.Str(sql));

        // SQL-J base: L ids 1,2,3; R (rid,lid,qty) = (10,1,0),(11,1,5),(12,2,0) exactly as the card states.
        X("CREATE TABLE L (id LONG CONSTRAINT PK_L PRIMARY KEY, name TEXT(20))");
        X("INSERT INTO L VALUES (1, 'a')");
        X("INSERT INTO L VALUES (2, 'b')");
        X("INSERT INTO L VALUES (3, 'c')");
        X("CREATE TABLE R (rid LONG CONSTRAINT PK_R PRIMARY KEY, lid LONG, qty LONG)");
        X("INSERT INTO R VALUES (10, 1, 0)");
        X("INSERT INTO R VALUES (11, 1, 5)");
        X("INSERT INTO R VALUES (12, 2, 0)");
        // SQL-J variants: several matches for lid 1, a Null join key, a Null qty.
        X("CREATE TABLE RX (rid LONG CONSTRAINT PK_RX PRIMARY KEY, lid LONG, qty LONG)");
        X("INSERT INTO RX VALUES (20, 1, 5)");
        X("INSERT INTO RX VALUES (21, 1, 6)");
        X("INSERT INTO RX (rid, qty) VALUES (22, 5)");
        X("INSERT INTO RX (rid, lid) VALUES (23, 3)");
        // Duplicate left keys and a Null left key (SQL-J, SQL-SUB-02 outer Null).
        X("CREATE TABLE LD (n LONG CONSTRAINT PK_LD PRIMARY KEY, id LONG)");
        X("INSERT INTO LD VALUES (1, 1)");
        X("INSERT INTO LD VALUES (2, 1)");
        X("INSERT INTO LD (n) VALUES (3)");
        // All-Null set and empty table (SQL-SUB-02, SQL-AGG-EMPTY).
        X("CREATE TABLE NS (id LONG CONSTRAINT PK_NS PRIMARY KEY, v LONG)");
        X("INSERT INTO NS (id) VALUES (1)");
        X("INSERT INTO NS (id) VALUES (2)");
        X("CREATE TABLE E (id LONG CONSTRAINT PK_E PRIMARY KEY, g LONG, d DOUBLE)");
        // Conversion-failure witness: id 3 does not convert to a number; equal values before and after it.
        X("CREATE TABLE TX (id LONG CONSTRAINT PK_TX PRIMARY KEY, t TEXT(10))");
        X("INSERT INTO TX VALUES (1, '1')");
        X("INSERT INTO TX VALUES (2, '1')");
        X("INSERT INTO TX VALUES (3, 'x')");
        X("INSERT INTO TX VALUES (4, '1')");
        X("INSERT INTO TX VALUES (5, '2')");
        // Bracketed identifiers with a space and a reserved word.
        X("CREATE TABLE [T Space] ([Order] LONG, [Col Name] TEXT(10))");
        X("INSERT INTO [T Space] VALUES (2, 'two')");
        X("INSERT INTO [T Space] VALUES (1, 'one')");

        // Text equality domain (SQL-EQ-01, SQL-KEY-03): Null, empty, trailing/leading space, case, accent,
        // embedded U+0000, long common prefixes (Text 55 + 1; Memo 300 + 1), ligature, exact duplicate.
        X("CREATE TABLE S (id LONG CONSTRAINT PK_S PRIMARY KEY, t TEXT(60), m MEMO)");
        string tp = new('p', 55), mp = new('p', 300);
        (Arg T, Arg M)[] texts =
        [
            (A.Null, A.Null), (A.Str(""), A.Str("")), (A.Str("abc"), A.Str("abc")), (A.Str("abc "), A.Str("abc ")),
            (A.Str("ABC"), A.Str("ABC")), (A.Str("Abc"), A.Str("Abc")), (A.Str("\u00E4bc"), A.Str("\u00E4bc")),
            (A.Str("abc\0x"), A.Str("abc\0x")), (A.Str("abc\0y"), A.Str("abc\0y")),
            (A.Str(tp + "1"), A.Str(mp + "1")), (A.Str(tp + "2"), A.Str(mp + "2")),
            (A.Str("b"), A.Str("b")), (A.Str(" abc"), A.Str(" abc")), (A.Str("ae"), A.Str("ae")),
            (A.Str("\u00E6"), A.Str("\u00E6")), (A.Str("abc"), A.Str("abc")),
        ];
        Rows(p, "S", ["id", "t", "m"], [.. texts.Select((x, i) => new[] { A.I4(i + 1), x.T, x.M })]);

        // Numeric domains (SQL-EQ-02, SQL-KEY-01, SQL-AGG-TYPE): equal values across types, signed zero, 0.1,
        // type maxima (overflow witnesses for Sum), an all-Null row and an exact duplicate of row 1.
        X("CREATE TABLE N (id LONG CONSTRAINT PK_N PRIMARY KEY, b BYTE, i SHORT, l LONG, s SINGLE, d DOUBLE, c CURRENCY, dt DATETIME, t TEXT(10))");
        Rows(p, "N", ["id", "b", "i", "l", "s", "d", "c", "dt", "t"],
            [A.I4(1), A.UI1(1), A.I2(1), A.I4(1), A.R4(1f), A.R8(1.0), A.Cy(1m), A.Date(1.0), A.Str("1")],
            [A.I4(2), A.Null, A.Null, A.Null, A.Null, A.Null, A.Null, A.Null, A.Null],
            [A.I4(3), A.UI1(0), A.I2(0), A.I4(0), A.R4(-0.0f), A.R8(-0.0), A.Cy(0m), A.Date(0.0), A.Str("0")],
            [A.I4(4), A.Null, A.Null, A.Null, A.R4(0.1f), A.R8(0.1), A.Cy(0.1m), A.Date(0.1), A.Str("0.1")],
            [A.I4(5), A.UI1(255), A.I2(short.MaxValue), A.I4(int.MaxValue), A.R4(float.MaxValue), A.R8(1e308), A.Cy(922337203685477.5807m), A.Date(2958465.0), A.Str("max")],
            [A.I4(6), A.UI1(1), A.I2(1), A.I4(1), A.R4(1f), A.R8(1.0), A.Cy(1m), A.Date(1.0), A.Str("1")]);
        // Decimal and Large Number in their own tables, so a refused type does not remove table N.
        X("CREATE TABLE ND (id LONG CONSTRAINT PK_ND PRIMARY KEY, x DECIMAL(18,4))");
        Rows(p, "ND", ["id", "x"],
            [A.I4(1), A.Str("1")], [A.I4(2), A.Null], [A.I4(3), A.Str("0")], [A.I4(4), A.Str("0.1")],
            [A.I4(5), A.Str("99999999999999.9999")], [A.I4(6), A.Str("1.0000")]);
        X("CREATE TABLE NB (id LONG CONSTRAINT PK_NB PRIMARY KEY, v BIGINT)");
        Rows(p, "NB", ["id", "v"], [A.I4(1), A.Str("9007199254740993")], [A.I4(2), A.Str("9007199254740992")], [A.I4(3), A.I4(1)]);

        // Aggregate groups (SQL-AGG-*): g -> Double values in id order (Null = absent value).
        X("CREATE TABLE AG (id LONG CONSTRAINT PK_AG PRIMARY KEY, g LONG, d DOUBLE)");
        (int G, double? D)[] ag =
        [
            (1, Two53), (1, 1), (1, -Two53),            // SQL-AGG-ORDER
            (2, 5),                                      // one value
            (3, null),                                   // all Null
            (4, null), (4, 3), (4, null),                // Null first/last around one value
            (5, 1), (5, 3),                              // two values
            (6, -Two53), (6, 1), (6, Two53),             // SQL-AGG-ORDER reversed
            (7, 1), (7, Two53), (7, -Two53),             // SQL-AGG-ORDER rotated
            (8, null), (8, 2), (8, 4),                   // SQL-AGG-FIRST Null first
            (9, 2), (9, 4), (9, null),                   // SQL-AGG-FIRST Null last
            (10, 1e9 + 4), (10, 1e9 + 7), (10, 1e9 + 13), (10, 1e9 + 16), // SQL-AGG-NUMERIC large offset
            (11, 1e15 + 4), (11, 1e15 + 7), (11, 1e15 + 13), (11, 1e15 + 16),
            (12, 1e308), (12, 1e308),                    // near overflow
        ];
        Rows(p, "AG", ["id", "g", "d"], [.. ag.Select((r, i) => new[] { A.I4(i + 1), A.I4(r.G), r.D is double v ? A.R8(v) : A.Null })]);
        // Same rows with an alternative index (SQL-AGG-FIRST "alternative indexes").
        X("SELECT * INTO AGI FROM AG");
        X("CREATE INDEX IX_AGI_gd ON AGI (g, d DESC)");

        // Saved queries: nesting (SQL-B) and mixed UNION / UNION ALL token order (SQL-UNION-03).
        SaveQuery(p, "QL", "SELECT id, name FROM L;");
        SaveQuery(p, "QL2", "SELECT * FROM QL WHERE id < 3;");
        SaveQuery(p, "QU3A", "SELECT id FROM L WHERE id = 1 UNION SELECT id FROM L WHERE id = 1 UNION ALL SELECT id FROM L WHERE id = 1;");
        SaveQuery(p, "QU3B", "SELECT id FROM L WHERE id = 1 UNION ALL SELECT id FROM L WHERE id = 1 UNION SELECT id FROM L WHERE id = 1;");
    }

    private static void SaveQuery(P p, string name, string sql) =>
        p.CallBind("qdc_" + name, "db", "CreateQueryDef", A.Str(name), A.Str(sql)).Release("qdc_" + name);

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

    // Common program parts --------------------------------------------------------------------------------------

    private static Probe Read(string name, string[] branches, string what, Action<P> body, string ext = "accdb", bool writable = false) =>
        new(Card, ext == "accdb" ? name : $"{name}-{ext}", [CardId, .. branches],
            $"{CardId} {string.Join(", ", branches)}: {what} Runs on a fresh copy of {Fixture(ext)} opened {(writable ? "read-write" : "read-only")} through ACE DAO; " +
            "each statement is opened with Database.OpenRecordset as a snapshot unless stated, Fields Name/Type/Size/SourceTable/SourceField are read before the first row, " +
            "then every row's Field values to EOF (each MoveNext captured), then Close and DBEngine.Errors.",
            p =>
            {
                p.Engine().OpenDb(readOnly: !writable).Get("db", "Version");
                body(p);
                p.Finish("db");
            })
        { Fixtures = [("main", Fixture(ext))] };

    private static P Meta(P p, string rs, bool types)
    {
        string fl = rs + "_mf", f = rs + "_mfi";
        return p.GetBind(fl, rs, "Fields").Each(fl, f, 64, b =>
        {
            b.Get(f, "Name").Get(f, "Type").Get(f, "Size").Get(f, "SourceTable").Get(f, "SourceField");
            if (types)
            {
                string props = f + "_p";
                b.GetBind(props, f, "Properties");
                foreach (string prop in TypeProps)
                {
                    string pr = props + "_" + prop;
                    b.Item(pr, props, A.Str(prop)).Get(pr, "Value").Release(pr);
                }
                b.Release(props);
            }
        }).Release(fl);
    }

    /// <summary>One statement through Database.OpenRecordset: metadata at open, rows to EOF, close, errors.</summary>
    private static P Q(P p, string rs, string sql, bool types = false, int type = Dao.OpenSnapshot, int maxRows = 40)
    {
        p.OpenRs(rs, "db", sql, type);
        Meta(p, rs, types);
        return p.ReadAll(rs, maxRows).Call(rs, "Close").Release(rs).Errors();
    }

    private static P Qs(P p, string prefix, bool types, params string[] sqls)
    {
        for (int i = 0; i < sqls.Length; i++)
        {
            Q(p, $"{prefix}{i + 1}", sqls[i], types);
        }
        return p;
    }

    private static P QdOpen(P p, string qd, string rs, bool types = false)
    {
        p.CallBind(rs, qd, "OpenRecordset", A.I4(Dao.OpenSnapshot));
        Meta(p, rs, types);
        return p.ReadAll(rs).Call(rs, "Close").Release(rs).Errors();
    }

    private static P Params(P p, string qd)
    {
        string ps = qd + "_ps", pr = qd + "_pi";
        return p.GetBind(ps, qd, "Parameters").Each(ps, pr, 16, b => b.Get(pr, "Name").Get(pr, "Type").Get(pr, "Direction")).Release(ps).Errors();
    }

    private static P SetParam(P p, string qd, string name, Arg value)
    {
        string ps = qd + "_pset", pv = qd + "_pv_" + name.Replace(' ', '_');
        return p.GetBind(ps, qd, "Parameters").Item(pv, ps, A.Str(name)).Put(pv, "Value", value).Release(pv).Release(ps);
    }

    private static P TempQd(P p, string qd, string sql) =>
        p.CallBind(qd, "db", "CreateQueryDef", A.Str(""), A.Str(sql)).Errors();

    // SQL-B: source syntax, scope and metadata --------------------------------------------------------------------

    private static IEnumerable<Probe> SyntaxScope()
    {
        yield return Read("b-output-names", ["SQL-B", "SQL-REL"],
            "What output Field names, types and SourceTable/SourceField does ACE give for star expansion over a join, two aliases of one table, a repeated column, unnamed expressions, and an empty result (WHERE 1 = 0, with BOF/EOF read at open)?",
            p =>
            {
                Qs(p, "on", false,
                    "SELECT * FROM L INNER JOIN R ON L.id = R.lid",
                    "SELECT a.id, b.id FROM L AS a INNER JOIN L AS b ON a.id = b.id",
                    "SELECT L.*, R.lid, R.lid FROM L INNER JOIN R ON L.id = R.lid",
                    "SELECT id + 1, name & 'x', id FROM L");
                p.OpenRs("empty", "db", "SELECT id, name, id + 1 AS e, name & 'x' AS f, (SELECT Max(qty) FROM R) AS g FROM L WHERE 1 = 0", Dao.OpenSnapshot)
                    .Get("empty", "BOF").Get("empty", "EOF");
                Meta(p, "empty", true).Get("empty", "RecordCount").Call("empty", "Close").Release("empty").Errors();
            });

        yield return Read("b-name-resolution", ["SQL-B"],
            "How are an ambiguous unqualified field (id in L and LD), an unknown field, an unknown qualified field and an unknown qualifier classified, and at which call does each outcome appear? " +
            "Each is opened directly; the ambiguous and the unknown field are also put in temporary QueryDefs whose Parameters are enumerated, opened without a value, and (unknown field) opened again after Parameters(\"nosuch\") = 2.",
            p =>
            {
                Qs(p, "nr", false,
                    "SELECT id FROM L, LD",
                    "SELECT nosuch FROM L",
                    "SELECT L.nosuch FROM L",
                    "SELECT x.id FROM L");
                TempQd(p, "qa", "SELECT id FROM L, LD");
                Params(p, "qa");
                QdOpen(p, "qa", "qars");
                p.Release("qa");
                TempQd(p, "qn", "SELECT nosuch FROM L");
                Params(p, "qn");
                QdOpen(p, "qn", "qnrs1");
                SetParam(p, "qn", "nosuch", A.I4(2));
                QdOpen(p, "qn", "qnrs2");
                p.Release("qn");
            });

        yield return Read("b-parameters", ["SQL-B"],
            "What Parameter names, types, directions and order does a temporary QueryDef expose for: declared pA Long and [p B] Text(255) plus an undeclared pC; a name declared twice with two types; " +
            "two undeclared names differing only in case ([p], [P]); and a declared parameter the body does not use? The first is opened after setting pA = 1, [p B] = 'b', pC = 3; the others are opened without values.",
            p =>
            {
                TempQd(p, "q1", "PARAMETERS pA Long, [p B] Text ( 255 ); SELECT id, name FROM L WHERE id = pA OR name = [p B] OR id = pC;");
                Params(p, "q1");
                SetParam(p, "q1", "pA", A.I4(1));
                SetParam(p, "q1", "p B", A.Str("b"));
                SetParam(p, "q1", "pC", A.I4(3));
                QdOpen(p, "q1", "q1rs");
                TempQd(p, "q2", "PARAMETERS a Long, a Text ( 255 ); SELECT id FROM L WHERE id = a;");
                Params(p, "q2");
                QdOpen(p, "q2", "q2rs");
                TempQd(p, "q3", "SELECT id FROM L WHERE id = [p] OR id = [P] + 1;");
                Params(p, "q3");
                QdOpen(p, "q3", "q3rs");
                TempQd(p, "q4", "PARAMETERS pA Long; SELECT id FROM L;");
                Params(p, "q4");
                QdOpen(p, "q4", "q4rs");
                p.Release("q1").Release("q2").Release("q3").Release("q4");
            });

        yield return Read("b-brackets", ["SQL-B"],
            "Which identifier forms bind for a table and fields named with a space and a reserved word: bracketed, unbracketed reserved word, an alias containing an escaped bracket ([x]]y]), backtick quoting, and a bracketed qualified name?",
            p => Qs(p, "br", false,
                "SELECT [Order], [Col Name] FROM [T Space]",
                "SELECT Order FROM [T Space]",
                "SELECT [Col Name] AS [x]]y] FROM [T Space]",
                "SELECT `Col Name` FROM `T Space`",
                "SELECT [T Space].[Col Name] FROM [T Space]"));

        yield return Read("b-saved-nesting", ["SQL-B"],
            "How do saved queries nest as sources: a saved query over a saved query (QL2 over QL), an aliased saved query with a restriction, a join of two saved queries, and a derived table over a saved query; with output names and SourceTable/SourceField of each?",
            p => Qs(p, "sv", false,
                "SELECT * FROM QL2",
                "SELECT q.id, q.name FROM QL AS q WHERE q.id > 1",
                "SELECT a.id, b.id FROM QL AS a INNER JOIN QL2 AS b ON a.id = b.id",
                "SELECT id FROM (SELECT id FROM QL WHERE id > 1) AS d"));

        yield return Read("b-in-contexts", ["SQL-B", "SQL-SUB-02"],
            "How does each IN context bind and evaluate independently: a value list containing Null, an IN subquery, PIVOT ... IN with fixed headings (one with no data), and the same crosstab with dynamic headings?",
            p => Qs(p, "in", true,
                "SELECT id FROM L WHERE id IN (1, 3, Null)",
                "SELECT id FROM L WHERE id IN (SELECT lid FROM R)",
                "TRANSFORM Sum(qty) AS s SELECT lid FROM R GROUP BY lid PIVOT rid IN (10, 12, 99)",
                "TRANSFORM Sum(qty) AS s SELECT lid FROM R GROUP BY lid PIVOT rid"));

        const string routeSql = "SELECT L.name, Count(R.rid) AS n, Sum(R.qty) AS s FROM L LEFT JOIN R ON L.id = R.lid GROUP BY L.name ORDER BY L.name;";
        yield return Read("b-routes", ["SQL-B", "SQL-REL"],
            $"Does one exact SQL text ({routeSql}) give the same metadata and rows through Database.OpenRecordset, a temporary QueryDef, and a permanent QueryDef QRoute? " +
            "For QRoute the stored SQL property is read after CreateQueryDef and again through QueryDefs(\"QRoute\") after QueryDefs.Refresh, and QueryDef.Fields (Name/Type/Size) is read before opening.",
            p =>
            {
                Q(p, "rd", routeSql, true);
                TempQd(p, "rt", routeSql);
                QdOpen(p, "rt", "rtrs", true);
                p.Release("rt");
                p.CallBind("rp", "db", "CreateQueryDef", A.Str("QRoute"), A.Str(routeSql)).Errors().Get("rp", "SQL").Get("rp", "Type")
                    .GetBind("rpf", "rp", "Fields").Each("rpf", "rpfi", 16, b => b.Get("rpfi", "Name").Get("rpfi", "Type").Get("rpfi", "Size")).Release("rpf");
                QdOpen(p, "rp", "rprs", true);
                p.Release("rp")
                    .GetBind("qdfs", "db", "QueryDefs").Call("qdfs", "Refresh").Item("rn", "qdfs", A.Str("QRoute")).Get("rn", "SQL");
                QdOpen(p, "rn", "rnrs", true);
                p.Release("rn").Release("qdfs");
            },
            writable: true);

        string[] adoSql =
        [
            "SELECT L.id, R.rid, R.lid, R.qty FROM L LEFT JOIN R ON L.id = R.lid WHERE R.qty > 0",
            "SELECT c AS v FROM N UNION SELECT x FROM ND",
            "SELECT id FROM L WHERE name LIKE 'a%'",
            "SELECT id FROM L WHERE name LIKE 'a*'",
        ];
        yield return new Probe(Card, "b-ado", [CardId, "SQL-B", "SQL-REL"],
            "R-SQL-1 SQL-B, SQL-REL: does the independently captured ADO surface (ADODB.Connection, Provider=Microsoft.ACE.OLEDB.12.0, Mode=adModeRead, Data Source set through Connection.Properties) " +
            "give the same metadata and rows as DAO for the same SQL texts (an outer join with a WHERE on the inner side, a Currency/Decimal UNION, LIKE with % and with *)? " +
            "Each text runs through Connection.Execute (Fields Name/Type/DefinedSize/Precision/NumericScale/Attributes, then rows to EOF, then Connection.Errors); after the connection closes, " +
            "a DAO engine opens the same fixture copy read-only and runs the same texts through Database.OpenRecordset.",
            p =>
            {
                p.Files("before-open").Modules("before-create").Create("ADODB.Connection", "cn").Modules("after-create")
                    .Put("cn", "Provider", A.Str("Microsoft.ACE.OLEDB.12.0"))
                    .Put("cn", "Mode", A.I4(1))
                    .GetBind("cnp", "cn", "Properties").Item("cnds", "cnp", A.Str("Data Source")).Put("cnds", "Value", A.In()).Release("cnds").Release("cnp")
                    .Call("cn", "Open").Get("cn", "Version").Get("cn", "Provider").Get("cn", "ConnectionString");
                for (int i = 0; i < adoSql.Length; i++)
                {
                    string ar = $"ar{i + 1}", af = ar + "_mf", afi = ar + "_mfi";
                    p.CallBind(ar, "cn", "Execute", A.Str(adoSql[i]))
                        .GetBind(af, ar, "Fields")
                        .Each(af, afi, 32, b => b.Get(afi, "Name").Get(afi, "Type").Get(afi, "DefinedSize").Get(afi, "Precision").Get(afi, "NumericScale").Get(afi, "Attributes"))
                        .Release(af)
                        .ReadAll(ar).Call(ar, "Close").Release(ar)
                        .GetBind("cerrs", "cn", "Errors")
                        .Each("cerrs", "ce", 16, b => b.Get("ce", "Number").Get("ce", "Description").Get("ce", "Source").Get("ce", "NativeError").Get("ce", "SQLState"))
                        .Release("cerrs");
                }
                p.Call("cn", "Close").Release("cn").Modules("after-ado-close");
                p.Create().Modules("after-dao-create").OpenDb().Get("db", "Version");
                Qs(p, "dr", true, adoSql);
                p.Finish("db");
            })
        { Fixtures = [("main", Fixture("accdb"))] };

        yield return Read("b-join-nesting", ["SQL-B"],
            "Which join nestings bind: an outer join whose result is inner-joined on the inner side's column, an outer join over a parenthesized inner join, two chained outer joins, and three tables joined without parentheses?",
            p => Qs(p, "jn", false,
                "SELECT L.id, R.rid, RX.rid FROM (L LEFT JOIN R ON L.id = R.lid) INNER JOIN RX ON R.lid = RX.lid",
                "SELECT L.id, R.rid, RX.rid FROM L LEFT JOIN (R INNER JOIN RX ON R.lid = RX.lid) ON L.id = R.lid",
                "SELECT L.id, R.rid, RX.rid FROM (L LEFT JOIN R ON L.id = R.lid) LEFT JOIN RX ON R.rid = RX.rid",
                "SELECT L.id, R.rid, RX.rid FROM L INNER JOIN R ON L.id = R.lid INNER JOIN RX ON R.lid = RX.lid"));

        yield return Read("b-join-bound-types", ["SQL-B", "SQL-KEY-02"],
            "Which bound type combinations are admitted in a join ON comparison, and with which matches: Memo = Memo, Left(Memo, 255) = Text (an admitted expression over a Memo), Single = Double, Currency = Double, Currency = Decimal, Long = Text?",
            p => Qs(p, "jt", false,
                "SELECT a.id, b.id FROM S AS a INNER JOIN S AS b ON a.m = b.m",
                "SELECT a.id, b.id FROM S AS a INNER JOIN S AS b ON Left(a.m, 255) = b.t",
                "SELECT a.id, b.id FROM N AS a INNER JOIN N AS b ON a.s = b.d",
                "SELECT a.id, b.id FROM N AS a INNER JOIN N AS b ON a.c = b.d",
                "SELECT a.id, b.id FROM N AS a INNER JOIN ND AS b ON a.c = b.x",
                "SELECT L.id, N.id FROM L INNER JOIN N ON L.id = N.t"));

        int[] lengths = [32000, 63999, 64000, 64001, 65535, 65536, 65537, 131072];
        yield return Read("b-limit-length", ["SQL-B"],
            $"Where does SQL text length (UTF-16 units: {string.Join(", ", lengths)}) stop being accepted, and at which phase? Only length varies: \"SELECT\" + spaces + \" id FROM L\". " +
            "Each text is opened directly and through a temporary QueryDef (create, then OpenRecordset).",
            p =>
            {
                foreach (int n in lengths)
                {
                    string sql = Padded(n);
                    Q(p, $"len{n}", sql);
                    TempQd(p, $"lq{n}", sql);
                    QdOpen(p, $"lq{n}", $"lqrs{n}");
                    p.Release($"lq{n}");
                }
            });

        int[] ands = [50, 98, 99, 100, 101, 200, 1000];
        int[] depths = [16, 32, 50, 63, 64, 65, 100, 128, 256];
        yield return Read("b-limit-and-nesting", ["SQL-B"],
            $"Where do two lexical dimensions stop being accepted, each varied alone: the number of ANDs in a WHERE chain of distinct conditions id <> -k ({string.Join(", ", ands)}), " +
            $"and the parenthesis nesting depth around one predicate id > 0 ({string.Join(", ", depths)})? Each text is opened directly; phase and errors are captured.",
            p =>
            {
                foreach (int n in ands)
                {
                    Q(p, $"and{n}", "SELECT id FROM L WHERE " + string.Join(" AND ", Enumerable.Range(1, n + 1).Select(k => $"id <> -{k}")));
                }
                foreach (int d in depths)
                {
                    Q(p, $"nest{d}", "SELECT id FROM L WHERE " + new string('(', d) + "id > 0" + new string(')', d));
                }
            });
    }

    private static string Padded(int length)
    {
        const string head = "SELECT", tail = " id FROM L";
        return head + new string(' ', length - head.Length - tail.Length) + tail;
    }

    // SQL-J: outer joins and predicate placement -----------------------------------------------------------------

    private static IEnumerable<Probe> Joins()
    {
        yield return Read("j-where-vs-on", ["SQL-J"],
            "With L ids 1,2,3 and R (rid,lid,qty) = (10,1,0),(11,1,5),(12,2,0): which typed rows, in which order, come from LEFT JOIN ... ON L.id = R.lid WHERE R.qty > 0, versus the Qty test moved into ON and WHERE removed? Rows are pulled one MoveNext at a time.",
            p => Qs(p, "jw", false,
                "SELECT L.id, R.rid, R.lid, R.qty FROM L LEFT JOIN R ON L.id = R.lid WHERE R.qty > 0",
                "SELECT L.id, R.rid, R.lid, R.qty FROM L LEFT JOIN R ON L.id = R.lid AND R.qty > 0"));

        yield return Read("j-null-keys-dups", ["SQL-J"],
            "How do outer and inner joins treat multiple matching right rows, duplicate left keys, Null left keys, Null right keys on the preserved side, and a WHERE that rejects some but not all matches of a left row (RX qty Null for L3)?",
            p => Qs(p, "jk", false,
                "SELECT L.id, RX.rid, RX.lid, RX.qty FROM L LEFT JOIN RX ON L.id = RX.lid",
                "SELECT LD.n, LD.id, R.rid FROM LD LEFT JOIN R ON LD.id = R.lid",
                "SELECT RX.rid, RX.lid, L.id FROM RX LEFT JOIN L ON RX.lid = L.id",
                "SELECT LD.n, RX.rid FROM LD INNER JOIN RX ON LD.id = RX.lid",
                "SELECT L.id, RX.rid, RX.qty FROM L LEFT JOIN RX ON L.id = RX.lid WHERE RX.qty > 5"));

        yield return Read("j-on-variants", ["SQL-J"],
            "Which rows, Null extensions and errors (with the call at which each error appears) come from an all-false ON residual, an ON residual that is Null, an ON residual over a Null column, " +
            "a conversion failure in ON (CLng(L.name)), the same failure in WHERE, and a WHERE that is Null?",
            p => Qs(p, "jo", false,
                "SELECT L.id, R.rid FROM L LEFT JOIN R ON L.id = R.lid AND R.qty < 0",
                "SELECT L.id, R.rid FROM L LEFT JOIN R ON L.id = R.lid AND R.qty > Null",
                "SELECT L.id, RX.rid, RX.qty FROM L LEFT JOIN RX ON L.id = RX.lid AND RX.qty > 5",
                "SELECT L.id, R.rid FROM L LEFT JOIN R ON L.id = R.lid AND CLng(L.name) > 0",
                "SELECT L.id, R.rid FROM L LEFT JOIN R ON L.id = R.lid WHERE CLng(L.name) > 0",
                "SELECT L.id, R.rid FROM L LEFT JOIN R ON L.id = R.lid WHERE R.qty > Null"));
    }

    // SQL-EQ / SQL-KEYS: equality domains and representatives ----------------------------------------------------

    private static IEnumerable<Probe> Equality()
    {
        foreach ((string ext, _) in Formats)
        {
            yield return Read("eq-distinct-text", ["SQL-EQ-01", "SQL-KEY-02", "SQL-KEY-03", "SQL-REL"],
                "Which S rows (Null, empty, trailing and leading space, case and accent variants, embedded U+0000, long common prefixes, ligature, exact duplicate) collapse together, and which representative value is returned, " +
                "under DISTINCT on Text, GROUP BY on Text with Count, DISTINCT on a Text expression (t & ''), DISTINCT and GROUP BY on Memo, and a self-join on Text equality?",
                p => Qs(p, "et", true,
                    "SELECT DISTINCT t FROM S",
                    "SELECT t, Count(*) AS n FROM S GROUP BY t",
                    "SELECT DISTINCT t & '' AS te FROM S",
                    "SELECT DISTINCT m FROM S",
                    "SELECT m, Count(*) AS n FROM S GROUP BY m",
                    "SELECT a.id, b.id FROM S AS a INNER JOIN S AS b ON a.t = b.t"),
                ext);
        }

        yield return Read("eq-numeric-domains", ["SQL-EQ-02", "SQL-KEY-01", "SQL-KEY-02"],
            "Within one declared type, which values group together (signed zero, equal duplicates, 0.1, maxima) and with which output type: GROUP BY d, DISTINCT s, GROUP BY c, GROUP BY x (Decimal), GROUP BY dt, GROUP BY v (BIGINT); " +
            "and what does GROUP BY return over an all-Null column (NS) and an empty table (E)?",
            p => Qs(p, "en", true,
                "SELECT d, Count(*) AS n FROM N GROUP BY d",
                "SELECT DISTINCT s FROM N",
                "SELECT c, Count(*) AS n FROM N GROUP BY c",
                "SELECT x, Count(*) AS n FROM ND GROUP BY x",
                "SELECT dt, Count(*) AS n FROM N GROUP BY dt",
                "SELECT v, Count(*) AS n FROM NB GROUP BY v",
                "SELECT v, Count(*) AS n FROM NS GROUP BY v",
                "SELECT d, Count(*) AS n FROM E GROUP BY d"));

        yield return Read("eq-cross-type", ["SQL-EQ-03", "SQL-KEY-01"],
            "When equal numbers arrive from different declared types through UNION ALL, which values group together, which representative value and VARIANT type is returned, and does reversing arm order change either? " +
            "Pairs: Integer/Double/Currency, Currency/Decimal, Date/Double, BIGINT/Double, each in both arm orders.",
            p => Qs(p, "ex", true,
                "SELECT v, Count(*) AS n FROM (SELECT i AS v FROM N UNION ALL SELECT d FROM N UNION ALL SELECT c FROM N) GROUP BY v",
                "SELECT v, Count(*) AS n FROM (SELECT c AS v FROM N UNION ALL SELECT d FROM N UNION ALL SELECT i FROM N) GROUP BY v",
                "SELECT v, Count(*) AS n FROM (SELECT c AS v FROM N UNION ALL SELECT x FROM ND) GROUP BY v",
                "SELECT v, Count(*) AS n FROM (SELECT x AS v FROM ND UNION ALL SELECT c FROM N) GROUP BY v",
                "SELECT DISTINCT v FROM (SELECT dt AS v FROM N UNION ALL SELECT d FROM N)",
                "SELECT DISTINCT v FROM (SELECT d AS v FROM N UNION ALL SELECT dt FROM N)",
                "SELECT v, Count(*) AS n FROM (SELECT v FROM NB UNION ALL SELECT d FROM N) GROUP BY v",
                "SELECT v, Count(*) AS n FROM (SELECT d AS v FROM N UNION ALL SELECT v FROM NB) GROUP BY v"));

        yield return Read("eq-distinct-error", ["SQL-EQ-04", "SQL-KEY-05"],
            "When a DISTINCT or GROUP BY key has a conversion failure (CLng over TX, where id 3 is 'x' with equal values before and after it), at which call does the error appear and are any rows delivered before it? Val(t) is the non-failing control.",
            p => Qs(p, "ee", false,
                "SELECT DISTINCT CLng(t) AS v FROM TX",
                "SELECT CLng(t) AS v, Count(*) AS n FROM TX GROUP BY CLng(t)",
                "SELECT DISTINCT Val(t) AS v FROM TX"));
    }

    // SQL-AGG ------------------------------------------------------------------------------------------------------

    private static IEnumerable<Probe> Aggregates()
    {
        const string all8 = "Count(*) AS c, Count(d) AS cd, Sum(d) AS s, Avg(d) AS a, Min(d) AS mn, Max(d) AS mx, First(d) AS f, Last(d) AS l";
        yield return Read("agg-empty", ["SQL-AGG-EMPTY"],
            "What rows, values and Field types do Count/Sum/Avg/Min/Max/First/Last return over an empty table without and with GROUP BY, with HAVING over the empty implicit group, under WHERE 1 = 0, over an all-Null group, " +
            "over one numeric value among Nulls, and grouped with HAVING Sum(d) IS NULL?",
            p => Qs(p, "ae", true,
                $"SELECT {all8} FROM E",
                $"SELECT g, {all8} FROM E GROUP BY g",
                "SELECT Count(*) AS c, Sum(d) AS s FROM E HAVING Count(*) = 0",
                $"SELECT {all8} FROM AG WHERE 1 = 0",
                $"SELECT {all8} FROM AG WHERE g = 3",
                $"SELECT {all8} FROM AG WHERE g = 4",
                "SELECT g, Count(*) AS c, Sum(d) AS s FROM AG WHERE g IN (3, 4) GROUP BY g HAVING Sum(d) IS NULL"));

        string[] numCols = ["b", "i", "l", "s", "d", "c", "dt"];
        string Over(string agg, IEnumerable<string> cols) => string.Join(", ", cols.Select(c => $"{agg}({c}) AS {agg}_{c}"));
        yield return Read("agg-result-type", ["SQL-AGG-TYPE"],
            "Per aggregate, what result Field.Type/Size/Precision/Scale and value VARTYPE does ACE give over each stored type (Byte, Integer, Long, Single, Double, Currency, Date, Text for Min/Max, Decimal): " +
            "Sum, Avg, Min, Max each in its own statement over rows without the maxima row; then Sum over all rows, one column per statement, to find each overflow and its phase.",
            p =>
            {
                Qs(p, "at", true,
                    $"SELECT {Over("Sum", numCols)} FROM N WHERE id <> 5",
                    "SELECT Sum(x) AS Sum_x FROM ND WHERE id <> 5",
                    $"SELECT {Over("Avg", numCols)} FROM N WHERE id <> 5",
                    "SELECT Avg(x) AS Avg_x FROM ND WHERE id <> 5",
                    $"SELECT {Over("Min", [.. numCols, "t"])} FROM N",
                    $"SELECT {Over("Max", [.. numCols, "t"])} FROM N",
                    "SELECT Min(x) AS Min_x, Max(x) AS Max_x FROM ND");
                foreach (string c in numCols)
                {
                    Q(p, $"ao_{c}", $"SELECT Sum({c}) AS v FROM N", true);
                }
                Q(p, "ao_x", "SELECT Sum(x) AS v FROM ND", true);
            });

        yield return Read("agg-arg-kinds", ["SQL-AGG-TYPE"],
            "What result type and value come from Sum over literal arguments (1, 2.5, '3', True), calculated arguments (i * 2, CCur(d), CDec(d)), and typed parameters (PARAMETERS pc Currency, pd Double, pl Long, set to 1.25, 0.1, 2 on a temporary QueryDef)?",
            p =>
            {
                Qs(p, "ak", true,
                    "SELECT Sum(1) AS s1, Sum(2.5) AS s2, Sum('3') AS s3, Sum(True) AS s4 FROM N",
                    "SELECT Sum(i * 2) AS s1, Sum(CCur(d)) AS s2 FROM N WHERE id <> 5",
                    "SELECT Sum(CDec(d)) AS s3 FROM N WHERE id <> 5");
                TempQd(p, "akq", "PARAMETERS pc Currency, pd Double, pl Long; SELECT Sum(pc) AS sc, Sum(pd) AS sd, Sum(pl) AS sl FROM N;");
                Params(p, "akq");
                SetParam(p, "akq", "pc", A.Cy(1.25m));
                SetParam(p, "akq", "pd", A.R8(0.1));
                SetParam(p, "akq", "pl", A.I4(2));
                QdOpen(p, "akq", "akqrs", true);
                p.Release("akq");
            });

        yield return Read("agg-count", ["SQL-AGG-COUNT"],
            "What do Count(*), Count(field) over Null-bearing columns, Count of the literal 'A & B', Count of the actual concatenation name & name, Count(Null), Count of an erroring expression (CLng over TX), " +
            "Count(*) over a four-table cross join, and Count(DISTINCT t) return or raise, with result types?",
            p => Qs(p, "ac", true,
                "SELECT Count(*) AS c, Count(d) AS cd, Count(s) AS cs, Count(t) AS ct FROM N",
                "SELECT Count('A & B') AS c1, Count(name & name) AS c2, Count(Null) AS c3 FROM L",
                "SELECT Count(CLng(t)) AS c FROM TX",
                "SELECT Count(*) AS c FROM L, R, RX, LD",
                "SELECT Count(DISTINCT t) AS c FROM S"));

        yield return Read("agg-order", ["SQL-AGG-ORDER"],
            "What exact Double bits do Sum and Avg give for [2^53, 1, -2^53] in three stored orders (groups 1, 6, 7), for all nine values together, and for group 1 fed through a derived table ordered by id DESC versus ASC?",
            p => Qs(p, "ao", true,
                "SELECT g, Sum(d) AS s, Avg(d) AS a FROM AG WHERE g IN (1, 6, 7) GROUP BY g",
                "SELECT Sum(d) AS s, Avg(d) AS a FROM AG WHERE g IN (1, 6, 7)",
                "SELECT Sum(d) AS s FROM (SELECT d FROM AG WHERE g = 1 ORDER BY id DESC)",
                "SELECT Sum(d) AS s FROM (SELECT d FROM AG WHERE g = 1 ORDER BY id)"));

        yield return Read("agg-first-last", ["SQL-AGG-FIRST"],
            "Which values do First and Last select (beside Min and Max) for groups with Null first, middle and last (4, 8, 9), with a final ORDER BY g DESC, over a nested derived table ordered by id DESC, over the whole table, " +
            "and over AGI (same rows with an index on (g, d DESC))?",
            p => Qs(p, "af", false,
                "SELECT g, First(d) AS f, Last(d) AS l, Min(d) AS mn, Max(d) AS mx FROM AG WHERE g IN (4, 8, 9) GROUP BY g",
                "SELECT g, First(d) AS f, Last(d) AS l FROM AG WHERE g IN (4, 8, 9) GROUP BY g ORDER BY g DESC",
                "SELECT First(d) AS f, Last(d) AS l FROM (SELECT d FROM AG WHERE g = 9 ORDER BY id DESC)",
                "SELECT First(id) AS fi, Last(id) AS li, First(d) AS f, Last(d) AS l FROM AG",
                "SELECT g, First(d) AS f, Last(d) AS l FROM AGI WHERE g IN (4, 8, 9) GROUP BY g",
                "SELECT First(d) AS f, Last(d) AS l FROM AGI WHERE g IN (4, 8, 9)"));

        yield return Read("agg-single", ["SQL-AGG-SINGLE"],
            "What do Var, VarP, StDev and StDevP return over zero values (empty table, all-Null group 3), one value (group 2), one value among Nulls (group 4) and two values (group 5)?",
            p => Qs(p, "as", true,
                "SELECT g, Count(d) AS n, Var(d) AS v, VarP(d) AS vp, StDev(d) AS sd, StDevP(d) AS sdp FROM AG WHERE g IN (2, 3, 4, 5) GROUP BY g",
                "SELECT Count(d) AS n, Var(d) AS v, VarP(d) AS vp, StDev(d) AS sd, StDevP(d) AS sdp FROM E"));

        yield return Read("agg-numeric", ["SQL-AGG-NUMERIC"],
            "What exact Double bits do Avg, Var, VarP, StDev, StDevP and Sum give for nearly constant values at a large offset (1e9 and 1e15 plus 4, 7, 13, 16) and for two values of 1e308?",
            p => Qs(p, "an", true,
                "SELECT g, Sum(d) AS s, Avg(d) AS a, Var(d) AS v, VarP(d) AS vp, StDev(d) AS sd, StDevP(d) AS sdp FROM AG WHERE g IN (10, 11, 12) GROUP BY g"));

        yield return Read("agg-occur", ["SQL-AGG-OCCUR"],
            "When the same aggregate appears in SELECT, HAVING and ORDER BY, what rows and order result; and with an erroring argument (Sum(CLng(t)) over TX) in all three places, or only in HAVING, at which call does the error appear?",
            p => Qs(p, "ag", false,
                "SELECT g, Sum(d) AS s FROM AG WHERE g IN (2, 5, 8) GROUP BY g HAVING Sum(d) > 4 ORDER BY Sum(d) DESC",
                "SELECT t, Sum(CLng(t)) AS s FROM TX GROUP BY t HAVING Sum(CLng(t)) > 0 ORDER BY Sum(CLng(t))",
                "SELECT t FROM TX GROUP BY t HAVING Sum(CLng(t)) > 0"));
    }

    // SQL-UNION ----------------------------------------------------------------------------------------------------

    private static IEnumerable<Probe> Unions()
    {
        yield return Read("union-binding", ["SQL-UNION-01"],
            "How do UNION arms bind and name output: alias changes between arms, different arity, a first-arm Null literal, an empty first arm (WHERE 1 = 0) and an empty-table first arm, a TABLE arm, and saved-query arms (QL, QL2)?",
            p => Qs(p, "ub", true,
                "SELECT id AS a, name AS b FROM L UNION SELECT rid AS c, qty AS d FROM R",
                "SELECT id FROM L UNION SELECT rid, lid FROM R",
                "SELECT Null AS v FROM L UNION SELECT id FROM L",
                "SELECT id AS v FROM L WHERE 1 = 0 UNION SELECT name FROM L",
                "SELECT d AS v FROM E UNION SELECT name FROM L",
                "TABLE L UNION SELECT rid, 'r' FROM R",
                "SELECT * FROM QL UNION SELECT rid, 'r' FROM R",
                "SELECT id, name FROM L UNION SELECT * FROM QL2"));

        foreach ((string ext, _) in Formats)
        {
            yield return Read("union-types", ["SQL-UNION-02", "SQL-REL"],
                "What common output Field type/size/precision and value types does UNION give for Currency/Decimal (both orders), Long/Text, Date/Double, Text(20)/Text(60), Text/Memo under UNION and UNION ALL, and a Null literal arm before and after a Currency arm?",
                p => Qs(p, "ut", true,
                    "SELECT c AS v FROM N UNION SELECT x FROM ND",
                    "SELECT x AS v FROM ND UNION SELECT c FROM N",
                    "SELECT l AS v FROM N UNION SELECT t FROM N",
                    "SELECT dt AS v FROM N UNION SELECT d FROM N",
                    "SELECT name AS v FROM L UNION SELECT t FROM S",
                    "SELECT t AS v FROM S UNION SELECT m FROM S",
                    "SELECT t AS v FROM S UNION ALL SELECT m FROM S",
                    "SELECT Null AS v FROM L UNION SELECT c FROM N",
                    "SELECT c AS v FROM N UNION SELECT Null FROM L"),
                ext);
        }

        yield return Read("union-mixed-tokens", ["SQL-UNION-03"],
            "With three arms of equal value, what multiplicity results from UNION then UNION ALL, and from UNION ALL then UNION, run directly; and for the saved QU3A/QU3B, what SQL property text is stored and what rows does opening them give?",
            p =>
            {
                Qs(p, "um", false,
                    "SELECT id FROM L WHERE id = 1 UNION SELECT id FROM L WHERE id = 1 UNION ALL SELECT id FROM L WHERE id = 1",
                    "SELECT id FROM L WHERE id = 1 UNION ALL SELECT id FROM L WHERE id = 1 UNION SELECT id FROM L WHERE id = 1");
                p.GetBind("uqdfs", "db", "QueryDefs");
                foreach (string q in new[] { "QU3A", "QU3B" })
                {
                    p.Item(q, "uqdfs", A.Str(q)).Get(q, "SQL").Get(q, "Type");
                    QdOpen(p, q, q + "rs");
                    p.Release(q);
                }
                p.Release("uqdfs");
            });

        yield return Read("union-error-order", ["SQL-UNION-04"],
            "When a later or earlier UNION arm has a conversion failure (CLng over TX), are rows delivered before the error and at which call does it appear, with and without a final ORDER BY; and what do TOP inside arms with a final ORDER BY, and TOP over a UNION ALL with duplicates, return?",
            p => Qs(p, "ue", false,
                "SELECT id AS v FROM L UNION SELECT CLng(t) FROM TX",
                "SELECT CLng(t) AS v FROM TX UNION ALL SELECT id FROM L",
                "SELECT id AS v FROM L UNION ALL SELECT CLng(t) FROM TX",
                "SELECT id AS v FROM L UNION SELECT CLng(t) FROM TX ORDER BY v",
                "SELECT TOP 2 id AS v FROM L UNION SELECT TOP 2 rid FROM R ORDER BY v DESC",
                "SELECT TOP 1 v FROM (SELECT id AS v FROM L UNION ALL SELECT id FROM L)"));
    }

    // SQL-SUB ------------------------------------------------------------------------------------------------------

    private static IEnumerable<Probe> Subqueries()
    {
        yield return Read("sub-scalar", ["SQL-SUB-01"],
            "What does a scalar subquery in the select list give, or raise and at which call, when the child returns one row, zero rows, two rows, two columns, a global aggregate over no rows, and a correlated child that returns two rows only for L.id = 1?",
            p => Qs(p, "ss", true,
                "SELECT id, (SELECT qty FROM R WHERE rid = 11) AS s FROM L",
                "SELECT id, (SELECT qty FROM R WHERE rid = 99) AS s FROM L",
                "SELECT id, (SELECT qty FROM R WHERE lid = 1) AS s FROM L",
                "SELECT id, (SELECT rid, qty FROM R WHERE rid = 11) AS s FROM L",
                "SELECT id, (SELECT Max(qty) FROM R WHERE rid = 99) AS s FROM L",
                "SELECT id, (SELECT qty FROM R WHERE R.lid = L.id) AS s FROM L"));

        yield return Read("sub-in-quantified", ["SQL-SUB-02"],
            "What truth value (projected as a column over LD, whose id is 1, 1, Null) do IN, NOT IN, = ANY, = SOME, > ALL, >= ALL and < ALL give over an empty set (E), an all-Null set (NS), a matching set (R.lid) and a mixed Null set (RX.lid)? " +
            "One NOT IN case is repeated in WHERE in case the projected form is refused.",
            p => Qs(p, "si", false,
                "SELECT n, id, (id IN (SELECT g FROM E)) AS x FROM LD",
                "SELECT n, id, (id IN (SELECT v FROM NS)) AS x FROM LD",
                "SELECT n, id, (id IN (SELECT lid FROM R)) AS x FROM LD",
                "SELECT n, id, (id IN (SELECT lid FROM RX)) AS x FROM LD",
                "SELECT n, id, (id NOT IN (SELECT g FROM E)) AS x FROM LD",
                "SELECT n, id, (id NOT IN (SELECT v FROM NS)) AS x FROM LD",
                "SELECT n, id, (id NOT IN (SELECT lid FROM RX)) AS x FROM LD",
                "SELECT n, id, (id = ANY (SELECT lid FROM RX)) AS x FROM LD",
                "SELECT n, id, (id = SOME (SELECT v FROM NS)) AS x FROM LD",
                "SELECT n, id, (id > ALL (SELECT g FROM E)) AS x FROM LD",
                "SELECT n, id, (id >= ALL (SELECT lid FROM RX)) AS x FROM LD",
                "SELECT n, id, (id < ALL (SELECT v FROM NS)) AS x FROM LD",
                "SELECT n FROM LD WHERE id NOT IN (SELECT lid FROM RX)"));

        yield return Read("sub-correlated", ["SQL-SUB-03"],
            "How are correlated names bound: an inner alias L that shadows the outer table L, an outer alias L2 with the child naming L, nested EXISTS both correlated to L, several outer LD rows with equal id, an unqualified inner name found only in the outer table, " +
            "and a declared parameter named like an inner column (PARAMETERS lid Long, value 1, on a temporary QueryDef with its Parameters enumerated)?",
            p =>
            {
                Qs(p, "sc", false,
                    "SELECT L.id, (SELECT Count(*) FROM R AS L WHERE L.lid = 1) AS c FROM L",
                    "SELECT L2.id, (SELECT Count(*) FROM R WHERE R.lid = L.id) AS c FROM L AS L2",
                    "SELECT id FROM L WHERE EXISTS (SELECT * FROM R WHERE R.lid = L.id AND EXISTS (SELECT * FROM RX WHERE RX.lid = L.id))",
                    "SELECT n, id, (SELECT Count(*) FROM R WHERE R.lid = LD.id) AS c FROM LD",
                    "SELECT n, (SELECT Count(*) FROM R WHERE lid = n) AS c FROM LD");
                TempQd(p, "scq", "PARAMETERS lid Long; SELECT id, (SELECT Count(*) FROM R WHERE R.lid = lid) AS c FROM L;");
                Params(p, "scq");
                SetParam(p, "scq", "lid", A.I4(1));
                QdOpen(p, "scq", "scqrs");
                p.Release("scq");
            });

        yield return Read("sub-exists-demand", ["SQL-SUB-04", "SQL-SUB-05"],
            "Does EXISTS demand an unused failing projection, a failing membership predicate whose decisive match comes before the failing row (CLng(t) = 1) or after it (CLng(t) = 2), and do IN memberships with a decisive value before or after the failure raise? " +
            "Also EXISTS over TOP, DISTINCT, GROUP BY/HAVING and a nested NOT EXISTS.",
            p => Qs(p, "se", false,
                "SELECT id FROM L WHERE EXISTS (SELECT CLng(t) FROM TX)",
                "SELECT id FROM L WHERE EXISTS (SELECT * FROM TX WHERE CLng(t) = 1)",
                "SELECT id FROM L WHERE EXISTS (SELECT * FROM TX WHERE CLng(t) = 2)",
                "SELECT id FROM L WHERE 1 IN (SELECT CLng(t) FROM TX)",
                "SELECT id FROM L WHERE 2 IN (SELECT CLng(t) FROM TX)",
                "SELECT id FROM L WHERE EXISTS (SELECT TOP 1 * FROM TX)",
                "SELECT id FROM L WHERE EXISTS (SELECT DISTINCT t FROM TX)",
                "SELECT id FROM L WHERE EXISTS (SELECT t FROM TX GROUP BY t HAVING Count(*) > 2)",
                "SELECT id FROM L WHERE NOT EXISTS (SELECT * FROM R WHERE R.lid = L.id AND NOT EXISTS (SELECT * FROM RX WHERE RX.lid = R.lid))"));
    }

    // ORDER BY and SQL-DEMAND ---------------------------------------------------------------------------------------

    private static IEnumerable<Probe> OrderAndDemand()
    {
        foreach ((string ext, _) in Formats)
        {
            yield return Read("order-nulls-text", ["SQL-B", "SQL-REL"],
                "What order, Null placement and tie order does ORDER BY give over the S text rows ascending, descending, with a secondary id DESC, over Memo, by Len(t), by output position, " +
                "when an alias (id AS t) collides with a source field name, and over the N Double column (signed zero, Null)?",
                p => Qs(p, "ob", false,
                    "SELECT id, t FROM S ORDER BY t",
                    "SELECT id, t FROM S ORDER BY t DESC",
                    "SELECT id, t FROM S ORDER BY t, id DESC",
                    "SELECT id, m FROM S ORDER BY m",
                    "SELECT id, t FROM S ORDER BY Len(t), id",
                    "SELECT id, t FROM S ORDER BY 2",
                    "SELECT id AS t, t AS x FROM S ORDER BY t",
                    "SELECT id, d FROM N ORDER BY d"),
                ext);
        }

        const string conv = "SELECT id, CLng(t) AS v FROM TX";
        yield return Read("demand-conversion", ["SQL-DEMAND"],
            $"At which public boundary does a projected conversion failure (TX id 3) appear: for '{conv}' as a snapshot, BOF/EOF/RecordCount are read at open, then rows to EOF, then RecordCount; " +
            "the same text with only MoveLast/RecordCount/MoveFirst and no field reads; as a dynaset; as forward-only. Then the failure in WHERE, a failing row excluded by WHERE, and the failure only in a GROUP BY's ORDER BY key, with EOF read at open.",
            p =>
            {
                p.OpenRs("dm1", "db", conv, Dao.OpenSnapshot).Get("dm1", "BOF").Get("dm1", "EOF").Get("dm1", "RecordCount");
                Meta(p, "dm1", false).ReadAll("dm1").Get("dm1", "RecordCount").Call("dm1", "Close").Release("dm1").Errors();
                p.OpenRs("dm2", "db", conv, Dao.OpenSnapshot).Call("dm2", "MoveLast").Get("dm2", "RecordCount").Call("dm2", "MoveFirst")
                    .Get("dm2", "EOF").Call("dm2", "Close").Release("dm2").Errors();
                Q(p, "dm3", conv, type: Dao.OpenDynaset);
                Q(p, "dm4", conv, type: Dao.OpenForwardOnly);
                Q(p, "dm5", "SELECT id FROM TX WHERE CLng(t) > 0");
                Q(p, "dm6", "SELECT id, CLng(t) AS v FROM TX WHERE id <> 3");
                p.OpenRs("dm7", "db", "SELECT t, Count(*) AS n FROM TX GROUP BY t ORDER BY CLng(t)", Dao.OpenSnapshot).Get("dm7", "EOF");
                Meta(p, "dm7", false).ReadAll("dm7").Call("dm7", "Close").Release("dm7").Errors();
            });
    }
}
