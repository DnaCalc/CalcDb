namespace CalcDb.Probes.Cards;

/// <summary>
/// Repairs of probes of R-EXPR-1, R-SQL-1, R-SQL-2, R-SQL-3 and R-PAR-1 whose records could not answer their own question
/// because of a defect in OUR program, not an ACE answer. Each repair asks the same question as the original and removes the
/// defect: a Decimal column made through ADO DDL and written as SQL literals, a read-back, an alias that cannot collide, a
/// statement with the arguments it needs, one fault per fresh object, a refreshed collection, a reseeded table.
/// <para>
/// Attribution: DBEngine.Errors keeps the last failure until the next one, so every statement is preceded by a marker (a
/// DROP TABLE of a table that does not exist, which always fails); Errors read after the statement then shows the statement's
/// own failure or the marker's, never an older one. The marker is a call of ours, not an ACE answer.
/// </para>
/// <para>
/// No call can open a dialog: every database is a fixture copy or an A.Out file; no ODBC, no DSN, no empty name. Requery is
/// only called without an argument (evidence/r-crash-requery) and no statement selects Array(...).
/// </para>
/// </summary>
public sealed class RFixExprSql : ICard
{
    private const string Card = "r-fix-expr-sql";
    private const string FxExpr = Card + "-expr";
    private const string FxSql1 = Card + "-sql1";
    private const string FxPar = Card + "-par";
    private const string Ace = "Microsoft.ACE.OLEDB.12.0";
    private const int Fail = Dao.FailOnError;
    private const string MarkerTable = "RfixNoSuchMarker";

    /// <summary>U+00E4, built from its code so that no non-ASCII character is in the source.</summary>
    private static readonly string AUmlaut = ((char)0xE4).ToString();

    private const string MarkerTail =
        " Each statement is preceded by a marker (a DROP TABLE of a table that does not exist, which always fails), so DBEngine.Errors read after the statement shows that statement's own failure or the marker's, never an older one. " +
        "Where Field Precision and Scale are read, the recordset is first positioned on its first row (MoveFirst) and DecimalPlaces is not read.";

    /// <summary>Unique binder names within one probe program.</summary>
    private sealed class Names
    {
        private int next;

        public string Next(string prefix) => prefix + next++;
    }

    public IEnumerable<Probe> Probes()
    {
        yield return CreateExpr();
        yield return CreateSql1();
        yield return CreatePar();
        foreach (Probe pr in ExprRepairs())
        {
            yield return pr;
        }
        foreach (Probe pr in ActionRepairs())
        {
            yield return pr;
        }
        foreach (Probe pr in SelectRepairs())
        {
            yield return pr;
        }
        foreach (Probe pr in LimitAndParameterRepairs())
        {
            yield return pr;
        }
        foreach (Probe pr in ParRepairs())
        {
            yield return pr;
        }
    }

    // Probe factories ------------------------------------------------------------------------------------------------

    /// <summary>
    /// A repair: the description names the repaired probe and its defect, then what is asked. With <paramref name="open"/>
    /// the standard opening (engine, OpenDatabase of the fixture, Version) and closing run around the body; without it the
    /// body opens and closes everything itself.
    /// </summary>
    private static Probe Rep(string name, string source, string[] refs, string defect, string asks, Action<P, Names> body,
        string fixture = FxExpr, bool writable = false, bool open = true, string[]? close = null) =>
        new(Card, name, refs,
            $"Repairs {source}: {defect}. {asks} Fixture {fixture} is opened {(writable ? "on a writable fresh copy" : "read-only")} through DAO.DBEngine.120; each recordset reads Name, Type and Value of every field.{MarkerTail}",
            p =>
            {
                var n = new Names();
                if (open)
                {
                    p.EngineLite().Get("eng", "Version").OpenDb(readOnly: !writable).Get("db", "Version");
                }
                body(p, n);
                if (open)
                {
                    p.FinishLite(close ?? ["db"]);
                }
            })
        { Fixtures = [("main", fixture)] };

    private static Probe Fixture(string name, string[] refs, string what, Action<P, Names, string> body)
    {
        string file = $"{Card}-{name}.accdb";
        return new Probe(Card, "create-" + name, refs,
            $"R-FIX-EXPR-SQL fixture {Card}-{name}: {what} Created in ACCDB by DBEngine.CreateDatabase(path, dbLangGeneral, dbVersion120); Decimal columns are made through ADO DDL with explicit precision and scale and written as SQL literals, other typed values through table-type AddNew/Value/Update; the closed file is kept.",
            p =>
            {
                var n = new Names();
                p.EngineLite().Get("eng", "Version")
                    .CallBind("db", "eng", "CreateDatabase", A.Out(file), A.Str(Dao.LangGeneral), A.I4(Dao.Version120)).Get("db", "Version");
                body(p, n, file);
                p.FinishLite("db");
            })
        { CreatesFixture = true };
    }

    // Program helpers ------------------------------------------------------------------------------------------------

    /// <summary>The attribution marker: a call that always fails with its own error.</summary>
    private static void Mark(P p, string db = "db") => p.Call(db, "Execute", A.Str("DROP TABLE " + MarkerTable));

    /// <summary>Setup statement through Database.Execute with dbFailOnError; no attribution reads.</summary>
    private static void Run(P p, string sql, string db = "db") => p.Call(db, "Execute", A.Str(sql), A.I4(Fail));

    /// <summary>The statement under test: marker, Execute with dbFailOnError, RecordsAffected, then DBEngine.Errors.</summary>
    private static void Act(P p, string sql, string db = "db")
    {
        Mark(p, db);
        p.Call(db, "Execute", A.Str(sql), A.I4(Fail)).Get(db, "RecordsAffected").Errors();
    }

    /// <summary>Every row to EOF (at most <paramref name="maxRows"/>): Name, Type and Value of each field.</summary>
    private static void Rows(P p, string rs, int maxRows)
    {
        string fl = rs + "f", f = rs + "x";
        p.Until(rs, "EOF", maxRows, b => b.GetBind(fl, rs, "Fields")
            .Each(fl, f, 64, x => x.Get(f, "Name").Get(f, "Type").Get(f, "Value"))
            .Release(fl).Call(rs, "MoveNext"));
    }

    /// <summary>Name, Type and Value of each field of the current record.</summary>
    private static void Cur(P p, Names n, string rs)
    {
        string fl = n.Next("cur"), f = fl + "x";
        p.GetBind(fl, rs, "Fields").Each(fl, f, 64, x => x.Get(f, "Name").Get(f, "Type").Get(f, "Value")).Release(fl);
    }

    /// <summary>One statement: marker, open, Errors, rows to EOF, close, Errors.</summary>
    private static void Q(P p, Names n, string sql, int type = Dao.OpenSnapshot, int maxRows = 16, string db = "db")
    {
        string rs = n.Next("rs");
        Mark(p, db);
        p.OpenRs(rs, db, sql, type).Errors();
        Rows(p, rs, maxRows);
        p.Call(rs, "Close").Release(rs).Errors();
    }

    private static void Qs(P p, Names n, IEnumerable<string> sqls, int maxRows = 16)
    {
        foreach (string sql in sqls)
        {
            Q(p, n, sql, maxRows: maxRows);
        }
    }

    /// <summary>Each expression in its own snapshot <c>SELECT expr AS v FROM One</c>.</summary>
    private static void Sc(P p, Names n, params string[] exprs)
    {
        foreach (string e in exprs)
        {
            Q(p, n, $"SELECT {e} AS v FROM One");
        }
    }

    /// <summary>One statement read without attribution (fixture read-backs).</summary>
    private static void Peek(P p, Names n, string sql)
    {
        string rs = n.Next("pk");
        p.OpenRs(rs, "db", sql, Dao.OpenSnapshot);
        Rows(p, rs, 16);
        p.Call(rs, "Close").Release(rs);
    }

    /// <summary>
    /// One statement: marker, open, Errors, MoveFirst (an ungrouped aggregate result loses its type properties before it is
    /// positioned on its row), Fields Name/Type/Size/Precision/Scale, rows, close, Errors. DecimalPlaces is never read.
    /// </summary>
    private static void Qm(P p, Names n, string sql, int type = Dao.OpenSnapshot, int maxRows = 16)
    {
        string rs = n.Next("rs"), fl = rs + "m", f = rs + "mx", props = rs + "mp";
        Mark(p);
        p.OpenRs(rs, "db", sql, type).Errors().Call(rs, "MoveFirst").Errors();
        p.GetBind(fl, rs, "Fields").Each(fl, f, 16, b =>
        {
            b.Get(f, "Name").Get(f, "Type").Get(f, "Size").GetBind(props, f, "Properties");
            foreach (string prop in new[] { "Precision", "Scale" })
            {
                string pr = props + prop;
                b.Item(pr, props, A.Str(prop)).Get(pr, "Value").Release(pr);
            }
            b.Release(props);
        }).Release(fl);
        Rows(p, rs, maxRows);
        p.Call(rs, "Close").Release(rs).Errors();
    }

    /// <summary>Table-type AddNew/Value/Update per row.</summary>
    private static void AddRows(P p, Names n, string table, string[] cols, IEnumerable<Arg[]> rows)
    {
        string rs = n.Next("add");
        p.OpenRs(rs, "db", table, Dao.OpenTable).GetBind(rs + "f", rs, "Fields");
        for (int i = 0; i < cols.Length; i++)
        {
            p.Item($"{rs}c{i}", rs + "f", A.Str(cols[i]));
        }
        foreach (Arg[] row in rows)
        {
            p.Call(rs, "AddNew");
            for (int i = 0; i < row.Length; i++)
            {
                p.Put($"{rs}c{i}", "Value", row[i]);
            }
            p.Call(rs, "Update");
        }
        for (int i = 0; i < cols.Length; i++)
        {
            p.Release($"{rs}c{i}");
        }
        p.Release(rs + "f").Call(rs, "Close").Release(rs);
    }

    /// <summary>Set AllowZeroLength True on a Text field, after a TableDefs.Refresh, and read it back.</summary>
    private static void AllowZeroLength(P p, string table, string field)
    {
        p.GetBind("azt", "db", "TableDefs").Call("azt", "Refresh").Item("azd", "azt", A.Str(table)).GetBind("azf", "azd", "Fields")
            .Item("azx", "azf", A.Str(field)).Put("azx", "AllowZeroLength", A.Bool(true)).Get("azx", "AllowZeroLength")
            .Release("azx").Release("azf").Release("azd").Release("azt");
    }

    /// <summary>
    /// Close the DAO database, run the statements through ADO (ACE OLE DB provider, explicit precision and scale), close ADO and
    /// reopen the DAO database read-write on the same file.
    /// </summary>
    private static void AdoDdl(P p, string file, params string[] statements)
    {
        p.Call("db", "Close").Release("db");
        p.Create("ADODB.Connection", "cn").Put("cn", "Provider", A.Str(Ace))
            .GetBind("cnp", "cn", "Properties").Item("cds", "cnp", A.Str("Data Source")).Put("cds", "Value", A.Out(file))
            .Release("cds").Release("cnp").Call("cn", "Open");
        foreach (string sql in statements)
        {
            p.Call("cn", "Execute", A.Str(sql));
        }
        p.Call("cn", "Close").Release("cn");
        p.CallBind("db", "eng", "OpenDatabase", A.Out(file), A.Bool(false), A.Bool(false));
    }

    /// <summary>A temporary QueryDef; its Parameters (Name, Type) are read and kept bound as <c>qd + "p"</c>.</summary>
    private static string QDef(P p, Names n, string sql, string name = "")
    {
        string qd = n.Next("qd"), ps = qd + "p", px = qd + "x";
        Mark(p);
        p.CallBind(qd, "db", "CreateQueryDef", A.Str(name), A.Str(sql)).Errors().Get(qd, "SQL")
            .GetBind(ps, qd, "Parameters").Each(ps, px, 16, b => b.Get(px, "Name").Get(px, "Type"));
        return qd;
    }

    /// <summary>Assign a parameter (marker first, Errors after) and read its Value back.</summary>
    private static void SetP(P p, Names n, string qd, string name, Arg value)
    {
        string pv = n.Next("pv");
        Mark(p);
        p.Item(pv, qd + "p", A.Str(name)).Put(pv, "Value", value).Errors().Get(pv, "Value").Release(pv);
    }

    /// <summary>Open the QueryDef, read the rows, close; each step attributed.</summary>
    private static void QOpen(P p, Names n, string qd, int type = Dao.OpenSnapshot, int maxRows = 16)
    {
        string rs = n.Next("qrs");
        Mark(p);
        p.CallBind(rs, qd, "OpenRecordset", A.I4(type)).Errors();
        Rows(p, rs, maxRows);
        p.Call(rs, "Close").Release(rs).Errors();
    }

    private static void QDone(P p, string qd) => p.Release(qd + "p").Call(qd, "Close").Release(qd);

    /// <summary>Fields Count, then Name/Type/Size of each field of a QueryDef or Recordset.</summary>
    private static void Schema(P p, string target)
    {
        p.GetBind("sf", target, "Fields").Get("sf", "Count")
            .Each("sf", "sfi", 64, b => b.Get("sfi", "Name").Get("sfi", "Type").Get("sfi", "Size"))
            .Release("sf");
    }

    /// <summary>Crosstab: marker, open a snapshot, Errors, publish the schema before any move, read the rows.</summary>
    private static void Xtab(P p, string sql)
    {
        Mark(p);
        p.OpenRs("rx", "db", sql, Dao.OpenSnapshot).Errors();
        Schema(p, "rx");
        p.ReadAll("rx", 16, 64).Call("rx", "Close").Release("rx").Errors();
    }

    /// <summary>The eight rows of Dm as SQL (dates and doubles as literals, so no conversion depends on the locale).</summary>
    private static readonly string[] DmRowSql =
    [
        "INSERT INTO Dm (ID, G, V, N, S, DT) VALUES (1, 'a', 1.5, 10, 'x', #2023-03-15 00:00:00#)",
        "INSERT INTO Dm (ID, G, V, N, S, DT) VALUES (2, 'a', 2.5, 20, 'y', #2023-03-16 12:00:00#)",
        "INSERT INTO Dm (ID, G, V, N, S, DT) VALUES (3, 'a', Null, 30, Null, Null)",
        "INSERT INTO Dm (ID, G, V, N, S, DT) VALUES (4, 'b', 4.0, Null, 'x', #2023-03-17 00:00:00#)",
        "INSERT INTO Dm (ID, G, V, N, S, DT) VALUES (5, 'b', 4.0, 50, 'z', #2023-03-17 00:00:00#)",
        "INSERT INTO Dm (ID, G, V, N, S, DT) VALUES (6, Null, 6.5, 60, 'q''r', #2023-03-18 00:00:00#)",
        "INSERT INTO Dm (ID, G, V, N, S, DT) VALUES (7, 'c', -1.0, 0, '', #2023-03-19 00:00:00#)",
        "INSERT INTO Dm (ID, G, V, N, S, DT) VALUES (8, 'c', 10000000000, 70, ' pad ', #2023-03-20 06:00:00#)",
    ];

    /// <summary>Put Dm back to its eight fixture rows and One to its one row, so a statement never starts from an earlier one's effects.</summary>
    private static void Reseed(P p)
    {
        Run(p, "DELETE FROM Dm");
        Run(p, "DELETE FROM One");
        Run(p, "INSERT INTO One (ID) VALUES (1)");
        foreach (string sql in DmRowSql)
        {
            Run(p, sql);
        }
    }

