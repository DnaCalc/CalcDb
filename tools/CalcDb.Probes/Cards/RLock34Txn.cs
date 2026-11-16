namespace CalcDb.Probes.Cards;

/// <summary>
/// R-LOCK-3 (reads beside an active ACE writer), R-LOCK-4 (shared edits, native publication and rollback) and the
/// cross-process branches of R-TXN-1 (TXN-LOCAL, TXN-VIEW, TXN-NEST, TXN-STMT, TXN-MULTI, TXN-CLOSE and TXN-FLUSH-LIVE
/// as seen from a second process). Two owned ACE processes (a, b) work on one shared-mode attempt copy of a native-created
/// fixture and are ordered only by barriers: each probe is a list of steps, each step owned by one participant and
/// started when the previous step of the other participant has signalled. Every call of every step is captured; the
/// probes ask and never assert, and the fixture rows are small: rows 1 and 2 are adjacent in the primary key and in
/// index IX_N (and, by size, should share a data page); row 64 is far from both. Pages and leaves are not claimed here:
/// the kept file images (Files with keep) let W-02 interpret them later.
/// Deferred, with reasons, in the delivery note: pauses inside one ACE call, process death, network paths, defaults of
/// engine options, option sweeps, callbacks, UI/Access/Excel routes, registry-level locking settings.
/// </summary>
public sealed class RLock34Txn : ICard
{
    private const string Card = "r-lock-3-4-txn";
    private const string FxAccdb = Card + "-main-accdb", FxMdb = Card + "-main-mdb", FxSecond = Card + "-second-accdb";
    private const int Wait = 120000;

    // DAO SetOptionEnum (the values RTxn1 uses), RecordsetOptionEnum, LockTypeEnum, CommitTrans options, Idle actions.
    private const int OptPageTimeout = 6, OptLockRetry = 57, OptMaxLocksPerFile = 62, OptLockDelay = 63, OptRecycleLvs = 65;
    private const int DenyWrite = 1, DenyRead = 2, Pessimistic = 2, Optimistic = 3, ForceOsFlush = 1, RefreshCache = 8;

    private const string R3 = "R-LOCK-3", R4 = "R-LOCK-4", Tx = "R-TXN-1";
    private const string Sel3 = "SELECT ID, N, V FROM T WHERE ID IN (1, 2, 64) ORDER BY ID";
    private const string Sel4 = "SELECT ID, N, V FROM T WHERE ID <= 4 ORDER BY ID";
    private const string Sel8 = "SELECT ID, N, V FROM T WHERE ID <= 8 ORDER BY ID";
    private const string SelU = "SELECT ID, N FROM U WHERE ID = 1";

    private static readonly string Pad = new('x', 200);
    private static readonly string[] Held = ["hs", "hd", "hf", "ht"];

    private sealed record Step(char Who, Action<P> Body);

    private static Step Sa(Action<P> body) => new('a', body);

    private static Step Sb(Action<P> body) => new('b', body);

