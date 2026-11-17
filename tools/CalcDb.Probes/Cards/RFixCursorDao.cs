namespace CalcDb.Probes.Cards;

/// <summary>
/// Repair probes for R-CURSOR-1, R-DAO-1 and R-ERR-1: each asks the same question as an earlier probe of evidence/r-cursor-1,
/// r-cursor-1b, r-dao-1-err-1, r-dao-1b, r-err-1b or r-def-cursor-dao whose record is void or confounded by a defect in OUR
/// probe (docs/work/evidence-summaries.md, "Probe or fixture defects"), designed so that the defect cannot recur: every fault
/// on its own fresh object or database, DBEngine.Errors read right after each failing call, collections refreshed before they
/// are read after DDL, state read back after each step, dbFailOnError (128) where a refusal must be raised, a destination
/// column list for every INSERT, writable opens where updatability is read, and a start position that no landing can equal.
/// Three small fixtures are created (shuffled storage order, an unindexed text column, an ADO-made Decimal column); every
/// other probe opens a fresh copy of a frozen fixture. Probes ask; they hold no expected answers. Arguments are constants;
/// every file is named through A.In or A.Out; no call here can open a dialog (no ODBC, no pass-through, no Empty or Missing
/// database name). Scratch files do not carry the card prefix.
/// </summary>
public sealed class RFixCursorDao : ICard
{
    private const string Card = "r-fix-cursor-dao";
    private const string Cur = "R-CURSOR-1", DaoCard = "R-DAO-1", ErrCard = "R-ERR-1";
    private const int UseJet = 2, DenyWrite = 1, DenyRead = 2, Inconsistent = 16, Consistent = 32;
    private const int Wait = 30000;
    private const int TypeText = 10, TypeLong = 4;
    private const string AceOleDb = "Microsoft.ACE.OLEDB.12.0";

    // Frozen fixtures of the original cards, opened as fresh copies.
    private const string FxNav = "r-cursor-1-nav", FxEdit = "r-cursor-1-edit", FxMain1B = "r-cursor-1b-main";
    private const string FxDao1 = "r-dao-1-err-1-accdb-base", FxRich = "r-dao-1b-rich", FxErrBase = "r-err-1b-accdb-base";
    private const string FxDef = "r-def-cursor-dao-main";

    // Fixtures of this card.
    private const string FxPages = Card + "-pages", FxFind = Card + "-find", FxDec = Card + "-dec";

    private static readonly string X200 = new('x', 200);

    private static readonly (string Name, int Type)[] Types =
        [("table", Dao.OpenTable), ("dynaset", Dao.OpenDynaset), ("snapshot", Dao.OpenSnapshot), ("forwardonly", Dao.OpenForwardOnly)];

    private static readonly string[] MetaMembers = ["Name", "Type", "Size", "Attributes", "DataUpdatable", "SourceTable", "SourceField", "Required", "OrdinalPosition"];

    private static readonly string[] WideMetaMembers =
    [
        .. MetaMembers, "CollatingOrder", "AllowZeroLength", "ValidationRule", "ValidationText", "DefaultValue", "CollectionIndex",
        "ForeignName", "Expression", "ValidateOnSet",
    ];

    public IEnumerable<Probe> Probes() =>
        [.. Fixtures(), .. Cursor1(), .. Cursor1B(), .. Dao1(), .. Dao1B(), .. Err1B(), .. DefCursorDao()];

    // Fixtures ----------------------------------------------------------------------------------------------

    private static IEnumerable<Probe> Fixtures()
    {
        yield return Create("pages", [Cur, "CUR-EMPTY-PAGES", "CUR-POS"],
            "M2(ID LONG, V TEXT(255)) with no primary key, a nonunique index IX_M2 (ID) and 512 rows with a 200-character V inserted in descending ID order " +
            "(512 first, by INSERT ... SELECT doubling with ID - k), then IDs 1-100, 200-320 and 450-512 deleted, so that storage order and key order are opposite; " +
            "a table-type recordset without Index reads RecordCount and the first and last ID in storage order",
            p =>
            {
                Sql(p, "CREATE TABLE M2 (ID LONG, V TEXT(255))");
                Sql(p, "CREATE INDEX IX_M2 ON M2 (ID)");
                Sql(p, $"INSERT INTO M2 (ID, V) VALUES (512, '{X200}')");
                for (int k = 1; k < 512; k *= 2)
                {
                    Sql(p, $"INSERT INTO M2 (ID, V) SELECT ID - {k}, V FROM M2");
                }
                Sql(p, "DELETE FROM M2 WHERE ID <= 100");
                Sql(p, "DELETE FROM M2 WHERE ID BETWEEN 200 AND 320");
                Sql(p, "DELETE FROM M2 WHERE ID >= 450");
                p.OpenRs("rs", "db", "M2", Dao.OpenTable).Get("rs", "RecordCount");
                State(p, "rs", false).Call("rs", "MoveLast");
                State(p, "rs", false);
                Close(p, "rs");
            });

        string[] findRows =
        [
            "1, 10, 'alpha', 'x', 'p1'",
            "2, 20, 'beta', Null, 'p2'",
            "3, 30, 'O''Brien', 'x', 'p3'",
            "4, 40, 'a\"b', Null, 'p4'",
            "5, 40, 'abc', 'y', 'p5'",
            "6, 50, 'ABC', Null, 'p6'",
            "7, 60, 'abd', 'x', 'p7'",
            "8, 70, Null, 'y', 'p8'",
        ];
        yield return Create("find", [Cur, "CUR-POS"],
            "FU(ID LONG primary key PK_FU, N LONG, S TEXT(20), Q TEXT(20), P TEXT(20)) and FI with the same columns and rows plus a nonunique index IX_FI_S (S); eight rows each " +
            "(N 10,20,30,40,40,50,60,70; S alpha, beta, O'Brien, a\"b, abc, ABC, abd, Null; Q x, Null, x, Null, y, Null, x, y; P the plain values p1-p8); " +
            "FU has no index except its key",
            p =>
            {
                Sql(p, "CREATE TABLE FU (ID LONG CONSTRAINT PK_FU PRIMARY KEY, N LONG, S TEXT(20), Q TEXT(20), P TEXT(20))");
                Sql(p, "CREATE TABLE FI (ID LONG CONSTRAINT PK_FI PRIMARY KEY, N LONG, S TEXT(20), Q TEXT(20), P TEXT(20))");
                Sql(p, "CREATE INDEX IX_FI_S ON FI (S)");
                foreach (string row in findRows)
                {
                    Sql(p, "INSERT INTO FU (ID, N, S, Q, P) VALUES (" + row + ")");
                    Sql(p, "INSERT INTO FI (ID, N, S, Q, P) VALUES (" + row + ")");
                }
            });

        // The Decimal column is made through ADO DDL with explicit precision and scale (a DAO CreateField(name, 20) Decimal has Precision 38 and
        // DAO cannot read it, evidence-log R-ORACLE-1), and the values are SQL literals (BSTR conversion depends on the locale).
        yield return new Probe(Card, "create-dec", [Cur, "CUR-BULK"],
            $"Repair fixture {Card}-dec: WD(ID INTEGER primary key PK_WD, Dc DECIMAL(18,4)) created by ADO (Provider {AceOleDb}) DDL inside a database made by DBEngine.CreateDatabase(path, dbLangGeneral, dbVersion120), " +
            "four rows written by DAO Database.Execute with SQL literals (1, 1.5), (2, no Dc), (3, -2.25), (4, 0.0001) and dbFailOnError; Dc Type, Size and Attributes and the rows read through DAO. The closed file is kept.",
            p =>
            {
                const string file = Card + "-dec.accdb";
                p.EngineLite().CallBind("db", "eng", "CreateDatabase", A.Out(file), A.Str(Dao.LangGeneral), A.I4(Dao.Version120)).Call("db", "Close").Release("db");
                p.Create("ADODB.Connection", "cn").Put("cn", "Provider", A.Str(AceOleDb))
                    .GetBind("cnp", "cn", "Properties").Item("cds", "cnp", A.Str("Data Source")).Put("cds", "Value", A.Out(file))
                    .Release("cds").Release("cnp").Call("cn", "Open")
                    .Call("cn", "Execute", A.Str("CREATE TABLE WD (ID INTEGER CONSTRAINT PK_WD PRIMARY KEY, Dc DECIMAL(18,4))"))
                    .Call("cn", "Close").Release("cn");
                p.CallBind("db", "eng", "OpenDatabase", A.Out(file), A.Bool(false), A.Bool(false));
                Exec(p, "INSERT INTO WD (ID, Dc) VALUES (1, 1.5)");
                Exec(p, "INSERT INTO WD (ID) VALUES (2)");
                Exec(p, "INSERT INTO WD (ID, Dc) VALUES (3, -2.25)");
                Exec(p, "INSERT INTO WD (ID, Dc) VALUES (4, 0.0001)");
                p.GetBind("tds", "db", "TableDefs").Call("tds", "Refresh").Item("td", "tds", A.Str("WD")).GetBind("tfl", "td", "Fields").Item("df", "tfl", A.Str("Dc"))
                    .Get("df", "Type").Get("df", "Size").Get("df", "Attributes").Release("df").Release("tfl").Release("td").Release("tds");
                p.OpenRs("rs", "db", "SELECT ID, Dc FROM WD ORDER BY ID", Dao.OpenSnapshot).ReadAll("rs", 8, 4).Call("rs", "Close").Release("rs");
                p.FinishLite("db");
            })
        { CreatesFixture = true };
    }

    // R-CURSOR-1 (evidence/r-cursor-1) ------------------------------------------------------------------------

