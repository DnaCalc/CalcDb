namespace CalcDb.Probes.Cards;

/// <summary>
/// Follow-up probes that settle, one deciding variant at a time, the branches of R-EXPR-1, R-SQL-1, R-SQL-2, R-SQL-3 and
/// R-PAR-1 that the retained records (r-expr-1, r-expr-1b, r-sql-1, r-sql-2-3, r-sql-13b, r-def-expr-sql, r-par-1,
/// r-fix-expr-sql) establish only in part (The Plan §8.1). Every probe asks; none asserts.
/// <para>
/// The first group is the Double fold order of PAR-FOLD / SQL-AGG-ORDER: FL group 3 (2^53, 4096 ones, -2^53 in insertion
/// order) summed to 0 while FLI (the same rows copied by INSERT SELECT ORDER BY id, indexes on g and d) summed to 1360.
/// Each probe reads, for one table, Sum for WHERE g = 1, 2 and 3 and as GROUP BY g, First/Last of id (the scan order the
/// aggregate saw), and the ids of the rows in default scan order (all of group 3 in one GetRows, and the first 40 of the
/// whole table), so a sequential Double sum can be recomputed offline from the retained scan order. The tables vary one
/// thing against the FL/FLI pair: FLN (copy, primary key only), FLX (copy, no index at all), FLR (copy inserted in
/// reverse id order), FLC (make-table copy), FLG (index on g only, built before the insert), FLD (index on d only, built
/// before the insert), FLI (both, the original recipe, as a control), and three post-load probes that create the index on
/// FL itself after the rows exist (g only, d only, both).
/// </para>
/// <para>
/// Attribution: DBEngine.Errors keeps the last failure until the next one, so each statement is preceded by a marker (a DROP
/// TABLE of a table that does not exist, which always fails); Errors read after the statement then shows that statement's own
/// failure or the marker's. No call can open a dialog: every database is a fixture copy or an A.Out file; no ODBC, no DSN, no
/// empty name. Requery is never called and no statement selects Array(...). Decimal columns are made through ADO DDL with
/// explicit precision and scale.
/// </para>
/// </summary>
public sealed class RQExprSql : ICard
{
    private const string Card = "r-q-expr-sql";
    private const string FxMain = Card + "-main";
    private const string FxFold = Card + "-fold";
    private const string Ace = "Microsoft.ACE.OLEDB.12.0";
    private const int Fail = Dao.FailOnError;
    private const string MarkerTable = "RqNoSuchMarker";
    private const double Two53 = 9007199254740992.0;

    /// <summary>U+00E6, built from its code so that no non-ASCII character is in the source.</summary>
    private static readonly string Ae = ((char)0xE6).ToString();

    private const string Tail =
        " Each statement is preceded by a marker (a DROP TABLE of a table that does not exist, which always fails), so DBEngine.Errors read after the statement shows that statement's own failure or the marker's, never an older one.";

    private sealed class Names
    {
        private int next;

        public string Next(string prefix) => prefix + next++;
    }

    public IEnumerable<Probe> Probes()
    {
        yield return CreateMain();
        yield return CreateFold();
        foreach (Probe pr in FoldProbes())
        {
            yield return pr;
        }
        foreach (Probe pr in ExprProbes())
        {
            yield return pr;
        }
        foreach (Probe pr in SqlProbes())
        {
            yield return pr;
        }
        foreach (Probe pr in ActionProbes())
        {
            yield return pr;
        }
    }

    // Factories ------------------------------------------------------------------------------------------------------

    private static Probe Rep(string name, string[] refs, string asks, Action<P, Names> body, string fixture = FxMain, bool writable = false) =>
        new(Card, name, refs,
            $"{asks} Follow-up probe (status F) for {string.Join(", ", refs.Skip(1))}; runs on {(writable ? "a writable fresh copy" : "a read-only open")} of fixture {fixture} through DAO.DBEngine.120; each recordset reads Name, Type and Value of every field.{Tail}",
            p =>
            {
                var n = new Names();
                p.EngineLite().Get("eng", "Version").OpenDb(readOnly: !writable).Get("db", "Version");
                body(p, n);
                p.FinishLite("db");
            })
        { Fixtures = [("main", fixture)] };

