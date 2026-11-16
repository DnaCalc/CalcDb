namespace CalcDb.Probes.Cards;

/// <summary>
/// R-STRING-1 (field values as BSTRs), R-ADO-1 (ADODB with the ACE OLE DB provider) and R-ACT-1 (native
/// activation) as seen from one in-process program on one STA (ACT-THREAD/SAME-STA). Three fixtures are
/// created first: a string table in ACCDB and MDB (written through DAO, plus a WITH COMPRESSION table
/// written through ADO), and a small keyed ADO fixture. Probes only ask; no answer is expected in code.
/// </summary>
public sealed class RStrAdoAct : ICard
{
    private const string Card = "r-str-ado-act";
    private const string Ace = "Microsoft.ACE.OLEDB.12.0";

    /// <summary>
    /// R-ACT-1 ACT-MISSING: a creation by an absent name (DAO.DBEngine.36 on x64, Jet 4 OLE DB, unknown
    /// providers) can go through CLSIDFromProgID missing-name handling and class-store/install lookups, which
    /// the book allows only in an isolated authorized disposable environment. Off until the owner approves.
    /// </summary>
    private static bool MissingNameRoutes => false; // off until the owner approves

    private static readonly string StrAccdb = Card + "-str-accdb";
    private static readonly string StrMdb = Card + "-str-mdb";
    // "-ado4-accdb": ado lost K to DECIMAL in DAO DDL (3292), so Decimal now comes from CreateField; ado2 and ado3
    // lost J to its unbracketed column name Note (3292; evidence/r-ddl-limits), now [Note].
    private static readonly string AdoAccdb = Card + "-ado4-accdb";

    private static readonly string[] StrRefs = ["R-STRING-1", "D-STRING-1", "II-02"];

    /// <summary>ADO constants (values from the ADO type library, inventoried by ado-describe).</summary>
    private static class Ado
    {
        public const int UseServer = 2, UseClient = 3;
        public const int OpenForwardOnly = 0, OpenKeyset = 1, OpenDynamic = 2, OpenStatic = 3;
        public const int LockReadOnly = 1, LockPessimistic = 2, LockOptimistic = 3, LockBatchOptimistic = 4;
        public const int CmdText = 1, CmdTable = 2, CmdStoredProc = 4, CmdFile = 256, CmdTableDirect = 512;
        public const int AsyncExecute = 0x10, AsyncFetch = 0x20, ExecuteNoRecords = 0x80;
        public const int ModeRead = 1;
        public const int AffectAll = 3;
        public const int FilterNone = 0, FilterConflictingRecords = 5;
        public const int PersistAdtg = 0, PersistXml = 1;
        public const int TypeBinary = 1, TypeText = 2;
        public const int SaveCreateOverWrite = 2;
        public const int ReadAll = -1, GetRowsRest = -1, BookmarkFirst = 1, ClipString = 2;
        public const int VarWChar = 202, ParamInput = 1;
        public const int XactSerializable = 0x100000;

        /// <summary>CursorOptionEnum values asked through Recordset.Supports.</summary>
        public static readonly (string Name, int Value)[] Supports =
        [
            ("adAddNew", 0x1000400), ("adApproxPosition", 0x4000), ("adBookmark", 0x2000), ("adDelete", 0x1000800),
            ("adFind", 0x80000), ("adHoldRecords", 0x100), ("adIndex", 0x100000), ("adMovePrevious", 0x200),
            ("adNotify", 0x40000), ("adResync", 0x20000), ("adSeek", 0x400000), ("adUpdate", 0x1008000),
            ("adUpdateBatch", 0x10000),
        ];

        /// <summary>SchemaEnum values asked through Connection.OpenSchema; 9999 is not a defined schema.</summary>
        public static readonly (string Name, int Value)[] Schemas =
        [
            ("adSchemaTables", 20), ("adSchemaColumns", 4), ("adSchemaIndexes", 12), ("adSchemaForeignKeys", 27),
            ("adSchemaProcedures", 16), ("adSchemaViews", 23), ("adSchemaProviderTypes", 22), ("undefined-9999", 9999),
        ];
    }

    private static string Rep(string s, int n) => string.Concat(Enumerable.Repeat(s, n));

    /// <summary>R-STRING-1 rows of S and C (ID = index + 1): empty, Null, embedded U+0000, short/long, mixed compression, non-BMP.</summary>
    private static readonly (Arg T, Arg M)[] StrRows =
    [
        (A.Str(""), A.Str("")),
        (A.Null, A.Null),
        (A.Str("a\0b"), A.Str("a\0b")),
        (A.Str("\0"), A.Str("\0")),
        (A.Str("abc\0"), A.Str(new string('n', 4000) + "\0" + new string('n', 4000))),
        (A.Str("alpha"), A.Str("short memo")),
        (A.Str(new string('x', 255)), A.Str(new string('m', 5000))),
        (A.Str(Rep("Bä€", 85)), A.Str(Rep("Bä€", 3000))),
        (A.Str(Rep("\U0001D11E", 127)), A.Str(Rep("\U0001D11E", 1000))),
        (A.Str("tail"), A.Str(Rep("0123456789", 7000))),
        (A.Str(" lead and trail "), A.Str("\0\0\0")),
    ];

    public IEnumerable<Probe> Probes() =>
        Creations().Concat(StringProbes()).Concat(AdoProbes()).Concat(ActivationProbes());

    // Fixtures -------------------------------------------------------------------------------------------

    private static IEnumerable<Probe> Creations()
    {
        yield return Creation("str-accdb", "accdb", Dao.Version120, [.. StrRefs, "R-ADO-1"],
            "table S(ID LONG primary key, T TEXT(255), M MEMO) with the R-STRING-1 rows stored through a DAO table-type recordset AddNew/Value/Update; " +
            "then, through ADODB.Connection with the ACE provider, table C with T TEXT(255) WITH COMPRESSION and M MEMO WITH COMPRESSION created by Execute and the same rows stored through an ADO keyset recordset AddNew/Value/Update",
            StrTable, StrCompressedTable);
        yield return Creation("str-mdb", "mdb", Dao.Version40, [.. StrRefs, "R-ADO-1"],
            "the same S and C tables and rows as str-accdb, in the Jet 4 MDB format", StrTable, StrCompressedTable);
        yield return Creation("ado4-accdb", "accdb", Dao.Version120, ["R-ADO-1", "D-OBJECT-1"],
            "R-ADO-1 fixture set: K(ID COUNTER primary key, Code TEXT(10) unique, Long, Double, Currency, Decimal, Date, Text, Memo, LongBinary) with four rows " +
            "including a Null row and an empty-text row; J with a foreign key to K and two rows; D(N LONG) with ten rows; saved queries QParam (one Text parameter) and QEmpty",
            AdoFixture, null);
    }

    private static Probe Creation(string name, string ext, int version, string[] refs, string what, Action<P> daoBody, Action<P>? adoBody)
    {
        Arg file = A.Out($"{Card}-{name}.{ext}");
        return new Probe(Card, "create-" + name, refs,
            $"Fixture: {what}. Created in {ext.ToUpperInvariant()} by DBEngine.CreateDatabase(path, dbLangGeneral, {version}); every call's result is recorded and the closed file is kept.",
            p =>
            {
                p.Modules("before-create").Create().Modules("after-create").Get("eng", "Version")
                    .CallBind("db", "eng", "CreateDatabase", file, A.Str(Dao.LangGeneral), A.I4(version))
                    .Get("db", "Version");
                daoBody(p);
                p.Call("db", "Close").Release("db").Release("eng").Modules("after-dao-close");
                if (adoBody is not null)
                {
                    p.Create("ADODB.Connection", "cn").Put("cn", "Provider", A.Str(Ace));
                    SetDataSource(p, "cn", file);
                    p.Call("cn", "Open").Get("cn", "State");
                    adoBody(p);
                    AdoErrors(p, "cn");
                    p.Call("cn", "Close").Release("cn");
                }
                p.Modules("after-close").Files("after-close");
            })
        { CreatesFixture = true };
    }

    private static void StrTable(P p)
    {
        p.Call("db", "Execute", A.Str("CREATE TABLE S (ID LONG CONSTRAINT PK_S PRIMARY KEY, T TEXT(255), M MEMO)"))
            .OpenRs("rs", "db", "S", Dao.OpenTable).GetBind("fl", "rs", "Fields")
            .Item("fid", "fl", A.Str("ID")).Item("ft", "fl", A.Str("T")).Item("fm", "fl", A.Str("M"));
        for (int i = 0; i < StrRows.Length; i++)
        {
            p.Call("rs", "AddNew").Put("fid", "Value", A.I4(i + 1)).Put("ft", "Value", StrRows[i].T)
                .Put("fm", "Value", StrRows[i].M).Call("rs", "Update");
        }
        p.Release("fid").Release("ft").Release("fm").Release("fl").Call("rs", "Close").Release("rs");
    }

    private static void StrCompressedTable(P p)
    {
        p.Call("cn", "Execute", A.Str("CREATE TABLE C (ID LONG CONSTRAINT PK_C PRIMARY KEY, T TEXT(255) WITH COMPRESSION, M MEMO WITH COMPRESSION)"),
                A.Missing, A.I4(Ado.CmdText | Ado.ExecuteNoRecords))
            .Create("ADODB.Recordset", "ars")
            .Call("ars", "Open", A.Str("C"), A.Obj("cn"), A.I4(Ado.OpenKeyset), A.I4(Ado.LockOptimistic), A.I4(Ado.CmdTable))
            .GetBind("afl", "ars", "Fields").Item("aid", "afl", A.Str("ID")).Item("at", "afl", A.Str("T")).Item("am", "afl", A.Str("M"));
        for (int i = 0; i < StrRows.Length; i++)
        {
            p.Call("ars", "AddNew").Put("aid", "Value", A.I4(i + 1)).Put("at", "Value", StrRows[i].T)
                .Put("am", "Value", StrRows[i].M).Call("ars", "Update");
        }
        p.Release("aid").Release("at").Release("am").Release("afl").Call("ars", "Close").Release("ars");
    }

