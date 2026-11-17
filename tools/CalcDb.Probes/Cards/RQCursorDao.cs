namespace CalcDb.Probes.Cards;

/// <summary>
/// Follow-up probes of the qualification pass over R-CURSOR-1, R-DAO-1 and R-ERR-1 (The Plan 8.1): each asks the one deciding
/// variant that the retained records of r-cursor-1, r-cursor-1b, r-dao-1-err-1, r-dao-1b, r-err-1b, r-def-cursor-dao and
/// r-fix-cursor-dao do not yet settle (docs/work/2026-11-17 status table). Every probe opens a fresh copy of one fixture
/// (r-q-cursor-dao-main) read-write, provokes a known error first (DROP TABLE QcdNoSuchMarker, 3376) so that each later
/// DBEngine.Errors read belongs to its own step, refreshes collections after DDL before reading them, and uses dbFailOnError (128)
/// where a refusal must be captured. Probes ask; they hold no expected answers. Arguments are constants; every file is named
/// through A.In or A.Out; no call can open a dialog (no ODBC, no DSN, no OpenDatabase/CreateDatabase with an empty name, no
/// Requery argument, no Array()). Scratch outputs do not carry the card prefix.
/// </summary>
public sealed class RQCursorDao : ICard
{
    private const string Card = "r-q-cursor-dao";
    private const string Cur = "R-CURSOR-1", DaoCard = "R-DAO-1", ErrCard = "R-ERR-1";
    private const string Fx = Card + "-main";
    private const int UseJet = 2, Fail = Dao.FailOnError, Wait = 30000;
    private const string AceOleDb = "Microsoft.ACE.OLEDB.12.0";

    private static readonly string[] TextBig = [new string('v', 100)];

    public IEnumerable<Probe> Probes() =>
        [CreateMain(), .. Pop(), .. Ids(), .. Seeks(), .. Edits(), .. Prov(), .. Bulk(), .. Life(), .. Dao1(), .. Err1()];

    // Fixture ------------------------------------------------------------------------------------------------

    private static Probe CreateMain() =>
        new(Card, "create-main", [Cur, DaoCard, ErrCard, "CUR-POP", "CUR-SEEK", "CUR-EDIT", "CUR-PROV", "CUR-BULK", "CUR-LIFE"],
            $"Fixture {Fx}: T(ID PK, N, S, M) 8 rows (N 10,20,30,40,40,50,60,70; S s1-s8); Tk copy of T(ID,N); H, H2, H3 heap tables (A,B) with rows (1,x),(1,x),(2,y),(3,Null); " +
            "Big(ID PK, V TEXT(120)) 2048 rows; Ix(ID PK, A, B, C) 13 rows with index IX_ABC (A asc, B desc, C asc), IDs 1-12 the grid A 1-2, B 1-3, C 1-2 in ascending order and ID 13 = (3,1,1); " +
            "Lk(ID PK, K TEXT(255)) with IX_LK; Gn(ID COUNTER PK, V); PA(ID PK) 1-5 and CH(CID PK, PID) with relation RelPC (PA.ID to CH.PID); Rm(ID PK, Mm MEMO NOT NULL); " +
            "DcT(ID PK, Q) with IX_DQ; Ob(ID PK, O LONGBINARY, Mm MEMO); Ib1 and Ib2 heap tables (A,B) with the same three rows in two insertion orders; saved query QP (PARAMETERS pA Long, pB Text). " +
            "Created in ACCDB by DBEngine.CreateDatabase(path, dbLangGeneral, dbVersion120); the closed file is kept.",
            p =>
            {
                p.EngineLite().CallBind("db", "eng", "CreateDatabase", A.Out(Fx + ".accdb"), A.Str(Dao.LangGeneral), A.I4(Dao.Version120));
                Sql(p, "CREATE TABLE T (ID LONG CONSTRAINT PK_T PRIMARY KEY, N LONG, S TEXT(20), M MEMO)");
                Sql(p, "CREATE TABLE Tk (ID LONG CONSTRAINT PK_Tk PRIMARY KEY, N LONG)");
                int[] ns = [10, 20, 30, 40, 40, 50, 60, 70];
                for (int i = 0; i < ns.Length; i++)
                {
                    Sql(p, $"INSERT INTO T (ID, N, S, M) VALUES ({i + 1}, {ns[i]}, 's{i + 1}', 'm{i + 1}')");
                    Sql(p, $"INSERT INTO Tk (ID, N) VALUES ({i + 1}, {ns[i]})");
                }
                foreach (string h in new[] { "H", "H2", "H3" })
                {
                    Sql(p, $"CREATE TABLE {h} (A LONG, B TEXT(10))");
                    Sql(p, $"INSERT INTO {h} (A, B) VALUES (1, 'x')");
                    Sql(p, $"INSERT INTO {h} (A, B) VALUES (1, 'x')");
                    Sql(p, $"INSERT INTO {h} (A, B) VALUES (2, 'y')");
                    Sql(p, $"INSERT INTO {h} (A, B) VALUES (3, Null)");
                }
                Sql(p, "CREATE TABLE Big (ID LONG CONSTRAINT PK_Big PRIMARY KEY, V TEXT(120))");
                Sql(p, $"INSERT INTO Big (ID, V) VALUES (1, '{TextBig[0]}')");
                for (int k = 1; k < 2048; k *= 2)
                {
                    Sql(p, $"INSERT INTO Big (ID, V) SELECT ID + {k}, V FROM Big");
                }
                Sql(p, "CREATE TABLE Ix (ID LONG CONSTRAINT PK_Ix PRIMARY KEY, A LONG, B LONG, C LONG)");
                Sql(p, "CREATE INDEX IX_ABC ON Ix (A, B DESC, C)");
                int id = 1;
                for (int a = 1; a <= 2; a++)
                {
                    for (int b = 1; b <= 3; b++)
                    {
                        for (int c = 1; c <= 2; c++)
                        {
                            Sql(p, $"INSERT INTO Ix (ID, A, B, C) VALUES ({id++}, {a}, {b}, {c})");
                        }
                    }
                }
                Sql(p, "INSERT INTO Ix (ID, A, B, C) VALUES (13, 3, 1, 1)");
                Sql(p, "CREATE TABLE Lk (ID LONG CONSTRAINT PK_Lk PRIMARY KEY, K TEXT(255))");
                Sql(p, "CREATE INDEX IX_LK ON Lk (K)");
                string[] keys = [Rep(100) + "b", Rep(100) + "c", Rep(250) + "1", Rep(250) + "2", Rep(254) + "z", Rep(255)];
                for (int i = 0; i < keys.Length; i++)
                {
                    Sql(p, $"INSERT INTO Lk (ID, K) VALUES ({i + 1}, '{keys[i]}')");
                }
                Sql(p, "CREATE TABLE Gn (ID COUNTER CONSTRAINT PK_Gn PRIMARY KEY, V TEXT(10))");
                Sql(p, "CREATE TABLE PA (ID LONG CONSTRAINT PK_PA PRIMARY KEY)");
                Sql(p, "CREATE TABLE CH (CID LONG CONSTRAINT PK_CH PRIMARY KEY, PID LONG)");
                for (int i = 1; i <= 5; i++)
                {
                    Sql(p, $"INSERT INTO PA (ID) VALUES ({i})");
                    Sql(p, $"INSERT INTO CH (CID, PID) VALUES ({i}, {i})");
                }
                p.AddRelation("RelPC", "PA", "CH", "ID", "PID");
                Sql(p, "CREATE TABLE Rm (ID LONG CONSTRAINT PK_Rm PRIMARY KEY, Mm MEMO NOT NULL)");
                Sql(p, "CREATE TABLE DcT (ID LONG CONSTRAINT PK_DcT PRIMARY KEY, Q LONG)");
                Sql(p, "CREATE INDEX IX_DQ ON DcT (Q)");
                int?[] qs = [1, 2, 2, 3, null];
                for (int i = 0; i < qs.Length; i++)
                {
                    Sql(p, $"INSERT INTO DcT (ID, Q) VALUES ({i + 1}, {(qs[i] is { } q ? q.ToString() : "Null")})");
                }
                Sql(p, "CREATE TABLE Ob (ID LONG CONSTRAINT PK_Ob PRIMARY KEY, O LONGBINARY, Mm MEMO)");
                Sql(p, "CREATE TABLE Ib1 (A LONG, B LONG)");
                Sql(p, "CREATE TABLE Ib2 (A LONG, B LONG)");
                foreach (string v in new[] { "7, 1", "7, 1", "2, Null" })
                {
                    Sql(p, $"INSERT INTO Ib1 (A, B) VALUES ({v})");
                }
                foreach (string v in new[] { "2, Null", "7, 1", "7, 1" })
                {
                    Sql(p, $"INSERT INTO Ib2 (A, B) VALUES ({v})");
                }
                p.CallBind("qd", "db", "CreateQueryDef", A.Str("QP"), A.Str("PARAMETERS pA Long, pB Text (10); SELECT ID, N FROM T WHERE ID >= pA AND S <> pB;")).Release("qd");
                p.FinishLite("db");
            })
        { CreatesFixture = true };

    // CUR-POP -------------------------------------------------------------------------------------------------

