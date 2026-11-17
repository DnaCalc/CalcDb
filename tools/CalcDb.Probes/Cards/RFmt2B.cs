namespace CalcDb.Probes.Cards;

/// <summary>
/// R-FMT-2 and R-PRESERVE-1, second set: native mutation shapes with one mutation per probe and the closed
/// file kept before and after it. Questions not asked by <see cref="RFmt2Preserve1"/>: single fixed-width edits
/// by width and by Null transition; variable-width growth around the free space of a page (rows of Text fields
/// of exactly known byte width, so page arithmetic does not depend on text compression); memo and OLE values
/// across one, two and many pages in both directions and at page-size boundaries; row delete and reinsert of
/// one key; deletes that empty a data page; index inserts, deletes and reinsertions on Long, duplicate-key and
/// Text indexes; AutoNumber after deletes, explicit values and reseeding; a compact to a work file after each
/// class of mutation; whether the file the opener sees after Close is the final file (a wait, an engine
/// release, and a participant that kills the writer right after Close); and a held cursor across a relocation
/// and across a delete/reinsert of its row. Fixtures are created through ACE DAO; the data of every table is
/// defined in <see cref="World"/>. The probes ask; they do not judge. Interpreting bytes is the inspector's work.
/// Deferred (not in code): Access/Excel UI, VBA, network paths, registry or settings, byte interpretation,
/// Random AutoNumber (needs a DAO property route not captured yet), raw-byte anchors for opaque blocks.
/// </summary>
public sealed class RFmt2B : ICard
{
    private const string Card = "r-fmt-2b";
    private const string WorldAccdb = Card + "-w-accdb";
    private const string WorldMdb = Card + "-w-mdb";
    private const int Wait = 30000;
    private const char Wide = (char)0x20AC;
    private const int PgChars = 61;

    private const string Images = " Images: 'before' (the fixture copy as given), then one kept image of the closed file after each step; every step opens the database read/write, makes one mutation and closes it, with the DBEngine still alive.";

    private const string PgText = "Pg: 12 rows, ID and eight Text(255) fields T1..T8, each holding 61 repetitions of U+20AC (122 UTF-16 bytes of value; the character lies outside Latin-1).";

    private static readonly string PgCols = string.Join(", ", Enumerable.Range(1, 8).Select(i => $"T{i}"));

    public IEnumerable<Probe> Probes()
    {
        yield return Fixture("w-accdb", "accdb", Dao.Version120, accdb: true);
        yield return Fixture("w-mdb", "mdb", Dao.Version40, accdb: false);
        foreach (IEnumerable<Probe> family in new[] { Fixed(), PageGrowth(), Memos(), Oles(), Deletes(), Indexes(), AutoNumbers(), Mdb(), Compacts(), Settle(), Cursors() })
        {
            foreach (Probe p in family)
            {
                yield return p;
            }
        }
    }

    // ------------------------------------------------------------------------------------------------
    // Fixtures

    private static Probe Fixture(string name, string ext, int version, bool accdb) =>
        new(Card, "create-" + name, ["R-FMT-2", "R-PRESERVE-1", "II-03 concrete mutation probes"],
            $"Fixture ({ext.ToUpperInvariant()}): small separate tables each prepared for one mutation family. " +
            (accdb ? "Fx (ten rows, fixed Byte, Long, Currency, Date, GUID, Decimal and a trailing Text; row 8 Null in all fixed fields), " : "") +
            "Pg (12 rows of eight Text fields of 61 U+20AC), Mm (eight rows, memos of Null, 1000, 3000, 12000 and 20 characters and three of 1000), Lo (Long Binary of Null, 4076, 4090 and three of 100 bytes), " +
            "Dr (20 small rows), IxM (602 Long keys with a gap at 302), IxD (300 rows with one duplicated indexed value), IxT (60 long unique Text keys)" +
            (accdb ? ", Au1..Au4 (AutoNumber tables: intact, tail deleted, middle deleted, all deleted)" : "") +
            $" and the Nums helper. Created by DBEngine.CreateDatabase(path, dbLangGeneral, {version}); the closed file is kept.",
            p =>
            {
                p.EngineLite().CallBind("db", "eng", "CreateDatabase", A.Out($"{Card}-{name}.{ext}"), A.Str(Dao.LangGeneral), A.I4(version)).Get("db", "Version");
                World(p, accdb);
                p.FinishLite("db").Files("fixture-closed", keep: true);
            })
        { CreatesFixture = true };