    private static void AdoFixture(P p)
    {
        p.Call("db", "Execute", A.Str("CREATE TABLE K (ID COUNTER CONSTRAINT PK_K PRIMARY KEY, Code TEXT(10) CONSTRAINT UX_K_Code UNIQUE, " +
                "L LONG, D DOUBLE, C CURRENCY, Dt DATETIME, T TEXT(50), M MEMO, B LONGBINARY)"))
            .AddDecimalField("K", "N")
            .Call("db", "Execute", A.Str("INSERT INTO K (Code, L, D, C, N, Dt, T, M) VALUES ('a1', 1, 1.5, 2.25, 3.125, #2024-01-02#, 'alpha', 'memo one')"))
            .Call("db", "Execute", A.Str("INSERT INTO K (Code) VALUES ('a2')"))
            .Call("db", "Execute", A.Str("INSERT INTO K (Code, L, D, C, N, Dt, T, M) VALUES ('b3', 0, -0.5, 0, 0, #1899-12-30 12:00:00#, '', '')"))
            .Call("db", "Execute", A.Str("INSERT INTO K (Code, L, D, C, N, Dt, T, M) VALUES ('b4', 2147483647, 1E300, 1234.5678, -0.0001, #9999-12-31#, 'beta', 'x')"))
            .OpenRs("rk", "db", "SELECT B FROM K WHERE Code = 'a1'", Dao.OpenDynaset)
            .GetBind("rkf", "rk", "Fields").Item("rkb", "rkf", A.I4(0))
            .Call("rk", "Edit").Put("rkb", "Value", A.Bytes(0x00, 0x01, 0x02, 0xff, 0x00)).Call("rk", "Update")
            .Release("rkb").Release("rkf").Call("rk", "Close").Release("rk")
            .Call("db", "Execute", A.Str("CREATE TABLE J (ID LONG CONSTRAINT PK_J PRIMARY KEY, KID LONG, [Note] TEXT(20))"))
            .AddRelation("FK_J_K", "K", "J", "ID", "KID")
            .Call("db", "Execute", A.Str("INSERT INTO J VALUES (1, 1, 'j1')"))
            .Call("db", "Execute", A.Str("INSERT INTO J VALUES (2, 1, 'j2')"))
            .Call("db", "Execute", A.Str("CREATE TABLE D (N LONG)"));
        for (int i = 0; i < 10; i++)
        {
            p.Call("db", "Execute", A.Str($"INSERT INTO D (N) VALUES ({i})"));
        }
        p.CallBind("q1", "db", "CreateQueryDef", A.Str("QParam"), A.Str("PARAMETERS pCode Text(10); SELECT ID, Code, T FROM K WHERE Code = pCode;"))
            .CallBind("q2", "db", "CreateQueryDef", A.Str("QEmpty"), A.Str("SELECT * FROM K WHERE 1=0;"))
            .Release("q1").Release("q2");
    }

    // R-STRING-1 -----------------------------------------------------------------------------------------

