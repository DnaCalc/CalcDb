namespace CalcDb.Probes.Cards;

/// <summary>
/// Deferred branches of R-CAT-1, R-TXN-1, R-LOCK-1 to R-LOCK-4, R-FMT-2, R-PRESERVE-1, R-SORT-3, R-ENC-1, R-LINK-1,
/// R-COMPLEX-1 and R-REBUILD-1 that a DAO-only, single-host, no-UI program can ask (docs/work/deferred-branches.md,
/// waves 1 to 4). Every database named by a call is a file of the attempt: a fixture of this card (inputs) or a file
/// the probe creates (work). No call here names ODBC, a DSN, an empty or missing database name or a prompt.
/// Run order: the three fixture creations first (create-rel-accdb, create-lksrc-accdb, create-lksrc-mdb), freeze them,
/// then everything else; probes that name no fixture create their own files and run in any order.
/// Link targets are relative paths from the attempt directory (the worker's working directory), as in r-enc-link;
/// whether ACE resolves them that way is recorded by each link probe's failed or successful Append.
/// The probes ask; they do not judge.
/// Deferred (not here): byte interpretation of kept images, Access or Excel UI, VBA, network paths, registry and
/// settings, ODBC and DSN sources, absolute link paths (a path cannot be joined to a Connect string), fault injection,
/// callbacks, secured-workgroup cases, and ADO routes.
/// </summary>
public sealed class RDefCatTxn : ICard
{
    private const string Card = "r-def-cat-txn";
    private const int Wait = 20000;
    private const int DbRefreshCache = 8;
    private const int DbEncrypt = 2, DbDecrypt = 4, AttachExclusive = 65536;
    private const string Pwd = "CalcDbProbe1";

    private static string Fx(string name) => $"{Card}-{name}";

    private static string[] R(string card, params string[] ids) => [card, .. ids];

    private static Probe Own(string name, string[] refs, string description, Action<P> program) => new(Card, name, refs, description, program);

    private static Probe OwnX(string name, string[] refs, string description, Action<P> a, Action<P> b) =>
        new(Card, name, refs, description, a) { Participants = [("b", b)] };

    // ---- program pieces ---------------------------------------------------------------------------------------

    private static void Ddl(P p, string db, string sql) => p.Call(db, "Execute", A.Str(sql));

    private static void Dml(P p, string db, string sql) => p.Call(db, "Execute", A.Str(sql), A.I4(Dao.FailOnError));

    private static void Make(P p, string file, bool mdb = false, string bind = "db") =>
        p.CallBind(bind, "eng", "CreateDatabase", A.Out(file), A.Str(Dao.LangGeneral), A.I4(mdb ? Dao.Version40 : Dao.Version120));

    private static void OpenOut(P p, string bind, string file, bool exclusive = false, bool readOnly = false, string? connect = null)
    {
        Arg[] args = connect is null
            ? [A.Out(file), A.Bool(exclusive), A.Bool(readOnly)]
            : [A.Out(file), A.Bool(exclusive), A.Bool(readOnly), A.Str(connect)];
        p.CallBind(bind, "eng", "OpenDatabase", args);
    }

    /// <summary>Open the file read-only with an optional Connect, read DBEngine.Errors, count T through a snapshot and close.</summary>
    private static void OpenTry(P p, string tag, string file, string? connect)
    {
        OpenOut(p, "o" + tag, file, false, true, connect);
        p.Errors().OpenRs("c" + tag, "o" + tag, "SELECT COUNT(*) FROM T", Dao.OpenSnapshot).ReadRow("c" + tag)
            .Call("c" + tag, "Close").Release("c" + tag).Call("o" + tag, "Close").Release("o" + tag);
    }

    private static void Insert(P p, string db, string table, string columns, params string[] rows)
    {
        foreach (string row in rows)
        {
            Dml(p, db, $"INSERT INTO {table} ({columns}) VALUES ({row})");
        }
    }

    private static void Workspace(P p) => p.GetBind("ws", "eng", "Workspaces", A.I4(0));

    private static void TableT(P p, string db)
    {
        Ddl(p, db, "CREATE TABLE T (ID LONG CONSTRAINT PK_T PRIMARY KEY, V LONG)");
        Insert(p, db, "T", "ID, V", "1, 10", "2, 20", "3, 30");
    }

    private static void Catalog(P p, string db, string tag)
    {
        string t = "ct" + tag, td = "cd" + tag, r = "cr" + tag, rl = "cl" + tag, q = "cq" + tag, qd = "cn" + tag;
        p.GetBind(t, db, "TableDefs").Each(t, td, 64, b => b.Get(td, "Name").Get(td, "Attributes")).Release(t)
            .GetBind(r, db, "Relations").Each(r, rl, 32, b => b.Get(rl, "Name").Get(rl, "Table").Get(rl, "ForeignTable").Get(rl, "Attributes")).Release(r)
            .GetBind(q, db, "QueryDefs").Each(q, qd, 32, b => b.Get(qd, "Name")).Release(q);
    }

    private static void FieldList(P p, string db, string table, string tag)
    {
        string ts = "ft" + tag, td = "fd" + tag, fs = "ff" + tag, f = "fe" + tag;
        p.GetBind(ts, db, "TableDefs").Item(td, ts, A.Str(table)).GetBind(fs, td, "Fields")
            .Each(fs, f, 32, b => b.Get(f, "Name").Get(f, "Type").Get(f, "Attributes")).Release(fs).Release(td).Release(ts);
    }

    private static void IndexList(P p, string db, string table, string tag)
    {
        string ts = "it" + tag, td = "id" + tag, xs = "ix" + tag, x = "ie" + tag, xf = "if" + tag, f = "ig" + tag;
        p.GetBind(ts, db, "TableDefs").Item(td, ts, A.Str(table)).GetBind(xs, td, "Indexes")
            .Each(xs, x, 16, b => b.Get(x, "Name").Get(x, "Primary").Get(x, "Unique").Get(x, "Foreign").GetBind(xf, x, "Fields")
                .Each(xf, f, 8, c => c.Get(f, "Name").Get(f, "Attributes")).Release(xf))
            .Release(xs).Release(td).Release(ts);
    }

    private static void DistinctCount(P p, string db, string table, string index, string tag) =>
        p.GetBind("dt" + tag, db, "TableDefs").Item("dd" + tag, "dt" + tag, A.Str(table)).GetBind("dx" + tag, "dd" + tag, "Indexes")
            .Item("de" + tag, "dx" + tag, A.Str(index)).Get("de" + tag, "DistinctCount").Release("de" + tag).Release("dx" + tag).Release("dd" + tag).Release("dt" + tag);

    private static void LinkInfo(P p, string db, string table, string tag) =>
        p.GetBind("lt" + tag, db, "TableDefs").Item("ld" + tag, "lt" + tag, A.Str(table)).Get("ld" + tag, "Name").Get("ld" + tag, "Connect")
            .Get("ld" + tag, "SourceTableName").Get("ld" + tag, "Attributes").Get("ld" + tag, "Updatable").Release("ld" + tag).Release("lt" + tag);

    /// <summary>CreateTableDef(name, attributes, SourceTableName, Connect) and TableDefs.Append, with the errors read after the append.</summary>
    private static void LinkTable(P p, string db, string tag, string name, string source, string connect, int attributes = 0) =>
        p.CallBind("lk" + tag, db, "CreateTableDef", A.Str(name), A.I4(attributes), A.Str(source), A.Str(connect))
            .GetBind("ls" + tag, db, "TableDefs").Call("ls" + tag, "Append", A.Obj("lk" + tag)).Errors()
            .Release("ls" + tag).Release("lk" + tag);

    private static string LinkConnect(string ext) => $@";DATABASE=inputs\{Fx("lksrc-" + ext)}.{ext}";

    private static string LinkPath(string ext) => $@"inputs\{Fx("lksrc-" + ext)}.{ext}";

    /// <summary>The two-column relation of the compound-key probes: PC(A, B) primary key, CC(ID, X, Y), PC.A, PC.B to CC.X, CC.Y.</summary>
    private static void CompoundSchema(P p, string db)
    {
        Ddl(p, db, "CREATE TABLE PC (A LONG, B LONG, CONSTRAINT PK_PC PRIMARY KEY (A, B))");
        Ddl(p, db, "CREATE TABLE CC (ID COUNTER CONSTRAINT PK_CC PRIMARY KEY, X LONG, Y LONG)");
        Insert(p, db, "PC", "A, B", "1, 1", "1, 2");
        p.CallBind("rel", db, "CreateRelation", A.Str("R_PC_CC"), A.Str("PC"), A.Str("CC"), A.I4(0))
            .CallBind("rf1", "rel", "CreateField", A.Str("A")).Put("rf1", "ForeignName", A.Str("X"))
            .CallBind("rf2", "rel", "CreateField", A.Str("B")).Put("rf2", "ForeignName", A.Str("Y"))
            .GetBind("rfs", "rel", "Fields").Call("rfs", "Append", A.Obj("rf1")).Call("rfs", "Append", A.Obj("rf2"))
            .GetBind("rls", db, "Relations").Call("rls", "Append", A.Obj("rel")).Errors()
            .Release("rls").Release("rfs").Release("rf2").Release("rf1").Release("rel");
    }

    /// <summary>Tokens: B/C/R = Workspaces(0) BeginTrans/CommitTrans/Rollback; a/b/c = UPDATE T SET V = 101/202/303 on ID 1/2/3; L = read T; E = read DBEngine.Errors.</summary>
    private static void Steps(P p, string db, string tokens)
    {
        int n = 0;
        foreach (string token in tokens.Split(' '))
        {
            n++;
            switch (token)
            {
                case "B": p.Call("ws", "BeginTrans"); break;
                case "C": p.Call("ws", "CommitTrans"); break;
                case "R": p.Call("ws", "Rollback"); break;
                case "a": Dml(p, db, "UPDATE T SET V = 101 WHERE ID = 1"); break;
                case "b": Dml(p, db, "UPDATE T SET V = 202 WHERE ID = 2"); break;
                case "c": Dml(p, db, "UPDATE T SET V = 303 WHERE ID = 3"); break;
                case "L": p.Snap(db, "s" + n, "SELECT ID, V FROM T ORDER BY ID"); break;
                case "E": p.Errors(); break;
                default: throw new InvalidOperationException("unknown step " + token);
            }
        }
    }

    // ---- the probes -------------------------------------------------------------------------------------------

    public IEnumerable<Probe> Probes()
    {
        yield return RelFixture();
        yield return SrcFixture("accdb");
        yield return SrcFixture("mdb");

        foreach (Probe probe in CatalogProbes().Concat(TxnProbes()).Concat(LockProbes()).Concat(FormatProbes())
                     .Concat(SortProbes()).Concat(EncProbes()).Concat(LinkProbes()).Concat(ComplexRebuildProbes()))
        {
            yield return probe;
        }
    }

    // ============================================================================================ fixtures

    private static Probe RelFixture() =>
        new(Card, "create-rel-accdb", R("R-CAT-1", "CAT-D", "R-TXN-1", "TXN-STMT", "R-LOCK-3"),
            $"Fixture {Fx("rel-accdb")} (ACCDB, DBEngine.CreateDatabase with dbVersion120): Parent(ID LONG primary key, Name) rows 1 to 5; Child, ChildCD and ChildCU (ID COUNTER primary key, PID LONG, V LONG) " +
            "with rows referencing parents 1 and 2 (Child), 3 (ChildCD, two rows) and 4 (ChildCU, two rows); parent 5 has no child. Relations appended by Database.CreateRelation: R_Child (attributes 0), R_CD (4096) and R_CU (256), all from Parent.ID to PID. The relations are read back.",
            p =>
            {
                p.EngineLite();
                Make(p, Fx("rel-accdb") + ".accdb");
                Ddl(p, "db", "CREATE TABLE Parent (ID LONG CONSTRAINT PK_Parent PRIMARY KEY, Name TEXT(20))");
                foreach (string t in new[] { "Child", "ChildCD", "ChildCU" })
                {
                    Ddl(p, "db", $"CREATE TABLE {t} (ID COUNTER CONSTRAINT PK_{t} PRIMARY KEY, PID LONG, V LONG)");
                }
                Insert(p, "db", "Parent", "ID, Name", "1, 'p1'", "2, 'p2'", "3, 'p3'", "4, 'p4'", "5, 'p5'");
                Insert(p, "db", "Child", "PID, V", "1, 10", "2, 20");
                Insert(p, "db", "ChildCD", "PID, V", "3, 30", "3, 31");
                Insert(p, "db", "ChildCU", "PID, V", "4, 40", "4, 41");
                p.AddRelation("R_Child", "Parent", "Child", "ID", "PID", 0)
                    .AddRelation("R_CD", "Parent", "ChildCD", "ID", "PID", 4096)
                    .AddRelation("R_CU", "Parent", "ChildCU", "ID", "PID", 256);
                Catalog(p, "db", "f");
                p.FinishLite("db");
            })
        { CreatesFixture = true };

