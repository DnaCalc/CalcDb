namespace CalcDb.Probes.Cards;

/// <summary>
/// Ready-gate probe round <c>r-q48-ado-batch-edits</c> (32 probes across ACCDB &amp; Jet 4 MDB):
/// <list type="bullet">
/// <item>
/// <b>Part A — <c>wzq4</c> Ready Gate (<c>W-09/edit-client-batch</c>, <c>R-ADO-1</c>, 30 probes):</b>
/// genuine open cases beyond <c>r-ado-1b</c>, <c>r-q-ado-misc</c>, <c>r-q2-ado-misc</c>, and <c>r-fix-wire-ado</c>:
/// client static batch-optimistic <c>(3,3,4)</c> full <c>Properties</c> inventory, <c>Status</c> at BOF/EOF,
/// <c>MarshalOptions</c> (<c>adMarshalAll</c> vs <c>adMarshalModifiedOnly</c>, invalid values, clone/disconnect, ACCDB &amp; MDB),
/// deferred local <c>AddNew</c>/<c>Update</c>/<c>Delete</c> vs immediate <c>adLockOptimistic</c> with byte-level page capture
/// (ACCDB &amp; MDB), <c>AddNew</c>/<c>EditMode</c>/<c>OriginalValue</c>/<c>UnderlyingValue</c>/<c>Status</c> channels,
/// true <c>adLockBatchOptimistic</c> pending-op matrix, <c>Delete(adAffectCurrent/adAffectGroup/adAffectAll)</c> (ACCDB &amp; MDB),
/// <c>CancelBatch</c> &amp; <c>UpdateBatch</c> omitted/scope/invalid/non-batch matrices, combined <c>Filter</c>+<c>Sort</c>+<c>Find</c>
/// with batch edits, <c>FilterGroupEnum</c> full lifecycle, clone edits with filtered original, <c>adResyncUnderlyingValues</c>
/// conflict-trigger discriminator, <c>Resync</c> omitted args and <c>adAffectGroup</c> with active filter (ACCDB &amp; MDB),
/// explicit disconnect (<c>ActiveConnection = Empty / ""</c>) &amp; reconnect, multi-row mixed update/delete/constraint conflicts
/// and local-<c>Delete</c> conflicts (ACCDB &amp; MDB), and <c>BeginTrans</c>/<c>CommitTrans</c>/<c>RollbackTrans</c> around
/// <c>UpdateBatch</c> (including partial-conflict rollback) with exact <c>0xE02</c>, page-1, and row/index bytes on ACCDB &amp; MDB.
/// </item>
/// <item>
/// <b>Part B — <c>bi-select-aggregates</c> Group 5 Overflow Isolate (<c>r-q47</c> Open follow-up, 2 probes):</b>
/// isolates <c>SUM(B)</c> vs <c>AVG(B)</c> vs <c>MIN(B)</c>/<c>MAX(B)</c>/<c>COUNT(B)</c> on Group 5
/// (<c>-9223372036854775808</c> + <c>9223372036854775807</c>), single-row extremes, and mixed <c>Null</c> + extreme
/// <c>BIGINT</c> groups via both DAO and ADO.
/// </item>
/// </list>
/// Every writing probe keeps ALL file pages (<c>nativeRead keep:true</c>, catalog pages included) before and after
/// every write, <c>UpdateBatch</c>, <c>CommitTrans</c>, <c>RollbackTrans</c>, pause, and <c>Close</c>.
/// </summary>
public sealed class RQ48AdoBatchEdits : ICard
{
    private const string Card = "r-q48-ado-batch-edits";
    private const string MainAccdb = "r-ado-1b-main";
    private const string MainMdb = "r-ado-1b-mdb";
    private const string EmptyAccdb = "r-cat-1-accdb-empty";
    private const string AceProvider = "Microsoft.ACE.OLEDB.12.0";
    private const int FullReadBytes = 524288;

    private const string SelW = "SELECT ID, Code, V, V2, S FROM W ORDER BY ID";
    private const string SelT = "SELECT ID, Code, L, S FROM T ORDER BY ID";

    private static class Ado
    {
        public const int UseServer = 2, UseClient = 3;
        public const int OpenForwardOnly = 0, OpenKeyset = 1, OpenStatic = 3;
        public const int LockReadOnly = 1, LockOptimistic = 3, LockBatchOptimistic = 4;
        public const int CmdText = 1, ExecuteNoRecords = 0x80;
        public const int ModeRead = 1;
        public const int AffectCurrent = 1, AffectGroup = 2, AffectAll = 3;
        public const int ResyncUnderlying = 1, ResyncAll = 2;
        public const int FilterNone = 0, FilterPending = 1, FilterAffected = 2, FilterFetched = 3, FilterConflicting = 5;
        public const int MarshalAll = 0, MarshalModifiedOnly = 1;
        public const int SearchForward = 1, SearchBackward = -1;
    }

    private static readonly string[] BatchRefs = ["R-ADO-1", "ADO-BATCH", "ADO-BATCH-SCOPE", "ADO-VALUES", "W-09", "II-12"];
    private static readonly string[] ResyncRefs = ["R-ADO-1", "ADO-RESYNC", "ADO-VALUES", "ADO-BATCH", "W-09", "II-12"];
    private static readonly string[] ViewRefs = ["R-ADO-1", "ADO-VIEW", "ADO-BATCH-SCOPE", "ADO-BOOK", "W-09", "II-12"];
    private static readonly string[] TxnRefs = ["R-ADO-1", "ADO-TXN", "ADO-BATCH", "R-TXN-1", "W-09", "II-08", "II-12"];
    private static readonly string[] BiRefs = ["R-FMT-1", "EXT-READ", "R-EXPR-1", "R-SQL-1", "R-ADO-1", "II-04", "II-10", "W-04", "W-06", "W-08", "W-09"];

    private sealed class Names
    {
        private int _n;
        public string Next(string prefix) => $"{prefix}{++_n}";
    }

    public IEnumerable<Probe> Probes()
    {
        foreach (Probe p in PartA1LifecyclePropsMarshalProbes()) yield return p;
        foreach (Probe p in PartA2DeleteCancelUpdateViewProbes()) yield return p;
        foreach (Probe p in PartA3ResyncValuesDisconnectProbes()) yield return p;
        foreach (Probe p in PartA4ConflictProbes()) yield return p;
        foreach (Probe p in PartA5TxnByteProbes()) yield return p;
        foreach (Probe p in PartBBigIntAggIsolateProbes()) yield return p;
    }

    // ================================================================================================
    // Part A1 — Client-Cursor Properties, MarshalOptions, Deferred vs Immediate, AddNew Channels (7 probes)

    private static IEnumerable<Probe> PartA1LifecyclePropsMarshalProbes()
    {
        // 1. Client static batch-optimistic (3,3,4) & optimistic (3,3,3) full Properties inventory + Status at BOF/EOF
        yield return Q("client-props-inventory-accdb", BatchRefs, MainAccdb,
            "ACCDB (read-only): open client static batch-optimistic (3,3,4) and client static optimistic (3,3,3) Recordsets of W; " +
            "capture full Properties inventory (Count, Name, Type, Attributes, Value), Update Criteria invalid puts (-1, 4, 'bad'), " +
            "Update Resync combinations (3, 5, 6, 7, -1, 16), and Recordset.Status at row 1, BOF (MovePrevious from row 1), and EOF (MoveLast+MoveNext).",
            p =>
            {
                var n = new Names();
                StartReadOnly(p);
                AdoOpen(p, "cn", readOnly: false, captureModules: true);

                AdoRs(p, "rs", SelW, "cn", Ado.UseClient, Ado.OpenStatic, Ado.LockBatchOptimistic);
                p.Get("rs", "CursorLocation").Get("rs", "CursorType").Get("rs", "LockType")
                    .Get("rs", "RecordCount").Get("rs", "EditMode").Get("rs", "Status");

                // Full client Recordset Properties inventory
                p.GetBind("pr", "rs", "Properties").Get("pr", "Count")
                    .Each("pr", "pi", 128, b => b
                        .Get("pi", "Name").Get("pi", "Type").Get("pi", "Attributes").Get("pi", "Value"));

                // Update Criteria edge puts
                p.Item("puc", "pr", A.Str("Update Criteria")).Get("puc", "Value");
                foreach (Arg bad in new[] { A.I4(-1), A.I4(4), A.Str("bad") })
                {
                    p.Put("puc", "Value", bad).Get("puc", "Value");
                    AdoErrorsClear(p, "cn", n);
                }
                p.Release("puc");

                // Update Resync bitmask & out-of-range puts
                p.Item("pur", "pr", A.Str("Update Resync")).Get("pur", "Value");
                foreach (int v in new[] { 3, 5, 6, 7, -1, 16 })
                {
                    p.Put("pur", "Value", A.I4(v)).Get("pur", "Value");
                    AdoErrorsClear(p, "cn", n);
                }
                p.Release("pur").Release("pr");

                // Status at BOF and EOF
                p.Call("rs", "MoveFirst").Get("rs", "BOF").Get("rs", "EOF").Get("rs", "Status")
                    .Call("rs", "MovePrevious").Get("rs", "BOF").Get("rs", "EOF").Get("rs", "Status");
                AdoErrorsClear(p, "cn", n);
                p.Call("rs", "MoveLast").Call("rs", "MoveNext").Get("rs", "BOF").Get("rs", "EOF").Get("rs", "Status");
                AdoErrorsClear(p, "cn", n);
                p.Call("rs", "Close").Release("rs");

                // Client static optimistic (3,3,3) Properties Count & Update Criteria/Resync defaults
                AdoRs(p, "rso", SelW, "cn", Ado.UseClient, Ado.OpenStatic, Ado.LockOptimistic);
                p.Get("rso", "CursorLocation").Get("rso", "CursorType").Get("rso", "LockType")
                    .GetBind("pro", "rso", "Properties").Get("pro", "Count")
                    .Item("pouc", "pro", A.Str("Update Criteria")).Get("pouc", "Value").Release("pouc")
                    .Item("pour", "pro", A.Str("Update Resync")).Get("pour", "Value").Release("pour")
                    .Release("pro")
                    .Call("rso", "Close").Release("rso");

                AdoClose(p, "cn");
                p.Release("eng");
            });

        // 2. MarshalOptions lifecycle (ACCDB, writing)
        yield return MarshalOptionsProbe("client-marshal-options-accdb", MainAccdb, "ACCDB");

        // 3. MarshalOptions lifecycle (Jet 4 MDB, writing)
        yield return MarshalOptionsProbe("client-marshal-options-mdb", MainMdb, "Jet 4 MDB");

        // 4. Deferred batch-optimistic (4) vs immediate client optimistic (3) with page bytes (ACCDB)
        yield return DeferredVsImmediateProbe("client-deferred-vs-immediate-accdb", MainAccdb, "ACCDB");

        // 5. Deferred batch-optimistic (4) vs immediate client optimistic (3) with page bytes (Jet 4 MDB)
        yield return DeferredVsImmediateProbe("client-deferred-vs-immediate-mdb", MainMdb, "Jet 4 MDB");

        // 6. AddNew / Update / EditMode / Status / OriginalValue / UnderlyingValue channels (ACCDB)
        yield return Q("client-addnew-channels-status-accdb", BatchRefs, MainAccdb,
            "ACCDB: on client static batch-optimistic (3,3,4) Recordsets of W and AutoNumber table T, capture Value/OriginalValue/UnderlyingValue/Status/EditMode " +
            "on clean row, edited row before Update / after Update / after MoveNext+MovePrevious / after CancelUpdate (before vs after Update), " +
            "AddNew row before Put / after Put / after Update / after MoveFirst+MoveLast / after CancelUpdate on unposted AddNew, and after UpdateBatch (plus AutoNumber ID channels). " +
            "Keeps all pages before, after local staging, after UpdateBatch, after pause, and after Close.",
            p =>
            {
                var n = new Names();
                StartWriting(p);
                AdoOpen(p, "cn", readOnly: false, captureModules: true);

                // Table W (non-AutoNumber)
                AdoRs(p, "rs", SelW, "cn", Ado.UseClient, Ado.OpenStatic, Ado.LockBatchOptimistic);
                p.GetBind("fl", "rs", "Fields")
                    .Item("fid", "fl", A.Str("ID")).Item("fc", "fl", A.Str("Code"))
                    .Item("fv", "fl", A.Str("V")).Item("fs", "fl", A.Str("S"));

                // Clean row 1
                RowChannels(p, "rs", "fs", "cn", n);

                // Edit row 1 -> check before Update, after Update, after MoveNext+MovePrevious back
                p.Put("fs", "Value", A.Str("ed1"));
                RowChannels(p, "rs", "fs", "cn", n);
                p.Call("rs", "Update");
                RowChannels(p, "rs", "fs", "cn", n);
                p.Call("rs", "MoveNext").Get("rs", "EditMode").Get("rs", "Status")
                    .Call("rs", "MovePrevious");
                RowChannels(p, "rs", "fs", "cn", n);

                // CancelUpdate AFTER local Update on row 1 (what happens?)
                p.Call("rs", "CancelUpdate");
                AdoErrorsClear(p, "cn", n);
                RowChannels(p, "rs", "fs", "cn", n);

                // Edit row 2 then CancelUpdate BEFORE Update
                p.Call("rs", "MoveNext").Put("fs", "Value", A.Str("ed2tmp"));
                RowChannels(p, "rs", "fs", "cn", n);
                p.Call("rs", "CancelUpdate");
                AdoErrorsClear(p, "cn", n);
                RowChannels(p, "rs", "fs", "cn", n);

                // Unposted AddNew then CancelUpdate
                p.Call("rs", "AddNew").Get("rs", "EditMode").Get("rs", "Status").Get("rs", "RecordCount");
                FieldChannels(p, "fs", "cn", n);
                p.Put("fid", "Value", A.I4(49)).Put("fc", "Value", A.Str("w49")).Put("fs", "Value", A.Str("cancelnew"));
                FieldChannels(p, "fs", "cn", n);
                p.Call("rs", "CancelUpdate").Get("rs", "EditMode").Get("rs", "Status").Get("rs", "RecordCount");
                AdoErrorsClear(p, "cn", n);

                // Posted AddNew (ID 50) -> channels immediately after AddNew, after Put, after Update, after round-trip nav
                p.Call("rs", "AddNew").Get("rs", "EditMode").Get("rs", "Status")
                    .Put("fid", "Value", A.I4(50)).Put("fc", "Value", A.Str("w50")).Put("fv", "Value", A.I4(500)).Put("fs", "Value", A.Str("new50"));
                RowChannels(p, "rs", "fs", "cn", n);
                p.Call("rs", "Update");
                RowChannels(p, "rs", "fs", "cn", n);
                p.Call("rs", "MoveFirst").Call("rs", "MoveLast");
                RowChannels(p, "rs", "fs", "cn", n);

                KeepAllPages(p, "after-local-staging", n.Next("pg"));
                p.Call("rs", "UpdateBatch", A.I4(Ado.AffectAll));
                AdoErrorsClear(p, "cn", n);
                KeepAllPages(p, "after-w-updatebatch", n.Next("pg"));

                // Read row 1 and new row 50 after UpdateBatch
                p.Call("rs", "MoveFirst");
                RowChannels(p, "rs", "fs", "cn", n);
                p.Call("rs", "MoveLast");
                RowChannels(p, "rs", "fs", "cn", n);
                p.Release("fs").Release("fv").Release("fc").Release("fid").Release("fl").Call("rs", "Close").Release("rs");

                // Table T (AutoNumber ID) AddNew channels before/after Update and UpdateBatch
                AdoRs(p, "rt", SelT, "cn", Ado.UseClient, Ado.OpenStatic, Ado.LockBatchOptimistic);
                p.GetBind("tfl", "rt", "Fields").Item("tid", "tfl", A.Str("ID")).Item("tc", "tfl", A.Str("Code")).Item("ts", "tfl", A.Str("S"))
                    .Call("rt", "AddNew").Put("tc", "Value", A.Str("z99")).Put("ts", "Value", A.Str("autonum"));
                FieldChannels(p, "tid", "cn", n);
                p.Call("rt", "Update").Get("rt", "EditMode").Get("rt", "Status");
                FieldChannels(p, "tid", "cn", n);
                p.Call("rt", "UpdateBatch", A.I4(Ado.AffectAll));
                AdoErrorsClear(p, "cn", n);
                KeepAllPages(p, "after-t-updatebatch", n.Next("pg"));
                p.Get("rt", "EditMode").Get("rt", "Status");
                FieldChannels(p, "tid", "cn", n);
                FieldChannels(p, "ts", "cn", n);
                p.Release("ts").Release("tc").Release("tid").Release("tfl").Call("rt", "Close").Release("rt");

                p.Pause(1500);
                KeepAllPages(p, "after-pause-live", n.Next("pg"));
                AdoClose(p, "cn");
                KeepAllPages(p, "after-close", n.Next("pg"));
                p.Release("eng");
            });

        // 7. True adLockBatchOptimistic (4) pending-edit & pending-AddNew operation matrix (ACCDB)
        yield return Q("client-batch-pending-ops-accdb", BatchRefs, MainAccdb,
            "ACCDB: on a true client static batch-optimistic (3,3,4) Recordset of W, tests every operation while an unposted field Put (EditMode=1) " +
            "or unposted AddNew (EditMode=2) is open: Find, Filter, Sort, Clone, Requery, Resync, MoveNext, MoveFirst, AddNew, Delete, CancelUpdate, CancelBatch, UpdateBatch, Close. " +
            "Captures EditMode, Status, RecordCount, Errors, and keeps all pages before/after.",
            p =>
            {
                var n = new Names();
                StartWriting(p);
                AdoOpen(p, "cn", readOnly: false, captureModules: true);

                (string Name, Action<P, string> Act)[] editOps =
                [
                    ("find", (q, r) => q.Call(r, "Find", A.Str("ID = 3"))),
                    ("filter", (q, r) => q.Put(r, "Filter", A.Str("ID <= 3"))),
                    ("sort", (q, r) => q.Put(r, "Sort", A.Str("ID DESC"))),
                    ("clone", (q, r) => q.CallBind("rc_" + r, r, "Clone").Call("rc_" + r, "Close").Release("rc_" + r)),
                    ("requery", (q, r) => q.Call(r, "Requery")),
                    ("resync", (q, r) => q.Call(r, "Resync", A.I4(Ado.AffectAll), A.I4(Ado.ResyncAll))),
                    ("movenext", (q, r) => q.Call(r, "MoveNext")),
                    ("movefirst", (q, r) => q.Call(r, "MoveFirst")),
                    ("addnew", (q, r) => q.Call(r, "AddNew")),
                    ("delete", (q, r) => q.Call(r, "Delete")),
                    ("cancelupdate", (q, r) => q.Call(r, "CancelUpdate")),
                    ("cancelbatch", (q, r) => q.Call(r, "CancelBatch", A.I4(Ado.AffectAll))),
                    ("close", (q, r) => q.Call(r, "Close")),
                ];

                // Round 1: unposted Put on existing row 1 (EditMode = 1)
                foreach ((string name, Action<P, string> act) in editOps)
                {
                    string r = n.Next("re");
                    AdoRs(p, r, SelW, "cn", Ado.UseClient, Ado.OpenStatic, Ado.LockBatchOptimistic);
                    p.GetBind(r + "f", r, "Fields").Item(r + "s", r + "f", A.Str("S"))
                        .Put(r + "s", "Value", A.Str("pe-" + name))
                        .Get(r, "EditMode").Get(r, "Status");
                    act(p, r);
                    AdoErrorsClear(p, "cn", n);
                    p.Get(r, "State").Get(r, "EditMode").Get(r, "Status").Get(r, "RecordCount");
                    AdoErrorsClear(p, "cn", n);
                    p.Release(r + "s").Release(r + "f");
                    if (name != "close")
                    {
                        p.Call(r, "CancelBatch", A.I4(Ado.AffectAll));
                        AdoErrorsClear(p, "cn", n);
                        p.Call(r, "Close");
                        AdoErrorsClear(p, "cn", n);
                    }
                    else
                    {
                        p.Call(r, "CancelUpdate").Call(r, "Close");
                        AdoErrorsClear(p, "cn", n);
                    }
                    p.Release(r);
                }

                // Round 2: unposted AddNew (EditMode = 2) with UpdateBatch, CancelBatch, CancelUpdate, Close, MoveFirst
                foreach (string act in new[] { "updatebatch", "cancelbatch", "cancelupdate", "movefirst", "close" })
                {
                    string r = n.Next("ra");
                    AdoRs(p, r, SelW, "cn", Ado.UseClient, Ado.OpenStatic, Ado.LockBatchOptimistic);
                    p.GetBind(r + "f", r, "Fields").Item(r + "id", r + "f", A.Str("ID")).Item(r + "c", r + "f", A.Str("Code")).Item(r + "s", r + "f", A.Str("S"))
                        .Call(r, "AddNew")
                        .Put(r + "id", "Value", A.I4(60)).Put(r + "c", "Value", A.Str("w60")).Put(r + "s", "Value", A.Str("an-" + act))
                        .Get(r, "EditMode").Get(r, "Status");
                    switch (act)
                    {
                        case "updatebatch": p.Call(r, "UpdateBatch", A.I4(Ado.AffectAll)); break;
                        case "cancelbatch": p.Call(r, "CancelBatch", A.I4(Ado.AffectAll)); break;
                        case "cancelupdate": p.Call(r, "CancelUpdate"); break;
                        case "movefirst": p.Call(r, "MoveFirst"); break;
                        case "close": p.Call(r, "Close"); break;
                    }
                    AdoErrorsClear(p, "cn", n);
                    p.Get(r, "State").Get(r, "EditMode").Get(r, "Status").Get(r, "RecordCount");
                    AdoErrorsClear(p, "cn", n);
                    if (act == "updatebatch") KeepAllPages(p, "after-unposted-addnew-ub", n.Next("pg"));
                    p.Release(r + "s").Release(r + "c").Release(r + "id").Release(r + "f");
                    p.Call(r, "CancelBatch", A.I4(Ado.AffectAll));
                    AdoErrorsClear(p, "cn", n);
                    p.Call(r, "Close");
                    AdoErrorsClear(p, "cn", n);
                    p.Release(r);
                }

                p.Pause(1500);
                KeepAllPages(p, "after-pause-live", n.Next("pg"));
                AdoClose(p, "cn");
                KeepAllPages(p, "after-close", n.Next("pg"));
                p.Release("eng");
            });
    }

