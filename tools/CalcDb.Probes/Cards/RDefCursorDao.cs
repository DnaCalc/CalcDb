namespace CalcDb.Probes.Cards;

/// <summary>
/// Deferred branches of R-CURSOR-1, R-DAO-1 and R-ERR-1 that an in-process, single-thread, DAO-only program can ask
/// (docs/work/deferred-branches.md, waves 1 to 4). Two fixtures are created through ACE DAO: main (ACCDB) and mdb (Jet 4
/// format). The probes ask: CUR-POS on the MDB format and on query sources (DISTINCT, GROUP BY, TOP, UNION, crosstab, outer
/// join); CUR-PROV per-field origin and update targets (repeated aliases, DISTINCT and DISTINCTROW, aggregates, UNION and
/// crosstab, null-extended side, two child rows sharing a parent, join-field change, dbInconsistent with declared
/// relationships, two equal heap rows, a missing projected key); CUR-BULK large Memo and binary values, GetChunk and
/// AppendChunk; CUR-EDIT change-then-restore, edits under commit, LockEdits after open, OriginalValue and VisibleValue,
/// assignment without Edit, AddNew landing position; CUR-LIFE deletion of the current row through another cursor and the
/// dbOpenDynamic and option-flag matrix; DAO-PROP and DAO-COLL members of recordset fields (Collect, join names, lookup
/// forms, query-column metadata, Properties, QueryDef MaxRecords, closed-recordset members); ERR-BASE classes the
/// earlier cards left out (the same fault through different members, duplicate names, missing objects, ambiguous
/// columns, UNION width, held objects after deletion, Errors.Refresh, one row violating several constraints).
/// Probes ask; they hold no expected answers. Arguments are constants; every file is named through A.In or A.Out; no
/// call here can open a dialog (no ODBC, no pass-through, no Empty or Missing database name).
/// </summary>
public sealed class RDefCursorDao : ICard
{
    private const string Card = "r-def-cursor-dao";
    private const string RCur = "R-CURSOR-1", RDao = "R-DAO-1", RErr = "R-ERR-1";
    private const int Inconsistent = 16, Consistent = 32;

    private static readonly string Memo12K = new('m', 12000);

    private static readonly string[] TRows =
    [
        "1, 10, 'alpha', #1/15/2020#, True, 'm1', 'c1'",
        "2, 20, 'beta', Null, False, Null, 'c2'",
        "3, 30, 'gamma', #3/25/2020#, True, 'm3', 'c3'",
        "4, 40, 'delta', #1/5/2021#, False, Null, 'c4'",
        "5, 40, 'epsilon', #1/15/2020#, True, 'm5', 'c5'",
        "6, 50, 'zeta', Null, False, Null, 'c6'",
        "7, 60, 'eta', #12/31/2020#, True, 'm7', 'c7'",
        "8, 70, Null, Null, False, Null, 'c8'",
    ];

    private static readonly string[] MetaMembers = ["Name", "Type", "Size", "Attributes", "DataUpdatable", "SourceTable", "SourceField", "Required", "OrdinalPosition"];

    private static readonly string[] WideMetaMembers =
    [
        .. MetaMembers, "CollatingOrder", "AllowZeroLength", "ValidationRule", "ValidationText", "DefaultValue", "CollectionIndex",
        "ForeignName", "Expression", "ValidateOnSet",
    ];

    public IEnumerable<Probe> Probes() =>
    [
        .. Fixtures(), .. MdbPositions(), .. QueryPositions(), .. Provenance(), .. Bulk(), .. Edits(), .. Lifetimes(), .. Members(), .. Faults(),
    ];

    // Fixtures ----------------------------------------------------------------------------------------------

    private static void Tables(P p)
    {
        Sql(p, "CREATE TABLE T (ID LONG CONSTRAINT PK_T PRIMARY KEY, N LONG, S TEXT(20), D DATETIME, Fl YESNO, Mm MEMO, Cd TEXT(10))");
        foreach (string row in TRows)
        {
            Sql(p, "INSERT INTO T VALUES (" + row + ")");
        }
        Sql(p, "CREATE INDEX IX_TN ON T (N)");
        Sql(p, "CREATE INDEX IX_TND ON T (N DESC)");
        Sql(p, "CREATE INDEX IX_TS ON T (S)");
        Sql(p, "CREATE UNIQUE INDEX UX_TCd ON T (Cd)");
        Sql(p, "CREATE TABLE H (A LONG, B TEXT(10))");
        Sql(p, "INSERT INTO H VALUES (1, 'x')");
        Sql(p, "INSERT INTO H VALUES (1, 'x')");
        Sql(p, "INSERT INTO H VALUES (2, 'y')");
        Sql(p, "INSERT INTO H VALUES (3, Null)");
        Sql(p, "CREATE TABLE PA (ID LONG CONSTRAINT PK_PA PRIMARY KEY, V TEXT(10))");
        Sql(p, "CREATE TABLE CH (ID LONG CONSTRAINT PK_CH PRIMARY KEY, PID LONG, W TEXT(10))");
        Sql(p, "INSERT INTO PA VALUES (1, 'p1')");
        Sql(p, "INSERT INTO PA VALUES (2, 'p2')");
        Sql(p, "INSERT INTO PA VALUES (3, 'p3')");
        Sql(p, "ALTER TABLE CH ADD CONSTRAINT FK_CH_PA FOREIGN KEY (PID) REFERENCES PA (ID)");
        Sql(p, "INSERT INTO CH VALUES (1, 1, 'c1')");
        Sql(p, "INSERT INTO CH VALUES (2, 1, 'c2')");
        Sql(p, "INSERT INTO CH VALUES (3, 2, 'c3')");
    }

    private static IEnumerable<Probe> Fixtures()
    {
        yield return Creation("main", "accdb", Dao.Version120,
            "T(ID LONG primary key PK_T, N LONG, S TEXT(20), D DATETIME, Fl YESNO, Mm MEMO, Cd TEXT(10)) with eight rows (N 10,20,30,40,40,50,60,70; S Null in row 8; D with Nulls and a repeated date; Mm Null in four rows) " +
            "and indexes IX_TN (N), IX_TND (N DESC), IX_TS (S), unique UX_TCd (Cd); H(A LONG, B TEXT(10)) with no key and four rows (two equal rows (1,'x')); PA(ID primary key, V TEXT(10)) with three rows; " +
            "CH(ID primary key, PID LONG, W TEXT(10)) with three rows and FOREIGN KEY FK_CH_PA (PID) REFERENCES PA (ID), parent 3 without children; LB(ID primary key, Mm MEMO, Bn LONGBINARY, Tx TEXT(20)) with a short Memo, " +
            "a 12,000-character Memo and an all-Null row; R(ID primary key, Cd TEXT(10) with unique index UX_R_Cd, Rq TEXT(10) NOT NULL, Vv LONG with ValidationRule '> 0' set through DAO) with one row; stored query QSel",
            p =>
            {
                Tables(p);
                Sql(p, "CREATE TABLE LB (ID LONG CONSTRAINT PK_LB PRIMARY KEY, Mm MEMO, Bn LONGBINARY, Tx TEXT(20))");
                Sql(p, "INSERT INTO LB (ID, Mm, Tx) VALUES (1, 'short memo', 't1')");
                Sql(p, "INSERT INTO LB (ID, Mm, Tx) VALUES (2, '" + Memo12K + "', 't2')");
                Sql(p, "INSERT INTO LB (ID) VALUES (3)");
                Sql(p, "CREATE TABLE R (ID LONG CONSTRAINT PK_R PRIMARY KEY, Cd TEXT(10), Rq TEXT(10) NOT NULL, Vv LONG)");
                Sql(p, "CREATE UNIQUE INDEX UX_R_Cd ON R (Cd)");
                Sql(p, "INSERT INTO R VALUES (1, 'r1', 'q1', 5)");
                p.GetBind("tdfs", "db", "TableDefs").Call("tdfs", "Refresh").Item("tr", "tdfs", A.Str("R")).GetBind("rfl", "tr", "Fields").Item("rv", "rfl", A.Str("Vv"))
                    .Put("rv", "ValidationRule", A.Str("> 0")).Put("rv", "ValidationText", A.Str("Vv must be positive"))
                    .Release("rv").Release("rfl").Release("tr").Release("tdfs");
                p.CallBind("qd", "db", "CreateQueryDef", A.Str("QSel"), A.Str("SELECT ID, N FROM T WHERE N >= 30 ORDER BY ID")).Release("qd");
            });

        yield return Creation("mdb", "mdb", Dao.Version40,
            "the MDB (Jet 4, dbVersion40) twin of the T, H, PA and CH tables, indexes, rows and relationship of main, without LB, R and QSel",
            Tables);
    }

    // CUR-POS on the MDB format ------------------------------------------------------------------------------

    private static IEnumerable<Probe> MdbPositions()
    {
        foreach ((string label, int type) in new[] { ("table", Dao.OpenTable), ("dynaset", Dao.OpenDynaset), ("snapshot", Dao.OpenSnapshot) })
        {
            bool abs = type != Dao.OpenTable;
            yield return On("mdb", $"mdb-pos-{label}", Refs(RCur, "CUR-POS"),
                $"On the MDB fixture, a {label} recordset over T (table-type with Index PK_T): read Type and RecordCount, BOF, EOF, PercentPosition{(abs ? ", AbsolutePosition" : "")} and ID; MoveLast, MoveNext (past the end), " +
                "MovePrevious, MoveFirst, MovePrevious (before the start), MoveNext, then Move 3, -2, 100, -100 and 0, each read the same way; a forward walk to EOF and a backward walk from MoveLast to BOF reading ID; " +
                "then MoveFirst, Delete and read the state, MoveNext and read, MoveLast, Delete and read, MovePrevious and read. Asks the position, boundary and recovery states of each move on the Jet 4 format and the " +
                "refusal of Delete where the type has none. The fixture copy is opened read-write.",
                p =>
                {
                    p.OpenRs("rs", "db", "T", type);
                    if (type == Dao.OpenTable)
                    {
                        p.Put("rs", "Index", A.Str("PK_T"));
                    }
                    p.Get("rs", "Type").Get("rs", "RecordCount");
                    PosState(p, abs);
                    p.Call("rs", "MoveLast");
                    PosState(p, abs);
                    p.Get("rs", "RecordCount").Call("rs", "MoveNext");
                    PosState(p, abs);
                    p.Call("rs", "MovePrevious");
                    PosState(p, abs);
                    p.Call("rs", "MoveFirst").Call("rs", "MovePrevious");
                    PosState(p, abs);
                    p.Call("rs", "MoveNext");
                    PosState(p, abs);
                    foreach (int n in new[] { 3, -2, 100, -100, 0 })
                    {
                        p.Call("rs", "Move", A.I4(n));
                        PosState(p, abs);
                    }
                    p.Call("rs", "MoveFirst").Until("rs", "EOF", 16, b => Val(b, "rs", "ID").Call("rs", "MoveNext"));
                    p.Call("rs", "MoveLast").Until("rs", "BOF", 16, b => Val(b, "rs", "ID").Call("rs", "MovePrevious"));
                    p.Call("rs", "MoveFirst").Call("rs", "Delete");
                    PosState(p, abs);
                    p.Call("rs", "MoveNext");
                    PosState(p, abs);
                    p.Call("rs", "MoveLast").Call("rs", "Delete");
                    PosState(p, abs);
                    p.Call("rs", "MovePrevious");
                    PosState(p, abs);
                    p.Get("rs", "RecordCount");
                    Close(p, "rs");
                }, write: true);
        }

        yield return On("mdb", "mdb-seek-duplicates", Refs(RCur, "CUR-SEEK", "CUR-POS"),
            "On the MDB fixture, table-type recordsets over T with Index IX_TN (rows N 10,20,30,40,40,50,60,70): for each operator =, >=, >, <=, <, Seek with key 40 on a fresh recordset, read NoMatch, BOF, EOF and ID; " +
            "MoveNext and read ID; MovePrevious twice and read ID. Then Seek \"=\" 45 and Seek \">\" 70, reading NoMatch, BOF, EOF and ID. Asks which duplicate each operator selects and the neighbours " +
            "on the Jet 4 format.",
            p =>
            {
                foreach (string op in new[] { "=", ">=", ">", "<=", "<" })
                {
                    p.OpenRs("rt", "db", "T", Dao.OpenTable).Put("rt", "Index", A.Str("IX_TN")).Call("rt", "Seek", A.Str(op), A.I4(40)).Get("rt", "NoMatch").Get("rt", "BOF").Get("rt", "EOF");
                    Val(p, "rt", "ID").Call("rt", "MoveNext");
                    Val(p, "rt", "ID").Call("rt", "MovePrevious").Call("rt", "MovePrevious");
                    Val(p, "rt", "ID");
                    Close(p, "rt");
                }
                foreach ((string op, int key) in new[] { ("=", 45), (">", 70) })
                {
                    p.OpenRs("rt", "db", "T", Dao.OpenTable).Put("rt", "Index", A.Str("IX_TN")).Call("rt", "Seek", A.Str(op), A.I4(key)).Get("rt", "NoMatch").Get("rt", "BOF").Get("rt", "EOF");
                    Val(p, "rt", "ID");
                    Close(p, "rt");
                }
            });
    }

