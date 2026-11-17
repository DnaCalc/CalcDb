namespace CalcDb.Probes.Cards;

/// <summary>
/// Second qualification follow-up card for R-CURSOR-1, R-DAO-1 and R-ERR-1 (The Plan 8.1), after the 49 records of
/// evidence/r-q-cursor-dao. Two kinds of probe only: (1) the variants that the final notes still name as F (snapshot
/// materialisation, bookmark after a populated Requery, GetRows on a deleted current row, a clone created before BeginTrans, a Databases
/// collection held before DBEngine.CreateDatabase, Permissions after a put, a Null that sorts after a duplicate, lock granularity on a
/// multi-page table); (2) the T items that the new worker operations make addressable: IEnumVARIANT enumeration (DAO-ENUM,
/// ERR-SNAPSHOT), by-reference and named arguments (ERR-BIND), raw activation (ERR-BASE), ADO connection and recordset event sinks
/// (ERR-ADO, ERR-NEST) and suspending a participant inside a call (DAO-CREATE). Every probe opens a fresh copy of fixture
/// r-q2-cursor-dao-main (read-write) and provokes a known error first (DROP TABLE Q2NoSuchMarker, 3376) so each later
/// DBEngine.Errors read belongs to its own step; collections are refreshed after DDL before they are read; dbFailOnError (128)
/// is used where a refusal must be raised. Probes ask; they hold no expected answers. No call can open a dialog (no ODBC, no DSN,
/// no OpenDatabase/CreateDatabase/CompactDatabase with an empty, Missing or Null name, no Requery with an argument, no Array()).
/// A database returned by CreateDatabase is closed before a peer opens it. Scratch outputs do not carry the card prefix.
/// </summary>
public sealed class RQ2CursorDao : ICard
{
    private const string Card = "r-q2-cursor-dao";
    private const string Cur = "R-CURSOR-1", DaoCard = "R-DAO-1", ErrCard = "R-ERR-1";
    private const string Fx = Card + "-main";
    private const int UseJet = 2, Fail = Dao.FailOnError, Wait = 30000;
    private const string AceOleDb = "Microsoft.ACE.OLEDB.12.0";
    private const string AdoConnEvents = "{00001400-0000-0010-8000-00AA006D2EA4}";
    private const string AdoRsEvents = "{00001266-0000-0010-8000-00AA006D2EA4}";
    private const string IidDispatch = "{00020400-0000-0000-C000-000000000046}";
    private static readonly string V100 = new('v', 100);

    public IEnumerable<Probe> Probes() =>
        [CreateMain(), .. Pop(), .. Ids(), .. Bulk(), .. Life(), .. Dao1(), .. Enums(), .. Args(), .. Err1()];

    // Fixture ------------------------------------------------------------------------------------------------

    private static Probe CreateMain() =>
        new(Card, "create-main", [Cur, DaoCard, ErrCard],
            $"Fixture {Fx}: T(ID PK, N, S) 8 rows (N 10,20,30,40,40,50,60,70); Sm(ID PK, V TEXT(100)) 8 rows; Big(ID PK, V TEXT(120)) 2048 rows; IbA and IbB heap tables (A,B) with the rows (1,1),(1,1),(9,Null) in two insertion orders (IbB: the Null row first); " +
            "PA(ID PK) 1-5, CH(CID PK, PID) with relation RelPC (PA.ID to CH.PID); saved query QP (PARAMETERS pA Long, pB Text). Created in ACCDB by DBEngine.CreateDatabase(path, dbLangGeneral, dbVersion120); the closed file is kept.",
            p =>
            {
                p.EngineLite().CallBind("db", "eng", "CreateDatabase", A.Out(Fx + ".accdb"), A.Str(Dao.LangGeneral), A.I4(Dao.Version120));
                Sql(p, "CREATE TABLE T (ID LONG CONSTRAINT PK_T PRIMARY KEY, N LONG, S TEXT(20))");
                Sql(p, "CREATE TABLE Sm (ID LONG CONSTRAINT PK_Sm PRIMARY KEY, V TEXT(100))");
                int[] ns = [10, 20, 30, 40, 40, 50, 60, 70];
                for (int i = 0; i < ns.Length; i++)
                {
                    Sql(p, $"INSERT INTO T (ID, N, S) VALUES ({i + 1}, {ns[i]}, 's{i + 1}')");
                    Sql(p, $"INSERT INTO Sm (ID, V) VALUES ({i + 1}, '{V100}')");
                }
                Sql(p, "CREATE TABLE Big (ID LONG CONSTRAINT PK_Big PRIMARY KEY, V TEXT(120))");
                Sql(p, $"INSERT INTO Big (ID, V) VALUES (1, '{V100}')");
                for (int k = 1; k < 2048; k *= 2)
                {
                    Sql(p, $"INSERT INTO Big (ID, V) SELECT ID + {k}, V FROM Big");
                }
                Sql(p, "CREATE TABLE IbA (A LONG, B LONG)");
                Sql(p, "CREATE TABLE IbB (A LONG, B LONG)");
                foreach (string v in new[] { "1, 1", "1, 1", "9, Null" })
                {
                    Sql(p, $"INSERT INTO IbA (A, B) VALUES ({v})");
                }
                foreach (string v in new[] { "9, Null", "1, 1", "1, 1" })
                {
                    Sql(p, $"INSERT INTO IbB (A, B) VALUES ({v})");
                }
                Sql(p, "CREATE TABLE PA (ID LONG CONSTRAINT PK_PA PRIMARY KEY)");
                Sql(p, "CREATE TABLE CH (CID LONG CONSTRAINT PK_CH PRIMARY KEY, PID LONG)");
                for (int i = 1; i <= 5; i++)
                {
                    Sql(p, $"INSERT INTO PA (ID) VALUES ({i})");
                    Sql(p, $"INSERT INTO CH (CID, PID) VALUES ({i}, {i})");
                }
                p.AddRelation("RelPC", "PA", "CH", "ID", "PID");
                p.CallBind("qd", "db", "CreateQueryDef", A.Str("QP"), A.Str("PARAMETERS pA Long, pB Text (10); SELECT ID, N FROM T WHERE ID >= pA AND S <> pB;")).Release("qd");
                p.FinishLite("db");
            })
        { CreatesFixture = true };

    // F: CUR-POP ----------------------------------------------------------------------------------------------

    private static IEnumerable<Probe> Pop()
    {
        yield return SnapPop("pop-snapshot-plain-table-big", "Big", "ID, V FROM Big", [2, 1000, 2000], [1, 998, 1000], true,
            "a snapshot opened on the plain TABLE name 'Big' (2048 rows)");
        yield return SnapPop("pop-snapshot-select-noorder-big", "SELECT ID, V FROM Big", "ID, V FROM Big", [2, 1000, 2000], [1, 998, 1000], true,
            "a snapshot over SELECT ID, V FROM Big WITHOUT an ORDER BY (2048 rows)");
        yield return SnapPop("pop-snapshot-orderby-small", "SELECT ID, V FROM Sm ORDER BY ID", "ID, V FROM Sm", [2, 4, 8], [1, 2, 4], false,
            "a snapshot over SELECT ... ORDER BY ID on the 8-row table Sm");
    }