    private static Probe SrcFixture(string ext) =>
        new(Card, $"create-lksrc-{ext}", R("R-LINK-1", "LINK-OPEN", "LINK-SCHEMA", "LINK-UPDATE"),
            $"Fixture {Fx("lksrc-" + ext)} ({ext.ToUpperInvariant()}, dbVersion {(ext == "mdb" ? Dao.Version40 : Dao.Version120)}): link source with T(ID LONG primary key, V LONG, S TEXT(20)) rows 1 to 5 (V = 10 x ID, S = 's' and the ID) and NoKey(A LONG, B TEXT(10)), no key or index, rows (1,'x'), (2,'y'), (2,'z').",
            p =>
            {
                p.EngineLite();
                Make(p, $"{Fx("lksrc-" + ext)}.{ext}", ext == "mdb");
                Ddl(p, "db", "CREATE TABLE T (ID LONG CONSTRAINT PK_T PRIMARY KEY, V LONG, S TEXT(20))");
                for (int i = 1; i <= 5; i++)
                {
                    Insert(p, "db", "T", "ID, V, S", $"{i}, {i * 10}, 's{i}'");
                }
                Ddl(p, "db", "CREATE TABLE NoKey (A LONG, B TEXT(10))");
                Insert(p, "db", "NoKey", "A, B", "1, 'x'", "2, 'y'", "2, 'z'");
                p.FinishLite("db");
            })
        { CreatesFixture = true };

    // ============================================================================================ R-CAT-1