    private static Probe MarshalOptionsProbe(string name, string fixture, string fmt) =>
        Q(name, BatchRefs, fixture,
            $"{fmt}: MarshalOptions lifecycle on client static batch-optimistic (3,3,4), client read-only (3,3,1), and server keyset (2,1,3): " +
            "default before/after Open, setting adMarshalModifiedOnly(1) vs adMarshalAll(0) vs invalid(2, -1, 'bad'), " +
            "inheritance on Clone, survival across disconnect (cn.Close) and reconnect (cn2), and UpdateBatch of update+insert+delete under MarshalOptions=1. " +
            "Keeps all pages before, after local staging, after UpdateBatch, after pause, and after Close.",
            p =>
            {
                var n = new Names();
                StartWriting(p);
                AdoOpen(p, "cn", readOnly: false, captureModules: true);

                // Default before Open & after Open on client (3,3,4)
                p.Create("ADODB.Recordset", "rs")
                    .Get("rs", "MarshalOptions")
                    .Put("rs", "CursorLocation", A.I4(Ado.UseClient))
                    .Put("rs", "MarshalOptions", A.I4(Ado.MarshalModifiedOnly)).Get("rs", "MarshalOptions")
                    .Call("rs", "Open", A.Str(SelW), A.Obj("cn"), A.I4(Ado.OpenStatic), A.I4(Ado.LockBatchOptimistic), A.I4(Ado.CmdText))
                    .Get("rs", "MarshalOptions");

                // Switch between 0, 1, and invalid values while open
                foreach (Arg val in new[] { A.I4(Ado.MarshalAll), A.I4(Ado.MarshalModifiedOnly), A.I4(2), A.I4(-1), A.Str("bad") })
                {
                    p.Put("rs", "MarshalOptions", val).Get("rs", "MarshalOptions");
                    AdoErrorsClear(p, "cn", n);
                }

                // Clone inheritance
                p.CallBind("rc", "rs", "Clone").Get("rc", "MarshalOptions")
                    .Put("rc", "MarshalOptions", A.I4(Ado.MarshalAll)).Get("rc", "MarshalOptions").Get("rs", "MarshalOptions")
                    .Call("rc", "Close").Release("rc");

                // Stage update + insert + delete under MarshalOptions = 1, commit via UpdateBatch (keeps page bytes), then check survival across Close
                p.GetBind("fl", "rs", "Fields").Item("fid", "fl", A.Str("ID")).Item("fc", "fl", A.Str("Code")).Item("fs", "fl", A.Str("S"))
                    .Put("fs", "Value", A.Str("mo1")).Call("rs", "Update")
                    .Call("rs", "MoveNext").Call("rs", "Delete")
                    .Call("rs", "AddNew").Put("fid", "Value", A.I4(25)).Put("fc", "Value", A.Str("w25")).Put("fs", "Value", A.Str("mo25")).Call("rs", "Update");
                KeepAllPages(p, "after-local-staging", n.Next("pg"));

                p.Call("rs", "UpdateBatch", A.I4(Ado.AffectAll));
                AdoErrorsClear(p, "cn", n);
                p.Get("rs", "MarshalOptions");
                KeepAllPages(p, "after-updatebatch-live", n.Next("pg"));
                p.Release("fs").Release("fc").Release("fid").Release("fl").Call("rs", "Close").Get("rs", "MarshalOptions").Release("rs");

                // Default on client read-only (3,3,1) and server keyset (2,1,3)
                AdoRs(p, "rro", SelW, "cn", Ado.UseClient, Ado.OpenStatic, Ado.LockReadOnly);
                p.Get("rro", "MarshalOptions").Put("rro", "MarshalOptions", A.I4(Ado.MarshalModifiedOnly)).Get("rro", "MarshalOptions")
                    .Call("rro", "Close").Release("rro");
                AdoRs(p, "rks", SelW, "cn", Ado.UseServer, Ado.OpenKeyset, Ado.LockOptimistic);
                p.Get("rks", "MarshalOptions").Put("rks", "MarshalOptions", A.I4(Ado.MarshalModifiedOnly)).Get("rks", "MarshalOptions");
                AdoErrorsClear(p, "cn", n);
                p.Call("rks", "Close").Release("rks");

                p.Pause(1500);
                KeepAllPages(p, "after-pause-live", n.Next("pg"));
                AdoClose(p, "cn");
                KeepAllPages(p, "after-close", n.Next("pg"));
                AdoReadBackW(p, n, "ID <= 3 OR ID = 25");
                p.Release("eng");
            });

    private static Probe DeferredVsImmediateProbe(string name, string fixture, string fmt) =>
        Q(name, BatchRefs, fixture,
            $"{fmt}: side-by-side page-byte discriminator between client static batch-optimistic (3,3,4) and client static optimistic (3,3,3) on W. " +
            "Phase 1 (batch-optimistic): Put+Update, AddNew+Update, Delete (keeps all pages after each -> 0 bytes), then UpdateBatch (keeps all pages -> exact 0xE02/page-1/row/index bytes), pause, Close. " +
            "Phase 2 (client optimistic): Put+Update, AddNew+Update, Delete (keeps all pages after each -> immediate commits), pause, Close.",
            p =>
            {
                var n = new Names();
                StartWriting(p);
                AdoOpen(p, "cn1", readOnly: false, captureModules: true);

                // Phase 1: adLockBatchOptimistic (4)
                AdoRs(p, "rb", SelW, "cn1", Ado.UseClient, Ado.OpenStatic, Ado.LockBatchOptimistic);
                p.GetBind("bf", "rb", "Fields").Item("bid", "bf", A.Str("ID")).Item("bc", "bf", A.Str("Code")).Item("bs", "bf", A.Str("S"))
                    .Put("bs", "Value", A.Str("bat1")).Call("rb", "Update").Get("rb", "Status").Get("rb", "EditMode");
                KeepAllPages(p, "p1-after-local-update", n.Next("pg"));

                p.Call("rb", "AddNew").Put("bid", "Value", A.I4(31)).Put("bc", "Value", A.Str("w31")).Put("bs", "Value", A.Str("bat31")).Call("rb", "Update").Get("rb", "Status");
                KeepAllPages(p, "p1-after-local-addnew", n.Next("pg"));

                p.Call("rb", "MoveFirst").Call("rb", "MoveNext").Call("rb", "Delete").Get("rb", "Status");
                KeepAllPages(p, "p1-after-local-delete", n.Next("pg"));

                p.Call("rb", "UpdateBatch", A.I4(Ado.AffectAll));
                AdoErrorsClear(p, "cn1", n);
                KeepAllPages(p, "p1-after-updatebatch-live", n.Next("pg"));
                p.Release("bs").Release("bc").Release("bid").Release("bf").Call("rb", "Close").Release("rb");
                p.Pause(1500);
                KeepAllPages(p, "p1-after-pause-live", n.Next("pg"));
                AdoClose(p, "cn1");
                KeepAllPages(p, "p1-after-close", n.Next("pg"));

                // Phase 2: adLockOptimistic (3) on client cursor
                AdoOpen(p, "cn2", readOnly: false);
                AdoRs(p, "ro", SelW, "cn2", Ado.UseClient, Ado.OpenStatic, Ado.LockOptimistic);
                p.GetBind("of", "ro", "Fields").Item("oid", "of", A.Str("ID")).Item("oc", "of", A.Str("Code")).Item("os", "of", A.Str("S"))
                    .Call("ro", "Move", A.I4(2)).Put("os", "Value", A.Str("imm4")).Call("ro", "Update").Get("ro", "Status").Get("ro", "EditMode");
                KeepAllPages(p, "p2-after-opt-update", n.Next("pg"));

                p.Call("ro", "AddNew").Put("oid", "Value", A.I4(32)).Put("oc", "Value", A.Str("w32")).Put("os", "Value", A.Str("imm32")).Call("ro", "Update").Get("ro", "Status");
                KeepAllPages(p, "p2-after-opt-addnew", n.Next("pg"));

                p.Call("ro", "MoveFirst").Call("ro", "Move", A.I4(3)).Call("ro", "Delete").Get("ro", "Status");
                KeepAllPages(p, "p2-after-opt-delete", n.Next("pg"));

                p.Release("os").Release("oc").Release("oid").Release("of").Call("ro", "Close").Release("ro");
                p.Pause(1500);
                KeepAllPages(p, "p2-after-pause-live", n.Next("pg"));
                AdoClose(p, "cn2");
                KeepAllPages(p, "p2-after-close", n.Next("pg"));

                AdoReadBackW(p, n, "ID <= 6 OR ID >= 30");
                p.Release("eng");
            });

    // ================================================================================================
    // Part A2 — Delete, CancelBatch, UpdateBatch Scopes, Filter+Sort+Find, FilterGroup, Clone (7 probes)