    // CUR-POS on query sources -------------------------------------------------------------------------------

    private static IEnumerable<Probe> QueryPositions()
    {
        yield return QueryPos("distinct-groupby",
            ["SELECT DISTINCT N FROM T ORDER BY N", "SELECT N, Count(*) AS Cn FROM T GROUP BY N ORDER BY N"],
            "DISTINCT over N and GROUP BY N with Count(*)");
        yield return QueryPos("top",
            ["SELECT TOP 3 ID, N FROM T ORDER BY N DESC, ID", "SELECT TOP 4 ID, N FROM T ORDER BY N"],
            "TOP 3 over a unique ordering and TOP 4 over an ordering whose fourth and fifth rows tie");
        yield return QueryPos("union",
            ["SELECT ID, N FROM T WHERE ID <= 3 UNION SELECT ID + 10, N FROM T WHERE ID > 6"],
            "a UNION of two selects over T");
        yield return QueryPos("crosstab",
            ["TRANSFORM Count(ID) AS Cn SELECT S FROM T GROUP BY S PIVOT Fl"],
            "a crosstab (TRANSFORM Count(ID) SELECT S FROM T GROUP BY S PIVOT Fl)");
        yield return QueryPos("left-join",
            ["SELECT PA.ID AS PAID, CH.ID AS CHID FROM PA LEFT JOIN CH ON PA.ID = CH.PID ORDER BY PA.ID, CH.ID"],
            "a LEFT JOIN of PA to CH (parent 3 without a child)");
    }

    private static Probe QueryPos(string name, string[] sources, string what) =>
        On("main", "pos-query-" + name, Refs(RCur, "CUR-POS", "CUR-POP"),
            $"For {what}, as a dynaset and as a snapshot: read Type, Updatable, Bookmarkable, RecordCount, the Fields Count; then BOF, EOF, AbsolutePosition, PercentPosition and the value of field 0 at the first row; MoveLast and read " +
            "the same and RecordCount; MoveNext (past the end) and read; MovePrevious and read; MovePrevious again; MoveFirst, MovePrevious (before the start) and read; MoveFirst, Move 2 and read; Move -1 and read. " +
            "Asks the position, count and boundary states of a recordset whose source is not a plain table, per requested type, and which requests are refused.",
            p =>
            {
                foreach (string sql in sources)
                {
                    foreach (int type in new[] { Dao.OpenDynaset, Dao.OpenSnapshot })
                    {
                        p.OpenRs("rs", "db", sql, type).Get("rs", "Type").Get("rs", "Updatable").Get("rs", "Bookmarkable").Get("rs", "RecordCount")
                            .GetBind("wfs", "rs", "Fields").Get("wfs", "Count").Item("w0", "wfs", A.I4(0));
                        WalkState(p);
                        p.Call("rs", "MoveLast");
                        WalkState(p);
                        p.Get("rs", "RecordCount").Call("rs", "MoveNext");
                        WalkState(p);
                        p.Call("rs", "MovePrevious");
                        WalkState(p);
                        p.Call("rs", "MovePrevious");
                        WalkState(p);
                        p.Call("rs", "MoveFirst").Call("rs", "MovePrevious");
                        WalkState(p);
                        p.Call("rs", "MoveFirst").Call("rs", "Move", A.I4(2));
                        WalkState(p);
                        p.Call("rs", "Move", A.I4(-1));
                        WalkState(p);
                        p.Release("w0").Release("wfs");
                        Close(p, "rs");
                    }
                }
            });

    private static P WalkState(P p) =>
        p.Get("rs", "BOF").Get("rs", "EOF").Get("rs", "AbsolutePosition").Get("rs", "PercentPosition").Get("w0", "Value");

    // CUR-PROV --------------------------------------------------------------------------------------------------

    private const string JoinSql = "SELECT PA.ID AS PAID, PA.V, CH.ID AS CHID, CH.PID, CH.W FROM PA INNER JOIN CH ON PA.ID = CH.PID ORDER BY CH.ID";
    private const string LeftJoinSql = "SELECT PA.ID AS PAID, PA.V, CH.ID AS CHID, CH.PID, CH.W FROM PA LEFT JOIN CH ON PA.ID = CH.PID ORDER BY PA.ID, CH.ID";