    private static IEnumerable<Probe> CatalogProbes()
    {
        yield return Own("cat-drop-table-held-recordset", R("R-CAT-1", "CAT-D", "CAT-V"),
            "Does DROP TABLE T succeed in the same Database while a dynaset on T is open and positioned? Then: the errors, the catalog, the held recordset (RecordCount, MoveNext and its row, Close), and a second DROP TABLE after the recordset is closed.",
            p =>
            {
                p.EngineLite();
                Make(p, Fx("cat-drop-held") + ".accdb");
                TableT(p, "db");
                p.OpenRs("rs", "db", "SELECT * FROM T ORDER BY ID", Dao.OpenDynaset).ReadRow("rs");
                Ddl(p, "db", "DROP TABLE T");
                p.Errors();
                Catalog(p, "db", "1");
                p.Get("rs", "RecordCount").Call("rs", "MoveNext").ReadRow("rs").Call("rs", "Close").Release("rs");
                Ddl(p, "db", "DROP TABLE T");
                p.Errors();
                Catalog(p, "db", "2");
                p.FinishLite("db");
            });

        yield return Own("cat-drop-querydef-held-and-dependent", R("R-CAT-1", "CAT-D", "CAT-Q"),
            "With Q1 = SELECT ID, V FROM T and Q2 = SELECT * FROM Q1 saved, and a held QueryDef object for Q1: after QueryDefs.Delete('Q1') what do the held object (Name, SQL, OpenRecordset), the saved query Q2 and the QueryDefs collection show?",
            p =>
            {
                p.EngineLite();
                Make(p, Fx("cat-drop-qd") + ".accdb");
                TableT(p, "db");
                p.CallBind("q1", "db", "CreateQueryDef", A.Str("Q1"), A.Str("SELECT ID, V FROM T;"))
                    .CallBind("q2", "db", "CreateQueryDef", A.Str("Q2"), A.Str("SELECT * FROM Q1;"));
                p.Snap("db", "before", "SELECT * FROM Q2 ORDER BY ID");
                p.GetBind("qds", "db", "QueryDefs").Call("qds", "Delete", A.Str("Q1")).Errors().Release("qds");
                Catalog(p, "db", "1");
                p.Get("q1", "Name").Get("q1", "SQL").CallBind("r1", "q1", "OpenRecordset").Get("r1", "RecordCount").Errors();
                p.OpenRs("r2", "db", "SELECT * FROM Q2", Dao.OpenSnapshot).Errors().Get("q2", "SQL").Get("q2", "Name");
                p.FinishLite("db");
            });

        yield return Own("cat-drop-index-held-table-recordset", R("R-CAT-1", "CAT-D", "R-CURSOR-1"),
            "A table-type recordset on T has Index = IX_V set and is positioned. DROP INDEX IX_V ON T is executed. What do the errors, the recordset's Index property, MoveNext, the current row, Seek('=', 20) with NoMatch and a new assignment of Index = 'IX_V' show?",
            p =>
            {
                p.EngineLite();
                Make(p, Fx("cat-drop-index") + ".accdb");
                Ddl(p, "db", "CREATE TABLE T (ID LONG CONSTRAINT PK_T PRIMARY KEY, V LONG)");
                Ddl(p, "db", "CREATE INDEX IX_V ON T (V)");
                Insert(p, "db", "T", "ID, V", "1, 30", "2, 20", "3, 10");
                p.OpenRs("rs", "db", "T", Dao.OpenTable).Put("rs", "Index", A.Str("IX_V")).Call("rs", "MoveFirst").ReadRow("rs");
                Ddl(p, "db", "DROP INDEX IX_V ON T");
                p.Errors().Get("rs", "Index").Call("rs", "MoveNext").ReadRow("rs").Call("rs", "Seek", A.Str("="), A.I4(20)).Get("rs", "NoMatch")
                    .Put("rs", "Index", A.Str("IX_V")).Errors();
                IndexList(p, "db", "T", "1");
                p.FinishLite("rs", "db");
            });

        yield return new Probe(Card, "cat-drop-table-in-relation", R("R-CAT-1", "CAT-D"),
            $"On {Fx("rel-accdb")}: DROP TABLE Parent (primary side of three relations), then DROP TABLE Child (foreign side of R_Child), then DROP TABLE Parent again. After each statement: the errors, the TableDefs and the Relations with their attributes.",
            p =>
            {
                p.EngineLite().OpenDb("db", "main", false, false);
                Catalog(p, "db", "0");
                foreach ((string sql, string tag) in new[] { ("DROP TABLE Parent", "1"), ("DROP TABLE Child", "2"), ("DROP TABLE Parent", "3") })
                {
                    Ddl(p, "db", sql);
                    p.Errors();
                    Catalog(p, "db", tag);
                }
                p.FinishLite("db");
            })
        { Fixtures = [("main", Fx("rel-accdb"))] };

        yield return new Probe(Card, "cat-drop-column-in-relation", R("R-CAT-1", "CAT-D"),
            $"On {Fx("rel-accdb")}: ALTER TABLE Child DROP COLUMN PID (foreign column of R_Child), ALTER TABLE Parent DROP COLUMN ID (primary key and referenced column), ALTER TABLE ChildCD DROP COLUMN V (a column in no relation). After each statement: the errors, the fields and indexes of the table and the Relations.",
            p =>
            {
                p.EngineLite().OpenDb("db", "main", false, false);
                foreach ((string table, string column, string tag) in new[] { ("Child", "PID", "1"), ("Parent", "ID", "2"), ("ChildCD", "V", "3") })
                {
                    Ddl(p, "db", $"ALTER TABLE {table} DROP COLUMN {column}");
                    p.Errors();
                    FieldList(p, "db", table, tag);
                    IndexList(p, "db", table, tag);
                    Catalog(p, "db", tag);
                }
                p.FinishLite("db");
            })
        { Fixtures = [("main", Fx("rel-accdb"))] };

        foreach ((string name, int attributes, string text) in new[] { ("cascade", 4096, "delete-cascade relation (attributes 4096)"), ("plain", 0, "enforced relation without cascade (attributes 0)") })
        {
            yield return Own($"cat-rel-self-reference-{name}", R("R-CAT-1", "CAT-D", "R-SQL-2"),
                $"S(ID LONG primary key, Up LONG, N) holds rows 1 (Up Null), 2 (Up 1), 3 (Up 2), 4 (Up 1). A self-referencing {text} S.ID to S.Up is appended through CreateRelation. Is the append accepted? Then DELETE FROM S WHERE ID = 3, then DELETE FROM S WHERE ID = 1 (dbFailOnError): errors and the rows left after each.",
                p =>
                {
                    p.EngineLite();
                    Make(p, Fx("cat-self-" + name) + ".accdb");
                    Ddl(p, "db", "CREATE TABLE S (ID LONG CONSTRAINT PK_S PRIMARY KEY, Up LONG, N TEXT(10))");
                    Insert(p, "db", "S", "ID, Up, N", "1, Null, 'root'", "2, 1, 'mid'", "3, 2, 'leaf'", "4, 1, 'leaf2'");
                    p.AddRelation("R_Self", "S", "S", "ID", "Up", attributes).Errors();
                    Catalog(p, "db", "1");
                    Dml(p, "db", "DELETE FROM S WHERE ID = 3");
                    p.Errors().Snap("db", "after3", "SELECT * FROM S ORDER BY ID");
                    Dml(p, "db", "DELETE FROM S WHERE ID = 1");
                    p.Errors().Snap("db", "after1", "SELECT * FROM S ORDER BY ID");
                    p.FinishLite("db");
                });
        }

        yield return Own("cat-rel-cascade-cycle", R("R-CAT-1", "CAT-D"),
            "A(ID key, BID) and B(ID key, AID): R_AB from A.ID to B.AID with delete cascade (4096), then R_BA from B.ID to A.BID with delete cascade. Is each append accepted? With rows A(1, Null) and B(1, 1), then A.BID set to 1, what does DELETE FROM A WHERE ID = 1 (dbFailOnError) do to both tables?",
            p =>
            {
                p.EngineLite();
                Make(p, Fx("cat-cycle") + ".accdb");
                Ddl(p, "db", "CREATE TABLE A (ID LONG CONSTRAINT PK_A PRIMARY KEY, BID LONG)");
                Ddl(p, "db", "CREATE TABLE B (ID LONG CONSTRAINT PK_B PRIMARY KEY, AID LONG)");
                p.AddRelation("R_AB", "A", "B", "ID", "AID", 4096).Errors();
                p.AddRelation("R_BA", "B", "A", "ID", "BID", 4096).Errors();
                Catalog(p, "db", "1");
                Insert(p, "db", "A", "ID, BID", "1, Null");
                Insert(p, "db", "B", "ID, AID", "1, 1");
                Dml(p, "db", "UPDATE A SET BID = 1 WHERE ID = 1");
                p.Errors();
                Dml(p, "db", "DELETE FROM A WHERE ID = 1");
                p.Errors().Snap("db", "a", "SELECT * FROM A").Snap("db", "b", "SELECT * FROM B");
                p.FinishLite("db");
            });

        yield return Own("cat-rel-type-pairs", R("R-CAT-1", "CAT-D"),
            "Which field-type pairs does Relations.Append accept? Primary fields P_Long.ID (LONG), P_Text.ID (TEXT 10), P_Counter.ID (COUNTER), P_Int.ID (SMALLINT) against F.L (LONG), F.I (SMALLINT), F.D (DOUBLE), F.T10 (TEXT 10), F.T20 (TEXT 20) and F.B (BYTE), eleven pairs by CreateRelation with attributes 0; errors after each append, then the Relations.",
            p =>
            {
                p.EngineLite();
                Make(p, Fx("cat-typepairs") + ".accdb");
                Ddl(p, "db", "CREATE TABLE P_Long (ID LONG CONSTRAINT PK_PL PRIMARY KEY)");
                Ddl(p, "db", "CREATE TABLE P_Text (ID TEXT(10) CONSTRAINT PK_PT PRIMARY KEY)");
                Ddl(p, "db", "CREATE TABLE P_Counter (ID COUNTER CONSTRAINT PK_PC2 PRIMARY KEY)");
                Ddl(p, "db", "CREATE TABLE P_Int (ID SMALLINT CONSTRAINT PK_PI PRIMARY KEY)");
                Ddl(p, "db", "CREATE TABLE F (L LONG, I SMALLINT, D DOUBLE, T10 TEXT(10), T20 TEXT(20), B BYTE)");
                (string Primary, string Foreign)[] pairs =
                [
                    ("P_Long", "L"), ("P_Long", "I"), ("P_Long", "D"), ("P_Long", "T10"), ("P_Long", "B"), ("P_Text", "T10"),
                    ("P_Text", "T20"), ("P_Counter", "L"), ("P_Counter", "I"), ("P_Int", "L"), ("P_Int", "B"),
                ];
                for (int i = 0; i < pairs.Length; i++)
                {
                    p.AddRelation($"R_{i + 1}", pairs[i].Primary, "F", "ID", pairs[i].Foreign, 0).Errors();
                }
                Catalog(p, "db", "1");
                p.FinishLite("db");
            });

        yield return Own("cat-rel-compound-key-enforcement", R("R-CAT-1", "CAT-D", "R-SQL-2"),
            "PC(A, B) has primary key (A, B) with rows (1,1) and (1,2); CC(ID, X, Y) is related by one two-field relation PC.A to CC.X and PC.B to CC.Y. Which of these inserts into CC (dbFailOnError) does ACE accept: (1,1), (1,3), (Null,1), (1,Null), (Null,Null), (2,1), (1,2)? Errors after each, then the rows of CC.",
            p =>
            {
                p.EngineLite();
                Make(p, Fx("cat-compound") + ".accdb");
                CompoundSchema(p, "db");
                Catalog(p, "db", "1");
                foreach (string row in new[] { "1, 1", "1, 3", "Null, 1", "1, Null", "Null, Null", "2, 1", "1, 2" })
                {
                    Insert(p, "db", "CC", "X, Y", row);
                    p.Errors();
                }
                p.Snap("db", "cc", "SELECT * FROM CC ORDER BY ID");
                p.FinishLite("db");
            });

        yield return Own("cat-workspace-close-held-objects", R("R-CAT-1", "CAT-V", "TXN-CLOSE"),
            "A Workspace made by CreateWorkspace('wx', 'Admin', '') opens a file and holds a dynaset, a TableDef and a temporary QueryDef. After Workspace.Close, what do the held Database (Name), the recordset (RecordCount, row), the TableDef (Name), the QueryDef (SQL), the workspace itself (Name) and DBEngine.Workspaces.Count show, and does the file open normally again?",
            p =>
            {
                p.EngineLite();
                Make(p, Fx("cat-wsclose") + ".accdb");
                TableT(p, "db");
                p.Call("db", "Close").Release("db");
                p.CallBind("wx", "eng", "CreateWorkspace", A.Str("wx"), A.Str("Admin"), A.Str(""));
                OpenOut(p, "wdb", Fx("cat-wsclose") + ".accdb");
                p.OpenRs("rs", "wdb", "SELECT * FROM T ORDER BY ID", Dao.OpenDynaset).ReadRow("rs")
                    .GetBind("tds", "wdb", "TableDefs").Item("td", "tds", A.Str("T")).CallBind("qd", "wdb", "CreateQueryDef", A.Str(""), A.Str("SELECT * FROM T;"));
                p.Call("wx", "Close").Errors();
                p.Get("wdb", "Name").Get("rs", "RecordCount").ReadRow("rs").Get("td", "Name").Get("qd", "SQL").Get("wx", "Name")
                    .GetBind("wss", "eng", "Workspaces").Get("wss", "Count");
                OpenOut(p, "again", Fx("cat-wsclose") + ".accdb");
                p.Snap("again", "t", "SELECT * FROM T ORDER BY ID").Call("again", "Close").Files("after-close");
            });

        yield return OwnX("cat-xproc-catalog-visibility", R("R-CAT-1", "CAT-V", "R-LOCK-3"),
            "Process A holds a shared Database and reads TableDefs.Count, QueryDefs.Count and Properties.Count. Process B (shared, read/write) creates table T2 with a row, saves query Q9 and appends Database property ProbeProp, then closes. A reads the same counts and the new items without refresh, after each collection's Refresh, after DBEngine.Idle(dbRefreshCache), and opens T2 by a recordset.",
            a =>
            {
                a.EngineLite();
                Make(a, Fx("cat-xvis") + ".accdb");
                TableT(a, "db");
                a.Call("db", "Close").Release("db");
                OpenOut(a, "db", Fx("cat-xvis") + ".accdb");
                a.GetBind("tds", "db", "TableDefs").Get("tds", "Count").GetBind("qds", "db", "QueryDefs").Get("qds", "Count")
                    .GetBind("prs", "db", "Properties").Get("prs", "Count").Signal("a-ready").Await("b-done", Wait);
                a.Get("tds", "Count").Item("t2a", "tds", A.Str("T2")).Get("qds", "Count").Item("q9a", "qds", A.Str("Q9")).Get("prs", "Count").Item("ppa", "prs", A.Str("ProbeProp"));
                a.Call("tds", "Refresh").Call("qds", "Refresh").Call("prs", "Refresh");
                a.Get("tds", "Count").Item("t2b", "tds", A.Str("T2")).Get("qds", "Count").Item("q9b", "qds", A.Str("Q9")).Get("prs", "Count").Item("ppb", "prs", A.Str("ProbeProp"));
                a.Call("eng", "Idle", A.I4(DbRefreshCache)).Get("tds", "Count").Get("qds", "Count").Get("prs", "Count");
                a.OpenRs("rt2", "db", "SELECT * FROM T2", Dao.OpenSnapshot).ReadAll("rt2").Call("rt2", "Close");
                a.FinishLite("db");
            },
            b =>
            {
                b.Await("a-ready", Wait).Create().Modules("after-create");
                OpenOut(b, "db", Fx("cat-xvis") + ".accdb");
                Ddl(b, "db", "CREATE TABLE T2 (ID LONG CONSTRAINT PK_T2 PRIMARY KEY, S TEXT(10))");
                Insert(b, "db", "T2", "ID, S", "1, 'b'");
                b.CallBind("q9", "db", "CreateQueryDef", A.Str("Q9"), A.Str("SELECT * FROM T2;"))
                    .CallBind("pr", "db", "CreateProperty", A.Str("ProbeProp"), A.I4(10), A.Str("x"))
                    .GetBind("props", "db", "Properties").Call("props", "Append", A.Obj("pr")).Errors();
                b.Call("db", "Close").Signal("b-done");
            });

        yield return OwnX("cat-xproc-drop-table-under-reader", R("R-CAT-1", "CAT-D", "READ-SCHEMA", "R-LOCK-3"),
            "Process A holds a snapshot, a dynaset and a table-type recordset on T, each positioned on row 1, and a TableDef object. Process B tries DROP TABLE T, then ALTER TABLE T ADD COLUMN W LONG; A then moves each recordset one row, closes the snapshot; B tries DROP TABLE T again; A closes the others; B tries a last DROP TABLE T. Errors, catalogs and A's reads after each step.",
            a =>
            {
                a.EngineLite();
                Make(a, Fx("cat-xdrop") + ".accdb");
                TableT(a, "db");
                a.Call("db", "Close").Release("db");
                OpenOut(a, "db", Fx("cat-xdrop") + ".accdb");
                a.OpenRs("r1", "db", "SELECT * FROM T ORDER BY ID", Dao.OpenSnapshot).ReadRow("r1")
                    .OpenRs("r2", "db", "SELECT * FROM T ORDER BY ID", Dao.OpenDynaset).ReadRow("r2")
                    .OpenRs("r3", "db", "T", Dao.OpenTable).ReadRow("r3")
                    .GetBind("tds", "db", "TableDefs").Item("td", "tds", A.Str("T")).Signal("a-ready").Await("b-1", Wait);
                a.Call("r1", "MoveNext").ReadRow("r1").Call("r2", "MoveNext").ReadRow("r2").Call("r3", "MoveNext").ReadRow("r3").Errors();
                a.Get("td", "Name").Call("r1", "Close").Signal("a-1").Await("b-2", Wait);
                a.Call("r2", "Close").Call("r3", "Close").Release("td").Signal("a-2").Await("b-done", Wait);
                Catalog(a, "db", "a");
                a.FinishLite("db");
            },
            b =>
            {
                b.Await("a-ready", Wait).Create().Modules("after-create");
                OpenOut(b, "db", Fx("cat-xdrop") + ".accdb");
                Ddl(b, "db", "DROP TABLE T");
                b.Errors();
                Ddl(b, "db", "ALTER TABLE T ADD COLUMN W LONG");
                b.Errors();
                Catalog(b, "db", "1");
                b.Signal("b-1").Await("a-1", Wait);
                Ddl(b, "db", "DROP TABLE T");
                b.Errors().Signal("b-2").Await("a-2", Wait);
                Ddl(b, "db", "DROP TABLE T");
                b.Errors();
                Catalog(b, "db", "3");
                b.Call("db", "Close").Signal("b-done");
            });

        yield return OwnX("cat-xproc-relation-append-over-uncommitted-orphan", R("R-CAT-1", "CAT-D", "TXN-DDL", "R-LOCK-4"),
            "Process A begins a transaction and inserts child row C(PID 99) with no parent in P (uncommitted). Process B appends the enforced relation P.ID to C.PID through CreateRelation: errors and relations. A commits; B appends again: errors and relations.",
            a =>
            {
                a.EngineLite();
                Make(a, Fx("cat-xrel") + ".accdb");
                Ddl(a, "db", "CREATE TABLE P (ID LONG CONSTRAINT PK_P PRIMARY KEY)");
                Ddl(a, "db", "CREATE TABLE C (ID COUNTER CONSTRAINT PK_C PRIMARY KEY, PID LONG)");
                Insert(a, "db", "P", "ID", "1");
                a.Call("db", "Close").Release("db");
                OpenOut(a, "db", Fx("cat-xrel") + ".accdb");
                Workspace(a);
                a.Call("ws", "BeginTrans");
                Dml(a, "db", "INSERT INTO C (PID) VALUES (99)");
                a.Signal("a-open").Await("b-1", Wait).Call("ws", "CommitTrans").Signal("a-committed").Await("b-done", Wait);
                a.Snap("db", "c", "SELECT * FROM C");
                Catalog(a, "db", "a");
                a.FinishLite("db");
            },
            b =>
            {
                b.Await("a-open", Wait).Create().Modules("after-create");
                OpenOut(b, "db", Fx("cat-xrel") + ".accdb");
                b.AddRelation("R_PC", "P", "C", "ID", "PID", 0).Errors();
                Catalog(b, "db", "1");
                b.Signal("b-1").Await("a-committed", Wait);
                b.AddRelation("R_PC", "P", "C", "ID", "PID", 0).Errors();
                Catalog(b, "db", "2");
                b.Call("db", "Close").Signal("b-done");
            });
    }

    // ============================================================================================ R-TXN-1

