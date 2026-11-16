namespace CalcDb.Probes.Cards;

/// <summary>
/// R-FMT-2 (rows, long values, allocation and native mutations) and R-PRESERVE-1 (opaque objects, protected
/// bytes and shared references), the ACE-answerable parts. Fixtures are created through ACE DAO. Each probe
/// keeps the fixture copy as given ("before"), then runs one or more steps; a step opens the database
/// read/write, makes exactly one mutation, closes it and keeps the closed file ("after-..." images), so each
/// image shows one mutation. Ladders are several such steps in one probe, used where the question is a
/// boundary (page, slot, long-value form, index leaf) crossed by repeated single mutations. Public
/// projections (typed rows, field sizes, index statistics, catalog views) are captured beside the images.
/// The probes ask; they do not judge. Interpreting the bytes is the inspector's work, not the probe's.
/// </summary>
public sealed class RFmt2Preserve1 : ICard
{
    private const string Card = "r-fmt-2-pres-1";
    private const string WorldAccdb = Card + "-world-accdb";
    private const string WorldMdb = Card + "-world-mdb";
    private const string PresAccdb = Card + "-pres-accdb";

    private const string Images = " Images: 'before' (the fixture copy as given), then one kept image of the closed file after each step; every step opens the database read/write, makes one mutation and closes it.";

    public IEnumerable<Probe> Probes()
    {
        yield return Fixture("world-accdb", "accdb", Dao.Version120, preserve: false);
        yield return Fixture("world-mdb", "mdb", Dao.Version40, preserve: false);
        yield return Fixture("pres-accdb", "accdb", Dao.Version120, preserve: true);
        foreach (Probe p in Fmt2())
        {
            yield return p;
        }
        foreach (Probe p in Preserve())
        {
            yield return p;
        }
    }

    // ------------------------------------------------------------------------------------------------
    // Fixtures

    private static Probe Fixture(string name, string ext, int version, bool preserve) =>
        new(Card, "create-" + name, preserve ? ["R-PRESERVE-1", "R-FMT-2", "II-04 preservation cases", "II-06 catalog/property discrimination"] : ["R-FMT-2", "R-PRESERVE-1"],
            preserve
                ? $"Fixture ({ext.ToUpperInvariant()}): the R-FMT-2 world tables plus objects the engine itself creates and a front end would also carry: saved queries (select, parameter, action, join), a foreign-key relationship, database/table/field user and Description properties, and the engine's system tables and containers. Created by DBEngine.CreateDatabase(path, dbLangGeneral, {version}); the closed file is kept."
                : $"Fixture ({ext.ToUpperInvariant()}): the R-FMT-2 world, small separate tables each prepared for one mutation family (rows, dense variable rows, page growth, indexes, a 598-entry key table, long values, a one-page Boolean table, Yes/No-only tables, alter-history, text grammar, scalar admission, constraints), a Nums helper, no saved queries and no properties. Created by DBEngine.CreateDatabase(path, dbLangGeneral, {version}); the closed file is kept.",
            p =>
            {
                p.Modules("before-create").Create().Modules("after-create")
                    .CallBind("db", "eng", "CreateDatabase", A.Out($"{Card}-{name}.{ext}"), A.Str(Dao.LangGeneral), A.I4(version)).Get("db", "Version");
                World(p);
                if (preserve)
                {
                    Extras(p);
                }
                p.Call("db", "Close").Release("db").Release("eng").Modules("after-close").Files("after-close", keep: true);
            })
        { CreatesFixture = true };

    private static void World(P p)
    {
        // Nums: 1..1024 by doubling, the row source of every bulk fill below.
        Exec(p, "CREATE TABLE Nums (K LONG CONSTRAINT PK_Nums PRIMARY KEY)", "INSERT INTO Nums (K) VALUES (1)");
        for (int m = 1; m <= 512; m *= 2)
        {
            Exec(p, $"INSERT INTO Nums (K) SELECT K + {m} FROM Nums");
        }

        Exec(p,
            // T: 40 narrow rows (fixed, variable, Boolean, date fields; a unique and a non-unique index). N is Null where ID mod 7 = 0.
            "CREATE TABLE T (ID LONG CONSTRAINT PK_T PRIMARY KEY, A LONG, B DOUBLE, N TEXT(100), V TEXT(255), Y YESNO, D DATETIME)",
            "CREATE UNIQUE INDEX UX_T_A ON T (A)",
            "CREATE INDEX IX_T_N ON T (N)",
            "INSERT INTO T (ID, A, B, N, V, Y, D) SELECT K, K * 10, K * 1.5, IIf(K Mod 7 = 0, Null, 'row ' & K), 'v' & K, CBool(K Mod 2), #1/1/2000# FROM Nums WHERE K <= 40",
            // Dense: 14 rows of about 530 bytes (two nearly full data pages); C is the short column that grows.
            "CREATE TABLE Dense (ID LONG CONSTRAINT PK_Dense PRIMARY KEY, A TEXT(255), B TEXT(255), C TEXT(255))",
            $"INSERT INTO Dense (ID, A, B, C) SELECT K, '{new string('a', 255)}', '{new string('b', 255)}', 'c' FROM Nums WHERE K <= 14",
            // G: five rows of about 520 bytes on one page, room for two more.
            "CREATE TABLE G (ID LONG CONSTRAINT PK_G PRIMARY KEY, S1 TEXT(255), S2 TEXT(255))",
            $"INSERT INTO G (ID, S1, S2) SELECT K, '{new string('x', 255)}', '{new string('y', 255)}' FROM Nums WHERE K <= 5",
            // Ix: thirty ordered Text keys; IxL: 598 small positive Long keys (the key-split family).
            "CREATE TABLE Ix (ID LONG CONSTRAINT PK_Ix PRIMARY KEY, W TEXT(40))",
            "CREATE INDEX IX_Ix_W ON Ix (W)",
            "INSERT INTO Ix (ID, W) SELECT K, 'k' & Format(K * 10, '000') FROM Nums WHERE K <= 30",
            "CREATE TABLE IxL (ID LONG CONSTRAINT PK_IxL PRIMARY KEY)",
            "INSERT INTO IxL (ID) SELECT K FROM Nums WHERE K <= 598",
            // Sl: Boolean-only one-column rows, 253 of them (the slot-count family). Bo and Bo2: Yes/No-only three-column rows (short roots).
            "CREATE TABLE Sl (F YESNO)",
            "INSERT INTO Sl (F) SELECT True FROM Nums WHERE K <= 253",
            "CREATE TABLE Bo (F1 YESNO, F2 YESNO, F3 YESNO)",
            "INSERT INTO Bo (F1, F2, F3) SELECT CBool(K Mod 2), CBool(K Mod 3), False FROM Nums WHERE K <= 60",
            "CREATE TABLE Bo2 (F1 YESNO, F2 YESNO, F3 YESNO)",
            "INSERT INTO Bo2 (F1, F2, F3) SELECT CBool(K Mod 2), CBool(K Mod 3), False FROM Nums WHERE K <= 60",
            "ALTER TABLE Bo2 ADD COLUMN Tx TEXT(100)",
            // Alt: rows written before columns are dropped, appended or reordered.
            "CREATE TABLE Alt (ID LONG CONSTRAINT PK_Alt PRIMARY KEY, A TEXT(20), B TEXT(20), C LONG, D TEXT(20))",
            "INSERT INTO Alt (ID, A, B, C, D) SELECT K, 'a' & K, 'b' & K, K * 100, 'd' & K FROM Nums WHERE K <= 20",
            // Fv: unique index, NOT NULL, validation rule and default (set below).
            "CREATE TABLE Fv (ID LONG CONSTRAINT PK_Fv PRIMARY KEY, U LONG, P LONG, Q TEXT(10) NOT NULL)",
            "CREATE UNIQUE INDEX UX_Fv_U ON Fv (U)",
            "INSERT INTO Fv (ID, U, P, Q) SELECT K, K * 10, K, 'q' & K FROM Nums WHERE K <= 5",
            // Tg and Sc: empty targets for text-grammar and scalar-admission writes.
            "CREATE TABLE Tg (ID LONG CONSTRAINT PK_Tg PRIMARY KEY, S TEXT(50), M MEMO)",
            "CREATE TABLE Sc (ID LONG CONSTRAINT PK_Sc PRIMARY KEY, Fb BYTE, Fi SHORT, Fl LONG, Fs SINGLE, Fd DOUBLE, Fc CURRENCY, Ft DATETIME, Fg GUID)",
            "ALTER TABLE Sc ADD COLUMN Fx DATETIME2",
            "CREATE TABLE Lv (ID LONG CONSTRAINT PK_Lv PRIMARY KEY, M MEMO, O LONGBINARY)");

        FieldPut(p, "T", "V", "AllowZeroLength", A.Bool(true));
        FieldPut(p, "Fv", "P", "ValidationRule", A.Str("> 0"));
        FieldPut(p, "Fv", "P", "ValidationText", A.Str("P must be positive"));
        FieldPut(p, "Fv", "P", "DefaultValue", A.Str("1"));

        // The Decimal scalar: DAO DDL refuses DECIMAL, so the field is appended with CreateField(name, dbDecimal).
        p.GetBind("tdfs", "db", "TableDefs").Item("tsc", "tdfs", A.Str("Sc")).CallBind("fdm", "tsc", "CreateField", A.Str("Fm"), A.I4(20))
            .GetBind("sfl", "tsc", "Fields").Call("sfl", "Append", A.Obj("fdm")).Release("fdm").Release("sfl").Release("tsc").Release("tdfs");

        // Lv: no long value, inline-size, single-external and chained values, and six 100-byte values packed side by side.
        LvAdd(p, 1, null, null);
        LvAdd(p, 2, A.Str("short memo"), Ole(10));
        LvAdd(p, 3, null, Ole(50));
        LvAdd(p, 4, null, Ole(5000));
        LvAdd(p, 5, null, Ole(9000));
        LvAdd(p, 6, A.Str(new string('m', 2000)), null);
        LvAdd(p, 7, A.Str(new string('n', 9000)), null);
        for (int id = 10; id <= 15; id++)
        {
            LvAdd(p, id, null, Ole(100, id));
        }
    }

