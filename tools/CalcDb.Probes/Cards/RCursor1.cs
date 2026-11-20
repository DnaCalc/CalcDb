namespace CalcDb.Probes.Cards;

/// <summary>
/// R-CURSOR-1: identity, population and cursor transitions (A-02), public DAO surface only. Three ACCDB
/// fixtures are created through ACE DAO (nav: small navigation, seek and lifetime tables; pages: multi-page
/// tables with deleted page ranges and a long duplicate key; edit: generated, default, required, validated and
/// unique fields plus a growable table). The other probes open a fresh copy and ask one transition question each:
/// CUR-POS, CUR-EMPTY-PAGES, CUR-POP, CUR-ID, CUR-SEEK, CUR-EDIT and CUR-LIFE. Every read (RecordCount,
/// AbsolutePosition, Bookmark, Field.Value) is itself an operation of the sequence, never a free observer
/// (R-CURSOR-1 CUR-POP). Probes ask; they hold no expected answers.
/// </summary>
public sealed class RCursor1 : ICard
{
    private const string Card = "r-cursor-1";
    private const string CardId = "R-CURSOR-1";

    private static readonly string X200 = new('x', 200);
    private static readonly string K200 = new('k', 200);
    private static readonly string G255 = new('g', 255);

    private static readonly (string Name, int Type)[] Types =
        [("table", Dao.OpenTable), ("dynaset", Dao.OpenDynaset), ("snapshot", Dao.OpenSnapshot), ("forwardonly", Dao.OpenForwardOnly)];

    /// <summary>CUR-POS movement script on five rows: invalid moves repeated, then recovery moves.</summary>
    private static readonly (string Member, int? Count)[] MoveScript =
    [
        ("MovePrevious", null), ("MovePrevious", null), ("MoveNext", null), ("MoveNext", null), ("MoveLast", null),
        ("MoveNext", null), ("MoveNext", null), ("MovePrevious", null), ("Move", 0), ("Move", -2), ("Move", 10),
        ("Move", 0), ("Move", -1), ("Move", -10), ("Move", 1), ("MoveFirst", null), ("Move", -1), ("Move", 0),
    ];

    /// <summary>CUR-POS movement script for the empty and singleton boundaries.</summary>
    private static readonly (string Member, int? Count)[] BoundaryScript =
    [
        ("MoveNext", null), ("MovePrevious", null), ("MoveFirst", null), ("MoveLast", null), ("Move", 0), ("Move", 1), ("Move", -1),
    ];

    private static readonly string[] SeekOps = ["<", "<=", "=", ">=", ">", "<>"];
    private static readonly string[] BoundaryTables = ["E", "S"];
    private static readonly string[] EndMoves = ["MoveFirst", "MoveLast", "MoveNext", "MovePrevious"];
    private static readonly string[] CIndexes = ["PK_C", "IX_AA"];
    private static readonly string[] DIndexes = ["IX_K", "IX_KN"];
    private static readonly bool[] UFirst = [true, false];
    private static readonly string[] RequeryCursors = ["rd", "rn"];

    public IEnumerable<Probe> Probes() =>
        [.. Fixtures(), .. Position(), .. EmptyPages(), .. Population(), .. Identity(), .. Seeks(), .. Edits(), .. Lifetime()];

    // Fixtures ----------------------------------------------------------------------------------------------

    private static IEnumerable<Probe> Fixtures()
    {
        yield return Creation("nav", ["CUR-POS", "CUR-POP", "CUR-ID", "CUR-SEEK", "CUR-LIFE"],
            "T(ID LONG primary key PK_T, K LONG, V TEXT(20)) with rows ID 1-5, K = 10*ID, V 'a'-'e'; E(ID primary key PK_E, V) with no rows; " +
            "S(ID primary key PK_S, V) with one row; C(A LONG, B LONG, V TEXT(10)) with primary unique index PK_C (A ASC, B DESC), unique index " +
            "IX_AA (A ASC, B ASC) and rows (1,10),(1,20),(1,30),(2,10); D(ID primary key PK_D, K LONG) with nonunique index IX_K (K), " +
            "index IX_KN (K) WITH IGNORE NULL and rows (1,1),(2,2),(3,2),(4,2),(5,3),(6,Null)",
            p =>
            {
                Sql(p, "CREATE TABLE T (ID LONG CONSTRAINT PK_T PRIMARY KEY, K LONG, V TEXT(20))");
                for (int i = 1; i <= 5; i++)
                {
                    Sql(p, $"INSERT INTO T VALUES ({i}, {i * 10}, '{(char)('a' + i - 1)}')");
                }
                Sql(p, "CREATE TABLE E (ID LONG CONSTRAINT PK_E PRIMARY KEY, V TEXT(20))");
                Sql(p, "CREATE TABLE S (ID LONG CONSTRAINT PK_S PRIMARY KEY, V TEXT(20))");
                Sql(p, "INSERT INTO S VALUES (1, 'only')");
                Sql(p, "CREATE TABLE C (A LONG NOT NULL, B LONG NOT NULL, V TEXT(10))");
                Sql(p, "CREATE UNIQUE INDEX PK_C ON C (A ASC, B DESC) WITH PRIMARY");
                Sql(p, "CREATE UNIQUE INDEX IX_AA ON C (A ASC, B ASC)");
                Sql(p, "INSERT INTO C VALUES (1, 10, 'p')");
                Sql(p, "INSERT INTO C VALUES (1, 20, 'q')");
                Sql(p, "INSERT INTO C VALUES (1, 30, 'r')");
                Sql(p, "INSERT INTO C VALUES (2, 10, 's')");
                Sql(p, "CREATE TABLE D (ID LONG CONSTRAINT PK_D PRIMARY KEY, K LONG)");
                Sql(p, "CREATE INDEX IX_K ON D (K)");
                Sql(p, "CREATE INDEX IX_KN ON D (K) WITH IGNORE NULL");
                foreach ((int id, string k) in new[] { (1, "1"), (2, "2"), (3, "2"), (4, "2"), (5, "3"), (6, "Null") })
                {
                    Sql(p, $"INSERT INTO D VALUES ({id}, {k})");
                }
            });

        yield return Creation("pages", ["CUR-EMPTY-PAGES", "CUR-POP", "CUR-SEEK"],
            "M(ID LONG primary key PK_M, V TEXT(255)) filled to IDs 1-512 with a 200-character V by repeated INSERT ... SELECT doubling, then " +
            "IDs 1-100, 200-320 and 450-512 deleted; MA(ID primary key PK_MA, V TEXT(255)) filled the same way to 256 rows and then all deleted; " +
            "DL(ID LONG primary key PK_DL, K TEXT(200)) with nonunique index IX_K (K), IDs 1-256 sharing one 200-character K, plus (1001,'a') and (1002,'z')",
            p =>
            {
                Sql(p, "CREATE TABLE M (ID LONG CONSTRAINT PK_M PRIMARY KEY, V TEXT(255))");
                Sql(p, $"INSERT INTO M VALUES (1, '{X200}')");
                Doubling(p, "M", "V", 512);
                Sql(p, "DELETE FROM M WHERE ID <= 100");
                Sql(p, "DELETE FROM M WHERE ID BETWEEN 200 AND 320");
                Sql(p, "DELETE FROM M WHERE ID >= 450");
                Sql(p, "CREATE TABLE MA (ID LONG CONSTRAINT PK_MA PRIMARY KEY, V TEXT(255))");
                Sql(p, $"INSERT INTO MA VALUES (1, '{X200}')");
                Doubling(p, "MA", "V", 256);
                Sql(p, "DELETE FROM MA");
                Sql(p, "CREATE TABLE DL (ID LONG CONSTRAINT PK_DL PRIMARY KEY, K TEXT(200))");
                Sql(p, "CREATE INDEX IX_K ON DL (K)");
                Sql(p, $"INSERT INTO DL VALUES (1, '{K200}')");
                Doubling(p, "DL", "K", 256);
                Sql(p, "INSERT INTO DL VALUES (1001, 'a')");
                Sql(p, "INSERT INTO DL VALUES (1002, 'z')");
            });

        yield return Creation("edit", ["CUR-EDIT", "CUR-ID"],
            "G(ID COUNTER primary key PK_G, T TEXT(10) NOT NULL, N LONG, U TEXT(10)) with unique index UX_U (U), N.DefaultValue \"5\", " +
            "U.ValidationRule \"<>'bad'\", table ValidationRule \"[N] < 1000\" (all set through DAO TableDef/Field properties) and rows " +
            "('r1', 1, 'u1'), ('r2', 2, 'u2'); R(ID LONG primary key PK_R, V TEXT(255)) with IDs 1-64 and V 'r'",
            p =>
            {
                Sql(p, "CREATE TABLE G (ID COUNTER CONSTRAINT PK_G PRIMARY KEY, T TEXT(10) NOT NULL, N LONG, U TEXT(10))");
                Sql(p, "CREATE UNIQUE INDEX UX_U ON G (U)");
                p.GetBind("tdfs", "db", "TableDefs").Item("tdf", "tdfs", A.Str("G")).GetBind("fl", "tdf", "Fields")
                    .Item("fn", "fl", A.Str("N")).Put("fn", "DefaultValue", A.Str("5")).Release("fn")
                    .Item("fu", "fl", A.Str("U")).Put("fu", "ValidationRule", A.Str("<>'bad'")).Put("fu", "ValidationText", A.Str("U must not be bad")).Release("fu")
                    .Put("tdf", "ValidationRule", A.Str("[N] < 1000")).Put("tdf", "ValidationText", A.Str("N must be below 1000"))
                    .Release("fl").Release("tdf").Release("tdfs");
                Sql(p, "INSERT INTO G (T, N, U) VALUES ('r1', 1, 'u1')");
                Sql(p, "INSERT INTO G (T, N, U) VALUES ('r2', 2, 'u2')");
                Sql(p, "CREATE TABLE R (ID LONG CONSTRAINT PK_R PRIMARY KEY, V TEXT(255))");
                Sql(p, "INSERT INTO R VALUES (1, 'r')");
                Doubling(p, "R", "V", 64);
            });
    }