    private static IEnumerable<Probe> TxnProbes()
    {
        foreach ((string name, string tokens, string text) in new[]
                 {
                     ("middle-rollback-after-inner-commit", "B a B b B c C L R L C L", "after the inner CommitTrans, a Rollback at the middle level"),
                     ("outer-rollback-after-inner-commits", "B a B b B c C C L R L", "after both inner levels commit, the outer Rollback"),
                 })
        {
            yield return Own("txn-nest-mdb-" + name, R("R-TXN-1", "TXN-NEST"),
                $"Jet 4 MDB repeat of the nested-level questions. T(ID, V) rows 10, 20, 30. Tokens: B/C/R = Workspaces(0).BeginTrans/CommitTrans/Rollback; a/b/c = UPDATE V = 101/202/303 on rows 1/2/3; L = read T. Program: {tokens}. Which changes remain {text}?",
                p =>
                {
                    p.EngineLite();
                    Make(p, Fx("txn-nest-" + name) + ".mdb", mdb: true);
                    TableT(p, "db");
                    Workspace(p);
                    Steps(p, "db", tokens);
                    p.FinishLite("db");
                });
        }

        yield return Own("txn-stmt-compound-relation-fail", R("R-TXN-1", "TXN-STMT", "TXN-DDL"),
            "PC/CC with the two-field relation (PC has (1,1) and (1,2)); S(Seq, X, Y) holds (1,1,1), (2,1,3), (3,1,2). Inside BeginTrans: INSERT INTO CC (X, Y) SELECT X, Y FROM S ORDER BY Seq with dbFailOnError, so the second candidate breaks the relation. Errors and the rows of CC after the statement, after CommitTrans, and the row count of S.",
            p =>
            {
                p.EngineLite();
                Make(p, Fx("txn-stmt-compound") + ".accdb");
                CompoundSchema(p, "db");
                Ddl(p, "db", "CREATE TABLE S (Seq LONG, X LONG, Y LONG)");
                Insert(p, "db", "S", "Seq, X, Y", "1, 1, 1", "2, 1, 3", "3, 1, 2");
                Workspace(p);
                p.Call("ws", "BeginTrans");
                Dml(p, "db", "INSERT INTO CC (X, Y) SELECT X, Y FROM S ORDER BY Seq");
                p.Errors().Snap("db", "mid", "SELECT * FROM CC ORDER BY ID").Call("ws", "CommitTrans").Errors()
                    .Snap("db", "end", "SELECT * FROM CC ORDER BY ID").Snap("db", "s", "SELECT COUNT(*) FROM S");
                p.FinishLite("db");
            });

        foreach ((string resolve, string text) in new[] { ("CommitTrans", "CommitTrans"), ("Rollback", "Rollback") })
        {
            yield return Own("txn-pending-addnew-" + resolve.ToLowerInvariant(), R("R-TXN-1", "TXN-CLOSE", "R-CURSOR-1"),
                $"T(ID COUNTER key, V LONG) holds one row. A dynaset calls AddNew and sets V = 7 inside BeginTrans, then the Workspace calls {text} before the recordset calls Update. Errors from Update, EditMode before and after, and the rows of T from the same recordset and from a fresh snapshot.",
                p =>
                {
                    p.EngineLite();
                    Make(p, Fx("txn-addnew-" + resolve.ToLowerInvariant()) + ".accdb");
                    Ddl(p, "db", "CREATE TABLE T (ID COUNTER CONSTRAINT PK_T PRIMARY KEY, V LONG)");
                    Insert(p, "db", "T", "V", "10");
                    Workspace(p);
                    p.OpenRs("rs", "db", "SELECT * FROM T ORDER BY ID", Dao.OpenDynaset).GetBind("fl", "rs", "Fields").Item("fv", "fl", A.Str("V"))
                        .Call("ws", "BeginTrans").Call("rs", "AddNew").Put("fv", "Value", A.I4(7)).Get("rs", "EditMode")
                        .Call("ws", resolve).Errors().Get("rs", "EditMode").Call("rs", "Update").Errors().Get("rs", "EditMode")
                        .Call("rs", "MoveFirst").ReadAll("rs");
                    p.Snap("db", "fresh", "SELECT * FROM T ORDER BY ID");
                    p.FinishLite("rs", "db");
                });
        }

        yield return Own("txn-close-original-recordset-while-clone-holds-level", R("R-TXN-1", "TXN-CLOSE"),
            "A dynaset and its Clone are open on T. Inside BeginTrans the clone does AddNew, sets V = 7 and Update; the original is then closed and Rollback is called. Errors, the rows of T from a fresh snapshot, and the clone's RecordCount and EOF afterwards.",
            p =>
            {
                p.EngineLite();
                Make(p, Fx("txn-clone") + ".accdb");
                Ddl(p, "db", "CREATE TABLE T (ID COUNTER CONSTRAINT PK_T PRIMARY KEY, V LONG)");
                Insert(p, "db", "T", "V", "10");
                Workspace(p);
                p.OpenRs("rs", "db", "SELECT * FROM T ORDER BY ID", Dao.OpenDynaset).CallBind("cl", "rs", "Clone")
                    .GetBind("cf", "cl", "Fields").Item("cv", "cf", A.Str("V"))
                    .Call("ws", "BeginTrans").Call("cl", "AddNew").Put("cv", "Value", A.I4(7)).Call("cl", "Update").Errors()
                    .Call("rs", "Close").Release("rs").Errors().Call("ws", "Rollback").Errors();
                p.Snap("db", "fresh", "SELECT * FROM T ORDER BY ID").Get("cl", "RecordCount").Get("cl", "EOF").Errors();
                p.FinishLite("cl", "db");
            });

        yield return Own("txn-engine-level-transaction-members", R("R-TXN-1", "TXN-NEST", "TXN-LOCAL"),
            "Are the hidden DBEngine.BeginTrans, DBEngine.CommitTrans and DBEngine.Rollback accepted, and do they act on Workspaces(0)? T(ID, V) rows 10, 20, 30. DBEngine.BeginTrans, UPDATE row 1 to 101, read T, DBEngine.Rollback, read T; DBEngine.BeginTrans, UPDATE row 2 to 202, DBEngine.CommitTrans, read T; then Workspaces(0).BeginTrans, UPDATE row 3 to 303 and DBEngine.Rollback, read T. Errors after each call.",
            p =>
            {
                p.EngineLite();
                Make(p, Fx("txn-engine") + ".accdb");
                TableT(p, "db");
                p.Call("eng", "BeginTrans").Errors();
                Dml(p, "db", "UPDATE T SET V = 101 WHERE ID = 1");
                p.Snap("db", "s1", "SELECT * FROM T ORDER BY ID").Call("eng", "Rollback").Errors().Snap("db", "s2", "SELECT * FROM T ORDER BY ID");
                p.Call("eng", "BeginTrans").Errors();
                Dml(p, "db", "UPDATE T SET V = 202 WHERE ID = 2");
                p.Call("eng", "CommitTrans").Errors().Snap("db", "s3", "SELECT * FROM T ORDER BY ID");
                Workspace(p);
                p.Call("ws", "BeginTrans");
                Dml(p, "db", "UPDATE T SET V = 303 WHERE ID = 3");
                p.Call("eng", "Rollback").Errors().Snap("db", "s4", "SELECT * FROM T ORDER BY ID");
                p.FinishLite("db");
            });

        yield return new Probe(Card, "txn-cascade-delete-rollback-and-commit", R("R-TXN-1", "TXN-DDL", "TXN-STMT"),
            $"On {Fx("rel-accdb")}: inside BeginTrans, DELETE FROM Parent WHERE ID = 3 (R_CD cascades to ChildCD): rows of ChildCD and Parent, then Rollback and the rows again; then the same delete inside BeginTrans and CommitTrans and the rows again.",
            p =>
            {
                p.EngineLite().OpenDb("db", "main", false, false);
                Workspace(p);
                p.Call("ws", "BeginTrans");
                Dml(p, "db", "DELETE FROM Parent WHERE ID = 3");
                p.Snap("db", "d1", "SELECT * FROM ChildCD ORDER BY ID").Call("ws", "Rollback").Errors()
                    .Snap("db", "d2", "SELECT * FROM ChildCD ORDER BY ID").Snap("db", "d3", "SELECT COUNT(*) FROM Parent WHERE ID = 3");
                p.Call("ws", "BeginTrans");
                Dml(p, "db", "DELETE FROM Parent WHERE ID = 3");
                p.Call("ws", "CommitTrans").Errors().Snap("db", "d4", "SELECT * FROM ChildCD ORDER BY ID").Snap("db", "d5", "SELECT COUNT(*) FROM Parent WHERE ID = 3");
                p.FinishLite("db");
            })
        { Fixtures = [("main", Fx("rel-accdb"))] };

        foreach ((string name, bool insertParent, bool commit) in new[]
                 {
                     ("parent-insert-uncommitted-then-commit", true, true),
                     ("parent-insert-uncommitted-then-rollback", true, false),
                     ("parent-delete-uncommitted-then-commit", false, true),
                     ("parent-delete-uncommitted-then-rollback", false, false),
                 })
        {
            int parent = insertParent ? 100 : 5;
            string action = insertParent ? "inserts Parent (100, 'new')" : "deletes Parent 5 (no child)";
            string resolve = commit ? "CommitTrans" : "Rollback";
            yield return new Probe(Card, "txn-xproc-" + name, R("R-TXN-1", "TXN-STMT", "TXN-LOCAL", "R-LOCK-4"),
                $"On {Fx("rel-accdb")}: process A begins a transaction and {action}, uncommitted. Process B inserts Child (PID {parent}) with dbFailOnError and reads the Child rows for PID {parent}. A calls {resolve}; B repeats the insert and the read. Errors, rows and the Parent row at the end.",
                a =>
                {
                    a.EngineLite().OpenDb("db", "main", false, false);
                    Workspace(a);
                    a.Call("ws", "BeginTrans");
                    if (insertParent)
                    {
                        Dml(a, "db", "INSERT INTO Parent (ID, Name) VALUES (100, 'new')");
                    }
                    else
                    {
                        Dml(a, "db", "DELETE FROM Parent WHERE ID = 5");
                    }
                    a.Signal("a-open").Await("b-1", Wait).Call("ws", resolve).Errors().Signal("a-resolved").Await("b-done", Wait);
                    a.Snap("db", "parent", $"SELECT * FROM Parent WHERE ID = {parent}");
                    a.FinishLite("db");
                })
            {
                Fixtures = [("main", Fx("rel-accdb"))],
                Participants =
                [
                    ("b", b =>
                    {
                        b.Await("a-open", Wait).Create().Modules("after-create").OpenDb("db", "main", false, false);
                        Dml(b, "db", $"INSERT INTO Child (PID, V) VALUES ({parent}, 1)");
                        b.Errors().Snap("db", "r1", $"SELECT * FROM Child WHERE PID = {parent}").Signal("b-1").Await("a-resolved", Wait);
                        Dml(b, "db", $"INSERT INTO Child (PID, V) VALUES ({parent}, 2)");
                        b.Errors().Snap("db", "r2", $"SELECT * FROM Child WHERE PID = {parent} ORDER BY ID").Call("db", "Close").Signal("b-done");
                    }),
                ],
            };
        }

        yield return new Probe(Card, "txn-xproc-cascade-delete-vs-pending-child-edit", R("R-TXN-1", "TXN-STMT", "WRITE-TIMING", "R-LOCK-4"),
            $"On {Fx("rel-accdb")}: process B opens a dynaset on ChildCD, calls Edit on the first row and sets V = 999 (not updated). Process A runs DELETE FROM Parent WHERE ID = 3 (R_CD cascades to ChildCD) without dbFailOnError, then reads Database.RecordsAffected, DBEngine.Errors and the rows of ChildCD and Parent. B then calls Update and closes; A reads the same again.",
            a =>
            {
                a.EngineLite().OpenDb("db", "main", false, false).Signal("a-open").Await("b-edit", Wait);
                a.Call("db", "Execute", A.Str("DELETE FROM Parent WHERE ID = 3")).Get("db", "RecordsAffected").Errors()
                    .Snap("db", "c1", "SELECT * FROM ChildCD ORDER BY ID").Snap("db", "p1", "SELECT * FROM Parent WHERE ID = 3").Signal("a-1").Await("b-done", Wait);
                a.Snap("db", "c2", "SELECT * FROM ChildCD ORDER BY ID").Snap("db", "p2", "SELECT * FROM Parent WHERE ID = 3");
                a.FinishLite("db");
            })
        {
            Fixtures = [("main", Fx("rel-accdb"))],
            Participants =
            [
                ("b", b =>
                {
                    b.Await("a-open", Wait).Create().Modules("after-create").OpenDb("db", "main", false, false)
                        .OpenRs("rs", "db", "SELECT * FROM ChildCD ORDER BY ID", Dao.OpenDynaset).Call("rs", "Edit")
                        .GetBind("fl", "rs", "Fields").Item("fv", "fl", A.Str("V")).Put("fv", "Value", A.I4(999)).Errors()
                        .Signal("b-edit").Await("a-1", Wait).Call("rs", "Update").Errors().Call("rs", "Close").Call("db", "Close").Signal("b-done");
                }),
            ],
        };
    }