    private static Probe Fixture(string kind, string[] refs, string what, Action<P, Names, string> body)
    {
        string file = $"{Card}-{kind}.accdb";
        return new Probe(Card, "create-" + kind, refs,
            $"{Card} fixture {Card}-{kind}: {what} Created in ACCDB by DBEngine.CreateDatabase(path, dbLangGeneral, dbVersion120); DDL and set-based rows go through Database.Execute with dbFailOnError, typed values through table-type AddNew/Value/Update; Decimal columns are made through ADO DDL with explicit precision and scale; the closed file is kept.",
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

    private static void Mark(P p, string db = "db") => p.Call(db, "Execute", A.Str("DROP TABLE " + MarkerTable));

    private static void Run(P p, string sql, string db = "db") => p.Call(db, "Execute", A.Str(sql), A.I4(Fail));

    /// <summary>The statement under test: marker, Execute (with dbFailOnError unless <paramref name="flag"/> is false), RecordsAffected, Errors.</summary>
    private static void Act(P p, string sql, bool flag = true, string db = "db")
    {
        Mark(p, db);
        if (flag)
        {
            p.Call(db, "Execute", A.Str(sql), A.I4(Fail));
        }
        else
        {
            p.Call(db, "Execute", A.Str(sql));
        }
        p.Get(db, "RecordsAffected").Errors();
    }

    private static void Rows(P p, string rs, int maxRows)
    {
        string fl = rs + "f", f = rs + "x";
        p.Until(rs, "EOF", maxRows, b => b.GetBind(fl, rs, "Fields")
            .Each(fl, f, 64, x => x.Get(f, "Name").Get(f, "Type").Get(f, "Value"))
            .Release(fl).Call(rs, "MoveNext"));
    }

    private static void Schema(P p, string target)
    {
        p.GetBind("sf", target, "Fields").Get("sf", "Count")
            .Each("sf", "sfi", 64, b => b.Get("sfi", "Name").Get("sfi", "Type").Get("sfi", "Size"))
            .Release("sf");
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

    /// <summary>As <see cref="Q"/> but the schema (Count, Name, Type, Size) is read before the first move.</summary>
    private static void QSch(P p, Names n, string sql, int type = Dao.OpenSnapshot, int maxRows = 16)
    {
        string rs = n.Next("rs");
        Mark(p);
        p.OpenRs(rs, "db", sql, type).Errors();
        Schema(p, rs);
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

    /// <summary>One statement read by a single GetRows(<paramref name="rows"/>) (the whole array is retained), then EOF.</summary>
    private static void Gr(P p, Names n, string sql, int rows)
    {
        string rs = n.Next("gr");
        Mark(p);
        p.OpenRs(rs, "db", sql, Dao.OpenSnapshot).Errors().Call(rs, "GetRows", A.I4(rows)).Get(rs, "EOF").Call(rs, "Close").Release(rs).Errors();
    }

    private static void Peek(P p, Names n, string sql)
    {
        string rs = n.Next("pk");
        p.OpenRs(rs, "db", sql, Dao.OpenSnapshot);
        Rows(p, rs, 16);
        p.Call(rs, "Close").Release(rs);
    }

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

    /// <summary>Set a Field property of a table (TableDefs refreshed first), then read it back; marker and Errors around the put.</summary>
    private static void SetFieldProp(P p, Names n, string table, string field, string prop, string value)
    {
        string tds = n.Next("tds"), td = n.Next("td"), tf = n.Next("tf"), fv = n.Next("fv");
        p.GetBind(tds, "db", "TableDefs").Call(tds, "Refresh").Item(td, tds, A.Str(table)).GetBind(tf, td, "Fields").Item(fv, tf, A.Str(field));
        Mark(p);
        p.Put(fv, prop, A.Str(value)).Errors().Get(fv, prop);
        p.Release(fv).Release(tf).Release(td).Release(tds);
    }

    private static void SetTableRule(P p, Names n, string table, string rule)
    {
        string tds = n.Next("tds"), td = n.Next("td");
        p.GetBind(tds, "db", "TableDefs").Call(tds, "Refresh").Item(td, tds, A.Str(table));
        Mark(p);
        p.Put(td, "ValidationRule", A.Str(rule)).Errors().Get(td, "ValidationRule");
        p.Release(td).Release(tds);
    }

    /// <summary>A newly opened table recordset: AddNew, assign the given (field, value) pairs, Update, EditMode, close; Errors after the puts and after Update.</summary>
    private static void AddOne(P p, Names n, string table, params (string Field, Arg Value)[] values)
    {
        string rs = n.Next("ao"), fl = rs + "f";
        p.OpenRs(rs, "db", table, Dao.OpenTable).GetBind(fl, rs, "Fields").Call(rs, "AddNew");
        var items = new List<string>();
        foreach ((string field, Arg value) in values)
        {
            string fv = n.Next("av");
            items.Add(fv);
            p.Item(fv, fl, A.Str(field));
            Mark(p);
            p.Put(fv, "Value", value).Errors();
        }
        Mark(p);
        p.Call(rs, "Update").Errors().Get(rs, "EditMode");
        foreach (string fv in items)
        {
            p.Release(fv);
        }
        p.Release(fl).Call(rs, "Close").Release(rs);
    }

    private static string QDef(P p, Names n, string sql, string name = "")
    {
        string qd = n.Next("qd"), ps = qd + "p", px = qd + "x";
        Mark(p);
        p.CallBind(qd, "db", "CreateQueryDef", A.Str(name), A.Str(sql)).Errors().Get(qd, "SQL")
            .GetBind(ps, qd, "Parameters").Each(ps, px, 16, b => b.Get(px, "Name").Get(px, "Type"));
        return qd;
    }

    private static void SetP(P p, Names n, string qd, string name, Arg value)
    {
        string pv = n.Next("pv");
        Mark(p);
        p.Item(pv, qd + "p", A.Str(name)).Put(pv, "Value", value).Errors().Get(pv, "Value").Release(pv);
    }

    private static string QOpen(P p, Names n, string qd, int maxRows = 16, bool close = true)
    {
        string rs = n.Next("qrs");
        Mark(p);
        p.CallBind(rs, qd, "OpenRecordset", A.I4(Dao.OpenSnapshot)).Errors();
        Rows(p, rs, maxRows);
        if (close)
        {
            p.Call(rs, "Close").Release(rs).Errors();
        }
        return rs;
    }

    private static void QDone(P p, string qd) => p.Release(qd + "p").Call(qd, "Close").Release(qd);

    /// <summary>Close the DAO database, run the statements through ADO (ACE OLE DB provider), close ADO and reopen the DAO database read-write.</summary>
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

    // Fixtures -------------------------------------------------------------------------------------------------------

    private static Probe CreateMain() =>
        Fixture("main", ["R-EXPR-1", "R-SQL-1", "R-SQL-2", "R-SQL-3"],
            "One; Ev (Id, Denominator, TextValue: (1,5,'1'), (2,0,'x'), (3,2,'7')); Dts and DtsI (Date payloads -1.5, -1.25, -0.25, 0, 0.25, 1.25, 45000.5, DtsI with an index on D built before the insert); Nul (S = 'A', 'A'+U+0000, 'A'+U+0000+'B', 'A'+U+0000+'C'); Eq1, Eq2 and Eq3 ('ae' and U+00E6 inserted in both orders, Eq3 with an index on t); Ut (one row of every type for UNION arms); AgV (Double groups for variance); Bad (z = 0 at id 3); PvT and PvN (crosstab headings); Dd, DdI and DdN (domain lookup duplicates, with an index on g, and with no key); Dd2 (Currency with Nulls); and DT (a, b, c as Decimal(28,4), (28,20) and (28,28) made by ADO DDL).",
            (p, n, file) =>
            {
                Run(p, "CREATE TABLE One (ID LONG CONSTRAINT PK_One PRIMARY KEY)");
                Run(p, "INSERT INTO One (ID) VALUES (1)");
                Run(p, "CREATE TABLE Ev (Id LONG CONSTRAINT PK_Ev PRIMARY KEY, Denominator LONG, TextValue TEXT(10))");
                Run(p, "INSERT INTO Ev VALUES (1, 5, '1')");
                Run(p, "INSERT INTO Ev VALUES (2, 0, 'x')");
                Run(p, "INSERT INTO Ev VALUES (3, 2, '7')");

                double[] dts = [-1.5, -1.25, -0.25, 0, 0.25, 1.25, 45000.5];
                foreach (string t in new[] { "Dts", "DtsI" })
                {
                    Run(p, $"CREATE TABLE {t} (ID LONG CONSTRAINT PK_{t} PRIMARY KEY, D DATETIME)");
                    if (t == "DtsI")
                    {
                        Run(p, "CREATE INDEX IX_DtsI_D ON DtsI (D)");
                    }
                    AddRows(p, n, t, ["ID", "D"], dts.Select((d, i) => new[] { A.I4(i + 1), A.Date(d) }));
                }

                Run(p, "CREATE TABLE Nul (ID LONG CONSTRAINT PK_Nul PRIMARY KEY, S TEXT(20))");
                AddRows(p, n, "Nul", ["ID", "S"],
                [
                    [A.I4(1), A.Str("A")], [A.I4(2), A.BstrUnits("A\u0000")], [A.I4(3), A.BstrUnits("A\u0000B")], [A.I4(4), A.BstrUnits("A\u0000C")],
                ]);

                Run(p, "CREATE TABLE Eq1 (ID LONG CONSTRAINT PK_Eq1 PRIMARY KEY, t TEXT(20))");
                Run(p, "CREATE TABLE Eq2 (ID LONG CONSTRAINT PK_Eq2 PRIMARY KEY, t TEXT(20))");
                Run(p, "CREATE TABLE Eq3 (ID LONG CONSTRAINT PK_Eq3 PRIMARY KEY, t TEXT(20))");
                Run(p, "CREATE INDEX IX_Eq3_T ON Eq3 (t)");
                AddRows(p, n, "Eq1", ["ID", "t"], [[A.I4(1), A.Str("ae")], [A.I4(2), A.Str(Ae)], [A.I4(3), A.Str("AE")]]);
                AddRows(p, n, "Eq2", ["ID", "t"], [[A.I4(1), A.Str(Ae)], [A.I4(2), A.Str("ae")], [A.I4(3), A.Str("AE")]]);
                AddRows(p, n, "Eq3", ["ID", "t"], [[A.I4(1), A.Str("ae")], [A.I4(2), A.Str(Ae)], [A.I4(3), A.Str("AE")]]);

                Run(p, "CREATE TABLE Ut (ID LONG CONSTRAINT PK_Ut PRIMARY KEY, l LONG, d DOUBLE, dt DATETIME, c CURRENCY, t TEXT(20), y YESNO)");
                Run(p, "INSERT INTO Ut (ID, l, d, dt, c, t, y) VALUES (1, 7, 2.5, #2024-01-02 00:00:00#, 1.5, 'x', True)");

                Run(p, "CREATE TABLE AgV (id LONG CONSTRAINT PK_AgV PRIMARY KEY, g LONG, d DOUBLE)");
                (int G, double D)[] agv =
                [
                    (1, 1e9 + 4), (1, 1e9 + 7), (1, 1e9 + 13), (1, 1e9 + 16),
                    (2, 1e15 + 1), (2, 1e15 + 2), (2, 1e15 + 3),
                    (3, 1e8 + 0.1), (3, 1e8 + 0.2), (3, 1e8 + 0.3),
                    (4, 1e16), (4, 1e16 + 2), (4, 1e16 + 4),
                    (5, 1e150), (5, 2e150), (5, 3e150),
                    (7, 1e154), (7, 2e154), (7, 3e154),
                ];
                AddRows(p, n, "AgV", ["id", "g", "d"], agv.Select((r, i) => new[] { A.I4(i + 1), A.I4(r.G), A.R8(r.D) }));

                Run(p, "CREATE TABLE Bad (id LONG CONSTRAINT PK_Bad PRIMARY KEY, z LONG)");
                for (int i = 1; i <= 5; i++)
                {
                    Run(p, $"INSERT INTO Bad (id, z) VALUES ({i}, {(i == 3 ? 0 : 1)})");
                }

                Run(p, "CREATE TABLE PvT (ID LONG CONSTRAINT PK_PvT PRIMARY KEY, Grp TEXT(10), Piv TEXT(10), Amt LONG)");
                string[] pvt = ["1", "1.0", "01", " 1", "a", "A"];
                for (int i = 0; i < pvt.Length; i++)
                {
                    Run(p, $"INSERT INTO PvT (ID, Grp, Piv, Amt) VALUES ({i + 1}, 'g', '{pvt[i]}', {i + 1})");
                }
                Run(p, "CREATE TABLE PvN (ID LONG CONSTRAINT PK_PvN PRIMARY KEY, Grp TEXT(10), Piv DOUBLE, Amt LONG)");
                string[] pvn = ["1", "1.5", "10", "1.5", "1", "-1"];
                for (int i = 0; i < pvn.Length; i++)
                {
                    Run(p, $"INSERT INTO PvN (ID, Grp, Piv, Amt) VALUES ({i + 1}, 'g', {pvn[i]}, {i + 1})");
                }

                foreach (string t in new[] { "Dd", "DdI", "DdN" })
                {
                    Run(p, t == "DdN"
                        ? "CREATE TABLE DdN (id LONG, g LONG, v LONG)"
                        : $"CREATE TABLE {t} (id LONG CONSTRAINT PK_{t} PRIMARY KEY, g LONG, v LONG)");
                    if (t == "DdI")
                    {
                        Run(p, "CREATE INDEX IX_DdI_G ON DdI (g)");
                    }
                    foreach ((int id, int g, int v) in new[] { (3, 1, 30), (1, 1, 10), (2, 1, 20), (4, 2, 40), (5, 1, 50), (6, 2, 60) })
                    {
                        Run(p, $"INSERT INTO {t} (id, g, v) VALUES ({id}, {g}, {v})");
                    }
                }
                Run(p, "CREATE TABLE Dd2 (id LONG CONSTRAINT PK_Dd2 PRIMARY KEY, g LONG, v CURRENCY)");
                foreach (string row in new[] { "1, 1, Null", "2, 1, Null", "3, 2, 5", "4, 2, Null", "5, 3, 1.5", "6, 3, 2.5" })
                {
                    Run(p, $"INSERT INTO Dd2 (id, g, v) VALUES ({row})");
                }

                AdoDdl(p, file, "CREATE TABLE DT (id INTEGER CONSTRAINT PK_DT PRIMARY KEY, a DECIMAL(28,4), b DECIMAL(28,20), c DECIMAL(28,28))");
                Run(p, "INSERT INTO DT (id, a, b, c) VALUES (1, 1.5, 1.5, 0.0000000000000000000000000001)");
                Run(p, "INSERT INTO DT (id, a, b, c) VALUES (2, 0.0001, 0.0001, 0.0000000000000000000000000002)");
                Run(p, "INSERT INTO DT (id, a, b, c) VALUES (3, 2, 2, 0.5)");

                Peek(p, n, "SELECT id, a, b, c FROM DT ORDER BY id");
                Peek(p, n, "SELECT (SELECT Count(*) FROM Ev) AS ev, (SELECT Count(*) FROM Dts) AS dts, (SELECT Count(*) FROM AgV) AS agv, (SELECT Count(*) FROM Dd) AS dd FROM One");
            });

    private static Probe CreateFold() =>
        Fixture("fold", ["R-PAR-1", "PAR-FOLD", "SQL-AGG-ORDER"],
            "DG (0..15), NUM (1..4096 ascending), FL(id Counter PK, g, d) with g1 one 2^53 then 4096 ones, g2 4096 ones then one 2^53, g3 one 2^53, 4096 ones, -2^53 (inserted in that order); and copies of the same 12,293 rows made by INSERT SELECT ORDER BY id unless noted: FLI (PK, indexes on g and d created before the insert, the original recipe), FLG (PK, index on g only), FLD (PK, index on d only), FLN (PK only), FLX (no key, no index), FLR (PK only, inserted ORDER BY id DESC), FLC (make-table SELECT INTO ORDER BY id, no index).",
            (p, n, _) =>
            {
                Run(p, "CREATE TABLE DG (d LONG CONSTRAINT PK_DG PRIMARY KEY)");
                for (int i = 0; i < 16; i++)
                {
                    Run(p, $"INSERT INTO DG VALUES ({i})");
                }
                Run(p, "CREATE TABLE NUM (n LONG CONSTRAINT PK_NUM PRIMARY KEY)");
                Run(p, "INSERT INTO NUM (n) SELECT a.d * 256 + b.d * 16 + c.d + 1 FROM DG AS a, DG AS b, DG AS c ORDER BY a.d, b.d, c.d");
                Run(p, "CREATE TABLE FL (id COUNTER CONSTRAINT PK_FL PRIMARY KEY, g LONG, d DOUBLE)");
                Run(p, "INSERT INTO FL (g, d) VALUES (1, 9007199254740992)");
                Run(p, "INSERT INTO FL (g, d) SELECT 1, 1 FROM NUM ORDER BY n");
                Run(p, "INSERT INTO FL (g, d) SELECT 2, 1 FROM NUM ORDER BY n");
                Run(p, "INSERT INTO FL (g, d) VALUES (2, 9007199254740992)");
                Run(p, "INSERT INTO FL (g, d) VALUES (3, 9007199254740992)");
                Run(p, "INSERT INTO FL (g, d) SELECT 3, 1 FROM NUM ORDER BY n");
                Run(p, "INSERT INTO FL (g, d) VALUES (3, -9007199254740992)");
                (string Table, string Pk, string[] Indexes, string Order)[] copies =
                [
                    ("FLI", "id LONG CONSTRAINT PK_FLI PRIMARY KEY", ["CREATE INDEX IX_FLI_G ON FLI (g)", "CREATE INDEX IX_FLI_D ON FLI (d)"], "id"),
                    ("FLG", "id LONG CONSTRAINT PK_FLG PRIMARY KEY", ["CREATE INDEX IX_FLG_G ON FLG (g)"], "id"),
                    ("FLD", "id LONG CONSTRAINT PK_FLD PRIMARY KEY", ["CREATE INDEX IX_FLD_D ON FLD (d)"], "id"),
                    ("FLN", "id LONG CONSTRAINT PK_FLN PRIMARY KEY", [], "id"),
                    ("FLX", "id LONG", [], "id"),
                    ("FLR", "id LONG CONSTRAINT PK_FLR PRIMARY KEY", [], "id DESC"),
                ];
                foreach ((string table, string pk, string[] indexes, string order) in copies)
                {
                    Run(p, $"CREATE TABLE {table} ({pk}, g LONG, d DOUBLE)");
                    foreach (string ix in indexes)
                    {
                        Run(p, ix);
                    }
                    Run(p, $"INSERT INTO {table} (id, g, d) SELECT id, g, d FROM FL ORDER BY {order}");
                }
                Run(p, "SELECT id, g, d INTO FLC FROM FL ORDER BY id");
                foreach (string t in new[] { "FL", "FLI", "FLG", "FLD", "FLN", "FLX", "FLR", "FLC" })
                {
                    Peek(p, n, $"SELECT g, Count(*) AS n, Min(id) AS lo, Max(id) AS hi FROM {t} GROUP BY g");
                }
            });

    // PAR-FOLD / SQL-AGG-ORDER: the Double fold order ----------------------------------------------------------------

    private static void FoldReads(P p, Names n, string t)
    {
        Q(p, n, $"SELECT Sum(d) AS s FROM {t} WHERE g = 3");
        Q(p, n, $"SELECT Sum(d) AS s FROM {t} WHERE g = 1");
        Q(p, n, $"SELECT Sum(d) AS s FROM {t} WHERE g = 2");
        Q(p, n, $"SELECT g, Sum(d) AS s FROM {t} GROUP BY g");
        Q(p, n, $"SELECT First(id) AS fi, Last(id) AS la, First(d) AS fd, Last(d) AS ld FROM {t} WHERE g = 3");
        Q(p, n, $"SELECT g, First(id) AS fi, Last(id) AS la FROM {t} GROUP BY g");
        Gr(p, n, $"SELECT id FROM {t} WHERE g = 3", 5000);
        Gr(p, n, $"SELECT id FROM {t}", 40);
    }

    private static IEnumerable<Probe> FoldProbes()
    {
        (string Table, string Varies)[] tables =
        [
            ("FL", "the original FL: primary key only, rows in insertion order (control for FLI)"),
            ("FLI", "FLI: indexes on g and on d built before INSERT SELECT ORDER BY id (the original recipe; control that the 1360 repeats)"),
            ("FLG", "FLG: an index on g only, built before INSERT SELECT ORDER BY id (FLI without the d index)"),
            ("FLD", "FLD: an index on d only, built before INSERT SELECT ORDER BY id (FLI without the g index)"),
            ("FLN", "FLN: a copy by INSERT SELECT ORDER BY id with the primary key only (FLI without both secondary indexes)"),
            ("FLX", "FLX: a copy by INSERT SELECT ORDER BY id with no key and no index at all"),
            ("FLR", "FLR: a copy by INSERT SELECT ORDER BY id DESC (reverse insertion order), primary key only"),
            ("FLC", "FLC: a make-table copy (SELECT INTO ... ORDER BY id), no index"),
        ];
        foreach ((string t, string varies) in tables)
        {
            yield return Rep($"fold-scan-{t}", ["R-PAR-1", "PAR-FOLD", "SQL-AGG-ORDER", "SQL-AGG-FIRST"],
                $"Double fold order, one thing varied: table {varies}. Asks Sum(d) for WHERE g = 3, 1 and 2 and as GROUP BY g, First and Last of id and d in group 3 and per group, the ids of all group-3 rows in default scan order (one GetRows) and the first 40 ids of the whole table in scan order, so the sum a strictly sequential Double accumulation would give in the observed scan order can be recomputed offline and compared with the Sum ACE returned.",
                (p, n) => FoldReads(p, n, t), FxFold);
        }
        foreach ((string name, string[] indexes, string what) in new (string, string[], string)[]
        {
            ("g", ["CREATE INDEX IX_FL_G ON FL (g)"], "an index on g only, created on FL after the rows exist"),
            ("d", ["CREATE INDEX IX_FL_D ON FL (d)"], "an index on d only, created on FL after the rows exist"),
            ("both", ["CREATE INDEX IX_FL_G ON FL (g)", "CREATE INDEX IX_FL_D ON FL (d)"], "indexes on g and d, created on FL after the rows exist (built, not maintained during the insert)"),
        })
        {
            yield return Rep($"fold-post-{name}", ["R-PAR-1", "PAR-FOLD", "SQL-AGG-ORDER", "SQL-AGG-FIRST"],
                $"Double fold order, one thing varied: FL with {what}. Asks the same reads as the fold-scan probes (Sum for WHERE g = 3, 1, 2 and GROUP BY g, First/Last, the group-3 scan order and the first 40 ids) after the CREATE INDEX statements, so that an index built after load can be told from one maintained during load (FLI, FLG, FLD).",
                (p, n) =>
                {
                    foreach (string ix in indexes)
                    {
                        Run(p, ix);
                    }
                    FoldReads(p, n, "FL");
                }, FxFold, writable: true);
        }
    }

    // R-EXPR-1 ------------------------------------------------------------------------------------------------------

    private static IEnumerable<Probe> ExprProbes()
    {
        yield return Rep("a-optarg-null-empty-params", ["R-EXPR-1", "Branch A"],
            "Branch A, deciding variant: omission versus explicit Null and Empty for an optional argument, supplied through a declared Long parameter p. Asks Round(2.5, p), Mid('abcd', 2, p), Left('abcd', p) and InStr(p, 'abcd', 'c') with p = Null, Empty and 0 (each on a fresh temporary QueryDef, p assigned and read back, opened as a snapshot), next to the omitted forms Round(2.5), Mid('abcd', 2) and InStr('abcd', 'c').",
            (p, n) =>
            {
                string[] exprs = ["Round(2.5, p)", "Mid('abcd', 2, p)", "Left('abcd', p)", "InStr(p, 'abcd', 'c')"];
                (string Label, Arg Value)[] inputs = [("null", A.Null), ("empty", A.Empty), ("zero", A.I4(0))];
                foreach (string e in exprs)
                {
                    foreach ((_, Arg value) in inputs)
                    {
                        string qd = QDef(p, n, $"PARAMETERS p Long; SELECT {e} AS v FROM One;");
                        SetP(p, n, qd, "p", value);
                        QOpen(p, n, qd);
                        QDone(p, qd);
                    }
                }
                Qs(p, n, ["SELECT Round(2.5) AS v FROM One", "SELECT Mid('abcd', 2) AS v FROM One", "SELECT InStr('abcd', 'c') AS v FROM One"]);
            });

        yield return Rep("dec-scale-compare", ["R-EXPR-1", "Branch B", "SQL-EQ-02"],
            "Branch B Decimal fidelity, deciding variants: equal values of different scale and scale-28 low values. DT holds a Decimal(28,4), b Decimal(28,20) and c Decimal(28,28) made through ADO DDL. Asks the stored values and VarType, a = b, a + b, a * b, a - b, c, c + c, c * 1, CStr(c), CDbl(c), WHERE a = b, WHERE c > 0, Sum(c) and Sum(a) (Field.Type, Size and Value of each result).",
            (p, n) => Qs(p, n,
            [
                "SELECT id, a, b, c, VarType(a) AS va, VarType(b) AS vb, VarType(c) AS vc FROM DT",
                "SELECT id, a = b AS eq, a + b AS s, a * b AS m, a - b AS d FROM DT",
                "SELECT id, c, c + c AS cc, c * 1 AS c1, CStr(c) AS cs, CDbl(c) AS cd FROM DT",
                "SELECT id FROM DT WHERE a = b",
                "SELECT id FROM DT WHERE c > 0",
                "SELECT id FROM DT WHERE c = 0.5",
                "SELECT Sum(c) AS sc, Sum(a) AS sa FROM DT",
            ]));

        yield return Rep("c-date-index-vs-residual", ["R-EXPR-1", "Branch C"],
            "Branch C, deciding variant: an indexed comparison against a residual (expression-only) comparison of negative DATE payloads. Dts has no index on D and DtsI has IX_DtsI_D (both built from the same seven payloads -1.5 to 45000.5). Asks, for each table, D = CDate(-0.25), D = #12/30/1899 06:00#, D BETWEEN #12/29/1899# AND #12/31/1899#, D < 0, D > -1.3, CDbl(D) = -0.25 (residual only), and ORDER BY D, listing the selected ids in order.",
            (p, n) =>
            {
                foreach (string t in new[] { "Dts", "DtsI" })
                {
                    Qs(p, n,
                    [
                        $"SELECT ID FROM {t} WHERE D = CDate(-0.25)",
                        $"SELECT ID FROM {t} WHERE D = #12/30/1899 06:00#",
                        $"SELECT ID FROM {t} WHERE D BETWEEN #12/29/1899# AND #12/31/1899# ORDER BY ID",
                        $"SELECT ID FROM {t} WHERE D < 0 ORDER BY ID",
                        $"SELECT ID FROM {t} WHERE D > -1.3 ORDER BY ID",
                        $"SELECT ID FROM {t} WHERE CDbl(D) = -0.25",
                        $"SELECT ID, D FROM {t} ORDER BY D",
                    ]);
                }
            });

        yield return Rep("d-validation-andor", ["R-EXPR-1", "Branch D", "EXPR-MUTATION"],
            "Branch D, deciding variant: And/Or results as a validation consumer. Tables Va0 (rule [V] > 0 And [V] < 10), Va1 (rule [V] > 0 Or [V] < -5) and Va2 (rule ([V] > 0) And Null) each get V = 5, -1, 15, -7 and Null on a newly opened table recordset (AddNew, put, Update); Errors after each put and each Update and EditMode after Update. Required and AllowZeroLength are left at their defaults.",
            (p, n) =>
            {
                string[] rules = ["[V] > 0 And [V] < 10", "[V] > 0 Or [V] < -5", "([V] > 0) And Null"];
                for (int i = 0; i < rules.Length; i++)
                {
                    string t = $"Va{i}";
                    Run(p, $"CREATE TABLE {t} (ID COUNTER CONSTRAINT PK_{t} PRIMARY KEY, V LONG)");
                    SetFieldProp(p, n, t, "V", "ValidationRule", rules[i]);
                    foreach (Arg v in new[] { A.I4(5), A.I4(-1), A.I4(15), A.I4(-7), A.Null })
                    {
                        AddOne(p, n, t, ("V", v));
                    }
                    Q(p, n, $"SELECT ID, V FROM {t} ORDER BY ID");
                }
            }, writable: true);

        yield return Rep("mut-declaration-order", ["R-EXPR-1", "EXPR-MUTATION", "EXPR-STAGE"],
            "EXPR-MUTATION, deciding variant: error precedence of two invalid fields when declaration order and assignment order are swapped independently. MA declares A then B and MB declares B then A (each field has rule > 0 and its own ValidationText); on a newly opened recordset each table gets A = -1 and B = -1 assigned in both orders and Update; Errors after each put and each Update.",
            (p, n) =>
            {
                foreach ((string t, string cols) in new[] { ("MA", "A LONG, B LONG"), ("MB", "B LONG, A LONG") })
                {
                    Run(p, $"CREATE TABLE {t} (ID COUNTER CONSTRAINT PK_{t} PRIMARY KEY, {cols})");
                    foreach (string f in new[] { "A", "B" })
                    {
                        SetFieldProp(p, n, t, f, "ValidationRule", $"[{f}] > 0");
                        SetFieldProp(p, n, t, f, "ValidationText", $"{f} must be positive");
                    }
                    AddOne(p, n, t, ("A", A.I4(-1)), ("B", A.I4(-1)));
                    AddOne(p, n, t, ("B", A.I4(-1)), ("A", A.I4(-1)));
                }
            }, writable: true);

        yield return Rep("mut-field-rule-other-field", ["R-EXPR-1", "EXPR-MUTATION"],
            "EXPR-MUTATION, deciding variant: a field-level rule that names another field, against the same rule at table level. MO(ID, V, R): the V field rule [V] < [R] is set (the put is attributed), (V, R) = (2, 1), (1, 2) and (1, Null) are added; then the field rule is cleared, TableDef.ValidationRule [V] < [R] is set and the same three rows are tried.",
            (p, n) =>
            {
                Run(p, "CREATE TABLE MO (ID COUNTER CONSTRAINT PK_MO PRIMARY KEY, V LONG, R LONG)");
                SetFieldProp(p, n, "MO", "V", "ValidationRule", "[V] < [R]");
                foreach ((int v, Arg r) in new[] { (2, A.I4(1)), (1, A.I4(2)), (1, A.Null) })
                {
                    AddOne(p, n, "MO", ("V", A.I4(v)), ("R", r));
                }
                SetFieldProp(p, n, "MO", "V", "ValidationRule", "");
                SetTableRule(p, n, "MO", "[V] < [R]");
                foreach ((int v, Arg r) in new[] { (2, A.I4(1)), (1, A.I4(2)), (1, A.Null) })
                {
                    AddOne(p, n, "MO", ("V", A.I4(v)), ("R", r));
                }
                Q(p, n, "SELECT ID, V, R FROM MO ORDER BY ID");
            }, writable: true);

        yield return Rep("mut-default-now-rnd", ["R-EXPR-1", "EXPR-MUTATION", "EXPR-RUNTIME"],
            "EXPR-MUTATION and EXPR-RUNTIME, deciding variant: when a clock or random default is sampled. MD(ID, T DATETIME default Now(), R DOUBLE default Rnd()): AddNew, read T and R, wait 2.1 s, read them again, put ID = 1 and Update; then AddNew a second row immediately, read T and R, CancelUpdate, AddNew again, read, put ID = 3 and Update; the stored rows are read last.",
            (p, n) =>
            {
                Run(p, "CREATE TABLE MD (ID LONG CONSTRAINT PK_MD PRIMARY KEY, T DATETIME, R DOUBLE)");
                SetFieldProp(p, n, "MD", "T", "DefaultValue", "Now()");
                SetFieldProp(p, n, "MD", "R", "DefaultValue", "Rnd()");
                p.OpenRs("mrs", "db", "MD", Dao.OpenTable).GetBind("mfl", "mrs", "Fields")
                    .Item("mi", "mfl", A.Str("ID")).Item("mt", "mfl", A.Str("T")).Item("mr", "mfl", A.Str("R"));
                p.Call("mrs", "AddNew").Get("mt", "Value").Get("mr", "Value").Pause(2100).Get("mt", "Value").Get("mr", "Value")
                    .Put("mi", "Value", A.I4(1));
                Mark(p);
                p.Call("mrs", "Update").Errors().Get("mt", "Value").Get("mr", "Value");
                p.Call("mrs", "AddNew").Get("mt", "Value").Get("mr", "Value");
                Mark(p);
                p.Call("mrs", "CancelUpdate").Errors();
                p.Call("mrs", "AddNew").Get("mt", "Value").Get("mr", "Value").Put("mi", "Value", A.I4(3));
                Mark(p);
                p.Call("mrs", "Update").Errors();
                p.Release("mr").Release("mt").Release("mi").Release("mfl").Call("mrs", "Close").Release("mrs");
                Q(p, n, "SELECT ID, T, R FROM MD ORDER BY ID");
            }, writable: true);

        yield return Rep("e-row-arms", ["R-EXPR-1", "Branch E", "EXPR-STAGE"],
            "Branch E, deciding variants: row-dependent arms of Switch and Choose against IIf, arm-order reversal, and a zero-row filter. Ev has (Id, Denominator, TextValue) = (1,5,'1'), (2,0,'x'), (3,2,'7'). Asks Switch and Choose with a selected arm and an unselected arm 1/[Denominator], IIf with row-dependent arms, two distinct failures (1/[Denominator], CInt([TextValue])) in both arm orders inside Switch, Choose, IIf and a plain sum, each as SELECT Id, expr FROM Ev ORDER BY Id with every Value read attempted, and three of them with WHERE Id = 999 (open, no rows).",
            (p, n) =>
            {
                string[] exprs =
                [
                    "IIf(Id = 1, 1, 1 / [Denominator])",
                    "Switch(Id = 1, 1, Id = 2, 1 / [Denominator])",
                    "Switch(True, 1, True, 1 / [Denominator])",
                    "Choose(1, 'a', 1 / [Denominator])",
                    "Choose(Id, 'a', 1 / [Denominator], 'c')",
                    "IIf(Id = 2, 1 / [Denominator], CInt([TextValue]))",
                    "IIf(Id = 2, CInt([TextValue]), 1 / [Denominator])",
                    "Switch(Id = 2, CInt([TextValue]), Id = 2, 1 / [Denominator])",
                    "Switch(Id = 2, 1 / [Denominator], Id = 2, CInt([TextValue]))",
                    "Choose(1, CInt([TextValue]), 1 / [Denominator])",
                    "Choose(1, 1 / [Denominator], CInt([TextValue]))",
                    "CInt([TextValue]) + 1 / [Denominator]",
                    "1 / [Denominator] + CInt([TextValue])",
                ];
                foreach (string e in exprs)
                {
                    Q(p, n, $"SELECT Id, {e} AS v FROM Ev ORDER BY Id", maxRows: 8);
                }
                foreach (string e in new[] { exprs[2], exprs[3], exprs[8] })
                {
                    Q(p, n, $"SELECT Id, {e} AS v FROM Ev WHERE Id = 999");
                }
            });

        yield return Rep("bind-param-fresh-open", ["R-EXPR-1", "EXPR-BIND"],
            "EXPR-BIND, deciding variants: parameter change, held recordset and SQL replacement, with no Requery. Saved QBp (PARAMETERS pl Long; SELECT pl + 1 AS v FROM One) is opened with pl = 1 (rs1 kept open), pl set to 2 and opened again (rs2), rs1 moved to its first row and read again, then QBp.SQL is replaced by a text adding 10, QBp is opened again (rs3) and rs1 and rs2 are read again.",
            (p, n) =>
            {
                Mark(p);
                p.CallBind("qbp", "db", "CreateQueryDef", A.Str("QBp"), A.Str("PARAMETERS pl Long; SELECT pl + 1 AS v FROM One;")).Errors()
                    .GetBind("qbpp", "qbp", "Parameters").Item("qbpl", "qbpp", A.Str("pl"));
                p.Put("qbpl", "Value", A.I4(1));
                string rs1 = QOpen(p, n, "qbp", close: false);
                p.Put("qbpl", "Value", A.I4(2));
                string rs2 = QOpen(p, n, "qbp", close: false);
                p.Call(rs1, "MoveFirst");
                Rows(p, rs1, 4);
                Mark(p);
                p.Put("qbp", "SQL", A.Str("PARAMETERS pl Long; SELECT pl + 10 AS v FROM One;")).Errors().Get("qbp", "SQL");
                string rs3 = QOpen(p, n, "qbp", close: false);
                p.Call(rs1, "MoveFirst");
                Rows(p, rs1, 4);
                p.Call(rs2, "MoveFirst");
                Rows(p, rs2, 4);
                foreach (string rs in new[] { rs1, rs2, rs3 })
                {
                    p.Call(rs, "Close").Release(rs);
                }
                p.Release("qbpl").Release("qbpp").Call("qbp", "Close").Release("qbp");
            }, writable: true);

        yield return Rep("bind-schema-change", ["R-EXPR-1", "EXPR-BIND"],
            "EXPR-BIND, deciding variants: schema change under saved queries, with no Requery. Sc(ID, A) holds two rows; saved QStar (SELECT * FROM Sc) and QCols (SELECT ID, A FROM Sc) are opened and read; ALTER TABLE Sc ADD COLUMN X is run while a recordset of QStar is still open and again after closing it; QStar and QCols are opened and Fields read; ALTER TABLE Sc DROP COLUMN A is run and both are opened again; Sc is dropped and recreated with A as Text and both are opened again.",
            (p, n) =>
            {
                Run(p, "CREATE TABLE Sc (ID LONG CONSTRAINT PK_Sc PRIMARY KEY, A LONG)");
                Run(p, "INSERT INTO Sc VALUES (1, 10)");
                Run(p, "INSERT INTO Sc VALUES (2, 20)");
                foreach ((string name, string sql) in new[] { ("QStar", "SELECT * FROM Sc;"), ("QCols", "SELECT ID, A FROM Sc;") })
                {
                    Mark(p);
                    p.CallBind("q" + name, "db", "CreateQueryDef", A.Str(name), A.Str(sql)).Errors().Release("q" + name);
                }
                void OpenBoth()
                {
                    p.GetBind("qds", "db", "QueryDefs").Call("qds", "Refresh");
                    foreach (string name in new[] { "QStar", "QCols" })
                    {
                        string rs = n.Next("sq");
                        Mark(p);
                        p.Item("qd", "qds", A.Str(name)).CallBind(rs, "qd", "OpenRecordset", A.I4(Dao.OpenSnapshot)).Errors();
                        Schema(p, rs);
                        Rows(p, rs, 4);
                        p.Call(rs, "Close").Release(rs).Release("qd");
                    }
                    p.Release("qds");
                }
                OpenBoth();
                p.OpenRs("held", "db", "SELECT * FROM Sc", Dao.OpenSnapshot);
                Act(p, "ALTER TABLE Sc ADD COLUMN X LONG");
                p.Call("held", "Close").Release("held");
                Act(p, "ALTER TABLE Sc ADD COLUMN X LONG");
                OpenBoth();
                Act(p, "ALTER TABLE Sc DROP COLUMN A");
                OpenBoth();
                Act(p, "DROP TABLE Sc");
                Run(p, "CREATE TABLE Sc (ID LONG CONSTRAINT PK_Sc2 PRIMARY KEY, A TEXT(10))");
                Run(p, "INSERT INTO Sc VALUES (1, 'q')");
                OpenBoth();
            }, writable: true);

        yield return Rep("pat-11-16-counted-nul", ["R-EXPR-1", "PAT-11", "PAT-16", "EXPR-BIND"],
            "PAT-11 and PAT-16, deciding variants: counted U+0000 in subject and pattern, and pattern reuse after the parameter changes, with no Requery. Nul holds S = 'A', 'A'+NUL, 'A'+NUL+'B', 'A'+NUL+'C'. Asks, for pattern values 'A', 'A'+NUL, 'A'+NUL+'B', 'A'+NUL+'C', 'A'+NUL+'*' (each on a fresh temporary QueryDef PARAMETERS pp Text(255): Len(S), S = pp and S LIKE pp per row); then one QueryDef whose pp is assigned 'A'+NUL+'B', then 'A'+NUL+'C', then 'A' with a newly opened recordset after each assignment.",
            (p, n) =>
            {
                const string Sql = "PARAMETERS pp Text(255); SELECT ID, Len(S) AS l, S = pp AS e, S LIKE pp AS lk FROM Nul ORDER BY ID;";
                string[] values = ["A", "A\u0000", "A\u0000B", "A\u0000C", "A\u0000*"];
                foreach (string v in values)
                {
                    string qd = QDef(p, n, Sql);
                    SetP(p, n, qd, "pp", A.BstrUnits(v));
                    QOpen(p, n, qd);
                    QDone(p, qd);
                }
                string one = QDef(p, n, Sql);
                foreach (string v in new[] { "A\u0000B", "A\u0000C", "A" })
                {
                    SetP(p, n, one, "pp", A.BstrUnits(v));
                    QOpen(p, n, one);
                }
                QDone(p, one);
            });

        yield return Rep("domain-lookup-order", ["R-EXPR-1", "EXPR-DOMAIN"],
            "EXPR-DOMAIN, deciding variant: which duplicate match DLookup, DFirst and DLast return, by key layout. Dd (primary key on id), DdI (primary key and an index on g) and DdN (no key, no index) hold rows (id, g, v) = (3,1,30), (1,1,10), (2,1,20), (4,2,40), (5,1,50), (6,2,60) in that insertion order. Asks DLookup, DFirst, DLast, DMin and DMax of v where g = 1, DLookup without criteria, and the plain scan order of g = 1 rows, for each table.",
            (p, n) =>
            {
                foreach (string t in new[] { "Dd", "DdI", "DdN" })
                {
                    Q(p, n, $"SELECT DLookup('v', '{t}', 'g = 1') AS lk, DFirst('v', '{t}', 'g = 1') AS fi, DLast('v', '{t}', 'g = 1') AS la, DMin('v', '{t}', 'g = 1') AS mn, DMax('v', '{t}', 'g = 1') AS mx, DLookup('v', '{t}') AS nc FROM One");
                    Q(p, n, $"SELECT id, v FROM {t} WHERE g = 1");
                    Q(p, n, $"SELECT id, v FROM {t}");
                }
            });

        yield return Rep("domain-dsum-null-empty", ["R-EXPR-1", "EXPR-DOMAIN"],
            "EXPR-DOMAIN, deciding variants: all-Null input against no rows, expression errors and an absent criteria field in DSum. Dd2(id, g, v Currency) has g = 1 all Null, g = 2 one value and Nulls, g = 3 two values. Asks DSum, DCount and DAvg of v for g = 1, 2, 3 and 99, DSum without criteria, DSum('v * 2', ...), DSum('1/0', ...), DLookup with the absent criteria field nosuch = 1 and with an empty criteria string, each as its own SELECT ... FROM One.",
            (p, n) =>
            {
                string[] exprs =
                [
                    "DSum('v', 'Dd2', 'g = 1')", "DSum('v', 'Dd2', 'g = 2')", "DSum('v', 'Dd2', 'g = 3')", "DSum('v', 'Dd2', 'g = 99')", "DSum('v', 'Dd2')",
                    "DCount('v', 'Dd2', 'g = 1')", "DCount('v', 'Dd2', 'g = 99')", "DAvg('v', 'Dd2', 'g = 1')", "DAvg('v', 'Dd2', 'g = 2')",
                    "DSum('v * 2', 'Dd2', 'g = 3')", "DSum('1 / 0', 'Dd2', 'g = 3')", "DLookup('v', 'Dd2', 'nosuch = 1')", "DLookup('v', 'Dd2', '')",
                ];
                foreach (string e in exprs)
                {
                    Q(p, n, $"SELECT {e} AS v FROM One");
                }
            });

        yield return Rep("domain-txn-visibility", ["R-EXPR-1", "EXPR-DOMAIN"],
            "EXPR-DOMAIN, deciding variants: what a domain function sees of an uncommitted row, an edit buffer and a rolled-back row inside one Workspace. Dtx(id) is created; DCount('*', 'Dtx') is read at baseline; BeginTrans on Workspaces(0), INSERT id 1, DCount; Rollback, DCount; BeginTrans, INSERT id 2, CommitTrans, DCount; a dynaset AddNew with id = 50 is read through DCount before and after its Update.",
            (p, n) =>
            {
                Run(p, "CREATE TABLE Dtx (id LONG CONSTRAINT PK_Dtx PRIMARY KEY)");
                void Count() => Q(p, n, "SELECT DCount('*', 'Dtx') AS c FROM One");
                Count();
                p.GetBind("wss", "eng", "Workspaces").Item("ws", "wss", A.I4(0)).Call("ws", "BeginTrans");
                Run(p, "INSERT INTO Dtx (id) VALUES (1)");
                Count();
                p.Call("ws", "Rollback");
                Count();
                p.Call("ws", "BeginTrans");
                Run(p, "INSERT INTO Dtx (id) VALUES (2)");
                p.Call("ws", "CommitTrans");
                Count();
                p.OpenRs("drs", "db", "SELECT id FROM Dtx", Dao.OpenDynaset).GetBind("dfl", "drs", "Fields").Item("did", "dfl", A.Str("id"));
                p.Call("drs", "AddNew").Put("did", "Value", A.I4(50));
                Count();
                Mark(p);
                p.Call("drs", "Update").Errors();
                Count();
                p.Release("did").Release("dfl").Call("drs", "Close").Release("drs").Release("ws").Release("wss");
            }, writable: true);
    }

    // R-SQL-1 / R-SQL-3 ----------------------------------------------------------------------------------------------

    private static IEnumerable<Probe> SqlProbes()
    {
        yield return Rep("sqlb-limit-chains", ["R-SQL-1", "SQL-B"],
            "SQL-B, deciding variants: bracket the AND-chain and parenthesis-depth limits one dimension at a time. Opens SELECT ID FROM One WHERE ID = 1 AND ... with 60, 70, 80, 90 and 97 conjuncts (98 gave 3072 and 100 gave 3360 earlier), and WHERE ((...ID = 1...)) with depth 20, 24, 28 and 31 (16 opened, 32 gave 3075 earlier); each is an OpenRecordset with its rows read.",
            (p, n) =>
            {
                foreach (int k in new[] { 60, 70, 80, 90, 97 })
                {
                    Q(p, n, "SELECT ID FROM One WHERE " + string.Join(" AND ", Enumerable.Repeat("ID = 1", k)));
                }
                foreach (int d in new[] { 20, 24, 28, 31 })
                {
                    Q(p, n, "SELECT ID FROM One WHERE " + new string('(', d) + "ID = 1" + new string(')', d));
                }
            });

        yield return Rep("sqlb-limit-length", ["R-SQL-1", "SQL-B"],
            "SQL-B, deciding variant: bracket the statement-length limit (64001 opened, 65535 gave 3035 earlier). Opens SELECT ID FROM One padded with trailing spaces to 64500, 65000 and 65534 UTF-16 units through OpenRecordset, and creates a temporary QueryDef of 65534 units.",
            (p, n) =>
            {
                foreach (int len in new[] { 64500, 65000, 65534 })
                {
                    Q(p, n, "SELECT ID FROM One" + new string(' ', len - 18));
                }
                string qd = QDef(p, n, "SELECT ID FROM One" + new string(' ', 65534 - 18));
                QDone(p, qd);
            });

        yield return Rep("eq-repr-order", ["R-SQL-1", "SQL-EQ-03", "SQL-EQ-01"],
            "SQL-EQ-03, deciding variants: which spelling represents a group of values that compare equal, with insertion order and an index changed one at a time. Eq1 inserts 'ae', U+00E6, 'AE'; Eq2 inserts U+00E6, 'ae', 'AE'; Eq3 is Eq1 with an index on t. Asks SELECT DISTINCT t, GROUP BY t with Count, Min/Max/First/Last of t, ORDER BY t and UNION of the table with itself.",
            (p, n) =>
            {
                foreach (string t in new[] { "Eq1", "Eq2", "Eq3" })
                {
                    Qs(p, n,
                    [
                        $"SELECT DISTINCT t FROM {t}",
                        $"SELECT t, Count(*) AS n FROM {t} GROUP BY t",
                        $"SELECT Min(t) AS mn, Max(t) AS mx, First(t) AS f, Last(t) AS l FROM {t}",
                        $"SELECT ID, t FROM {t} ORDER BY t",
                        $"SELECT t FROM {t} UNION SELECT t FROM {t}",
                    ]);
                }
            });

        yield return Rep("union-null-first", ["R-SQL-1", "SQL-UNION-02", "SQL-UNION-01"],
            "SQL-UNION-02, deciding variant: a Null literal first arm against each column type (a Null-first Long arm returned Text holding raw bytes). For each column of Ut (Long l, Double d, Date dt, Currency c, Text t, Yes/No y) asks SELECT Null AS v FROM One UNION SELECT col FROM Ut, the same with UNION ALL, and the reversed arm order, with the schema (Name, Type, Size) read before the first move and every value.",
            (p, n) =>
            {
                foreach (string col in new[] { "l", "d", "dt", "c", "t", "y" })
                {
                    QSch(p, n, $"SELECT Null AS v FROM One UNION SELECT {col} FROM Ut");
                    QSch(p, n, $"SELECT Null AS v FROM One UNION ALL SELECT {col} FROM Ut");
                    QSch(p, n, $"SELECT {col} AS v FROM Ut UNION SELECT Null FROM One");
                }
            });

        yield return Rep("sub-corr-shapes", ["R-SQL-1", "SQL-SUB-03", "SQL-SUB-04"],
            "SQL-SUB-03 and SQL-SUB-04, deciding variants: correlation name precedence and the demand of EXISTS shapes. Over Ev asks a correlated count through an alias, through a shadowing same-named table, three levels of nesting, and EXISTS with DISTINCT, TOP 1, GROUP BY and with a failing projection 1/x.Denominator (row Id 2 fails), each as its own statement.",
            (p, n) => Qs(p, n,
            [
                "SELECT Id, (SELECT Count(*) FROM Ev AS e2 WHERE e2.Id <= Ev.Id) AS c FROM Ev",
                "SELECT Id, (SELECT Count(*) FROM Ev WHERE Id <= Ev.Id) AS c FROM Ev",
                "SELECT Id FROM Ev AS a WHERE EXISTS (SELECT 1 FROM Ev AS b WHERE b.Id = a.Id AND EXISTS (SELECT 1 FROM Ev AS c WHERE c.Id = a.Id))",
                "SELECT Id FROM Ev WHERE EXISTS (SELECT DISTINCT x.Id FROM Ev AS x WHERE x.Id = Ev.Id)",
                "SELECT Id FROM Ev WHERE EXISTS (SELECT TOP 1 x.Id FROM Ev AS x WHERE x.Id = Ev.Id)",
                "SELECT Id FROM Ev WHERE EXISTS (SELECT x.Id FROM Ev AS x WHERE x.Id = Ev.Id GROUP BY x.Id)",
                "SELECT Id FROM Ev WHERE EXISTS (SELECT 1 / x.Denominator FROM Ev AS x WHERE x.Id = Ev.Id)",
            ]));

        yield return Rep("agg-var-recurrence", ["R-SQL-1", "SQL-AGG-NUMERIC", "SQL-AGG-SINGLE"],
            "SQL-AGG-NUMERIC, deciding variants: large offset, spread and magnitude. AgV groups: g1 1e9 + (4, 7, 13, 16) (exact Var 30), g2 1e15 + (1, 2, 3) (Var 1), g3 1e8 + (0.1, 0.2, 0.3), g4 1e16 + (0, 2, 4) (Var 4), g5 1e150 * (1, 2, 3), g7 1e154 * (1, 2, 3) (squares overflow). Asks Var, VarP, StDev, StDevP, Avg and Sum for every group at once and for g5 and g7 alone, so an overflow in a group does not hide the others; the exact two-pass values are recomputed offline.",
            (p, n) =>
            {
                Q(p, n, "SELECT g, Var(d) AS v, VarP(d) AS vp, StDev(d) AS sd, StDevP(d) AS sdp FROM AgV WHERE g <> 7 GROUP BY g");
                Q(p, n, "SELECT g, Avg(d) AS a, Sum(d) AS s, Count(*) AS n FROM AgV GROUP BY g");
                foreach (int g in new[] { 5, 7 })
                {
                    Q(p, n, $"SELECT Var(d) AS v, VarP(d) AS vp, StDev(d) AS sd, StDevP(d) AS sdp FROM AgV WHERE g = {g}");
                }
            });

        yield return Rep("demand-saved-querydefs", ["R-SQL-1", "SQL-DEMAND", "R-PAR-1", "PAR-ERROR"],
            "SQL-DEMAND, deciding variant: the same failing row (Bad: z = 0 at id 3 of 5) read through a saved QueryDef instead of a direct statement. Saved queries QPlain (id, 1/z), QWhere (WHERE 1/z > 0), QSort (ORDER BY id DESC), QGroup (Sum(1/z)), QTop2 (TOP 2 ... ORDER BY id, cutoff before the bad row), QTop4 (TOP 4, cutoff after it) and QXtab (TRANSFORM ... PIVOT 1/z) are created and each opened with QueryDef.OpenRecordset as a snapshot; the open, every row's Value reads and the movements are captured.",
            (p, n) =>
            {
                (string Name, string Sql)[] qs =
                [
                    ("QPlain", "SELECT id, 1 / z AS e FROM Bad;"),
                    ("QWhere", "SELECT id FROM Bad WHERE 1 / z > 0;"),
                    ("QSort", "SELECT id, 1 / z AS e FROM Bad ORDER BY id DESC;"),
                    ("QGroup", "SELECT Sum(1 / z) AS s FROM Bad;"),
                    ("QTop2", "SELECT TOP 2 id, 1 / z AS e FROM Bad ORDER BY id;"),
                    ("QTop4", "SELECT TOP 4 id, 1 / z AS e FROM Bad ORDER BY id;"),
                    ("QXtab", "TRANSFORM Sum(z) SELECT id FROM Bad GROUP BY id PIVOT 1 / z;"),
                ];
                foreach ((string name, string sql) in qs)
                {
                    string qd = n.Next("sd");
                    Mark(p);
                    p.CallBind(qd, "db", "CreateQueryDef", A.Str(name), A.Str(sql)).Errors();
                    string rs = QOpen(p, n, qd, maxRows: 8, close: false);
                    p.Call(rs, "MoveLast").Errors().Get(rs, "RecordCount").Call(rs, "Close").Release(rs).Release(qd);
                }
            }, writable: true);

        yield return Rep("pivot-typed-collisions", ["R-SQL-3", "SQL-PIVOT-04", "SQL-PIVOT-01"],
            "SQL-PIVOT-04, deciding variant: headings that render alike from different typed values. PvT's Piv is Text ('1', '1.0', '01', ' 1', 'a', 'A') and PvN's Piv is Double (1, 1.5, 10, 1.5, 1, -1); asks the dynamic crosstab over each, and fixed IN lists ('1','1.0','01') and (1, 1.5, 10), with the schema (Name, Type, Size) read before the first move and the rows.",
            (p, n) =>
            {
                QSch(p, n, "TRANSFORM Sum(Amt) SELECT Grp FROM PvT GROUP BY Grp PIVOT Piv");
                QSch(p, n, "TRANSFORM Sum(Amt) SELECT Grp FROM PvN GROUP BY Grp PIVOT Piv");
                QSch(p, n, "TRANSFORM Sum(Amt) SELECT Grp FROM PvT GROUP BY Grp PIVOT Piv IN ('1', '1.0', '01')");
                QSch(p, n, "TRANSFORM Sum(Amt) SELECT Grp FROM PvN GROUP BY Grp PIVOT Piv IN (1, 1.5, 10)");
            });
    }

    // R-SQL-2 -------------------------------------------------------------------------------------------------------

    private static IEnumerable<Probe> ActionProbes()
    {
        yield return Rep("act-sel-repeated-keys", ["R-SQL-2", "ACT-SEL-01"],
            "ACT-SEL-01, deciding variants: repeated equal keys and an index. Sk (index IX_Sk_V on V) and Sk2 (no index) hold (ID, V) = (1,10), (2,20), (3,20), (4,20), (5,30), (6,30), (7,5). On each runs UPDATE SET V = V + 100 WHERE V BETWEEN 10 AND 30, then UPDATE SET V = V - 100 WHERE V >= 100, then UPDATE SET V = V + 5 WHERE V >= 10, with dbFailOnError; RecordsAffected and the rows after each statement.",
            (p, n) =>
            {
                foreach (string t in new[] { "Sk", "Sk2" })
                {
                    Run(p, $"CREATE TABLE {t} (ID LONG CONSTRAINT PK_{t} PRIMARY KEY, V LONG)");
                    if (t == "Sk")
                    {
                        Run(p, "CREATE INDEX IX_Sk_V ON Sk (V)");
                    }
                    foreach ((int id, int v) in new[] { (1, 10), (2, 20), (3, 20), (4, 20), (5, 30), (6, 30), (7, 5) })
                    {
                        Run(p, $"INSERT INTO {t} (ID, V) VALUES ({id}, {v})");
                    }
                    foreach (string stmt in new[] { $"UPDATE {t} SET V = V + 100 WHERE V BETWEEN 10 AND 30", $"UPDATE {t} SET V = V - 100 WHERE V >= 100", $"UPDATE {t} SET V = V + 5 WHERE V >= 10" })
                    {
                        Act(p, stmt);
                        Q(p, n, $"SELECT ID, V FROM {t} ORDER BY ID");
                    }
                }
            }, writable: true);

        yield return Rep("act-sel-leaf-split", ["R-SQL-2", "ACT-SEL-02"],
            "ACT-SEL-02, deciding variant: candidate stability when index leaves split. Sl (index on k) and Sl2 (no index) are filled with 4096 rows (n, n) from NUM; on each UPDATE SET k = k + 100000 WHERE k >= 0 and then UPDATE SET k = k - 1 WHERE k > 100 run with dbFailOnError; RecordsAffected, then Count(*), Min(k), Max(k) and the number of rows with k above 100000.",
            (p, n) =>
            {
                foreach (string t in new[] { "Sl", "Sl2" })
                {
                    Run(p, $"CREATE TABLE {t} (ID LONG CONSTRAINT PK_{t} PRIMARY KEY, k LONG)");
                    if (t == "Sl")
                    {
                        Run(p, "CREATE INDEX IX_Sl_K ON Sl (k)");
                    }
                    Run(p, $"INSERT INTO {t} (ID, k) SELECT n, n FROM NUM ORDER BY n");
                    foreach (string stmt in new[] { $"UPDATE {t} SET k = k + 100000 WHERE k >= 0", $"UPDATE {t} SET k = k - 1 WHERE k > 100" })
                    {
                        Act(p, stmt);
                        Q(p, n, $"SELECT Count(*) AS n, Min(k) AS lo, Max(k) AS hi, Sum(IIf(k > 100000, 1, 0)) AS above FROM {t}");
                    }
                }
            }, FxFold, writable: true);

        yield return Rep("act-upd-join-layout", ["R-SQL-2", "ACT-UPD-02"],
            "ACT-UPD-02, deciding variant: which child value a joined UPDATE leaves, with the child insertion order and an index on the foreign key changed one at a time. P (ID 7, Name 'p') joins C1 (children 10, 11 inserted in that order, no index), C2 (11, 10, no index), C3 (10, 11, index on PID) and C4 (11, 10, index on PID); on each UPDATE P INNER JOIN Cx ON P.ID = Cx.PID SET P.Name = Cx.Tag runs with dbFailOnError after Name is reset to 'p'; RecordsAffected and Name after each.",
            (p, n) =>
            {
                Run(p, "CREATE TABLE PJ (ID LONG CONSTRAINT PK_PJ PRIMARY KEY, Name TEXT(20))");
                Run(p, "INSERT INTO PJ VALUES (7, 'p')");
                (string T, bool Index, int[] Order)[] cs = [("CJ1", false, [10, 11]), ("CJ2", false, [11, 10]), ("CJ3", true, [10, 11]), ("CJ4", true, [11, 10])];
                foreach ((string t, bool index, int[] order) in cs)
                {
                    Run(p, $"CREATE TABLE {t} (ID LONG CONSTRAINT PK_{t} PRIMARY KEY, PID LONG, Tag TEXT(10))");
                    if (index)
                    {
                        Run(p, $"CREATE INDEX IX_{t}_PID ON {t} (PID)");
                    }
                    foreach (int id in order)
                    {
                        Run(p, $"INSERT INTO {t} (ID, PID, Tag) VALUES ({id}, 7, 'c{id}')");
                    }
                    Run(p, "UPDATE PJ SET Name = 'p'");
                    Act(p, $"UPDATE PJ INNER JOIN {t} ON PJ.ID = {t}.PID SET PJ.Name = {t}.Tag");
                    Q(p, n, "SELECT ID, Name FROM PJ");
                }
            }, writable: true);

        yield return Rep("act-ins-autonumber", ["R-SQL-2", "ACT-INS-01", "SQL-CONTINUE"],
            "ACT-INS-01, deciding variant: AutoNumber consumption by failed inserts. An (ID Counter primary key, Req Text NOT NULL, U Long unique) receives, with dbFailOnError and the table read after each: ('a', 1); ('b', 1) (unique failure); ('c', 2); (Null, 3) (required failure); ('d', 3); an explicit ID = 1 ('e', 9) (collision); then a plain ('f', 10) and ('g', 11).",
            (p, n) =>
            {
                Run(p, "CREATE TABLE An (ID COUNTER CONSTRAINT PK_An PRIMARY KEY, Req TEXT(10) NOT NULL, U LONG CONSTRAINT UQ_An UNIQUE)");
                string[] stmts =
                [
                    "INSERT INTO An (Req, U) VALUES ('a', 1)", "INSERT INTO An (Req, U) VALUES ('b', 1)", "INSERT INTO An (Req, U) VALUES ('c', 2)",
                    "INSERT INTO An (Req, U) VALUES (Null, 3)", "INSERT INTO An (Req, U) VALUES ('d', 3)", "INSERT INTO An (ID, Req, U) VALUES (1, 'e', 9)",
                    "INSERT INTO An (Req, U) VALUES ('f', 10)", "INSERT INTO An (Req, U) VALUES ('g', 11)",
                ];
                foreach (string s in stmts)
                {
                    Act(p, s);
                    Q(p, n, "SELECT ID, Req, U FROM An ORDER BY ID");
                }
            }, writable: true);

        yield return Rep("act-owneraccess-routes", ["R-SQL-2", "SQL-OWNERACCESS"],
            "SQL-OWNERACCESS, deciding variants: route (direct, temporary QueryDef, saved QueryDef, Execute of an append) and file format (ACCDB fixture copy and a new Jet 4 MDB scratch-oa.mdb). On Ow(ID, V) in each file, asks OpenRecordset of a SELECT ... WITH OWNERACCESS OPTION, a temporary QueryDef of it (SQL read back, then opened), a saved QueryDef (SQL, Type, Updatable read after QueryDefs.Refresh, then opened), and Execute of INSERT ... SELECT ... WITH OWNERACCESS OPTION (RecordsAffected, rows); the clause is also run without OWNERACCESS as the control. Default Admin, blank password, no workgroup.",
            (p, n) =>
            {
                p.CallBind("dbm", "eng", "CreateDatabase", A.Out("scratch-oa.mdb"), A.Str(Dao.LangGeneral), A.I4(Dao.Version40)).Get("dbm", "Version");
                foreach (string d in new[] { "db", "dbm" })
                {
                    Run(p, "CREATE TABLE Ow (ID LONG CONSTRAINT PK_Ow PRIMARY KEY, V LONG)", d);
                    Run(p, "CREATE TABLE Ow2 (ID LONG CONSTRAINT PK_Ow2 PRIMARY KEY, V LONG)", d);
                    Run(p, "INSERT INTO Ow VALUES (1, 10)", d);
                    Run(p, "INSERT INTO Ow VALUES (2, 20)", d);
                    foreach (string clause in new[] { " WITH OWNERACCESS OPTION", "" })
                    {
                        Q(p, n, $"SELECT ID, V FROM Ow{clause}", db: d);
                        string qt = n.Next("oq");
                        Mark(p, d);
                        p.CallBind(qt, d, "CreateQueryDef", A.Str(""), A.Str($"SELECT ID, V FROM Ow{clause};")).Errors().Get(qt, "SQL").Get(qt, "Type");
                        string rt = n.Next("ors");
                        Mark(p, d);
                        p.CallBind(rt, qt, "OpenRecordset", A.I4(Dao.OpenSnapshot)).Errors();
                        Rows(p, rt, 4);
                        p.Call(rt, "Close").Release(rt).Call(qt, "Close").Release(qt);
                        string name = clause.Length == 0 ? "QOaPlain" : "QOa";
                        string qs = n.Next("oq");
                        Mark(p, d);
                        p.CallBind(qs, d, "CreateQueryDef", A.Str(name), A.Str($"SELECT ID, V FROM Ow{clause};")).Errors();
                        p.GetBind("oqds", d, "QueryDefs").Call("oqds", "Refresh").Item("oqd", "oqds", A.Str(name)).Get("oqd", "SQL").Get("oqd", "Type").Get("oqd", "Updatable");
                        string rs = n.Next("ors");
                        Mark(p, d);
                        p.CallBind(rs, "oqd", "OpenRecordset", A.I4(Dao.OpenSnapshot)).Errors();
                        Rows(p, rs, 4);
                        p.Call(rs, "Close").Release(rs).Release("oqd").Release("oqds").Release(qs);
                        Act(p, $"INSERT INTO Ow2 (ID, V) SELECT ID, V FROM Ow{clause}", db: d);
                        Q(p, n, "SELECT ID, V FROM Ow2 ORDER BY ID", db: d);
                        Run(p, "DELETE FROM Ow2", d);
                    }
                }
                p.Call("dbm", "Close").Release("dbm");
            }, writable: true);

        yield return Rep("cont-blocked-positions", ["R-SQL-2", "SQL-CONTINUE", "SQL-ACT-FAIL"],
            "SQL-CONTINUE, deciding variants: the position of the blocked candidate and the flag. Eight tables Cb<position><mode> (position m = ID 2 blocked, f = ID 1, l = ID 3, a = all three; mode n = no flag, f = dbFailOnError) each hold (ID, V) = (1,10), (2,20), (3,30); a second Workspace holds a pessimistic Edit on the blocked rows; each table gets UPDATE SET V = V + 1 after a failing marker, so Errors read afterwards shows whether a successful Execute keeps or clears a prior error; the return, RecordsAffected, Errors and every table's rows are captured.",
            (p, n) =>
            {
                var tables = new List<(string T, int[] Blocked, bool Flag)>();
                foreach ((string pos, int[] blocked) in new[] { ("m", new[] { 2 }), ("f", new[] { 1 }), ("l", new[] { 3 }), ("a", new[] { 1, 2, 3 }) })
                {
                    foreach ((string mode, bool flag) in new[] { ("n", false), ("f", true) })
                    {
                        string t = $"Cb{pos}{mode}";
                        Run(p, $"CREATE TABLE {t} (ID LONG CONSTRAINT PK_{t} PRIMARY KEY, V LONG)");
                        foreach ((int id, int v) in new[] { (1, 10), (2, 20), (3, 30) })
                        {
                            Run(p, $"INSERT INTO {t} (ID, V) VALUES ({id}, {v})");
                        }
                        tables.Add((t, blocked, flag));
                    }
                }
                p.CallBind("ws2", "eng", "CreateWorkspace", A.Str("w2"), A.Str("admin"), A.Str(""), A.Missing)
                    .CallBind("dbL", "ws2", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false));
                var held = new List<string>();
                foreach ((string t, int[] blocked, _) in tables)
                {
                    foreach (int id in blocked)
                    {
                        string rl = $"lk{t}{id}";
                        p.OpenRs(rl, "dbL", $"SELECT * FROM {t} WHERE ID = {id}", Dao.OpenDynaset).Get(rl, "LockEdits").Call(rl, "Edit");
                        held.Add(rl);
                    }
                }
                foreach ((string t, _, bool flag) in tables)
                {
                    Act(p, $"UPDATE {t} SET V = V + 1", flag);
                }
                foreach (string rl in held)
                {
                    p.Call(rl, "CancelUpdate").Call(rl, "Close").Release(rl);
                }
                p.Call("dbL", "Close").Release("dbL").Call("ws2", "Close").Release("ws2");
                foreach ((string t, _, _) in tables)
                {
                    Q(p, n, $"SELECT ID, V FROM {t} ORDER BY ID");
                }
            }, writable: true);

        yield return Rep("fail-late-candidate", ["R-SQL-2", "SQL-ACT-FAIL", "SQL-CONTINUE"],
            "SQL-ACT-FAIL, deciding variants: failure class with the failing candidate in the middle, not first. With dbFailOnError: Fu (unique K; rows 1..3 and 100; UPDATE SET K = IIf(ID = 3, 100, K + 10) collides at ID 3), Fc (text S = '1','2','x','4'; UPDATE SET V = CLng(S) fails at ID 3), Fi (Req NOT NULL; INSERT SELECT with Null at the third source row) and Fp/Fch (relationship; DELETE FROM Fp WHERE ID <= 3 where only ID 3 has a child). Each statement's return, RecordsAffected, Errors and the rows afterwards.",
            (p, n) =>
            {
                Run(p, "CREATE TABLE Fu (ID LONG CONSTRAINT PK_Fu PRIMARY KEY, K LONG CONSTRAINT UQ_Fu UNIQUE)");
                foreach ((int id, int k) in new[] { (1, 1), (2, 2), (3, 3), (4, 100) })
                {
                    Run(p, $"INSERT INTO Fu (ID, K) VALUES ({id}, {k})");
                }
                Act(p, "UPDATE Fu SET K = IIf(ID = 3, 100, K + 10)");
                Q(p, n, "SELECT ID, K FROM Fu ORDER BY ID");

                Run(p, "CREATE TABLE Fc (ID LONG CONSTRAINT PK_Fc PRIMARY KEY, S TEXT(10), V LONG)");
                foreach ((int id, string s) in new[] { (1, "1"), (2, "2"), (3, "x"), (4, "4") })
                {
                    Run(p, $"INSERT INTO Fc (ID, S) VALUES ({id}, '{s}')");
                }
                Act(p, "UPDATE Fc SET V = CLng(S)");
                Q(p, n, "SELECT ID, S, V FROM Fc ORDER BY ID");

                Run(p, "CREATE TABLE Fsrc (ID LONG CONSTRAINT PK_Fsrc PRIMARY KEY, R TEXT(10))");
                Run(p, "CREATE TABLE Fi (ID LONG CONSTRAINT PK_Fi PRIMARY KEY, R TEXT(10) NOT NULL)");
                foreach ((int id, string r) in new[] { (1, "'x'"), (2, "'y'"), (3, "Null"), (4, "'z'") })
                {
                    Run(p, $"INSERT INTO Fsrc (ID, R) VALUES ({id}, {r})");
                }
                Act(p, "INSERT INTO Fi (ID, R) SELECT ID, R FROM Fsrc ORDER BY ID");
                Q(p, n, "SELECT ID, R FROM Fi ORDER BY ID");

                Run(p, "CREATE TABLE Fp (ID LONG CONSTRAINT PK_Fp PRIMARY KEY)");
                Run(p, "CREATE TABLE Fch (ID LONG CONSTRAINT PK_Fch PRIMARY KEY, PID LONG)");
                foreach (int id in new[] { 1, 2, 3, 4 })
                {
                    Run(p, $"INSERT INTO Fp (ID) VALUES ({id})");
                }
                Run(p, "INSERT INTO Fch (ID, PID) VALUES (10, 3)");
                p.GetBind("fts", "db", "TableDefs").Call("fts", "Refresh").Release("fts");
                p.AddRelation("Rel_Fp_Fch", "Fp", "Fch", "ID", "PID");
                Act(p, "DELETE FROM Fp WHERE ID <= 3");
                Q(p, n, "SELECT ID FROM Fp ORDER BY ID");
            }, writable: true);
    }
}