    private static IEnumerable<Probe> StringProbes()
    {
        foreach ((string fmt, string fixture) in new[] { ("accdb", StrAccdb), ("mdb", StrMdb) })
        {
            yield return Q($"str-ordinal-{fmt}", StrRefs,
                $"R-STRING-1 read once by ordinal ({fmt.ToUpperInvariant()}): DAO snapshots of S (DAO-written) and C (ADO-written WITH COMPRESSION) ordered by ID; " +
                "every Field.Value of every row is read through Fields.Item(i). Asks the VARIANT tag and exact BSTR byte length/payload of empty, Null, embedded U+0000, long Memo, mixed-compression and non-BMP values.",
                fixture, p =>
                {
                    p.Engine().OpenDb();
                    DaoRead(p, "rs", "SELECT * FROM S ORDER BY ID");
                    DaoRead(p, "rc", "SELECT * FROM C ORDER BY ID");
                    p.Finish("db");
                });
        }

        yield return Q("str-named-retained", StrRefs,
            "R-STRING-1 by name, repeated and alternating, through retained Field objects: Fields(\"ID\"/\"T\"/\"M\") are bound once before the loop; on every row " +
            "ID, T, M, T, M are read through those retained Fields, then M through a fresh Fields(\"M\") and T through a fresh Fields(1), then MoveNext. Asks whether retained and fresh Field objects return identical BSTRs on every row and repeat.",
            StrAccdb, p => p.Engine().OpenDb()
                .OpenRs("rs", "db", "SELECT ID, T, M FROM S ORDER BY ID", Dao.OpenSnapshot)
                .GetBind("fl", "rs", "Fields").Item("fid", "fl", A.Str("ID")).Item("ft", "fl", A.Str("T")).Item("fm", "fl", A.Str("M"))
                .Until("rs", "EOF", 16, b => b.Get("fid", "Value").Get("ft", "Value").Get("fm", "Value").Get("ft", "Value").Get("fm", "Value")
                    .Item("fx", "fl", A.Str("M")).Get("fx", "Value").Release("fx")
                    .Item("fo", "fl", A.I4(1)).Get("fo", "Value").Release("fo")
                    .Call("rs", "MoveNext"))
                .Release("fid").Release("ft").Release("fm").Release("fl").Call("rs", "Close").Release("rs").Finish("db"));

        yield return Q("str-after-eof-close", StrRefs,
            "R-STRING-1 retained Field after movement and Close: with Fields(\"T\") and Fields(\"M\") of a snapshot bound, read both at MoveLast, after MoveNext to EOF, " +
            "after MoveFirst, and after Recordset.Close (Value, Name and Fields.Count), then MoveFirst on the closed recordset. Asks what a retained Field returns or raises in each state.",
            StrAccdb, p => p.Engine().OpenDb()
                .OpenRs("rs", "db", "SELECT ID, T, M FROM S ORDER BY ID", Dao.OpenSnapshot)
                .GetBind("fl", "rs", "Fields").Item("ft", "fl", A.Str("T")).Item("fm", "fl", A.Str("M"))
                .Call("rs", "MoveLast").Get("ft", "Value").Get("fm", "Value")
                .Call("rs", "MoveNext").Get("rs", "EOF").Get("ft", "Value").Get("fm", "Value")
                .Call("rs", "MoveFirst").Get("ft", "Value").Get("fm", "Value")
                .Call("rs", "Close").Get("ft", "Value").Get("ft", "Name").Get("fm", "Value").Get("fl", "Count").Call("rs", "MoveFirst")
                .Release("ft").Release("fm").Release("fl").Release("rs").Finish("db"));

        yield return Q("str-projection", StrRefs,
            "R-STRING-1 projected-column count and engine string functions: one snapshot projecting only M, then one projecting ID, M, T, M AS M2, T & '' AS T2, " +
            "Len(T), Len(M), Mid(M, 3999, 4) and InStr(T, Chr(0)); every value of every row is read by ordinal. Asks whether projection width or duplicated columns change returned BSTRs, " +
            "and what lengths and positions the engine computes for embedded U+0000 and non-BMP text.",
            StrAccdb, p =>
            {
                p.Engine().OpenDb();
                DaoRead(p, "r1", "SELECT M FROM S ORDER BY ID");
                DaoRead(p, "r2", "SELECT ID, M, T, M AS M2, T & '' AS T2, Len(T) AS LT, Len(M) AS LM, Mid(M, 3999, 4) AS MM, InStr(T, Chr(0)) AS NT FROM S ORDER BY ID");
                p.Finish("db");
            });

        yield return Q("str-revisit", StrRefs,
            "R-STRING-1 revisiting rows: on a snapshot with retained ID/T/M Fields, read ID, T, M and AbsolutePosition after MoveLast, MoveFirst, Move 4, MovePrevious, " +
            "FindFirst \"ID = 10\", FindPrevious \"Len(M) > 1000\", FindFirst \"ID = 10\" again, FindFirst and FindNext \"InStr(T, Chr(0)) > 0\" (NoMatch read after each Find). " +
            "Asks whether a revisited row returns the same BSTRs and which rows the criteria over U+0000 select.",
            StrAccdb, p =>
            {
                p.Engine().OpenDb().OpenRs("rs", "db", "SELECT ID, T, M FROM S ORDER BY ID", Dao.OpenSnapshot)
                    .GetBind("fl", "rs", "Fields").Item("fid", "fl", A.Str("ID")).Item("ft", "fl", A.Str("T")).Item("fm", "fl", A.Str("M"));
                void Look(P q) => q.Get("fid", "Value").Get("ft", "Value").Get("fm", "Value").Get("rs", "AbsolutePosition");
                p.Call("rs", "MoveLast"); Look(p);
                p.Call("rs", "MoveFirst"); Look(p);
                p.Call("rs", "Move", A.I4(4)); Look(p);
                p.Call("rs", "MovePrevious"); Look(p);
                p.Call("rs", "FindFirst", A.Str("ID = 10")).Get("rs", "NoMatch"); Look(p);
                p.Call("rs", "FindPrevious", A.Str("Len(M) > 1000")).Get("rs", "NoMatch"); Look(p);
                p.Call("rs", "FindFirst", A.Str("ID = 10")).Get("rs", "NoMatch"); Look(p);
                p.Call("rs", "FindFirst", A.Str("InStr(T, Chr(0)) > 0")).Get("rs", "NoMatch"); Look(p);
                p.Call("rs", "FindNext", A.Str("InStr(T, Chr(0)) > 0")).Get("rs", "NoMatch"); Look(p);
                p.Release("fid").Release("ft").Release("fm").Release("fl").Call("rs", "Close").Release("rs").Finish("db");
            });

        yield return Q("str-memo-chunks", StrRefs,
            "R-STRING-1 long-value sizing: for rows 1, 2, 3, 5, 8, 9, 10, 11 (FindFirst by ID), call M.FieldSize and M.GetChunk at (offset 0, 16 bytes), (7999, 5) odd offset, " +
            "(8000, 4), (0, 0) and (1000000, 4); then FieldSize and GetChunk(0, 4) on the Text field T of row 3. Asks the reported sizes, returned tags/byte lengths, and errors.",
            StrAccdb, p =>
            {
                p.Engine().OpenDb().OpenRs("rs", "db", "SELECT ID, T, M FROM S ORDER BY ID", Dao.OpenSnapshot)
                    .GetBind("fl", "rs", "Fields").Item("ft", "fl", A.Str("T")).Item("fm", "fl", A.Str("M"));
                foreach (int id in new[] { 1, 2, 3, 5, 8, 9, 10, 11 })
                {
                    p.Call("rs", "FindFirst", A.Str($"ID = {id}")).Call("fm", "FieldSize")
                        .Call("fm", "GetChunk", A.I4(0), A.I4(16)).Call("fm", "GetChunk", A.I4(7999), A.I4(5))
                        .Call("fm", "GetChunk", A.I4(8000), A.I4(4)).Call("fm", "GetChunk", A.I4(0), A.I4(0))
                        .Call("fm", "GetChunk", A.I4(1000000), A.I4(4));
                }
                p.Call("rs", "FindFirst", A.Str("ID = 3")).Call("ft", "FieldSize").Call("ft", "GetChunk", A.I4(0), A.I4(4))
                    .Release("ft").Release("fm").Release("fl").Call("rs", "Close").Release("rs").Finish("db");
            });

        yield return Q("str-edit-requery", StrRefs,
            "R-STRING-1 edits and Requery (own fixture copy, read-write): on a dynaset at ID 6, Edit; set T = \"x\\0y\\0\" and M = 6000 'e' + U+0000; read T, M and EditMode before and after Update; " +
            "Requery, FindFirst ID 6, read again, and read the row through a fresh snapshot with Len(T)/Len(M); then Edit with a 256-unit T and Update, read EditMode, CancelUpdate, read T; " +
            "then Edit to T = Null, M = \"\", Update and read. Asks what is stored, returned and refused at each step.",
            StrAccdb, p => p.Engine().OpenDb(readOnly: false)
                .OpenRs("rs", "db", "SELECT ID, T, M FROM S ORDER BY ID", Dao.OpenDynaset)
                .GetBind("fl", "rs", "Fields").Item("ft", "fl", A.Str("T")).Item("fm", "fl", A.Str("M"))
                .Call("rs", "FindFirst", A.Str("ID = 6")).Call("rs", "Edit")
                .Put("ft", "Value", A.Str("x\0y\0")).Put("fm", "Value", A.Str(new string('e', 6000) + "\0"))
                .Get("ft", "Value").Get("fm", "Value").Get("rs", "EditMode").Call("rs", "Update").Get("rs", "EditMode")
                .Get("ft", "Value").Get("fm", "Value")
                .Call("rs", "Requery").Call("rs", "FindFirst", A.Str("ID = 6")).Get("ft", "Value").Get("fm", "Value")
                .OpenRs("rc", "db", "SELECT ID, T, M, Len(T) AS LT, Len(M) AS LM FROM S WHERE ID = 6", Dao.OpenSnapshot)
                .ReadAll("rc", 4, 8).Call("rc", "Close").Release("rc")
                .Call("rs", "Edit").Put("ft", "Value", A.Str(new string('z', 256))).Call("rs", "Update").Get("rs", "EditMode")
                .Call("rs", "CancelUpdate").Get("ft", "Value")
                .Call("rs", "Edit").Put("ft", "Value", A.Null).Put("fm", "Value", A.Str("")).Call("rs", "Update")
                .Get("ft", "Value").Get("fm", "Value")
                .Release("ft").Release("fm").Release("fl").Call("rs", "Close").Release("rs").Finish("db"));

        yield return Q("str-schema-change", StrRefs,
            "R-STRING-1 schema-generation change (read-write copy): with a dynaset on S open and Fields(\"T\") retained, read T; execute ALTER TABLE S ADD COLUMN X TEXT(10) and read DBEngine.Errors; " +
            "read T, MoveNext, read T, Requery, read T and Fields.Count; Close; repeat the ALTER with no recordset open; UPDATE X = Left(T, 10) for IDs 3-5 and read ID, X, Len(X). " +
            "Asks whether the open recordset blocks the change and what the retained Field and copied values return.",
            StrAccdb, p => p.Engine().OpenDb(readOnly: false)
                .OpenRs("rs", "db", "SELECT ID, T FROM S ORDER BY ID", Dao.OpenDynaset)
                .GetBind("fl", "rs", "Fields").Item("ft", "fl", A.Str("T")).Get("ft", "Value")
                .Call("db", "Execute", A.Str("ALTER TABLE S ADD COLUMN X TEXT(10)")).Errors()
                .Get("ft", "Value").Call("rs", "MoveNext").Get("ft", "Value").Call("rs", "Requery").Get("ft", "Value")
                .GetBind("fl2", "rs", "Fields").Get("fl2", "Count").Release("fl2")
                .Release("ft").Release("fl").Call("rs", "Close").Release("rs")
                .Call("db", "Execute", A.Str("ALTER TABLE S ADD COLUMN X TEXT(10)")).Errors()
                .Call("db", "Execute", A.Str("UPDATE S SET X = Left(T, 10) WHERE ID IN (3, 4, 5)"))
                .OpenRs("rx", "db", "SELECT ID, X, Len(X) AS LX FROM S WHERE ID IN (3, 4, 5) ORDER BY ID", Dao.OpenSnapshot)
                .ReadAll("rx", 8, 8).Call("rx", "Close").Release("rx").Finish("db"));

        yield return Q("str-ado-read", [.. StrRefs, "R-ADO-1", "ADO-BULK"],
            "R-STRING-1 through ADO: on an ACE OLE DB connection (Mode=Read), read every Field.Value and ActualSize of S and of C through server forward-only read-only recordsets; " +
            "then GetString(adClipString, -1, \"|\", \"\\n\", \"<null>\") over a client static recordset of ID, T and one of ID, M. Asks the ADO tags, BSTR byte lengths, ActualSize and GetString text for the same stored values.",
            StrAccdb, p =>
            {
                p.Modules("before-create");
                AdoOpen(p, "cn");
                p.Modules("after-open");
                AdoRs(p, "rs", "SELECT * FROM S ORDER BY ID", "cn", Ado.UseServer, Ado.OpenForwardOnly, Ado.LockReadOnly, Ado.CmdText);
                AdoReadAll(p, "rs", 16, 8, actualSize: true);
                p.Call("rs", "Close").Release("rs");
                AdoRs(p, "rc", "SELECT * FROM C ORDER BY ID", "cn", Ado.UseServer, Ado.OpenForwardOnly, Ado.LockReadOnly, Ado.CmdText);
                AdoReadAll(p, "rc", 16, 8, actualSize: true);
                p.Call("rc", "Close").Release("rc");
                foreach ((string bind, string sql) in new[] { ("g1", "SELECT ID, T FROM S ORDER BY ID"), ("g2", "SELECT ID, M FROM S ORDER BY ID") })
                {
                    AdoRs(p, bind, sql, "cn", Ado.UseClient, Ado.OpenStatic, Ado.LockReadOnly, Ado.CmdText);
                    p.Call(bind, "GetString", A.I4(Ado.ClipString), A.I4(-1), A.Str("|"), A.Str("\n"), A.Str("<null>")).Call(bind, "Close").Release(bind);
                }
                AdoErrors(p, "cn");
                p.Finish("cn");
            });
    }

    // R-ADO-1 ---------------------------------------------------------------------------------------------