    // ============================================================================================ R-LOCK

    private static IEnumerable<Probe> LockProbes()
    {
        yield return new Probe(Card, "lock-xproc-lockfile-by-holder-count", R("R-LOCK-1", "EX-PROBE", "R-LOCK-2", "R-LOCK-3"),
            $"On {Fx("rel-accdb")}: process A opens it with DAO shared read/write; process C opens the lock file with all sharing and reads 256 bytes at offset 0 (one holder); process B opens it shared read/write too and C reads again (two holders); B closes and C reads again (one holder). The bytes and the file states are recorded; nothing is interpreted.",
            a => a.EngineLite().OpenDb("db", "main", false, false).Files("a-open").Signal("a-open").Await("c-done", Wait).FinishLite("db"))
        {
            Fixtures = [("main", Fx("rel-accdb"))],
            Participants =
            [
                ("b", b => b.Await("c-1", Wait).Create().Modules("after-create").OpenDb("db", "main", false, false).Files("b-open").Signal("b-open").Await("c-2", Wait).Call("db", "Close").Signal("b-closed")),
                ("c", c => c.Await("a-open", Wait)
                    .NativeOpen("lk1", "lock:main", Win.GenericRead, Win.ShareAll).NativeRead("lk1", 0, 256).NativeClose("lk1").Files("c-1").Signal("c-1")
                    .Await("b-open", Wait)
                    .NativeOpen("lk2", "lock:main", Win.GenericRead, Win.ShareAll).NativeRead("lk2", 0, 256).NativeClose("lk2").Files("c-2").Signal("c-2")
                    .Await("b-closed", Wait)
                    .NativeOpen("lk3", "lock:main", Win.GenericRead, Win.ShareAll).NativeRead("lk3", 0, 256).NativeClose("lk3").Files("c-3").Signal("c-done")),
            ],
        };
    }

    // ============================================================================================ R-FMT-2, R-PRESERVE-1

    private static IEnumerable<Probe> FormatProbes()
    {
        yield return Own("fmt-mask-boundary-columns", R("R-FMT-2", "R-PRESERVE-1", "II-04"),
            "Tables W7, W8, W9, W15, W16 and W17 hold 7, 8, 9, 15, 16 and 17 LONG columns c1..cN. Each gets three rows: all columns set, only the last column set, and every column but the last set. The rows are read back and the closed file is kept, so null-mask boundaries at multiples of eight are in one image.",
            p =>
            {
                p.EngineLite();
                Make(p, Fx("fmt-mask") + ".accdb");
                foreach (int n in new[] { 7, 8, 9, 15, 16, 17 })
                {
                    string[] names = [.. Enumerable.Range(1, n).Select(i => "c" + i)];
                    Ddl(p, "db", $"CREATE TABLE W{n} ({string.Join(", ", names.Select(c => c + " LONG"))})");
                    Insert(p, "db", $"W{n}", string.Join(", ", names), string.Join(", ", Enumerable.Range(1, n)));
                    Insert(p, "db", $"W{n}", names[n - 1], "5");
                    Insert(p, "db", $"W{n}", string.Join(", ", names.Take(n - 1)), string.Join(", ", Enumerable.Range(1, n - 1).Select(i => i * 100)));
                    p.Snap("db", "w" + n, $"SELECT * FROM W{n}", 8, 24);
                }
                p.Call("db", "Close").Files("closed-image", keep: true);
            });

        yield return Own("fmt-mdb-delete-compact-index", R("R-FMT-2", "R-PRESERVE-1", "R-REBUILD-1"),
            "Jet 4 MDB: T(ID LONG key, K TEXT 40 under index IX_K, M MEMO) with 2,048 rows built by doubling inserts. Half the rows are deleted by ID Mod 2. The index DistinctCount and the row count are read after the build, after the delete, and after DBEngine.CompactDatabase to a new file; images after the build, the delete and the compaction are kept.",
            p =>
            {
                p.EngineLite();
                string file = Fx("fmt-mdb-dc") + ".mdb", compacted = Fx("fmt-mdb-dc-compacted") + ".mdb";
                Make(p, file, mdb: true);
                Ddl(p, "db", "CREATE TABLE T (ID LONG CONSTRAINT PK_T PRIMARY KEY, K TEXT(40), M MEMO)");
                Ddl(p, "db", "CREATE INDEX IX_K ON T (K)");
                Dml(p, "db", $"INSERT INTO T (ID, K, M) VALUES (1, 'key1', '{new string('m', 1500)}')");
                for (int n = 1; n <= 1024; n *= 2)
                {
                    Dml(p, "db", $"INSERT INTO T (ID, K, M) SELECT ID + {n}, 'key' & (ID + {n}), M FROM T");
                }
                p.Snap("db", "c1", "SELECT COUNT(*) FROM T");
                DistinctCount(p, "db", "T", "IX_K", "1");
                p.Call("db", "Close").Files("built", keep: true);
                OpenOut(p, "db", file);
                Dml(p, "db", "DELETE FROM T WHERE ID Mod 2 = 0");
                p.Snap("db", "c2", "SELECT COUNT(*) FROM T");
                DistinctCount(p, "db", "T", "IX_K", "2");
                p.Call("db", "Close").Release("db").Files("deleted", keep: true);
                p.Call("eng", "CompactDatabase", A.Out(file), A.Out(compacted), A.Str(Dao.LangGeneral), A.I4(Dao.Version40)).Errors();
                OpenOut(p, "db", compacted, false, true);
                p.Snap("db", "c3", "SELECT COUNT(*) FROM T");
                DistinctCount(p, "db", "T", "IX_K", "3");
                p.Call("db", "Close").Files("compacted", keep: true);
            });

        yield return Own("fmt-guid-genguid-values", R("R-FMT-2", "R-PRESERVE-1", "R-EXPR-1"),
            "Generated GUID values through DAO: G(ID COUNTER key, U GUID) takes three rows from INSERT ... VALUES (GenGUID()) and one literal '{00112233-4455-6677-8899-AABBCCDDEEFF}'; DDL 'U GUID DEFAULT GenGUID()' is attempted; and G3 gets a GUID field by CreateField(15) with DefaultValue 'GenGUID()' and takes three rows without naming it. Errors, the stored U values, their length as text and the number of distinct values are read.",
            p =>
            {
                p.EngineLite();
                Make(p, Fx("fmt-guid") + ".accdb");
                Ddl(p, "db", "CREATE TABLE G (ID COUNTER CONSTRAINT PK_G PRIMARY KEY, U GUID)");
                for (int i = 0; i < 3; i++)
                {
                    Dml(p, "db", "INSERT INTO G (U) VALUES (GenGUID())");
                    p.Errors();
                }
                Dml(p, "db", "INSERT INTO G (U) VALUES ('{00112233-4455-6677-8899-AABBCCDDEEFF}')");
                p.Errors().Snap("db", "g", "SELECT ID, U, Len(CStr(U)) FROM G ORDER BY ID").Snap("db", "gd", "SELECT COUNT(*) FROM (SELECT DISTINCT U FROM G)");
                Ddl(p, "db", "CREATE TABLE G2 (ID COUNTER CONSTRAINT PK_G2 PRIMARY KEY, U GUID DEFAULT GenGUID())");
                p.Errors();
                Ddl(p, "db", "CREATE TABLE G3 (ID COUNTER CONSTRAINT PK_G3 PRIMARY KEY, Tag LONG)");
                p.GetBind("g3s", "db", "TableDefs").Item("g3t", "g3s", A.Str("G3")).CallBind("g3f", "g3t", "CreateField", A.Str("U"), A.I4(15))
                    .Put("g3f", "DefaultValue", A.Str("GenGUID()")).Errors().GetBind("g3fs", "g3t", "Fields").Call("g3fs", "Append", A.Obj("g3f")).Errors();
                for (int i = 1; i <= 3; i++)
                {
                    Dml(p, "db", $"INSERT INTO G3 (Tag) VALUES ({i})");
                    p.Errors();
                }
                p.Snap("db", "g3", "SELECT ID, Tag, U FROM G3 ORDER BY ID").Snap("db", "g3d", "SELECT COUNT(*) FROM (SELECT DISTINCT U FROM G3)");
                p.FinishLite("db");
            });

        yield return Own("fmt-index-growth-ladder", R("R-FMT-2", "II-05", "R-SORT-3"),
            "ACCDB: T(ID LONG key, K TEXT 100 under index IX_K, V LONG) starts with one row and doubles by INSERT ... SELECT 13 times to 8,192 rows with distinct keys of about 30 characters. At 2, 16, 128, 1,024 and 8,192 rows: the row count, the DistinctCount of IX_K and the file length and state; the closed file with 8,192 rows is kept.",
            p =>
            {
                p.EngineLite();
                Make(p, Fx("fmt-growth") + ".accdb");
                Ddl(p, "db", "CREATE TABLE T (ID LONG CONSTRAINT PK_T PRIMARY KEY, K TEXT(100), V LONG)");
                Ddl(p, "db", "CREATE INDEX IX_K ON T (K)");
                Dml(p, "db", "INSERT INTO T (ID, K, V) VALUES (1, 'key-1-padding-padding-padding', 0)");
                int rows = 1;
                for (int step = 1; step <= 13; step++)
                {
                    Dml(p, "db", $"INSERT INTO T (ID, K, V) SELECT ID + {rows}, 'key-' & (ID + {rows}) & '-padding-padding-padding', V FROM T");
                    rows *= 2;
                    if (step is 1 or 4 or 7 or 10 or 13)
                    {
                        p.Snap("db", "n" + step, "SELECT COUNT(*) FROM T");
                        DistinctCount(p, "db", "T", "IX_K", step.ToString());
                        p.Files("rows-" + rows);
                    }
                }
                p.Call("db", "Close").Files("closed-image", keep: true);
            });

        yield return OwnX("pres-xproc-interleaved-inserts", R("R-PRESERVE-1", "R-FMT-2", "WRITE-ALLOC", "R-LOCK-4"),
            "Two processes, both shared read/write on T(ID COUNTER key, Who TEXT 1, N LONG), insert ten rows each in four alternating rounds (A, B, A, B, ...) ordered by barriers. A then reads the row count, the distinct IDs and the rows by writer; the closed file is kept.",
            a =>
            {
                a.EngineLite();
                string file = Fx("pres-interleave") + ".accdb";
                Make(a, file);
                Ddl(a, "db", "CREATE TABLE T (ID COUNTER CONSTRAINT PK_T PRIMARY KEY, Who TEXT(1), N LONG)");
                Ddl(a, "db", "CREATE TABLE D (X LONG CONSTRAINT PK_D PRIMARY KEY)");
                Insert(a, "db", "D", "X", "0", "1", "2", "3", "4", "5", "6", "7", "8", "9");
                a.Call("db", "Close").Release("db");
                OpenOut(a, "db", file);
                a.Signal("a-ready");
                for (int r = 1; r <= 4; r++)
                {
                    Dml(a, "db", $"INSERT INTO T (Who, N) SELECT 'a', X + {r * 100} FROM D");
                    a.Signal("a-" + r).Await("b-" + r, Wait);
                }
                a.Snap("db", "count", "SELECT COUNT(*), MAX(ID) FROM T").Snap("db", "distinct", "SELECT COUNT(*) FROM (SELECT DISTINCT ID FROM T)")
                    .Snap("db", "by", "SELECT Who, COUNT(*), MIN(ID), MAX(ID) FROM T GROUP BY Who ORDER BY Who");
                a.Call("db", "Close").Files("closed-image", keep: true);
            },
            b =>
            {
                string file = Fx("pres-interleave") + ".accdb";
                b.Await("a-ready", Wait).Create().Modules("after-create");
                OpenOut(b, "db", file);
                for (int r = 1; r <= 4; r++)
                {
                    b.Await("a-" + r, Wait);
                    Dml(b, "db", $"INSERT INTO T (Who, N) SELECT 'b', X + {r * 100} FROM D");
                    b.Signal("b-" + r);
                }
                b.Call("db", "Close");
            });
    }

