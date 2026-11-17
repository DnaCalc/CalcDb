namespace CalcDb.Probes.Cards;

/// <summary>
/// Repair probes for ACE oracle evidence in three folders (r-wire: R-ORACLE-1 OR-WIRE and R-STRING-1;
/// r-ado-1b and r-str-ado-act: R-ADO-1) whose records were made invalid or confounded by defects in OUR probes
/// (docs/work/evidence-summaries.md, "Probe or fixture defects" and "Revisit"). Each repair asks the same
/// question as the original, built so the defect cannot recur: each fault gets its own fresh object or database,
/// DBEngine.Errors and Connection.Errors are read right after each failing call, collections are refreshed
/// before they are read after DDL, state is read back after each step, statements carry the column list they need,
/// refusals are captured with dbFailOnError, and a property is read with a property get. Decimal columns are made
/// through ADO DDL with explicit precision and scale (a DAO CreateField(name, 20) Decimal has Precision 38 and
/// DAO cannot read it) and written as SQL literals. No call here can open a dialog: every OpenDatabase,
/// CreateDatabase and Connection names an explicit file from A.In or A.Out. Probes only ask; no answer is
/// expected in code. Fixtures start with "r-fix-wire-ado-".
/// </summary>
public sealed class RFixWireAdo : ICard
{
    private const string Card = "r-fix-wire-ado";
    private const string Ace = "Microsoft.ACE.OLEDB.12.0";
    private const string BlankFx = "r-wire-blank";
    private const string AdoMainFx = "r-ado-1b-main";
    private const string StrFx = "r-str-ado-act-str-accdb";
    private const string Ado5Fx = Card + "-ado5";

    private const string SelS = "SELECT ID, Code, L, S FROM T ORDER BY ID";
    private const string SelW = "SELECT ID, Code, V, V2, S FROM W ORDER BY ID";

    /// <summary>ADO constants (values from the ADO type library, inventoried by r-str-ado-act/ado-describe).</summary>
    private static class Ado
    {
        public const int UseServer = 2, UseClient = 3;
        public const int OpenForwardOnly = 0, OpenKeyset = 1, OpenDynamic = 2, OpenStatic = 3;
        public const int LockReadOnly = 1, LockPessimistic = 2, LockOptimistic = 3, LockBatchOptimistic = 4;
        public const int CmdText = 1, CmdTable = 2, CmdStoredProc = 4, CmdFile = 256, CmdTableDirect = 512;
        public const int ExecuteNoRecords = 0x80;
        public const int ModeRead = 1;
        public const int AffectCurrent = 1, AffectGroup = 2, AffectAll = 3;
        public const int ResyncUnderlying = 1, ResyncAll = 2;
        public const int FilterNone = 0, FilterPending = 1, FilterAffected = 2, FilterConflicting = 5;
        public const int PersistAdtg = 0, PersistXml = 1;
        public const int Integer = 3, Decimal = 14, VarWChar = 202;
        public const int ParamInput = 1;
        public const int SupBookmark = 0x2000;
        public const int ClipString = 2, GetRowsRest = -1, BookmarkFirst = 1;

        /// <summary>CursorOptionEnum values asked through Recordset.Supports.</summary>
        public static readonly (string Name, int Value)[] Supports =
        [
            ("adAddNew", 0x1000400), ("adApproxPosition", 0x4000), ("adBookmark", 0x2000), ("adDelete", 0x1000800),
            ("adFind", 0x80000), ("adHoldRecords", 0x100), ("adIndex", 0x100000), ("adMovePrevious", 0x200),
            ("adNotify", 0x40000), ("adResync", 0x20000), ("adSeek", 0x400000), ("adUpdate", 0x1008000),
            ("adUpdateBatch", 0x10000),
        ];
    }

    private static readonly string[] WireRefs = ["R-ORACLE-1", "OR-WIRE", "R-STRING-1", "D-STRING-1"];

    private static string[] Wire(params string[] more) => [.. WireRefs, .. more];

    private static string[] AdoRefs(params string[] more) => ["R-ADO-1", .. more];

    private static string[] StrRefs(params string[] more) => ["R-STRING-1", "D-STRING-1", "II-02", .. more];

    // Cells of the wire matrix (copied from r-wire, which holds the original definitions) -----------------------

    /// <summary>A column of table W: DDL type, PARAMETERS type, ID base.</summary>
    private sealed record Col(string Name, string Ddl, string ParamType, int Base);

    /// <summary>One value for one column: row ID, label and the Arg (its VARIANT tag is the question).</summary>
    private sealed record Cell(int Id, Col Col, string Label, Arg Value);

    private static readonly Col FByte = new("FByte", "BYTE", "Byte", 100);
    private static readonly Col FInt = new("FInt", "SHORT", "Short", 200);
    private static readonly Col FLong = new("FLong", "LONG", "Long", 300);
    private static readonly Col FSingle = new("FSingle", "SINGLE", "IEEESingle", 400);
    private static readonly Col FDouble = new("FDouble", "DOUBLE", "IEEEDouble", 500);
    private static readonly Col FCurr = new("FCurr", "CURRENCY", "Currency", 600);
    private static readonly Col FDate = new("FDate", "DATETIME", "DateTime", 700);
    private static readonly Col FText = new("FText", "TEXT(255)", "Text(255)", 800);
    private static readonly Col FMemo = new("FMemo", "MEMO", "LongText", 900);
    private static readonly Col FBin = new("FBin", "BINARY(255)", "Binary", 1000);
    private static readonly Col FOle = new("FOle", "LONGBINARY", "LongBinary", 1100);

    /// <summary>The columns whose parameter-route cells were stale after a failed put (r-wire/create-param).</summary>
    private static readonly Col[] ParamCols = [FByte, FInt, FLong, FSingle, FCurr, FText, FMemo];

    private static readonly Cell[] Cells = BuildCells();

    private static readonly string CellList = string.Join("; ", new[] { FByte, FInt, FLong, FSingle, FDouble, FCurr, FDate, FText, FMemo, FBin, FOle }.Select(c =>
        $"{c.Name} (IDs from {c.Base}): " + string.Join(", ", Cells.Where(x => x.Col == c).Select(x => x.Label))));

    private static Arg F4(uint bits) => new R4Arg(bits.ToString("x8"));

    private static Arg Dt(ulong bits) => new DateArg(bits.ToString("x16"));

    private static byte[] Ramp(int count, int step) => [.. Enumerable.Range(0, count).Select(i => (byte)(i * step))];

    private static IEnumerable<Cell> CellsOf(params Col[] cols) => Cells.Where(c => cols.Contains(c.Col));

    private static Cell[] BuildCells()
    {
        var list = new List<Cell>();
        var next = new[] { FByte, FInt, FLong, FSingle, FDouble, FCurr, FDate, FText, FMemo, FBin, FOle }.ToDictionary(c => c.Name, c => c.Base);
        void Add(Col col, string label, Arg value) => list.Add(new Cell(next[col.Name]++, col, label, value));

        string x255 = new('x', 255), x256 = new('x', 256), y300 = new('y', 300);

        Add(FByte, "ui1-0", A.UI1(0));
        Add(FByte, "ui1-255", A.UI1(255));
        Add(FByte, "i2-256", A.I2(256));
        Add(FByte, "i2-minus1", A.I2(-1));
        Add(FByte, "r8-1.5", A.R8(1.5));
        Add(FByte, "r8-2.5", A.R8(2.5));

        Add(FInt, "i2-min", A.I2(short.MinValue));
        Add(FInt, "i2-max", A.I2(short.MaxValue));
        Add(FInt, "i4-32768", A.I4(32768));
        Add(FInt, "i4-minus32769", A.I4(-32769));
        Add(FInt, "r8-1.5", A.R8(1.5));
        Add(FInt, "r8-2.5", A.R8(2.5));

        Add(FLong, "i4-min", A.I4(int.MinValue));
        Add(FLong, "i4-max", A.I4(int.MaxValue));
        Add(FLong, "r8-2147483648", A.R8(2147483648.0));
        Add(FLong, "r8-minus2147483649", A.R8(-2147483649.0));
        Add(FLong, "r8-0.5", A.R8(0.5));
        Add(FLong, "r8-1.5", A.R8(1.5));
        Add(FLong, "r8-2.5", A.R8(2.5));

        Add(FSingle, "plus-zero", F4(0x00000000));
        Add(FSingle, "minus-zero", F4(0x80000000));
        Add(FSingle, "min-subnormal", F4(0x00000001));
        Add(FSingle, "max-subnormal", F4(0x007fffff));
        Add(FSingle, "min-normal", F4(0x00800000));
        Add(FSingle, "max-finite", F4(0x7f7fffff));
        Add(FSingle, "plus-inf", F4(0x7f800000));
        Add(FSingle, "minus-inf", F4(0xff800000));
        Add(FSingle, "qnan", F4(0x7fc00000));
        Add(FSingle, "qnan-payload", F4(0x7fc12345));
        Add(FSingle, "snan", F4(0x7f800001));
        Add(FSingle, "negative-qnan", F4(0xffc00000));
        Add(FSingle, "r8-1e39", A.R8(1e39));
        Add(FSingle, "r8-1e-50", A.R8(1e-50));
        Add(FSingle, "r8-0.1", A.R8(0.1));

        Add(FDouble, "plus-zero", A.R8Bits(0x0000000000000000UL));
        Add(FDouble, "minus-zero", A.R8Bits(0x8000000000000000UL));
        Add(FDouble, "min-subnormal", A.R8Bits(0x0000000000000001UL));
        Add(FDouble, "max-subnormal", A.R8Bits(0x000fffffffffffffUL));
        Add(FDouble, "min-normal", A.R8Bits(0x0010000000000000UL));
        Add(FDouble, "max-finite", A.R8Bits(0x7fefffffffffffffUL));
        Add(FDouble, "plus-inf", A.R8Bits(0x7ff0000000000000UL));
        Add(FDouble, "minus-inf", A.R8Bits(0xfff0000000000000UL));
        Add(FDouble, "qnan", A.R8Bits(0x7ff8000000000000UL));
        Add(FDouble, "qnan-payload", A.R8Bits(0x7ff8000000000123UL));
        Add(FDouble, "snan", A.R8Bits(0x7ff0000000000001UL));
        Add(FDouble, "negative-qnan", A.R8Bits(0xfff8000000000000UL));
        Add(FDouble, "one-plus-ulp", A.R8Bits(0x3ff0000000000001UL));
        Add(FDouble, "r4-0.1-widened", A.R4(0.1f));
        Add(FDouble, "r8-0.1", A.R8(0.1));

        Add(FCurr, "cy-min", A.Cy(-922337203685477.5808m));
        Add(FCurr, "cy-max", A.Cy(922337203685477.5807m));
        Add(FCurr, "cy-0.0001", A.Cy(0.0001m));
        Add(FCurr, "cy-minus-0.0001", A.Cy(-0.0001m));
        Add(FCurr, "r8-0.00005", A.R8(0.00005));
        Add(FCurr, "r8-0.00015", A.R8(0.00015));
        Add(FCurr, "r8-1e15", A.R8(1e15));
        Add(FCurr, "bstr-5-decimals", A.Str("1.23456"));

        Add(FDate, "oa-0", A.Date(0.0));
        Add(FDate, "oa-min", A.Date(-657434.0));
        Add(FDate, "oa-max", A.Date(2958465.0 + (86399.0 / 86400.0)));
        Add(FDate, "below-min", A.Date(-657435.0));
        Add(FDate, "above-max", A.Date(2958466.0));
        Add(FDate, "negative-1.5", A.Date(-1.5));
        Add(FDate, "negative-0.25", A.Date(-0.25));
        Add(FDate, "positive-1.5", A.Date(1.5));
        Add(FDate, "one-millisecond", A.Date(1.0 + (0.001 / 86400.0)));
        Add(FDate, "half-second", A.Date(1.0 + (0.5 / 86400.0)));
        Add(FDate, "nan", Dt(0x7ff8000000000000UL));
        Add(FDate, "plus-inf", Dt(0x7ff0000000000000UL));
        Add(FDate, "r8-tag-1.5", A.R8(1.5));
        Add(FDate, "bstr-iso", A.Str("2024-01-02 03:04:05"));

        Add(FText, "empty", A.Str(""));
        Add(FText, "ascii", A.Str("alpha"));
        Add(FText, "bmp", A.Str("B\u00e4\u20ac"));
        Add(FText, "nul-middle", A.Str("a\0b"));
        Add(FText, "nul-only", A.Str("\0"));
        Add(FText, "astral", A.Str("\U0001D11E"));
        Add(FText, "lone-high", A.BstrUnits("\uD800"));
        Add(FText, "lone-low", A.BstrUnits("\uDC00"));
        Add(FText, "reversed-pair", A.BstrUnits("\uDC00\uD800"));
        Add(FText, "u-fffe", A.Str("\uFFFE"));
        Add(FText, "odd-1-byte", A.BstrBytes(0x41));
        Add(FText, "odd-3-bytes", A.BstrBytes(0x41, 0x00, 0x42));
        Add(FText, "odd-3-bytes-high", A.BstrBytes(0xff, 0xfe, 0xfd));
        Add(FText, "len-255", A.Str(x255));
        Add(FText, "len-256", A.Str(x256));

        Add(FMemo, "empty", A.Str(""));
        Add(FMemo, "nul-middle", A.Str("a\0b"));
        Add(FMemo, "lone-high", A.BstrUnits("\uD800"));
        Add(FMemo, "lone-low", A.BstrUnits("\uDC00"));
        Add(FMemo, "odd-1-byte", A.BstrBytes(0x41));
        Add(FMemo, "odd-5-bytes", A.BstrBytes(0x41, 0x00, 0x42, 0x00, 0x43));
        Add(FMemo, "len-300", A.Str(y300));
        Add(FMemo, "len-5000", A.Str(new string('m', 5000)));
        Add(FMemo, "astral-4000", A.Str(string.Concat(Enumerable.Repeat("\U0001D11E", 4000))));
        Add(FMemo, "odd-8001-bytes", A.BstrBytes([.. Enumerable.Range(0, 8001).Select(i => i % 2 == 0 ? (byte)0x41 : (byte)0x00)]));

        Add(FBin, "empty", A.Bytes());
        Add(FBin, "leading-zeros", A.Bytes(0x00, 0x00, 0x01));
        Add(FBin, "all-zero", A.Bytes(0x00, 0x00, 0x00, 0x00));
        Add(FBin, "ramp-255", A.Bytes(Ramp(255, 1)));
        Add(FBin, "ramp-256", A.Bytes(Ramp(256, 1)));
        Add(FBin, "bstr-ab", A.Str("AB"));
        Add(FBin, "bstr-odd-3-bytes", A.BstrBytes(0x41, 0x00, 0x42));
        Add(FBin, "hex-literal", A.Bytes(0x00, 0x00, 0x01, 0x02));

        Add(FOle, "empty", A.Bytes());
        Add(FOle, "leading-zeros", A.Bytes(0x00, 0x00, 0x01));
        Add(FOle, "ramp-256", A.Bytes(Ramp(256, 1)));
        Add(FOle, "pattern-5000", A.Bytes(Ramp(5000, 7)));
        Add(FOle, "bstr-ab", A.Str("AB"));
        Add(FOle, "bstr-odd-3-bytes", A.BstrBytes(0x01, 0x02, 0x03));
        return [.. list];
    }