    private static IEnumerable<Probe> Provenance()
    {
        yield return On("main", "prov-alias-fields-metadata", Refs(RCur, "CUR-PROV", "DAO-PROP"),
            "A dynaset over \"SELECT ID, N AS N1, N AS N2, N + 1 AS Np, S & 'x' AS Sx, T.S FROM T WHERE ID <= 3 ORDER BY ID\": read Updatable; for each field, by its own call, Name, Type, Size, Attributes, " +
            "DataUpdatable, SourceTable, SourceField, Required and OrdinalPosition; then read every field of every row. Asks the per-field origin and updatability of repeated aliases, an expression and a " +
            "qualified name.",
            p =>
            {
                p.OpenRs("pr", "db", "SELECT ID, N AS N1, N AS N2, N + 1 AS Np, S & 'x' AS Sx, T.S FROM T WHERE ID <= 3 ORDER BY ID", Dao.OpenDynaset).Get("pr", "Updatable");
                FieldMeta(p, "pr", MetaMembers);
                p.ReadAll("pr", 8, 8);
                Close(p, "pr");
            });

        yield return On("main", "prov-alias-edit-duplicate-source", Refs(RCur, "CUR-PROV", "CUR-EDIT"),
            "A dynaset over \"SELECT ID, N AS N1, N AS N2 FROM T WHERE ID = 3\" with held Fields N1 and N2, inside BeginTrans (rolled back at the end): Edit, assign N1 31, read N1 and N2, Update, read N1 and N2; Edit, assign " +
            "N2 32 then N1 33, read both, Update, read both, Errors; Requery and read both; a snapshot over T row 3 reads N. Asks which source column a duplicate alias writes, whether the sibling alias " +
            "follows before and after Update, and the stored value.",
            p =>
            {
                p.Call("eng", "BeginTrans").OpenRs("pr", "db", "SELECT ID, N AS N1, N AS N2 FROM T WHERE ID = 3", Dao.OpenDynaset);
                Hold(p, "pr", "a_", "N1", "N2");
                p.Call("pr", "Edit").Put("a_N1", "Value", A.I4(31)).Get("a_N1", "Value").Get("a_N2", "Value").Call("pr", "Update").Get("a_N1", "Value").Get("a_N2", "Value");
                p.Call("pr", "Edit").Put("a_N2", "Value", A.I4(32)).Put("a_N1", "Value", A.I4(33)).Get("a_N1", "Value").Get("a_N2", "Value").Call("pr", "Update");
                Errs(p).Get("a_N1", "Value").Get("a_N2", "Value").Call("pr", "Requery").Get("a_N1", "Value").Get("a_N2", "Value");
                p.OpenRs("pb", "db", "SELECT N FROM T WHERE ID = 3", Dao.OpenSnapshot).ReadRow("pb").Call("pb", "Close");
                Unhold(p, "a_", "N1", "N2");
                Close(p, "pr");
                p.Call("eng", "Rollback");
            }, write: true);

        yield return On("main", "prov-distinct-distinctrow-plain", Refs(RCur, "CUR-PROV", "CUR-EDIT"),
            "Over T rows 4 and 5 (equal N 40), three dynasets in turn, each inside BeginTrans and rolled back: \"SELECT DISTINCT N\", \"SELECT DISTINCTROW N\" and \"SELECT N\" (WHERE ID IN (4,5)). Each: read Updatable, " +
            "per-field Name, Type, Size, Attributes, DataUpdatable, SourceTable, SourceField, Required and OrdinalPosition; Edit, assign N 41, Update, read EditMode and N, Errors; Requery and read all rows. " +
            "Asks whether DISTINCT and DISTINCTROW change updatability and the base-row target of an edit.",
            p =>
            {
                foreach (string kind in new[] { "DISTINCT", "DISTINCTROW", "" })
                {
                    ProvSource(p, $"SELECT {kind} N FROM T WHERE ID IN (4, 5)", "N", A.I4(41));
                }
            }, write: true);

        yield return On("main", "prov-aggregate-groupby", Refs(RCur, "CUR-PROV", "CUR-EDIT"),
            "A dynaset over \"SELECT N, Count(*) AS Cn, Sum(ID) AS Si FROM T GROUP BY N\", inside BeginTrans and rolled back: Updatable, per-field Name, Type, Size, Attributes, DataUpdatable, SourceTable, SourceField, Required " +
            "and OrdinalPosition; Edit, assign Cn 9, Update, EditMode, Errors; Requery and read all rows. Then the same with the grouping field N assigned 41. Asks the updatability of aggregate and grouping columns.",
            p =>
            {
                ProvSource(p, "SELECT N, Count(*) AS Cn, Sum(ID) AS Si FROM T GROUP BY N", "Cn", A.I4(9));
                ProvSource(p, "SELECT N, Count(*) AS Cn, Sum(ID) AS Si FROM T GROUP BY N", "N", A.I4(41));
            }, write: true);

        yield return On("main", "prov-union-crosstab", Refs(RCur, "CUR-PROV", "CUR-EDIT"),
            "A dynaset over \"SELECT ID, N FROM T WHERE ID <= 3 UNION SELECT ID + 10, N FROM T WHERE ID > 6\" and one over the crosstab \"TRANSFORM Count(ID) AS Cn SELECT S FROM T GROUP BY S PIVOT Fl\", each inside " +
            "BeginTrans and rolled back: Updatable, per-field Name, Type, Size, Attributes, DataUpdatable, SourceTable, SourceField, Required and OrdinalPosition; Edit, assign the first field, Update, EditMode, Errors; Requery " +
            "and read all rows. Asks the updatability and edit target of UNION and crosstab results.",
            p =>
            {
                ProvSource(p, "SELECT ID, N FROM T WHERE ID <= 3 UNION SELECT ID + 10, N FROM T WHERE ID > 6", "N", A.I4(99));
                ProvSource(p, "TRANSFORM Count(ID) AS Cn SELECT S FROM T GROUP BY S PIVOT Fl", "S", A.Str("changed"));
            }, write: true);

        yield return On("main", "prov-outer-join-absent-side", Refs(RCur, "CUR-PROV", "CUR-EDIT"),
            "A dynaset over a LEFT JOIN of PA to CH (PAID, V, CHID, PID, W; parent 3 has no child), inside BeginTrans and rolled back: Updatable, per-field DataUpdatable, SourceTable and SourceField; read all rows; MoveLast " +
            "(the null-extended row); Edit, assign W 'new', Update, EditMode, Errors, read the row; Edit, assign V 'pv3', Update, Errors, read the row; AddNew, assign W 'added', Update, Errors; Requery and read all rows; " +
            "then COUNT(*) of CH and of PA. Asks what an edit of the child side of a null-extended row does, and whether the parent side and AddNew are accepted.",
            p =>
            {
                p.Call("eng", "BeginTrans").OpenRs("pr", "db", LeftJoinSql, Dao.OpenDynaset).Get("pr", "Updatable");
                FieldMeta(p, "pr", ["Name", "DataUpdatable", "SourceTable", "SourceField"]);
                p.ReadAll("pr", 8, 8).Call("pr", "MoveLast").ReadRow("pr");
                p.GetBind("pfs", "pr", "Fields").Item("pw", "pfs", A.Str("W")).Item("pv", "pfs", A.Str("V"));
                p.Call("pr", "Edit").Put("pw", "Value", A.Str("new")).Call("pr", "Update").Get("pr", "EditMode");
                Errs(p).ReadRow("pr");
                p.Call("pr", "Edit").Put("pv", "Value", A.Str("pv3")).Call("pr", "Update");
                Errs(p).ReadRow("pr");
                p.Call("pr", "AddNew").Put("pw", "Value", A.Str("added")).Call("pr", "Update");
                Errs(p).Call("pr", "Requery").ReadAll("pr", 8, 8);
                p.Release("pw").Release("pv").Release("pfs");
                Close(p, "pr");
                CountOf(p, "CH");
                CountOf(p, "PA");
                p.Call("eng", "Rollback");
            }, write: true);

        yield return On("main", "prov-parent-edit-two-children", Refs(RCur, "CUR-PROV", "CUR-EDIT"),
            "A dynaset over PA INNER JOIN CH (CHID 1 and 2 share parent 1; CHID 3 has parent 2) and a second dynaset over the same join at its second row, inside BeginTrans and rolled back. On the first, at row 1: Edit, assign " +
            "V 'changed', read V, Update, read V, Errors; MoveNext and read V (second child of parent 1); MoveNext and read V; the second dynaset reads V; Requery both and read all rows of each. Asks whether the parent field " +
            "change through one occurrence is seen in the other occurrence and in the other cursor, before and after Requery.",
            p =>
            {
                p.Call("eng", "BeginTrans").OpenRs("pr", "db", JoinSql, Dao.OpenDynaset).OpenRs("po", "db", JoinSql, Dao.OpenDynaset).Call("po", "MoveNext");
                Hold(p, "pr", "q_", "V");
                Hold(p, "po", "o_", "V");
                p.Call("pr", "Edit").Put("q_V", "Value", A.Str("changed")).Get("q_V", "Value").Call("pr", "Update").Get("q_V", "Value");
                Errs(p).Call("pr", "MoveNext").Get("q_V", "Value").Call("pr", "MoveNext").Get("q_V", "Value").Get("o_V", "Value");
                p.Call("pr", "Requery").ReadAll("pr", 8, 8).Call("po", "Requery").ReadAll("po", 8, 8);
                Unhold(p, "q_", "V");
                Unhold(p, "o_", "V");
                Close(p, "po");
                Close(p, "pr");
                p.Call("eng", "Rollback");
            }, write: true);

        yield return On("main", "prov-join-field-change", Refs(RCur, "CUR-PROV", "CUR-EDIT"),
            "A dynaset over PA INNER JOIN CH (PAID, V, CHID, PID, W), inside BeginTrans and rolled back: at row 1 Edit, assign PID 2 (an existing parent), Update, Errors, read the row, Requery and read all rows; Edit, assign PID 99 (no " +
            "such parent), Update, Errors, CancelUpdate; Edit, assign the parent key PAID 10 on a parent with children, Update, Errors, CancelUpdate; then read PA and CH through snapshots. Asks which side's key an edit of " +
            "a join field changes, and how declared referential integrity answers.",
            p =>
            {
                p.Call("eng", "BeginTrans").OpenRs("pr", "db", JoinSql, Dao.OpenDynaset);
                p.GetBind("pfs", "pr", "Fields").Item("pp", "pfs", A.Str("PID")).Item("pk", "pfs", A.Str("PAID"));
                p.Call("pr", "Edit").Put("pp", "Value", A.I4(2)).Call("pr", "Update");
                Errs(p).ReadRow("pr").Call("pr", "Requery").ReadAll("pr", 8, 8);
                p.Call("pr", "MoveFirst").Call("pr", "Edit").Put("pp", "Value", A.I4(99)).Call("pr", "Update");
                Errs(p).Call("pr", "CancelUpdate");
                p.Call("pr", "Edit").Put("pk", "Value", A.I4(10)).Call("pr", "Update");
                Errs(p).Call("pr", "CancelUpdate");
                p.Release("pp").Release("pk").Release("pfs");
                Close(p, "pr");
                p.OpenRs("sa", "db", "SELECT ID, V FROM PA ORDER BY ID", Dao.OpenSnapshot).ReadAll("sa", 8, 4).Call("sa", "Close");
                p.OpenRs("sb", "db", "SELECT ID, PID, W FROM CH ORDER BY ID", Dao.OpenSnapshot).ReadAll("sb", 8, 4).Call("sb", "Close");
                p.Call("eng", "Rollback");
            }, write: true);

        yield return On("main", "prov-inconsistent-referential-integrity", Refs(RCur, "CUR-PROV", "CUR-EDIT"),
            "For OpenRecordset option dbInconsistent (16) and then dbConsistent (32) on a dynaset over PA INNER JOIN CH, each inside BeginTrans and rolled back: Edit row 1, assign PID 99, Update, EditMode, Errors, CancelUpdate; Edit, " +
            "assign PAID 10 (parent key with children), Update, Errors, CancelUpdate; AddNew, assign PID 99 and W 'orphan', Update, Errors, CancelUpdate; Requery and read all rows; COUNT(*) of CH. Asks whether the " +
            "declared relationship still refuses the edits under each option.",
            p =>
            {
                foreach (int option in new[] { Inconsistent, Consistent })
                {
                    p.Call("eng", "BeginTrans").OpenRs("pr", "db", JoinSql, Dao.OpenDynaset, A.I4(option));
                    p.GetBind("pfs", "pr", "Fields").Item("pp", "pfs", A.Str("PID")).Item("pk", "pfs", A.Str("PAID")).Item("pw", "pfs", A.Str("W"));
                    p.Call("pr", "Edit").Put("pp", "Value", A.I4(99)).Call("pr", "Update").Get("pr", "EditMode");
                    Errs(p).Call("pr", "CancelUpdate");
                    p.Call("pr", "Edit").Put("pk", "Value", A.I4(10)).Call("pr", "Update");
                    Errs(p).Call("pr", "CancelUpdate");
                    p.Call("pr", "AddNew").Put("pp", "Value", A.I4(99)).Put("pw", "Value", A.Str("orphan")).Call("pr", "Update");
                    Errs(p).Call("pr", "CancelUpdate").Call("pr", "Requery").ReadAll("pr", 8, 8);
                    p.Release("pp").Release("pk").Release("pw").Release("pfs");
                    Close(p, "pr");
                    CountOf(p, "CH");
                    p.Call("eng", "Rollback");
                }
            }, write: true);

        yield return On("main", "prov-heap-duplicate-rows", Refs(RCur, "CUR-PROV", "CUR-EDIT"),
            "H has no key and two equal rows (1,'x'). Inside BeginTrans and rolled back, a dynaset over H: Updatable, per-field DataUpdatable, SourceTable and SourceField; read all rows; MoveFirst, Edit, assign B 'z', Update, " +
            "Errors; Requery and read all rows; MoveFirst, Delete, Errors, Requery and read all rows; AddNew (2,'y'), Update, Errors, Requery and read all rows. Then the same edit and delete on a table-type recordset over H. " +
            "Asks which of two equal heap rows an edit or delete reaches and what each refusal says.",
            p =>
            {
                p.Call("eng", "BeginTrans").OpenRs("pr", "db", "H", Dao.OpenDynaset).Get("pr", "Updatable");
                FieldMeta(p, "pr", ["Name", "DataUpdatable", "SourceTable", "SourceField"]);
                p.ReadAll("pr", 8, 4).Call("pr", "MoveFirst").GetBind("pfs", "pr", "Fields").Item("pb", "pfs", A.Str("B")).Item("pa", "pfs", A.Str("A"));
                p.Call("pr", "Edit").Put("pb", "Value", A.Str("z")).Call("pr", "Update");
                Errs(p).Call("pr", "Requery").ReadAll("pr", 8, 4).Call("pr", "MoveFirst").Call("pr", "Delete");
                Errs(p).Call("pr", "Requery").ReadAll("pr", 8, 4);
                p.Call("pr", "AddNew").Put("pa", "Value", A.I4(2)).Put("pb", "Value", A.Str("y")).Call("pr", "Update");
                Errs(p).Call("pr", "Requery").ReadAll("pr", 8, 4);
                p.Release("pa").Release("pb").Release("pfs");
                Close(p, "pr");
                p.Call("eng", "Rollback").Call("eng", "BeginTrans").OpenRs("pt", "db", "H", Dao.OpenTable).Call("pt", "MoveFirst").GetBind("tfs", "pt", "Fields").Item("tb", "tfs", A.Str("B"));
                p.Call("pt", "Edit").Put("tb", "Value", A.Str("z")).Call("pt", "Update");
                Errs(p).Call("pt", "MoveFirst").ReadRow("pt").Call("pt", "MoveNext").ReadRow("pt").Call("pt", "MoveFirst").Call("pt", "Delete");
                Errs(p).Call("pt", "MoveFirst").ReadAll("pt", 8, 4);
                p.Release("tb").Release("tfs");
                Close(p, "pt");
                p.Call("eng", "Rollback");
            }, write: true);

        yield return On("main", "prov-missing-projected-key", Refs(RCur, "CUR-PROV", "CUR-EDIT"),
            "A dynaset over \"SELECT N FROM T WHERE N = 40\" (rows 4 and 5, the key ID is not projected), inside BeginTrans and rolled back: Updatable, per-field DataUpdatable, SourceTable and SourceField; read all rows; MoveFirst, " +
            "Edit, assign N 41, Update, Errors, read all rows; Requery and read all rows; a snapshot of ID and N for IDs 4 and 5. Asks which base row an edit through a projection without the key reaches when two rows are equal.",
            p =>
            {
                p.Call("eng", "BeginTrans").OpenRs("pr", "db", "SELECT N FROM T WHERE N = 40", Dao.OpenDynaset).Get("pr", "Updatable");
                FieldMeta(p, "pr", ["Name", "DataUpdatable", "SourceTable", "SourceField"]);
                p.ReadAll("pr", 8, 4).Call("pr", "MoveFirst").GetBind("pfs", "pr", "Fields").Item("pn", "pfs", A.Str("N"));
                p.Call("pr", "Edit").Put("pn", "Value", A.I4(41)).Call("pr", "Update");
                Errs(p).ReadAll("pr", 8, 4).Call("pr", "Requery").ReadAll("pr", 8, 4);
                p.Release("pn").Release("pfs");
                Close(p, "pr");
                p.OpenRs("pb", "db", "SELECT ID, N FROM T WHERE ID IN (4, 5) ORDER BY ID", Dao.OpenSnapshot).ReadAll("pb", 4, 4).Call("pb", "Close");
                p.Call("eng", "Rollback");
            }, write: true);
    }

    private static void ProvSource(P p, string sql, string editField, Arg editValue)
    {
        p.Call("eng", "BeginTrans").OpenRs("pr", "db", sql, Dao.OpenDynaset).Get("pr", "Updatable");
        FieldMeta(p, "pr", MetaMembers);
        p.GetBind("pfs", "pr", "Fields").Item("pf", "pfs", A.Str(editField));
        p.Call("pr", "Edit").Put("pf", "Value", editValue).Get("pf", "Value").Call("pr", "Update").Get("pr", "EditMode");
        Errs(p).Call("pr", "CancelUpdate").Call("pr", "Requery").ReadAll("pr", 16, 8);
        p.Release("pf").Release("pfs");
        Close(p, "pr");
        p.Call("eng", "Rollback");
    }

    private static P CountOf(P p, string table) =>
        p.OpenRs("cn", "db", $"SELECT COUNT(*) FROM {table}", Dao.OpenSnapshot).ReadRow("cn").Call("cn", "Close").Release("cn");

    // CUR-BULK: large Memo and binary, GetChunk and AppendChunk -------------------------------------------------