    // CUR-POS -----------------------------------------------------------------------------------------------

    private static IEnumerable<Probe> Position()
    {
        string script = Script(MoveScript);
        foreach ((string tn, int type) in Types)
        {
            bool pos = type != Dao.OpenTable;
            yield return On("nav", $"pos-moves-{tn}", ["CUR-POS"],
                $"Open a {tn}-type recordset on table T (IDs 1-5, no Index set), then call in order: {script}. After the open and after every call read " +
                $"BOF, EOF{(pos ? ", AbsolutePosition" : "")} and Fields(\"ID\").Value. Asks the position, BOF/EOF transition and error of each move, " +
                "including repeated invalid moves and the recovery moves after them.",
                p =>
                {
                    p.OpenRs("rs", "db", "T", type);
                    Run(p, "rs", MoveScript, pos);
                    Close(p, "rs");
                });
        }

        yield return On("nav", "pos-empty-singleton", ["CUR-POS", "CUR-POP"],
            $"For table E (no rows) and then table S (one row), open in turn a table, dynaset, snapshot and forward-only recordset and call {Script(BoundaryScript)}; " +
            "read BOF, EOF, AbsolutePosition (not on the table type) and Fields(\"ID\").Value after the open and after every call, and RecordCount at the end. " +
            "Asks the empty and singleton boundary transitions per recordset type.",
            p =>
            {
                foreach (string table in BoundaryTables)
                {
                    foreach ((_, int type) in Types)
                    {
                        p.OpenRs("rs", "db", table, type);
                        Run(p, "rs", BoundaryScript, type != Dao.OpenTable);
                        p.Get("rs", "RecordCount");
                        Close(p, "rs");
                    }
                }
            });

        yield return On("nav", "pos-delete-ends", ["CUR-POS", "CUR-EDIT"],
            "On a dynaset over T at its first row: Delete, MoveNext, MovePrevious, MoveLast, Delete, MoveNext, MovePrevious, Move 0, reading BOF, EOF, " +
            "AbsolutePosition and Fields(\"ID\").Value after each call, then RecordCount and the IDs from MoveFirst to EOF. Then the same sequence on a " +
            "table-type recordset with Index PK_T over the remaining rows (without AbsolutePosition). Asks the current-row state after deleting the first " +
            "and the last row, and the recovery moves.",
            p =>
            {
                p.OpenRs("rd", "db", "T", Dao.OpenDynaset);
                DeleteEnds(p, "rd", true);
                p.OpenRs("rt", "db", "T", Dao.OpenTable).Put("rt", "Index", A.Str("PK_T")).Call("rt", "MoveFirst");
                DeleteEnds(p, "rt", false);
            }, write: true);

        yield return On("nav", "pos-find", ["CUR-POS"],
            "On a dynaset over T: FindFirst \"K = 30\", FindNext \"K = 30\", MoveNext, FindLast \"K < 30\", FindPrevious \"K < 0\", MovePrevious, " +
            "FindFirst \"K = = 1\" (malformed), FindFirst with a Null argument, Move 0, reading NoMatch, BOF, EOF, AbsolutePosition and Fields(\"ID\").Value " +
            "after each call. Then FindFirst \"V = 'c'\" on a snapshot, a table-type and a forward-only recordset, read the same way. Asks NoMatch and the " +
            "position after successful and failed Find calls, and which types accept Find.",
            p =>
            {
                p.OpenRs("rs", "db", "T", Dao.OpenDynaset);
                foreach ((string member, Arg? arg) in new (string, Arg?)[]
                {
                    ("FindFirst", A.Str("K = 30")), ("FindNext", A.Str("K = 30")), ("MoveNext", null), ("FindLast", A.Str("K < 30")),
                    ("FindPrevious", A.Str("K < 0")), ("MovePrevious", null), ("FindFirst", A.Str("K = = 1")), ("FindFirst", A.Null), ("Move", A.I4(0)),
                })
                {
                    if (arg is null)
                    {
                        p.Call("rs", member);
                    }
                    else
                    {
                        p.Call("rs", member, arg);
                    }
                    p.Get("rs", "NoMatch");
                    State(p, "rs", true);
                }
                Close(p, "rs");
                foreach (int type in new[] { Dao.OpenSnapshot, Dao.OpenTable, Dao.OpenForwardOnly })
                {
                    p.OpenRs("rs", "db", "T", type).Call("rs", "FindFirst", A.Str("V = 'c'")).Get("rs", "NoMatch");
                    State(p, "rs", type != Dao.OpenTable);
                    Close(p, "rs");
                }
            });
    }

    // CUR-EMPTY-PAGES ---------------------------------------------------------------------------------------

    private static IEnumerable<Probe> EmptyPages()
    {
        foreach ((string tn, int type, string order) in new[] { ("table", Dao.OpenTable, " with no Index (storage order)"), ("dynaset", Dao.OpenDynaset, "") })
        {
            yield return On("pages", $"pages-traverse-{tn}", ["CUR-EMPTY-PAGES", "CUR-POS", "T-ISAM-43"],
                "Table M held IDs 1-512 (200-character rows over many pages) and then lost IDs 1-100, 200-320 and 450-512, leaving empty first, consecutive " +
                $"middle and last page ranges. Open a {tn}-type recordset on M{order}, read RecordCount, read ID through a held Field at each row by MoveNext " +
                "until EOF (at most 600), read BOF and EOF, then MoveLast and read ID by MovePrevious until BOF (at most 600), then BOF, EOF and RecordCount. " +
                "Asks the visited row sequence and the boundary reports in both directions across empty pages.",
                p =>
                {
                    p.OpenRs("rs", "db", "M", type).Get("rs", "RecordCount");
                    Forward(p, "rs", 600, "ID");
                    p.Get("rs", "BOF").Get("rs", "EOF").Call("rs", "MoveLast");
                    Backward(p, "rs", 600, "ID");
                    p.Get("rs", "BOF").Get("rs", "EOF").Get("rs", "RecordCount");
                    Close(p, "rs");
                });
        }

        yield return On("pages", "pages-all-deleted", ["CUR-EMPTY-PAGES", "CUR-POS", "T-ISAM-43"],
            "Table MA had 256 rows over many pages, all deleted. For a table-type recordset without Index, one with Index PK_MA, a dynaset, a snapshot " +
            "and a forward-only recordset in turn: open MA, read BOF, EOF and RecordCount, then call MoveFirst, MoveLast, MoveNext and MovePrevious " +
            "reading BOF, EOF and Fields(\"ID\").Value after each. Asks whether a table whose pages hold only deleted rows reports empty in every type.",
            p =>
            {
                foreach ((int type, string? index) in new (int, string?)[]
                {
                    (Dao.OpenTable, null), (Dao.OpenTable, "PK_MA"), (Dao.OpenDynaset, null), (Dao.OpenSnapshot, null), (Dao.OpenForwardOnly, null),
                })
                {
                    p.OpenRs("rs", "db", "MA", type);
                    if (index is not null)
                    {
                        p.Put("rs", "Index", A.Str(index));
                    }
                    p.Get("rs", "BOF").Get("rs", "EOF").Get("rs", "RecordCount");
                    foreach (string move in EndMoves)
                    {
                        p.Call("rs", move);
                        State(p, "rs", false);
                    }
                    Close(p, "rs");
                }
            });
    }

