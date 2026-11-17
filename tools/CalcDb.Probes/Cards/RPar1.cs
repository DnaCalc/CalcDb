namespace CalcDb.Probes.Cards;

/// <summary>
/// R-PAR-1, native serial baseline only (A-02 PAR-FOLD, PAR-ORDER, PAR-ERROR): what the installed ACE's own serial
/// execution answers, from one in-process DAO program per probe, on the attempt's own copy of a fixture created
/// by this card. No worker, thread or process other than the probe's single STA is involved; the multiworker
/// comparison belongs to the CalcDb side. PAR-FOLD asks the exact bits and result types of Sum, Avg, Min, Max, Var
/// and StDev over Double, Currency, Decimal and Single columns whose exact result depends on the order of the
/// additions (insertion, primary-key, index and sorted order, per GROUP BY group, with Nulls, with sizes beyond one
/// page), and where Currency and Decimal sums overflow. PAR-ORDER asks which tied rows ORDER BY, TOP n and TOP n
/// PERCENT return, and the output order of UNION, UNION ALL, duplicate join matches, GROUP BY and DISTINCT when no
/// ORDER BY fixes it. PAR-ERROR asks which error is reported, and at which call, when a row of a scan raises an
/// expression error (division by zero, overflow, type mismatch): at OpenRecordset, MoveLast, a MoveNext, GetRows,
/// inside an aggregate, with TOP stopping before the bad row and with the bad row in an unread later part.
/// The probes ask; no expected answers are stated. Existing R-SQL-1 branches (agg-order, agg-numeric, agg-occur,
/// demand-conversion, union-error-order) ask the same kinds of questions on five-row tables; these probes add
/// order-sensitive witnesses at larger sizes, Currency/Decimal/Single, tie and cutoff behaviour, and per-row error
/// phase on a 240-row table. Deferred (not in this file): multi-process or multi-thread concurrency, Access and Excel
/// UI, VBA callbacks and user-defined functions, ODBC, network, registry or settings, and byte interpretation of
/// the files.
/// </summary>
public sealed class RPar1 : ICard
{
    private const string Card = "r-par-1";
    private const string Id = "R-PAR-1";

    private const double Two53 = 9007199254740992.0;
    private const decimal CyMax = 922337203685477.5807m;
    private const decimal CyMin = -922337203685477.5808m;

    private static readonly (string Ext, int Version)[] Formats = [("accdb", Dao.Version120), ("mdb", Dao.Version40)];

    private static readonly string[] TypeProps = ["Precision", "Scale", "DecimalPlaces"];

    private static string Fx(string kind, string ext) => ext == "accdb" ? $"{Card}-{kind}" : $"{Card}-{kind}-{ext}";

    public IEnumerable<Probe> Probes()
    {
        List<Probe> all = [];
        all.Add(CreateFold());
        foreach ((string ext, int version) in Formats)
        {
            all.Add(CreateOrder(ext, version));
            all.Add(CreateError(ext, version));
        }
        all.AddRange(Fold());
        all.AddRange(Order());
        all.AddRange(Errors());
        return all;
    }

    // Common program parts ----------------------------------------------------------------------------------------

    private static Probe Create(string kind, string ext, int version, string[] branches, string what, Action<P> build) =>
        new(Card, ext == "accdb" ? $"create-{kind}" : $"create-{kind}-{ext}", [Id, .. branches],
            $"{Id} fixture {Fx(kind, ext)}: {what} Created in {ext.ToUpperInvariant()} by DBEngine.CreateDatabase(path, dbLangGeneral, {version}); " +
            "DDL and set-based rows go through Database.Execute, exact typed values through table-type AddNew/Value/Update; every statement's outcome is captured " +
            "(a refused statement is an observation) and a read-back follows; the closed file is kept.",
            p =>
            {
                p.EngineLite().CallBind("db", "eng", "CreateDatabase", A.Out($"{Fx(kind, ext)}.{ext}"), A.Str(Dao.LangGeneral), A.I4(version));
                build(p);
                p.FinishLite("db");
            })
        { CreatesFixture = true };

    private static Probe Read(string kind, string name, string[] branches, string what, Action<P> body, string ext = "accdb", bool writable = false) =>
        new(Card, ext == "accdb" ? name : $"{name}-{ext}", [Id, .. branches],
            $"{Id} {string.Join(", ", branches)}: {what} Runs on a fresh copy of {Fx(kind, ext)} opened {(writable ? "read-write" : "read-only")} through DAO OpenDatabase " +
            "in one process and one STA; each statement is opened with Database.OpenRecordset as a snapshot unless stated, and every call's result, HRESULT and DBEngine.Errors are captured.",
            p =>
            {
                p.EngineLite().OpenDb(readOnly: !writable).Get("db", "Version");
                body(p);
                p.FinishLite("db");
            })
        { Fixtures = [("main", Fx(kind, ext))] };

    private static void X(P p, string sql) => p.Call("db", "Execute", A.Str(sql));

    /// <summary>DG(d 0..7) and NUM(n 1..512 in ascending insertion order): the row source of every set-based insert.</summary>
    private static void Numbers(P p, int digitMax)
    {
        X(p, "CREATE TABLE DG (d LONG CONSTRAINT PK_DG PRIMARY KEY)");
        for (int i = 0; i < digitMax; i++)
        {
            X(p, $"INSERT INTO DG VALUES ({i})");
        }
        X(p, "CREATE TABLE NUM (n LONG CONSTRAINT PK_NUM PRIMARY KEY)");
        int b = digitMax;
        X(p, $"INSERT INTO NUM (n) SELECT a.d * {b * b} + b.d * {b} + c.d + 1 FROM DG AS a, DG AS b, DG AS c ORDER BY a.d, b.d, c.d");
        Count(p, "nn", "NUM");
    }

    private static P Count(P p, string rs, string table) =>
        p.OpenRs(rs, "db", $"SELECT Count(*) AS n FROM {table}", Dao.OpenSnapshot).ReadRow(rs).Call(rs, "Close").Release(rs).Errors();

    private static void AddRows(P p, string table, string[] cols, IEnumerable<Arg[]> rows)
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