    private static IEnumerable<Probe> Bulk()
    {
        byte[] bytes64 = [.. Enumerable.Range(0, 64).Select(i => (byte)i)];

        yield return On("main", "bulk-getrows-memo-binary", Refs(RCur, "CUR-BULK"),
            "LB holds a short Memo (row 1), a 12,000-character Memo (row 2) and an all-Null row 3. Edit row 3 of a dynaset, AppendChunk a 64-byte binary into Bn, Update. Then, on a table-type, a dynaset, a snapshot and a " +
            "forward-only recordset over LB: GetRows(3) and read BOF, EOF and ID; and on a dynaset over \"SELECT Bn FROM LB ORDER BY ID\" GetRows(3). The VARIANT of each call is captured exactly. Asks the VARIANT type and " +
            "length of a large Memo and of a binary cell, the Null cells and the array dimensions per type.",
            p =>
            {
                p.OpenRs("wr", "db", "SELECT ID, Bn FROM LB WHERE ID = 3", Dao.OpenDynaset).GetBind("wfs", "wr", "Fields").Item("wb", "wfs", A.Str("Bn"));
                p.Call("wr", "Edit").Call("wb", "AppendChunk", A.Bytes(bytes64)).Call("wr", "Update");
                Errs(p).Release("wb").Release("wfs");
                Close(p, "wr");
                foreach (int type in new[] { Dao.OpenTable, Dao.OpenDynaset, Dao.OpenSnapshot, Dao.OpenForwardOnly })
                {
                    p.OpenRs("gr", "db", "LB", type).Call("gr", "GetRows", A.I4(3)).Get("gr", "BOF").Get("gr", "EOF");
                    Val(p, "gr", "ID");
                    Close(p, "gr");
                }
                p.OpenRs("gb", "db", "SELECT Bn FROM LB ORDER BY ID", Dao.OpenDynaset).Call("gb", "GetRows", A.I4(3));
                Close(p, "gb");
            }, write: true);

        yield return On("main", "chunk-read-offsets", Refs(RCur, "CUR-BULK", "DAO-PROP"),
            "A dynaset over \"SELECT ID, Mm, Tx, Bn FROM LB WHERE ID = 2\" (12,000-character Memo; Bn Null): FieldSize of ID, Mm, Tx and Bn; GetChunk on Mm with (offset, size) (0,10), (0,0), (0,1), (11990,100), (11999,1), " +
            "(12000,1), (12001,1), (-1,5), (0,-1), (0,12000), (0,100000), Null and Long, Long and Null, the strings \"2\" and \"3\", Double 1.5 and 2.5; GetChunk (0,2) on Tx, (0,1) on ID and (0,4) on Bn. Asks the " +
            "FieldSize per type, the exact return VARIANT of GetChunk (type, length, units), the clamping or refusal at and beyond the end, and the argument coercion.",
            p =>
            {
                p.OpenRs("cr", "db", "SELECT ID, Mm, Tx, Bn FROM LB WHERE ID = 2", Dao.OpenDynaset);
                Hold(p, "cr", "c_", "ID", "Mm", "Tx", "Bn");
                foreach (string f in new[] { "ID", "Mm", "Tx", "Bn" })
                {
                    p.Get("c_" + f, "FieldSize");
                }
                (Arg Offset, Arg Size)[] calls =
                [
                    (A.I4(0), A.I4(10)), (A.I4(0), A.I4(0)), (A.I4(0), A.I4(1)), (A.I4(11990), A.I4(100)), (A.I4(11999), A.I4(1)), (A.I4(12000), A.I4(1)),
                    (A.I4(12001), A.I4(1)), (A.I4(-1), A.I4(5)), (A.I4(0), A.I4(-1)), (A.I4(0), A.I4(12000)), (A.I4(0), A.I4(100000)), (A.Null, A.I4(5)),
                    (A.I4(0), A.Null), (A.Str("2"), A.Str("3")), (A.R8(1.5), A.R8(2.5)),
                ];
                foreach ((Arg offset, Arg size) in calls)
                {
                    p.Call("c_Mm", "GetChunk", offset, size);
                }
                p.Call("c_Tx", "GetChunk", A.I4(0), A.I4(2)).Call("c_ID", "GetChunk", A.I4(0), A.I4(1)).Call("c_Bn", "GetChunk", A.I4(0), A.I4(4));
                Errs(p);
                Unhold(p, "c_", "ID", "Mm", "Tx", "Bn");
                Close(p, "cr");
            });

        yield return On("main", "chunk-append-memo-text", Refs(RCur, "CUR-BULK", "CUR-EDIT"),
            "A dynaset over \"SELECT ID, Mm, Tx FROM LB WHERE ID = 1\", inside BeginTrans and rolled back: AppendChunk on Mm with no Edit, Errors; Edit, AppendChunk \"abc\" and \"def\" on Mm, FieldSize, GetChunk (0,10), read " +
            "Value, Update, read Value and FieldSize; Edit, AppendChunk \"zzz\", CancelUpdate, read Value; Edit, AppendChunk on the Text field Tx and read FieldSize and Value, CancelUpdate; Edit, AppendChunk on the Long " +
            "field ID, Errors, CancelUpdate; Edit, AppendChunk Null, Long 5 and the bytes 65 66 on Mm, FieldSize, read Value, Update, read Value. Asks the state in which AppendChunk is accepted, whether the pending chunk " +
            "is visible through FieldSize and Value, the fields and argument types it refuses, and what CancelUpdate discards.",
            p =>
            {
                p.Call("eng", "BeginTrans").OpenRs("ar", "db", "SELECT ID, Mm, Tx FROM LB WHERE ID = 1", Dao.OpenDynaset);
                Hold(p, "ar", "m_", "ID", "Mm", "Tx");
                p.Call("m_Mm", "AppendChunk", A.Str("x"));
                Errs(p);
                p.Call("ar", "Edit").Call("m_Mm", "AppendChunk", A.Str("abc")).Call("m_Mm", "AppendChunk", A.Str("def")).Get("m_Mm", "FieldSize").Call("m_Mm", "GetChunk", A.I4(0), A.I4(10))
                    .Get("m_Mm", "Value").Call("ar", "Update").Get("m_Mm", "Value").Get("m_Mm", "FieldSize");
                p.Call("ar", "Edit").Call("m_Mm", "AppendChunk", A.Str("zzz")).Call("ar", "CancelUpdate").Get("m_Mm", "Value");
                p.Call("ar", "Edit").Call("m_Tx", "AppendChunk", A.Str("abc")).Get("m_Tx", "FieldSize").Get("m_Tx", "Value").Call("ar", "CancelUpdate");
                p.Call("ar", "Edit").Call("m_ID", "AppendChunk", A.Str("x"));
                Errs(p).Call("ar", "CancelUpdate");
                p.Call("ar", "Edit").Call("m_Mm", "AppendChunk", A.Null).Call("m_Mm", "AppendChunk", A.I4(5)).Call("m_Mm", "AppendChunk", A.Bytes(65, 66)).Get("m_Mm", "FieldSize")
                    .Get("m_Mm", "Value").Call("ar", "Update").Get("m_Mm", "Value");
                Errs(p);
                Unhold(p, "m_", "ID", "Mm", "Tx");
                Close(p, "ar");
                p.Call("eng", "Rollback");
            }, write: true);

        yield return On("main", "chunk-binary-roundtrip", Refs(RCur, "CUR-BULK", "CUR-EDIT"),
            "A dynaset over \"SELECT ID, Bn FROM LB ORDER BY ID\", inside BeginTrans and rolled back: FieldSize of Bn on rows 1 to 3 (all Null); at row 3 Edit, AppendChunk bytes 0..31, then bytes 32..63, FieldSize, GetChunk (0,64), " +
            "Update, FieldSize, GetChunk (0,64), GetChunk (60,10), GetChunk (64,1), read Value; Edit, assign Value the bytes 1 2 3, FieldSize, Update, GetChunk (0,8), read Value; Edit, assign Value Null, Update, FieldSize. " +
            "Asks the exact VARIANT of binary reads, whether chunk appends and a whole-value assignment give the same stored length, and Null versus empty after the edits.",
            p =>
            {
                byte[] first = [.. Enumerable.Range(0, 32).Select(i => (byte)i)];
                byte[] second = [.. Enumerable.Range(32, 32).Select(i => (byte)i)];
                p.Call("eng", "BeginTrans").OpenRs("br", "db", "SELECT ID, Bn FROM LB ORDER BY ID", Dao.OpenDynaset);
                Hold(p, "br", "b_", "Bn");
                p.Get("b_Bn", "FieldSize").Call("br", "MoveNext").Get("b_Bn", "FieldSize").Call("br", "MoveNext").Get("b_Bn", "FieldSize");
                p.Call("br", "Edit").Call("b_Bn", "AppendChunk", A.Bytes(first)).Call("b_Bn", "AppendChunk", A.Bytes(second)).Get("b_Bn", "FieldSize").Call("b_Bn", "GetChunk", A.I4(0), A.I4(64));
                p.Call("br", "Update").Get("b_Bn", "FieldSize").Call("b_Bn", "GetChunk", A.I4(0), A.I4(64)).Call("b_Bn", "GetChunk", A.I4(60), A.I4(10)).Call("b_Bn", "GetChunk", A.I4(64), A.I4(1)).Get("b_Bn", "Value");
                p.Call("br", "Edit").Put("b_Bn", "Value", A.Bytes(1, 2, 3)).Get("b_Bn", "FieldSize").Call("br", "Update").Call("b_Bn", "GetChunk", A.I4(0), A.I4(8)).Get("b_Bn", "Value");
                p.Call("br", "Edit").Put("b_Bn", "Value", A.Null).Call("br", "Update").Get("b_Bn", "FieldSize").Get("b_Bn", "Value");
                Errs(p);
                Unhold(p, "b_", "Bn");
                Close(p, "br");
                p.Call("eng", "Rollback");
            }, write: true);
    }

    // CUR-EDIT ----------------------------------------------------------------------------------------------------