    private static IEnumerable<Probe> AdoProbes()
    {
        yield return Bare("ado-describe", ["R-ADO-1", "R-ACT-1", "ACT-META", "ACT-DECL"],
            "ADO type library inventory: create ADODB.Connection and describe its whole containing type library through ITypeInfo (every type, interface, dispid, invoke kind, vtable slot and parameter type).",
            p => p.Modules("before-create").Create("ADODB.Connection", "cn").Modules("after-create").Describe("cn", wholeLibrary: true).Release("cn"));

        yield return Q("ado-open-provider-property", ["R-ADO-1", "ADO-CONN", "ADO-PROPS"],
            "ADO-CONN/ADO-PROPS: on a fresh Connection read State, Provider, ConnectionString, Mode, CursorLocation, timeouts, Version, Attributes, IsolationLevel and the full Properties inventory (Name, Type, Value, Attributes); " +
            "set Provider = ACE and inventory again; set Properties(\"Data Source\") to the fixture path through a retained Property wrapper and Mode = adModeRead; Open; read the same state and inventory; " +
            "read the retained Data Source wrapper; Close; read state, the wrapper, Properties.Count and inventory once more. Asks which properties exist and what they hold in each state.",
            AdoAccdb, p =>
            {
                p.Modules("before-create").Create("ADODB.Connection", "cn").Modules("after-create");
                ConnState(p, "cn");
                Props(p, "cn", "fresh");
                p.Put("cn", "Provider", A.Str(Ace)).Get("cn", "Provider").Get("cn", "ConnectionString");
                Props(p, "cn", "provider");
                p.GetBind("cnp", "cn", "Properties").Item("ds", "cnp", A.Str("Data Source")).Put("ds", "Value", A.In())
                    .Get("ds", "Value").Get("ds", "Attributes")
                    .Put("cn", "Mode", A.I4(Ado.ModeRead)).Call("cn", "Open").Modules("after-open");
                ConnState(p, "cn");
                p.Get("cn", "DefaultDatabase");
                Props(p, "cn", "open");
                p.Get("ds", "Value");
                AdoErrors(p, "cn");
                p.Call("cn", "Close");
                ConnState(p, "cn");
                p.Get("ds", "Value").Get("cnp", "Count");
                Props(p, "cn", "closed");
                p.Release("ds").Release("cnp").Modules("after-close").Files("after-close");
            });

        yield return Q("ado-open-alt-orders", ["R-ADO-1", "ADO-CONN", "ADO-PROVIDER"],
            "ADO-CONN assignment orders: (a) ConnectionString = \"Provider=ACE;Mode=Read\" with no Data Source, then Properties(\"Data Source\") = path, Open(omitted); " +
            "(b) Provider property = ACE, then Open(path) with the bare file path as the whole connection string; (c) Open(\"Provider=ACE;Mode=Read\") with no Data Source anywhere. " +
            "Each on its own Connection: State, Provider, ConnectionString and Errors after Open, then Close. Asks which orders open the file and what each records.",
            AdoAccdb, p =>
            {
                p.Create("ADODB.Connection", "ca").Put("ca", "ConnectionString", A.Str($"Provider={Ace};Mode=Read")).Get("ca", "Provider");
                SetDataSource(p, "ca", A.In());
                p.Call("ca", "Open", A.Missing);
                OpenedState(p, "ca");
                p.Create("ADODB.Connection", "cb").Put("cb", "Provider", A.Str(Ace)).Call("cb", "Open", A.In());
                OpenedState(p, "cb");
                p.Create("ADODB.Connection", "cc").Call("cc", "Open", A.Str($"Provider={Ace};Mode=Read"));
                OpenedState(p, "cc");
                p.Files("after-close");
            });

        yield return Q("ado-connstring-keys", ["R-ADO-1", "ADO-CONN"],
            "ADO-CONN key parsing, each on its own Connection with Data Source set through Properties and then Open(omitted): (a) a duplicated Provider key with spaces around \"Mode = Read\"; " +
            "(b) an unknown key CalcDbUnknownOption=1; (c) double-quoted Provider value and single-quoted Mode value; (d) an empty value Jet OLEDB:Database Password=. " +
            "Asks the ConnectionString before and after Open, Mode, State and Errors for each.",
            AdoAccdb, p =>
            {
                (string bind, string text)[] cases =
                [
                    ("ka", $"Provider={Ace};Provider={Ace};  Mode = Read ; Persist Security Info=False;"),
                    ("kb", $"Provider={Ace};Mode=Read;CalcDbUnknownOption=1"),
                    ("kc", $"Provider=\"{Ace}\";Mode='Read'"),
                    ("kd", $"Provider={Ace};Jet OLEDB:Database Password=;Mode=Read"),
                ];
                foreach ((string bind, string text) in cases)
                {
                    p.Create("ADODB.Connection", bind).Put(bind, "ConnectionString", A.Str(text)).Get(bind, "ConnectionString").Get(bind, "Provider");
                    SetDataSource(p, bind, A.In());
                    p.Call(bind, "Open", A.Missing).Get(bind, "Mode");
                    OpenedState(p, bind);
                }
            });

        yield return Bare("ado-provider-non-ace", ["R-ADO-1", "ADO-PROVIDER", "R-ACT-1", "ACT-ADO-SCOPE"],
            "ADO-PROVIDER non-ACE and omitted providers, all local (ODBC DSN lookup only): (a) \"Provider=MSDASQL;Data Source=CalcDbNoSuchDsn\"; (b) \"Data Source=CalcDbNoSuchDsn\" with no Provider key " +
            "(Provider read before and after Open); (c) Open with no connection string at all. Asks the effective Provider, the Open outcome and the Errors collection for each, with loaded modules.",
            p =>
            {
                p.Modules("before-create");
                p.Create("ADODB.Connection", "na").Put("na", "ConnectionString", A.Str("Provider=MSDASQL;Data Source=CalcDbNoSuchDsn")).Get("na", "Provider")
                    .Call("na", "Open", A.Missing).Modules("after-msdasql");
                OpenedState(p, "na");
                p.Create("ADODB.Connection", "nb").Get("nb", "Provider").Put("nb", "ConnectionString", A.Str("Data Source=CalcDbNoSuchDsn")).Get("nb", "Provider")
                    .Call("nb", "Open", A.Missing).Modules("after-omitted");
                OpenedState(p, "nb");
                p.Create("ADODB.Connection", "nc").Call("nc", "Open").Modules("after-empty");
                OpenedState(p, "nc");
            });

        if (MissingNameRoutes)
        {
            yield return Bare("ado-provider-missing-names", ["R-ADO-1", "ADO-PROVIDER", "R-ACT-1", "ACT-MISSING"],
                "ADO-PROVIDER names that may be absent from the x64 process (isolated environment only, ACT-MISSING): Provider = Microsoft.Jet.OLEDB.4.0, CalcDb.NoSuch.Provider, and \"Provider=;\" (empty). " +
                "Each with Data Source = a nonexistent work file; asks the Provider assignment, Open outcome and Errors.",
                p =>
                {
                    (string bind, string text)[] cases =
                    [
                        ("ma", "Provider=Microsoft.Jet.OLEDB.4.0"), ("mb", "Provider=CalcDb.NoSuch.Provider"), ("mc", "Provider=;"),
                    ];
                    foreach ((string bind, string text) in cases)
                    {
                        p.Create("ADODB.Connection", bind).Put(bind, "ConnectionString", A.Str(text)).Get(bind, "Provider");
                        SetDataSource(p, bind, A.Out("absent.mdb"));
                        p.Call(bind, "Open", A.Missing).Modules("after-" + bind);
                        OpenedState(p, bind);
                    }
                });
        }

        yield return Q("ado-execute-results", ["R-ADO-1", "ADO-RESULTS"],
            "ADO-RESULTS on a read-mode ACE connection, Connection.Execute of: a row SELECT (State, BOF, EOF, cursor/lock/location, RecordCount, then all rows); a SELECT with no rows; the saved query QEmpty by name; " +
            "a compound \"SELECT 1 AS A; SELECT 2 AS B\" followed by NextRecordset; an UPDATE on the read-mode connection; a SELECT with adExecuteNoRecords. Asks which return an open empty, closed or no Recordset, and the errors.",
            AdoAccdb, p =>
            {
                AdoOpen(p, "cn");
                p.CallBind("r1", "cn", "Execute", A.Str("SELECT ID, Code, T FROM K ORDER BY ID"), A.Missing, A.I4(Ado.CmdText));
                RsState(p, "r1");
                p.Get("r1", "RecordCount");
                AdoReadAll(p, "r1", 8, 8);
                p.Call("r1", "Close").Release("r1");
                p.CallBind("r2", "cn", "Execute", A.Str("SELECT * FROM K WHERE 1=0"), A.Missing, A.I4(Ado.CmdText));
                RsState(p, "r2");
                p.GetBind("r2f", "r2", "Fields").Get("r2f", "Count").Release("r2f").Call("r2", "Close").Release("r2");
                p.CallBind("r3", "cn", "Execute", A.Str("QEmpty"));
                RsState(p, "r3");
                p.Call("r3", "Close").Release("r3");
                p.CallBind("r4", "cn", "Execute", A.Str("SELECT 1 AS A; SELECT 2 AS B"), A.Missing, A.I4(Ado.CmdText));
                RsState(p, "r4");
                AdoReadAll(p, "r4", 4, 4);
                p.CallBind("r5", "r4", "NextRecordset");
                RsState(p, "r5");
                AdoErrors(p, "cn");
                p.CallBind("r6", "cn", "Execute", A.Str("UPDATE K SET T = T WHERE Code = 'a1'"), A.Missing, A.I4(Ado.CmdText)).Get("r6", "State");
                AdoErrors(p, "cn");
                p.CallBind("r7", "cn", "Execute", A.Str("SELECT ID FROM K"), A.Missing, A.I4(Ado.CmdText | Ado.ExecuteNoRecords)).Get("r7", "State");
                p.Finish("cn");
            });

        yield return Q("ado-execute-action", ["R-ADO-1", "ADO-RESULTS"],
            "ADO-RESULTS action commands on a read-write ACE connection (own copy): UPDATE returning a Recordset (State read); INSERT with adExecuteNoRecords; DELETE of J row 2; " +
            "DELETE of K row a1 that J still references (Errors read); SELECT @@IDENTITY; then all ID, Code, T of K. Asks each return value, the errors and the resulting rows.",
            AdoAccdb, p =>
            {
                AdoOpen(p, "cn", readOnly: false);
                p.CallBind("ra", "cn", "Execute", A.Str("UPDATE K SET T = 'upd' WHERE Code = 'a1'"), A.Missing, A.I4(Ado.CmdText)).Get("ra", "State")
                    .Call("cn", "Execute", A.Str("INSERT INTO K (Code, T) VALUES ('c5', 'ins')"), A.Missing, A.I4(Ado.CmdText | Ado.ExecuteNoRecords))
                    .Call("cn", "Execute", A.Str("DELETE FROM J WHERE ID = 2"), A.Missing, A.I4(Ado.CmdText))
                    .Call("cn", "Execute", A.Str("DELETE FROM K WHERE Code = 'a1'"), A.Missing, A.I4(Ado.CmdText));
                AdoErrors(p, "cn");
                p.CallBind("rid", "cn", "Execute", A.Str("SELECT @@IDENTITY"), A.Missing, A.I4(Ado.CmdText));
                AdoReadAll(p, "rid", 2, 2);
                p.CallBind("rk", "cn", "Execute", A.Str("SELECT ID, Code, T FROM K ORDER BY ID"), A.Missing, A.I4(Ado.CmdText));
                AdoReadAll(p, "rk", 8, 4);
                p.Finish("cn");
            });

        yield return Q("ado-rs-cursors", ["R-ADO-1", "ADO-CURSOR"],
            "ADO-CURSOR requested versus effective settings on a read-mode ACE connection: for eight (CursorLocation, CursorType, LockType, Options, source) requests — server forward/keyset/dynamic/static, " +
            "client static batch, client forward, adCmdTable and adCmdTableDirect on K — read effective CursorType, LockType, CursorLocation, State, RecordCount, CacheSize, BOF, EOF, every listed Supports option, " +
            "then MoveLast, AbsolutePosition and RecordCount, then Close.",
            AdoAccdb, p =>
            {
                AdoOpen(p, "cn");
                (string bind, int loc, int type, int lck, int opts, string src)[] cases =
                [
                    ("c1", Ado.UseServer, Ado.OpenForwardOnly, Ado.LockReadOnly, Ado.CmdText, "SELECT * FROM K"),
                    ("c2", Ado.UseServer, Ado.OpenKeyset, Ado.LockOptimistic, Ado.CmdText, "SELECT * FROM K"),
                    ("c3", Ado.UseServer, Ado.OpenDynamic, Ado.LockPessimistic, Ado.CmdText, "SELECT * FROM K"),
                    ("c4", Ado.UseServer, Ado.OpenStatic, Ado.LockReadOnly, Ado.CmdText, "SELECT * FROM K"),
                    ("c5", Ado.UseClient, Ado.OpenStatic, Ado.LockBatchOptimistic, Ado.CmdText, "SELECT * FROM K"),
                    ("c6", Ado.UseClient, Ado.OpenForwardOnly, Ado.LockReadOnly, Ado.CmdText, "SELECT * FROM K"),
                    ("c7", Ado.UseServer, Ado.OpenKeyset, Ado.LockReadOnly, Ado.CmdTable, "K"),
                    ("c8", Ado.UseServer, Ado.OpenKeyset, Ado.LockReadOnly, Ado.CmdTableDirect, "K"),
                ];
                foreach (var c in cases)
                {
                    AdoRs(p, c.bind, c.src, "cn", c.loc, c.type, c.lck, c.opts);
                    RsState(p, c.bind);
                    p.Get(c.bind, "RecordCount").Get(c.bind, "CacheSize");
                    foreach ((string _, int value) in Ado.Supports)
                    {
                        p.Call(c.bind, "Supports", A.I4(value));
                    }
                    p.Call(c.bind, "MoveLast").Get(c.bind, "AbsolutePosition").Get(c.bind, "RecordCount").Call(c.bind, "Close").Release(c.bind);
                }
                AdoErrors(p, "cn");
                p.Finish("cn");
            });

        yield return Q("ado-rs-read-close", ["R-ADO-1", "ADO-RESULTS", "ADO-CONN"],
            "ADO Recordset open/read/close and closed-object behaviour: client static read-only recordset on K; field metadata (Name, Type, DefinedSize, Precision, NumericScale, Attributes); every Value and ActualSize of every row; " +
            "with Fields and Fields(\"T\") retained, Close; then State, the retained Field's Value and Name, Fields.Count, a second Close, MoveFirst on the closed recordset, reopen with the same arguments and State. Asks each outcome.",
            AdoAccdb, p =>
            {
                AdoOpen(p, "cn");
                AdoRs(p, "rs", "SELECT * FROM K ORDER BY ID", "cn", Ado.UseClient, Ado.OpenStatic, Ado.LockReadOnly, Ado.CmdText);
                p.GetBind("rf", "rs", "Fields").Each("rf", "fd", 16, b => b.Get("fd", "Name").Get("fd", "Type").Get("fd", "DefinedSize")
                    .Get("fd", "Precision").Get("fd", "NumericScale").Get("fd", "Attributes"))
                    .Item("kt", "rf", A.Str("T"));
                AdoReadAll(p, "rs", 8, 16, actualSize: true);
                p.Call("rs", "Close").Get("rs", "State").Get("kt", "Value").Get("kt", "Name").Get("rf", "Count")
                    .Call("rs", "Close").Call("rs", "MoveFirst")
                    .Call("rs", "Open", A.Str("SELECT * FROM K ORDER BY ID"), A.Obj("cn"), A.I4(Ado.OpenStatic), A.I4(Ado.LockReadOnly), A.I4(Ado.CmdText))
                    .Get("rs", "State").Get("kt", "Value").Release("kt").Release("rf").Call("rs", "Close").Release("rs");
                AdoErrors(p, "cn");
                p.Finish("cn");
            });

        yield return Q("ado-errors", ["R-ADO-1", "R-ERR-1", "ADO-RESULTS"],
            "ADO Connection.Errors with ACE: Execute \"SELEC * FROM K\", \"SELECT * FROM NoSuchTable\", \"SELECT NoSuchColumn FROM K\" and a Recordset.Open of bad SQL, reading Errors (Number, Description, Source, SQLState, NativeError) after each; " +
            "then Errors.Clear and Count; then a successful SELECT and Errors again. Asks the entries, their order, and whether success or Clear empties the collection.",
            AdoAccdb, p =>
            {
                AdoOpen(p, "cn");
                foreach (string sql in new[] { "SELEC * FROM K", "SELECT * FROM NoSuchTable", "SELECT NoSuchColumn FROM K" })
                {
                    p.Call("cn", "Execute", A.Str(sql), A.Missing, A.I4(Ado.CmdText));
                    AdoErrors(p, "cn");
                }
                AdoRs(p, "rb", "SELECT FROM WHERE", "cn", Ado.UseServer, Ado.OpenForwardOnly, Ado.LockReadOnly, Ado.CmdText);
                AdoErrors(p, "cn");
                p.GetBind("ce", "cn", "Errors").Call("ce", "Clear").Get("ce", "Count").Release("ce")
                    .CallBind("ok", "cn", "Execute", A.Str("SELECT ID FROM K"), A.Missing, A.I4(Ado.CmdText)).Get("ok", "State");
                AdoErrors(p, "cn");
                p.Finish("cn");
            });

        yield return Q("ado-session", ["R-ADO-1", "ADO-SESSION"],
            "ADO-SESSION: two Recordsets and a Command (ActiveConnection set by reference) on one ACE Connection, reading each ActiveConnection (object identity captured); a second Connection with the identical configuration " +
            "gets a failing Execute and both Errors collections are read; then the first Connection is closed and the State of each Recordset, the Command's ActiveConnection and the second Connection are read.",
            AdoAccdb, p =>
            {
                AdoOpen(p, "cn");
                AdoRs(p, "rs1", "SELECT * FROM K ORDER BY ID", "cn", Ado.UseServer, Ado.OpenKeyset, Ado.LockReadOnly, Ado.CmdText);
                AdoRs(p, "rs2", "SELECT * FROM J", "cn", Ado.UseServer, Ado.OpenForwardOnly, Ado.LockReadOnly, Ado.CmdText);
                p.GetBind("ac1", "rs1", "ActiveConnection").Get("ac1", "State").GetBind("ac2", "rs2", "ActiveConnection")
                    .Create("ADODB.Command", "cmd").PutRef("cmd", "ActiveConnection", "cn").Put("cmd", "CommandText", A.Str("SELECT * FROM D"))
                    .CallBind("rs3", "cmd", "Execute").Get("rs3", "State").GetBind("ac3", "rs3", "ActiveConnection").GetBind("acc", "cmd", "ActiveConnection");
                AdoOpen(p, "cn2");
                p.Call("cn2", "Execute", A.Str("SELEC bad"), A.Missing, A.I4(Ado.CmdText));
                AdoErrors(p, "cn2");
                AdoErrors(p, "cn");
                p.Call("cn", "Close").Get("rs1", "State").Get("rs2", "State").Get("rs3", "State").Get("ac1", "State")
                    .Get("cmd", "ActiveConnection").Get("cmd", "State").Get("cn2", "State")
                    .Release("ac1").Release("ac2").Release("ac3").Release("acc").Release("rs1").Release("rs2").Release("rs3").Release("cmd");
                p.Finish("cn2");
            });

        yield return Q("ado-params", ["R-ADO-1", "ADO-PARAM"],
            "ADO-PARAM: (a) Command for saved query QParam (adCmdStoredProc): Parameters.Count on first access, each Name/Type/Direction/Size/Precision/NumericScale/Value, Refresh and the inventory again, " +
            "set Parameters(0) = \"a1\", Execute and read rows; (b) Command text with two '?' markers: Refresh inventory, then two CreateParameter(adVarWChar, input, 10) appended with \"a2\"/\"b3\", Execute and read; " +
            "Delete \"x\", Count, Execute again and Errors. Asks discovered versus manual parameters and their binding.",
            AdoAccdb, p =>
            {
                AdoOpen(p, "cn");
                p.Create("ADODB.Command", "cq").PutRef("cq", "ActiveConnection", "cn").Put("cq", "CommandText", A.Str("QParam"))
                    .Put("cq", "CommandType", A.I4(Ado.CmdStoredProc)).GetBind("pq", "cq", "Parameters");
                ParamInventory(p, "pq");
                p.Call("pq", "Refresh");
                ParamInventory(p, "pq");
                p.Item("p0", "pq", A.I4(0)).Put("p0", "Value", A.Str("a1")).CallBind("rq", "cq", "Execute");
                AdoReadAll(p, "rq", 4, 4);
                p.Release("p0").Release("rq").Release("pq");
                p.Create("ADODB.Command", "cp").PutRef("cp", "ActiveConnection", "cn")
                    .Put("cp", "CommandText", A.Str("SELECT ID, Code FROM K WHERE Code = ? OR Code = ?")).Put("cp", "CommandType", A.I4(Ado.CmdText))
                    .GetBind("pp", "cp", "Parameters").Call("pp", "Refresh");
                ParamInventory(p, "pp");
                p.CallBind("px", "cp", "CreateParameter", A.Str("x"), A.I4(Ado.VarWChar), A.I4(Ado.ParamInput), A.I4(10), A.Str("a2")).Call("pp", "Append", A.Obj("px"))
                    .CallBind("py", "cp", "CreateParameter", A.Str("y"), A.I4(Ado.VarWChar), A.I4(Ado.ParamInput), A.I4(10), A.Str("b3")).Call("pp", "Append", A.Obj("py"));
                ParamInventory(p, "pp");
                p.CallBind("rp", "cp", "Execute");
                AdoReadAll(p, "rp", 8, 4);
                p.Call("pp", "Delete", A.Str("x")).Get("pp", "Count").CallBind("rp2", "cp", "Execute").Get("rp2", "State");
                AdoErrors(p, "cn");
                p.Release("px").Release("py").Release("pp").Finish("cn");
            });

        yield return Q("ado-edit", ["R-ADO-1", "ADO-EDIT", "ADO-VALUES"],
            "ADO-EDIT/ADO-VALUES (read-write copy): server keyset optimistic recordset on K; with Fields(\"T\") retained, read EditMode; set T = \"edited\" and read Value, OriginalValue, UnderlyingValue and EditMode before and after Update; " +
            "set T = \"moved\" then MoveNext (EditMode after), MovePrevious and read T; set T = \"cancel\", CancelUpdate and read T; AddNew with Code = \"n6\", Update and read ID; Close; then read ID, Code, T of K through Execute.",
            AdoAccdb, p =>
            {
                AdoOpen(p, "cn", readOnly: false);
                AdoRs(p, "rs", "SELECT * FROM K ORDER BY ID", "cn", Ado.UseServer, Ado.OpenKeyset, Ado.LockOptimistic, Ado.CmdText);
                p.GetBind("fl", "rs", "Fields").Item("ft", "fl", A.Str("T")).Item("fc", "fl", A.Str("Code")).Item("fi", "fl", A.Str("ID"))
                    .Get("rs", "EditMode").Put("ft", "Value", A.Str("edited"));
                ValueTriple(p, "ft");
                p.Get("rs", "EditMode").Call("rs", "Update").Get("rs", "EditMode");
                ValueTriple(p, "ft");
                p.Put("ft", "Value", A.Str("moved")).Call("rs", "MoveNext").Get("rs", "EditMode").Call("rs", "MovePrevious").Get("ft", "Value")
                    .Put("ft", "Value", A.Str("cancel")).Call("rs", "CancelUpdate").Get("rs", "EditMode").Get("ft", "Value")
                    .Call("rs", "AddNew").Get("rs", "EditMode").Put("fc", "Value", A.Str("n6")).Call("rs", "Update").Get("fi", "Value").Get("rs", "EditMode")
                    .Release("ft").Release("fc").Release("fi").Release("fl").Call("rs", "Close").Release("rs");
                AdoErrors(p, "cn");
                p.CallBind("rk", "cn", "Execute", A.Str("SELECT ID, Code, T FROM K ORDER BY ID"), A.Missing, A.I4(Ado.CmdText));
                AdoReadAll(p, "rk", 8, 4);
                p.Finish("cn");
            });

        yield return Q("ado-batch", ["R-ADO-1", "ADO-BATCH", "ADO-VALUES"],
            "ADO-BATCH (read-write copy): client static batch-optimistic recordset on ID, Code, T of K; stage T = \"b1\" on row 1, Code = \"a1\" (unique-key duplicate) on row 2, T = \"b3\" on row 3; " +
            "UpdateBatch(adAffectAll) and Errors; then Status, Value and OriginalValue of each row; Filter = adFilterConflictingRecords and RecordCount; then the stored rows through Execute. Asks which staged changes reach the file and how each row reports.",
            AdoAccdb, p =>
            {
                AdoOpen(p, "cn", readOnly: false);
                AdoRs(p, "rs", "SELECT ID, Code, T FROM K ORDER BY ID", "cn", Ado.UseClient, Ado.OpenStatic, Ado.LockBatchOptimistic, Ado.CmdText);
                p.GetBind("fl", "rs", "Fields").Item("ft", "fl", A.Str("T")).Item("fc", "fl", A.Str("Code"))
                    .Put("ft", "Value", A.Str("b1")).Call("rs", "MoveNext")
                    .Put("fc", "Value", A.Str("a1")).Call("rs", "MoveNext")
                    .Put("ft", "Value", A.Str("b3")).Call("rs", "UpdateBatch", A.I4(Ado.AffectAll));
                AdoErrors(p, "cn");
                p.Call("rs", "MoveFirst")
                    .Until("rs", "EOF", 8, b => b.Get("rs", "Status").Get("fc", "Value").Get("fc", "OriginalValue").Get("ft", "Value").Get("ft", "OriginalValue").Call("rs", "MoveNext"))
                    .Put("rs", "Filter", A.I4(Ado.FilterConflictingRecords)).Get("rs", "RecordCount")
                    .Release("ft").Release("fc").Release("fl").Call("rs", "Close").Release("rs")
                    .CallBind("rk", "cn", "Execute", A.Str("SELECT ID, Code, T FROM K ORDER BY ID"), A.Missing, A.I4(Ado.CmdText));
                AdoReadAll(p, "rk", 8, 4);
                p.Finish("cn");
            });

        yield return Q("ado-txn", ["R-ADO-1", "ADO-TXN"],
            "ADO-TXN (read-write copy): read Attributes and IsolationLevel; BeginTrans (returned level); UPDATE T = 't1'; nested BeginTrans; UPDATE T = 't2'; RollbackTrans; read T; CommitTrans; read T; " +
            "CommitTrans and RollbackTrans with no transaction (Errors); IsolationLevel = adXactSerializable, BeginTrans, UPDATE T = 't3', Close with the transaction open; reopen and read T. Asks levels, values and errors.",
            AdoAccdb, p =>
            {
                const string readT = "SELECT T FROM K WHERE Code = 'a1'";
                AdoOpen(p, "cn", readOnly: false);
                p.Get("cn", "Attributes").Get("cn", "IsolationLevel").Call("cn", "BeginTrans")
                    .Call("cn", "Execute", A.Str("UPDATE K SET T = 't1' WHERE Code = 'a1'"), A.Missing, A.I4(Ado.CmdText | Ado.ExecuteNoRecords))
                    .Call("cn", "BeginTrans")
                    .Call("cn", "Execute", A.Str("UPDATE K SET T = 't2' WHERE Code = 'a1'"), A.Missing, A.I4(Ado.CmdText | Ado.ExecuteNoRecords))
                    .Call("cn", "RollbackTrans");
                ExecRead(p, "cn", "t1r", readT);
                p.Call("cn", "CommitTrans");
                ExecRead(p, "cn", "t2r", readT);
                p.Call("cn", "CommitTrans").Call("cn", "RollbackTrans");
                AdoErrors(p, "cn");
                p.Put("cn", "IsolationLevel", A.I4(Ado.XactSerializable)).Get("cn", "IsolationLevel").Call("cn", "BeginTrans")
                    .Call("cn", "Execute", A.Str("UPDATE K SET T = 't3' WHERE Code = 'a1'"), A.Missing, A.I4(Ado.CmdText | Ado.ExecuteNoRecords))
                    .Call("cn", "Close").Get("cn", "State");
                AdoErrors(p, "cn");
                AdoOpen(p, "cn2");
                ExecRead(p, "cn2", "t3r", readT);
                p.Finish("cn2");
            });

        yield return Q("ado-view", ["R-ADO-1", "ADO-VIEW", "ADO-BOOK"],
            "ADO-VIEW/ADO-BOOK on a client static read-only recordset of K: RecordCount; Filter \"T LIKE 'a*'\" and \"T LIKE 'a%'\" (RecordCount and rows); Filter adFilterNone; Sort \"ID DESC\" and rows; Sort \"\" and first Code; " +
            "Find \"ID = 3\", Find \"Code LIKE 'b*'\", malformed Find \"ID = = 3\" (EOF and Code after each); Clone, its AbsolutePosition/Filter/Sort; Filter on the original and RecordCount of both; Bookmark of both; Close the clone and read the original's State.",
            AdoAccdb, p =>
            {
                AdoOpen(p, "cn");
                AdoRs(p, "rs", "SELECT ID, Code, T FROM K ORDER BY ID", "cn", Ado.UseClient, Ado.OpenStatic, Ado.LockReadOnly, Ado.CmdText);
                p.Get("rs", "RecordCount");
                foreach (string filter in new[] { "T LIKE 'a*'", "T LIKE 'a%'" })
                {
                    p.Put("rs", "Filter", A.Str(filter)).Get("rs", "RecordCount");
                    AdoReadAll(p, "rs", 8, 4);
                }
                p.Put("rs", "Filter", A.I4(Ado.FilterNone)).Put("rs", "Sort", A.Str("ID DESC"));
                AdoReadAll(p, "rs", 8, 4);
                p.Put("rs", "Sort", A.Str("")).Call("rs", "MoveFirst").GetBind("fl", "rs", "Fields").Item("fc", "fl", A.Str("Code")).Get("fc", "Value");
                foreach (string find in new[] { "ID = 3", "Code LIKE 'b*'", "ID = = 3" })
                {
                    p.Call("rs", "MoveFirst").Call("rs", "Find", A.Str(find)).Get("rs", "EOF").Get("fc", "Value");
                }
                p.Call("rs", "MoveLast").CallBind("rc", "rs", "Clone").Get("rc", "AbsolutePosition").Get("rc", "Filter").Get("rc", "Sort")
                    .Put("rs", "Filter", A.Str("Code = 'a2'")).Get("rs", "RecordCount").Get("rc", "RecordCount")
                    .Get("rs", "Bookmark").Get("rc", "Bookmark").Call("rc", "Close").Release("rc").Get("rs", "State")
                    .Release("fc").Release("fl").Call("rs", "Close").Release("rs");
                p.Finish("cn");
            });

        yield return Q("ado-schema", ["R-ADO-1", "ADO-SCHEMA"],
            "ADO-SCHEMA: Connection.OpenSchema for adSchemaTables, Columns, Indexes, ForeignKeys, Procedures, Views, ProviderTypes and an undefined value 9999, with restrictions omitted; for each, State, column Name/Type and every row; " +
            "then adSchemaTables with Empty and with Null as the restriction argument. Asks which schemas are supported and their typed rowsets.",
            AdoAccdb, p =>
            {
                AdoOpen(p, "cn");
                int n = 0;
                foreach ((string _, int value) in Ado.Schemas)
                {
                    string s = "s" + n++;
                    p.CallBind(s, "cn", "OpenSchema", A.I4(value)).Get(s, "State")
                        .GetBind(s + "f", s, "Fields").Each(s + "f", s + "c", 64, b => b.Get(s + "c", "Name").Get(s + "c", "Type")).Release(s + "f");
                    AdoReadAll(p, s, 400, 64);
                    p.Release(s);
                }
                p.CallBind("se", "cn", "OpenSchema", A.I4(20), A.Empty).Get("se", "State").Release("se")
                    .CallBind("sn", "cn", "OpenSchema", A.I4(20), A.Null).Get("sn", "State").Release("sn");
                AdoErrors(p, "cn");
                p.Finish("cn");
            });

        yield return Q("ado-bulk", ["R-ADO-1", "ADO-BULK"],
            "ADO-BULK on a client static recordset of K: GetRows(adGetRowsRest) (array tag and bounds), position after; GetRows(2, adBookmarkFirst, \"T\"); GetString(adClipString, -1, \"|\", \"\\n\", \"<null>\") and GetString with defaults; " +
            "EOF/AbsolutePosition after each. Element-wise VARIANT array contents are outside capture/1, so only tag and bounds are asked for GetRows.",
            AdoAccdb, p =>
            {
                AdoOpen(p, "cn");
                AdoRs(p, "rs", "SELECT ID, Code, T, M FROM K ORDER BY ID", "cn", Ado.UseClient, Ado.OpenStatic, Ado.LockReadOnly, Ado.CmdText);
                p.Call("rs", "GetRows", A.I4(Ado.GetRowsRest)).Get("rs", "EOF")
                    .Call("rs", "MoveFirst").Call("rs", "GetRows", A.I4(2), A.I4(Ado.BookmarkFirst), A.Str("T")).Get("rs", "AbsolutePosition")
                    .Call("rs", "MoveFirst").Call("rs", "GetString", A.I4(Ado.ClipString), A.I4(-1), A.Str("|"), A.Str("\n"), A.Str("<null>")).Get("rs", "EOF")
                    .Call("rs", "MoveFirst").Call("rs", "GetString", A.I4(Ado.ClipString)).Get("rs", "EOF")
                    .Call("rs", "Close").Release("rs");
                p.Finish("cn");
            });

        yield return Bare("ado-stream", ["R-ADO-1", "ADO-STREAM", "ADO-PROVIDER", "R-STRING-1"],
            "ADO-STREAM standalone (no connection): fresh Stream State/Type/Charset/Mode; Open; text \"unicode\" WriteText of \"a\\0b€\" plus a non-BMP pair; Position, Size; ReadText(adReadAll) from 0; switch to binary at 0 and Read(adReadAll); " +
            "back to text with Charset \"utf-8\" and ReadText; SaveToFile(work file, overwrite) with the file kept; SetEOS at 0, Size and ReadText at EOS; Close and Size/ReadText after Close. " +
            "Second Stream: binary Write of 3 bytes, Type change at a nonzero Position, then text \"unicode\" ReadText of the odd byte count.",
            p =>
            {
                p.Modules("before-create").Create("ADODB.Stream", "st").Modules("after-create")
                    .Get("st", "State").Get("st", "Type").Get("st", "Charset").Get("st", "Mode").Call("st", "Open").Get("st", "State")
                    .Put("st", "Type", A.I4(Ado.TypeText)).Put("st", "Charset", A.Str("unicode"))
                    .Call("st", "WriteText", A.Str("a\0b€\U0001D11E")).Get("st", "Position").Get("st", "Size")
                    .Put("st", "Position", A.I4(0)).Call("st", "ReadText", A.I4(Ado.ReadAll))
                    .Put("st", "Position", A.I4(0)).Put("st", "Type", A.I4(Ado.TypeBinary)).Call("st", "Read", A.I4(Ado.ReadAll))
                    .Put("st", "Position", A.I4(0)).Put("st", "Type", A.I4(Ado.TypeText)).Put("st", "Charset", A.Str("utf-8")).Get("st", "Charset")
                    .Call("st", "ReadText", A.I4(Ado.ReadAll))
                    .Call("st", "SaveToFile", A.Out("stream.bin"), A.I4(Ado.SaveCreateOverWrite)).Files("after-save", keep: true)
                    .Put("st", "Position", A.I4(0)).Call("st", "SetEOS").Get("st", "Size").Get("st", "EOS").Call("st", "ReadText", A.I4(Ado.ReadAll))
                    .Call("st", "Close").Get("st", "State").Get("st", "Size").Call("st", "ReadText", A.I4(Ado.ReadAll)).Release("st")
                    .Create("ADODB.Stream", "sb").Put("sb", "Type", A.I4(Ado.TypeBinary)).Call("sb", "Open")
                    .Call("sb", "Write", A.Bytes(0x41, 0x00, 0x42)).Get("sb", "Position").Get("sb", "Size")
                    .Put("sb", "Type", A.I4(Ado.TypeText))
                    .Put("sb", "Position", A.I4(0)).Put("sb", "Type", A.I4(Ado.TypeText)).Put("sb", "Charset", A.Str("unicode"))
                    .Call("sb", "ReadText", A.I4(Ado.ReadAll)).Get("sb", "Position").Get("sb", "EOS")
                    .Call("sb", "Close").Release("sb").Modules("after-close");
            });

        yield return Q("ado-persist", ["R-ADO-1", "ADO-PERSIST", "ADO-PROVIDER", "R-ACT-1", "ACT-ADO-SCOPE"],
            "ADO-PERSIST: a client static batch-optimistic recordset of K is saved as ADTG, then given a pending local change (row 1 T = \"pending\") and saved as ADTG and XML (files kept); " +
            "the pending ADTG and the XML are reopened connectionless with adCmdFile (State, RecordCount, Status, T Value/OriginalValue and every row); then an explicit Provider=MSPersist Connection opens, " +
            "a Recordset opens the plain ADTG through it, and Connection.Execute is called with the ADTG path. Asks what each route accepts and restores.",
            AdoAccdb, p =>
            {
                AdoOpen(p, "cn");
                AdoRs(p, "rs", "SELECT * FROM K ORDER BY ID", "cn", Ado.UseClient, Ado.OpenStatic, Ado.LockBatchOptimistic, Ado.CmdText);
                p.Call("rs", "Save", A.Out("k.adtg"), A.I4(Ado.PersistAdtg))
                    .GetBind("fl", "rs", "Fields").Item("ft", "fl", A.Str("T")).Put("ft", "Value", A.Str("pending")).Call("rs", "Update").Get("rs", "Status")
                    .Call("rs", "Save", A.Out("k-pending.adtg"), A.I4(Ado.PersistAdtg)).Call("rs", "Save", A.Out("k-pending.xml"), A.I4(Ado.PersistXml))
                    .Release("ft").Release("fl").Call("rs", "Close").Release("rs").Call("cn", "Close").Release("cn")
                    .Files("after-save", keep: true);
                foreach ((string bind, string file) in new[] { ("ra", "k-pending.adtg"), ("rx", "k-pending.xml") })
                {
                    p.Create("ADODB.Recordset", bind)
                        .Call(bind, "Open", A.Out(file), A.Missing, A.I4(Ado.OpenStatic), A.I4(Ado.LockBatchOptimistic), A.I4(Ado.CmdFile))
                        .Get(bind, "State").Get(bind, "RecordCount").Get(bind, "Status").Get(bind, "ActiveConnection")
                        .GetBind(bind + "f", bind, "Fields").Item(bind + "t", bind + "f", A.Str("T"))
                        .Get(bind + "t", "Value").Get(bind + "t", "OriginalValue").Release(bind + "t").Release(bind + "f");
                    AdoReadAll(p, bind, 8, 16);
                    p.Call(bind, "Close").Release(bind);
                }
                p.Create("ADODB.Connection", "cm").Put("cm", "Provider", A.Str("MSPersist")).Call("cm", "Open").Get("cm", "State").Get("cm", "Provider")
                    .Create("ADODB.Recordset", "rm")
                    .Call("rm", "Open", A.Out("k.adtg"), A.Obj("cm"), A.I4(Ado.OpenStatic), A.I4(Ado.LockBatchOptimistic), A.I4(Ado.CmdFile))
                    .Get("rm", "State").Get("rm", "RecordCount").Call("rm", "Close").Release("rm")
                    .CallBind("re", "cm", "Execute", A.Out("k.adtg")).Get("re", "State").Get("re", "RecordCount").Release("re");
                AdoErrors(p, "cm");
                p.Finish("cm");
            });

        yield return Q("ado-async", ["R-ADO-1", "ADO-ASYNC", "D-CALL-1"],
            "ADO-ASYNC without an event sink: Connection.Execute of a seven-way cross join COUNT over D (10^7 combinations) with adAsyncExecute; Connection and Recordset State immediately after return, " +
            "Cancel, State again and Errors; then a client-cursor Recordset.Open of the same query with adAsyncFetch, State and RecordCount immediately, Cancel, State. Asks whether either call returns before completion.",
            AdoAccdb, p =>
            {
                const string slow = "SELECT COUNT(*) AS Cnt FROM D AS a, D AS b, D AS c, D AS d, D AS e, D AS f, D AS g";
                AdoOpen(p, "cn");
                p.CallBind("ra", "cn", "Execute", A.Str(slow), A.Missing, A.I4(Ado.CmdText | Ado.AsyncExecute))
                    .Get("cn", "State").Get("ra", "State").Call("cn", "Cancel").Get("cn", "State").Get("ra", "State");
                AdoErrors(p, "cn");
                AdoReadAll(p, "ra", 2, 2);
                p.Release("ra");
                AdoRs(p, "rf", slow, "cn", Ado.UseClient, Ado.OpenStatic, Ado.LockReadOnly, Ado.CmdText | Ado.AsyncFetch);
                p.Get("rf", "RecordCount").Call("rf", "Cancel").Get("rf", "State");
                AdoErrors(p, "cn");
                p.Release("rf").Finish("cn");
            });
    }