    /// <summary>Objects the engine creates and keeps beside user tables: queries, a relationship, properties.</summary>
    private static void Extras(P p)
    {
        Exec(p,
            "CREATE TABLE Parent (ID LONG CONSTRAINT PK_Parent PRIMARY KEY, Nm TEXT(20))",
            "INSERT INTO Parent (ID, Nm) VALUES (1, 'p1')",
            "INSERT INTO Parent (ID, Nm) VALUES (2, 'p2')",
            "CREATE TABLE Child (ID LONG CONSTRAINT PK_Child PRIMARY KEY, PID LONG CONSTRAINT FK_Child_Parent REFERENCES Parent (ID), Note TEXT(20))",
            "INSERT INTO Child (ID, PID, Note) VALUES (1, 1, 'c1')");
        p.CallBind("q1", "db", "CreateQueryDef", A.Str("QSel"), A.Str("SELECT ID, A, N FROM T WHERE ID < 10 ORDER BY ID;"))
            .CallBind("q2", "db", "CreateQueryDef", A.Str("QParam"), A.Str("PARAMETERS pId Long; SELECT * FROM T WHERE ID = pId;"))
            .CallBind("q3", "db", "CreateQueryDef", A.Str("QAct"), A.Str("UPDATE T SET B = B WHERE ID < 0;"))
            .CallBind("q4", "db", "CreateQueryDef", A.Str("QJoin"), A.Str("SELECT Child.ID, Parent.Nm FROM Parent INNER JOIN Child ON Parent.ID = Child.PID;"))
            .Release("q1").Release("q2").Release("q3").Release("q4");
        // Unknown-to-CalcDb properties at database, table and field level (stored beside the catalog rows).
        p.CallBind("dprop", "db", "CreateProperty", A.Str("CalcDbProp"), A.I4(10), A.Str("database-level user property"))
            .GetBind("dprops", "db", "Properties").Call("dprops", "Append", A.Obj("dprop")).Release("dprop").Release("dprops")
            .GetBind("tdfs", "db", "TableDefs").Item("tdf", "tdfs", A.Str("T"))
            .CallBind("tp1", "tdf", "CreateProperty", A.Str("CalcDbUnknown"), A.I4(10), A.Str("unknown table property"))
            .CallBind("tp2", "tdf", "CreateProperty", A.Str("Description"), A.I4(10), A.Str("table T description"))
            .GetBind("tprops", "tdf", "Properties").Call("tprops", "Append", A.Obj("tp1")).Call("tprops", "Append", A.Obj("tp2"))
            .Release("tp1").Release("tp2").Release("tprops")
            .GetBind("tfl", "tdf", "Fields").Item("fa", "tfl", A.Str("A"))
            .CallBind("fp", "fa", "CreateProperty", A.Str("Description"), A.I4(10), A.Str("field A description"))
            .GetBind("fprops", "fa", "Properties").Call("fprops", "Append", A.Obj("fp"))
            .Release("fp").Release("fprops").Release("fa").Release("tfl").Release("tdf").Release("tdfs");
    }

    // ------------------------------------------------------------------------------------------------
    // R-FMT-2

    private static string[] F2(params string[] branches) => ["R-FMT-2", .. branches];