    private static void World(P p, bool accdb)
    {
        // Nums: 1..1024 by doubling, the row source of every bulk fill below.
        Exec(p, "CREATE TABLE Nums (K LONG CONSTRAINT PK_Nums PRIMARY KEY)", "INSERT INTO Nums (K) VALUES (1)");
        for (int m = 1; m <= 512; m *= 2)
        {
            Exec(p, $"INSERT INTO Nums (K) SELECT K + {m} FROM Nums");
        }

        string t61 = new(Wide, PgChars);
        string pad = new('p', 40);
        Exec(p,
            $"CREATE TABLE Pg (ID LONG CONSTRAINT PK_Pg PRIMARY KEY, {string.Join(", ", Enumerable.Range(1, 8).Select(i => $"T{i} TEXT(255)"))})",
            $"INSERT INTO Pg (ID, {PgCols}) SELECT K, {string.Join(", ", Enumerable.Repeat($"'{t61}'", 8))} FROM Nums WHERE K <= 12",
            "CREATE TABLE Mm (ID LONG CONSTRAINT PK_Mm PRIMARY KEY, M MEMO)",
            "CREATE TABLE Lo (ID LONG CONSTRAINT PK_Lo PRIMARY KEY, O LONGBINARY)",
            "CREATE TABLE Dr (ID LONG CONSTRAINT PK_Dr PRIMARY KEY, A LONG, Vt TEXT(60))",
            "INSERT INTO Dr (ID, A, Vt) SELECT K, K * 10, 'original text of row ' & K FROM Nums WHERE K <= 20",
            "CREATE TABLE IxM (ID LONG CONSTRAINT PK_IxM PRIMARY KEY)",
            "INSERT INTO IxM (ID) SELECT K FROM Nums WHERE K <= 603 AND K <> 302",
            "CREATE TABLE IxD (ID LONG CONSTRAINT PK_IxD PRIMARY KEY, G LONG)",
            "CREATE INDEX IX_IxD_G ON IxD (G)",
            "INSERT INTO IxD (ID, G) SELECT K, 7 FROM Nums WHERE K <= 300",
            "CREATE TABLE IxT (ID LONG CONSTRAINT PK_IxT PRIMARY KEY, W TEXT(60))",
            "CREATE UNIQUE INDEX UX_IxT_W ON IxT (W)",
            $"INSERT INTO IxT (ID, W) SELECT K, 'k' & Format(K * 10, '0000') & '{pad}' FROM Nums WHERE K <= 60");

        AddRow(p, "Mm", ("ID", A.I4(1)));
        AddRow(p, "Mm", ("ID", A.I4(2)), ("M", Memo(1000)));
        AddRow(p, "Mm", ("ID", A.I4(3)), ("M", Memo(3000)));
        AddRow(p, "Mm", ("ID", A.I4(4)), ("M", Memo(12000)));
        AddRow(p, "Mm", ("ID", A.I4(5)), ("M", A.Str(Short)));
        for (int id = 6; id <= 8; id++)
        {
            AddRow(p, "Mm", ("ID", A.I4(id)), ("M", Memo(1000)));
        }

        AddRow(p, "Lo", ("ID", A.I4(1)));
        AddRow(p, "Lo", ("ID", A.I4(2)), ("O", Ole(4076, 2)));
        AddRow(p, "Lo", ("ID", A.I4(3)), ("O", Ole(4090, 3)));
        for (int id = 10; id <= 12; id++)
        {
            AddRow(p, "Lo", ("ID", A.I4(id)), ("O", Ole(100, id)));
        }

        if (!accdb)
        {
            return;
        }

        Exec(p,
            "CREATE TABLE Fx (ID LONG CONSTRAINT PK_Fx PRIMARY KEY, Fb BYTE, Fl LONG, Fc CURRENCY, Ft DATETIME, Fg GUID, Vt TEXT(30))",
            "INSERT INTO Fx (ID, Fb, Fl, Fc, Ft, Vt) SELECT K, K * 10, K * 1000, CCur(K * 1.25), #1/1/2000#, 'v' & K FROM Nums WHERE K <= 10",
            "UPDATE Fx SET Fb = Null, Fl = Null, Fc = Null, Ft = Null WHERE ID = 8");
        // The GUID is written through a table recordset, one distinct value per row except row 8.
        p.OpenRs("rs", "db", "Fx", Dao.OpenTable).GetBind("fl", "rs", "Fields").Item("fg", "fl", A.Str("Fg"));
        for (int id = 1; id <= 10; id++)
        {
            if (id != 8)
            {
                p.Call("rs", "Edit").Put("fg", "Value", A.Str($"{{00112233-4455-6677-8899-AABBCCDDEE{id:X2}}}")).Call("rs", "Update");
            }
            p.Call("rs", "MoveNext");
        }
        p.Release("fg").Release("fl").Call("rs", "Close").Release("rs");
        // The Decimal field: DAO DDL refuses DECIMAL (3292), so it is appended with CreateField(name, dbDecimal).
        p.AddDecimalField("Fx", "Fm");
        Exec(p, "UPDATE Fx SET Fm = ID * 100 WHERE ID <> 8");

        for (int n = 1; n <= 4; n++)
        {
            Exec(p, $"CREATE TABLE Au{n} (ID COUNTER CONSTRAINT PK_Au{n} PRIMARY KEY, S TEXT(20))",
                $"INSERT INTO Au{n} (S) SELECT 'row' & K FROM Nums WHERE K <= 10");
        }
        Exec(p, "DELETE FROM Au2 WHERE ID >= 8", "DELETE FROM Au3 WHERE ID = 5", "DELETE FROM Au4");
    }

    // ------------------------------------------------------------------------------------------------
    // Fixed-width edits (Fx): one field of one row per probe

    private static string[] F(params string[] branches) => ["R-FMT-2", .. branches];

    private static string[] FP(params string[] branches) => ["R-FMT-2", "R-PRESERVE-1", .. branches];

    private const string FxText = "Fx: ten rows on one small table (ID Long key; Byte Fb, Long Fl, Currency Fc, Date Ft, GUID Fg, Decimal Fm and a trailing Text Vt); row 8 is Null in Fb, Fl, Fc, Ft and Fm.";

    private static void ReadFx(P p)
    {
        ReadSql(p, "SELECT * FROM Fx ORDER BY ID", 16, 12);
        IndexStats(p, "Fx");
    }