    public IEnumerable<Probe> Probes() =>
        Creations()
            .Concat(OpenDatabaseProbes())
            .Concat(ParamProbes())
            .Concat(WriteProbes())
            .Concat(DecimalProbes())
            .Concat(ChunkProbes())
            .Concat(StrAdoProbes())
            .Concat(Ado1BProbes());

    // Probe constructors ----------------------------------------------------------------------------------------

    private static Probe Q(string name, string[] refs, string description, string? fixture, Action<P> program) =>
        new(Card, name, refs, description, program) { Fixtures = fixture is null ? [] : [("main", fixture)] };

    private static Probe Create(string name, string[] refs, string description, Action<P> program) =>
        new(Card, name, refs, description, program) { CreatesFixture = true };

    // Fixtures --------------------------------------------------------------------------------------------------

    private static IEnumerable<Probe> Creations()
    {
        Arg file = A.Out(Card + "-ado5.accdb");
        yield return Create("create-ado5", AdoRefs("D-OBJECT-1"),
            "Repairs r-str-ado-act/create-ado (and ado2, ado3, ado4): fixture tables were lost to DAO DDL refusals (DECIMAL, unbracketed Note) and the Decimal column made by DAO CreateField had Precision 38. " +
            "Fixture r-fix-wire-ado-ado5, created in ACCDB by DBEngine.CreateDatabase(path, dbLangGeneral, 128), closed, then built through an ADO connection (Provider ACE) by DDL and INSERT with SQL literals, each statement's Connection.Errors read right after it: " +
            "K(ID COUNTER primary key, Code TEXT(10) unique, L LONG, D DOUBLE, C CURRENCY, N DECIMAL(18,4), Dt DATETIME, T TEXT(50), M MEMO, B LONGBINARY) with four rows (a1 full, a2 Code only, b3 zero/empty, b4 extremes), " +
            "J(ID LONG key, KID LONG referencing K(ID), [Note] TEXT(20)) with two rows, D(N LONG) with ten rows; every table is then read back through ADO. Then DAO reopens the file, writes B of row a1 through a dynaset Edit, " +
            "creates saved queries QParam (one Text parameter) and QEmpty, refreshes QueryDefs and reads their names. Asks which statements succeed and what each table, row and query holds.",
            p =>
            {
                p.EngineLite().CallBind("db", "eng", "CreateDatabase", file, A.Str(Dao.LangGeneral), A.I4(Dao.Version120)).Get("db", "Version").Call("db", "Close").Release("db");
                AdoOpen(p, "cn", file, readOnly: false);
                string[] statements =
                [
                    "CREATE TABLE K (ID COUNTER CONSTRAINT PK_K PRIMARY KEY, Code TEXT(10) CONSTRAINT UX_K_Code UNIQUE, L LONG, D DOUBLE, C CURRENCY, N DECIMAL(18,4), Dt DATETIME, T TEXT(50), M MEMO, B LONGBINARY)",
                    "INSERT INTO K (Code, L, D, C, N, Dt, T, M) VALUES ('a1', 1, 1.5, 2.25, 3.125, #2024-01-02#, 'alpha', 'memo one')",
                    "INSERT INTO K (Code) VALUES ('a2')",
                    "INSERT INTO K (Code, L, D, C, N, Dt, T, M) VALUES ('b3', 0, -0.5, 0, 0, #1899-12-30 12:00:00#, '', '')",
                    "INSERT INTO K (Code, L, D, C, N, Dt, T, M) VALUES ('b4', 2147483647, 1E300, 1234.5678, -0.0001, #9999-12-31#, 'beta', 'x')",
                    "CREATE TABLE J (ID LONG CONSTRAINT PK_J PRIMARY KEY, KID LONG CONSTRAINT FK_J_K REFERENCES K (ID), [Note] TEXT(20))",
                    "INSERT INTO J (ID, KID, [Note]) VALUES (1, 1, 'j1')",
                    "INSERT INTO J (ID, KID, [Note]) VALUES (2, 1, 'j2')",
                    "CREATE TABLE D (N LONG)",
                ];
                foreach (string sql in statements)
                {
                    ExecNo(p, "cn", sql);
                    ErrClear(p, "cn");
                }
                for (int i = 0; i < 10; i++)
                {
                    ExecNo(p, "cn", $"INSERT INTO D (N) VALUES ({i})");
                    ErrClear(p, "cn");
                }
                ExecRead(p, "cn", "rk", "SELECT ID, Code, L, D, C, N, Dt, T FROM K ORDER BY ID");
                ExecRead(p, "cn", "rj", "SELECT ID, KID, [Note] FROM J ORDER BY ID");
                ExecRead(p, "cn", "rd", "SELECT Count(*) AS Cnt FROM D");
                ErrClear(p, "cn");
                p.Call("cn", "Close").Release("cn");
                p.CallBind("db", "eng", "OpenDatabase", file, A.Bool(false), A.Bool(false))
                    .OpenRs("rb", "db", "SELECT B FROM K WHERE Code = 'a1'", Dao.OpenDynaset)
                    .GetBind("rbf", "rb", "Fields").Item("rbb", "rbf", A.I4(0))
                    .Call("rb", "Edit").Put("rbb", "Value", A.Bytes(0x00, 0x01, 0x02, 0xff, 0x00)).Call("rb", "Update").Get("rbb", "Value").Errors()
                    .Release("rbb").Release("rbf").Call("rb", "Close").Release("rb")
                    .CallBind("q1", "db", "CreateQueryDef", A.Str("QParam"), A.Str("PARAMETERS pCode Text(10); SELECT ID, Code, T FROM K WHERE Code = pCode;"))
                    .CallBind("q2", "db", "CreateQueryDef", A.Str("QEmpty"), A.Str("SELECT * FROM K WHERE 1=0;"))
                    .Release("q1").Release("q2").Errors()
                    .GetBind("qds", "db", "QueryDefs").Call("qds", "Refresh").Each("qds", "qd", 16, b => b.Get("qd", "Name")).Release("qds")
                    .Call("db", "Close").Release("db").FinishLite();
            });
    }

    // r-wire/opt-opendatabase ----------------------------------------------------------------------------------

    private static IEnumerable<Probe> OpenDatabaseProbes()
    {
        Arg path = A.In();
        (string Slug, string What, Arg[] Args)[] variants =
        [
            ("name-only", "Name only", [path]),
            ("options-missing", "Options Missing", [path, A.Missing]),
            ("options-readonly-missing", "Options and ReadOnly Missing", [path, A.Missing, A.Missing]),
            ("all-missing", "Options, ReadOnly and Connect Missing", [path, A.Missing, A.Missing, A.Missing]),
            ("all-empty", "Options, ReadOnly and Connect Empty", [path, A.Empty, A.Empty, A.Empty]),
            ("all-null", "Options, ReadOnly and Connect Null", [path, A.Null, A.Null, A.Null]),
            ("false-true", "Options Boolean False and ReadOnly Boolean True", [path, A.Bool(false), A.Bool(true)]),
            ("options-missing-readonly-true", "Options Missing and ReadOnly Boolean True", [path, A.Missing, A.Bool(true)]),
            ("options-missing-readonly-empty", "Options Missing and ReadOnly Empty", [path, A.Missing, A.Empty]),
            ("options-missing-readonly-null", "Options Missing and ReadOnly Null", [path, A.Missing, A.Null]),
            ("i2-0-i2-minus1", "Options I2 0 and ReadOnly I2 -1", [path, A.I2(0), A.I2(-1)]),
            ("options-bstr-x", "Options BSTR \"x\"", [path, A.Str("x")]),
            ("connect-empty-bstr", "Options and ReadOnly Missing and Connect an empty BSTR", [path, A.Missing, A.Missing, A.Str("")]),
            ("connect-null", "Options and ReadOnly Missing and Connect Null", [path, A.Missing, A.Missing, A.Null]),
            ("connect-empty", "Options and ReadOnly Missing and Connect Empty", [path, A.Missing, A.Missing, A.Empty]),
        ];
        foreach ((string slug, string what, Arg[] args) in variants)
        {
            yield return Q($"opt-opendatabase-{slug}", Wire("R-DAO-1", "R-ACT-1"),
                "Repairs r-wire/opt-opendatabase: one attempt held 18 variants and its Empty-name variant opened a modal ODBC dialog, so no variant was captured. " +
                $"This attempt asks one variant, {what}: DBEngine.OpenDatabase(Name, ...) on a copy of r-wire-blank with Name the explicit fixture path. DBEngine.Errors is read right after the call; " +
                "when the database opens, Database.Updatable and Name are read, then it is closed. The variants with Name as Empty, Null, Missing or an empty string are not asked.",
                BlankFx, p => p.EngineLite().CallBind("od", "eng", "OpenDatabase", args).Errors().Get("od", "Updatable").Get("od", "Name").Call("od", "Close").Release("od").FinishLite());
        }
    }

    // DAO and ADO helpers ---------------------------------------------------------------------------------------

    private static void FieldInfo(P p, string rs) =>
        p.GetBind(rs + "f", rs, "Fields")
            .Each(rs + "f", rs + "d", 32, b => b.Get(rs + "d", "Name").Get(rs + "d", "Type").Get(rs + "d", "Size").Get(rs + "d", "Attributes"))
            .Release(rs + "f");

    /// <summary>One DAO snapshot per column of W (ID range base..base+99): field info, then every row's Value read once by ordinal.</summary>
    private static void ReadColumns(P p, IEnumerable<Col> cols)
    {
        foreach (Col c in cols)
        {
            string rs = "r" + c.Name;
            p.OpenRs(rs, "db", $"SELECT ID, {c.Name} FROM W WHERE ID BETWEEN {c.Base} AND {c.Base + 99} ORDER BY ID", Dao.OpenSnapshot).Errors();
            FieldInfo(p, rs);
            p.ReadAll(rs, 32, 4).Call(rs, "Close").Release(rs);
        }
    }

    private static void SetDataSource(P p, string cn, Arg path) =>
        p.GetBind(cn + "Pr", cn, "Properties").Item(cn + "Ds", cn + "Pr", A.Str("Data Source")).Put(cn + "Ds", "Value", path)
            .Release(cn + "Ds").Release(cn + "Pr");

    /// <summary>ACE OLE DB connection to <paramref name="file"/>: Provider, Data Source property, adModeRead when read-only, Open, State.</summary>
    private static void AdoOpen(P p, string cn, Arg file, bool readOnly)
    {
        p.Create("ADODB.Connection", cn).Put(cn, "Provider", A.Str(Ace));
        SetDataSource(p, cn, file);
        if (readOnly)
        {
            p.Put(cn, "Mode", A.I4(Ado.ModeRead));
        }
        p.Call(cn, "Open").Get(cn, "State");
    }

    private static void AdoOpenMain(P p, string cn, bool readOnly) => AdoOpen(p, cn, A.In(), readOnly);

    private static void AdoErrors(P p, string cn) =>
        p.GetBind(cn + "Errs", cn, "Errors")
            .Each(cn + "Errs", cn + "Err", 16, b => b.Get(cn + "Err", "Number").Get(cn + "Err", "Description").Get(cn + "Err", "Source")
                .Get(cn + "Err", "SQLState").Get(cn + "Err", "NativeError"))
            .Release(cn + "Errs");

    private static void ClearErrors(P p, string cn) => p.GetBind(cn + "Ec", cn, "Errors").Call(cn + "Ec", "Clear").Release(cn + "Ec");

    /// <summary>Read the Errors collection, then clear it, so the next step starts clean.</summary>
    private static void ErrClear(P p, string cn)
    {
        AdoErrors(p, cn);
        ClearErrors(p, cn);
    }

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

    /// <summary>Read the named fields of every remaining row (Fields.Item by name on each row).</summary>
    private static void ReadCols(P p, string rs, int maxRows, params string[] cols) =>
        p.Until(rs, "EOF", maxRows, b =>
        {
            b.GetBind(rs + "Cf", rs, "Fields");
            foreach (string col in cols)
            {
                b.Item(rs + "Cx", rs + "Cf", A.Str(col)).Get(rs + "Cx", "Value").Release(rs + "Cx");
            }
            b.Release(rs + "Cf").Call(rs, "MoveNext");
        });

    /// <summary>Read one field's Value on the current row.</summary>
    private static void Cur(P p, string rs, string col) =>
        p.GetBind(rs + "Uf", rs, "Fields").Item(rs + "Ux", rs + "Uf", A.Str(col)).Get(rs + "Ux", "Value").Release(rs + "Ux").Release(rs + "Uf");

    /// <summary>Status, ID and one field's Value, OriginalValue and UnderlyingValue of every remaining row.</summary>
    private static void BatchValues(P p, string rs, string col) =>
        p.Until(rs, "EOF", 16, b => b.Get(rs, "Status").GetBind(rs + "Bf", rs, "Fields").Item(rs + "Bi", rs + "Bf", A.Str("ID")).Get(rs + "Bi", "Value").Release(rs + "Bi")
            .Item(rs + "Bx", rs + "Bf", A.Str(col)).Get(rs + "Bx", "Value").Get(rs + "Bx", "OriginalValue").Get(rs + "Bx", "UnderlyingValue").Release(rs + "Bx")
            .Release(rs + "Bf").Call(rs, "MoveNext"));

    private static void ValueTriple(P p, string field) =>
        p.Get(field, "Value").Get(field, "OriginalValue").Get(field, "UnderlyingValue");

    private static void FieldMeta(P p, string rs) =>
        p.GetBind(rs + "Mf", rs, "Fields").Get(rs + "Mf", "Count")
            .Each(rs + "Mf", rs + "Mx", 32, b => b.Get(rs + "Mx", "Name").Get(rs + "Mx", "Type").Get(rs + "Mx", "DefinedSize").Get(rs + "Mx", "Precision")
                .Get(rs + "Mx", "NumericScale").Get(rs + "Mx", "Attributes"))
            .Release(rs + "Mf");

    private static void RsState(P p, string rs) =>
        p.Get(rs, "State").Get(rs, "BOF").Get(rs, "EOF").Get(rs, "CursorType").Get(rs, "LockType").Get(rs, "CursorLocation");

    private static void ConnState(P p, string cn) =>
        p.Get(cn, "State").Get(cn, "Provider").Get(cn, "ConnectionString").Get(cn, "Mode").Get(cn, "CursorLocation")
            .Get(cn, "CommandTimeout").Get(cn, "ConnectionTimeout").Get(cn, "Version").Get(cn, "Attributes").Get(cn, "IsolationLevel");

    private static void Props(P p, string cn, string label) =>
        p.GetBind($"{cn}Props_{label}", cn, "Properties")
            .Each($"{cn}Props_{label}", $"{cn}Pr_{label}", 256, b => b.Get($"{cn}Pr_{label}", "Name").Get($"{cn}Pr_{label}", "Type")
                .Get($"{cn}Pr_{label}", "Value").Get($"{cn}Pr_{label}", "Attributes"))
            .Release($"{cn}Props_{label}");