    // ============================================================================================ R-SORT-3

    private static readonly string[] Corpus =
    [
        "a", "A", "b", "B", "z", "Z", "0", "1", "10", "2", "a-b", "ab", "a b", "a_b", "a'b", "\u00e1", "\u00c1", "\u00e4", "\u00c4",
        "\u00e6", "\u00df", "ss", "\u00e9", "e", "f", "\u00f8", "\u00fe", "\u0153", "\u0142", "\u20ac", "-", " ", "~", "[", " a", "a ", "co-op", "coop",
    ];

    private static void BuildCorpus(P p, string file, bool mdb)
    {
        p.EngineLite();
        Make(p, file, mdb);
        Ddl(p, "db", "CREATE TABLE K (ID COUNTER CONSTRAINT PK_K PRIMARY KEY, S TEXT(20))");
        Ddl(p, "db", "CREATE INDEX IXS ON K (S)");
        Ddl(p, "db", "CREATE INDEX IXD ON K (S DESC)");
        foreach (string s in Corpus)
        {
            Dml(p, "db", $"INSERT INTO K (S) VALUES ('{s.Replace("'", "''")}')");
        }
    }

    private static IEnumerable<Probe> SortProbes()
    {
        yield return Own("sort-mdb-corpus-index-walk", R("R-SORT-3", "SORT-CHECK"),
            "Jet 4 MDB twin of the ACCDB corpus probes: K(ID COUNTER key, S TEXT 20) holds 38 strings (case pairs, accents, ligatures, punctuation, spaces, digits). A table-type recordset walks the index IXS (ascending) and the index IXD (descending) from MoveFirst to EOF; the IDs and strings are read in index order.",
            p =>
            {
                BuildCorpus(p, Fx("sort-mdb-corpus") + ".mdb", mdb: true);
                p.OpenRs("rs", "db", "K", Dao.OpenTable).Put("rs", "Index", A.Str("IXS")).Call("rs", "MoveFirst").ReadAll("rs", 64, 4)
                    .Put("rs", "Index", A.Str("IXD")).Call("rs", "MoveFirst").ReadAll("rs", 64, 4);
                p.FinishLite("rs", "db");
            });

        yield return Own("sort-mdb-corpus-sql-order", R("R-SORT-3", "SORT-CHECK"),
            "The same Jet 4 MDB corpus through SQL: ORDER BY S, ORDER BY S DESC, SELECT DISTINCT S ... ORDER BY S, GROUP BY S with counts, and WHERE S > 'b' AND S < 'c' ORDER BY S. The row order and the groups are read.",
            p =>
            {
                BuildCorpus(p, Fx("sort-mdb-sql") + ".mdb", mdb: true);
                p.Snap("db", "asc", "SELECT ID, S FROM K ORDER BY S", 64, 4).Snap("db", "desc", "SELECT ID, S FROM K ORDER BY S DESC", 64, 4)
                    .Snap("db", "dist", "SELECT DISTINCT S FROM K ORDER BY S", 64, 4).Snap("db", "grp", "SELECT S, COUNT(*) FROM K GROUP BY S ORDER BY S", 64, 4)
                    .Snap("db", "rng", "SELECT ID, S FROM K WHERE S > 'b' AND S < 'c' ORDER BY S", 64, 4);
                p.FinishLite("db");
            });

        foreach ((string name, string index, bool mdb) in new[]
                 {
                     ("accdb-asc", "A, B", false),
                     ("accdb-desc", "A DESC, B DESC", false),
                     ("accdb-mixed", "A, B DESC", false),
                     ("mdb-mixed", "A, B DESC", true),
                 })
        {
            yield return Own("sort-compound-seek-operators-" + name, R("R-SORT-3", "SORT-CHECK", "R-CURSOR-1"),
                $"T(ID COUNTER key, A TEXT 10, B LONG) holds (a,1), (b,1), (b,2), (b,3), (B,2), (c,1), (Null,1), (b,Null), (Null,Null), (bb,1) and has the {(mdb ? "Jet 4 MDB" : "ACCDB")} compound index IX_AB ON T ({index}). On a table recordset with that Index: the walk from MoveFirst, then Seek with each of =, >=, >, <=, < for keys ('b',2), ('b',9), ('bb',1), ('b') and ('0',0), reading NoMatch and the current row after each.",
                p =>
                {
                    p.EngineLite();
                    Make(p, Fx("sort-seek-" + name) + (mdb ? ".mdb" : ".accdb"), mdb);
                    Ddl(p, "db", "CREATE TABLE T (ID COUNTER CONSTRAINT PK_T PRIMARY KEY, A TEXT(10), B LONG)");
                    Ddl(p, "db", $"CREATE INDEX IX_AB ON T ({index})");
                    Insert(p, "db", "T", "A, B", "'a', 1", "'b', 1", "'b', 2", "'b', 3", "'B', 2", "'c', 1", "Null, 1", "'b', Null", "Null, Null", "'bb', 1");
                    p.OpenRs("rs", "db", "T", Dao.OpenTable).Put("rs", "Index", A.Str("IX_AB")).Call("rs", "MoveFirst").ReadAll("rs", 32, 4);
                    foreach (string op in new[] { "=", ">=", ">", "<=", "<" })
                    {
                        foreach ((string a, int? b) in new (string, int?)[] { ("b", 2), ("b", 9), ("bb", 1), ("b", null), ("0", 0) })
                        {
                            if (b is int bv)
                            {
                                p.Call("rs", "Seek", A.Str(op), A.Str(a), A.I4(bv));
                            }
                            else
                            {
                                p.Call("rs", "Seek", A.Str(op), A.Str(a));
                            }
                            p.Get("rs", "NoMatch").ReadRow("rs", 4);
                        }
                    }
                    p.FinishLite("rs", "db");
                });
        }

        yield return Own("sort-unicode-compression-property", R("R-SORT-3", "R-FMT-2", "R-CAT-1", "CAT-P"),
            "Text field S of T is made by DDL (TEXT 20). Which properties does the Field list, does Properties('UnicodeCompression') exist and read, can its Value be put False then True, and if missing can CreateProperty('UnicodeCompression', dbBoolean, True) be appended? Then three rows ('abc', 'caf\u00e9', an Euro sign) are inserted and read back in order by S.",
            p =>
            {
                p.EngineLite();
                Make(p, Fx("sort-ucomp") + ".accdb");
                Ddl(p, "db", "CREATE TABLE T (ID COUNTER CONSTRAINT PK_T PRIMARY KEY, S TEXT(20))");
                p.GetBind("uts", "db", "TableDefs").Item("utd", "uts", A.Str("T")).GetBind("ufs", "utd", "Fields").Item("ufd", "ufs", A.Str("S")).GetBind("ups", "ufd", "Properties")
                    .Each("ups", "upe", 32, b => b.Get("upe", "Name")).Item("uc", "ups", A.Str("UnicodeCompression")).Get("uc", "Value")
                    .Put("uc", "Value", A.Bool(false)).Errors().Get("uc", "Value").Put("uc", "Value", A.Bool(true)).Errors().Get("uc", "Value");
                p.CallBind("np", "ufd", "CreateProperty", A.Str("UnicodeCompression"), A.I4(1), A.Bool(true)).Call("ups", "Append", A.Obj("np")).Errors();
                Insert(p, "db", "T", "S", "'abc'", "'caf\u00e9'", "'\u20ac'");
                p.Snap("db", "rows", "SELECT ID, S, LenB(S) FROM T ORDER BY S", 8, 4);
                p.FinishLite("db");
            });
    }

    // ============================================================================================ R-ENC-1

    private static IEnumerable<Probe> EncProbes()
    {
        yield return Own("enc-accdb-compact-encrypt-decrypt-options", R("R-ENC-1", "ENC-REKEY", "RB-OPTIONS"),
            "ACCDB source with T (3 rows). DBEngine.CompactDatabase with dbVersion120 + dbEncrypt (2); that output with dbVersion120 + dbDecrypt (4); the source with DstLocale carrying ';pwd=CalcDbProbe1'; and that output with SrcLocale ';pwd=CalcDbProbe1' and no password in DstLocale. Errors after each compaction; each output is opened read-only with no password and with the password and counted; the outputs are kept.",
            p =>
            {
                p.EngineLite();
                string src = Fx("enc-src") + ".accdb", enc = Fx("enc-encrypted") + ".accdb", dec = Fx("enc-decrypted") + ".accdb", pwd = Fx("enc-pwd") + ".accdb", plain = Fx("enc-unpwd") + ".accdb";
                Make(p, src);
                TableT(p, "db");
                p.Call("db", "Close").Release("db");
                p.Call("eng", "CompactDatabase", A.Out(src), A.Out(enc), A.Str(Dao.LangGeneral), A.I4(Dao.Version120 | DbEncrypt)).Errors();
                OpenTry(p, "enc", enc, null);
                p.Call("eng", "CompactDatabase", A.Out(enc), A.Out(dec), A.Str(Dao.LangGeneral), A.I4(Dao.Version120 | DbDecrypt)).Errors();
                OpenTry(p, "dec", dec, null);
                p.Call("eng", "CompactDatabase", A.Out(src), A.Out(pwd), A.Str(Dao.LangGeneral + ";pwd=" + Pwd), A.I4(Dao.Version120)).Errors();
                OpenTry(p, "pwdnone", pwd, null);
                OpenTry(p, "pwdright", pwd, ";pwd=" + Pwd);
                p.Call("eng", "CompactDatabase", A.Out(pwd), A.Out(plain), A.Str(Dao.LangGeneral), A.I4(Dao.Version120), A.Str(";pwd=" + Pwd)).Errors();
                OpenTry(p, "plain", plain, null);
                p.Files("after-close", keep: true);
            });

        yield return Own("enc-newpassword-shared-and-readonly-opens", R("R-ENC-1", "ENC-PASSWORD"),
            "ACCDB with T. Database.NewPassword('', 'pw-shared') on a shared read/write open; after closing, the file is opened with no password and with 'pw-shared'. Then NewPassword('', 'pw-ro') on an exclusive read-only open; after closing, opened with no password, with 'pw-ro' and with 'pw-shared'. Errors after each call.",
            p =>
            {
                p.EngineLite();
                string file = Fx("enc-newpw") + ".accdb";
                Make(p, file);
                TableT(p, "db");
                p.Call("db", "Close").Release("db");
                OpenOut(p, "d1", file, false, false);
                p.Call("d1", "NewPassword", A.Str(""), A.Str("pw-shared")).Errors().Call("d1", "Close").Release("d1");
                OpenTry(p, "n1", file, null);
                OpenTry(p, "s1", file, ";pwd=pw-shared");
                OpenOut(p, "d2", file, true, true, ";pwd=pw-shared");
                p.Errors().Call("d2", "NewPassword", A.Str("pw-shared"), A.Str("pw-ro")).Errors().Call("d2", "Close").Release("d2");
                OpenTry(p, "n2", file, null);
                OpenTry(p, "s2", file, ";pwd=pw-ro");
                OpenTry(p, "s3", file, ";pwd=pw-shared");
                p.Files("after-close", keep: true);
            });

        foreach (bool mdb in new[] { true, false })
        {
            string ext = mdb ? "mdb" : "accdb";
            yield return Own($"enc-password-ladder-{ext}", R("R-ENC-1", "ENC-PASSWORD", "ENC-PROFILE"),
                $"One {ext.ToUpperInvariant()} per password, each created with T and opened exclusive: Database.NewPassword('', pw) for pw = 'x', a 20-character string, a 21-character string, 'caf\u00e9\u20ac', 'a b', 'semi;colon' and 'q\"uote'. After closing: open read-only with no password, then with ';pwd=' and the password; then NewPassword(pw, '') on an exclusive open and an open with no password. Errors after each call.",
                p =>
                {
                    p.EngineLite();
                    string[] passwords = ["x", "abcdefghijklmnopqrst", "abcdefghijklmnopqrstu", "caf\u00e9\u20ac", "a b", "semi;colon", "q\"uote"];
                    for (int i = 0; i < passwords.Length; i++)
                    {
                        string file = $"{Fx("enc-ladder-" + ext + "-" + i)}.{ext}", pw = passwords[i], tag = "p" + i;
                        Make(p, file, mdb);
                        TableT(p, "db");
                        p.Call("db", "Close").Release("db");
                        OpenOut(p, "x" + tag, file, true, false);
                        p.Call("x" + tag, "NewPassword", A.Str(""), A.Str(pw)).Errors().Call("x" + tag, "Close").Release("x" + tag);
                        OpenTry(p, "n" + tag, file, null);
                        OpenTry(p, "r" + tag, file, ";pwd=" + pw);
                        OpenOut(p, "y" + tag, file, true, false, ";pwd=" + pw);
                        p.Errors().Call("y" + tag, "NewPassword", A.Str(pw), A.Str("")).Errors().Call("y" + tag, "Close").Release("y" + tag);
                        OpenTry(p, "z" + tag, file, null);
                    }
                    p.Files("after-close");
                });
        }

        yield return OwnX("enc-xproc-protected-holder", R("R-ENC-1", "ENC-PROFILE", "R-LOCK-1", "R-LOCK-2"),
            "ACCDB protected by Database.NewPassword('', 'CalcDbProbe1'). Process A holds it open shared read/write with the password. Process B opens it read-only shared with no password, with a wrong password and with the right one (each counted), then opens it shared read/write with the right password, updates one row and closes. A reads the row.",
            a =>
            {
                a.EngineLite();
                string file = Fx("enc-xholder") + ".accdb";
                Make(a, file);
                TableT(a, "db");
                a.Call("db", "Close").Release("db");
                OpenOut(a, "x", file, true, false);
                a.Call("x", "NewPassword", A.Str(""), A.Str(Pwd)).Errors().Call("x", "Close").Release("x");
                OpenOut(a, "db", file, false, false, ";pwd=" + Pwd);
                a.Errors().Files("a-open").Signal("a-open").Await("b-done", Wait);
                a.Snap("db", "t", "SELECT * FROM T ORDER BY ID");
                a.FinishLite("db");
            },
            b =>
            {
                string file = Fx("enc-xholder") + ".accdb";
                b.Await("a-open", Wait).Create().Modules("after-create");
                OpenTry(b, "none", file, null);
                OpenTry(b, "wrong", file, ";pwd=wrong-pass");
                OpenTry(b, "right", file, ";pwd=" + Pwd);
                OpenOut(b, "db", file, false, false, ";pwd=" + Pwd);
                Dml(b, "db", "UPDATE T SET V = 99 WHERE ID = 1");
                b.Errors().Call("db", "Close").Signal("b-done");
            });
    }