    private static IEnumerable<Probe> Fixed()
    {
        string w = WorldAccdb;
        yield return Mut("fx-byte-update", F("II-03 concrete mutation probes", "Scalar boundaries"), w,
            $"Which bytes of the closed file change when one one-byte fixed field (Fx.Fb, Byte) of row 5 is updated to a different value by UPDATE with dbFailOnError? {FxText}",
            ReadFx, Q("update-fb", "UPDATE Fx SET Fb = 200 WHERE ID = 5"));
        yield return Mut("fx-currency-update", F("II-03 concrete mutation probes", "Scalar boundaries"), w,
            $"Which bytes change when one eight-byte fixed field (Fx.Fc, Currency) of row 5 is updated to a different value? {FxText}",
            ReadFx, Q("update-fc", "UPDATE Fx SET Fc = CCur(1234.5678) WHERE ID = 5"));
        yield return Mut("fx-guid-update", F("II-03 concrete mutation probes", "Dates / GUID"), w,
            $"Which bytes change when one sixteen-byte fixed field (Fx.Fg, GUID) of row 5 is set through a dynaset Edit/Update to a different braced GUID string? {FxText}",
            ReadFx, D("edit-fg", p => EditRs(p, "Fx", 5, "Fg", A.Str("{FFEEDDCC-BBAA-9988-7766-554433221100}"))));
        yield return Mut("fx-decimal-update", F("II-03 concrete mutation probes", "NUMERIC"), w,
            $"Which bytes change when the Decimal field (Fx.Fm, appended by CreateField) of row 5 is updated to the integer 12345 by UPDATE? {FxText}",
            ReadFx, Q("update-fm", "UPDATE Fx SET Fm = 12345 WHERE ID = 5"));
        yield return Mut("fx-null-to-value", F("Ordinary Boolean / Null", "II-03 concrete mutation probes"), w,
            $"Which bytes change when a Null fixed field (Fx.Fl of row 8, whose other fixed fields stay Null) is updated to a value? {FxText}",
            ReadFx, Q("null-to-value", "UPDATE Fx SET Fl = 777 WHERE ID = 8"));
        yield return Mut("fx-value-to-null", F("Ordinary Boolean / Null", "II-03 concrete mutation probes"), w,
            $"Which bytes change when a fixed field with a value (Fx.Fl of row 5) is updated to Null? {FxText}",
            ReadFx, Q("value-to-null", "UPDATE Fx SET Fl = Null WHERE ID = 5"));
        yield return Mut("fx-same-value-update", F("II-03 concrete mutation probes"), w,
            $"Which bytes change when a fixed field is updated to the value it already holds (UPDATE Fx SET Fl = 5000 WHERE ID = 5; Fl is 5000)? {FxText}",
            ReadFx, Q("update-fl-same", "UPDATE Fx SET Fl = 5000 WHERE ID = 5"));
    }

    // ------------------------------------------------------------------------------------------------
    // Variable-width growth around page free space, inserts into a full page (Pg)

    private static void ReadPg(P p) =>
        ReadSql(p, "SELECT ID, LEN(T1), LEN(T2), LEN(T8) FROM Pg ORDER BY ID", 16, 4);

    private static IEnumerable<Probe> PageGrowth()
    {
        // Growth steps are chosen around the free space a page of four such rows would leave under the book's
        // II-03 row layout (14-byte header, two-byte slots, row overhead); the images decide where the boundary falls.
        string w = WorldAccdb;
        foreach (int k in new[] { 1, 29, 30, 60 })
        {
            yield return Mut($"pg-grow-row2-plus-{k}", F("Overflow / deletion", "II-03 concrete mutation probes", "Map ownership and growth"), w,
                $"Which bytes change when one variable field of one row (Pg.T1 of row 2) grows by {k} character{(k == 1 ? "" : "s")} ({2 * k} UTF-16 bytes) from {PgChars} to {PgChars + k} characters, by one UPDATE? {PgText}",
                ReadPg, Q($"grow-t1-plus-{k}", $"UPDATE Pg SET T1 = '{new string(Wide, PgChars + k)}' WHERE ID = 2"));
        }
        yield return Mut("pg-grow-row4-plus-60", F("Overflow / deletion", "II-03 concrete mutation probes"), w,
            $"The same growth by 60 characters (120 UTF-16 bytes) on row 4 instead of row 2 (Pg.T1 from {PgChars} to {PgChars + 60} characters). Asks whether the row's position among the page's rows changes what the growth does. {PgText}",
            ReadPg, Q("grow-t1-plus-60-row4", $"UPDATE Pg SET T1 = '{new string(Wide, PgChars + 60)}' WHERE ID = 4"));
        yield return Mut("pg-insert-min-row", F("Overflow / deletion", "Map ownership and growth"), w,
            $"Where does the smallest possible new row go: INSERT INTO Pg (ID) VALUES (13), every Text field Null, into a table whose existing pages were filled by 12 rows of equal size? {PgText}",
            ReadPg, Q("insert-id-only", "INSERT INTO Pg (ID) VALUES (13)"));
    }

    // ------------------------------------------------------------------------------------------------
    // Memo growth and shrink across pages (Mm)

    private const string Short = "twenty chars of text";

    private const string MmText = "Mm: eight rows (ID, Memo M): row 1 Null; rows 2, 6, 7 and 8 with 1000 characters of U+20AC (2000 UTF-16 bytes each); row 3 with 3000; row 4 with 12000; row 5 with the 20 ASCII characters 'twenty chars of text'. Typed lengths and FieldSize are read back.";

    private static Arg Memo(int chars) => A.Str(new string(Wide, chars));

    private static void ReadMm(P p)
    {
        ReadSql(p, "SELECT ID, LEN(M) FROM Mm ORDER BY ID", 16, 4);
        ReadLong(p, "Mm", "M");
    }