    private static IEnumerable<Probe> Fmt2()
    {
        string w = WorldAccdb;
        Action<P> readT = p => { ReadSql(p, "SELECT * FROM T ORDER BY ID", 64, 8); IndexStats(p, "T"); };
        Action<P> readDense = p => ReadSql(p, "SELECT ID, LEN(A), LEN(B), LEN(C) FROM Dense ORDER BY ID", 32, 8);

        yield return Mut("upd-fixed", F2("II-03 concrete mutation probes", "Scalar boundaries"), w,
            "Which bytes of the closed file change when one non-indexed fixed-width field (T.B, Double) of one row (ID 10) is updated to a new value of the same width? T is 40 narrow rows on one data page with a unique and a non-unique index; the statement is UPDATE with dbFailOnError.",
            readT, Q("update-b", "UPDATE T SET B = 99.25 WHERE ID = 10"));

        yield return Mut("upd-var-grow-shrink-ladder", F2("II-03 concrete mutation probes", "Overflow / deletion"), w,
            "Two single updates of one variable-width field of one row (T row 5, field N, an indexed Text): first grown from 5 to 100 characters, then shrunk to 1 character. Asks how the row and the page change in each direction while the page has room.",
            readT, Q("grow-n-to-100", $"UPDATE T SET N = '{new string('g', 100)}' WHERE ID = 5"), Q("shrink-n-to-1", "UPDATE T SET N = 'x' WHERE ID = 5"));

        yield return Mut("var-null-empty-ladder", F2("Ordinary Boolean / Null", "Text grammar", "Historical row coordinates"), w,
            "Null versus empty Text on one variable field. Row 7 has T.N Null and T.V = 'v7' (V allows zero length, N is a DDL default). Steps: V to the empty string; V to Null; V to a value; then an attempt to store the empty string in N. Asks how presence differs between Null and empty in the closed file and which attempt the engine refuses.",
            readT, Q("v-to-empty", "UPDATE T SET V = '' WHERE ID = 7"), Q("v-to-null", "UPDATE T SET V = Null WHERE ID = 7"),
            Q("v-to-value", "UPDATE T SET V = 'filled' WHERE ID = 7"), Q("n-empty-attempt", "UPDATE T SET N = '' WHERE ID = 7"));

        yield return Mut("bool-ladder", F2("Ordinary Boolean / Null", "Historical row coordinates"), w,
            "Boolean storage in single steps on T.Y (Yes/No): row 2 False to True; row 1 True to False; an attempt to set row 3 to Null; then an insert that omits Y. Asks which bit or presence change each leaves in the closed file and what the engine stores or refuses for Null and for the omitted value.",
            readT, Q("set-true", "UPDATE T SET Y = True WHERE ID = 2"), Q("set-false", "UPDATE T SET Y = False WHERE ID = 1"),
            Q("set-null-attempt", "UPDATE T SET Y = Null WHERE ID = 3"), Q("insert-omit-y", "INSERT INTO T (ID, A) VALUES (50, 5000)"));

        yield return Mut("upd-indexed-value", F2("II-03 concrete mutation probes", "II-05 Statistics word"), w,
            "Index maintenance on update, in two steps: T.N of row 5 replaced by a key that sorts last (non-unique index IX_T_N), then T.A of row 6 replaced by a new unique key (UX_T_A). Index DistinctCount per index is read before and after, so the statistic's behaviour under key replacement is observed beside the images.",
            readT, Q("replace-n-key", "UPDATE T SET N = 'zzz-moved-key' WHERE ID = 5"), Q("replace-a-key", "UPDATE T SET A = 5555 WHERE ID = 6"));

        yield return Mut("grow-ladder", F2("Overflow / deletion", "II-03 concrete mutation probes"), w,
            "Row growth on two nearly full data pages (Dense: 14 rows of about 530 bytes). Step 1 grows C of row 3 by 19 characters; step 2 grows C of row 4 by 254 characters. Asks which growth stays on the page and which moves the row, and how the old and new locations and neighbours appear in the images.",
            readDense, Q("grow-c-by-19", $"UPDATE Dense SET C = '{new string('c', 20)}' WHERE ID = 3"), Q("grow-c-by-254", $"UPDATE Dense SET C = '{new string('c', 255)}' WHERE ID = 4"));

        yield return Mut("del-reuse-ladder", F2("Overflow / deletion", "II-03 concrete mutation probes"), w,
            "Deletion and reuse: delete one middle row of T (ID 20); insert one similar row (ID 500); delete every row of G (its page or pages empty); insert one full-size G row. Asks how space freed by a delete appears in the closed file and whether the next insert reuses it.",
            p => { readT(p); ReadSql(p, "SELECT COUNT(*) FROM G", 4, 2); },
            Q("delete-middle-row", "DELETE FROM T WHERE ID = 20"),
            Q("insert-similar-row", "INSERT INTO T (ID, A, B, N, V, Y, D) VALUES (500, 5000, 750, 'row 500', 'v500', True, #1/1/2000#)"),
            Q("delete-all-g", "DELETE FROM G"),
            Q("insert-g-row", $"INSERT INTO G (ID, S1, S2) VALUES (201, '{new string('x', 255)}', '{new string('y', 255)}')"));

        yield return Mut("ins-data-ladder", F2("Map ownership and growth", "II-03 concrete mutation probes"), w,
            "Inserts until a data page fills: six single inserts of 520-byte rows into G (five rows on a page with room for two). Asks at which insert a new data page is added and what else changes (ownership and allocation pages, index) at that insert and at the ones before it.",
            p => ReadSql(p, "SELECT COUNT(*), MIN(ID), MAX(ID) FROM G", 4, 4),
            [.. Enumerable.Range(101, 6).Select(id => Q($"insert-g-{id}", $"INSERT INTO G (ID, S1, S2) VALUES ({id}, '{new string('x', 255)}', '{new string('y', 255)}')"))]);

        yield return Mut("idx-key-ladder", F2("II-05 Common prefix", "II-05 Statistics word", "II-03 concrete mutation probes"), w,
            "Index key inserts into IX_Ix_W (thirty keys k010..k300): a new lowest key, a new middle key, a new highest key, then a duplicate of an existing key. Index DistinctCount is read before and after. Asks where in the index pages each key lands and whether the statistic follows the distinct keys.",
            p => { ReadSql(p, "SELECT * FROM Ix ORDER BY W", 64, 4); IndexStats(p, "Ix"); },
            Q("insert-low-key", "INSERT INTO Ix (ID, W) VALUES (101, 'a000')"), Q("insert-mid-key", "INSERT INTO Ix (ID, W) VALUES (102, 'k155')"),
            Q("insert-high-key", "INSERT INTO Ix (ID, W) VALUES (103, 'z999')"), Q("insert-duplicate-key", "INSERT INTO Ix (ID, W) VALUES (104, 'k100')"));

        Action<P> readIxL = p => { ReadSql(p, "SELECT COUNT(*), MIN(ID), MAX(ID) FROM IxL", 4, 4); IndexStats(p, "IxL"); };
        yield return Mut("ix-append-ladder", F2("II-05 Splits / stable root", "II-05 Statistics word"), w,
            "Key-ascending appends into the primary-key index of IxL (598 Long keys 1..598): six single inserts, 599 to 604, crossing 602 entries. Asks at which insert the index leaf changes shape (a split, a new page, a root change) and what each earlier insert changes.",
            readIxL, [.. Enumerable.Range(599, 6).Select(id => Q($"insert-{id}", $"INSERT INTO IxL (ID) VALUES ({id})"))]);

        yield return Mut("ix-low-ladder", F2("II-05 Splits / stable root", "II-05 Common prefix"), w,
            "Low-key inserts into the primary-key index of IxL: one statement adds keys 599..602 (602 entries), then single inserts of -1, -2, 0 and 603 and -3. Asks what each insert does to the leaf and whether the low-key insert past 602 entries behaves differently from an append.",
            readIxL, Q("append-599-602", "INSERT INTO IxL (ID) SELECT K FROM Nums WHERE K BETWEEN 599 AND 602"), Q("insert-minus-1", "INSERT INTO IxL (ID) VALUES (-1)"),
            Q("insert-minus-2", "INSERT INTO IxL (ID) VALUES (-2)"), Q("insert-0", "INSERT INTO IxL (ID) VALUES (0)"),
            Q("insert-603", "INSERT INTO IxL (ID) VALUES (603)"), Q("insert-minus-3", "INSERT INTO IxL (ID) VALUES (-3)"));

        yield return Mut("lv-ole-small-ladder", F2("LV form selection", "Binary and OLE"), w,
            "Long Binary (OLE) values around 64 bytes: four inserts of 63, 64, 65 and 66 bytes (patterned bytes) into Lv, one per step. Asks where the inline/external form change falls and what each insert changes. Typed values and FieldSize are read back.",
            ReadLv, [.. new[] { 63, 64, 65, 66 }.Select(n => D($"insert-ole-{n}", p => LvAdd(p, 20 + n, null, Ole(n))))]);

        yield return Mut("lv-ole-page-ladder", F2("LV form selection", "Binary and OLE", "Map ownership and growth"), w,
            "Long Binary (OLE) values around one page: inserts of 4075, 4076, 4077, 4080, 4081 and 9000 bytes into Lv, one per step. Asks where the single-page form ends and the chained form begins, and which pages each value takes.",
            ReadLv, [.. new[] { 4075, 4076, 4077, 4080, 4081, 9000 }.Select(n => D($"insert-ole-{n}", p => LvAdd(p, 100 + n % 1000, null, Ole(n))))]);

        yield return Mut("lv-memo-ladder", F2("LV form selection", "Text grammar", "Map ownership and growth"), w,
            "Memo values by length and by compressibility: ASCII memos of 62, 64 and 66 characters, then 4078 and 4082 ASCII characters, then 2040 characters of U+20AC (two bytes each, not compressible). Asks where each memo changes form and how compressed and wide text differ in the closed file. Typed values and FieldSize are read back.",
            ReadLv,
            D("insert-memo-62", p => LvAdd(p, 62, A.Str(new string('a', 62)), null)), D("insert-memo-64", p => LvAdd(p, 64, A.Str(new string('a', 64)), null)),
            D("insert-memo-66", p => LvAdd(p, 66, A.Str(new string('a', 66)), null)), D("insert-memo-4078", p => LvAdd(p, 4078, A.Str(new string('a', 4078)), null)),
            D("insert-memo-4082", p => LvAdd(p, 4082, A.Str(new string('a', 4082)), null)), D("insert-memo-2040-wide", p => LvAdd(p, 2040, A.Str(new string('\u20AC', 2040)), null)));

        yield return Mut("lv-transition-ladder", F2("LV form selection", "Binary and OLE", "Overflow / deletion"), w,
            "Form transitions of an existing Long Binary value by Edit/Update: row 3 grows from 50 to 5000 bytes; row 4 shrinks from 5000 to 50; row 3 grows again to 9000; row 5 shrinks from 9000 to 5000. Asks how the old long-value space and the row's reference change in each direction.",
            ReadLv, D("grow-50-to-5000", p => LvEdit(p, 3, "O", Ole(5000, 11))), D("shrink-5000-to-50", p => LvEdit(p, 4, "O", Ole(50, 12))),
            D("grow-5000-to-9000", p => LvEdit(p, 3, "O", Ole(9000, 13))), D("shrink-9000-to-5000", p => LvEdit(p, 5, "O", Ole(5000, 14))));

        yield return Mut("lv-chain-ladder", F2("LV form selection", "Overflow / deletion", "Map ownership and growth"), w,
            "A chained Memo (row 7, 9000 ASCII characters) edited in four steps: replaced by 9000 different characters (same size); grown to 13000; shrunk to 100; set to Null. Asks how a chain is rewritten, extended, truncated and released.",
            ReadLv, D("replace-same-size", p => LvEdit(p, 7, "M", A.Str(new string('p', 9000)))), D("grow-to-13000", p => LvEdit(p, 7, "M", A.Str(new string('q', 13000)))),
            D("shrink-to-100", p => LvEdit(p, 7, "M", A.Str(new string('r', 100)))), D("set-null", p => LvEdit(p, 7, "M", A.Null)));

        yield return Mut("lv-delete-reuse-ladder", F2("Overflow / deletion", "LV form selection", "Map ownership and growth"), w,
            "Retirement and reuse among packed long values (Lv rows 10..15 hold six 100-byte OLE values side by side): delete row 12; insert a new 100-byte value (row 20); delete row 5 (a 9000-byte chain); insert a new 9000-byte value (row 22). Asks whether neighbouring values stay unchanged and whether retired space is reused.",
            ReadLv, Q("delete-packed-12", "DELETE FROM Lv WHERE ID = 12"), D("insert-ole-100", p => LvAdd(p, 20, null, Ole(100, 20))),
            Q("delete-chain-5", "DELETE FROM Lv WHERE ID = 5"), D("insert-ole-9000", p => LvAdd(p, 22, null, Ole(9000, 22))));

        yield return Mut("sl-ladder", F2("FMT-SLOTS256", "II-03 concrete mutation probes"), w,
            "Slot counts on one page: Sl holds 253 one-column Boolean rows. Four single inserts bring it to 254, 255, 256 and 257 rows; then one row (the 101st) is deleted through a table recordset, and one row is inserted. Asks what happens at each count (same page, new page, slot reuse) in the closed file.",
            p => ReadSql(p, "SELECT COUNT(*) FROM Sl", 4, 2),
            [.. Enumerable.Range(254, 4).Select(n => Q($"insert-to-{n}", "INSERT INTO Sl (F) VALUES (True)")),
                D("delete-row-101", p => p.OpenRs("rs", "db", "Sl", Dao.OpenTable).Call("rs", "MoveFirst").Call("rs", "Move", A.I4(100)).Call("rs", "Delete").Call("rs", "Close").Release("rs")),
                Q("insert-after-delete", "INSERT INTO Sl (F) VALUES (False)")]);

        yield return Mut("shortroot-append-text", F2("FMT-SHORTROOT", "Historical row coordinates"), w,
            "Yes/No-only rows (Bo: 60 rows of three Boolean columns) then two steps: ALTER TABLE ADD COLUMN Tx TEXT(100); asks whether old rows are rewritten; then one old row (the 31st) gets a 100-character Tx through a table recordset. Asks what an old short row and its neighbours look like before and after that growth.",
            p => ReadSql(p, "SELECT * FROM Bo", 64, 8), Q("add-text-column", "ALTER TABLE Bo ADD COLUMN Tx TEXT(100)"),
            D("grow-old-row", p => BoGrow(p, "Bo")));

        yield return Mut("shortroot-grow-old", F2("FMT-SHORTROOT"), w,
            "The same old-row growth on a table whose Text column was added by the fixture (Bo2, 60 Yes/No-only rows written before the column existed): one old row (the 31st) gets a 100-character Tx through a table recordset. Asks the page and neighbours' change when the column is already in the descriptor.",
            p => ReadSql(p, "SELECT * FROM Bo2", 64, 8), D("grow-old-row", p => BoGrow(p, "Bo2")));

        yield return Mut("alt-drop-ladder", F2("Historical row coordinates", "II-04 discriminating matrix"), w,
            "Rows written before columns are dropped: drop the middle variable column Alt.B; drop the final variable column Alt.D; update an old row's Alt.A to a longer value; insert a new row. Asks what the old rows' bytes look like after each schema or row step, and what the public projection returns.",
            p => ReadSql(p, "SELECT * FROM Alt ORDER BY ID", 32, 8), Q("drop-middle-b", "ALTER TABLE Alt DROP COLUMN B"), Q("drop-final-d", "ALTER TABLE Alt DROP COLUMN D"),
            Q("update-old-row", "UPDATE Alt SET A = 'a-longer-value-than-before' WHERE ID = 3"), Q("insert-new-row", "INSERT INTO Alt (ID, A, C) VALUES (99, 'new', 777)"));

        yield return Mut("alt-append-ladder", F2("Historical row coordinates", "Ordinary Boolean / Null"), w,
            "Columns appended after rows exist: add a Long, add a Text, add a Yes/No column; then move the Long column to ordinal position 0 through Field.OrdinalPosition. Old rows are read back after each step's closed image (appended Boolean and appended fixed columns read for old rows).",
            p => ReadSql(p, "SELECT * FROM Alt ORDER BY ID", 32, 12), Q("add-long", "ALTER TABLE Alt ADD COLUMN E LONG"), Q("add-text", "ALTER TABLE Alt ADD COLUMN F TEXT(30)"),
            Q("add-yesno", "ALTER TABLE Alt ADD COLUMN G YESNO"),
            D("move-e-to-position-0", p => p.GetBind("tdfs", "db", "TableDefs").Item("tdf", "tdfs", A.Str("Alt")).GetBind("fl", "tdf", "Fields").Item("fe", "fl", A.Str("E"))
                .Put("fe", "OrdinalPosition", A.I2(0)).Release("fe").Release("fl").Release("tdf").Release("tdfs")));

        yield return Mut("failed-ladder", F2("II-03 concrete mutation probes", "Generated values"), w,
            "Failed operations in Fv (unique index on U, NOT NULL Q, validation P > 0, default P = 1), each with dbFailOnError: a duplicate unique key; a validation failure; a NULL for the required Q; then one valid insert that omits P. Asks whether a failed insert changes the closed file at all and what the later valid insert stores.",
            p => ReadSql(p, "SELECT * FROM Fv ORDER BY ID", 32, 8), Q("duplicate-unique", "INSERT INTO Fv (ID, U, P, Q) VALUES (6, 10, 1, 'x')"),
            Q("validation-failure", "INSERT INTO Fv (ID, U, P, Q) VALUES (7, 70, -1, 'x')"), Q("null-required", "INSERT INTO Fv (ID, U, P, Q) VALUES (8, 80, 1, Null)"),
            Q("valid-insert-default-p", "INSERT INTO Fv (ID, U, Q) VALUES (9, 90, 'ok')"));

        yield return new Probe(Card, "txn-ladder", F2("II-03 concrete mutation probes", "Overflow / deletion"),
            "Explicit transactions on Workspaces(0), one UPDATE of T each: BeginTrans, update, CommitTrans, close; BeginTrans, update, Rollback, close; two nested BeginTrans levels, update, inner CommitTrans, outer Rollback, close. Asks what the closed file holds after each. Logical outcome scope belongs to R-TXN-1; this asks only the file images." + Images,
            p =>
            {
                Begin(p);
                p.GetBind("wss", "eng", "Workspaces").Item("ws", "wss", A.I4(0)).Release("wss");
                Read(p, "before", readT);
                RunStep(p, "commit", s => s.Call("ws", "BeginTrans").Call("db", "Execute", A.Str("UPDATE T SET B = 111 WHERE ID = 1"), A.I4(Dao.FailOnError)).Call("ws", "CommitTrans"));
                RunStep(p, "rollback", s => s.Call("ws", "BeginTrans").Call("db", "Execute", A.Str("UPDATE T SET B = 222 WHERE ID = 2"), A.I4(Dao.FailOnError)).Call("ws", "Rollback"));
                RunStep(p, "nested-inner-commit-outer-rollback", s => s.Call("ws", "BeginTrans").Call("ws", "BeginTrans").Call("db", "Execute", A.Str("UPDATE T SET B = 333 WHERE ID = 3"), A.I4(Dao.FailOnError))
                    .Call("ws", "CommitTrans").Call("ws", "Rollback"));
                Read(p, "after", readT);
                End(p);
            }) { Fixtures = [("main", w)] };

        yield return Mut("scalar-admission", F2("Scalar boundaries", "NUMERIC", "Dates / GUID", "Generated values"), w,
            "Scalar admission through a table recordset into Sc, one row per attempt (each row sets one field): Byte 255, 256 and -1; Integer min/max and 32768; Long min; Single subnormal, NaN, infinity; Double -0, subnormal, NaN, infinity; Currency max, overflow and a tie string; Date -1.5, -1, -0.5, 0, a sub-millisecond fraction and out-of-range; GUID in braced, unbraced and 16-byte forms; Decimal as strings and a Currency; extended date/time strings with a seven-digit tick and an eighth-digit tie. Typed values read back for every row; one image after all of them.",
            p => ReadSql(p, "SELECT * FROM Sc ORDER BY ID", 64, 16), D("write-attempts", ScalarAttempts));

        yield return Mut("text-grammar", F2("Text grammar"), w,
            "Text admission through a table recordset into Tg, one row per value: plain ASCII; Latin-1 'é'; a wide character U+20AC; mixed narrow and wide; a surrogate pair; a lone high and a lone low surrogate; an embedded NUL; a leading U+FEFF; 50 narrow and 50 wide characters (the field limit); 51 characters; then two Memo values of 4000 ASCII characters with one wide character at the start and at the end. Typed values read back; one image after all of them.",
            p => ReadSql(p, "SELECT * FROM Tg ORDER BY ID", 32, 4), D("write-values", TextAttempts));

        // The same mutations in the Jet 4 MDB format (the descriptor differs).
        yield return Mut("mdb-grow-relocate", F2("Overflow / deletion", "II-03 concrete mutation probes"), WorldMdb,
            "As grow-ladder step 2 in the Jet 4 MDB world: row 4 of Dense grows by 254 characters on a nearly full page. Asks the same placement question for the older format.",
            readDense, Q("grow-c-by-254", $"UPDATE Dense SET C = '{new string('c', 255)}' WHERE ID = 4"));

        yield return Mut("mdb-lv-ole-page-ladder", F2("LV form selection", "Binary and OLE"), WorldMdb,
            "As lv-ole-page-ladder in the Jet 4 MDB world: inserts of 4075, 4076, 4077, 4080, 4081 and 9000 bytes of Long Binary into Lv, one per step.",
            ReadLv, [.. new[] { 4075, 4076, 4077, 4080, 4081, 9000 }.Select(n => D($"insert-ole-{n}", p => LvAdd(p, 100 + n % 1000, null, Ole(n))))]);
    }