    private static IEnumerable<Probe> PartA2DeleteCancelUpdateViewProbes()
    {
        // 8. Delete scopes (adAffectCurrent, adAffectGroup, adAffectAll, omitted, invalid, on New/Modified/BOF/EOF) - ACCDB
        yield return DeleteScopesProbe("batch-delete-scopes-accdb", MainAccdb, "ACCDB");

        // 9. Delete scopes - Jet 4 MDB parity
        yield return DeleteScopesProbe("batch-delete-scopes-mdb", MainMdb, "Jet 4 MDB");

        // 10. CancelBatch complete scope & edge matrix (ACCDB)
        yield return Q("batch-cancel-scopes-accdb", BatchRefs, MainAccdb,
            "ACCDB: CancelBatch complete edge matrix on client static batch-optimistic (3,3,4) Recordset of W: " +
            "omitted arg CancelBatch(), CancelBatch(adAffectCurrent) on clean / modified / newly added / deleted (via adFilterPendingRecords) / EOF rows, " +
            "CancelBatch(adAffectGroup) with adFilterPendingRecords / string Filter (at valid row 1) / adFilterNone / empty Filter, " +
            "CancelBatch with unposted EditMode=1 and EditMode=2, and invalid CancelBatch(0) & CancelBatch(99). Keeps all pages before/after (0 bytes written).",
            p =>
            {
                var n = new Names();
                StartWriting(p);
                AdoOpen(p, "cn", readOnly: false, captureModules: true);
                AdoRs(p, "rs", SelW, "cn", Ado.UseClient, Ado.OpenStatic, Ado.LockBatchOptimistic);
                p.GetBind("fl", "rs", "Fields").Item("fid", "fl", A.Str("ID")).Item("fc", "fl", A.Str("Code")).Item("fs", "fl", A.Str("S"));

                // 1. CancelBatch(adAffectCurrent) on clean row 1
                p.Get("rs", "Status").Call("rs", "CancelBatch", A.I4(Ado.AffectCurrent)).Get("rs", "Status");
                AdoErrorsClear(p, "cn", n);

                // 2. Stage modified row 1, deleted row 2, newly added ID 70
                p.Put("fs", "Value", A.Str("c1")).Call("rs", "Update")
                    .Call("rs", "MoveNext").Call("rs", "Delete")
                    .Call("rs", "AddNew").Put("fid", "Value", A.I4(70)).Put("fc", "Value", A.Str("w70")).Put("fs", "Value", A.Str("c70")).Call("rs", "Update");

                // CancelBatch(adAffectCurrent) on newly added row 70 (does it disappear and RecordCount drop?)
                p.Get("rs", "RecordCount").Get("rs", "Status")
                    .Call("rs", "CancelBatch", A.I4(Ado.AffectCurrent))
                    .Get("rs", "RecordCount").Get("rs", "EOF").Get("rs", "Status");
                AdoErrorsClear(p, "cn", n);

                // CancelBatch(adAffectCurrent) on deleted row 2 positioned via adFilterPendingRecords
                p.Put("rs", "Filter", A.I4(Ado.FilterPending)).Get("rs", "RecordCount")
                    .Call("rs", "MoveFirst").Get("rs", "Status") // row 1 (mod)
                    .Call("rs", "MoveNext").Get("rs", "Status")  // row 2 (deleted)
                    .Call("rs", "CancelBatch", A.I4(Ado.AffectCurrent));
                AdoErrorsClear(p, "cn", n);
                p.Put("rs", "Filter", A.I4(Ado.FilterNone)).Get("rs", "RecordCount")
                    .Call("rs", "MoveFirst").Call("rs", "MoveNext").Get("fid", "Value").Get("rs", "Status");

                // CancelBatch(adAffectCurrent) at EOF
                p.Call("rs", "MoveLast").Call("rs", "MoveNext").Get("rs", "EOF")
                    .Call("rs", "CancelBatch", A.I4(Ado.AffectCurrent));
                AdoErrorsClear(p, "cn", n);

                // 3. CancelBatch(adAffectGroup) with string Filter at valid row 1, vs adFilterNone, vs empty Filter, vs adFilterPendingRecords
                p.Call("rs", "MoveFirst").Call("rs", "MoveNext").Put("fs", "Value", A.Str("c2")).Call("rs", "Update")
                    .Call("rs", "MoveNext").Put("fs", "Value", A.Str("c3")).Call("rs", "Update");
                p.Put("rs", "Filter", A.Str("ID <= 2")).Call("rs", "MoveFirst").Get("rs", "RecordCount")
                    .Call("rs", "CancelBatch", A.I4(Ado.AffectGroup));
                AdoErrorsClear(p, "cn", n);
                p.Put("rs", "Filter", A.I4(Ado.FilterNone)).Call("rs", "CancelBatch", A.I4(Ado.AffectGroup));
                AdoErrorsClear(p, "cn", n);
                p.Put("rs", "Filter", A.Str("ID > 99")).Get("rs", "RecordCount").Call("rs", "CancelBatch", A.I4(Ado.AffectGroup));
                AdoErrorsClear(p, "cn", n);
                p.Put("rs", "Filter", A.I4(Ado.FilterPending)).Get("rs", "RecordCount")
                    .Call("rs", "CancelBatch", A.I4(Ado.AffectGroup));
                AdoErrorsClear(p, "cn", n);
                p.Put("rs", "Filter", A.I4(Ado.FilterNone));
                DumpBatchRows(p, "rs", "S", 4);

                // 4. Omitted arg CancelBatch() after re-staging row 1 & AddNew ID 71
                p.Call("rs", "MoveFirst").Put("fs", "Value", A.Str("omit1")).Call("rs", "Update")
                    .Call("rs", "AddNew").Put("fid", "Value", A.I4(71)).Put("fc", "Value", A.Str("w71")).Put("fs", "Value", A.Str("omit71")).Call("rs", "Update")
                    .Call("rs", "CancelBatch");
                AdoErrorsClear(p, "cn", n);
                p.Get("rs", "RecordCount").Put("rs", "Filter", A.I4(Ado.FilterPending)).Get("rs", "RecordCount").Put("rs", "Filter", A.I4(Ado.FilterNone));

                // 5. Invalid enum values (0, 99)
                foreach (int bad in new[] { 0, 99 })
                {
                    p.Call("rs", "CancelBatch", A.I4(bad));
                    AdoErrorsClear(p, "cn", n);
                }

                KeepAllPages(p, "after-cancels-live", n.Next("pg"));
                p.Release("fs").Release("fc").Release("fid").Release("fl").Call("rs", "Close").Release("rs");
                AdoClose(p, "cn");
                KeepAllPages(p, "after-close", n.Next("pg"));
                p.Release("eng");
            });

        // 11. UpdateBatch omitted arg, adAffectCurrent on clean/mod/new/deleted/EOF, invalid AffectEnum, and non-batch cursors (ACCDB)
        yield return Q("batch-update-scopes-args-accdb", BatchRefs, MainAccdb,
            "ACCDB: UpdateBatch edge & cursor-type matrix: omitted UpdateBatch(), invalid AffectRecords (0, 99), " +
            "UpdateBatch(adAffectCurrent) on clean row / modified row / new row / deleted row (via adFilterPendingRecords) / EOF, " +
            "UpdateBatch(adAffectGroup) with Filter=adFilterNone, and UpdateBatch on non-batch cursors (3,3,1), (3,3,3), (2,1,3), (2,3,1). " +
            "Keeps all pages before/after each UpdateBatch, pause, and Close.",
            p =>
            {
                var n = new Names();
                StartWriting(p);
                AdoOpen(p, "cn", readOnly: false, captureModules: true);
                AdoRs(p, "rs", SelW, "cn", Ado.UseClient, Ado.OpenStatic, Ado.LockBatchOptimistic);
                p.GetBind("fl", "rs", "Fields").Item("fid", "fl", A.Str("ID")).Item("fc", "fl", A.Str("Code")).Item("fs", "fl", A.Str("S"));

                // UpdateBatch(adAffectCurrent) on clean row 1 (no-op)
                p.Call("rs", "UpdateBatch", A.I4(Ado.AffectCurrent));
                AdoErrorsClear(p, "cn", n);
                KeepAllPages(p, "after-clean-current", n.Next("pg"));

                // Invalid AffectEnum 0 and 99
                foreach (int bad in new[] { 0, 99 })
                {
                    p.Call("rs", "UpdateBatch", A.I4(bad));
                    AdoErrorsClear(p, "cn", n);
                }

                // Stage modified row 1, deleted row 2, modified row 3, new row 80
                p.Put("fs", "Value", A.Str("ub1")).Call("rs", "Update")
                    .Call("rs", "MoveNext").Call("rs", "Delete")
                    .Call("rs", "MoveNext").Put("fs", "Value", A.Str("ub3")).Call("rs", "Update")
                    .Call("rs", "AddNew").Put("fid", "Value", A.I4(80)).Put("fc", "Value", A.Str("w80")).Put("fs", "Value", A.Str("ub80")).Call("rs", "Update");

                // UpdateBatch(adAffectCurrent) on newly added row 80
                p.Call("rs", "UpdateBatch", A.I4(Ado.AffectCurrent));
                AdoErrorsClear(p, "cn", n);
                p.Get("rs", "Status");
                KeepAllPages(p, "after-new-current", n.Next("pg"));

                // UpdateBatch(adAffectCurrent) on deleted row 2 via adFilterPendingRecords
                p.Put("rs", "Filter", A.I4(Ado.FilterPending)).Get("rs", "RecordCount")
                    .Call("rs", "MoveFirst").Call("rs", "MoveNext").Get("rs", "Status")
                    .Call("rs", "UpdateBatch", A.I4(Ado.AffectCurrent));
                AdoErrorsClear(p, "cn", n);
                KeepAllPages(p, "after-del-current", n.Next("pg"));

                // UpdateBatch(adAffectGroup) with Filter = adFilterNone (refused?)
                p.Put("rs", "Filter", A.I4(Ado.FilterNone)).Call("rs", "UpdateBatch", A.I4(Ado.AffectGroup));
                AdoErrorsClear(p, "cn", n);

                // UpdateBatch(adAffectCurrent) at EOF
                p.Call("rs", "MoveLast").Call("rs", "MoveNext").Get("rs", "EOF")
                    .Call("rs", "UpdateBatch", A.I4(Ado.AffectCurrent));
                AdoErrorsClear(p, "cn", n);

                // Omitted arg UpdateBatch() commits remaining pending rows 1 & 3
                p.Call("rs", "MoveFirst").Call("rs", "UpdateBatch");
                AdoErrorsClear(p, "cn", n);
                KeepAllPages(p, "after-omitted-updatebatch", n.Next("pg"));
                p.Put("rs", "Filter", A.I4(Ado.FilterPending)).Get("rs", "RecordCount").Put("rs", "Filter", A.I4(Ado.FilterNone));
                p.Release("fs").Release("fc").Release("fid").Release("fl").Call("rs", "Close").Release("rs");

                // UpdateBatch on non-batch cursors: (3,3,1), (3,3,3), (2,1,3), (2,3,1)
                foreach ((string tag, int loc, int cur, int lck) in new[]
                {
                    ("c_ro", Ado.UseClient, Ado.OpenStatic, Ado.LockReadOnly),
                    ("c_opt", Ado.UseClient, Ado.OpenStatic, Ado.LockOptimistic),
                    ("s_ks", Ado.UseServer, Ado.OpenKeyset, Ado.LockOptimistic),
                    ("s_ro", Ado.UseServer, Ado.OpenStatic, Ado.LockReadOnly),
                })
                {
                    AdoRs(p, tag, SelW, "cn", loc, cur, lck);
                    p.Call(tag, "UpdateBatch", A.I4(Ado.AffectAll));
                    AdoErrorsClear(p, "cn", n);
                    p.Call(tag, "Close").Release(tag);
                }

                p.Pause(1500);
                KeepAllPages(p, "after-pause-live", n.Next("pg"));
                AdoClose(p, "cn");
                KeepAllPages(p, "after-close", n.Next("pg"));
                AdoReadBackW(p, n, "ID <= 4 OR ID = 80");
                p.Release("eng");
            });

        // 12. Combined Filter + Sort + Find with batch edits (ACCDB)
        yield return Q("batch-filter-sort-find-edits-accdb", ViewRefs, MainAccdb,
            "ACCDB: on client static batch-optimistic (3,3,4) Recordset of W, tests combining Filter + Sort (Filter then Sort, clear Sort keep Filter, Sort then Filter), " +
            "editing the sorted column V, AddNew and Delete while Sort is active, Find forward/backward/SkipRecords/Start-bookmark/not-found across pending edits, " +
            "and UpdateBatch while Sort is active. Keeps all pages before/after UpdateBatch, pause, and Close.",
            p =>
            {
                var n = new Names();
                StartWriting(p);
                AdoOpen(p, "cn", readOnly: false, captureModules: true);
                AdoRs(p, "rs", SelW, "cn", Ado.UseClient, Ado.OpenStatic, Ado.LockBatchOptimistic);
                p.GetBind("fl", "rs", "Fields").Item("fid", "fl", A.Str("ID")).Item("fc", "fl", A.Str("Code"))
                    .Item("fv", "fl", A.Str("V")).Item("fs", "fl", A.Str("S"));

                // Combine Filter then Sort
                p.Put("rs", "Filter", A.Str("ID <= 6")).Put("rs", "Sort", A.Str("V DESC"))
                    .Get("rs", "RecordCount").Call("rs", "MoveFirst");
                AdoErrorsClear(p, "cn", n);
                ReadIds(p, "rs", 8);

                // Clear Sort while keeping Filter
                p.Put("rs", "Sort", A.Str("")).Get("rs", "Filter").Get("rs", "RecordCount").Call("rs", "MoveFirst");
                ReadIds(p, "rs", 8);

                // Sort then Filter
                p.Put("rs", "Filter", A.I4(Ado.FilterNone)).Put("rs", "Sort", A.Str("S DESC"))
                    .Put("rs", "Filter", A.Str("ID >= 3 AND ID <= 8")).Get("rs", "RecordCount").Call("rs", "MoveFirst");
                AdoErrorsClear(p, "cn", n);
                ReadIds(p, "rs", 8);

                // Active Sort = "V DESC" (no filter): edit V of row 1 (from 10 -> 55), AddNew V=45, Delete row V=120
                p.Put("rs", "Filter", A.I4(Ado.FilterNone)).Put("rs", "Sort", A.Str("V DESC"))
                    .Call("rs", "MoveLast").Get("fid", "Value").Get("fv", "Value") // ID 1, V=10
                    .Put("fv", "Value", A.I4(55)).Call("rs", "Update")
                    .Get("fid", "Value").Get("rs", "AbsolutePosition");
                p.Call("rs", "AddNew").Put("fid", "Value", A.I4(45)).Put("fc", "Value", A.Str("w45")).Put("fv", "Value", A.I4(45)).Put("fs", "Value", A.Str("s45")).Call("rs", "Update")
                    .Call("rs", "MoveFirst").Get("fid", "Value").Call("rs", "Delete"); // deletes ID 12 (V=120)
                p.Call("rs", "MoveFirst");
                ReadIds(p, "rs", 16);

                // Find variations across pending sorted view: forward, SkipRecords=1, backward, Start bookmark (R8 1), not-found
                p.Call("rs", "MoveFirst").Call("rs", "Find", A.Str("V = 45")).Get("rs", "EOF").Get("fid", "Value").Get("rs", "AbsolutePosition")
                    .Call("rs", "MoveFirst").Call("rs", "Find", A.Str("S LIKE 's*'"), A.I4(1), A.I4(Ado.SearchForward)).Get("rs", "EOF").Get("fid", "Value")
                    .Call("rs", "MoveLast").Call("rs", "Find", A.Str("ID = 3"), A.I4(0), A.I4(Ado.SearchBackward)).Get("rs", "BOF").Get("fid", "Value")
                    .Call("rs", "Find", A.Str("ID = 5"), A.I4(0), A.I4(Ado.SearchForward), A.R8(1.0)).Get("rs", "EOF").Get("fid", "Value")
                    .Call("rs", "MoveFirst").Call("rs", "Find", A.Str("ID = 999")).Get("rs", "EOF").Get("rs", "AbsolutePosition");
                AdoErrorsClear(p, "cn", n);

                // UpdateBatch while Sort is active
                KeepAllPages(p, "after-local-sorted-edits", n.Next("pg"));
                p.Call("rs", "UpdateBatch", A.I4(Ado.AffectAll));
                AdoErrorsClear(p, "cn", n);
                KeepAllPages(p, "after-sorted-updatebatch", n.Next("pg"));
                p.Get("rs", "Sort").Call("rs", "MoveFirst");
                ReadIds(p, "rs", 16);

                p.Release("fs").Release("fv").Release("fc").Release("fid").Release("fl").Call("rs", "Close").Release("rs");
                p.Pause(1500);
                KeepAllPages(p, "after-pause-live", n.Next("pg"));
                AdoClose(p, "cn");
                KeepAllPages(p, "after-close", n.Next("pg"));
                AdoReadBackW(p, n, "ID IN (1, 12, 45)");
                p.Release("eng");
            });

        // 13. FilterGroupEnum full lifecycle (None, Pending, Affected, Fetched, Conflicting) across staging/conflict/Resync/UpdateBatch/CancelBatch
        yield return Q("batch-filter-groups-lifecycle-accdb", ViewRefs, MainAccdb,
            "ACCDB: observes FilterGroupEnum (adFilterNone=0, adFilterPendingRecords=1, adFilterAffectedRecords=2, adFilterFetchedRecords=3, adFilterConflictingRecords=5) " +
            "RecordCount at every lifecycle phase: fresh open, after staging 3 rows, after UpdateBatch(adAffectCurrent), after partial-conflict UpdateBatch(adAffectAll), " +
            "after Resync(adAffectGroup, adResyncUnderlyingValues) under adFilterConflictingRecords, after UpdateBatch(adAffectGroup), and after CancelBatch. Keeps all pages.",
            p =>
            {
                var n = new Names();
                StartWriting(p);
                AdoOpen(p, "cn", readOnly: false, captureModules: true);
                AdoOpen(p, "cn2", readOnly: false);
                AdoRs(p, "rs", SelW, "cn", Ado.UseClient, Ado.OpenStatic, Ado.LockBatchOptimistic);
                p.GetBind("fl", "rs", "Fields").Item("fid", "fl", A.Str("ID")).Item("fc", "fl", A.Str("Code")).Item("fs", "fl", A.Str("S"));

                // Phase A: fresh open
                CheckAllFilterGroups(p, "rs", "cn", n);

                // Phase B: stage edit on row 1, row 2, row 3, AddNew row 40
                p.Put("fs", "Value", A.Str("fg1")).Call("rs", "Update")
                    .Call("rs", "MoveNext").Put("fs", "Value", A.Str("fg2")).Call("rs", "Update")
                    .Call("rs", "MoveNext").Put("fs", "Value", A.Str("fg3")).Call("rs", "Update")
                    .Call("rs", "AddNew").Put("fid", "Value", A.I4(40)).Put("fc", "Value", A.Str("w40")).Put("fs", "Value", A.Str("fg40")).Call("rs", "Update");
                CheckAllFilterGroups(p, "rs", "cn", n);

                // Phase C: UpdateBatch(adAffectCurrent) on row 40
                p.Call("rs", "MoveLast").Call("rs", "UpdateBatch", A.I4(Ado.AffectCurrent));
                AdoErrorsClear(p, "cn", n);
                KeepAllPages(p, "after-ub-current", n.Next("pg"));
                CheckAllFilterGroups(p, "rs", "cn", n);

                // Phase D: cn2 conflicts row 1; UpdateBatch(adAffectAll) commits rows 2 & 3, conflicts row 1
                ExecNo(p, "cn2", "UPDATE W SET S = 'ext1' WHERE ID = 1");
                AdoErrorsClear(p, "cn2", n);
                KeepAllPages(p, "after-ext-update", n.Next("pg"));
                p.Call("rs", "UpdateBatch", A.I4(Ado.AffectAll));
                AdoErrorsClear(p, "cn", n);
                KeepAllPages(p, "after-partial-conflict-ub", n.Next("pg"));

                // Check FilterConflicting (5) FIRST right after failed UpdateBatch, then Resync(adAffectGroup, Underlying) & UpdateBatch(adAffectGroup)
                p.Put("rs", "Filter", A.I4(Ado.FilterConflicting)).Get("rs", "RecordCount")
                    .Call("rs", "MoveFirst");
                RowChannels(p, "rs", "fs", "cn", n);
                p.Call("rs", "Resync", A.I4(Ado.AffectGroup), A.I4(Ado.ResyncUnderlying));
                AdoErrorsClear(p, "cn", n);
                RowChannels(p, "rs", "fs", "cn", n);
                p.Call("rs", "UpdateBatch", A.I4(Ado.AffectGroup));
                AdoErrorsClear(p, "cn", n);
                KeepAllPages(p, "after-resync-group-ub", n.Next("pg"));
                CheckAllFilterGroups(p, "rs", "cn", n);

                // Phase E: second conflict to test whether setting Filter=0/1/2/3 clears FilterConflicting (5)
                p.Call("rs", "MoveFirst").Call("rs", "MoveNext").Put("fs", "Value", A.Str("fg2b")).Call("rs", "Update");
                ExecNo(p, "cn2", "UPDATE W SET S = 'ext2' WHERE ID = 2");
                AdoErrorsClear(p, "cn2", n);
                p.Call("rs", "UpdateBatch", A.I4(Ado.AffectAll));
                AdoErrorsClear(p, "cn", n);
                p.Put("rs", "Filter", A.I4(Ado.FilterConflicting)).Get("rs", "RecordCount");
                CheckAllFilterGroups(p, "rs", "cn", n);
                p.Call("rs", "CancelBatch", A.I4(Ado.AffectAll));
                AdoErrorsClear(p, "cn", n);
                CheckAllFilterGroups(p, "rs", "cn", n);

                p.Release("fs").Release("fc").Release("fid").Release("fl").Call("rs", "Close").Release("rs");
                p.Pause(1500);
                KeepAllPages(p, "after-pause-live", n.Next("pg"));
                AdoClose(p, "cn2");
                AdoClose(p, "cn");
                KeepAllPages(p, "after-close", n.Next("pg"));
                p.Release("eng");
            });

        // 14. Clone edits when original is filtered + Clone CancelBatch & UpdateBatch(adAffectCurrent)
        yield return Q("batch-clone-filtered-cancel-accdb", BatchRefs, MainAccdb,
            "ACCDB: closes ADO-BATCH-SCOPE open edge: original client batch Recordset of W has Filter='ID <= 4'; Clone edits row 6 (outside original filter) and row 2 (inside original filter); " +
            "inspects original RecordCount/Filter/Status of row 2 and adFilterPendingRecords (does row 6 show?); Clone calls CancelBatch(adAffectCurrent) on row 2 and UpdateBatch(adAffectCurrent) on row 6. " +
            "Keeps all pages before/after UpdateBatch, pause, and Close.",
            p =>
            {
                var n = new Names();
                StartWriting(p);
                AdoOpen(p, "cn", readOnly: false, captureModules: true);
                AdoRs(p, "rs", SelW, "cn", Ado.UseClient, Ado.OpenStatic, Ado.LockBatchOptimistic);
                p.Put("rs", "Filter", A.Str("ID <= 4")).Get("rs", "RecordCount")
                    .CallBind("rc", "rs", "Clone").Get("rc", "Filter").Get("rc", "RecordCount");

                p.GetBind("cf", "rc", "Fields").Item("cs", "cf", A.Str("S"))
                    .Call("rc", "MoveFirst").Call("rc", "MoveNext").Put("cs", "Value", A.Str("cl2")).Call("rc", "Update") // row 2
                    .Call("rc", "Move", A.I4(4)).Put("cs", "Value", A.Str("cl6")).Call("rc", "Update"); // row 6

                // Check original while still filtered to ID <= 4
                p.Get("rs", "RecordCount").Get("rs", "Filter")
                    .Call("rs", "MoveFirst").Call("rs", "MoveNext")
                    .GetBind("rf", "rs", "Fields").Item("rs_s", "rf", A.Str("S"));
                RowChannels(p, "rs", "rs_s", "cn", n);

                // Switch original to adFilterPendingRecords
                p.Put("rs", "Filter", A.I4(Ado.FilterPending)).Get("rs", "RecordCount").Call("rs", "MoveFirst");
                DumpBatchRows(p, "rs", "S", 4);
                p.Put("rs", "Filter", A.Str("ID <= 4"));

                // Clone cancels row 2 via CancelBatch(adAffectCurrent)
                p.Call("rc", "MoveFirst").Call("rc", "MoveNext").Call("rc", "CancelBatch", A.I4(Ado.AffectCurrent));
                AdoErrorsClear(p, "cn", n);
                p.Call("rs", "MoveFirst").Call("rs", "MoveNext");
                RowChannels(p, "rs", "rs_s", "cn", n);

                // Clone commits row 6 via UpdateBatch(adAffectCurrent)
                p.Call("rc", "MoveFirst").Call("rc", "Move", A.I4(5)).Call("rc", "UpdateBatch", A.I4(Ado.AffectCurrent));
                AdoErrorsClear(p, "cn", n);
                KeepAllPages(p, "after-clone-ub-current", n.Next("pg"));
                p.Put("rs", "Filter", A.I4(Ado.FilterPending)).Get("rs", "RecordCount")
                    .Put("rs", "Filter", A.I4(Ado.FilterNone)).Call("rs", "MoveFirst").Call("rs", "Move", A.I4(5));
                RowChannels(p, "rs", "rs_s", "cn", n);

                p.Release("rs_s").Release("rf").Release("cs").Release("cf")
                    .Call("rc", "Close").Release("rc").Call("rs", "Close").Release("rs");
                p.Pause(1500);
                KeepAllPages(p, "after-pause-live", n.Next("pg"));
                AdoClose(p, "cn");
                KeepAllPages(p, "after-close", n.Next("pg"));
                AdoReadBackW(p, n, "ID IN (2, 6)");
                p.Release("eng");
            });
    }