    private static IEnumerable<Probe> Memos()
    {
        string w = WorldAccdb;
        string[] r = F("LV form selection", "Map ownership and growth", "Overflow / deletion");
        (string Name, int Row, string From, Arg To, string ToText)[] edits =
        [
            ("null-to-1page", 1, "Null", Memo(1000), "1000 characters (2000 UTF-16 bytes)"),
            ("null-to-2pages", 1, "Null", Memo(3000), "3000 characters (6000 UTF-16 bytes)"),
            ("null-to-many-pages", 1, "Null", Memo(12000), "12000 characters (24000 UTF-16 bytes)"),
            ("1page-to-2pages", 2, "1000 characters", Memo(3000), "3000 characters"),
            ("2pages-to-many-pages", 3, "3000 characters", Memo(12000), "12000 characters"),
            ("many-pages-to-1page", 4, "12000 characters", Memo(1000), "1000 characters"),
            ("many-pages-to-short", 4, "12000 characters", A.Str(Short), "20 ASCII characters"),
            ("short-to-many-pages", 5, "20 ASCII characters", Memo(12000), "12000 characters"),
        ];
        foreach ((string name, int row, string from, Arg to, string toText) in edits)
        {
            yield return Mut("mm-" + name, r, w,
                $"One dynaset Edit/Update of Mm.M of row {row}, from {from} to {toText}. Asks how the old long-value space and the row's reference change, and what happens to the neighbouring values on the same pages. {MmText}",
                ReadMm, D("edit-m-" + name, p => EditRs(p, "Mm", row, "M", to)));
        }
    }

    // ------------------------------------------------------------------------------------------------
    // OLE values at exact page-boundary sizes (Lo)

    private const string LoText = "Lo: six rows (ID, Long Binary O): row 1 Null; row 2 with 4076 bytes; row 3 with 4090 bytes; rows 10, 11 and 12 with 100 bytes each. Bytes are patterned (index * 7 + seed, modulo 256). ID and FieldSize are read back.";

    private static void ReadLo(P p) => ReadLong(p, "Lo", "O");

    private static IEnumerable<Probe> Oles()
    {
        string w = WorldAccdb;
        string[] r = F("LV form selection", "Binary and OLE", "Map ownership and growth");
        foreach (int n in new[] { 4078, 4079, 8156 })
        {
            yield return Mut($"ole-insert-{n}", r, w,
                $"One insert of a {n}-byte Long Binary value into Lo (ID 100) through a table recordset. Asks where the single-page / chained form change and page-multiple boundaries fall for this size and which pages the value takes. {LoText}",
                ReadLo, D($"insert-ole-{n}", p => AddRow(p, "Lo", ("ID", A.I4(100)), ("O", Ole(n, 9)))));
        }
        yield return Mut("ole-grow-4076-to-4077", r, w,
            $"One dynaset Edit/Update of Lo.O of row 2 from 4076 bytes to 4077 bytes (one byte more). Asks how the value's pages and the row's reference change by a one-byte growth at a size near a page. {LoText}",
            ReadLo, D("grow-by-one-byte", p => EditRs(p, "Lo", 2, "O", Ole(4077, 21))));
        yield return Mut("ole-shrink-4090-to-4076", r, w,
            $"One dynaset Edit/Update of Lo.O of row 3 from 4090 bytes to 4076 bytes. Asks how the value's pages and the row's reference change by a shrink to a size near a page. {LoText}",
            ReadLo, D("shrink-to-4076", p => EditRs(p, "Lo", 3, "O", Ole(4076, 22))));
    }

    // ------------------------------------------------------------------------------------------------
    // Deletes, reinsertion of one key, pages emptied by deletes (Pg, Dr)

    private const string DrText = "Dr: 20 small rows (ID Long key, A Long, Vt Text(60) 'original text of row <ID>'), ID 1..20.";

    private static void ReadDr(P p)
    {
        ReadSql(p, "SELECT * FROM Dr ORDER BY ID", 32, 4);
        IndexStats(p, "Dr");
    }

    private static IEnumerable<Probe> Deletes()
    {
        string w = WorldAccdb;
        string[] r = F("Overflow / deletion", "II-03 concrete mutation probes", "II-05 Deletion");
        yield return Mut("dl-pg-delete-first-row", r, w,
            $"Which bytes change when the first row of a full data page (Pg ID 1) is deleted by one DELETE? {PgText}",
            ReadPg, Q("delete-id-1", "DELETE FROM Pg WHERE ID = 1"));
        yield return Mut("dl-pg-delete-page-one-statement", r, w,
            $"Which bytes change when every row of the first data page (Pg ID 1..4) is deleted by one DELETE statement? Asks what happens to the page, its ownership and available-space entries and the primary-key index. {PgText}",
            ReadPg, Q("delete-ids-1-to-4", "DELETE FROM Pg WHERE ID <= 4"));
        yield return Mut("dl-pg-empty-ladder", [.. r, "Map ownership and growth"], w,
            $"Deletes until a page empties: Pg ID 1, 2, 3 and 4 are deleted by four single statements, then one full-size row (ID 21, eight fields of 61 U+20AC) is inserted. Asks which delete empties the page, what each earlier delete changes, and whether the insert reuses the emptied page. {PgText}",
            ReadPg, Q("delete-id-1", "DELETE FROM Pg WHERE ID = 1"), Q("delete-id-2", "DELETE FROM Pg WHERE ID = 2"), Q("delete-id-3", "DELETE FROM Pg WHERE ID = 3"),
            Q("delete-id-4", "DELETE FROM Pg WHERE ID = 4"),
            Q("insert-id-21", $"INSERT INTO Pg (ID, {PgCols}) VALUES (21, {string.Join(", ", Enumerable.Repeat($"'{new string(Wide, PgChars)}'", 8))})"));
        yield return Mut("dr-reinsert-same-key-same-values", r, w,
            $"Delete then reinsert of one key: Dr row 8 is deleted, then ID 8 is inserted again with the values it had (A 80, Vt 'original text of row 8'). Asks whether the new row takes the old slot and body, and how the primary-key index entry is retired and re-added. {DrText}",
            ReadDr, Q("delete-id-8", "DELETE FROM Dr WHERE ID = 8"), Q("reinsert-id-8-same", "INSERT INTO Dr (ID, A, Vt) VALUES (8, 80, 'original text of row 8')"));
        yield return Mut("dr-reinsert-same-key-longer-value", r, w,
            $"Delete then reinsert of one key with a longer value: Dr row 8 is deleted, then ID 8 is inserted again with A 80 and a 60-character Vt. Asks whether the longer row can reuse the freed space. {DrText}",
            ReadDr, Q("delete-id-8", "DELETE FROM Dr WHERE ID = 8"), Q("reinsert-id-8-longer", $"INSERT INTO Dr (ID, A, Vt) VALUES (8, 80, '{new string('L', 60)}')"));
    }