    private static IEnumerable<Probe> Edits()
    {
        yield return On("main", "edit-conflict-restore-value", Refs(RCur, "CUR-EDIT", "CUR-ID"),
            "Two dynasets (r1, r2) over \"SELECT ID, N FROM T WHERE ID = 3\", inside BeginTrans and rolled back. r1 has LockEdits False and Edit with N read; r2 Edit, assign N 31, Update, Edit, assign N 30 (the original value), " +
            "Update; r1 assigns N 35 and Update, Errors, read N, EditMode; r1 Requery and read N; then the same with r2 assigning 31 once and not restoring. Asks whether an optimistic update by r1 is refused when another writer changed " +
            "the field and restored its original value, compared with the writer that did not restore.",
            p =>
            {
                p.Call("eng", "BeginTrans");
                foreach (bool restore in new[] { true, false })
                {
                    p.OpenRs("r1", "db", "SELECT ID, N FROM T WHERE ID = 3", Dao.OpenDynaset).Put("r1", "LockEdits", A.Bool(false));
                    Hold(p, "r1", "x_", "N");
                    p.OpenRs("r2", "db", "SELECT ID, N FROM T WHERE ID = 3", Dao.OpenDynaset);
                    Hold(p, "r2", "y_", "N");
                    p.Call("r1", "Edit").Get("x_N", "Value");
                    p.Call("r2", "Edit").Put("y_N", "Value", A.I4(31)).Call("r2", "Update");
                    if (restore)
                    {
                        p.Call("r2", "Edit").Put("y_N", "Value", A.I4(30)).Call("r2", "Update");
                    }
                    Errs(p).Put("x_N", "Value", A.I4(35)).Call("r1", "Update");
                    Errs(p).Get("x_N", "Value").Get("r1", "EditMode").Call("r1", "CancelUpdate").Call("r1", "Requery").Get("x_N", "Value");
                    Unhold(p, "x_", "N");
                    Unhold(p, "y_", "N");
                    Close(p, "r2");
                    Close(p, "r1");
                    p.Call("eng", "Rollback").Call("eng", "BeginTrans");
                }
                p.Call("eng", "Rollback");
            }, write: true);

        yield return On("main", "edit-under-commit", Refs(RCur, "CUR-EDIT", "CUR-POP", "CUR-LIFE"),
            "A dynaset rs over \"SELECT ID, N FROM T ORDER BY ID\" opened before BeginTrans: BeginTrans; Edit row 1, assign N 11, Update, EditMode; AddNew ID 20 N 200, Update; MoveFirst, MoveNext, Delete, read BOF, EOF and ID; " +
            "RecordCount; a second dynaset ro opened and read to EOF; CommitTrans; read rs RecordCount, BOF, EOF, ID and EditMode without Requery; rs Requery and read all rows; a fresh snapshot reads all rows. " +
            "Asks the visibility of the edits to a second cursor before and after CommitTrans, and the state of the held cursor after the commit.",
            p =>
            {
                p.OpenRs("rs", "db", "SELECT ID, N FROM T ORDER BY ID", Dao.OpenDynaset);
                Hold(p, "rs", "e_", "ID", "N");
                p.Call("eng", "BeginTrans").Call("rs", "MoveFirst").Call("rs", "Edit").Put("e_N", "Value", A.I4(11)).Call("rs", "Update").Get("rs", "EditMode");
                p.Call("rs", "AddNew").Put("e_ID", "Value", A.I4(20)).Put("e_N", "Value", A.I4(200)).Call("rs", "Update");
                p.Call("rs", "MoveFirst").Call("rs", "MoveNext").Call("rs", "Delete").Get("rs", "BOF").Get("rs", "EOF");
                Val(p, "rs", "ID").Get("rs", "RecordCount");
                p.OpenRs("ro", "db", "SELECT ID, N FROM T ORDER BY ID", Dao.OpenDynaset).ReadAll("ro", 16, 4).Call("ro", "Close");
                p.Call("eng", "CommitTrans").Get("rs", "RecordCount").Get("rs", "BOF").Get("rs", "EOF");
                Val(p, "rs", "ID").Get("rs", "EditMode");
                p.Call("rs", "Requery").ReadAll("rs", 16, 4);
                p.OpenRs("rf", "db", "SELECT ID, N FROM T ORDER BY ID", Dao.OpenSnapshot).ReadAll("rf", 16, 4).Call("rf", "Close");
                Errs(p);
                Unhold(p, "e_", "ID", "N");
                Close(p, "rs");
            }, write: true);

        yield return On("main", "lockedits-put-after-open", Refs(RCur, "CUR-EDIT", "CUR-PROV"),
            "For a table-type, a dynaset, a snapshot and a forward-only recordset over T: read LockEdits; assign False and read; assign True and read; where Edit is accepted, Edit, assign LockEdits False during the pending Edit, " +
            "read LockEdits and EditMode, CancelUpdate; Errors after each type. Asks whether LockEdits can be changed after open, per type and while an Edit is pending.",
            p =>
            {
                foreach (int type in new[] { Dao.OpenTable, Dao.OpenDynaset, Dao.OpenSnapshot, Dao.OpenForwardOnly })
                {
                    p.OpenRs("lr", "db", "T", type).Get("lr", "LockEdits").Put("lr", "LockEdits", A.Bool(false)).Get("lr", "LockEdits").Put("lr", "LockEdits", A.Bool(true)).Get("lr", "LockEdits");
                    p.Call("lr", "Edit").Put("lr", "LockEdits", A.Bool(false)).Get("lr", "LockEdits").Get("lr", "EditMode").Call("lr", "CancelUpdate");
                    Errs(p);
                    Close(p, "lr");
                }
            }, write: true);

        yield return On("main", "field-value-trio-during-edit", Refs(RCur, "CUR-EDIT", "DAO-PROP"),
            "A dynaset over \"SELECT ID, N FROM T WHERE ID = 3\" with held Field N; at each point read N Value, OriginalValue and VisibleValue, each by its own call: before Edit; after Edit; after assigning 99; after Update; after Edit, " +
            "assigning 100 and CancelUpdate; after AddNew; after assigning 5 in the new record; after CancelUpdate. Inside BeginTrans, rolled back. Asks what each of the three reads returns in each edit state.",
            p =>
            {
                p.Call("eng", "BeginTrans").OpenRs("rs", "db", "SELECT ID, N FROM T WHERE ID = 3", Dao.OpenDynaset);
                Hold(p, "rs", "t_", "N");
                Trio(p, "t_N");
                p.Call("rs", "Edit");
                Trio(p, "t_N");
                p.Put("t_N", "Value", A.I4(99));
                Trio(p, "t_N");
                p.Call("rs", "Update");
                Trio(p, "t_N");
                p.Call("rs", "Edit").Put("t_N", "Value", A.I4(100)).Call("rs", "CancelUpdate");
                Trio(p, "t_N");
                p.Call("rs", "AddNew");
                Trio(p, "t_N");
                p.Put("t_N", "Value", A.I4(5));
                Trio(p, "t_N");
                p.Call("rs", "CancelUpdate");
                Trio(p, "t_N");
                Errs(p);
                Unhold(p, "t_", "N");
                Close(p, "rs");
                p.Call("eng", "Rollback");
            }, write: true);

        yield return On("main", "field-value-trio-other-writer", Refs(RCur, "CUR-EDIT", "CUR-ID", "DAO-PROP"),
            "Two dynasets (r1 with LockEdits False, r2) over \"SELECT ID, N FROM T WHERE ID = 3\", inside BeginTrans and rolled back: r1 Edit and assign N 35; read N Value, OriginalValue and VisibleValue of r1; r2 Edit, assign 99, " +
            "Update; read the three of r1 and of r2; r1 Update, Errors, read the three of r1; r1 Requery and read the three. Asks what OriginalValue and VisibleValue return when another writer changed the row under a pending edit.",
            p =>
            {
                p.Call("eng", "BeginTrans").OpenRs("r1", "db", "SELECT ID, N FROM T WHERE ID = 3", Dao.OpenDynaset).Put("r1", "LockEdits", A.Bool(false));
                Hold(p, "r1", "x_", "N");
                p.OpenRs("r2", "db", "SELECT ID, N FROM T WHERE ID = 3", Dao.OpenDynaset);
                Hold(p, "r2", "y_", "N");
                p.Call("r1", "Edit").Put("x_N", "Value", A.I4(35));
                Trio(p, "x_N");
                p.Call("r2", "Edit").Put("y_N", "Value", A.I4(99)).Call("r2", "Update");
                Trio(p, "x_N");
                Trio(p, "y_N");
                p.Call("r1", "Update");
                Errs(p);
                Trio(p, "x_N");
                p.Call("r1", "Requery");
                Trio(p, "x_N");
                Unhold(p, "x_", "N");
                Unhold(p, "y_", "N");
                Close(p, "r2");
                Close(p, "r1");
                p.Call("eng", "Rollback");
            }, write: true);

        yield return On("main", "assign-without-edit-by-type", Refs(RCur, "CUR-EDIT", "ERR-BIND"),
            "For a table-type (Index PK_T), a dynaset, a snapshot and a forward-only recordset over T, and a snapshot over \"SELECT N FROM T GROUP BY N\": with no Edit, assign N the value 77, read Errors, the field Value and " +
            "EditMode; then Edit where it is accepted, assign 77, Update, read Value; CancelUpdate. Asks whether and how a Value assignment outside Edit or AddNew is refused per type, and its effect on the buffer.",
            p =>
            {
                (string Source, int Type)[] cases =
                [
                    ("T", Dao.OpenTable), ("T", Dao.OpenDynaset), ("T", Dao.OpenSnapshot), ("T", Dao.OpenForwardOnly), ("SELECT N FROM T GROUP BY N", Dao.OpenSnapshot),
                ];
                p.Call("eng", "BeginTrans");
                foreach ((string source, int type) in cases)
                {
                    p.OpenRs("ar", "db", source, type);
                    if (type == Dao.OpenTable)
                    {
                        p.Put("ar", "Index", A.Str("PK_T"));
                    }
                    Hold(p, "ar", "n_", "N");
                    p.Put("n_N", "Value", A.I4(77));
                    Errs(p).Get("n_N", "Value").Get("ar", "EditMode");
                    p.Call("ar", "Edit").Put("n_N", "Value", A.I4(77)).Call("ar", "Update").Get("n_N", "Value");
                    Errs(p).Call("ar", "CancelUpdate");
                    Unhold(p, "n_", "N");
                    Close(p, "ar");
                }
                p.Call("eng", "Rollback");
            }, write: true);

        yield return On("main", "addnew-landing-by-index-and-type", Refs(RCur, "CUR-EDIT", "CUR-POS", "CUR-ID"),
            "For a table-type with Index IX_TN, IX_TND and PK_T and a dynaset over T, over \"SELECT ID, N FROM T ORDER BY N DESC\" and over \"SELECT ID, N FROM T ORDER BY N\", each at its row 3 (Move 2) and inside BeginTrans, rolled back: " +
            "AddNew, assign ID 20 and N 35, Update; read BOF, EOF, ID and N of the current row, LastModified; MoveNext and read; MovePrevious twice and read; MoveLast and read; RecordCount. Asks which row is current after AddNew " +
            "and Update and where the new row sits in each ordering.",
            p =>
            {
                (string Label, string Source, int Type, string Index)[] cases =
                [
                    ("table-ix-tn", "T", Dao.OpenTable, "IX_TN"), ("table-ix-tnd", "T", Dao.OpenTable, "IX_TND"), ("table-pk", "T", Dao.OpenTable, "PK_T"),
                    ("dynaset-plain", "SELECT ID, N FROM T", Dao.OpenDynaset, ""), ("dynaset-order-desc", "SELECT ID, N FROM T ORDER BY N DESC", Dao.OpenDynaset, ""),
                    ("dynaset-order-asc", "SELECT ID, N FROM T ORDER BY N", Dao.OpenDynaset, ""),
                ];
                foreach ((_, string source, int type, string index) in cases)
                {
                    p.Call("eng", "BeginTrans").OpenRs("lr", "db", source, type);
                    if (index.Length > 0)
                    {
                        p.Put("lr", "Index", A.Str(index));
                    }
                    p.Call("lr", "MoveFirst").Call("lr", "Move", A.I4(2));
                    Hold(p, "lr", "l_", "ID", "N");
                    p.Call("lr", "AddNew").Put("l_ID", "Value", A.I4(20)).Put("l_N", "Value", A.I4(35)).Call("lr", "Update");
                    p.Get("lr", "BOF").Get("lr", "EOF").Get("l_ID", "Value").Get("l_N", "Value").Get("lr", "LastModified");
                    p.Call("lr", "MoveNext").Get("l_ID", "Value").Call("lr", "MovePrevious").Call("lr", "MovePrevious").Get("l_ID", "Value").Call("lr", "MoveLast").Get("l_ID", "Value").Get("lr", "RecordCount");
                    Unhold(p, "l_", "ID", "N");
                    Close(p, "lr");
                    p.Call("eng", "Rollback");
                }
            }, write: true);
    }

    private static P Trio(P p, string field) => p.Get(field, "Value").Get(field, "OriginalValue").Get(field, "VisibleValue");

    // CUR-LIFE ----------------------------------------------------------------------------------------------------