    // CUR-POP -----------------------------------------------------------------------------------------------

    private static IEnumerable<Probe> Population()
    {
        foreach ((string tn, int type) in Types)
        {
            yield return On("pages", $"pop-count-{tn}", ["CUR-POP", "CUR-POS"],
                $"Open a {tn}-type recordset on M (the multi-page table with deleted ranges) and read RecordCount at once; then Fields(\"ID\").Value and " +
                "RecordCount; MoveNext, RecordCount; Move 50, RecordCount, AbsolutePosition, PercentPosition; MoveLast, RecordCount, AbsolutePosition, " +
                "PercentPosition; MovePrevious, RecordCount; MoveFirst, RecordCount; Requery, RecordCount; MoveLast, RecordCount. Asks how RecordCount " +
                "and position progress with population for this type; each read is an operation of the sequence.",
                p =>
                {
                    p.OpenRs("rs", "db", "M", type).Get("rs", "RecordCount");
                    Val(p, "rs", "ID").Get("rs", "RecordCount")
                        .Call("rs", "MoveNext").Get("rs", "RecordCount")
                        .Call("rs", "Move", A.I4(50)).Get("rs", "RecordCount").Get("rs", "AbsolutePosition").Get("rs", "PercentPosition")
                        .Call("rs", "MoveLast").Get("rs", "RecordCount").Get("rs", "AbsolutePosition").Get("rs", "PercentPosition")
                        .Call("rs", "MovePrevious").Get("rs", "RecordCount")
                        .Call("rs", "MoveFirst").Get("rs", "RecordCount")
                        .Call("rs", "Requery").Get("rs", "RecordCount")
                        .Call("rs", "MoveLast").Get("rs", "RecordCount");
                    Close(p, "rs");
                });
        }

        yield return On("pages", "pop-count-minimal-dynaset", ["CUR-POP"],
            "Open a dynaset on M, call MoveLast with no earlier read, then read RecordCount once. The minimally observed counterpart of " +
            "pop-count-dynaset on an independent copy: asks whether the earlier reads there change the count reported after MoveLast.",
            p =>
            {
                p.OpenRs("rs", "db", "M", Dao.OpenDynaset).Call("rs", "MoveLast").Get("rs", "RecordCount");
                Close(p, "rs");
            });

        foreach ((string tn, int type) in new[] { ("dynaset", Dao.OpenDynaset), ("snapshot", Dao.OpenSnapshot) })
        {
            yield return On("nav", $"pop-visibility-{tn}", ["CUR-POP", "CUR-ID"],
                $"Open cursor A, a {tn} on T, read RecordCount and the first row's ID and K through held Fields. Through cursor B, a second dynaset on T " +
                "in the same Database: FindFirst/Edit/Update K = 111 on visited row ID 1 and K = 444 on unvisited row ID 4, FindFirst/Delete unvisited " +
                "row ID 3, AddNew/Update row (6, 60, 'f'); close B. Then on A: RecordCount, walk ID and K by MoveNext to EOF from the current row, " +
                "RecordCount; MoveFirst and walk again; Requery, RecordCount and walk again. Asks which membership and value changes made through " +
                "another cursor A shows, before and after Requery.",
                p =>
                {
                    p.OpenRs("ra", "db", "T", type).Get("ra", "RecordCount");
                    Hold(p, "ra", "a_", "ID", "K").Get("a_ID", "Value").Get("a_K", "Value");
                    p.OpenRs("rb", "db", "T", Dao.OpenDynaset);
                    Hold(p, "rb", "b_", "ID", "K", "V")
                        .Call("rb", "FindFirst", A.Str("ID = 1")).Call("rb", "Edit").Put("b_K", "Value", A.I4(111)).Call("rb", "Update")
                        .Call("rb", "FindFirst", A.Str("ID = 4")).Call("rb", "Edit").Put("b_K", "Value", A.I4(444)).Call("rb", "Update")
                        .Call("rb", "FindFirst", A.Str("ID = 3")).Call("rb", "Delete")
                        .Call("rb", "AddNew").Put("b_ID", "Value", A.I4(6)).Put("b_K", "Value", A.I4(60)).Put("b_V", "Value", A.Str("f")).Call("rb", "Update");
                    Unhold(p, "b_", "ID", "K", "V");
                    Close(p, "rb");
                    p.Get("ra", "RecordCount");
                    Forward(p, "ra", 16, "ID", "K");
                    p.Get("ra", "RecordCount").Call("ra", "MoveFirst");
                    Forward(p, "ra", 16, "ID", "K");
                    p.Call("ra", "Requery").Get("ra", "RecordCount");
                    Forward(p, "ra", 16, "ID", "K");
                    Unhold(p, "a_", "ID", "K");
                    Close(p, "ra");
                }, write: true);
        }
    }

    // CUR-ID ------------------------------------------------------------------------------------------------