    private static string Join(string sep, IEnumerable<string> items) => string.Join(sep, items);

    // Fixtures -------------------------------------------------------------------------------------------------------

    private static Probe CreateExpr() =>
        Fixture("expr", ["R-EXPR-1", "R-SQL-1", "R-SQL-2", "R-SQL-3", "RQ-SEM-01"],
            "tables One (one row), Den, Dm (8 rows with Nulls, a quote, an empty and a padded text), Mx (5 rows of N, T, D, DT, Y with Null, empty and padded values), Pn (A, A+NUL, A+NUL+B, A+NUL+C) and Vals (one row of typed scalars with M Decimal(28,4) = -1844674407370955.1621, made by ADO DDL and written as a SQL literal).",
            (p, n, file) =>
            {
                Run(p, "CREATE TABLE One (ID LONG CONSTRAINT PK_One PRIMARY KEY)");
                Run(p, "INSERT INTO One (ID) VALUES (1)");
                Run(p, "CREATE TABLE Den (Id LONG CONSTRAINT PK_Den PRIMARY KEY, Denominator LONG, TextValue TEXT(20))");
                Run(p, "INSERT INTO Den (Id, Denominator, TextValue) VALUES (1, 2, '12')");
                Run(p, "INSERT INTO Den (Id, Denominator, TextValue) VALUES (2, 0, 'abc')");
                Run(p, "INSERT INTO Den (Id, Denominator, TextValue) VALUES (3, Null, Null)");
                Run(p, "CREATE TABLE Dm (ID LONG CONSTRAINT PK_Dm PRIMARY KEY, G TEXT(10), V DOUBLE, N LONG, S TEXT(20), DT DATETIME)");
                foreach (string sql in DmRowSql)
                {
                    Run(p, sql);
                }
                Run(p, "CREATE TABLE Mx (ID LONG CONSTRAINT PK_Mx PRIMARY KEY, N LONG, T TEXT(30), D DOUBLE, DT DATETIME, Y YESNO)");
                AllowZeroLength(p, "Mx", "T");
                AddRows(p, n, "Mx", ["ID", "N", "T", "D", "DT", "Y"],
                [
                    [A.I4(1), A.I4(7), A.Str("7"), A.R8(2.5), A.Date(45000.75), A.Bool(true)],
                    [A.I4(2), A.Null, A.Str("abc"), A.R8(-2.5), A.Null, A.Bool(false)],
                    [A.I4(3), A.I4(0), A.Str(""), A.R8(0.5), A.Date(45000.0), A.Bool(false)],
                    [A.I4(4), A.I4(-3), A.Null, A.Null, A.Date(-0.25), A.Bool(true)],
                    [A.I4(5), A.I4(int.MaxValue), A.Str(" 12 "), A.R8(1e10), A.Date(0.0), A.Bool(false)],
                ]);
                Run(p, "CREATE TABLE Pn (Id LONG CONSTRAINT PK_Pn PRIMARY KEY, G TEXT(10), S TEXT(50))");
                AddRows(p, n, "Pn", ["Id", "G", "S"],
                [
                    [A.I4(1), A.Str("p11"), A.Str("A")],
                    [A.I4(2), A.Str("p11"), A.Str("A\0")],
                    [A.I4(3), A.Str("p11"), A.Str("A\0B")],
                    [A.I4(4), A.Str("p11"), A.Str("A\0C")],
                ]);
                // Vals: made through ADO DDL so that M is a readable Decimal(28,4); the other columns keep the original Vals types.
                AdoDdl(p, file,
                    "CREATE TABLE Vals (ID INTEGER CONSTRAINT PK_Vals PRIMARY KEY, B BYTE, I SMALLINT, L INTEGER, S REAL, D FLOAT, C CURRENCY, M DECIMAL(28,4), DT DATETIME, Y BIT, T TEXT(50), Q TEXT(50), N INTEGER)");
                AddRows(p, n, "Vals", ["ID", "B", "I", "L", "S", "D", "C", "DT", "Y", "T", "Q", "N"],
                [
                    [A.I4(1), A.UI1(255), A.I2(short.MaxValue), A.I4(int.MaxValue), A.R4(1.5f), A.R8(2.5), A.Cy(12.3456m), A.Date(45000.5), A.Bool(true), A.Str("7"), A.Str("q"), A.Null],
                ]);
                Run(p, "UPDATE Vals SET M = -1844674407370955.1621 WHERE ID = 1");
                Peek(p, n, "SELECT ID, M, VarType(M) AS vt, TypeName(M) AS tn, CStr(M) AS cs FROM Vals");
                Peek(p, n, "SELECT (SELECT Count(*) FROM One) AS one, (SELECT Count(*) FROM Den) AS den, (SELECT Count(*) FROM Dm) AS dm, (SELECT Count(*) FROM Mx) AS mx, (SELECT Count(*) FROM Pn) AS pn FROM One");
            });

    private static Probe CreateSql1() =>
        Fixture("sql1", ["R-SQL-1", "SQL-AGG-TYPE", "SQL-EQ-02", "SQL-UNION-02"],
            "tables L, N (Byte to Date columns with Null, signed zero, 0.1, maxima), E, NS, ND (x DECIMAL(18,4) made by ADO DDL: 1, Null, 0, 0.1, 99999999999999.9999, 1.0000 as SQL literals), NB (BIGINT), AG (groups 10 to 12), TX (t '1', '1', 'x', '1', '2') and TXC (every value converts).",
            (p, n, file) =>
            {
                Run(p, "CREATE TABLE L (id LONG CONSTRAINT PK_L PRIMARY KEY, name TEXT(20))");
                Run(p, "INSERT INTO L VALUES (1, 'a')");
                Run(p, "INSERT INTO L VALUES (2, 'b')");
                Run(p, "INSERT INTO L VALUES (3, 'c')");
                Run(p, "CREATE TABLE N (id LONG CONSTRAINT PK_N PRIMARY KEY, b BYTE, i SHORT, l LONG, s SINGLE, d DOUBLE, c CURRENCY, dt DATETIME, t TEXT(10))");
                AddRows(p, n, "N", ["id", "b", "i", "l", "s", "d", "c", "dt", "t"],
                [
                    [A.I4(1), A.UI1(1), A.I2(1), A.I4(1), A.R4(1f), A.R8(1.0), A.Cy(1m), A.Date(1.0), A.Str("1")],
                    [A.I4(2), A.Null, A.Null, A.Null, A.Null, A.Null, A.Null, A.Null, A.Null],
                    [A.I4(3), A.UI1(0), A.I2(0), A.I4(0), A.R4(-0.0f), A.R8(-0.0), A.Cy(0m), A.Date(0.0), A.Str("0")],
                    [A.I4(4), A.Null, A.Null, A.Null, A.R4(0.1f), A.R8(0.1), A.Cy(0.1m), A.Date(0.1), A.Str("0.1")],
                    [A.I4(5), A.UI1(255), A.I2(short.MaxValue), A.I4(int.MaxValue), A.R4(float.MaxValue), A.R8(1e308), A.Cy(922337203685477.5807m), A.Date(2958465.0), A.Str("max")],
                    [A.I4(6), A.UI1(1), A.I2(1), A.I4(1), A.R4(1f), A.R8(1.0), A.Cy(1m), A.Date(1.0), A.Str("1")],
                ]);
                Run(p, "CREATE TABLE NS (id LONG CONSTRAINT PK_NS PRIMARY KEY, v LONG)");
                Run(p, "INSERT INTO NS (id) VALUES (1)");
                Run(p, "INSERT INTO NS (id) VALUES (2)");
                Run(p, "CREATE TABLE E (id LONG CONSTRAINT PK_E PRIMARY KEY, g LONG, d DOUBLE)");
                Run(p, "CREATE TABLE NB (id LONG CONSTRAINT PK_NB PRIMARY KEY, v BIGINT)");
                AddRows(p, n, "NB", ["id", "v"], [[A.I4(1), A.Str("9007199254740993")], [A.I4(2), A.Str("9007199254740992")], [A.I4(3), A.I4(1)]]);
                Run(p, "CREATE TABLE AG (id LONG CONSTRAINT PK_AG PRIMARY KEY, g LONG, d DOUBLE)");
                (int G, double D)[] ag = [(10, 1e9 + 4), (10, 1e9 + 7), (10, 1e9 + 13), (10, 1e9 + 16), (11, 1e15 + 4), (11, 1e15 + 7), (11, 1e15 + 13), (11, 1e15 + 16), (12, 1e308), (12, 1e308)];
                AddRows(p, n, "AG", ["id", "g", "d"], ag.Select((r, i) => new[] { A.I4(i + 1), A.I4(r.G), A.R8(r.D) }));
                Run(p, "CREATE TABLE TX (id LONG CONSTRAINT PK_TX PRIMARY KEY, t TEXT(10))");
                Run(p, "CREATE TABLE TXC (id LONG CONSTRAINT PK_TXC PRIMARY KEY, t TEXT(10))");
                string[] tx = ["1", "1", "x", "1", "2"], txc = ["1", "1", "3", "1", "2"];
                for (int i = 0; i < 5; i++)
                {
                    Run(p, $"INSERT INTO TX VALUES ({i + 1}, '{tx[i]}')");
                    Run(p, $"INSERT INTO TXC VALUES ({i + 1}, '{txc[i]}')");
                }
                AdoDdl(p, file, "CREATE TABLE ND (id INTEGER CONSTRAINT PK_ND PRIMARY KEY, x DECIMAL(18,4))");
                foreach (string row in new[] { "1, 1", "2, Null", "3, 0", "4, 0.1", "5, 99999999999999.9999", "6, 1.0000" })
                {
                    Run(p, $"INSERT INTO ND (id, x) VALUES ({row})");
                }
                Peek(p, n, "SELECT id, x FROM ND ORDER BY id");
                Peek(p, n, "SELECT id, v FROM NB ORDER BY id");
                Peek(p, n, "SELECT (SELECT Count(*) FROM N) AS n, (SELECT Count(*) FROM AG) AS ag, (SELECT Count(*) FROM TXC) AS txc FROM L WHERE id = 1");
            });

    private static Probe CreatePar() =>
        Fixture("par", ["R-PAR-1", "PAR-FOLD", "PAR-ORDER", "PAR-ERROR"],
            "DG, NUM (1..512), FO (2^53, 1, -2^53 in three storage orders), ER (240 rows with bad values zf, zm, zl, zp, zq, tx, tq, ov at named ids), tie tables OT5, OT2, OT3, OT4 (256 rows) and Decimal tables FD0 (x DECIMAL(28,0), 28-digit witnesses) and FD20 (x DECIMAL(28,20)) made by ADO DDL and written as SQL literals.",
            (p, n, file) =>
            {
                Run(p, "CREATE TABLE DG (d LONG CONSTRAINT PK_DG PRIMARY KEY)");
                for (int i = 0; i < 8; i++)
                {
                    Run(p, $"INSERT INTO DG VALUES ({i})");
                }
                Run(p, "CREATE TABLE NUM (n LONG CONSTRAINT PK_NUM PRIMARY KEY)");
                Run(p, "INSERT INTO NUM (n) SELECT a.d * 64 + b.d * 8 + c.d + 1 FROM DG AS a, DG AS b, DG AS c ORDER BY a.d, b.d, c.d");
                Run(p, "CREATE TABLE FO (id LONG CONSTRAINT PK_FO PRIMARY KEY, k LONG, d DOUBLE)");
                Run(p, "CREATE INDEX IX_FO_K ON FO (k)");
                AddRows(p, n, "FO", ["id", "k", "d"], [[A.I4(2), A.I4(3), A.R8(9007199254740992.0)], [A.I4(3), A.I4(1), A.R8(1)], [A.I4(1), A.I4(2), A.R8(-9007199254740992.0)]]);
                Run(p, "CREATE TABLE ER (id LONG CONSTRAINT PK_ER PRIMARY KEY, pad TEXT(120), zf LONG, zm LONG, zl LONG, zp LONG, zq LONG, tx TEXT(10), tq TEXT(10), ov LONG)");
                Run(p, "INSERT INTO ER (id, pad, zf, zm, zl, zp, zq, tx, tq, ov) SELECT n, Space(100), IIf(n = 3, 0, 1), IIf(n = 100, 0, 1), IIf(n = 240, 0, 1), IIf(n = 50 Or n = 150, 0, 1), " +
                    "IIf(n = 80, 0, 1), IIf(n = 60, 'x', '1'), IIf(n = 80, 'x', '1'), IIf(n = 150, 100000, 1) FROM NUM WHERE n <= 240 ORDER BY n");
                foreach (string t in new[] { "OT5", "OT2", "OT3", "OT4" })
                {
                    Run(p, t == "OT5"
                        ? "CREATE TABLE OT5 (pk LONG, k LONG, ins LONG, s TEXT(10))"
                        : $"CREATE TABLE {t} (pk LONG CONSTRAINT PK_{t} PRIMARY KEY, k LONG, ins LONG, s TEXT(10))");
                }
                Run(p, "CREATE INDEX IX_OT3_K ON OT3 (k)");
                Run(p, "CREATE INDEX IX_OT4_KI ON OT4 (k, ins DESC)");
                foreach (string t in new[] { "OT5", "OT2", "OT3", "OT4" })
                {
                    Run(p, $"INSERT INTO {t} (pk, k, ins, s) SELECT 1000 - n, n Mod 7, n, 't' & (n Mod 5) FROM NUM WHERE n <= 256 ORDER BY n");
                }
                AdoDdl(p, file,
                    "CREATE TABLE FD0 (id INTEGER CONSTRAINT PK_FD0 PRIMARY KEY, g INTEGER, x DECIMAL(28,0))",
                    "CREATE TABLE FD20 (id INTEGER CONSTRAINT PK_FD20 PRIMARY KEY, g INTEGER, x DECIMAL(28,20))");
                string nines = "9999999999999999999999999999";
                string[] fd0 = [$"1, {nines}", "1, 1", $"1, -{nines}", $"2, {nines}", "2, 1", "3, 1", "3, 1"];
                for (int i = 0; i < fd0.Length; i++)
                {
                    string[] parts = fd0[i].Split(", ");
                    Run(p, $"INSERT INTO FD0 (id, g, x) VALUES ({i + 1}, {parts[0]}, {parts[1]})");
                }
                string[] fd20 = ["1, 1.00000000000000000001", "1, 1", "2, 0.1", "2, 0.2", "2, 0.3", "3, 0.5", "3, 0.5"];
                for (int i = 0; i < fd20.Length; i++)
                {
                    string[] parts = fd20[i].Split(", ");
                    Run(p, $"INSERT INTO FD20 (id, g, x) VALUES ({i + 1}, {parts[0]}, {parts[1]})");
                }
                Peek(p, n, "SELECT id, g, x FROM FD0 ORDER BY id");
                Peek(p, n, "SELECT id, g, x FROM FD20 ORDER BY id");
                Peek(p, n, "SELECT (SELECT Count(*) FROM NUM) AS num, (SELECT Count(*) FROM ER) AS er, (SELECT Count(*) FROM OT5) AS ot5, (SELECT Count(*) FROM OT4) AS ot4 FROM FO WHERE id = 1");
            });