    private static IEnumerable<Probe> Cursor1()
    {
        (string Label, Arg Value)[] bookmarks =
        [
            ("empty byte array", A.Bytes()), ("four zero bytes", A.Bytes(0, 0, 0, 0)), ("empty string", A.Str("")),
            ("Long 1", A.I4(1)), ("Null", A.Null), ("Empty", A.Empty),
        ];
        yield return Fix(FxNav, "id-bookmark-assign-fresh", [Cur, "D-CURSOR-1", "CUR-ID", "CUR-POS"], "r-cursor-1/id-bookmark-shape",
            "the six Bookmark assignments ran one after another on one dynaset, so the failed four-zero-byte assignment left no current row and confounded the effect of the later ones, and the first (empty array) could not be told from a no-op",
            $"For each constant [{string.Join(", ", bookmarks.Select(b => b.Label))}] a NEW dynaset on T is opened and moved to ID 2 (MoveNext); read BOF, EOF, AbsolutePosition, the ID and Bookmark; assign Bookmark the constant; read Errors; " +
            "read BOF, EOF, AbsolutePosition, the ID and Bookmark again; close. As a control for a no-op, a new dynaset first moves 2 rows (Move 2) and reads Bookmark, then MoveFirst, reads the state, " +
            "and has Bookmark assigned the four bytes 02 02 00 00 (the dynaset Bookmark that r-cursor-1b read for the third row, used here as an input only), then the same Errors and state reads. " +
            "Asks, for each constant on its own object, whether the assignment changes the position or leaves no current row, and how it is refused.",
            p =>
            {
                foreach ((_, Arg bad) in bookmarks)
                {
                    p.OpenRs("rs", "db", "T", Dao.OpenDynaset).Call("rs", "MoveNext");
                    State(p, "rs", true).Get("rs", "Bookmark");
                    p.Put("rs", "Bookmark", bad).Errors();
                    State(p, "rs", true).Get("rs", "Bookmark");
                    Close(p, "rs");
                }
                p.OpenRs("rs", "db", "T", Dao.OpenDynaset).Call("rs", "Move", A.I4(2)).Get("rs", "Bookmark").Call("rs", "MoveFirst");
                State(p, "rs", true).Get("rs", "Bookmark");
                p.Put("rs", "Bookmark", A.Bytes(2, 2, 0, 0)).Errors();
                State(p, "rs", true).Get("rs", "Bookmark");
                Close(p, "rs");
            }, write: false);

        (string Label, Arg[] Args)[] shapes =
        [
            ("= 1, 20", [A.Str("="), A.I4(1), A.I4(20)]),
            ("= 1", [A.Str("="), A.I4(1)]),
            ("= (no key)", [A.Str("=")]),
            ("= Missing, 20", [A.Str("="), A.Missing, A.I4(20)]),
            ("= 1, Null", [A.Str("="), A.I4(1), A.Null]),
            ("= Null, Null", [A.Str("="), A.Null, A.Null]),
            ("= Empty, Empty", [A.Str("="), A.Empty, A.Empty]),
            ("= \"1\", \"20\"", [A.Str("="), A.Str("1"), A.Str("20")]),
            ("= \"abc\", 20", [A.Str("="), A.Str("abc"), A.I4(20)]),
            ("= 1.5, 20", [A.Str("="), A.R8(1.5), A.I4(20)]),
            ("= 1, 20, 5", [A.Str("="), A.I4(1), A.I4(20), A.I4(5)]),
            (">= 1", [A.Str(">="), A.I4(1)]),
            ("> 1", [A.Str(">"), A.I4(1)]),
            ("<= 1", [A.Str("<="), A.I4(1)]),
            ("< 2", [A.Str("<"), A.I4(2)]),
            ("== 1, 20", [A.Str("=="), A.I4(1), A.I4(20)]),
        ];
        (string Start, Action<P> Move)[] starts =
        [
            ("the last row (MoveLast)", p => p.Call("rt", "MoveLast")),
            ("the third row (MoveFirst, Move 2)", p => p.Call("rt", "MoveFirst").Call("rt", "Move", A.I4(2))),
        ];
        yield return Fix(FxNav, "seek-arguments-from-two-starts", [Cur, "D-CURSOR-1", "CUR-SEEK", "CUR-POS"], "r-cursor-1/seek-arguments",
            "a fresh table-type recordset on PK_C already sits at (1,30), so every Seek that landed there could not be told from a Seek that did not move; the recovery step Seek =,1,30 had the same weakness",
            $"Table C holds (1,10),(1,20),(1,30),(2,10); Index PK_C is A ASC, B DESC. For each of two start positions [{string.Join("; ", starts.Select(s => s.Start))}] and each argument list of Seek [{string.Join("; ", shapes.Select(s => s.Label))}]: " +
            "a fresh table-type recordset over C, Index PK_C, moved to the start position; read A and B (the start); Seek with the arguments; read Errors; read NoMatch, BOF, EOF, A and B; MoveNext and read A and B; close. " +
            "Then, from each start, on one fresh recordset: Seek \"=\", 9, 9 and read Errors, NoMatch, BOF, EOF, A and B; Seek \"=\", 1, 30 and read NoMatch, A and B; MovePrevious and read A and B. " +
            "The two starts are different rows, so a landing row that equals one start is told from a Seek that did not move by the other run. Asks the omission, Null, coercion and wrong-type policy of Seek and the recovery after a failed Seek.",
            p =>
            {
                foreach ((_, Action<P> move) in starts)
                {
                    foreach ((_, Arg[] args) in shapes)
                    {
                        p.OpenRs("rt", "db", "C", Dao.OpenTable).Put("rt", "Index", A.Str("PK_C"));
                        move(p);
                        AB(p, "rt").Call("rt", "Seek", args).Errors().Get("rt", "NoMatch").Get("rt", "BOF").Get("rt", "EOF");
                        AB(p, "rt").Call("rt", "MoveNext");
                        AB(p, "rt");
                        Close(p, "rt");
                    }
                    p.OpenRs("rt", "db", "C", Dao.OpenTable).Put("rt", "Index", A.Str("PK_C"));
                    move(p);
                    AB(p, "rt").Call("rt", "Seek", A.Str("="), A.I4(9), A.I4(9)).Errors().Get("rt", "NoMatch").Get("rt", "BOF").Get("rt", "EOF");
                    AB(p, "rt").Call("rt", "Seek", A.Str("="), A.I4(1), A.I4(30)).Get("rt", "NoMatch");
                    AB(p, "rt").Call("rt", "MovePrevious");
                    AB(p, "rt");
                    Close(p, "rt");
                }
            }, write: false);

        yield return Fix(FxNav, "seek-duplicates-null-from-last", [Cur, "D-CURSOR-1", "CUR-SEEK"], "r-cursor-1/seek-duplicates",
            "Seek \"=\", Null on IX_K began on ID 6, which is already the first row of IX_K (Null sorts first), so the landing could not be told from a Seek that did not move",
            "Table D has K values 1,2,2,2,3,Null for IDs 1-6. For IX_K and for IX_KN (WITH IGNORE NULL) in turn: a fresh table-type recordset over D, Index set, MoveLast, read ID and K (the start); " +
            "Seek \"=\", Null; read Errors, NoMatch, BOF, EOF, ID and K; MoveNext and read ID; MovePrevious and read ID; on IX_KN then MoveFirst and walk ID to EOF (at most 16); close. " +
            "Asks where a Null key lands on an index that holds nulls and on one that ignores them, starting from a row that is not the first.",
            p =>
            {
                foreach (string index in new[] { "IX_K", "IX_KN" })
                {
                    p.OpenRs("rt", "db", "D", Dao.OpenTable).Put("rt", "Index", A.Str(index)).Call("rt", "MoveLast");
                    Val(Val(p, "rt", "ID"), "rt", "K").Call("rt", "Seek", A.Str("="), A.Null).Errors().Get("rt", "NoMatch").Get("rt", "BOF").Get("rt", "EOF");
                    Val(Val(p, "rt", "ID"), "rt", "K").Call("rt", "MoveNext");
                    Val(p, "rt", "ID").Call("rt", "MovePrevious");
                    Val(p, "rt", "ID");
                    if (index == "IX_KN")
                    {
                        p.Call("rt", "MoveFirst");
                        Forward(p, "rt", 16, "ID");
                    }
                    Close(p, "rt");
                }
            }, write: false);

        yield return Fix(FxEdit, "edit-conflict-pessimistic-second-workspace", [Cur, "D-CURSOR-1", "CUR-EDIT"], "r-cursor-1/edit-conflict-pessimistic",
            "both Databases came from the default Workspace, so the pessimistic case did not discriminate: B's Edit on A's locked row raised no error and Errors was empty",
            "Cursor B is opened through a second, appended Workspace W2 (CreateWorkspace('W2','admin','',dbUseJet), Workspaces.Append, W2.OpenDatabase of the same file read-write). Cursor A: dynaset on G in the default Workspace with LockEdits True; " +
            "cursor B: dynaset on G in W2. A Edit row 'r1', set U 'pesA'; B Edit row 'r1', read Errors and EditMode; B MoveNext and Edit row 'r2', read Errors and EditMode, CancelUpdate; A Update, read Errors; " +
            "B MovePrevious, Edit, set U 'pesB', Update, read Errors, read U; then a new snapshot through each Database reads every row of G. Asks what a pessimistic edit locks, and when the lock is released, across Workspaces.",
            p =>
            {
                p.GetBind("wss", "eng", "Workspaces").CallBind("w2", "eng", "CreateWorkspace", A.Str("W2"), A.Str("admin"), A.Str(""), A.I4(UseJet))
                    .Call("wss", "Append", A.Obj("w2")).CallBind("db2", "w2", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false));
                p.OpenRs("ra", "db", "G", Dao.OpenDynaset).Put("ra", "LockEdits", A.Bool(true));
                p.OpenRs("rb", "db2", "G", Dao.OpenDynaset);
                Hold(p, "ra", "a_", "U");
                Hold(p, "rb", "b_", "U");
                p.Call("ra", "Edit").Put("a_U", "Value", A.Str("pesA"));
                p.Call("rb", "Edit").Errors().Get("rb", "EditMode");
                p.Call("rb", "MoveNext").Call("rb", "Edit").Errors().Get("rb", "EditMode").Call("rb", "CancelUpdate");
                p.Call("ra", "Update").Errors();
                p.Call("rb", "MovePrevious").Call("rb", "Edit").Put("b_U", "Value", A.Str("pesB")).Call("rb", "Update").Errors().Get("b_U", "Value");
                Unhold(p, "a_", "U");
                Unhold(p, "b_", "U");
                Close(p, "rb");
                Close(p, "ra");
                p.OpenRs("rn", "db", "SELECT ID, T, N, U FROM G ORDER BY ID", Dao.OpenSnapshot).ReadAll("rn", 8, 4).Call("rn", "Close").Release("rn");
                p.OpenRs("rn", "db2", "SELECT ID, T, N, U FROM G ORDER BY ID", Dao.OpenSnapshot).ReadAll("rn", 8, 4).Call("rn", "Close").Release("rn");
                p.Call("db2", "Close").Release("db2").Call("w2", "Close").Release("w2").Release("wss");
            }, write: true);

        yield return Fix(FxEdit, "edit-conflict-optimistic-readback", [Cur, "D-CURSOR-1", "CUR-EDIT", "CUR-POP"], "r-cursor-1/edit-conflict-optimistic",
            "the stored result after A's second Update was never read back; only A's own buffer was read",
            "Open a second Database db2 on the same file. Cursor A: dynaset on G in db with LockEdits False; cursor B: dynaset on G in db2. (a) Same field: A Edit row 'r1', set U 'optA'; B Edit row 'r1', set U 'optB', Update; " +
            "A Update, read Errors, EditMode, U; A Update again, read Errors, U; a new snapshot through db and one through db2 read every row of G (ID, T, N, U); A Move 0 and read U. " +
            "(b) Other field: both MoveNext to row 'r2'; A Edit, set U 'optC'; B Edit, set N 77, Update; A Update, read Errors, EditMode, U, N; A Update again, read Errors; new snapshots through db and db2 read every row of G; A Move 0 and read U and N. " +
            "Asks the optimistic conflict outcome per field overlap, the buffer after a refused Update and after a second explicit Update, and what is stored.",
            p =>
            {
                p.OpenDb("db2", readOnly: false);
                p.OpenRs("ra", "db", "G", Dao.OpenDynaset).Put("ra", "LockEdits", A.Bool(false));
                p.OpenRs("rb", "db2", "G", Dao.OpenDynaset);
                Hold(p, "ra", "a_", "N", "U");
                Hold(p, "rb", "b_", "N", "U");
                p.Call("ra", "Edit").Put("a_U", "Value", A.Str("optA"))
                    .Call("rb", "Edit").Put("b_U", "Value", A.Str("optB")).Call("rb", "Update")
                    .Call("ra", "Update").Errors().Get("ra", "EditMode").Get("a_U", "Value")
                    .Call("ra", "Update").Errors().Get("a_U", "Value");
                StoredG(p);
                p.Call("ra", "Move", A.I4(0)).Get("a_U", "Value");
                p.Call("ra", "MoveNext").Call("rb", "MoveNext")
                    .Call("ra", "Edit").Put("a_U", "Value", A.Str("optC"))
                    .Call("rb", "Edit").Put("b_N", "Value", A.I4(77)).Call("rb", "Update")
                    .Call("ra", "Update").Errors().Get("ra", "EditMode").Get("a_U", "Value").Get("a_N", "Value")
                    .Call("ra", "Update").Errors();
                StoredG(p);
                p.Call("ra", "Move", A.I4(0)).Get("a_U", "Value").Get("a_N", "Value");
                Unhold(p, "a_", "N", "U");
                Unhold(p, "b_", "N", "U");
                Close(p, "ra");
                Close(p, "rb");
                p.Call("db2", "Close").Release("db2");
            }, write: true);

        yield return Fix(FxEdit, "edit-validation-errors-per-failure", [Cur, "D-CURSOR-1", "CUR-EDIT"], "r-cursor-1/edit-validation-order",
            "after the last, successful Update, DBEngine.Errors still reported the previous failure, so that count readout answered nothing for that call",
            "G has T required, U with rule <>'bad' and a unique index, and table rule [N] < 1000. Sequence 1 on a dynaset with held Fields: AddNew, set U 'bad', read U and EditMode; Update (T unset and U bad), Errors, EditMode; set T 't'; " +
            "Update, Errors; set U 'u1' (duplicate); Update, Errors; set U 'u9' and N 2000; Update, Errors; set N 3; Update (no Errors read after it), read ID, EditMode; a new snapshot reads every row of G. " +
            "Sequences 2 and 3 on fresh dynasets: AddNew, then U 'bad' and T Null, or T Null and U 'bad'; Update, Errors, CancelUpdate. Sequence 4: Field U ValidateOnSet True, Edit, set U 'bad', Errors, EditMode, U, CancelUpdate. " +
            "Errors is read right after each call that can fail and not after a call that succeeds. Asks the check order, which check runs at assignment and which at Update.",
            p =>
            {
                string[] g = ["ID", "T", "N", "U"];
                p.OpenRs("rs", "db", "G", Dao.OpenDynaset);
                Hold(p, "rs", "g_", g);
                p.Call("rs", "AddNew").Put("g_U", "Value", A.Str("bad")).Get("g_U", "Value").Get("rs", "EditMode")
                    .Call("rs", "Update").Errors().Get("rs", "EditMode")
                    .Put("g_T", "Value", A.Str("t")).Call("rs", "Update").Errors()
                    .Put("g_U", "Value", A.Str("u1")).Call("rs", "Update").Errors()
                    .Put("g_U", "Value", A.Str("u9")).Put("g_N", "Value", A.I4(2000)).Call("rs", "Update").Errors()
                    .Put("g_N", "Value", A.I4(3)).Call("rs", "Update").Get("g_ID", "Value").Get("rs", "EditMode");
                Unhold(p, "g_", g);
                Close(p, "rs");
                p.OpenRs("rn", "db", "SELECT ID, T, N, U FROM G ORDER BY ID", Dao.OpenSnapshot).ReadAll("rn", 8, 4).Call("rn", "Close").Release("rn");
                foreach (bool uFirst in new[] { true, false })
                {
                    p.OpenRs("rs", "db", "G", Dao.OpenDynaset);
                    Hold(p, "rs", "g_", "T", "U").Call("rs", "AddNew");
                    if (uFirst)
                    {
                        p.Put("g_U", "Value", A.Str("bad")).Put("g_T", "Value", A.Null);
                    }
                    else
                    {
                        p.Put("g_T", "Value", A.Null).Put("g_U", "Value", A.Str("bad"));
                    }
                    p.Call("rs", "Update").Errors().Call("rs", "CancelUpdate");
                    Unhold(p, "g_", "T", "U");
                    Close(p, "rs");
                }
                p.OpenRs("rs", "db", "G", Dao.OpenDynaset);
                Hold(p, "rs", "g_", "U").Put("g_U", "ValidateOnSet", A.Bool(true)).Get("g_U", "ValidateOnSet")
                    .Call("rs", "Edit").Put("g_U", "Value", A.Str("bad")).Errors().Get("rs", "EditMode").Get("g_U", "Value").Call("rs", "CancelUpdate");
                Unhold(p, "g_", "U");
                Close(p, "rs");
            }, write: true);