    private static IEnumerable<Probe> Pop()
    {
        foreach ((string name, int type) in new[] { ("dynaset", Dao.OpenDynaset), ("snapshot", Dao.OpenSnapshot) })
        {
            yield return Fix($"pop-frontier-{name}-big", [Cur, "CUR-POP"],
                $"CUR-POP, variant: how far does population of a {name} advance on a 2048-row table? RecordCount and AbsolutePosition are read at open, after a field read, after MoveNext, then after Move 9, 90, 400 and 1500 (cumulative positions 10, 100, 500, 2000), and after MoveLast; " +
                "a second new recordset reads RecordCount after MoveLast alone (minimal observation).",
                p =>
                {
                    p.OpenRs("rs", "db", "SELECT ID, V FROM Big ORDER BY ID", type).Get("rs", "RecordCount");
                    Val(p, "rs", "ID").Get("rs", "RecordCount").Call("rs", "MoveNext").Get("rs", "RecordCount").Get("rs", "AbsolutePosition");
                    foreach (int n in new[] { 9, 90, 400, 1500 })
                    {
                        p.Call("rs", "Move", A.I4(n)).Get("rs", "RecordCount").Get("rs", "AbsolutePosition");
                    }
                    p.Call("rs", "MoveLast").Get("rs", "RecordCount").Get("rs", "AbsolutePosition");
                    Close(p, "rs");
                    p.OpenRs("rs", "db", "SELECT ID, V FROM Big ORDER BY ID", type).Call("rs", "MoveLast").Get("rs", "RecordCount");
                    Close(p, "rs");
                });
        }

        foreach (string point in new[] { "open", "first-move", "movelast" })
        {
            yield return Fix($"pop-snapshot-change-after-{point}", [Cur, "CUR-POP"],
                "CUR-POP, variant: at which point does a snapshot fix a row's values? A snapshot over Big (ID, V) is opened; " + point switch
                {
                    "open" => "nothing else happens before the change",
                    "first-move" => "it reads row 1 and 2 (MoveNext) before the change",
                    _ => "it does MoveLast and MoveFirst before the change",
                } +
                "; then the same Database runs UPDATE Big SET V = 'chg' WHERE ID IN (2, 1000, 2000) (dbFailOnError); the snapshot does MoveFirst and reads ID and V, Move 1 (ID 2, adjacent), Move 998 (ID 1000) and Move 1000 (ID 2000), reading ID and V each time.",
                p =>
                {
                    p.OpenRs("rs", "db", "SELECT ID, V FROM Big ORDER BY ID", Dao.OpenSnapshot);
                    Val(Val(p, "rs", "ID"), "rs", "V");
                    if (point == "first-move")
                    {
                        p.Call("rs", "MoveNext");
                        Val(Val(p, "rs", "ID"), "rs", "V");
                    }
                    else if (point == "movelast")
                    {
                        p.Call("rs", "MoveLast").Call("rs", "MoveFirst");
                    }
                    Exec(p, "UPDATE Big SET V = 'chg' WHERE ID IN (2, 1000, 2000)");
                    p.Call("rs", "MoveFirst");
                    Val(Val(p, "rs", "ID"), "rs", "V");
                    foreach (int n in new[] { 1, 998, 1000 })
                    {
                        p.Call("rs", "Move", A.I4(n));
                        Val(Val(p, "rs", "ID"), "rs", "V");
                    }
                    Close(p, "rs");
                });
        }
    }

    // CUR-ID --------------------------------------------------------------------------------------------------

    private static IEnumerable<Probe> Ids()
    {
        yield return Fix("id-bookmark-bytes-by-row", [Cur, "CUR-ID"],
            "CUR-ID, variant: how does the 4-byte Bookmark encode row ordinals beyond 255? A dynaset on Big, a table-type recordset on Big without Index and one with Index PK_Big each read Bookmark (and ID) at rows 1, 255 (Move 254), 256, 257, 1000 and 2048 (MoveLast).",
            p =>
            {
                foreach ((string tag, int type, string? index) in new (string, int, string?)[] { ("dyn", Dao.OpenDynaset, null), ("tbl", Dao.OpenTable, null), ("tblpk", Dao.OpenTable, "PK_Big") })
                {
                    p.OpenRs("rs", "db", "Big", type);
                    if (index is not null)
                    {
                        p.Put("rs", "Index", A.Str(index));
                    }
                    p.Get("rs", "Bookmark");
                    Val(p, "rs", "ID");
                    foreach (int n in new[] { 254, 1, 1, 743 })
                    {
                        p.Call("rs", "Move", A.I4(n)).Get("rs", "Bookmark");
                        Val(p, "rs", "ID");
                    }
                    p.Call("rs", "MoveLast").Get("rs", "Bookmark");
                    Val(p, "rs", "ID");
                    Close(p, "rs");
                }
            });

        yield return Fix("id-bookmark-assign-deleted-and-beyond", [Cur, "CUR-ID"],
            "CUR-ID, variant: what does assigning a retained Bookmark do after its row was deleted, and for an ordinal past the end? A dynaset on T moves to ID 3 and reads Bookmark; another Database call deletes ID 3; " +
            "Bookmark is assigned the constant bytes of the row read earlier (02 02 00 00), Errors, BOF, EOF, AbsolutePosition and ID are read; then the constant 14 02 00 00 (row 20) is assigned and read the same way. A snapshot repeats this with 02 03 00 00 and 14 03 00 00 (no delete).",
            p =>
            {
                p.OpenRs("rs", "db", "T", Dao.OpenDynaset).Call("rs", "Move", A.I4(2)).Get("rs", "Bookmark");
                Val(p, "rs", "ID");
                Exec(p, "DELETE FROM T WHERE ID = 3");
                BookmarkTry(p, "rs", 2, 2);
                BookmarkTry(p, "rs", 20, 2);
                Close(p, "rs");
                p.OpenRs("rs", "db", "T", Dao.OpenSnapshot).Call("rs", "Move", A.I4(2)).Get("rs", "Bookmark");
                BookmarkTry(p, "rs", 2, 3);
                BookmarkTry(p, "rs", 20, 3);
                Close(p, "rs");
            });

        yield return Fix("id-bookmark-after-requery-and-reinsert", [Cur, "CUR-ID"],
            "CUR-ID, variant: is a retained Bookmark still valid after Requery, and after the row was deleted and a new row with the same key inserted? A dynaset on T reads the Bookmark of ID 3; Requery; the constant 02 02 00 00 is assigned and read back; " +
            "then ID 3 is deleted and reinserted (N 333, S 'new') through the Database, Requery, the constant assigned again, N and ID read.",
            p =>
            {
                p.OpenRs("rs", "db", "T", Dao.OpenDynaset).Call("rs", "Move", A.I4(2)).Get("rs", "Bookmark");
                p.Call("rs", "Requery");
                State(p, "rs");
                BookmarkTry(p, "rs", 2, 2);
                Exec(p, "DELETE FROM T WHERE ID = 3");
                Exec(p, "INSERT INTO T (ID, N, S) VALUES (3, 333, 'new')");
                p.Call("rs", "Requery");
                BookmarkTry(p, "rs", 2, 2);
                Val(p, "rs", "N");
                Close(p, "rs");
            });

        yield return Fix("id-held-field-after-delete-reinsert", [Cur, "CUR-ID", DaoCard, "DAO-ID"],
            "CUR-ID and DAO-ID, variant: what does a retained Field of a recordset read after its row is deleted and a row of the same key is reinserted by another call? A dynaset on T at ID 3 holds Field N (and S); " +
            "the Database deletes ID 3; the held Field is read; ID 3 is reinserted (N 333); read again; Move 0; read again; MoveFirst then MoveNext twice, reading ID and the held Field; Requery; read.",
            p =>
            {
                p.OpenRs("rs", "db", "T", Dao.OpenDynaset).Call("rs", "Move", A.I4(2));
                Hold(p, "rs", "h_", "N", "S");
                p.Get("h_N", "Value").Get("h_S", "Value");
                Exec(p, "DELETE FROM T WHERE ID = 3");
                p.Get("h_N", "Value").Errors().Get("h_S", "Value");
                Exec(p, "INSERT INTO T (ID, N, S) VALUES (3, 333, 'new')");
                p.Get("h_N", "Value").Errors().Call("rs", "Move", A.I4(0)).Errors().Get("h_N", "Value").Errors();
                p.Call("rs", "MoveFirst").Call("rs", "MoveNext").Call("rs", "MoveNext");
                Val(p, "rs", "ID").Get("h_N", "Value");
                p.Call("rs", "Requery").Get("h_N", "Value").Errors();
                p.Release("h_N").Release("h_S").Release("h_s");
                Close(p, "rs");
            });
    }

    // CUR-SEEK ------------------------------------------------------------------------------------------------