    private static void ParamInventory(P p, string prms) =>
        p.Get(prms, "Count").Each(prms, prms + "P", 8, b => b.Get(prms + "P", "Name").Get(prms + "P", "Type").Get(prms + "P", "Direction")
            .Get(prms + "P", "Size").Get(prms + "P", "Precision").Get(prms + "P", "NumericScale").Get(prms + "P", "Value"));

    private static void ParamMeta(P p, string prm) =>
        p.Get(prm, "Name").Get(prm, "Type").Get(prm, "Direction").Get(prm, "Size").Get(prm, "Precision").Get(prm, "NumericScale").Get(prm, "Attributes").Get(prm, "Value");

    private static void NewCmd(P p, string cmd, string cn, string sql, int type = Ado.CmdText) =>
        p.Create("ADODB.Command", cmd).PutRef(cmd, "ActiveConnection", cn).Put(cmd, "CommandText", A.Str(sql)).Put(cmd, "CommandType", A.I4(type));

    private static void ExecNo(P p, string cn, string sql) =>
        p.Call(cn, "Execute", A.Str(sql), A.Missing, A.I4(Ado.CmdText | Ado.ExecuteNoRecords));

    private static void ExecRead(P p, string cn, string bind, string sql)
    {
        p.CallBind(bind, cn, "Execute", A.Str(sql), A.Missing, A.I4(Ado.CmdText));
        AdoReadAll(p, bind, 16, 6);
        p.Release(bind);
    }

    /// <summary>Client static read-only recordset of the cells of one column of W: field metadata, then GetRows(-1).</summary>
    private static void AdoColumnRows(P p, string rs, Col c) =>
        p.Create("ADODB.Recordset", rs).Put(rs, "CursorLocation", A.I4(Ado.UseClient))
            .Call(rs, "Open", A.Str($"SELECT ID, {c.Name} FROM W WHERE ID BETWEEN {c.Base} AND {c.Base + 99} ORDER BY ID"), A.Obj("cn"), A.I4(Ado.OpenStatic), A.I4(Ado.LockReadOnly), A.I4(Ado.CmdText)).Get(rs, "State")
            .GetBind(rs + "f", rs, "Fields")
            .Each(rs + "f", rs + "d", 8, b => b.Get(rs + "d", "Name").Get(rs + "d", "Type").Get(rs + "d", "DefinedSize").Get(rs + "d", "Precision").Get(rs + "d", "NumericScale").Get(rs + "d", "Attributes"))
            .Release(rs + "f").Call(rs, "GetRows", A.I4(Ado.GetRowsRest)).Call(rs, "Close").Release(rs);

    // r-wire: stale parameter values, LongText order dependence ------------------------------------------------

    /// <summary>One fresh temporary QueryDef for one cell: parameters set and read back, Execute with dbFailOnError, Errors read right after.</summary>
    private static void FreshParamInsert(P p, Cell cell) =>
        p.CallBind("q", "db", "CreateQueryDef", A.Str(""), A.Str($"PARAMETERS pI Long, pV {cell.Col.ParamType}; INSERT INTO W (ID, {cell.Col.Name}) VALUES (pI, pV);"))
            .GetBind("qps", "q", "Parameters").Item("qpi", "qps", A.Str("pI")).Item("qpv", "qps", A.Str("pV"))
            .Put("qpi", "Value", A.I4(cell.Id)).Get("qpi", "Value")
            .Put("qpv", "Value", cell.Value).Get("qpv", "Value")
            .Call("q", "Execute", A.I4(Dao.FailOnError)).Errors().Get("q", "RecordsAffected")
            .Release("qpi").Release("qpv").Release("qps").Call("q", "Close").Release("q");

    private static IEnumerable<Probe> ParamProbes()
    {
        string cellList = string.Join("; ", ParamCols.Select(c => $"{c.Name} (IDs from {c.Base}): " + string.Join(", ", Cells.Where(x => x.Col == c).Select(x => x.Label))));
        yield return Create("create-param", Wire("R-FMT-1", "R-SQL-1"),
            "Repairs r-wire/create-param: parameter cells held the previous row's value after a failed Value put (rows 102, 103, 202, 203, 302, 303, 412, 606, 814) and the Memo rows 902 to 909 failed after a lone surrogate on a reused QueryDef. " +
            "Fixture r-fix-wire-ado-param, created in ACCDB by DBEngine.CreateDatabase(path, dbLangGeneral, 128): table W(ID LONG primary key, FByte BYTE, FInt SHORT, FLong LONG, FSingle SINGLE, FCurr CURRENCY, FText TEXT(255), FMemo MEMO), " +
            "then every cell written through its own fresh temporary QueryDef PARAMETERS pI Long, pV <type>; INSERT INTO W (ID, column) VALUES (pI, pV): Parameters(\"pI\").Value and Parameters(\"pV\").Value = Arg, each read back after its put, " +
            "QueryDef.Execute(dbFailOnError), DBEngine.Errors read right after, RecordsAffected, the QueryDef closed. Then one DAO snapshot per column reads every cell (field info, then each Value once). " +
            $"Asks per cell the HRESULT and error of the puts and Execute, the parameter read-back, and whether the stored cell exists, with its tag and value. Cells: {cellList}",
            p =>
            {
                p.EngineLite().CallBind("db", "eng", "CreateDatabase", A.Out(Card + "-param.accdb"), A.Str(Dao.LangGeneral), A.I4(Dao.Version120))
                    .Call("db", "Execute", A.Str("CREATE TABLE W (ID LONG CONSTRAINT PK_W PRIMARY KEY, " + string.Join(", ", ParamCols.Select(c => $"{c.Name} {c.Ddl}")) + ")"), A.I4(Dao.FailOnError)).Errors();
                foreach (Cell cell in CellsOf(ParamCols))
                {
                    FreshParamInsert(p, cell);
                }
                ReadColumns(p, ParamCols);
                p.FinishLite("db");
            });

        yield return Q("param-select-text-fresh", Wire("R-SQL-1", "II-02"),
            "Repairs r-wire/param-select-text: after a lone-surrogate value every later LongText value on the same QueryDef answered 0x800A0BF5, so later long values were never asked. " +
            "On a read-write copy of r-wire-blank, for each r-wire cell of FText (Text(255)) and FMemo (LongText) a fresh temporary QueryDef PARAMETERS pV <type>; SELECT pV AS V FROM One; Parameters(\"pV\").Value = Arg and read back, " +
            "OpenRecordset(snapshot) with DBEngine.Errors read right after, the V field read; the QueryDef is closed before the next value. Asks the Parameter read-back and the V field tag, BSTR byte length and payload per value, or the error. " +
            "Cells: " + string.Join(", ", CellsOf(FText, FMemo).Select(c => $"{c.Col.Name} {c.Label}")),
            BlankFx, p =>
            {
                p.EngineLite().OpenDb(readOnly: false);
                foreach (Cell cell in CellsOf(FText, FMemo))
                {
                    p.CallBind("q", "db", "CreateQueryDef", A.Str(""), A.Str($"PARAMETERS pV {cell.Col.ParamType}; SELECT pV AS V FROM One;"))
                        .GetBind("qps", "q", "Parameters").Item("qpv", "qps", A.Str("pV")).Put("qpv", "Value", cell.Value).Get("qpv", "Value")
                        .CallBind("qrs", "q", "OpenRecordset", A.I4(Dao.OpenSnapshot)).Errors().ReadAll("qrs", 2, 2).Call("qrs", "Close").Release("qrs")
                        .Release("qpv").Release("qps").Call("q", "Close").Release("q");
                }
                p.FinishLite("db");
            });

        Cell[] cmdCells = [.. Cells.Where(c => c.Id is 202 or 203 or 302 or 303)];
        yield return Q("ado-command-parameters-fresh", AdoRefs("R-ORACLE-1", "OR-WIRE", "ADO-PARAM", "ADO-VALUES"),
            "Repairs r-wire/ado-command-parameters: rows 202, 203, 302 and 303 held the previous row's value after a failed Parameter put, so Execute stored a stale value. " +
            "On a read-write copy of r-wire-blank (ACE OLE DB connection), for each of FInt i4-32768 (202) and i4-minus32769 (203), FLong r8-2147483648 (302) and r8-minus2147483649 (303): a fresh ADODB.Command " +
            "INSERT INTO W (ID, column) VALUES (?, ?) with fresh CreateParameter pI (adInteger) and pV (adSmallInt for FInt, adInteger for FLong), each Value put and read back, Execute with adExecuteNoRecords, Connection.Errors read and cleared right after. " +
            "Then FInt and FLong are read through client static read-only recordsets (field metadata, GetRows). Asks per cell the HRESULT and error of the put and Execute, the read-back, and the stored tag and value or absence.",
            BlankFx, p =>
            {
                p.EngineLite();
                AdoOpenMain(p, "cn", readOnly: false);
                foreach (Cell cell in cmdCells)
                {
                    string m = "m" + cell.Id;
                    int adoType = cell.Col == FInt ? 2 : Ado.Integer;
                    p.Create("ADODB.Command", m).PutRef(m, "ActiveConnection", "cn").Put(m, "CommandText", A.Str($"INSERT INTO W (ID, {cell.Col.Name}) VALUES (?, ?)")).Put(m, "CommandType", A.I4(Ado.CmdText))
                        .GetBind(m + "s", m, "Parameters")
                        .CallBind(m + "i", m, "CreateParameter", A.Str("pI"), A.I4(Ado.Integer), A.I4(Ado.ParamInput)).Call(m + "s", "Append", A.Obj(m + "i"))
                        .CallBind(m + "v", m, "CreateParameter", A.Str("pV"), A.I4(adoType), A.I4(Ado.ParamInput)).Call(m + "s", "Append", A.Obj(m + "v"))
                        .Put(m + "i", "Value", A.I4(cell.Id)).Get(m + "i", "Value")
                        .Put(m + "v", "Value", cell.Value).Get(m + "v", "Value")
                        .Call(m, "Execute", A.Missing, A.Missing, A.I4(Ado.CmdText | Ado.ExecuteNoRecords));
                    ErrClear(p, "cn");
                    p.Release(m + "i").Release(m + "v").Release(m + "s").Release(m);
                }
                AdoColumnRows(p, "rFInt", FInt);
                AdoColumnRows(p, "rFLong", FLong);
                p.FinishLite("cn");
            });
    }

    // r-wire: ADO write of every cell, one fresh recordset per cell --------------------------------------------

    private static void AdoWriteCell(P p, Cell cell)
    {
        p.Create("ADODB.Recordset", "w").Call("w", "Open", A.Str("W"), A.Obj("cn"), A.I4(Ado.OpenKeyset), A.I4(Ado.LockOptimistic), A.I4(Ado.CmdTable)).Get("w", "State")
            .GetBind("wf", "w", "Fields").Item("wi", "wf", A.Str("ID")).Item("wv", "wf", A.Str(cell.Col.Name))
            .Call("w", "AddNew").Put("wi", "Value", A.I4(cell.Id)).Put("wv", "Value", cell.Value).Get("wv", "Value")
            .Call("w", "Update").Get("w", "EditMode");
        ErrClear(p, "cn");
        p.Call("w", "CancelUpdate").Get("w", "EditMode");
        ErrClear(p, "cn");
        p.Release("wi").Release("wv").Release("wf").Call("w", "Close").Get("w", "State").Release("w");
    }

    private static IEnumerable<Probe> WriteProbes()
    {
        (string Slug, Col[] Cols, string What)[] groups =
        [
            ("numeric", [FSingle, FDouble, FCurr, FDate], "FSingle, FDouble, FCurr and FDate"),
            ("text-binary", [FText, FMemo, FBin, FOle], "FText, FMemo, FBin and FOle"),
        ];
        foreach ((string slug, Col[] cols, string what) in groups)
        {
            yield return Q($"ado-write-values-{slug}", AdoRefs("R-ORACLE-1", "OR-WIRE", "ADO-EDIT", "ADO-VALUES"),
                "Repairs r-wire/ado-write-values: the first failed odd-byte FText put left the one shared recordset unusable (112 later calls raised \"Update or CancelUpdate without AddNew or Edit\"), so rows 811 onward and FMemo, FBin and FOle were never written. " +
                $"On a read-write copy of r-wire-blank (ACE OLE DB), for each r-wire cell of {what}: a fresh server keyset optimistic adCmdTable recordset on W, AddNew, Fields(\"ID\").Value = id, Fields(column).Value = Arg and read back, Update and EditMode, " +
                "Connection.Errors read and cleared, CancelUpdate and EditMode, Errors read and cleared, Close and State. Then each of these columns is read through a client static read-only recordset (field metadata, GetRows(-1)). " +
                $"Asks per cell the HRESULT and error of the put, Update and CancelUpdate, and the stored tag and exact value or absence. Cells: {CellList}",
                BlankFx, p =>
                {
                    p.EngineLite();
                    AdoOpenMain(p, "cn", readOnly: false);
                    foreach (Cell cell in CellsOf(cols))
                    {
                        AdoWriteCell(p, cell);
                    }
                    foreach (Col c in cols)
                    {
                        AdoColumnRows(p, "r" + c.Name, c);
                    }
                    p.FinishLite("cn");
                });
        }
    }

    // r-wire: SQL literals for long and binary cells, Decimal columns -----------------------------------------

    private static string Hex(byte[] bytes) => "0x" + Convert.ToHexString(bytes);

    /// <summary>A Decimal cell: row ID, column, label, the SQL numeric literal and the DAO Arg for the put route.</summary>
    private sealed record DecCell(int Id, string Column, string Label, string Sql, Arg Value);

    private static readonly DecCell[] DecCells =
    [
        new(1300, "FDecS", "cy-1.5", "1.5", A.Cy(1.5m)),
        new(1301, "FDecS", "cy-minus-0.0001", "-0.0001", A.Cy(-0.0001m)),
        new(1302, "FDecS", "cy-14.4-digits", "12345678901234.5678", A.Cy(12345678901234.5678m)),
        new(1303, "FDecS", "r8-0.1", "0.1", A.R8(0.1)),
        new(1304, "FDecS", "r8-0.5", "0.5", A.R8(0.5)),
        new(1305, "FDecS", "i4-7", "7", A.I4(7)),
        new(1310, "FDecI", "bstr-28-digits", "7922816251426433759354395033", A.Str("7922816251426433759354395033")),
        new(1311, "FDecI", "bstr-29-digits-max", "79228162514264337593543950335", A.Str("79228162514264337593543950335")),
        new(1312, "FDecI", "bstr-minus-7", "-7", A.Str("-7")),
        new(1313, "FDecI", "r8-2.5", "2.5", A.R8(2.5)),
        new(1314, "FDecI", "i4-7", "7", A.I4(7)),
    ];