    private static Probe SnapPop(string name, string source, string table, int[] ids, int[] moves, bool bigCounts, string what) =>
        Fix(name, [Cur, "CUR-POP"],
            $"CUR-POP (F: why the ORDER BY snapshot of the first card reported 2048 at open), variant: {what}. Part 1: RecordCount at open, after a field read, after MoveNext" + (bigCounts ? ", Move 9, 90, 400 and 1500 (RecordCount and AbsolutePosition each), MoveLast" : " and MoveLast") +
            $". Part 2 (a new snapshot): read ID and V of row 1, then another call runs UPDATE {(table.Contains("Big") ? "Big" : "Sm")} SET V = 'chg' WHERE ID IN ({string.Join(", ", ids)}) (dbFailOnError); MoveFirst and ID and V are read, then Move {string.Join(", ", moves)} (cumulative) reading ID and V each time. Decides whether the count at open and the fixing of values follow ORDER BY or table size.",
            p =>
            {
                p.OpenRs("rs", "db", source, Dao.OpenSnapshot).Get("rs", "RecordCount");
                Val(p, "rs", "ID").Get("rs", "RecordCount").Call("rs", "MoveNext").Get("rs", "RecordCount").Get("rs", "AbsolutePosition");
                if (bigCounts)
                {
                    foreach (int n in new[] { 9, 90, 400, 1500 })
                    {
                        p.Call("rs", "Move", A.I4(n)).Get("rs", "RecordCount").Get("rs", "AbsolutePosition");
                    }
                }
                p.Call("rs", "MoveLast").Get("rs", "RecordCount");
                Close(p, "rs");
                p.OpenRs("rs", "db", source, Dao.OpenSnapshot);
                Val(Val(p, "rs", "ID"), "rs", "V");
                Exec(p, $"UPDATE {(table.Contains("Big") ? "Big" : "Sm")} SET V = 'chg' WHERE ID IN ({string.Join(", ", ids)})");
                p.Call("rs", "MoveFirst");
                Val(Val(p, "rs", "ID"), "rs", "V");
                foreach (int n in moves)
                {
                    p.Call("rs", "Move", A.I4(n));
                    Val(Val(p, "rs", "ID"), "rs", "V");
                }
                Close(p, "rs");
            });

    // F: CUR-ID ------------------------------------------------------------------------------------------------

    private static IEnumerable<Probe> Ids()
    {
        yield return Fix("id-bookmark-requery-populated", [Cur, "CUR-ID"],
            "CUR-ID (F: Requery confounded by population), variant: a retained Bookmark after Requery once the dynaset is populated again. A dynaset on T moves to ID 3 and reads Bookmark. (1) control: the constant 02 02 00 00 is assigned before Requery; " +
            "(2) Requery, MoveLast (population complete), the constant is assigned; (3) Requery, Move 2 (rows 1-3 populated), the constant is assigned; (4) Requery with no population, the constant is assigned (repeat of the first card). Errors, BOF, EOF, AbsolutePosition and ID are read after each assignment.",
            p =>
            {
                p.OpenRs("rs", "db", "T", Dao.OpenDynaset).Call("rs", "Move", A.I4(2)).Get("rs", "Bookmark");
                BookmarkTry(p, "rs", 2, 2);
                p.Call("rs", "Requery").Call("rs", "MoveLast");
                BookmarkTry(p, "rs", 2, 2);
                p.Call("rs", "Requery").Call("rs", "Move", A.I4(2));
                BookmarkTry(p, "rs", 2, 2);
                p.Call("rs", "Requery");
                BookmarkTry(p, "rs", 2, 2);
                Close(p, "rs");
            });

        yield return Fix("id-bookmark-number-map", [Cur, "CUR-ID"],
            "CUR-ID (F: mapping of the dynaset bookmark number beyond row 255), variant: on a dynaset over Big the Bookmark and ID are read at rows 1, 101, 201, ... 2001 (Move 100 each step) and at the last row, to locate where the bookmark number stops being ordinal + 0x200 (row 1000 gave +118, row 2048 +236 in the first card).",
            p =>
            {
                p.OpenRs("rs", "db", "Big", Dao.OpenDynaset).Get("rs", "Bookmark");
                Val(p, "rs", "ID");
                for (int i = 0; i < 20; i++)
                {
                    p.Call("rs", "Move", A.I4(100)).Get("rs", "Bookmark");
                    Val(p, "rs", "ID");
                }
                p.Call("rs", "MoveLast").Get("rs", "Bookmark");
                Val(p, "rs", "ID");
                Close(p, "rs");
            });
    }

    // F: CUR-BULK -----------------------------------------------------------------------------------------------

    private static IEnumerable<Probe> Bulk()
    {
        yield return Fix("bulk-getrows-cursor-on-deleted-row", [Cur, "CUR-BULK"],
            "CUR-BULK (F: deletion discovered before GetRows), variant: the cursor sits ON a row that another call deletes. (a) dynaset on T at ID 3: ID 3 is deleted through the Database, the ID read (deleted slot), GetRows(3), the state, GetRows(3) again; " +
            "(b) a new dynaset on Move 2 (ID 4): IDs 4 and 5 are deleted, GetRows(2), the state; (c) table-type on T at its third row: that row is deleted, GetRows(2), the state.",
            p =>
            {
                p.OpenRs("rs", "db", "T", Dao.OpenDynaset).Call("rs", "Move", A.I4(2));
                Val(p, "rs", "ID");
                Exec(p, "DELETE FROM T WHERE ID = 3");
                Val(p, "rs", "ID");
                p.Call("rs", "GetRows", A.I4(3)).Errors();
                StateNoPos(p, "rs").Call("rs", "GetRows", A.I4(3)).Errors();
                StateNoPos(p, "rs");
                Close(p, "rs");
                p.OpenRs("rs", "db", "T", Dao.OpenDynaset).Call("rs", "Move", A.I4(2));
                Val(p, "rs", "ID");
                Exec(p, "DELETE FROM T WHERE ID IN (4, 5)");
                p.Call("rs", "GetRows", A.I4(2)).Errors();
                StateNoPos(p, "rs");
                Close(p, "rs");
                p.OpenRs("rs", "db", "T", Dao.OpenTable).Call("rs", "Move", A.I4(2));
                Val(p, "rs", "ID");
                Exec(p, "DELETE FROM T WHERE ID = 6");
                p.Call("rs", "GetRows", A.I4(2)).Errors();
                StateNoPos(p, "rs");
                Close(p, "rs");
            });
    }

