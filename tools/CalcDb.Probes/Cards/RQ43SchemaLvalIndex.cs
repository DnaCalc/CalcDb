namespace CalcDb.Probes.Cards;

/// <summary>
/// Wave-2 ready-gate probes (r-q43-schema-lval-index):
/// (1) <c>CREATE INDEX</c> on ACCDB and Jet 4 MDB tables that already have LVAL (<c>MEMO</c>/<c>LONGBINARY</c>)
/// columns AND existing indexes or rows (41 wide stops in <c>NotModelled.SchemaWriter</c>), keeping ALL file
/// pages before and after (<c>nativeRead keep:true</c>);
/// (2) ADO <c>ALTER TABLE ADD CONSTRAINT CHECK</c> on ACCDB and Jet 4 MDB tables whose <c>MSysObjects.LvProp</c>
/// already holds column or table properties (<c>NOT NULL</c>, <c>AllowZeroLength</c>, <c>ValidationRule</c>;
/// 8 wide stops in <c>NotModelled.SchemaWriter</c>), keeping ALL file pages before and after;
/// (3) <c>BooleanComparison</c> discriminators across <c>=</c>, <c>&lt;&gt;</c>, <c>&lt;</c>, <c>&lt;=</c>,
/// <c>&gt;</c>, <c>&gt;=</c> and all Boolean origins (literals, <c>CBool</c>, comparison results, stored
/// <c>YESNO</c> fields) to unblock <c>nk97</c> (<c>ScalarKernel.cs</c>);
/// (4) Read-only <c>Database.Execute</c> DDL/DML discriminator alone and beside a read/write handle
/// (<c>NotModelled.ReadOnlyAction</c>).
/// </summary>
public sealed class RQ43SchemaLvalIndex : ICard
{
    private const string Card = "r-q43-schema-lval-index";
    private const string EmptyAccdb = "r-cat-1-accdb-empty";
    private const string EmptyMdb = "r-cat-1-mdb-empty";
    private const string ExprAccdb = "r-expr-1b-main";
    private const string ExprMdb = "r-expr-1b-mdb";
    private const string LvalAccdb = "r-q43-lval-accdb";
    private const string LvalMdb = "r-q43-lval-mdb";
    private const string AceProvider = "Microsoft.ACE.OLEDB.12.0";
    private const int FullReadBytes = 524288;

    private static readonly string[] SchemaRefs = ["W-07", "II-04", "II-05", "II-06", "R-CAT-1", "R-FMT-2", "R-PRESERVE-1"];
    private static readonly string[] BoolRefs = ["W-02", "W-06", "R-EXPR-1", "T-EXPR-12", "nk97"];
    private static readonly string[] RoRefs = ["W-08", "R-SQL-2", "R-LOCK-1"];

    public IEnumerable<Probe> Probes()
    {
        foreach (Probe p in FixtureCreators()) yield return p;
        foreach (Probe p in LvalIndexProbes()) yield return p;
        foreach (Probe p in CheckLvPropProbes()) yield return p;
        foreach (Probe p in BooleanComparisonProbes()) yield return p;
        foreach (Probe p in ReadOnlyDdlProbes()) yield return p;
    }

    // ------------------------------------------------------------------------------------------------
    // 0. Frozen populated LVAL fixtures (ACCDB + Jet 4 MDB)

    private static IEnumerable<Probe> FixtureCreators()
    {
        yield return CreateLvalFixture("create-lval-accdb", "r-q43-lval-accdb.accdb", Dao.Version120);
        yield return CreateLvalFixture("create-lval-mdb", "r-q43-lval-mdb.mdb", Dao.Version40);
    }

    private static Probe CreateLvalFixture(string name, string fileName, int version) => new(
        Card,
        name,
        SchemaRefs,
        $"Create populated LVAL fixture {fileName} with table T (PK on ID, Long K/U, Text S, Memo M, LongBinary B) and 4 rows spanning inline, Null, and external single-row LVAL values.",
        p =>
        {
            byte[] smallBin = [1, 2, 3, 4, 5, 6, 7, 8];
            byte[] largeBin = new byte[200];
            for (int i = 0; i < largeBin.Length; i++)
            {
                largeBin[i] = (byte)((i * 7 + 3) & 0xFF);
            }

            p.EngineLite()
                .CallBind("db", "eng", "CreateDatabase", A.Out(fileName), A.Str(Dao.LangGeneral), A.I4(version))
                .Call("db", "Execute", A.Str("CREATE TABLE T (ID LONG CONSTRAINT PK PRIMARY KEY, K LONG, U LONG, S TEXT(40), M MEMO, B LONGBINARY)"), A.I4(Dao.FailOnError))
                .OpenRs("rs", "db", "T", Dao.OpenTable);

            AddLvalRow(p, "rs", 1, 20, 30, "beta", "short", smallBin);
            AddLvalRow(p, "rs", 2, 10, 10, "alpha", null, null);
            AddLvalRow(p, "rs", 3, 20, 40, "gamma", new string('M', 200), largeBin);
            AddLvalRow(p, "rs", 4, null, 20, "delta", new string('N', 120), null);

            p.Call("rs", "Close").Release("rs")
                .Call("db", "Close").Release("db")
                .Release("eng")
                .Files("after-close", keep: true);
        })
    {
        CreatesFixture = true,
    };