    private static IEnumerable<Probe> DecimalProbes()
    {
        Arg sqlFile = A.Out(Card + "-sql-extra.accdb");
        yield return Create("create-sql-extra", Wire("R-FMT-1", "R-SQL-1"),
            "Repairs r-wire/create-sql: the SQL route had no cell for the 5,000-character and 4,000-pair Memo values (907, 908) and for FBin 1000 to 1004 and FOle (hex literals were not attempted). " +
            "Fixture r-fix-wire-ado-sql-extra, created in ACCDB by DBEngine.CreateDatabase(path, dbLangGeneral, 128): table W(ID LONG primary key, FMemo MEMO, FBin BINARY(255), FOle LONGBINARY), then INSERT INTO W (ID, column) VALUES (id, literal) " +
            "through Database.Execute(dbFailOnError), DBEngine.Errors read right after each: FMemo 907 a 5,000-character literal and 908 a literal of 4,000 surrogate pairs; FBin 1000 the literal 0x, 1001 0x000001, 1002 0x00000000, 1003 a 255-byte hex literal, 1004 a 256-byte hex literal; " +
            "FOle 1100 0x, 1101 0x000001, 1102 a 256-byte hex literal, 1103 a 5,000-byte hex literal. Then DAO snapshots read each column (field info, every Value once) and GetRows(100) of each. Asks per cell whether the statement succeeds and what is stored, with tag, exact value and length. " +
            "Not asked: cells with no SQL literal (NaN, infinities, odd-byte BSTRs, the 8,001-byte Memo).",
            p =>
            {
                p.EngineLite().CallBind("db", "eng", "CreateDatabase", sqlFile, A.Str(Dao.LangGeneral), A.I4(Dao.Version120))
                    .Call("db", "Execute", A.Str("CREATE TABLE W (ID LONG CONSTRAINT PK_W PRIMARY KEY, FMemo MEMO, FBin BINARY(255), FOle LONGBINARY)"), A.I4(Dao.FailOnError)).Errors();
                (int Id, string Col, string Literal)[] inserts =
                [
                    (907, "FMemo", "'" + new string('m', 5000) + "'"),
                    (908, "FMemo", "'" + string.Concat(Enumerable.Repeat("\U0001D11E", 4000)) + "'"),
                    (1000, "FBin", "0x"),
                    (1001, "FBin", Hex([0x00, 0x00, 0x01])),
                    (1002, "FBin", Hex([0x00, 0x00, 0x00, 0x00])),
                    (1003, "FBin", Hex(Ramp(255, 1))),
                    (1004, "FBin", Hex(Ramp(256, 1))),
                    (1100, "FOle", "0x"),
                    (1101, "FOle", Hex([0x00, 0x00, 0x01])),
                    (1102, "FOle", Hex(Ramp(256, 1))),
                    (1103, "FOle", Hex(Ramp(5000, 7))),
                ];
                foreach ((int id, string col, string literal) in inserts)
                {
                    p.Call("db", "Execute", A.Str($"INSERT INTO W (ID, {col}) VALUES ({id}, {literal})"), A.I4(Dao.FailOnError)).Errors();
                }
                ReadColumns(p, [FMemo, FBin, FOle]);
                foreach (Col c in new[] { FMemo, FBin, FOle })
                {
                    string g = "g" + c.Name;
                    p.OpenRs(g, "db", $"SELECT ID, {c.Name} FROM W WHERE ID BETWEEN {c.Base} AND {c.Base + 99} ORDER BY ID", Dao.OpenSnapshot)
                        .Call(g, "GetRows", A.I4(100)).Errors().Call(g, "Close").Release(g);
                }
                p.FinishLite("db");
            });

        foreach (string route in new[] { "sql-literal", "dao-put" })
        {
            Arg file = A.Out($"{Card}-fdec-{route}.accdb");
            string how = route == "sql-literal"
                ? "INSERT INTO WD (ID, column) VALUES (id, SQL numeric literal) through Database.Execute(dbFailOnError)"
                : "a DAO table-type recordset AddNew, Fields(\"ID\").Value, Fields(column).Value = Arg (fractional values as VT_CY, R8 and I4; integers as BSTR integer text or I4), the Value read back, Update";
            yield return Create("create-fdec-" + route, Wire("R-FMT-1", "R-ADO-1", "R-EXPR-1"),
                "Repairs r-wire/create-dao (FDec): the Decimal field was made by CreateField(name, 20) with Precision 38 and Scale 0, every fractional put failed, the stored values were unreadable by DAO and GetRows(100) stopped after 4 of 10 rows; " +
                "and r-wire/expr-decimal-fields: CDec expressions were refused. " +
                $"Fixture r-fix-wire-ado-fdec-{route}: an ACCDB created by DBEngine.CreateDatabase(path, dbLangGeneral, 128) and closed, then an ADO connection (Provider ACE) runs CREATE TABLE WD (ID INTEGER primary key, FDecS DECIMAL(18,4), FDecI DECIMAL(28,0)) and closes. " +
                $"DAO reopens read-write and writes each cell (only ID and one column) by {how}, DBEngine.Errors read right after each. Cells: {string.Join(", ", DecCells.Select(c => $"{c.Id} {c.Column} {c.Label}"))}. " +
                "After closing and reopening read-only: TableDef field Type, Size, Attributes and Properties of FDecS and FDecI; a DAO snapshot of each column (field info, every Value once) and GetRows(100) of each; " +
                "ten expression snapshots, each alone (FDecS + 1, FDecS * 3, FDecS / 3, -FDecS, FDecS + FDecS, FDecI + 1, FDecI * 2, FDecI / 7, Sum(FDecS), Sum(FDecI)), with field info and every Value; then, through ADO, a client static read-only recordset of ID, FDecS and FDecI (field metadata, every Value, GetRows(-1)). " +
                "Asks the stored values, tags and Decimal words per route, and the errors.",
                p =>
                {
                    p.EngineLite().CallBind("db", "eng", "CreateDatabase", file, A.Str(Dao.LangGeneral), A.I4(Dao.Version120)).Call("db", "Close").Release("db");
                    AdoOpen(p, "cn", file, readOnly: false);
                    ExecNo(p, "cn", "CREATE TABLE WD (ID INTEGER CONSTRAINT PK_WD PRIMARY KEY, FDecS DECIMAL(18,4), FDecI DECIMAL(28,0))");
                    ErrClear(p, "cn");
                    p.Call("cn", "Close").Release("cn");
                    p.CallBind("db", "eng", "OpenDatabase", file, A.Bool(false), A.Bool(false));
                    if (route == "sql-literal")
                    {
                        foreach (DecCell cell in DecCells)
                        {
                            p.Call("db", "Execute", A.Str($"INSERT INTO WD (ID, {cell.Column}) VALUES ({cell.Id}, {cell.Sql})"), A.I4(Dao.FailOnError)).Errors();
                        }
                    }
                    else
                    {
                        p.OpenRs("w", "db", "WD", Dao.OpenTable).GetBind("wf", "w", "Fields").Item("wid", "wf", A.Str("ID")).Item("wS", "wf", A.Str("FDecS")).Item("wI", "wf", A.Str("FDecI"));
                        foreach (DecCell cell in DecCells)
                        {
                            string fx = cell.Column == "FDecS" ? "wS" : "wI";
                            p.Call("w", "AddNew").Put("wid", "Value", A.I4(cell.Id)).Put(fx, "Value", cell.Value).Get(fx, "Value").Call("w", "Update").Errors();
                        }
                        p.Release("wid").Release("wS").Release("wI").Release("wf").Call("w", "Close").Release("w");
                    }
                    p.Call("db", "Close").Release("db");

                    p.CallBind("db", "eng", "OpenDatabase", file, A.Bool(false), A.Bool(true))
                        .GetBind("tds", "db", "TableDefs").Call("tds", "Refresh").Item("td", "tds", A.Str("WD")).GetBind("tdf", "td", "Fields").Call("tdf", "Refresh");
                    foreach (string f in new[] { "FDecS", "FDecI" })
                    {
                        p.Item("df", "tdf", A.Str(f)).Get("df", "Type").Get("df", "Size").Get("df", "Attributes")
                            .GetBind("dfp", "df", "Properties").Each("dfp", "pp", 40, b => b.Get("pp", "Name").Get("pp", "Value")).Release("dfp").Release("df");
                    }
                    p.Release("tdf").Release("td").Release("tds");
                    foreach ((string f, int lo) in new[] { ("FDecS", 1300), ("FDecI", 1310) })
                    {
                        string rs = "s" + f;
                        p.OpenRs(rs, "db", $"SELECT ID, {f} FROM WD WHERE ID BETWEEN {lo} AND {lo + 9} ORDER BY ID", Dao.OpenSnapshot).Errors();
                        FieldInfo(p, rs);
                        p.ReadAll(rs, 32, 4).Call(rs, "Close").Release(rs);
                        string g = "g" + f;
                        p.OpenRs(g, "db", $"SELECT ID, {f} FROM WD WHERE ID BETWEEN {lo} AND {lo + 9} ORDER BY ID", Dao.OpenSnapshot)
                            .Call(g, "GetRows", A.I4(100)).Errors().Call(g, "Close").Release(g);
                    }
                    string[] exprs = ["FDecS + 1", "FDecS * 3", "FDecS / 3", "-FDecS", "FDecS + FDecS", "FDecI + 1", "FDecI * 2", "FDecI / 7", "Sum(FDecS)", "Sum(FDecI)"];
                    for (int i = 0; i < exprs.Length; i++)
                    {
                        string rs = "x" + i;
                        string tail = exprs[i].StartsWith("Sum(", StringComparison.Ordinal) ? "FROM WD" : "FROM WD ORDER BY ID";
                        p.OpenRs(rs, "db", $"SELECT {exprs[i]} AS V {tail}", Dao.OpenSnapshot).Errors();
                        FieldInfo(p, rs);
                        p.ReadAll(rs, 16, 2).Call(rs, "Close").Release(rs);
                    }
                    p.Call("db", "Close").Release("db");

                    AdoOpen(p, "cn", file, readOnly: true);
                    p.Create("ADODB.Recordset", "ar").Put("ar", "CursorLocation", A.I4(Ado.UseClient))
                        .Call("ar", "Open", A.Str("SELECT ID, FDecS, FDecI FROM WD ORDER BY ID"), A.Obj("cn"), A.I4(Ado.OpenStatic), A.I4(Ado.LockReadOnly), A.I4(Ado.CmdText)).Get("ar", "State");
                    FieldMeta(p, "ar");
                    AdoReadAll(p, "ar", 16, 4);
                    p.Call("ar", "MoveFirst").Call("ar", "GetRows", A.I4(Ado.GetRowsRest)).Call("ar", "Close").Release("ar");
                    ErrClear(p, "cn");
                    p.FinishLite("cn");
                });
        }
    }

    // r-wire: FieldSize is a property --------------------------------------------------------------------------

    private static IEnumerable<Probe> ChunkProbes()
    {
        Arg[][] oleChunks =
        [
            [A.Bytes(0x00, 0x00, 0x01)], [A.Bytes()], [A.Bytes(0x00, 0x00, 0x00, 0x00)], [A.BstrBytes(0x01, 0x02, 0x03)], [A.Str("AB")], [A.Bytes(0x01, 0x02, 0x03), A.Bytes(0x00, 0x00), A.Bytes(0xff)],
            [A.Null], [A.Empty], [A.Missing], [A.I4(5)], [A.R8(1.5)], [A.Bytes(Ramp(256, 1))], [A.Bool(true)],
        ];
        yield return Q("chunk-ole-append-fieldsize", Wire("R-FMT-1", "R-DAO-1"),
            "Repairs r-wire/chunk-ole-append: Field.FieldSize was invoked as a method (the call kind), refused with 0x800A0CB3 on all rows, so the size question is unanswered. FieldSize is now read with a property get. " +
            "On a dynaset of SELECT ID, FOle FROM W over a read-write copy of r-wire-blank: thirteen rows each AddNew, ID = n, Field.AppendChunk of the given Arg(s), Update, DBEngine.Errors read right after: 1 bytes 00 00 01, 2 empty array, 3 four zero bytes, 4 BSTR of 3 odd bytes, " +
            "5 BSTR \"AB\", 6 three arrays in sequence, 7 Null, 8 Empty, 9 Missing, 10 I4, 11 R8, 12 a 256-byte array, 13 Boolean. Then each row re-read through FindFirst with NoMatch, FieldSize (property get, Errors after) and GetChunk(0, 300), and the table read once. " +
            "Asks per row the HRESULT of AppendChunk and Update, the FieldSize, and the stored bytes.",
            BlankFx, p =>
            {
                p.EngineLite().OpenDb(readOnly: false).OpenRs("rs", "db", "SELECT ID, FOle FROM W ORDER BY ID", Dao.OpenDynaset).GetBind("fl", "rs", "Fields")
                    .Item("fid", "fl", A.Str("ID")).Item("fo", "fl", A.Str("FOle"));
                for (int i = 0; i < oleChunks.Length; i++)
                {
                    p.Call("rs", "AddNew").Put("fid", "Value", A.I4(i + 1));
                    foreach (Arg chunk in oleChunks[i])
                    {
                        p.Call("fo", "AppendChunk", chunk);
                    }
                    p.Call("rs", "Update").Errors();
                }
                for (int i = 0; i < oleChunks.Length; i++)
                {
                    p.Call("rs", "FindFirst", A.Str($"ID = {i + 1}")).Get("rs", "NoMatch").Get("fo", "FieldSize").Errors().Call("fo", "GetChunk", A.I4(0), A.I4(300));
                }
                p.Release("fid").Release("fo").Release("fl").Call("rs", "Close").Release("rs")
                    .OpenRs("rr", "db", "SELECT ID, FOle FROM W ORDER BY ID", Dao.OpenSnapshot).ReadAll("rr", 16, 4).Call("rr", "Close").Release("rr").FinishLite("db");
            });

        Arg[][] memoChunks =
        [
            [A.Str("abc")], [A.Str("a\0b")], [A.BstrBytes(0x41)], [A.BstrBytes(0x41, 0x00, 0x42)], [A.BstrUnits("\uD800")], [A.Str("ab"), A.BstrBytes(0x41), A.Str("cd")], [A.Bytes(0x41, 0x00, 0x42, 0x00)],
            [A.Null], [A.Empty], [A.Missing], [A.I4(5)], [A.Str("")], [A.Str(new string('m', 300))],
        ];
        yield return Q("chunk-memo-text-append-fieldsize", Wire("R-FMT-1", "II-02", "R-DAO-1"),
            "Repairs r-wire/chunk-memo-text-append: Field.FieldSize was invoked as a method (the call kind), refused with 0x800A0CB3 on all rows, so the size question is unanswered. FieldSize is now read with a property get. " +
            "On a dynaset of SELECT ID, FMemo, FText FROM W over a read-write copy of r-wire-blank: thirteen rows each AddNew, ID = n, AppendChunk on FMemo of: 1 \"abc\", 2 U+0000 inside, 3 BSTR of 1 byte, 4 BSTR of 3 bytes, 5 lone high surrogate, 6 \"ab\" then odd BSTR then \"cd\", " +
            "7 UI1 array of four bytes, 8 Null, 9 Empty, 10 Missing, 11 I4, 12 empty BSTR, 13 300 characters; Update with DBEngine.Errors read right after. Rows 21 to 23: AppendChunk on FText of \"abc\", an odd BSTR and a UI1 array. " +
            "Then each Memo row re-read through FindFirst with NoMatch, FieldSize (property get, Errors after) and GetChunk(0, 40), FieldSize of the three Text rows, and the table read once. Asks per row the HRESULT of each step, the FieldSize, the stored values and BSTR byte lengths.",
            BlankFx, p =>
            {
                p.EngineLite().OpenDb(readOnly: false).OpenRs("rs", "db", "SELECT ID, FMemo, FText FROM W ORDER BY ID", Dao.OpenDynaset).GetBind("fl", "rs", "Fields")
                    .Item("fid", "fl", A.Str("ID")).Item("fm", "fl", A.Str("FMemo")).Item("ft", "fl", A.Str("FText"));
                for (int i = 0; i < memoChunks.Length; i++)
                {
                    p.Call("rs", "AddNew").Put("fid", "Value", A.I4(i + 1));
                    foreach (Arg chunk in memoChunks[i])
                    {
                        p.Call("fm", "AppendChunk", chunk);
                    }
                    p.Call("rs", "Update").Errors();
                }
                Arg[] textChunks = [A.Str("abc"), A.BstrBytes(0x41), A.Bytes(0x41, 0x00)];
                for (int i = 0; i < textChunks.Length; i++)
                {
                    p.Call("rs", "AddNew").Put("fid", "Value", A.I4(21 + i)).Call("ft", "AppendChunk", textChunks[i]).Call("rs", "Update").Errors();
                }
                for (int i = 0; i < memoChunks.Length; i++)
                {
                    p.Call("rs", "FindFirst", A.Str($"ID = {i + 1}")).Get("rs", "NoMatch").Get("fm", "FieldSize").Errors().Call("fm", "GetChunk", A.I4(0), A.I4(40));
                }
                for (int i = 0; i < textChunks.Length; i++)
                {
                    p.Call("rs", "FindFirst", A.Str($"ID = {21 + i}")).Get("rs", "NoMatch").Get("ft", "FieldSize").Errors();
                }
                p.Release("fid").Release("fm").Release("ft").Release("fl").Call("rs", "Close").Release("rs")
                    .OpenRs("rr", "db", "SELECT ID, FMemo, FText FROM W ORDER BY ID", Dao.OpenSnapshot).ReadAll("rr", 32, 4).Call("rr", "Close").Release("rr").FinishLite("db");
            });
    }