    private static IEnumerable<Probe> Identity()
    {
        yield return On("nav", "id-bookmark-shape", ["CUR-ID", "CUR-POS"],
            "For a table, dynaset, snapshot and forward-only recordset on T in turn: read Bookmarkable, Bookmark at the first row, MoveNext, Bookmark, " +
            "MoveFirst, Bookmark, LastModified. Then on a dynaset at ID 2, assign Bookmark the constants: empty byte array, four zero bytes, empty " +
            "string, Long 1, Null and Empty, reading BOF, EOF, AbsolutePosition and Fields(\"ID\").Value after each. Asks the exact bookmark VARIANT " +
            "per type, whether revisiting a row returns equal bytes, and how malformed bookmarks are refused.",
            p =>
            {
                foreach ((_, int type) in Types)
                {
                    p.OpenRs("rs", "db", "T", type).Get("rs", "Bookmarkable").Get("rs", "Bookmark")
                        .Call("rs", "MoveNext").Get("rs", "Bookmark").Call("rs", "MoveFirst").Get("rs", "Bookmark").Get("rs", "LastModified");
                    Close(p, "rs");
                }
                p.OpenRs("rs", "db", "T", Dao.OpenDynaset).Call("rs", "MoveNext");
                foreach (Arg bad in new[] { A.Bytes(), A.Bytes(0, 0, 0, 0), A.Str(""), A.I4(1), A.Null, A.Empty })
                {
                    p.Put("rs", "Bookmark", bad);
                    State(p, "rs", true);
                }
                Close(p, "rs");
            });

        yield return On("nav", "id-field-held", ["CUR-ID", "CUR-POP"],
            "Open a dynaset and a snapshot on T, Move 1 on each (ID 2) and hold Fields(\"V\") of each; read both. Then db.Execute UPDATE T SET " +
            "V = 'changed' WHERE ID = 2 (dbFailOnError); read both held Fields; Move 0 on each and read; MoveNext then MovePrevious on each and read " +
            "V and ID. Asks whether a held Field shows an external change to the current row, and which move refreshes it, per type.",
            p =>
            {
                p.OpenRs("rd", "db", "T", Dao.OpenDynaset).Call("rd", "Move", A.I4(1));
                p.OpenRs("rn", "db", "T", Dao.OpenSnapshot).Call("rn", "Move", A.I4(1));
                Hold(p, "rd", "d_", "V").Get("d_V", "Value");
                Hold(p, "rn", "n_", "V").Get("n_V", "Value");
                Exec(p, "UPDATE T SET V = 'changed' WHERE ID = 2");
                p.Get("d_V", "Value").Get("n_V", "Value")
                    .Call("rd", "Move", A.I4(0)).Get("d_V", "Value").Call("rn", "Move", A.I4(0)).Get("n_V", "Value")
                    .Call("rd", "MoveNext").Call("rd", "MovePrevious").Get("d_V", "Value")
                    .Call("rn", "MoveNext").Call("rn", "MovePrevious").Get("n_V", "Value");
                Val(p, "rd", "ID");
                Val(p, "rn", "ID");
                Unhold(p, "d_", "V");
                Unhold(p, "n_", "V");
                Close(p, "rd");
                Close(p, "rn");
            }, write: true);

        yield return On("nav", "id-index-switch", ["CUR-ID", "CUR-SEEK"],
            "On a table-type recordset over C with Index PK_C (A ASC, B DESC): MoveFirst, MoveNext, hold Fields A and B and read them and Bookmark. " +
            "Set Index IX_AA (A ASC, B ASC): read BOF, EOF, A, B, Bookmark; MoveNext and read A, B. Set Index \"NO_SUCH_INDEX\": read BOF, EOF, A, B. " +
            "Set Index PK_C again: read A, B; MoveNext and read A, B. Asks the current row, held Field values and bookmark across an index switch.",
            p =>
            {
                p.OpenRs("rt", "db", "C", Dao.OpenTable).Put("rt", "Index", A.Str("PK_C")).Call("rt", "MoveFirst").Call("rt", "MoveNext");
                Hold(p, "rt", "t_", "A", "B").Get("t_A", "Value").Get("t_B", "Value").Get("rt", "Bookmark")
                    .Put("rt", "Index", A.Str("IX_AA")).Get("rt", "BOF").Get("rt", "EOF").Get("t_A", "Value").Get("t_B", "Value").Get("rt", "Bookmark")
                    .Call("rt", "MoveNext").Get("t_A", "Value").Get("t_B", "Value")
                    .Put("rt", "Index", A.Str("NO_SUCH_INDEX")).Get("rt", "BOF").Get("rt", "EOF").Get("t_A", "Value").Get("t_B", "Value")
                    .Put("rt", "Index", A.Str("PK_C")).Get("t_A", "Value").Get("t_B", "Value")
                    .Call("rt", "MoveNext").Get("t_A", "Value").Get("t_B", "Value");
                Unhold(p, "t_", "A", "B");
                Close(p, "rt");
            });

        yield return On("edit", "id-relocation", ["CUR-ID"],
            "On a dynaset over R (64 short rows): FindFirst \"ID = 20\", read Bookmark and the held Fields ID and V. Then db.Execute (dbFailOnError) " +
            "set V to a 255-character value on IDs 15-25 except 20, and then on ID 20, to make the row grow without a key change. Read the held ID and " +
            "V, Bookmark; Move 0 and read them and Bookmark; MoveNext and read ID; MovePrevious twice and read ID. The closed file is kept so that a " +
            "later inspector can say whether the row body moved. Asks whether the cursor's identity and bookmark survive row growth.",
            p =>
            {
                p.OpenRs("rd", "db", "R", Dao.OpenDynaset).Call("rd", "FindFirst", A.Str("ID = 20")).Get("rd", "Bookmark");
                Hold(p, "rd", "r_", "ID", "V").Get("r_ID", "Value").Get("r_V", "Value");
                Exec(p, $"UPDATE R SET V = '{G255}' WHERE ID BETWEEN 15 AND 25 AND ID <> 20");
                Exec(p, $"UPDATE R SET V = '{G255}' WHERE ID = 20");
                p.Get("r_ID", "Value").Get("r_V", "Value").Get("rd", "Bookmark")
                    .Call("rd", "Move", A.I4(0)).Get("r_ID", "Value").Get("r_V", "Value").Get("rd", "Bookmark")
                    .Call("rd", "MoveNext").Get("r_ID", "Value").Call("rd", "MovePrevious").Call("rd", "MovePrevious").Get("r_ID", "Value");
                Unhold(p, "r_", "ID", "V");
                Close(p, "rd");
            }, write: true, keepFile: true);

        yield return On("nav", "id-key-change", ["CUR-ID"],
            "Position a dynaset over T at ID 2 (FindFirst) and read Bookmark, and a table-type recordset with Index PK_T at ID 2 (Seek \"=\", 2); hold " +
            "Fields ID and K of each. db.Execute UPDATE T SET ID = 20 WHERE ID = 2 (dbFailOnError). Dynaset: read ID, K, Bookmark; Move 0 and read; " +
            "MoveNext and read ID; MoveFirst and walk ID to EOF. Table type: read ID, K; MoveNext and read ID; MovePrevious and read ID. Asks whether " +
            "a cursor follows a row whose key moved, per type.",
            p =>
            {
                p.OpenRs("rd", "db", "T", Dao.OpenDynaset).Call("rd", "FindFirst", A.Str("ID = 2")).Get("rd", "Bookmark");
                Hold(p, "rd", "d_", "ID", "K");
                p.OpenRs("rt", "db", "T", Dao.OpenTable).Put("rt", "Index", A.Str("PK_T")).Call("rt", "Seek", A.Str("="), A.I4(2));
                Hold(p, "rt", "t_", "ID", "K");
                Exec(p, "UPDATE T SET ID = 20 WHERE ID = 2");
                p.Get("d_ID", "Value").Get("d_K", "Value").Get("rd", "Bookmark")
                    .Call("rd", "Move", A.I4(0)).Get("d_ID", "Value").Get("d_K", "Value")
                    .Call("rd", "MoveNext").Get("d_ID", "Value").Call("rd", "MoveFirst");
                Forward(p, "rd", 16, "ID");
                p.Get("t_ID", "Value").Get("t_K", "Value").Call("rt", "MoveNext").Get("t_ID", "Value").Call("rt", "MovePrevious").Get("t_ID", "Value");
                Unhold(p, "d_", "ID", "K");
                Unhold(p, "t_", "ID", "K");
                Close(p, "rd");
                Close(p, "rt");
            }, write: true);

        yield return On("nav", "id-delete-reinsert", ["CUR-ID"],
            "Position a dynaset over T at ID 3 (FindFirst), read Bookmark and hold Fields ID, K, V. db.Execute (dbFailOnError) DELETE FROM T WHERE " +
            "ID = 3, then INSERT INTO T VALUES (3, 33, 'reinserted'). Read the held ID, K, V and Bookmark; Move 0 and read them; MoveNext, MovePrevious " +
            "and read them; MoveFirst and walk ID, K to EOF. Asks whether the cursor treats the reinserted row with the same key as the deleted one.",
            p =>
            {
                p.OpenRs("rd", "db", "T", Dao.OpenDynaset).Call("rd", "FindFirst", A.Str("ID = 3")).Get("rd", "Bookmark");
                Hold(p, "rd", "d_", "ID", "K", "V");
                Exec(p, "DELETE FROM T WHERE ID = 3");
                Exec(p, "INSERT INTO T VALUES (3, 33, 'reinserted')");
                p.Get("d_ID", "Value").Get("d_K", "Value").Get("d_V", "Value").Get("rd", "Bookmark")
                    .Call("rd", "Move", A.I4(0)).Get("d_ID", "Value").Get("d_K", "Value").Get("d_V", "Value")
                    .Call("rd", "MoveNext").Call("rd", "MovePrevious").Get("d_ID", "Value").Get("d_K", "Value").Get("d_V", "Value")
                    .Call("rd", "MoveFirst");
                Forward(p, "rd", 16, "ID", "K");
                Unhold(p, "d_", "ID", "K", "V");
                Close(p, "rd");
            }, write: true);
    }

    // CUR-SEEK ----------------------------------------------------------------------------------------------