    private static void AddLvalRow(P p, string rs, int id, int? k, int u, string s, string? m, byte[]? b)
    {
        p.Call(rs, "AddNew")
            .GetBind("fs", rs, "Fields");
        SetField(p, "fs", "ID", A.I4(id));
        SetField(p, "fs", "K", k is { } kv ? A.I4(kv) : A.Null);
        SetField(p, "fs", "U", A.I4(u));
        SetField(p, "fs", "S", A.Str(s));
        SetField(p, "fs", "M", m is not null ? A.Str(m) : A.Null);
        SetField(p, "fs", "B", b is not null ? A.Bytes(b) : A.Null);
        p.Release("fs").Call(rs, "Update");
    }

    private static void SetField(P p, string fields, string name, Arg value) =>
        p.Item("fld", fields, A.Str(name)).Put("fld", "Value", value).Release("fld");

    // ------------------------------------------------------------------------------------------------
    // 1. CREATE INDEX on tables with LVAL columns and existing indexes or rows

    private static IEnumerable<Probe> LvalIndexProbes()
    {
        yield return InlineSetupDaoIndex(
            "ix-lval-pk-empty-accdb",
            EmptyAccdb,
            "ACCDB DAO: CREATE INDEX IX_K ON T (K) on empty table with PK + MEMO. Keeps full file after setup and after DDL.",
            ["CREATE TABLE T (ID LONG CONSTRAINT PK PRIMARY KEY, K LONG, M MEMO)"],
            ["CREATE INDEX IX_K ON T (K)"]);

        yield return InlineSetupDaoIndex(
            "ix-lval-two-uq-empty-accdb",
            EmptyAccdb,
            "ACCDB DAO: CREATE UNIQUE INDEX UQ_K ON T (K) on empty table with PK + MEMO + LONGBINARY (2 LVAL cols). Keeps full file before and after.",
            ["CREATE TABLE T (ID LONG CONSTRAINT PK PRIMARY KEY, K LONG, M MEMO, B LONGBINARY)"],
            ["CREATE UNIQUE INDEX UQ_K ON T (K)"]);

        yield return FrozenFixtureDaoIndex(
            "ix-lval-pk-rows-accdb",
            LvalAccdb,
            "ACCDB DAO: CREATE INDEX IX_K ON T (K) on populated frozen LVAL table (PK + MEMO + LONGBINARY + 4 rows with inline/Null/LVAL, duplicate K & Null K). Keeps full file before and after.",
            ["CREATE INDEX IX_K ON T (K)"]);

        yield return InlineSetupDaoIndex(
            "ix-lval-noix-rows-accdb",
            EmptyAccdb,
            "ACCDB DAO: CREATE UNIQUE INDEX UQ_S ON T (S) on populated unindexed MEMO table (with external LVAL Memo row). Keeps full file before and after.",
            [
                "CREATE TABLE T (ID LONG, S TEXT(40), M MEMO)",
                "INSERT INTO T VALUES (1, 'beta', 'short')",
                $"INSERT INTO T VALUES (2, 'alpha', '{new string('M', 200)}')",
                "INSERT INTO T VALUES (3, 'gamma', NULL)",
            ],
            ["CREATE UNIQUE INDEX UQ_S ON T (S)"]);

        yield return FrozenFixtureDaoIndex(
            "ix-lval-second-uq-rows-accdb",
            LvalAccdb,
            "ACCDB DAO: CREATE INDEX IX_K ON T (K) then CREATE UNIQUE INDEX UQ_S ON T (S) on populated frozen LVAL table (chained indexes over LVAL table). Keeps full file before, after 1st, and after 2nd.",
            ["CREATE INDEX IX_K ON T (K)", "CREATE UNIQUE INDEX UQ_S ON T (S)"]);

        yield return InlineSetupDaoIndex(
            "ix-lval-pk-empty-mdb",
            EmptyMdb,
            "Jet 4 MDB DAO: CREATE INDEX IX_K ON T (K) on empty table with PK + MEMO. Keeps full file before and after.",
            ["CREATE TABLE T (ID LONG CONSTRAINT PK PRIMARY KEY, K LONG, M MEMO)"],
            ["CREATE INDEX IX_K ON T (K)"]);

        yield return FrozenFixtureDaoIndex(
            "ix-lval-pk-rows-mdb",
            LvalMdb,
            "Jet 4 MDB DAO: CREATE INDEX IX_K ON T (K) on populated frozen LVAL table (PK + MEMO + LONGBINARY + 4 rows with inline/Null/LVAL). Keeps full file before and after.",
            ["CREATE INDEX IX_K ON T (K)"]);

        yield return FrozenFixtureDaoIndex(
            "ix-lval-two-uq-rows-mdb",
            LvalMdb,
            "Jet 4 MDB DAO: CREATE UNIQUE INDEX UQ_U ON T (U) on populated frozen LVAL table (PK + MEMO + LONGBINARY + 4 rows). Keeps full file before and after.",
            ["CREATE UNIQUE INDEX UQ_U ON T (U)"]);
    }