    // ------------------------------------------------------------------------------------------------
    // R-PRESERVE-1: the same mutations beside engine-created objects, with catalog views before and after.

    private static string[] Pr(params string[] branches) => ["R-PRESERVE-1", .. branches];

    private static IEnumerable<Probe> Preserve()
    {
        string f = PresAccdb;
        yield return Pres("noop", Pr("II-04 preservation cases", "Capture and comparison"), f,
            "Opener bookkeeping versus rewriting: images of the fixture copy, after a read-only catalog view, after one read/write open and close with no statement, and after a second read-only view. Asks whether and where a plain open, read and close changes the file.",
            Step("noop-open-close", _ => { }));
        yield return Pres("row-edit", Pr("II-04 preservation cases", "Capture and comparison"), f,
            "One supported table edit beside preserved objects: UPDATE T SET B (non-indexed Double) on one row, with the full catalog view (properties, queries, relations, containers, system tables) before and after. Asks which catalog or system-table state changes, beyond table T's own pages.",
            Step("update-b", s => Sql(s, "UPDATE T SET B = 99.25 WHERE ID = 10")));
        yield return Pres("grow-relocate", Pr("II-04 preservation cases", "Capture and comparison"), f,
            "Row growth beside preserved objects: row 4 of Dense grows by 254 characters on a nearly full page. Asks whether the row's move changes anything of the objects around it.",
            Step("grow-c-by-254", s => Sql(s, $"UPDATE Dense SET C = '{new string('c', 255)}' WHERE ID = 4")));
        yield return Pres("lv-delete-retire", Pr("II-04 preservation cases", "Capture and comparison"), f,
            "Retirement of one long value among packed neighbours with objects present: delete Lv row 12 (one of six 100-byte OLE values side by side). Asks whether the neighbouring values or the catalog change.",
            Step("delete-packed-12", s => Sql(s, "DELETE FROM Lv WHERE ID = 12")));
        yield return Pres("lv-replace-chain", Pr("II-04 preservation cases", "Capture and comparison"), f,
            "Replacing a chained OLE value through a recordset with objects present: Lv row 5 changes from 9000 to 12000 bytes. Asks about the other values' bytes and the catalog.",
            Step("replace-ole-12000", s => LvEdit(s, 5, "O", Ole(12000, 15))));
        yield return Pres("ins-pressure", Pr("II-04 preservation cases", "Capture and comparison"), f,
            "Allocation pressure with objects present: one statement inserts 400 rows of about 790 bytes into Dense (several dozen new data pages and ownership/allocation growth). Asks what happens to the catalog's pages and to every other table's pages.",
            Step("insert-400-rows", s => Sql(s, $"INSERT INTO Dense (ID, A, B, C) SELECT K + 100, '{new string('p', 255)}', '{new string('q', 255)}', '{new string('r', 255)}' FROM Nums WHERE K <= 400")));
        yield return Pres("catalog-ladder", Pr("II-04 preservation cases", "II-06 catalog/property discrimination"), f,
            "Supported catalog mutations beside preserved objects, one per step: CREATE INDEX on T.B; ALTER TABLE G ADD COLUMN; ALTER TABLE G DROP COLUMN; CREATE TABLE. Asks what changes in catalog rows, property blocks and other objects' pages at each.",
            Step("create-index", s => Sql(s, "CREATE INDEX IX_T_B ON T (B)")), Step("add-column", s => Sql(s, "ALTER TABLE G ADD COLUMN Z LONG")),
            Step("drop-column", s => Sql(s, "ALTER TABLE G DROP COLUMN S2")), Step("create-table", s => Sql(s, "CREATE TABLE NewT (ID LONG CONSTRAINT PK_NewT PRIMARY KEY, X TEXT(20))")));
        yield return Pres("property-ladder", Pr("II-06 catalog/property discrimination", "II-04 preservation cases"), f,
            "Property edits beside unknown properties, one per step: the user property T.CalcDbUnknown given a new value of the same length; then a longer value; then the field property T.A Description changed; then the database property CalcDbProp given a longer value. Asks a same-length patch versus a length-changing rewrite, and what happens to the other properties' values and positions.",
            Step("table-prop-same-length", s => TableProp(s, "T", "CalcDbUnknown", "UNKNOWN table property")), Step("table-prop-longer", s => TableProp(s, "T", "CalcDbUnknown", "a considerably longer unknown table property value")),
            Step("field-prop-description", s => s.GetBind("tdfs", "db", "TableDefs").Item("tdf", "tdfs", A.Str("T")).GetBind("tfl", "tdf", "Fields").Item("fa", "tfl", A.Str("A"))
                .GetBind("fprops", "fa", "Properties").Item("fpr", "fprops", A.Str("Description")).Put("fpr", "Value", A.Str("field A description, now longer")).Release("fpr").Release("fprops")
                .Release("fa").Release("tfl").Release("tdf").Release("tdfs")),
            Step("db-prop-longer", s => s.GetBind("props", "db", "Properties").Item("pr", "props", A.Str("CalcDbProp")).Put("pr", "Value", A.Str("database-level user property, now much longer")).Release("pr").Release("props")));
        yield return Pres("rename-referenced", Pr("II-06 catalog/property discrimination", "Capture and comparison"), f,
            "Shared references: rename Parent (referenced by the Child foreign key and QJoin), then rename T (referenced by QSel, QParam and QAct) through TableDef.Name. Asks what the engine rewrites in relationships, saved query text and the other catalog rows, and what it leaves.",
            Step("rename-parent", s => s.GetBind("tdfs", "db", "TableDefs").Item("tdf", "tdfs", A.Str("Parent")).Put("tdf", "Name", A.Str("Parent2")).Release("tdf").Release("tdfs")),
            Step("rename-t", s => s.GetBind("tdfs", "db", "TableDefs").Item("tdf", "tdfs", A.Str("T")).Put("tdf", "Name", A.Str("T2")).Release("tdf").Release("tdfs")));

        yield return new Probe(Card, "pres-txn-failed-ladder", Pr("II-04 preservation cases", "Capture and comparison"),
            "Rollback and failed operations with objects present: BeginTrans, UPDATE T, Rollback; an insert into Child with a missing parent (foreign key); an insert into T repeating a unique key. Asks whether the closed file, the catalog and the system tables change at all." + Images,
            p =>
            {
                Begin(p);
                p.GetBind("wss", "eng", "Workspaces").Item("ws", "wss", A.I4(0)).Release("wss");
                PublicView(p, "v0", A.In());
                RunStep(p, "rollback", s => s.Call("ws", "BeginTrans").Call("db", "Execute", A.Str("UPDATE T SET B = 222 WHERE ID = 2"), A.I4(Dao.FailOnError)).Call("ws", "Rollback"));
                RunStep(p, "fk-violation", s => Sql(s, "INSERT INTO Child (ID, PID, Note) VALUES (2, 99, 'orphan')"));
                RunStep(p, "unique-violation", s => Sql(s, "INSERT INTO T (ID, A) VALUES (60, 10)"));
                PublicView(p, "v1", A.In());
                End(p);
            }) { Fixtures = [("main", f)] };

        yield return new Probe(Card, "pres-compact", Pr("Capture and comparison", "Compact & Repair diagnostic"),
            "Compact as a separate derivative: DBEngine.CompactDatabase of the fixture copy into a work file; the source is unchanged. The catalog view is taken of the source and of the compacted derivative, and both files are kept. Asks what the compacted file preserves of the properties, queries, relations and system objects, and what differs.",
            p =>
            {
                Begin(p);
                PublicView(p, "source", A.In());
                p.Call("eng", "CompactDatabase", A.In(), A.Out("compacted.accdb")).Files("after-compact", keep: true);
                PublicView(p, "compacted", A.Out("compacted.accdb"));
                End(p);
            }) { Fixtures = [("main", f)] };
    }