    private static IEnumerable<Probe> Seeks()
    {
        yield return On("nav", "seek-operators", ["CUR-SEEK", "CUR-POS"],
            $"Table C holds (1,10),(1,20),(1,30),(2,10). For index PK_C (A ASC, B DESC) and then IX_AA (A ASC, B ASC), and for each operator " +
            $"{string.Join(" ", SeekOps)}: open a fresh table-type recordset, set Index, Seek op, 1, 20; read NoMatch, A, B; MoveNext and read A, B; " +
            "MovePrevious twice and read A, B. Asks which row each operator selects on each index direction and the following moves in both directions.",
            p =>
            {
                foreach (string index in CIndexes)
                {
                    foreach (string op in SeekOps)
                    {
                        p.OpenRs("rt", "db", "C", Dao.OpenTable).Put("rt", "Index", A.Str(index)).Call("rt", "Seek", A.Str(op), A.I4(1), A.I4(20)).Get("rt", "NoMatch");
                        AB(p, "rt").Call("rt", "MoveNext");
                        AB(p, "rt").Call("rt", "MovePrevious").Call("rt", "MovePrevious");
                        AB(p, "rt");
                        Close(p, "rt");
                    }
                }
            });

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
        yield return On("nav", "seek-arguments", ["CUR-SEEK", "CUR-POS"],
            $"On fresh table-type recordsets over C with Index PK_C (A ASC, B DESC), Seek with each argument shape: {string.Join("; ", shapes.Select(s => s.Label))}; " +
            "after each read NoMatch, A and B, MoveNext and read A and B. Then on one recordset Seek \"=\", 9, 9 (no such key), read NoMatch, BOF, EOF, " +
            "A; Seek \"=\", 1, 30, read NoMatch, A, B; MovePrevious and read A, B. Then Seek \"=\", 1, 20 on a table-type recordset with no Index set and " +
            "on a dynaset. Asks the omission, Null, coercion and wrong-type policy, recovery after a failed Seek, and the refusal cases.",
            p =>
            {
                foreach ((_, Arg[] args) in shapes)
                {
                    p.OpenRs("rt", "db", "C", Dao.OpenTable).Put("rt", "Index", A.Str("PK_C")).Call("rt", "Seek", args).Get("rt", "NoMatch");
                    AB(p, "rt").Call("rt", "MoveNext");
                    AB(p, "rt");
                    Close(p, "rt");
                }
                p.OpenRs("rt", "db", "C", Dao.OpenTable).Put("rt", "Index", A.Str("PK_C"))
                    .Call("rt", "Seek", A.Str("="), A.I4(9), A.I4(9)).Get("rt", "NoMatch").Get("rt", "BOF").Get("rt", "EOF");
                Val(p, "rt", "A").Call("rt", "Seek", A.Str("="), A.I4(1), A.I4(30)).Get("rt", "NoMatch");
                AB(p, "rt").Call("rt", "MovePrevious");
                AB(p, "rt");
                Close(p, "rt");
                p.OpenRs("rt", "db", "C", Dao.OpenTable).Call("rt", "Seek", A.Str("="), A.I4(1), A.I4(20)).Get("rt", "NoMatch");
                Close(p, "rt");
                p.OpenRs("rd", "db", "C", Dao.OpenDynaset).Call("rd", "Seek", A.Str("="), A.I4(1), A.I4(20)).Get("rd", "NoMatch");
                Close(p, "rd");
            });

        string[] dupOps = ["=", ">=", ">", "<=", "<"];
        yield return On("nav", "seek-duplicates", ["CUR-SEEK"],
            $"Table D has K values 1,2,2,2,3,Null for IDs 1-6. For each operator {string.Join(" ", dupOps)} with key 2 on Index IX_K: fresh table-type " +
            "recordset, Seek, read NoMatch, ID, K; MoveNext and read ID; MovePrevious twice and read ID. Then Seek \"=\", Null on IX_K and on IX_KN " +
            "(WITH IGNORE NULL), reading NoMatch and ID; then on IX_KN MoveFirst and walk ID to EOF. Asks which duplicate each operator selects and " +
            "how Null keys and an ignore-null index take part.",
            p =>
            {
                foreach (string op in dupOps)
                {
                    p.OpenRs("rt", "db", "D", Dao.OpenTable).Put("rt", "Index", A.Str("IX_K")).Call("rt", "Seek", A.Str(op), A.I4(2)).Get("rt", "NoMatch");
                    Val(p, "rt", "ID");
                    Val(p, "rt", "K").Call("rt", "MoveNext");
                    Val(p, "rt", "ID").Call("rt", "MovePrevious").Call("rt", "MovePrevious");
                    Val(p, "rt", "ID");
                    Close(p, "rt");
                }
                foreach (string index in DIndexes)
                {
                    p.OpenRs("rt", "db", "D", Dao.OpenTable).Put("rt", "Index", A.Str(index)).Call("rt", "Seek", A.Str("="), A.Null).Get("rt", "NoMatch");
                    Val(p, "rt", "ID");
                    if (index == "IX_KN")
                    {
                        p.Call("rt", "MoveFirst");
                        Forward(p, "rt", 16, "ID");
                    }
                    Close(p, "rt");
                }
            });

        yield return On("pages", "seek-duplicates-multileaf", ["CUR-SEEK"],
            $"Table DL has 256 rows (IDs 1-256) sharing one 200-character K over several index pages, plus (1001,'a') and (1002,'z'). For each operator " +
            $"{string.Join(" ", dupOps)} with that long K on Index IX_K: fresh table-type recordset, Seek, read NoMatch and ID; MoveNext and read ID; " +
            "MovePrevious twice and read ID. Asks which member of a duplicate group spanning index leaves each operator selects, and its neighbours.",
            p =>
            {
                foreach (string op in dupOps)
                {
                    p.OpenRs("rt", "db", "DL", Dao.OpenTable).Put("rt", "Index", A.Str("IX_K")).Call("rt", "Seek", A.Str(op), A.Str(K200)).Get("rt", "NoMatch");
                    Val(p, "rt", "ID").Call("rt", "MoveNext");
                    Val(p, "rt", "ID").Call("rt", "MovePrevious").Call("rt", "MovePrevious");
                    Val(p, "rt", "ID");
                    Close(p, "rt");
                }
            });
    }

    // CUR-EDIT ----------------------------------------------------------------------------------------------

    private static readonly string[] GFields = ["ID", "T", "N", "U"];