    private static IEnumerable<Probe> Seeks()
    {
        Arg[][] shortKeys = [[A.I4(1)], [A.I4(2)], [A.I4(3)], [A.I4(1), A.I4(2)], [A.I4(2), A.I4(1)], [A.I4(1), A.I4(2), A.I4(1)], [A.I4(2), A.I4(3)]];
        string[] ops = ["=", ">=", ">", "<=", "<"];
        foreach (string start in new[] { "first", "last" })
        {
            yield return Fix($"seek-short-keys-from-{start}", [Cur, "CUR-SEEK"],
                $"CUR-SEEK, variant: how is a key shorter than the index (or full) compared on IX_ABC (A asc, B desc, C asc)? Ix rows: IDs 1-12 are (A,B,C) with A 1-2, B 1-3, C 1-2 in ascending order, ID 13 is (3,1,1). A new table-type recordset starting at the {start} index row " +
                "runs Seek for each of =, >=, >, <=, < with the keys (1), (2), (3), (1,2), (2,1), (1,2,1) and (2,3); after each Seek Errors, NoMatch, BOF, EOF and ID are read.",
                p =>
                {
                    foreach (Arg[] keys in shortKeys)
                    {
                        foreach (string op in ops)
                        {
                            SeekOnce(p, "Ix", "IX_ABC", start, op, keys);
                        }
                    }
                });
        }

        yield return Fix("seek-omission-positions", [Cur, "CUR-SEEK"],
            "CUR-SEEK, variant: omitted, Null and Empty key components in leading, middle and trailing position on IX_ABC. Each new table-type recordset starts at the last index row; Seek = and >= with the keys (Missing,2), (Missing,2,1), (1,Missing,1), (1,2,Missing), (Null,2), (Empty,2,1), (1,Null,1) and (1,2,Null); after each Errors, NoMatch, BOF, EOF and ID are read.",
            p =>
            {
                Arg[][] keys = [[A.Missing, A.I4(2)], [A.Missing, A.I4(2), A.I4(1)], [A.I4(1), A.Missing, A.I4(1)], [A.I4(1), A.I4(2), A.Missing], [A.Null, A.I4(2)], [A.Empty, A.I4(2), A.I4(1)], [A.I4(1), A.Null, A.I4(1)], [A.I4(1), A.I4(2), A.Null]];
                foreach (Arg[] k in keys)
                {
                    foreach (string op in new[] { "=", ">=" })
                    {
                        SeekOnce(p, "Ix", "IX_ABC", "last", op, k);
                    }
                }
            });

        yield return Fix("seek-long-key-collision", [Cur, "CUR-SEEK"],
            "CUR-SEEK, variant: do long text keys collide in an index? Lk(ID, K TEXT(255)) with IX_LK holds K = 100 a + b (ID 1), 100 a + c (2), 250 a + 1 (3), 250 a + 2 (4), 254 a + z (5) and 255 a (6). " +
            "From a new table-type recordset starting at the last row, Seek = for the keys of IDs 1, 3, 4 and 6; > and < for the key of ID 3; = and >= for the 250-a prefix; = for 254 a + y and > for 254 a + y; after each Errors, NoMatch and ID are read.",
            p =>
            {
                (string Op, string Key)[] seeks =
                [
                    ("=", Rep(100) + "b"), ("=", Rep(250) + "1"), ("=", Rep(250) + "2"), ("=", Rep(255)), (">", Rep(250) + "1"), ("<", Rep(250) + "1"),
                    ("=", Rep(250)), (">=", Rep(250)), ("=", Rep(254) + "y"), (">", Rep(254) + "y"),
                ];
                foreach ((string op, string key) in seeks)
                {
                    SeekOnce(p, "Lk", "IX_LK", "last", op, A.Str(key));
                }
            });
    }

    // CUR-EDIT ------------------------------------------------------------------------------------------------

    private static IEnumerable<Probe> Edits()
    {
        yield return Fix("edit-conflict-other-row-and-restore", [Cur, "CUR-EDIT"],
            "CUR-EDIT original-evidence controls (c) and (f), variant: what makes A's Update a conflict? A (default Workspace Database) edits N of a row while B (a second Database of the same Workspace) (c) updates N of another row ID 3 while A edits ID 2; " +
            "(f2) changes S of the same row ID 4 to 'tmp' and back to 's4' while A edits N of ID 4; (f3) runs UPDATE T SET N = N on the same row ID 6 while A edits N of ID 6. After each A Update, Errors, EditMode and N are read; at the end every row is read through both Databases.",
            p =>
            {
                p.CallBind("db2", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false));
                foreach ((int row, string tag) in new[] { (2, "c"), (4, "f2"), (6, "f3") })
                {
                    p.OpenRs("ra", "db", "T", Dao.OpenDynaset).Call("ra", "Move", A.I4(row - 1)).Call("ra", "Edit");
                    SetVal(p, "ra", "N", A.I4(row * 111));
                    switch (tag)
                    {
                        case "c":
                            Exec(p, "UPDATE T SET N = 333 WHERE ID = 3", "db2");
                            break;
                        case "f2":
                            Exec(p, "UPDATE T SET S = 'tmp' WHERE ID = 4", "db2");
                            Exec(p, "UPDATE T SET S = 's4' WHERE ID = 4", "db2");
                            break;
                        default:
                            Exec(p, "UPDATE T SET N = N WHERE ID = 6", "db2");
                            break;
                    }
                    p.Call("ra", "Update").Errors().Get("ra", "EditMode");
                    Val(p, "ra", "N");
                    p.Call("ra", "CancelUpdate");
                    Close(p, "ra");
                }
                Snap(p, "SELECT ID, N, S FROM T ORDER BY ID", 3);
                Snap(p, "SELECT ID, N, S FROM T ORDER BY ID", 3, "db2");
                p.Call("db2", "Close").Release("db2");
            });