    // R-EXPR-1 -------------------------------------------------------------------------------------------------------

    private static IEnumerable<Probe> ExprRepairs()
    {
        string[] mixed = ["C + D", "D + C", "C * D", "D * C", "S + L", "L + S", "S * L", "L * S", "S + D", "D + S", "C + L", "L + C",
            "M + D", "D + M", "M + C", "C + M", "M + L", "M * L", "Y + 1", "Y * Y", "DT + 1", "1 + DT", "DT - DT", "DT + C", "B + B", "I + B"];
        yield return Rep("dec-mixed-precedence", "r-expr-1/b-mixed-precedence", ["R-EXPR-1", "Branch B"],
            "M held Null, not a Decimal",
            "Asks the same result-type question with M a stored Decimal(28,4): M is read back (VarType, TypeName, CStr, CDbl); then the 26 original expressions in one projection as Field.Type and VARIANT tag, again as VarType, and each expression with M in its own snapshot.",
            (p, n) =>
            {
                Q(p, n, "SELECT ID, M, VarType(M) AS vt, TypeName(M) AS tn, CStr(M) AS cs, CDbl(M) AS cd FROM Vals");
                Q(p, n, "SELECT " + Join(", ", mixed.Select((e, i) => $"{e} AS e{i}")) + " FROM Vals");
                Q(p, n, "SELECT " + Join(", ", mixed.Select((e, i) => $"VarType({e}) AS t{i}")) + " FROM Vals");
                foreach (string e in new[] { "M + D", "D + M", "M + C", "C + M", "M + L", "M * L" })
                {
                    Q(p, n, $"SELECT {e} AS v, VarType({e}) AS vt FROM Vals");
                }
            });

        yield return Rep("dec-fixed-point-fidelity", "r-expr-1/b-fixed-point-fidelity", ["R-EXPR-1", "Branch B"],
            "M held Null, not a Decimal",
            "Asks whether the stored Decimal M survives identity, unary minus, +0, *1, CDbl, CCur, division and comparison, next to the Currency C forms; M is read back first. CDec is not used. Each expression is its own snapshot.",
            (p, n) =>
            {
                Q(p, n, "SELECT ID, M, VarType(M) AS vt, TypeName(M) AS tn FROM Vals");
                foreach (string e in new[] { "M", "-M", "M + 0", "M * 1", "CDbl(M)", "CCur(M)", "M / 1", "M \\ 1", "M = M", "VarType(M)", "M + C", "M * C", "M + D",
                    "C", "C * C", "C / 3", "C + 0.00005", "VarType(C * C)" })
                {
                    Q(p, n, $"SELECT {e} AS v FROM Vals");
                }
            });

        yield return Rep("integer-conversion-alias", "r-expr-1/b-integer-conversion", ["R-EXPR-1", "Branch B"],
            "alias d collided with field D",
            "Asks how CInt, CLng and CByte treat the stored Double D = 2.5 and its negation, with aliases that cannot collide with a field: SELECT CInt(D) AS ci, CLng(D) AS cl, CInt(-D) AS cn, CByte(D) AS cb FROM Vals, and each conversion in its own snapshot.",
            (p, n) =>
            {
                Q(p, n, "SELECT CInt(D) AS ci, CLng(D) AS cl, CInt(-D) AS cn, CByte(D) AS cb FROM Vals");
                foreach (string e in new[] { "CInt(D)", "CLng(D)", "CInt(-D)", "CByte(D)" })
                {
                    Q(p, n, $"SELECT {e} AS conv FROM Vals");
                }
            });

        yield return Rep("findfirst-fresh-recordset", "r-expr-1/d-predicate-consumers", ["R-EXPR-1", "Branch D"],
            "one recordset reused after the first NoMatch",
            "Asks the FindFirst consumer of Null, 0, 1, -1, 2, 2.5, '', '1', '0', 'q', 'True' and CVErr(5), each on its own freshly opened dynaset of SELECT ID FROM One, with Errors read right after FindFirst and NoMatch after; the valid criteria ID = 1 (first and last) and ID = 2 are controls.",
            (p, n) =>
            {
                foreach (string v in new[] { "ID = 1", "Null", "0", "1", "-1", "2", "2.5", "''", "'1'", "'0'", "'q'", "'True'", "CVErr(5)", "ID = 2", "ID = 1" })
                {
                    string rs = n.Next("fr");
                    p.OpenRs(rs, "db", "SELECT ID FROM One", Dao.OpenDynaset);
                    Mark(p);
                    p.Call(rs, "FindFirst", A.Str(v)).Errors().Get(rs, "NoMatch").Call(rs, "Close").Release(rs);
                }
            });

        yield return Rep("validation-rule-literals", "r-expr-1/mut-validation-rules", ["R-EXPR-1", "EXPR-MUTATION", "Branch D"],
            "V = 5 alone cannot tell a truth value from a comparison",
            "Asks, for each bare-literal field rule (Null, 0, 2, '', '1', 'q', True, False) on its own fresh table Vr<i> (TableDefs refreshed before the field is read): the rule is set and read back, then AddNew/Update with V = 5, 0, 2, -1, Null and V unassigned, each on a newly opened recordset, with Errors and EditMode read after each Update; the stored rows follow.",
            (p, n) =>
            {
                string[] rules = ["Null", "0", "2", "''", "'1'", "'q'", "True", "False"];
                Arg?[] values = [A.I4(5), A.I4(0), A.I4(2), A.I4(-1), A.Null, null];
                for (int i = 0; i < rules.Length; i++)
                {
                    string t = $"Vr{i}";
                    Run(p, $"CREATE TABLE {t} (ID LONG CONSTRAINT PK_{t} PRIMARY KEY, V LONG)");
                    string tds = n.Next("tds"), td = n.Next("td"), tf = n.Next("tf"), fv = n.Next("fv");
                    p.GetBind(tds, "db", "TableDefs").Call(tds, "Refresh").Item(td, tds, A.Str(t)).GetBind(tf, td, "Fields").Item(fv, tf, A.Str("V"));
                    Mark(p);
                    p.Put(fv, "ValidationRule", A.Str(rules[i])).Errors().Get(fv, "ValidationRule");
                    p.Release(fv).Release(tf).Release(td).Release(tds);
                    for (int j = 0; j < values.Length; j++)
                    {
                        string rs = n.Next("vr"), fl = rs + "f", fi = rs + "i", fx = rs + "v";
                        p.OpenRs(rs, "db", t, Dao.OpenTable).GetBind(fl, rs, "Fields").Item(fi, fl, A.Str("ID")).Item(fx, fl, A.Str("V"))
                            .Call(rs, "AddNew").Put(fi, "Value", A.I4(j + 1));
                        if (values[j] is Arg v)
                        {
                            p.Put(fx, "Value", v);
                        }
                        Mark(p);
                        p.Call(rs, "Update").Errors().Get(rs, "EditMode");
                        p.Release(fi).Release(fx).Release(fl).Call(rs, "Close").Release(rs);
                    }
                    Q(p, n, $"SELECT ID, V FROM {t} ORDER BY ID");
                }
            }, writable: true);

        yield return Rep("parameter-lifecycle-no-requery-argument", "r-expr-1/bind-parameter-lifecycle", ["R-EXPR-1", "EXPR-BIND"],
            "the worker crashed in Requery(qd)",
            "Asks the same question without the argument form: a QueryDef dynaset is read with pl = 1, pl is set to 2 and it is read, Requery() with no argument is called and it is read, a second recordset is read; the SQL is replaced by a Text-parameter statement, Parameters are refreshed and read and a value is assigned and read; a saved QueryDef over One is read before and after ALTER TABLE adds a column (QueryDefs and Fields refreshed; SQL, Fields.Count and rows read).",
            (p, n) =>
            {
                string qd = QDef(p, n, "PARAMETERS pl Long; SELECT pl * 2 AS v, ID FROM One");
                string pl = n.Next("pl"), lrs = n.Next("lrs"), lrs2 = n.Next("lrs");
                p.Item(pl, qd + "p", A.Str("pl")).Put(pl, "Value", A.I4(1)).CallBind(lrs, qd, "OpenRecordset", A.I4(Dao.OpenDynaset));
                Cur(p, n, lrs);
                p.Put(pl, "Value", A.I4(2));
                Cur(p, n, lrs);
                Mark(p);
                p.Call(lrs, "Requery").Errors();
                Cur(p, n, lrs);
                p.CallBind(lrs2, qd, "OpenRecordset", A.I4(Dao.OpenSnapshot));
                Cur(p, n, lrs2);
                p.Call(lrs2, "Close").Release(lrs2).Call(lrs, "Close").Release(lrs).Release(pl).Release(qd + "p");
                Mark(p);
                p.Put(qd, "SQL", A.Str("PARAMETERS pl Text; SELECT pl & '!' AS v FROM One")).Errors().Get(qd, "SQL");
                string qp2 = n.Next("qp"), pl2 = n.Next("pl"), rs3 = n.Next("rs");
                p.GetBind(qp2, qd, "Parameters").Call(qp2, "Refresh").Each(qp2, qp2 + "x", 8, b => b.Get(qp2 + "x", "Name").Get(qp2 + "x", "Type"));
                Mark(p);
                p.Item(pl2, qp2, A.Str("pl")).Put(pl2, "Value", A.Str("x")).Errors();
                Mark(p);
                p.CallBind(rs3, qd, "OpenRecordset", A.I4(Dao.OpenSnapshot)).Errors();
                Rows(p, rs3, 4);
                p.Call(rs3, "Close").Release(rs3).Release(pl2).Release(qp2).Call(qd, "Close").Release(qd);
                Mark(p);
                p.CallBind("qs", "db", "CreateQueryDef", A.Str("QSaved"), A.Str("SELECT * FROM One")).Errors();
                p.GetBind("qsf", "qs", "Fields").Get("qsf", "Count").Release("qsf");
                Q(p, n, "QSaved");
                Act(p, "ALTER TABLE One ADD COLUMN X LONG");
                string qdfs = n.Next("qdfs"), q2 = n.Next("q"), qf2 = n.Next("qf");
                p.GetBind(qdfs, "db", "QueryDefs").Call(qdfs, "Refresh").Item(q2, qdfs, A.Str("QSaved")).Get(q2, "SQL")
                    .GetBind(qf2, q2, "Fields").Call(qf2, "Refresh").Get(qf2, "Count").Release(qf2).Release(q2).Release(qdfs);
                Q(p, n, "QSaved");
                p.Get("qs", "SQL").Release("qs");
            }, writable: true);

        yield return Rep("nul-pattern-requery-no-argument", "r-expr-1/pat-11-16-counted-nul-and-reuse", ["R-EXPR-1", "PAT-11", "PAT-16", "EXPR-BIND"],
            "the worker crashed in Requery(qd)",
            "Asks the same counted-NUL question over Pn (A, A+NUL, A+NUL+B, A+NUL+C): Len, '=', LIKE A*, A? and A??; a WHERE with a NUL literal; an undeclared [pp] set in turn to A+NUL+B, A+NUL+C, A+NUL+B, A+NUL+* and A; then a dynaset opened with pp = A+NUL+B, pp changed to A+NUL+C, Requery() with no argument, and read again.",
            (p, n) =>
            {
                Q(p, n, "SELECT Id, S, Len(S) AS len, S = 'A' AS eq, S LIKE 'A*' AS m0, S LIKE 'A?' AS m1, S LIKE 'A??' AS m2 FROM Pn WHERE G = 'p11' ORDER BY Id");
                Q(p, n, "SELECT Id FROM Pn WHERE G = 'p11' AND S = 'A\0B' ORDER BY Id");
                string qd = QDef(p, n, "SELECT Id, S LIKE [pp] AS m, S = [pp] AS e FROM Pn WHERE G = 'p11' ORDER BY Id");
                foreach (string v in new[] { "A\0B", "A\0C", "A\0B", "A\0*", "A" })
                {
                    SetP(p, n, qd, "pp", A.Str(v));
                    QOpen(p, n, qd);
                }
                string ppx = n.Next("pp"), prs = n.Next("prs");
                p.Item(ppx, qd + "p", A.Str("pp")).Put(ppx, "Value", A.Str("A\0B")).CallBind(prs, qd, "OpenRecordset", A.I4(Dao.OpenDynaset));
                Rows(p, prs, 8);
                p.Put(ppx, "Value", A.Str("A\0C"));
                Mark(p);
                p.Call(prs, "Requery").Errors();
                Rows(p, prs, 8);
                p.Call(prs, "Close").Release(prs).Release(ppx);
                QDone(p, qd);
            });

        yield return Rep("stored-null-iif", "r-expr-1b/info-stored-null-handling", ["R-EXPR-1", "Branch A", "Branch E"],
            "Nz is undefined and the first failure was not isolated",
            "Asks how IsNull, Len, VarType, &, +, IIf and comparison treat the stored Null, empty and padded values of the five Mx rows, with IIf(IsNull(x), ...) in place of Nz and each expression in its own snapshot.",
            (p, n) =>
            {
                Q(p, n, "SELECT ID, N, T, D, DT, Y FROM Mx ORDER BY ID");
                foreach (string e in new[] { "IsNull(N)", "IsNull(T)", "Len(T)", "LenB(T)", "VarType(N)", "VarType(T)", "IIf(IsNull(N), -1, N)", "IIf(IsNull(T), 'none', T)",
                    "VarType(IIf(IsNull(N), -1, N))", "VarType(IIf(IsNull(T), '', T))", "N + 1", "N & 'x'", "T & 'x'", "T + 'x'", "N & T",
                    "IIf(IsNull(T), 'null', IIf(T = '', 'empty', T))", "IIf(IsNull(D), N, D)", "IIf(IsNull(DT), 0, DT)", "IIf(IsNull(Y), 0, Y)", "IIf(IsNull(D), 'x', D)",
                    "Trim(T) & '|'", "Left(T, 1)", "UCase(T)", "Val(T)" })
                {
                    Q(p, n, $"SELECT ID, {e} AS v FROM Mx ORDER BY ID");
                }
            });

        yield return Rep("instr-start-argument", "r-expr-1b/str-instr-compare-modes", ["R-EXPR-1", "EXPR-INVENTORY", "Branch A"],
            "a string was passed as the InStr start argument",
            "Asks what InStr returns for InStr(1, 'abc', 'b', 1) and for the a-umlaut subject searched for 'a' with compare modes 1 and 0, the start argument present, each in its own snapshot.",
            (p, n) => Sc(p, n, "InStr(1, 'abc', 'b', 1)", $"InStr(1, '{AUmlaut}', 'a', 1)", $"InStr(1, '{AUmlaut}', 'a', 0)"));

        string[] filters = ["IsNull(N)", "Nz(N, 0) > 0", "Len(T) > 1", "InStr(T, 'b') > 0", "Mid(T, 1, 1) = 'a'", "Format(D, '0') = '3'", "CInt(T) = 7", "NoSuchFunc(N) = 1", "CurDir() <> ''",
            "Environ('R_EXPR_1B_NOVAR') = ''", "IIf(IsNull(N), 0, N) < 1", "DateDiff('d', DT, #2024-03-05#) > 0", "Val(T) = 12", "T = ''", "T Is Null", "N Between -5 And 5", "N In (7, 0)", "Not (N > 0)", "1/0 = 1"];
        string[] sorts = ["N", "N DESC", "T, N", "T DESC, ID", "Len(T)", "Nz(N, 0)", "NoSuchField"];
        yield return Rep("filter-sort-fresh-recordsets", "r-expr-1b/route-recordset-filter-sort", ["R-EXPR-1", "Branch E", "EXPR-STAGE", "EXPR-INVENTORY"],
            "the filter named a column the recordset did not select",
            "Asks which functions the expression service evaluates in a Recordset.Filter and Sort over Mx, with ID, N, T, D, DT and Y selected and every Filter and Sort on its own fresh dynaset (Errors after the put and after OpenRecordset); then the narrow SELECT ID, N, T recordset with the filter on the unselected D, a valid filter on N as control, and Sort 'NoSuchField' on a fresh narrow and a fresh full recordset with no earlier filter.",
            (p, n) =>
            {
                const string full = "SELECT ID, N, T, D, DT, Y FROM Mx ORDER BY ID";
                const string narrow = "SELECT ID, N, T FROM Mx ORDER BY ID";
                foreach (string f in filters)
                {
                    FilterOpen(p, n, full, "Filter", f);
                }
                FilterOpen(p, n, narrow, "Filter", "Format(D, '0') = '3'");
                FilterOpen(p, n, narrow, "Filter", "N = 7");
                foreach (string s in sorts)
                {
                    FilterOpen(p, n, full, "Sort", s);
                }
                FilterOpen(p, n, narrow, "Sort", "NoSuchField");
            });

        string[] preds = ["T = N", "N = T", "T < 10", "T = D", "N = Y", "DT = N", "T Is Null OR T = ''", "NOT (N > 0)", "N > 0 OR T = 'abc'"];
        yield return Rep("stored-where-per-row", "r-expr-1b/cmp-stored-lanes", ["R-EXPR-1", "Branch B", "Branch A", "Branch C"],
            "a type mismatch at open hid the per-row outcomes",
            "Asks the same nine selections over Mx (T = N, N = T, T < 10, T = D, N = Y, DT = N, T Is Null OR T = '', NOT (N > 0), N > 0 OR T = 'abc') one row at a time: SELECT ID FROM Mx WHERE ID = k AND (predicate) for k = 1 to 5, each in its own snapshot.",
            (p, n) =>
            {
                foreach (string w in preds)
                {
                    for (int k = 1; k <= 5; k++)
                    {
                        Q(p, n, $"SELECT ID FROM Mx WHERE ID = {k} AND ({w}) ORDER BY ID");
                    }
                }
            });

        yield return Rep("cdec-argument-forms", "r-expr-1b/conv-text-to-floating and r-par-1/fold-currency-argument-forms", ["R-EXPR-1", "EXPR-INVENTORY", "Branch B"],
            "CDec answered 3075 in every form tried and no probe discriminates its argument forms",
            "Asks how CDec binds with zero to three arguments, as [CDec], VBA.CDec and [VBA].[CDec], over literals, stored Vals columns (M, C, D, L, T, N) and declared and undeclared QueryDef parameters (Currency, IEEEDouble, Long, Text), with CCur, CDbl, CSng, CLng, CInt and CByte (right and wrong argument counts) as controls. Each form is its own snapshot.",
            (p, n) =>
            {
                Sc(p, n, "CDec(1)", "CDec()", "CDec(1, 2)", "CDec(1, 2, 3)", "CDec(1.5)", "CDec('1')", "CDec(Null)", "CDec(True)", "CDec(CCur(1))", "CDec(CDbl(1))", "CDec (1)", "[CDec](1)", "VBA.CDec(1)", "[VBA].[CDec](1)");
                Sc(p, n, "CCur(1)", "CDbl(1)", "CSng(1)", "CLng(1)", "CInt(1)", "CByte(1)", "CCur()", "CCur(1, 2)", "CDbl()", "CDbl(1, 2)", "VBA.CCur(1)", "[VBA].[CDbl](1)");
                foreach (string c in new[] { "M", "C", "D", "L", "T", "N" })
                {
                    Q(p, n, $"SELECT CDec({c}) AS v FROM Vals");
                }
                foreach ((string decl, string name, Arg value) in new (string, string, Arg)[]
                {
                    ("PARAMETERS pc Currency; ", "pc", A.Cy(1.5m)), ("PARAMETERS pd IEEEDouble; ", "pd", A.R8(2.5)), ("PARAMETERS pl Long; ", "pl", A.I4(7)), ("PARAMETERS pt Text; ", "pt", A.Str("7")),
                })
                {
                    string qd = QDef(p, n, $"{decl}SELECT CDec({name}) AS v, CCur({name}) AS w FROM One");
                    SetP(p, n, qd, name, value);
                    QOpen(p, n, qd);
                    QDone(p, qd);
                }
                string qu = QDef(p, n, "SELECT CDec([pu]) AS v, CCur([pu]) AS w FROM One");
                SetP(p, n, qu, "pu", A.I4(7));
                QOpen(p, n, qu);
                QDone(p, qu);
            });

        string[] arrays = ["Join(Split('a b', ' '), '-')", "Split('a,b,c', ',')", "Split('a b')", "UBound(Split('a,b,c', ','))", "LBound(Split('a,b', ','))", "Split('a,b', ',')(1)", "Split('a,b', ',')(2)",
            "UBound(1)", "LBound('a')", "UBound(Null)", "Split('')", "UBound(Split(''))", "Split(Null)", "Join(Split('a b', ' '))", "Join(Split('1,2', ','), 0)",
            "Filter(Split('ab,b', ','), 'a')", "VarType(Split('a b'))", "TypeName(Split('a b'))", "CVar(Split('a b'))",
            "IRR(Split('-100,50,60', ','))", "NPV(0.1, Split('10,20', ','))", "MIRR(Split('-100,50,60', ','), 0.1, 0.1)"];
        yield return Rep("array-functions-split-only", "r-expr-1b/sandbox-array-functions", ["R-EXPR-1", "EXPR-INVENTORY", "Branch A"],
            "the Array() crash destroyed the other 28 results",
            "Asks which array-valued functions the expression service admits and what an array result does at the DAO boundary, without the Array constructor (no statement here contains it): Split, Join, Filter, UBound and LBound over Split results, indexing in place, VarType/TypeName/CVar of an array, and IRR, NPV and MIRR over Split results. Each expression is its own snapshot.",
            (p, n) => Sc(p, n, arrays));

        string[] bigSetup =
        [
            "INSERT INTO Bg (ID, B) VALUES (1, 9223372036854775807)", "INSERT INTO Bg (ID, B) VALUES (2, -9223372036854775808)", "INSERT INTO Bg (ID, B) VALUES (3, 5)",
            "INSERT INTO Bg (ID, B) VALUES (4, '9223372036854775807')", "INSERT INTO Bg (ID, B) VALUES (5, 2.5)", "INSERT INTO Bg (ID, B) VALUES (6, 9223372036854775808)", "INSERT INTO Bg (ID, B) VALUES (7, 1E19)",
            "INSERT INTO Bg (ID, B) VALUES (8, Null)", "INSERT INTO Bg (ID, B) VALUES (9, 'x')", "INSERT INTO Bg (ID, B) VALUES (10, 4611686018427387904)", "INSERT INTO Bg (ID, B) VALUES (11, 4611686018427387905)",
        ];
        yield return Rep("bigint-stored-arith", "r-def-expr-sql/expr-bigint-stored-arith", ["R-EXPR-1", "Branch B", "R-FMT-1"],
            "row ID 3 was never inserted (CLngLng is undefined)",
            "Asks how a stored BIGINT column takes values and what arithmetic returns: table Bg is created by DDL; the rows are inserted by SQL, each statement attributed (ID 3 as the literal 5): the signed 64-bit extremes, 5, the text '9223372036854775807', 2.5, one past the maximum, 1E19, Null, 'x', 2^62 and 2^62 + 1. The rows are read back, then for IDs 1, 2, 3 and 10 separately B + 1, B - 1, B * 2, B / 2, B \\ 2, B Mod 7, -B, Abs(B), B + 1.5 and B * B with VarType.",
            (p, n) =>
            {
                Run(p, "CREATE TABLE Bg (ID LONG CONSTRAINT PK_Bg PRIMARY KEY, B BIGINT)");
                foreach (string sql in bigSetup)
                {
                    Act(p, sql);
                }
                Q(p, n, "SELECT ID, B, VarType(B) AS vt, TypeName(B) AS tn FROM Bg ORDER BY ID");
                foreach (int id in new[] { 1, 2, 3, 10 })
                {
                    foreach (string e in new[] { "B + 1", "B - 1", "B * 2", "B / 2", "B \\ 2", "B Mod 7", "-B", "Abs(B)", "B + 1.5", "B * B" })
                    {
                        Q(p, n, $"SELECT {e} AS v, VarType({e}) AS vt FROM Bg WHERE ID = {id}");
                    }
                }
            }, writable: true);

        yield return Rep("bigint-stored-compare-convert", "r-def-expr-sql/expr-bigint-stored-compare-convert", ["R-EXPR-1", "Branch B", "R-FMT-1"],
            "row ID 3 was never inserted and alias b collided with column B",
            "Asks, over the same Bg rows (ID 3 as the literal 5), how BIGINT compares and converts: equality and range against 64-bit literals and 2^62 + 1 written three ways, ORDER BY B and B DESC, Min, Max and Count with aliases mn, mx and cn, Sum, Avg, GROUP BY B, and CStr, Str, Format, Hex, CDbl, CDec, CCur, CLng, CInt, CByte, CBool and CVar of B. Each is its own snapshot.",
            (p, n) =>
            {
                Run(p, "CREATE TABLE Bg (ID LONG CONSTRAINT PK_Bg PRIMARY KEY, B BIGINT)");
                foreach (string sql in bigSetup)
                {
                    Act(p, sql);
                }
                foreach (string w in new[] { "B = 9223372036854775807", "B > 9223372036854775806", "B = 4611686018427387905", "B = 4611686018427387904 + 1",
                    "B = CDbl(4611686018427387904)", "B = CDbl(4611686018427387905)", "B = '9223372036854775807'", "B = 2.5", "B = 3", "B Is Null", "B In (1, 5, 3)", "B Between 0 And 10" })
                {
                    Q(p, n, $"SELECT ID FROM Bg WHERE {w} ORDER BY ID");
                }
                foreach (string q in new[] { "SELECT ID, B FROM Bg ORDER BY B, ID", "SELECT ID, B FROM Bg ORDER BY B DESC, ID", "SELECT Min(B) AS mn, Max(B) AS mx, Count(B) AS cn FROM Bg",
                    "SELECT Sum(B) AS s FROM Bg", "SELECT Avg(B) AS s FROM Bg", "SELECT Sum(B) AS s FROM Bg WHERE ID In (3, 5)", "SELECT B, Count(*) AS c FROM Bg GROUP BY B ORDER BY B" })
                {
                    Q(p, n, q);
                }
                foreach (string e in new[] { "CStr(B)", "Str(B)", "Format(B, '0')", "Hex(B)", "CDbl(B)", "CDec(B)", "CCur(B)", "CLng(B)", "CInt(B)", "CByte(B)", "CBool(B)", "CVar(B)", "B & ''" })
                {
                    Q(p, n, $"SELECT ID, {e} AS v FROM Bg WHERE ID In (1, 3)");
                }
            }, writable: true);

        yield return Rep("domain-per-row-no-nz", "r-def-expr-sql/expr-domain-per-row", ["R-EXPR-1", "EXPR-DOMAIN", "EXPR-STAGE"],
            "one form used Nz, which is undefined",
            "Asks how DMax evaluates per row with a criteria text built from the current row's G, using IIf(IsNull(G), '', G) in place of Nz: SELECT ID, DMax('N', 'Dm', 'G = ''' & IIf(IsNull(G), '', G) & '''') AS c FROM Dm ORDER BY ID, and the same with G used directly.",
            (p, n) =>
            {
                Q(p, n, "SELECT ID, DMax('N', 'Dm', 'G = ''' & IIf(IsNull(G), '', G) & '''') AS c FROM Dm ORDER BY ID");
                Q(p, n, "SELECT ID, DMax('N', 'Dm', 'G = ''' & G & '''') AS c FROM Dm ORDER BY ID");
            });

        yield return Rep("nonjet-whole-statement-entries", "r-def-expr-sql/expr-nonjet-function-names", ["R-EXPR-1", "EXPR-INVENTORY", "EXPR-SANDBOX", "R-SQL-1", "SQL-B"],
            "two entries were wrapped into statements that say nothing about the names",
            "Asks what ACE answers to the two entries that were wrapped as SELECT 1 FROM One AS v FROM One and SELECT * AS v FROM One, run as whole statements: SELECT 1 FROM One, SELECT * FROM One and SELECT COUNT(*) FROM One.",
            (p, n) => Qs(p, n, ["SELECT 1 FROM One", "SELECT * FROM One", "SELECT COUNT(*) FROM One"]));
    }