    // ------------------------------------------------------------------------------------------------
    // Index inserts, deletes and reinsertion (IxM, IxD, IxT)

    private static void ReadIx(P p, string table)
    {
        ReadSql(p, $"SELECT COUNT(*), MIN(ID), MAX(ID) FROM {table}", 4, 4);
        IndexStats(p, table);
    }

    private static IEnumerable<Probe> Indexes()
    {
        string w = WorldAccdb;
        const string ixm = "IxM: 602 Long primary-key entries (1..301 and 303..603), no other column.";
        string[] split = F("II-05 Splits / stable root", "II-05 Statistics word", "II-03 concrete mutation probes");
        yield return Mut("ix-mid-insert-at-602", split, w,
            $"One insert of key 302 into the middle of the primary-key index of IxM, whose leaf holds 602 entries. Asks what the insert does to the leaf (in place, split, new page) when the new key falls between existing ones rather than below or above all of them. {ixm}",
            p => ReadIx(p, "IxM"), Q("insert-302", "INSERT INTO IxM (ID) VALUES (302)"));
        yield return Mut("ix-delete-max-key", F("II-05 Deletion", "II-05 Statistics word"), w,
            $"One delete of the highest key (ID 603) of the primary-key index of IxM. Asks what the leaf and the index statistic do. {ixm}",
            p => ReadIx(p, "IxM"), Q("delete-603", "DELETE FROM IxM WHERE ID = 603"));
        yield return Mut("ix-delete-low-range", F("II-05 Deletion", "II-05 Index page retirement", "II-05 Statistics word"), w,
            $"One DELETE of the 300 lowest keys (ID <= 300) of the primary-key index of IxM. Asks what happens to the index pages, the leaf's entries and the statistic when a large run of entries is removed. {ixm}",
            p => ReadIx(p, "IxM"), Q("delete-ids-up-to-300", "DELETE FROM IxM WHERE ID <= 300"));
        yield return Mut("ix-delete-reinsert-ladder", F("II-05 Deletion", "II-05 Statistics word"), w,
            $"Delete then reinsert of one index key: key 100 is deleted, then inserted again. Asks how the entry is retired and re-added and whether the statistic follows. {ixm}",
            p => ReadIx(p, "IxM"), Q("delete-100", "DELETE FROM IxM WHERE ID = 100"), Q("reinsert-100", "INSERT INTO IxM (ID) VALUES (100)"));
        yield return Mut("ix-dup-ladder", split, w,
            "Inserts into a non-unique index that holds one value many times: IxD has 300 rows (ID 1..300) all with G = 7 and index IX_IxD_G on G. Four single statements add 100, 100, 200 and 400 more rows with G = 7. Asks at which statement the duplicate-key index gains pages and how the primary-key index and the statistic change.",
            p => ReadIx(p, "IxD"),
            Q("add-100", "INSERT INTO IxD (ID, G) SELECT K + 300, 7 FROM Nums WHERE K <= 100"), Q("add-100-more", "INSERT INTO IxD (ID, G) SELECT K + 400, 7 FROM Nums WHERE K <= 100"),
            Q("add-200", "INSERT INTO IxD (ID, G) SELECT K + 500, 7 FROM Nums WHERE K <= 200"), Q("add-400", "INSERT INTO IxD (ID, G) SELECT K + 700, 7 FROM Nums WHERE K <= 400"));
        string pad = new('p', 40);
        yield return Mut("ix-text-ladder", split, w,
            "Ascending key inserts into a unique Text index of long keys: IxT has 60 rows (ID 1..60, W 'k' + a four-digit number + 40 'p' characters) and unique index UX_IxT_W. Four statements add 20 rows each with higher keys. Asks at which statement the index leaf is split and where the new keys land.",
            p => ReadIx(p, "IxT"),
            [.. Enumerable.Range(1, 4).Select(n => Q($"add-20-rows-{n}", $"INSERT INTO IxT (ID, W) SELECT K + {40 + 20 * n}, 'k' & Format((K + {40 + 20 * n}) * 10, '0000') & '{pad}' FROM Nums WHERE K <= 20"))]);
    }

    // ------------------------------------------------------------------------------------------------
    // AutoNumber (Au1..Au4)

    private static void ReadAu(P p, string table)
    {
        ReadSql(p, $"SELECT * FROM {table} ORDER BY ID", 32, 4);
        IndexStats(p, table);
    }

    private static (string Label, Action<P> Body) InsertAuto(string label, string table, string text) =>
        D(label, p =>
        {
            Sql(p, $"INSERT INTO {table} (S) VALUES ('{text}')");
            ReadSql(p, "SELECT @@IDENTITY", 2, 2);
        });