    public IEnumerable<Probe> Probes()
    {
        // Fixtures ---------------------------------------------------------------------------------------------
        yield return Creation("main-accdb", "accdb", Dao.Version120, "the main fixture", MainBody);
        yield return Creation("main-mdb", "mdb", Dao.Version40, "the main fixture", MainBody);
        yield return Creation("second-accdb", "accdb", Dao.Version120, "the second-file fixture of the multi-file probe",
            p => p.Call("db", "Execute", A.Str("CREATE TABLE T2 (ID LONG CONSTRAINT PK_T2 PRIMARY KEY, N LONG, V LONG)"))
                .Call("db", "Execute", A.Str("INSERT INTO T2 VALUES (1, 100, 0)")).Call("db", "Execute", A.Str("INSERT INTO T2 VALUES (2, 200, 0)"))
                .Call("db", "Execute", A.Str("INSERT INTO T2 VALUES (3, 300, 0)")).Call("db", "Execute", A.Str("INSERT INTO T2 VALUES (4, 400, 0)")));

        // R-LOCK-3 -----------------------------------------------------------------------------------------------
        yield return Run("read-role-edit-then-update-new-reader", Ids(R3, "READ-ROLE"),
            "Process a holds a dynaset on row 1 of T opened with dbPessimistic and calls Edit (no Update). A newly arriving process b reads rows 1, 2 and 64 through a new snapshot, dynaset, forward-only recordset and table-type Seek, each result and error recorded. Then a cancels, begins a transaction, edits and Updates row 1 without committing, and b reads the same three rows again with new cursors; finally a rolls back and b reads again. The kept file images are taken in a at each point.",
            [
                Sa(p =>
                {
                    p.OpenRs("ra", "db", "SELECT * FROM T WHERE ID = 1", Dao.OpenDynaset, A.I4(0), A.I4(Pessimistic));
                    Hold(p, "ra", "a", "N");
                    p.Call("ra", "Edit").Get("ra", "EditMode").Files("a-edit-pending", true);
                }),
                Sb(p => ReadRows(p, "b1")),
                Sa(p => p.Call("ra", "CancelUpdate").Call("ws", "BeginTrans").Call("ra", "Edit").Put("aN", "Value", A.I4(7777)).Call("ra", "Update")
                    .Get("ra", "EditMode").Files("a-updated-uncommitted", true)),
                Sb(p => ReadRows(p, "b2")),
                Sa(p => p.Call("ws", "Rollback").Files("a-rolled-back", true)),
                Sb(p => ReadRows(p, "b3")),
            ], closeA: ["ra", "db"]);

        yield return Run("read-role-edit-existing-readers", Ids(R3, "READ-ROLE", "READ-REFRESH"),
            "Process b opens a snapshot, dynaset, forward-only and table-type (index PK_T) cursor over rows 1, 2 and 64 and reads the first row of each. Process a then Edits row 1 through a dbPessimistic dynaset; b re-reads each held cursor (Move 0, read, Transactions) and also opens new cursors. Then a assigns N and Updates; b re-reads the held cursors, then Requery and reads, then new cursors. Existing readers and a later new reader are thereby separate observations of the same Edit/Update.",
            [
                Sb(OpenHeld),
                Sa(p =>
                {
                    p.OpenRs("ra", "db", "SELECT * FROM T WHERE ID = 1", Dao.OpenDynaset, A.I4(0), A.I4(Pessimistic));
                    Hold(p, "ra", "a", "N");
                    p.Call("ra", "Edit").Get("ra", "EditMode");
                }),
                Sb(p =>
                {
                    LookHeld(p);
                    ReadRows(p, "n1");
                }),
                Sa(p => p.Put("aN", "Value", A.I4(5555)).Call("ra", "Update").Files("a-updated", true)),
                Sb(p =>
                {
                    LookHeld(p);
                    foreach (string h in Held)
                    {
                        p.Call(h, "Requery").ReadRow(h);
                    }
                    ReadRows(p, "n2");
                }),
            ], closeA: ["ra", "db"], closeB: [.. Held, "db"]);

        Step[] ladder = [Sa(p => { OpenLadder(p); LookLadder(p); })];
        Action<P>[] techniques =
        [
            _ => { },
            p => p.Call("eng", "Idle", A.I4(RefreshCache)),
            p => { foreach (string c in Ladder) { p.Call(c, "Move", A.I4(0)); } },
            p => { foreach (string c in Ladder) { p.Call(c, "Requery"); } },
            p => { foreach (string c in Ladder) { p.Call(c, "MoveFirst"); } },
        ];
        for (int k = 0; k < techniques.Length; k++)
        {
            int value = 1001 + k;
            Action<P> technique = techniques[k];
            ladder = [.. ladder, Sb(p => Exec(p, $"UPDATE T SET N = {value} WHERE ID = 1", errors: false)), Sa(p => { technique(p); LookLadder(p); })];
        }
        ladder = [.. ladder, Sa(p => Rows(p, "db", "reopen", Sel4, Dao.OpenDynaset, 4, 4))];
        yield return Run("read-refresh-ladder", Ids(R3, "READ-REFRESH"),
            "Process a holds a dynaset, a snapshot and a table-type recordset (index PK_T) positioned on row 1 and reads each. Process b then changes N of row 1 (to 1001, 1002, ... in five successive Execute statements); after each change a applies one technique and reads row 1 through the three held cursors: no call at all, DBEngine.Idle(dbRefreshCache), Move 0, Requery, MoveFirst, in that order. Finally a opens a new dynaset and reads. Member and value are read together; every call result and error is recorded.",
            ladder, closeA: ["rd", "rs", "rt", "db"]);

        yield return Run("read-refresh-elapsed", Ids(R3, "READ-REFRESH"),
            "Process a holds a dynaset and a table-type recordset on row 1 and reads them. Process b changes N of row 1. Process a reads without any move, then waits 6.5 seconds (an Await on a name nobody signals, so the recorded timeout is the intended delay, longer than a 5-second page timeout), reads again without a move, then Move 0 and reads. Asks whether elapsed time alone changes what the held cursors return.",
            [
                Sa(p => { OpenLadder(p); LookLadder(p); }),
                Sb(p => Exec(p, "UPDATE T SET N = 2001 WHERE ID = 1", errors: false)),
                Sa(p =>
                {
                    LookLadder(p);
                    p.Await("never-signalled", 6500);
                    LookLadder(p);
                    foreach (string c in Ladder)
                    {
                        p.Call(c, "Move", A.I4(0));
                    }
                    LookLadder(p);
                }),
            ], closeA: ["rd", "rs", "rt", "db"]);

        yield return Run("read-path-index-moved", Ids(R3, "READ-PATH", "READ-REFRESH"),
            "Process a walks a table-type recordset on T by index IX_N and has read the first two rows. Process b then changes N of row 3 to a value beyond the last key, of row 62 to a value before the first and of row 64 to a value between the two keys already visited. Process a continues MoveNext to EOF reading ID, N, V, then MoveFirst and re-reads, then Seeks to the two moved keys. Asks which rows the continued walk returns, in what order, with what errors.",
            [
                Sa(p =>
                {
                    p.OpenRs("rt", "db", "T", Dao.OpenTable).Put("rt", "Index", A.Str("IX_N")).Call("rt", "MoveFirst").ReadRow("rt", 4).Call("rt", "MoveNext").ReadRow("rt", 4);
                }),
                Sb(p =>
                {
                    Exec(p, "UPDATE T SET N = 100000 WHERE ID = 3");
                    Exec(p, "UPDATE T SET N = 5 WHERE ID = 62");
                    Exec(p, "UPDATE T SET N = 15 WHERE ID = 64");
                    p.Files("b-after", true);
                }),
                Sa(p =>
                {
                    p.ReadAll("rt", 70, 4).Call("rt", "MoveFirst").ReadRow("rt", 4).Call("rt", "MoveNext").ReadRow("rt", 4).Call("rt", "MoveNext").ReadRow("rt", 4)
                        .Call("rt", "Seek", A.Str("="), A.I4(15)).Get("rt", "NoMatch").ReadRow("rt", 4)
                        .Call("rt", "Seek", A.Str("="), A.I4(100000)).Get("rt", "NoMatch").ReadRow("rt", 4);
                }),
            ], closeA: ["rt", "db"]);

        string longA = new('p', 9000), longB = new('d', 7000);
        yield return Run("read-path-lv-replace", Ids(R3, "READ-PATH", "READ-SNAPSHOT"),
            "Process a holds a snapshot and a dynaset over table L (ID, MEMO) and has read row 1 of each. Process b replaces the memo of row 2 by a 9,000-character value, replaces the long memo of row 4 by a short value, deletes row 3 and reinserts it with a 7,000-character memo. Process a continues to the end of each cursor reading the memos, MoveFirst and reads again, then Requery and reads. Asks which memo values, row membership and errors the held cursors return.",
            [
                Sa(p =>
                {
                    p.OpenRs("rs", "db", "SELECT ID, M FROM L ORDER BY ID", Dao.OpenSnapshot).ReadRow("rs");
                    p.OpenRs("rd", "db", "SELECT ID, M FROM L ORDER BY ID", Dao.OpenDynaset).ReadRow("rd");
                }),
                Sb(p =>
                {
                    Exec(p, $"UPDATE L SET M = '{longA}' WHERE ID = 2");
                    Exec(p, "UPDATE L SET M = 'short again' WHERE ID = 4");
                    Exec(p, "DELETE FROM L WHERE ID = 3");
                    Exec(p, $"INSERT INTO L (ID, M) VALUES (3, '{longB}')");
                    p.Files("b-after", true);
                }),
                Sa(p =>
                {
                    foreach (string c in new[] { "rs", "rd" })
                    {
                        p.ReadAll(c, 8, 2).Call(c, "MoveFirst").ReadAll(c, 8, 2);
                    }
                    p.Call("rd", "Requery").ReadAll("rd", 8, 2);
                }),
            ], closeA: ["rs", "rd", "db"]);

        yield return Run("read-id-delete-reinsert", Ids(R3, "READ-ID"),
            "Process a holds a dynaset over rows 1-8 positioned on row 5 and a clone positioned on row 5 by FindFirst, and reads fields, Bookmark, BOF, EOF and EditMode of both. Process b deletes row 5 and reinserts a row with the identical key and values; a reads the same properties of both, then Move 0 on both. Process b then deletes it again and reinserts it with a different N, V and S; a reads again, then Requery on the dynaset and reads. Asks what the retained cursors and their bookmarks report after delete/reinsert of one key.",
            [
                Sa(p =>
                {
                    p.OpenRs("rd", "db", Sel8, Dao.OpenDynaset);
                    for (int i = 0; i < 4; i++)
                    {
                        p.Call("rd", "MoveNext");
                    }
                    p.CallBind("cl", "rd", "Clone").Call("cl", "FindFirst", A.Str("ID = 5"));
                    ObserveId(p);
                }),
                Sb(p =>
                {
                    Exec(p, "DELETE FROM T WHERE ID = 5");
                    Exec(p, InsertT(5, 50, 0, Pad));
                }),
                Sa(p =>
                {
                    ObserveId(p);
                    p.Call("rd", "Move", A.I4(0)).ReadRow("rd").Call("cl", "Move", A.I4(0)).ReadRow("cl").Get("rd", "Bookmark").Get("cl", "Bookmark");
                }),
                Sb(p =>
                {
                    Exec(p, "DELETE FROM T WHERE ID = 5");
                    Exec(p, InsertT(5, 51, 9, "changed"));
                    p.Files("b-after", true);
                }),
                Sa(p =>
                {
                    ObserveId(p);
                    p.Call("rd", "Requery").ReadRow("rd").Get("rd", "Bookmark");
                }),
            ], closeA: ["cl", "rd", "db"]);

        yield return Run("read-schema-held-descriptors", Ids(R3, "READ-SCHEMA", R4, "WRITE-DDL"),
            "Process a holds the TableDef, Fields, Indexes and Properties collections of table U, the saved QueryDef QT with its Fields, and an open dynaset on U and a snapshot from QT, and records name, count and SQL through them. Process b, while those cursors are open, adds a column and an index to U; a records the held descriptors again and closes both cursors. Process b then drops the index, adds and drops columns, adds a Description property to the TableDef, changes QT's SQL, drops U and creates a different U with one row; a records the held descriptors, calls TableDefs.Refresh and records again, then reopens U, runs the held QueryDef and reads QT's SQL through a new QueryDefs binding.",
            [
                Sa(p =>
                {
                    p.GetBind("tdfs", "db", "TableDefs").Item("tdf", "tdfs", A.Str("U")).GetBind("tfl", "tdf", "Fields").GetBind("tix", "tdf", "Indexes")
                        .GetBind("tpr", "tdf", "Properties").GetBind("qdfs", "db", "QueryDefs").Item("qd", "qdfs", A.Str("QT")).GetBind("qfl", "qd", "Fields");
                    p.OpenRs("ru", "db", "SELECT ID, N, W FROM U ORDER BY ID", Dao.OpenDynaset).ReadRow("ru");
                    p.CallBind("rq", "qd", "OpenRecordset", A.I4(Dao.OpenSnapshot)).ReadRow("rq");
                    Descr(p);
                }),
                Sb(p =>
                {
                    Exec(p, "ALTER TABLE U ADD COLUMN Z1 LONG");
                    Exec(p, "CREATE INDEX IX_U1 ON U (N)");
                }),
                Sa(p =>
                {
                    Descr(p);
                    p.Call("ru", "Move", A.I4(0)).ReadRow("ru").Call("rq", "Move", A.I4(0)).ReadRow("rq").Call("ru", "Close").Call("rq", "Close");
                }),
                Sb(p =>
                {
                    Exec(p, "DROP INDEX IX_U1 ON U");
                    Exec(p, "ALTER TABLE U ADD COLUMN Z2 LONG");
                    Exec(p, "ALTER TABLE U DROP COLUMN W");
                    p.GetBind("btdfs", "db", "TableDefs").Item("btdf", "btdfs", A.Str("U"))
                        .CallBind("bprop", "btdf", "CreateProperty", A.Str("Description"), A.I4(10), A.Str("changed by b"))
                        .GetBind("bprops", "btdf", "Properties").Call("bprops", "Append", A.Obj("bprop"))
                        .GetBind("bqdfs", "db", "QueryDefs").Item("bqd", "bqdfs", A.Str("QT")).Put("bqd", "SQL", A.Str("SELECT ID, N, V FROM T WHERE ID <= 3 ORDER BY ID;"))
                        .Errors();
                    Exec(p, "DROP TABLE U");
                    Exec(p, "CREATE TABLE U (ID TEXT(10) CONSTRAINT PK_U PRIMARY KEY, Q DOUBLE)");
                    Exec(p, "INSERT INTO U VALUES ('x', 1.5)");
                }),
                Sa(p =>
                {
                    Descr(p);
                    p.Call("tdfs", "Refresh");
                    Descr(p);
                    Rows(p, "db", "ru2", "SELECT * FROM U", Dao.OpenSnapshot, 4, 4);
                    p.CallBind("rq2", "qd", "OpenRecordset", A.I4(Dao.OpenSnapshot)).ReadAll("rq2", 4, 4).Call("rq2", "Close")
                        .GetBind("nqdfs", "db", "QueryDefs").Item("nqd", "nqdfs", A.Str("QT")).Get("nqd", "SQL");
                }),
            ]);

        yield return Run("read-schema-index-held-cursor", Ids(R3, "READ-SCHEMA"),
            "Process a holds a table-type recordset on T with Index IX_N and has read the first row. Process b drops IX_N and creates IX_V on V while a holds the cursor; a reads Index, MoveNext, Seek on the old index, sets Index IX_V, MoveFirst and reads, then closes the cursor. Process b then repeats the drop (and creates IX_V2) with no cursor held; a reopens a table-type recordset and sets Index IX_N, IX_V and IX_V2 in turn, reading the first row. Asks how an index-root change reaches a held and a fresh cursor.",
            [
                Sa(p => p.OpenRs("rt", "db", "T", Dao.OpenTable).Put("rt", "Index", A.Str("IX_N")).Call("rt", "MoveFirst").ReadRow("rt", 4).Get("rt", "Index")),
                Sb(p =>
                {
                    Exec(p, "DROP INDEX IX_N ON T");
                    Exec(p, "CREATE INDEX IX_V ON T (V)");
                }),
                Sa(p =>
                {
                    p.Get("rt", "Index").Call("rt", "MoveNext").ReadRow("rt", 4).Call("rt", "Seek", A.Str("="), A.I4(30)).Get("rt", "NoMatch")
                        .Put("rt", "Index", A.Str("IX_V")).Get("rt", "Index").Call("rt", "MoveFirst").ReadRow("rt", 4).Call("rt", "Close").Release("rt");
                }),
                Sb(p =>
                {
                    Exec(p, "DROP INDEX IX_N ON T");
                    Exec(p, "CREATE INDEX IX_V2 ON T (V DESC)");
                }),
                Sa(p =>
                {
                    p.OpenRs("rt2", "db", "T", Dao.OpenTable);
                    foreach (string index in new[] { "IX_N", "IX_V", "IX_V2" })
                    {
                        p.Put("rt2", "Index", A.Str(index)).Get("rt2", "Index").Call("rt2", "MoveFirst").ReadRow("rt2", 4);
                    }
                }),
            ], closeA: ["rt2", "db"]);

        yield return Run("read-snapshot-cursor-kinds", Ids(R3, "READ-SNAPSHOT", "READ-REFRESH"),
            "Process a opens a snapshot, a dynaset, a forward-only recordset (each over rows 1-8) and a table-type recordset (PK_T), reads rows 1 and 2 of each (visited) and leaves each positioned on row 3 (unread). Process b updates N of row 1 (visited), row 3 (current) and row 6 (unread), deletes row 4, and inserts rows 0 and 65. Process a reads the current row and then every remaining row of each cursor, then MoveFirst and reads again, then Requery of the three query cursors and reads, then a new snapshot. Visited versus unread, member versus value, and cursor kind are read separately.",
            [
                Sa(p =>
                {
                    p.OpenRs("cs", "db", Sel8, Dao.OpenSnapshot).OpenRs("cd", "db", Sel8, Dao.OpenDynaset).OpenRs("cf", "db", Sel8, Dao.OpenForwardOnly)
                        .OpenRs("ct", "db", "T", Dao.OpenTable).Put("ct", "Index", A.Str("PK_T")).Call("ct", "MoveFirst");
                    foreach (string c in Kinds)
                    {
                        p.ReadRow(c, 4).Call(c, "MoveNext").ReadRow(c, 4).Call(c, "MoveNext");
                    }
                }),
                Sb(p =>
                {
                    Exec(p, "UPDATE T SET N = -1 WHERE ID = 1");
                    Exec(p, "UPDATE T SET N = -3 WHERE ID = 3");
                    Exec(p, "UPDATE T SET N = -6 WHERE ID = 6");
                    Exec(p, "DELETE FROM T WHERE ID = 4");
                    Exec(p, InsertT(0, 0, 0, Pad));
                    Exec(p, InsertT(65, 650, 0, Pad));
                    p.Files("b-after", true);
                }),
                Sa(p =>
                {
                    foreach (string c in Kinds)
                    {
                        p.ReadRow(c, 4).ReadAll(c, 12, 4).Call(c, "MoveFirst").ReadAll(c, 12, 4);
                    }
                    foreach (string c in new[] { "cs", "cd", "cf" })
                    {
                        p.Call(c, "Requery").ReadAll(c, 12, 4);
                    }
                    Rows(p, "db", "fresh", Sel8, Dao.OpenSnapshot, 12, 4);
                }),
            ], closeA: ["cs", "cd", "cf", "ct", "db"]);

        // R-LOCK-4: WRITE-TIMING -----------------------------------------------------------------------------------
        const string Attempts = "UPDATE T SET V = V + 1 WHERE ID = ";
        yield return Run("write-timing-pessimistic-matrix", Ids(R4, "WRITE-TIMING", R3, "READ-ROLE"),
            "Process a holds a dbPessimistic dynaset on row 1, Edits and assigns N (no Update). Process b then tries, one at a time with errors recorded: an UPDATE statement on row 1, on row 2, on row 64, on row 1 of table U, an INSERT of row 200, and a dbPessimistic dynaset Edit/assign/Update on row 2 and on row 64. Process a then Updates and b reads rows 1, 2 and 64. Asks which of b's operations are refused or delayed while a's Edit is retained, and how a's Update ends.",
            [
                Sa(p =>
                {
                    p.OpenRs("ra", "db", "SELECT * FROM T WHERE ID = 1", Dao.OpenDynaset, A.I4(0), A.I4(Pessimistic));
                    Hold(p, "ra", "a", "N");
                    p.Call("ra", "Edit").Put("aN", "Value", A.I4(9001)).Get("ra", "EditMode").Files("a-edit-pending", true);
                }),
                Sb(p =>
                {
                    Exec(p, Attempts + "1");
                    Exec(p, Attempts + "2");
                    Exec(p, Attempts + "64");
                    Exec(p, "UPDATE U SET N = N + 1 WHERE ID = 1");
                    Exec(p, InsertT(200, 2000, 0, Pad));
                    foreach (int id in new[] { 2, 64 })
                    {
                        string rb = "rb" + id;
                        p.OpenRs(rb, "db", $"SELECT * FROM T WHERE ID = {id}", Dao.OpenDynaset, A.I4(0), A.I4(Pessimistic));
                        Hold(p, rb, rb, "N");
                        p.Call(rb, "Edit").Put(rb + "N", "Value", A.I4(8000 + id)).Call(rb, "Update").Errors().Call(rb, "Close");
                    }
                }),
                Sa(p => p.Call("ra", "Update").Errors().Get("ra", "EditMode").Files("a-after-update", true)),
                Sb(p => Rows(p, "db", "final", Sel3, Dao.OpenSnapshot, 4, 4)),
            ], closeA: ["ra", "db"]);

        yield return Run("write-timing-optimistic-seechanges", Ids(R4, "WRITE-TIMING", "WRITE-ORIGINAL"),
            "Process a opens a dynaset on row 1 with options dbSeeChanges and lock type dbOptimistic, Edits and assigns N (no Update). Process b then, with errors recorded, updates row 2, row 64 and row 1 of table U by statements, inserts row 200, and edits row 2 through its own dbOptimistic dynaset (Edit/assign/Update). Process a then Updates and reads; b reads rows 1, 2 and 64. Asks which operations are admitted while an optimistic Edit is pending and what a's Update returns.",
            [
                Sa(p =>
                {
                    p.OpenRs("ra", "db", "SELECT * FROM T WHERE ID = 1", Dao.OpenDynaset, A.I4(Dao.SeeChanges), A.I4(Optimistic));
                    Hold(p, "ra", "a", "N");
                    p.Call("ra", "Edit").Put("aN", "Value", A.I4(9002)).Get("ra", "EditMode");
                }),
                Sb(p =>
                {
                    Exec(p, Attempts + "2");
                    Exec(p, Attempts + "64");
                    Exec(p, "UPDATE U SET N = N + 1 WHERE ID = 1");
                    Exec(p, InsertT(200, 2000, 0, Pad));
                    p.OpenRs("rb", "db", "SELECT * FROM T WHERE ID = 2", Dao.OpenDynaset, A.I4(0), A.I4(Optimistic));
                    Hold(p, "rb", "b", "N");
                    p.Call("rb", "Edit").Put("bN", "Value", A.I4(8002)).Call("rb", "Update").Errors().Call("rb", "Close");
                }),
                Sa(p => p.Call("ra", "Update").Errors().Get("ra", "EditMode").ReadRow("ra").Files("a-after-update", true)),
                Sb(p => Rows(p, "db", "final", Sel3, Dao.OpenSnapshot, 4, 4)),
            ], closeA: ["ra", "db"]);

        yield return Run("write-timing-release-boundaries", Ids(R4, "WRITE-TIMING", R3, "READ-LIFE"),
            "Process a holds a dbPessimistic Edit on row 1 and passes through six states: (0) Edit pending; (1) CancelUpdate; (2) Edit, assign and Update; (3) Edit then Recordset.Close; (4) a new Edit, then the Recordset is released without Close; (5) a new Edit, then Database.Close with the recordset still open. After each state process b executes UPDATE T SET V = V + 1 WHERE ID = 1 (errors and RecordsAffected recorded) and reads row 1. Asks at which state b's write on the held row is admitted.",
            [
                Sa(p =>
                {
                    p.OpenRs("ra", "db", "SELECT * FROM T WHERE ID = 1", Dao.OpenDynaset, A.I4(0), A.I4(Pessimistic));
                    Hold(p, "ra", "a", "N");
                    p.Call("ra", "Edit").Get("ra", "EditMode");
                }),
                Sb(TryRow1),
                Sa(p => p.Call("ra", "CancelUpdate").Get("ra", "EditMode")),
                Sb(TryRow1),
                Sa(p => p.Call("ra", "Edit").Put("aN", "Value", A.I4(9003)).Call("ra", "Update").Get("ra", "EditMode")),
                Sb(TryRow1),
                Sa(p => p.Call("ra", "Edit").Call("ra", "Close")),
                Sb(TryRow1),
                Sa(p =>
                {
                    p.OpenRs("rb", "db", "SELECT * FROM T WHERE ID = 1", Dao.OpenDynaset, A.I4(0), A.I4(Pessimistic)).Call("rb", "Edit").Release("rb");
                }),
                Sb(TryRow1),
                Sa(p =>
                {
                    p.OpenRs("rc", "db", "SELECT * FROM T WHERE ID = 1", Dao.OpenDynaset, A.I4(0), A.I4(Pessimistic)).Call("rc", "Edit").Call("db", "Close").Release("rc");
                }),
                Sb(TryRow1),
            ], closeA: []);

        foreach ((string mode, string text) in new[] { ("0", "page"), ("1", "row") })
        {
            yield return Run($"write-timing-ado-lock-mode-{text}", Ids(R4, "WRITE-TIMING", "WRITE-PROMOTE", "RQ-ADO-01"),
                $"Process a is the first opener: an ADODB.Connection (Provider Microsoft.ACE.OLEDB.12.0, Data Source set through the Properties collection) whose connection string carries 'Jet OLEDB:Database Locking Mode={mode}'; the requested and the reported property values are read after Open. Through an adOpenKeyset, adLockPessimistic ADO recordset on row 1 it assigns N (starting an edit, no Update). Process b, a DAO process opened afterwards, tries with errors recorded: UPDATE statements on row 1, row 2, row 64, and an INSERT of row 200. Process a then calls CancelUpdate and closes. Asks how the first opener's locking mode changes what a pending pessimistic edit blocks.",
                [
                    Sa(p =>
                    {
                        p.Create("ADODB.Recordset", "ars").Call("ars", "Open", A.Str("SELECT * FROM T WHERE ID = 1"), A.Obj("cn"), A.I4(1), A.I4(2), A.I4(1))
                            .GetBind("afl", "ars", "Fields").Item("aN", "afl", A.Str("N")).Put("aN", "Value", A.I4(9004)).Get("ars", "EditMode").Files("a-edit-pending", true);
                    }),
                    Sb(p =>
                    {
                        Exec(p, Attempts + "1");
                        Exec(p, Attempts + "2");
                        Exec(p, Attempts + "64");
                        Exec(p, InsertT(200, 2000, 0, Pad));
                    }),
                    Sa(p => p.Call("ars", "CancelUpdate").Call("ars", "Close")),
                ], openA: p =>
                {
                    p.Files("before-open").Modules("before-create").Create("ADODB.Connection", "cn").Modules("after-create")
                        .Put("cn", "ConnectionString", A.Str($"Provider=Microsoft.ACE.OLEDB.12.0;Jet OLEDB:Database Locking Mode={mode}"))
                        .GetBind("cprops", "cn", "Properties").Item("ds", "cprops", A.Str("Data Source")).Put("ds", "Value", A.In()).Release("ds")
                        .Call("cn", "Open").Get("cn", "Version").Item("lm", "cprops", A.Str("Jet OLEDB:Database Locking Mode")).Get("lm", "Value");
                }, closeA: ["cn"], daoA: false);
        }

        // R-LOCK-4: WRITE-ORIGINAL ---------------------------------------------------------------------------------
        yield return WriteOriginal("write-original-same-row", FxAccdb, "WRITE-ORIGINAL",
            "On the ACCDB fixture, process b changes N of the same row (a different value) by a statement",
            [p => Exec(p, "UPDATE T SET N = 222 WHERE ID = 1")]);
        yield return WriteOriginal("write-original-same-row-mdb", FxMdb, "WRITE-ORIGINAL",
            "On the Jet 4 MDB fixture, process b changes N of the same row (a different value) by a statement",
            [p => Exec(p, "UPDATE T SET N = 222 WHERE ID = 1")]);
        yield return WriteOriginal("write-original-unconflicting", FxAccdb, "WRITE-ORIGINAL",
            "On the ACCDB fixture, in two phases, process b changes only V of the same row (phase 1) and then only row 2 (phase 2, N of row 2 changed too)",
            [p => Exec(p, "UPDATE T SET V = 77 WHERE ID = 1"), p => Exec(p, "UPDATE T SET N = 25, V = 78 WHERE ID = 2")]);
        yield return WriteOriginal("write-original-changed-then-restored", FxAccdb, "WRITE-ORIGINAL",
            "On the ACCDB fixture, process b changes N of the same row and then, by a second statement, restores the original value",
            [p => { Exec(p, "UPDATE T SET N = 222 WHERE ID = 1"); Exec(p, "UPDATE T SET N = 10 WHERE ID = 1"); }]);

        // R-LOCK-4: WRITE-LV ------------------------------------------------------------------------------------------
        string[] lv = [new('q', 9000), new('r', 9000), new('s', 9000), new('t', 9000), new('u', 9000), new('v', 9000)];
        yield return Run("write-lv-replace-reuse", Ids(R4, "WRITE-LV"),
            "Process a holds a dynaset on table L positioned on row 4 (a long memo). Process b replaces that memo twice, keeping the file image after each; process a reads its row and then closes the Recordset and the Database (b is then the only opener); b replaces the memo again, appends a new row with a 9,000-character memo, sets DBEngine.SetOption(dbRecycleLVs, 1) in its own process, replaces the memo again and appends one more row, keeping the image after each. Asks how the file images and sizes differ across participant count and the recycle setting.",
            [
                Sa(p => p.OpenRs("rd", "db", "SELECT ID, M FROM L ORDER BY ID", Dao.OpenDynaset).Call("rd", "MoveLast").ReadRow("rd")),
                Sb(p =>
                {
                    Exec(p, $"UPDATE L SET M = '{lv[0]}' WHERE ID = 4");
                    p.Files("b-lv-1", true);
                    Exec(p, $"UPDATE L SET M = '{lv[1]}' WHERE ID = 4");
                    p.Files("b-lv-2", true);
                }),
                Sa(p => p.ReadRow("rd").Call("rd", "Close").Call("db", "Close").Release("db")),
                Sb(p =>
                {
                    Exec(p, $"UPDATE L SET M = '{lv[2]}' WHERE ID = 4");
                    p.Files("b-lv-3", true);
                    Exec(p, $"INSERT INTO L (ID, M) VALUES (5, '{lv[3]}')");
                    p.Files("b-lv-4", true).Call("eng", "SetOption", A.I4(OptRecycleLvs), A.I4(1));
                    Exec(p, $"UPDATE L SET M = '{lv[4]}' WHERE ID = 4");
                    p.Files("b-lv-5", true);
                    Exec(p, $"INSERT INTO L (ID, M) VALUES (6, '{lv[5]}')");
                    p.Files("b-lv-6", true);
                }),
            ], closeA: []);

        // R-LOCK-4: WRITE-ALLOC, WRITE-TREE ---------------------------------------------------------------------------
        yield return Run("write-alloc-insert-txn", Ids(R4, "WRITE-ALLOC", "WRITE-TREE", "TXN-RANGE"),
            "Phase 1: process a begins a transaction and inserts row 201 (N 2010); process b (no transaction) inserts rows 202 (N 2020) and 203 (N 11, inside the low key range); a inserts row 204 and keeps the file image, then rolls back; b reads the row count and rows above 200 and inserts row 201 again; a reads the same. Phase 2: a begins, inserts 211; b inserts 212 and 213; a commits; b and a read. Asks which inserts are admitted beside another process's uncommitted insert and what survives each resolution.",
            [
                Sa(p =>
                {
                    p.Call("ws", "BeginTrans");
                    Exec(p, InsertT(201, 2010, 0, Pad));
                }),
                Sb(p =>
                {
                    Exec(p, InsertT(202, 2020, 0, Pad));
                    Exec(p, InsertT(203, 11, 0, Pad));
                }),
                Sa(p =>
                {
                    Exec(p, InsertT(204, 2040, 0, Pad));
                    p.Files("a-before-rollback", true).Call("ws", "Rollback").Errors();
                }),
                Sb(p =>
                {
                    ReadHigh(p, "b1");
                    Exec(p, InsertT(201, 2011, 0, Pad));
                }),
                Sa(p =>
                {
                    ReadHigh(p, "a1");
                    p.Call("ws", "BeginTrans");
                    Exec(p, InsertT(211, 2110, 0, Pad));
                }),
                Sb(p =>
                {
                    Exec(p, InsertT(212, 2120, 0, Pad));
                    Exec(p, InsertT(213, 2130, 0, Pad));
                }),
                Sa(p => p.Call("ws", "CommitTrans").Errors().Files("a-after-commit", true)),
                Sb(p => ReadHigh(p, "b2")),
                Sa(p => ReadHigh(p, "a2")),
            ]);

        yield return Run("write-tree-unique-candidate", Ids(R4, "WRITE-TREE", "TXN-RANGE"),
            "Phase 1: process a begins a transaction and inserts key 300; process b (no transaction) inserts key 300 and then key 301, errors recorded; a rolls back; b inserts key 300 again and reads keys of 300 and above. Phase 2: a begins and inserts key 310; b inserts key 310 and key 311; a commits; b inserts key 310 again and reads. Asks how a concurrent uniqueness candidate on the primary key and on index IX_N is resolved against an uncommitted and then a committed insert.",
            [
                Sa(p =>
                {
                    p.Call("ws", "BeginTrans");
                    Exec(p, InsertT(300, 3000, 0, Pad));
                }),
                Sb(p =>
                {
                    Exec(p, InsertT(300, 3000, 0, Pad));
                    Exec(p, InsertT(301, 3010, 0, Pad));
                }),
                Sa(p => p.Call("ws", "Rollback").Errors()),
                Sb(p =>
                {
                    Exec(p, InsertT(300, 3000, 0, Pad));
                    Rows(p, "db", "b1", "SELECT ID, N FROM T WHERE ID >= 300 ORDER BY ID", Dao.OpenSnapshot, 8, 4);
                }),
                Sa(p =>
                {
                    p.Call("ws", "BeginTrans");
                    Exec(p, InsertT(310, 3100, 0, Pad));
                }),
                Sb(p =>
                {
                    Exec(p, InsertT(310, 3100, 0, Pad));
                    Exec(p, InsertT(311, 3110, 0, Pad));
                }),
                Sa(p => p.Call("ws", "CommitTrans").Errors()),
                Sb(p =>
                {
                    Exec(p, InsertT(310, 3100, 0, Pad));
                    Rows(p, "db", "b2", "SELECT ID, N FROM T WHERE ID >= 300 ORDER BY ID", Dao.OpenSnapshot, 8, 4);
                }),
            ]);

        yield return Run("write-alloc-counter-rollback", Ids(R4, "WRITE-ALLOC", Tx, "TXN-DDL"),
            "Process a begins a transaction and appends a row to table C (AutoNumber ID) through a recordset AddNew/Update, reading ID after AddNew and after Update; process b (no transaction) appends a row the same way; a rolls back; b appends another; a appends another after its rollback. Asks which AutoNumber values each process is given and how the generated-value sequence continues after a rolled-back append beside another process's append.",
            [
                Sa(p =>
                {
                    p.Call("ws", "BeginTrans");
                    AddC(p, "a1", "a-in-txn");
                }),
                Sb(p => AddC(p, "b1", "b-implicit")),
                Sa(p => p.Call("ws", "Rollback").Errors()),
                Sb(p =>
                {
                    AddC(p, "b2", "b-after-rollback");
                    Rows(p, "db", "bc", "SELECT ID, V FROM C ORDER BY ID", Dao.OpenSnapshot, 12, 4);
                }),
                Sa(p =>
                {
                    AddC(p, "a2", "a-after-rollback");
                    Rows(p, "db", "ac", "SELECT ID, V FROM C ORDER BY ID", Dao.OpenSnapshot, 12, 4);
                }),
            ]);

        // R-LOCK-4: WRITE-DDL ------------------------------------------------------------------------------------------
        yield return Run("write-ddl-vs-pending-edit", Ids(R4, "WRITE-DDL", "WRITE-TIMING"),
            "Process a holds a dbPessimistic Edit on a row of table U. Process b, with errors recorded, tries ALTER TABLE U ADD COLUMN, CREATE INDEX and DROP TABLE U. Process a then calls CancelUpdate (recordset still open) and b tries ADD COLUMN and CREATE INDEX with new names; a closes the recordset and b tries ADD COLUMN, CREATE INDEX and DROP TABLE U again, then reads the field names. Asks what a row edit lock does and does not protect against DDL from another process.",
            [
                Sa(p =>
                {
                    p.OpenRs("ra", "db", "SELECT * FROM U WHERE ID = 1", Dao.OpenDynaset, A.I4(0), A.I4(Pessimistic));
                    Hold(p, "ra", "a", "N");
                    p.Call("ra", "Edit").Put("aN", "Value", A.I4(99)).Get("ra", "EditMode");
                }),
                Sb(p =>
                {
                    Exec(p, "ALTER TABLE U ADD COLUMN Z1 LONG");
                    Exec(p, "CREATE INDEX IX_U1 ON U (W)");
                    Exec(p, "DROP TABLE U");
                }),
                Sa(p => p.Call("ra", "CancelUpdate").Get("ra", "EditMode")),
                Sb(p =>
                {
                    Exec(p, "ALTER TABLE U ADD COLUMN Z2 LONG");
                    Exec(p, "CREATE INDEX IX_U2 ON U (W)");
                }),
                Sa(p => p.Call("ra", "Close").Release("ra")),
                Sb(p =>
                {
                    Exec(p, "ALTER TABLE U ADD COLUMN Z3 LONG");
                    Exec(p, "CREATE INDEX IX_U3 ON U (W)");
                    Exec(p, "DROP TABLE U");
                    FieldNames(p, "U");
                }),
            ], closeA: ["db"]);

        yield return Run("write-ddl-vs-open-transaction", Ids(R4, "WRITE-DDL", Tx, "TXN-DDL"),
            "Process a begins a transaction, inserts a row into table U, adds a column and creates an index on U. Process b, with errors recorded, tries ALTER TABLE ADD COLUMN, CREATE INDEX and DROP TABLE on U, then reads U's field names (after a TableDefs and Fields Refresh) and rows. Process a rolls back and reads U's field names; b reads field names and rows again and tries ADD COLUMN once more. Asks how another process sees and is blocked by a transaction's catalog changes and what a rollback restores.",
            [
                Sa(p =>
                {
                    p.Call("ws", "BeginTrans");
                    Exec(p, "INSERT INTO U (ID, N, W) VALUES (10, 10, 'in-txn')");
                    Exec(p, "ALTER TABLE U ADD COLUMN Za LONG");
                    Exec(p, "CREATE INDEX IX_Ua ON U (N)");
                }),
                Sb(p =>
                {
                    Exec(p, "ALTER TABLE U ADD COLUMN Zb LONG");
                    Exec(p, "CREATE INDEX IX_Ub ON U (W)");
                    Exec(p, "DROP TABLE U");
                    FieldNames(p, "U");
                    Rows(p, "db", "b1", "SELECT * FROM U ORDER BY ID", Dao.OpenSnapshot, 8, 6);
                }),
                Sa(p =>
                {
                    p.Call("ws", "Rollback").Errors();
                    FieldNames(p, "U");
                }),
                Sb(p =>
                {
                    FieldNames(p, "U");
                    Rows(p, "db", "b2", "SELECT * FROM U ORDER BY ID", Dao.OpenSnapshot, 8, 6);
                    Exec(p, "ALTER TABLE U ADD COLUMN Zc LONG");
                }),
            ]);

        // R-LOCK-4: WRITE-PROMOTE ------------------------------------------------------------------------------------
        yield return Run("write-promote-bulk-update", Ids(R4, "WRITE-PROMOTE", Tx, "TXN-LOCAL"),
            "Phase 1 (engine defaults): process a begins a transaction and updates V of all 64 rows of T; process b tries UPDATE statements on row 64 and on row 1 of table U, an INSERT of row 400 and a count of T, errors recorded; a rolls back. Phase 2: process a sets DBEngine.SetOption(dbMaxLocksPerFile, 8) in its own process, begins and runs the same bulk update (its error and RecordsAffected are recorded), then process b repeats its attempts; a rolls back and reads SUM(V). Asks what lock footprint a many-row transaction leaves for another process, with and without a low lock-count limit.",
            [
                Sa(p =>
                {
                    p.Call("ws", "BeginTrans");
                    Exec(p, "UPDATE T SET V = V + 1");
                }),
                Sb(BulkAttempts),
                Sa(p =>
                {
                    p.Call("ws", "Rollback").Errors().Call("eng", "SetOption", A.I4(OptMaxLocksPerFile), A.I4(8)).Call("ws", "BeginTrans");
                    Exec(p, "UPDATE T SET V = V + 1");
                }),
                Sb(BulkAttempts),
                Sa(p =>
                {
                    p.Call("ws", "Rollback").Errors();
                    Rows(p, "db", "sum", "SELECT SUM(V) AS SumV, COUNT(*) AS Cnt FROM T", Dao.OpenSnapshot, 2, 4);
                }),
            ]);

        // R-LOCK-4: recordset deny options (WRITE-TIMING, WRITE-PROMOTE input dimension) -------------------------------
        yield return Run("write-deny-options", Ids(R4, "WRITE-TIMING", "RO-ACTUAL"),
            "Process a opens a table-type recordset on T with option dbDenyWrite and reads a row; process b tries to open a snapshot of row 1 and read it, UPDATE row 1, and open a table-type recordset. Process a closes it and b repeats. Then a opens the table with dbDenyRead and the same sequence runs, ending with a close and b's repeat. Asks which operations of another process each Recordset deny option refuses, at what point, and when the refusal ends.",
            DenySteps(), closeA: ["db"]);

        // R-TXN-1 cross-process: TXN-LOCAL ----------------------------------------------------------------------------
        const string UpdateA = "UPDATE T SET N = N + 1000 WHERE ID = 1";
        yield return Run("txn-local-xproc-same-row", Ids(Tx, "TXN-LOCAL", R4, "TXN-RANGE"),
            "Two live owners in two processes on one row. In each phase process a begins a transaction and updates N of row 1; process b begins its own and updates V of the same row (errors recorded, default engine options); then the transactions resolve in the given order (tokens: A/B owner, C commit, R rollback): phase 1 'A:C B:C', phase 2 'B:R A:C'. After each phase both processes read rows 1, 2 and 64 and row 1 of U.",
            TxnLocalSteps("UPDATE T SET V = V + 5 WHERE ID = 1", ["BE AC BC", "BE BR AC"], UpdateA));
        yield return Run("txn-local-xproc-same-page", Ids(Tx, "TXN-LOCAL", R4, "TXN-RANGE", "WRITE-REBASE"),
            "Two live owners on adjacent rows 1 and 2. In each phase process a begins a transaction and updates N of row 1; process b updates V of row 2 (errors recorded), explicitly in its own transaction (phases 1-4) or with no transaction (phases 5-6); the transactions resolve in the given order (A/B owner, C commit, R rollback): 'A:C B:C', 'B:C A:C', 'B:C A:R', 'A:R B:C', then b implicit with 'A:R' and 'A:C'. After each phase both processes read rows 1, 2 and 64 and row 1 of U.",
            TxnLocalSteps("UPDATE T SET V = V + 5 WHERE ID = 2", ["BE AC BC", "BE BC AC", "BE BC AR", "BE AR BC", "BI AR", "BI AC"], UpdateA));
        yield return Run("txn-local-xproc-same-page-mdb", Ids(Tx, "TXN-LOCAL", R4, "TXN-RANGE", "WRITE-REBASE"),
            "Jet 4 MDB repeat of txn-local-xproc-same-page: process a updates N of row 1 inside a transaction, process b updates V of row 2 (own transaction in phases 1-4, none in phases 5-6), resolution orders 'A:C B:C', 'B:C A:C', 'B:C A:R', 'A:R B:C', 'A:R' and 'A:C' with b implicit; both processes read rows 1, 2, 64 and row 1 of U after each phase.",
            TxnLocalSteps("UPDATE T SET V = V + 5 WHERE ID = 2", ["BE AC BC", "BE BC AC", "BE BC AR", "BE AR BC", "BI AR", "BI AC"], UpdateA), FxMdb);
        yield return Run("txn-local-xproc-far-row", Ids(Tx, "TXN-LOCAL", R4, "TXN-RANGE", "WRITE-REBASE"),
            "Two live owners on rows 1 and 64. Process a begins and updates N of row 1; process b begins and updates V of row 64 (errors recorded); phase 1 resolves 'B:C A:C', phase 2 'B:C A:R'; both processes read rows 1, 2, 64 and row 1 of U after each phase.",
            TxnLocalSteps("UPDATE T SET V = V + 5 WHERE ID = 64", ["BE BC AC", "BE BC AR"], UpdateA));
        yield return Run("txn-local-xproc-other-table", Ids(Tx, "TXN-LOCAL"),
            "Two live owners on different tables. Process a begins and updates N of row 1 of T; process b begins and updates N of row 1 of U (errors recorded); phase 1 resolves 'A:C B:C', phase 2 'B:C A:R'; both processes read rows 1, 2, 64 of T and row 1 of U after each phase.",
            TxnLocalSteps("UPDATE U SET N = N + 5 WHERE ID = 1", ["BE AC BC", "BE BC AR"], UpdateA));

        yield return Run("txn-local-xproc-conflict-settings", Ids(Tx, "TXN-LOCAL"),
            "Process a begins a transaction and updates row 1; process b, three times, closes and reopens its Database after DBEngine.SetOption calls in its own process (condition 1: dbLockRetry 1 and dbLockDelay 1; condition 2: dbLockRetry 5 and dbLockDelay 100; condition 3: condition 2's values plus dbPageTimeout 100) and updates the same row (errors recorded, call timings recorded by the worker). Process a rolls back; both read row 1. Asks how the conflicting call ends under each setting; engine defaults are read in txn-local-xproc-same-row, not here.",
            [
                Sa(p =>
                {
                    p.Call("ws", "BeginTrans");
                    Exec(p, UpdateA);
                }),
                Sb(p => Condition(p, [(OptLockRetry, 1), (OptLockDelay, 1)])),
                Sb(p => Condition(p, [(OptLockRetry, 5), (OptLockDelay, 100)])),
                Sb(p => Condition(p, [(OptPageTimeout, 100)])),
                Sa(p => p.Call("ws", "Rollback").Errors()),
                Sb(p => Rows(p, "db", "b1", Sel3, Dao.OpenSnapshot, 4, 4)),
                Sa(p => Rows(p, "db", "a1", Sel3, Dao.OpenSnapshot, 4, 4)),
            ]);

        // R-TXN-1 cross-process: TXN-CLOSE, TXN-VIEW, TXN-NEST, TXN-MULTI, TXN-STMT, TXN-FLUSH-LIVE ------------------------
        yield return Run("txn-range-close", Ids(Tx, "TXN-CLOSE", R4, "TXN-RANGE"),
            "Phase 1: process a creates a second Workspace, opens the file in it, begins a transaction there and updates N of row 1; process b updates V of row 2 (no transaction); a closes the second Workspace with its transaction open; b and a read rows 1, 2 and 64. Phase 2: a begins a transaction on Workspaces(0), updates row 1; b updates row 2; a closes its Database (transaction still open), reopens it and reads; b reads; a calls Rollback on the Workspace and reads; b reads. Asks what an open transaction's close resolves and whether another process's unrelated update survives.",
            [
                Sa(p =>
                {
                    p.CallBind("w2", "eng", "CreateWorkspace", A.Str("w2"), A.Str("Admin"), A.Str(""))
                        .CallBind("db2", "w2", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false)).Call("w2", "BeginTrans")
                        .Call("db2", "Execute", A.Str(UpdateA), A.I4(Dao.FailOnError)).Get("db2", "RecordsAffected");
                }),
                Sb(p => Exec(p, "UPDATE T SET V = V + 1 WHERE ID = 2")),
                Sa(p =>
                {
                    p.Call("w2", "Close").Errors();
                    Rows(p, "db", "a1", Sel3, Dao.OpenSnapshot, 4, 4);
                }),
                Sb(p => Rows(p, "db", "b1", Sel3, Dao.OpenSnapshot, 4, 4)),
                Sa(p =>
                {
                    p.Call("ws", "BeginTrans");
                    Exec(p, UpdateA);
                }),
                Sb(p => Exec(p, "UPDATE T SET V = V + 1 WHERE ID = 2")),
                Sa(p =>
                {
                    p.Call("db", "Close").Errors().Release("db").OpenDb(readOnly: false);
                    Rows(p, "db", "a2", Sel3, Dao.OpenSnapshot, 4, 4);
                }),
                Sb(p => Rows(p, "db", "b2", Sel3, Dao.OpenSnapshot, 4, 4)),
                Sa(p =>
                {
                    p.Call("ws", "Rollback").Errors();
                    Rows(p, "db", "a3", Sel3, Dao.OpenSnapshot, 4, 4);
                }),
                Sb(p => Rows(p, "db", "b3", Sel3, Dao.OpenSnapshot, 4, 4)),
            ]);