    /// <summary>A fresh dynaset, one Filter or Sort put, Errors, then OpenRecordset on it and its rows.</summary>
    private static void FilterOpen(P p, Names n, string sql, string property, string value)
    {
        string fr = n.Next("fr"), r2 = n.Next("flt");
        p.OpenRs(fr, "db", sql, Dao.OpenDynaset);
        Mark(p);
        p.Put(fr, property, A.Str(value)).Errors().Get(fr, property);
        Mark(p);
        p.CallBind(r2, fr, "OpenRecordset").Errors();
        Rows(p, r2, 8);
        p.Call(r2, "Close").Release(r2).Call(fr, "Close").Release(fr);
    }

    // R-SQL-2 and R-SQL-3 --------------------------------------------------------------------------------------------

    private static IEnumerable<Probe> ActionRepairs()
    {
        yield return Rep("querydef-name-space-then-plain", "r-def-expr-sql/sql-querydef-names", ["R-SQL-1", "SQL-B", "CAT-Q"],
            "the early name 'q ' collided with the later names",
            "Asks whether Database.CreateQueryDef accepts 'q ' (trailing space) and then 'q' on a fresh copy with no other QueryDef, what Name reads for each, and whether each opens by that name; QueryDefs is refreshed before Count and the names are read.",
            (p, n) => NamePair(p, n, "q ", "q"), writable: true);

        yield return Rep("querydef-name-plain-then-upper", "r-def-expr-sql/sql-querydef-names", ["R-SQL-1", "SQL-B", "CAT-Q"],
            "the early name 'q ' collided with the later names",
            "Asks whether Database.CreateQueryDef accepts 'q' and then 'Q' (case only) on a fresh copy with no other QueryDef, what Name reads for each, and whether each opens by that name; QueryDefs is refreshed before Count and the names are read.",
            (p, n) => NamePair(p, n, "q", "Q"), writable: true);

        string[] texts =
        [
            "select  id   from   one", "SELECT ID FROM One;", "UPDATE One SET ID = 1", "DELETE * FROM One", "INSERT INTO One (ID) VALUES (9)", "SELECT ID INTO Mk FROM One", "CREATE TABLE QT (A LONG)",
            "TRANSFORM Count(ID) SELECT ID FROM One GROUP BY ID PIVOT ID", "SELECT ID FROM One UNION SELECT ID FROM One", "PARAMETERS p Long; SELECT ID FROM One WHERE ID = p;",
            "PARAMETERS p Long; UPDATE One SET ID = p", "DROP TABLE Nope", "ALTER TABLE One ADD COLUMN X LONG",
        ];
        yield return Rep("querydef-create-texts-reopen", "r-def-expr-sql/sql-querydef-create-texts", ["R-SQL-1", "SQL-B", "CAT-Q"],
            "Type and ReturnsRecords were read only before the reopen",
            "Asks, for thirteen SQL texts (select, UPDATE, DELETE, INSERT, make-table, DDL, crosstab, union, parameterised select and update, DROP of a missing table, ALTER), what SQL, Type and ReturnsRecords a QueryDef reads right after CreateQueryDef and after the Database is closed and reopened (QueryDefs refreshed, each found by name).",
            (p, n) =>
            {
                for (int i = 0; i < texts.Length; i++)
                {
                    string q = n.Next("q");
                    Mark(p);
                    p.CallBind(q, "db", "CreateQueryDef", A.Str($"Rf{i}"), A.Str(texts[i])).Errors().Get(q, "SQL").Get(q, "Type").Get(q, "ReturnsRecords").Release(q);
                }
                p.Call("db", "Close").Release("db").OpenDb("db2", readOnly: false);
                string qs = n.Next("qs");
                p.GetBind(qs, "db2", "QueryDefs").Call(qs, "Refresh").Get(qs, "Count");
                for (int i = 0; i < texts.Length; i++)
                {
                    string q = n.Next("q");
                    Mark(p, "db2");
                    p.Item(q, qs, A.Str($"Rf{i}")).Errors().Get(q, "SQL").Get(q, "Type").Get(q, "ReturnsRecords").Release(q);
                }
                p.Release(qs);
            }, writable: true, close: ["db2"]);

        string[] deletes =
        [
            "DELETE FROM Dm WHERE ID = 1", "DELETE * FROM Dm WHERE ID = 2", "DELETE Dm.* FROM Dm WHERE ID = 3", "DELETE ID FROM Dm WHERE ID = 4",
            "DELETE FROM Dm WHERE ID IN (SELECT TOP 1 ID FROM Dm ORDER BY ID DESC)", "DELETE FROM Dm WHERE N = (SELECT Max(N) FROM Dm)", "DELETE FROM Dm AS d WHERE d.ID = 5", "DELETE d.* FROM Dm AS d WHERE d.ID = 5",
            "DELETE FROM Dm, One", "DELETE Dm.* FROM Dm INNER JOIN One ON Dm.ID = One.ID", "DELETE One.* FROM Dm INNER JOIN One ON Dm.ID = One.ID", "DELETE DISTINCTROW Dm.* FROM Dm INNER JOIN One ON Dm.ID = One.ID",
            "DELETE TOP 1 FROM Dm", "DELETE FROM Dm LIMIT 1", "DELETE FROM Dm ORDER BY ID", "DELETE FROM Dm WHERE 1 = 0", "DELETE FROM Nope", "DELETE FROM Dm WHERE Nope = 1",
            "DELETE FROM Dm WHERE ID = (SELECT ID FROM Dm)", "DELETE FROM Dm WHERE ID IN (SELECT ID FROM Dm WHERE N Is Null)", "DELETE FROM Dm WHERE EXISTS (SELECT 1 FROM One WHERE One.ID = Dm.ID)",
            "DELETE FROM Dm WHERE EXISTS (SELECT 1 FROM One)", "DELETE FROM Dm",
        ];
        yield return Rep("delete-forms-reseeded", "r-def-expr-sql/sql-delete-forms", ["R-SQL-2", "ACT-DEL", "SQL-ACT-DDL"],
            "each DELETE ran on what the previous one left",
            "Asks which DELETE spellings Database.Execute (dbFailOnError) accepts over Dm and One. Before every statement Dm and One are emptied and refilled with their fixture rows by SQL; after it RecordsAffected, Errors, the IDs left in Dm and the count of One are read.",
            (p, n) =>
            {
                foreach (string sql in deletes)
                {
                    Reseed(p);
                    Act(p, sql);
                    Q(p, n, "SELECT ID FROM Dm ORDER BY ID");
                    Q(p, n, "SELECT Count(*) AS c FROM One");
                }
            }, writable: true);

        string[][] updates =
        [
            ["UPDATE Dm SET N = 1, V = N WHERE ID = 1"], ["UPDATE Dm SET V = N, N = 2 WHERE ID = 2"], ["UPDATE Dm SET N = V, V = N WHERE ID = 5"], ["UPDATE Dm SET N = N + 1, V = N + 1 WHERE ID = 7"],
            ["UPDATE Dm SET Dm.N = 5 WHERE ID = 6"], ["UPDATE Dm AS d SET d.N = 6 WHERE d.ID = 6"], ["UPDATE Dm SET N = N WHERE 1 = 0"], ["UPDATE Dm SET S = S & 'x' WHERE ID IN (3, 4)"],
            ["UPDATE Dm SET (N, V) = (1, 2)"], ["UPDATE Dm SET N = 1 FROM One"], ["UPDATE Dm SET N = (SELECT Max(N) FROM Dm) WHERE ID = 7"], ["UPDATE Dm SET N = DMax('N', 'Dm') WHERE ID = 8"],
            ["UPDATE Dm SET N = DCount('*', 'Dm')"], ["UPDATE Dm SET N = ID * 2, V = N WHERE ID < 3"], ["UPDATE Dm SET N = '5' WHERE ID = 1"], ["UPDATE Dm SET N = 'x' WHERE ID = 1"],
            ["UPDATE Dm SET G = 123 WHERE ID = 1"], ["UPDATE Dm SET ID = ID + 1", "UPDATE Dm SET ID = ID - 1 WHERE ID > 1"], ["UPDATE Dm SET N = Null"],
        ];
        yield return Rep("update-assignment-reseeded", "r-def-expr-sql/sql-update-assignment-order", ["R-SQL-2", "ACT-UPD-02", "SQL-ACT-DDL"],
            "the UPDATEs shared state across statements",
            "Asks how Database.Execute (dbFailOnError) evaluates UPDATE assignments over Dm. Before every statement Dm and One are reseeded by SQL; after it RecordsAffected, Errors and the rows (ID, V, N, S) are read. The primary-key shift (ID + 1, then ID - 1 for ID > 1) is one group on one reseed, as originally.",
            (p, n) =>
            {
                foreach (string[] group in updates)
                {
                    Reseed(p);
                    foreach (string sql in group)
                    {
                        Act(p, sql);
                        Q(p, n, "SELECT ID, V, N, S FROM Dm ORDER BY ID");
                    }
                }
            }, writable: true);

        (string Label, int Attr)[] variants = [("none", 0), ("update-cascade", 256), ("delete-cascade", 4096), ("both", 4352)];
        yield return Rep("fk-actions-dao-relations", "r-def-expr-sql/ddl-fk-actions", ["R-SQL-2", "SQL-ACT-DDL", "R-CAT-1", "CAT-D"],
            "every ON DELETE / ON UPDATE ALTER failed, so no action ran",
            "Asks what a parent DELETE and a parent key UPDATE do when the relationship is made through Database.CreateRelation with Attributes 0, 256 (update cascade), 4096 (delete cascade) and 4352 (both): for each variant parent Fp<i> (rows 1, 2) and child Fc<i> (10 -> 1, 11 -> 2) are created, the relation is appended and read back (Relations refreshed), parent row 1 is deleted and parent row 2 renumbered to 9, each with dbFailOnError and Errors, and both tables are read.",
            (p, n) =>
            {
                for (int i = 0; i < variants.Length; i++)
                {
                    Run(p, $"CREATE TABLE Fp{i} (ID LONG CONSTRAINT PK_Fp{i} PRIMARY KEY)");
                    Run(p, $"CREATE TABLE Fc{i} (CID LONG CONSTRAINT PK_Fc{i} PRIMARY KEY, PID LONG)");
                    Mark(p);
                    p.AddRelation($"R_{variants[i].Label}", $"Fp{i}", $"Fc{i}", "ID", "PID", variants[i].Attr).Errors();
                    Run(p, $"INSERT INTO Fp{i} (ID) VALUES (1)");
                    Run(p, $"INSERT INTO Fp{i} (ID) VALUES (2)");
                    Run(p, $"INSERT INTO Fc{i} (CID, PID) VALUES (10, 1)");
                    Run(p, $"INSERT INTO Fc{i} (CID, PID) VALUES (11, 2)");
                    Act(p, $"DELETE FROM Fp{i} WHERE ID = 1");
                    Act(p, $"UPDATE Fp{i} SET ID = 9 WHERE ID = 2");
                    Q(p, n, $"SELECT CID, PID FROM Fc{i} ORDER BY CID");
                    Q(p, n, $"SELECT ID FROM Fp{i} ORDER BY ID");
                }
                p.GetBind("rels", "db", "Relations").Call("rels", "Refresh").Get("rels", "Count")
                    .Each("rels", "rel", 64, x => x.Get("rel", "Name").Get("rel", "Table").Get("rel", "ForeignTable").Get("rel", "Attributes")).Release("rels");
            }, writable: true);

        (string Table, string Pk, string[] Statements)[] indexGroups =
        [
            ("IxA", "", ["CREATE INDEX ix4 ON IxA (B) WITH PRIMARY", "CREATE INDEX ix5 ON IxA (K) WITH PRIMARY"]),
            ("IxB", "", ["CREATE INDEX ix6 ON IxB (A) WITH DISALLOW NULL"]),
            ("IxC", "", ["CREATE INDEX ix7 ON IxC (A) WITH IGNORE NULL DISALLOW NULL"]),
            ("IxD", "", ["CREATE INDEX ix8 ON IxD (A) WITH DISALLOW NULL IGNORE NULL"]),
            ("IxE", "", ["CREATE INDEX ix9 ON IxE (A) WITH IGNORE NULL"]),
            ("IxF", "CONSTRAINT PK_IxF PRIMARY KEY", ["CREATE INDEX ix10 ON IxF (B) WITH PRIMARY"]),
            ("IxG", "", ["CREATE INDEX ix11 ON IxG (A) WITH DISALLOW NULL"]),
        ];
        yield return Rep("index-forms-fresh-tables", "r-def-expr-sql/ddl-index-forms", ["R-SQL-2", "SQL-ACT-DDL", "R-CAT-1", "CAT-D"],
            "WITH PRIMARY and DISALLOW NULL failed for other causes",
            "Asks what CREATE INDEX ... WITH PRIMARY, DISALLOW NULL, IGNORE NULL and the two combined orders do, each on its own fresh table IxA to IxG (K Long, A Text(10), B Long; three rows; no Null except in IxG A; a primary key only on IxF), with dbFailOnError, Errors and the table's indexes (refreshed: Name, Primary, Unique, IgnoreNulls, Required, Clustered). WITH PRIMARY twice is IxA, over an existing key IxF, DISALLOW NULL over a Null is IxG.",
            (p, n) =>
            {
                foreach ((string table, string pk, string[] statements) in indexGroups)
                {
                    Run(p, $"CREATE TABLE {table} (K LONG{(pk.Length > 0 ? " " + pk : "")}, A TEXT(10), B LONG)");
                    Run(p, $"INSERT INTO {table} (K, A, B) VALUES (1, 'a', 10)");
                    Run(p, $"INSERT INTO {table} (K, A, B) VALUES (2, 'b', 20)");
                    Run(p, table == "IxG" ? $"INSERT INTO {table} (K, A, B) VALUES (3, Null, 30)" : $"INSERT INTO {table} (K, A, B) VALUES (3, 'c', 30)");
                    foreach (string sql in statements)
                    {
                        Act(p, sql);
                    }
                    string tds = n.Next("tds"), td = n.Next("td"), ixs = n.Next("ixs"), ix = ixs + "x";
                    p.GetBind(tds, "db", "TableDefs").Call(tds, "Refresh").Item(td, tds, A.Str(table)).GetBind(ixs, td, "Indexes").Call(ixs, "Refresh")
                        .Each(ixs, ix, 16, x => x.Get(ix, "Name").Get(ix, "Primary").Get(ix, "Unique").Get(ix, "IgnoreNulls").Get(ix, "Required").Get(ix, "Clustered"))
                        .Release(ixs).Release(td).Release(tds);
                }
            }, writable: true);

        yield return Rep("record-width-bracket", "r-def-expr-sql/ddl-name-and-size-limits", ["R-SQL-2", "SQL-ACT-DDL", "SQL-B", "R-CAT-1", "CAT-D"],
            "every INSERT was refused, so no accepted width was found",
            "Asks which record width an INSERT accepts: tables of n = 1 to 8 Text(255) columns, one row each whose every column holds 255 'x' (Wx<n>) and, separately, 255 euro signs (We<n>), each INSERT with dbFailOnError and Errors, then the row count and first column length read back.",
            (p, n) =>
            {
                foreach ((string prefix, string fill) in new[] { ("Wx", "String(255, 'x')"), ("We", "String(255, ChrW(8364))") })
                {
                    for (int cols = 1; cols <= 8; cols++)
                    {
                        string t = prefix + cols;
                        Run(p, $"CREATE TABLE {t} ({Join(", ", Enumerable.Range(1, cols).Select(i => $"c{i} TEXT(255)"))})");
                        Act(p, $"INSERT INTO {t} ({Join(", ", Enumerable.Range(1, cols).Select(i => $"c{i}"))}) VALUES ({Join(", ", Enumerable.Repeat(fill, cols))})");
                        Q(p, n, $"SELECT Count(*) AS c, Max(Len(c1)) AS l FROM {t}");
                    }
                }
            }, writable: true);

        yield return Rep("fail-relationship-update-isolated", "r-sql-2-3/act-fail-relationship", ["R-SQL-2", "SQL-CONTINUE"],
            "an earlier DELETE had removed parent P2",
            "Asks the mixed case on a fresh pair of tables with no earlier DELETE: P (1, 2, 3) and C (10 -> 1, 11 -> 1, 12 -> 3) under a restrict foreign key, UPDATE C SET PID = PID + 1 without options (two children move to existing parent 2, one to missing 4); and the same on P2/C2 with dbFailOnError. RecordsAffected, Errors and the rows are read after each.",
            (p, n) =>
            {
                foreach (string s in new[] { "", "2" })
                {
                    Run(p, $"CREATE TABLE P{s} (PID LONG CONSTRAINT PK_P{s} PRIMARY KEY, Name TEXT(10))");
                    Run(p, $"CREATE TABLE C{s} (CID LONG CONSTRAINT PK_C{s} PRIMARY KEY, PID LONG, Tag TEXT(10), CONSTRAINT FK_C{s} FOREIGN KEY (PID) REFERENCES P{s} (PID))");
                    foreach (string row in new[] { "1, 'p1'", "2, 'p2'", "3, 'p3'" })
                    {
                        Run(p, $"INSERT INTO P{s} VALUES ({row})");
                    }
                    foreach (string row in new[] { "10, 1, 'c10'", "11, 1, 'c11'", "12, 3, 'c12'" })
                    {
                        Run(p, $"INSERT INTO C{s} VALUES ({row})");
                    }
                }
                Mark(p);
                p.Call("db", "Execute", A.Str("UPDATE C SET PID = PID + 1")).Get("db", "RecordsAffected").Errors();
                Q(p, n, "SELECT * FROM C ORDER BY CID");
                Q(p, n, "SELECT * FROM P ORDER BY PID");
                Act(p, "UPDATE C2 SET PID = PID + 1");
                Q(p, n, "SELECT * FROM C2 ORDER BY CID");
                Q(p, n, "SELECT * FROM P2 ORDER BY PID");
            }, writable: true);

        yield return Rep("fail-insert-required-cross-table", "r-sql-2-3/act-fail-insert-required", ["R-SQL-2", "SQL-CONTINUE", "ACT-INS-02"],
            "the description said T from T2 but the statement ran T2 from T2",
            "Asks the required-field failure of INSERT ... SELECT from another table, as described: T2 holds four rows (R 'r'); INSERT INTO T3 (ID, V, U, S, W, R) SELECT ID + 10, V, U + 10, S, W, IIf(ID = 2, Null, R) FROM T2 without options into the empty T3, and the same into the empty T4 with dbFailOnError. RecordsAffected, Errors and the rows are read after each.",
            (p, n) =>
            {
                foreach (string t in new[] { "T2", "T3", "T4" })
                {
                    Run(p, $"CREATE TABLE {t} (ID LONG CONSTRAINT PK_{t} PRIMARY KEY, V LONG, U LONG, S TEXT(10), W LONG, R TEXT(10) NOT NULL)");
                }
                foreach (string row in new[] { "1, 10, 101, '1', 30, 'r'", "2, 20, 102, 'x', 10, 'r'", "3, 20, 103, '3', 30, 'r'", "4, 30, 104, '4', 30, 'r'" })
                {
                    Run(p, $"INSERT INTO T2 VALUES ({row})");
                }
                const string select = "SELECT ID + 10, V, U + 10, S, W, IIf(ID = 2, Null, R) FROM T2";
                Mark(p);
                p.Call("db", "Execute", A.Str($"INSERT INTO T3 (ID, V, U, S, W, R) {select}")).Get("db", "RecordsAffected").Errors();
                Q(p, n, "SELECT ID, U, R FROM T3 ORDER BY ID");
                Act(p, $"INSERT INTO T4 (ID, V, U, S, W, R) {select}");
                Q(p, n, "SELECT ID, U, R FROM T4 ORDER BY ID");
                Q(p, n, "SELECT ID, U, R FROM T2 ORDER BY ID");
            }, writable: true);

        yield return Rep("pivot-dynaset-requery", "r-sql-2-3/pivot-schema-lifetime", ["R-SQL-3", "SQL-PIVOT-05"],
            "Requery was called on a snapshot",
            "Asks when the crosstab schema is published and how long it lasts, with the recordset opened as a dynaset (type 2) and Requery() called without argument: saved crosstab QXtab (PARAMETERS pMin Long) Fields before and after pMin = 0; Fields after OpenRecordset, MoveNext and MoveLast; a new pivot value 'new' inserted; Requery(), schema and rows; pMin = 3 and Requery() again; then saved QXFixed.",
            (p, n) =>
            {
                CrosstabSource(p);
                Mark(p);
                p.CallBind("qx", "db", "CreateQueryDef", A.Str("QXtab"), A.Str("PARAMETERS pMin Long; TRANSFORM Sum(Amt) AS SA SELECT Grp FROM S WHERE Amt >= pMin GROUP BY Grp PIVOT Piv;")).Errors();
                Mark(p);
                p.CallBind("qf", "db", "CreateQueryDef", A.Str("QXFixed"), A.Str("TRANSFORM Sum(Amt) AS SA SELECT Grp FROM S GROUP BY Grp PIVOT Piv IN ('b', 'a', 'zz');")).Errors();
                Schema(p, "qx");
                p.GetBind("pxs", "qx", "Parameters").Item("pmin", "pxs", A.Str("pMin")).Put("pmin", "Value", A.I4(0));
                Schema(p, "qx");
                Mark(p);
                p.CallBind("rx", "qx", "OpenRecordset", A.I4(Dao.OpenDynaset)).Errors();
                Schema(p, "rx");
                p.Call("rx", "MoveNext");
                Schema(p, "rx");
                p.Call("rx", "MoveLast").Get("rx", "EOF");
                Schema(p, "rx");
                Act(p, "INSERT INTO S (ID, Grp, Piv, Amt) VALUES (20, 'g1', 'new', 9)");
                Mark(p);
                p.Call("rx", "Requery").Errors();
                Schema(p, "rx");
                p.ReadAll("rx", 16, 64);
                p.Put("pmin", "Value", A.I4(3));
                Mark(p);
                p.Call("rx", "Requery").Errors();
                Schema(p, "rx");
                p.ReadAll("rx", 16, 64);
                p.Call("rx", "Close").Release("rx");
                Schema(p, "qx");
                p.Release("pmin").Release("pxs").Release("qx");
                Schema(p, "qf");
                p.Release("qf");
            }, writable: true);

        yield return Rep("pivot-headings-clean", "r-sql-2-3/pivot-headings", ["R-SQL-3", "SQL-PIVOT-01", "SQL-PIVOT-04"],
            "a duplicate in the IN list and a Null in PN",
            "Asks the heading order of a fixed list without a duplicate, IN ('c', 'b', 'a', 'zz') and IN ('a', 'zz', 'c'), and the headings of PIVOT CStr(PN), IIf(ID Mod 2 = 0, PN, CStr(PN)) and PN over only the rows where PN is not Null; the schema is published before any move and the rows are read.",
            (p, n) =>
            {
                CrosstabSource(p);
                const string head = "TRANSFORM Sum(Amt) AS SA SELECT Grp FROM S GROUP BY Grp PIVOT ";
                const string headNn = "TRANSFORM Sum(Amt) AS SA SELECT Grp FROM S WHERE PN Is Not Null GROUP BY Grp PIVOT ";
                Xtab(p, head + "Piv IN ('c', 'b', 'a', 'zz')");
                Xtab(p, head + "Piv IN ('a', 'zz', 'c')");
                Xtab(p, headNn + "CStr(PN)");
                Xtab(p, headNn + "IIf(ID Mod 2 = 0, PN, CStr(PN))");
                Xtab(p, headNn + "PN");
            }, writable: true);
    }