    private static IEnumerable<Probe> AutoNumbers()
    {
        string w = WorldAccdb;
        string[] r = F("Generated values", "II-03 concrete mutation probes");
        yield return Mut("au-after-tail-delete", r, w,
            "AutoNumber after deletes at the end: Au2 had ten rows and IDs 8..10 were deleted in the fixture. One insert without an ID; the new ID is read by @@IDENTITY in the same open and the table afterwards. Asks which ID the engine assigns and what the closed file changes.",
            p => ReadAu(p, "Au2"), InsertAuto("insert-auto", "Au2", "new"));
        yield return Mut("au-after-middle-delete", r, w,
            "AutoNumber after a delete in the middle: Au3 had ten rows and ID 5 was deleted in the fixture. One insert without an ID; the new ID is read by @@IDENTITY and the table afterwards. Asks which ID is assigned and whether the free key is reused.",
            p => ReadAu(p, "Au3"), InsertAuto("insert-auto", "Au3", "new"));
        yield return Mut("au-after-all-deleted", r, w,
            "AutoNumber after every row was deleted: Au4 had ten rows, all deleted in the fixture. One insert without an ID; the new ID is read by @@IDENTITY and the table afterwards. Asks which ID is assigned to the first row of an emptied table.",
            p => ReadAu(p, "Au4"), InsertAuto("insert-auto", "Au4", "new"));
        yield return Mut("au-explicit-high-ladder", r, w,
            "AutoNumber and an explicit key: Au1 (ten rows, IDs 1..10) first receives INSERT with ID 100 given, then an insert without an ID. The second new ID is read by @@IDENTITY. Asks how the explicit value changes the counter and what each step changes in the file.",
            p => ReadAu(p, "Au1"), Q("insert-explicit-100", "INSERT INTO Au1 (ID, S) VALUES (100, 'explicit')"), InsertAuto("insert-auto", "Au1", "after explicit"));
        yield return Mut("au-reseed-ladder", r, w,
            "AutoNumber reseed: Au1 (ten rows) receives ALTER TABLE Au1 ALTER COLUMN ID COUNTER(200, 5); then an insert without an ID, whose ID is read by @@IDENTITY. The ALTER may be refused (the key column is the primary key); the refusal is an observation. Asks what the file shows after each step and which ID the insert gets.",
            p => ReadAu(p, "Au1"), Q("alter-counter-200-5", "ALTER TABLE Au1 ALTER COLUMN ID COUNTER(200, 5)"), InsertAuto("insert-auto", "Au1", "after reseed"));
    }

    // ------------------------------------------------------------------------------------------------
    // The Jet 4 MDB world: three of the shapes above

    private static IEnumerable<Probe> Mdb()
    {
        string w = WorldMdb;
        yield return Mut("mdb-pg-grow-row2-plus-30", F("Overflow / deletion", "II-03 concrete mutation probes"), w,
            $"As pg-grow-row2-plus-30 in the Jet 4 MDB world: Pg.T1 of row 2 grows by 30 characters. Asks the same page question for the older format. {PgText}",
            ReadPg, Q("grow-t1-plus-30", $"UPDATE Pg SET T1 = '{new string(Wide, PgChars + 30)}' WHERE ID = 2"));
        yield return Mut("mdb-mm-null-to-many-pages", F("LV form selection", "Map ownership and growth"), w,
            $"As mm-null-to-many-pages in the Jet 4 MDB world: Mm.M of row 1 from Null to 12000 characters. {MmText}",
            ReadMm, D("edit-m-null-to-many-pages", p => EditRs(p, "Mm", 1, "M", Memo(12000))));
        yield return Mut("mdb-ole-insert-4078", F("LV form selection", "Binary and OLE"), w,
            $"As ole-insert-4078 in the Jet 4 MDB world: one insert of a 4078-byte Long Binary value (ID 100). {LoText}",
            ReadLo, D("insert-ole-4078", p => AddRow(p, "Lo", ("ID", A.I4(100)), ("O", Ole(4078, 9)))));
    }

    // ------------------------------------------------------------------------------------------------
    // Compact to a work file after each class of mutation

    private static Probe CompactAfter(string name, string[] extra, string what, Action<P> read, params (string Label, Action<P> Body)[] steps) =>
        new(Card, name, FP([.. extra, "Compact & Repair diagnostic", "II-04 preservation cases", "Capture and comparison"]),
            what + " Then DBEngine.CompactDatabase of the mutated file into a work file (the mutated file is unchanged); the public state is read before the mutation, after it and from the compacted file. " + Images + " The compacted file is kept as a separate derivative (after-compact).",
            p =>
            {
                Begin(p);
                Read(p, "before", read);
                foreach ((string label, Action<P> body) in steps)
                {
                    RunStep(p, label, body);
                }
                Read(p, "mutated", read);
                p.Call("eng", "CompactDatabase", A.In(), A.Out("compacted.accdb")).Files("after-compact", keep: true)
                    .CallBind("db", "eng", "OpenDatabase", A.Out("compacted.accdb"), A.Bool(false), A.Bool(true));
                read(p);
                p.Call("db", "Close").Release("db").Files("read-compacted");
                End(p);
            })
        { Fixtures = [("main", WorldAccdb)] };