    private static Probe DeleteScopesProbe(string name, string fixture, string fmt) =>
        Q(name, BatchRefs, fixture,
            $"{fmt}: client static batch-optimistic (3,3,4) Recordset.Delete scopes on W: default Delete(), Delete(adAffectCurrent=1) on existing / modified / pending-AddNew rows and at BOF/EOF, " +
            "Delete(adAffectGroup=2) with string Filter 'ID >= 9 AND ID <= 11', with Filter=adFilterNone, and with empty Filter 'ID > 99', " +
            "Delete(adAffectAll=3), and invalid Delete(99). Then UpdateBatch(adAffectAll), keeping all pages before/after UpdateBatch, pause, and Close.",
            p =>
            {
                var n = new Names();
                StartWriting(p);
                AdoOpen(p, "cn", readOnly: false, captureModules: true);
                AdoRs(p, "rs", SelW, "cn", Ado.UseClient, Ado.OpenStatic, Ado.LockBatchOptimistic);
                p.GetBind("fl", "rs", "Fields").Item("fid", "fl", A.Str("ID")).Item("fc", "fl", A.Str("Code")).Item("fs", "fl", A.Str("S"));

                // 1. Default Delete() on existing row 1
                p.Call("rs", "Delete").Get("rs", "Status").Get("rs", "EditMode").Get("rs", "RecordCount");
                AdoErrorsClear(p, "cn", n);

                // 2. Delete(adAffectCurrent) on modified row 2
                p.Call("rs", "MoveNext").Put("fs", "Value", A.Str("moddel")).Call("rs", "Update")
                    .Call("rs", "Delete", A.I4(Ado.AffectCurrent)).Get("rs", "Status").Get("rs", "RecordCount");
                AdoErrorsClear(p, "cn", n);

                // 3. Delete(adAffectCurrent) on pending AddNew row (ID 90)
                p.Call("rs", "AddNew").Put("fid", "Value", A.I4(90)).Put("fc", "Value", A.Str("w90")).Put("fs", "Value", A.Str("newdel")).Call("rs", "Update")
                    .Get("rs", "RecordCount").Call("rs", "Delete", A.I4(Ado.AffectCurrent))
                    .Get("rs", "Status").Get("rs", "RecordCount");
                AdoErrorsClear(p, "cn", n);

                // 4. Delete at BOF and EOF
                p.Call("rs", "MoveFirst").Call("rs", "MovePrevious").Get("rs", "BOF").Call("rs", "Delete", A.I4(Ado.AffectCurrent));
                AdoErrorsClear(p, "cn", n);
                p.Call("rs", "MoveLast").Call("rs", "MoveNext").Get("rs", "EOF").Call("rs", "Delete", A.I4(Ado.AffectCurrent));
                AdoErrorsClear(p, "cn", n);

                // 5. Delete(adAffectGroup) with Filter=adFilterNone (refused?) & empty Filter & string Filter ID 9..11
                p.Call("rs", "MoveFirst").Call("rs", "Delete", A.I4(Ado.AffectGroup));
                AdoErrorsClear(p, "cn", n);
                p.Put("rs", "Filter", A.Str("ID > 99")).Get("rs", "RecordCount").Call("rs", "Delete", A.I4(Ado.AffectGroup));
                AdoErrorsClear(p, "cn", n);
                p.Put("rs", "Filter", A.Str("ID >= 9 AND ID <= 11")).Get("rs", "RecordCount")
                    .Call("rs", "Delete", A.I4(Ado.AffectGroup));
                AdoErrorsClear(p, "cn", n);
                p.Get("rs", "RecordCount").Put("rs", "Filter", A.I4(Ado.FilterNone)).Get("rs", "RecordCount");

                // 6. Delete(adAffectAll=3) and invalid Delete(99) on separate Recordset so we don't wipe W before testing
                AdoRs(p, "ra", SelW, "cn", Ado.UseClient, Ado.OpenStatic, Ado.LockBatchOptimistic);
                p.Call("ra", "Delete", A.I4(Ado.AffectAll)).Get("ra", "RecordCount");
                AdoErrorsClear(p, "cn", n);
                p.Call("ra", "Delete", A.I4(99));
                AdoErrorsClear(p, "cn", n);
                p.Call("ra", "CancelBatch", A.I4(Ado.AffectAll)).Call("ra", "Close").Release("ra");

                // Check adFilterPendingRecords count (rows 1, 2, 9, 10, 11 = 5; row 90 gone!)
                p.Put("rs", "Filter", A.I4(Ado.FilterPending)).Get("rs", "RecordCount").Put("rs", "Filter", A.I4(Ado.FilterNone));
                KeepAllPages(p, "after-local-deletes", n.Next("pg"));

                p.Call("rs", "UpdateBatch", A.I4(Ado.AffectAll));
                AdoErrorsClear(p, "cn", n);
                KeepAllPages(p, "after-delete-updatebatch", n.Next("pg"));
                p.Get("rs", "RecordCount");

                p.Release("fs").Release("fc").Release("fid").Release("fl").Call("rs", "Close").Release("rs");
                p.Pause(1500);
                KeepAllPages(p, "after-pause-live", n.Next("pg"));
                AdoClose(p, "cn");
                KeepAllPages(p, "after-close", n.Next("pg"));
                AdoReadBackW(p, n, "ID >= 1");
                p.Release("eng");
            });

    // ================================================================================================
    // Part A3 — Resync Discriminators, Omitted Args, Value Witness Status, Explicit Disconnect (5 probes)