        yield return Run("txn-view-readers", Ids(Tx, "TXN-VIEW", R3, "READ-REFRESH"),
            "Process b holds a snapshot, dynaset, forward-only and table-type cursor over rows 1, 2 and 64 opened before any transaction. Process a begins a transaction and updates N of row 1; b reads each held cursor (Move 0, read, Transactions) and opens new cursors. Process a commits; b reads the held cursors without Requery, then Requery on each and reads, then new cursors. Process a begins, updates and has b read (held cursors without Requery, new cursors), then rolls back; b reads without and then with Requery. Asks membership, value and refresh timing for readers on both sides of another process's commit and rollback.",
            [
                Sb(p => { OpenHeld(p); p.Get("db", "Transactions"); }),
                Sa(p =>
                {
                    p.Call("ws", "BeginTrans").Get("db", "Transactions");
                    Exec(p, UpdateA, errors: false);
                }),
                Sb(p => { LookHeld(p); ReadRows(p, "n1"); }),
                Sa(p => p.Call("ws", "CommitTrans").Errors()),
                Sb(p =>
                {
                    LookHeld(p);
                    foreach (string h in Held)
                    {
                        p.Call(h, "Requery").ReadRow(h);
                    }
                    ReadRows(p, "n2");
                }),
                Sa(p =>
                {
                    p.Call("ws", "BeginTrans");
                    Exec(p, UpdateA, errors: false);
                }),
                Sb(p => { LookHeld(p); ReadRows(p, "n3"); }),
                Sa(p => p.Call("ws", "Rollback").Errors()),
                Sb(p =>
                {
                    LookHeld(p);
                    foreach (string h in Held)
                    {
                        p.Call(h, "Requery").ReadRow(h);
                    }
                    ReadRows(p, "n4");
                }),
            ], closeB: [.. Held, "db"]);