    private static void NamePair(P p, Names n, string first, string second)
    {
        foreach (string name in new[] { first, second })
        {
            string q = n.Next("q"), rs = n.Next("rs");
            Mark(p);
            p.CallBind(q, "db", "CreateQueryDef", A.Str(name), A.Str("SELECT ID FROM One;")).Errors().Get(q, "Name").Release(q);
            Mark(p);
            p.OpenRs(rs, "db", name, Dao.OpenSnapshot).Errors();
            Rows(p, rs, 4);
            p.Call(rs, "Close").Release(rs);
        }
        string qdfs = n.Next("qdfs"), qx = qdfs + "x";
        p.GetBind(qdfs, "db", "QueryDefs").Call(qdfs, "Refresh").Get(qdfs, "Count").Each(qdfs, qx, 8, x => x.Get(qx, "Name")).Release(qdfs);
    }

    /// <summary>The crosstab source S of r-sql-2-3 (without the Odd column), made on the writable copy.</summary>
    private static void CrosstabSource(P p)
    {
        Run(p, "CREATE TABLE S (ID LONG CONSTRAINT PK_S PRIMARY KEY, Grp TEXT(10), Piv TEXT(10), Amt LONG, PN LONG)");
        foreach (string row in new[] { "1, 'g1', 'a', 1, 1", "2, 'g1', 'b', 2, 2", "3, 'g2', 'a', 3, 1", "4, 'g2', 'A', 4, 3", "5, 'g2', Null, 5, 2", "6, 'g3', 'c', Null, 3", "7, 'g1', 'b', Null, 2", "8, 'g4', Null, Null, Null" })
        {
            Run(p, $"INSERT INTO S VALUES ({row})");
        }
    }