    private static IEnumerable<Probe> PartA3ResyncValuesDisconnectProbes()
    {
        // 15. adResyncUnderlyingValues trigger discriminator (ACCDB)
        yield return Q("resync-underlying-trigger-accdb", ResyncRefs, MainAccdb,
            "ACCDB: closes ADO-RESYNC open question: tests whether clean vs pending-edit vs conflicted (post-failed-UpdateBatch) rows refresh UnderlyingValue under Resync(..., adResyncUnderlyingValues). " +
            "rs1 (NO failed UpdateBatch): row 1 pending edit V=120, row 4 clean V=40; cn2 updates rows 1..4 to 110..410; 8s pause; Resync(adAffectAll, adResyncUnderlyingValues) on rs1 checks BOTH row 1 and clean row 4. " +
            "rs2 (WITH failed UpdateBatch): row 2 V=220 and row 3 V=320; row 2 hits 0x80040E38 -> Resync(adAffectCurrent, adResyncUnderlyingValues) -> UpdateBatch(adAffectCurrent) succeeds; " +
            "row 3 hits 0x80040E38 -> Filter=adFilterConflictingRecords -> Resync(adAffectGroup, adResyncUnderlyingValues) -> UpdateBatch(adAffectGroup) succeeds. Keeps all pages.",
            p =>
            {
                var n = new Names();
                StartWriting(p);
                AdoOpen(p, "cn", readOnly: false, captureModules: true);
                AdoOpen(p, "cn2", readOnly: false);

                AdoRs(p, "rs1", SelW, "cn", Ado.UseClient, Ado.OpenStatic, Ado.LockBatchOptimistic);
                AdoRs(p, "rs2", SelW, "cn", Ado.UseClient, Ado.OpenStatic, Ado.LockBatchOptimistic);
                p.GetBind("fl1", "rs1", "Fields").Item("fv1", "fl1", A.Str("V"))
                    .GetBind("fl2", "rs2", "Fields").Item("fv2", "fl2", A.Str("V"));

                // rs1: stage row 1 only (row 4 stays clean); rs2: stage rows 2 & 3
                p.Put("fv1", "Value", A.I4(120)).Call("rs1", "Update")
                    .Call("rs2", "MoveNext").Put("fv2", "Value", A.I4(220)).Call("rs2", "Update")
                    .Call("rs2", "MoveNext").Put("fv2", "Value", A.I4(320)).Call("rs2", "Update");

                // External updates on rows 1..4
                ExecNo(p, "cn2", "UPDATE W SET V = 110 WHERE ID = 1");
                ExecNo(p, "cn2", "UPDATE W SET V = 210 WHERE ID = 2");
                ExecNo(p, "cn2", "UPDATE W SET V = 310 WHERE ID = 3");
                ExecNo(p, "cn2", "UPDATE W SET V = 410 WHERE ID = 4");
                AdoErrorsClear(p, "cn2", n);
                KeepAllPages(p, "after-ext-updates", n.Next("pg"));
                p.Pause(8000);

                // Case 1 (rs1, NO failed UpdateBatch): check pending row 1 AND clean row 4 before & after Resync(adAffectAll, adResyncUnderlyingValues)
                p.Call("rs1", "MoveFirst");
                RowChannels(p, "rs1", "fv1", "cn", n);
                p.Call("rs1", "Move", A.I4(3));
                RowChannels(p, "rs1", "fv1", "cn", n); // clean row 4 before
                p.Call("rs1", "Resync", A.I4(Ado.AffectAll), A.I4(Ado.ResyncUnderlying));
                AdoErrorsClear(p, "cn", n);
                p.Call("rs1", "MoveFirst");
                RowChannels(p, "rs1", "fv1", "cn", n); // pending row 1 after
                p.Call("rs1", "Move", A.I4(3));
                RowChannels(p, "rs1", "fv1", "cn", n); // clean row 4 after
                p.Call("rs1", "CancelBatch", A.I4(Ado.AffectAll));
                p.Release("fv1").Release("fl1").Call("rs1", "Close").Release("rs1");

                // Case 2 (rs2, WITH failed UpdateBatch): row 2 conflicts 0x80040E38 -> Resync(adAffectCurrent, Underlying) -> UpdateBatch(adAffectCurrent)
                p.Call("rs2", "MoveFirst").Call("rs2", "MoveNext").Call("rs2", "UpdateBatch", A.I4(Ado.AffectCurrent));
                AdoErrorsClear(p, "cn", n);
                RowChannels(p, "rs2", "fv2", "cn", n);
                p.Call("rs2", "Resync", A.I4(Ado.AffectCurrent), A.I4(Ado.ResyncUnderlying));
                AdoErrorsClear(p, "cn", n);
                RowChannels(p, "rs2", "fv2", "cn", n);
                p.Call("rs2", "UpdateBatch", A.I4(Ado.AffectCurrent));
                AdoErrorsClear(p, "cn", n);
                RowChannels(p, "rs2", "fv2", "cn", n);
                KeepAllPages(p, "after-row2-resync-ub", n.Next("pg"));

                // Case 3 (rs2, WITH failed UpdateBatch): row 3 conflicts -> Filter=adFilterConflictingRecords -> Resync(adAffectGroup, Underlying) -> UpdateBatch(adAffectGroup)
                p.Call("rs2", "MoveNext").Call("rs2", "UpdateBatch", A.I4(Ado.AffectCurrent));
                AdoErrorsClear(p, "cn", n);
                p.Put("rs2", "Filter", A.I4(Ado.FilterConflicting)).Get("rs2", "RecordCount").Call("rs2", "MoveFirst");
                RowChannels(p, "rs2", "fv2", "cn", n);
                p.Call("rs2", "Resync", A.I4(Ado.AffectGroup), A.I4(Ado.ResyncUnderlying));
                AdoErrorsClear(p, "cn", n);
                RowChannels(p, "rs2", "fv2", "cn", n);
                p.Call("rs2", "UpdateBatch", A.I4(Ado.AffectGroup));
                AdoErrorsClear(p, "cn", n);
                KeepAllPages(p, "after-row3-group-ub", n.Next("pg"));
                p.Put("rs2", "Filter", A.I4(Ado.FilterNone));
                p.Release("fv2").Release("fl2").Call("rs2", "Close").Release("rs2");

                p.Pause(1500);
                KeepAllPages(p, "after-pause-live", n.Next("pg"));
                AdoClose(p, "cn2");
                AdoClose(p, "cn");
                KeepAllPages(p, "after-close", n.Next("pg"));
                AdoReadBackW(p, n, "ID <= 4");
                p.Release("eng");
            });

        // 16. Resync omitted args, adAffectGroup with active Filters, and adResyncAllValues over local pending edit (ACCDB)
        yield return Q("resync-omitted-and-group-accdb", ResyncRefs, MainAccdb,
            "ACCDB: tests Resync() with both args omitted, Resync(adAffectCurrent) with 2nd arg omitted, " +
            "Resync(adAffectGroup, adResyncAllValues) with string Filter 'ID <= 2', with adFilterPendingRecords, and with empty Filter 'ID > 99', " +
            "plus Resync(adAffectAll, adResyncAllValues) over a row with a local pending edit (checks Value/OriginalValue/UnderlyingValue/Status/EditMode). Keeps all pages.",
            p =>
            {
                var n = new Names();
                StartWriting(p);
                AdoOpen(p, "cn", readOnly: false, captureModules: true);
                AdoOpen(p, "cn2", readOnly: false);
                AdoRs(p, "rs", SelW, "cn", Ado.UseClient, Ado.OpenStatic, Ado.LockBatchOptimistic);
                p.GetBind("fl", "rs", "Fields").Item("fv", "fl", A.Str("V"));

                // Round 1: external update IDs 1..4 to 901..904, 8s pause
                ExecNo(p, "cn2", "UPDATE W SET V = 900 + ID WHERE ID <= 4");
                AdoErrorsClear(p, "cn2", n);
                KeepAllPages(p, "after-ext1", n.Next("pg"));
                p.Pause(8000);

                // Resync(adAffectCurrent) with 2nd arg omitted (defaults to adResyncAllValues=2?)
                p.Call("rs", "MoveFirst").Call("rs", "Resync", A.I4(Ado.AffectCurrent));
                AdoErrorsClear(p, "cn", n);
                ReadIdsAndV(p, "rs", 4);

                // Resync(adAffectGroup, adResyncAllValues) with string Filter "ID <= 2"
                p.Put("rs", "Filter", A.Str("ID <= 2")).Call("rs", "Resync", A.I4(Ado.AffectGroup), A.I4(Ado.ResyncAll));
                AdoErrorsClear(p, "cn", n);
                p.Put("rs", "Filter", A.I4(Ado.FilterNone));
                ReadIdsAndV(p, "rs", 4);

                // Resync(adAffectGroup, adResyncAllValues) with empty Filter "ID > 99"
                p.Put("rs", "Filter", A.Str("ID > 99")).Call("rs", "Resync", A.I4(Ado.AffectGroup), A.I4(Ado.ResyncAll));
                AdoErrorsClear(p, "cn", n);
                p.Put("rs", "Filter", A.I4(Ado.FilterNone));

                // Resync() with both args omitted (defaults to adAffectAll, adResyncAllValues?)
                p.Call("rs", "Resync");
                AdoErrorsClear(p, "cn", n);
                ReadIdsAndV(p, "rs", 4);

                // Round 2: stage local pending edit on row 3 (V=333) while cn2 sets V=993; check Resync(adAffectGroup, adResyncAllValues) on adFilterPendingRecords
                p.Call("rs", "MoveFirst").Call("rs", "Move", A.I4(2)).Put("fv", "Value", A.I4(333)).Call("rs", "Update");
                ExecNo(p, "cn2", "UPDATE W SET V = 993 WHERE ID = 3");
                AdoErrorsClear(p, "cn2", n);
                KeepAllPages(p, "after-ext2", n.Next("pg"));
                p.Pause(8000);
                p.Put("rs", "Filter", A.I4(Ado.FilterPending)).Get("rs", "RecordCount")
                    .Call("rs", "Resync", A.I4(Ado.AffectGroup), A.I4(Ado.ResyncAll));
                AdoErrorsClear(p, "cn", n);
                RowChannels(p, "rs", "fv", "cn", n);
                p.Put("rs", "Filter", A.I4(Ado.FilterNone)).Call("rs", "MoveFirst").Call("rs", "Move", A.I4(2));
                RowChannels(p, "rs", "fv", "cn", n);

                p.Release("fv").Release("fl").Call("rs", "Close").Release("rs");
                p.Pause(1500);
                KeepAllPages(p, "after-pause-live", n.Next("pg"));
                AdoClose(p, "cn2");
                AdoClose(p, "cn");
                KeepAllPages(p, "after-close", n.Next("pg"));
                p.Release("eng");
            });

        // 17. Resync adResyncAllValues vs adResyncUnderlyingValues on Jet 4 MDB
        yield return Q("resync-all-vs-underlying-mdb", ResyncRefs, MainMdb,
            "Jet 4 MDB: parity of Resync(adAffectCurrent, adResyncAllValues), external-delete Resync(adAffectAll, adResyncAllValues) (0x80040E23), " +
            "and conflict UpdateBatch (0x80040E38) -> Resync(adAffectCurrent, adResyncUnderlyingValues) -> second UpdateBatch. Keeps all pages.",
            p =>
            {
                var n = new Names();
                StartWriting(p);
                AdoOpen(p, "cn", readOnly: false, captureModules: true);
                AdoOpen(p, "cn2", readOnly: false);
                AdoRs(p, "rs", SelW, "cn", Ado.UseClient, Ado.OpenStatic, Ado.LockBatchOptimistic);
                p.GetBind("fl", "rs", "Fields").Item("fv", "fl", A.Str("V"));

                // Stage local edit on row 3 (V=320); cn2 updates row 1 (V=701), deletes row 2, updates row 3 (V=310)
                p.Call("rs", "Move", A.I4(2)).Put("fv", "Value", A.I4(320)).Call("rs", "Update");
                ExecNo(p, "cn2", "UPDATE W SET V = 701 WHERE ID = 1");
                ExecNo(p, "cn2", "DELETE FROM W WHERE ID = 2");
                ExecNo(p, "cn2", "UPDATE W SET V = 310 WHERE ID = 3");
                AdoErrorsClear(p, "cn2", n);
                KeepAllPages(p, "after-ext-writes", n.Next("pg"));
                p.Pause(8000);

                // Resync(adAffectCurrent, adResyncAllValues) on row 1
                p.Call("rs", "MoveFirst").Call("rs", "Resync", A.I4(Ado.AffectCurrent), A.I4(Ado.ResyncAll));
                AdoErrorsClear(p, "cn", n);
                RowChannels(p, "rs", "fv", "cn", n);

                // Conflict on row 3 -> UpdateBatch fails -> Resync(adAffectCurrent, adResyncUnderlyingValues) -> UpdateBatch succeeds
                p.Call("rs", "Move", A.I4(2)).Call("rs", "UpdateBatch", A.I4(Ado.AffectCurrent));
                AdoErrorsClear(p, "cn", n);
                RowChannels(p, "rs", "fv", "cn", n);
                p.Call("rs", "Resync", A.I4(Ado.AffectCurrent), A.I4(Ado.ResyncUnderlying));
                AdoErrorsClear(p, "cn", n);
                RowChannels(p, "rs", "fv", "cn", n);
                p.Call("rs", "UpdateBatch", A.I4(Ado.AffectCurrent));
                AdoErrorsClear(p, "cn", n);
                RowChannels(p, "rs", "fv", "cn", n);
                KeepAllPages(p, "after-row3-ub", n.Next("pg"));

                // Resync(adAffectAll, adResyncAllValues) hits externally deleted row 2 (0x80040E23)
                p.Call("rs", "Resync", A.I4(Ado.AffectAll), A.I4(Ado.ResyncAll));
                AdoErrorsClear(p, "cn", n);
                p.Get("rs", "RecordCount");

                p.Release("fv").Release("fl").Call("rs", "Close").Release("rs");
                p.Pause(1500);
                KeepAllPages(p, "after-pause-live", n.Next("pg"));
                AdoClose(p, "cn2");
                AdoClose(p, "cn");
                KeepAllPages(p, "after-close", n.Next("pg"));
                AdoReadBackW(p, n, "ID <= 4");
                p.Release("eng");
            });

        // 18. Values witness with exact row-level Recordset.Status & paused read-back (closes ADO-VALUES Open)
        yield return Q("values-witness-status-accdb", ResyncRefs, MainAccdb,
            "ACCDB: closes ADO-VALUES open edge: full 100->120 / external 110 / failed UpdateBatch / Resync(Underlying) / second UpdateBatch / 130 sequence " +
            "recording exact row-level Recordset.Status, EditMode, Value/OriginalValue/UnderlyingValue, Errors, kept page bytes at every step, and 8s-paused independent read-back.",
            p =>
            {
                var n = new Names();
                StartWriting(p);
                AdoOpen(p, "cn", readOnly: false, captureModules: true);
                AdoOpen(p, "cn2", readOnly: false);
                ExecNo(p, "cn", "UPDATE W SET V = 100 WHERE ID = 1");
                KeepAllPages(p, "after-seed-100", n.Next("pg"));

                AdoRs(p, "rs", "SELECT ID, V FROM W WHERE ID = 1", "cn", Ado.UseClient, Ado.OpenStatic, Ado.LockBatchOptimistic);
                p.GetBind("fl", "rs", "Fields").Item("fv", "fl", A.Str("V"));
                RowChannels(p, "rs", "fv", "cn", n);

                p.Put("fv", "Value", A.I4(120)).Call("rs", "Update");
                RowChannels(p, "rs", "fv", "cn", n);

                ExecNo(p, "cn2", "UPDATE W SET V = 110 WHERE ID = 1");
                AdoErrorsClear(p, "cn2", n);
                KeepAllPages(p, "after-ext-110", n.Next("pg"));

                p.Call("rs", "UpdateBatch", A.I4(Ado.AffectAll));
                AdoErrorsClear(p, "cn", n);
                RowChannels(p, "rs", "fv", "cn", n);
                KeepAllPages(p, "after-failed-ub", n.Next("pg"));

                p.Call("rs", "Resync", A.I4(Ado.AffectAll), A.I4(Ado.ResyncUnderlying));
                AdoErrorsClear(p, "cn", n);
                RowChannels(p, "rs", "fv", "cn", n);

                p.Call("rs", "UpdateBatch", A.I4(Ado.AffectAll));
                AdoErrorsClear(p, "cn", n);
                RowChannels(p, "rs", "fv", "cn", n);
                KeepAllPages(p, "after-second-ub-120", n.Next("pg"));

                p.Pause(8000);
                AdoExecRead(p, "cn2", n, "SELECT ID, V FROM W WHERE ID = 1", 2);

                p.Put("fv", "Value", A.I4(130)).Call("rs", "Update").Call("rs", "UpdateBatch", A.I4(Ado.AffectAll));
                AdoErrorsClear(p, "cn", n);
                RowChannels(p, "rs", "fv", "cn", n);
                KeepAllPages(p, "after-third-ub-130", n.Next("pg"));

                p.Release("fv").Release("fl").Call("rs", "Close").Release("rs");
                p.Pause(1500);
                KeepAllPages(p, "after-pause-live", n.Next("pg"));
                AdoClose(p, "cn2");
                AdoClose(p, "cn");
                KeepAllPages(p, "after-close", n.Next("pg"));
                AdoReadBackW(p, n, "ID = 1");
                p.Release("eng");
            });

        // 19. Explicit disconnect via ActiveConnection = Empty / "" (while cn1 stays open) + local ops + reconnect cn2 + UpdateBatch
        yield return Q("disconnect-put-empty-reconnect-accdb", BatchRefs, MainAccdb,
            "ACCDB: explicit disconnect of an open client static batch-optimistic (3,3,4) Recordset while cn1 remains open: " +
            "tests Put(ActiveConnection, '') and Put(ActiveConnection, Empty), checks State/ActiveConnection/RecordCount, performs local edit/AddNew/Delete/Filter/Sort/Find/CancelBatch(Current), " +
            "attempts UpdateBatch & Resync while disconnected (exact errors), reconnects via PutRef(ActiveConnection, cn2), and runs UpdateBatch(adAffectAll). Keeps all pages.",
            p =>
            {
                var n = new Names();
                StartWriting(p);
                AdoOpen(p, "cn1", readOnly: false, captureModules: true);
                AdoRs(p, "rs", SelW, "cn1", Ado.UseClient, Ado.OpenStatic, Ado.LockBatchOptimistic);
                p.GetBind("fl", "rs", "Fields").Item("fid", "fl", A.Str("ID")).Item("fc", "fl", A.Str("Code")).Item("fs", "fl", A.Str("S"));

                // Try Put("", Empty) while cn1 stays open
                p.Put("rs", "ActiveConnection", A.Str("")).Get("rs", "State").Get("rs", "ActiveConnection");
                AdoErrorsClear(p, "cn1", n);
                p.Put("rs", "ActiveConnection", A.Empty).Get("rs", "State").Get("rs", "ActiveConnection").Get("cn1", "State");
                AdoErrorsClear(p, "cn1", n);

                // Local operations while disconnected
                p.Put("fs", "Value", A.Str("d1")).Call("rs", "Update")
                    .Call("rs", "MoveNext").Call("rs", "Delete")
                    .Call("rs", "AddNew").Put("fid", "Value", A.I4(35)).Put("fc", "Value", A.Str("w35")).Put("fs", "Value", A.Str("d35")).Call("rs", "Update")
                    .Put("rs", "Sort", A.Str("ID DESC")).Call("rs", "MoveFirst").Get("fid", "Value")
                    .Put("rs", "Sort", A.Str("")).Call("rs", "MoveFirst").Call("rs", "Find", A.Str("ID = 35")).Get("fid", "Value")
                    .Put("rs", "Filter", A.I4(Ado.FilterPending)).Get("rs", "RecordCount").Put("rs", "Filter", A.I4(Ado.FilterNone));
                RowChannels(p, "rs", "fs", "cn1", n);

                // CancelBatch(adAffectCurrent) on row 1 while disconnected
                p.Call("rs", "MoveFirst").Call("rs", "CancelBatch", A.I4(Ado.AffectCurrent)).Get("rs", "Status");

                // Attempt UpdateBatch and Resync while disconnected
                p.Call("rs", "UpdateBatch", A.I4(Ado.AffectAll));
                AdoErrorsClear(p, "cn1", n);
                p.Call("rs", "Resync", A.I4(Ado.AffectAll), A.I4(Ado.ResyncAll));
                AdoErrorsClear(p, "cn1", n);
                KeepAllPages(p, "after-disconnected-ops", n.Next("pg"));

                // Reconnect to fresh cn2 (close cn1 first) and UpdateBatch
                AdoClose(p, "cn1");
                AdoOpen(p, "cn2", readOnly: false);
                p.PutRef("rs", "ActiveConnection", "cn2").Get("rs", "State")
                    .Call("rs", "UpdateBatch", A.I4(Ado.AffectAll));
                AdoErrorsClear(p, "cn2", n);
                KeepAllPages(p, "after-reconnect-updatebatch", n.Next("pg"));

                p.Release("fs").Release("fc").Release("fid").Release("fl").Call("rs", "Close").Release("rs");
                p.Pause(1500);
                KeepAllPages(p, "after-pause-live", n.Next("pg"));
                AdoClose(p, "cn2");
                KeepAllPages(p, "after-close", n.Next("pg"));
                AdoReadBackW(p, n, "ID <= 3 OR ID = 35");
                p.Release("eng");
            });
    }