    // R-ACT-1 ---------------------------------------------------------------------------------------------

    private static IEnumerable<Probe> ActivationProbes()
    {
        yield return Bare("act-dao120", ["R-ACT-1", "ACT-NAME", "ACT-META", "ACT-THREAD/SAME-STA"],
            "ACT-NAME/ACT-META: create DAO.DBEngine.120 on the worker STA with loaded modules recorded before creation, after creation and after release; read Version; describe the object's own type through its live GetTypeInfo.",
            p => p.Modules("before-create").Create("DAO.DBEngine.120", "eng").Modules("after-create").Get("eng", "Version")
                .Describe("eng").Release("eng").Modules("after-release"));

        yield return Bare("act-ado-classes", ["R-ACT-1", "ACT-NAME", "ACT-META", "ACT-ADO-SCOPE", "R-ADO-1"],
            "ACT-NAME for ADO: create ADODB.Connection, ADODB.Recordset, ADODB.Command and ADODB.Stream in turn, recording loaded modules before and after each creation and after each release; " +
            "read State of each (and Connection.Version), and describe each object's own type through GetTypeInfo.",
            p =>
            {
                foreach ((string progId, string bind) in new[] { ("ADODB.Connection", "cn"), ("ADODB.Recordset", "rs"), ("ADODB.Command", "cmd"), ("ADODB.Stream", "st") })
                {
                    p.Modules("before-" + bind).Create(progId, bind).Modules("after-" + bind).Get(bind, "State");
                    if (bind == "cn")
                    {
                        p.Get(bind, "Version");
                    }
                    p.Describe(bind).Release(bind).Modules("after-release-" + bind);
                }
            });

        yield return Bare("act-two-engines", ["R-ACT-1", "ACT-THREAD/ENGINE-IDENTITY"],
            "ACT-THREAD/ENGINE-IDENTITY on one STA: create DAO.DBEngine.120 twice and keep both; read Version and Workspaces.Count of each; append a workspace created on the first and read both counts; " +
            "cause an OpenDatabase failure on the first (nonexistent work file) and read both Errors collections; release the first and read the second's Version and Workspaces.Count. Asks whether engine state is shared.",
            p =>
            {
                p.Modules("before-create").Create("DAO.DBEngine.120", "e1").Create("DAO.DBEngine.120", "e2").Modules("after-create")
                    .Get("e1", "Version").Get("e2", "Version")
                    .GetBind("w1", "e1", "Workspaces").GetBind("w2", "e2", "Workspaces").Get("w1", "Count").Get("w2", "Count")
                    .CallBind("ws", "e1", "CreateWorkspace", A.Str("probe"), A.Str("admin"), A.Str("")).Call("w1", "Append", A.Obj("ws"))
                    .Get("w1", "Count").Get("w2", "Count")
                    .CallBind("dbx", "e1", "OpenDatabase", A.Out("missing.accdb"));
                DaoErrors(p, "e1");
                DaoErrors(p, "e2");
                p.Release("ws").Release("w1").Release("e1").Get("e2", "Version").Get("w2", "Count").Release("w2").Release("e2").Modules("after-release");
            });

        if (MissingNameRoutes)
        {
            yield return Bare("act-dao36", ["R-ACT-1", "ACT-MISSING", "ACT-NAME"],
                "ACT-MISSING (isolated environment only): create DAO.DBEngine.36 from the x64 worker, recording loaded modules before and after; read Version on the result. Asks the creation outcome and HRESULT.",
                p => p.Modules("before-create").Create("DAO.DBEngine.36", "eng").Modules("after-create").Get("eng", "Version").Release("eng"));
        }
    }