    private static Probe InlineSetupDaoIndex(string name, string fixture, string description, string[] setupSql, string[] targetSql) => new(
        Card,
        name,
        SchemaRefs,
        description,
        p =>
        {
            p.Files("before-open")
                .Create("DAO.DBEngine.120", "eng0")
                .Modules("after-create")
                .CallBind("db0", "eng0", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false));
            foreach (string sql in setupSql)
            {
                p.Call("db0", "Execute", A.Str(sql), A.I4(Dao.FailOnError));
            }
            p.Call("db0", "Close").Release("db0").Release("eng0");
            KeepAllPages(p, "after-setup", "pg0");

            for (int i = 0; i < targetSql.Length; i++)
            {
                string suffix = targetSql.Length == 1 ? "" : $"-{i + 1}";
                string eng = $"eng{i + 1}", db = $"db{i + 1}", pg = $"pg{i + 1}";
                p.Create("DAO.DBEngine.120", eng)
                    .CallBind(db, eng, "OpenDatabase", A.In(), A.Bool(false), A.Bool(false))
                    .Call(db, "Execute", A.Str(targetSql[i]), A.I4(Dao.FailOnError))
                    .Call(db, "Close").Release(db).Release(eng);
                KeepAllPages(p, "after-ddl" + suffix, pg);
            }
        })
    {
        Fixtures = [("main", fixture)],
    };

    private static Probe FrozenFixtureDaoIndex(string name, string fixture, string description, string[] targetSql) => new(
        Card,
        name,
        SchemaRefs,
        description,
        p =>
        {
            KeepAllPages(p, "before-open", "pg0");
            for (int i = 0; i < targetSql.Length; i++)
            {
                string suffix = targetSql.Length == 1 ? "" : $"-{i + 1}";
                string eng = $"eng{i + 1}", db = $"db{i + 1}", pg = $"pg{i + 1}";
                p.Create("DAO.DBEngine.120", eng);
                if (i == 0)
                {
                    p.Modules("after-create");
                }
                p.CallBind(db, eng, "OpenDatabase", A.In(), A.Bool(false), A.Bool(false))
                    .Call(db, "Execute", A.Str(targetSql[i]), A.I4(Dao.FailOnError))
                    .Call(db, "Close").Release(db).Release(eng);
                KeepAllPages(p, "after-ddl" + suffix, pg);
            }
        })
    {
        Fixtures = [("main", fixture)],
    };

    // ------------------------------------------------------------------------------------------------
    // 2. ALTER TABLE ADD CONSTRAINT CHECK with existing MSysObjects.LvProp properties

    private static IEnumerable<Probe> CheckLvPropProbes()
    {
        yield return AdoCheckAfterAdoSetup(
            "chk-lvprop-notnull-accdb",
            EmptyAccdb,
            "ACCDB ADO: ALTER TABLE T ADD CONSTRAINT CK_V CHECK (V >= 0) after CREATE TABLE with R TEXT(20) NOT NULL (existing LvProp column property). Keeps full file before and after.",
            "CREATE TABLE T (ID LONG CONSTRAINT PK PRIMARY KEY, V LONG, R TEXT(20) NOT NULL)",
            ["ALTER TABLE T ADD CONSTRAINT CK_V CHECK (V >= 0)"]);

        yield return AdoCheckAfterAdoSetup(
            "chk-lvprop-notnull-mdb",
            EmptyMdb,
            "Jet 4 MDB ADO: ALTER TABLE T ADD CONSTRAINT CK_V CHECK (V >= 0) after CREATE TABLE with R TEXT(20) NOT NULL. Keeps full file before and after.",
            "CREATE TABLE T (ID LONG CONSTRAINT PK PRIMARY KEY, V LONG, R TEXT(20) NOT NULL)",
            ["ALTER TABLE T ADD CONSTRAINT CK_V CHECK (V >= 0)"]);

        yield return AdoCheckAfterAdoSetup(
            "chk-lvprop-notnull-two-accdb",
            EmptyAccdb,
            "ACCDB ADO: two ALTER TABLE ADD CONSTRAINT CHECK statements (CK_V1 then CK_V2) on table with R TEXT(20) NOT NULL. Keeps full file after setup, 1st CHECK, and 2nd CHECK.",
            "CREATE TABLE T (ID LONG CONSTRAINT PK PRIMARY KEY, V LONG, R TEXT(20) NOT NULL)",
            [
                "ALTER TABLE T ADD CONSTRAINT CK_V1 CHECK (V >= 0)",
                "ALTER TABLE T ADD CONSTRAINT CK_V2 CHECK (V <= 100)",
            ]);

        yield return AdoCheckAfterDaoPropSetup(
            "chk-lvprop-azl-accdb",
            EmptyAccdb,
            "ACCDB: DAO sets Field.AllowZeroLength = False on T.S, then ADO ALTER TABLE T ADD CONSTRAINT CK_V CHECK (V >= 0). Keeps full file before and after.",
            "CREATE TABLE T (ID LONG CONSTRAINT PK PRIMARY KEY, V LONG, S TEXT(20))",
            p =>
            {
                p.GetBind("tdfs", "db0", "TableDefs").Call("tdfs", "Refresh")
                    .Item("td", "tdfs", A.Str("T"))
                    .GetBind("fs", "td", "Fields")
                    .Item("f", "fs", A.Str("S"))
                    .Put("f", "AllowZeroLength", A.Bool(false))
                    .Release("f").Release("fs").Release("td").Release("tdfs");
            });

        yield return AdoCheckAfterDaoPropSetup(
            "chk-lvprop-azl-mdb",
            EmptyMdb,
            "Jet 4 MDB: DAO sets Field.AllowZeroLength = False on T.S, then ADO ALTER TABLE T ADD CONSTRAINT CK_V CHECK (V >= 0). Keeps full file before and after.",
            "CREATE TABLE T (ID LONG CONSTRAINT PK PRIMARY KEY, V LONG, S TEXT(20))",
            p =>
            {
                p.GetBind("tdfs", "db0", "TableDefs").Call("tdfs", "Refresh")
                    .Item("td", "tdfs", A.Str("T"))
                    .GetBind("fs", "td", "Fields")
                    .Item("f", "fs", A.Str("S"))
                    .Put("f", "AllowZeroLength", A.Bool(false))
                    .Release("f").Release("fs").Release("td").Release("tdfs");
            });

        yield return AdoCheckAfterDaoPropSetup(
            "chk-lvprop-valrule-accdb",
            EmptyAccdb,
            "ACCDB: DAO sets Field.ValidationRule and ValidationText on T.S (already LVAL LvProp), then ADO ALTER TABLE T ADD CONSTRAINT CK_V CHECK (V >= 0). Keeps full file before and after.",
            "CREATE TABLE T (ID LONG CONSTRAINT PK PRIMARY KEY, V LONG, S TEXT(20))",
            p =>
            {
                p.GetBind("tdfs", "db0", "TableDefs").Call("tdfs", "Refresh")
                    .Item("td", "tdfs", A.Str("T"))
                    .GetBind("fs", "td", "Fields")
                    .Item("f", "fs", A.Str("S"))
                    .Put("f", "ValidationRule", A.Str("<> 'bad'"))
                    .Put("f", "ValidationText", A.Str("no bad"))
                    .Release("f").Release("fs").Release("td").Release("tdfs");
            });

        yield return AdoCheckAfterDaoPropSetup(
            "chk-lvprop-valrule-mdb",
            EmptyMdb,
            "Jet 4 MDB: DAO sets Field.ValidationRule and ValidationText on T.S (already LVAL LvProp), then ADO ALTER TABLE T ADD CONSTRAINT CK_V CHECK (V >= 0). Keeps full file before and after.",
            "CREATE TABLE T (ID LONG CONSTRAINT PK PRIMARY KEY, V LONG, S TEXT(20))",
            p =>
            {
                p.GetBind("tdfs", "db0", "TableDefs").Call("tdfs", "Refresh")
                    .Item("td", "tdfs", A.Str("T"))
                    .GetBind("fs", "td", "Fields")
                    .Item("f", "fs", A.Str("S"))
                    .Put("f", "ValidationRule", A.Str("<> 'bad'"))
                    .Put("f", "ValidationText", A.Str("no bad"))
                    .Release("f").Release("fs").Release("td").Release("tdfs");
            });

        yield return AdoCheckAfterDaoPropSetup(
            "chk-lvprop-table-valrule-accdb",
            EmptyAccdb,
            "ACCDB: DAO sets TableDef.ValidationRule on T (existing ObjectValues section in LvProp), then ADO ALTER TABLE T ADD CONSTRAINT CK_V CHECK (V >= 0). Keeps full file before and after.",
            "CREATE TABLE T (ID LONG CONSTRAINT PK PRIMARY KEY, V LONG, W LONG)",
            p =>
            {
                p.GetBind("tdfs", "db0", "TableDefs").Call("tdfs", "Refresh")
                    .Item("td", "tdfs", A.Str("T"))
                    .Put("td", "ValidationRule", A.Str("[V] < [W]"))
                    .Release("td").Release("tdfs");
            });
    }

    private static Probe AdoCheckAfterAdoSetup(string name, string fixture, string description, string createSql, string[] alterSqls) => new(
        Card,
        name,
        SchemaRefs,
        description,
        p =>
        {
            p.Files("before-open");
            AdoOpen(p, "cn0", observeModules: true);
            p.Call("cn0", "Execute", A.Str(createSql), A.Missing, A.I4(1))
                .Call("cn0", "Close").Release("cn0");
            KeepAllPages(p, "after-setup", "pg0");

            for (int i = 0; i < alterSqls.Length; i++)
            {
                string suffix = alterSqls.Length == 1 ? "" : $"-{i + 1}";
                string cn = $"cn{i + 1}", pg = $"pg{i + 1}";
                AdoOpen(p, cn, observeModules: false);
                p.Call(cn, "Execute", A.Str(alterSqls[i]), A.Missing, A.I4(1))
                    .Call(cn, "Close").Release(cn);
                KeepAllPages(p, "after-ddl" + suffix, pg);
            }
        })
    {
        Fixtures = [("main", fixture)],
    };

    private static Probe AdoCheckAfterDaoPropSetup(string name, string fixture, string description, string createSql, Action<P> setProps) => new(
        Card,
        name,
        SchemaRefs,
        description,
        p =>
        {
            p.Files("before-open")
                .Create("DAO.DBEngine.120", "eng0")
                .Modules("after-create")
                .CallBind("db0", "eng0", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false))
                .Call("db0", "Execute", A.Str(createSql), A.I4(Dao.FailOnError));
            setProps(p);
            p.Call("db0", "Close").Release("db0").Release("eng0");
            KeepAllPages(p, "after-setup", "pg0");

            AdoOpen(p, "cn1", observeModules: false);
            p.Call("cn1", "Execute", A.Str("ALTER TABLE T ADD CONSTRAINT CK_V CHECK (V >= 0)"), A.Missing, A.I4(1))
                .Call("cn1", "Close").Release("cn1");
            KeepAllPages(p, "after-ddl", "pg1");
        })
    {
        Fixtures = [("main", fixture)],
    };

    private static void AdoOpen(P p, string cn, bool observeModules)
    {
        string props = $"{cn}_p", ds = $"{cn}_ds";
        p.Create("ADODB.Connection", cn);
        if (observeModules)
        {
            p.Modules("after-create");
        }
        p.Put(cn, "Provider", A.Str(AceProvider))
            .GetBind(props, cn, "Properties")
            .Item(ds, props, A.Str("Data Source"))
            .Put(ds, "Value", A.In())
            .Release(ds).Release(props)
            .Call(cn, "Open");
    }

    private static void KeepAllPages(P p, string label, string handle) =>
        p.FilePages(label, 4096)
            .NativeOpen(handle, "input:main", Win.GenericRead, Win.ShareAll, Win.OpenExisting)
            .NativeRead(handle, 0, FullReadBytes, keep: true)
            .NativeClose(handle);

    // ------------------------------------------------------------------------------------------------
    // 3. BooleanComparison discriminators (nk97 / ScalarKernel.cs)

    private static IEnumerable<Probe> BooleanComparisonProbes()
    {
        string[] eqNeExprs =
        [
            // Bool vs numbers (= and <>, both orders)
            "True = 0", "True = 1", "True = -1", "True = 2", "True = -2", "True = 0.5", "True = -0.5",
            "True = CCur(0)", "True = CCur(1)", "True = CCur(-1)", "True = CCur(2)", "True = CSng(1)", "True = CByte(1)",
            "0 = True", "2 = True", "-1 = True", "-2 = True",
            "False = 0", "False = 1", "False = -1", "False = 2", "False = 0.5", "0 = False", "1 = False", "-1 = False",
            "True <> 0", "True <> 1", "True <> -1", "True <> 2", "False <> 0", "False <> 1", "False <> -1", "2 <> True", "0 <> False",
            // Bool vs strings (= and <>, both orders)
            "True = ''", "True = '0'", "True = '1'", "True = '-1'", "True = '2'", "True = 'x'", "True = 'True'", "True = 'False'", "True = ' '",
            "'x' = True", "'' = True", "'0' = True",
            "False = ''", "False = '0'", "False = '1'", "False = '-1'", "False = 'x'", "False = 'True'", "False = 'False'", "False = ' '",
            "'' = False", "'0' = False", "'x' = False",
            "True <> ''", "True <> '0'", "True <> '1'", "True <> 'x'", "True <> 'False'",
            "False <> ''", "False <> '0'", "False <> '1'", "False <> 'x'", "'b' <> True", "'b' <> False",
            // Bool vs dates (= and <>, both orders)
            "True = #12/30/1899#", "True = #12/29/1899#", "True = #12/31/1899#", "True = #2024-01-01#",
            "False = #12/30/1899#", "False = #12/29/1899#", "False = #12/31/1899#", "False = #2024-01-01#",
            "True <> #12/30/1899#", "False <> #12/30/1899#", "#12/30/1899# = False", "#12/29/1899# = True",
            // Bool vs Null
            "True = Null", "False = Null", "True <> Null", "False <> Null", "Null = True", "Null <> True",
        ];

        string[] orderExprs =
        [
            // Bool vs Bool
            "True < False", "True <= False", "True > False", "True >= False",
            "False < True", "False > True", "True = False", "True <> False",
            // Bool vs numbers
            "True < -2", "True < -1", "True < 0", "True < 1", "True < 2", "True < -0.5", "True < 0.5",
            "True <= -2", "True <= -1", "True <= 0", "True <= 1",
            "True > -2", "True > -1", "True > 0", "True > 1", "True > -1.5",
            "True >= -2", "True >= -1", "True >= 0", "True >= 1",
            "False < -1", "False < 0", "False < 1", "False <= -1", "False <= 0", "False <= 1",
            "False > -1", "False > 0", "False > 1", "False >= -1", "False >= 0", "False >= 1",
            "-2 < True", "-1 < True", "0 < True", "1 < True", "0 > True", "-1 > True", "1 > False", "-1 < False",
            // Bool vs strings
            "True < ''", "True < '0'", "True < '1'", "True < 'a'", "True <= 'a'", "True > 'a'", "True >= 'a'",
            "False < ''", "False < '0'", "False < 'a'", "False <= 'a'", "False > 'a'", "False >= 'a'",
            "'a' < True", "'a' > True", "'0' < False", "'' < False",
            // Bool vs dates
            "True < #12/29/1899#", "True <= #12/29/1899#", "True < #12/30/1899#", "True < #12/31/1899#", "True > #12/28/1899#",
            "False < #12/30/1899#", "False <= #12/30/1899#", "False < #12/31/1899#", "False > #12/29/1899#",
            "#12/30/1899# > True", "#12/30/1899# < True",
        ];

        yield return SelectExprBatch("bool-cmp-eq-ne-accdb", ExprAccdb, "ACCDB DAO: Boolean = and <> discriminators against numbers, strings, dates, and Null.", eqNeExprs);
        yield return SelectExprBatch("bool-cmp-eq-ne-mdb", ExprMdb, "Jet 4 MDB DAO: Boolean = and <> discriminators against numbers, strings, dates, and Null.", eqNeExprs);
        yield return SelectExprBatch("bool-cmp-order-accdb", ExprAccdb, "ACCDB DAO: Boolean <, <=, >, >= discriminators against Booleans, numbers, strings, and dates.", orderExprs);
        yield return SelectExprBatch("bool-cmp-order-mdb", ExprMdb, "Jet 4 MDB DAO: Boolean <, <=, >, >= discriminators against Booleans, numbers, strings, and dates.", orderExprs);

        yield return new Probe(
            Card,
            "bool-cmp-origin-accdb",
            BoolRefs,
            "ACCDB DAO: BooleanComparison origin discriminators — comparison result (1=1)/(S<>'a'), CBool(...), and stored YESNO field Y against numbers and strings in SELECT and WHERE.",
            p =>
            {
                p.EngineLite()
                    .OpenDb("db", "main", exclusive: false, readOnly: false)
                    .Call("db", "Execute", A.Str("CREATE TABLE B (ID LONG CONSTRAINT PK PRIMARY KEY, Y YESNO, N LONG, S TEXT(20))"), A.I4(Dao.FailOnError))
                    .Call("db", "Execute", A.Str("INSERT INTO B VALUES (1, True, -1, 'x')"), A.I4(Dao.FailOnError))
                    .Call("db", "Execute", A.Str("INSERT INTO B VALUES (2, True, 2, '0')"), A.I4(Dao.FailOnError))
                    .Call("db", "Execute", A.Str("INSERT INTO B VALUES (3, False, 0, '')"), A.I4(Dao.FailOnError))
                    .Call("db", "Execute", A.Str("INSERT INTO B VALUES (4, False, 1, 'False')"), A.I4(Dao.FailOnError))
                    .Call("db", "Execute", A.Str("INSERT INTO B VALUES (5, True, 1, 'a')"), A.I4(Dao.FailOnError));

                string[] queries =
                [
                    // Comparison results on left
                    "SELECT (1 = 1) = 2 FROM B WHERE ID = 1",
                    "SELECT (1 = 1) = -1 FROM B WHERE ID = 1",
                    "SELECT (1 = 1) = 1 FROM B WHERE ID = 1",
                    "SELECT (1 = 1) = 0 FROM B WHERE ID = 1",
                    "SELECT (1 = 1) = 'x' FROM B WHERE ID = 1",
                    "SELECT (1 = 1) = '' FROM B WHERE ID = 1",
                    "SELECT (1 = 1) = '0' FROM B WHERE ID = 1",
                    "SELECT (1 = 1) <> 'b' FROM B WHERE ID = 1",
                    "SELECT (1 = 1) < 1 FROM B WHERE ID = 1",
                    "SELECT (1 = 1) < 'a' FROM B WHERE ID = 1",
                    "SELECT (1 = 0) = 0 FROM B WHERE ID = 1",
                    "SELECT (1 = 0) = 1 FROM B WHERE ID = 1",
                    "SELECT (1 = 0) = '' FROM B WHERE ID = 1",
                    "SELECT (1 = 0) = '0' FROM B WHERE ID = 1",
                    "SELECT (1 = 0) <> 'b' FROM B WHERE ID = 1",
                    "SELECT (1 = 0) < 'a' FROM B WHERE ID = 1",
                    // CBool(...) on left
                    "SELECT CBool(1) = 2 FROM B WHERE ID = 1",
                    "SELECT CBool(1) = -1 FROM B WHERE ID = 1",
                    "SELECT CBool(1) = 1 FROM B WHERE ID = 1",
                    "SELECT CBool(1) = 'x' FROM B WHERE ID = 1",
                    "SELECT CBool(1) = '0' FROM B WHERE ID = 1",
                    "SELECT CBool(1) <> 'b' FROM B WHERE ID = 1",
                    "SELECT CBool(1) < 1 FROM B WHERE ID = 1",
                    "SELECT CBool(1) < 'a' FROM B WHERE ID = 1",
                    "SELECT CBool(0) = 0 FROM B WHERE ID = 1",
                    "SELECT CBool(0) = '' FROM B WHERE ID = 1",
                    "SELECT CBool(0) = '0' FROM B WHERE ID = 1",
                    "SELECT CBool(0) <> 'b' FROM B WHERE ID = 1",
                    // r-q42 edge-forms E4/E5 expressions over stored Text column S
                    "SELECT ID, (S <> 'a') <> 'b' AS r FROM B ORDER BY ID",
                    "SELECT ID, (S <> 'a') = False AS r FROM B ORDER BY ID",
                    // Stored YESNO column Y in SELECT
                    "SELECT ID, Y = 2 AS e2, Y = 1 AS e1, Y = -1 AS em1, Y = 0 AS e0, Y <> 2 AS ne2, Y < 1 AS lt1, Y < 0 AS lt0 FROM B ORDER BY ID",
                    "SELECT ID, Y = 'x' AS ex, Y = '' AS ee, Y = '0' AS ez, Y = '1' AS e1s, Y = 'False' AS ef, Y <> 'b' AS neb FROM B ORDER BY ID",
                    "SELECT ID, Y < 'a' AS lta FROM B ORDER BY ID",
                    "SELECT ID, Y = N AS yn, Y < N AS yltn FROM B ORDER BY ID",
                    "SELECT ID, Y = S AS ys FROM B ORDER BY ID",
                    // Stored YESNO column Y in WHERE
                    "SELECT ID FROM B WHERE Y = 2 ORDER BY ID",
                    "SELECT ID FROM B WHERE Y = 1 ORDER BY ID",
                    "SELECT ID FROM B WHERE Y = -1 ORDER BY ID",
                    "SELECT ID FROM B WHERE Y = 0 ORDER BY ID",
                    "SELECT ID FROM B WHERE Y = 'x' ORDER BY ID",
                    "SELECT ID FROM B WHERE Y = '' ORDER BY ID",
                    "SELECT ID FROM B WHERE Y = '0' ORDER BY ID",
                    "SELECT ID FROM B WHERE Y = N ORDER BY ID",
                    "SELECT ID FROM B WHERE Y = S ORDER BY ID",
                    "SELECT ID FROM B WHERE Y < 1 ORDER BY ID",
                ];

                for (int i = 0; i < queries.Length; i++)
                {
                    string rs = $"rs{i}";
                    p.OpenRs(rs, "db", queries[i], Dao.OpenSnapshot)
                        .Until(rs, "EOF", 16, b => b
                            .GetBind($"{rs}f", rs, "Fields")
                            .Each($"{rs}f", $"{rs}x", 16, f => f.Get($"{rs}x", "Name").Get($"{rs}x", "Type").Get($"{rs}x", "Value"))
                            .Release($"{rs}f")
                            .Call(rs, "MoveNext"))
                        .Call(rs, "Close").Release(rs);
                }

                p.FinishLite("db");
            })
        {
            Fixtures = [("main", EmptyAccdb)],
        };
        yield return new Probe(
            Card,
            "bool-cmp-text-disc-accdb",
            BoolRefs,
            "ACCDB DAO: discriminates False/computed-Boolean vs Text comparison (stringified '-1'/'0' vs numeric 3464) across literals, CBool, comparisons, integers, and stored Text.",
            p =>
            {
                p.EngineLite()
                    .OpenDb("db", "main", exclusive: false, readOnly: false)
                    .Call("db", "Execute", A.Str("CREATE TABLE S1 (ID LONG CONSTRAINT PK PRIMARY KEY, S TEXT(20))"), A.I4(Dao.FailOnError))
                    .Call("db", "Execute", A.Str("INSERT INTO S1 VALUES (1, '-1')"), A.I4(Dao.FailOnError))
                    .Call("db", "Execute", A.Str("INSERT INTO S1 VALUES (2, '0')"), A.I4(Dao.FailOnError))
                    .Call("db", "Execute", A.Str("INSERT INTO S1 VALUES (3, 'x')"), A.I4(Dao.FailOnError))
                    .Call("db", "Execute", A.Str("INSERT INTO S1 VALUES (4, '')"), A.I4(Dao.FailOnError));

                string[] queries =
                [
                    // Computed comparison (1=1) and (1=0) against text boundary chars ('!'=33 < '-'=45 < '0'=48 < '1'=49 < 'a'=97)
                    "SELECT (1 = 1) = '-1' FROM S1 WHERE ID = 1",
                    "SELECT (1 = 1) = 'True' FROM S1 WHERE ID = 1",
                    "SELECT (1 = 1) < '!' FROM S1 WHERE ID = 1",
                    "SELECT (1 = 1) > '!' FROM S1 WHERE ID = 1",
                    "SELECT (1 = 1) < '0' FROM S1 WHERE ID = 1",
                    "SELECT (1 = 1) > '0' FROM S1 WHERE ID = 1",
                    "SELECT (1 = 0) = 'False' FROM S1 WHERE ID = 1",
                    "SELECT (1 = 0) < '!' FROM S1 WHERE ID = 1",
                    "SELECT (1 = 0) > '!' FROM S1 WHERE ID = 1",
                    "SELECT (1 = 0) < '1' FROM S1 WHERE ID = 1",
                    "SELECT (1 = 0) > '1' FROM S1 WHERE ID = 1",
                    // False keyword ordering against text boundary chars
                    "SELECT False < '!' FROM S1 WHERE ID = 1",
                    "SELECT False > '!' FROM S1 WHERE ID = 1",
                    "SELECT False < '1' FROM S1 WHERE ID = 1",
                    "SELECT False > '1' FROM S1 WHERE ID = 1",
                    "SELECT False <= '0' FROM S1 WHERE ID = 1",
                    "SELECT False >= '0' FROM S1 WHERE ID = 1",
                    "SELECT False < '-1' FROM S1 WHERE ID = 1",
                    "SELECT False > '-1' FROM S1 WHERE ID = 1",
                    // CBool against text boundary chars
                    "SELECT CBool(1) = '-1' FROM S1 WHERE ID = 1",
                    "SELECT CBool(1) < '!' FROM S1 WHERE ID = 1",
                    "SELECT CBool(1) > '!' FROM S1 WHERE ID = 1",
                    "SELECT CBool(0) < '!' FROM S1 WHERE ID = 1",
                    "SELECT CBool(0) > '!' FROM S1 WHERE ID = 1",
                    // Plain integer controls (must fail 3464)
                    "SELECT CInt(-1) = '-1' FROM S1 WHERE ID = 1",
                    "SELECT CInt(0) = '0' FROM S1 WHERE ID = 1",
                    "SELECT 0 = '0' FROM S1 WHERE ID = 1",
                    "SELECT 0 < 'a' FROM S1 WHERE ID = 1",
                    "SELECT -1 < 'a' FROM S1 WHERE ID = 1",
                    // Stored Text column S against True, False, (1=1), (1=0)
                    "SELECT ID, True = S AS ts, False = S AS fs FROM S1 ORDER BY ID",
                    "SELECT ID, True < S AS tls FROM S1 ORDER BY ID",
                    "SELECT ID, False < S AS fls FROM S1 ORDER BY ID",
                    "SELECT ID, (1 = 1) = S AS c1s, (1 = 0) = S AS c0s FROM S1 ORDER BY ID",
                    "SELECT ID, (1 = 1) < S AS c1ls, (1 = 0) < S AS c0ls FROM S1 ORDER BY ID",
                ];

                for (int i = 0; i < queries.Length; i++)
                {
                    string rs = $"rs{i}";
                    p.OpenRs(rs, "db", queries[i], Dao.OpenSnapshot)
                        .Until(rs, "EOF", 16, b => b
                            .GetBind($"{rs}f", rs, "Fields")
                            .Each($"{rs}f", $"{rs}x", 16, f => f.Get($"{rs}x", "Name").Get($"{rs}x", "Type").Get($"{rs}x", "Value"))
                            .Release($"{rs}f")
                            .Call(rs, "MoveNext"))
                        .Call(rs, "Close").Release(rs);
                }

                p.FinishLite("db");
            })
        {
            Fixtures = [("main", EmptyAccdb)],
        };
    }

    private static Probe SelectExprBatch(string name, string fixture, string description, string[] exprs) => new(
        Card,
        name,
        BoolRefs,
        description,
        p =>
        {
            p.EngineLite()
                .OpenDb("db", "main", exclusive: false, readOnly: true);
            for (int i = 0; i < exprs.Length; i++)
            {
                string rs = $"rs{i}";
                p.OpenRs(rs, "db", $"SELECT {exprs[i]} FROM One", Dao.OpenSnapshot)
                    .Until(rs, "EOF", 4, b => b
                        .GetBind($"{rs}f", rs, "Fields")
                        .Each($"{rs}f", $"{rs}x", 4, f => f.Get($"{rs}x", "Name").Get($"{rs}x", "Type").Get($"{rs}x", "Value"))
                        .Release($"{rs}f")
                        .Call(rs, "MoveNext"))
                    .Call(rs, "Close").Release(rs);
            }
            p.FinishLite("db");
        })
    {
        Fixtures = [("main", fixture)],
    };

    // ------------------------------------------------------------------------------------------------
    // 4. Read-only Database.Execute DDL/DML discriminator

    private static IEnumerable<Probe> ReadOnlyDdlProbes()
    {
        yield return new Probe(
            Card,
            "ro-ddl-disc-accdb",
            RoRefs,
            "ACCDB DAO: Database.Execute DDL and DML on a read-only Database handle alone (vs beside a read/write handle in the same process).",
            p =>
            {
                p.EngineLite()
                    // Setup table T
                    .OpenDb("db0", "main", exclusive: false, readOnly: false)
                    .Call("db0", "Execute", A.Str("CREATE TABLE T (ID LONG CONSTRAINT PK PRIMARY KEY, V LONG)"), A.I4(Dao.FailOnError))
                    .Call("db0", "Execute", A.Str("INSERT INTO T VALUES (1, 10)"), A.I4(Dao.FailOnError))
                    .Call("db0", "Close").Release("db0")
                    .Files("after-setup")
                    // Read-only handle alone
                    .OpenDb("dro1", "main", exclusive: false, readOnly: true)
                    .Call("dro1", "Execute", A.Str("DROP TABLE NoSuchMarker"))
                    .Call("dro1", "Execute", A.Str("DROP TABLE T"))
                    .Call("dro1", "Execute", A.Str("CREATE TABLE U1 (ID LONG)"))
                    .Call("dro1", "Execute", A.Str("CREATE INDEX IX_V ON T (V)"))
                    .Call("dro1", "Execute", A.Str("ALTER TABLE T ADD COLUMN W LONG"))
                    .Call("dro1", "Execute", A.Str("UPDATE T SET V = 20"))
                    .Call("dro1", "Execute", A.Str("DELETE FROM T"))
                    .Call("dro1", "Close").Release("dro1")
                    .Files("after-ro-alone")
                    // Read-only handle beside open RW handle
                    .OpenDb("drw", "main", exclusive: false, readOnly: false)
                    .OpenDb("dro2", "main", exclusive: false, readOnly: true)
                    .Call("dro2", "Execute", A.Str("DROP TABLE NoSuchMarker"))
                    .Call("dro2", "Execute", A.Str("CREATE TABLE U2 (ID LONG)"))
                    .Call("dro2", "Execute", A.Str("CREATE INDEX IX_V2 ON T (V)"))
                    .Call("dro2", "Execute", A.Str("DROP TABLE U2"))
                    .Call("dro2", "Close").Release("dro2")
                    .Call("drw", "Close").Release("drw")
                    .Release("eng")
                    .Files("after-close");
            })
        {
            Fixtures = [("main", EmptyAccdb)],
        };
    }
}