        yield return Fix("edit-addnew-counter-rollback-reopen", [Cur, "CUR-EDIT"],
            "CUR-EDIT, variant: when is an AutoNumber value reserved and reused? On Gn (ID COUNTER): inside BeginTrans, AddNew (read ID), CancelUpdate; AddNew (read ID, set V 'a'), Update; Close; Rollback; a new recordset: AddNew (read ID), CancelUpdate, Close; " +
            "then the Database is closed and reopened: AddNew (read ID), set V 'z', Update; then every Gn row is read.",
            p =>
            {
                p.Call("eng", "BeginTrans").OpenRs("rs", "db", "Gn", Dao.OpenDynaset).Call("rs", "AddNew");
                Val(p, "rs", "ID").Call("rs", "CancelUpdate").Call("rs", "AddNew");
                Val(p, "rs", "ID");
                SetVal(p, "rs", "V", A.Str("a"));
                p.Call("rs", "Update");
                Close(p, "rs");
                p.Call("eng", "Rollback").Errors();
                p.OpenRs("rs", "db", "Gn", Dao.OpenDynaset).Call("rs", "AddNew");
                Val(p, "rs", "ID").Call("rs", "CancelUpdate");
                Close(p, "rs");
                p.Call("db", "Close").Release("db").CallBind("db", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false));
                p.OpenRs("rs", "db", "Gn", Dao.OpenDynaset).Call("rs", "AddNew");
                Val(p, "rs", "ID");
                SetVal(p, "rs", "V", A.Str("z"));
                p.Call("rs", "Update");
                Close(p, "rs");
                Snap(p, "SELECT ID, V FROM Gn ORDER BY ID", 2);
            });

        yield return Fix("edit-fk-violation", [Cur, "CUR-EDIT"],
            "CUR-EDIT, variant: relationship violations through the recordset edit path. On CH (relation RelPC to PA): AddNew with CID 10, PID 99 and Update; then Edit of CID 1 setting PID 99 and Update; on PA: Edit of ID 1 setting ID 10 and Update, then Delete of the first row; " +
            "after each call Errors, EditMode and the row state are read, a failed edit is cancelled; at the end PA and CH are read.",
            p =>
            {
                p.OpenRs("rs", "db", "CH", Dao.OpenDynaset).Call("rs", "AddNew");
                SetVal(SetVal(p, "rs", "CID", A.I4(10)), "rs", "PID", A.I4(99));
                p.Call("rs", "Update").Errors().Get("rs", "EditMode").Call("rs", "CancelUpdate");
                p.Call("rs", "MoveFirst").Call("rs", "Edit");
                SetVal(p, "rs", "PID", A.I4(99));
                p.Call("rs", "Update").Errors().Get("rs", "EditMode").Call("rs", "CancelUpdate");
                Close(p, "rs");
                p.OpenRs("rs", "db", "PA", Dao.OpenDynaset).Call("rs", "Edit");
                SetVal(p, "rs", "ID", A.I4(10));
                p.Call("rs", "Update").Errors().Get("rs", "EditMode").Call("rs", "CancelUpdate");
                p.Call("rs", "Delete").Errors().Get("rs", "EditMode");
                State(p, "rs");
                Close(p, "rs");
                Snap(p, "SELECT ID FROM PA ORDER BY ID", 1);
                Snap(p, "SELECT CID, PID FROM CH ORDER BY CID", 2);
            });

        yield return Fix("edit-required-memo-addnew", [Cur, "CUR-EDIT"],
            "CUR-EDIT, variant: Required Memo on AddNew. On Rm (Mm MEMO NOT NULL): AddNew, set ID 1 only, Update; then set Mm Null, Update; then Mm '' (zero length), Update; then Mm 'text', Update; Errors, EditMode and the stored rows are read after each step.",
            p =>
            {
                p.OpenRs("rs", "db", "Rm", Dao.OpenDynaset).Call("rs", "AddNew");
                SetVal(p, "rs", "ID", A.I4(1));
                p.Call("rs", "Update").Errors().Get("rs", "EditMode");
                SetVal(p, "rs", "Mm", A.Null);
                p.Errors().Call("rs", "Update").Errors().Get("rs", "EditMode");
                SetVal(p, "rs", "Mm", A.Str(""));
                p.Errors().Call("rs", "Update").Errors().Get("rs", "EditMode");
                SetVal(p, "rs", "Mm", A.Str("text"));
                p.Errors().Call("rs", "Update").Errors().Get("rs", "EditMode");
                Close(p, "rs");
                Snap(p, "SELECT ID, Mm FROM Rm ORDER BY ID", 2);
            });
    }

    // CUR-PROV ------------------------------------------------------------------------------------------------

    private static IEnumerable<Probe> Prov()
    {
        yield return Fix("prov-heap-second-row-edit", [Cur, "CUR-PROV"],
            "CUR-PROV, variant: which of two equal heap rows does an edit or delete reach when the cursor is on the SECOND one? H, H2 and H3 each hold (1,'x'),(1,'x'),(2,'y'),(3,Null). Dynaset on H: MoveNext, Edit B 'z', Update; table-type on H2 (no Index): MoveNext, Edit B 'z', Update; dynaset on H3: MoveNext, Delete; each table is then read in full.",
            p =>
            {
                p.OpenRs("rs", "db", "H", Dao.OpenDynaset).Call("rs", "MoveNext").Call("rs", "Edit");
                SetVal(p, "rs", "B", A.Str("z"));
                p.Call("rs", "Update").Errors();
                Close(p, "rs");
                p.OpenRs("rs", "db", "H2", Dao.OpenTable).Call("rs", "MoveNext").Call("rs", "Edit");
                SetVal(p, "rs", "B", A.Str("z"));
                p.Call("rs", "Update").Errors();
                Close(p, "rs");
                p.OpenRs("rs", "db", "H3", Dao.OpenDynaset).Call("rs", "MoveNext").Call("rs", "Delete").Errors();
                Close(p, "rs");
                foreach (string t in new[] { "H", "H2", "H3" })
                {
                    Snap(p, $"SELECT A, B FROM {t}", 2);
                }
            });

        yield return Fix("prov-missing-key-second-row-edit", [Cur, "CUR-PROV"],
            "CUR-PROV, variant: a projection without the key, cursor on the SECOND of two equal rows. Dynaset on SELECT N FROM T WHERE N = 40 (IDs 4 and 5): MoveNext, Edit N 41, Update; then dynaset on SELECT N FROM Tk WHERE N = 40: MoveNext, Delete; both tables are read by ID.",
            p =>
            {
                p.OpenRs("rs", "db", "SELECT N FROM T WHERE N = 40", Dao.OpenDynaset).Call("rs", "MoveNext").Call("rs", "Edit");
                SetVal(p, "rs", "N", A.I4(41));
                p.Call("rs", "Update").Errors();
                Close(p, "rs");
                p.OpenRs("rs", "db", "SELECT N FROM Tk WHERE N = 40", Dao.OpenDynaset).Call("rs", "MoveNext").Call("rs", "Delete").Errors();
                Close(p, "rs");
                Snap(p, "SELECT ID, N FROM T ORDER BY ID", 2);
                Snap(p, "SELECT ID, N FROM Tk ORDER BY ID", 2);
            });

        yield return Fix("prov-readonly-option-forms", [Cur, "CUR-PROV"],
            "CUR-PROV, variant: why does OpenRecordset('F', Missing, 4) report Updatable True while option 4 on a table-type reports False? On T, writable: OpenRecordset('T', 1, 4), ('T', Missing, 4), ('T', 1, 4, Missing), ('T', 2, 4) and ('T', Missing, 4, 3); for each Type, Updatable, then Edit (Errors, EditMode) and CancelUpdate.",
            p =>
            {
                Arg[][] forms = [[A.Str("T"), A.I4(1), A.I4(4)], [A.Str("T"), A.Missing, A.I4(4)], [A.Str("T"), A.I4(1), A.I4(4), A.Missing], [A.Str("T"), A.I4(2), A.I4(4)], [A.Str("T"), A.Missing, A.I4(4), A.I4(3)]];
                foreach (Arg[] f in forms)
                {
                    p.CallBind("rs", "db", "OpenRecordset", f).Errors().Get("rs", "Type").Get("rs", "Updatable").Call("rs", "Edit").Errors().Get("rs", "EditMode").Call("rs", "CancelUpdate");
                    Close(p, "rs");
                }
            });
    }

    // CUR-BULK ------------------------------------------------------------------------------------------------

    private static IEnumerable<Probe> Bulk()
    {
        yield return Fix("bulk-getrows-deletion-discovery", [Cur, "CUR-BULK"],
            "CUR-BULK, variant: deletion discovered before versus inside GetRows. Dynaset on T: (a) at ID 1 another call deletes IDs 5 and 6 (undiscovered), GetRows(4) (BOF, EOF, ID read), GetRows(4) again; " +
            "(b) a new dynaset at ID 1: ID 2 is deleted, MoveNext onto the deleted slot (ID read, discovered), GetRows(3), then GetRows(3) again.",
            p =>
            {
                p.OpenRs("rs", "db", "T", Dao.OpenDynaset);
                Exec(p, "DELETE FROM T WHERE ID IN (5, 6)");
                p.Call("rs", "GetRows", A.I4(4)).Errors();
                State(p, "rs");
                p.Call("rs", "GetRows", A.I4(4)).Errors();
                State(p, "rs");
                Close(p, "rs");
                p.OpenRs("rs", "db", "T", Dao.OpenDynaset);
                Exec(p, "DELETE FROM T WHERE ID = 2");
                p.Call("rs", "MoveNext");
                State(p, "rs");
                p.Call("rs", "GetRows", A.I4(3)).Errors();
                State(p, "rs");
                p.Call("rs", "GetRows", A.I4(3)).Errors();
                State(p, "rs");
                Close(p, "rs");
            });

        yield return Fix("bulk-getrows-large-count", [Cur, "CUR-BULK"],
            "CUR-BULK, variant: is there a ceiling on GetRows rows per call? On Big (2048 rows), a dynaset and a snapshot each call GetRows(1500), read ID, EOF; GetRows(1000); then GetRows(1000) at EOF (Errors).",
            p =>
            {
                foreach ((string tag, int type) in new[] { ("dyn", Dao.OpenDynaset), ("snap", Dao.OpenSnapshot) })
                {
                    p.OpenRs("rs", "db", "SELECT ID, V FROM Big ORDER BY ID", type).Call("rs", "GetRows", A.I4(1500)).Errors();
                    State(p, "rs");
                    p.Call("rs", "GetRows", A.I4(1000)).Errors();
                    State(p, "rs");
                    p.Call("rs", "GetRows", A.I4(1000)).Errors();
                    Close(p, "rs");
                }
            });
    }

    // CUR-LIFE and DAO-LIFE -----------------------------------------------------------------------------------

    private static IEnumerable<Probe> Life()
    {
        yield return Fix("life-clone-population-order", [Cur, "CUR-LIFE"],
            "CUR-LIFE, variant: is population shared by a clone made before versus after the parent populated? On Big dynasets: (A) parent MoveLast, then Clone: clone RecordCount, AbsolutePosition; (B) Clone first, clone RecordCount, parent RecordCount, then parent MoveLast and both counts; (C) Clone first, clone MoveLast, then parent RecordCount.",
            p =>
            {
                p.OpenRs("ra", "db", "SELECT ID, V FROM Big ORDER BY ID", Dao.OpenDynaset).Call("ra", "MoveLast").Get("ra", "RecordCount").CallBind("ca", "ra", "Clone");
                p.Get("ca", "RecordCount").Get("ca", "AbsolutePosition");
                Close(p, "ca");
                Close(p, "ra");
                p.OpenRs("rb", "db", "SELECT ID, V FROM Big ORDER BY ID", Dao.OpenDynaset).CallBind("cb", "rb", "Clone").Get("cb", "RecordCount").Get("rb", "RecordCount");
                p.Call("rb", "MoveLast").Get("cb", "RecordCount").Get("rb", "RecordCount");
                Close(p, "cb");
                Close(p, "rb");
                p.OpenRs("rc", "db", "SELECT ID, V FROM Big ORDER BY ID", Dao.OpenDynaset).CallBind("cc", "rc", "Clone").Call("cc", "MoveLast").Get("rc", "RecordCount").Get("cc", "RecordCount");
                Close(p, "cc");
                Close(p, "rc");
            });

        yield return Fix("life-clone-close-transaction-one-level", [Cur, "CUR-LIFE", "TXN-CLOSE"],
            "CUR-LIFE, variant: does closing a Clone inside an explicit transaction undo work? Each case on its own row of T: (1) BeginTrans, edit and Update N of ID 1, Clone, close the clone, Rollback; (2) BeginTrans, Edit N of ID 2 left pending, Clone, close clone, read EditMode, Update, CommitTrans; " +
            "(3) BeginTrans, Update N of ID 3, Clone, close clone, CommitTrans; (4) no transaction, Update N of ID 4, Clone, close clone; Errors after each close; T is read at the end.",
            p =>
            {
                CloneCase(p, 1, true, true, "Rollback");
                p.OpenRs("rs", "db", "T", Dao.OpenDynaset);
                p.Call("eng", "BeginTrans").Call("rs", "Move", A.I4(1)).Call("rs", "Edit");
                SetVal(p, "rs", "N", A.I4(1002));
                p.CallBind("cl", "rs", "Clone").Call("cl", "Close").Release("cl").Errors().Get("rs", "EditMode").Call("rs", "Update").Errors().Call("eng", "CommitTrans").Errors();
                Close(p, "rs");
                CloneCase(p, 3, true, true, "CommitTrans");
                CloneCase(p, 4, false, true, null);
                Snap(p, "SELECT ID, N FROM T ORDER BY ID", 2);
            });

        yield return Fix("life-clone-close-transaction-nested", [Cur, "CUR-LIFE", "TXN-CLOSE"],
            "CUR-LIFE, variant: clone close under nested transaction levels. (1) BeginTrans twice, Update N of ID 1, Clone, close, inner Rollback, read N, outer CommitTrans, read N; (2) BeginTrans, Update N of ID 2, BeginTrans, Clone, close, inner CommitTrans, outer Rollback, read N; " +
            "(3) a clone opened before any BeginTrans, BeginTrans, Update N of ID 3, close the clone, Rollback, read N.",
            p =>
            {
                p.OpenRs("rs", "db", "T", Dao.OpenDynaset).Call("eng", "BeginTrans").Call("eng", "BeginTrans").Call("rs", "Edit");
                SetVal(p, "rs", "N", A.I4(2001));
                p.Call("rs", "Update").CallBind("cl", "rs", "Clone").Call("cl", "Close").Release("cl").Errors().Call("eng", "Rollback").Errors();
                Snap(p, "SELECT ID, N FROM T WHERE ID = 1", 2);
                p.Call("eng", "CommitTrans").Errors();
                Snap(p, "SELECT ID, N FROM T WHERE ID = 1", 2);
                Close(p, "rs");
                p.OpenRs("rs", "db", "T", Dao.OpenDynaset).Call("rs", "Move", A.I4(1)).Call("eng", "BeginTrans").Call("rs", "Edit");
                SetVal(p, "rs", "N", A.I4(2002));
                p.Call("rs", "Update").Call("eng", "BeginTrans").CallBind("cl", "rs", "Clone").Call("cl", "Close").Release("cl").Errors().Call("eng", "CommitTrans").Call("eng", "Rollback").Errors();
                Snap(p, "SELECT ID, N FROM T WHERE ID = 2", 2);
                Close(p, "rs");
                p.OpenRs("rs", "db", "T", Dao.OpenDynaset).Call("rs", "Move", A.I4(2)).CallBind("cl", "rs", "Clone").Call("eng", "BeginTrans").Call("rs", "Edit");
                SetVal(p, "rs", "N", A.I4(2003));
                p.Call("rs", "Update").Call("cl", "Close").Release("cl").Errors().Call("eng", "Rollback").Errors();
                Snap(p, "SELECT ID, N FROM T WHERE ID = 3", 2);
                Close(p, "rs");
            });

        foreach (bool alsoEngine in new[] { false, true })
        {
            string tag = alsoEngine ? "database-and-engine" : "database";
            yield return new Probe(Card, $"life-release-without-close-{tag}",
                [DaoCard, "DAO-LIFE", "DAO-CLOSE-OWN", "R-LOCK-1"],
                $"DAO-LIFE, variant: public Close versus last reference release. Process A opens the fixture exclusive read-write, then releases its {tag} reference WITHOUT calling Close, records the file and lock file, and signals; process B then opens the same file exclusive read-write, reads COUNT(*) of T, Errors, closes and signals; A ends after B.",
                a =>
                {
                    a.EngineLite().CallBind("db", "eng", "OpenDatabase", A.In(), A.Bool(true), A.Bool(false)).Get("db", "Name").Release("db");
                    if (alsoEngine)
                    {
                        a.Release("eng");
                    }
                    a.Files("a-released").Signal("a-released").Await("b-done", Wait).Files("a-end");
                })
            {
                Fixtures = [("main", Fx)],
                Participants =
                [
                    ("b", b =>
                    {
                        b.Await("a-released", Wait).Create().CallBind("dbb", "eng", "OpenDatabase", A.In(), A.Bool(true), A.Bool(false)).Errors();
                        b.OpenRs("rb", "dbb", "SELECT COUNT(*) FROM T", Dao.OpenSnapshot).ReadRow("rb").Call("rb", "Close").Release("rb");
                        b.Call("dbb", "Close").Release("dbb").Files("b-after").Signal("b-done");
                    }),
                ],
            };
        }
    }

    // R-DAO-1 -------------------------------------------------------------------------------------------------

    private static IEnumerable<Probe> Dao1()
    {
        yield return new Probe(Card, "boot-systemdb-direct-first", [DaoCard, "DAO-BOOT"],
            "DAO-BOOT, variant: order of the SystemDB reads. In a new process, before any session exists: DBEngine.SystemDB (direct getter), Workspaces.Count, SystemDB direct again, then Properties.Item('SystemDB').Value, IniPath and DefaultType.",
            p =>
            {
                p.EngineLite().Get("eng", "SystemDB").GetBind("wss", "eng", "Workspaces").Get("wss", "Count").Get("eng", "SystemDB").Release("wss");
                p.GetBind("eprops", "eng", "Properties").Item("sp", "eprops", A.Str("SystemDB")).Get("sp", "Value").Release("sp").Release("eprops");
                p.Get("eng", "IniPath").Get("eng", "DefaultType").FinishLite();
            });

        yield return new Probe(Card, "boot-systemdb-properties-first", [DaoCard, "DAO-BOOT"],
            "DAO-BOOT, variant: order of the SystemDB reads, reversed. In a new process, before any session exists: Properties.Item('SystemDB').Value first, then the direct SystemDB getter, Workspaces.Count and the direct getter again.",
            p =>
            {
                p.EngineLite().GetBind("eprops", "eng", "Properties").Item("sp", "eprops", A.Str("SystemDB")).Get("sp", "Value").Release("sp").Release("eprops");
                p.Get("eng", "SystemDB").GetBind("wss", "eng", "Workspaces").Get("wss", "Count").Get("eng", "SystemDB").Release("wss");
                p.FinishLite();
            });

        yield return new Probe(Card, "create-membership-refresh", [DaoCard, "DAO-CREATE", "DAO-COLL"],
            "DAO-CREATE, variant: is the Database made by CreateDatabase a member of Workspace.Databases, and is the count stale until Refresh? In a new process: DBEngine.CreateDatabase (new file), then Workspaces(0).Databases Count, Refresh, Count, member names; " +
            "then Workspaces(0).CreateDatabase (a second file): Count, Refresh, Count, names; then Close of the first created Database: Count, Refresh, Count.",
            p =>
            {
                p.EngineLite().CallBind("dbe", "eng", "CreateDatabase", A.Out("qcd-eng-created.accdb"), A.Str(Dao.LangGeneral), A.I4(Dao.Version120));
                p.GetBind("wss", "eng", "Workspaces").Item("w0", "wss", A.I4(0)).GetBind("dbs", "w0", "Databases");
                DbMembers(p);
                p.CallBind("dbw", "w0", "CreateDatabase", A.Out("qcd-ws-created.accdb"), A.Str(Dao.LangGeneral), A.I4(Dao.Version120));
                DbMembers(p);
                p.Call("dbe", "Close");
                DbMembers(p);
                p.Call("dbw", "Close").Release("dbs").Release("w0").Release("wss").FinishLite();
            });

        yield return Fix("id-recreate-same-name-held", [DaoCard, "DAO-ID"],
            "DAO-ID, variant: a held TableDef when a native table of the same name is recreated. Rc1 and Rc2 (A LONG) are created; TableDefs are refreshed and both TableDefs held. Rc1: TableDefs.Delete, then CREATE TABLE Rc1 (B TEXT(5), C LONG); Rc2: DROP TABLE, then CREATE TABLE Rc2 (B TEXT(5), C LONG). " +
            "After each step the held object's Name and Fields.Count are read (Errors); then TableDefs.Refresh and the held object again; then TableDefs.Item of the name (new object) with its Fields.Count.",
            p =>
            {
                Sql(p, "CREATE TABLE Rc1 (A LONG)");
                Sql(p, "CREATE TABLE Rc2 (A LONG)");
                p.GetBind("tds", "db", "TableDefs").Call("tds", "Refresh").Item("t1", "tds", A.Str("Rc1")).Item("t2", "tds", A.Str("Rc2"));
                p.Call("tds", "Delete", A.Str("Rc1"));
                Sql(p, "CREATE TABLE Rc1 (B TEXT(5), C LONG)");
                Sql(p, "DROP TABLE Rc2");
                Sql(p, "CREATE TABLE Rc2 (B TEXT(5), C LONG)");
                HeldTd(p, "t1");
                HeldTd(p, "t2");
                p.Call("tds", "Refresh").Get("tds", "Count");
                HeldTd(p, "t1");
                HeldTd(p, "t2");
                p.Item("n1", "tds", A.Str("Rc1"));
                HeldTd(p, "n1");
                p.Item("n2", "tds", A.Str("Rc2"));
                HeldTd(p, "n2");
                p.Release("n1").Release("n2").Release("t1").Release("t2").Release("tds");
            });

        yield return Fix("draft-correct-after-failure", [DaoCard, "DAO-DRAFT"],
            "DAO-DRAFT, variant: is a draft correctable after the failing call? (a) TableDef Dr1 with no fields: Append (fails), CreateField F1 Long, Fields.Append, Append again; (b) TableDef Dr2 with field F1, Name put to 'bad!name', Append (fails), Name put back to Dr2, Append; " +
            "(c) on T: Index IxDraft with no fields, Indexes.Append (fails), CreateField N, Fields.Append, Indexes.Append again; (d) Relation RelDraft PA/CH with no fields, Relations.Append (fails), CreateField ID with ForeignName CID, Fields.Append, Relations.Append again. Errors and the collection Count after each call.",
            p =>
            {
                p.GetBind("tds", "db", "TableDefs");
                p.CallBind("d1", "db", "CreateTableDef", A.Str("Dr1")).Call("tds", "Append", A.Obj("d1")).Errors().Get("tds", "Count");
                p.CallBind("f1", "d1", "CreateField", A.Str("F1"), A.I4(4)).GetBind("d1f", "d1", "Fields").Call("d1f", "Append", A.Obj("f1")).Call("tds", "Append", A.Obj("d1")).Errors().Get("tds", "Count");
                p.CallBind("d2", "db", "CreateTableDef", A.Str("Dr2")).CallBind("f2", "d2", "CreateField", A.Str("F1"), A.I4(4)).GetBind("d2f", "d2", "Fields").Call("d2f", "Append", A.Obj("f2"));
                p.Put("d2", "Name", A.Str("bad!name")).Errors().Call("tds", "Append", A.Obj("d2")).Errors().Get("tds", "Count");
                p.Put("d2", "Name", A.Str("Dr2")).Call("tds", "Append", A.Obj("d2")).Errors().Get("tds", "Count");
                p.Call("tds", "Refresh").Item("tt", "tds", A.Str("T")).GetBind("tix", "tt", "Indexes").CallBind("ix", "tt", "CreateIndex", A.Str("IxDraft"));
                p.Call("tix", "Append", A.Obj("ix")).Errors().Get("tix", "Count");
                p.CallBind("ixf", "ix", "CreateField", A.Str("N")).GetBind("ixfs", "ix", "Fields").Call("ixfs", "Append", A.Obj("ixf")).Call("tix", "Append", A.Obj("ix")).Errors().Call("tix", "Refresh").Get("tix", "Count");
                p.GetBind("rels", "db", "Relations").CallBind("rl", "db", "CreateRelation", A.Str("RelDraft"), A.Str("PA"), A.Str("CH"), A.I4(0));
                p.Call("rels", "Append", A.Obj("rl")).Errors().Get("rels", "Count");
                p.CallBind("rf", "rl", "CreateField", A.Str("ID")).Put("rf", "ForeignName", A.Str("CID")).GetBind("rfs", "rl", "Fields").Call("rfs", "Append", A.Obj("rf"));
                p.Call("rels", "Append", A.Obj("rl")).Errors().Call("rels", "Refresh").Get("rels", "Count");
            });

        yield return Fix("draft-setters-after-attach-txn", [DaoCard, "DAO-DRAFT"],
            "DAO-DRAFT, variant: setters after attachment and drafts inside a transaction. Sa (A LONG, B TEXT(10)) is created and its TableDef held: Name put 'Sa2'; field B Size put 20, Required put True, Name put 'B2' (Errors, read back, after TableDefs/Fields Refresh); " +
            "then BeginTrans, a TableDef draft Sb with field F1 appended, TableDefs.Count, Rollback, Count, Refresh, Count, Item('Sb'); BeginTrans, Fields.Append of field Cx to Sa2, Rollback, Fields.Count, Refresh, Count.",
            p =>
            {
                Sql(p, "CREATE TABLE Sa (A LONG, B TEXT(10))");
                p.GetBind("tds", "db", "TableDefs").Call("tds", "Refresh").Item("t", "tds", A.Str("Sa")).GetBind("tf", "t", "Fields").Item("fb", "tf", A.Str("B"));
                p.Put("t", "Name", A.Str("Sa2")).Errors().Get("t", "Name");
                p.Put("fb", "Size", A.I4(20)).Errors().Get("fb", "Size").Put("fb", "Required", A.Bool(true)).Errors().Get("fb", "Required").Put("fb", "Name", A.Str("B2")).Errors().Get("fb", "Name");
                p.Call("tds", "Refresh").Call("tf", "Refresh").Get("tf", "Count").Get("tds", "Count");
                p.Call("eng", "BeginTrans").CallBind("sb", "db", "CreateTableDef", A.Str("Sb")).CallBind("sf1", "sb", "CreateField", A.Str("F1"), A.I4(4)).GetBind("sbf", "sb", "Fields").Call("sbf", "Append", A.Obj("sf1"));
                p.Call("tds", "Append", A.Obj("sb")).Errors().Get("tds", "Count").Call("eng", "Rollback").Errors().Get("tds", "Count").Call("tds", "Refresh").Get("tds", "Count");
                p.Item("sbx", "tds", A.Str("Sb")).Errors();
                p.Call("eng", "BeginTrans").CallBind("cx", "t", "CreateField", A.Str("Cx"), A.I4(4)).Call("tf", "Append", A.Obj("cx")).Errors().Get("tf", "Count").Call("eng", "Rollback").Errors().Get("tf", "Count").Call("tf", "Refresh").Get("tf", "Count");
            });

        yield return Fix("meta-document-permissions-repeat", [DaoCard, "DAO-META", "DAO-SEC"],
            "DAO-META and DAO-SEC, variant: does the first Permissions read change the next? Containers('Tables').Documents('T'): Permissions, Permissions again, AllPermissions, AllPermissions again, Permissions a third time (Errors after each); Container Permissions twice; then Name, Container, Owner, UserName, DateCreated, LastUpdated.",
            p =>
            {
                p.GetBind("cs", "db", "Containers").Item("ct", "cs", A.Str("Tables")).GetBind("ds", "ct", "Documents").Item("dt", "ds", A.Str("T"));
                foreach (string m in new[] { "Permissions", "Permissions", "AllPermissions", "AllPermissions", "Permissions" })
                {
                    p.Get("dt", m).Errors();
                }
                p.Get("ct", "Permissions").Errors().Get("ct", "Permissions").Errors();
                foreach (string m in new[] { "Name", "Container", "Owner", "UserName", "DateCreated", "LastUpdated" })
                {
                    p.Get("dt", m);
                }
            });

        yield return Fix("meta-held-document-recreate", [DaoCard, "DAO-META", "DAO-ID"],
            "DAO-META, variant: a held Document across native drop and recreation of its table. Dm1 (A LONG) is created, Tables.Documents refreshed and Document 'Dm1' held; Name, Container and DateCreated read; DROP TABLE Dm1: held Name (Errors); Documents.Refresh, Count, held Name; " +
            "CREATE TABLE Dm1 (B TEXT(5)): held Name; Refresh; Item('Dm1') as a new Document: Name, DateCreated; the old held Document again.",
            p =>
            {
                Sql(p, "CREATE TABLE Dm1 (A LONG)");
                p.GetBind("cs", "db", "Containers").Item("ct", "cs", A.Str("Tables")).GetBind("ds", "ct", "Documents").Call("ds", "Refresh").Item("d", "ds", A.Str("Dm1"));
                p.Get("d", "Name").Get("d", "Container").Get("d", "DateCreated");
                Sql(p, "DROP TABLE Dm1");
                p.Get("d", "Name").Errors().Call("ds", "Refresh").Get("ds", "Count").Get("d", "Name").Errors();
                Sql(p, "CREATE TABLE Dm1 (B TEXT(5))");
                p.Get("d", "Name").Errors().Call("ds", "Refresh").Item("d2", "ds", A.Str("Dm1")).Get("d2", "Name").Get("d2", "DateCreated").Get("d", "Name").Errors();
            });

        yield return Fix("index-distinctcount-dml", [DaoCard, "II-05"],
            "II-05 index slice, variant: when does Index.DistinctCount change? DcT (Q 1,2,2,3,Null) with IX_DQ: held Index DistinctCount; INSERT Q 9: held, after Indexes.Refresh, and from a new TableDefs.Refresh/Item; BeginTrans, INSERT Q 10, held, Rollback, held, after Refresh; then the Database is closed and reopened and DistinctCount read again.",
            p =>
            {
                p.GetBind("tds", "db", "TableDefs").Call("tds", "Refresh").Item("t", "tds", A.Str("DcT")).GetBind("ixs", "t", "Indexes").Item("ix", "ixs", A.Str("IX_DQ")).Get("ix", "DistinctCount");
                Exec(p, "INSERT INTO DcT (ID, Q) VALUES (6, 9)");
                p.Get("ix", "DistinctCount").Call("ixs", "Refresh").Get("ix", "DistinctCount");
                DistinctFresh(p);
                p.Call("eng", "BeginTrans");
                Exec(p, "INSERT INTO DcT (ID, Q) VALUES (7, 10)");
                p.Get("ix", "DistinctCount").Call("eng", "Rollback").Get("ix", "DistinctCount").Call("ixs", "Refresh").Get("ix", "DistinctCount");
                DistinctFresh(p);
                p.Release("ix").Release("ixs").Release("t").Release("tds");
                p.Call("db", "Close").Release("db").CallBind("db", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false));
                DistinctFresh(p);
            });

        yield return Fix("index-nonindexable-types", [DaoCard, "II-05"],
            "II-05 index slice, variant: indexing OLE and Memo fields. On Ob: CREATE INDEX over O (LONGBINARY) and over Mm (MEMO) by SQL; then the DAO route (CreateIndex, CreateField O or Mm, Fields.Append, Indexes.Append) for each; Errors and Indexes.Count (after Refresh) after each.",
            p =>
            {
                p.GetBind("tds", "db", "TableDefs").Call("tds", "Refresh").Item("t", "tds", A.Str("Ob")).GetBind("ixs", "t", "Indexes");
                Sql(p, "CREATE INDEX IxObSql ON Ob (O)");
                p.Errors().Call("ixs", "Refresh").Get("ixs", "Count");
                Sql(p, "CREATE INDEX IxMmSql ON Ob (Mm)");
                p.Errors().Call("ixs", "Refresh").Get("ixs", "Count");
                foreach (string f in new[] { "O", "Mm" })
                {
                    p.CallBind("ix", "t", "CreateIndex", A.Str("IxDao" + f)).CallBind("xf", "ix", "CreateField", A.Str(f)).GetBind("xfs", "ix", "Fields").Call("xfs", "Append", A.Obj("xf")).Errors();
                    p.Call("ixs", "Append", A.Obj("ix")).Errors().Call("ixs", "Refresh").Get("ixs", "Count");
                    p.Release("xfs").Release("xf").Release("ix");
                }
            });

        yield return Fix("index-build-order-failure", [DaoCard, "II-05"],
            "II-05 index slice, variant: which key failure does index construction reach first? Ib1 and Ib2 hold the rows (7,1), (7,1), (2,Null) in two insertion orders (Ib2: (2,Null) first). CREATE UNIQUE INDEX over (A,B) WITH DISALLOW NULL on each (a duplicate and a Null in one build); " +
            "then on Ib1 a unique index without DISALLOW NULL (duplicate only) and a nonunique index WITH DISALLOW NULL (Null only); Errors and the Indexes count (after Refresh) after each.",
            p =>
            {
                p.GetBind("tds", "db", "TableDefs").Call("tds", "Refresh");
                foreach ((string t, string ix, string clause) in new[]
                {
                    ("Ib1", "UxA", "UNIQUE INDEX UxA ON Ib1 (A, B) WITH DISALLOW NULL"), ("Ib2", "UxB", "UNIQUE INDEX UxB ON Ib2 (A, B) WITH DISALLOW NULL"),
                    ("Ib1", "UxC", "UNIQUE INDEX UxC ON Ib1 (A, B)"), ("Ib1", "UxD", "INDEX UxD ON Ib1 (A, B) WITH DISALLOW NULL"),
                })
                {
                    Sql(p, "CREATE " + clause);
                    p.Errors().Item("t", "tds", A.Str(t)).GetBind("ixs", "t", "Indexes").Call("ixs", "Refresh").Get("ixs", "Count").Release("ixs").Release("t");
                }
            });

        yield return Fix("query-param-remove-reintroduce", [DaoCard, "DAO-QUERY"],
            "DAO-QUERY, variant: removing and reintroducing a declared parameter. Saved QP (PARAMETERS pA Long, pB Text(10)): both Parameters held and set (pA 1, pB 'x'); SQL put with only pA declared: held pA and pB Value (Errors), Parameters.Count, Refresh; " +
            "SQL put with both declared again: held pA, pB (Errors), Parameters.Count and a new Item('pB') Value.",
            p =>
            {
                p.GetBind("qds", "db", "QueryDefs").Call("qds", "Refresh").Item("qd", "qds", A.Str("QP")).GetBind("ps", "qd", "Parameters").Item("pa", "ps", A.Str("pA")).Item("pb", "ps", A.Str("pB"));
                p.Put("pa", "Value", A.I4(1)).Put("pb", "Value", A.Str("x"));
                p.Put("qd", "SQL", A.Str("PARAMETERS pA Long; SELECT ID, N FROM T WHERE ID >= pA;")).Errors();
                p.Get("pa", "Value").Errors().Get("pb", "Value").Errors().Get("ps", "Count").Call("ps", "Refresh").Get("ps", "Count");
                p.Put("qd", "SQL", A.Str("PARAMETERS pA Long, pB Text (10); SELECT ID, N FROM T WHERE ID >= pA AND S <> pB;")).Errors();
                p.Get("pa", "Value").Errors().Get("pb", "Value").Errors().Get("ps", "Count").Item("pb2", "ps", A.Str("pB")).Get("pb2", "Value").Errors();
            });
    }

    // R-ERR-1 -------------------------------------------------------------------------------------------------

    private static IEnumerable<Probe> Err1()
    {
        yield return new Probe(Card, "errbase-two-ado-connections", [ErrCard, "ERR-BASE", "ERR-ADO"],
            $"ERR-BASE and ERR-ADO, variant: is ADO Errors per Connection? Two ADODB.Connections (Provider {AceOleDb}, read-only, the fixture) are opened; c1 Execute 'SELEC ID FROM T' (provider failure): both Errors read; c2 Execute 'SELECT * FROM NoSuchTable': both read; " +
            "c1 successful Execute 'SELECT ID FROM T' (recordset closed): both read.",
            p =>
            {
                p.EngineLite();
                AdoOpen(p, "c1");
                AdoOpen(p, "c2");
                p.Call("c1", "Execute", A.Str("SELEC ID FROM T"));
                AdoErrs(AdoErrs(p, "c1"), "c2");
                p.Call("c2", "Execute", A.Str("SELECT * FROM NoSuchTable"));
                AdoErrs(AdoErrs(p, "c1"), "c2");
                p.CallBind("r1", "c1", "Execute", A.Str("SELECT ID FROM T")).Call("r1", "Close").Release("r1");
                AdoErrs(AdoErrs(p, "c1"), "c2");
                p.Call("c1", "Close").Call("c2", "Close").Release("c1").Release("c2").FinishLite();
            })
        { Fixtures = [("main", Fx)] };

        yield return new Probe(Card, "errado-clear-by-operation", [ErrCard, "ERR-ADO"],
            $"ERR-ADO, variant: which later calls clear a Connection's Errors? On one Connection (Provider {AceOleDb}, read-only), each case first runs the provider failure Execute 'SELEC ID FROM T' and reads Errors, then does ONE of: (1) successful Execute of 'SELECT ID FROM T'; (2) a property get (State); " +
            "(3) MoveNext on a recordset opened earlier; (4) an ADO runtime failure (Properties.Item 'NoSuchProp'); (5) the held Errors.Clear; and reads Errors again.",
            p =>
            {
                p.EngineLite();
                AdoOpen(p, "cn");
                p.CallBind("ars", "cn", "Execute", A.Str("SELECT ID FROM T"));
                (string Name, Action<P> Do)[] cases =
                [
                    ("execute-success", q => q.CallBind("tmp", "cn", "Execute", A.Str("SELECT ID FROM T")).Call("tmp", "Close").Release("tmp")),
                    ("property-get", q => q.Get("cn", "State")),
                    ("recordset-movenext", q => q.Call("ars", "MoveNext")),
                    ("runtime-failure", q => q.GetBind("cps", "cn", "Properties").Item("nope", "cps", A.Str("NoSuchProp")).Release("cps")),
                    ("errors-clear", q => q.GetBind("cle", "cn", "Errors").Call("cle", "Clear").Release("cle")),
                ];
                foreach ((string _, Action<P> action) in cases)
                {
                    p.Call("cn", "Execute", A.Str("SELEC ID FROM T"));
                    AdoErrs(p, "cn");
                    action(p);
                    AdoErrs(p, "cn");
                }
                p.Call("ars", "Close").Release("ars").Call("cn", "Close").Release("cn").FinishLite();
            })
        { Fixtures = [("main", Fx)] };

        yield return new Probe(Card, "errado-held-after-clear", [ErrCard, "ERR-ADO", "ERR-SNAPSHOT"],
            $"ERR-SNAPSHOT (ADO), variant: a held ADO Error after Clear and after a second failure. Connection (Provider {AceOleDb}, read-only): Execute 'SELEC ID FROM T'; hold Errors and Item(0): Number, Description, SQLState; Errors.Clear; held Error Number, Description; held Errors Count; " +
            "Execute 'SELECT * FROM NoSuchTable'; held Errors Count and Item(0) (new object) Number; the first held Error again.",
            p =>
            {
                p.EngineLite();
                AdoOpen(p, "cn");
                p.Call("cn", "Execute", A.Str("SELEC ID FROM T")).GetBind("he", "cn", "Errors").Item("e0", "he", A.I4(0)).Get("e0", "Number").Get("e0", "Description").Get("e0", "SQLState");
                p.Call("he", "Clear").Get("e0", "Number").Get("e0", "Description").Get("he", "Count");
                p.Call("cn", "Execute", A.Str("SELECT * FROM NoSuchTable")).Get("he", "Count").Item("e1", "he", A.I4(0)).Get("e1", "Number").Get("e0", "Number").Get("e0", "Description");
                p.Release("e1").Release("e0").Release("he").Call("cn", "Close").Release("cn").FinishLite();
            })
        { Fixtures = [("main", Fx)] };

        yield return Fix("errsnap-held-variants", [ErrCard, "ERR-SNAPSHOT"],
            "ERR-SNAPSHOT (DAO), variant: held Error and Errors across further calls. Failure A (Execute 'SELEC ID FROM T'): hold DBEngine.Errors and Item(0) as e0; failure B (OpenRecordset 'SELECT NoCol FROM T'): held Errors Count, a fresh Item(0) Number, e0 Number and Description; " +
            "a success (TableDefs.Count), Refresh of the held Errors, the reads again; failure A repeated: e0 and a fresh Item(0); then Database.Close: e0, held Errors Count; then Item(1) (a failure of the Errors collection itself): e0 and Count.",
            p =>
            {
                p.Call("db", "Execute", A.Str("SELEC ID FROM T"));
                p.GetBind("he", "eng", "Errors").Item("e0", "he", A.I4(0)).Get("e0", "Number");
                p.Call("db", "OpenRecordset", A.Str("SELECT NoCol FROM T"), A.I4(Dao.OpenSnapshot));
                HeldErr(p);
                p.GetBind("tc", "db", "TableDefs").Get("tc", "Count").Release("tc").Call("he", "Refresh");
                HeldErr(p);
                p.Call("db", "Execute", A.Str("SELEC ID FROM T"));
                HeldErr(p);
                p.Call("db", "Close");
                HeldErr(p);
                p.Item("bad", "he", A.I4(1));
                HeldErr(p);
            });

        yield return Fix("errinfo-presence-by-kind", [ErrCard, "ERR-EXCEP", "ERR-THREAD", "ERR-BIND"],
            "ERR-EXCEP and ERR-THREAD, variant: which failing members publish an IErrorInfo (the capture records it per call) besides EXCEPINFO. Each kind is run after a marker failure and followed by an Errors read: (1) OpenRecordset with seven arguments; (2) OpenRecordset with none; " +
            "(3) Recordset.MoveNext(1); (4) Field.Value put 'abc' into Long N after Edit; (5) Field.Value get of SELECT 1/0; (6) Execute of a statement on a missing table; (7) TableDefs.Item('Nope'); (8) MoveNext on a closed recordset; (9) Update without Edit.",
            p =>
            {
                Marker(p).Errors();
                p.Call("db", "OpenRecordset", A.Str("T"), A.I4(4), A.I4(0), A.I4(3), A.I4(5), A.I4(6), A.I4(7)).Errors();
                Marker(p).Call("db", "OpenRecordset").Errors();
                p.OpenRs("rs", "db", "T", Dao.OpenDynaset);
                Marker(p).Call("rs", "MoveNext", A.I4(1)).Errors();
                p.Call("rs", "Edit");
                Marker(p);
                SetVal(p, "rs", "N", A.Str("abc"));
                p.Errors().Call("rs", "CancelUpdate");
                p.OpenRs("rz", "db", "SELECT 1 / 0 AS Z FROM T", Dao.OpenSnapshot);
                Marker(p);
                Val(p, "rz", "Z");
                p.Errors();
                Close(p, "rz");
                Marker(p).Call("db", "Execute", A.Str("DELETE FROM NoSuchTable")).Errors();
                Marker(p).GetBind("tds", "db", "TableDefs").Item("nope", "tds", A.Str("Nope")).Errors();
                Marker(p).Call("rs", "Update").Errors();
                Close(p, "rs");
                Marker(p).Call("rs", "MoveNext").Errors();
            });

        foreach (bool flag in new[] { true, false })
        {
            string name = flag ? "failonerror" : "plain";
            yield return Fix($"errpart-delete-ri-position-{name}", [ErrCard, "ERR-PARTIAL"],
                $"ERR-PARTIAL, variant: where in a multi-row DELETE does a relationship refusal fall, {(flag ? "with" : "without")} dbFailOnError? For k = 1, 3, 5: CH and PA are emptied and PA IDs 1-5 reinserted with ONE child (CID k, PID k); DELETE FROM PA " +
                $"{(flag ? "with dbFailOnError" : "with no options")}; Errors, RecordsAffected, then PA and CH are read.",
                p =>
                {
                    foreach (int k in new[] { 1, 3, 5 })
                    {
                        Exec(p, "DELETE FROM CH");
                        Exec(p, "DELETE FROM PA");
                        for (int i = 1; i <= 5; i++)
                        {
                            Exec(p, $"INSERT INTO PA (ID) VALUES ({i})");
                        }
                        Exec(p, $"INSERT INTO CH (CID, PID) VALUES ({k}, {k})");
                        Marker(p);
                        if (flag)
                        {
                            Exec(p, "DELETE FROM PA");
                        }
                        else
                        {
                            Sql(p, "DELETE FROM PA");
                        }
                        p.Errors().Get("db", "RecordsAffected");
                        Snap(p, "SELECT ID FROM PA ORDER BY ID", 1);
                        Snap(p, "SELECT CID, PID FROM CH ORDER BY CID", 2);
                    }
                });
        }

        yield return Fix("errpart-lock-position", [ErrCard, "ERR-PARTIAL"],
            "ERR-PARTIAL, variant: where in a multi-row UPDATE does a lock refusal fall? A second Workspace W2 opens the file, a dynaset on T with LockEdits True and Edit on row k (FindFirst ID = k) for k = 1, 4, 8; for each k the first Database runs UPDATE T SET N = N + 1 with dbFailOnError and, again, with no options; " +
            "Errors, RecordsAffected and every N are read after each; W2 then does CancelUpdate and closes the recordset.",
            p =>
            {
                p.GetBind("wss", "eng", "Workspaces").CallBind("w2", "eng", "CreateWorkspace", A.Str("W2"), A.Str("admin"), A.Str(""), A.I4(UseJet))
                    .Call("wss", "Append", A.Obj("w2")).CallBind("db2", "w2", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false));
                foreach (int k in new[] { 1, 4, 8 })
                {
                    p.OpenRs("r2", "db2", "T", Dao.OpenDynaset).Put("r2", "LockEdits", A.Bool(true)).Call("r2", "FindFirst", A.Str($"ID = {k}")).Call("r2", "Edit").Errors();
                    Marker(p);
                    Exec(p, "UPDATE T SET N = N + 1");
                    p.Errors().Get("db", "RecordsAffected");
                    Snap(p, "SELECT ID, N FROM T ORDER BY ID", 2);
                    Marker(p);
                    Sql(p, "UPDATE T SET N = N + 1");
                    p.Errors().Get("db", "RecordsAffected");
                    Snap(p, "SELECT ID, N FROM T ORDER BY ID", 2);
                    p.Call("r2", "CancelUpdate");
                    Close(p, "r2");
                }
                p.Call("db2", "Close").Release("db2").Call("w2", "Close").Release("w2").Release("wss");
            });

        yield return Fix("errorder-dispatch-ordinal-by-collection", [ErrCard, "ERR-DAO-ORDER"],
            "ERR-DAO-ORDER, variant: do other collections give a multi-entry Errors when Item gets an object where a name or ordinal is expected (TableDefs.Item(Database) gave entries 3001 then 3421 in r-dao-1-err-1)? After a marker failure each of TableDefs, QueryDefs, Relations, " +
            "Fields of T, Indexes of T and Properties of T is called with Item(db) (an object); Errors (Count, each Number, Description, Source) is read after each; the EXCEPINFO of the call is captured by the record.",
            p =>
            {
                p.GetBind("tds", "db", "TableDefs").Item("t", "tds", A.Str("T"));
                foreach ((string bind, string via, string member) in new[] { ("c1", "db", "TableDefs"), ("c2", "db", "QueryDefs"), ("c3", "db", "Relations"), ("c4", "t", "Fields"), ("c5", "t", "Indexes"), ("c6", "t", "Properties") })
                {
                    p.GetBind(bind, via, member);
                    Marker(p).Item("x" + bind, bind, A.Obj("db")).Errors();
                }
            });
    }

    // Helpers -------------------------------------------------------------------------------------------------

    private static string Rep(int n) => new('a', n);

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

    /// <summary>A failing call that leaves its own entry in DBEngine.Errors (3376), so that a later read belongs to its own step.</summary>
    private static P Marker(P p) => p.Call("db", "Execute", A.Str("DROP TABLE QcdNoSuchMarker"));

    private static P Sql(P p, string sql) => p.Call("db", "Execute", A.Str(sql));

    private static P Exec(P p, string sql, string db = "db") => p.Call(db, "Execute", A.Str(sql), A.I4(Fail));

    private static P Close(P p, string rs) => p.Call(rs, "Close").Release(rs);

    private static P Val(P p, string rs, string field) =>
        p.GetBind("sf", rs, "Fields").Item("sv", "sf", A.Str(field)).Get("sv", "Value").Release("sv").Release("sf");

    private static P SetVal(P p, string rs, string field, Arg value) =>
        p.GetBind("pf", rs, "Fields").Item("pv", "pf", A.Str(field)).Put("pv", "Value", value).Release("pv").Release("pf");

    private static P State(P p, string rs) => Val(p.Get(rs, "BOF").Get(rs, "EOF").Get(rs, "AbsolutePosition"), rs, "ID");

    private static P Hold(P p, string rs, string prefix, params string[] fields)
    {
        p.GetBind(prefix + "s", rs, "Fields");
        foreach (string f in fields)
        {
            p.Item(prefix + f, prefix + "s", A.Str(f));
        }
        return p;
    }

    private static P Snap(P p, string sql, int fields, string db = "db") =>
        p.OpenRs("sn", db, sql, Dao.OpenSnapshot).ReadAll("sn", 64, fields).Call("sn", "Close").Release("sn");

    /// <summary>Assign the constant bookmark (row, kind, 0, 0) and read what it did.</summary>
    private static P BookmarkTry(P p, string rs, byte row, byte kind) =>
        State(p.Put(rs, "Bookmark", A.Bytes(row, kind, 0, 0)).Errors(), rs);

    private static P SeekOnce(P p, string table, string index, string start, string op, params Arg[] keys)
    {
        p.OpenRs("sk", "db", table, Dao.OpenTable).Put("sk", "Index", A.Str(index));
        if (start == "last")
        {
            p.Call("sk", "MoveLast");
        }
        p.Call("sk", "Seek", [A.Str(op), .. keys]).Errors().Get("sk", "NoMatch").Get("sk", "BOF").Get("sk", "EOF");
        Val(p, "sk", "ID");
        return Close(p, "sk");
    }

    private static P CloneCase(P p, int row, bool transaction, bool update, string? end)
    {
        p.OpenRs("rs", "db", "T", Dao.OpenDynaset).Call("rs", "Move", A.I4(row - 1));
        if (transaction)
        {
            p.Call("eng", "BeginTrans");
        }
        p.Call("rs", "Edit");
        SetVal(p, "rs", "N", A.I4(row * 1000 + 1));
        if (update)
        {
            p.Call("rs", "Update");
        }
        p.CallBind("cl", "rs", "Clone").Call("cl", "Close").Release("cl").Errors();
        if (end is not null)
        {
            p.Call("eng", end).Errors();
        }
        return Close(p, "rs");
    }

    private static P DbMembers(P p) =>
        p.Get("dbs", "Count").Call("dbs", "Refresh").Get("dbs", "Count").Each("dbs", "dbm", 8, b => b.Get("dbm", "Name"));

    private static P HeldTd(P p, string td) =>
        p.Get(td, "Name").Errors().GetBind("hf" + td, td, "Fields").Get("hf" + td, "Count").Errors().Release("hf" + td);

    private static P DistinctFresh(P p) =>
        p.GetBind("fts", "db", "TableDefs").Call("fts", "Refresh").Item("ft", "fts", A.Str("DcT")).GetBind("fxs", "ft", "Indexes").Item("fx", "fxs", A.Str("IX_DQ")).Get("fx", "DistinctCount")
            .Release("fx").Release("fxs").Release("ft").Release("fts");

    private static P HeldErr(P p) =>
        p.Get("he", "Count").Get("e0", "Number").Get("e0", "Description").Errors();

    private static P AdoOpen(P p, string conn) =>
        p.Create("ADODB.Connection", conn).Put(conn, "Provider", A.Str(AceOleDb))
            .GetBind("cp" + conn, conn, "Properties").Item("cds" + conn, "cp" + conn, A.Str("Data Source")).Put("cds" + conn, "Value", A.In())
            .Release("cds" + conn).Release("cp" + conn).Put(conn, "Mode", A.I4(1)).Call(conn, "Open");

    private static P AdoErrs(P p, string conn) =>
        p.GetBind("ae" + conn, conn, "Errors").Get("ae" + conn, "Count")
            .Each("ae" + conn, "aer" + conn, 8, b => b.Get("aer" + conn, "Number").Get("aer" + conn, "Description").Get("aer" + conn, "SQLState"))
            .Release("ae" + conn);
}