    private static IEnumerable<Probe> Compacts()
    {
        yield return CompactAfter("compact-after-fixed-update", ["II-03 concrete mutation probes"],
            $"Compact after a fixed-width edit: Fx.Fb of row 5 is updated by UPDATE. {FxText}",
            ReadFx, Q("update-fb", "UPDATE Fx SET Fb = 200 WHERE ID = 5"));
        yield return CompactAfter("compact-after-page-growth", ["Overflow / deletion"],
            $"Compact after a row grew past its page's free space: Pg.T1 of row 2 grows by 60 characters. {PgText}",
            ReadPg, Q("grow-t1-plus-60", $"UPDATE Pg SET T1 = '{new string(Wide, PgChars + 60)}' WHERE ID = 2"));
        yield return CompactAfter("compact-after-memo-growth", ["LV form selection", "Map ownership and growth"],
            $"Compact after a memo grew from Null to many pages: Mm.M of row 1 receives 12000 characters. {MmText}",
            ReadMm, D("edit-m-null-to-many-pages", p => EditRs(p, "Mm", 1, "M", Memo(12000))));
        yield return CompactAfter("compact-after-memo-shrink", ["LV form selection", "Overflow / deletion"],
            $"Compact after a memo shrank from many pages to a short value: Mm.M of row 4 receives 20 ASCII characters. {MmText}",
            ReadMm, D("edit-m-many-to-short", p => EditRs(p, "Mm", 4, "M", A.Str(Short))));
        yield return CompactAfter("compact-after-ole-insert", ["LV form selection", "Binary and OLE"],
            $"Compact after an OLE value of 4078 bytes was inserted into Lo (ID 100). {LoText}",
            ReadLo, D("insert-ole-4078", p => AddRow(p, "Lo", ("ID", A.I4(100)), ("O", Ole(4078, 9)))));
        yield return CompactAfter("compact-after-page-emptied", ["Overflow / deletion", "II-05 Index page retirement"],
            $"Compact after the first data page was emptied by deleting Pg ID 1..4. {PgText}",
            ReadPg, Q("delete-ids-1-to-4", "DELETE FROM Pg WHERE ID <= 4"));
        yield return CompactAfter("compact-after-index-growth", ["II-05 Splits / stable root", "II-05 Statistics word"],
            "Compact after a duplicate-key index grew: 400 more rows with G = 7 are inserted into IxD (300 rows with G = 7 and index IX_IxD_G), by one statement.",
            p => ReadIx(p, "IxD"), Q("add-400", "INSERT INTO IxD (ID, G) SELECT K + 300, 7 FROM Nums WHERE K <= 400"));
    }

    // ------------------------------------------------------------------------------------------------
    // Is the file seen after Close the final file? Waits, engine release and a kill right after Close.

    private static Probe SettleProbe(string name, string what, Action<P> read, (string Label, Action<P> Body) step) =>
        new(Card, name, F("II-03 concrete mutation probes", "Capture and comparison", "R-TXN-1 flush window"),
            what + " One kept image after the database Close (DBEngine alive), a second after a 1500 ms pause, a third after the DBEngine is released. Asks whether the three images are the same bytes.",
            p =>
            {
                Begin(p);
                Read(p, "before", read);
                RunStep(p, step.Label, step.Body);
                p.Pause(1500).Files("after-1500ms", keep: true).Release("eng").Files("after-engine-release", keep: true).FinishLite();
            })
        { Fixtures = [("main", WorldAccdb)] };