    // Helpers ----------------------------------------------------------------------------------------------

    private static Probe Q(string name, string[] refs, string description, string fixture, Action<P> program) =>
        new(Card, name, refs, description, program) { Fixtures = [("main", fixture)] };

    private static Probe Bare(string name, string[] refs, string description, Action<P> program) => new(Card, name, refs, description, program);

    private static void DaoRead(P p, string rs, string sql) =>
        p.OpenRs(rs, "db", sql, Dao.OpenSnapshot).ReadAll(rs, 16, 16).Call(rs, "Close").Release(rs);

    private static void DaoErrors(P p, string eng) =>
        p.GetBind(eng + "Errs", eng, "Errors")
            .Each(eng + "Errs", eng + "Err", 16, b => b.Get(eng + "Err", "Number").Get(eng + "Err", "Description").Get(eng + "Err", "Source"))
            .Release(eng + "Errs");

    /// <summary><c>cn.Properties("Data Source").Value = path</c> through a wrapper released at once.</summary>
    private static void SetDataSource(P p, string cn, Arg path) =>
        p.GetBind(cn + "Props", cn, "Properties").Item(cn + "Ds", cn + "Props", A.Str("Data Source")).Put(cn + "Ds", "Value", path)
            .Release(cn + "Ds").Release(cn + "Props");