    // R-SQL-1 on the Decimal fixture ---------------------------------------------------------------------------------

    private static IEnumerable<Probe> SelectRepairs()
    {
        yield return Rep("agg-result-type-decimal", "r-sql-1/agg-result-type", ["R-SQL-1", "SQL-AGG-TYPE"],
            "ND was never created (3292)",
            "Asks, over the stored Decimal x of ND (made by ADO DDL, SQL literals), what result Field.Type, Size, Precision, Scale and value type Sum, Avg, Min and Max give, each in its own statement over the rows without the 99999999999999.9999 row, then Sum over all rows. The stored rows are read back first.",
            (p, n) =>
            {
                Q(p, n, "SELECT id, x FROM ND ORDER BY id");
                Qm(p, n, "SELECT Sum(x) AS Sum_x FROM ND WHERE id <> 5");
                Qm(p, n, "SELECT Avg(x) AS Avg_x FROM ND WHERE id <> 5");
                Qm(p, n, "SELECT Min(x) AS Min_x, Max(x) AS Max_x FROM ND");
                Qm(p, n, "SELECT Sum(x) AS v FROM ND");
            }, fixture: FxSql1);

        yield return Rep("union-types-decimal", "r-sql-1/union-types", ["R-SQL-1", "SQL-UNION-02", "SQL-REL"],
            "ND was never created (3292)",
            "Asks what common output Field type, size, precision and value types UNION gives for Currency and the stored Decimal x of ND in both arm orders, and UNION ALL in both orders (ACCDB; the MDB file is not repaired here).",
            (p, n) =>
            {
                Qm(p, n, "SELECT c AS v FROM N UNION SELECT x FROM ND");
                Qm(p, n, "SELECT x AS v FROM ND UNION SELECT c FROM N");
                Qm(p, n, "SELECT c AS v FROM N UNION ALL SELECT x FROM ND");
                Qm(p, n, "SELECT x AS v FROM ND UNION ALL SELECT c FROM N");
            }, fixture: FxSql1);

        string[] adoSql = ["SELECT c AS v FROM N UNION SELECT x FROM ND", "SELECT x AS v FROM ND UNION SELECT c FROM N", "SELECT id, x FROM ND ORDER BY id"];
        yield return Rep("ado-decimal-union", "r-sql-1/b-ado", ["R-SQL-1", "SQL-B", "SQL-REL"],
            "ND was never created (3292)",
            "Asks whether ADO (ADODB.Connection, Provider Microsoft.ACE.OLEDB.12.0, adModeRead, Data Source through Properties) gives the same metadata and rows as DAO for the Decimal UNION texts and the stored x of ND: each text through Connection.Execute (Fields Name/Type/DefinedSize/Precision/NumericScale/Attributes, rows, Connection.Errors cleared before and read after); then DAO opens the same copy read-only and runs the same texts.",
            (p, n) =>
            {
                p.EngineLite().Get("eng", "Version");
                p.Create("ADODB.Connection", "cn").Modules("after-ado-create")
                    .Put("cn", "Provider", A.Str(Ace)).Put("cn", "Mode", A.I4(1))
                    .GetBind("cnp", "cn", "Properties").Item("cnds", "cnp", A.Str("Data Source")).Put("cnds", "Value", A.In()).Release("cnds").Release("cnp")
                    .Call("cn", "Open").Get("cn", "Version").Get("cn", "Provider").Get("cn", "ConnectionString");
                foreach (string sql in adoSql)
                {
                    string ar = n.Next("ar"), af = ar + "f", afi = ar + "fi", ce = ar + "e", cex = ar + "ex";
                    p.GetBind(ce + "c", "cn", "Errors").Call(ce + "c", "Clear").Release(ce + "c");
                    p.CallBind(ar, "cn", "Execute", A.Str(sql))
                        .GetBind(af, ar, "Fields")
                        .Each(af, afi, 32, b => b.Get(afi, "Name").Get(afi, "Type").Get(afi, "DefinedSize").Get(afi, "Precision").Get(afi, "NumericScale").Get(afi, "Attributes"))
                        .Release(af)
                        .ReadAll(ar, 16, 32).Call(ar, "Close").Release(ar)
                        .GetBind(ce, "cn", "Errors")
                        .Each(ce, cex, 16, b => b.Get(cex, "Number").Get(cex, "Description").Get(cex, "Source").Get(cex, "NativeError").Get(cex, "SQLState"))
                        .Release(ce);
                }
                p.Call("cn", "Close").Release("cn").Modules("after-ado-close");
                p.OpenDb().Get("db", "Version");
                foreach (string sql in adoSql)
                {
                    Qm(p, n, sql);
                }
                p.FinishLite("db");
            }, fixture: FxSql1, open: false);

        yield return Rep("eq-decimal-bigint-groups", "r-sql-1/eq-cross-type, r-sql-1/eq-numeric-domains and r-sql-1/b-join-bound-types", ["R-SQL-1", "SQL-B", "SQL-EQ-02", "SQL-EQ-03", "SQL-KEY-01", "SQL-KEY-02"],
            "ND was never created (3292)",
            "Asks which values group together and which representative and VARIANT type returns: GROUP BY the Decimal x of ND and the BIGINT v of NB; Currency/Decimal and BIGINT/Double through UNION ALL in both arm orders; and whether N.c = ND.x is admitted in an INNER JOIN ON and which rows match. The stored ND and NB rows are read first.",
            (p, n) =>
            {
                Q(p, n, "SELECT id, x FROM ND ORDER BY id");
                Q(p, n, "SELECT id, v FROM NB ORDER BY id");
                Q(p, n, "SELECT a.id, b.id FROM N AS a INNER JOIN ND AS b ON a.c = b.x");
                Qm(p, n, "SELECT x, Count(*) AS n FROM ND GROUP BY x");
                Qm(p, n, "SELECT v, Count(*) AS n FROM NB GROUP BY v");
                Qm(p, n, "SELECT v, Count(*) AS n FROM (SELECT c AS v FROM N UNION ALL SELECT x FROM ND) GROUP BY v");
                Qm(p, n, "SELECT v, Count(*) AS n FROM (SELECT x AS v FROM ND UNION ALL SELECT c FROM N) GROUP BY v");
                Qm(p, n, "SELECT v, Count(*) AS n FROM (SELECT v FROM NB UNION ALL SELECT d FROM N) GROUP BY v");
                Qm(p, n, "SELECT v, Count(*) AS n FROM (SELECT d AS v FROM N UNION ALL SELECT v FROM NB) GROUP BY v");
            }, fixture: FxSql1);

        yield return Rep("agg-numeric-per-group", "r-sql-1/agg-numeric", ["R-SQL-1", "SQL-AGG-NUMERIC"],
            "three groups in one statement overflowed together",
            "Asks what exact Double bits Avg, Var, VarP, StDev, StDevP and Sum give for nearly constant values at a large offset (group 10: 1e9 plus 4, 7, 13, 16; group 11: 1e15 plus the same) and for two values of 1e308 (group 12), each group in its own statement and each aggregate in its own statement per group.",
            (p, n) =>
            {
                foreach (int g in new[] { 10, 11, 12 })
                {
                    Qm(p, n, $"SELECT Sum(d) AS s, Avg(d) AS a, Var(d) AS v, VarP(d) AS vp, StDev(d) AS sd, StDevP(d) AS sdp FROM AG WHERE g = {g}");
                    foreach (string agg in new[] { "Sum", "Avg", "Var", "VarP", "StDev", "StDevP" })
                    {
                        Qm(p, n, $"SELECT {agg}(d) AS v FROM AG WHERE g = {g}");
                    }
                }
            }, fixture: FxSql1);

        yield return Rep("agg-occur-convertible", "r-sql-1/agg-occur", ["R-SQL-1", "SQL-AGG-OCCUR"],
            "the 'x' row of TX failed every variant at open",
            "Asks what rows and order result when the same aggregate over a conversion appears in SELECT, HAVING and ORDER BY (ascending, descending, HAVING only), over TXC where every value converts (1, 1, 3, 1, 2), over TX with its 'x' row excluded by WHERE t <> 'x', and over unfiltered TX for the error phase.",
            (p, n) =>
            {
                foreach (string src in new[] { "TXC", "TX WHERE t <> 'x'" })
                {
                    Qm(p, n, $"SELECT t, Sum(CLng(t)) AS s FROM {src} GROUP BY t HAVING Sum(CLng(t)) > 0 ORDER BY Sum(CLng(t))");
                    Qm(p, n, $"SELECT t, Sum(CLng(t)) AS s FROM {src} GROUP BY t HAVING Sum(CLng(t)) > 0 ORDER BY Sum(CLng(t)) DESC");
                    Qm(p, n, $"SELECT t FROM {src} GROUP BY t HAVING Sum(CLng(t)) > 1");
                }
                Qm(p, n, "SELECT t, Sum(CLng(t)) AS s FROM TX GROUP BY t HAVING Sum(CLng(t)) > 0 ORDER BY Sum(CLng(t))");
                Qm(p, n, "SELECT t FROM TX GROUP BY t HAVING Sum(CLng(t)) > 0");
            }, fixture: FxSql1);

        yield return Rep("parameters-values-set", "r-sql-1/b-parameters", ["R-SQL-1", "SQL-B"],
            "[p], [P] and the unused pA were never assigned",
            "Asks, on a temporary QueryDef, what an assigned value does for the undeclared [p]/[P] pair (SELECT id FROM L WHERE id = [p] OR id = [P] + 1), assigned 2 through the one parameter '[p]', and for the declared but unused pA Long (SELECT id FROM L), assigned 1: Parameters read, value assigned and read back, open read; each also opened first without an assignment.",
            (p, n) =>
            {
                string q3 = QDef(p, n, "SELECT id FROM L WHERE id = [p] OR id = [P] + 1;");
                QOpen(p, n, q3);
                SetP(p, n, q3, "[p]", A.I4(2));
                QOpen(p, n, q3);
                QDone(p, q3);
                string q4 = QDef(p, n, "PARAMETERS pA Long; SELECT id FROM L;");
                QOpen(p, n, q4);
                SetP(p, n, q4, "pA", A.I4(1));
                QOpen(p, n, q4);
                QDone(p, q4);
            }, fixture: FxSql1);
    }