    private static IEnumerable<Probe> Lifetimes()
    {
        yield return On("main", "delete-by-other-cursor-current-row", Refs(RCur, "CUR-LIFE", "CUR-POS", "CUR-POP"),
            "For a table-type (Index PK_T), a dynaset and a snapshot rs over T ordered by ID, at row 3 (Move 2) with a held ID, inside BeginTrans and rolled back: a second dynaset rd at row 3 calls Delete. On rs read BOF, EOF, " +
            "ID, RecordCount; MoveNext and read; MovePrevious and read; MovePrevious and read; Edit, Errors, CancelUpdate; Requery and read. Then for a dynaset and its Clone rc: rc deletes the row that rs is on and rs is read " +
            "the same way. Asks the state of a cursor whose current row another cursor, or its own clone, deleted, per type.",
            p =>
            {
                foreach (int type in new[] { Dao.OpenTable, Dao.OpenDynaset, Dao.OpenSnapshot })
                {
                    p.Call("eng", "BeginTrans").OpenRs("rs", "db", type == Dao.OpenTable ? "T" : "SELECT ID, N FROM T ORDER BY ID", type);
                    if (type == Dao.OpenTable)
                    {
                        p.Put("rs", "Index", A.Str("PK_T"));
                    }
                    p.Call("rs", "MoveFirst").Call("rs", "Move", A.I4(2));
                    Hold(p, "rs", "d_", "ID");
                    p.OpenRs("rd", "db", "SELECT ID, N FROM T ORDER BY ID", Dao.OpenDynaset).Call("rd", "Move", A.I4(2)).Call("rd", "Delete");
                    DeletedViewState(p, "rs", type != Dao.OpenTable);
                    Unhold(p, "d_", "ID");
                    Close(p, "rd");
                    Close(p, "rs");
                    p.Call("eng", "Rollback");
                }
                p.Call("eng", "BeginTrans").OpenRs("rs", "db", "SELECT ID, N FROM T ORDER BY ID", Dao.OpenDynaset).Call("rs", "MoveFirst").Call("rs", "Move", A.I4(2));
                Hold(p, "rs", "d_", "ID");
                p.CallBind("rc", "rs", "Clone").Call("rc", "MoveFirst").Call("rc", "Move", A.I4(2)).Call("rc", "Delete");
                DeletedViewState(p, "rs", true);
                Unhold(p, "d_", "ID");
                Close(p, "rc");
                Close(p, "rs");
                p.Call("eng", "Rollback");
            }, write: true);

        yield return On("main", "open-dynamic-and-option-matrix", Refs(RCur, "CUR-PROV", "CUR-LIFE", "DAO-QUERY"),
            "Database.OpenRecordset over T or \"SELECT ID, N FROM T ORDER BY ID\" with these (type, options): (T, 16), (SQL, 16), (SQL dynaset 2, forward-only option 256), (SQL snapshot 4, 256), (SQL forward-only 8, 256), (T table 1, " +
            "read-only option 4), (SQL snapshot 4, append-only option 8), (SQL dynaset 2, 12), (SQL dynaset 2, 48 = Inconsistent plus Consistent); a temporary QueryDef over the SQL with OpenRecordset type 16 and type 2 with option 256; a " +
            "dynaset's own OpenRecordset with type 16. Each success reads Type, Updatable, Bookmarkable, Restartable and RecordCount and the ID of the first row, then closes. Asks which type and option combinations are accepted, the " +
            "recordset kind they give, and the refusals. The pass-through (64) and async (1024) options are not used.",
            p =>
            {
                const string sql = "SELECT ID, N FROM T ORDER BY ID";
                (string Label, string Source, int Type, int Options)[] cases =
                [
                    ("t-16", "T", 16, 0), ("sql-16", sql, 16, 0), ("sql-dynaset-fwd", sql, Dao.OpenDynaset, 256), ("sql-snap-fwd", sql, Dao.OpenSnapshot, 256),
                    ("sql-fwd-fwd", sql, Dao.OpenForwardOnly, 256), ("t-table-ro", "T", Dao.OpenTable, Dao.ReadOnly), ("sql-snap-append", sql, Dao.OpenSnapshot, Dao.AppendOnly),
                    ("sql-dynaset-ro-append", sql, Dao.OpenDynaset, Dao.ReadOnly | Dao.AppendOnly), ("sql-dynaset-both-consistency", sql, Dao.OpenDynaset, Inconsistent | Consistent),
                ];
                foreach ((_, string source, int type, int options) in cases)
                {
                    if (options == 0)
                    {
                        p.CallBind("xr", "db", "OpenRecordset", A.Str(source), A.I4(type));
                    }
                    else
                    {
                        p.CallBind("xr", "db", "OpenRecordset", A.Str(source), A.I4(type), A.I4(options));
                    }
                    OpenedState(p, "xr");
                }
                p.CallBind("qd", "db", "CreateQueryDef", A.Str(""), A.Str(sql));
                p.CallBind("xr", "qd", "OpenRecordset", A.I4(16));
                OpenedState(p, "xr");
                p.CallBind("xr", "qd", "OpenRecordset", A.I4(Dao.OpenDynaset), A.I4(256));
                OpenedState(p, "xr");
                p.Call("qd", "Close").Release("qd");
                p.OpenRs("pr", "db", sql, Dao.OpenDynaset).CallBind("xr", "pr", "OpenRecordset", A.I4(16));
                OpenedState(p, "xr");
                Errs(p);
                Close(p, "pr");
            }, write: true);
    }

    private static void DeletedViewState(P p, string rs, bool abs)
    {
        p.Get(rs, "BOF").Get(rs, "EOF");
        if (abs)
        {
            p.Get(rs, "AbsolutePosition");
        }
        p.Get("d_ID", "Value").Get(rs, "RecordCount").Call(rs, "MoveNext").Get(rs, "BOF").Get(rs, "EOF").Get("d_ID", "Value");
        p.Call(rs, "MovePrevious").Get("d_ID", "Value").Call(rs, "MovePrevious").Get("d_ID", "Value");
        p.Call(rs, "Edit");
        Errs(p).Call(rs, "CancelUpdate").Call(rs, "Requery").Get(rs, "BOF").Get(rs, "EOF").Get("d_ID", "Value").Get(rs, "RecordCount");
    }

    private static void OpenedState(P p, string rs)
    {
        p.Get(rs, "Type").Get(rs, "Updatable").Get(rs, "Bookmarkable").Get(rs, "Restartable").Get(rs, "RecordCount");
        Val(p, rs, "ID");
        p.Call(rs, "Close").Release(rs);
    }

    // R-DAO-1: members of recordset Fields, Properties and QueryDefs --------------------------------------------

    private static IEnumerable<Probe> Members()
    {
        yield return On("main", "recordset-collect-member", Refs(RDao, "DAO-COLL", "DAO-ID", "CUR-EDIT"),
            "A dynaset over \"SELECT ID, N, S FROM T WHERE ID = 3\", inside BeginTrans and rolled back: the property Collect read with the index Long 0, 1, 3 and -1, the strings \"N\", \"n\" and \"T.N\", Null and Empty; then, with no Edit, Collect " +
            "assigned 41 for \"N\"; Edit, Collect assigned 41 for \"N\" and 42 for index 1, read both through Collect and through Fields, Update, read; Collect assigned for the unknown name \"Nope\". Asks whether the hidden Collect member " +
            "exists on a recordset, its index forms and refusals, and whether a write through it equals a Field write.",
            p =>
            {
                p.Call("eng", "BeginTrans").OpenRs("rs", "db", "SELECT ID, N, S FROM T WHERE ID = 3", Dao.OpenDynaset);
                foreach (Arg index in new[] { A.I4(0), A.I4(1), A.I4(3), A.I4(-1), A.Str("N"), A.Str("n"), A.Str("T.N"), A.Null, A.Empty })
                {
                    p.Get("rs", "Collect", index);
                }
                p.Put("rs", "Collect", A.I4(41), A.Str("N"));
                Errs(p).Call("rs", "Edit").Put("rs", "Collect", A.I4(41), A.Str("N")).Put("rs", "Collect", A.I4(42), A.I4(1)).Get("rs", "Collect", A.Str("N")).Get("rs", "Collect", A.I4(1));
                p.ReadRow("rs").Call("rs", "Update");
                Errs(p).ReadRow("rs");
                p.Put("rs", "Collect", A.I4(1), A.Str("Nope"));
                Errs(p);
                Close(p, "rs");
                p.Call("eng", "Rollback");
            }, write: true);

        yield return On("main", "join-field-names-and-lookup", Refs(RDao, "DAO-COLL", "DAO-ID", "CUR-PROV"),
            "Dynasets over \"SELECT PA.ID, PA.V, CH.ID, CH.PID, CH.W FROM PA INNER JOIN CH ON PA.ID = CH.PID ORDER BY CH.ID\" and \"SELECT * FROM PA INNER JOIN CH ON PA.ID = CH.PID\": Fields Count; for each field Name, " +
            "SourceTable, SourceField, OrdinalPosition and Value; then Fields.Item with \"ID\", \"PA.ID\", \"CH.ID\", \"ID1\", \"ID2\", \"[ID]\", \"V\", \"PA.V\", \"W\", Long 0 to 4 and Long 5, each followed by Name and Value. " +
            "Asks how duplicate output names are named and which lookup forms find which column.",
            p =>
            {
                int q = 0;
                foreach (string sql in new[]
                {
                    "SELECT PA.ID, PA.V, CH.ID, CH.PID, CH.W FROM PA INNER JOIN CH ON PA.ID = CH.PID ORDER BY CH.ID",
                    "SELECT * FROM PA INNER JOIN CH ON PA.ID = CH.PID",
                })
                {
                    p.OpenRs("jr", "db", sql, Dao.OpenDynaset).GetBind("jfs", "jr", "Fields").Get("jfs", "Count");
                    p.Each("jfs", "jf", 12, b => b.Get("jf", "Name").Get("jf", "SourceTable").Get("jf", "SourceField").Get("jf", "OrdinalPosition").Get("jf", "Value"));
                    Arg[] lookups =
                    [
                        A.Str("ID"), A.Str("PA.ID"), A.Str("CH.ID"), A.Str("ID1"), A.Str("ID2"), A.Str("[ID]"), A.Str("V"), A.Str("PA.V"), A.Str("W"),
                        A.I4(0), A.I4(1), A.I4(2), A.I4(3), A.I4(4), A.I4(5),
                    ];
                    for (int i = 0; i < lookups.Length; i++)
                    {
                        p.Item($"jl{q}_{i}", "jfs", lookups[i]).Get($"jl{q}_{i}", "Name").Get($"jl{q}_{i}", "Value");
                    }
                    Errs(p).Release("jfs");
                    Close(p, "jr");
                    q++;
                }
            });

        yield return On("main", "fields-lookup-forms-on-recordset", Refs(RDao, "DAO-COLL", "ERR-BIND"),
            "Fields.Item of a dynaset over \"SELECT ID, N, S FROM T WHERE ID = 3\" with each index: \"N\", \"n\", \" N\", \"N \", \"[N]\", \"T.N\", \"T.n\", \"!N\", Long 0, 1, 2, 3, -1, Double 1.5, the string \"1\", True, False, Null, Empty and the " +
            "missing-argument marker; each successful lookup followed by Name and Value, and Errors at the end. Asks the lookup forms the recordset Fields collection accepts and what each selects, compared with TableDefs and Fields " +
            "of a TableDef (RDao1Err1 coll-name-variants).",
            p =>
            {
                p.OpenRs("fr", "db", "SELECT ID, N, S FROM T WHERE ID = 3", Dao.OpenDynaset).GetBind("ffs", "fr", "Fields").Get("ffs", "Count");
                Arg[] lookups =
                [
                    A.Str("N"), A.Str("n"), A.Str(" N"), A.Str("N "), A.Str("[N]"), A.Str("T.N"), A.Str("T.n"), A.Str("!N"), A.I4(0), A.I4(1), A.I4(2), A.I4(3), A.I4(-1),
                    A.R8(1.5), A.Str("1"), A.Bool(true), A.Bool(false), A.Null, A.Empty, A.Missing,
                ];
                for (int i = 0; i < lookups.Length; i++)
                {
                    p.Item("fl" + i, "ffs", lookups[i]).Get("fl" + i, "Name").Get("fl" + i, "Value");
                }
                Errs(p).Release("ffs");
                Close(p, "fr");
            });

        yield return On("main", "query-field-metadata-aggregates", Refs(RDao, "DAO-PROP", "DAO-COLL", "CUR-PROV"),
            "A snapshot over \"SELECT Count(*) AS Cn, Count(S) AS Cs, Sum(N) AS Sn, Avg(N) AS An, Min(S) AS Ms, Max(D) AS Md, Min(Fl) AS Mf FROM T\": for each Field, by its own call, Name, Type, Size, Attributes, DataUpdatable, " +
            "SourceTable, SourceField, Required, OrdinalPosition, CollatingOrder, AllowZeroLength, ValidationRule, ValidationText, DefaultValue, CollectionIndex, ForeignName, Expression and ValidateOnSet; then the row. " +
            "Asks the Type, Size and origin members that an aggregate column reports.",
            p =>
            {
                p.OpenRs("mr", "db", "SELECT Count(*) AS Cn, Count(S) AS Cs, Sum(N) AS Sn, Avg(N) AS An, Min(S) AS Ms, Max(D) AS Md, Min(Fl) AS Mf FROM T", Dao.OpenSnapshot);
                FieldMeta(p, "mr", WideMetaMembers);
                p.ReadRow("mr");
                Close(p, "mr");
            });

        yield return On("main", "query-field-metadata-expressions", Refs(RDao, "DAO-PROP", "DAO-COLL", "CUR-PROV"),
            "A snapshot over \"SELECT N + 1 AS A1, N * 1.5 AS A2, S & 'x' AS A3, IIf(N > 20, 'a', 'b') AS A4, CInt(N) AS A5, CDbl(N) AS A6, Format(D, 'yyyy') AS A7, Now() AS A8, Null AS A9, True AS A10, 'lit' AS A11, 7 AS A12, " +
            "7.5 AS A13, #1/1/2020# AS A14 FROM T WHERE ID = 1\": for each Field, by its own call, Name, Type, Size, Attributes, DataUpdatable, SourceTable, SourceField, Required, OrdinalPosition, CollatingOrder, AllowZeroLength, " +
            "ValidationRule, ValidationText, DefaultValue, CollectionIndex, ForeignName, Expression and ValidateOnSet; then ReadRow reads the Value of each field. Asks the Type, Size and origin members of expression and literal columns.",
            p =>
            {
                p.OpenRs("mr", "db", "SELECT N + 1 AS A1, N * 1.5 AS A2, S & 'x' AS A3, IIf(N > 20, 'a', 'b') AS A4, CInt(N) AS A5, CDbl(N) AS A6, Format(D, 'yyyy') AS A7, Now() AS A8, " +
                    "Null AS A9, True AS A10, 'lit' AS A11, 7 AS A12, 7.5 AS A13, #1/1/2020# AS A14 FROM T WHERE ID = 1", Dao.OpenSnapshot);
                FieldMeta(p, "mr", WideMetaMembers);
                p.ReadRow("mr");
                Close(p, "mr");
            });

        yield return On("main", "recordset-field-properties", Refs(RDao, "DAO-PROP", "DAO-COLL"),
            "For a Field of a dynaset over T (N), a Field of a join (PA.V) and an expression Field (N + 1 AS Np): Properties Count, and for each Property, by its own call, Name, Type, Inherited and Value; then Properties.Item with \"Name\", " +
            "\"Type\" and \"Nope\". Asks which Properties a recordset Field publishes, their kinds and values, and the refusals.",
            p =>
            {
                foreach ((string sql, string field) in new[]
                {
                    ("SELECT ID, N FROM T WHERE ID = 3", "N"),
                    ("SELECT PA.ID, PA.V FROM PA INNER JOIN CH ON PA.ID = CH.PID", "V"),
                    ("SELECT N + 1 AS Np FROM T WHERE ID = 3", "Np"),
                })
                {
                    p.OpenRs("pr", "db", sql, Dao.OpenDynaset).GetBind("pfs", "pr", "Fields").Item("pf", "pfs", A.Str(field)).GetBind("pps", "pf", "Properties").Get("pps", "Count");
                    p.Each("pps", "pp", 40, b => b.Get("pp", "Name").Get("pp", "Type").Get("pp", "Inherited").Get("pp", "Value"));
                    string[] names = ["Name", "Type", "Nope"];
                    for (int i = 0; i < names.Length; i++)
                    {
                        p.Item("pi" + i, "pps", A.Str(names[i])).Get("pi" + i, "Value");
                    }
                    Errs(p).Release("pps").Release("pf").Release("pfs");
                    Close(p, "pr");
                }
            });

        yield return On("main", "querydef-maxrecords-effect", Refs(RDao, "DAO-QUERY", "DAO-PROP", "CUR-POP"),
            "A temporary QueryDef over \"SELECT ID, N FROM T ORDER BY ID\": read MaxRecords and QueryTimeout; for MaxRecords 3, 1, 0, 100 and -1: assign, read it back, and for a snapshot and a dynaset opened from the QueryDef read " +
            "RecordCount, MoveLast, RecordCount, then close; finally assign MaxRecords the string \"abc\" and Null. Asks whether MaxRecords limits a local recordset, its accepted values and read-back.",
            p =>
            {
                p.CallBind("qd", "db", "CreateQueryDef", A.Str(""), A.Str("SELECT ID, N FROM T ORDER BY ID")).Get("qd", "MaxRecords").Get("qd", "QueryTimeout");
                foreach (int max in new[] { 3, 1, 0, 100, -1 })
                {
                    p.Put("qd", "MaxRecords", A.I4(max)).Get("qd", "MaxRecords");
                    foreach (int type in new[] { Dao.OpenSnapshot, Dao.OpenDynaset })
                    {
                        p.CallBind("mr", "qd", "OpenRecordset", A.I4(type)).Get("mr", "RecordCount").Call("mr", "MoveLast").Get("mr", "RecordCount");
                        Close(p, "mr");
                    }
                }
                p.Put("qd", "MaxRecords", A.Str("abc")).Put("qd", "MaxRecords", A.Null);
                Errs(p).Call("qd", "Close").Release("qd");
            }, write: true);

        yield return On("main", "recordset-closed-member-inventory", Refs(RDao, "DAO-LIFE", "DAO-CLOSE-OWN", "ERR-CLOSE"),
            "A dynaset over T with its Fields collection and Field N held is closed. After the Close, each of AbsolutePosition, BOF, Bookmark, Bookmarkable, CacheSize, EditMode, EOF, Filter, Index, LastModified, LockEdits, Name, " +
            "NoMatch, PercentPosition, RecordCount, Restartable, Sort, Type, Updatable and Fields is read by its own call, and Count of the held Fields, Value, Name of the held Field are read; then MoveFirst, MoveNext, Clone, Requery, GetRows, " +
            "OpenRecordset, FindFirst \"ID = 1\", AddNew, Update, CancelUpdate and Close are called; Errors read after the reads and after the calls. Asks which members of a closed Recordset still answer and which refuse, and with what.",
            p =>
            {
                p.OpenRs("cr", "db", "T", Dao.OpenDynaset).GetBind("cfs", "cr", "Fields").Item("cf", "cfs", A.Str("N")).Call("cr", "Close");
                foreach (string member in new[]
                {
                    "AbsolutePosition", "BOF", "Bookmark", "Bookmarkable", "CacheSize", "EditMode", "EOF", "Filter", "Index", "LastModified", "LockEdits", "Name", "NoMatch",
                    "PercentPosition", "RecordCount", "Restartable", "Sort", "Type", "Updatable", "Fields",
                })
                {
                    p.Get("cr", member);
                }
                p.Get("cfs", "Count").Get("cf", "Value").Get("cf", "Name");
                Errs(p);
                p.Call("cr", "MoveFirst").Call("cr", "MoveNext").Call("cr", "Clone").Call("cr", "Requery").Call("cr", "GetRows", A.I4(1)).Call("cr", "OpenRecordset").Call("cr", "FindFirst", A.Str("ID = 1"))
                    .Call("cr", "AddNew").Call("cr", "Update").Call("cr", "CancelUpdate").Call("cr", "Close");
                Errs(p).Release("cf").Release("cfs");
            });
    }