    /// <summary>ACE OLE DB connection to the input with this role: Provider, Data Source property, optional adModeRead, Open.</summary>
    private static void AdoOpen(P p, string cn, bool readOnly = true, string role = "main")
    {
        p.Create("ADODB.Connection", cn).Put(cn, "Provider", A.Str(Ace));
        SetDataSource(p, cn, A.In(role));
        if (readOnly)
        {
            p.Put(cn, "Mode", A.I4(Ado.ModeRead));
        }
        p.Call(cn, "Open").Get(cn, "State");
    }

    private static void AdoErrors(P p, string cn) =>
        p.GetBind(cn + "Errs", cn, "Errors")
            .Each(cn + "Errs", cn + "Err", 16, b => b.Get(cn + "Err", "Number").Get(cn + "Err", "Description").Get(cn + "Err", "Source")
                .Get(cn + "Err", "SQLState").Get(cn + "Err", "NativeError"))
            .Release(cn + "Errs");

    private static void AdoRs(P p, string rs, string source, string cn, int location, int cursorType, int lockType, int options) =>
        p.Create("ADODB.Recordset", rs).Put(rs, "CursorLocation", A.I4(location))
            .Call(rs, "Open", A.Str(source), A.Obj(cn), A.I4(cursorType), A.I4(lockType), A.I4(options)).Get(rs, "State");