        const string Sel123 = "SELECT ID, N FROM T WHERE ID <= 3 ORDER BY ID";
        yield return Run("txn-nest-observed", Ids(Tx, "TXN-NEST", R4, "TXN-RANGE"),
            "Process a begins three nested levels, updating N of rows 1, 2 and 3 at the outer, middle and inner levels. Process b reads rows 1-3 through a new snapshot at each of: all three levels open; after the inner CommitTrans; after a Rollback (of the middle level); after a CommitTrans of the outer level; and after two further calls (CommitTrans, Rollback) with no level open. Process a reads its own view after the Rollback. Asks which changes another process sees at each resolution.",
            [
                Sa(p =>
                {
                    p.Call("ws", "BeginTrans");
                    Exec(p, UpdateA, errors: false);
                    p.Call("ws", "BeginTrans");
                    Exec(p, "UPDATE T SET N = N + 1000 WHERE ID = 2", errors: false);
                    p.Call("ws", "BeginTrans");
                    Exec(p, "UPDATE T SET N = N + 1000 WHERE ID = 3", errors: false);
                }),
                Sb(p => Rows(p, "db", "b1", Sel123, Dao.OpenSnapshot, 4, 4)),
                Sa(p => p.Call("ws", "CommitTrans").Errors()),
                Sb(p => Rows(p, "db", "b2", Sel123, Dao.OpenSnapshot, 4, 4)),
                Sa(p =>
                {
                    p.Call("ws", "Rollback").Errors();
                    Rows(p, "db", "a3", Sel123, Dao.OpenSnapshot, 4, 4);
                }),
                Sb(p => Rows(p, "db", "b3", Sel123, Dao.OpenSnapshot, 4, 4)),
                Sa(p => p.Call("ws", "CommitTrans").Errors()),
                Sb(p => Rows(p, "db", "b4", Sel123, Dao.OpenSnapshot, 4, 4)),
                Sa(p => p.Call("ws", "CommitTrans").Errors().Call("ws", "Rollback").Errors()),
                Sb(p => Rows(p, "db", "b5", Sel123, Dao.OpenSnapshot, 4, 4)),
            ]);