    // r-str-ado-act repairs --------------------------------------------------------------------------------------

    private static IEnumerable<Probe> StrAdoProbes()
    {
        // ADO-CONN / ADO-PROPS ---------------------------------------------------------------------------------
        yield return Q("ado-open-provider-property-ace-first", AdoRefs("ADO-CONN", "ADO-PROPS"),
            "Repairs r-str-ado-act/ado-open-provider-property: reading Properties on a fresh Connection pinned MSDASQL, so the probe never inventoried ACE and its Open failure was ODBC. " +
            "A fresh Connection sets Provider = ACE before anything is read; then State, Provider, ConnectionString, Mode, CursorLocation, timeouts, Version, Attributes, IsolationLevel and the full Properties inventory (Name, Type, Value, Attributes); " +
            "Properties(\"Data Source\") is set to the fixture path through a retained Property wrapper (Value and Attributes read back) and Mode = adModeRead; Open; the same state and inventory, DefaultDatabase, the retained wrapper's Value, Connection.Errors; Close; " +
            "state, the wrapper, Properties.Count and the inventory once more. Asks which properties exist and what they hold in each state for the ACE provider.",
            Ado5Fx, p =>
            {
                p.EngineLite().Create("ADODB.Connection", "cn").Put("cn", "Provider", A.Str(Ace));
                ErrClear(p, "cn");
                ConnState(p, "cn");
                Props(p, "cn", "provider");
                p.GetBind("cnp", "cn", "Properties").Item("ds", "cnp", A.Str("Data Source")).Put("ds", "Value", A.In()).Get("ds", "Value").Get("ds", "Attributes")
                    .Put("cn", "Mode", A.I4(Ado.ModeRead)).Call("cn", "Open").Modules("after-open");
                ErrClear(p, "cn");
                ConnState(p, "cn");
                p.Get("cn", "DefaultDatabase");
                Props(p, "cn", "open");
                p.Get("ds", "Value");
                AdoErrors(p, "cn");
                p.Call("cn", "Close");
                ConnState(p, "cn");
                p.Get("ds", "Value").Get("cnp", "Count");
                Props(p, "cn", "closed");
                p.Release("ds").Release("cnp").Release("cn").FinishLite();
            });

        (string Bind, string Trigger, Action<P> Read)[] pins =
        [
            ("pa", "nothing read before the assignment", _ => { }),
            ("pb", "Provider read first", q => q.Get("pb", "Provider")),
            ("pc", "Properties.Count read first", q => q.GetBind("pbp", "pc", "Properties").Get("pbp", "Count").Release("pbp")),
            ("pd", "State read first", q => q.Get("pd", "State")),
            ("pe", "ConnectionString read first", q => q.Get("pe", "ConnectionString")),
            ("pf", "Mode read first", q => q.Get("pf", "Mode")),
        ];
        yield return Q("ado-provider-pin-isolated", AdoRefs("ADO-CONN", "ADO-PROVIDER", "ADO-PROPS"),
            "Repairs r-str-ado-act/ado-open-provider-property: whether the Provider read or the Properties read made a later Provider = ACE fail was not separated. " +
            "Six fresh Connections, each given one first read only: (a) nothing, (b) Provider, (c) Properties.Count, (d) State, (e) ConnectionString, (f) Mode. After that read each assigns Provider = ACE, reads Provider back and reads Connection.Errors right after the assignment. " +
            "No Connection is opened. Asks which first read, if any, makes the Provider assignment fail and what Provider reads afterwards.",
            null, p =>
            {
                p.EngineLite();
                foreach ((string bind, string _, Action<P> read) in pins)
                {
                    p.Create("ADODB.Connection", bind).Get(bind, "Provider");
                    read(p);
                    p.Put(bind, "Provider", A.Str(Ace));
                    AdoErrors(p, bind);
                    p.Get(bind, "Provider").Release(bind);
                }
                p.FinishLite();
            });

        // ADO-RESULTS ------------------------------------------------------------------------------------------
        yield return Q("ado-execute-results-rows", AdoRefs("ADO-RESULTS"),
            "Repairs r-str-ado-act/ado-execute-results: the connection ran in read mode (Mode = 1) and a later statement's recordsets stayed unbound after an earlier error. " +
            "On a read-write ACE connection (own copy of r-fix-wire-ado-ado5), each statement on its own with Connection.Errors read and cleared right after: Connection.Execute of a row SELECT of ID, Code, T from K (State, BOF, EOF, CursorType, LockType, CursorLocation, RecordCount, all rows, Close); " +
            "a SELECT with no rows (State, Fields.Count); the saved query QEmpty by name (state); a SELECT with adExecuteNoRecords (State of the result). Asks which return an open empty, closed or no Recordset.",
            Ado5Fx, p =>
            {
                p.EngineLite();
                AdoOpenMain(p, "cn", readOnly: false);
                p.CallBind("r1", "cn", "Execute", A.Str("SELECT ID, Code, T FROM K ORDER BY ID"), A.Missing, A.I4(Ado.CmdText));
                ErrClear(p, "cn");
                RsState(p, "r1");
                p.Get("r1", "RecordCount");
                AdoReadAll(p, "r1", 8, 8);
                p.Call("r1", "Close").Release("r1");
                p.CallBind("r2", "cn", "Execute", A.Str("SELECT * FROM K WHERE 1=0"), A.Missing, A.I4(Ado.CmdText));
                ErrClear(p, "cn");
                RsState(p, "r2");
                p.GetBind("r2f", "r2", "Fields").Get("r2f", "Count").Release("r2f").Call("r2", "Close").Release("r2");
                p.CallBind("r3", "cn", "Execute", A.Str("QEmpty"));
                ErrClear(p, "cn");
                RsState(p, "r3");
                p.Call("r3", "Close").Release("r3");
                p.CallBind("r7", "cn", "Execute", A.Str("SELECT ID FROM K"), A.Missing, A.I4(Ado.CmdText | Ado.ExecuteNoRecords)).Get("r7", "State");
                ErrClear(p, "cn");
                p.FinishLite("cn");
            });

        yield return Q("ado-execute-update-compound", AdoRefs("ADO-RESULTS", "ADO-CURSOR"),
            "Repairs r-str-ado-act/ado-execute-results: the UPDATE ran on a read-mode connection (\"Operation must use an updateable query\") and the compound statement's later steps were skipped. " +
            "On a read-write ACE connection (own copy of r-fix-wire-ado-ado5): Connection.Execute of UPDATE K SET T = T WHERE Code = 'a1' (State of the result, Errors read and cleared right after), the row read back; " +
            "then Execute of \"SELECT 1 AS A; SELECT 2 AS B\" with Errors read and cleared right after, and the result's State. Asks what the UPDATE returns on a writable connection and what the compound statement answers.",
            Ado5Fx, p =>
            {
                p.EngineLite();
                AdoOpenMain(p, "cn", readOnly: false);
                p.CallBind("r6", "cn", "Execute", A.Str("UPDATE K SET T = T WHERE Code = 'a1'"), A.Missing, A.I4(Ado.CmdText)).Get("r6", "State");
                ErrClear(p, "cn");
                ExecRead(p, "cn", "rb", "SELECT ID, Code, T FROM K WHERE Code = 'a1'");
                ErrClear(p, "cn");
                p.CallBind("r4", "cn", "Execute", A.Str("SELECT 1 AS A; SELECT 2 AS B"), A.Missing, A.I4(Ado.CmdText)).Get("r4", "State");
                ErrClear(p, "cn");
                p.FinishLite("cn");
            });

        // ADO-CURSOR -------------------------------------------------------------------------------------------
        yield return Q("ado-rs-cursors-writable", AdoRefs("ADO-CURSOR"),
            "Repairs r-str-ado-act/ado-rs-cursors: the connection ran in read mode (Mode = 1), so effective LockType and Supports were confounded. " +
            "On a read-write ACE connection (own copy of r-fix-wire-ado-ado5), for eight (CursorLocation, CursorType, LockType, Options, source) requests: server forward-only read-only, keyset optimistic, dynamic pessimistic, static read-only, " +
            "client static batch-optimistic, client forward-only read-only, adCmdTable keyset read-only on K and adCmdTableDirect keyset read-only on K; read effective CursorType, LockType, CursorLocation, State, RecordCount, CacheSize, BOF, EOF, every Supports option, " +
            "then MoveLast, AbsolutePosition and RecordCount, then Close, with Connection.Errors read and cleared after each recordset. Asks the requested versus effective tuple and the supported options on a writable connection.",
            Ado5Fx, p =>
            {
                p.EngineLite();
                AdoOpenMain(p, "cn", readOnly: false);
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
                foreach ((string bind, int loc, int type, int lck, int opts, string src) in cases)
                {
                    AdoRs(p, bind, src, "cn", loc, type, lck, opts);
                    ErrClear(p, "cn");
                    RsState(p, bind);
                    p.Get(bind, "RecordCount").Get(bind, "CacheSize");
                    foreach ((string _, int value) in Ado.Supports)
                    {
                        p.Call(bind, "Supports", A.I4(value));
                    }
                    p.Call(bind, "MoveLast").Get(bind, "AbsolutePosition").Get(bind, "RecordCount").Call(bind, "Close").Release(bind);
                    ErrClear(p, "cn");
                }
                p.FinishLite("cn");
            });

        // ADO-PARAM --------------------------------------------------------------------------------------------
        yield return Q("ado-params-saved-query", AdoRefs("ADO-PARAM"),
            "Repairs r-str-ado-act/ado-params: part (a) ran on a read-mode connection and its failing Execute left later steps unbound. On a read-write ACE connection (own copy of r-fix-wire-ado-ado5), a Command for the saved query QParam (adCmdStoredProc): " +
            "Parameters.Count and each Name, Type, Direction, Size, Precision, NumericScale and Value on first access, Refresh, the inventory again, Parameters(0).Value = \"a1\" and read back, Execute, and all rows; Connection.Errors read and cleared right after Refresh and after Execute. Asks the discovered parameters and their binding.",
            Ado5Fx, p =>
            {
                p.EngineLite();
                AdoOpenMain(p, "cn", readOnly: false);
                p.Create("ADODB.Command", "cq").PutRef("cq", "ActiveConnection", "cn").Put("cq", "CommandText", A.Str("QParam")).Put("cq", "CommandType", A.I4(Ado.CmdStoredProc)).GetBind("pq", "cq", "Parameters");
                ParamInventory(p, "pq");
                p.Call("pq", "Refresh");
                ErrClear(p, "cn");
                ParamInventory(p, "pq");
                p.Item("p0", "pq", A.I4(0)).Put("p0", "Value", A.Str("a1")).Get("p0", "Value").CallBind("rq", "cq", "Execute");
                ErrClear(p, "cn");
                AdoReadAll(p, "rq", 4, 4);
                p.Release("p0").Release("rq").Release("pq").Release("cq").FinishLite("cn");
            });

        const string twoMarkers = "SELECT ID, Code FROM K WHERE Code = ? OR Code = ?";
        yield return Q("ado-params-refresh-only", AdoRefs("ADO-PARAM"),
            "Repairs r-str-ado-act/ado-params: Refresh followed by two Appends double-counted the parameters, so a failure about \"?_1\" said nothing about named mapping. " +
            "On a read-write ACE connection (own copy of r-fix-wire-ado-ado5), a Command with the text \"" + twoMarkers + "\" and Refresh only, no Append: Parameters.Count before Refresh, Refresh, the inventory (Name, Type, Direction, Size, Precision, NumericScale, Value), " +
            "Parameters(0).Value = \"a2\" and Parameters(1).Value = \"b3\" with each read back, Execute and all rows; Connection.Errors read and cleared right after Refresh and after Execute. Asks the discovered parameters and whether Execute binds them.",
            Ado5Fx, p =>
            {
                p.EngineLite();
                AdoOpenMain(p, "cn", readOnly: false);
                NewCmd(p, "cp", "cn", twoMarkers);
                p.GetBind("pp", "cp", "Parameters").Get("pp", "Count").Call("pp", "Refresh");
                ErrClear(p, "cn");
                ParamInventory(p, "pp");
                p.Item("p0", "pp", A.I4(0)).Put("p0", "Value", A.Str("a2")).Get("p0", "Value").Item("p1", "pp", A.I4(1)).Put("p1", "Value", A.Str("b3")).Get("p1", "Value").CallBind("rp", "cp", "Execute");
                ErrClear(p, "cn");
                AdoReadAll(p, "rp", 8, 4);
                p.Release("p0").Release("p1").Release("rp").Release("pp").Release("cp").FinishLite("cn");
            });

        yield return Q("ado-params-manual-append", AdoRefs("ADO-PARAM"),
            "Repairs r-str-ado-act/ado-params: Refresh followed by two Appends double-counted the parameters; and a failing Execute left the next steps unbound. " +
            "On a read-write ACE connection (own copy of r-fix-wire-ado-ado5), a Command with the text \"" + twoMarkers + "\" with no Refresh: Parameters.Count, CreateParameter(\"x\", adVarWChar, adParamInput, 10, \"a2\") and Append, the inventory, " +
            "CreateParameter(\"y\", adVarWChar, adParamInput, 10, \"b3\") and Append, the inventory, Execute and all rows; Delete(\"x\"), Count, Execute again and its State; Connection.Errors read and cleared right after each Append, Execute and Delete. Asks the manual parameters and their binding.",
            Ado5Fx, p =>
            {
                p.EngineLite();
                AdoOpenMain(p, "cn", readOnly: false);
                NewCmd(p, "cp", "cn", twoMarkers);
                p.GetBind("pp", "cp", "Parameters").Get("pp", "Count");
                p.CallBind("px", "cp", "CreateParameter", A.Str("x"), A.I4(Ado.VarWChar), A.I4(Ado.ParamInput), A.I4(10), A.Str("a2")).Call("pp", "Append", A.Obj("px"));
                ErrClear(p, "cn");
                ParamInventory(p, "pp");
                p.CallBind("py", "cp", "CreateParameter", A.Str("y"), A.I4(Ado.VarWChar), A.I4(Ado.ParamInput), A.I4(10), A.Str("b3")).Call("pp", "Append", A.Obj("py"));
                ErrClear(p, "cn");
                ParamInventory(p, "pp");
                p.CallBind("rp", "cp", "Execute");
                ErrClear(p, "cn");
                AdoReadAll(p, "rp", 8, 4);
                p.Call("pp", "Delete", A.Str("x"));
                ErrClear(p, "cn");
                p.Get("pp", "Count").CallBind("rp2", "cp", "Execute").Get("rp2", "State");
                ErrClear(p, "cn");
                p.Release("px").Release("py").Release("rp").Release("rp2").Release("pp").Release("cp").FinishLite("cn");
            });

        // ADO-SESSION ------------------------------------------------------------------------------------------
        yield return Q("ado-session-writable", AdoRefs("ADO-SESSION"),
            "Repairs r-str-ado-act/ado-session: the connections ran in read mode (Mode = 1), confounding the cursor and lock results. " +
            "On two read-write ACE connections (own copy of r-fix-wire-ado-ado5): two Recordsets (keyset read-only of K, forward-only read-only of J) and a Command (ActiveConnection set by reference, CommandText SELECT * FROM D) on the first, each ActiveConnection read (object identity captured); " +
            "a failing Execute on the second connection with both Errors collections read right after; then the first connection is closed and the State of each Recordset, the Command's ActiveConnection and State and the second connection's State are read. Asks whether the objects share one Connection and what Close leaves.",
            Ado5Fx, p =>
            {
                p.EngineLite();
                AdoOpenMain(p, "cn", readOnly: false);
                AdoRs(p, "rs1", "SELECT * FROM K ORDER BY ID", "cn", Ado.UseServer, Ado.OpenKeyset, Ado.LockReadOnly, Ado.CmdText);
                AdoRs(p, "rs2", "SELECT * FROM J", "cn", Ado.UseServer, Ado.OpenForwardOnly, Ado.LockReadOnly, Ado.CmdText);
                p.GetBind("ac1", "rs1", "ActiveConnection").Get("ac1", "State").GetBind("ac2", "rs2", "ActiveConnection")
                    .Create("ADODB.Command", "cmd").PutRef("cmd", "ActiveConnection", "cn").Put("cmd", "CommandText", A.Str("SELECT * FROM D"))
                    .CallBind("rs3", "cmd", "Execute").Get("rs3", "State").GetBind("ac3", "rs3", "ActiveConnection").GetBind("acc", "cmd", "ActiveConnection");
                AdoOpenMain(p, "cn2", readOnly: false);
                p.Call("cn2", "Execute", A.Str("SELEC bad"), A.Missing, A.I4(Ado.CmdText));
                AdoErrors(p, "cn2");
                AdoErrors(p, "cn");
                p.Call("cn", "Close").Get("rs1", "State").Get("rs2", "State").Get("rs3", "State").Get("ac1", "State")
                    .Get("cmd", "ActiveConnection").Get("cmd", "State").Get("cn2", "State")
                    .Release("ac1").Release("ac2").Release("ac3").Release("acc").Release("rs1").Release("rs2").Release("rs3").Release("cmd");
                p.FinishLite("cn2");
            });

        // ADO-EDIT ---------------------------------------------------------------------------------------------
        yield return Q("ado-edit-fresh", AdoRefs("ADO-EDIT", "ADO-VALUES"),
            "Repairs r-str-ado-act/ado-edit: fixture state was not controlled (n6 was read back as ID 5 on a copy that may have been mutated) and Errors were read only once at the end. " +
            "On a read-write ACE connection (own copy of r-fix-wire-ado-ado5) the stored rows of K (ID, Code, T) are read first; then a server keyset optimistic recordset on K: with Fields(\"T\") retained read EditMode; set T = \"edited\" and read Value, OriginalValue, UnderlyingValue and EditMode before and after Update; " +
            "set T = \"moved\" then MoveNext (EditMode after), MovePrevious and read T; set T = \"cancel\", CancelUpdate and read T; AddNew with Code = \"n6\", Update and read ID; Close; the stored rows of K are read again. Connection.Errors is read and cleared after each of these steps. Asks what each step reports and the generated ID.",
            Ado5Fx, p =>
            {
                p.EngineLite();
                AdoOpenMain(p, "cn", readOnly: false);
                ExecRead(p, "cn", "r0", "SELECT ID, Code, T FROM K ORDER BY ID");
                ErrClear(p, "cn");
                AdoRs(p, "rs", "SELECT * FROM K ORDER BY ID", "cn", Ado.UseServer, Ado.OpenKeyset, Ado.LockOptimistic, Ado.CmdText);
                p.GetBind("fl", "rs", "Fields").Item("ft", "fl", A.Str("T")).Item("fc", "fl", A.Str("Code")).Item("fi", "fl", A.Str("ID"))
                    .Get("rs", "EditMode").Put("ft", "Value", A.Str("edited"));
                ValueTriple(p, "ft");
                p.Get("rs", "EditMode").Call("rs", "Update").Get("rs", "EditMode");
                ErrClear(p, "cn");
                ValueTriple(p, "ft");
                p.Put("ft", "Value", A.Str("moved")).Call("rs", "MoveNext").Get("rs", "EditMode");
                ErrClear(p, "cn");
                p.Call("rs", "MovePrevious").Get("ft", "Value");
                p.Put("ft", "Value", A.Str("cancel")).Call("rs", "CancelUpdate").Get("rs", "EditMode").Get("ft", "Value");
                ErrClear(p, "cn");
                p.Call("rs", "AddNew").Get("rs", "EditMode").Put("fc", "Value", A.Str("n6")).Call("rs", "Update").Get("fi", "Value").Get("rs", "EditMode");
                ErrClear(p, "cn");
                p.Release("ft").Release("fc").Release("fi").Release("fl").Call("rs", "Close").Release("rs");
                ExecRead(p, "cn", "r1", "SELECT ID, Code, T FROM K ORDER BY ID");
                ErrClear(p, "cn");
                p.FinishLite("cn");
            });

        // ADO-BULK ---------------------------------------------------------------------------------------------
        yield return Q("ado-bulk-elements", AdoRefs("ADO-BULK"),
            "Repairs r-str-ado-act/ado-bulk: GetRows stored only array bounds because the capture was not element-wise then, and the rows came from a fixture whose tables had been lost. " +
            "On a client static read-only recordset of SELECT ID, Code, T, M FROM K ORDER BY ID (own copy of r-fix-wire-ado-ado5): GetRows(adGetRowsRest) and EOF; MoveFirst, GetRows(2, adBookmarkFirst, \"T\") and AbsolutePosition; " +
            "MoveFirst, GetString(adClipString, -1, \"|\", \"\\n\", \"<null>\") and EOF; MoveFirst, GetString(adClipString) with defaults and EOF; Connection.Errors read and cleared after each call. Asks the array tag, bounds, each element's tag and value, and the strings.",
            Ado5Fx, p =>
            {
                p.EngineLite();
                AdoOpenMain(p, "cn", readOnly: true);
                AdoRs(p, "rs", "SELECT ID, Code, T, M FROM K ORDER BY ID", "cn", Ado.UseClient, Ado.OpenStatic, Ado.LockReadOnly, Ado.CmdText);
                p.Call("rs", "GetRows", A.I4(Ado.GetRowsRest)).Get("rs", "EOF");
                ErrClear(p, "cn");
                p.Call("rs", "MoveFirst").Call("rs", "GetRows", A.I4(2), A.I4(Ado.BookmarkFirst), A.Str("T")).Get("rs", "AbsolutePosition");
                ErrClear(p, "cn");
                p.Call("rs", "MoveFirst").Call("rs", "GetString", A.I4(Ado.ClipString), A.I4(-1), A.Str("|"), A.Str("\n"), A.Str("<null>")).Get("rs", "EOF");
                ErrClear(p, "cn");
                p.Call("rs", "MoveFirst").Call("rs", "GetString", A.I4(Ado.ClipString)).Get("rs", "EOF");
                ErrClear(p, "cn");
                p.Call("rs", "Close").Release("rs").FinishLite("cn");
            });

        // R-STRING-1 -------------------------------------------------------------------------------------------
        yield return Q("str-memo-fieldsize", StrRefs("R-DAO-1"),
            "Repairs r-str-ado-act/str-memo-chunks: Field.FieldSize was invoked as a method (the call kind) and refused on all snapshot calls, so it is unknown whether FieldSize is a snapshot limit or was a probe-choice limit. FieldSize is now read with a property get. " +
            "On a snapshot of SELECT ID, T, M FROM S ORDER BY ID (read-only copy of r-str-ado-act-str-accdb) with Fields(\"T\") and Fields(\"M\") retained, for rows 1, 2, 3, 5, 8, 9, 10 and 11 (FindFirst by ID, NoMatch read): M.FieldSize (property get, DBEngine.Errors read right after) and M.GetChunk at (0, 16), (7999, 5), (8000, 4), (0, 0) and (1000000, 4); " +
            "then FieldSize (property get) and GetChunk(0, 4) of the Text field T of row 3. Asks the reported sizes, returned tags and byte lengths, and errors.",
            StrFx, p =>
            {
                p.EngineLite().OpenDb().OpenRs("rs", "db", "SELECT ID, T, M FROM S ORDER BY ID", Dao.OpenSnapshot)
                    .GetBind("fl", "rs", "Fields").Item("ft", "fl", A.Str("T")).Item("fm", "fl", A.Str("M"));
                foreach (int id in new[] { 1, 2, 3, 5, 8, 9, 10, 11 })
                {
                    p.Call("rs", "FindFirst", A.Str($"ID = {id}")).Get("rs", "NoMatch").Get("fm", "FieldSize").Errors()
                        .Call("fm", "GetChunk", A.I4(0), A.I4(16)).Call("fm", "GetChunk", A.I4(7999), A.I4(5))
                        .Call("fm", "GetChunk", A.I4(8000), A.I4(4)).Call("fm", "GetChunk", A.I4(0), A.I4(0))
                        .Call("fm", "GetChunk", A.I4(1000000), A.I4(4));
                }
                p.Call("rs", "FindFirst", A.Str("ID = 3")).Get("rs", "NoMatch").Get("ft", "FieldSize").Errors().Call("ft", "GetChunk", A.I4(0), A.I4(4))
                    .Release("ft").Release("fm").Release("fl").Call("rs", "Close").Release("rs").FinishLite("db");
            });

        yield return Q("str-schema-change-isolated", StrRefs("R-DAO-1", "R-CAT-1"),
            "Repairs r-str-ado-act/str-schema-change: DBEngine.Errors was read once and kept the last failure through a later successful ALTER, and the Fields collection was read without a Refresh after the DDL. " +
            "On a read-write copy of r-str-ado-act-str-accdb: a dynaset of SELECT ID, T FROM S with Fields(\"T\") retained, T read; Database.Execute(\"ALTER TABLE S ADD COLUMN X TEXT(10)\", dbFailOnError) with DBEngine.Errors read right after; T read, MoveNext, T read, Requery, T read, Fields.Count of a fresh Fields; recordset closed; " +
            "the same ALTER again with no recordset open, dbFailOnError, DBEngine.Errors read right after; TableDefs refreshed, TableDefs(\"S\").Fields refreshed, its Count and Fields(\"X\") Name read; UPDATE S SET X = Left(T, 10) for IDs 3 to 5 with dbFailOnError and Errors read; " +
            "a fresh snapshot of ID, X, Len(X) for IDs 3 to 5 read. Asks whether the open recordset blocks the change, what the retained Field returns, and what the refreshed collections and copied values hold.",
            StrFx, p =>
            {
                const string alter = "ALTER TABLE S ADD COLUMN X TEXT(10)";
                p.EngineLite().OpenDb(readOnly: false).OpenRs("rs", "db", "SELECT ID, T FROM S ORDER BY ID", Dao.OpenDynaset)
                    .GetBind("fl", "rs", "Fields").Item("ft", "fl", A.Str("T")).Get("ft", "Value")
                    .Call("db", "Execute", A.Str(alter), A.I4(Dao.FailOnError)).Errors()
                    .Get("ft", "Value").Call("rs", "MoveNext").Get("ft", "Value").Call("rs", "Requery").Get("ft", "Value")
                    .GetBind("fl2", "rs", "Fields").Call("fl2", "Refresh").Get("fl2", "Count").Release("fl2")
                    .Release("ft").Release("fl").Call("rs", "Close").Release("rs")
                    .Call("db", "Execute", A.Str(alter), A.I4(Dao.FailOnError)).Errors()
                    .GetBind("tds", "db", "TableDefs").Call("tds", "Refresh").Item("td", "tds", A.Str("S")).GetBind("tdf", "td", "Fields").Call("tdf", "Refresh").Get("tdf", "Count")
                    .Item("tx", "tdf", A.Str("X")).Get("tx", "Name").Release("tx").Release("tdf").Release("td").Release("tds")
                    .Call("db", "Execute", A.Str("UPDATE S SET X = Left(T, 10) WHERE ID IN (3, 4, 5)"), A.I4(Dao.FailOnError)).Errors()
                    .OpenRs("rx", "db", "SELECT ID, X, Len(X) AS LX FROM S WHERE ID IN (3, 4, 5) ORDER BY ID", Dao.OpenSnapshot).Errors()
                    .ReadAll("rx", 8, 8).Call("rx", "Close").Release("rx").FinishLite("db");
            });
    }