    // ================================================================================================
    // Part A4 — Concurrency Conflicts, Multi-Row Partial Failures, Delete Conflicts & MDB Parity (6 probes)

    private static IEnumerable<Probe> PartA4ConflictProbes()
    {
        // 20. Last-row conflict with exact Recordset.Status per row + kept page bytes + read-back (closes ADO-BATCH Open)
        yield return Q("conflict-update-last-status-accdb", BatchRefs, MainAccdb,
            "ACCDB: closes ADO-BATCH open edge: stages rows 1..3 (S='b1'..'b3'), cn2 updates row 3 ('ext3'), UpdateBatch(adAffectAll); " +
            "records exact per-row Recordset.Status (rows 1..4), adFilterConflictingRecords, Value/OriginalValue/UnderlyingValue, Resync(Underlying), second UpdateBatch, " +
            "kept page bytes at every boundary (showing rows 1 & 2 committed while row 3 conflicted), and fresh read-back after Close.",
            p =>
            {
                var n = new Names();
                StartWriting(p);
                AdoOpen(p, "cn", readOnly: false, captureModules: true);
                AdoOpen(p, "cn2", readOnly: false);
                AdoRs(p, "rs", SelW, "cn", Ado.UseClient, Ado.OpenStatic, Ado.LockBatchOptimistic);
                p.GetBind("fl", "rs", "Fields").Item("fs", "fl", A.Str("S"));

                p.Put("fs", "Value", A.Str("b1")).Call("rs", "Update")
                    .Call("rs", "MoveNext").Put("fs", "Value", A.Str("b2")).Call("rs", "Update")
                    .Call("rs", "MoveNext").Put("fs", "Value", A.Str("b3")).Call("rs", "Update");
                ExecNo(p, "cn2", "UPDATE W SET S = 'ext3' WHERE ID = 3");
                AdoErrorsClear(p, "cn2", n);
                KeepAllPages(p, "after-ext-row3", n.Next("pg"));

                p.Call("rs", "UpdateBatch", A.I4(Ado.AffectAll));
                AdoErrorsClear(p, "cn", n);
                KeepAllPages(p, "after-partial-ub", n.Next("pg"));
                DumpBatchRows(p, "rs", "S", 4);

                p.Put("rs", "Filter", A.I4(Ado.FilterConflicting)).Get("rs", "RecordCount");
                DumpBatchRows(p, "rs", "S", 4);
                p.Put("rs", "Filter", A.I4(Ado.FilterNone))
                    .Call("rs", "Resync", A.I4(Ado.AffectAll), A.I4(Ado.ResyncUnderlying));
                AdoErrorsClear(p, "cn", n);
                DumpBatchRows(p, "rs", "S", 4);

                p.Call("rs", "UpdateBatch", A.I4(Ado.AffectAll));
                AdoErrorsClear(p, "cn", n);
                KeepAllPages(p, "after-second-ub", n.Next("pg"));
                DumpBatchRows(p, "rs", "S", 4);

                p.Release("fs").Release("fl").Call("rs", "Close").Release("rs");
                p.Pause(1500);
                KeepAllPages(p, "after-pause-live", n.Next("pg"));
                AdoClose(p, "cn2");
                AdoClose(p, "cn");
                KeepAllPages(p, "after-close", n.Next("pg"));
                AdoReadBackW(p, n, "ID <= 4");
                p.Release("eng");
            });

        // 21. Multi-conflict single UpdateBatch: update-vs-update (row 1) + update-vs-delete (row 2) + clean (row 3) + diff-col (row 4)
        yield return Q("conflict-update-delete-mixed-accdb", BatchRefs, MainAccdb,
            "ACCDB: single UpdateBatch(adAffectAll) with 4 staged edits: row 1 externally updated on same col S, row 2 externally deleted, row 3 clean, row 4 externally updated on different col V (default Update Criteria=2). " +
            "Captures Connection.Errors.Count and every Error, per-row Recordset.Status and channels, adFilterConflictingRecords, CancelBatch(adAffectGroup) on conflicting rows, and kept page bytes.",
            p =>
            {
                var n = new Names();
                StartWriting(p);
                AdoOpen(p, "cn", readOnly: false, captureModules: true);
                AdoOpen(p, "cn2", readOnly: false);
                AdoRs(p, "rs", SelW, "cn", Ado.UseClient, Ado.OpenStatic, Ado.LockBatchOptimistic);
                p.GetBind("fl", "rs", "Fields").Item("fs", "fl", A.Str("S"));

                p.Put("fs", "Value", A.Str("m1")).Call("rs", "Update")
                    .Call("rs", "MoveNext").Put("fs", "Value", A.Str("m2")).Call("rs", "Update")
                    .Call("rs", "MoveNext").Put("fs", "Value", A.Str("m3")).Call("rs", "Update")
                    .Call("rs", "MoveNext").Put("fs", "Value", A.Str("m4")).Call("rs", "Update");

                ExecNo(p, "cn2", "UPDATE W SET S = 'ext1' WHERE ID = 1");
                ExecNo(p, "cn2", "DELETE FROM W WHERE ID = 2");
                ExecNo(p, "cn2", "UPDATE W SET V = 444 WHERE ID = 4");
                AdoErrorsClear(p, "cn2", n);
                KeepAllPages(p, "after-ext-writes", n.Next("pg"));

                p.Call("rs", "UpdateBatch", A.I4(Ado.AffectAll));
                AdoErrorsClear(p, "cn", n);
                KeepAllPages(p, "after-mixed-conflict-ub", n.Next("pg"));
                DumpBatchRows(p, "rs", "S", 5);

                p.Put("rs", "Filter", A.I4(Ado.FilterConflicting)).Get("rs", "RecordCount");
                DumpBatchRows(p, "rs", "S", 4);

                // CancelBatch(adAffectGroup) while Filter = adFilterConflictingRecords
                p.Call("rs", "CancelBatch", A.I4(Ado.AffectGroup));
                AdoErrorsClear(p, "cn", n);
                p.Put("rs", "Filter", A.I4(Ado.FilterPending)).Get("rs", "RecordCount")
                    .Put("rs", "Filter", A.I4(Ado.FilterConflicting)).Get("rs", "RecordCount")
                    .Put("rs", "Filter", A.I4(Ado.FilterNone)).Get("rs", "RecordCount");

                p.Release("fs").Release("fl").Call("rs", "Close").Release("rs");
                p.Pause(1500);
                KeepAllPages(p, "after-pause-live", n.Next("pg"));
                AdoClose(p, "cn2");
                AdoClose(p, "cn");
                KeepAllPages(p, "after-close", n.Next("pg"));
                AdoReadBackW(p, n, "ID <= 5");
                p.Release("eng");
            });

        // 22. Local Delete vs external Update (Criteria 2 vs Criteria 0) and local Delete vs external Delete (ACCDB)
        yield return Q("conflict-local-delete-ext-mod-accdb", BatchRefs, MainAccdb,
            "ACCDB: tests local rs.Delete conflicts: (a) under default Update Criteria=2, local Delete of row 1 (cn2 updated S), row 2 (cn2 already deleted), and row 3 (untouched) -> UpdateBatch(adAffectAll); " +
            "(b) under Update Criteria=0 (adCriteriaKey), local Delete of row 4 (cn2 updated S) -> UpdateBatch(adAffectAll). " +
            "Captures HRESULT, Errors, Recordset.Status, adFilterConflictingRecords, and kept page bytes.",
            p =>
            {
                var n = new Names();
                StartWriting(p);
                AdoOpen(p, "cn", readOnly: false, captureModules: true);
                AdoOpen(p, "cn2", readOnly: false);

                // (a) Update Criteria = 2 (default)
                AdoRs(p, "rs1", SelW, "cn", Ado.UseClient, Ado.OpenStatic, Ado.LockBatchOptimistic);
                p.Call("rs1", "Delete")                                 // row 1
                    .Call("rs1", "MoveNext").Call("rs1", "Delete")      // row 2
                    .Call("rs1", "MoveNext").Call("rs1", "Delete");     // row 3
                ExecNo(p, "cn2", "UPDATE W SET S = 'extd1' WHERE ID = 1");
                ExecNo(p, "cn2", "DELETE FROM W WHERE ID = 2");
                AdoErrorsClear(p, "cn2", n);
                KeepAllPages(p, "after-ext-a", n.Next("pg"));

                p.Call("rs1", "UpdateBatch", A.I4(Ado.AffectAll));
                AdoErrorsClear(p, "cn", n);
                KeepAllPages(p, "after-del-conflict-a", n.Next("pg"));
                p.Put("rs1", "Filter", A.I4(Ado.FilterConflicting)).Get("rs1", "RecordCount")
                    .Until("rs1", "EOF", 6, b => b.Get("rs1", "Status").Call("rs1", "MoveNext"))
                    .Put("rs1", "Filter", A.I4(Ado.FilterPending)).Get("rs1", "RecordCount")
                    .Call("rs1", "CancelBatch", A.I4(Ado.AffectAll)).Call("rs1", "Close").Release("rs1");

                // (b) Update Criteria = 0 (adCriteriaKey): local Delete of row 4 when cn2 updated S of row 4
                AdoRs(p, "rs2", SelW, "cn", Ado.UseClient, Ado.OpenStatic, Ado.LockBatchOptimistic);
                p.GetBind("p2", "rs2", "Properties").Item("uc2", "p2", A.Str("Update Criteria")).Put("uc2", "Value", A.I4(0)).Release("uc2").Release("p2")
                    .Call("rs2", "Find", A.Str("ID = 4")).Call("rs2", "Delete");
                ExecNo(p, "cn2", "UPDATE W SET S = 'extd4' WHERE ID = 4");
                AdoErrorsClear(p, "cn2", n);
                KeepAllPages(p, "after-ext-b", n.Next("pg"));

                p.Call("rs2", "UpdateBatch", A.I4(Ado.AffectAll));
                AdoErrorsClear(p, "cn", n);
                KeepAllPages(p, "after-del-conflict-b", n.Next("pg"));
                p.Put("rs2", "Filter", A.I4(Ado.FilterConflicting)).Get("rs2", "RecordCount")
                    .Call("rs2", "Close").Release("rs2");

                p.Pause(1500);
                KeepAllPages(p, "after-pause-live", n.Next("pg"));
                AdoClose(p, "cn2");
                AdoClose(p, "cn");
                KeepAllPages(p, "after-close", n.Next("pg"));
                AdoReadBackW(p, n, "ID <= 5");
                p.Release("eng");
            });

        // 23. Constraint-violation partial batch failure (unique Code dup, PK dup, Null PK) mixed with valid edits
        yield return Q("conflict-unique-dup-and-null-accdb", BatchRefs, MainAccdb,
            "ACCDB: partial UpdateBatch(adAffectAll) mixing valid edits (row 1, row 4) with constraint violations: " +
            "row 2 duplicate unique Code='w1', AddNew duplicate PK ID=1 (Code='w99'), and AddNew Null PK (Code='w98'). " +
            "Captures HRESULT, Connection.Errors, per-row Recordset.Status (adRecIntegrityViolation bits?), adFilterConflictingRecords, adFilterPendingRecords, and kept page bytes.",
            p =>
            {
                var n = new Names();
                StartWriting(p);
                AdoOpen(p, "cn", readOnly: false, captureModules: true);
                AdoRs(p, "rs", SelW, "cn", Ado.UseClient, Ado.OpenStatic, Ado.LockBatchOptimistic);
                p.GetBind("fl", "rs", "Fields").Item("fid", "fl", A.Str("ID")).Item("fc", "fl", A.Str("Code")).Item("fs", "fl", A.Str("S"));

                // Valid edit row 1, unique dup row 2, valid edit row 4
                p.Put("fs", "Value", A.Str("ok1")).Call("rs", "Update")
                    .Call("rs", "MoveNext").Put("fc", "Value", A.Str("w1")).Call("rs", "Update")
                    .Call("rs", "Move", A.I4(2)).Put("fs", "Value", A.Str("ok4")).Call("rs", "Update")
                    // AddNew dup PK ID=1
                    .Call("rs", "AddNew").Put("fid", "Value", A.I4(1)).Put("fc", "Value", A.Str("w99")).Put("fs", "Value", A.Str("duppk")).Call("rs", "Update")
                    // AddNew Null PK
                    .Call("rs", "AddNew").Put("fc", "Value", A.Str("w98")).Put("fs", "Value", A.Str("nullpk")).Call("rs", "Update");
                AdoErrorsClear(p, "cn", n);

                p.Call("rs", "UpdateBatch", A.I4(Ado.AffectAll));
                AdoErrorsClear(p, "cn", n);
                KeepAllPages(p, "after-constraint-ub", n.Next("pg"));

                // Inspect statuses of rows 1..4 and added rows
                p.Call("rs", "MoveFirst");
                DumpBatchRows(p, "rs", "Code", 4);
                p.Put("rs", "Filter", A.I4(Ado.FilterConflicting)).Get("rs", "RecordCount");
                DumpBatchRows(p, "rs", "Code", 6);
                p.Put("rs", "Filter", A.I4(Ado.FilterPending)).Get("rs", "RecordCount");
                DumpBatchRows(p, "rs", "Code", 6);

                p.Put("rs", "Filter", A.I4(Ado.FilterNone)).Call("rs", "CancelBatch", A.I4(Ado.AffectAll));
                p.Release("fs").Release("fc").Release("fid").Release("fl").Call("rs", "Close").Release("rs");
                p.Pause(1500);
                KeepAllPages(p, "after-pause-live", n.Next("pg"));
                AdoClose(p, "cn");
                KeepAllPages(p, "after-close", n.Next("pg"));
                AdoReadBackW(p, n, "ID <= 4");
                p.Release("eng");
            });

        // 24. Jet 4 MDB parity of mixed update/delete conflict + Resync(Underlying) + second UpdateBatch
        yield return Q("conflict-batch-mdb", BatchRefs, MainMdb,
            "Jet 4 MDB: parity of mixed batch concurrency conflict: stage edits on rows 1..3 while cn2 updates row 1 and deletes row 2; " +
            "UpdateBatch(adAffectAll) (partial: row 3 commits, rows 1 & 2 conflict), inspect Errors/Status/adFilterConflictingRecords, Resync(Underlying) + second UpdateBatch on row 1, " +
            "CancelBatch on row 2. Keeps all pages before/after every step, pause, and Close.",
            p =>
            {
                var n = new Names();
                StartWriting(p);
                AdoOpen(p, "cn", readOnly: false, captureModules: true);
                AdoOpen(p, "cn2", readOnly: false);
                AdoRs(p, "rs", SelW, "cn", Ado.UseClient, Ado.OpenStatic, Ado.LockBatchOptimistic);
                p.GetBind("fl", "rs", "Fields").Item("fs", "fl", A.Str("S"));

                p.Put("fs", "Value", A.Str("mb1")).Call("rs", "Update")
                    .Call("rs", "MoveNext").Put("fs", "Value", A.Str("mb2")).Call("rs", "Update")
                    .Call("rs", "MoveNext").Put("fs", "Value", A.Str("mb3")).Call("rs", "Update");

                ExecNo(p, "cn2", "UPDATE W SET S = 'mext1' WHERE ID = 1");
                ExecNo(p, "cn2", "DELETE FROM W WHERE ID = 2");
                AdoErrorsClear(p, "cn2", n);
                KeepAllPages(p, "after-ext-mdb", n.Next("pg"));

                p.Call("rs", "UpdateBatch", A.I4(Ado.AffectAll));
                AdoErrorsClear(p, "cn", n);
                KeepAllPages(p, "after-partial-conflict-mdb", n.Next("pg"));
                DumpBatchRows(p, "rs", "S", 4);

                p.Put("rs", "Filter", A.I4(Ado.FilterConflicting)).Get("rs", "RecordCount");
                DumpBatchRows(p, "rs", "S", 4);

                // Resolve row 1 via Resync(adAffectCurrent, adResyncUnderlyingValues) + UpdateBatch(adAffectCurrent); cancel row 2
                p.Put("rs", "Filter", A.I4(Ado.FilterNone)).Call("rs", "MoveFirst")
                    .Call("rs", "Resync", A.I4(Ado.AffectCurrent), A.I4(Ado.ResyncUnderlying));
                AdoErrorsClear(p, "cn", n);
                RowChannels(p, "rs", "fs", "cn", n);
                p.Call("rs", "UpdateBatch", A.I4(Ado.AffectCurrent));
                AdoErrorsClear(p, "cn", n);
                KeepAllPages(p, "after-row1-resubmit-mdb", n.Next("pg"));
                p.Call("rs", "CancelBatch", A.I4(Ado.AffectAll));
                AdoErrorsClear(p, "cn", n);

                p.Release("fs").Release("fl").Call("rs", "Close").Release("rs");
                p.Pause(1500);
                KeepAllPages(p, "after-pause-live", n.Next("pg"));
                AdoClose(p, "cn2");
                AdoClose(p, "cn");
                KeepAllPages(p, "after-close", n.Next("pg"));
                AdoReadBackW(p, n, "ID <= 4");
                p.Release("eng");
            });

        // 25. Jet 4 MDB parity of Update Criteria (0, 1, 2, 3) on same-col and diff-col external updates
        yield return Q("conflict-update-criteria-mdb", BatchRefs, MainMdb,
            "Jet 4 MDB: parity of Recordset property 'Update Criteria' values 0..3 on W: opens all 8 client batch Recordsets first (rows 1..4 same-col S vs S, rows 5..8 diff-col V2 vs S), " +
            "runs all 8 external UPDATEs on cn2 and closes cn2 (releasing Jet 4 page locks cleanly), then runs UpdateBatch on each Recordset. Records Errors, Recordset.Status, Value/OriginalValue, and kept page bytes.",
            p =>
            {
                var n = new Names();
                StartWriting(p);
                AdoOpen(p, "cn", readOnly: false, captureModules: true);

                // Open all 8 client Recordsets before external updates and configure Update Criteria 0..3
                for (int crit = 0; crit < 4; crit++)
                {
                    foreach (bool sameCol in new[] { true, false })
                    {
                        int id = sameCol ? crit + 1 : crit + 5;
                        string r = $"uc{crit}_{(sameCol ? "s" : "d")}";
                        AdoRs(p, r, $"SELECT ID, V2, S FROM W WHERE ID = {id}", "cn", Ado.UseClient, Ado.OpenStatic, Ado.LockBatchOptimistic);
                        p.GetBind(r + "p", r, "Properties").Item(r + "c", r + "p", A.Str("Update Criteria")).Put(r + "c", "Value", A.I4(crit)).Get(r + "c", "Value")
                            .Release(r + "c").Release(r + "p");
                    }
                }

                // External connection updates all 8 rows and closes cleanly so no Jet 4 page lock races UpdateBatch
                AdoOpen(p, "cn2", readOnly: false);
                for (int crit = 0; crit < 4; crit++)
                {
                    int idSame = crit + 1;
                    int idDiff = crit + 5;
                    ExecNo(p, "cn2", $"UPDATE W SET S = 'ext{idSame}' WHERE ID = {idSame}");
                    ExecNo(p, "cn2", $"UPDATE W SET V2 = V2 + 100 WHERE ID = {idDiff}");
                }
                AdoErrorsClear(p, "cn2", n);
                AdoClose(p, "cn2");
                KeepAllPages(p, "after-ext-mdb", n.Next("pg"));

                // Now edit S and call UpdateBatch on each of the 8 Recordsets
                for (int crit = 0; crit < 4; crit++)
                {
                    foreach (bool sameCol in new[] { true, false })
                    {
                        int id = sameCol ? crit + 1 : crit + 5;
                        string r = $"uc{crit}_{(sameCol ? "s" : "d")}";
                        p.GetBind(r + "f", r, "Fields").Item(r + "s", r + "f", A.Str("S"))
                            .Put(r + "s", "Value", A.Str("loc" + id)).Call(r, "Update")
                            .Call(r, "UpdateBatch", A.I4(Ado.AffectAll));
                        AdoErrorsClear(p, "cn", n);
                        RowChannels(p, r, r + "s", "cn", n);
                        p.Release(r + "s").Release(r + "f").Call(r, "Close").Release(r);
                    }
                }
                KeepAllPages(p, "after-criteria-mdb", n.Next("pg"));
                p.Pause(1500);
                KeepAllPages(p, "after-pause-live", n.Next("pg"));
                AdoClose(p, "cn");
                KeepAllPages(p, "after-close", n.Next("pg"));
                AdoReadBackW(p, n, "ID <= 8");
                p.Release("eng");
            });
    }