        yield return Run("txn-multi-two-files", Ids(Tx, "TXN-MULTI"),
            "Both processes open two files (main fixture T and a second fixture T2) in Workspaces(0). Process a begins one transaction and updates row 1 of T and row 1 of T2; b reads both; a commits; b reads both. Then a begins, updates T2 first and T second (the opposite order), and rolls back; b reads both, then a reads both. Asks how another process sees a transaction spanning two files at each point.",
            [
                Sa(p =>
                {
                    p.Call("ws", "BeginTrans");
                    Exec(p, UpdateA, errors: false);
                    Exec(p, "UPDATE T2 SET N = N + 1000 WHERE ID = 1", db: "dbs");
                }),
                Sb(ReadBoth("b1")),
                Sa(p => p.Call("ws", "CommitTrans").Errors()),
                Sb(ReadBoth("b2")),
                Sa(p =>
                {
                    p.Call("ws", "BeginTrans");
                    Exec(p, "UPDATE T2 SET N = N + 1000 WHERE ID = 2", db: "dbs");
                    Exec(p, "UPDATE T SET N = N + 1000 WHERE ID = 2", errors: false);
                    p.Call("ws", "Rollback").Errors();
                }),
                Sb(ReadBoth("b3")),
                Sa(ReadBoth("a3")),
            ], openA: OpenTwo, openB: OpenTwo, closeA: ["dbs", "db"], closeB: ["dbs", "db"], withSecond: true);