    // R-ERR-1: classes the earlier cards left out ------------------------------------------------------------------

    private static IEnumerable<Probe> Faults()
    {
        // The same fault (a column that does not exist) reaching DAO through different members.
        const string BadSql = "SELECT NoSuchCol FROM T";
        yield return Fault("err-badcol-openrecordset", Refs(RErr, "ERR-BASE"),
            "Database.OpenRecordset of \"SELECT NoSuchCol FROM T\" (snapshot). The same missing-column fault is delivered by the next five probes through other members; compare Number, Description and Source across them.",
            p => p.Call("db", "OpenRecordset", A.Str(BadSql), A.I4(Dao.OpenSnapshot)));
        yield return Fault("err-badcol-createquerydef", Refs(RErr, "ERR-BASE"),
            "Database.CreateQueryDef with an empty name and the SQL \"SELECT NoSuchCol FROM T\" (the same missing column, delivered when a query is defined).",
            p => p.Call("db", "CreateQueryDef", A.Str(""), A.Str(BadSql)));
        yield return Fault("err-badcol-querydef-sql", Refs(RErr, "ERR-BASE"),
            "A temporary QueryDef over \"SELECT ID FROM T\"; its SQL is assigned \"SELECT NoSuchCol FROM T\" and QueryDef.OpenRecordset (snapshot) is called (the same missing column, delivered through a changed definition).",
            p => p.Put("qd", "SQL", A.Str(BadSql)).Call("qd", "OpenRecordset", A.I4(Dao.OpenSnapshot)),
            setup: p => p.CallBind("qd", "db", "CreateQueryDef", A.Str(""), A.Str("SELECT ID FROM T")),
            cleanup: p => p.Call("qd", "Close").Release("qd"));
        yield return Fault("err-badcol-sort", Refs(RErr, "ERR-BASE", "CUR-LIFE"),
            "On a dynaset over T: Recordset.Sort assigned \"NoSuchCol\" and Recordset.OpenRecordset (the same missing column, delivered when the derived recordset is built).",
            p => p.Put("rs", "Sort", A.Str("NoSuchCol")).CallBind("rd", "rs", "OpenRecordset"),
            setup: p => p.OpenRs("rs", "db", "T", Dao.OpenDynaset),
            cleanup: p => p.Call("rs", "Close").Release("rs"));
        yield return Fault("err-badcol-filter", Refs(RErr, "ERR-BASE", "CUR-LIFE"),
            "On a dynaset over T: Recordset.Filter assigned \"NoSuchCol = 1\" and Recordset.OpenRecordset (the same missing column, delivered through a filter).",
            p => p.Put("rs", "Filter", A.Str("NoSuchCol = 1")).CallBind("rd", "rs", "OpenRecordset"),
            setup: p => p.OpenRs("rs", "db", "T", Dao.OpenDynaset),
            cleanup: p => p.Call("rs", "Close").Release("rs"));
        yield return Fault("err-badcol-findfirst", Refs(RErr, "ERR-BASE", "CUR-POS"),
            "On a dynaset over T: Recordset.FindFirst \"NoSuchCol = 1\" (the same missing column, delivered through a Find criterion).",
            p => p.Call("rs", "FindFirst", A.Str("NoSuchCol = 1")),
            setup: p => p.OpenRs("rs", "db", "T", Dao.OpenDynaset),
            cleanup: p => p.Call("rs", "Close").Release("rs"));

        // Duplicate names reaching DAO through different Append members.
        yield return Fault("err-dup-tabledef-append", Refs(RErr, "ERR-BASE", "DAO-COLL"),
            "A new TableDef named T (an existing table) with one Long field X, appended with TableDefs.Append.",
            p => p.CallBind("ntd", "db", "CreateTableDef", A.Str("T")).CallBind("nf", "ntd", "CreateField", A.Str("X"), A.I4(4)).GetBind("nfs", "ntd", "Fields").Call("nfs", "Append", A.Obj("nf"))
                .GetBind("ntds", "db", "TableDefs").Call("ntds", "Append", A.Obj("ntd")).Release("ntds").Release("nfs").Release("nf").Release("ntd"));
        yield return Fault("err-dup-index-append", Refs(RErr, "ERR-BASE", "DAO-COLL"),
            "A new Index named IX_TN (an existing index of T) over field N, appended with TableDef.Indexes.Append.",
            p => p.GetBind("tds", "db", "TableDefs").Item("tt", "tds", A.Str("T")).CallBind("ix", "tt", "CreateIndex", A.Str("IX_TN")).CallBind("ixf", "ix", "CreateField", A.Str("N"))
                .GetBind("ixfs", "ix", "Fields").Call("ixfs", "Append", A.Obj("ixf")).GetBind("ixs", "tt", "Indexes").Call("ixs", "Append", A.Obj("ix"))
                .Release("ixs").Release("ixfs").Release("ixf").Release("ix").Release("tt").Release("tds"));
        yield return Fault("err-dup-field-append", Refs(RErr, "ERR-BASE", "DAO-COLL"),
            "A new Long Field named N (an existing field of T) appended with TableDef.Fields.Append.",
            p => p.GetBind("tds", "db", "TableDefs").Item("tt", "tds", A.Str("T")).CallBind("nf", "tt", "CreateField", A.Str("N"), A.I4(4)).GetBind("tfs", "tt", "Fields")
                .Call("tfs", "Append", A.Obj("nf")).Release("tfs").Release("nf").Release("tt").Release("tds"));
        yield return Fault("err-dup-property-append", Refs(RErr, "ERR-BASE", "DAO-PROP"),
            "TableDef T has a user property UP1 (appended in the setup); a second Property UP1 (Text 'b') is created and appended with TableDef.Properties.Append.",
            p => p.GetBind("tds", "db", "TableDefs").Item("tt", "tds", A.Str("T")).CallBind("np", "tt", "CreateProperty", A.Str("UP1"), A.I4(10), A.Str("b")).GetBind("tps", "tt", "Properties")
                .Call("tps", "Append", A.Obj("np")).Release("tps").Release("np").Release("tt").Release("tds"),
            setup: p => p.GetBind("sds", "db", "TableDefs").Item("st", "sds", A.Str("T")).CallBind("sp", "st", "CreateProperty", A.Str("UP1"), A.I4(10), A.Str("a")).GetBind("sps", "st", "Properties")
                .Call("sps", "Append", A.Obj("sp")).Release("sps").Release("sp").Release("st").Release("sds"));

        // DDL and query classes left out of RErr1B.
        yield return Fault("err-index-over-duplicates", Refs(RErr, "ERR-BASE", "ERR-PARTIAL"),
            "Database.Execute of \"CREATE UNIQUE INDEX UX_TN_dup ON T (N)\" (N holds the value 40 twice) and \"ALTER TABLE H ADD CONSTRAINT PK_H PRIMARY KEY (A)\" (A holds the value 1 twice).",
            p => p.Call("db", "Execute", A.Str("CREATE UNIQUE INDEX UX_TN_dup ON T (N)")).Call("db", "Execute", A.Str("ALTER TABLE H ADD CONSTRAINT PK_H PRIMARY KEY (A)")));
        yield return Fault("err-ambiguous-column", Refs(RErr, "ERR-BASE"),
            "Database.OpenRecordset of \"SELECT ID FROM PA INNER JOIN CH ON PA.ID = CH.PID\" (ID exists in both tables).",
            p => p.Call("db", "OpenRecordset", A.Str("SELECT ID FROM PA INNER JOIN CH ON PA.ID = CH.PID"), A.I4(Dao.OpenSnapshot)));
        yield return Fault("err-union-column-count", Refs(RErr, "ERR-BASE"),
            "Database.OpenRecordset of \"SELECT ID, N FROM T UNION SELECT ID FROM T\" (different column counts).",
            p => p.Call("db", "OpenRecordset", A.Str("SELECT ID, N FROM T UNION SELECT ID FROM T"), A.I4(Dao.OpenSnapshot)));
        yield return Fault("err-drop-missing-table", Refs(RErr, "ERR-BASE"),
            "Database.Execute of \"DROP TABLE NoSuchTable\".",
            p => p.Call("db", "Execute", A.Str("DROP TABLE NoSuchTable")));
        yield return Fault("err-drop-missing-index-column", Refs(RErr, "ERR-BASE"),
            "Database.Execute of \"DROP INDEX NoSuchIx ON T\" and of \"ALTER TABLE T DROP COLUMN NoSuchCol\".",
            p => p.Call("db", "Execute", A.Str("DROP INDEX NoSuchIx ON T")).Call("db", "Execute", A.Str("ALTER TABLE T DROP COLUMN NoSuchCol")));

        // Held objects after deletion.
        yield return Fault("err-held-querydef-after-delete", Refs(RErr, "ERR-BASE", "ERR-CLOSE", "DAO-LIFE"),
            "A QueryDef QSel held before QueryDefs.Delete(\"QSel\") (in the setup); the step reads its Name and SQL and calls OpenRecordset (snapshot) on it.",
            p => p.Get("qh", "Name").Get("qh", "SQL").Call("qh", "OpenRecordset", A.I4(Dao.OpenSnapshot)),
            setup: p => p.GetBind("hds", "db", "QueryDefs").Item("qh", "hds", A.Str("QSel")).Call("hds", "Delete", A.Str("QSel")).Release("hds"),
            cleanup: p => p.Release("qh"));

        // Errors collection operations.
        yield return On("main", "errors-refresh-and-members", Refs(RErr, "ERR-BASE", "ERR-DAO-ORDER", "ERR-SNAPSHOT"),
            "Database.Execute of \"SELEC ID FROM T\" fails; then Errors.Count; Errors.Refresh and Count; the first Error (Item 0) held and its Number, Description, Source, HelpFile and HelpContext read; a successful snapshot " +
            "OpenRecordset, then Count and the held Error's members again; Errors.Refresh and Count; a second failure of the same Execute, Count and Item 0 again. Asks the effect of Errors.Refresh, whether a held Error object keeps its " +
            "members across later calls, and the Count after repeated failures.",
            p =>
            {
                p.Call("db", "Execute", A.Str("SELEC ID FROM T")).GetBind("ers", "eng", "Errors").Get("ers", "Count").Call("ers", "Refresh").Get("ers", "Count");
                p.Item("e0", "ers", A.I4(0));
                ErrorMembers(p, "e0");
                p.OpenRs("sr", "db", "SELECT ID FROM T", Dao.OpenSnapshot).Get("ers", "Count");
                ErrorMembers(p, "e0");
                p.Call("ers", "Refresh").Get("ers", "Count");
                ErrorMembers(p, "e0");
                p.Call("db", "Execute", A.Str("SELEC ID FROM T")).Get("ers", "Count").Item("e1", "ers", A.I4(0));
                ErrorMembers(p, "e1");
                ErrorMembers(p, "e0");
                p.Release("e1").Release("e0").Release("ers").Call("sr", "Close");
            }, write: true);

        // One row violating several constraints at once.
        yield return Fault("err-multi-violation-sql", Refs(RErr, "ERR-BASE", "ERR-DAO-ORDER", "ERR-PARTIAL"),
            "Database.Execute of \"INSERT INTO R (ID, Cd, Rq, Vv) VALUES (1, 'r1', Null, -5)\": one row that duplicates the primary key and the unique Cd, omits the required Rq and breaks the validation rule of Vv. One question: " +
            "how many Errors entries and which Number, Description and Source does the one failing call leave.",
            p => p.Call("db", "Execute", A.Str("INSERT INTO R (ID, Cd, Rq, Vv) VALUES (1, 'r1', Null, -5)")));
        yield return Fault("err-multi-violation-recordset", Refs(RErr, "ERR-BASE", "ERR-DAO-ORDER", "ERR-PARTIAL"),
            "On a dynaset over R: AddNew; assign ID 1, Cd 'r1', Rq Null and Vv -5 (the same four violations); Update; CancelUpdate. Asks which call reports which violation and how many Errors entries are left.",
            p => p.Call("rs", "AddNew").Put("rf_ID", "Value", A.I4(1)).Put("rf_Cd", "Value", A.Str("r1")).Put("rf_Rq", "Value", A.Null).Put("rf_Vv", "Value", A.I4(-5)).Call("rs", "Update").Call("rs", "CancelUpdate"),
            setup: p =>
            {
                p.OpenRs("rs", "db", "R", Dao.OpenDynaset);
                Hold(p, "rs", "rf_", "ID", "Cd", "Rq", "Vv");
            },
            cleanup: p =>
            {
                Unhold(p, "rf_", "ID", "Cd", "Rq", "Vv");
                p.Call("rs", "Close").Release("rs");
            });
    }