    // r-ado-1b repairs -------------------------------------------------------------------------------------------

    private static void ReopenFile(P p, string bind, string file) =>
        p.Create("ADODB.Recordset", bind).Call(bind, "Open", A.Out(file), A.Missing, A.I4(Ado.OpenStatic), A.I4(Ado.LockReadOnly), A.I4(Ado.CmdFile)).Get(bind, "State")
            .Get(bind, "RecordCount").Call(bind, "Close").Release(bind);

    /// <summary>The recordset kinds open when the DDL runs, and whether they are closed first, for fields-refresh-ddl.</summary>
    private static Probe RefreshDdl(string slug, string what, bool keyset, bool client, bool closeFirst)
    {
        const string sel = "SELECT * FROM W ORDER BY ID";
        var sets = new List<(string Bind, int Loc, int Type)>();
        if (keyset)
        {
            sets.Add(("rk", Ado.UseServer, Ado.OpenKeyset));
        }
        if (client)
        {
            sets.Add(("rc", Ado.UseClient, Ado.OpenStatic));
        }
        return Q("fields-refresh-ddl-" + slug, AdoRefs("ADO-SCHEMA", "ADO-RESYNC", "R-CAT-1"),
            "Repairs r-ado-1b/fields-refresh-ddl: ALTER TABLE W failed because the probe's own recordsets held W, so no DDL was applied and the later Fields.Item(\"X\") answers carry no meaning. " +
            $"On a read-write copy of r-ado-1b-main, {what}: each recordset of SELECT * FROM W ORDER BY ID reads Fields.Count, calls Fields.Refresh and reads Count; " +
            "ALTER TABLE W ADD COLUMN X TEXT(10) executes on the Connection with Connection.Errors read and cleared right after; each retained Fields collection reads Count, is Refreshed (Errors after) and reads Count; " +
            (closeFirst
                ? "each recordset is then reopened with the same arguments and reads State, the retained collection's Count, Refresh and Count, and Requery and Count; "
                : "each recordset then calls Requery and reads Count; ") +
            "Fields.Item(\"X\") Name is read; a new recordset of the same SELECT reads Count and Fields.Item(\"X\"). Asks whether the ALTER succeeds, and whether Refresh, Requery, reopening or neither picks up the new column.",
            AdoMainFx, p =>
            {
                p.EngineLite();
                AdoOpenMain(p, "cn", readOnly: false);
                foreach ((string bind, int loc, int type) in sets)
                {
                    AdoRs(p, bind, sel, "cn", loc, type, Ado.LockReadOnly, Ado.CmdText);
                    p.GetBind(bind + "f", bind, "Fields").Get(bind + "f", "Count").Call(bind + "f", "Refresh").Get(bind + "f", "Count");
                    ErrClear(p, "cn");
                }
                if (closeFirst)
                {
                    foreach ((string bind, int _, int _) in sets)
                    {
                        p.Call(bind, "Close").Get(bind, "State");
                    }
                    ErrClear(p, "cn");
                }
                ExecNo(p, "cn", "ALTER TABLE W ADD COLUMN X TEXT(10)");
                ErrClear(p, "cn");
                foreach ((string bind, int _, int type) in sets)
                {
                    string f = bind + "f";
                    p.Get(f, "Count");
                    ErrClear(p, "cn");
                    p.Call(f, "Refresh");
                    ErrClear(p, "cn");
                    p.Get(f, "Count");
                    if (closeFirst)
                    {
                        p.Call(bind, "Open", A.Str(sel), A.Obj("cn"), A.I4(type), A.I4(Ado.LockReadOnly), A.I4(Ado.CmdText)).Get(bind, "State");
                        ErrClear(p, "cn");
                        p.Get(f, "Count").Call(f, "Refresh").Get(f, "Count");
                        ErrClear(p, "cn");
                    }
                    p.Call(bind, "Requery").Get(f, "Count");
                    ErrClear(p, "cn");
                    p.Item(bind + "x", f, A.Str("X")).Get(bind + "x", "Name").Release(bind + "x");
                    ErrClear(p, "cn");
                }
                AdoRs(p, "rn", sel, "cn", Ado.UseServer, Ado.OpenKeyset, Ado.LockReadOnly, Ado.CmdText);
                p.GetBind("rnf", "rn", "Fields").Get("rnf", "Count").Item("rnx", "rnf", A.Str("X")).Get("rnx", "Name").Release("rnx").Release("rnf");
                ErrClear(p, "cn");
                foreach ((string bind, int _, int _) in sets)
                {
                    p.Release(bind + "f").Call(bind, "Close").Release(bind);
                }
                p.Call("rn", "Close").Release("rn").FinishLite("cn");
            });
    }