        yield return Run("txn-stmt-blocked-no-scope", Ids(Tx, "TXN-STMT", R4, "WRITE-TIMING"),
            "Process a holds a dbPessimistic Edit on the middle row (11) of rows 10-12; process b runs, each as its own statement with no transaction: an earlier UPDATE of row 1, a multi-row UPDATE of rows 10-12 with no options (RecordsAffected and errors recorded), and a later UPDATE of row 40, then reads those rows. Process a then edits row 21 (middle of 20-22) and b repeats the three statements with dbFailOnError on the multi-row one. Asks which candidates a blocked multi-row Execute changes, its count and error, and whether earlier and later statements are unaffected.",
            [
                Sa(p => HoldRow(p, 11)),
                Sb(p => Stmt(p, 10, 12, -1, 40)),
                Sa(p =>
                {
                    p.Call("ra", "CancelUpdate").Call("ra", "Close").Release("ra");
                    HoldRow(p, 21);
                }),
                Sb(p => Stmt(p, 20, 22, Dao.FailOnError, 41)),
                Sa(p => p.Call("ra", "CancelUpdate")),
                Sb(p => Rows(p, "db", "final", "SELECT ID, V FROM T WHERE ID IN (1, 10, 11, 12, 20, 21, 22, 40, 41) ORDER BY ID", Dao.OpenSnapshot, 12, 4)),
            ], closeA: ["ra", "db"]);

        yield return Run("txn-stmt-blocked-in-txn", Ids(Tx, "TXN-STMT", "TXN-LOCAL"),
            "Phase 1: process a holds a dbPessimistic Edit on the first row (30) of rows 30-32; process b begins a transaction, runs an earlier UPDATE of row 1, the multi-row UPDATE of rows 30-32 with no options, and a later UPDATE of row 50, then CommitTrans. Phase 2: a edits the last row (42) of rows 40-42; b begins, runs an UPDATE of row 2, the multi-row UPDATE of rows 40-42 with dbFailOnError and an UPDATE of row 51, then Rollback. Each statement's RecordsAffected and errors are recorded and both processes read the affected rows after each phase. Asks what a failed or partial statement leaves of the surrounding transaction.",
            [
                Sa(p => HoldRow(p, 30)),
                Sb(p =>
                {
                    p.Call("ws", "BeginTrans");
                    Stmt(p, 30, 32, -1, 50, earlier: 1);
                    p.Call("ws", "CommitTrans").Errors();
                }),
                Sa(p =>
                {
                    p.Call("ra", "CancelUpdate").Call("ra", "Close").Release("ra");
                    Rows(p, "db", "a1", "SELECT ID, V FROM T WHERE ID IN (1, 30, 31, 32, 50) ORDER BY ID", Dao.OpenSnapshot, 8, 4);
                    HoldRow(p, 42);
                }),
                Sb(p =>
                {
                    p.Call("ws", "BeginTrans");
                    Stmt(p, 40, 42, Dao.FailOnError, 51, earlier: 2);
                    p.Call("ws", "Rollback").Errors();
                }),
                Sa(p =>
                {
                    p.Call("ra", "CancelUpdate");
                    Rows(p, "db", "a2", "SELECT ID, V FROM T WHERE ID IN (1, 2, 40, 41, 42, 50, 51) ORDER BY ID", Dao.OpenSnapshot, 8, 4);
                }),
            ], closeA: ["ra", "db"]);