    // ============================================================================================ R-LINK-1

    private static IEnumerable<Probe> LinkProbes()
    {
        foreach ((string name, bool hostMdb, string srcExt) in new[] { ("accdb-host-mdb-source", false, "mdb"), ("mdb-host-accdb-source", true, "accdb") })
        {
            yield return new Probe(Card, "link-" + name, R("R-LINK-1", "LINK-OPEN", "LINK-META", "LINK-UPDATE", "LINK-SCHEMA"),
                $"A {(hostMdb ? "Jet 4 MDB" : "ACCDB")} host made by the probe links T of {Fx("lksrc-" + srcExt)} ({srcExt.ToUpperInvariant()}) as LK through CreateTableDef and TableDefs.Append with a relative Connect. Is the append accepted? Then LK's Connect, SourceTableName, Attributes, Updatable and fields, a read of all rows, INSERT (6, 60, 'six'), UPDATE of row 2 and DELETE of row 3 through LK (dbFailOnError), and a read of the source file itself through a second Database.",
                p =>
                {
                    p.EngineLite();
                    Make(p, $"{Fx("link-host-" + name)}.{(hostMdb ? "mdb" : "accdb")}", hostMdb);
                    LinkTable(p, "db", "1", "LK", "T", LinkConnect(srcExt));
                    LinkInfo(p, "db", "LK", "1");
                    FieldList(p, "db", "LK", "1");
                    p.Snap("db", "r1", "SELECT * FROM LK ORDER BY ID");
                    Dml(p, "db", "INSERT INTO LK (ID, V, S) VALUES (6, 60, 'six')");
                    p.Errors();
                    Dml(p, "db", "UPDATE LK SET V = 222 WHERE ID = 2");
                    p.Errors();
                    Dml(p, "db", "DELETE FROM LK WHERE ID = 3");
                    p.Errors().Snap("db", "r2", "SELECT * FROM LK ORDER BY ID").Call("db", "Close").Release("db");
                    p.OpenDb("src", "src", false, true).Snap("src", "s", "SELECT * FROM T ORDER BY ID").Call("src", "Close").Files("after-close");
                })
            { Fixtures = [("src", Fx("lksrc-" + srcExt))] };
        }

        yield return new Probe(Card, "link-select-in-clause-forms", R("R-LINK-1", "LINK-OPEN", "LINK-PUSHDOWN", "R-SQL-3"),
            $"Without any linked table, in an empty ACCDB made by the probe: which external-table forms does ACE accept against {Fx("lksrc-accdb")} by relative path: SELECT FROM T IN 'path'; SELECT FROM [;DATABASE=path].T; SELECT FROM T IN '' [;DATABASE=path]; INSERT INTO T IN 'path' SELECT ... FROM T IN 'path'; SELECT * INTO Local FROM T IN 'path'; and SELECT FROM T IN a path of a file that does not exist. Errors and rows after each.",
            p =>
            {
                p.EngineLite();
                Make(p, Fx("link-in-host") + ".accdb");
                string path = LinkPath("accdb");
                string[] selects =
                [
                    $"SELECT * FROM T IN '{path}' ORDER BY ID",
                    $"SELECT * FROM [;DATABASE={path}].T ORDER BY ID",
                    $"SELECT * FROM T IN '' [;DATABASE={path}] ORDER BY ID",
                    $@"SELECT * FROM T IN 'inputs\{Fx("lksrc-missing")}.accdb'",
                ];
                for (int i = 0; i < selects.Length; i++)
                {
                    p.Snap("db", "q" + i, selects[i]).Errors();
                }
                Ddl(p, "db", $"INSERT INTO T IN '{path}' SELECT ID + 100, V, S FROM T IN '{path}'");
                p.Errors().Snap("db", "after", $"SELECT * FROM T IN '{path}' ORDER BY ID");
                Ddl(p, "db", $"SELECT * INTO Local FROM T IN '{path}'");
                p.Errors();
                Catalog(p, "db", "1");
                p.FinishLite("db");
            })
        { Fixtures = [("src", Fx("lksrc-accdb"))] };

        yield return new Probe(Card, "link-drop-delete-and-alter-link", R("R-LINK-1", "LINK-SCHEMA", "LINK-RELINK", "CAT-D"),
            $"LK links T of {Fx("lksrc-accdb")}. DROP TABLE LK: errors, the host catalog and the source rows; the link is appended again; ALTER TABLE LK ADD COLUMN W LONG and CREATE INDEX IX_LK ON LK (V): errors, and the fields of the source T through a second Database; the link is renamed by TableDef.Name; TableDefs.Delete('LK2'): errors, host catalog and source rows.",
            p =>
            {
                p.EngineLite();
                Make(p, Fx("link-drop-host") + ".accdb");
                LinkTable(p, "db", "1", "LK", "T", LinkConnect("accdb"));
                Ddl(p, "db", "DROP TABLE LK");
                p.Errors();
                Catalog(p, "db", "1");
                p.OpenDb("src", "src", false, true).Snap("src", "s1", "SELECT COUNT(*) FROM T");
                LinkTable(p, "db", "2", "LK", "T", LinkConnect("accdb"));
                Ddl(p, "db", "ALTER TABLE LK ADD COLUMN W LONG");
                p.Errors();
                Ddl(p, "db", "CREATE INDEX IX_LK ON LK (V)");
                p.Errors();
                FieldList(p, "src", "T", "src");
                IndexList(p, "src", "T", "src");
                p.GetBind("rts", "db", "TableDefs").Item("rtd", "rts", A.Str("LK")).Put("rtd", "Name", A.Str("LK2")).Errors().Release("rtd");
                p.Call("rts", "Delete", A.Str("LK2")).Errors();
                Catalog(p, "db", "2");
                p.Snap("src", "s2", "SELECT COUNT(*) FROM T").Call("src", "Close").FinishLite("db");
            })
        { Fixtures = [("src", Fx("lksrc-accdb"))] };

        yield return new Probe(Card, "link-keyless-source-updatable", R("R-LINK-1", "LINK-UPDATE", "LINK-IDENTITY"),
            $"LKN links NoKey (no key, no index, a duplicate-capable row) of {Fx("lksrc-accdb")}. Updatable and the indexes of LKN; INSERT (9, 'q'), UPDATE and DELETE through LKN (dbFailOnError) with errors; then CREATE UNIQUE INDEX UX_AB ON LKN (A, B) and the same three statements again; the rows of LKN after each stage.",
            p =>
            {
                p.EngineLite();
                Make(p, Fx("link-keyless-host") + ".accdb");
                LinkTable(p, "db", "1", "LKN", "NoKey", LinkConnect("accdb"));
                LinkInfo(p, "db", "LKN", "1");
                IndexList(p, "db", "LKN", "1");
                for (int stage = 1; stage <= 2; stage++)
                {
                    int a = stage * 10;
                    Dml(p, "db", $"INSERT INTO LKN (A, B) VALUES ({a}, 'q')");
                    p.Errors();
                    Dml(p, "db", $"UPDATE LKN SET B = 'u' WHERE A = {a}");
                    p.Errors();
                    Dml(p, "db", "DELETE FROM LKN WHERE A = 1");
                    p.Errors().Snap("db", "r" + stage, "SELECT * FROM LKN ORDER BY A, B");
                    if (stage == 1)
                    {
                        Ddl(p, "db", "CREATE UNIQUE INDEX UX_AB ON LKN (A, B)");
                        p.Errors();
                        LinkInfo(p, "db", "LKN", "2");
                        IndexList(p, "db", "LKN", "2");
                    }
                }
                p.FinishLite("db");
            })
        { Fixtures = [("src", Fx("lksrc-accdb"))] };

        yield return new Probe(Card, "link-xproc-external-source-writer", R("R-LINK-1", "LINK-SESSION", "LINK-SCHEMA", "R-LOCK-3"),
            $"Process A (host made by the probe) holds LK linked to T of {Fx("lksrc-accdb")} and reads it through a held snapshot and a held dynaset. Process B opens the source file directly (shared, read/write), updates rows 1 and 2, inserts row 7 and closes. A reads LK again through the held recordsets, through a new snapshot, after DBEngine.Idle(dbRefreshCache), and after a Requery of the dynaset.",
            a =>
            {
                a.EngineLite();
                Make(a, Fx("link-xw-host") + ".accdb");
                LinkTable(a, "db", "1", "LK", "T", LinkConnect("accdb"));
                a.OpenRs("r1", "db", "SELECT * FROM LK ORDER BY ID", Dao.OpenSnapshot).ReadAll("r1").Call("r1", "MoveFirst")
                    .OpenRs("r2", "db", "SELECT * FROM LK ORDER BY ID", Dao.OpenDynaset).ReadAll("r2").Call("r2", "MoveFirst")
                    .Signal("a-ready").Await("b-done", Wait);
                a.ReadAll("r1").Call("r1", "MoveFirst").ReadAll("r2").Call("r2", "MoveFirst");
                a.Snap("db", "fresh", "SELECT * FROM LK ORDER BY ID").Call("eng", "Idle", A.I4(DbRefreshCache)).Snap("db", "idle", "SELECT * FROM LK ORDER BY ID");
                a.Call("r2", "Requery").Errors().ReadAll("r2");
                a.FinishLite("r1", "r2", "db");
            })
        {
            Fixtures = [("src", Fx("lksrc-accdb"))],
            Participants =
            [
                ("b", b =>
                {
                    b.Await("a-ready", Wait).Create().Modules("after-create").OpenDb("sdb", "src", false, false);
                    Dml(b, "sdb", "UPDATE T SET V = V + 1000 WHERE ID <= 2");
                    Insert(b, "sdb", "T", "ID, V, S", "7, 70, 's7'");
                    b.Errors().Call("sdb", "Close").Signal("b-done");
                }),
            ],
        };

        yield return new Probe(Card, "link-xproc-attach-exclusive-source", R("R-LINK-1", "LINK-SESSION", "R-LOCK-1", "R-LOCK-2"),
            $"Process A appends LK to T of {Fx("lksrc-accdb")} with Attributes dbAttachExclusive (65536) and reads LK through a held snapshot. Process B tries DAO shared read-only and read/write opens of the source and a native read share-read open of it; A closes the recordset and the Database and B tries again. Errors and file states recorded at each step.",
            a =>
            {
                a.EngineLite();
                Make(a, Fx("link-ex-host") + ".accdb");
                LinkTable(a, "db", "1", "LK", "T", LinkConnect("accdb"), AttachExclusive);
                LinkInfo(a, "db", "LK", "1");
                a.OpenRs("r1", "db", "SELECT * FROM LK ORDER BY ID", Dao.OpenSnapshot).ReadAll("r1").Files("a-open").Signal("a-open").Await("b-1", Wait);
                a.Call("r1", "Close").Release("r1").Call("db", "Close").Release("db").Files("a-closed").Signal("a-closed").Await("b-done", Wait);
                a.Files("end");
            })
        {
            Fixtures = [("src", Fx("lksrc-accdb"))],
            Participants =
            [
                ("b", b =>
                {
                    b.Await("a-open", Wait).Create().Modules("after-create");
                    foreach ((string tag, bool ro) in new[] { ("1ro", true), ("1rw", false) })
                    {
                        b.CallBind("d" + tag, "eng", "OpenDatabase", A.In("src"), A.Bool(false), A.Bool(ro)).Errors();
                        b.Snap("d" + tag, "c" + tag, "SELECT COUNT(*) FROM T").Call("d" + tag, "Close");
                    }
                    b.NativeOpen("n1", "input:src", Win.GenericRead, Win.ShareRead).NativeClose("n1").Signal("b-1").Await("a-closed", Wait);
                    b.CallBind("d2", "eng", "OpenDatabase", A.In("src"), A.Bool(false), A.Bool(false)).Errors();
                    b.Snap("d2", "c2", "SELECT COUNT(*) FROM T").Call("d2", "Close");
                    b.NativeOpen("n2", "input:src", Win.GenericRead, Win.ShareRead).NativeClose("n2").Signal("b-done");
                }),
            ],
        };
    }