    // F: CUR-LIFE (and the enlisted Database) -----------------------------------------------------------------

    private static IEnumerable<Probe> Life()
    {
        foreach ((string name, string what, bool update, string? inner, string end) in new (string, string, bool, string?, string)[]
        {
            ("rollback", "Update, close the clone, Rollback (repeat of the one case of the first card)", true, null, "Rollback"),
            ("commit", "Update, close the clone, CommitTrans", true, null, "CommitTrans"),
            ("no-update", "a pending Edit left unposted, close the clone, EditMode read, Update, CommitTrans", false, null, "CommitTrans"),
            ("two-levels", "Update, a second BeginTrans, close the clone, Rollback (inner) and CommitTrans (outer)", true, "BeginTrans", "Rollback"),
        })
        {
            yield return Fix($"life-clone-before-begintrans-{name}", [Cur, "CUR-LIFE", "TXN-CLOSE"],
                $"CUR-LIFE (F: clone created before BeginTrans), variant: {what}. A dynaset on T at ID 1 makes a Clone BEFORE the transaction; BeginTrans; Edit N of ID 1 to 9001; " +
                $"{(update ? "Update; " : "")}{(inner is not null ? "BeginTrans again; " : "")}the clone is closed (Errors read); EditMode of the parent is read; the remaining transaction calls are issued and their Errors read; row ID 1 is then read from a new snapshot.",
                p =>
                {
                    p.OpenRs("rs", "db", "T", Dao.OpenDynaset).CallBind("cl", "rs", "Clone").Call("eng", "BeginTrans").Call("rs", "Edit");
                    SetVal(p, "rs", "N", A.I4(9001));
                    if (update)
                    {
                        p.Call("rs", "Update");
                    }
                    if (inner is not null)
                    {
                        p.Call("eng", inner);
                    }
                    p.Call("cl", "Close").Release("cl").Errors().Get("rs", "EditMode");
                    if (!update)
                    {
                        p.Call("rs", "Update").Errors();
                    }
                    p.Call("eng", end).Errors();
                    if (inner is not null)
                    {
                        p.Call("eng", "CommitTrans").Errors();
                    }
                    Close(p, "rs");
                    Snap(p, "SELECT ID, N FROM T WHERE ID = 1", 2);
                });
        }

        yield return Fix("life-clone-close-second-database-enlisted", [Cur, "CUR-LIFE", "TXN-CLOSE"],
            "CUR-LIFE (T: another Database enlisted, now asked with two Databases of one Workspace), variant: BeginTrans; a dynaset on T through Database 1 updates N of ID 1 (2001), a dynaset through Database 2 (the same file opened a second time) updates N of ID 2 (2002); " +
            "a Clone of each recordset is made INSIDE the transaction and closed (Errors after each); Rollback; rows 1 and 2 are read through both Databases. A second run commits instead.",
            p =>
            {
                p.CallBind("db2", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false));
                foreach (string end in new[] { "Rollback", "CommitTrans" })
                {
                    p.Call("eng", "BeginTrans").OpenRs("r1", "db", "T", Dao.OpenDynaset).OpenRs("r2", "db2", "T", Dao.OpenDynaset).Call("r2", "Move", A.I4(1)).Call("r1", "Edit");
                    SetVal(p, "r1", "N", A.I4(2001));
                    p.Call("r1", "Update").Call("r2", "Edit");
                    SetVal(p, "r2", "N", A.I4(2002));
                    p.Call("r2", "Update").CallBind("c1", "r1", "Clone").CallBind("c2", "r2", "Clone").Call("c1", "Close").Release("c1").Errors().Call("c2", "Close").Release("c2").Errors();
                    p.Call("eng", end).Errors();
                    Close(p, "r1");
                    Close(p, "r2");
                    Snap(p, "SELECT ID, N FROM T WHERE ID IN (1, 2) ORDER BY ID", 2);
                    Snap(p, "SELECT ID, N FROM T WHERE ID IN (1, 2) ORDER BY ID", 2, "db2");
                }
                p.Call("db2", "Close").Release("db2");
            });
    }

    // F and T: R-DAO-1 ------------------------------------------------------------------------------------------

    private static IEnumerable<Probe> Dao1()
    {
        yield return new Probe(Card, "create-held-databases-before-engine-create", [DaoCard, "DAO-CREATE", "DAO-COLL"],
            "DAO-CREATE (F: a Databases collection held BEFORE DBEngine.CreateDatabase), variant: Workspaces(0).Databases is retrieved first and its Count read (0); DBEngine.CreateDatabase of a new file: the held Count, Refresh, Count, member names; " +
            "the returned Database is closed and Count read again; Workspaces(0).CreateDatabase of a second file: held Count, Refresh, Count; the first card showed that a collection fetched AFTER the engine route holds the Database.",
            p =>
            {
                p.EngineLite().GetBind("wss", "eng", "Workspaces").Item("w0", "wss", A.I4(0)).GetBind("dbs", "w0", "Databases").Get("dbs", "Count");
                p.CallBind("dbe", "eng", "CreateDatabase", A.Out("qc2-eng-created.accdb"), A.Str(Dao.LangGeneral), A.I4(Dao.Version120));
                DbMembers(p);
                p.Call("dbe", "Close").Release("dbe");
                DbMembers(p);
                p.CallBind("dbw", "w0", "CreateDatabase", A.Out("qc2-ws-created.accdb"), A.Str(Dao.LangGeneral), A.I4(Dao.Version120));
                DbMembers(p);
                p.Call("dbw", "Close").Release("dbw").Release("dbs").Release("w0").Release("wss").FinishLite();
            });

        yield return Fix("meta-permissions-put-then-read", [DaoCard, "DAO-META", "DAO-SEC"],
            "DAO-META (F: Permissions read 0 after failed puts in the first card's d1e), variant: Containers('Tables').Documents('T'): Permissions read (Errors); Permissions put 0 (Errors); Permissions read; AllPermissions read; Permissions put 1048575 (Errors); Permissions and AllPermissions read; then the same on the Container itself.",
            p =>
            {
                p.GetBind("cs", "db", "Containers").Item("ct", "cs", A.Str("Tables")).GetBind("ds", "ct", "Documents").Item("dt", "ds", A.Str("T"));
                foreach (string target in new[] { "dt", "ct" })
                {
                    p.Get(target, "Permissions").Errors();
                    p.Put(target, "Permissions", A.I4(0)).Errors();
                    p.Get(target, "Permissions").Errors().Get(target, "AllPermissions").Errors();
                    p.Put(target, "Permissions", A.I4(1048575)).Errors();
                    p.Get(target, "Permissions").Errors().Get(target, "AllPermissions").Errors();
                }
            });

        yield return Fix("index-null-sorts-after-duplicate", [DaoCard, "II-05"],
            "II-05 (F: is the Null refusal a separate pass?), variant: IbA holds (1,1),(1,1),(9,Null) and IbB the same rows with the Null row inserted first. In key order (A, B) the Null row sorts AFTER the duplicates. CREATE UNIQUE INDEX over (A,B) WITH DISALLOW NULL on each: Errors (3022 or 3058) and the Indexes count after Refresh; " +
            "then CREATE UNIQUE INDEX without DISALLOW NULL and CREATE INDEX WITH DISALLOW NULL on IbA as single-defect controls.",
            p =>
            {
                p.GetBind("tds", "db", "TableDefs").Call("tds", "Refresh");
                foreach ((string t, string ix, string clause) in new[]
                {
                    ("IbA", "UxA", "UNIQUE INDEX UxA ON IbA (A, B) WITH DISALLOW NULL"), ("IbB", "UxB", "UNIQUE INDEX UxB ON IbB (A, B) WITH DISALLOW NULL"),
                    ("IbA", "UxC", "UNIQUE INDEX UxC ON IbA (A, B)"), ("IbA", "UxD", "INDEX UxD ON IbA (A, B) WITH DISALLOW NULL"),
                })
                {
                    Marker(p);
                    Sql(p, "CREATE " + clause);
                    p.Errors().Item("t", "tds", A.Str(t)).GetBind("ixs", "t", "Indexes").Call("ixs", "Refresh").Get("ixs", "Count").Release("ixs").Release("t");
                }
            });

        yield return new Probe(Card, "boot-alias-path-open-twice", [DaoCard, "DAO-BOOT"],
            "DAO-BOOT (T: alias paths, addressable with the existing path spellings), variant: the fixture is opened shared read-write by its plain path as Database 1, then again by each alias spelling (upper-case, extended \\\\?\\ form, forward slashes, dot segments) in the same Workspace: Name of each, Databases.Count and Item(0) after all of them, " +
            "and COUNT(*) of T through each; then in a second appended Workspace by the upper-case spelling.",
            p =>
            {
                p.EngineLite().CallBind("d0", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false)).Get("d0", "Name");
                p.GetBind("wss", "eng", "Workspaces").Item("w0", "wss", A.I4(0)).GetBind("dbs", "w0", "Databases");
                foreach (string s in new[] { "upper", "extended", "slashes", "dot" })
                {
                    string bind = "d_" + s;
                    p.CallBind(bind, "eng", "OpenDatabase", A.InAs(s), A.Bool(false), A.Bool(false)).Errors().Get(bind, "Name").Get("dbs", "Count");
                    p.OpenRs("rc", bind, "SELECT COUNT(*) FROM T", Dao.OpenSnapshot).ReadRow("rc").Call("rc", "Close").Release("rc");
                }
                p.Item("first", "dbs", A.I4(0)).Get("first", "Name").Release("first");
                p.Call("dbs", "Refresh").Get("dbs", "Count");
                p.CallBind("w2", "eng", "CreateWorkspace", A.Str("W2"), A.Str("admin"), A.Str(""), A.I4(UseJet)).Call("wss", "Append", A.Obj("w2"))
                    .CallBind("d2", "w2", "OpenDatabase", A.InAs("upper"), A.Bool(false), A.Bool(false)).Errors().Get("d2", "Name");
                p.Call("d2", "Close").Release("d2").Call("w2", "Close").Release("w2");
                foreach (string s in new[] { "upper", "extended", "slashes", "dot" })
                {
                    p.Call("d_" + s, "Close");
                }
                p.Call("d0", "Close").FinishLite();
            })
        { Fixtures = [("main", Fx)] };

        foreach ((string tag, int delay) in new[] { ("early", 5), ("late", 40) })
        {
            yield return new Probe(Card, $"create-suspend-mid-call-{tag}", [DaoCard, "DAO-CREATE", "R-CRASH-1", "CRASH-CONTROL"],
                $"DAO-CREATE (T: partial outcomes at the creation boundaries, addressable by freezing), variant: participant a calls DBEngine.CreateDatabase for a new ACCDB; participant b freezes a {delay} ms after arming, records the destination file and lock file, tries a DAO shared read-only OpenDatabase of the destination (Errors), " +
                "waits, records the files again, resumes a, then after a has closed the new database (a closes it before signalling) records the files and opens it again, reading TableDefs.Count. Asks what a second process sees of the destination while the creator is frozen inside the call. The delay is a timing probe: the first card measured no creation time.",
                a =>
                {
                    a.EngineLite().Signal("ready").Await("armed", Wait)
                        .CallBind("dbc", "eng", "CreateDatabase", A.Out("qc2-suspended.accdb"), A.Str(Dao.LangGeneral), A.I4(Dao.Version120)).Errors()
                        .Call("dbc", "Close").Release("dbc").Signal("created").Await("b-done", 60000).Files("a-end");
                })
            {
                Participants =
                [
                    ("b", b =>
                    {
                        b.EngineLite().Await("ready", Wait).Signal("armed").SuspendParticipant("a", delay).Files("while-suspended")
                            .CallBind("dbb", "eng", "OpenDatabase", A.Out("qc2-suspended.accdb"), A.Bool(false), A.Bool(true)).Errors();
                        b.Call("dbb", "Close").Release("dbb").Pause(400).Files("still-suspended").ResumeParticipant("a").Await("created", 60000).Files("after-resume")
                            .CallBind("dbb2", "eng", "OpenDatabase", A.Out("qc2-suspended.accdb"), A.Bool(false), A.Bool(true)).GetBind("tds", "dbb2", "TableDefs").Get("tds", "Count")
                            .Release("tds").Call("dbb2", "Close").Release("dbb2").Signal("b-done");
                    }),
                ],
            };
        }
    }

    // T: DAO-ENUM, ERR-SNAPSHOT ------------------------------------------------------------------------------

    private static IEnumerable<Probe> Enums()
    {
        yield return Fix("enum-dao-top-level-collections", [DaoCard, "DAO-ENUM", "DAO-COLL"],
            "DAO-ENUM (T: IEnumVARIANT), variant: _NewEnum of the top-level collections, each walk followed by the worker's Next past the end, Reset, Next, Skip(1) and Clone: DBEngine.Workspaces, Workspace.Databases, Users, Groups, Database.TableDefs (after Refresh), QueryDefs, Relations, Containers, Database.Properties and DBEngine.Errors after a marker failure; each item's Name (Number for Errors) is read.",
            p =>
            {
                p.GetBind("wss", "eng", "Workspaces").Enumerate("wss", "w", 8, b => b.Get("w", "Name")).Item("w0", "wss", A.I4(0));
                p.GetBind("dbs", "w0", "Databases").Enumerate("dbs", "d", 8, b => b.Get("d", "Name")).Release("dbs");
                p.GetBind("us", "w0", "Users").Enumerate("us", "u", 8, b => b.Get("u", "Name")).Release("us");
                p.GetBind("gs", "w0", "Groups").Enumerate("gs", "g", 8, b => b.Get("g", "Name")).Release("gs");
                p.GetBind("tds", "db", "TableDefs").Call("tds", "Refresh").Enumerate("tds", "t", 32, b => b.Get("t", "Name")).Release("tds");
                p.GetBind("qds", "db", "QueryDefs").Enumerate("qds", "q", 8, b => b.Get("q", "Name")).Release("qds");
                p.GetBind("rls", "db", "Relations").Enumerate("rls", "r", 8, b => b.Get("r", "Name")).Release("rls");
                p.GetBind("cs", "db", "Containers").Enumerate("cs", "c", 8, b => b.Get("c", "Name")).Release("cs");
                p.GetBind("dps", "db", "Properties").Enumerate("dps", "pp", 40, b => b.Get("pp", "Name")).Release("dps");
                Marker(p);
                p.GetBind("errs", "eng", "Errors").Enumerate("errs", "e", 8, b => b.Get("e", "Number")).Release("errs");
                p.Release("w0").Release("wss");
            });

        yield return Fix("enum-dao-child-collections", [DaoCard, "DAO-ENUM", "DAO-COLL"],
            "DAO-ENUM (T: IEnumVARIANT), variant: _NewEnum of child collections: Fields and Indexes of T, the IndexFields of its primary key, the Fields of a Recordset on T, Relation RelPC's Fields (Name and ForeignName), the Documents of Container 'Tables', and the Parameters of QueryDef QP (Name and Type); each walk ends with the worker's Next past the end, Reset, Next, Skip(1) and Clone.",
            p =>
            {
                p.GetBind("tds", "db", "TableDefs").Item("t", "tds", A.Str("T")).GetBind("fs", "t", "Fields").Enumerate("fs", "f", 8, b => b.Get("f", "Name").Get("f", "Type")).Release("fs");
                p.GetBind("ixs", "t", "Indexes").Enumerate("ixs", "i", 8, b => b.Get("i", "Name")).Item("pk", "ixs", A.Str("PK_T")).GetBind("ifs", "pk", "Fields").Enumerate("ifs", "ifd", 8, b => b.Get("ifd", "Name"));
                p.Release("ifs").Release("pk").Release("ixs").Release("t").Release("tds");
                p.OpenRs("rs", "db", "T", Dao.OpenDynaset).GetBind("rfs", "rs", "Fields").Enumerate("rfs", "rf", 8, b => b.Get("rf", "Name")).Release("rfs");
                Close(p, "rs");
                p.GetBind("rls", "db", "Relations").Item("rl", "rls", A.Str("RelPC")).GetBind("rlf", "rl", "Fields").Enumerate("rlf", "rff", 8, b => b.Get("rff", "Name").Get("rff", "ForeignName"));
                p.Release("rlf").Release("rl").Release("rls");
                p.GetBind("cs", "db", "Containers").Item("ct", "cs", A.Str("Tables")).GetBind("ds", "ct", "Documents").Enumerate("ds", "dd", 40, b => b.Get("dd", "Name"));
                p.Release("ds").Release("ct").Release("cs");
                p.GetBind("qds", "db", "QueryDefs").Item("qd", "qds", A.Str("QP")).GetBind("ps", "qd", "Parameters").Enumerate("ps", "pr", 8, b => b.Get("pr", "Name").Get("pr", "Type"));
                p.Release("ps").Release("qd").Release("qds");
            });

        yield return Fix("enum-mutation-add-during-walk", [DaoCard, "DAO-ENUM"],
            "DAO-ENUM (T), variant: a member added while an enumerator is live. TableDefs is refreshed and enumerated; in the body of every Next the item Name and the collection Count are read and CREATE TABLE EnumAdd (A LONG) is run (it succeeds on the first item and fails 3010 afterwards); " +
            "the walk's own Next past the end, Reset, Next, Skip and Clone then show whether the new table is seen, whether the enumerator is invalidated, and whether Count follows.",
            p =>
            {
                p.GetBind("tds", "db", "TableDefs").Call("tds", "Refresh").Enumerate("tds", "t", 40, b => b.Get("t", "Name").Get("tds", "Count").Call("db", "Execute", A.Str("CREATE TABLE EnumAdd (A LONG)")));
                p.Call("tds", "Refresh").Get("tds", "Count").Release("tds");
            });

        yield return Fix("enum-mutation-remove-during-walk", [DaoCard, "DAO-ENUM"],
            "DAO-ENUM (T), variant: a member removed while an enumerator is live. Tables ZDn1, ZDn2 and ZDn3 are created; TableDefs is refreshed and enumerated; in the body of every Next the item Name is read and DROP TABLE ZDn3 is run (it succeeds on the first item and fails 3376 afterwards) so the dropped table is still ahead of the cursor; " +
            "the later Next, Reset, Next, Skip and Clone answers and the final Count show how a stale enumerator treats the removed member.",
            p =>
            {
                foreach (string t in new[] { "ZDn1", "ZDn2", "ZDn3" })
                {
                    Sql(p, $"CREATE TABLE {t} (A LONG)");
                }
                p.GetBind("tds", "db", "TableDefs").Call("tds", "Refresh").Enumerate("tds", "t", 40, b => b.Get("t", "Name").Call("db", "Execute", A.Str("DROP TABLE ZDn3")));
                p.Call("tds", "Refresh").Get("tds", "Count").Release("tds");
            });

        yield return Fix("enum-refresh-during-walk", [DaoCard, "DAO-ENUM"],
            "DAO-ENUM (T), variant: Refresh while an enumerator is live. A second Database of the same file creates table Rf1 (the held TableDefs does not see it); TableDefs is enumerated and in the body of every Next the item Name is read, TableDefs.Refresh is called and Count read; " +
            "the walk's later Next, Reset, Next, Skip and Clone show whether Refresh invalidates or extends the enumerator.",
            p =>
            {
                p.CallBind("db2", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false));
                p.GetBind("tds", "db", "TableDefs").Call("tds", "Refresh");
                p.Call("db2", "Execute", A.Str("CREATE TABLE Rf1 (A LONG)"));
                p.Enumerate("tds", "t", 40, b => b.Get("t", "Name").Call("tds", "Refresh").Get("tds", "Count"));
                p.Get("tds", "Count").Release("tds").Call("db2", "Close").Release("db2");
            });

        yield return Fix("enum-close-during-walk", [DaoCard, "DAO-ENUM", "DAO-LIFE"],
            "DAO-ENUM and DAO-LIFE (T), variant: the parent Database is closed while an enumerator is live. TableDefs is enumerated; in the body of the first Next the item Name is read and Database.Close is called; later Next, Reset, Next, Skip and Clone answers and the held item are read after the close.",
            p =>
            {
                p.GetBind("tds", "db", "TableDefs").Call("tds", "Refresh").Enumerate("tds", "t", 40, b => b.Get("t", "Name").Call("db", "Close"));
                p.Get("tds", "Count").Release("tds");
            });

        yield return Fix("enum-errors-partial-consume", [ErrCard, "ERR-SNAPSHOT"],
            "ERR-SNAPSHOT (T: a partly consumed IEnumVARIANT), variant: a two-entry Errors collection is produced (TableDefs.Item(Database): entries 3001 and 3421); DBEngine.Errors is enumerated and in the body of every Next the item Number is read and a NEW failure (DROP TABLE of a missing table) is provoked, " +
            "so the collection is replaced while the enumerator is half consumed; the walk's own Next past the end, Reset, Next, Skip(1) and Clone show what the enumerator and the already returned items hold; afterwards DBEngine.Errors is read fresh.",
            p =>
            {
                p.GetBind("tds", "db", "TableDefs");
                Marker(p).Item("x", "tds", A.Obj("db")).Errors();
                p.GetBind("errs", "eng", "Errors").Enumerate("errs", "e", 8, b => b.Get("e", "Number").Get("e", "Description").Call("db", "Execute", A.Str("DROP TABLE Q2NoSuchMarker")));
                p.Get("errs", "Count").Release("errs").Errors();
            });
    }

    // T: ERR-BIND by-reference and named arguments ------------------------------------------------------------

    private static IEnumerable<Probe> Args()
    {
        yield return Fix("byref-dao-openrecordset", [ErrCard, "ERR-BIND", DaoCard, "DAO-ID"],
            "ERR-BIND (T: ByRef aliases), variant: DAO arguments passed by reference to an input-only parameter. Database.OpenRecordset('T', ByRef Variant holding I4 4); ('T', typed ByRef I4 4); (typed ByRef BSTR 'T', I4 4); ('T', ByRef Variant Empty); ('T', ByRef Variant Null); ('T', typed ByRef I4 4, typed ByRef I4 0); " +
            "each recordset's Type and RecordCount are read and the worker records the value of every by-reference argument after the call (a DAO member must not write into an input-only parameter).",
            p =>
            {
                Arg[][] forms =
                [
                    [A.Str("T"), A.ByRef(A.I4(4))], [A.Str("T"), A.ByRef(A.I4(4), typed: true)], [A.ByRef(A.Str("T"), typed: true), A.I4(4)],
                    [A.Str("T"), A.ByRef(A.Empty)], [A.Str("T"), A.ByRef(A.Null)], [A.Str("T"), A.ByRef(A.I4(4), typed: true), A.ByRef(A.I4(0), typed: true)],
                ];
                foreach (Arg[] f in forms)
                {
                    Marker(p);
                    p.CallBind("rs", "db", "OpenRecordset", f).Errors().Get("rs", "Type").Get("rs", "RecordCount");
                    Close(p, "rs");
                }
            });

        yield return Fix("byref-dao-move-seek", [ErrCard, "ERR-BIND", DaoCard],
            "ERR-BIND (T: ByRef aliases), variant: by-reference arguments to cursor members. Dynaset on T: Move(ByRef Variant 2), GetRows(typed ByRef I4 2), FindFirst(typed ByRef BSTR 'ID = 5'), Database.Execute(typed ByRef BSTR 'UPDATE T SET N = N', ByRef Variant 128); " +
            "table-type with Index PK_T: Seek(typed ByRef BSTR '=', ByRef Variant I4 3). The state (ID, NoMatch) and every by-reference value after the call are read.",
            p =>
            {
                p.OpenRs("rs", "db", "T", Dao.OpenDynaset);
                p.Call("rs", "Move", A.ByRef(A.I4(2)));
                Val(p, "rs", "ID");
                p.Call("rs", "GetRows", A.ByRef(A.I4(2), typed: true)).Errors();
                StateNoPos(p, "rs");
                p.Call("rs", "FindFirst", A.ByRef(A.Str("ID = 5"), typed: true)).Errors().Get("rs", "NoMatch");
                Val(p, "rs", "ID");
                Close(p, "rs");
                Marker(p).Call("db", "Execute", A.ByRef(A.Str("UPDATE T SET N = N"), typed: true), A.ByRef(A.I4(128))).Errors().Get("db", "RecordsAffected");
                p.OpenRs("rt", "db", "T", Dao.OpenTable).Put("rt", "Index", A.Str("PK_T"));
                p.Call("rt", "Seek", A.ByRef(A.Str("="), typed: true), A.ByRef(A.I4(3))).Errors().Get("rt", "NoMatch");
                Val(p, "rt", "ID");
                Close(p, "rt");
            });

        yield return Fix("named-dao-order-variants", [ErrCard, "ERR-BIND", DaoCard],
            "ERR-BIND (T: named arguments), variant: named-argument binding order and the omitted Type cell. Database.OpenRecordset: ('T', Options:=4) (Type omitted: does option 4 make it read-only, as the Missing cell did not); ('T', Type:=1, Options:=4); ('T', Missing, Options:=4); (Type:=2, Name:='T') in reverse order; " +
            "('T', 2, Type:=4) (positional and named for the same parameter); (Type:=2, LockEdit:=3, Name:='T'); each recordset's Type and Updatable are read and Errors after every call; Database.Execute with a failing statement and Options:=128 named; Recordset.Move(Rows:=2).",
            p =>
            {
                (Arg[] pos, (string, Arg)[] named)[] calls =
                [
                    ([A.Str("T")], [("Options", A.I4(4))]), ([A.Str("T")], [("Type", A.I4(1)), ("Options", A.I4(4))]), ([A.Str("T"), A.Missing], [("Options", A.I4(4))]),
                    ([], [("Type", A.I4(2)), ("Name", A.Str("T"))]), ([A.Str("T"), A.I4(2)], [("Type", A.I4(4))]), ([], [("Type", A.I4(2)), ("LockEdit", A.I4(3)), ("Name", A.Str("T"))]),
                ];
                foreach ((Arg[] pos, (string, Arg)[] named) in calls)
                {
                    Marker(p);
                    p.CallNamed("rs", "db", "OpenRecordset", pos, named).Errors().Get("rs", "Type").Get("rs", "Updatable");
                    Close(p, "rs");
                }
                Marker(p).CallNamed(null, "db", "Execute", [A.Str("DELETE FROM NoSuchTable")], ("Options", A.I4(128))).Errors();
                p.OpenRs("rs", "db", "T", Dao.OpenDynaset).CallNamed(null, "rs", "Move", [], ("Rows", A.I4(2)));
                Val(p, "rs", "ID");
                Close(p, "rs");
            });

        yield return Fix("named-dao-refusals", [ErrCard, "ERR-BIND", DaoCard],
            "ERR-BIND (T: named arguments), variant: refusals of named binding. OpenRecordset('T', Type:='abc'); ('T', Type:=Null); (Name:=Null); ('T', type:=4) (lower-case name); ('T', Type:=4, TYPE:=2) (same name twice with different case); a name that belongs to another member (Database.Execute named Name:='x'); " +
            "a named argument on a parameterless member (Recordset.MoveNext named Rows:=1) and on a property get (Database.TableDefs named Index:=0); Errors, and the IErrorInfo and EXCEPINFO the record captures, after each.",
            p =>
            {
                (Arg[] pos, (string, Arg)[] named)[] calls =
                [
                    ([A.Str("T")], [("Type", A.Str("abc"))]), ([A.Str("T")], [("Type", A.Null)]), ([], [("Name", A.Null)]), ([A.Str("T")], [("type", A.I4(4))]),
                    ([A.Str("T")], [("Type", A.I4(4)), ("TYPE", A.I4(2))]),
                ];
                foreach ((Arg[] pos, (string, Arg)[] named) in calls)
                {
                    Marker(p);
                    p.CallNamed("rs", "db", "OpenRecordset", pos, named).Errors();
                    Close(p, "rs");
                }
                Marker(p).CallNamed(null, "db", "Execute", [A.Str("DELETE FROM NoSuchTable")], ("Name", A.Str("x"))).Errors();
                p.OpenRs("rs", "db", "T", Dao.OpenDynaset);
                Marker(p).CallNamed(null, "rs", "MoveNext", [], ("Rows", A.I4(1))).Errors();
                Marker(p).CallNamed("tdx", "db", "TableDefs", [], ("Index", A.I4(0))).Errors();
                Close(p, "rs");
            });
    }

    // T: ERR-BASE activation, ERR-ADO and ERR-NEST events -----------------------------------------------------

    private static IEnumerable<Probe> Err1()
    {
        foreach (bool flag in new[] { true, false })
        {
            string name = flag ? "failonerror" : "plain";
            yield return Fix($"errpart-lock-multipage-{name}", [ErrCard, "ERR-PARTIAL"],
                $"ERR-PARTIAL (F: lock granularity on a table spanning many pages), variant: {(flag ? "with" : "without")} dbFailOnError. A second Workspace W2 opens the file; a dynaset on Big (2048 rows, about 100 pages) with LockEdits True runs FindFirst ID = k and Edit for k = 1, 1000 and 2048; " +
                $"for each k the first Database runs UPDATE Big SET V = 'u' {(flag ? "with dbFailOnError" : "with no options")}; Errors, RecordsAffected, COUNT of rows with V = 'u' and their MIN and MAX ID are read; W2 cancels and closes; the rows are reset (UPDATE ... WHERE V = 'u'). " +
                "Decides whether rows on pages other than the locked row's page are updated or refused.",
                p =>
                {
                    p.GetBind("wss", "eng", "Workspaces").CallBind("w2", "eng", "CreateWorkspace", A.Str("W2"), A.Str("admin"), A.Str(""), A.I4(UseJet))
                        .Call("wss", "Append", A.Obj("w2")).CallBind("db2", "w2", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false));
                    foreach (int k in new[] { 1, 1000, 2048 })
                    {
                        p.OpenRs("r2", "db2", "Big", Dao.OpenDynaset).Put("r2", "LockEdits", A.Bool(true)).Call("r2", "FindFirst", A.Str($"ID = {k}")).Call("r2", "Edit").Errors();
                        Marker(p);
                        if (flag)
                        {
                            Exec(p, "UPDATE Big SET V = 'u'");
                        }
                        else
                        {
                            Sql(p, "UPDATE Big SET V = 'u'");
                        }
                        p.Errors().Get("db", "RecordsAffected");
                        Snap(p, "SELECT COUNT(*), MIN(ID), MAX(ID) FROM Big WHERE V = 'u'", 3);
                        p.Call("r2", "CancelUpdate");
                        Close(p, "r2");
                        Exec(p, $"UPDATE Big SET V = '{V100}' WHERE V = 'u'");
                    }
                    p.Call("db2", "Close").Release("db2").Call("w2", "Close").Release("w2").Release("wss");
                });
        }

        yield return new Probe(Card, "errbase-activation-failure-then-member", [ErrCard, "ERR-BASE"],
            "ERR-BASE (T: failure during construction before association), variant: activation of DAO.DBEngine.120 fails twice (CLSCTX_LOCAL_SERVER; in-process aggregation with a controlled outer) and is then made by CoCreateInstance; DBEngine.Errors Count is read before any member call; " +
            "then the first member failure (OpenDatabase of a missing file, non-empty name) and Errors; separates the activation failure from the first member failure.",
            p =>
            {
                p.Files("before-open");
                p.Activate("f1", "DAO.DBEngine.120", "cocreate", 4, IidDispatch);
                p.Activate("f2", "DAO.DBEngine.120", "cocreate", 1, IidDispatch, aggregate: true);
                p.Activate("eng", "DAO.DBEngine.120", "cocreate", 1, IidDispatch).Modules("after-create");
                p.Errors();
                p.Call("eng", "OpenDatabase", A.Out("qc2-missing.accdb"), A.Bool(false), A.Bool(true)).Errors();
                p.Files("after-close");
            });

        yield return new Probe(Card, "errado-warning-infomessage", [ErrCard, "ERR-ADO", "R-ADO-1"],
            $"ERR-ADO (T: a provider warning cannot be induced), variant: a recording sink on ConnectionEvents (InfoMessage) of an ADODB.Connection (Provider {AceOleDb}, read-write, the fixture). Recordsets are opened with requests ACE may downgrade: CursorType 2 (dynamic) with LockType 3 (optimistic); " +
            "CursorType 3 (static) with LockType 4 (batch optimistic); forward-only with LockType 2 (pessimistic); and Execute of a SELECT; the cursor and lock type read back, Connection.Errors read, and the events the sink records (InfoMessage or not) are compared.",
            p =>
            {
                p.EngineLite().Create("ADODB.Connection", "cn").Advise("cn", AdoConnEvents, "ev");
                AdoOpenKeepSink(p, "cn");
                foreach ((int cursor, int lockType) in new[] { (2, 3), (3, 4), (0, 2) })
                {
                    p.Create("ADODB.Recordset", "ars").Put("ars", "CursorType", A.I4(cursor)).Put("ars", "LockType", A.I4(lockType))
                        .Call("ars", "Open", A.Str("SELECT * FROM T"), A.Obj("cn")).Get("ars", "CursorType").Get("ars", "LockType");
                    AdoErrs(p, "cn");
                    p.Call("ars", "Close").Release("ars");
                }
                p.CallBind("ars2", "cn", "Execute", A.Str("SELECT * FROM T")).Call("ars2", "Close").Release("ars2");
                AdoErrs(p, "cn");
                p.Call("cn", "Close").Unadvise("ev").Release("cn").FinishLite();
            })
        { Fixtures = [("main", Fx)] };

        yield return new Probe(Card, "errnest-ado-connection-event-error", [ErrCard, "ERR-NEST", "ERR-ADO", "R-ADO-1"],
            $"ERR-NEST (T: ADO completion event with an error), variant: a recording sink on ConnectionEvents of an ADODB.Connection (Provider {AceOleDb}, read-write): Open; Execute 'SELEC ID FROM T' (provider failure); Connection.Errors read; a successful Execute; Errors read; " +
            "BeginTrans, a failing Execute, RollbackTrans; Execute 'SELECT * FROM NoSuchTable'; Errors read. Asks what ExecuteComplete and WillExecute carry as pError and adStatus for each failure and in which order the events and the Errors collection change.",
            p =>
            {
                p.EngineLite().Create("ADODB.Connection", "cn").Advise("cn", AdoConnEvents, "ev");
                AdoOpenKeepSink(p, "cn");
                p.Call("cn", "Execute", A.Str("SELEC ID FROM T"));
                AdoErrs(p, "cn");
                p.CallBind("a1", "cn", "Execute", A.Str("SELECT ID FROM T")).Call("a1", "Close").Release("a1");
                AdoErrs(p, "cn");
                p.Call("cn", "BeginTrans").Call("cn", "Execute", A.Str("SELEC ID FROM T")).Call("cn", "RollbackTrans");
                AdoErrs(p, "cn");
                p.Call("cn", "Execute", A.Str("SELECT * FROM NoSuchTable"));
                AdoErrs(p, "cn");
                p.Call("cn", "Close").Unadvise("ev").Release("cn").FinishLite();
            })
        { Fixtures = [("main", Fx)] };

        yield return new Probe(Card, "errnest-ado-recordset-event-error", [ErrCard, "ERR-NEST", "ERR-ADO", "R-ADO-1"],
            $"ERR-NEST (T: recordset events with an error), variant: a recording sink on RecordsetEvents of an ADODB.Recordset (keyset, optimistic) opened on T through an ADODB.Connection (Provider {AceOleDb}, read-write): AddNew, set ID 1 (a duplicate primary key), Update (provider failure), Connection.Errors read, CancelUpdate; " +
            "MoveNext twice past the end. Asks which RecordsetEvents (WillChangeField, WillChangeRecord, RecordChangeComplete, FieldChangeComplete, WillMove, MoveComplete, EndOfRecordset) fire with which pError and adStatus.",
            p =>
            {
                p.EngineLite().Create("ADODB.Connection", "cn");
                AdoOpenKeepSink(p, "cn");
                p.Create("ADODB.Recordset", "ars").Advise("ars", AdoRsEvents, "rev").Put("ars", "CursorType", A.I4(1)).Put("ars", "LockType", A.I4(3))
                    .Call("ars", "Open", A.Str("SELECT ID, N, S FROM T ORDER BY ID"), A.Obj("cn"));
                p.Call("ars", "AddNew").GetBind("arf", "ars", "Fields").Item("aid", "arf", A.Str("ID")).Put("aid", "Value", A.I4(1)).Release("aid").Release("arf");
                p.Call("ars", "Update");
                AdoErrs(p, "cn");
                p.Call("ars", "CancelUpdate").Call("ars", "MoveLast").Call("ars", "MoveNext").Call("ars", "MoveNext");
                AdoErrs(p, "cn");
                p.Call("ars", "Close").Unadvise("rev").Release("ars").Call("cn", "Close").Release("cn").FinishLite();
            })
        { Fixtures = [("main", Fx)] };
    }

    // Helpers -------------------------------------------------------------------------------------------------

    private static Probe Fix(string name, string[] refs, string description, Action<P> body) =>
        new(Card, name, refs, description,
            p =>
            {
                p.EngineLite().OpenDb(readOnly: false);
                Marker(p).Errors();
                body(p);
                p.FinishLite("db");
            })
        { Fixtures = [("main", Fx)] };

    private static P Marker(P p) => p.Call("db", "Execute", A.Str("DROP TABLE Q2NoSuchMarker"));

    private static P Sql(P p, string sql) => p.Call("db", "Execute", A.Str(sql));

    private static P Exec(P p, string sql, string db = "db") => p.Call(db, "Execute", A.Str(sql), A.I4(Fail));

    private static P Close(P p, string rs) => p.Call(rs, "Close").Release(rs);

    private static P Val(P p, string rs, string field) =>
        p.GetBind("sf", rs, "Fields").Item("sv", "sf", A.Str(field)).Get("sv", "Value").Release("sv").Release("sf");

    private static P SetVal(P p, string rs, string field, Arg value) =>
        p.GetBind("pf", rs, "Fields").Item("pv", "pf", A.Str(field)).Put("pv", "Value", value).Release("pv").Release("pf");

    private static P StateNoPos(P p, string rs) => Val(p.Get(rs, "BOF").Get(rs, "EOF"), rs, "ID");

    private static P Snap(P p, string sql, int fields, string db = "db") =>
        p.OpenRs("sn", db, sql, Dao.OpenSnapshot).ReadAll("sn", 64, fields).Call("sn", "Close").Release("sn");

    private static P BookmarkTry(P p, string rs, byte row, byte kind) =>
        StateNoPos(p.Put(rs, "Bookmark", A.Bytes(row, kind, 0, 0)).Errors().Get(rs, "AbsolutePosition"), rs);

    private static P DbMembers(P p) =>
        p.Get("dbs", "Count").Call("dbs", "Refresh").Get("dbs", "Count").Each("dbs", "dbm", 8, b => b.Get("dbm", "Name"));

    /// <summary>Provider and Data Source on an existing connection object (the sink, if any, is already connected), then Open.</summary>
    private static P AdoOpenKeepSink(P p, string conn)
    {
        if (conn == "cn")
        {
            // The Connection is created by the caller so that a sink can be advised before Provider is set.
            return p.Put(conn, "Provider", A.Str(AceOleDb))
                .GetBind("cp" + conn, conn, "Properties").Item("cds" + conn, "cp" + conn, A.Str("Data Source")).Put("cds" + conn, "Value", A.In())
                .Release("cds" + conn).Release("cp" + conn).Call(conn, "Open");
        }
        return p;
    }

    private static P AdoErrs(P p, string conn) =>
        p.GetBind("ae" + conn, conn, "Errors").Get("ae" + conn, "Count")
            .Each("ae" + conn, "aer" + conn, 8, b => b.Get("aer" + conn, "Number").Get("aer" + conn, "Description").Get("aer" + conn, "SQLState"))
            .Release("ae" + conn);
}