    private static P ErrorMembers(P p, string error) =>
        p.Get(error, "Number").Get(error, "Description").Get(error, "Source").Get(error, "HelpFile").Get(error, "HelpContext");

    /// <summary>
    /// One failure class: the setup (recorded), Errors read, the step, Errors read, a successful snapshot OpenRecordset,
    /// Errors read, the same step again, Errors read, then the cleanup and the Database close.
    /// </summary>
    private static Probe Fault(string name, string[] refs, string step, Action<P> body, Action<P>? setup = null, Action<P>? cleanup = null) =>
        On("main", name, refs,
            $"{step} Sequence on a fresh copy of the main fixture opened writable as db: the setup calls (recorded); Errors read (Count, then Number, Description, Source, HelpContext and HelpFile of each member in order); the step; " +
            "Errors read; a snapshot OpenRecordset of 'SELECT ID, N FROM T ORDER BY ID'; Errors read; the same step again; Errors read; the snapshot and the objects of the setup closed. Each call's HRESULT, EXCEPINFO " +
            "and IErrorInfo are in its own call record.",
            p =>
            {
                setup?.Invoke(p);
                Errs(p);
                body(p);
                Errs(p);
                p.OpenRs("sr", "db", "SELECT ID, N FROM T ORDER BY ID", Dao.OpenSnapshot);
                Errs(p);
                body(p);
                Errs(p);
                p.Call("sr", "Close");
                cleanup?.Invoke(p);
            }, write: true);

    // Helpers ---------------------------------------------------------------------------------------------------------

    private static string[] Refs(string card, params string[] branches) => [card, .. branches];

    private static Probe Creation(string name, string ext, int version, string what, Action<P> body) =>
        new(Card, "create-" + name, [RCur, RDao, RErr, "D-CURSOR-1", "CUR-POS", "CUR-PROV", "CUR-BULK", "CUR-EDIT", "DAO-PROP", "ERR-BASE"],
            $"Deferred-branch fixture {Card}-{name}: {what}. Created in {ext.ToUpperInvariant()} by DBEngine.CreateDatabase(path, dbLangGeneral, dbVersion {version}) and Database.Execute DDL and DML; the closed file is kept.",
            p =>
            {
                p.EngineLite().CallBind("db", "eng", "CreateDatabase", A.Out($"{Card}-{name}.{ext}"), A.Str(Dao.LangGeneral), A.I4(version));
                body(p);
                p.FinishLite("db");
            })
        { CreatesFixture = true };

    /// <summary>A probe on a fresh copy of a fixture: light opening, the body, light closing.</summary>
    private static Probe On(string fixture, string name, string[] refs, string what, Action<P> body, bool write = false) =>
        new(Card, name, refs,
            what + (write ? " The fixture copy is opened read-write." : " The fixture copy is opened read-only."),
            p =>
            {
                p.EngineLite().OpenDb(readOnly: !write).Files("after-open");
                body(p);
                p.FinishLite("db");
            })
        { Fixtures = [("main", $"{Card}-{fixture}")] };

    private static P Sql(P p, string sql) => p.Call("db", "Execute", A.Str(sql));

    /// <summary>Read DBEngine.Errors: Count, then each member in order.</summary>
    private static P Errs(P p) =>
        p.GetBind("errs", "eng", "Errors")
            .Each("errs", "e", 16, b => b.Get("e", "Number").Get("e", "Description").Get("e", "Source").Get("e", "HelpContext").Get("e", "HelpFile"))
            .Release("errs");

    /// <summary>Read one field of the current row through a fresh Fields collection.</summary>
    private static P Val(P p, string rs, string field) =>
        p.GetBind("sf", rs, "Fields").Item("sv", "sf", A.Str(field)).Get("sv", "Value").Release("sv").Release("sf");

    private static P Close(P p, string rs) => p.Call(rs, "Close").Release(rs);

    /// <summary>BOF, EOF, PercentPosition, optional AbsolutePosition and the ID of the current row of <c>rs</c>.</summary>
    private static P PosState(P p, bool abs)
    {
        p.Get("rs", "BOF").Get("rs", "EOF").Get("rs", "PercentPosition");
        if (abs)
        {
            p.Get("rs", "AbsolutePosition");
        }
        return Val(p, "rs", "ID");
    }

    /// <summary>Bind <c>prefix + "s"</c> to the Fields collection and <c>prefix + name</c> to each named Field.</summary>
    private static P Hold(P p, string rs, string prefix, params string[] fields)
    {
        p.GetBind(prefix + "s", rs, "Fields");
        foreach (string f in fields)
        {
            p.Item(prefix + f, prefix + "s", A.Str(f));
        }
        return p;
    }

    private static P Unhold(P p, string prefix, params string[] fields)
    {
        foreach (string f in fields)
        {
            p.Release(prefix + f);
        }
        return p.Release(prefix + "s");
    }

    /// <summary>For each Field of the recordset, each named member by its own call.</summary>
    private static P FieldMeta(P p, string rs, string[] members) =>
        p.GetBind("mfs", rs, "Fields").Each("mfs", "mf", 24, b =>
        {
            foreach (string member in members)
            {
                b.Get("mf", member);
            }
        }).Release("mfs");
}