    private static IEnumerable<Probe> Edits()
    {
        yield return On("edit", "edit-buffer-lifecycle", ["CUR-EDIT"],
            "On a dynaset over G at row 'r1' with held Fields ID, T, N, U: read EditMode and U; Edit, EditMode, U; set U 'new1', read U; Edit again, " +
            "EditMode, U; CancelUpdate, EditMode, U; CancelUpdate again; Edit, set U 'new2', Update, EditMode, U, LastModified, Bookmark; Update again " +
            "with no Edit, DBEngine.Errors, EditMode, U; Move 0 and read U. Asks the copy-buffer lifecycle of Edit, repeated Edit, CancelUpdate and a " +
            "second Update.",
            p =>
            {
                p.OpenRs("rs", "db", "G", Dao.OpenDynaset);
                Hold(p, "rs", "g_", GFields)
                    .Get("rs", "EditMode").Get("g_U", "Value")
                    .Call("rs", "Edit").Get("rs", "EditMode").Get("g_U", "Value")
                    .Put("g_U", "Value", A.Str("new1")).Get("g_U", "Value")
                    .Call("rs", "Edit").Get("rs", "EditMode").Get("g_U", "Value")
                    .Call("rs", "CancelUpdate").Get("rs", "EditMode").Get("g_U", "Value")
                    .Call("rs", "CancelUpdate")
                    .Call("rs", "Edit").Put("g_U", "Value", A.Str("new2")).Call("rs", "Update")
                    .Get("rs", "EditMode").Get("g_U", "Value").Get("rs", "LastModified").Get("rs", "Bookmark")
                    .Call("rs", "Update").Errors().Get("rs", "EditMode").Get("g_U", "Value")
                    .Call("rs", "Move", A.I4(0)).Get("g_U", "Value");
                Unhold(p, "g_", GFields);
                Close(p, "rs");
            }, write: true);

        yield return On("edit", "edit-addnew-generated", ["CUR-EDIT", "CUR-ID"],
            "On a dynaset over G with held Fields ID, T, N, U at row 'r1': read all four; AddNew, EditMode, read all four (generated ID, default N, unset " +
            "T, U); set T 'a1', read ID and N; CancelUpdate, EditMode, read all four; AddNew, read ID; set T 'a2' and U 'ua2'; Update; EditMode, read " +
            "all four, LastModified; MoveLast and read all four; then MoveFirst and walk all fields to EOF. Asks the AddNew buffer values, generated " +
            "and default value timing, whether a cancelled AddNew consumes a counter, and the current row after AddNew/Update.",
            p =>
            {
                p.OpenRs("rs", "db", "G", Dao.OpenDynaset);
                Hold(p, "rs", "g_", GFields);
                ReadHeld(p, "g_", GFields).Call("rs", "AddNew").Get("rs", "EditMode");
                ReadHeld(p, "g_", GFields).Put("g_T", "Value", A.Str("a1")).Get("g_ID", "Value").Get("g_N", "Value")
                    .Call("rs", "CancelUpdate").Get("rs", "EditMode");
                ReadHeld(p, "g_", GFields).Call("rs", "AddNew").Get("g_ID", "Value")
                    .Put("g_T", "Value", A.Str("a2")).Put("g_U", "Value", A.Str("ua2")).Call("rs", "Update").Get("rs", "EditMode");
                ReadHeld(p, "g_", GFields).Get("rs", "LastModified").Call("rs", "MoveLast");
                ReadHeld(p, "g_", GFields).Call("rs", "MoveFirst");
                Forward(p, "rs", 16, GFields);
                Unhold(p, "g_", GFields);
                Close(p, "rs");
            }, write: true);

        yield return On("edit", "edit-move-close-buffered", ["CUR-EDIT", "CUR-LIFE"],
            "On a dynaset over G with held Field U: Edit row 'r1', set U 'moved', MoveNext, read EditMode, MovePrevious and read U; AddNew, set T 'z', " +
            "MoveFirst, read EditMode and U; Edit, set U 'closed', Close the recordset with the edit pending. Then open a snapshot on G and walk all " +
            "fields to EOF. Asks whether movement and Close discard or post a pending Edit or AddNew.",
            p =>
            {
                p.OpenRs("rs", "db", "G", Dao.OpenDynaset);
                Hold(p, "rs", "g_", "T", "U")
                    .Call("rs", "Edit").Put("g_U", "Value", A.Str("moved")).Call("rs", "MoveNext").Get("rs", "EditMode")
                    .Call("rs", "MovePrevious").Get("g_U", "Value")
                    .Call("rs", "AddNew").Put("g_T", "Value", A.Str("z")).Call("rs", "MoveFirst").Get("rs", "EditMode").Get("g_U", "Value")
                    .Call("rs", "Edit").Put("g_U", "Value", A.Str("closed"));
                Unhold(p, "g_", "T", "U");
                Close(p, "rs");
                p.OpenRs("rn", "db", "G", Dao.OpenSnapshot);
                Forward(p, "rn", 16, GFields);
                Close(p, "rn");
            }, write: true);

        yield return On("edit", "edit-validation-order", ["CUR-EDIT"],
            "G has T required, U with rule <>'bad' and a unique index, and table rule [N] < 1000. Sequence 1 on a dynaset with held Fields: AddNew, set U " +
            "'bad', read U and EditMode; Update (T unset and U bad), Errors, EditMode; set T 't'; Update, Errors; set U 'u1' (duplicate); Update, Errors; " +
            "set U 'u9' and N 2000; Update, Errors; set N 3; Update, Errors, read ID. Sequences 2 and 3 on fresh dynasets: AddNew, then U 'bad' and T " +
            "Null, or T Null and U 'bad'; Update, Errors, CancelUpdate. Sequence 4: set the recordset Field U ValidateOnSet True, Edit, set U 'bad', " +
            "Errors, EditMode, CancelUpdate. Asks the check order, which check runs at assignment and which at Update.",
            p =>
            {
                p.OpenRs("rs", "db", "G", Dao.OpenDynaset);
                Hold(p, "rs", "g_", GFields)
                    .Call("rs", "AddNew").Put("g_U", "Value", A.Str("bad")).Get("g_U", "Value").Get("rs", "EditMode")
                    .Call("rs", "Update").Errors().Get("rs", "EditMode")
                    .Put("g_T", "Value", A.Str("t")).Call("rs", "Update").Errors()
                    .Put("g_U", "Value", A.Str("u1")).Call("rs", "Update").Errors()
                    .Put("g_U", "Value", A.Str("u9")).Put("g_N", "Value", A.I4(2000)).Call("rs", "Update").Errors()
                    .Put("g_N", "Value", A.I4(3)).Call("rs", "Update").Errors().Get("g_ID", "Value");
                Unhold(p, "g_", GFields);
                Close(p, "rs");
                foreach (bool uFirst in UFirst)
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

        yield return On("edit", "edit-conflict-optimistic", ["CUR-EDIT", "CUR-POP"],
            "Open a second Database on the same file. Cursor A: dynaset on G in db with LockEdits False; cursor B: dynaset on G in db2. (a) Same field: " +
            "A Edit row 'r1', set U 'optA'; B Edit row 'r1', set U 'optB', Update; A Update, Errors, EditMode, U; A Update again, Errors, U; A Move 0, " +
            "U. (b) Other field: both MoveNext to 'r2'; A Edit, set U 'optC'; B Edit, set N 77, Update; A Update, Errors, EditMode, U, N; Move 0, U, N. " +
            "Asks optimistic conflict detection per field overlap, the buffer after a refused Update and a second explicit Update.",
            p =>
            {
                p.OpenDb(bind: "db2", readOnly: false);
                p.OpenRs("ra", "db", "G", Dao.OpenDynaset).Put("ra", "LockEdits", A.Bool(false));
                p.OpenRs("rb", "db2", "G", Dao.OpenDynaset);
                Hold(p, "ra", "a_", "N", "U");
                Hold(p, "rb", "b_", "N", "U")
                    .Call("ra", "Edit").Put("a_U", "Value", A.Str("optA"))
                    .Call("rb", "Edit").Put("b_U", "Value", A.Str("optB")).Call("rb", "Update")
                    .Call("ra", "Update").Errors().Get("ra", "EditMode").Get("a_U", "Value")
                    .Call("ra", "Update").Errors().Get("a_U", "Value")
                    .Call("ra", "Move", A.I4(0)).Get("a_U", "Value")
                    .Call("ra", "MoveNext").Call("rb", "MoveNext")
                    .Call("ra", "Edit").Put("a_U", "Value", A.Str("optC"))
                    .Call("rb", "Edit").Put("b_N", "Value", A.I4(77)).Call("rb", "Update")
                    .Call("ra", "Update").Errors().Get("ra", "EditMode").Get("a_U", "Value").Get("a_N", "Value")
                    .Call("ra", "Move", A.I4(0)).Get("a_U", "Value").Get("a_N", "Value");
                Unhold(p, "a_", "N", "U");
                Unhold(p, "b_", "N", "U");
                Close(p, "ra");
                Close(p, "rb");
                p.Call("db2", "Close").Release("db2");
            }, write: true);

        yield return On("edit", "edit-conflict-pessimistic", ["CUR-EDIT"],
            "Open a second Database on the same file. Cursor A: dynaset on G in db with LockEdits True; cursor B: dynaset on G in db2. A Edit row 'r1', " +
            "set U 'pesA'; B Edit row 'r1', Errors, EditMode; B MoveNext and Edit row 'r2' (same page), Errors, EditMode, CancelUpdate; A Update, Errors; " +
            "B MovePrevious, Edit, set U 'pesB', Update, Errors, read U. Asks what a pessimistic edit locks and when the lock is released.",
            p =>
            {
                p.OpenDb(bind: "db2", readOnly: false);
                p.OpenRs("ra", "db", "G", Dao.OpenDynaset).Put("ra", "LockEdits", A.Bool(true));
                p.OpenRs("rb", "db2", "G", Dao.OpenDynaset);
                Hold(p, "ra", "a_", "U");
                Hold(p, "rb", "b_", "U")
                    .Call("ra", "Edit").Put("a_U", "Value", A.Str("pesA"))
                    .Call("rb", "Edit").Errors().Get("rb", "EditMode")
                    .Call("rb", "MoveNext").Call("rb", "Edit").Errors().Get("rb", "EditMode").Call("rb", "CancelUpdate")
                    .Call("ra", "Update").Errors()
                    .Call("rb", "MovePrevious").Call("rb", "Edit").Put("b_U", "Value", A.Str("pesB")).Call("rb", "Update").Errors().Get("b_U", "Value");
                Unhold(p, "a_", "U");
                Unhold(p, "b_", "U");
                Close(p, "ra");
                Close(p, "rb");
                p.Call("db2", "Close").Release("db2");
            }, write: true);

        yield return On("edit", "edit-cancel-rollback", ["CUR-EDIT", "CUR-ID", "CUR-LIFE"],
            "DBEngine.BeginTrans; on a dynaset over G with held Field U: Edit row 'r1', set U 'A', Update, read LastModified and Bookmark; MoveNext, " +
            "Edit, set U 'B', CancelUpdate; DBEngine.Rollback. Then on the same recordset: EditMode, U; Move 0, U; MoveFirst, U, LastModified. Close " +
            "the recordset and the Database, reopen the file read-only and walk G. Asks how a rollback after a successful edit and a cancelled edit " +
            "affects the open cursor and the stored rows.",
            p =>
            {
                p.Call("eng", "BeginTrans");
                p.OpenRs("rs", "db", "G", Dao.OpenDynaset);
                Hold(p, "rs", "g_", "U")
                    .Call("rs", "Edit").Put("g_U", "Value", A.Str("A")).Call("rs", "Update").Get("rs", "LastModified").Get("rs", "Bookmark")
                    .Call("rs", "MoveNext").Call("rs", "Edit").Put("g_U", "Value", A.Str("B")).Call("rs", "CancelUpdate")
                    .Call("eng", "Rollback")
                    .Get("rs", "EditMode").Get("g_U", "Value")
                    .Call("rs", "Move", A.I4(0)).Get("g_U", "Value")
                    .Call("rs", "MoveFirst").Get("g_U", "Value").Get("rs", "LastModified");
                Unhold(p, "g_", "U");
                Close(p, "rs");
                p.Call("db", "Close").Release("db").OpenDb();
                p.OpenRs("rn", "db", "G", Dao.OpenSnapshot);
                Forward(p, "rn", 16, GFields);
                Close(p, "rn");
            }, write: true);

        // Discriminators for edit-cancel-rollback (TXN-CLOSE, 7htc): after eng.Rollback that dynaset answered 3420 to
        // Field.Value and Move and a null LastModified. Each variant changes one element: no change in the level, only a
        // cancelled edit, the recordset opened before BeginTrans, a table-type recordset, CommitTrans instead of Rollback.
        foreach ((string name, string what, bool update, bool cancel, bool before, int type, string resolve) in new[]
        {
            ("edit-rollback-no-change", "no edit in the level", false, false, false, Dao.OpenDynaset, "Rollback"),
            ("edit-rollback-cancel-only", "only an Edit, a put and CancelUpdate in the level", false, true, false, Dao.OpenDynaset, "Rollback"),
            ("edit-rollback-opened-before", "the dynaset opened before BeginTrans", true, true, true, Dao.OpenDynaset, "Rollback"),
            ("edit-rollback-table", "a table-type recordset", true, true, false, Dao.OpenTable, "Rollback"),
            ("edit-commit-opened-inside", "CommitTrans instead of Rollback", true, true, false, Dao.OpenDynaset, "CommitTrans"),
        })
        {
            yield return On("edit", name, ["CUR-EDIT", "CUR-LIFE", "TXN-CLOSE"],
                $"Discriminator for edit-cancel-rollback, which it repeats except for {what}: DBEngine.BeginTrans and a recordset over G " +
                "with held Field U (Edit row 'r1', set U 'A', Update, LastModified; MoveNext, Edit, set U 'B', CancelUpdate, as the variant has " +
                $"them); DBEngine.{resolve}. Then EditMode, U; Move 0, U; MoveFirst, U, LastModified, Bookmark; Close; reopen and walk G. Asks " +
                "which element makes the recordset answer 3420 after the resolution.",
                p =>
                {
                    if (before)
                    {
                        p.OpenRs("rs", "db", "G", type);
                    }
                    p.Call("eng", "BeginTrans");
                    if (!before)
                    {
                        p.OpenRs("rs", "db", "G", type);
                    }
                    Hold(p, "rs", "g_", "U");
                    if (update)
                    {
                        p.Call("rs", "Edit").Put("g_U", "Value", A.Str("A")).Call("rs", "Update").Get("rs", "LastModified");
                    }
                    if (cancel)
                    {
                        p.Call("rs", "MoveNext").Call("rs", "Edit").Put("g_U", "Value", A.Str("B")).Call("rs", "CancelUpdate");
                    }
                    p.Call("eng", resolve)
                        .Get("rs", "EditMode").Get("g_U", "Value")
                        .Call("rs", "Move", A.I4(0)).Get("g_U", "Value")
                        .Call("rs", "MoveFirst").Get("g_U", "Value").Get("rs", "LastModified").Get("rs", "Bookmark");
                    Unhold(p, "g_", "U");
                    Close(p, "rs");
                    p.Call("db", "Close").Release("db").OpenDb();
                    p.OpenRs("rn", "db", "G", Dao.OpenSnapshot);
                    Forward(p, "rn", 16, GFields);
                    Close(p, "rn");
                }, write: true);
        }
    }

    // CUR-LIFE ----------------------------------------------------------------------------------------------

    private static IEnumerable<Probe> Lifetime()
    {
        yield return On("nav", "life-clone", ["CUR-LIFE", "CUR-ID"],
            "Dynaset P over T: MoveNext twice, read ID, Clone into C; read C's BOF, EOF, AbsolutePosition, ID and Bookmark, and P's Bookmark; C " +
            "MoveFirst, read P's ID; C Edit, set V 'clone', Update; read LastModified of P and C; Close P; C MoveNext, read ID and RecordCount; Close C. " +
            "Then dynaset Q, Clone into Q2 at once, read Q2 RecordCount, Q2 MoveLast, read Q's and Q2's RecordCount. Asks the clone's initial position, " +
            "shared versus private state, and survival after the parent closes.",
            p =>
            {
                p.OpenRs("rp", "db", "T", Dao.OpenDynaset).Call("rp", "MoveNext").Call("rp", "MoveNext");
                Val(p, "rp", "ID").CallBind("rc", "rp", "Clone");
                State(p, "rc", true).Get("rc", "Bookmark").Get("rp", "Bookmark").Call("rc", "MoveFirst");
                Val(p, "rp", "ID");
                Hold(p, "rc", "c_", "V").Call("rc", "Edit").Put("c_V", "Value", A.Str("clone")).Call("rc", "Update");
                Unhold(p, "c_", "V").Get("rp", "LastModified").Get("rc", "LastModified");
                Close(p, "rp");
                p.Call("rc", "MoveNext");
                Val(p, "rc", "ID").Get("rc", "RecordCount");
                Close(p, "rc");
                p.OpenRs("rq", "db", "T", Dao.OpenDynaset).CallBind("rq2", "rq", "Clone").Get("rq2", "RecordCount")
                    .Call("rq2", "MoveLast").Get("rq", "RecordCount").Get("rq2", "RecordCount");
                Close(p, "rq2");
                Close(p, "rq");
            }, write: true);

        yield return On("nav", "life-close-held", ["CUR-LIFE"],
            "Dynaset R over T: hold Fields and Fields(\"V\"), read V; Close R; read the held Field's Value and Name, the Fields Count, R.EOF; call " +
            "R.MoveNext and R.Close again; release R and read the held Field's Value again. Then dynaset S with a held Field V: Close the Database with " +
            "S open; read S.EOF, call S.MoveNext, read the held Field's Value. Asks what held recordset, Fields and Field objects do after Close of " +
            "their recordset or Database and after release.",
            p =>
            {
                p.OpenRs("rs", "db", "T", Dao.OpenDynaset).GetBind("flds", "rs", "Fields").Item("f", "flds", A.Str("V")).Get("f", "Value")
                    .Call("rs", "Close").Get("f", "Value").Get("f", "Name").Get("flds", "Count").Get("rs", "EOF")
                    .Call("rs", "MoveNext").Call("rs", "Close").Release("rs").Get("f", "Value").Release("f").Release("flds");
                p.OpenRs("rs2", "db", "T", Dao.OpenDynaset);
                Hold(p, "rs2", "s_", "V").Call("db", "Close").Get("rs2", "EOF").Call("rs2", "MoveNext").Get("s_V", "Value");
                Unhold(p, "s_", "V").Release("rs2").Release("db");
            }, write: false, closeDb: false);

        yield return On("nav", "life-clone-close-transaction", ["CUR-LIFE", "CUR-EDIT"],
            "DBEngine.BeginTrans; dynaset R over T: FindFirst \"ID = 1\", Edit, set V 'txn', Update; Clone into C and Close C; read R's V; " +
            "DBEngine.CommitTrans; Close R and the Database, reopen read-only and walk ID, V of T. Asks whether closing a clone inside a transaction " +
            "affects the transaction or the parent's edit.",
            p =>
            {
                p.Call("eng", "BeginTrans");
                p.OpenRs("rs", "db", "T", Dao.OpenDynaset).Call("rs", "FindFirst", A.Str("ID = 1"));
                Hold(p, "rs", "r_", "V").Call("rs", "Edit").Put("r_V", "Value", A.Str("txn")).Call("rs", "Update")
                    .CallBind("rc", "rs", "Clone").Call("rc", "Close").Release("rc").Get("r_V", "Value")
                    .Call("eng", "CommitTrans").Errors();
                Unhold(p, "r_", "V");
                Close(p, "rs");
                p.Call("db", "Close").Release("db").OpenDb();
                p.OpenRs("rn", "db", "T", Dao.OpenSnapshot);
                Forward(p, "rn", 16, "ID", "V");
                Close(p, "rn");
            }, write: true);

        yield return On("nav", "life-filter-sort", ["CUR-LIFE", "CUR-POP"],
            "Dynaset R over T: set Filter \"K > 20\" and Sort \"K DESC\", read them back, walk ID from the current row to EOF; R.OpenRecordset() with no " +
            "arguments into R2, read R2's RecordCount after MoveLast, MoveFirst and walk ID; R.Requery, MoveFirst and walk ID. Then set Filter on a " +
            "table-type recordset. Asks whether Filter and Sort change the live recordset, the derived recordset, or the requeried one.",
            p =>
            {
                p.OpenRs("rs", "db", "T", Dao.OpenDynaset).Put("rs", "Filter", A.Str("K > 20")).Put("rs", "Sort", A.Str("K DESC"))
                    .Get("rs", "Filter").Get("rs", "Sort");
                Forward(p, "rs", 16, "ID");
                p.CallBind("rs2", "rs", "OpenRecordset").Call("rs2", "MoveLast").Get("rs2", "RecordCount").Call("rs2", "MoveFirst");
                Forward(p, "rs2", 16, "ID");
                Close(p, "rs2");
                p.Call("rs", "Requery").Call("rs", "MoveFirst");
                Forward(p, "rs", 16, "ID");
                Close(p, "rs");
                p.OpenRs("rt", "db", "T", Dao.OpenTable).Put("rt", "Filter", A.Str("K > 20")).Get("rt", "Filter");
                Close(p, "rt");
            });

        yield return On("nav", "life-requery", ["CUR-LIFE", "CUR-POP", "CUR-ID"],
            "A dynaset and a snapshot over T, each Move 2 (ID 3) with held Field K; read Bookmark of each. db.Execute (dbFailOnError) UPDATE T SET " +
            "K = 333 WHERE ID = 3 and INSERT INTO T VALUES (7, 70, 'g'). Requery each; then for each read BOF, EOF, AbsolutePosition, ID, the held K, " +
            "Bookmark, LastModified, RecordCount, and RecordCount after MoveLast. Then Requery on a table-type recordset. Asks the position, held " +
            "Field, bookmark and population after Requery.",
            p =>
            {
                foreach (string rs in RequeryCursors)
                {
                    p.OpenRs(rs, "db", "T", rs == "rd" ? Dao.OpenDynaset : Dao.OpenSnapshot).Call(rs, "Move", A.I4(2)).Get(rs, "Bookmark");
                    Hold(p, rs, rs + "_", "K");
                }
                Exec(p, "UPDATE T SET K = 333 WHERE ID = 3");
                Exec(p, "INSERT INTO T VALUES (7, 70, 'g')");
                foreach (string rs in RequeryCursors)
                {
                    p.Call(rs, "Requery");
                    State(p, rs, true).Get(rs + "_K", "Value").Get(rs, "Bookmark").Get(rs, "LastModified").Get(rs, "RecordCount")
                        .Call(rs, "MoveLast").Get(rs, "RecordCount");
                    Unhold(p, rs + "_", "K");
                    Close(p, rs);
                }
                p.OpenRs("rt", "db", "T", Dao.OpenTable).Call("rt", "Requery");
                Close(p, "rt");
            }, write: true);
    }

    // Helpers -----------------------------------------------------------------------------------------------

    private static Probe Creation(string name, string[] branches, string what, Action<P> body) =>
        new(Card, "create-" + name, [CardId, "D-CURSOR-1", .. branches],
            $"R-CURSOR-1 fixture {Card}-{name}: {what}. Created in ACCDB by DBEngine.CreateDatabase(path, dbLangGeneral, dbVersion120) and " +
            "db.Execute DDL/DML; the closed file is kept.",
            p =>
            {
                p.Modules("before-create").Create().Modules("after-create").Get("eng", "Version")
                    .CallBind("db", "eng", "CreateDatabase", A.Out($"{Card}-{name}.accdb"), A.Str(Dao.LangGeneral), A.I4(Dao.Version120))
                    .Get("db", "Version");
                body(p);
                p.Call("db", "Close").Release("db").Release("eng").Modules("after-close").Files("after-close");
            })
        { CreatesFixture = true };

    /// <summary>A probe on a fresh copy of a fixture: standard opening, the body, standard closing.</summary>
    private static Probe On(string fixture, string name, string[] branches, string what, Action<P> body,
        bool write = false, bool closeDb = true, bool keepFile = false) =>
        new(Card, name, [CardId, "D-CURSOR-1", .. branches],
            what + (write ? " The fixture copy is opened read-write." : " The fixture copy is opened read-only."),
            p =>
            {
                p.Engine().OpenDb(readOnly: !write).Files("after-open");
                body(p);
                if (closeDb)
                {
                    p.Finish("db");
                }
                else
                {
                    p.Finish();
                }
                if (keepFile)
                {
                    p.Files("kept-after-close", keep: true);
                }
            })
        { Fixtures = [("main", $"{Card}-{fixture}")] };

    private static string Script((string Member, int? Count)[] script) =>
        string.Join(", ", script.Select(m => m.Count is int n ? $"Move {n}" : m.Member));

    /// <summary>Run a movement script, reading the state after the open and after every call.</summary>
    private static void Run(P p, string rs, (string Member, int? Count)[] script, bool position)
    {
        State(p, rs, position);
        foreach ((string member, int? count) in script)
        {
            if (count is int n)
            {
                p.Call(rs, "Move", A.I4(n));
            }
            else
            {
                p.Call(rs, member);
            }
            State(p, rs, position);
        }
    }

    private static void DeleteEnds(P p, string rs, bool position)
    {
        State(p, rs, position);
        foreach (string member in new[] { "Delete", "MoveNext", "MovePrevious", "MoveLast", "Delete", "MoveNext", "MovePrevious" })
        {
            p.Call(rs, member);
            State(p, rs, position);
        }
        p.Call(rs, "Move", A.I4(0));
        State(p, rs, position).Get(rs, "RecordCount").Call(rs, "MoveFirst");
        Forward(p, rs, 16, "ID");
        Close(p, rs);
    }

    private static P Sql(P p, string sql) => p.Call("db", "Execute", A.Str(sql));

    private static P Exec(P p, string sql) => p.Call("db", "Execute", A.Str(sql), A.I4(Dao.FailOnError));

    /// <summary>Fill a table holding row ID 1 to <paramref name="rows"/> rows by INSERT ... SELECT doubling.</summary>
    private static void Doubling(P p, string table, string column, int rows)
    {
        for (int k = 1; k < rows; k *= 2)
        {
            Sql(p, $"INSERT INTO {table} (ID, {column}) SELECT ID + {k}, {column} FROM {table}");
        }
    }

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

    private static P Close(P p, string rs) => p.Call(rs, "Close").Release(rs);
}