    /// <summary>Client static batch-optimistic recordset of W with S of rows 1 to 3 staged ("g1".."g3"); the last row is posted with Update or left pending.</summary>
    private static void ScopeStage(P p, bool postLast)
    {
        AdoOpenMain(p, "cn", readOnly: false);
        AdoRs(p, "rs", SelW, "cn", Ado.UseClient, Ado.OpenStatic, Ado.LockBatchOptimistic, Ado.CmdText);
        p.GetBind("fl", "rs", "Fields").Item("fs", "fl", A.Str("S"))
            .Put("fs", "Value", A.Str("g1")).Call("rs", "Update").Call("rs", "MoveNext")
            .Put("fs", "Value", A.Str("g2")).Call("rs", "Update").Call("rs", "MoveNext")
            .Put("fs", "Value", A.Str("g3"));
        if (postLast)
        {
            p.Call("rs", "Update");
        }
        p.Get("rs", "EditMode");
        ErrClear(p, "cn");
    }

    private static void ScopeEnd(P p) =>
        p.Release("fs").Release("fl").Call("rs", "Close").Release("rs").FinishLite("cn");

    private static IEnumerable<Probe> Ado1BProbes()
    {
        // ADO-PERSIST ------------------------------------------------------------------------------------------
        yield return Q("persist-save-two-parameter", AdoRefs("ADO-PERSIST"),
            "Repairs r-ado-1b/persist-overwrite-flags: the flagged calls Save(path, 0, 1), (path, 0, 2) and (path, 1, 2) passed three arguments to Save(Destination, PersistFormat) and returned 0x8002000E (bad parameter count), so the overwrite question is unanswered. Only the one- and two-argument forms are used. " +
            "On a client static read-only recordset of SELECT ID, Code, L, S FROM T ORDER BY ID (copy of r-ado-1b-main), each step with Connection.Errors read and cleared right after and the recordset State read: Save(o.dat) with the format omitted, reopened with adCmdFile (State, RecordCount); " +
            "Save(o.dat) again to the same path; Save(o.dat, adPersistADTG) to the same path; Save(o.dat, adPersistXML) to the same path, reopened; Save(new-adtg.dat, adPersistADTG) to a new path, reopened; Save(new-xml.dat, adPersistXML) to a new path, reopened; " +
            "Save(f99.dat, 99); Save with Destination omitted; Close then Save(closed.dat, adPersistADTG). The files are kept. Asks the default format, whether a second Save to an existing path is refused, and which forms are refused.",
            AdoMainFx, p =>
            {
                p.EngineLite();
                AdoOpenMain(p, "cn", readOnly: true);
                AdoRs(p, "rs", SelS, "cn", Ado.UseClient, Ado.OpenStatic, Ado.LockReadOnly, Ado.CmdText);
                p.Call("rs", "Save", A.Out("o.dat")).Get("rs", "State");
                ErrClear(p, "cn");
                ReopenFile(p, "o1", "o.dat");
                ErrClear(p, "cn");
                p.Files("after-first", keep: true).Call("rs", "Save", A.Out("o.dat")).Get("rs", "State");
                ErrClear(p, "cn");
                p.Call("rs", "Save", A.Out("o.dat"), A.I4(Ado.PersistAdtg)).Get("rs", "State");
                ErrClear(p, "cn");
                p.Call("rs", "Save", A.Out("o.dat"), A.I4(Ado.PersistXml)).Get("rs", "State");
                ErrClear(p, "cn");
                ReopenFile(p, "o2", "o.dat");
                ErrClear(p, "cn");
                p.Call("rs", "Save", A.Out("new-adtg.dat"), A.I4(Ado.PersistAdtg)).Get("rs", "State");
                ErrClear(p, "cn");
                ReopenFile(p, "o3", "new-adtg.dat");
                ErrClear(p, "cn");
                p.Call("rs", "Save", A.Out("new-xml.dat"), A.I4(Ado.PersistXml)).Get("rs", "State");
                ErrClear(p, "cn");
                ReopenFile(p, "o4", "new-xml.dat");
                ErrClear(p, "cn");
                p.Call("rs", "Save", A.Out("f99.dat"), A.I4(99)).Get("rs", "State");
                ErrClear(p, "cn");
                p.Call("rs", "Save").Get("rs", "State").Files("after-all", keep: true);
                ErrClear(p, "cn");
                p.Call("rs", "Close").Call("rs", "Save", A.Out("closed.dat"), A.I4(Ado.PersistAdtg));
                ErrClear(p, "cn");
                p.Release("rs").FinishLite("cn");
            });

        // ADO-SCHEMA -------------------------------------------------------------------------------------------
        yield return RefreshDdl("closed", "with each recordset closed before the DDL", keyset: true, client: true, closeFirst: true);
        yield return RefreshDdl("keyset-only", "with only a server keyset read-only recordset open when the DDL runs", keyset: true, client: false, closeFirst: false);
        yield return RefreshDdl("client-only", "with only a client static read-only recordset open when the DDL runs", keyset: false, client: true, closeFirst: false);

        // ADO-PARAM, ADO-BUFFER (Decimal) ----------------------------------------------------------------------
        (int? Prec, int? Scale)[] shapes = [(null, null), (18, 3), (5, 2), (28, 10), (1, 0)];
        Arg[] decValues = [A.Str("3.125"), A.R8(3.125), A.Cy(3.125m), A.Str("3.1251"), A.I4(3)];
        yield return Q("param-decimal-scale-fresh", AdoRefs("ADO-PARAM", "R-FMT-1"),
            "Repairs r-ado-1b/param-decimal-scale: the Decimal column N had been made by DAO CreateField (Precision 38, Scale 0) and its stored values could not be decoded, so all 25 combinations returned an empty recordset. " +
            "On a read-only ACE connection (own copy of r-fix-wire-ado-ado5, where K.N is DECIMAL(18,4) holding 3.125 in row a1, Null in a2, 0 in b3 and -0.0001 in b4, written as SQL literals), for each (Precision, NumericScale) of (default, default), (18, 3), (5, 2), (28, 10), (1, 0) " +
            "and each value String \"3.125\", Double 3.125, Currency 3.125, String \"3.1251\", Long 3: a fresh Command on \"SELECT ID FROM K WHERE N = ?\" with a fresh appended Decimal input parameter; the parameter's metadata read before and after the Value put, the Value read back, Execute, all rows, Connection.Errors read and cleared right after. " +
            "Asks how precision and scale change conversion and matching against the Decimal column.",
            Ado5Fx, p =>
            {
                p.EngineLite();
                AdoOpenMain(p, "cn", readOnly: true);
                for (int s = 0; s < shapes.Length; s++)
                {
                    for (int v = 0; v < decValues.Length; v++)
                    {
                        string c = $"d{s}_{v}";
                        NewCmd(p, c, "cn", "SELECT ID FROM K WHERE N = ?");
                        p.CallBind(c + "x", c, "CreateParameter", A.Str("x"), A.I4(Ado.Decimal), A.I4(Ado.ParamInput), A.Missing, A.Str("3.125"));
                        if (shapes[s].Prec is int prec)
                        {
                            p.Put(c + "x", "Precision", A.I4(prec));
                        }
                        if (shapes[s].Scale is int scale)
                        {
                            p.Put(c + "x", "NumericScale", A.I4(scale));
                        }
                        ParamMeta(p, c + "x");
                        p.GetBind(c + "s", c, "Parameters").Call(c + "s", "Append", A.Obj(c + "x"))
                            .Put(c + "x", "Value", decValues[v]).Get(c + "x", "Value");
                        ParamMeta(p, c + "x");
                        p.CallBind(c + "r", c, "Execute");
                        ErrClear(p, "cn");
                        AdoReadAll(p, c + "r", 8, 1);
                        p.Release(c + "r").Release(c + "s").Release(c + "x").Release(c);
                    }
                }
                p.FinishLite("cn");
            });

        yield return Q("fields-meta-decimal-expressions", AdoRefs("ADO-BUFFER", "ADO-SCHEMA", "R-FMT-1"),
            "Repairs r-ado-1b/fields-meta-expressions: CDec(L) is refused (\"Wrong number of arguments\"), so the metadata of Decimal-valued expressions (x1, x2) is missing; CDec cannot be repaired, so Decimal-valued expressions over the DECIMAL(18,4) column N are asked instead. " +
            "On a read-only ACE connection (own copy of r-fix-wire-ado-ado5), each SELECT alone, as a server forward-only and as a client static read-only recordset: N, N + 1, N * 1.5, N / 2, -N, N + N from K WHERE ID = 1, and Sum(N), Avg(N), Min(N) with Max(N), Count(N) from K; " +
            "State and, for every field, Name, Type, DefinedSize, Precision, NumericScale and Attributes, the first row's Value and ActualSize, Connection.Errors read and cleared right after each open. Asks the type, precision, scale and value ADO publishes for Decimal expressions and aggregates.",
            Ado5Fx, p =>
            {
                p.EngineLite();
                AdoOpenMain(p, "cn", readOnly: true);
                string[] sqls =
                [
                    "SELECT N AS x0 FROM K WHERE ID = 1", "SELECT N + 1 AS x1 FROM K WHERE ID = 1", "SELECT N * 1.5 AS x2 FROM K WHERE ID = 1", "SELECT N / 2 AS x3 FROM K WHERE ID = 1",
                    "SELECT -N AS x4 FROM K WHERE ID = 1", "SELECT N + N AS x5 FROM K WHERE ID = 1", "SELECT Sum(N) AS a1 FROM K", "SELECT Avg(N) AS a2 FROM K",
                    "SELECT Min(N) AS a3, Max(N) AS a4 FROM K", "SELECT Count(N) AS a5 FROM K",
                ];
                for (int i = 0; i < sqls.Length; i++)
                {
                    foreach ((string tag, int loc, int type) in new[] { ("s", Ado.UseServer, Ado.OpenForwardOnly), ("c", Ado.UseClient, Ado.OpenStatic) })
                    {
                        string r = $"x{i}{tag}";
                        AdoRs(p, r, sqls[i], "cn", loc, type, Ado.LockReadOnly, Ado.CmdText);
                        ErrClear(p, "cn");
                        FieldMeta(p, r);
                        AdoReadAll(p, r, 1, 20, actualSize: true);
                        p.Call(r, "Close").Release(r);
                    }
                }
                p.FinishLite("cn");
            });

        // ADO-BATCH-SCOPE --------------------------------------------------------------------------------------
        yield return Q("batch-scope-group-pending-constant", AdoRefs("ADO-BATCH-SCOPE", "ADO-VIEW"),
            "Repairs r-ado-1b/batch-scope-group: both adAffectGroup attempts failed while a row 3 edit was pending and with a string Filter, so the two causes were not separated. This probe has neither: no edit is pending and the Filter is a constant. " +
            "On a client static batch-optimistic recordset of SELECT ID, Code, V, V2, S FROM W ORDER BY ID (read-write copy of r-ado-1b-main): S of rows 1, 2 and 3 set to \"g1\", \"g2\", \"g3\", each posted with Update (EditMode read), Errors read and cleared; Filter = adFilterPendingRecords (Filter, RecordCount); " +
            "UpdateBatch(adAffectGroup) with Errors read and cleared right after; the stored rows through Execute; Filter none and every row's Status and S; Filter = adFilterPendingRecords again (RecordCount) and UpdateBatch(adAffectGroup) on that remaining group, Errors; Filter = adFilterAffectedRecords (RecordCount). Asks which rows the group submits and how an empty group answers.",
            AdoMainFx, p =>
            {
                p.EngineLite();
                ScopeStage(p, postLast: true);
                p.Put("rs", "Filter", A.I4(Ado.FilterPending)).Get("rs", "Filter").Get("rs", "RecordCount");
                ErrClear(p, "cn");
                p.Call("rs", "UpdateBatch", A.I4(Ado.AffectGroup));
                ErrClear(p, "cn");
                ExecRead(p, "cn", "s1", "SELECT ID, Code, V, V2, S FROM W ORDER BY ID");
                p.Put("rs", "Filter", A.I4(Ado.FilterNone)).Call("rs", "MoveFirst");
                BatchValues(p, "rs", "S");
                p.Put("rs", "Filter", A.I4(Ado.FilterPending)).Get("rs", "RecordCount").Call("rs", "UpdateBatch", A.I4(Ado.AffectGroup));
                ErrClear(p, "cn");
                p.Put("rs", "Filter", A.I4(Ado.FilterAffected)).Get("rs", "RecordCount");
                ErrClear(p, "cn");
                ScopeEnd(p);
            });

        yield return Q("batch-scope-group-string-filter", AdoRefs("ADO-BATCH-SCOPE", "ADO-VIEW"),
            "Repairs r-ado-1b/batch-scope-group: both adAffectGroup attempts failed while a row 3 edit was pending and with a string Filter, so the two causes were not separated. This probe has no pending edit and varies only the Filter kind. " +
            "On a client static batch-optimistic recordset of SELECT ID, Code, V, V2, S FROM W ORDER BY ID (read-write copy of r-ado-1b-main): S of rows 1, 2 and 3 set to \"g1\", \"g2\", \"g3\", each posted with Update (EditMode read), Errors read and cleared; Filter = \"ID <= 2\" (Filter, RecordCount); " +
            "UpdateBatch(adAffectGroup) with Errors read and cleared right after; the stored rows through Execute; Filter none; Filter = \"ID > 99\" (RecordCount) and UpdateBatch(adAffectGroup) with Errors read right after; Filter none and UpdateBatch(adAffectAll) with Errors; the stored rows through Execute. " +
            "Asks whether adAffectGroup accepts a string Filter, which rows it submits, and how an empty string group answers.",
            AdoMainFx, p =>
            {
                p.EngineLite();
                ScopeStage(p, postLast: true);
                p.Put("rs", "Filter", A.Str("ID <= 2")).Get("rs", "Filter").Get("rs", "RecordCount");
                ErrClear(p, "cn");
                p.Call("rs", "UpdateBatch", A.I4(Ado.AffectGroup));
                ErrClear(p, "cn");
                ExecRead(p, "cn", "s1", "SELECT ID, Code, V, V2, S FROM W ORDER BY ID");
                p.Put("rs", "Filter", A.I4(Ado.FilterNone));
                p.Put("rs", "Filter", A.Str("ID > 99")).Get("rs", "RecordCount").Call("rs", "UpdateBatch", A.I4(Ado.AffectGroup));
                ErrClear(p, "cn");
                p.Put("rs", "Filter", A.I4(Ado.FilterNone)).Call("rs", "UpdateBatch", A.I4(Ado.AffectAll));
                ErrClear(p, "cn");
                ExecRead(p, "cn", "s2", "SELECT ID, Code, V, V2, S FROM W ORDER BY ID");
                ScopeEnd(p);
            });

        yield return Q("batch-scope-group-unposted-edit", AdoRefs("ADO-BATCH-SCOPE", "ADO-VIEW"),
            "Repairs r-ado-1b/batch-scope-group: both adAffectGroup attempts failed while a row 3 edit was pending and with a string Filter, so the two causes were not separated. This probe has the pending edit and no string Filter. " +
            "On a client static batch-optimistic recordset of SELECT ID, Code, V, V2, S FROM W ORDER BY ID (read-write copy of r-ado-1b-main): S of rows 1 and 2 set to \"g1\" and \"g2\" and posted with Update, S of row 3 set to \"g3\" with the edit left pending (EditMode read), Errors read and cleared; " +
            "UpdateBatch(adAffectGroup) with no Filter (Errors right after); Filter = adFilterPendingRecords (EditMode, RecordCount, Errors) and UpdateBatch(adAffectGroup) (Errors right after); then Update on row 3, EditMode, and UpdateBatch(adAffectGroup) once more (Errors); the stored rows through Execute. " +
            "Asks whether an unposted current edit blocks or joins a group submission.",
            AdoMainFx, p =>
            {
                p.EngineLite();
                ScopeStage(p, postLast: false);
                p.Call("rs", "UpdateBatch", A.I4(Ado.AffectGroup));
                ErrClear(p, "cn");
                p.Put("rs", "Filter", A.I4(Ado.FilterPending)).Get("rs", "EditMode").Get("rs", "RecordCount");
                ErrClear(p, "cn");
                p.Call("rs", "UpdateBatch", A.I4(Ado.AffectGroup));
                ErrClear(p, "cn");
                p.Call("rs", "Update").Get("rs", "EditMode");
                ErrClear(p, "cn");
                p.Call("rs", "UpdateBatch", A.I4(Ado.AffectGroup));
                ErrClear(p, "cn");
                ExecRead(p, "cn", "s1", "SELECT ID, Code, V, V2, S FROM W ORDER BY ID");
                ScopeEnd(p);
            });

        // ADO-BATCH --------------------------------------------------------------------------------------------
        yield return Q("batch-conflict-delete-readback", AdoRefs("ADO-BATCH", "ADO-VALUES"),
            "Repairs r-ado-1b/batch-conflict-delete: the final read-back Execute failed with \"Record is deleted\", so the stored rows after the conflict were never seen, and its cause (the reading connection or the data) was not separated. " +
            "A client static batch-optimistic recordset of SELECT ID, Code, V, V2, S FROM W ORDER BY ID is opened on a read-write copy of r-ado-1b-main; a second Connection runs \"DELETE FROM W WHERE ID = 2\" (Errors cleared); S of rows 1, 2 and 3 is set to \"c1\", \"c2\", \"c3\"; " +
            "UpdateBatch(adAffectAll) and the Errors; each row's Status, ID, S, OriginalValue and UnderlyingValue; Filter = adFilterConflictingRecords (RecordCount, rows) and Filter none; Resync(adAffectAll, adResyncUnderlyingValues), the rows again, a second UpdateBatch (Errors) and the rows. " +
            "The stored rows (ID <= 4) are then read three ways, each with its own errors read and cleared: Execute on the first Connection, Execute on a third fresh Connection, and a DAO read-only snapshot. Asks which staged changes reach the file and what each of the three reads returns.",
            AdoMainFx, p =>
            {
                p.EngineLite();
                AdoOpenMain(p, "cn", readOnly: false);
                AdoOpenMain(p, "cn2", readOnly: false);
                AdoRs(p, "rs", SelW, "cn", Ado.UseClient, Ado.OpenStatic, Ado.LockBatchOptimistic, Ado.CmdText);
                ExecNo(p, "cn2", "DELETE FROM W WHERE ID = 2");
                ErrClear(p, "cn2");
                p.GetBind("fl", "rs", "Fields").Item("fs", "fl", A.Str("S")).Put("fs", "Value", A.Str("c1")).Call("rs", "MoveNext").Put("fs", "Value", A.Str("c2"))
                    .Call("rs", "MoveNext").Put("fs", "Value", A.Str("c3")).Call("rs", "UpdateBatch", A.I4(Ado.AffectAll));
                ErrClear(p, "cn");
                p.Call("rs", "MoveFirst");
                BatchValues(p, "rs", "S");
                p.Put("rs", "Filter", A.I4(Ado.FilterConflicting)).Get("rs", "RecordCount");
                BatchValues(p, "rs", "S");
                p.Put("rs", "Filter", A.I4(Ado.FilterNone)).Call("rs", "Resync", A.I4(Ado.AffectAll), A.I4(Ado.ResyncUnderlying)).Call("rs", "MoveFirst");
                ErrClear(p, "cn");
                BatchValues(p, "rs", "S");
                p.Call("rs", "UpdateBatch", A.I4(Ado.AffectAll));
                ErrClear(p, "cn");
                p.Call("rs", "MoveFirst");
                BatchValues(p, "rs", "S");
                p.Release("fs").Release("fl").Call("rs", "Close").Release("rs");
                const string stored = "SELECT ID, Code, S FROM W WHERE ID <= 4 ORDER BY ID";
                ExecRead(p, "cn", "st1", stored);
                ErrClear(p, "cn");
                AdoOpenMain(p, "cn3", readOnly: true);
                ExecRead(p, "cn3", "st3", stored);
                ErrClear(p, "cn3");
                p.OpenDb().OpenRs("dr", "db", stored, Dao.OpenSnapshot).Errors().ReadAll("dr", 8, 3).Call("dr", "Close").Release("dr").Call("db", "Close").Release("db");
                p.FinishLite("cn3", "cn2", "cn");
            });

        // ADO-BOOK ---------------------------------------------------------------------------------------------
        yield return Q("book-position-readback", AdoRefs("ADO-BOOK", "ADO-CURSOR"),
            "Repairs r-ado-1b/book-raw-forms: the probe read Bookmark at MoveLast but not the current row, AbsolutePosition or RecordCount, so a server static Bookmark of 3 with RecordCount 7 cannot be explained. " +
            "On SELECT ID, S FROM T ORDER BY ID (read-only copy of r-ado-1b-main), for a server forward-only, a server keyset, a server static and a client static read-only recordset: Supports(adBookmark); then at row 1, after MoveNext, after MoveLast, after MoveNext past the end (EOF read) " +
            "and after MoveFirst then MovePrevious (BOF read), each position reads the current ID, AbsolutePosition, RecordCount and the raw Bookmark, with Connection.Errors read and cleared right after each position. Asks the Bookmark representation and the row, position and count it goes with.",
            AdoMainFx, p =>
            {
                p.EngineLite();
                AdoOpenMain(p, "cn", readOnly: true);
                (string Bind, int Loc, int Type)[] sets =
                [
                    ("b1", Ado.UseServer, Ado.OpenForwardOnly), ("b2", Ado.UseServer, Ado.OpenKeyset), ("b3", Ado.UseServer, Ado.OpenStatic), ("b4", Ado.UseClient, Ado.OpenStatic),
                ];
                void Position(string bind)
                {
                    Cur(p, bind, "ID");
                    p.Get(bind, "AbsolutePosition").Get(bind, "RecordCount").Get(bind, "Bookmark");
                    ErrClear(p, "cn");
                }
                foreach ((string bind, int loc, int type) in sets)
                {
                    AdoRs(p, bind, "SELECT ID, S FROM T ORDER BY ID", "cn", loc, type, Ado.LockReadOnly, Ado.CmdText);
                    p.Call(bind, "Supports", A.I4(Ado.SupBookmark));
                    ErrClear(p, "cn");
                    Position(bind);
                    p.Call(bind, "MoveNext");
                    Position(bind);
                    p.Call(bind, "MoveLast");
                    Position(bind);
                    p.Call(bind, "MoveNext").Get(bind, "EOF");
                    Position(bind);
                    p.Call(bind, "MoveFirst").Call(bind, "MovePrevious").Get(bind, "BOF");
                    Position(bind);
                    p.Call(bind, "Close").Release(bind);
                }
                p.FinishLite("cn");
            });

        // ADO-TXN ----------------------------------------------------------------------------------------------
        yield return Q("txn-client-cache-rollback-distinct-rows", AdoRefs("ADO-TXN", "ADO-CURSOR", "ADO-RESYNC"),
            "Repairs r-ado-1b/txn-client-cache-rollback: the keyset and the client optimistic recordset both wrote ID 2, so which route wrote the surviving \"txr-2\" was not traced. Each route now writes its own row. " +
            "On a read-write copy of r-ado-1b-main: a server keyset optimistic, a server static read-only, a client static optimistic and a client static batch-optimistic recordset of SELECT ID, S FROM T ORDER BY ID are opened; BeginTrans; the Connection UPDATEs S of ID 1 to \"txc-cn\"; " +
            "the keyset recordset edits and Updates S of ID 2 to \"txr-keyset\"; the client optimistic one edits and Updates S of ID 3 to \"txr-client\"; the batch one edits S of ID 4 to \"txr-batch\" and calls UpdateBatch; Connection.Errors read and cleared. " +
            "Each recordset reads ID and S of every row and RecordCount; RollbackTrans; each reads the same again without refresh, then after Resync(adAffectAll, adResyncAllValues), then after Requery; the stored rows through Execute and through a DAO read-only snapshot. Asks what each cursor's cache shows after the rollback, which route's change survives and which refresh restores the stored values.",
            AdoMainFx, p =>
            {
                p.EngineLite();
                AdoOpenMain(p, "cn", readOnly: false);
                (string Bind, int Loc, int Type, int Lock)[] sets =
                [
                    ("t1", Ado.UseServer, Ado.OpenKeyset, Ado.LockOptimistic), ("t2", Ado.UseServer, Ado.OpenStatic, Ado.LockReadOnly),
                    ("t3", Ado.UseClient, Ado.OpenStatic, Ado.LockOptimistic), ("t4", Ado.UseClient, Ado.OpenStatic, Ado.LockBatchOptimistic),
                ];
                foreach ((string bind, int loc, int type, int lck) in sets)
                {
                    AdoRs(p, bind, "SELECT ID, S FROM T ORDER BY ID", "cn", loc, type, lck, Ado.CmdText);
                }
                p.Call("cn", "BeginTrans");
                ExecNo(p, "cn", "UPDATE T SET S = 'txc-cn' WHERE ID = 1");
                foreach ((string bind, int row, string text) in new[] { ("t1", 1, "txr-keyset"), ("t3", 2, "txr-client"), ("t4", 3, "txr-batch") })
                {
                    p.GetBind(bind + "f", bind, "Fields").Item(bind + "s", bind + "f", A.Str("S")).Call(bind, "MoveFirst").Call(bind, "Move", A.I4(row))
                        .Put(bind + "s", "Value", A.Str(text)).Call(bind, bind == "t4" ? "UpdateBatch" : "Update").Release(bind + "s").Release(bind + "f");
                    ErrClear(p, "cn");
                }
                foreach ((string bind, int _, int _, int _) in sets)
                {
                    p.Call(bind, "MoveFirst");
                    ReadCols(p, bind, 16, "ID", "S");
                    p.Get(bind, "RecordCount");
                }
                p.Call("cn", "RollbackTrans");
                ErrClear(p, "cn");
                foreach ((string bind, int _, int _, int _) in sets)
                {
                    p.Call(bind, "MoveFirst");
                    ReadCols(p, bind, 16, "ID", "S");
                    p.Get(bind, "RecordCount");
                }
                foreach ((string bind, int _, int _, int _) in sets)
                {
                    p.Call(bind, "Resync", A.I4(Ado.AffectAll), A.I4(Ado.ResyncAll)).Call(bind, "MoveFirst");
                    ErrClear(p, "cn");
                    ReadCols(p, bind, 16, "ID", "S");
                }
                foreach ((string bind, int _, int _, int _) in sets)
                {
                    p.Call(bind, "Requery");
                    ErrClear(p, "cn");
                    ReadCols(p, bind, 16, "ID", "S");
                    p.Call(bind, "Close").Release(bind);
                }
                ExecRead(p, "cn", "st", "SELECT ID, S FROM T WHERE ID <= 4 ORDER BY ID");
                ErrClear(p, "cn");
                p.OpenDb().OpenRs("dr", "db", "SELECT ID, S FROM T WHERE ID <= 4 ORDER BY ID", Dao.OpenSnapshot).Errors().ReadAll("dr", 8, 2).Call("dr", "Close").Release("dr").Call("db", "Close").Release("db");
                p.FinishLite("cn");
            });
    }
}