    // ============================================================================================ R-COMPLEX-1, R-REBUILD-1

    private static IEnumerable<Probe> ComplexRebuildProbes()
    {
        yield return Own("cx-ddl-complex-keywords", R("R-COMPLEX-1", "CX-DESC"),
            "Does DAO SQL DDL create a complex column? In an ACCDB, one statement each: CREATE TABLE with a column typed ATTACHMENT, MULTIVALUE, LONG MULTIVALUE, TEXT(20) MULTIVALUE and COMPLEX; then a control table X7(ID, F LONG) with ALTER TABLE ADD COLUMN A ATTACHMENT and ADD COLUMN B LONG MULTIVALUE. Errors after each; the TableDefs and the fields of X7 are read.",
            p =>
            {
                p.EngineLite();
                Make(p, Fx("cx-ddl") + ".accdb");
                string[] statements =
                [
                    "CREATE TABLE X1 (ID COUNTER CONSTRAINT PK_X1 PRIMARY KEY, A ATTACHMENT)",
                    "CREATE TABLE X2 (ID COUNTER CONSTRAINT PK_X2 PRIMARY KEY, A MULTIVALUE)",
                    "CREATE TABLE X3 (ID COUNTER CONSTRAINT PK_X3 PRIMARY KEY, A LONG MULTIVALUE)",
                    "CREATE TABLE X4 (ID COUNTER CONSTRAINT PK_X4 PRIMARY KEY, A TEXT(20) MULTIVALUE)",
                    "CREATE TABLE X6 (ID COUNTER CONSTRAINT PK_X6 PRIMARY KEY, A COMPLEX)",
                    "CREATE TABLE X7 (ID COUNTER CONSTRAINT PK_X7 PRIMARY KEY, F LONG)",
                    "ALTER TABLE X7 ADD COLUMN A ATTACHMENT",
                    "ALTER TABLE X7 ADD COLUMN B LONG MULTIVALUE",
                ];
                foreach (string sql in statements)
                {
                    Ddl(p, "db", sql);
                    p.Errors();
                }
                Catalog(p, "db", "1");
                FieldList(p, "db", "X7", "1");
                p.FinishLite("db");
            });

        yield return Own("cx-drop-complex-column-then-compact", R("R-COMPLEX-1", "CX-DESC", "R-REBUILD-1", "RB-RELOC"),
            "ACCDB table T(ID COUNTER key, Name) with a multi-value Text field Tags (CreateField 109) and children for two parents. TableDefs with attributes, then ALTER TABLE T DROP COLUMN Tags (errors, TableDefs and the fields of T), then DBEngine.CompactDatabase to a new file and the TableDefs and fields of the output. The closed files are kept.",
            p =>
            {
                p.EngineLite();
                string file = Fx("cx-dropcol") + ".accdb", compacted = Fx("cx-dropcol-compacted") + ".accdb";
                Make(p, file);
                p.Sql("CREATE TABLE T (ID COUNTER CONSTRAINT PK_T PRIMARY KEY, Name TEXT(20))").AddField("T", "Tags", 109, "a")
                    .Sql("INSERT INTO T (Name) VALUES ('p1')").Sql("INSERT INTO T (Name) VALUES ('p2')")
                    .Fill("T", "p1", "Tags", A.Str("alpha"), A.Str("beta")).Fill("T", "p2", "Tags", A.Str("alpha"));
                Catalog(p, "db", "1");
                FieldList(p, "db", "T", "1");
                p.Sql("ALTER TABLE T DROP COLUMN Tags").Errors();
                Catalog(p, "db", "2");
                FieldList(p, "db", "T", "2");
                p.Call("db", "Close").Release("db").Files("dropped", keep: true);
                p.Call("eng", "CompactDatabase", A.Out(file), A.Out(compacted), A.Str(Dao.LangGeneral), A.I4(Dao.Version120)).Errors();
                OpenOut(p, "db", compacted, false, true);
                Catalog(p, "db", "3");
                FieldList(p, "db", "T", "3");
                p.Call("db", "Close").Files("compacted", keep: true);
            });

        yield return new Probe(Card, "rb-compact-linked-database", R("R-REBUILD-1", "RB-CLEAN", "RB-OPTIONS", "R-LINK-1"),
            $"An ACCDB host made by the probe holds LK linked to T of {Fx("lksrc-accdb")} and a local table L with two rows. DBEngine.CompactDatabase of the host to a new file: errors; in the output, LK's Connect, SourceTableName, Attributes and Updatable, the rows of LK through the link, and the rows of L. The closed outputs are kept.",
            p =>
            {
                p.EngineLite();
                string host = Fx("rb-link-host") + ".accdb", compacted = Fx("rb-link-compacted") + ".accdb";
                Make(p, host);
                Ddl(p, "db", "CREATE TABLE L (ID LONG CONSTRAINT PK_L PRIMARY KEY, S TEXT(10))");
                Insert(p, "db", "L", "ID, S", "1, 'a'", "2, 'b'");
                LinkTable(p, "db", "1", "LK", "T", LinkConnect("accdb"));
                LinkInfo(p, "db", "LK", "1");
                p.Call("db", "Close").Release("db").Files("before", keep: true);
                p.Call("eng", "CompactDatabase", A.Out(host), A.Out(compacted), A.Str(Dao.LangGeneral), A.I4(Dao.Version120)).Errors();
                OpenOut(p, "db", compacted, false, true);
                LinkInfo(p, "db", "LK", "2");
                p.Snap("db", "lk", "SELECT * FROM LK ORDER BY ID").Snap("db", "l", "SELECT * FROM L ORDER BY ID");
                p.Call("db", "Close").Files("compacted", keep: true);
            })
        { Fixtures = [("src", Fx("lksrc-accdb"))] };

        yield return Own("rb-compact-hidden-attributes", R("R-REBUILD-1", "RB-CLEAN", "R-PRESERVE-1", "R-CAT-1"),
            "ACCDB tables T1 and T2 with a row each. TableDef.Attributes is put to dbHiddenObject (1) on T1 and read back (errors recorded), T2 is renamed to T2x by TableDef.Name, and a saved query Q1 over T1 gets its Name put to Q1x. The TableDefs with attributes and the QueryDefs are read before and after DBEngine.CompactDatabase to a new file; images are kept.",
            p =>
            {
                p.EngineLite();
                string file = Fx("rb-hidden") + ".accdb", compacted = Fx("rb-hidden-compacted") + ".accdb";
                Make(p, file);
                Ddl(p, "db", "CREATE TABLE T1 (ID LONG CONSTRAINT PK_T1 PRIMARY KEY, V LONG)");
                Ddl(p, "db", "CREATE TABLE T2 (ID LONG CONSTRAINT PK_T2 PRIMARY KEY, V LONG)");
                Insert(p, "db", "T1", "ID, V", "1, 10");
                Insert(p, "db", "T2", "ID, V", "1, 20");
                p.CallBind("q1", "db", "CreateQueryDef", A.Str("Q1"), A.Str("SELECT * FROM T1;"));
                p.GetBind("hts", "db", "TableDefs").Item("h1", "hts", A.Str("T1")).Put("h1", "Attributes", A.I4(1)).Errors().Get("h1", "Attributes")
                    .Item("h2", "hts", A.Str("T2")).Put("h2", "Name", A.Str("T2x")).Errors().Put("q1", "Name", A.Str("Q1x")).Errors();
                Catalog(p, "db", "1");
                p.Call("db", "Close").Release("db").Files("before", keep: true);
                p.Call("eng", "CompactDatabase", A.Out(file), A.Out(compacted), A.Str(Dao.LangGeneral), A.I4(Dao.Version120)).Errors();
                OpenOut(p, "db", compacted, false, true);
                Catalog(p, "db", "2");
                p.Snap("db", "t1", "SELECT * FROM T1").Snap("db", "t2", "SELECT * FROM T2x");
                p.Call("db", "Close").Files("compacted", keep: true);
            });

        yield return new Probe(Card, "rb-compact-source-equals-destination", R("R-REBUILD-1", "RB-NEGATIVE", "RB-PUBLISH"),
            $"On {Fx("rel-accdb")}: DBEngine.CompactDatabase with the destination the same path as the source; then with the destination the same file in upper-case spelling. Errors after each, then the file is opened read-only and its Parent rows and relations are read, and the file states recorded.",
            p =>
            {
                p.EngineLite().Files("before");
                p.Call("eng", "CompactDatabase", A.In(), A.In(), A.Str(Dao.LangGeneral), A.I4(Dao.Version120)).Errors().Files("after-same");
                p.Call("eng", "CompactDatabase", A.In(), A.InAs("upper"), A.Str(Dao.LangGeneral), A.I4(Dao.Version120)).Errors().Files("after-upper");
                p.OpenDb("db", "main", false, true).Snap("db", "parent", "SELECT * FROM Parent ORDER BY ID");
                Catalog(p, "db", "1");
                p.FinishLite("db");
            })
        { Fixtures = [("main", Fx("rel-accdb"))] };
    }
}