    // ================================================================================================
    // Part A5 — Transactions (BeginTrans/CommitTrans/RollbackTrans) around UpdateBatch + 0xE02 Bytes (5 probes)

    private static IEnumerable<Probe> PartA5TxnByteProbes()
    {
        // 26. BeginTrans -> UpdateBatch (update+insert+delete) -> CommitTrans with full page bytes (ACCDB)
        yield return TxnCommitProbe("txn-updatebatch-commit-accdb", MainAccdb, "ACCDB");

        // 27. BeginTrans -> UpdateBatch (update+insert+delete) -> RollbackTrans + client cache state + Resync/Requery (ACCDB)
        yield return TxnRollbackProbe("txn-updatebatch-rollback-accdb", MainAccdb, "ACCDB");

        // 28. BeginTrans -> partial-conflict UpdateBatch -> RollbackTrans (proves accepted rows 2 & 3 revert on disk!) (ACCDB)
        yield return Q("txn-updatebatch-partial-rollback-accdb", TxnRefs, MainAccdb,
            "ACCDB: BeginTrans on cn -> client batch Recordset of W stages rows 1..3 while cn2 updates row 1 outside txn -> UpdateBatch(adAffectAll) fails partially " +
            "(row 1 conflicts, rows 2 & 3 write inside txn) -> keeps all pages -> RollbackTrans -> keeps all pages -> inspects client cache (Status, Value/OriginalValue, adFilterPendingRecords), " +
            "pause, Close, keeps all pages, and verifies rows 2 & 3 reverted cleanly on disk.",
            p =>
            {
                var n = new Names();
                StartWriting(p);
                AdoOpen(p, "cn", readOnly: false, captureModules: true);
                AdoOpen(p, "cn2", readOnly: false);
                AdoRs(p, "rs", SelW, "cn", Ado.UseClient, Ado.OpenStatic, Ado.LockBatchOptimistic);
                p.GetBind("fl", "rs", "Fields").Item("fs", "fl", A.Str("S"));

                p.Put("fs", "Value", A.Str("tp1")).Call("rs", "Update")
                    .Call("rs", "MoveNext").Put("fs", "Value", A.Str("tp2")).Call("rs", "Update")
                    .Call("rs", "MoveNext").Put("fs", "Value", A.Str("tp3")).Call("rs", "Update");

                ExecNo(p, "cn2", "UPDATE W SET S = 'extp1' WHERE ID = 1");
                AdoErrorsClear(p, "cn2", n);
                KeepAllPages(p, "after-ext-outside-txn", n.Next("pg"));

                p.Call("cn", "BeginTrans").Call("rs", "UpdateBatch", A.I4(Ado.AffectAll));
                AdoErrorsClear(p, "cn", n);
                KeepAllPages(p, "after-partial-in-txn", n.Next("pg"));
                DumpBatchRows(p, "rs", "S", 4);

                p.Call("cn", "RollbackTrans");
                AdoErrorsClear(p, "cn", n);
                KeepAllPages(p, "after-rollback-live", n.Next("pg"));
                DumpBatchRows(p, "rs", "S", 4);
                p.Put("rs", "Filter", A.I4(Ado.FilterPending)).Get("rs", "RecordCount")
                    .Put("rs", "Filter", A.I4(Ado.FilterConflicting)).Get("rs", "RecordCount")
                    .Put("rs", "Filter", A.I4(Ado.FilterNone));

                p.Release("fs").Release("fl").Call("rs", "Close").Release("rs");
                p.Pause(1500);
                KeepAllPages(p, "after-pause-live", n.Next("pg"));
                AdoClose(p, "cn2");
                AdoClose(p, "cn");
                KeepAllPages(p, "after-close", n.Next("pg"));
                AdoReadBackW(p, n, "ID <= 4");
                p.Release("eng");
            });

        // 29. BeginTrans -> UpdateBatch -> CommitTrans with full page bytes (Jet 4 MDB)
        yield return TxnCommitProbe("txn-updatebatch-commit-mdb", MainMdb, "Jet 4 MDB");

        // 30. BeginTrans -> UpdateBatch -> RollbackTrans with full page bytes (Jet 4 MDB)
        yield return TxnRollbackProbe("txn-updatebatch-rollback-mdb", MainMdb, "Jet 4 MDB");
    }

    private static Probe TxnCommitProbe(string name, string fixture, string fmt) =>
        Q(name, TxnRefs, fixture,
            $"{fmt}: BeginTrans -> client static batch-optimistic (3,3,4) UpdateBatch(adAffectAll) staging update (row 1), delete (row 2), and insert (ID 40) -> CommitTrans -> pause 1500ms -> Close. " +
            "Keeps all pages before, after BeginTrans+UpdateBatch (in txn), after CommitTrans, after pause, and after Close (exact 0xE02, page-1, row & index bytes).",
            p =>
            {
                var n = new Names();
                StartWriting(p);
                AdoOpen(p, "cn", readOnly: false, captureModules: true);
                AdoRs(p, "rs", SelW, "cn", Ado.UseClient, Ado.OpenStatic, Ado.LockBatchOptimistic);
                p.GetBind("fl", "rs", "Fields").Item("fid", "fl", A.Str("ID")).Item("fc", "fl", A.Str("Code")).Item("fs", "fl", A.Str("S"))
                    .Put("fs", "Value", A.Str("tc1")).Call("rs", "Update")
                    .Call("rs", "MoveNext").Call("rs", "Delete")
                    .Call("rs", "AddNew").Put("fid", "Value", A.I4(40)).Put("fc", "Value", A.Str("w40")).Put("fs", "Value", A.Str("tc40")).Call("rs", "Update");

                p.Call("cn", "BeginTrans");
                KeepAllPages(p, "after-begintrans", n.Next("pg"));
                p.Call("rs", "UpdateBatch", A.I4(Ado.AffectAll));
                AdoErrorsClear(p, "cn", n);
                KeepAllPages(p, "after-batch-in-txn", n.Next("pg"));
                DumpBatchRows(p, "rs", "S", 3);

                p.Call("cn", "CommitTrans");
                AdoErrorsClear(p, "cn", n);
                KeepAllPages(p, "after-commit-live", n.Next("pg"));
                p.Get("rs", "State").Get("rs", "RecordCount").Call("rs", "MoveFirst");
                RowChannels(p, "rs", "fs", "cn", n);

                p.Release("fs").Release("fc").Release("fid").Release("fl").Call("rs", "Close").Release("rs");
                p.Pause(1500);
                KeepAllPages(p, "after-pause-live", n.Next("pg"));
                AdoClose(p, "cn");
                KeepAllPages(p, "after-close", n.Next("pg"));
                AdoReadBackW(p, n, "ID <= 3 OR ID = 40");
                p.Release("eng");
            });

    private static Probe TxnRollbackProbe(string name, string fixture, string fmt) =>
        Q(name, TxnRefs, fixture,
            $"{fmt}: BeginTrans -> client static batch-optimistic (3,3,4) UpdateBatch(adAffectAll) staging update (row 1), delete (row 2), and insert (ID 40) -> RollbackTrans -> " +
            "inspects client cache (Status, Value/OriginalValue/UnderlyingValue, RecordCount, adFilterPendingRecords), calls Resync(adAffectAll, adResyncAllValues) and Requery, pause 1500ms, Close. " +
            "Keeps all pages before, after UpdateBatch (in txn), after RollbackTrans, after pause, and after Close.",
            p =>
            {
                var n = new Names();
                StartWriting(p);
                AdoOpen(p, "cn", readOnly: false, captureModules: true);
                AdoRs(p, "rs", SelW, "cn", Ado.UseClient, Ado.OpenStatic, Ado.LockBatchOptimistic);
                p.GetBind("fl", "rs", "Fields").Item("fid", "fl", A.Str("ID")).Item("fc", "fl", A.Str("Code")).Item("fs", "fl", A.Str("S"))
                    .Put("fs", "Value", A.Str("tr1")).Call("rs", "Update")
                    .Call("rs", "MoveNext").Call("rs", "Delete")
                    .Call("rs", "AddNew").Put("fid", "Value", A.I4(40)).Put("fc", "Value", A.Str("w40")).Put("fs", "Value", A.Str("tr40")).Call("rs", "Update");

                p.Call("cn", "BeginTrans").Call("rs", "UpdateBatch", A.I4(Ado.AffectAll));
                AdoErrorsClear(p, "cn", n);
                KeepAllPages(p, "after-batch-in-txn", n.Next("pg"));
                p.Get("rs", "RecordCount").Call("rs", "MoveFirst");
                RowChannels(p, "rs", "fs", "cn", n);

                p.Call("cn", "RollbackTrans");
                AdoErrorsClear(p, "cn", n);
                KeepAllPages(p, "after-rollback-live", n.Next("pg"));

                // Client cache state right after RollbackTrans
                p.Get("rs", "State").Get("rs", "RecordCount")
                    .Put("rs", "Filter", A.I4(Ado.FilterPending)).Get("rs", "RecordCount")
                    .Put("rs", "Filter", A.I4(Ado.FilterNone)).Call("rs", "MoveFirst");
                RowChannels(p, "rs", "fs", "cn", n);

                // Resync(adAffectAll, adResyncAllValues) after RollbackTrans (row 40 no longer in DB -> 0x80040E23?)
                p.Call("rs", "Resync", A.I4(Ado.AffectAll), A.I4(Ado.ResyncAll));
                AdoErrorsClear(p, "cn", n);
                p.Get("rs", "RecordCount").Call("rs", "MoveFirst");
                RowChannels(p, "rs", "fs", "cn", n);

                // Requery restores full DB state
                p.Call("rs", "Requery").Get("rs", "RecordCount").Call("rs", "MoveFirst");
                RowChannels(p, "rs", "fs", "cn", n);

                p.Release("fs").Release("fc").Release("fid").Release("fl").Call("rs", "Close").Release("rs");
                p.Pause(1500);
                KeepAllPages(p, "after-pause-live", n.Next("pg"));
                AdoClose(p, "cn");
                KeepAllPages(p, "after-close", n.Next("pg"));
                AdoReadBackW(p, n, "ID <= 3 OR ID = 40");
                p.Release("eng");
            });

    // ================================================================================================
    // Part B — bi-select-aggregates Group 5 Overflow Isolate (r-q47 Open follow-up, 2 probes)