    private static Probe KillProbe(string name, string what, Action<P> mutate, Action<P> read) =>
        new(Card, name, F("II-03 concrete mutation probes", "Capture and comparison", "R-TXN-1 flush window", "R-CRASH-1"),
            what + " Writer A opens the database read/write, makes the mutation, closes the database (DBEngine alive) and signals; process B kills A at once (TerminateProcess, verified own worker), waits 0.5 s, keeps the frozen file, opens it read-only with DAO and reads the public state. Process-termination model only.",
            a =>
            {
                a.EngineLite().Files("before", keep: true).CallBind("db", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false));
                mutate(a);
                a.Call("db", "Close").Release("db").Signal("a-closed").Await("never", Wait).FinishLite();
            })
        {
            Fixtures = [("main", WorldAccdb)],
            Participants =
            [
                ("b", b =>
                {
                    b.Await("a-closed", Wait).KillParticipant("a", 0).Pause(500).Files("frozen", keep: true).EngineLite().OpenDb();
                    read(b);
                    b.Call("db", "Close").Release("db").Files("after-read", keep: true).FinishLite();
                }),
            ],
        };

    private static IEnumerable<Probe> Settle()
    {
        yield return SettleProbe("settle-fixed-update", $"One fixed-width edit (UPDATE Fx SET Fb = 200 WHERE ID = 5). {FxText}", ReadFx, Q("update-fb", "UPDATE Fx SET Fb = 200 WHERE ID = 5"));
        yield return SettleProbe("settle-memo-many-pages", $"One memo from Null to 12000 characters (Mm.M of row 1). {MmText}", ReadMm, D("edit-m-null-to-many-pages", p => EditRs(p, "Mm", 1, "M", Memo(12000))));
        yield return KillProbe("kill-after-close-fixed-update", $"One fixed-width edit (UPDATE Fx SET Fb = 200 WHERE ID = 5). {FxText}",
            p => Sql(p, "UPDATE Fx SET Fb = 200 WHERE ID = 5"), ReadFx);
        yield return KillProbe("kill-after-close-memo-many-pages", $"One memo from Null to 12000 characters (Mm.M of row 1). {MmText}",
            p => EditRs(p, "Mm", 1, "M", Memo(12000)), ReadMm);
    }

    // ------------------------------------------------------------------------------------------------
    // A held cursor across a relocation and across a delete/reinsert of its own row

    private static void CursorRead(P p) => p.Get("fi", "Value").Get("fv", "Value").Get("rs", "Bookmark");

    private static IEnumerable<Probe> Cursors()
    {
        yield return new Probe(Card, "cur-held-grow-past-page", ["R-FMT-2", "R-CURSOR-1", "Overflow / deletion"],
            $"A dynaset over Pg (ORDER BY ID, fields ID and T1) is positioned on row 2 (MoveNext from the first record) and ID, T1 and Bookmark are read; then Execute with dbFailOnError grows Pg.T1 of row 2 by 60 characters on a second path of the same open; then ID, T1 and Bookmark are read again from the held cursor, MoveNext, MovePrevious, Requery and a MoveNext from the first record each read again. Asks what the held cursor and bookmark show after the row's page placement may have changed. {PgText}" + Images,
            p =>
            {
                Begin(p);
                p.CallBind("db", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false))
                    .OpenRs("rs", "db", "SELECT ID, T1 FROM Pg ORDER BY ID", Dao.OpenDynaset).Call("rs", "MoveNext")
                    .GetBind("fl", "rs", "Fields").Item("fi", "fl", A.Str("ID")).Item("fv", "fl", A.Str("T1"));
                CursorRead(p);
                p.Call("db", "Execute", A.Str($"UPDATE Pg SET T1 = '{new string(Wide, PgChars + 60)}' WHERE ID = 2"), A.I4(Dao.FailOnError)).Get("db", "RecordsAffected");
                CursorRead(p);
                p.Call("rs", "MoveNext").Get("fi", "Value").Call("rs", "MovePrevious");
                CursorRead(p);
                p.Call("rs", "Requery").Call("rs", "MoveNext");
                CursorRead(p);
                p.Release("fi").Release("fv").Release("fl").Call("rs", "Close").Release("rs").Call("db", "Close").Release("db").Files("after-step", keep: true);
                End(p);
            })
        { Fixtures = [("main", WorldAccdb)] };

        yield return new Probe(Card, "cur-held-delete-reinsert-key", ["R-FMT-2", "R-CURSOR-1", "Overflow / deletion", "II-05 Deletion"],
            $"A dynaset over Dr (ORDER BY ID, fields ID and Vt) is positioned on row 8 (MoveFirst, Move 7) and ID, Vt and Bookmark are read; then Execute deletes ID 8 and Execute inserts ID 8 again with the same values; then ID, Vt and Bookmark are read from the held cursor, MoveNext, MovePrevious, Requery and MoveFirst plus Move 7 each read again. Asks what the held cursor and bookmark show after the row was deleted and its key reinserted. {DrText}" + Images,
            p =>
            {
                Begin(p);
                p.CallBind("db", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false))
                    .OpenRs("rs", "db", "SELECT ID, Vt FROM Dr ORDER BY ID", Dao.OpenDynaset).Call("rs", "MoveFirst").Call("rs", "Move", A.I4(7))
                    .GetBind("fl", "rs", "Fields").Item("fi", "fl", A.Str("ID")).Item("fv", "fl", A.Str("Vt"));
                CursorRead(p);
                p.Call("db", "Execute", A.Str("DELETE FROM Dr WHERE ID = 8"), A.I4(Dao.FailOnError))
                    .Call("db", "Execute", A.Str("INSERT INTO Dr (ID, A, Vt) VALUES (8, 80, 'original text of row 8')"), A.I4(Dao.FailOnError));
                CursorRead(p);
                p.Call("rs", "MoveNext").Get("fi", "Value").Call("rs", "MovePrevious");
                CursorRead(p);
                p.Call("rs", "Requery").Call("rs", "MoveFirst").Call("rs", "Move", A.I4(7));
                CursorRead(p);
                p.Release("fi").Release("fv").Release("fl").Call("rs", "Close").Release("rs").Call("db", "Close").Release("db").Files("after-step", keep: true);
                End(p);
            })
        { Fixtures = [("main", WorldAccdb)] };
    }

    // ------------------------------------------------------------------------------------------------
    // Probe shapes (the shapes of RFmt2Preserve1, kept private to each card)

    private static (string Label, Action<P> Body) Q(string label, params string[] sqls) => (label, p => Sql(p, sqls));

    private static (string Label, Action<P> Body) D(string label, Action<P> body) => (label, body);

    private static Probe Mut(string name, string[] refs, string fixture, string what, Action<P> read, params (string Label, Action<P> Body)[] steps) =>
        new(Card, name, refs, what + Images, p =>
        {
            Begin(p);
            Read(p, "before", read);
            foreach ((string label, Action<P> body) in steps)
            {
                RunStep(p, label, body);
            }
            Read(p, "after", read);
            End(p);
        })
        { Fixtures = [("main", fixture)] };

    private static void Begin(P p) => p.EngineLite().Files("before", keep: true);

    private static void End(P p) => p.Release("eng").FinishLite();

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

    /// <summary>ID and the byte size DAO reports (FieldSize) for one long field of every row, without reading the bytes.</summary>
    private static void ReadLong(P p, string table, string field) =>
        p.OpenRs("rs", "db", $"SELECT ID, {field} FROM {table} ORDER BY ID", Dao.OpenSnapshot).GetBind("fl", "rs", "Fields")
            .Item("fi", "fl", A.Str("ID")).Item("ff", "fl", A.Str(field))
            .Until("rs", "EOF", 64, b => b.Get("fi", "Value").Get("ff", "FieldSize").Call("rs", "MoveNext"))
            .Release("fi").Release("ff").Release("fl").Call("rs", "Close").Release("rs");

    private static Arg Ole(int n, int seed) => A.Bytes([.. Enumerable.Range(0, n).Select(i => (byte)(i * 7 + seed))]);

    /// <summary>AddNew one row through a table recordset, setting the given fields.</summary>
    private static void AddRow(P p, string table, params (string Field, Arg Value)[] values)
    {
        p.OpenRs("rs", "db", table, Dao.OpenTable).GetBind("fl", "rs", "Fields");
        for (int i = 0; i < values.Length; i++)
        {
            p.Item("f" + i, "fl", A.Str(values[i].Field));
        }
        p.Call("rs", "AddNew");
        for (int i = 0; i < values.Length; i++)
        {
            p.Put("f" + i, "Value", values[i].Value);
        }
        p.Call("rs", "Update");
        for (int i = 0; i < values.Length; i++)
        {
            p.Release("f" + i);
        }
        p.Release("fl").Call("rs", "Close").Release("rs");
    }

    /// <summary>Edit one field of the row with this ID through a dynaset (Edit/Update).</summary>
    private static void EditRs(P p, string table, int id, string field, Arg value) =>
        p.OpenRs("rs", "db", $"SELECT * FROM {table} WHERE ID = {id}", Dao.OpenDynaset).GetBind("fl", "rs", "Fields").Item("ff", "fl", A.Str(field))
            .Call("rs", "Edit").Put("ff", "Value", value).Call("rs", "Update").Release("ff").Release("fl").Call("rs", "Close").Release("rs");
}