    // ------------------------------------------------------------------------------------------------
    // Probe shapes

    private static (string Label, Action<P> Body) Q(string label, params string[] sqls) => (label, p => Sql(p, sqls));

    private static (string Label, Action<P> Body) D(string label, Action<P> body) => (label, body);

    private static (string Label, Action<P> Body) Step(string label, Action<P> body) => (label, body);

    private static Probe Mut(string name, string[] refs, string fixture, string what, Action<P>? read, params (string Label, Action<P> Body)[] steps) =>
        new(Card, name, refs, what + Images, p =>
        {
            Begin(p);
            if (read is not null)
            {
                Read(p, "before", read);
            }
            foreach ((string label, Action<P> body) in steps)
            {
                RunStep(p, label, body);
            }
            if (read is not null)
            {
                Read(p, "after", read);
            }
            End(p);
        })
        { Fixtures = [("main", fixture)] };

    private static Probe Pres(string name, string[] refs, string fixture, string what, params (string Label, Action<P> Body)[] steps) =>
        new(Card, "pres-" + name, refs, what + Images + " The full catalog view is taken before the steps and after them (each a read-only open, with its own file state).", p =>
        {
            Begin(p);
            PublicView(p, "v0", A.In());
            foreach ((string label, Action<P> body) in steps)
            {
                RunStep(p, label, body);
            }
            PublicView(p, "v1", A.In());
            End(p);
        })
        { Fixtures = [("main", fixture)] };