    // R-SQL-1 and R-SQL-3, r-sql-13b -----------------------------------------------------------------------------------

    private static IEnumerable<Probe> LimitAndParameterRepairs()
    {
        yield return Rep("join-limit-comma-tables", "r-sql-13b/join-limit-tables", ["R-SQL-1", "SQL-B"],
            "the 32-table nest timed out and lost the other cases",
            "Asks where the number of table occurrences stops being accepted, and at which phase, with only the count varying: a comma cross join of n aliases of the one-row table One, for n = 32, 33, 34 and 50 in ascending order, each opened directly. The nested INNER JOIN form is in its own probe.",
            (p, n) =>
            {
                foreach (int tables in new[] { 32, 33, 34, 50 })
                {
                    Q(p, n, "SELECT t1.ID FROM " + Join(", ", Enumerable.Range(1, tables).Select(i => $"One AS t{i}")));
                }
            });

        yield return Rep("join-limit-nest-32", "r-sql-13b/join-limit-tables", ["R-SQL-1", "SQL-B"],
            "the 32-table nest timed out and lost the other cases",
            "Asks whether a nest of parenthesized INNER JOINs of 32 aliases of the one-row table One joined on ID is accepted, and at which phase, opened directly as the only statement of its probe so that nothing else is lost if the attempt times out.",
            (p, n) => Q(p, n, NestSql(32)));

        int[] positions = [250, 254, 255, 256, 257, 300, 1000];
        yield return Rep("memo-compare-counts", "r-sql-13b/memo-compare-scope", ["R-SQL-1", "SQL-EQ-01", "SQL-KEY-03", "SQL-B"],
            "the 48-row cap cut off the pair list",
            "Asks which Memo pairs match, as counts so that no row cap applies: MMP holds pairs of Memo values equal up to position p and different at p ('a' versus 'b') for p = 250, 254, 255, 256, 257, 300, 1000 plus a repeat of row 1; Count(*) of pairs (a.id < b.id) with a.m = b.m as INNER JOIN and as comma join, with a.m < b.m, and both grouped by a.p and b.p.",
            (p, n) =>
            {
                Run(p, "CREATE TABLE MMP (id LONG CONSTRAINT PK_MMP PRIMARY KEY, p LONG, m MEMO)");
                List<Arg[]> rows = [];
                foreach (int pos in positions)
                {
                    int id = rows.Count + 1;
                    rows.Add([A.I4(id), A.I4(pos), A.Str(new string('q', pos - 1) + "a")]);
                    rows.Add([A.I4(id + 1), A.I4(pos), A.Str(new string('q', pos - 1) + "b")]);
                }
                rows.Add([A.I4(rows.Count + 1), A.I4(positions[0]), A.Str(new string('q', positions[0] - 1) + "a")]);
                AddRows(p, n, "MMP", ["id", "p", "m"], rows);
                Q(p, n, "SELECT Count(*) AS n FROM MMP");
                Q(p, n, "SELECT Count(*) AS n FROM MMP AS a INNER JOIN MMP AS b ON a.m = b.m WHERE a.id < b.id");
                Q(p, n, "SELECT Count(*) AS n FROM MMP AS a, MMP AS b WHERE a.m = b.m AND a.id < b.id");
                Q(p, n, "SELECT Count(*) AS n FROM MMP AS a, MMP AS b WHERE a.m < b.m AND a.id < b.id");
                Q(p, n, "SELECT a.p AS pa, b.p AS pb, Count(*) AS n FROM MMP AS a, MMP AS b WHERE a.m = b.m AND a.id < b.id GROUP BY a.p, b.p ORDER BY a.p, b.p", maxRows: 64);
                Q(p, n, "SELECT a.p AS pa, b.p AS pb, Count(*) AS n FROM MMP AS a, MMP AS b WHERE a.m < b.m AND a.id < b.id GROUP BY a.p, b.p ORDER BY a.p, b.p", maxRows: 64);
            }, writable: true);

        yield return Rep("agg-stat-argument-types-renamed", "r-sql-13b/agg-stat-argument-types", ["R-SQL-1", "SQL-AGG-SINGLE", "SQL-AGG-TYPE"],
            "aliases b, c and d collided with columns",
            "Asks what Var, VarP, StDev and StDevP give, with which Field type, over two rows (a Byte, Integer, Long, Single, Currency, Double and Date column of table SI, rows 1 and 2) with aliases va, vpp, sdv and sdp that cannot collide with a column, each column in its own statement.",
            (p, n) =>
            {
                Run(p, "CREATE TABLE SI (id LONG CONSTRAINT PK_SI PRIMARY KEY, i SHORT, b BYTE, l LONG, s SINGLE, c CURRENCY, d DOUBLE, dt DATETIME)");
                AddRows(p, n, "SI", ["id", "i", "b", "l", "s", "c", "d", "dt"],
                [
                    [A.I4(1), A.I2(1), A.UI1(1), A.I4(1), A.R4(1f), A.Cy(0.0001m), A.R8(1.0), A.Date(1.0)],
                    [A.I4(2), A.I2(-1), A.UI1(0), A.I4(-1), A.R4(-1f), A.Cy(-0.0001m), A.R8(-1.0), A.Date(-100001.0)],
                ]);
                Q(p, n, "SELECT * FROM SI ORDER BY id");
                foreach (string col in new[] { "i", "b", "l", "s", "c", "d", "dt" })
                {
                    Qm(p, n, $"SELECT Var({col}) AS va, VarP({col}) AS vpp, StDev({col}) AS sdv, StDevP({col}) AS sdp FROM SI");
                }
            }, writable: true);

        yield return Rep("agg-sum-boundary-per-group", "r-sql-13b/agg-sum-boundary", ["R-SQL-1", "SQL-AGG-TYPE"],
            "no below-limit Currency group and no per-group isolation",
            "Asks what Field type and value, or error and call, Sum and Avg give for Currency c and Double d per group, each group and aggregate in its own statement: groups 1 to 4 as in r-sql-13b (maximum plus a small value, two maxima, two halves adding to one past the maximum, minimum plus -1) and Currency groups 5 (1, 2), 6 (922337203685477 and 0.5807, adding to the Currency maximum) and 7 (the maximum and 0).",
            (p, n) =>
            {
                const decimal cyMax = 922337203685477.5807m;
                Run(p, "CREATE TABLE SC (id LONG CONSTRAINT PK_SC PRIMARY KEY, g LONG, c CURRENCY, d DOUBLE)");
                AddRows(p, n, "SC", ["id", "g", "c", "d"],
                [
                    [A.I4(1), A.I4(1), A.Cy(cyMax), A.R8(1e308)], [A.I4(2), A.I4(1), A.Cy(0.0001m), A.R8(1.0)],
                    [A.I4(3), A.I4(2), A.Cy(cyMax), A.R8(1e308)], [A.I4(4), A.I4(2), A.Cy(cyMax), A.R8(1e308)],
                    [A.I4(5), A.I4(3), A.Cy(461168601842738.7904m), A.R8(9e307)], [A.I4(6), A.I4(3), A.Cy(461168601842738.7904m), A.R8(9e307)],
                    [A.I4(7), A.I4(4), A.Cy(-922337203685477.5808m), A.R8(-1e308)], [A.I4(8), A.I4(4), A.Cy(-0.0001m), A.R8(-1.0)],
                    [A.I4(9), A.I4(5), A.Cy(1m), A.R8(1.0)], [A.I4(10), A.I4(5), A.Cy(2m), A.R8(2.0)],
                    [A.I4(11), A.I4(6), A.Cy(922337203685477m), A.R8(1.0)], [A.I4(12), A.I4(6), A.Cy(0.5807m), A.R8(2.0)],
                    [A.I4(13), A.I4(7), A.Cy(cyMax), A.R8(1.0)], [A.I4(14), A.I4(7), A.Cy(0m), A.R8(2.0)],
                ]);
                Q(p, n, "SELECT id, g, c, d FROM SC ORDER BY id", maxRows: 16);
                for (int g = 1; g <= 7; g++)
                {
                    Qm(p, n, $"SELECT Sum(c) AS s FROM SC WHERE g = {g}");
                    Qm(p, n, $"SELECT Avg(c) AS a FROM SC WHERE g = {g}");
                }
                for (int g = 1; g <= 4; g++)
                {
                    Qm(p, n, $"SELECT Sum(d) AS s FROM SC WHERE g = {g}");
                    Qm(p, n, $"SELECT Avg(d) AS a FROM SC WHERE g = {g}");
                }
            }, writable: true);

        yield return Rep("nz-free-forms", "r-sql-13b/params-null-and-unset, join-on-predicate-forms, order-expression-forms and agg-expression-composition", ["R-SQL-1", "R-SQL-3", "SQL-B", "SQL-KEY-02", "SQL-SUB-02"],
            "Nz is undefined in four queries",
            "Asks the same four questions with IIf(IsNull(x), 0, x) in place of Nz: a LEFT JOIN ON over JA.a and JB.b, Sum and ORDER BY over GA.v, and a declared Long parameter p (unassigned, Null, Empty) projected as itself, p + 1, IIf(IsNull(p), 7, p), (x = p) and (p Is Null) over CX.",
            (p, n) =>
            {
                Run(p, "CREATE TABLE JA (a LONG, g TEXT(5))");
                foreach (string row in new[] { "1, 'x'", "2, 'y'", "Null, 'z'", "4, 'x'" })
                {
                    Run(p, $"INSERT INTO JA (a, g) VALUES ({row})");
                }
                Run(p, "CREATE TABLE JB (b LONG, h TEXT(5))");
                foreach (string row in new[] { "1, 'p'", "1, 'q'", "Null, 'r'", "5, 's'" })
                {
                    Run(p, $"INSERT INTO JB (b, h) VALUES ({row})");
                }
                Run(p, "CREATE TABLE GA (id LONG CONSTRAINT PK_GA PRIMARY KEY, g LONG, v LONG)");
                foreach (string row in new[] { "1, 1, 10", "2, 1, 20", "3, 1, Null", "4, 2, Null", "5, 2, 5", "6, Null, 6", "7, Null, 7", "8, Null, Null", "9, 3, 5" })
                {
                    Run(p, $"INSERT INTO GA (id, g, v) VALUES ({row})");
                }
                Run(p, "CREATE TABLE CX (x LONG)");
                Run(p, "INSERT INTO CX (x) VALUES (1)");
                Run(p, "INSERT INTO CX (x) VALUES (2)");
                Q(p, n, "SELECT JA.a, JB.b FROM JA LEFT JOIN JB ON IIf(IsNull(JA.a), 0, JA.a) = IIf(IsNull(JB.b), 0, JB.b)");
                Q(p, n, "SELECT Sum(IIf(IsNull(v), 0, v)) AS s FROM GA");
                Q(p, n, "SELECT id FROM GA ORDER BY IIf(IsNull(v), 0, v) DESC, id");
                string qd = QDef(p, n, "PARAMETERS p Long; SELECT x, p AS v, p + 1 AS w, IIf(IsNull(p), 7, p) AS z, (x = p) AS e, (p Is Null) AS n FROM CX;");
                QOpen(p, n, qd);
                foreach (Arg v in new[] { A.Null, A.Empty })
                {
                    SetP(p, n, qd, "p", v);
                    QOpen(p, n, qd);
                }
                QDone(p, qd);
            }, writable: true);

        string?[] lkRows = ["a", "b", "c", "A", "B", "ab", "abc", "abc ", " abc", "", null, AUmlaut + "bc", ((char)0xE6).ToString(), "ae", "1", "5", "Z", "z", "ss", ((char)0xDF).ToString()];
        yield return Rep("like-clean-operands", "r-sql-13b/like-null-pattern-edges and r-sql-13b/like-operand-types", ["R-SQL-1", "SQL-B", "SQL-KEY-02"],
            "pattern-like rows and an all-Null row spoiled two queries",
            "Asks what LIKE does with a pattern taken from the row, over LKC (no wildcard or bracket characters; case and accent variants, padded, empty and Null text): t LIKE t, t LIKE Left(t, 1) & '*' and their projected truth values; and CStr(dt) LIKE and Format(dt, 'yyyy') LIKE over BT with its Null row excluded by a derived table, the unfiltered form as control.",
            (p, n) =>
            {
                Run(p, "CREATE TABLE LKC (id LONG CONSTRAINT PK_LKC PRIMARY KEY, t TEXT(20))");
                for (int i = 0; i < lkRows.Length; i++)
                {
                    Run(p, lkRows[i] is string s ? $"INSERT INTO LKC (id, t) VALUES ({i + 1}, '{s}')" : $"INSERT INTO LKC (id) VALUES ({i + 1})");
                }
                Q(p, n, "SELECT id FROM LKC WHERE t LIKE t ORDER BY id", maxRows: 32);
                Q(p, n, "SELECT id FROM LKC WHERE t LIKE Left(t, 1) & '*' ORDER BY id", maxRows: 32);
                Q(p, n, "SELECT id, (t LIKE t) AS m1, (t LIKE Left(t, 1) & '*') AS m2 FROM LKC ORDER BY id", maxRows: 32);
                Run(p, "CREATE TABLE BT (id LONG CONSTRAINT PK_BT PRIMARY KEY, dt DATETIME)");
                foreach (string row in new[] { "1, #2024-01-01#", "2, #2024-01-05#", "3, #2024-01-10#", "4, Null", "5, #2024-01-07#", "6, #2023-12-31#", "7, #2024-01-05 12:00:00#" })
                {
                    Run(p, $"INSERT INTO BT (id, dt) VALUES ({row})");
                }
                Q(p, n, "SELECT id FROM BT WHERE CStr(dt) LIKE '*2024*' ORDER BY id");
                Q(p, n, "SELECT id FROM (SELECT id, dt FROM BT WHERE dt Is Not Null) WHERE CStr(dt) LIKE '*2024*' ORDER BY id");
                Q(p, n, "SELECT id FROM (SELECT id, dt FROM BT WHERE dt Is Not Null) WHERE Format(dt, 'yyyy') LIKE '2024' ORDER BY id");
            }, writable: true);

        string[] paramTypes = ["Byte", "Short", "Long", "Currency", "IEEESingle", "IEEEDouble", "Bit", "DateTime", "Text ( 5 )", "Text ( 20 )"];
        yield return Rep("params-values-rounding-and-length", "r-sql-13b/params-values-integers, params-values-fractional, params-values-other and params-value-readback", ["R-SQL-3", "SQL-KEY-01", "SQL-B"],
            "only 2.5 and in-length values were assigned",
            "Asks what a declared Byte, Short, Long, Currency, IEEESingle, IEEEDouble, Bit, DateTime, Text(5) and Text(20) parameter does when assigned 2.6, 3.5, -2.5, 255.5, 300, a 10-character text and a 26-character text: per assignment the Value is read back, then PARAMETERS pX <type>; SELECT pX AS v FROM One is opened and its row and VARIANT tag read; each type is a fresh QueryDef.",
            (p, n) =>
            {
                Arg[] values = [A.R8(2.6), A.R8(3.5), A.R8(-2.5), A.R8(255.5), A.I4(300), A.Str("abcdefghij"), A.Str("abcdefghijklmnopqrstuvwxyz")];
                foreach (string type in paramTypes)
                {
                    string qd = QDef(p, n, $"PARAMETERS pX {type}; SELECT pX AS v FROM One;");
                    foreach (Arg v in values)
                    {
                        SetP(p, n, qd, "pX", v);
                        QOpen(p, n, qd, maxRows: 4);
                    }
                    QDone(p, qd);
                }
            });
    }