        (string Label, int Type, string Source, string? Index)[] walks =
        [
            ("table-type, no Index (storage order)", Dao.OpenTable, "M2", null),
            ("table-type, Index IX_M2 (key order)", Dao.OpenTable, "M2", "IX_M2"),
            ("dynaset on the table name", Dao.OpenDynaset, "M2", null),
            ("dynaset on SELECT * FROM M2 ORDER BY ID", Dao.OpenDynaset, "SELECT * FROM M2 ORDER BY ID", null),
        ];
        yield return Fix(FxPages, "pages-traverse-storage-order", [Cur, "D-CURSOR-1", "CUR-EMPTY-PAGES", "CUR-POS", "T-ISAM-43"], "r-cursor-1/pages-traverse-table and pages-traverse-dynaset",
            "storage order equalled ID order, so the table-type and the dynaset walks could not be told apart (page emptiness itself is not observable here)",
            "Table M2 held IDs 512 down to 1 inserted in descending order with a nonunique index IX_M2 (ID), and then lost IDs 1-100, 200-320 and 450-512. " +
            $"For each of [{string.Join("; ", walks.Select(w => w.Label))}]: open it (Index set where named), read RecordCount, read ID through a held Field at each row by MoveNext until EOF (at most 600), read BOF and EOF, then MoveLast and read ID by MovePrevious until BOF (at most 600), " +
            "then BOF, EOF and RecordCount. Asks the visited row sequence and the boundary reports in both directions across empty pages when storage order and key order differ, per recordset type.",
            p =>
            {
                foreach ((_, int type, string source, string? index) in walks)
                {
                    p.OpenRs("rs", "db", source, type);
                    if (index is not null)
                    {
                        p.Put("rs", "Index", A.Str(index));
                    }
                    p.Get("rs", "RecordCount");
                    Forward(p, "rs", 600, "ID");
                    p.Get("rs", "BOF").Get("rs", "EOF").Call("rs", "MoveLast");
                    Backward(p, "rs", 600, "ID");
                    p.Get("rs", "BOF").Get("rs", "EOF").Get("rs", "RecordCount");
                    Close(p, "rs");
                }
            }, write: false);
    }

    // R-CURSOR-1 (evidence/r-cursor-1b) -----------------------------------------------------------------------

    private static IEnumerable<Probe> Cursor1B()
    {
        int?[] flags =
        [
            null, 0, DenyWrite, DenyRead, Dao.ReadOnly, Dao.AppendOnly, Inconsistent, Consistent, Dao.FailOnError, Dao.ForwardOnlyOpt, Dao.SeeChanges,
            DenyWrite | DenyRead, Dao.ReadOnly | Dao.AppendOnly, Inconsistent | Consistent, Dao.ReadOnly | Dao.ForwardOnlyOpt,
        ];
        yield return Fix(FxMain1B, "option-flags-rw", [Cur, "D-CURSOR-1", "CUR-PROV", "CUR-EDIT"], "r-cursor-1b/option-flags-by-type",
            "the fixture was opened read-only, so Updatable read 0 throughout and the flag effects on updatability were not observed",
            $"For a table-type, a dynaset, a snapshot and a forward-only recordset over F and each Options value [omitted, {string.Join(", ", flags.Skip(1))}] (1 dbDenyWrite, 2 dbDenyRead, 4 dbReadOnly, 8 dbAppendOnly, 16 dbInconsistent, " +
            "32 dbConsistent, 128 dbFailOnError, 256 dbForwardOnly, 512 dbSeeChanges): OpenRecordset(F, type, options) on a writable Database; read Type, Updatable, Bookmarkable, EOF and RecordCount; close. " +
            "Each recordset is closed before the next opens and nothing is written. The pass-through, async and exec-direct values (64, 1024, 2048) were refused by the original and are not repeated, because their purpose is ODBC. " +
            "Asks which flags each type accepts, the resulting Type and Updatable on a writable open, and the refusals.",
            p =>
            {
                foreach ((_, int type) in Types)
                {
                    foreach (int? flag in flags)
                    {
                        if (flag is int f)
                        {
                            p.OpenRs("rs", "db", "F", type, A.I4(f));
                        }
                        else
                        {
                            p.OpenRs("rs", "db", "F", type);
                        }
                        p.Get("rs", "Type").Get("rs", "Updatable").Get("rs", "Bookmarkable").Get("rs", "EOF").Get("rs", "RecordCount");
                        Close(p, "rs");
                    }
                }
            }, write: true);

        (string Label, Arg[] Args)[] opens =
        [
            ("\"F\"", [A.Str("F")]), ("SQL over F", [A.Str("SELECT * FROM F")]), ("\"QP\" (parameter query)", [A.Str("QP")]), ("F, Long 0", [A.Str("F"), A.I4(0)]),
            ("F, Long 1", [A.Str("F"), A.I4(1)]), ("SQL, Long 1", [A.Str("SELECT * FROM F"), A.I4(1)]), ("F, Integer 2", [A.Str("F"), A.I2(2)]), ("F, \"2\"", [A.Str("F"), A.Str("2")]),
            ("F, Null", [A.Str("F"), A.Null]), ("F, Empty", [A.Str("F"), A.Empty]), ("F, Long 3", [A.Str("F"), A.I4(3)]), ("F, Long 5", [A.Str("F"), A.I4(5)]),
            ("F, Long 16", [A.Str("F"), A.I4(16)]), ("F, Long 32", [A.Str("F"), A.I4(32)]), ("F, Missing, Long 4", [A.Str("F"), A.Missing, A.I4(4)]),
            ("F, Long 2, Missing, Long 3", [A.Str("F"), A.I4(2), A.Missing, A.I4(3)]), ("F, Long 2, Long 0, Long 0", [A.Str("F"), A.I4(2), A.I4(0), A.I4(0)]),
            ("F, Long 2, Long 0, Long 2, Long 0", [A.Str("F"), A.I4(2), A.I4(0), A.I4(2), A.I4(0)]), ("F, Long 8", [A.Str("F"), A.I4(8)]),
            ("F, Long 2, Long 256", [A.Str("F"), A.I4(2), A.I4(256)]), ("\"\", Long 2", [A.Str(""), A.I4(2)]), ("NoSuch, Long 2", [A.Str("NoSuch"), A.I4(2)]),
            ("Null, Long 2", [A.Null, A.I4(2)]), ("SQL WHERE 1 = 0, Long 4", [A.Str("SELECT * FROM F WHERE 1 = 0"), A.I4(4)]), ("F, Long 2, Long 8", [A.Str("F"), A.I4(2), A.I4(8)]),
        ];
        yield return Fix(FxMain1B, "open-argument-shapes-rw", [Cur, "D-CURSOR-1", "CUR-PROV", "CUR-POS"], "r-cursor-1b/open-argument-shapes",
            "the fixture was opened read-only, so Updatable read 0 for every shape",
            $"For each argument list of Database.OpenRecordset on a writable Database [{string.Join(" ;; ", opens.Select(o => o.Label))}]: call it, read Type, Updatable, Bookmarkable, EOF, RecordCount and Name; close. " +
            "Nothing is written. Asks the default recordset type for a table name and for SQL, the refused type and option values, the accepted VARIANT types, omitted middle arguments and excess arguments, with Updatable on a writable open.",
            p =>
            {
                foreach ((_, Arg[] args) in opens)
                {
                    p.CallBind("rs", "db", "OpenRecordset", args).Get("rs", "Type").Get("rs", "Updatable").Get("rs", "Bookmarkable").Get("rs", "EOF").Get("rs", "RecordCount").Get("rs", "Name");
                    Close(p, "rs");
                }
            }, write: true);

        const string join = "SELECT P2.ID AS PID, P2.N AS PN, C2.ID AS CID, C2.V AS CV FROM P2 INNER JOIN C2 ON P2.ID = C2.PID ORDER BY C2.ID";
        foreach ((string label, int? flag) in new (string, int?)[] { ("omitted", null), ("inconsistent", Inconsistent), ("consistent", Consistent) })
        {
            yield return Fix(FxMain1B, $"option-join-addnew-delete-{label}", [Cur, "D-CURSOR-1", "CUR-PROV"], "r-cursor-1b/option-join-addnew-delete",
                "all three Options variants shared one database, so later variants started from earlier mutations (P2 ID 3 already existed, C2 ID 1 already deleted) and could not be compared",
                $"P2 (1,10),(2,20) and C2 (1,1,'a'),(2,1,'b'),(3,2,'c') with a declared relationship C2.PID to P2.ID; this probe has its own copy and uses the Options value {(flag is int f0 ? f0.ToString(System.Globalization.CultureInfo.InvariantCulture) : "omitted")} only. " +
                $"On a fresh dynaset over \"{join}\" with held Fields PID, PN, CID and CV: P2 and C2 are read through snapshots (the baseline); (a) AddNew, assign PID 3 and PN 30, Update, Errors, then the base tables are read; " +
                "(b) AddNew, assign CID 10 and CV 'n' only, Update, Errors, then the base tables; (c) AddNew, assign PID 1, CID 11 and CV 'm', Update, Errors, then the base tables; (d) MoveFirst, Delete, Errors, EditMode, BOF, EOF, then the base tables. " +
                "Asks which base row an AddNew or Delete on a join touches, the effect of the option and of the declared relationship, and the rows left in each base table after every step.",
                p =>
                {
                    if (flag is int f)
                    {
                        p.OpenRs("rs", "db", join, Dao.OpenDynaset, A.I4(f));
                    }
                    else
                    {
                        p.OpenRs("rs", "db", join, Dao.OpenDynaset);
                    }
                    Hold(p, "rs", "j_", "PID", "PN", "CID", "CV");
                    BaseTables(p);
                    p.Call("rs", "AddNew").Put("j_PID", "Value", A.I4(3)).Put("j_PN", "Value", A.I4(30)).Call("rs", "Update").Errors();
                    BaseTables(p);
                    p.Call("rs", "AddNew").Put("j_CID", "Value", A.I4(10)).Put("j_CV", "Value", A.Str("n")).Call("rs", "Update").Errors();
                    BaseTables(p);
                    p.Call("rs", "AddNew").Put("j_PID", "Value", A.I4(1)).Put("j_CID", "Value", A.I4(11)).Put("j_CV", "Value", A.Str("m")).Call("rs", "Update").Errors();
                    BaseTables(p);
                    p.Call("rs", "MoveFirst").Call("rs", "Delete").Errors().Get("rs", "EditMode").Get("rs", "BOF").Get("rs", "EOF");
                    Unhold(p, "j_", "PID", "PN", "CID", "CV");
                    Close(p, "rs");
                    BaseTables(p);
                }, write: true);
        }

        yield return Fix(FxMain1B, "clone-write-visibility-same-row", [Cur, "D-CURSOR-1", "CUR-LIFE", "CUR-POP", "CUR-EDIT"], "r-cursor-1b/clone-write-visibility",
            "the clone was not on the row the parent was on when it deleted, so its Delete removed ID 2 and not the row the parent stood on (ID 9); the cross-clone delete of the same row was not tested",
            "Dynaset P over F at row 2 (Move 1), held Fields N and S; Clone C; C MoveFirst and Move 1 so that it is on row 2, read both IDs; C: Edit, assign S \"clone\" and N 222, Update. Read P's held S and N, C's; P.Move 0 and read; P.MoveNext, P.MovePrevious, read. " +
            "C: AddNew, assign ID 9, N 99, S \"cn\", Update; read P's RecordCount; P.MoveLast and C.MoveLast, read both IDs; read C's RecordCount. C: Delete (its current row is the row P stands on); read P's BOF, EOF, ID and held S, P.Move 0, read again. " +
            "Then P.MoveFirst and C.MoveFirst, read both IDs; P: Delete; read C's BOF, EOF and ID, C.Move 0, read, C.Requery, read. Asks which edits, additions and deletions of one and the same row made through one family member the other sees without Requery, and the state after each.",
            p =>
            {
                p.OpenRs("rp", "db", "F", Dao.OpenDynaset).Call("rp", "Move", A.I4(1));
                Hold(p, "rp", "p_", "N", "S");
                p.CallBind("rc", "rp", "Clone");
                Hold(p, "rc", "c_", "ID", "N", "S");
                p.Call("rc", "MoveFirst").Call("rc", "Move", A.I4(1));
                Val(p, "rp", "ID");
                Val(p, "rc", "ID");
                p.Call("rc", "Edit").Put("c_S", "Value", A.Str("clone")).Put("c_N", "Value", A.I4(222)).Call("rc", "Update");
                ReadHeld(p, "p_", "S", "N");
                ReadHeld(p, "c_", "S", "N").Call("rp", "Move", A.I4(0));
                ReadHeld(p, "p_", "S", "N").Call("rp", "MoveNext").Call("rp", "MovePrevious");
                ReadHeld(p, "p_", "S", "N");
                p.Call("rc", "AddNew").Put("c_ID", "Value", A.I4(9)).Put("c_N", "Value", A.I4(99)).Put("c_S", "Value", A.Str("cn")).Call("rc", "Update");
                p.Get("rp", "RecordCount").Call("rp", "MoveLast").Call("rc", "MoveLast");
                Val(p, "rp", "ID");
                Val(p, "rc", "ID").Get("rc", "RecordCount").Call("rc", "Delete");
                State(p, "rp", false).Get("p_S", "Value").Call("rp", "Move", A.I4(0));
                State(p, "rp", false).Get("p_S", "Value").Call("rp", "MoveFirst").Call("rc", "MoveFirst");
                Val(p, "rp", "ID");
                Val(p, "rc", "ID").Call("rp", "Delete");
                State(p, "rc", false).Call("rc", "Move", A.I4(0));
                State(p, "rc", false).Call("rc", "Requery");
                State(p, "rc", false);
                Unhold(p, "p_", "N", "S");
                Unhold(p, "c_", "ID", "N", "S");
                Close(p, "rc");
                Close(p, "rp");
            }, write: true);

        (string Label, Arg Op)[] spellings =
        [
            ("= (control)", A.Str("=")), ("< (control)", A.Str("<")), ("<= (control)", A.Str("<=")), (">= (control)", A.Str(">=")), ("> (control)", A.Str(">")),
            ("=>", A.Str("=>")), ("=<", A.Str("=<")), ("!=", A.Str("!=")), ("<=>", A.Str("<=>")), (" = ", A.Str(" = ")), (">= ", A.Str(">= ")), ("\"\"", A.Str("")),
            ("Like", A.Str("Like")), ("EQ", A.Str("EQ")), ("~", A.Str("~")), ("Null", A.Null), ("Empty", A.Empty), ("Missing", A.Missing),
            ("Long 0", A.I4(0)), ("Long 61", A.I4(61)), ("Boolean True", A.Bool(true)),
        ];
        yield return Fix(FxMain1B, "seek-operator-spelling-controls", [Cur, "D-CURSOR-1", "CUR-SEEK"], "r-cursor-1b/seek-operator-spelling",
            "no valid spelling was included, so all sixteen failing spellings had no control for a working Seek on the same recordset shape",
            $"On a fresh table-type recordset over F with Index PK_F, for each comparison argument [{string.Join(" ;; ", spellings.Select(s => s.Label))}] (the five valid operators first, as controls): read ID (the start); Seek with that argument and the key Long 4; read Errors, NoMatch, BOF, EOF and ID; close. " +
            "Asks which operator spellings and argument types the method accepts and which row each selects, with the valid operators beside them on the same shape.",
            p =>
            {
                foreach ((_, Arg op) in spellings)
                {
                    p.OpenRs("rt", "db", "F", Dao.OpenTable).Put("rt", "Index", A.Str("PK_F"));
                    Val(p, "rt", "ID").Call("rt", "Seek", op, A.I4(4)).Errors().Get("rt", "NoMatch");
                    State(p, "rt", false);
                    Close(p, "rt");
                }
            }, write: false);

        string[] texts =
        [
            "S = 'alpha'", "S = 'ALPHA'", "S = 'alpha '", "S = 'O''Brien'", "S = \"O'Brien\"", "S = 'O'Brien'", "S = 'a\"b'", "S = \"a\"\"b\"",
            "S = \"a\"b\"", "S = '''", "S = ''", "S = \"\"", "S = 'abc'", "S > 'abc'", "S < 'b'", "S = [S]", "S = 'Zed'", "[S] = 'beta'", "S = 'beta",
            "S = beta", "S = 'be' & 'ta'", "S = 'a' + 'lpha'", "S = Left('alphabet', 5)",
            "P = 'p1'", "P = 'P1'", "P = 'p1 '", "P > 'p3'", "[P] = 'p2'",
        ];
        string[] likes =
        [
            "S Like 'a*'", "S Like 'A*'", "S Like 'a?c'", "S Like 'a_c'", "S Like 'a%'", "S Like 'a#c'", "S Like '[ab]*'", "S Like '[!a]*'",
            "S Like '[a-b]*'", "S Like '*r*'", "S Like '*'", "S Like ''", "S Like 'a*' AND N > 40", "S Not Like 'a*'", "S Like \"a*\"",
            "S Like 'O''B*'", "S Like 'a[\"]b'", "S Like 'a?b'", "S Like '[*]'", "Q Like '*'", "S Like Null", "S Like 'alpha'", "S Like 'ALPHA'",
            "N Like '4*'",
            "P Like '*'", "P Like ''", "P Like 'p?'", "P Like 'p*'", "P Like 'p[1-3]'",
        ];
        yield return FindUnindexed("find-text-quotes-unindexed", "r-cursor-1b/find-text-quotes", "S = 'O''Brien'", texts, 1,
            "Asks quote and escape handling in text criteria, case and trailing-space comparison, comparison operators on text, field-to-field and function-built text, and which malformed texts are refused, with the second match after FindNext, on the indexed and the unindexed column.");
        yield return FindUnindexed("find-like-patterns-unindexed", "r-cursor-1b/find-like-patterns", "S Like '*' and S Like 'O''B*'", likes, 2,
            "Asks which Like wildcards (Jet and ANSI spellings, sets, ranges, negation, escapes), case rules and Null rows the database accepts, and the rows reached in order, on the indexed and the unindexed column.");

        yield return Fix(FxDec, "getrows-decimal-ado-ddl", [Cur, "D-CURSOR-1", "CUR-BULK"], "r-cursor-1b/getrows-shape-types (the WD Decimal array)",
            "the Decimal column was made through DAO TableDef.CreateField(name, 20), which gives Precision 38 and Scale 0 and which DAO cannot read, so the GetRows array [0+0,0+2] came from an unreadable column",
            "WD(ID INTEGER, Dc DECIMAL(18,4)) was made through ADO DDL with explicit precision and scale and holds (1, 1.5), (2, no Dc), (3, -2.25), (4, 0.0001) written as SQL literals. " +
            "For a table-type, a dynaset and a snapshot over WD: GetRows(2), then BOF, EOF and ID; GetRows(2) again; BOF, EOF and ID; GetRows(2) at the end; BOF and EOF; close. Then a snapshot over \"SELECT ID, Dc FROM WD ORDER BY ID\" reads every row's Value. " +
            "Asks the VARIANT type of a Decimal cell in the GetRows array, the array dimensions per type, and how a Null Decimal appears.",
            p =>
            {
                foreach ((_, int type) in Types.Where(t => t.Type != Dao.OpenForwardOnly))
                {
                    p.OpenRs("rs", "db", "WD", type).Call("rs", "GetRows", A.I4(2));
                    State(p, "rs", false).Call("rs", "GetRows", A.I4(2));
                    State(p, "rs", false).Call("rs", "GetRows", A.I4(2)).Get("rs", "BOF").Get("rs", "EOF");
                    Close(p, "rs");
                }
                p.OpenRs("rs", "db", "SELECT ID, Dc FROM WD ORDER BY ID", Dao.OpenSnapshot).ReadAll("rs", 8, 4).Call("rs", "Close").Release("rs");
            }, write: false);
    }

    // R-DAO-1 and R-ERR-1 (evidence/r-dao-1-err-1) ------------------------------------------------------------

    private static IEnumerable<Probe> Dao1()
    {
        yield return Fix(FxDao1, "index-metadata-seek-pk-name", [DaoCard, "II-05"], "r-dao-1-err-1/index-metadata-seek",
            "rs.Index(\"PrimaryKey\") gave 3800 because the fixture's primary key is named PK_T, so every later Seek ran with no current index (3019 or 3001) and the Seek arity and error-order evidence is void",
            "A table-type recordset on T; Index put to 'PK_T' (the name of T's primary key), read Errors, read Index; Seek('=', 3), read Errors, NoMatch and the row; Seek with 13 key arguments (Long 1), read Errors and NoMatch; " +
            "Seek with 14 key arguments, read Errors; Seek with the comparison 'bogus' and one key, read Errors. Asks the Seek arity limit and the error order on a recordset whose current index is set.",
            p =>
            {
                p.OpenRs("rs", "db", "T", Dao.OpenTable).Put("rs", "Index", A.Str("PK_T")).Errors().Get("rs", "Index");
                p.Call("rs", "Seek", A.Str("="), A.I4(3)).Errors().Get("rs", "NoMatch").ReadRow("rs");
                p.Call("rs", "Seek", [A.Str("="), .. Enumerable.Repeat(A.I4(1), 13)]).Errors().Get("rs", "NoMatch");
                p.Call("rs", "Seek", [A.Str("="), .. Enumerable.Repeat(A.I4(1), 14)]).Errors();
                p.Call("rs", "Seek", A.Str("bogus"), A.I4(1)).Errors();
                p.Call("rs", "Close");
            }, write: false);

        yield return Fix(FxDao1, "prop-user-appended-lifecycle", [DaoCard, "DAO-PROP"], "r-dao-1-err-1/prop-user-lifecycle",
            "the held-after-Delete reads used pd, which was never appended (Value(\"\") gave 3385, Append 3386); the appended and deleted object was pdup, so held-Property behaviour across Delete and same-name recreation was untested",
            "On TableDef T: a Property pa made by CreateProperty('P1', Text, 'init') and appended; Name, Type and Value of pa read; a duplicate P1 made by CreateProperty('P1', Text, 'dup') and appended, Errors; Type of pa put to Long, Errors; " +
            "Value of pa put to 7, Null, Empty, each read back, Errors; Properties.Delete('P1'), Errors, then the HELD pa's Name and Value read with Errors after each; Delete('P1') again, Errors; " +
            "a Property pnew made by CreateProperty('P1', Long, 9) and appended, Errors; the held pa's Value and pnew's Value read; the names of T's Properties enumerated. Then Close, a read-only reopen, and the reopened P1's Type and Value read. " +
            "Asks what a held Property that WAS appended does across Delete and same-name recreation.",
            p =>
            {
                p.GetBind("tdfs", "db", "TableDefs").Call("tdfs", "Refresh").Item("t", "tdfs", A.Str("T")).GetBind("tp", "t", "Properties")
                    .CallBind("pa", "t", "CreateProperty", A.Str("P1"), A.I4(TypeText), A.Str("init")).Get("pa", "Name").Get("pa", "Type").Get("pa", "Value")
                    .Call("tp", "Append", A.Obj("pa")).Errors().Get("pa", "Value").Get("tp", "Count");
                p.CallBind("pdup", "t", "CreateProperty", A.Str("P1"), A.I4(TypeText), A.Str("dup")).Call("tp", "Append", A.Obj("pdup")).Errors();
                p.Put("pa", "Type", A.I4(TypeLong)).Errors();
                p.Put("pa", "Value", A.I4(7)).Errors().Get("pa", "Value").Put("pa", "Value", A.Null).Errors().Get("pa", "Value")
                    .Put("pa", "Value", A.Empty).Errors().Get("pa", "Value");
                p.Call("tp", "Delete", A.Str("P1")).Errors().Get("pa", "Name").Errors().Get("pa", "Value").Errors().Call("tp", "Delete", A.Str("P1")).Errors();
                p.CallBind("pnew", "t", "CreateProperty", A.Str("P1"), A.I4(TypeLong), A.I4(9)).Call("tp", "Append", A.Obj("pnew")).Errors()
                    .Get("pa", "Value").Get("pnew", "Value").Each("tp", "x", 64, b => b.Get("x", "Name"));
                p.Release("pnew").Release("pdup").Release("pa").Release("tp").Release("t").Release("tdfs").Call("db", "Close").Release("db");
                p.OpenDb("db2").GetBind("tdfs2", "db2", "TableDefs").Item("t2", "tdfs2", A.Str("T")).GetBind("tp2", "t2", "Properties")
                    .Item("p1r", "tp2", A.Str("P1")).Get("p1r", "Type").Get("p1r", "Value").Errors();
                p.Release("p1r").Release("tp2").Release("t2").Release("tdfs2").Call("db2", "Close").Release("db2");
            }, write: true, closeDb: false);

        yield return Fix(FxDao1, "recordset-state-sort-discriminating", [DaoCard, "II-07", "DAO-ID"], "r-dao-1-err-1/recordset-state-inventory",
            "Sort \"V DESC\" over the rows left by Filter 'ID > 2' (three, four, five) gave the same order as ID ascending and so did not discriminate; GetRows was not captured element by element; the dynaset was opened read-only so Updatable read False",
            "On a dynaset over T opened on a writable Database: read Updatable and LockEdits; GetRows(2), MoveFirst, GetRows() (all remaining). Filter 'ID > 2' put and a snapshot derived with OpenRecordset, read to EOF with Updatable; " +
            "Sort 'V ASC' put (the Filter stays) and a derived snapshot read to EOF; Filter put to '' with Sort 'V DESC' and a derived snapshot read to EOF; Sort 'N DESC' with Filter '' and a derived snapshot read to EOF; each derived recordset closed. " +
            "Asks the order and rows of a derived recordset under a Sort whose order differs from the ID order, and the GetRows arrays.",
            p =>
            {
                p.OpenRs("rs", "db", "T", Dao.OpenDynaset).Get("rs", "Updatable").Get("rs", "LockEdits")
                    .Call("rs", "GetRows", A.I4(2)).Call("rs", "MoveFirst").Call("rs", "GetRows");
                p.Put("rs", "Filter", A.Str("ID > 2")).CallBind("rf", "rs", "OpenRecordset", A.I4(Dao.OpenSnapshot)).Get("rf", "Updatable").ReadAll("rf", 8, 4).Call("rf", "Close").Release("rf");
                p.Put("rs", "Sort", A.Str("V ASC")).CallBind("rso", "rs", "OpenRecordset", A.I4(Dao.OpenSnapshot)).ReadAll("rso", 8, 4).Call("rso", "Close").Release("rso");
                p.Put("rs", "Filter", A.Str("")).Put("rs", "Sort", A.Str("V DESC")).CallBind("rsd", "rs", "OpenRecordset", A.I4(Dao.OpenSnapshot)).ReadAll("rsd", 8, 4).Call("rsd", "Close").Release("rsd");
                p.Put("rs", "Sort", A.Str("N DESC")).CallBind("rsn", "rs", "OpenRecordset", A.I4(Dao.OpenSnapshot)).ReadAll("rsn", 8, 4).Call("rsn", "Close").Release("rsn");
                p.Call("rs", "Close").Release("rs");
            }, write: true);

        yield return new Probe(Card, "ado-connection-errors-clear-with-entries", [ErrCard, "ERR-ADO", "ERR-BASE"],
            "Repairs r-dao-1-err-1/ado-connection-errors: Errors.Clear ran with Count already 0 (a successful Execute had emptied it), so the clear effect was untested. " +
            $"On a new ADODB.Connection (Provider {AceOleDb}, Data Source through Properties, Mode adModeRead): Connection.Errors read; Open; Errors read; Execute 'SELEC ID FROM T' (provider failure) and, with NO call in between, the held Errors object's Count and a fresh read of Errors; " +
            "Errors.Clear on the held object; its Count and a fresh read. Then Execute 'SELECT * FROM NoSuchTable' (a second provider failure), the same reads, Clear, the same reads. Then a successful Execute of 'SELECT ID, V FROM T' and the Count. " +
            "Asks what Errors.Clear does to entries that are present, and whether reading Errors changes them. The fixture copy is opened read-only through ADO.",
            p =>
            {
                p.EngineLite().Create("ADODB.Connection", "conn");
                AdoErrs(p);
                p.Put("conn", "Provider", A.Str(AceOleDb))
                    .GetBind("cprops", "conn", "Properties").Item("cds", "cprops", A.Str("Data Source")).Put("cds", "Value", A.In())
                    .Release("cds").Release("cprops").Put("conn", "Mode", A.I4(1)).Call("conn", "Open");
                AdoErrs(p);
                foreach (string sql in new[] { "SELEC ID FROM T", "SELECT * FROM NoSuchTable" })
                {
                    p.Call("conn", "Execute", A.Str(sql)).GetBind("herrs", "conn", "Errors").Get("herrs", "Count");
                    AdoErrs(p);
                    p.Call("herrs", "Clear").Get("herrs", "Count");
                    AdoErrs(p);
                    p.Release("herrs");
                }
                p.CallBind("ars", "conn", "Execute", A.Str("SELECT ID, V FROM T")).GetBind("herrs", "conn", "Errors").Get("herrs", "Count").Release("herrs");
                p.Call("ars", "Close").Release("ars");
                p.FinishLite("conn");
            })
        { Fixtures = [("main", FxDao1)] };

        yield return new Probe(Card, "err-base-scope-second-process", [ErrCard, "ERR-BASE", "ERR-DAO-ORDER"],
            "Repairs r-dao-1-err-1/err-base-scope: the 'independent DBEngine' was a second CoCreate in the same process and returned the same object (u1), so it was not independent. " +
            "Two worker processes: A opens the fixture read-only, reads DBEngine.Errors, runs OpenRecordset('NoSuchTable') (snapshot), reads Errors, signals, and waits for B; " +
            "B waits for A's signal, creates its own DAO.DBEngine.120, reads its Errors (before any failure of its own), runs OpenDatabase of a file that does not exist (read-only), reads its Errors, and signals; " +
            "A then reads its Errors again and runs a snapshot OpenRecordset of T. Asks whether a failure in one DBEngine appears in the Errors of a truly independent engine, and the converse.",
            a =>
            {
                a.EngineLite().OpenDb();
                a.Errors().OpenRs("ra", "db", "NoSuchTable", Dao.OpenSnapshot);
                a.Errors().Signal("a-failed").Await("b-failed", Wait);
                a.Errors().OpenRs("rs", "db", "T", Dao.OpenSnapshot).Call("rs", "Close");
                a.Errors().FinishLite("db");
            })
        {
            Fixtures = [("main", FxDao1)],
            Participants =
            [
                ("b", b =>
                {
                    b.Await("a-failed", Wait).EngineLite();
                    b.Errors().CallBind("dabs", "eng", "OpenDatabase", A.Out("absent-engine-b.accdb"), A.Bool(false), A.Bool(true));
                    b.Errors().Signal("b-failed").FinishLite();
                }),
            ],
        };
    }

    // R-DAO-1 (evidence/r-dao-1b) -----------------------------------------------------------------------------

    private static IEnumerable<Probe> Dao1B()
    {
        yield return Fix(FxRich, "updatable-members-rw", [DaoCard, "DAO-MANIFEST", "DAO-PROP", "II-07"], "r-dao-1b/db-members-accdb, tabledef-members-each, recordset-members-*, field-members-typed-each, recordset-fields-members-each",
            "the fixture was opened read-only, so every Updatable and DataUpdatable read (all 0) could not be attributed to ACE",
            "On a writable Database: Database.Updatable; every TableDef (at most 32) Name and Updatable; every QueryDef (at most 16) Name and Updatable; for a table-type, a dynaset, a snapshot and a forward-only recordset on Parent: Updatable, LockEdits, " +
            "and every Field's Name and DataUpdatable; a dynaset on Parent opened with LockEdits 2 and with 3 (the fourth argument): Updatable and LockEdits; a snapshot through QueryDef QUnion: Updatable and every Field's DataUpdatable; " +
            "every Field of TableDef Typed: Name and DataUpdatable. Nothing is written. Asks Updatable, DataUpdatable and LockEdits on a writable open, where the original read them on a read-only one.",
            p =>
            {
                p.Get("db", "Updatable");
                p.GetBind("tds", "db", "TableDefs").Each("tds", "t", 32, b => b.Get("t", "Name").Get("t", "Updatable")).Release("tds");
                p.GetBind("qds", "db", "QueryDefs").Each("qds", "q", 16, b => b.Get("q", "Name").Get("q", "Updatable")).Release("qds");
                foreach ((_, int type) in Types)
                {
                    p.OpenRs("rs", "db", "Parent", type).Get("rs", "Updatable").Get("rs", "LockEdits");
                    p.GetBind("rfl", "rs", "Fields").Each("rfl", "f", 8, b => b.Get("f", "Name").Get("f", "DataUpdatable")).Release("rfl");
                    Close(p, "rs");
                }
                foreach (int lockType in new[] { 2, 3 })
                {
                    p.OpenRs("rs", "db", "Parent", Dao.OpenDynaset, A.I4(0), A.I4(lockType)).Get("rs", "Updatable").Get("rs", "LockEdits");
                    Close(p, "rs");
                }
                p.GetBind("qds", "db", "QueryDefs").Item("qu", "qds", A.Str("QUnion")).CallBind("rq", "qu", "OpenRecordset", A.I4(Dao.OpenSnapshot)).Get("rq", "Updatable");
                p.GetBind("qfl", "rq", "Fields").Each("qfl", "f", 8, b => b.Get("f", "Name").Get("f", "DataUpdatable")).Release("qfl");
                p.Call("rq", "Close").Release("rq").Release("qu").Release("qds");
                p.GetBind("tds", "db", "TableDefs").Call("tds", "Refresh").Item("tt", "tds", A.Str("Typed")).GetBind("tfl", "tt", "Fields")
                    .Each("tfl", "f", 24, b => b.Get("f", "Name").Get("f", "DataUpdatable")).Release("tfl").Release("tt").Release("tds");
            }, write: true);

        yield return Fix(FxRich, "coll-delete-field-unindexed", [DaoCard, "DAO-COLL", "DAO-DRAFT", "II-05"], "r-dao-1b/coll-delete-field-indexed",
            "every field tried was indexed or in a relation, so no successful Fields.Delete was captured",
            "On a writable copy, TableDef Typed (primary key K; the other fields are in no index and no relation; one row): Fields Count and names, Indexes Count and names, Relations Count; Fields.Delete('TText'), Errors; the same reads (before Refresh); Fields.Refresh; the same reads; " +
            "a fresh Fields collection read; Fields.Item('TText') read, Errors; Fields.Delete('TOle'), Errors, the same reads after Refresh; a snapshot on Typed reads the row and its field names. " +
            "Asks whether a field in no index and no relation can be deleted, when the collection shows it, and what is left of the row.",
            p =>
            {
                p.GetBind("tds", "db", "TableDefs").Call("tds", "Refresh").Item("t", "tds", A.Str("Typed")).GetBind("tf", "t", "Fields").GetBind("ti", "t", "Indexes").GetBind("rels", "db", "Relations");
                void Snapshot(string tag) =>
                    Names(Names(p.Get("tf", "Count").Get("ti", "Count").Get("rels", "Count"), "tf", "f" + tag, 24), "ti", "i" + tag, 16);
                Snapshot("0");
                p.Call("tf", "Delete", A.Str("TText")).Errors();
                Snapshot("1");
                p.Call("tf", "Refresh").Call("ti", "Refresh").Call("rels", "Refresh");
                Snapshot("2");
                p.GetBind("tf2", "t", "Fields").Get("tf2", "Count");
                Names(p, "tf2", "g2", 24).Release("tf2");
                p.Item("gone", "tf", A.Str("TText")).Errors();
                p.Call("tf", "Delete", A.Str("TOle")).Errors().Call("tf", "Refresh");
                Snapshot("3");
                p.OpenRs("rs", "db", "Typed", Dao.OpenSnapshot).GetBind("rfl", "rs", "Fields");
                Names(p, "rfl", "h", 24).Release("rfl").ReadRow("rs").Call("rs", "Close").Release("rs");
            }, write: true);

        yield return Fix(FxRich, "coll-append-field-readback", [DaoCard, "DAO-COLL", "DAO-DRAFT"], "r-dao-1b/coll-append-field-existing-rows",
            "ZReq was never read back, so whether a Required field appended to a table holding rows stores Null, a zero or nothing was not observed",
            "On a writable copy, on Parent (five rows): a Required Long field ZReq (no default), an optional Long ZOpt and a Text field ZTxt (size 10, AllowZeroLength True) are appended one after another; after each the Fields Count and each Field's Name, OrdinalPosition, " +
            "Required and AllowZeroLength are read; after the third, Refresh and the same reads; then a new snapshot of ID, ZReq, ZOpt, ZTxt reads every row, and a dynaset over Parent reads ZReq, ZOpt and ZTxt of the first row; the Database is closed, " +
            "reopened read-only, and the snapshot is read again. Asks what the existing rows hold in each new field, in the same session and after a reopen.",
            p =>
            {
                p.GetBind("tds", "db", "TableDefs").Call("tds", "Refresh").Item("t", "tds", A.Str("Parent")).GetBind("tf", "t", "Fields");
                void Snapshot(string tag) =>
                    p.Get("tf", "Count").Each("tf", "f" + tag, 16, b => b.Get("f" + tag, "Name").Get("f" + tag, "OrdinalPosition")
                        .Get("f" + tag, "Required").Get("f" + tag, "AllowZeroLength"));
                p.CallBind("zr", "t", "CreateField", A.Str("ZReq"), A.I4(TypeLong)).Put("zr", "Required", A.Bool(true)).Call("tf", "Append", A.Obj("zr")).Errors();
                Snapshot("0");
                p.CallBind("zo", "t", "CreateField", A.Str("ZOpt"), A.I4(TypeLong)).Call("tf", "Append", A.Obj("zo")).Errors();
                Snapshot("1");
                p.CallBind("zt", "t", "CreateField", A.Str("ZTxt"), A.I4(TypeText), A.I4(10)).Put("zt", "AllowZeroLength", A.Bool(true)).Call("tf", "Append", A.Obj("zt")).Errors();
                Snapshot("2");
                p.Call("tf", "Refresh");
                Snapshot("3");
                p.Release("tf").Release("t").Release("tds");
                p.OpenRs("rs", "db", "SELECT ID, ZReq, ZOpt, ZTxt FROM Parent ORDER BY ID", Dao.OpenSnapshot).ReadAll("rs", 8, 4).Call("rs", "Close").Release("rs");
                p.OpenRs("rd", "db", "Parent", Dao.OpenDynaset);
                Hold(p, "rd", "z_", "ZReq", "ZOpt", "ZTxt");
                ReadHeld(p, "z_", "ZReq", "ZOpt", "ZTxt");
                Unhold(p, "z_", "ZReq", "ZOpt", "ZTxt");
                Close(p, "rd");
                p.Call("db", "Close").Release("db").OpenDb();
                p.OpenRs("rs", "db", "SELECT ID, ZReq, ZOpt, ZTxt FROM Parent ORDER BY ID", Dao.OpenSnapshot).ReadAll("rs", 8, 4).Call("rs", "Close").Release("rs");
            }, write: true);
    }

    // R-ERR-1 (evidence/r-err-1b) -----------------------------------------------------------------------------

    private static IEnumerable<Probe> Err1B()
    {
        const string InsertDefect = "the INSERT ran through Database.Execute without dbFailOnError (128) and the table was never read back, so the record cannot say whether ACE refused the row, skipped it silently or accepted it";
        string[] insertRefs = [ErrCard, "ERR-BASE", "ERR-PARTIAL"];
        string Asks(string sql, string table, string detail) =>
            $"{detail} Errors read; every row of {table} read through a snapshot (the baseline); Database.Execute of \"{sql}\" with Options dbFailOnError (128); Errors read; RecordsAffected read; every row of {table} read through a new snapshot. " +
            "Asks whether the row is refused, skipped or accepted, the Number and message, and what the table holds afterwards.";

        (string Name, string Sql, string Table, string Order, string Detail)[] inserts =
        [
            ("con-pk-duplicate-insert", "INSERT INTO P (ID, Code, Req) VALUES (1, 'x1', 'r')", "P", "ID", "P holds a row with ID 1 under primary key PK_P."),
            ("con-unique-index-insert", "INSERT INTO P (ID, Code, Req) VALUES (10, 'c1', 'r')", "P", "ID", "P holds Code 'c1' under unique index UX_P_Code."),
            ("con-required-omitted", "INSERT INTO P (ID, Code) VALUES (11, 'x11')", "P", "ID", "Column Req is NOT NULL and is omitted."),
            ("con-zero-length-required", "INSERT INTO P (ID, Code, Req) VALUES (12, 'x12', '')", "P", "ID", "Req is NOT NULL with AllowZeroLength set False."),
            ("con-fk-orphan-insert", "INSERT INTO Ch (CID, PID) VALUES (10, 999)", "Ch", "CID", "Ch.PID references P.ID through FK_Ch and no P row has ID 999."),
            ("con-field-validation-insert", "INSERT INTO VR (ID, Pv, A, B2) VALUES (10, -1, 1, 2)", "VR", "ID", "VR.Pv carries a field ValidationRule and ValidationText."),
            ("con-table-validation-insert", "INSERT INTO VR (ID, Pv, A, B2) VALUES (11, 1, 5, 2)", "VR", "ID", "VR carries a table ValidationRule and ValidationText."),
            ("sql-insert-text-into-number", "INSERT INTO P (ID, Code, Req, Cnt) VALUES (15, 'x15', 'r', 'abc')", "P", "ID", "Cnt is an Integer column."),
            ("ovf-insert-byte", "INSERT INTO P (ID, Code, Req, Bt) VALUES (16, 'x16', 'r', 300)", "P", "ID", "Bt is a Byte column."),
        ];
        foreach ((string name, string sql, string table, string order, string detail) in inserts)
        {
            yield return Fix(FxErrBase, name + "-failonerror", insertRefs, "r-err-1b/" + name, InsertDefect, Asks(sql, table, detail),
                p =>
                {
                    p.Errors();
                    ReadTable(p, "db", table, order);
                    p.Call("db", "Execute", A.Str(sql), A.I4(Dao.FailOnError)).Errors().Get("db", "RecordsAffected");
                    ReadTable(p, "db", table, order);
                }, write: true);
        }

        yield return Fix(FxErrBase, "lock-readonly-db-insert-failonerror", insertRefs, "r-err-1b/lock-readonly-db-insert", InsertDefect + "; the read-only Database's insert also had no read-back",
            "A second Database dro is opened read-only on the same file. Errors read; every row of Q read through a snapshot on db and on dro (the baseline); dro.Execute of \"INSERT INTO Q (ID, V) VALUES (50, 'z')\" with dbFailOnError (128); " +
            "Errors read; RecordsAffected of dro read; every row of Q read through a new snapshot on db and on dro. Asks whether a read-only Database refuses the insert, the Number and message, and what Q holds afterwards.",
            p =>
            {
                p.OpenDb("dro");
                p.Errors();
                ReadTable(p, "db", "Q", "ID", "rt1");
                ReadTable(p, "dro", "Q", "ID", "rt2");
                p.Call("dro", "Execute", A.Str("INSERT INTO Q (ID, V) VALUES (50, 'z')"), A.I4(Dao.FailOnError)).Errors().Get("dro", "RecordsAffected");
                ReadTable(p, "db", "Q", "ID", "rt1");
                ReadTable(p, "dro", "Q", "ID", "rt2");
                p.Call("dro", "Close").Release("dro");
            }, write: true);

        const string Dup = "INSERT INTO P (ID, Code, Req) VALUES (1, 'x1', 'r')";
        yield return Fix(FxErrBase, "proto-txn-fail-rollback-failonerror", [ErrCard, "ERR-BASE", "ERR-DAO-ORDER", "ERR-THREAD"], "r-err-1b/proto-txn-fail-rollback",
            "the Dup insert used no dbFailOnError, so it never failed and the rollback and commit of a failed statement inside a transaction were not observed",
            $"Errors read; every row of P read through a snapshot; DBEngine.BeginTrans; Database.Execute of \"{Dup}\" with dbFailOnError (128); Errors read; P read; Rollback; Errors read; P read; BeginTrans; the same Execute again; Errors read; CommitTrans; Errors read; P read. " +
            "Asks what a failed statement does inside a transaction, what Rollback and CommitTrans do afterwards, and the Errors collection after each.",
            p =>
            {
                p.Errors();
                ReadTable(p, "db", "P", "ID");
                p.Call("eng", "BeginTrans").Call("db", "Execute", A.Str(Dup), A.I4(Dao.FailOnError)).Errors();
                ReadTable(p, "db", "P", "ID");
                p.Call("eng", "Rollback").Errors();
                ReadTable(p, "db", "P", "ID");
                p.Call("eng", "BeginTrans").Call("db", "Execute", A.Str(Dup), A.I4(Dao.FailOnError)).Errors();
                p.Call("eng", "CommitTrans").Errors();
                ReadTable(p, "db", "P", "ID");
            }, write: true);

        yield return Fix(FxErrBase, "deleted-tabledef-held-calls", [ErrCard, "ERR-BASE", "ERR-CLOSE"], "r-err-1b/deleted-tabledef-createindex",
            "CreateIndex on a held, dropped TableDef returned an object and never failed, so it said nothing about calls on a deleted TableDef",
            "Two tables are made by SQL (Tmp1 and Tmp2, one Long column A), TableDefs refreshed, and each held as a TableDef; Tmp1 is dropped by Database.Execute 'DROP TABLE Tmp1' and Tmp2 is removed by TableDefs.Delete('Tmp2'). For each held TableDef in turn, " +
            "with Errors read right after each call: Name; Fields (and Count); CreateIndex('IxX'); CreateField('A') on that index; Fields.Append of it; Indexes.Append of the index on the TableDef; then TableDefs.Refresh and the Indexes.Append again. " +
            "Asks which calls on a held TableDef of a deleted table fail, with which Number, and whether Refresh changes it.",
            p =>
            {
                p.Call("db", "Execute", A.Str("CREATE TABLE Tmp1 (A LONG)")).Call("db", "Execute", A.Str("CREATE TABLE Tmp2 (A LONG)"))
                    .GetBind("tdfs", "db", "TableDefs").Call("tdfs", "Refresh").Item("t1", "tdfs", A.Str("Tmp1")).Item("t2", "tdfs", A.Str("Tmp2"));
                p.Call("db", "Execute", A.Str("DROP TABLE Tmp1")).Errors();
                p.Call("tdfs", "Delete", A.Str("Tmp2")).Errors();
                foreach (string t in new[] { "t1", "t2" })
                {
                    p.Get(t, "Name").Errors().GetBind("f" + t, t, "Fields").Errors().Get("f" + t, "Count").Errors();
                    p.CallBind("ix" + t, t, "CreateIndex", A.Str("IxX")).Errors().CallBind("ixf" + t, "ix" + t, "CreateField", A.Str("A")).Errors()
                        .GetBind("ixfs" + t, "ix" + t, "Fields").Call("ixfs" + t, "Append", A.Obj("ixf" + t)).Errors()
                        .GetBind("tix" + t, t, "Indexes").Call("tix" + t, "Append", A.Obj("ix" + t)).Errors();
                }
                p.Call("tdfs", "Refresh").Errors();
                foreach (string t in new[] { "t1", "t2" })
                {
                    p.Call("tix" + t, "Append", A.Obj("ix" + t)).Errors();
                }
            }, write: true);

        yield return Fix(FxErrBase, "proto-two-fails-different-numbers", [ErrCard, "ERR-BASE", "ERR-DAO-ORDER", "ERR-SNAPSHOT"], "r-err-1b/proto-* (repeat-A cases)",
            "every case repeated the same failure A, and the intermediate success was always a read, so repeat-A could not separate 'replace' from 'keep'; only the pair with different text and the same number 3078 discriminated",
            "Failures with different Numbers: Errors read; A = Database.Execute('SELEC ID FROM P'), Errors read; B = OpenRecordset('SELECT NoCol FROM P') (snapshot), Errors read; C = TableDefs.Item('NoSuchTable'), Errors read; " +
            "a successful snapshot OpenRecordset of Q (closed), Errors read; D = Database.Execute('DROP TABLE P'), Errors read. Then A and B back to back with no Errors read between them, Errors read. " +
            "Asks whether a later failure with a different Number replaces, adds to or leaves the earlier entry, and what a success does to the entry.",
            p =>
            {
                p.Errors().Call("db", "Execute", A.Str("SELEC ID FROM P")).Errors();
                p.Call("db", "OpenRecordset", A.Str("SELECT NoCol FROM P"), A.I4(Dao.OpenSnapshot)).Errors();
                p.GetBind("tdfs", "db", "TableDefs").Item("x", "tdfs", A.Str("NoSuchTable")).Errors();
                p.OpenRs("sr", "db", "SELECT ID, V FROM Q ORDER BY ID", Dao.OpenSnapshot).Call("sr", "Close").Release("sr").Errors();
                p.Call("db", "Execute", A.Str("DROP TABLE P")).Errors();
                p.Call("db", "Execute", A.Str("SELEC ID FROM P")).Call("db", "OpenRecordset", A.Str("SELECT NoCol FROM P"), A.I4(Dao.OpenSnapshot)).Errors();
            }, write: true);

        yield return Fix(FxErrBase, "arg-percentposition-150-readback", [ErrCard, "ERR-BASE", "ERR-BIND"], "r-err-1b/arg-percentposition-150",
            "the assignment returned success and nothing was read back (position, rows), so whether it moved the cursor or did nothing was not observed",
            "On a NEW dynaset over Q (two rows) and then on another after MoveLast: read PercentPosition, AbsolutePosition, RecordCount and the ID; put PercentPosition R4 150; read Errors; " +
            "read PercentPosition, AbsolutePosition, RecordCount, BOF, EOF, EditMode and the ID; close. Asks what an out-of-range PercentPosition does to the position and rows, before and after the recordset is populated.",
            p =>
            {
                foreach (bool last in new[] { false, true })
                {
                    p.OpenRs("rs", "db", "SELECT * FROM Q ORDER BY ID", Dao.OpenDynaset);
                    if (last)
                    {
                        p.Call("rs", "MoveLast");
                    }
                    p.Get("rs", "PercentPosition").Get("rs", "AbsolutePosition").Get("rs", "RecordCount");
                    Val(p, "rs", "ID").Put("rs", "PercentPosition", A.R4(150f)).Errors();
                    p.Get("rs", "PercentPosition").Get("rs", "AbsolutePosition").Get("rs", "RecordCount").Get("rs", "BOF").Get("rs", "EOF").Get("rs", "EditMode");
                    Val(p, "rs", "ID");
                    Close(p, "rs");
                }
            }, write: false);
    }

    // R-CURSOR-1, R-DAO-1, R-ERR-1 (evidence/r-def-cursor-dao) ------------------------------------------------

    private static IEnumerable<Probe> DefCursorDao()
    {
        string[] errRefs = [ErrCard, "ERR-BASE"];

        yield return Fix(FxDef, "err-multi-violation-sql-failonerror", [ErrCard, "ERR-BASE", "ERR-DAO-ORDER", "ERR-PARTIAL"], "r-def-cursor-dao/err-multi-violation-sql",
            "the INSERT ran without dbFailOnError and R was never read back: success, Errors 0, and so it answered nothing",
            "Errors read; every row of R read through a snapshot (the baseline); Database.Execute of \"INSERT INTO R (ID, Cd, Rq, Vv) VALUES (1, 'r1', Null, -5)\" with dbFailOnError (128) (one row that duplicates the primary key and the unique Cd, omits the required Rq and breaks the validation rule of Vv); " +
            "Errors read (Count, then Number, Description and Source of each entry); RecordsAffected read; R read through a new snapshot; a snapshot of T opened and closed; Errors read; the same Execute again; Errors read; R read. " +
            "Asks how many Errors entries and which Number, Description and Source the one failing call leaves, and whether the row was stored.",
            p =>
            {
                p.Errors();
                ReadTable(p, "db", "R", "ID");
                p.Call("db", "Execute", A.Str(MultiViolation), A.I4(Dao.FailOnError)).Errors().Get("db", "RecordsAffected");
                ReadTable(p, "db", "R", "ID");
                OpenCloseSnapshot(p).Errors();
                p.Call("db", "Execute", A.Str(MultiViolation), A.I4(Dao.FailOnError)).Errors();
                ReadTable(p, "db", "R", "ID");
            }, write: true);

        yield return Fix(FxDef, "err-drop-missing-two-pass", errRefs, "r-def-cursor-dao/err-drop-missing-index-column",
            "two failing statements ran before one Errors read, so the first failure's number was only in its EXCEPINFO; the second pass failed 3211 because the snapshot sr was open on T, so it was a different fault",
            "Errors read; Database.Execute of \"DROP INDEX NoSuchIx ON T\", Errors read; Execute of \"ALTER TABLE T DROP COLUMN NoSuchCol\", Errors read; a snapshot of T opened and CLOSED, Errors read; " +
            "the two statements again, each followed by its own Errors read; then the Indexes of T (after Refresh) and the Fields of T read. Asks the Number of each failure on its own, in both passes, and what the catalog holds afterwards.",
            p =>
            {
                string[] steps = ["DROP INDEX NoSuchIx ON T", "ALTER TABLE T DROP COLUMN NoSuchCol"];
                p.Errors();
                foreach (string sql in steps)
                {
                    p.Call("db", "Execute", A.Str(sql)).Errors();
                }
                OpenCloseSnapshot(p).Errors();
                foreach (string sql in steps)
                {
                    p.Call("db", "Execute", A.Str(sql)).Errors();
                }
                IndexNames(p, "T", "a");
                FieldNames(p, "T", "a");
            }, write: true);

        yield return Fix(FxDef, "err-index-over-duplicates-two-pass", [ErrCard, "ERR-BASE", "ERR-PARTIAL"], "r-def-cursor-dao/err-index-over-duplicates",
            "two failing statements ran before one Errors read, so the first failure's number was only in its EXCEPINFO; the CREATE INDEX of the second pass failed 3211 because the snapshot sr was open on T",
            "Errors read; Database.Execute of \"CREATE UNIQUE INDEX UX_TN_dup ON T (N)\" (N holds 40 twice), Errors read; Execute of \"ALTER TABLE H ADD CONSTRAINT PK_H PRIMARY KEY (A)\" (A holds 1 twice), Errors read; a snapshot of T opened and CLOSED, Errors read; " +
            "the two statements again, each followed by its own Errors read; then the Indexes of T and of H (after Refresh) read. Asks the Number of each failure on its own, in both passes, and whether any index was left behind.",
            p =>
            {
                string[] steps = ["CREATE UNIQUE INDEX UX_TN_dup ON T (N)", "ALTER TABLE H ADD CONSTRAINT PK_H PRIMARY KEY (A)"];
                p.Errors();
                foreach (string sql in steps)
                {
                    p.Call("db", "Execute", A.Str(sql)).Errors();
                }
                OpenCloseSnapshot(p).Errors();
                foreach (string sql in steps)
                {
                    p.Call("db", "Execute", A.Str(sql)).Errors();
                }
                IndexNames(p, "T", "a");
                IndexNames(p, "H", "b");
            }, write: true);

        yield return Fix(FxDef, "err-dup-append-two-pass", [ErrCard, "ERR-BASE", "DAO-COLL"], "r-def-cursor-dao/err-dup-field-append and err-dup-index-append",
            "the second pass failed 3211 because the snapshot sr was open on T, so 'the same step again' was a different fault",
            "Errors read; a new Index named IX_TN (an existing index of T) over N appended with TableDef.Indexes.Append, Errors read; a new Long Field named N appended with TableDef.Fields.Append, Errors read; a snapshot of T opened and CLOSED, Errors read; " +
            "the two appends again, each followed by its own Errors read; then the Indexes of T (after Refresh) and the Fields of T read. Asks the Number of each duplicate-name failure on its own, in both passes, and what the collections hold afterwards.",
            p =>
            {
                void DupIndex() =>
                    p.GetBind("tds", "db", "TableDefs").Item("tt", "tds", A.Str("T")).CallBind("ix", "tt", "CreateIndex", A.Str("IX_TN")).CallBind("ixf", "ix", "CreateField", A.Str("N"))
                        .GetBind("ixfs", "ix", "Fields").Call("ixfs", "Append", A.Obj("ixf")).GetBind("ixs", "tt", "Indexes").Call("ixs", "Append", A.Obj("ix")).Errors()
                        .Release("ixs").Release("ixfs").Release("ixf").Release("ix").Release("tt").Release("tds");
                void DupField() =>
                    p.GetBind("tds", "db", "TableDefs").Item("tt", "tds", A.Str("T")).CallBind("nf", "tt", "CreateField", A.Str("N"), A.I4(TypeLong)).GetBind("tfs", "tt", "Fields")
                        .Call("tfs", "Append", A.Obj("nf")).Errors().Release("tfs").Release("nf").Release("tt").Release("tds");
                p.Errors();
                DupIndex();
                DupField();
                OpenCloseSnapshot(p).Errors();
                DupIndex();
                DupField();
                IndexNames(p, "T", "a");
                FieldNames(p, "T", "a");
            }, write: true);

        string[] querySources =
        [
            "SELECT DISTINCT N FROM T ORDER BY N", "SELECT N, Count(*) AS Cn FROM T GROUP BY N ORDER BY N",
            "SELECT TOP 3 ID, N FROM T ORDER BY N DESC, ID", "SELECT TOP 4 ID, N FROM T ORDER BY N",
            "SELECT ID, N FROM T WHERE ID <= 3 UNION SELECT ID + 10, N FROM T WHERE ID > 6",
            "TRANSFORM Count(ID) AS Cn SELECT S FROM T GROUP BY S PIVOT Fl",
            "SELECT PA.ID AS PAID, CH.ID AS CHID FROM PA LEFT JOIN CH ON PA.ID = CH.PID ORDER BY PA.ID, CH.ID",
        ];
        yield return Fix(FxDef, "pos-query-sources-rw", [Cur, "D-CURSOR-1", "CUR-POS", "CUR-POP"], "r-def-cursor-dao/pos-query-*",
            "the fixture was opened read-only, so Updatable read False for every query source; the same shapes opened read-write differ",
            $"For each source [{string.Join(" ;; ", querySources)}], as a dynaset and as a snapshot on a writable Database: read Type, Updatable, Bookmarkable, RecordCount, the Fields Count; then BOF, EOF, AbsolutePosition, PercentPosition and the value of field 0 at the first row; " +
            "MoveLast and read the same and RecordCount; MoveNext (past the end) and read; MovePrevious and read; MovePrevious again; MoveFirst, MovePrevious (before the start) and read; MoveFirst, Move 2 and read; Move -1 and read. Nothing is written. " +
            "Asks the position, count and boundary states and the updatability of a recordset whose source is not a plain table, per requested type, and which requests are refused.",
            p =>
            {
                foreach (string sql in querySources)
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
            }, write: true);

        const string AggSql = "SELECT Count(*) AS Cn, Count(S) AS Cs, Sum(N) AS Sn, Avg(N) AS An, Min(S) AS Ms, Max(D) AS Md, Min(Fl) AS Mf FROM T";
        const string ExprSql = "SELECT N + 1 AS A1, N * 1.5 AS A2, S & 'x' AS A3, IIf(N > 20, 'a', 'b') AS A4, CInt(N) AS A5, CDbl(N) AS A6, Format(D, 'yyyy') AS A7, Now() AS A8, " +
            "Null AS A9, True AS A10, 'lit' AS A11, 7 AS A12, 7.5 AS A13, #1/1/2020# AS A14 FROM T WHERE ID = 1";
        yield return Fix(FxDef, "field-metadata-rw", [Cur, DaoCard, "D-CURSOR-1", "CUR-PROV", "DAO-PROP", "DAO-COLL"], "r-def-cursor-dao/prov-alias-fields-metadata, query-field-metadata-aggregates, query-field-metadata-expressions, recordset-field-properties",
            "the fixture was opened read-only, so DataUpdatable read False for every field",
            "On a writable Database: a dynaset over \"SELECT ID, N AS N1, N AS N2, N + 1 AS Np, S & 'x' AS Sx, T.S FROM T WHERE ID <= 3 ORDER BY ID\": Updatable, and for each field by its own call Name, Type, Size, Attributes, DataUpdatable, SourceTable, SourceField, Required and OrdinalPosition; every row. " +
            $"A snapshot over \"{AggSql}\" and one over \"{ExprSql}\": for each field by its own call the wide member list (the nine above plus CollatingOrder, AllowZeroLength, ValidationRule, ValidationText, DefaultValue, CollectionIndex, ForeignName, Expression, ValidateOnSet); the row. " +
            "For a Field of a dynaset over T (N), of a join (PA.V) and an expression Field (N + 1 AS Np): Properties Count, and each Property's Name, Type, Inherited and Value. Nothing is written. Asks the per-field origin, type and updatability of aliases, expressions, aggregates and join fields on a writable open.",
            p =>
            {
                p.OpenRs("pr", "db", "SELECT ID, N AS N1, N AS N2, N + 1 AS Np, S & 'x' AS Sx, T.S FROM T WHERE ID <= 3 ORDER BY ID", Dao.OpenDynaset).Get("pr", "Updatable");
                FieldMeta(p, "pr", MetaMembers);
                p.ReadAll("pr", 8, 8);
                Close(p, "pr");
                foreach (string sql in new[] { AggSql, ExprSql })
                {
                    p.OpenRs("mr", "db", sql, Dao.OpenSnapshot);
                    FieldMeta(p, "mr", WideMetaMembers);
                    p.ReadRow("mr");
                    Close(p, "mr");
                }
                foreach ((string sql, string field) in new[]
                {
                    ("SELECT ID, N FROM T WHERE ID = 3", "N"),
                    ("SELECT PA.ID, PA.V FROM PA INNER JOIN CH ON PA.ID = CH.PID", "V"),
                    ("SELECT N + 1 AS Np FROM T WHERE ID = 3", "Np"),
                })
                {
                    p.OpenRs("pr", "db", sql, Dao.OpenDynaset).GetBind("pfs", "pr", "Fields").Item("pf", "pfs", A.Str(field)).GetBind("pps", "pf", "Properties").Get("pps", "Count");
                    p.Each("pps", "pp", 40, b => b.Get("pp", "Name").Get("pp", "Type").Get("pp", "Inherited").Get("pp", "Value"));
                    p.Errors().Release("pps").Release("pf").Release("pfs");
                    Close(p, "pr");
                }
            }, write: true);

        yield return Fix(FxDef, "querydef-maxrecords-odbctimeout", [DaoCard, "DAO-QUERY", "DAO-PROP", "CUR-POP"], "r-def-cursor-dao/querydef-maxrecords-effect",
            "QueryTimeout is not a QueryDef member (GetIDsOfNames 0x80020006); the DAO member is ODBCTimeout",
            "A temporary QueryDef over \"SELECT ID, N FROM T ORDER BY ID\": read MaxRecords and ODBCTimeout; for MaxRecords 3, 1, 0, 100 and -1: assign, read it back, and for a snapshot and a dynaset opened from the QueryDef read RecordCount, MoveLast, RecordCount, then close; " +
            "finally assign MaxRecords the string \"abc\" and Null, with Errors read after each. Asks whether MaxRecords limits a local recordset, its accepted values and read-back, and the ODBCTimeout value of a local query.",
            p =>
            {
                p.CallBind("qd", "db", "CreateQueryDef", A.Str(""), A.Str("SELECT ID, N FROM T ORDER BY ID")).Get("qd", "MaxRecords").Get("qd", "ODBCTimeout");
                foreach (int max in new[] { 3, 1, 0, 100, -1 })
                {
                    p.Put("qd", "MaxRecords", A.I4(max)).Get("qd", "MaxRecords");
                    foreach (int type in new[] { Dao.OpenSnapshot, Dao.OpenDynaset })
                    {
                        p.CallBind("mr", "qd", "OpenRecordset", A.I4(type)).Get("mr", "RecordCount").Call("mr", "MoveLast").Get("mr", "RecordCount");
                        Close(p, "mr");
                    }
                }
                p.Put("qd", "MaxRecords", A.Str("abc")).Errors().Put("qd", "MaxRecords", A.Null).Errors().Call("qd", "Close").Release("qd");
            }, write: true);

        (string Source, int Type)[] cases =
        [
            ("T", Dao.OpenTable), ("T", Dao.OpenDynaset), ("T", Dao.OpenSnapshot), ("T", Dao.OpenForwardOnly), ("SELECT N FROM T GROUP BY N", Dao.OpenSnapshot),
        ];
        yield return Fix(FxDef, "assign-without-edit-own-transaction", [Cur, "D-CURSOR-1", "CUR-EDIT", "ERR-BIND"], "r-def-cursor-dao/assign-without-edit-by-type",
            "one transaction wrapped all cases and the table-type run stored N = 77 on ID 1, so later types read 77 after the refused assignment of 77 and could not show whether it applied",
            "For a table-type (Index PK_T), a dynaset, a snapshot and a forward-only recordset over T, and a snapshot over \"SELECT N FROM T GROUP BY N\", each in ITS OWN BeginTrans that is rolled back after the recordset is closed: " +
            "open, hold N, read N (the stored value); with no Edit assign N the value 77, read Errors, the field Value and EditMode; then Edit where it is accepted, assign 77, Update, read Value; Errors; CancelUpdate; close; Rollback; " +
            "then a new snapshot of T reads ID and N for every row. Asks whether and how a Value assignment outside Edit or AddNew is refused per type, and its effect on the buffer, from a state each case shares.",
            p =>
            {
                foreach ((string source, int type) in cases)
                {
                    p.Call("eng", "BeginTrans").OpenRs("ar", "db", source, type);
                    if (type == Dao.OpenTable)
                    {
                        p.Put("ar", "Index", A.Str("PK_T"));
                    }
                    Hold(p, "ar", "n_", "N");
                    p.Get("n_N", "Value").Put("n_N", "Value", A.I4(77)).Errors().Get("n_N", "Value").Get("ar", "EditMode");
                    p.Call("ar", "Edit").Put("n_N", "Value", A.I4(77)).Call("ar", "Update").Get("n_N", "Value");
                    p.Errors().Call("ar", "CancelUpdate");
                    Unhold(p, "n_", "N");
                    Close(p, "ar");
                    p.Call("eng", "Rollback");
                    p.OpenRs("rn", "db", "SELECT ID, N FROM T ORDER BY ID", Dao.OpenSnapshot).ReadAll("rn", 8, 2).Call("rn", "Close").Release("rn");
                }
            }, write: true);

        (string Sql, string Field, Arg Value)[] provSources =
        [
            ("SELECT DISTINCT N FROM T WHERE ID IN (4, 5)", "N", A.I4(41)),
            ("SELECT DISTINCTROW N FROM T WHERE ID IN (4, 5)", "N", A.I4(41)),
            ("SELECT N FROM T WHERE ID IN (4, 5)", "N", A.I4(41)),
            ("SELECT N, Count(*) AS Cn, Sum(ID) AS Si FROM T GROUP BY N", "Cn", A.I4(9)),
            ("SELECT N, Count(*) AS Cn, Sum(ID) AS Si FROM T GROUP BY N", "N", A.I4(41)),
            ("SELECT ID, N FROM T WHERE ID <= 3 UNION SELECT ID + 10, N FROM T WHERE ID > 6", "N", A.I4(99)),
            ("TRANSFORM Count(ID) AS Cn SELECT S FROM T GROUP BY S PIVOT Fl", "S", A.Str("changed")),
        ];
        yield return Fix(FxDef, "prov-sources-errors-per-call", [Cur, "D-CURSOR-1", "CUR-PROV", "CUR-EDIT"], "r-def-cursor-dao/prov-distinct-distinctrow-plain, prov-aggregate-groupby, prov-union-crosstab",
            "an unconditional CancelUpdate followed Update and Errors was read once, so a stale 3020 from an earlier source showed even after a successful Update",
            $"For each source [{string.Join(" ;; ", provSources.Select(s => s.Sql + " (edit " + s.Field + ")"))}]: a marker failure (Database.Execute of 'DROP TABLE MarkerNoSuchTable') so that a stale entry can be recognised; BeginTrans; a dynaset over the source; Updatable and the per-field metadata (Name, Type, Size, Attributes, DataUpdatable, SourceTable, SourceField, Required, OrdinalPosition); " +
            "Edit, Errors; assign the field, Errors; read the field; Update, Errors; EditMode; Requery and read every row; the recordset closed; Rollback. There is no CancelUpdate. Asks the updatability and edit target of DISTINCT, DISTINCTROW, aggregate, UNION and crosstab results, with the Number of each call that fails.",
            p =>
            {
                foreach ((string sql, string field, Arg value) in provSources)
                {
                    p.Call("db", "Execute", A.Str("DROP TABLE MarkerNoSuchTable"));
                    p.Call("eng", "BeginTrans").OpenRs("pr", "db", sql, Dao.OpenDynaset).Get("pr", "Updatable");
                    FieldMeta(p, "pr", MetaMembers);
                    p.GetBind("pfs", "pr", "Fields").Item("pf", "pfs", A.Str(field));
                    p.Call("pr", "Edit").Errors().Put("pf", "Value", value).Errors().Get("pf", "Value").Call("pr", "Update").Errors().Get("pr", "EditMode");
                    p.Call("pr", "Requery").ReadAll("pr", 16, 8);
                    p.Release("pf").Release("pfs");
                    Close(p, "pr");
                    p.Call("eng", "Rollback");
                }
            }, write: true);

        const string LeftJoinSql = "SELECT PA.ID AS PAID, PA.V, CH.ID AS CHID, CH.PID, CH.W FROM PA LEFT JOIN CH ON PA.ID = CH.PID ORDER BY PA.ID, CH.ID";
        yield return Fix(FxDef, "prov-outer-join-cancel-after-failed-update", [Cur, "D-CURSOR-1", "CUR-PROV", "CUR-EDIT"], "r-def-cursor-dao/prov-outer-join-absent-side",
            "no CancelUpdate followed the failed Update, so the next Edit ran with a pending edit",
            $"A dynaset over a LEFT JOIN of PA to CH (\"{LeftJoinSql}\"; parent 3 has no child), inside BeginTrans and rolled back: Updatable, per-field DataUpdatable, SourceTable and SourceField; read all rows; MoveLast (the null-extended row); " +
            "Edit, assign W 'new', Update, Errors, EditMode, then CancelUpdate, Errors, EditMode and read the row; Edit, assign V 'pv3', Update, Errors, EditMode, read the row, CancelUpdate when pending; AddNew, assign W 'added', Update, Errors, EditMode, CancelUpdate; " +
            "Requery and read all rows; then COUNT(*) of CH and of PA. Asks what an edit of the child side of a null-extended row does, whether the parent side and AddNew are accepted, each from a state with no pending edit.",
            p =>
            {
                p.Call("eng", "BeginTrans").OpenRs("pr", "db", LeftJoinSql, Dao.OpenDynaset).Get("pr", "Updatable");
                FieldMeta(p, "pr", ["Name", "DataUpdatable", "SourceTable", "SourceField"]);
                p.ReadAll("pr", 8, 8).Call("pr", "MoveLast").ReadRow("pr");
                p.GetBind("pfs", "pr", "Fields").Item("pw", "pfs", A.Str("W")).Item("pv", "pfs", A.Str("V"));
                p.Call("pr", "Edit").Put("pw", "Value", A.Str("new")).Call("pr", "Update").Errors().Get("pr", "EditMode");
                p.Call("pr", "CancelUpdate").Errors().Get("pr", "EditMode").ReadRow("pr");
                p.Call("pr", "Edit").Put("pv", "Value", A.Str("pv3")).Call("pr", "Update").Errors().Get("pr", "EditMode");
                p.ReadRow("pr").Call("pr", "CancelUpdate").Errors();
                p.Call("pr", "AddNew").Put("pw", "Value", A.Str("added")).Call("pr", "Update").Errors().Get("pr", "EditMode");
                p.Call("pr", "CancelUpdate").Errors().Get("pr", "EditMode");
                p.Call("pr", "Requery").ReadAll("pr", 8, 8);
                p.Release("pw").Release("pv").Release("pfs");
                Close(p, "pr");
                CountOf(p, "CH");
                CountOf(p, "PA");
                p.Call("eng", "Rollback");
            }, write: true);

        yield return Fix(FxDef, "edit-under-commit-second-workspace", [Cur, "D-CURSOR-1", "CUR-EDIT", "CUR-POP", "CUR-LIFE"], "r-def-cursor-dao/edit-under-commit",
            "the second cursor ro shared the session and the transaction, so 'visibility to a second cursor' was same-session only",
            "A second, appended Workspace W2 holds a read-write Database db2 on the same file. A dynaset rs over \"SELECT ID, N FROM T ORDER BY ID\" opened before BeginTrans: BeginTrans; Edit row 1, assign N 11, Update, EditMode; AddNew ID 20 N 200, Update; " +
            "MoveFirst, MoveNext, Delete, read BOF, EOF and ID; RecordCount; a second dynaset ro in the same Workspace read to EOF (the original control); a dynaset rx opened through db2 in W2 and read to EOF, then closed; CommitTrans; " +
            "read rs RecordCount, BOF, EOF, ID and EditMode without Requery; Requery and read all rows; a fresh snapshot in the first Workspace and another through db2 read all rows. Asks the visibility of the edits to a cursor in another Workspace before and after CommitTrans, and the state of the held cursor after the commit.",
            p =>
            {
                p.GetBind("wss", "eng", "Workspaces").CallBind("w2", "eng", "CreateWorkspace", A.Str("W2"), A.Str("admin"), A.Str(""), A.I4(UseJet))
                    .Call("wss", "Append", A.Obj("w2")).CallBind("db2", "w2", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false));
                p.OpenRs("rs", "db", "SELECT ID, N FROM T ORDER BY ID", Dao.OpenDynaset);
                Hold(p, "rs", "e_", "ID", "N");
                p.Call("eng", "BeginTrans").Call("rs", "MoveFirst").Call("rs", "Edit").Put("e_N", "Value", A.I4(11)).Call("rs", "Update").Get("rs", "EditMode");
                p.Call("rs", "AddNew").Put("e_ID", "Value", A.I4(20)).Put("e_N", "Value", A.I4(200)).Call("rs", "Update");
                p.Call("rs", "MoveFirst").Call("rs", "MoveNext").Call("rs", "Delete").Get("rs", "BOF").Get("rs", "EOF");
                Val(p, "rs", "ID").Get("rs", "RecordCount");
                p.OpenRs("ro", "db", "SELECT ID, N FROM T ORDER BY ID", Dao.OpenDynaset).ReadAll("ro", 16, 4).Call("ro", "Close").Release("ro");
                p.OpenRs("rx", "db2", "SELECT ID, N FROM T ORDER BY ID", Dao.OpenDynaset).ReadAll("rx", 16, 4).Call("rx", "Close").Release("rx");
                p.Call("eng", "CommitTrans").Get("rs", "RecordCount").Get("rs", "BOF").Get("rs", "EOF");
                Val(p, "rs", "ID").Get("rs", "EditMode");
                p.Call("rs", "Requery").ReadAll("rs", 16, 4);
                p.OpenRs("rf", "db", "SELECT ID, N FROM T ORDER BY ID", Dao.OpenSnapshot).ReadAll("rf", 16, 4).Call("rf", "Close").Release("rf");
                p.OpenRs("rg", "db2", "SELECT ID, N FROM T ORDER BY ID", Dao.OpenSnapshot).ReadAll("rg", 16, 4).Call("rg", "Close").Release("rg");
                p.Errors();
                Unhold(p, "e_", "ID", "N");
                Close(p, "rs");
                p.Call("db2", "Close").Release("db2").Call("w2", "Close").Release("w2").Release("wss");
            }, write: true);
    }

    // Builders and helpers ------------------------------------------------------------------------------------

    private const string MultiViolation = "INSERT INTO R (ID, Cd, Rq, Vv) VALUES (1, 'r1', Null, -5)";

    /// <summary>A fixture-creating probe: light opening, CreateDatabase of <c>Card-name.accdb</c>, the body, light closing.</summary>
    private static Probe Create(string name, string[] branches, string what, Action<P> body) =>
        new(Card, "create-" + name, [Cur, DaoCard, "D-CURSOR-1", .. branches],
            $"Repair fixture {Card}-{name}: {what}. Created in ACCDB by DBEngine.CreateDatabase(path, dbLangGeneral, dbVersion120) and Database.Execute DDL and DML; the closed file is kept.",
            p =>
            {
                p.EngineLite().CallBind("db", "eng", "CreateDatabase", A.Out($"{Card}-{name}.accdb"), A.Str(Dao.LangGeneral), A.I4(Dao.Version120));
                body(p);
                p.FinishLite("db");
            })
        { CreatesFixture = true };

    /// <summary>
    /// A repair probe on a fresh copy of a fixture opened as <c>db</c>. The description starts with the repaired folder/probe and its
    /// defect, then says exactly what is asked.
    /// </summary>
    private static Probe Fix(string fixture, string name, string[] refs, string repairs, string defect, string asks, Action<P> body, bool write, bool closeDb = true) =>
        new(Card, name, refs,
            $"Repairs {repairs}: {defect}. {asks} The fixture copy is opened {(write ? "read-write" : "read-only")}.",
            p =>
            {
                p.EngineLite().OpenDb(readOnly: !write).Files("after-open");
                body(p);
                if (closeDb)
                {
                    p.FinishLite("db");
                }
                else
                {
                    p.FinishLite();
                }
            })
        { Fixtures = [("main", fixture)] };

    /// <summary>One criteria list run on a dynaset over FU (no index on S) and on FI (IX_FI_S on S): FindFirst, then FindNext, each read in full.</summary>
    private static Probe FindUnindexed(string name, string repairs, string failing, string[] criteria, int nexts, string asks) =>
        Fix(FxFind, name, [Cur, "D-CURSOR-1", "CUR-POS"], repairs,
            $"{failing} gave NoMatch while the same pattern on Q matched, and the NoMatch was not isolated from the IX_S index on S",
            $"On a dynaset over FU (IDs 1-8; N 10,20,30,40,40,50,60,70; S alpha, beta, O'Brien, a\"b, abc, ABC, abd, Null; Q x, Null, x, Null, y, Null, x, y; P the plain values p1-p8; no index but the key) and then on a dynaset over FI (the same rows with a nonunique index on S), " +
            $"for each criterion [{string.Join(" ;; ", criteria)}] (the criteria on P are plain-value controls): FindFirst, read Errors, then FindNext x{nexts} with the same criterion; after each call read NoMatch, BOF, EOF and ID. " + asks,
            p =>
            {
                foreach (string table in new[] { "FU", "FI" })
                {
                    p.OpenRs("rs", "db", table, Dao.OpenDynaset);
                    foreach (string criterion in criteria)
                    {
                        State(p.Call("rs", "FindFirst", A.Str(criterion)).Errors().Get("rs", "NoMatch"), "rs", false);
                        for (int i = 0; i < nexts; i++)
                        {
                            State(p.Call("rs", "FindNext", A.Str(criterion)).Get("rs", "NoMatch"), "rs", false);
                        }
                    }
                    Close(p, "rs");
                }
            }, write: false);

    private static P Sql(P p, string sql) => p.Call("db", "Execute", A.Str(sql));

    private static P Exec(P p, string sql, string db = "db") => p.Call(db, "Execute", A.Str(sql), A.I4(Dao.FailOnError));

    /// <summary>Read one field of the current row through a fresh Fields collection.</summary>
    private static P Val(P p, string rs, string field) =>
        p.GetBind("sf", rs, "Fields").Item("sv", "sf", A.Str(field)).Get("sv", "Value").Release("sv").Release("sf");

    private static P AB(P p, string rs) => Val(Val(p, rs, "A"), rs, "B");

    private static P State(P p, string rs, bool position, string field = "ID")
    {
        p.Get(rs, "BOF").Get(rs, "EOF");
        if (position)
        {
            p.Get(rs, "AbsolutePosition");
        }
        return Val(p, rs, field);
    }

    private static P Close(P p, string rs) => p.Call(rs, "Close").Release(rs);

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

    private static P ReadHeld(P p, string prefix, params string[] fields)
    {
        foreach (string f in fields)
        {
            p.Get(prefix + f, "Value");
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

    private static P Walk(P p, string rs, string step, string until, int max, string[] fields)
    {
        Hold(p, rs, "w_", fields);
        p.Until(rs, until, max, b =>
        {
            foreach (string f in fields)
            {
                b.Get("w_" + f, "Value");
            }
            b.Call(rs, step);
        });
        return Unhold(p, "w_", fields);
    }

    private static P Forward(P p, string rs, int max, params string[] fields) => Walk(p, rs, "MoveNext", "EOF", max, fields);

    private static P Backward(P p, string rs, int max, params string[] fields) => Walk(p, rs, "MovePrevious", "BOF", max, fields);

    /// <summary>Every row of a table through a new snapshot of the named Database, closed again.</summary>
    private static P ReadTable(P p, string db, string table, string order, string bind = "rt") =>
        p.OpenRs(bind, db, $"SELECT * FROM {table} ORDER BY {order}", Dao.OpenSnapshot).ReadAll(bind, 16, 16).Call(bind, "Close").Release(bind);

    /// <summary>The rows of G through a new snapshot on each of db and db2.</summary>
    private static P StoredG(P p)
    {
        foreach (string db in new[] { "db", "db2" })
        {
            p.OpenRs("rn", db, "SELECT ID, T, N, U FROM G ORDER BY ID", Dao.OpenSnapshot).ReadAll("rn", 8, 4).Call("rn", "Close").Release("rn");
        }
        return p;
    }

    /// <summary>The rows of P2 and C2 through new snapshots.</summary>
    private static P BaseTables(P p)
    {
        p.OpenRs("rn", "db", "SELECT ID, N FROM P2 ORDER BY ID", Dao.OpenSnapshot).ReadAll("rn", 8, 2).Call("rn", "Close").Release("rn");
        return p.OpenRs("rn", "db", "SELECT ID, PID, V FROM C2 ORDER BY ID", Dao.OpenSnapshot).ReadAll("rn", 8, 3).Call("rn", "Close").Release("rn");
    }

    /// <summary>A snapshot of T opened and closed again: the successful call between two passes, holding no lock afterwards.</summary>
    private static P OpenCloseSnapshot(P p) =>
        p.OpenRs("sr", "db", "SELECT ID, N FROM T ORDER BY ID", Dao.OpenSnapshot).Call("sr", "Close").Release("sr");

    private static P CountOf(P p, string table) =>
        p.OpenRs("cn", "db", $"SELECT COUNT(*) FROM {table}", Dao.OpenSnapshot).ReadRow("cn").Call("cn", "Close").Release("cn");

    private static P Names(P p, string collection, string bind, int max) =>
        p.Each(collection, bind, max, b => b.Get(bind, "Name"));

    private static P IndexNames(P p, string table, string tag) =>
        p.GetBind("xds" + tag, "db", "TableDefs").Call("xds" + tag, "Refresh").Item("xt" + tag, "xds" + tag, A.Str(table)).GetBind("xix" + tag, "xt" + tag, "Indexes")
            .Call("xix" + tag, "Refresh").Get("xix" + tag, "Count").Each("xix" + tag, "xi" + tag, 16, b => b.Get("xi" + tag, "Name"))
            .Release("xix" + tag).Release("xt" + tag).Release("xds" + tag);

    private static P FieldNames(P p, string table, string tag) =>
        p.GetBind("yds" + tag, "db", "TableDefs").Call("yds" + tag, "Refresh").Item("yt" + tag, "yds" + tag, A.Str(table)).GetBind("yfl" + tag, "yt" + tag, "Fields")
            .Call("yfl" + tag, "Refresh").Get("yfl" + tag, "Count").Each("yfl" + tag, "yf" + tag, 24, b => b.Get("yf" + tag, "Name"))
            .Release("yfl" + tag).Release("yt" + tag).Release("yds" + tag);

    private static P WalkState(P p) =>
        p.Get("rs", "BOF").Get("rs", "EOF").Get("rs", "AbsolutePosition").Get("rs", "PercentPosition").Get("w0", "Value");

    /// <summary>For each Field of the recordset, each named member by its own call.</summary>
    private static P FieldMeta(P p, string rs, string[] members) =>
        p.GetBind("mfs", rs, "Fields").Each("mfs", "mf", 24, b =>
        {
            foreach (string member in members)
            {
                b.Get("mf", member);
            }
        }).Release("mfs");

    /// <summary>Read ADODB Connection.Errors: Count, then each entry with its provider fields.</summary>
    private static P AdoErrs(P p) =>
        p.GetBind("aerrs", "conn", "Errors")
            .Each("aerrs", "ae", 16, b => b.Get("ae", "Number").Get("ae", "Description").Get("ae", "Source").Get("ae", "SQLState").Get("ae", "NativeError"))
            .Release("aerrs");
}