    private static void AdoReadAll(P p, string rs, int maxRows, int maxFields, bool actualSize = false) =>
        p.Until(rs, "EOF", maxRows, b => b.GetBind(rs + "Flds", rs, "Fields")
            .Each(rs + "Flds", rs + "F", maxFields, f =>
            {
                f.Get(rs + "F", "Value");
                if (actualSize)
                {
                    f.Get(rs + "F", "ActualSize");
                }
            })
            .Release(rs + "Flds").Call(rs, "MoveNext"));

    private static void RsState(P p, string rs) =>
        p.Get(rs, "State").Get(rs, "BOF").Get(rs, "EOF").Get(rs, "CursorType").Get(rs, "LockType").Get(rs, "CursorLocation");

    private static void ConnState(P p, string cn) =>
        p.Get(cn, "State").Get(cn, "Provider").Get(cn, "ConnectionString").Get(cn, "Mode").Get(cn, "CursorLocation")
            .Get(cn, "CommandTimeout").Get(cn, "ConnectionTimeout").Get(cn, "Version").Get(cn, "Attributes").Get(cn, "IsolationLevel");

    /// <summary>State, Provider, ConnectionString and Errors after an Open attempt, then Close and release.</summary>
    private static void OpenedState(P p, string cn)
    {
        p.Get(cn, "State").Get(cn, "Provider").Get(cn, "ConnectionString");
        AdoErrors(p, cn);
        p.Call(cn, "Close").Release(cn);
    }

    private static void Props(P p, string cn, string label) =>
        p.GetBind($"{cn}Props_{label}", cn, "Properties")
            .Each($"{cn}Props_{label}", $"{cn}Pr_{label}", 256, b => b.Get($"{cn}Pr_{label}", "Name").Get($"{cn}Pr_{label}", "Type")
                .Get($"{cn}Pr_{label}", "Value").Get($"{cn}Pr_{label}", "Attributes"))
            .Release($"{cn}Props_{label}");

    private static void ParamInventory(P p, string prms) =>
        p.Get(prms, "Count").Each(prms, prms + "P", 8, b => b.Get(prms + "P", "Name").Get(prms + "P", "Type").Get(prms + "P", "Direction")
            .Get(prms + "P", "Size").Get(prms + "P", "Precision").Get(prms + "P", "NumericScale").Get(prms + "P", "Value"));

    private static void ValueTriple(P p, string field) =>
        p.Get(field, "Value").Get(field, "OriginalValue").Get(field, "UnderlyingValue");

    private static void ExecRead(P p, string cn, string bind, string sql)
    {
        p.CallBind(bind, cn, "Execute", A.Str(sql), A.Missing, A.I4(Ado.CmdText));
        AdoReadAll(p, bind, 4, 4);
        p.Release(bind);
    }
}