    private static string NestSql(int tables)
    {
        string nest = "One AS t1";
        for (int i = 2; i <= tables; i++)
        {
            string join = $"{nest} INNER JOIN One AS t{i} ON t1.ID = t{i}.ID";
            nest = i == tables ? join : $"({join})";
        }
        return "SELECT t1.ID FROM " + nest;
    }

    // R-PAR-1 ----------------------------------------------------------------------------------------------------------

    private static IEnumerable<Probe> ParRepairs()
    {
        string[] decRefs = ["R-PAR-1", "PAR-FOLD", "SQL-AGG-TYPE"];
        yield return Rep("fold-decimal-28-digits", "r-par-1/fold-decimal-sum-groups and r-par-1/create-fold2", decRefs,
            "the 28-digit and small-scale witnesses were not stored",
            "Asks what Sum returns (Field type, size, Precision, Scale, value, or error and call) over columns that can hold the witnesses: FD0 (DECIMAL(28,0); 28 nines, 1, minus 28 nines; 28 nines and 1; 1, 1) and FD20 (DECIMAL(28,20); 1.00000000000000000001 and 1; 0.1, 0.2, 0.3; 0.5, 0.5), per group, over all rows and as GROUP BY g. The stored values are read first.",
            (p, n) =>
            {
                foreach ((string table, int groups) in new[] { ("FD0", 3), ("FD20", 3) })
                {
                    Qm(p, n, $"SELECT id, g, x FROM {table} ORDER BY id", maxRows: 16);
                    for (int g = 1; g <= groups; g++)
                    {
                        Qm(p, n, $"SELECT Sum(x) AS s FROM {table} WHERE g = {g}");
                    }
                    Qm(p, n, $"SELECT Sum(x) AS s FROM {table}");
                    Qm(p, n, $"SELECT g, Sum(x) AS s FROM {table} GROUP BY g");
                }
            }, fixture: FxPar);

        yield return Rep("fold-decimal-avg-var-forms", "r-par-1/fold-decimal-avg-var-forms", decRefs,
            "the CDec forms failed and the witnesses were not stored",
            "Asks what Avg, Var and StDev return over FD0 and FD20 per group, what Min, Max and Count give as GROUP BY g, and what Sum(x * 1), Sum(CCur(x)) and Sum(CDbl(x)) give per group, each its own statement. CDec is not used.",
            (p, n) =>
            {
                foreach (string table in new[] { "FD0", "FD20" })
                {
                    for (int g = 1; g <= 3; g++)
                    {
                        Qm(p, n, $"SELECT Avg(x) AS a FROM {table} WHERE g = {g}");
                        Qm(p, n, $"SELECT Var(x) AS v, StDev(x) AS sd FROM {table} WHERE g = {g}");
                        foreach (string f in new[] { "Sum(x * 1)", "Sum(CCur(x))", "Sum(CDbl(x))" })
                        {
                            Qm(p, n, $"SELECT {f} AS s FROM {table} WHERE g = {g}");
                        }
                    }
                    Qm(p, n, $"SELECT g, Min(x) AS mn, Max(x) AS mx, Count(x) AS n FROM {table} GROUP BY g");
                }
            }, fixture: FxPar);

        string[] makes =
        [
            "FOA|id", "FOB|id DESC", "FOK|k", "FOKD|k DESC", "FOD|d", "FODD|d DESC",
        ];
        yield return Rep("fold-double-routes-materialized", "r-par-1/fold-double-routes-sum-avg and r-par-1/fold-double-routes-var-minmax", ["R-PAR-1", "PAR-FOLD", "SQL-AGG-ORDER", "SQL-AGG-SINGLE"],
            "three-row routes did not separate feed orders",
            "Asks what exact Double bits Sum, Avg, Var, VarP, StDev, StDevP, Min and Max give over FO (2^53, 1, -2^53) when the rows reach the aggregate in a physically different order: six make-table copies of FO written in id, id DESC, k, k DESC, d and d DESC order, each read back in scan order and aggregated; and a saved query QFoDesc (SELECT d FROM FO ORDER BY d DESC) as a non-derived ordered source.",
            (p, n) =>
            {
                foreach (string make in makes)
                {
                    string[] parts = make.Split('|');
                    Act(p, $"SELECT id, k, d INTO {parts[0]} FROM FO ORDER BY {parts[1]}");
                    Q(p, n, $"SELECT id FROM {parts[0]}");
                    Qm(p, n, $"SELECT Sum(d) AS s, Avg(d) AS a FROM {parts[0]}");
                    Qm(p, n, $"SELECT Var(d) AS v, VarP(d) AS vp, StDev(d) AS sd, StDevP(d) AS sdp, Min(d) AS mn, Max(d) AS mx, Count(d) AS cn FROM {parts[0]}");
                }
                Mark(p);
                p.CallBind("qfo", "db", "CreateQueryDef", A.Str("QFoDesc"), A.Str("SELECT d FROM FO ORDER BY d DESC;")).Errors().Release("qfo");
                Qm(p, n, "SELECT Sum(d) AS s, Avg(d) AS a FROM QFoDesc");
                Qm(p, n, "SELECT Var(d) AS v, StDev(d) AS sd FROM QFoDesc");
            }, fixture: FxPar, writable: true);

        string[] four = ["OT5", "OT2", "OT3", "OT4"];
        yield return Rep("order-distinct-full-read", "r-par-1/order-distinct-without-order-by", ["R-PAR-1", "PAR-ORDER", "SQL-KEY-02"],
            "GetRows(100) read 100 of 256 rows",
            "Asks, without ORDER BY, in what order SELECT DISTINCT k, DISTINCT k, s, DISTINCTROW k, DISTINCT TOP 3 k and DISTINCT k ORDER BY k DESC return rows for OT5, OT2, OT3 and OT4, each read by one GetRows(300) (more than the 256 rows) with EOF read after it.",
            (p, n) =>
            {
                foreach (string t in four)
                {
                    foreach (string sql in new[]
                    {
                        $"SELECT DISTINCT k FROM {t}", $"SELECT DISTINCT k, s FROM {t}", $"SELECT DISTINCTROW k FROM {t}", $"SELECT DISTINCT TOP 3 k FROM {t}", $"SELECT DISTINCT k FROM {t} ORDER BY k DESC",
                    })
                    {
                        FetchAll(p, n, sql);
                    }
                }
            }, fixture: FxPar);

        yield return Rep("err-where-order-attributed", "r-par-1/err-where-evaluation-order", ["R-PAR-1", "PAR-ERROR", "SQL-DEMAND"],
            "Errors was not cleared between statements",
            "Asks which WHERE forms over ER raise the division error and at which call (open or GetRows(300)), and which rows come back: 1 / zm > 0; id < 50 AND 1 / zm > 0; 1 / zm > 0 AND id < 50; id < 50 OR 1 / zm > 0; id >= 150 AND 1 / zm > 0; id BETWEEN 1 AND 40 AND 1 / zm > 0; IIf(id < 50, True, 1 / zm > 0); and the select-list expression with id < 50. A marker precedes the open and the GetRows of each statement and Errors follows each.",
            (p, n) =>
            {
                foreach (string sql in new[]
                {
                    "SELECT id FROM ER WHERE 1 / zm > 0", "SELECT id FROM ER WHERE id < 50 AND 1 / zm > 0", "SELECT id FROM ER WHERE 1 / zm > 0 AND id < 50", "SELECT id FROM ER WHERE id < 50 OR 1 / zm > 0",
                    "SELECT id FROM ER WHERE id >= 150 AND 1 / zm > 0", "SELECT id FROM ER WHERE id BETWEEN 1 AND 40 AND 1 / zm > 0", "SELECT id FROM ER WHERE IIf(id < 50, True, 1 / zm > 0)",
                    "SELECT id, 1 / zm AS e FROM ER WHERE id < 50",
                })
                {
                    FetchAll(p, n, sql);
                }
            }, fixture: FxPar);

        yield return Rep("err-aggregate-attributed", "r-par-1/err-aggregate-and-groups", ["R-PAR-1", "PAR-ERROR", "SQL-DEMAND", "SQL-AGG-OCCUR"],
            "Errors was not cleared between statements",
            "Asks at which call an erroring argument surfaces inside an aggregate (zm = 0 at id 100, zl = 0 at id 240, zf = 0 at id 3): Sum, Avg, Count, Min, Max of 1 / zm; Sum of 1 / zl and 1 / zf; Count(*) beside Sum(1 / zm); Sum with WHERE id <> 100, id < 100, id <= 100; derived tables with TOP 10, TOP 150 ORDER BY id and WHERE id < 50; and the grouped forms.",
            (p, n) =>
            {
                Qs(p, n,
                [
                    "SELECT Sum(1 / zm) AS s FROM ER", "SELECT Avg(1 / zm) AS a FROM ER", "SELECT Count(1 / zm) AS c FROM ER", "SELECT Min(1 / zm) AS mn, Max(1 / zm) AS mx FROM ER",
                    "SELECT Sum(1 / zl) AS s FROM ER", "SELECT Sum(1 / zf) AS s FROM ER", "SELECT Count(*) AS c, Sum(1 / zm) AS s FROM ER", "SELECT Sum(1 / zm) AS s FROM ER WHERE id <> 100",
                    "SELECT Sum(1 / zm) AS s FROM ER WHERE id < 100", "SELECT Sum(1 / zm) AS s FROM ER WHERE id <= 100", "SELECT Sum(e) AS s FROM (SELECT TOP 10 1 / zm AS e FROM ER)",
                    "SELECT Sum(e) AS s FROM (SELECT TOP 150 1 / zm AS e FROM ER ORDER BY id)", "SELECT Sum(e) AS s FROM (SELECT 1 / zm AS e FROM ER WHERE id < 50)",
                    "SELECT id Mod 2 AS p, Sum(1 / zm) AS s FROM ER GROUP BY id Mod 2", "SELECT id \\ 100 AS b, Sum(1 / zm) AS s FROM ER GROUP BY id \\ 100",
                    "SELECT id \\ 100 AS b, Sum(1 / zm) AS s FROM ER GROUP BY id \\ 100 HAVING Sum(1 / zm) > 0", "SELECT CLng(tx) AS v, Count(*) AS n FROM ER GROUP BY CLng(tx)",
                    "SELECT DISTINCT 1 / zm AS e FROM ER",
                ], maxRows: 8);
            }, fixture: FxPar);

        yield return Rep("err-last-bad-row-bounded", "r-par-1/err-snapshot-last-bad-row", ["R-PAR-1", "PAR-ERROR", "SQL-DEMAND"],
            "the loop stepped past EOF",
            "Asks, for SELECT id, 1 / zl AS e FROM ER (zl = 0 at id 240, the last row) as a snapshot, which of OpenRecordset, EOF, MoveLast, RecordCount, the read of e at the last row or the MoveNext past it raises the error, with reads bounded to rows that exist: id and e after the open, after MoveLast, after MoveFirst, and rows 237 to 240 after Move(236), then EOF.",
            (p, n) =>
            {
                Mark(p);
                p.OpenRs("rs", "db", "SELECT id, 1 / zl AS e FROM ER", Dao.OpenSnapshot).Errors().Get("rs", "EOF");
                AtErr(p, "rs");
                Mark(p);
                p.Call("rs", "MoveLast").Errors();
                AtErr(p, "rs");
                Mark(p);
                p.Get("rs", "RecordCount").Errors().Call("rs", "MoveFirst");
                AtErr(p, "rs");
                Mark(p);
                p.Call("rs", "Move", A.I4(236)).Errors();
                for (int i = 0; i < 4; i++)
                {
                    AtErr(p, "rs");
                    Mark(p);
                    p.Call("rs", "MoveNext").Errors();
                }
                p.Get("rs", "EOF").Call("rs", "Close").Release("rs");
            }, fixture: FxPar);
    }

    /// <summary>One statement: marker, GetRows(300), Errors, EOF.</summary>
    private static void FetchAll(P p, Names n, string sql)
    {
        string rs = n.Next("rs");
        Mark(p);
        p.OpenRs(rs, "db", sql, Dao.OpenSnapshot).Errors();
        Mark(p);
        p.Call(rs, "GetRows", A.I4(300)).Errors().Get(rs, "EOF");
        p.Call(rs, "Close").Release(rs);
    }

    /// <summary>Marker, then read id and e of the current row of <paramref name="rs"/>, then Errors.</summary>
    private static void AtErr(P p, string rs)
    {
        string fl = rs + "_al", fi = rs + "_ai", fe = rs + "_ae";
        Mark(p);
        p.GetBind(fl, rs, "Fields").Item(fi, fl, A.Str("id")).Item(fe, fl, A.Str("e")).Get(fi, "Value").Get(fe, "Value")
            .Release(fe).Release(fi).Release(fl).Errors();
    }
}