    private static P Meta(P p, string rs, bool types)
    {
        string fl = rs + "_mf", f = rs + "_mfi";
        return p.GetBind(fl, rs, "Fields").Each(fl, f, 16, b =>
        {
            b.Get(f, "Name").Get(f, "Type").Get(f, "Size");
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

    /// <summary>One statement: open, Fields metadata before the first row, every row's Field values to EOF, close, errors.</summary>
    private static P Q(P p, string rs, string sql, bool types = false, int type = Dao.OpenSnapshot, int maxRows = 16)
    {
        p.OpenRs(rs, "db", sql, type);
        Meta(p, rs, types);
        return p.ReadAll(rs, maxRows, 16).Call(rs, "Close").Release(rs).Errors();
    }

    private static P Qs(P p, string prefix, IEnumerable<string> sqls, bool types = false, int maxRows = 16)
    {
        int i = 0;
        foreach (string sql in sqls)
        {
            Q(p, $"{prefix}{++i}", sql, types, maxRows: maxRows);
        }
        return p;
    }

    /// <summary>One statement: open, one GetRows(rows) (the whole array VARIANT, element by element), EOF, close, errors.</summary>
    private static P Fetch(P p, string rs, string sql, int rows = 300, int type = Dao.OpenSnapshot) =>
        p.OpenRs(rs, "db", sql, type).Call(rs, "GetRows", A.I4(rows)).Get(rs, "EOF").Call(rs, "Close").Release(rs).Errors();

    private static P Fetches(P p, string prefix, IEnumerable<string> sqls, int rows = 300, int type = Dao.OpenSnapshot)
    {
        int i = 0;
        foreach (string sql in sqls)
        {
            Fetch(p, $"{prefix}{++i}", sql, rows, type);
        }
        return p;
    }

    /// <summary>One statement read in <paramref name="count"/> successive GetRows(<paramref name="size"/>) calls on one recordset.</summary>
    private static P Chunks(P p, string rs, string sql, int size, int count, int type = Dao.OpenSnapshot)
    {
        p.OpenRs(rs, "db", sql, type);
        for (int i = 0; i < count; i++)
        {
            p.Call(rs, "GetRows", A.I4(size)).Get(rs, "EOF");
        }
        return p.Call(rs, "Close").Release(rs).Errors();
    }

    /// <summary>Read Fields("id") and Fields("e") of the current row.</summary>
    private static P At(P p, string rs)
    {
        string fl = rs + "_al", fi = rs + "_ai", fe = rs + "_ae";
        return p.GetBind(fl, rs, "Fields").Item(fi, fl, A.Str("id")).Item(fe, fl, A.Str("e")).Get(fi, "Value").Get(fe, "Value")
            .Release(fe).Release(fi).Release(fl);
    }

    /// <summary>Read id and e, then MoveNext, until EOF (at most <paramref name="max"/> rows); an error does not end the loop.</summary>
    private static P Step(P p, string rs, int max)
    {
        string fl = rs + "_sl", fi = rs + "_si", fe = rs + "_se";
        return p.GetBind(fl, rs, "Fields").Item(fi, fl, A.Str("id")).Item(fe, fl, A.Str("e"))
            .Until(rs, "EOF", max, b => b.Get(fi, "Value").Get(fe, "Value").Call(rs, "MoveNext"))
            .Release(fe).Release(fi).Release(fl);
    }

    private static string Groups<T>(IEnumerable<(int G, T V)> rows, Func<T, string> format) =>
        string.Join("; ", rows.GroupBy(r => r.G).Select(g => $"g{g.Key} [{string.Join(", ", g.Select(r => format(r.V)))}]"));

    // Fixture rows ------------------------------------------------------------------------------------------------

    /// <summary>FOI/FOV: three groups interleaved in storage, the witness in a different order per group.</summary>
    private static readonly (int G, double D)[] Interleaved =
    [
        (1, Two53), (2, -Two53), (3, 1), (1, 1), (2, Two53), (3, Two53), (1, -Two53), (2, 1), (3, -Two53),
    ];

    private static readonly (int G, double? D)[] NullMixed =
    [
        (1, null), (1, Two53), (1, null), (1, 1), (1, null), (1, -Two53),
        (2, 1), (2, null), (2, null), (2, null),
        (3, null), (3, null),
        (4, null), (4, 5), (4, null), (4, 7), (4, null),
        (5, 2.5),
    ];

    private static readonly (int G, decimal V)[] CurrencyRows =
    [
        (1, CyMax), (1, 0.0001m), (1, -0.0001m),
        (2, CyMax), (2, 1m), (2, -1m),
        (3, -1m), (3, 1m), (3, CyMax),
        (4, 1m), (4, CyMax), (4, -1m),
        (5, CyMax), (5, CyMax),
        (6, CyMin), (6, -1m), (6, 1m),
        (7, 0.0001m), (7, 0.0002m), (7, 0.0003m),
        (8, CyMax), (8, CyMin), (8, 1m),
        (9, 0.0001m), (9, 0.0002m),
        (10, 100000000.0001m), (10, 100000000.0003m),
        (11, CyMin), (11, CyMin),
        (12, 0.0001m), (12, CyMax),
    ];

    private static readonly (int G, string V)[] DecimalRows =
    [
        (1, "9999999999999.9999"), (1, "0.0001"), (1, "-0.0001"),
        (2, "9999999999999.9999"), (2, "1"), (2, "-1"),
        (3, "-1"), (3, "1"), (3, "9999999999999.9999"),
        (4, "0.1"), (4, "0.2"), (4, "0.3"),
        (5, "99999999999999999"), (5, "1"), (5, "-99999999999999999"),
        (6, "79228162514264337593543950335"), (6, "1"), (6, "-1"),
        (7, "1.00000000000000000001"), (7, "1"),
        (8, "0.5"), (8, "0.5"),
    ];

    private static readonly (int G, float? V)[] SingleRows =
    [
        (1, 16777216f), (1, 1f), (1, -16777216f),
        (2, 0.1f), (2, 0.1f), (2, 0.1f), (2, 0.1f), (2, 0.1f), (2, 0.1f), (2, 0.1f), (2, 0.1f), (2, 0.1f), (2, 0.1f),
        (3, float.MaxValue), (3, float.MaxValue),
        (4, float.Epsilon), (4, float.Epsilon),
        (5, null), (5, 3.5f), (5, null),
        (6, 16777216f), (6, 1f), (6, 1f),
    ];

    private static Arg Dbl(double? v) => v is double d ? A.R8(d) : A.Null;

    // Fold fixture -------------------------------------------------------------------------------------------------

    private static Probe CreateFold() =>
        Create("fold2", "accdb", Dao.Version120, ["PAR-FOLD"],
            "DG(d 0..15) and NUM(n 1..4096, inserted ascending from the cross join DG x DG x DG); FO(id Long PK, k Long indexed IX_FO_K, d Double) with three rows inserted as " +
            "(id 2, k 3, d 2^53), (id 3, k 1, d 1), (id 1, k 2, d -2^53), so that insertion, primary-key and k order differ; FOI(id PK, g, d) with three groups interleaved in storage " +
            $"({Groups(Interleaved.Select(r => (r.G, r.D)), v => v.ToString("R"))}; ids 1-9 in insertion order) and FOV (the same rows, ids 9 down to 1, index IX_FOV_G on g); " +
            "FL(id Counter PK, g, d): g1 one 2^53 then 4096 rows of 1, g2 4096 rows of 1 then one 2^53, g3 one 2^53, 4096 rows of 1, then -2^53 (inserted in that order), and FLI (the same rows, " +
            "indexes IX_FLI_G on g and IX_FLI_D on d); FG(id Counter PK, g, d): 4096 groups, each as three rows [2^53, 1, -2^53], the groups interleaved (all 2^53 rows, then all 1 rows, then all -2^53 rows), and FGI " +
            "(the same rows, index IX_FGI_G); FN (Null-mixed Double groups); FS (Single groups); FC (Currency groups at and near the type limits: " +
            $"{Groups(CurrencyRows, v => v.ToString(System.Globalization.CultureInfo.InvariantCulture))}); FD (made through ADO DDL with Provider Microsoft.ACE.OLEDB.12.0 as id INTEGER PK, g INTEGER, x DECIMAL(28,4), after closing the DAO database, then reopened by DAO; rows inserted as SQL numeric literals; " +
            $"{Groups(DecimalRows, v => v)}).",
            p =>
            {
                void Fixed(string sql) => X(p, sql);
                Numbers(p, 16);
                Fixed("CREATE TABLE FO (id LONG CONSTRAINT PK_FO PRIMARY KEY, k LONG, d DOUBLE)");
                Fixed("CREATE INDEX IX_FO_K ON FO (k)");
                AddRows(p, "FO", ["id", "k", "d"], [[A.I4(2), A.I4(3), A.R8(Two53)], [A.I4(3), A.I4(1), A.R8(1)], [A.I4(1), A.I4(2), A.R8(-Two53)]]);

                Fixed("CREATE TABLE FOI (id LONG CONSTRAINT PK_FOI PRIMARY KEY, g LONG, d DOUBLE)");
                Fixed("CREATE TABLE FOV (id LONG CONSTRAINT PK_FOV PRIMARY KEY, g LONG, d DOUBLE)");
                Fixed("CREATE INDEX IX_FOV_G ON FOV (g)");
                AddRows(p, "FOI", ["id", "g", "d"], Interleaved.Select((r, i) => new[] { A.I4(i + 1), A.I4(r.G), A.R8(r.D) }));
                AddRows(p, "FOV", ["id", "g", "d"], Interleaved.Select((r, i) => new[] { A.I4(9 - i), A.I4(r.G), A.R8(r.D) }));

                Fixed("CREATE TABLE FL (id COUNTER CONSTRAINT PK_FL PRIMARY KEY, g LONG, d DOUBLE)");
                Fixed("INSERT INTO FL (g, d) VALUES (1, 9007199254740992)");
                Fixed("INSERT INTO FL (g, d) SELECT 1, 1 FROM NUM ORDER BY n");
                Fixed("INSERT INTO FL (g, d) SELECT 2, 1 FROM NUM ORDER BY n");
                Fixed("INSERT INTO FL (g, d) VALUES (2, 9007199254740992)");
                Fixed("INSERT INTO FL (g, d) VALUES (3, 9007199254740992)");
                Fixed("INSERT INTO FL (g, d) SELECT 3, 1 FROM NUM ORDER BY n");
                Fixed("INSERT INTO FL (g, d) VALUES (3, -9007199254740992)");
                Fixed("CREATE TABLE FLI (id LONG CONSTRAINT PK_FLI PRIMARY KEY, g LONG, d DOUBLE)");
                Fixed("CREATE INDEX IX_FLI_G ON FLI (g)");
                Fixed("CREATE INDEX IX_FLI_D ON FLI (d)");
                Fixed("INSERT INTO FLI (id, g, d) SELECT id, g, d FROM FL ORDER BY id");

                Fixed("CREATE TABLE FG (id COUNTER CONSTRAINT PK_FG PRIMARY KEY, g LONG, d DOUBLE)");
                Fixed("INSERT INTO FG (g, d) SELECT n, 9007199254740992 FROM NUM ORDER BY n");
                Fixed("INSERT INTO FG (g, d) SELECT n, 1 FROM NUM ORDER BY n");
                Fixed("INSERT INTO FG (g, d) SELECT n, -9007199254740992 FROM NUM ORDER BY n");
                Fixed("CREATE TABLE FGI (id LONG CONSTRAINT PK_FGI PRIMARY KEY, g LONG, d DOUBLE)");
                Fixed("CREATE INDEX IX_FGI_G ON FGI (g)");
                Fixed("INSERT INTO FGI (id, g, d) SELECT id, g, d FROM FG ORDER BY id");

                Fixed("CREATE TABLE FN (id LONG CONSTRAINT PK_FN PRIMARY KEY, g LONG, d DOUBLE)");
                AddRows(p, "FN", ["id", "g", "d"], NullMixed.Select((r, i) => new[] { A.I4(i + 1), A.I4(r.G), Dbl(r.D) }));

                Fixed("CREATE TABLE FS (id LONG CONSTRAINT PK_FS PRIMARY KEY, g LONG, s SINGLE)");
                AddRows(p, "FS", ["id", "g", "s"], SingleRows.Select((r, i) => new[] { A.I4(i + 1), A.I4(r.G), r.V is float f ? A.R4(f) : A.Null }));

                Fixed("CREATE TABLE FC (id LONG CONSTRAINT PK_FC PRIMARY KEY, g LONG, c CURRENCY)");
                AddRows(p, "FC", ["id", "g", "c"], CurrencyRows.Select((r, i) => new[] { A.I4(i + 1), A.I4(r.G), A.Cy(r.V) }));

                // A DAO CreateField(name, 20) column has Precision 38 and DAO cannot read it (evidence/r-dec-read), so FD is
                // made by ADO DDL with explicit precision and scale, and filled by locale-independent SQL literals.
                string file = $"{Fx("fold2", "accdb")}.accdb";
                p.Call("db", "Close").Release("db");
                p.Create("ADODB.Connection", "cn").Put("cn", "Provider", A.Str("Microsoft.ACE.OLEDB.12.0"))
                    .GetBind("cnp", "cn", "Properties").Item("cds", "cnp", A.Str("Data Source")).Put("cds", "Value", A.Out(file))
                    .Release("cds").Release("cnp").Call("cn", "Open")
                    .Call("cn", "Execute", A.Str("CREATE TABLE FD (id INTEGER CONSTRAINT PK_FD PRIMARY KEY, g INTEGER, x DECIMAL(28,4))"))
                    .Call("cn", "Close").Release("cn");
                p.CallBind("db", "eng", "OpenDatabase", A.Out(file), A.Bool(false), A.Bool(false));
                for (int i = 0; i < DecimalRows.Length; i++)
                {
                    Fixed($"INSERT INTO FD (id, g, x) VALUES ({i + 1}, {DecimalRows[i].G}, {DecimalRows[i].V})");
                }

                Qs(p, "rb",
                [
                    "SELECT g, Count(*) AS n, Min(id) AS lo, Max(id) AS hi FROM FL GROUP BY g",
                    "SELECT g, Count(*) AS n FROM FLI GROUP BY g",
                    "SELECT Count(*) AS n FROM FG",
                    "SELECT Count(*) AS n FROM FGI",
                    "SELECT id, k, d FROM FO ORDER BY id",
                    "SELECT Count(*) AS n FROM FN",
                    "SELECT Count(*) AS n FROM FS",
                    "SELECT g, c FROM FC",
                ], maxRows: 40);
                Q(p, "rbd", "SELECT id, g, x FROM FD", types: true, maxRows: 32);
            });

    // Fold ---------------------------------------------------------------------------------------------------------

    private static IEnumerable<Probe> Fold()
    {
        const string F = "fold2"; // fold2: FD rebuilt with an explicit-precision Decimal column
        string[] foRoutes =
        [
            "FROM FO",
            "FROM FO WHERE id >= 1",
            "FROM FO WHERE k >= 1",
            "FROM (SELECT d FROM FO ORDER BY id)",
            "FROM (SELECT d FROM FO ORDER BY id DESC)",
            "FROM (SELECT d FROM FO ORDER BY k)",
            "FROM (SELECT d FROM FO ORDER BY k DESC)",
            "FROM (SELECT d FROM FO ORDER BY d)",
            "FROM (SELECT d FROM FO ORDER BY d DESC)",
        ];

        yield return Read(F, "fold-double-routes-sum-avg", ["PAR-FOLD", "SQL-AGG-ORDER"],
            "What exact Double bits (and Field type) do Sum and Avg give over FO (d = 2^53, 1, -2^53 in insertion order; primary-key, k and insertion order all differ) through nine routes " +
            "(plain scan, primary-key range, index range on k, and a derived table ordered by id, id DESC, k, k DESC, d, d DESC), and each of Sum and Avg alone on the plain scan?",
            p => Qs(p, "r", [.. foRoutes.Select(r => $"SELECT Sum(d) AS s, Avg(d) AS a {r}"), "SELECT Sum(d) AS s FROM FO", "SELECT Avg(d) AS a FROM FO"], types: true));

        yield return Read(F, "fold-double-routes-var-minmax", ["PAR-FOLD", "SQL-AGG-ORDER", "SQL-AGG-SINGLE"],
            "What exact Double bits do Var, VarP, StDev, StDevP, Min, Max and Count give over FO (d = 2^53, 1, -2^53) through the same nine routes as the Sum probe " +
            "(plain scan, primary-key range, index range on k, derived table ordered by id, id DESC, k, k DESC, d, d DESC), one statement per route?",
            p => Qs(p, "r", foRoutes.Select(r => $"SELECT Var(d) AS v, VarP(d) AS vp, StDev(d) AS sd, StDevP(d) AS sdp, Min(d) AS mn, Max(d) AS mx, Count(d) AS n {r}")));

        yield return Read(F, "fold-double-groups-interleaved", ["PAR-FOLD", "SQL-AGG-ORDER"],
            "For three groups whose rows are interleaved in storage (FOI: ids 1-9 in insertion order; FOV: the same rows with ids reversed and an index on g), what exact bits do Sum, Avg, Var, VarP, StDev and StDevP give per GROUP BY group " +
            "without ORDER BY, with ORDER BY g and g DESC, through a derived table ordered by id and by id DESC, and under WHERE g >= 1; and in which order do the group rows come out?",
            p => Qs(p, "g", new[] { "FOI", "FOV" }.SelectMany(t => new[]
            {
                $"SELECT g, Sum(d) AS s, Avg(d) AS a FROM {t} GROUP BY g",
                $"SELECT g, Sum(d) AS s, Avg(d) AS a FROM {t} GROUP BY g ORDER BY g",
                $"SELECT g, Sum(d) AS s, Avg(d) AS a FROM {t} GROUP BY g ORDER BY g DESC",
                $"SELECT g, Sum(d) AS s, Avg(d) AS a FROM (SELECT g, d FROM {t} ORDER BY id) GROUP BY g",
                $"SELECT g, Sum(d) AS s, Avg(d) AS a FROM (SELECT g, d FROM {t} ORDER BY id DESC) GROUP BY g",
                $"SELECT g, Var(d) AS v, VarP(d) AS vp, StDev(d) AS sd, StDevP(d) AS sdp FROM {t} GROUP BY g",
                $"SELECT g, Sum(d) AS s FROM {t} WHERE g >= 1 GROUP BY g",
            })));

        yield return Read(F, "fold-double-large-groups", ["PAR-FOLD", "SQL-AGG-ORDER"],
            "What exact Double bits do Sum, Avg and Count give per group over FL and over its indexed copy FLI (g1: one 2^53 then 4096 ones; g2: 4096 ones then one 2^53; g3: 2^53, 4096 ones, -2^53; about 4100 rows each), " +
            "as GROUP BY g, GROUP BY g ORDER BY g DESC, and as one statement per group with WHERE g = 1, 2, 3?",
            p => Qs(p, "l", new[] { "FL", "FLI" }.SelectMany(t => new[]
            {
                $"SELECT g, Sum(d) AS s, Avg(d) AS a, Count(d) AS n FROM {t} GROUP BY g",
                $"SELECT g, Sum(d) AS s, Avg(d) AS a, Count(d) AS n FROM {t} GROUP BY g ORDER BY g DESC",
                $"SELECT Sum(d) AS s, Avg(d) AS a FROM {t} WHERE g = 1",
                $"SELECT Sum(d) AS s, Avg(d) AS a FROM {t} WHERE g = 2",
                $"SELECT Sum(d) AS s, Avg(d) AS a FROM {t} WHERE g = 3",
            })));

        yield return Read(F, "fold-double-large-reordered", ["PAR-FOLD", "SQL-AGG-ORDER"],
            "What exact Double bits does Sum give per FL group (g1, g2, g3 of the large witness) when the rows are fed through a derived table ordered by id, id DESC, d and d DESC (one statement per group and order)?",
            p => Qs(p, "o", Enumerable.Range(1, 3).SelectMany(g => new[] { "id", "id DESC", "d", "d DESC" }
                .Select(o => $"SELECT Sum(d) AS s FROM (SELECT d FROM FL WHERE g = {g} ORDER BY {o})"))));

        yield return Read(F, "fold-double-large-partials", ["PAR-FOLD", "SQL-AGG-ORDER"],
            "What do partial sums of the large FL witness give: Sum for g3 over all rows, over d = 1, d <> 1, d > 1 and d < 0; for g1, g2 and g3 over the lower and the upper part of the id range " +
            "(g1 at id 2049, g2 at id 6000, g3 at id 10000); and Sum of the two partial sums of g1, g2 and g3 (d = 1 part and the rest) through UNION ALL in both arm orders?",
            p => Qs(p,"p",
            [
                "SELECT Sum(d) AS s FROM FL WHERE g = 3",
                "SELECT Sum(d) AS s FROM FL WHERE g = 3 AND d = 1",
                "SELECT Sum(d) AS s FROM FL WHERE g = 3 AND d <> 1",
                "SELECT Sum(d) AS s FROM FL WHERE g = 3 AND d > 1",
                "SELECT Sum(d) AS s FROM FL WHERE g = 3 AND d < 0",
                "SELECT Sum(d) AS s FROM FL WHERE g = 1 AND id <= 2049",
                "SELECT Sum(d) AS s FROM FL WHERE g = 1 AND id > 2049",
                "SELECT Sum(d) AS s FROM FL WHERE g = 2 AND id <= 6000",
                "SELECT Sum(d) AS s FROM FL WHERE g = 2 AND id > 6000",
                "SELECT Sum(d) AS s FROM FL WHERE g = 3 AND id <= 10000",
                "SELECT Sum(d) AS s FROM FL WHERE g = 3 AND id > 10000",
                .. Enumerable.Range(1, 3).SelectMany(g => new[]
                {
                    $"SELECT Sum(s) AS total FROM (SELECT Sum(d) AS s FROM FL WHERE g = {g} AND d = 1 UNION ALL SELECT Sum(d) AS s FROM FL WHERE g = {g} AND d <> 1)",
                    $"SELECT Sum(s) AS total FROM (SELECT Sum(d) AS s FROM FL WHERE g = {g} AND d <> 1 UNION ALL SELECT Sum(d) AS s FROM FL WHERE g = {g} AND d = 1)",
                }),
            ]));

        yield return Read(F, "fold-double-large-var", ["PAR-FOLD", "SQL-AGG-SINGLE", "SQL-AGG-NUMERIC"],
            "What exact Double bits do Var, VarP, StDev and StDevP give per FL group (g1 to g3, about 4100 rows each) as GROUP BY g over FL and over FLI, and through a derived table of g2 ordered by d, d DESC and id DESC, and of g1 ordered by id DESC?",
            p => Qs(p, "v",
            [
                "SELECT g, Var(d) AS v, VarP(d) AS vp, StDev(d) AS sd, StDevP(d) AS sdp FROM FL GROUP BY g",
                "SELECT g, Var(d) AS v, VarP(d) AS vp, StDev(d) AS sd, StDevP(d) AS sdp FROM FLI GROUP BY g",
                "SELECT Var(d) AS v, VarP(d) AS vp, StDev(d) AS sd, StDevP(d) AS sdp FROM (SELECT d FROM FL WHERE g = 2 ORDER BY d)",
                "SELECT Var(d) AS v, VarP(d) AS vp, StDev(d) AS sd, StDevP(d) AS sdp FROM (SELECT d FROM FL WHERE g = 2 ORDER BY d DESC)",
                "SELECT Var(d) AS v, VarP(d) AS vp, StDev(d) AS sd, StDevP(d) AS sdp FROM (SELECT d FROM FL WHERE g = 2 ORDER BY id DESC)",
                "SELECT Var(d) AS v, VarP(d) AS vp, StDev(d) AS sd, StDevP(d) AS sdp FROM (SELECT d FROM FL WHERE g = 1 ORDER BY id DESC)",
            ]));

        yield return Read(F, "fold-double-many-groups", ["PAR-FOLD", "SQL-AGG-ORDER"],
            "With 4096 groups of three rows [2^53, 1, -2^53] interleaved in storage (FG; FGI adds an index on g), which Sum values occur over the groups and for which g (a derived table's own GROUP BY s), what is Sum of the 4096 group sums, " +
            "what are the first eight groups with ORDER BY g and g DESC, what do the distinct values of Var per group look like, and what changes when the rows are fed in id DESC order?",
            p => Qs(p, "m", new[] { "FG", "FGI" }.SelectMany(t => new[]
            {
                $"SELECT s, Count(*) AS n, Min(g) AS lo, Max(g) AS hi FROM (SELECT g, Sum(d) AS s FROM {t} GROUP BY g) GROUP BY s",
                $"SELECT Count(*) AS n, Sum(s) AS total FROM (SELECT g, Sum(d) AS s FROM {t} GROUP BY g)",
                $"SELECT TOP 8 g, Sum(d) AS s FROM {t} GROUP BY g ORDER BY g",
                $"SELECT TOP 8 g, Sum(d) AS s FROM {t} GROUP BY g ORDER BY g DESC",
                $"SELECT s, Count(*) AS n, Min(g) AS lo, Max(g) AS hi FROM (SELECT g, Sum(d) AS s FROM (SELECT g, d FROM {t} ORDER BY id DESC) GROUP BY g) GROUP BY s",
                $"SELECT v, Count(*) AS n FROM (SELECT g, Var(d) AS v FROM {t} GROUP BY g) GROUP BY v",
            })));

        yield return Read(F, "fold-double-null-mixed", ["PAR-FOLD", "SQL-AGG-FIRST", "SQL-AGG-SINGLE"],
            $"With Nulls mixed into Double groups (FN: {Groups(NullMixed.Select(r => (r.G, r.D)), v => v is double d ? d.ToString("R") : "Null")}), what do Count(*), Count(d), Sum, Avg, Min, Max, Var, VarP, StDev and StDevP give per group, " +
            "overall, and through a derived table ordered by id DESC?",
            p => Qs(p, "n",
            [
                "SELECT g, Count(*) AS c, Count(d) AS cd, Sum(d) AS s, Avg(d) AS a, Min(d) AS mn, Max(d) AS mx FROM FN GROUP BY g",
                "SELECT g, Var(d) AS v, VarP(d) AS vp, StDev(d) AS sd, StDevP(d) AS sdp FROM FN GROUP BY g",
                "SELECT Count(*) AS c, Count(d) AS cd, Sum(d) AS s, Avg(d) AS a, Min(d) AS mn, Max(d) AS mx FROM FN",
                "SELECT Var(d) AS v, VarP(d) AS vp, StDev(d) AS sd, StDevP(d) AS sdp FROM FN",
                "SELECT g, Sum(d) AS s, Avg(d) AS a FROM (SELECT g, d FROM FN ORDER BY id DESC) GROUP BY g",
                "SELECT g, Var(d) AS v, StDev(d) AS sd FROM (SELECT g, d FROM FN ORDER BY id DESC) GROUP BY g",
                "SELECT g, Sum(d) AS s FROM FN WHERE d Is Not Null GROUP BY g",
            ]));

        string cyDesc = Groups(CurrencyRows, v => v.ToString(System.Globalization.CultureInfo.InvariantCulture));
        string[] cyGroups = [.. Enumerable.Range(1, 12).Select(g => g.ToString(System.Globalization.CultureInfo.InvariantCulture))];

        yield return Read(F, "fold-currency-sum-groups", ["PAR-FOLD", "SQL-AGG-TYPE"],
            $"For the Currency groups of FC ({cyDesc}), what does Sum return, with which Field type, size and precision properties, or raise, and at which call (open, first row), one statement per group (WHERE g = 1 to 12); " +
            "and what do SELECT Sum(c) over the whole table, and GROUP BY g with Sum, give when some groups overflow?",
            p => Qs(p, "c", [.. cyGroups.Select(g => $"SELECT Sum(c) AS s FROM FC WHERE g = {g}"), "SELECT Sum(c) AS s FROM FC", "SELECT g, Sum(c) AS s FROM FC GROUP BY g"], types: true));

        yield return Read(F, "fold-currency-avg-minmax", ["PAR-FOLD", "SQL-AGG-TYPE"],
            "For the Currency groups of FC, what does Avg return (type, value, or error and call) per group (WHERE g = 1 to 12, one statement each); and what do Min, Max and Count give as GROUP BY g?",
            p => Qs(p, "a", [.. cyGroups.Select(g => $"SELECT Avg(c) AS a FROM FC WHERE g = {g}"), "SELECT g, Min(c) AS mn, Max(c) AS mx, Count(c) AS n FROM FC GROUP BY g"], types: true));

        yield return Read(F, "fold-currency-var-stdev", ["PAR-FOLD", "SQL-AGG-SINGLE", "SQL-AGG-TYPE"],
            "For the Currency groups of FC, what do Var, VarP, StDev and StDevP return (type, exact bits, or error and call), one statement per group (WHERE g = 1 to 12)?",
            p => Qs(p, "s", cyGroups.Select(g => $"SELECT Var(c) AS v, VarP(c) AS vp, StDev(c) AS sd, StDevP(c) AS sdp FROM FC WHERE g = {g}"), types: true));

        string[] cyForms = ["Sum(CDbl(c))", "Sum(CDec(c))", "Sum(CCur(c))", "Sum(c * 1)", "Sum(c + 0)", "Sum(c * 1.0)"];
        yield return Read(F, "fold-currency-argument-forms", ["PAR-FOLD", "SQL-AGG-TYPE"],
            "For FC groups 1 (Currency maximum, 0.0001, -0.0001), 2 (maximum, 1, -1) and 7 (0.0001, 0.0002, 0.0003), what result type and value (or error and call) do Sum(CDbl(c)), Sum(CDec(c)), Sum(CCur(c)), Sum(c * 1), Sum(c + 0) and Sum(c * 1.0) give, one statement per form and group?",
            p => Qs(p, "f", new[] { 1, 2, 7 }.SelectMany(g => cyForms.Select(f => $"SELECT {f} AS s FROM FC WHERE g = {g}")), types: true));

        string dcDesc = Groups(DecimalRows, v => v);
        yield return Read(F, "fold-decimal-sum-groups", ["PAR-FOLD", "SQL-AGG-TYPE"],
            $"What Decimal values are stored in FD (x DECIMAL(28,4) made by ADO DDL; intended groups {dcDesc}), and what does Sum return (Field type, size, Precision and Scale properties, value, or error and call) per group (WHERE g = 1 to 8), over all rows, and as GROUP BY g?",
            p =>
            {
                Q(p, "d0", "SELECT id, g, x FROM FD", types: true, maxRows: 32);
                Qs(p, "d", [.. Enumerable.Range(1, 8).Select(g => $"SELECT Sum(x) AS s FROM FD WHERE g = {g}"), "SELECT Sum(x) AS s FROM FD", "SELECT g, Sum(x) AS s FROM FD GROUP BY g"], types: true);
            });

        yield return Read(F, "fold-decimal-avg-var-forms", ["PAR-FOLD", "SQL-AGG-TYPE", "SQL-AGG-SINGLE"],
            "For the Decimal field of FD, what do Avg, Var and StDev return (type, value, or error and call) per group 1 to 8 (one statement per group and aggregate family), what do Min, Max and Count give as GROUP BY g, " +
            "and what do Sum(x * 1), Sum(CDec(x)), Sum(CCur(x)) and Sum(CDbl(x)) give for groups 1, 2, 4 and 5?",
            p => Qs(p, "e",
            [
                .. Enumerable.Range(1, 8).Select(g => $"SELECT Avg(x) AS a FROM FD WHERE g = {g}"),
                .. Enumerable.Range(1, 8).Select(g => $"SELECT Var(x) AS v, StDev(x) AS sd FROM FD WHERE g = {g}"),
                "SELECT g, Min(x) AS mn, Max(x) AS mx, Count(x) AS n FROM FD GROUP BY g",
                .. new[] { 1, 2, 4, 5 }.SelectMany(g => new[] { "Sum(x * 1)", "Sum(CDec(x))", "Sum(CCur(x))", "Sum(CDbl(x))" }.Select(f => $"SELECT {f} AS s FROM FD WHERE g = {g}")),
            ], types: true));

        yield return Read(F, "fold-single-groups", ["PAR-FOLD", "SQL-AGG-TYPE", "SQL-AGG-SINGLE"],
            $"For the Single groups of FS ({Groups(SingleRows, v => v is float f ? f.ToString("R", System.Globalization.CultureInfo.InvariantCulture) : "Null")}), what do Sum, Avg, Min and Max return (type, exact bits, or error and call) per group (WHERE g = 1 to 6, one statement each), " +
            "what do Var and StDev give per group, and what do Sum and Avg give as GROUP BY g?",
            p => Qs(p, "s",
            [
                .. Enumerable.Range(1, 6).Select(g => $"SELECT Sum(s) AS sm, Avg(s) AS av, Min(s) AS mn, Max(s) AS mx FROM FS WHERE g = {g}"),
                .. Enumerable.Range(1, 6).Select(g => $"SELECT Var(s) AS v, StDev(s) AS sd FROM FS WHERE g = {g}"),
                "SELECT g, Sum(s) AS sm, Avg(s) AS av FROM FS GROUP BY g",
            ], types: true));

        yield return Read(F, "fold-double-join-union-fed", ["PAR-FOLD", "PAR-ORDER", "SQL-AGG-ORDER"],
            "What exact Double bits do Sum and Avg give when the rows reach the aggregate through a UNION ALL of FO with itself, through a derived UNION of FO with itself, and through joins that fan out rows " +
            "(FOI joined with FOV on g, inner and left, plain and per group, Sum over each side)?",
            p => Qs(p, "j",
            [
                "SELECT Sum(d) AS s, Avg(d) AS a FROM (SELECT d FROM FO UNION ALL SELECT d FROM FO)",
                "SELECT Sum(d) AS s, Avg(d) AS a FROM (SELECT d FROM FO UNION SELECT d FROM FO)",
                "SELECT Sum(a.d) AS s, Avg(a.d) AS av FROM FOI AS a INNER JOIN FOV AS b ON a.g = b.g",
                "SELECT Sum(b.d) AS s, Avg(b.d) AS av FROM FOI AS a INNER JOIN FOV AS b ON a.g = b.g",
                "SELECT a.g, Sum(a.d) AS s, Sum(b.d) AS t FROM FOI AS a INNER JOIN FOV AS b ON a.g = b.g GROUP BY a.g",
                "SELECT a.g, Sum(a.d) AS s, Sum(b.d) AS t FROM FOI AS a LEFT JOIN FOV AS b ON a.g = b.g GROUP BY a.g",
                "SELECT a.g, Sum(b.d) AS t FROM FOV AS a INNER JOIN FOI AS b ON a.g = b.g GROUP BY a.g",
            ]));
    }

    // Order fixture ------------------------------------------------------------------------------------------------

    private static Probe CreateOrder(string ext, int version) =>
        Create("order", ext, version, ["PAR-ORDER"],
            "DG(d 0..7) and NUM(n 1..512, inserted ascending); tie tables with 256 rows each inserted by INSERT ... SELECT pk = 1000 - n, k = n Mod 7, ins = n, s = 't' & (n Mod 5) FROM NUM ORDER BY n " +
            "(so insertion order is ins ascending and primary-key order is descending): OT5 (no key, no index), OT2 (primary key pk), OT3 (primary key and index IX_OT3_K on k), OT4 (primary key and compound index IX_OT4_KI on k, ins DESC); " +
            "UA(id PK, v = 21 - n), UB(v = n + 10) and UC(v = (n * 7) Mod 23) with 20 rows each; join tables JL and JLI (id PK, k = n Mod 5, 16 rows) and JR and JRI (rid = 100 - n PK, k = n Mod 4, ins = n, 32 rows), with indexes on k in JLI and JRI; " +
            "OG and OGI (id PK, t Text, n Long; OGI indexes t) holding the same twelve Text values with case and accent variants in one insertion order.",
            p =>
            {
                Numbers(p, 8);
                foreach (string t in new[] { "OT5", "OT2", "OT3", "OT4" })
                {
                    X(p, t == "OT5"
                        ? "CREATE TABLE OT5 (pk LONG, k LONG, ins LONG, s TEXT(10))"
                        : $"CREATE TABLE {t} (pk LONG CONSTRAINT PK_{t} PRIMARY KEY, k LONG, ins LONG, s TEXT(10))");
                }
                X(p, "CREATE INDEX IX_OT3_K ON OT3 (k)");
                X(p, "CREATE INDEX IX_OT4_KI ON OT4 (k, ins DESC)");
                foreach (string t in new[] { "OT5", "OT2", "OT3", "OT4" })
                {
                    X(p, $"INSERT INTO {t} (pk, k, ins, s) SELECT 1000 - n, n Mod 7, n, 't' & (n Mod 5) FROM NUM WHERE n <= 256 ORDER BY n");
                    Count(p, "c" + t, t);
                }
                foreach (string t in new[] { "UA", "UB", "UC" })
                {
                    X(p, $"CREATE TABLE {t} (id LONG CONSTRAINT PK_{t} PRIMARY KEY, v LONG)");
                }
                X(p, "INSERT INTO UA (id, v) SELECT n, 21 - n FROM NUM WHERE n <= 20 ORDER BY n");
                X(p, "INSERT INTO UB (id, v) SELECT n, n + 10 FROM NUM WHERE n <= 20 ORDER BY n");
                X(p, "INSERT INTO UC (id, v) SELECT n, (n * 7) Mod 23 FROM NUM WHERE n <= 20 ORDER BY n");
                foreach (string t in new[] { "JL", "JLI" })
                {
                    X(p, $"CREATE TABLE {t} (id LONG CONSTRAINT PK_{t} PRIMARY KEY, k LONG)");
                }
                foreach (string t in new[] { "JR", "JRI" })
                {
                    X(p, $"CREATE TABLE {t} (rid LONG CONSTRAINT PK_{t} PRIMARY KEY, k LONG, ins LONG)");
                }
                X(p, "CREATE INDEX IX_JLI_K ON JLI (k)");
                X(p, "CREATE INDEX IX_JRI_K ON JRI (k)");
                foreach (string t in new[] { "JL", "JLI" })
                {
                    X(p, $"INSERT INTO {t} (id, k) SELECT n, n Mod 5 FROM NUM WHERE n <= 16 ORDER BY n");
                }
                foreach (string t in new[] { "JR", "JRI" })
                {
                    X(p, $"INSERT INTO {t} (rid, k, ins) SELECT 100 - n, n Mod 4, n FROM NUM WHERE n <= 32 ORDER BY n");
                }
                foreach (string t in new[] { "OG", "OGI" })
                {
                    X(p, $"CREATE TABLE {t} (id LONG CONSTRAINT PK_{t} PRIMARY KEY, t TEXT(10), n LONG)");
                    AddRows(p, t, ["id", "t", "n"], OgValues.Select((v, i) => new[] { A.I4(i + 1), A.Str(v), A.I4((i % 3) + 1) }));
                }
                X(p, "CREATE INDEX IX_OGI_T ON OGI (t)");
                Qs(p, "rb",
                [
                    "SELECT k, Count(*) AS n FROM OT5 GROUP BY k",
                    "SELECT Count(*) AS n FROM UA",
                    "SELECT Count(*) AS n FROM JL",
                    "SELECT Count(*) AS n FROM JR",
                    "SELECT Count(*) AS n FROM OGI",
                ]);
            });

    private static readonly string[] OgValues = ["b", "A", "a", "B", "c", "a", "C", "b", "B", "A", char.ConvertFromUtf32(0xE4), "a"];

    // Order --------------------------------------------------------------------------------------------------------

    private static IEnumerable<Probe> Order()
    {
        const string O = "order";
        string[] four = ["OT5", "OT2", "OT3", "OT4"];

        yield return Read(O, "order-scan-storage-order", ["PAR-ORDER", "SQL-REL"],
            "Without ORDER BY, in what order do the 256 rows of OT5 (no key, no index), OT2 (primary key descending in insertion), OT3 (index on k) and OT4 (compound index on k, ins DESC) come out of SELECT ins FROM table (one GetRows(300) per table)?",
            p => Fetches(p, "s", four.Select(t => $"SELECT ins FROM {t}")));

        yield return Read(O, "order-scan-filter-routes", ["PAR-ORDER", "SQL-REL"],
            "Without ORDER BY, in what order do the ins values come out of OT5, OT2, OT3 and OT4 under WHERE k = 3, k >= 5, k IN (2, 4) and pk BETWEEN 800 AND 850 (one GetRows(300) per statement)?",
            p => Fetches(p, "f", four.SelectMany(t => new[]
            {
                $"SELECT ins FROM {t} WHERE k = 3",
                $"SELECT ins FROM {t} WHERE k >= 5",
                $"SELECT ins FROM {t} WHERE k IN (2, 4)",
                $"SELECT ins FROM {t} WHERE pk BETWEEN 800 AND 850",
            })));

        yield return Read(O, "order-ties-order-by-k", ["PAR-ORDER", "SQL-REL"],
            "Under ORDER BY k and ORDER BY k DESC (k has seven values, 36-37 tied rows each), in what order do the tied rows (ins) come out for OT5, OT2, OT3 and OT4 (one GetRows(300) per statement)?",
            p => Fetches(p, "t", four.SelectMany(t => new[] { $"SELECT ins FROM {t} ORDER BY k", $"SELECT ins FROM {t} ORDER BY k DESC" })));

        yield return Read(O, "order-ties-partial-keys", ["PAR-ORDER", "SQL-REL"],
            "Under ORDER BY k, s; k DESC, s; s; and s DESC, k (ties remain after the last key), in what order do the tied rows (ins) come out for OT5 and OT3 (one GetRows(300) per statement)?",
            p => Fetches(p, "u", new[] { "OT5", "OT3" }.SelectMany(t => new[]
            {
                $"SELECT ins FROM {t} ORDER BY k, s",
                $"SELECT ins FROM {t} ORDER BY k DESC, s",
                $"SELECT ins FROM {t} ORDER BY s",
                $"SELECT ins FROM {t} ORDER BY s DESC, k",
            })));

        int[] topCounts = [1, 5, 36, 37, 38, 80];
        foreach ((string ext, _) in Formats)
        {
            yield return Read(O, "order-top-n-ties-asc", ["PAR-ORDER", "SQL-REL"],
                "Which tied rows (ins) does SELECT TOP n ins FROM table ORDER BY k return for n = 1, 5, 36, 37, 38 and 80 (the tie groups of k are 36 or 37 rows, so n = 36, 37 and 38 cut a tie group) on OT5, OT3 and OT4 (one GetRows(300) per statement)?",
                p => Fetches(p, "n", new[] { "OT5", "OT3", "OT4" }.SelectMany(t => topCounts.Select(n => $"SELECT TOP {n} ins FROM {t} ORDER BY k"))), ext);
        }

        yield return Read(O, "order-top-n-ties-desc", ["PAR-ORDER", "SQL-REL"],
            "Which tied rows (ins) does SELECT TOP n ins FROM table ORDER BY k DESC return for n = 1, 5, 36, 37, 38 and 80 on OT5, OT3 and OT4 (one GetRows(300) per statement)?",
            p => Fetches(p, "n", new[] { "OT5", "OT3", "OT4" }.SelectMany(t => topCounts.Select(n => $"SELECT TOP {n} ins FROM {t} ORDER BY k DESC"))));

        yield return Read(O, "order-top-percent-ties", ["PAR-ORDER", "SQL-REL"],
            "How many rows and which tied rows (ins) does SELECT TOP n PERCENT ins FROM table ORDER BY k return for n = 1, 5, 10, 25 and 50 on OT5 and OT3, and the same under ORDER BY k DESC (one GetRows(300) per statement)?",
            p => Fetches(p, "q", new[] { "OT5", "OT3" }.SelectMany(t => new[] { 1, 5, 10, 25, 50 }.SelectMany(n => new[]
            {
                $"SELECT TOP {n} PERCENT ins FROM {t} ORDER BY k",
                $"SELECT TOP {n} PERCENT ins FROM {t} ORDER BY k DESC",
            }))));

        yield return Read(O, "order-top-without-order-by", ["PAR-ORDER", "SQL-REL"],
            "Which rows (ins) does TOP n (n = 1, 5 and 40), TOP 5 PERCENT and TOP 5 under WHERE k = 3 return without ORDER BY, on OT5, OT2, OT3 and OT4 (one GetRows(300) per statement)?",
            p => Fetches(p, "w", four.SelectMany(t => new[]
            {
                $"SELECT TOP 1 ins FROM {t}",
                $"SELECT TOP 5 ins FROM {t}",
                $"SELECT TOP 40 ins FROM {t}",
                $"SELECT TOP 5 PERCENT ins FROM {t}",
                $"SELECT TOP 5 ins FROM {t} WHERE k = 3",
            })));

        yield return Read(O, "order-union-without-order-by", ["PAR-ORDER", "SQL-UNION-03"],
            "Without ORDER BY, in what order do the rows come out of UNION and UNION ALL of UA (v 20 down to 1), UB (v 11 to 30) and UC (v = n * 7 Mod 23), in both arm orders, with three arms, with mixed UNION and UNION ALL, " +
            "with a literal arm tag column, and over k of OT5 and OT3 (one GetRows(300) per statement)?",
            p => Fetches(p, "u",
            [
                "SELECT v FROM UA UNION SELECT v FROM UB",
                "SELECT v FROM UB UNION SELECT v FROM UA",
                "SELECT v FROM UA UNION ALL SELECT v FROM UB",
                "SELECT v FROM UB UNION ALL SELECT v FROM UA",
                "SELECT v FROM UA UNION SELECT v FROM UB UNION SELECT v FROM UC",
                "SELECT v FROM UA UNION ALL SELECT v FROM UB UNION ALL SELECT v FROM UC",
                "SELECT v FROM UC UNION SELECT v FROM UA UNION ALL SELECT v FROM UB",
                "SELECT v, 'a' AS src FROM UA UNION SELECT v, 'b' FROM UB",
                "SELECT v, 'a' AS src FROM UA UNION ALL SELECT v, 'b' FROM UB",
                "SELECT k AS v FROM OT5 UNION SELECT k FROM OT3",
            ]));

        yield return Read(O, "order-union-ties-and-top", ["PAR-ORDER", "SQL-UNION-03", "SQL-REL"],
            "Which tied rows cross UNION ALL arms and TOP: ORDER BY v and v DESC over UA UNION ALL UB with an arm tag (v 11 to 20 occurs in both arms); TOP 11 and TOP 13 with ORDER BY v and TOP 11 with ORDER BY v DESC (the cutoff falls inside a tie of two arms); " +
            "TOP 5 over UNION ALL and over UNION without ORDER BY; and TOP 3 inside each arm without ORDER BY (one GetRows(300) per statement)?",
            p =>
            {
                const string u = "(SELECT v, 'a' AS src FROM UA UNION ALL SELECT v, 'b' AS src FROM UB)";
                Fetches(p, "t",
                [
                    $"SELECT v, src FROM {u} ORDER BY v",
                    $"SELECT v, src FROM {u} ORDER BY v DESC",
                    $"SELECT TOP 11 v, src FROM {u} ORDER BY v",
                    $"SELECT TOP 13 v, src FROM {u} ORDER BY v",
                    $"SELECT TOP 11 v, src FROM {u} ORDER BY v DESC",
                    "SELECT TOP 5 v FROM (SELECT v FROM UA UNION ALL SELECT v FROM UB)",
                    "SELECT TOP 5 v FROM (SELECT v FROM UA UNION SELECT v FROM UB)",
                    "SELECT TOP 3 v FROM UA UNION ALL SELECT TOP 3 v FROM UB",
                ]);
            });

        (string L, string R)[] pairs = [("JL", "JR"), ("JLI", "JR"), ("JL", "JRI"), ("JLI", "JRI")];
        yield return Read(O, "order-join-duplicate-matches", ["PAR-ORDER", "SQL-J", "SQL-REL"],
            "Without ORDER BY, in what order do the join pairs (left id, rid) come out of INNER JOIN in both FROM orders, LEFT JOIN, RIGHT JOIN and the comma join with WHERE, on k (each left key matches 8 right rows, left keys repeat 3 to 4 times, left k = 4 matches nothing), " +
            "for the table pairs (JL, JR), (JLI, JR), (JL, JRI) and (JLI, JRI) (one GetRows(300) per statement)?",
            p => Fetches(p, "j", pairs.SelectMany(t => new[]
            {
                $"SELECT {t.L}.id AS lid, {t.R}.rid AS rid FROM {t.L} INNER JOIN {t.R} ON {t.L}.k = {t.R}.k",
                $"SELECT {t.L}.id AS lid, {t.R}.rid AS rid FROM {t.R} INNER JOIN {t.L} ON {t.L}.k = {t.R}.k",
                $"SELECT {t.L}.id AS lid, {t.R}.rid AS rid FROM {t.L} LEFT JOIN {t.R} ON {t.L}.k = {t.R}.k",
                $"SELECT {t.L}.id AS lid, {t.R}.rid AS rid FROM {t.L} RIGHT JOIN {t.R} ON {t.L}.k = {t.R}.k",
                $"SELECT {t.L}.id AS lid, {t.R}.rid AS rid FROM {t.L}, {t.R} WHERE {t.L}.k = {t.R}.k",
            })));

        yield return Read(O, "order-join-ties-top-and-group-ties", ["PAR-ORDER", "SQL-J", "SQL-REL"],
            "Which tied join pairs does ORDER BY (JL.k; JL.k DESC; JR.k, JL.id) return over JL INNER JOIN JR on k and over JLI INNER JOIN JRI, which pairs do TOP 5 and TOP 20 with ORDER BY JL.k return, " +
            "and which groups do TOP 3 over GROUP BY k ORDER BY Count(*) and Count(*) DESC return over JL and OT5 when the counts tie (one GetRows(300) per statement)?",
            p => Fetches(p, "g", new[] { ("JL", "JR"), ("JLI", "JRI") }.SelectMany(t => new[]
                {
                    $"SELECT {t.Item1}.id AS lid, {t.Item2}.rid AS rid FROM {t.Item1} INNER JOIN {t.Item2} ON {t.Item1}.k = {t.Item2}.k ORDER BY {t.Item1}.k",
                    $"SELECT {t.Item1}.id AS lid, {t.Item2}.rid AS rid FROM {t.Item1} INNER JOIN {t.Item2} ON {t.Item1}.k = {t.Item2}.k ORDER BY {t.Item1}.k DESC",
                    $"SELECT {t.Item1}.id AS lid, {t.Item2}.rid AS rid FROM {t.Item1} INNER JOIN {t.Item2} ON {t.Item1}.k = {t.Item2}.k ORDER BY {t.Item2}.k, {t.Item1}.id",
                    $"SELECT TOP 5 {t.Item1}.id AS lid, {t.Item2}.rid AS rid FROM {t.Item1} INNER JOIN {t.Item2} ON {t.Item1}.k = {t.Item2}.k ORDER BY {t.Item1}.k",
                    $"SELECT TOP 20 {t.Item1}.id AS lid, {t.Item2}.rid AS rid FROM {t.Item1} INNER JOIN {t.Item2} ON {t.Item1}.k = {t.Item2}.k ORDER BY {t.Item1}.k",
                })
                .Concat(
                [
                    "SELECT TOP 3 k, Count(*) AS n FROM JL GROUP BY k ORDER BY Count(*)",
                    "SELECT TOP 3 k, Count(*) AS n FROM JL GROUP BY k ORDER BY Count(*) DESC",
                    "SELECT TOP 3 k, Count(*) AS n FROM OT5 GROUP BY k ORDER BY Count(*)",
                    "SELECT TOP 3 k, Count(*) AS n FROM OT5 GROUP BY k ORDER BY Count(*) DESC",
                ])));

        foreach ((string ext, _) in Formats)
        {
            yield return Read(O, "order-groupby-output-order", ["PAR-ORDER", "SQL-KEY-02", "SQL-AGG-FIRST"],
                "Without ORDER BY, in what order do the groups come out of GROUP BY k (with Count, Min, Max, First and Last of ins), GROUP BY s, GROUP BY k, s, GROUP BY k Mod 3 and GROUP BY k HAVING Count(*) > 36, " +
                "for OT5, OT2, OT3 and OT4 (one GetRows(100) per statement)?",
                p => Fetches(p, "b", four.SelectMany(t => new[]
                {
                    $"SELECT k, Count(*) AS n, Min(ins) AS mi, Max(ins) AS ma, First(ins) AS fi, Last(ins) AS la FROM {t} GROUP BY k",
                    $"SELECT s, Count(*) AS n, First(ins) AS fi FROM {t} GROUP BY s",
                    $"SELECT k, s, Count(*) AS n FROM {t} GROUP BY k, s",
                    $"SELECT k Mod 3 AS m, Count(*) AS n FROM {t} GROUP BY k Mod 3",
                    $"SELECT k, Count(*) AS n FROM {t} GROUP BY k HAVING Count(*) > 36",
                }), 100), ext);
        }

        yield return Read(O, "order-groupby-text-representative", ["PAR-ORDER", "SQL-KEY-02", "SQL-KEY-03"],
            "Over the twelve Text values of OG and its indexed copy OGI (b, A, a, B, c, a, C, b, B, A, a-umlaut, a in insertion order), which spelling and which row (First, Last, Min of id) represents each group, and in what order do the groups, DISTINCT values, " +
            "and ties under ORDER BY t come out without further ordering (one GetRows(100) per statement)?",
            p => Fetches(p, "x", new[] { "OG", "OGI" }.SelectMany(t => new[]
            {
                $"SELECT t, Count(*) AS n, First(id) AS fi, Last(id) AS la, Min(id) AS mi FROM {t} GROUP BY t",
                $"SELECT DISTINCT t FROM {t}",
                $"SELECT t FROM {t} GROUP BY t",
                $"SELECT t, id FROM {t} ORDER BY t",
                $"SELECT DISTINCT t, n FROM {t}",
            }), 100));

        yield return Read(O, "order-distinct-without-order-by", ["PAR-ORDER", "SQL-KEY-02"],
            "Without ORDER BY, in what order do the rows come out of SELECT DISTINCT k, DISTINCT k, s, DISTINCTROW k, DISTINCT TOP 3 k, and DISTINCT k ORDER BY k DESC, for OT5, OT2, OT3 and OT4 (one GetRows(100) per statement)?",
            p => Fetches(p, "d", four.SelectMany(t => new[]
            {
                $"SELECT DISTINCT k FROM {t}",
                $"SELECT DISTINCT k, s FROM {t}",
                $"SELECT DISTINCTROW k FROM {t}",
                $"SELECT DISTINCT TOP 3 k FROM {t}",
                $"SELECT DISTINCT k FROM {t} ORDER BY k DESC",
            }), 100));

        yield return Read(O, "order-recordset-types", ["PAR-ORDER", "SQL-REL"],
            "Do the snapshot, dynaset and forward-only recordsets of the same statement give the same row order and the same tied rows: TOP 20 ins ORDER BY k, ins WHERE k = 2, ins unordered, and GROUP BY k with Count, on OT3 (one GetRows(300) per statement and type)?",
            p =>
            {
                string[] sqls =
                [
                    "SELECT TOP 20 ins FROM OT3 ORDER BY k",
                    "SELECT ins FROM OT3 WHERE k = 2",
                    "SELECT ins FROM OT3",
                    "SELECT k, Count(*) AS n FROM OT3 GROUP BY k",
                ];
                int i = 0;
                foreach (int type in new[] { Dao.OpenSnapshot, Dao.OpenDynaset, Dao.OpenForwardOnly })
                {
                    foreach (string sql in sqls)
                    {
                        Fetch(p, $"r{++i}", sql, 300, type);
                    }
                }
            });

        yield return Read(O, "order-after-delete-insert-update", ["PAR-ORDER", "SQL-REL"],
            "How do scan order, ORDER BY k tie order and TOP 10 with ORDER BY k change on OT5 and OT3 after DELETE of every third ins, then INSERT ... SELECT of the first 20 ins rows again with ins + 1000 and pk - 500, then UPDATE k = 6 - k for every fifth ins? " +
            "Reads SELECT ins, ORDER BY k and TOP 10 ORDER BY k for both tables before and after each of the three writes (one GetRows(300) per read).",
            p =>
            {
                string[] tables = ["OT5", "OT3"];
                string[] writes =
                [
                    "DELETE FROM {0} WHERE ins Mod 3 = 0",
                    "INSERT INTO {0} (pk, k, ins, s) SELECT pk - 500, k, ins + 1000, s FROM {0} WHERE ins <= 20 ORDER BY ins",
                    "UPDATE {0} SET k = 6 - k WHERE ins Mod 5 = 0",
                ];
                int i = 0;
                void Reads(string phase)
                {
                    foreach (string t in tables)
                    {
                        Fetches(p, $"{phase}{t}{++i}_",
                        [
                            $"SELECT ins FROM {t}",
                            $"SELECT ins FROM {t} ORDER BY k",
                            $"SELECT TOP 10 ins FROM {t} ORDER BY k",
                        ]);
                    }
                }
                Reads("a");
                for (int w = 0; w < writes.Length; w++)
                {
                    foreach (string t in tables)
                    {
                        X(p, string.Format(System.Globalization.CultureInfo.InvariantCulture, writes[w], t));
                        p.Get("db", "RecordsAffected");
                    }
                    Reads($"w{w}");
                }
            }, writable: true);
    }

    // Error fixture ------------------------------------------------------------------------------------------------

    private static Probe CreateError(string ext, int version) =>
        Create("error", ext, version, ["PAR-ERROR"],
            "DG(d 0..7), NUM(n 1..512), SM(id 1..49), and ER(id Long PK, pad Text(120) of 100 spaces, zf, zm, zl, zp, zq, tx Text, tq Text, ov Long) with 240 rows inserted in id order, every value 1 or '1' except: " +
            "zf = 0 at id 3, zm = 0 at id 100, zl = 0 at id 240, zp = 0 at ids 50 and 150, zq = 0 at id 80, tx = 'x' at id 60, tq = 'x' at id 80, ov = 100000 at id 150 (bad rows at an early, a middle and the last row, two in one column, one row with two bad columns, and three different bad rows for three error kinds).",
            p =>
            {
                Numbers(p, 8);
                X(p, "CREATE TABLE SM (id LONG CONSTRAINT PK_SM PRIMARY KEY)");
                X(p, "INSERT INTO SM (id) SELECT n FROM NUM WHERE n < 50 ORDER BY n");
                X(p, "CREATE TABLE ER (id LONG CONSTRAINT PK_ER PRIMARY KEY, pad TEXT(120), zf LONG, zm LONG, zl LONG, zp LONG, zq LONG, tx TEXT(10), tq TEXT(10), ov LONG)");
                X(p, "INSERT INTO ER (id, pad, zf, zm, zl, zp, zq, tx, tq, ov) SELECT n, Space(100), IIf(n = 3, 0, 1), IIf(n = 100, 0, 1), IIf(n = 240, 0, 1), IIf(n = 50 Or n = 150, 0, 1), " +
                    "IIf(n = 80, 0, 1), IIf(n = 60, 'x', '1'), IIf(n = 80, 'x', '1'), IIf(n = 150, 100000, 1) FROM NUM WHERE n <= 240 ORDER BY n");
                Qs(p, "rb",
                [
                    "SELECT Count(*) AS n, Min(id) AS lo, Max(id) AS hi FROM ER",
                    "SELECT id, zf, zm, zl, zp, zq, tx, tq, ov FROM ER WHERE zf = 0 OR zm = 0 OR zl = 0 OR zp = 0 OR zq = 0 OR tx = 'x' OR tq = 'x' OR ov <> 1",
                    "SELECT Count(*) AS n FROM SM",
                ], maxRows: 16);
            });

    // Error --------------------------------------------------------------------------------------------------------

    private static IEnumerable<Probe> Errors()
    {
        const string E = "error";

        yield return Read(E, "err-snapshot-first-bad-row", ["PAR-ERROR", "SQL-DEMAND"],
            "For SELECT id, 1 / zf AS e FROM ER (zf = 0 at id 3) as a snapshot: at which call does the division error appear (OpenRecordset, BOF/EOF read, the Value read of e at row 3, the MoveNext before or after it), what do rows 1 and 2 give, " +
            "and what do the calls after the error give (up to 10 rows stepped with Fields id and e read, then RecordCount)?",
            p =>
            {
                p.OpenRs("rs", "db", "SELECT id, 1 / zf AS e FROM ER", Dao.OpenSnapshot).Get("rs", "BOF").Get("rs", "EOF");
                Step(p, "rs", 10);
                p.Get("rs", "RecordCount").Call("rs", "Close").Release("rs").Errors();
            });

        foreach ((string ext, _) in Formats)
        {
            yield return Read(E, "err-snapshot-mid-bad-row-step", ["PAR-ERROR", "SQL-DEMAND"],
                "For SELECT id, 1 / zm AS e FROM ER (zm = 0 at id 100 of 240 rows) as a snapshot: at which call does the division error appear (OpenRecordset, the first reads, a row before id 100, the Value read of e at row 100, the MoveNext after it), " +
                "and what do the calls after it give? Steps 105 rows reading Fields id and e before each MoveNext.",
                p =>
                {
                    p.OpenRs("rs", "db", "SELECT id, 1 / zm AS e FROM ER", Dao.OpenSnapshot).Get("rs", "EOF");
                    Step(p, "rs", 105);
                    p.Call("rs", "Close").Release("rs").Errors();
                }, ext);
        }

        yield return Read(E, "err-snapshot-mid-bad-row-movelast", ["PAR-ERROR", "SQL-DEMAND"],
            "For SELECT id, 1 / zm AS e FROM ER (zm = 0 at id 100) as a snapshot: does MoveLast, RecordCount or EOF raise the error, which rows can be read afterwards (MoveFirst; Move(98) to id 99; then four rows with Fields id and e read and MoveNext, crossing id 100)?",
            p =>
            {
                p.OpenRs("rs", "db", "SELECT id, 1 / zm AS e FROM ER", Dao.OpenSnapshot).Call("rs", "MoveLast").Get("rs", "RecordCount").Get("rs", "EOF");
                At(p, "rs");
                p.Call("rs", "MoveFirst");
                At(p, "rs");
                p.Call("rs", "Move", A.I4(98));
                for (int i = 0; i < 4; i++)
                {
                    At(p, "rs");
                    p.Call("rs", "MoveNext");
                }
                p.Get("rs", "EOF").Call("rs", "Close").Release("rs").Errors();
            });

        yield return Read(E, "err-snapshot-last-bad-row", ["PAR-ERROR", "SQL-DEMAND"],
            "For SELECT id, 1 / zl AS e FROM ER (zl = 0 at id 240, the last row) as a snapshot: which of OpenRecordset, EOF, MoveLast, RecordCount, the read of e at the last row, or MoveNext past it raises the error; " +
            "reads id and e after MoveLast, after MoveFirst, and for five rows from Move(236) to EOF.",
            p =>
            {
                p.OpenRs("rs", "db", "SELECT id, 1 / zl AS e FROM ER", Dao.OpenSnapshot).Get("rs", "EOF");
                At(p, "rs");
                p.Call("rs", "MoveLast");
                At(p, "rs");
                p.Get("rs", "RecordCount").Call("rs", "MoveFirst");
                At(p, "rs");
                p.Call("rs", "Move", A.I4(236));
                for (int i = 0; i < 5; i++)
                {
                    At(p, "rs");
                    p.Call("rs", "MoveNext");
                }
                p.Get("rs", "EOF").Call("rs", "Close").Release("rs").Errors();
            });

        yield return Read(E, "err-dynaset-mid-bad-row-step", ["PAR-ERROR", "SQL-DEMAND"],
            "For SELECT id, 1 / zm AS e FROM ER (zm = 0 at id 100) as a dynaset: at which call does the division error appear and what follows? Steps 105 rows reading Fields id and e before each MoveNext.",
            p =>
            {
                p.OpenRs("rs", "db", "SELECT id, 1 / zm AS e FROM ER", Dao.OpenDynaset).Get("rs", "EOF");
                Step(p, "rs", 105);
                p.Call("rs", "Close").Release("rs").Errors();
            });

        yield return Read(E, "err-forwardonly-mid-bad-row-step", ["PAR-ERROR", "SQL-DEMAND"],
            "For SELECT id, 1 / zm AS e FROM ER (zm = 0 at id 100) as a forward-only recordset: at which call does the division error appear and what follows? Steps 105 rows reading Fields id and e before each MoveNext.",
            p =>
            {
                p.OpenRs("rs", "db", "SELECT id, 1 / zm AS e FROM ER", Dao.OpenForwardOnly).Get("rs", "EOF");
                Step(p, "rs", 105);
                p.Call("rs", "Close").Release("rs").Errors();
            });

        yield return Read(E, "err-getrows-chunks", ["PAR-ERROR", "SQL-DEMAND"],
            "For SELECT id, 1 / zm AS e FROM ER (zm = 0 at id 100) as a snapshot, a dynaset and a forward-only recordset read by nine successive GetRows(30) calls: which call fails, what array (if any) does it return, " +
            "and what do the later calls and EOF give?",
            p =>
            {
                Chunks(p, "s", "SELECT id, 1 / zm AS e FROM ER", 30, 9, Dao.OpenSnapshot);
                Chunks(p, "d", "SELECT id, 1 / zm AS e FROM ER", 30, 9, Dao.OpenDynaset);
                Chunks(p, "f", "SELECT id, 1 / zm AS e FROM ER", 30, 9, Dao.OpenForwardOnly);
            });

        yield return Read(E, "err-where-evaluation-order", ["PAR-ERROR", "SQL-DEMAND"],
            "With zm = 0 at id 100, which of these WHERE forms raise the division error and at which call (open or the GetRows(300)), and which rows come back: 1 / zm > 0; id < 50 AND 1 / zm > 0; 1 / zm > 0 AND id < 50; id < 50 OR 1 / zm > 0; " +
            "id >= 150 AND 1 / zm > 0; id BETWEEN 1 AND 40 AND 1 / zm > 0; IIf(id < 50, True, 1 / zm > 0); and the expression in the select list with id < 50 in WHERE?",
            p => Fetches(p, "w",
            [
                "SELECT id FROM ER WHERE 1 / zm > 0",
                "SELECT id FROM ER WHERE id < 50 AND 1 / zm > 0",
                "SELECT id FROM ER WHERE 1 / zm > 0 AND id < 50",
                "SELECT id FROM ER WHERE id < 50 OR 1 / zm > 0",
                "SELECT id FROM ER WHERE id >= 150 AND 1 / zm > 0",
                "SELECT id FROM ER WHERE id BETWEEN 1 AND 40 AND 1 / zm > 0",
                "SELECT id FROM ER WHERE IIf(id < 50, True, 1 / zm > 0)",
                "SELECT id, 1 / zm AS e FROM ER WHERE id < 50",
            ]));

        yield return Read(E, "err-top-stops-before-bad-row", ["PAR-ERROR", "SQL-DEMAND", "SQL-TOP-01"],
            "With zm = 0 at id 100 (and zl = 0 at id 240), does TOP stop before the bad row without error, and when is the error raised if the cutoff includes it: TOP 10 with no ORDER BY; ORDER BY id; ORDER BY id DESC; TOP 150 and TOP 99 and TOP 100 with ORDER BY id; " +
            "TOP 5 ORDER BY 1 / zm; TOP 239 and TOP 240 over zl with ORDER BY id; and TOP 10, 50 and 100 PERCENT of the rows (one GetRows(300) per statement)?",
            p => Fetches(p, "t",
            [
                "SELECT TOP 10 id, 1 / zm AS e FROM ER",
                "SELECT TOP 10 id, 1 / zm AS e FROM ER ORDER BY id",
                "SELECT TOP 10 id, 1 / zm AS e FROM ER ORDER BY id DESC",
                "SELECT TOP 99 id, 1 / zm AS e FROM ER ORDER BY id",
                "SELECT TOP 100 id, 1 / zm AS e FROM ER ORDER BY id",
                "SELECT TOP 150 id, 1 / zm AS e FROM ER ORDER BY id",
                "SELECT TOP 5 id, 1 / zm AS e FROM ER ORDER BY 1 / zm",
                "SELECT TOP 239 id, 1 / zl AS e FROM ER ORDER BY id",
                "SELECT TOP 240 id, 1 / zl AS e FROM ER ORDER BY id",
                "SELECT TOP 10 PERCENT id, 1 / zm AS e FROM ER",
                "SELECT TOP 50 PERCENT id, 1 / zm AS e FROM ER ORDER BY id",
                "SELECT TOP 100 PERCENT id, 1 / zm AS e FROM ER ORDER BY id",
            ]));

        yield return Read(E, "err-aggregate-and-groups", ["PAR-ERROR", "SQL-DEMAND", "SQL-AGG-OCCUR"],
            "At which call does an erroring argument surface inside an aggregate (zm = 0 at id 100, zl = 0 at id 240, zf = 0 at id 3): Sum, Avg, Count, Min and Max of 1 / zm; Sum of 1 / zl and of 1 / zf; Count(*) beside Sum(1 / zm); Sum with WHERE id <> 100, id < 100 and id <= 100; " +
            "Sum over derived tables with TOP 10, TOP 150 ORDER BY id and WHERE id < 50; and grouped forms (GROUP BY id Mod 2; GROUP BY id \\ 100 with and without HAVING Sum(1 / zm) > 0; GROUP BY CLng(tx); DISTINCT 1 / zm)?",
            p => Qs(p, "a",
            [
                "SELECT Sum(1 / zm) AS s FROM ER",
                "SELECT Avg(1 / zm) AS a FROM ER",
                "SELECT Count(1 / zm) AS c FROM ER",
                "SELECT Min(1 / zm) AS mn, Max(1 / zm) AS mx FROM ER",
                "SELECT Sum(1 / zl) AS s FROM ER",
                "SELECT Sum(1 / zf) AS s FROM ER",
                "SELECT Count(*) AS c, Sum(1 / zm) AS s FROM ER",
                "SELECT Sum(1 / zm) AS s FROM ER WHERE id <> 100",
                "SELECT Sum(1 / zm) AS s FROM ER WHERE id < 100",
                "SELECT Sum(1 / zm) AS s FROM ER WHERE id <= 100",
                "SELECT Sum(e) AS s FROM (SELECT TOP 10 1 / zm AS e FROM ER)",
                "SELECT Sum(e) AS s FROM (SELECT TOP 150 1 / zm AS e FROM ER ORDER BY id)",
                "SELECT Sum(e) AS s FROM (SELECT 1 / zm AS e FROM ER WHERE id < 50)",
                "SELECT id Mod 2 AS p, Sum(1 / zm) AS s FROM ER GROUP BY id Mod 2",
                "SELECT id \\ 100 AS b, Sum(1 / zm) AS s FROM ER GROUP BY id \\ 100",
                "SELECT id \\ 100 AS b, Sum(1 / zm) AS s FROM ER GROUP BY id \\ 100 HAVING Sum(1 / zm) > 0",
                "SELECT CLng(tx) AS v, Count(*) AS n FROM ER GROUP BY CLng(tx)",
                "SELECT DISTINCT 1 / zm AS e FROM ER",
            ], maxRows: 8));

        yield return Read(E, "err-two-errors-two-rows", ["PAR-ERROR", "SQL-DEMAND"],
            "Which error is reported first when three different bad rows exist (tx = 'x' at id 60 for CLng, zm = 0 at id 100 for the division, ov = 100000 at id 150 for CInt): SELECT id, CLng(tx), 1 / zm, CInt(ov) in id order, in id DESC order, " +
            "without the CLng column, and with the columns in another order; nine GetRows(30) calls per statement record which call fails, with which error, and what the later calls give.",
            p =>
            {
                Chunks(p, "a", "SELECT id, CLng(tx) AS a, 1 / zm AS b, CInt(ov) AS c FROM ER", 30, 9);
                Chunks(p, "b", "SELECT id, CLng(tx) AS a, 1 / zm AS b, CInt(ov) AS c FROM ER ORDER BY id DESC", 30, 9);
                Chunks(p, "c", "SELECT id, 1 / zm AS b, CInt(ov) AS c FROM ER", 30, 9);
                Chunks(p, "d", "SELECT id, CInt(ov) AS c, 1 / zm AS b, CLng(tx) AS a FROM ER ORDER BY id", 30, 9);
            });

        yield return Read(E, "err-two-errors-one-row", ["PAR-ERROR", "SQL-DEMAND"],
            "Which error is reported for a row with two bad columns (id 80: zq = 0 and tq = 'x'): SELECT id, CLng(tq) AS a, 1 / zq AS b and the swapped column order, each with a read of a then b, b then a, and one GetRows(1); " +
            "what do the Field reads give after the first error?",
            p =>
            {
                string[] sqls =
                [
                    "SELECT id, CLng(tq) AS a, 1 / zq AS b FROM ER WHERE id = 80",
                    "SELECT id, 1 / zq AS b, CLng(tq) AS a FROM ER WHERE id = 80",
                ];
                for (int s = 0; s < sqls.Length; s++)
                {
                    foreach (string[] order in new[] { new[] { "a", "b" }, new[] { "b", "a" } })
                    {
                        string rs = $"r{s}{order[0]}";
                        p.OpenRs(rs, "db", sqls[s], Dao.OpenSnapshot).GetBind(rs + "_f", rs, "Fields");
                        foreach (string f in order)
                        {
                            p.Item($"{rs}_{f}", rs + "_f", A.Str(f)).Get($"{rs}_{f}", "Value").Release($"{rs}_{f}");
                        }
                        p.Release(rs + "_f").Call(rs, "Close").Release(rs).Errors();
                    }
                    Fetch(p, $"g{s}", sqls[s], 1);
                }
            });

        string[] kindExprs =
        [
            "1 / (id - 3)",
            "CByte(id * 100)",
            "CLng(IIf(id = 3, 'x', '1'))",
            "Sqr(id - 4)",
            "Log(id - 3)",
            "CDate(IIf(id = 3, 'x', '1/1/2000'))",
            "2147483645 + id",
        ];
        yield return Read(E, "err-expression-kinds-phase", ["PAR-ERROR", "SQL-DEMAND", "SQL-REL"],
            "At which call does each error kind appear, and with which number and description, when row id 3 is the first bad row: 1 / (id - 3); CByte(id * 100); CLng of a non-numeric Text; Sqr(id - 4); Log(id - 3); CDate of a non-date Text; 2147483645 + id; " +
            "and for constant expressions with no column (1 / 0, CByte(300), CLng('x'), alone and beside id), does the error appear at OpenRecordset or at a row (one GetRows(5) per statement)?",
            p => Fetches(p, "k",
            [
                .. kindExprs.Select(e => $"SELECT id, {e} AS e FROM ER"),
                "SELECT 1 / 0 AS e FROM ER",
                "SELECT id, 1 / 0 AS e FROM ER",
                "SELECT id, CByte(300) AS e FROM ER",
                "SELECT id, CLng('x') AS e FROM ER",
            ], 5));

        yield return Read(E, "err-revisit-bad-row", ["PAR-ERROR", "SQL-DEMAND"],
            "After the error at id 100 of SELECT id, 1 / zm AS e FROM ER: is it raised again when the row is read twice, after MovePrevious from id 101, after MoveFirst and Move(98) back to id 99 and forward again, and does Move(149) past it (to id 150) read normally; " +
            "asked for a snapshot and a dynaset (Fields id and e read at each position).",
            p =>
            {
                foreach ((string name, int type) in new[] { ("s", Dao.OpenSnapshot), ("d", Dao.OpenDynaset) })
                {
                    string rs = "v" + name;
                    p.OpenRs(rs, "db", "SELECT id, 1 / zm AS e FROM ER", type).Call(rs, "MoveFirst").Call(rs, "Move", A.I4(98));
                    At(p, rs);
                    p.Call(rs, "MoveNext");
                    At(p, rs);
                    At(p, rs);
                    p.Call(rs, "MoveNext");
                    At(p, rs);
                    p.Call(rs, "MovePrevious");
                    At(p, rs);
                    p.Call(rs, "MovePrevious");
                    At(p, rs);
                    p.Call(rs, "MoveFirst").Call(rs, "Move", A.I4(98));
                    At(p, rs);
                    p.Call(rs, "MoveNext");
                    At(p, rs);
                    p.Call(rs, "MoveFirst").Call(rs, "Move", A.I4(149));
                    At(p, rs);
                    p.Call(rs, "MoveNext");
                    At(p, rs);
                    p.Call(rs, "Close").Release(rs).Errors();
                }
            });

        yield return Read(E, "err-join-excludes-bad-row", ["PAR-ERROR", "SQL-DEMAND", "SQL-J"],
            "Is the error of zm = 0 at id 100 raised when the join or a subquery removes row 100 before the expression is read: ER INNER JOIN SM (ids 1 to 49) with the division in the select list, in both FROM orders, with the division in WHERE, ER LEFT JOIN SM, " +
            "WHERE id IN (SELECT id FROM SM), and WHERE EXISTS over SM (one GetRows(300) per statement)?",
            p => Fetches(p, "j",
            [
                "SELECT ER.id, 1 / ER.zm AS e FROM ER INNER JOIN SM ON ER.id = SM.id",
                "SELECT ER.id, 1 / ER.zm AS e FROM SM INNER JOIN ER ON ER.id = SM.id",
                "SELECT ER.id FROM ER INNER JOIN SM ON ER.id = SM.id WHERE 1 / ER.zm > 0",
                "SELECT ER.id, 1 / ER.zm AS e FROM ER LEFT JOIN SM ON ER.id = SM.id",
                "SELECT id, 1 / zm AS e FROM ER WHERE id IN (SELECT id FROM SM)",
                "SELECT id, 1 / zm AS e FROM ER WHERE EXISTS (SELECT 1 FROM SM WHERE SM.id = ER.id)",
            ]));

        yield return Read(E, "err-union-bad-late-arm", ["PAR-ERROR", "SQL-DEMAND", "SQL-UNION-04"],
            "With arm A = ids below 50 of ER (49 rows) and arm B = CLng(tx) over all of ER (tx = 'x' at id 60): are the rows of A delivered before the error of B, and which GetRows(30) call fails, for A UNION ALL B, A UNION B, B UNION ALL A, A UNION ALL B ORDER BY v, " +
            "TOP 5 over A UNION ALL B (outer and inside the arms), each read by eight successive GetRows(30) calls?",
            p =>
            {
                const string a = "SELECT id AS v FROM ER WHERE id < 50";
                const string b = "SELECT CLng(tx) AS v FROM ER";
                string[] sqls =
                [
                    $"{a} UNION ALL {b}",
                    $"{a} UNION {b}",
                    $"{b} UNION ALL {a}",
                    $"{a} UNION ALL {b} ORDER BY v",
                    $"SELECT TOP 5 v FROM ({a} UNION ALL {b})",
                    $"SELECT TOP 5 id AS v FROM ER WHERE id < 50 UNION ALL SELECT TOP 5 CLng(tx) FROM ER",
                ];
                for (int i = 0; i < sqls.Length; i++)
                {
                    Chunks(p, $"u{i}", sqls[i], 30, 8);
                }
            });

        yield return Read(E, "err-write-statements-bad-row", ["PAR-ERROR", "SQL-DEMAND", "ERR-PARTIAL"],
            "What is written when a row in the middle of the scan raises (zm = 0 at id 100) during a write statement: INSERT INTO SINK SELECT id, 1 / zm without and with dbFailOnError, UPDATE of a copy EU SET w = 5 / w without and with dbFailOnError, " +
            "and a make-table SELECT ... INTO EM; reads RecordsAffected, and the row counts and values afterwards (SINK count and Max(id); EU rows with w = 5; whether EM exists).",
            p =>
            {
                X(p, "CREATE TABLE SINK (id LONG, e LONG)");
                p.Call("db", "Execute", A.Str("INSERT INTO SINK (id, e) SELECT id, 1 / zm FROM ER")).Get("db", "RecordsAffected").Errors();
                Q(p, "k1", "SELECT Count(*) AS n, Max(id) AS hi FROM SINK");
                X(p, "DELETE FROM SINK");
                p.Call("db", "Execute", A.Str("INSERT INTO SINK (id, e) SELECT id, 1 / zm FROM ER"), A.I4(Dao.FailOnError)).Get("db", "RecordsAffected").Errors();
                Q(p, "k2", "SELECT Count(*) AS n, Max(id) AS hi FROM SINK");
                X(p, "SELECT id, zm AS w INTO EU FROM ER");
                p.Call("db", "Execute", A.Str("UPDATE EU SET w = 5 / w")).Get("db", "RecordsAffected").Errors();
                Q(p, "k3", "SELECT Count(*) AS n, Sum(IIf(w = 5, 1, 0)) AS updated FROM EU");
                X(p, "UPDATE EU SET w = 1");
                X(p, "UPDATE EU SET w = 0 WHERE id = 100");
                p.Call("db", "Execute", A.Str("UPDATE EU SET w = 5 / w"), A.I4(Dao.FailOnError)).Get("db", "RecordsAffected").Errors();
                Q(p, "k4", "SELECT Count(*) AS n, Sum(IIf(w = 5, 1, 0)) AS updated FROM EU");
                p.Call("db", "Execute", A.Str("SELECT id, 1 / zm AS e INTO EM FROM ER")).Get("db", "RecordsAffected").Errors();
                Q(p, "k5", "SELECT Count(*) AS n FROM EM");
            }, writable: true);
    }
}