        yield return Run("txn-flush-live-images", Ids(Tx, "TXN-FLUSH-LIVE", "TXN-FLUSH-COMMIT", "TXN-FLUSH-OP", "TXN-FLUSH-BEHIND"),
            "Process a keeps the database image (and the observed lock-file state) at: start; inside a transaction after an update; after an ordinary CommitTrans; inside a second transaction; after CommitTrans(dbForceOSFlush); inside two nested levels after an inner CommitTrans(dbForceOSFlush); after the outer Rollback; right after an implicit-commit UPDATE statement; and after DBEngine.Idle(dbRefreshCache). Process b reads row 1 through a new snapshot after the ordinary commit, after the outer rollback and after the implicit update. Asks which images differ at which point; byte interpretation is later work.",
            [
                Sa(p => p.Files("f0-start", true)),
                Sa(p =>
                {
                    p.Call("ws", "BeginTrans");
                    Exec(p, UpdateA, errors: false);
                    p.Files("f1-in-txn", true).Call("ws", "CommitTrans").Files("f2-after-commit", true);
                }),
                Sb(p => Rows(p, "db", "b1", Sel3, Dao.OpenSnapshot, 4, 4)),
                Sa(p =>
                {
                    p.Call("ws", "BeginTrans");
                    Exec(p, UpdateA, errors: false);
                    p.Files("f3-in-txn-2", true).Call("ws", "CommitTrans", A.I4(ForceOsFlush)).Files("f4-after-forced-commit", true);
                    p.Call("ws", "BeginTrans").Call("ws", "BeginTrans");
                    Exec(p, UpdateA, errors: false);
                    p.Call("ws", "CommitTrans", A.I4(ForceOsFlush)).Files("f5-after-inner-forced", true).Call("ws", "Rollback").Files("f6-after-outer-rollback", true);
                }),
                Sb(p => Rows(p, "db", "b2", Sel3, Dao.OpenSnapshot, 4, 4)),
                Sa(p =>
                {
                    Exec(p, UpdateA, errors: false);
                    p.Files("f7-after-implicit-op", true).Call("eng", "Idle", A.I4(RefreshCache)).Files("f8-after-idle", true);
                }),
                Sb(p => Rows(p, "db", "b3", Sel3, Dao.OpenSnapshot, 4, 4)),
            ]);
    }

    // Program engine -----------------------------------------------------------------------------------------------

    private static string[] Ids(string card, params string[] ids) => [card, .. ids];

    /// <summary>
    /// Builds a two-participant probe from ordered steps. Each participant opens its Database (a first, b after a's
    /// "ready"), runs only its own steps, waits for the previous step's signal when that step belongs to the other
    /// participant, and signals after a step when the next step belongs to the other participant.
    /// </summary>
    private static Probe Run(string name, string[] refs, string description, Step[] steps, string fixture = FxAccdb,
        Action<P>? openA = null, Action<P>? openB = null, string[]? closeA = null, string[]? closeB = null, bool withSecond = false, bool daoA = true)
    {
        (string Role, string Fixture)[] fixtures = withSecond ? [("main", fixture), ("second", FxSecond)] : [("main", fixture)];
        return new Probe(Card, name, refs, description, a =>
        {
            (openA ?? OpenDefaultA)(a);
            a.Signal("ready");
            Play(a, 'a', steps);
            if (steps[^1].Who == 'b')
            {
                a.Await($"s{steps.Length - 1}", Wait);
            }
            if (daoA)
            {
                a.Errors();
            }
            a.Finish(closeA ?? ["db"]);
        })
        {
            Fixtures = fixtures,
            Participants =
            [
                ("b", b =>
                {
                    b.Await("ready", Wait).Create().Modules("after-create");
                    (openB ?? OpenDefaultB)(b);
                    Play(b, 'b', steps);
                    b.Errors().Finish(closeB ?? ["db"]);
                }),
            ],
        };
    }

    private static void Play(P p, char who, Step[] steps)
    {
        for (int k = 0; k < steps.Length; k++)
        {
            if (steps[k].Who != who)
            {
                continue;
            }
            if (k > 0 && steps[k - 1].Who != who)
            {
                p.Await($"s{k - 1}", Wait);
            }
            steps[k].Body(p);
            if (k == steps.Length - 1 || steps[k + 1].Who != who)
            {
                p.Signal($"s{k}");
            }
        }
    }

    private static void OpenDefaultA(P a) => a.Engine().GetBind("ws", "eng", "Workspaces", A.I4(0)).OpenDb(readOnly: false);

    private static void OpenDefaultB(P b) => b.GetBind("ws", "eng", "Workspaces", A.I4(0)).OpenDb(readOnly: false);

    private static void OpenTwo(P p)
    {
        p.Engine();
        p.GetBind("ws", "eng", "Workspaces", A.I4(0)).OpenDb(readOnly: false).OpenDb("dbs", "second", false, false);
    }

    // Fixtures ---------------------------------------------------------------------------------------------------------

    private static Probe Creation(string name, string ext, int version, string what, Action<P> body) =>
        new(Card, "create-" + name, ["R-LOCK-3", "R-LOCK-4", "R-TXN-1"],
            $"Fixture for the lock and cross-process transaction probes: {what}. Created in {ext.ToUpperInvariant()} by DBEngine.CreateDatabase(path, dbLangGeneral, {version}); the closed file is kept.",
            p =>
            {
                p.Modules("before-create").Create().Modules("after-create")
                    .CallBind("db", "eng", "CreateDatabase", A.Out($"{Card}-{name}.{ext}"), A.Str(Dao.LangGeneral), A.I4(version));
                body(p);
                p.Call("db", "Close").Release("db").Release("eng").Modules("after-close").Files("after-close");
            })
        { CreatesFixture = true };

    /// <summary>
    /// T: 64 rows of about 210 bytes (ID 1-64, N = 10 * ID, V = 0), primary key PK_T and index IX_N on N; U: 4 small rows;
    /// L: four MEMO rows (row 4 about 6,000 characters); C: AutoNumber table with 2 rows; QueryDef QT.
    /// </summary>
    private static void MainBody(P p)
    {
        p.Call("db", "Execute", A.Str("CREATE TABLE T (ID LONG CONSTRAINT PK_T PRIMARY KEY, N LONG, V LONG, S TEXT(200))"))
            .Call("db", "Execute", A.Str("CREATE INDEX IX_N ON T (N)"))
            .Call("db", "Execute", A.Str(InsertT(1, 10, 0, Pad)));
        for (int k = 1; k <= 32; k *= 2)
        {
            p.Call("db", "Execute", A.Str($"INSERT INTO T (ID, N, V, S) SELECT ID + {k}, N + {10 * k}, V, S FROM T"));
        }
        p.Call("db", "Execute", A.Str("CREATE TABLE U (ID LONG CONSTRAINT PK_U PRIMARY KEY, N LONG, W TEXT(20))"));
        for (int i = 1; i <= 4; i++)
        {
            p.Call("db", "Execute", A.Str($"INSERT INTO U (ID, N, W) VALUES ({i}, {i}, 'u{i}')"));
        }
        p.Call("db", "Execute", A.Str("CREATE TABLE L (ID LONG CONSTRAINT PK_L PRIMARY KEY, M MEMO)"));
        for (int i = 1; i <= 3; i++)
        {
            p.Call("db", "Execute", A.Str($"INSERT INTO L (ID, M) VALUES ({i}, 'short memo {i}')"));
        }
        p.Call("db", "Execute", A.Str($"INSERT INTO L (ID, M) VALUES (4, '{new string('l', 6000)}')"))
            .Call("db", "Execute", A.Str("CREATE TABLE C (ID COUNTER CONSTRAINT PK_C PRIMARY KEY, V TEXT(20))"))
            .Call("db", "Execute", A.Str("INSERT INTO C (V) VALUES ('c1')"))
            .Call("db", "Execute", A.Str("INSERT INTO C (V) VALUES ('c2')"))
            .CallBind("q1", "db", "CreateQueryDef", A.Str("QT"), A.Str("SELECT ID, N FROM T WHERE ID <= 10 ORDER BY ID;")).Release("q1");
        Rows(p, "db", "check", "SELECT COUNT(*) AS Cnt FROM T", Dao.OpenSnapshot, 1, 4);
    }

    private static string InsertT(int id, int n, int v, string s) => $"INSERT INTO T (ID, N, V, S) VALUES ({id}, {n}, {v}, '{s}')";

    // Operations -------------------------------------------------------------------------------------------------------

    /// <summary>Execute through <paramref name="db"/>; options &lt; 0 omit the argument. RecordsAffected and Errors follow.</summary>
    private static void Exec(P p, string sql, int options = Dao.FailOnError, bool errors = true, string db = "db")
    {
        if (options < 0)
        {
            p.Call(db, "Execute", A.Str(sql));
        }
        else
        {
            p.Call(db, "Execute", A.Str(sql), A.I4(options));
        }
        p.Get(db, "RecordsAffected");
        if (errors)
        {
            p.Errors();
        }
    }

    private static void Rows(P p, string db, string tag, string sql, int type = Dao.OpenSnapshot, int maxRows = 8, int maxFields = 8) =>
        p.OpenRs(tag, db, sql, type).ReadAll(tag, maxRows, maxFields).Call(tag, "Close").Release(tag);

    /// <summary>Hold the named fields of the recordset's current record as binds <paramref name="prefix"/> + name.</summary>
    private static void Hold(P p, string rs, string prefix, params string[] names)
    {
        p.GetBind(prefix + "fl", rs, "Fields");
        foreach (string n in names)
        {
            p.Item(prefix + n, prefix + "fl", A.Str(n));
        }
    }

    /// <summary>A new snapshot, dynaset, forward-only recordset and a table-type Seek on rows 1, 2 and 64.</summary>
    private static void ReadRows(P p, string tag)
    {
        foreach ((string label, int type) in new[] { ("snap", Dao.OpenSnapshot), ("dyn", Dao.OpenDynaset), ("fwd", Dao.OpenForwardOnly) })
        {
            Rows(p, "db", tag + label, Sel3, type, 4, 4);
        }
        string t = tag + "tbl";
        p.OpenRs(t, "db", "T", Dao.OpenTable).Put(t, "Index", A.Str("PK_T"));
        foreach (int id in new[] { 1, 2, 64 })
        {
            p.Call(t, "Seek", A.Str("="), A.I4(id)).Get(t, "NoMatch").ReadRow(t, 4);
        }
        p.Call(t, "Close").Release(t);
    }

    private static void OpenHeld(P p)
    {
        p.OpenRs("hs", "db", Sel3, Dao.OpenSnapshot).OpenRs("hd", "db", Sel3, Dao.OpenDynaset).OpenRs("hf", "db", Sel3, Dao.OpenForwardOnly)
            .OpenRs("ht", "db", "T", Dao.OpenTable).Put("ht", "Index", A.Str("PK_T")).Call("ht", "MoveFirst");
        foreach (string h in Held)
        {
            p.ReadRow(h, 4);
        }
    }

    private static void LookHeld(P p)
    {
        foreach (string h in Held)
        {
            p.Call(h, "Move", A.I4(0)).ReadRow(h, 4).Get(h, "Transactions");
        }
    }

    private static readonly string[] Ladder = ["rd", "rs", "rt"];
    private static readonly string[] Kinds = ["cs", "cd", "cf", "ct"];

    private static void OpenLadder(P p)
    {
        p.OpenRs("rd", "db", Sel4, Dao.OpenDynaset).OpenRs("rs", "db", Sel4, Dao.OpenSnapshot)
            .OpenRs("rt", "db", "T", Dao.OpenTable).Put("rt", "Index", A.Str("PK_T")).Call("rt", "MoveFirst");
    }

    private static void LookLadder(P p)
    {
        foreach (string c in Ladder)
        {
            p.ReadRow(c, 4);
        }
    }

    private static void ObserveId(P p)
    {
        foreach (string c in new[] { "rd", "cl" })
        {
            p.ReadRow(c, 4).Get(c, "Bookmark").Get(c, "BOF").Get(c, "EOF").Get(c, "EditMode");
        }
    }

    private static void Descr(P p) =>
        p.Get("tdf", "Name").Get("tfl", "Count").Each("tfl", "fd", 16, b => b.Get("fd", "Name").Get("fd", "Type"))
            .Get("tix", "Count").Each("tix", "ix", 8, b => b.Get("ix", "Name"))
            .Get("tpr", "Count").Get("qd", "SQL").Get("qfl", "Count").Each("qfl", "qf", 8, b => b.Get("qf", "Name"));

    private static void FieldNames(P p, string table) =>
        p.GetBind("tdfs", "db", "TableDefs").Call("tdfs", "Refresh").Item("tdf", "tdfs", A.Str(table)).Get("tdf", "Name")
            .GetBind("tfl", "tdf", "Fields").Call("tfl", "Refresh").Each("tfl", "fd", 16, b => b.Get("fd", "Name"))
            .Release("tfl").Release("tdf").Release("tdfs");

    private static void ReadHigh(P p, string tag)
    {
        Rows(p, "db", tag + "n", "SELECT COUNT(*) AS Cnt, MAX(ID) AS MaxId FROM T", Dao.OpenSnapshot, 1, 4);
        Rows(p, "db", tag + "r", "SELECT ID, N FROM T WHERE ID > 200 ORDER BY ID", Dao.OpenSnapshot, 12, 4);
    }

    /// <summary>AddNew/Update on table C through a dynaset, reading the AutoNumber ID after AddNew and after Update.</summary>
    private static void AddC(P p, string tag, string text)
    {
        p.OpenRs(tag, "db", "SELECT * FROM C", Dao.OpenDynaset);
        Hold(p, tag, tag, "ID", "V");
        p.Call(tag, "AddNew").Get(tag + "ID", "Value").Put(tag + "V", "Value", A.Str(text)).Call(tag, "Update").Errors().Get(tag + "ID", "Value").Call(tag, "Close");
    }

    private static void TryRow1(P p)
    {
        Exec(p, "UPDATE T SET V = V + 1 WHERE ID = 1");
        Rows(p, "db", "r1", "SELECT ID, N, V FROM T WHERE ID = 1", Dao.OpenSnapshot, 2, 4);
    }

    private static void BulkAttempts(P p)
    {
        Exec(p, "UPDATE T SET V = V + 100 WHERE ID = 64");
        Exec(p, "UPDATE U SET N = N + 1 WHERE ID = 1");
        Exec(p, InsertT(400, 4000, 0, Pad));
        Rows(p, "db", "cnt", "SELECT COUNT(*) AS Cnt FROM T", Dao.OpenSnapshot, 1, 4);
    }

    private static void Condition(P p, (int Option, int Value)[] options)
    {
        p.Call("db", "Close").Release("db");
        foreach ((int option, int value) in options)
        {
            p.Call("eng", "SetOption", A.I4(option), A.I4(value));
        }
        p.OpenDb(readOnly: false);
        Exec(p, "UPDATE T SET V = V + 5 WHERE ID = 1");
    }

    private static Action<P> ReadBoth(string tag) => p =>
    {
        Rows(p, "db", tag + "t", "SELECT ID, N FROM T WHERE ID <= 2 ORDER BY ID", Dao.OpenSnapshot, 4, 4);
        Rows(p, "dbs", tag + "s", "SELECT ID, N FROM T2 ORDER BY ID", Dao.OpenSnapshot, 6, 4);
    };

    private static void HoldRow(P p, int id)
    {
        p.OpenRs("ra", "db", $"SELECT * FROM T WHERE ID = {id}", Dao.OpenDynaset, A.I4(0), A.I4(Pessimistic));
        Hold(p, "ra", "a", "N");
        p.Call("ra", "Edit").Put("aN", "Value", A.I4(7000 + id)).Get("ra", "EditMode");
    }

    /// <summary>An earlier change, a multi-row Execute over [from, to] and a later change; the reads follow.</summary>
    private static void Stmt(P p, int from, int to, int options, int later, int earlier = 1)
    {
        Exec(p, $"UPDATE T SET V = V + 1 WHERE ID = {earlier}");
        Exec(p, $"UPDATE T SET V = V + 100 WHERE ID BETWEEN {from} AND {to}", options);
        Exec(p, $"UPDATE T SET V = V + 1 WHERE ID = {later}");
        Rows(p, "db", $"s{from}", $"SELECT ID, V FROM T WHERE ID IN ({earlier}, {from}, {from + 1}, {to}, {later}) ORDER BY ID", Dao.OpenSnapshot, 8, 4);
    }

    private static Step[] DenySteps()
    {
        List<Step> steps = [];
        foreach (int deny in new[] { DenyWrite, DenyRead })
        {
            steps.Add(Sa(p => p.OpenRs("rt", "db", "T", Dao.OpenTable, A.I4(deny)).Put("rt", "Index", A.Str("PK_T")).Call("rt", "MoveFirst").ReadRow("rt", 4)));
            string tag = "d" + steps.Count;
            steps.Add(Sb(p => DenyTries(p, tag)));
            steps.Add(Sa(p => p.Call("rt", "Close").Release("rt")));
            string tag2 = "d" + steps.Count;
            steps.Add(Sb(p => DenyTries(p, tag2)));
        }
        return [.. steps];
    }

    private static void DenyTries(P p, string tag)
    {
        Rows(p, "db", tag + "s", "SELECT ID, N FROM T WHERE ID = 1", Dao.OpenSnapshot, 2, 4);
        Exec(p, "UPDATE T SET V = V + 1 WHERE ID = 1");
        p.OpenRs(tag + "t", "db", "T", Dao.OpenTable).Put(tag + "t", "Index", A.Str("PK_T")).Call(tag + "t", "MoveFirst").ReadRow(tag + "t", 4).Call(tag + "t", "Close");
    }

    private static Probe WriteOriginal(string name, string fixture, string branch, string bText, Action<P>[] bPhases)
    {
        List<Step> steps =
        [
            Sa(p =>
            {
                p.OpenRs("ra", "db", "SELECT * FROM T WHERE ID = 1", Dao.OpenDynaset, A.I4(0), A.I4(Optimistic));
                Hold(p, "ra", "a", "N");
            }),
        ];
        for (int i = 0; i < bPhases.Length; i++)
        {
            int value = 100 + i;
            Action<P> b = bPhases[i];
            steps.Add(Sa(p => p.Call("ra", "Edit").Put("aN", "Value", A.I4(value)).Get("ra", "EditMode")));
            steps.Add(Sb(b));
            steps.Add(Sa(p => p.Call("ra", "Update").Errors().Get("ra", "EditMode").ReadRow("ra", 4).Call("ra", "Update").Errors().Get("ra", "EditMode")
                .Call("ra", "Move", A.I4(0)).Errors().ReadRow("ra", 4).Get("ra", "EditMode").Call("ra", "CancelUpdate").Errors().Get("ra", "EditMode").ReadRow("ra", 4)));
        }
        steps.Add(Sa(p => p.Files("a-final", true)));
        steps.Add(Sb(p => Rows(p, "db", "final", Sel3, Dao.OpenSnapshot, 4, 4)));
        return Run(name, Ids(R4, branch, "WRITE-TIMING"),
            $"Process a holds an optimistic (dbOptimistic) dynaset on row 1 with Edit pending after assigning N. {bText} while the Edit is pending (errors recorded). Process a then calls Update, reads the row and EditMode, calls Update a second time, Move 0, then CancelUpdate, reading the row, EditMode and DBEngine.Errors after each. Process b finally reads rows 1, 2 and 64. Asks whether the first Update is refused, the second succeeds and what the row holds.",
            [.. steps], fixture, closeA: ["ra", "db"]);
    }

    /// <summary>
    /// Phases of two live transaction owners: process a begins and updates row 1; process b begins (or not, tokens BE/BI) and runs
    /// <paramref name="bStmt"/>; the resolution tokens (A/B then C/R) run in order; both read afterwards.
    /// </summary>
    private static Step[] TxnLocalSteps(string bStmt, string[] orders, string updateA)
    {
        List<Step> steps = [];
        foreach (string order in orders)
        {
            string[] tokens = order.Split(' ');
            bool explicitB = tokens[0] == "BE";
            steps.Add(Sa(p =>
            {
                p.Call("ws", "BeginTrans");
                Exec(p, updateA, errors: false);
            }));
            steps.Add(Sb(p =>
            {
                if (explicitB)
                {
                    p.Call("ws", "BeginTrans");
                }
                Exec(p, bStmt);
            }));
            foreach (string token in tokens.Skip(1))
            {
                string call = token[1] == 'C' ? "CommitTrans" : "Rollback";
                Action<P> resolve = p => p.Call("ws", call).Errors();
                steps.Add(token[0] == 'A' ? Sa(resolve) : Sb(resolve));
            }
            string label = "r" + steps.Count;
            steps.Add(Sb(p => ReadPhase(p, "b" + label)));
            steps.Add(Sa(p => ReadPhase(p, "a" + label)));
        }
        return [.. steps];
    }

    private static void ReadPhase(P p, string tag)
    {
        Rows(p, "db", tag + "t", Sel3, Dao.OpenSnapshot, 4, 4);
        Rows(p, "db", tag + "u", SelU, Dao.OpenSnapshot, 2, 4);
    }
}