    private static IEnumerable<Probe> PartBBigIntAggIsolateProbes()
    {
        // 31. Isolate Group 5 (-9223372036854775808 + 9223372036854775807) SUM vs AVG vs MIN/MAX/COUNT & single-row extremes (DAO + ADO)
        yield return new Probe(
            Card,
            "bi-agg-g5-isolate-accdb",
            BiRefs,
            "ACCDB (r-q47 follow-up): seeds T(ID PK, G LONG, B BIGINT) with G=1..10 (G=1 small+Null, G=2 2147483647+2147483648, G=4 Null, " +
            "G=5 min+max, G=6 min alone, G=7 max alone, G=8 min+0, G=9 (min+1)+max, G=10 min+1). " +
            "Isolates SUM(B), AVG(B), MIN(B), MAX(B), COUNT(B), COUNT(*) individually on G=5 and SUM/AVG separately on G=6..10 via both DAO and ADO with full metadata.",
            p =>
            {
                var n = new Names();
                StartWriting(p);
                p.CallBind("db0", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false))
                    .Call("db0", "Execute", A.Str("CREATE TABLE T (ID LONG CONSTRAINT PK_T PRIMARY KEY, G LONG, B BIGINT)"), A.I4(Dao.FailOnError));
                foreach ((int id, int g, string v) in new[]
                {
                    (1, 1, "0"), (2, 1, "1"), (3, 1, "2147483647"), (4, 1, "Null"),
                    (5, 2, "2147483647"), (6, 2, "2147483648"),
                    (7, 4, "Null"),
                    (8, 5, "-9223372036854775808"), (9, 5, "9223372036854775807"),
                    (10, 6, "-9223372036854775808"),
                    (11, 7, "9223372036854775807"),
                    (12, 8, "-9223372036854775808"), (13, 8, "0"),
                    (14, 9, "-9223372036854775807"), (15, 9, "9223372036854775807"),
                    (16, 10, "-9223372036854775808"), (17, 10, "1"),
                })
                {
                    p.Call("db0", "Execute", A.Str($"INSERT INTO T VALUES ({id}, {g}, {v})"), A.I4(Dao.FailOnError)).Errors();
                }
                KeepAllPages(p, "after-setup-live", n.Next("pg"));
                p.Pause(1500);
                KeepAllPages(p, "after-setup-pause", n.Next("pg"));
                p.Call("db0", "Close").Release("db0");
                KeepAllPages(p, "after-setup-close", n.Next("pg"));

                string[] queries =
                [
                    // Baseline non-G5 groups vs exact r-q47 failing query (WITH GROUP BY G)
                    "SELECT G, SUM(B) AS s, MIN(B) AS mn, MAX(B) AS mx, AVG(B) AS av, COUNT(B) AS cb, COUNT(*) AS ca FROM T WHERE G IN (1, 2, 4) GROUP BY G ORDER BY G",
                    "SELECT G, SUM(B) AS s, MIN(B) AS mn, MAX(B) AS mx, AVG(B) AS av, COUNT(B) AS cb, COUNT(*) AS ca FROM T WHERE G IN (1, 2, 4, 5) GROUP BY G ORDER BY G",
                    // Bisect exact group & aggregate combination causing Overflow on G=5
                    "SELECT G, SUM(B) AS s, MIN(B) AS mn, MAX(B) AS mx, AVG(B) AS av, COUNT(B) AS cb, COUNT(*) AS ca FROM T WHERE G = 5 GROUP BY G",
                    "SELECT G, SUM(B) AS s, MIN(B) AS mn, MAX(B) AS mx, AVG(B) AS av, COUNT(B) AS cb, COUNT(*) AS ca FROM T WHERE G IN (1, 5) GROUP BY G ORDER BY G",
                    "SELECT G, SUM(B) AS s, MIN(B) AS mn, MAX(B) AS mx, AVG(B) AS av, COUNT(B) AS cb, COUNT(*) AS ca FROM T WHERE G IN (2, 5) GROUP BY G ORDER BY G",
                    "SELECT G, SUM(B) AS s, MIN(B) AS mn, MAX(B) AS mx, AVG(B) AS av, COUNT(B) AS cb, COUNT(*) AS ca FROM T WHERE G IN (4, 5) GROUP BY G ORDER BY G",
                    "SELECT G, SUM(B) AS s, AVG(B) AS av FROM T WHERE G IN (1, 2, 4, 5) GROUP BY G ORDER BY G",
                    "SELECT G, SUM(B) AS s, MIN(B) AS mn, MAX(B) AS mx FROM T WHERE G IN (1, 2, 4, 5) GROUP BY G ORDER BY G",
                    "SELECT G, SUM(B) AS s, MIN(B) AS mn FROM T WHERE G = 5 GROUP BY G",
                    "SELECT G, SUM(B) AS s, MAX(B) AS mx FROM T WHERE G = 5 GROUP BY G",
                    "SELECT SUM(B) AS s, MIN(B) AS mn, MAX(B) AS mx FROM T WHERE G = 5",
                    "SELECT G, SUM(B) AS s, MIN(B) AS mn, MAX(B) AS mx FROM T WHERE G = 6 GROUP BY G",
                    "SELECT G, SUM(B) AS s, MIN(B) AS mn, MAX(B) AS mx FROM T WHERE G = 9 GROUP BY G",
                    "SELECT G, AVG(B) AS av, MIN(B) AS mn, MAX(B) AS mx FROM T WHERE G IN (1, 2, 4, 5) GROUP BY G ORDER BY G",
                    "SELECT G, MIN(B) AS mn, MAX(B) AS mx, COUNT(B) AS cb, COUNT(*) AS ca FROM T WHERE G IN (1, 2, 4, 5) GROUP BY G ORDER BY G",
                    // GROUP BY G isolated per aggregate on G IN (1, 5) and G = 5
                    "SELECT G, SUM(B) AS s FROM T WHERE G IN (1, 5) GROUP BY G ORDER BY G",
                    "SELECT G, AVG(B) AS av FROM T WHERE G IN (1, 5) GROUP BY G ORDER BY G",
                    "SELECT G, MIN(B) AS mn FROM T WHERE G IN (1, 5) GROUP BY G ORDER BY G",
                    "SELECT G, MAX(B) AS mx FROM T WHERE G IN (1, 5) GROUP BY G ORDER BY G",
                    "SELECT G, SUM(B) AS s FROM T WHERE G = 5 GROUP BY G",
                    "SELECT G, AVG(B) AS av FROM T WHERE G = 5 GROUP BY G",
                    "SELECT G, SUM(B) AS s, AVG(B) AS av FROM T WHERE G = 5 GROUP BY G",
                    "SELECT G, SUM(B) AS s FROM T WHERE G = 6 GROUP BY G",
                    "SELECT G, AVG(B) AS av FROM T WHERE G = 6 GROUP BY G",
                    "SELECT G, SUM(B) AS s FROM T WHERE G = 9 GROUP BY G",
                    "SELECT G, AVG(B) AS av FROM T WHERE G = 9 GROUP BY G",
                    // Scalar (no GROUP BY) isolated per aggregate on G=5 and single-row extremes G=6..10
                    "SELECT SUM(B) AS s FROM T WHERE G = 5",
                    "SELECT AVG(B) AS av FROM T WHERE G = 5",
                    "SELECT MIN(B) AS mn, MAX(B) AS mx, COUNT(B) AS cb, COUNT(*) AS ca FROM T WHERE G = 5",
                    "SELECT SUM(B) AS s FROM T WHERE G = 6",
                    "SELECT AVG(B) AS av FROM T WHERE G = 6",
                    "SELECT SUM(B) AS s FROM T WHERE G = 7",
                    "SELECT AVG(B) AS av FROM T WHERE G = 7",
                    "SELECT SUM(B) AS s FROM T WHERE G = 8",
                    "SELECT AVG(B) AS av FROM T WHERE G = 8",
                    "SELECT SUM(B) AS s FROM T WHERE G = 9",
                    "SELECT AVG(B) AS av FROM T WHERE G = 9",
                    "SELECT SUM(B) AS s FROM T WHERE G = 10",
                    "SELECT AVG(B) AS av FROM T WHERE G = 10",
                ];

                foreach (string q in queries)
                {
                    DaoReadQueryMetaAll(p, n, q, 6);
                }
                AdoOpen(p, "cn", readOnly: true, captureModules: true);
                foreach (string q in queries)
                {
                    AdoReadQueryMetaAll(p, "cn", n, q, 6);
                }
                AdoClose(p, "cn");
                p.Release("eng");
            })
        { Fixtures = [("main", EmptyAccdb)] };

        // 32. Mixed Null + extreme BIGINT values & 2*min / 2*max in aggregates (DAO + ADO)
        yield return new Probe(
            Card,
            "bi-agg-null-extremes-accdb",
            BiRefs,
            "ACCDB (r-q47 follow-up): seeds T(ID PK, G LONG, B BIGINT) with mixed Null + extreme values: " +
            "G=1 {Null, min}, G=2 {Null, max}, G=3 {Null, min, max}, G=4 {Null, min+1, max}, G=5 {min, min}, G=6 {max, max}. " +
            "Runs separate SUM(B), AVG(B), and MIN/MAX/COUNT queries for each group via both DAO and ADO with full metadata.",
            p =>
            {
                var n = new Names();
                StartWriting(p);
                p.CallBind("db0", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false))
                    .Call("db0", "Execute", A.Str("CREATE TABLE T (ID LONG CONSTRAINT PK_T PRIMARY KEY, G LONG, B BIGINT)"), A.I4(Dao.FailOnError));
                foreach ((int id, int g, string v) in new[]
                {
                    (1, 1, "Null"), (2, 1, "-9223372036854775808"),
                    (3, 2, "Null"), (4, 2, "9223372036854775807"),
                    (5, 3, "Null"), (6, 3, "-9223372036854775808"), (7, 3, "9223372036854775807"),
                    (8, 4, "Null"), (9, 4, "-9223372036854775807"), (10, 4, "9223372036854775807"),
                    (11, 5, "-9223372036854775808"), (12, 5, "-9223372036854775808"),
                    (13, 6, "9223372036854775807"), (14, 6, "9223372036854775807"),
                })
                {
                    p.Call("db0", "Execute", A.Str($"INSERT INTO T VALUES ({id}, {g}, {v})"), A.I4(Dao.FailOnError)).Errors();
                }
                KeepAllPages(p, "after-setup-live", n.Next("pg"));
                p.Pause(1500);
                KeepAllPages(p, "after-setup-pause", n.Next("pg"));
                p.Call("db0", "Close").Release("db0");
                KeepAllPages(p, "after-setup-close", n.Next("pg"));

                var queries = new List<string>();
                for (int g = 1; g <= 6; g++)
                {
                    queries.Add($"SELECT SUM(B) AS s FROM T WHERE G = {g}");
                    queries.Add($"SELECT AVG(B) AS av FROM T WHERE G = {g}");
                    queries.Add($"SELECT G, SUM(B) AS s FROM T WHERE G = {g} GROUP BY G");
                    queries.Add($"SELECT G, AVG(B) AS av FROM T WHERE G = {g} GROUP BY G");
                    queries.Add($"SELECT MIN(B) AS mn, MAX(B) AS mx, COUNT(B) AS cb, COUNT(*) AS ca FROM T WHERE G = {g}");
                }

                foreach (string q in queries)
                {
                    DaoReadQueryMetaAll(p, n, q, 4);
                }
                AdoOpen(p, "cn", readOnly: true, captureModules: true);
                foreach (string q in queries)
                {
                    AdoReadQueryMetaAll(p, "cn", n, q, 4);
                }
                AdoClose(p, "cn");
                p.Release("eng");
            })
        { Fixtures = [("main", EmptyAccdb)] };
    }

    // ================================================================================================
    // Shared helpers

    private static Probe Q(string name, string[] refs, string fixture, string description, Action<P> body) =>
        new(Card, name, refs, description, body) { Fixtures = [("main", fixture)] };

    private static void StartWriting(P p)
    {
        KeepAllPages(p, "before", "pg0");
        p.Create("DAO.DBEngine.120", "eng").Modules("after-create");
    }

    private static void StartReadOnly(P p) =>
        p.Files("before-open").Create("DAO.DBEngine.120", "eng").Modules("after-create");

    private static void AdoOpen(P p, string cn, bool readOnly, bool captureModules = false)
    {
        p.Create("ADODB.Connection", cn)
            .Put(cn, "Provider", A.Str(AceProvider));
        if (readOnly)
        {
            p.Put(cn, "Mode", A.I4(Ado.ModeRead));
        }
        p.GetBind(cn + "_p", cn, "Properties")
            .Item(cn + "_ds", cn + "_p", A.Str("Data Source"))
            .Put(cn + "_ds", "Value", A.In())
            .Release(cn + "_ds").Release(cn + "_p")
            .Call(cn, "Open");
        if (captureModules)
        {
            p.Modules("after-ado-open");
        }
    }

    private static void AdoClose(P p, string cn) =>
        p.Call(cn, "Close").Release(cn);

    private static void AdoRs(P p, string rs, string sql, string cn, int loc, int cur, int lck) =>
        p.Create("ADODB.Recordset", rs)
            .Put(rs, "CursorLocation", A.I4(loc))
            .Call(rs, "Open", A.Str(sql), A.Obj(cn), A.I4(cur), A.I4(lck), A.I4(Ado.CmdText));

    private static void ExecNo(P p, string cn, string sql) =>
        p.Call(cn, "Execute", A.Str(sql), A.Missing, A.I4(Ado.CmdText | Ado.ExecuteNoRecords));

    private static void AdoErrorsClear(P p, string cn, Names n)
    {
        string ec = n.Next("ec"), ei = n.Next("ei");
        p.GetBind(ec, cn, "Errors").Get(ec, "Count")
            .Each(ec, ei, 8, b => b
                .Get(ei, "Number").Get(ei, "NativeError").Get(ei, "SQLState")
                .Get(ei, "Source").Get(ei, "Description"))
            .Call(ec, "Clear").Release(ec);
    }

    private static void RowChannels(P p, string rs, string fld, string cn, Names n)
    {
        p.Get(rs, "EditMode").Get(rs, "Status");
        FieldChannels(p, fld, cn, n);
    }

    private static void FieldChannels(P p, string fld, string cn, Names n)
    {
        p.Get(fld, "Value").Get(fld, "OriginalValue").Get(fld, "UnderlyingValue");
        AdoErrorsClear(p, cn, n);
    }

    private static void DumpBatchRows(P p, string rs, string col, int maxRows)
    {
        p.Call(rs, "MoveFirst").Until(rs, "EOF", maxRows, b => b
            .Get(rs, "Status").Get(rs, "EditMode")
            .GetBind("bf_", rs, "Fields")
            .Item("bid_", "bf_", A.Str("ID")).Get("bid_", "Value").Release("bid_")
            .Item("bc_", "bf_", A.Str(col))
            .Get("bc_", "Value").Get("bc_", "OriginalValue").Get("bc_", "UnderlyingValue")
            .Release("bc_").Release("bf_")
            .Call(rs, "MoveNext"));
    }

    private static void ReadIds(P p, string rs, int maxRows) =>
        p.Until(rs, "EOF", maxRows, b => b
            .GetBind("if_", rs, "Fields").Item("id_", "if_", A.Str("ID")).Get("id_", "Value").Release("id_").Release("if_")
            .Call(rs, "MoveNext"));

    private static void ReadIdsAndV(P p, string rs, int maxRows) =>
        p.Call(rs, "MoveFirst").Until(rs, "EOF", maxRows, b => b
            .GetBind("vf_", rs, "Fields")
            .Item("vi_", "vf_", A.Str("ID")).Get("vi_", "Value").Release("vi_")
            .Item("vv_", "vf_", A.Str("V")).Get("vv_", "Value").Release("vv_")
            .Release("vf_").Call(rs, "MoveNext"));

    private static void CheckAllFilterGroups(P p, string rs, string cn, Names n)
    {
        foreach (int fg in new[] { Ado.FilterNone, Ado.FilterPending, Ado.FilterAffected, Ado.FilterFetched, Ado.FilterConflicting })
        {
            p.Put(rs, "Filter", A.I4(fg)).Get(rs, "RecordCount");
            AdoErrorsClear(p, cn, n);
        }
        p.Put(rs, "Filter", A.I4(Ado.FilterNone));
    }

    private static void AdoExecRead(P p, string cn, Names n, string sql, int maxRows)
    {
        string rs = n.Next("er");
        p.CallBind(rs, cn, "Execute", A.Str(sql));
        AdoErrorsClear(p, cn, n);
        p.Until(rs, "EOF", maxRows, b => b
            .GetBind(rs + "f", rs, "Fields")
            .Each(rs + "f", rs + "x", 8, f => f.Get(rs + "x", "Name").Get(rs + "x", "Value"))
            .Release(rs + "f").Call(rs, "MoveNext"))
            .Call(rs, "Close").Release(rs);
    }

    private static void AdoReadBackW(P p, Names n, string where)
    {
        string cn = n.Next("cnrb");
        AdoOpen(p, cn, readOnly: true);
        AdoExecRead(p, cn, n, $"SELECT ID, Code, V, V2, S FROM W WHERE {where} ORDER BY ID", 16);
        AdoClose(p, cn);
    }

    private static void DaoReadQueryMetaAll(P p, Names n, string sql, int maxRows)
    {
        string db = n.Next("dbr"), rs = n.Next("drs");
        string mf = rs + "mf", mfx = rs + "mfx", rf = rs + "rf", rfx = rs + "rfx";
        p.CallBind(db, "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(true))
            .OpenRs(rs, db, sql, Dao.OpenSnapshot).Errors()
            .GetBind(mf, rs, "Fields").Get(mf, "Count")
            .Each(mf, mfx, 16, f => f.Get(mfx, "Name").Get(mfx, "Type").Get(mfx, "Size").Get(mfx, "Attributes"))
            .Release(mf)
            .Until(rs, "EOF", maxRows, b => b
                .GetBind(rf, rs, "Fields")
                .Each(rf, rfx, 16, f => f.Get(rfx, "Name").Get(rfx, "Value").Errors())
                .Release(rf)
                .Call(rs, "MoveNext"))
            .Call(rs, "Close").Release(rs)
            .Call(db, "Close").Release(db);
    }

    private static void AdoReadQueryMetaAll(P p, string cn, Names n, string sql, int maxRows)
    {
        string rs = n.Next("ars");
        string mf = rs + "mf", mfx = rs + "mfx", rf = rs + "rf", rfx = rs + "rfx";
        p.CallBind(rs, cn, "Execute", A.Str(sql));
        AdoErrorsClear(p, cn, n);
        p.GetBind(mf, rs, "Fields").Get(mf, "Count")
            .Each(mf, mfx, 16, f => f
                .Get(mfx, "Name").Get(mfx, "Type").Get(mfx, "DefinedSize")
                .Get(mfx, "Precision").Get(mfx, "NumericScale").Get(mfx, "Attributes"))
            .Release(mf)
            .Until(rs, "EOF", maxRows, b => b
                .GetBind(rf, rs, "Fields")
                .Each(rf, rfx, 16, f => f.Get(rfx, "Name").Get(rfx, "Value"))
                .Release(rf)
                .Call(rs, "MoveNext"))
            .Call(rs, "Close").Release(rs);
    }

    private static void KeepAllPages(P p, string label, string handle) =>
        p.FilePages(label, 4096)
            .NativeOpen(handle, "input:main", Win.GenericRead, Win.ShareAll, Win.OpenExisting)
            .NativeRead(handle, 0, FullReadBytes, keep: true)
            .NativeClose(handle);
}