    private static void Begin(P p) => p.Files("before", keep: true).Modules("before-create").Create().Modules("after-create");

    private static void End(P p) => p.Release("eng").Modules("after-close").Files("final");

    /// <summary>Open read/write, run the body (one mutation), close, and keep the closed file's image.</summary>
    private static void RunStep(P p, string label, Action<P> body)
    {
        p.CallBind("db", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false));
        body(p);
        p.Call("db", "Close").Release("db").Files("after-" + label, keep: true);
    }

    /// <summary>Open read-only, read the public projection, close; the file state after is recorded.</summary>
    private static void Read(P p, string tag, Action<P> body)
    {
        p.OpenDb();
        body(p);
        p.Call("db", "Close").Release("db").Files("read-" + tag);
    }

    // ------------------------------------------------------------------------------------------------
    // Statements and readbacks

    private static void Exec(P p, params string[] sqls)
    {
        foreach (string sql in sqls)
        {
            p.Call("db", "Execute", A.Str(sql));
        }
    }

    private static void Sql(P p, params string[] sqls)
    {
        foreach (string sql in sqls)
        {
            p.Call("db", "Execute", A.Str(sql), A.I4(Dao.FailOnError)).Get("db", "RecordsAffected");
        }
    }

    private static void ReadSql(P p, string sql, int maxRows, int maxFields) =>
        p.OpenRs("rs", "db", sql, Dao.OpenSnapshot).ReadAll("rs", maxRows, maxFields).Call("rs", "Close").Release("rs");

    private static void IndexStats(P p, string table) =>
        p.GetBind("tdfs", "db", "TableDefs").Item("tdf", "tdfs", A.Str(table)).GetBind("idxs", "tdf", "Indexes")
            .Each("idxs", "ix", 8, b => b.Get("ix", "Name").Get("ix", "Primary").Get("ix", "Unique").Get("ix", "DistinctCount")).Release("idxs").Release("tdf").Release("tdfs");

    /// <summary>Lv rows with their typed values and the byte size DAO reports for the long fields.</summary>
    private static void ReadLv(P p) =>
        p.OpenRs("rs", "db", "SELECT ID, M, O FROM Lv ORDER BY ID", Dao.OpenSnapshot)
            .Until("rs", "EOF", 64, b => b.ReadRow("rs", 4).GetBind("fl", "rs", "Fields")
                .Item("fm", "fl", A.Str("M")).Get("fm", "FieldSize").Item("fo", "fl", A.Str("O")).Get("fo", "FieldSize")
                .Release("fm").Release("fo").Release("fl").Call("rs", "MoveNext"))
            .Call("rs", "Close").Release("rs");

    private static Arg Ole(int n, int seed = 3) => A.Bytes([.. Enumerable.Range(0, n).Select(i => (byte)(i * 7 + seed))]);

    private static void FieldPut(P p, string table, string field, string property, Arg value) =>
        p.GetBind("tdfs", "db", "TableDefs").Item("tdfx", "tdfs", A.Str(table)).GetBind("ffl", "tdfx", "Fields").Item("ffx", "ffl", A.Str(field))
            .Put("ffx", property, value).Release("ffx").Release("ffl").Release("tdfx").Release("tdfs");

    private static void TableProp(P p, string table, string property, string value) =>
        p.GetBind("tdfs", "db", "TableDefs").Item("tdf", "tdfs", A.Str(table)).GetBind("tprops", "tdf", "Properties").Item("tpr", "tprops", A.Str(property))
            .Put("tpr", "Value", A.Str(value)).Release("tpr").Release("tprops").Release("tdf").Release("tdfs");

    /// <summary>AddNew a Lv row through a table recordset; a null argument leaves that field unset.</summary>
    private static void LvAdd(P p, int id, Arg? memo, Arg? ole)
    {
        p.OpenRs("rs", "db", "Lv", Dao.OpenTable).GetBind("fl", "rs", "Fields").Item("fi", "fl", A.Str("ID")).Item("fm", "fl", A.Str("M")).Item("fo", "fl", A.Str("O"))
            .Call("rs", "AddNew").Put("fi", "Value", A.I4(id));
        if (memo is not null)
        {
            p.Put("fm", "Value", memo);
        }
        if (ole is not null)
        {
            p.Put("fo", "Value", ole);
        }
        p.Call("rs", "Update").Release("fi").Release("fm").Release("fo").Release("fl").Call("rs", "Close").Release("rs");
    }

    /// <summary>Edit one long field of one Lv row (dynaset, Edit/Update).</summary>
    private static void LvEdit(P p, int id, string field, Arg value) =>
        p.OpenRs("rs", "db", $"SELECT * FROM Lv WHERE ID = {id}", Dao.OpenDynaset).GetBind("fl", "rs", "Fields").Item("ff", "fl", A.Str(field))
            .Call("rs", "Edit").Put("ff", "Value", value).Call("rs", "Update").Release("ff").Release("fl").Call("rs", "Close").Release("rs");

    /// <summary>Move to the 31st record of a Yes/No-only table and give its Text column a 100-character value.</summary>
    private static void BoGrow(P p, string table) =>
        p.OpenRs("rs", "db", table, Dao.OpenTable).Call("rs", "MoveFirst").Call("rs", "Move", A.I4(30)).GetBind("fl", "rs", "Fields").Item("ft", "fl", A.Str("Tx"))
            .Call("rs", "Edit").Put("ft", "Value", A.Str(new string('t', 100))).Call("rs", "Update").Release("ft").Release("fl").Call("rs", "Close").Release("rs");

    private static void ScalarAttempts(P p)
    {
        (string Field, Arg Value)[] attempts =
        [
            ("Fb", A.UI1(255)), ("Fb", A.I4(256)), ("Fb", A.I4(-1)),
            ("Fi", A.I2(short.MinValue)), ("Fi", A.I2(short.MaxValue)), ("Fi", A.I4(32768)),
            ("Fl", A.I4(int.MinValue)),
            ("Fs", A.R4(float.Epsilon)), ("Fs", A.R4(float.NaN)), ("Fs", A.R4(float.PositiveInfinity)),
            ("Fd", A.R8(-0.0)), ("Fd", A.R8(double.Epsilon)), ("Fd", A.R8(double.NaN)), ("Fd", A.R8(double.PositiveInfinity)),
            ("Fc", A.Cy(922337203685477.5807m)), ("Fc", A.Str("922337203685477.5808")), ("Fc", A.Str("0.00005")), ("Fc", A.Str("0.00015")),
            ("Ft", A.Date(-1.5)), ("Ft", A.Date(-1.0)), ("Ft", A.Date(-0.5)), ("Ft", A.Date(0.0)), ("Ft", A.Date(1.5 + 1e-9)), ("Ft", A.Date(2958466.0)),
            ("Fg", A.Str("{00112233-4455-6677-8899-AABBCCDDEEFF}")), ("Fg", A.Str("00112233-4455-6677-8899-AABBCCDDEEFF")),
            ("Fg", A.Bytes(0x00, 0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77, 0x88, 0x99, 0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF)),
            ("Fm", A.Str("12345678901234.5678")), ("Fm", A.Str("-0.0001")), ("Fm", A.Cy(1.5m)),
            ("Fx", A.Str("0001-01-01 00:00:00.0000001")), ("Fx", A.Str("9999-12-31 23:59:59.9999999")), ("Fx", A.Str("2024-01-02 03:04:05.12345675")),
        ];
        p.OpenRs("rs", "db", "Sc", Dao.OpenTable).GetBind("fl", "rs", "Fields").Item("f_ID", "fl", A.Str("ID"));
        foreach (string field in attempts.Select(a => a.Field).Distinct())
        {
            p.Item("f_" + field, "fl", A.Str(field));
        }
        for (int i = 0; i < attempts.Length; i++)
        {
            p.Call("rs", "AddNew").Put("f_ID", "Value", A.I4(i + 1)).Put("f_" + attempts[i].Field, "Value", attempts[i].Value).Call("rs", "Update");
        }
        p.Call("rs", "Close").Release("rs");
    }

    private static void TextAttempts(P p)
    {
        (string Field, Arg Value)[] attempts =
        [
            ("S", A.Str("plain")), ("S", A.Str("Latin-1 \u00E9")), ("S", A.Str("wide \u20AC")), ("S", A.Str("mixed ab\u20ACcd")),
            ("S", A.BstrUnits("pair \uD834\uDD1E")), ("S", A.BstrUnits("high \uD800 lone")), ("S", A.BstrUnits("low \uDC00 lone")),
            ("S", A.BstrUnits("a\0b")), ("S", A.BstrUnits("\uFEFFbom first")),
            ("S", A.Str(new string('n', 50))), ("S", A.Str(new string('\u20AC', 50))), ("S", A.Str(new string('n', 51))),
            ("M", A.Str("\u20AC" + new string('a', 3998) + "\u20AC")), ("M", A.Str(new string('a', 4000))),
        ];
        p.OpenRs("rs", "db", "Tg", Dao.OpenTable).GetBind("fl", "rs", "Fields").Item("f_ID", "fl", A.Str("ID")).Item("f_S", "fl", A.Str("S")).Item("f_M", "fl", A.Str("M"));
        for (int i = 0; i < attempts.Length; i++)
        {
            p.Call("rs", "AddNew").Put("f_ID", "Value", A.I4(i + 1)).Put("f_" + attempts[i].Field, "Value", attempts[i].Value).Call("rs", "Update");
        }
        p.Call("rs", "Close").Release("rs");
    }

    // ------------------------------------------------------------------------------------------------
    // The catalog view of R-PRESERVE-1

    /// <summary>
    /// Opens <paramref name="path"/> read-only and reads: database properties; every TableDef (system tables
    /// included) with Attributes, dates and properties; the properties of T.A; every QueryDef with SQL and
    /// properties; Relations with fields; Containers with Documents; and the engine's own catalog tables
    /// through SQL (a refusal is an observation). The closed file's state is recorded after.
    /// </summary>
    private static void PublicView(P p, string tag, Arg path)
    {
        p.CallBind("db", "eng", "OpenDatabase", path, A.Bool(false), A.Bool(true)).Get("db", "Version").Get("db", "CollatingOrder")
            .GetBind("dprops", "db", "Properties").Each("dprops", "dp", 64, b => b.Get("dp", "Name").Get("dp", "Type").Get("dp", "Value")).Release("dprops")
            .GetBind("tdfs", "db", "TableDefs")
            .Each("tdfs", "tdf", 96, t => t.Get("tdf", "Name").Get("tdf", "Attributes").Get("tdf", "DateCreated").Get("tdf", "LastUpdated")
                .GetBind("tprops", "tdf", "Properties").Each("tprops", "tpp", 48, x => x.Get("tpp", "Name").Get("tpp", "Type").Get("tpp", "Value")).Release("tprops"))
            .Item("tt", "tdfs", A.Str("T")).GetBind("tfl", "tt", "Fields").Item("fa", "tfl", A.Str("A")).GetBind("fprops", "fa", "Properties")
            .Each("fprops", "fpp", 32, x => x.Get("fpp", "Name").Get("fpp", "Value")).Release("fprops").Release("fa").Release("tfl").Release("tt").Release("tdfs")
            .GetBind("qdfs", "db", "QueryDefs")
            .Each("qdfs", "qd", 16, q => q.Get("qd", "Name").Get("qd", "Type").Get("qd", "SQL").Get("qd", "DateCreated").Get("qd", "LastUpdated")
                .GetBind("qprops", "qd", "Properties").Each("qprops", "qpp", 48, x => x.Get("qpp", "Name").Get("qpp", "Value")).Release("qprops"))
            .Release("qdfs")
            .GetBind("rels", "db", "Relations")
            .Each("rels", "rl", 16, r => r.Get("rl", "Name").Get("rl", "Table").Get("rl", "ForeignTable").Get("rl", "Attributes")
                .GetBind("rflds", "rl", "Fields").Each("rflds", "rf", 8, x => x.Get("rf", "Name").Get("rf", "ForeignName")).Release("rflds"))
            .Release("rels")
            .GetBind("cons", "db", "Containers")
            .Each("cons", "co", 16, c => c.Get("co", "Name").Get("co", "Owner")
                .GetBind("docs", "co", "Documents").Each("docs", "dc", 32, d => d.Get("dc", "Name").Get("dc", "Owner").Get("dc", "DateCreated").Get("dc", "LastUpdated")).Release("docs"))
            .Release("cons");
        ReadSql(p, "SELECT Id, ParentId, Name, Type, Flags, DateCreate, DateUpdate FROM MSysObjects ORDER BY Id", 128, 8);
        ReadSql(p, "SELECT * FROM MSysRelationships", 16, 12);
        ReadSql(p, "SELECT * FROM MSysQueries", 128, 12);
        p.Call("db", "Close").Release("db").Files("view-" + tag);
    }
}
