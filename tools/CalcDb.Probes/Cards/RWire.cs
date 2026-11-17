namespace CalcDb.Probes.Cards;

/// <summary>
/// R-ORACLE-1 OR-WIRE and R-STRING-1 (value wire forms): every shared scalar tag stored into and read back from
/// ACE through three routes (DAO field Value, QueryDef parameters, SQL literals), GetRows shape and element tags,
/// optional-argument omission (Missing, Empty, Null) on DAO methods, object identity of repeated Fields and
/// Properties reads, expression results (tags, Decimal, Error, clock, random) and a few ADO routes. Probes only
/// ask; every Arg carries its VARIANT tag, and a failed call is an observation. Fixtures are created through
/// DAO in ACCDB: blank (empty W plus a one-row table One), dao, sql and param (the same cell table, one route each).
/// Not covered here: R-FMT-1 (basic scalar families) and r-str-ado-act (embedded U+0000 rows, long Memo sizing,
/// ADO string reads), which this card extends rather than repeats.
/// </summary>
public sealed class RWire : ICard
{
    private const string Card = "r-wire";
    private const string Ace = "Microsoft.ACE.OLEDB.12.0";
    private const string BlankFx = Card + "-blank";
    private const string DaoFx = Card + "-dao";
    private const string SqlFx = Card + "-sql";
    private const string ParamFx = Card + "-param";

    private static readonly string[] Wire = ["R-ORACLE-1", "OR-WIRE", "R-STRING-1", "D-STRING-1"];

    private static string[] R(params string[] more) => [.. Wire, .. more];

    /// <summary>A column of table W: DDL type (empty when added through CreateField), PARAMETERS type, ID base and probe slug.</summary>
    private sealed record Col(string Name, string Slug, string Ddl, string ParamType, int Base);

    private static readonly Col[] Cols =
    [
        new("FByte", "byte", "BYTE", "Byte", 100),
        new("FInt", "integer", "SHORT", "Short", 200),
        new("FLong", "long", "LONG", "Long", 300),
        new("FSingle", "single", "SINGLE", "IEEESingle", 400),
        new("FDouble", "double", "DOUBLE", "IEEEDouble", 500),
        new("FCurr", "currency", "CURRENCY", "Currency", 600),
        new("FDate", "date", "DATETIME", "DateTime", 700),
        new("FText", "text", "TEXT(255)", "Text(255)", 800),
        new("FMemo", "memo", "MEMO", "LongText", 900),
        new("FBin", "binary", "BINARY(255)", "Binary", 1000),
        new("FOle", "ole", "LONGBINARY", "LongBinary", 1100),
        new("FBool", "boolean", "YESNO", "Bit", 1200),
        new("FDec", "decimal", "", "Decimal", 1300),
    ];

    /// <summary>One value for one column: row ID, the DAO/parameter Arg and, where expressible, the SQL literal.</summary>
    private sealed record Cell(int Id, Col Col, string Label, Arg Value, string? Sql);

    private static readonly Cell[] Cells = BuildCells();

    private static Arg F4(uint bits) => new R4Arg(bits.ToString("x8"));

    private static Arg Dt(ulong bits) => new DateArg(bits.ToString("x16"));

    private static byte[] Ramp(int count, int step) => [.. Enumerable.Range(0, count).Select(i => (byte)(i * step))];

    private static Cell[] BuildCells()
    {
        var list = new List<Cell>();
        var next = Cols.ToDictionary(c => c.Name, c => c.Base);
        void Add(string col, string label, Arg value, string? sql = null)
        {
            list.Add(new Cell(next[col]++, Cols.Single(c => c.Name == col), label, value, sql));
        }

        string x255 = new('x', 255), x256 = new('x', 256), y300 = new('y', 300);

        Add("FByte", "ui1-0", A.UI1(0), "0");
        Add("FByte", "ui1-255", A.UI1(255), "255");
        Add("FByte", "i2-256", A.I2(256), "256");
        Add("FByte", "i2-minus1", A.I2(-1), "-1");
        Add("FByte", "r8-1.5", A.R8(1.5), "1.5");
        Add("FByte", "r8-2.5", A.R8(2.5), "2.5");

        Add("FInt", "i2-min", A.I2(short.MinValue), "-32768");
        Add("FInt", "i2-max", A.I2(short.MaxValue), "32767");
        Add("FInt", "i4-32768", A.I4(32768), "32768");
        Add("FInt", "i4-minus32769", A.I4(-32769), "-32769");
        Add("FInt", "r8-1.5", A.R8(1.5), "1.5");
        Add("FInt", "r8-2.5", A.R8(2.5), "2.5");

        Add("FLong", "i4-min", A.I4(int.MinValue), "-2147483648");
        Add("FLong", "i4-max", A.I4(int.MaxValue), "2147483647");
        Add("FLong", "r8-2147483648", A.R8(2147483648.0), "2147483648");
        Add("FLong", "r8-minus2147483649", A.R8(-2147483649.0), "-2147483649");
        Add("FLong", "r8-0.5", A.R8(0.5), "0.5");
        Add("FLong", "r8-1.5", A.R8(1.5), "1.5");
        Add("FLong", "r8-2.5", A.R8(2.5), "2.5");

        Add("FSingle", "plus-zero", F4(0x00000000), "0");
        Add("FSingle", "minus-zero", F4(0x80000000), "-0.0");
        Add("FSingle", "min-subnormal", F4(0x00000001), "1.401298464324817E-45");
        Add("FSingle", "max-subnormal", F4(0x007fffff), "1.1754942106924411E-38");
        Add("FSingle", "min-normal", F4(0x00800000), "1.1754943508222875E-38");
        Add("FSingle", "max-finite", F4(0x7f7fffff), "3.4028234663852886E+38");
        Add("FSingle", "plus-inf", F4(0x7f800000));
        Add("FSingle", "minus-inf", F4(0xff800000));
        Add("FSingle", "qnan", F4(0x7fc00000));
        Add("FSingle", "qnan-payload", F4(0x7fc12345));
        Add("FSingle", "snan", F4(0x7f800001));
        Add("FSingle", "negative-qnan", F4(0xffc00000));
        Add("FSingle", "r8-1e39", A.R8(1e39), "1E39");
        Add("FSingle", "r8-1e-50", A.R8(1e-50), "1E-50");
        Add("FSingle", "r8-0.1", A.R8(0.1), "0.1");

        Add("FDouble", "plus-zero", A.R8Bits(0x0000000000000000UL), "0");
        Add("FDouble", "minus-zero", A.R8Bits(0x8000000000000000UL), "-0.0");
        Add("FDouble", "min-subnormal", A.R8Bits(0x0000000000000001UL), "4.94065645841247E-324");
        Add("FDouble", "max-subnormal", A.R8Bits(0x000fffffffffffffUL), "2.2250738585072009E-308");
        Add("FDouble", "min-normal", A.R8Bits(0x0010000000000000UL), "2.2250738585072014E-308");
        Add("FDouble", "max-finite", A.R8Bits(0x7fefffffffffffffUL), "1.7976931348623157E+308");
        Add("FDouble", "plus-inf", A.R8Bits(0x7ff0000000000000UL));
        Add("FDouble", "minus-inf", A.R8Bits(0xfff0000000000000UL));
        Add("FDouble", "qnan", A.R8Bits(0x7ff8000000000000UL));
        Add("FDouble", "qnan-payload", A.R8Bits(0x7ff8000000000123UL));
        Add("FDouble", "snan", A.R8Bits(0x7ff0000000000001UL));
        Add("FDouble", "negative-qnan", A.R8Bits(0xfff8000000000000UL));
        Add("FDouble", "one-plus-ulp", A.R8Bits(0x3ff0000000000001UL), "1.0000000000000002");
        Add("FDouble", "r4-0.1-widened", A.R4(0.1f), "0.100000001490116");
        Add("FDouble", "r8-0.1", A.R8(0.1), "0.1");

        Add("FCurr", "cy-min", A.Cy(-922337203685477.5808m), "-922337203685477.5808");
        Add("FCurr", "cy-max", A.Cy(922337203685477.5807m), "922337203685477.5807");
        Add("FCurr", "cy-0.0001", A.Cy(0.0001m), "0.0001");
        Add("FCurr", "cy-minus-0.0001", A.Cy(-0.0001m), "-0.0001");
        Add("FCurr", "r8-0.00005", A.R8(0.00005), "0.00005");
        Add("FCurr", "r8-0.00015", A.R8(0.00015), "0.00015");
        Add("FCurr", "r8-1e15", A.R8(1e15), "1E15");
        Add("FCurr", "bstr-5-decimals", A.Str("1.23456"), "1.23456");

        Add("FDate", "oa-0", A.Date(0.0), "#12/30/1899#");
        Add("FDate", "oa-min", A.Date(-657434.0), "#1/1/0100#");
        Add("FDate", "oa-max", A.Date(2958465.0 + (86399.0 / 86400.0)), "#12/31/9999 23:59:59#");
        Add("FDate", "below-min", A.Date(-657435.0));
        Add("FDate", "above-max", A.Date(2958466.0));
        Add("FDate", "negative-1.5", A.Date(-1.5));
        Add("FDate", "negative-0.25", A.Date(-0.25));
        Add("FDate", "positive-1.5", A.Date(1.5), "#12/31/1899 12:00:00#");
        Add("FDate", "one-millisecond", A.Date(1.0 + (0.001 / 86400.0)));
        Add("FDate", "half-second", A.Date(1.0 + (0.5 / 86400.0)));
        Add("FDate", "nan", Dt(0x7ff8000000000000UL));
        Add("FDate", "plus-inf", Dt(0x7ff0000000000000UL));
        Add("FDate", "r8-tag-1.5", A.R8(1.5), "1.5");
        Add("FDate", "bstr-iso", A.Str("2024-01-02 03:04:05"), "#2024-01-02 03:04:05#");

        Add("FText", "empty", A.Str(""), "''");
        Add("FText", "ascii", A.Str("alpha"), "'alpha'");
        Add("FText", "bmp", A.Str("B\u00e4\u20ac"), "'B\u00e4\u20ac'");
        Add("FText", "nul-middle", A.Str("a\0b"), "'a' & Chr(0) & 'b'");
        Add("FText", "nul-only", A.Str("\0"), "Chr(0)");
        Add("FText", "astral", A.Str("\U0001D11E"), "'\U0001D11E'");
        Add("FText", "lone-high", A.BstrUnits("\uD800"), "ChrW(55296)");
        Add("FText", "lone-low", A.BstrUnits("\uDC00"), "ChrW(56320)");
        Add("FText", "reversed-pair", A.BstrUnits("\uDC00\uD800"), "ChrW(56320) & ChrW(55296)");
        Add("FText", "u-fffe", A.Str("\uFFFE"), "ChrW(65534)");
        Add("FText", "odd-1-byte", A.BstrBytes(0x41));
        Add("FText", "odd-3-bytes", A.BstrBytes(0x41, 0x00, 0x42));
        Add("FText", "odd-3-bytes-high", A.BstrBytes(0xff, 0xfe, 0xfd));
        Add("FText", "len-255", A.Str(x255), $"'{x255}'");
        Add("FText", "len-256", A.Str(x256), $"'{x256}'");

        Add("FMemo", "empty", A.Str(""), "''");
        Add("FMemo", "nul-middle", A.Str("a\0b"), "'a' & Chr(0) & 'b'");
        Add("FMemo", "lone-high", A.BstrUnits("\uD800"), "ChrW(55296)");
        Add("FMemo", "lone-low", A.BstrUnits("\uDC00"), "ChrW(56320)");
        Add("FMemo", "odd-1-byte", A.BstrBytes(0x41));
        Add("FMemo", "odd-5-bytes", A.BstrBytes(0x41, 0x00, 0x42, 0x00, 0x43));
        Add("FMemo", "len-300", A.Str(y300), $"'{y300}'");
        Add("FMemo", "len-5000", A.Str(new string('m', 5000)));
        Add("FMemo", "astral-4000", A.Str(string.Concat(Enumerable.Repeat("\U0001D11E", 4000))));
        Add("FMemo", "odd-8001-bytes", A.BstrBytes([.. Enumerable.Range(0, 8001).Select(i => i % 2 == 0 ? (byte)0x41 : (byte)0x00)]));

        Add("FBin", "empty", A.Bytes());
        Add("FBin", "leading-zeros", A.Bytes(0x00, 0x00, 0x01));
        Add("FBin", "all-zero", A.Bytes(0x00, 0x00, 0x00, 0x00));
        Add("FBin", "ramp-255", A.Bytes(Ramp(255, 1)));
        Add("FBin", "ramp-256", A.Bytes(Ramp(256, 1)));
        Add("FBin", "bstr-ab", A.Str("AB"));
        Add("FBin", "bstr-odd-3-bytes", A.BstrBytes(0x41, 0x00, 0x42));
        Add("FBin", "hex-literal", A.Bytes(0x00, 0x00, 0x01, 0x02), "0x00000102");

        Add("FOle", "empty", A.Bytes());
        Add("FOle", "leading-zeros", A.Bytes(0x00, 0x00, 0x01));
        Add("FOle", "ramp-256", A.Bytes(Ramp(256, 1)));
        Add("FOle", "pattern-5000", A.Bytes(Ramp(5000, 7)));
        Add("FOle", "bstr-ab", A.Str("AB"));
        Add("FOle", "bstr-odd-3-bytes", A.BstrBytes(0x01, 0x02, 0x03));

        Add("FBool", "true", A.Bool(true), "True");
        Add("FBool", "false", A.Bool(false), "False");
        Add("FBool", "i2-1", A.I2(1), "1");
        Add("FBool", "i2-2", A.I2(2), "2");
        Add("FBool", "i2-minus1", A.I2(-1), "-1");
        Add("FBool", "r8-0.5", A.R8(0.5), "0.5");
        Add("FBool", "bstr-True", A.Str("True"), "'True'");
        Add("FBool", "bstr-0", A.Str("0"), "'0'");
        Add("FBool", "ui1-255", A.UI1(255), "255");

        Add("FDec", "bstr-1.5", A.Str("1.5"), "1.5");
        Add("FDec", "bstr-minus-0.0001", A.Str("-0.0001"), "-0.0001");
        Add("FDec", "bstr-14.4-digits", A.Str("12345678901234.5678"), "12345678901234.5678");
        Add("FDec", "bstr-28-digits", A.Str("7922816251426433759354395033.5"), "7922816251426433759354395033.5");
        Add("FDec", "bstr-29-digits-max", A.Str("79228162514264337593543950335"), "79228162514264337593543950335");
        Add("FDec", "bstr-trailing-zero", A.Str("1.50"), "1.50");
        Add("FDec", "bstr-negative-zero", A.Str("-0.00"), "-0.00");
        Add("FDec", "cy-1.5", A.Cy(1.5m), "1.5");
        Add("FDec", "r8-0.1", A.R8(0.1), "0.1");
        Add("FDec", "i4-7", A.I4(7), "7");
        return [.. list];
    }

    /// <summary>Source tags for the tag-coercion matrix (one row per entry; ID = index + 1).</summary>
    private static readonly (string Label, Arg Value)[] SourceTags =
    [
        ("empty", A.Empty), ("null", A.Null), ("missing", A.Missing), ("bool-true", A.Bool(true)), ("ui1-7", A.UI1(7)),
        ("i2-7", A.I2(7)), ("i4-7", A.I4(7)), ("r4-1.5", A.R4(1.5f)), ("r8-1.5", A.R8(1.5)), ("cy-1.5", A.Cy(1.5m)),
        ("date-1.5", A.Date(1.5)), ("bstr-7", A.Str("7")), ("bstr-abc", A.Str("abc")), ("bstr-empty", A.Str("")),
        ("bytes-07", A.Bytes(0x07)), ("bstr-odd-37", A.BstrBytes(0x37)),
    ];

    private static readonly string CellList = string.Join("; ", Cols.Select(c =>
        $"{c.Name} (IDs from {c.Base}): " + string.Join(", ", Cells.Where(x => x.Col == c).Select(x => x.Label))));

    private static Arg[] Values(string col) => [.. Cells.Where(c => c.Col.Name == col).Select(c => c.Value)];

    public IEnumerable<Probe> Probes() =>
        Creations().Concat(ReadProbes()).Concat(GetRowsProbes()).Concat(PutTagProbes()).Concat(ExprProbes())
            .Concat(ParamProbes()).Concat(OptionalProbes()).Concat(ChunkProbes()).Concat(IdentityProbes()).Concat(AdoProbes());

    // Fixtures --------------------------------------------------------------------------------------------

    private static Probe Q(string name, string[] refs, string description, string? fixture, Action<P> program) =>
        new(Card, name, refs, description, program) { Fixtures = fixture is null ? [] : [("main", fixture)] };

    private static Probe Creation(string name, string what, Action<P> body) =>
        new(Card, "create-" + name, R("R-FMT-1"),
            $"R-WIRE fixture {Card}-{name}: {what}. Created in ACCDB by DBEngine.CreateDatabase(path, dbLangGeneral, 128); every call's result and error is recorded and the closed file is kept.",
            p =>
            {
                p.EngineLite().CallBind("db", "eng", "CreateDatabase", A.Out($"{Card}-{name}.accdb"), A.Str(Dao.LangGeneral), A.I4(Dao.Version120));
                body(p);
                p.FinishLite("db");
            })
        { CreatesFixture = true };

    private static P CreateTable(P p) =>
        p.Call("db", "Execute", A.Str("CREATE TABLE W (ID LONG CONSTRAINT PK_W PRIMARY KEY, " +
                string.Join(", ", Cols.Where(c => c.Ddl.Length > 0).Select(c => $"{c.Name} {c.Ddl}")) + ")"))
            .AddDecimalField("W", "FDec");

    private static IEnumerable<Probe> Creations()
    {
        yield return Creation("blank",
            "table W(ID LONG primary key PK_W, one column per shared scalar family: FByte BYTE, FInt SHORT, FLong LONG, FSingle SINGLE, FDouble DOUBLE, FCurr CURRENCY, FDate DATETIME, FText TEXT(255), " +
            "FMemo MEMO, FBin BINARY(255), FOle LONGBINARY, FBool YESNO, FDec Decimal added through TableDef.CreateField) empty, and table One(ID LONG) holding one row (1)",
            p => CreateTable(p).Call("db", "Execute", A.Str("CREATE TABLE One (ID LONG)")).Call("db", "Execute", A.Str("INSERT INTO One (ID) VALUES (1)")));

        yield return Creation("dao",
            $"table W as in r-wire-blank, then {Cells.Length} cells, each written as its own row (only ID and one column set) through a DAO table-type recordset AddNew, Fields(name).Value = Arg, Update. Cells: {CellList}",
            p =>
            {
                CreateTable(p);
                p.OpenRs("rs", "db", "W", Dao.OpenTable).GetBind("fl", "rs", "Fields").Item("fid", "fl", A.Str("ID"));
                foreach (Col c in Cols)
                {
                    p.Item("f" + c.Name, "fl", A.Str(c.Name));
                }
                foreach (Cell cell in Cells)
                {
                    p.Call("rs", "AddNew").Put("fid", "Value", A.I4(cell.Id)).Put("f" + cell.Col.Name, "Value", cell.Value).Call("rs", "Update");
                }
                p.Release("fid");
                foreach (Col c in Cols)
                {
                    p.Release("f" + c.Name);
                }
                p.Release("fl").Call("rs", "Close").Release("rs");
            });

        yield return Creation("sql",
            "table W as in r-wire-blank, then every cell of r-wire-dao that has a SQL literal written as INSERT INTO W (ID, column) VALUES (id, literal) through Database.Execute (cells without a literal are not attempted; " +
            $"the literals are in the statements). Cells: {CellList}",
            p =>
            {
                CreateTable(p);
                foreach (Cell cell in Cells.Where(c => c.Sql is not null))
                {
                    p.Call("db", "Execute", A.Str($"INSERT INTO W (ID, {cell.Col.Name}) VALUES ({cell.Id}, {cell.Sql})"));
                }
            });

        yield return Creation("param",
            "table W as in r-wire-blank, then every cell of r-wire-dao written through a temporary QueryDef per column, PARAMETERS pI Long, pV <column's parameter type>; INSERT INTO W (ID, column) VALUES (pI, pV), " +
            $"with Parameters(\"pI\").Value and Parameters(\"pV\").Value = Arg and QueryDef.Execute. Cells: {CellList}",
            p =>
            {
                CreateTable(p);
                foreach (Col c in Cols)
                {
                    string q = "q" + c.Name;
                    p.CallBind(q, "db", "CreateQueryDef", A.Str(""), A.Str($"PARAMETERS pI Long, pV {c.ParamType}; INSERT INTO W (ID, {c.Name}) VALUES (pI, pV);"))
                        .GetBind(q + "ps", q, "Parameters").Item(q + "pi", q + "ps", A.Str("pI")).Item(q + "pv", q + "ps", A.Str("pV"));
                    foreach (Cell cell in Cells.Where(x => x.Col == c))
                    {
                        p.Put(q + "pi", "Value", A.I4(cell.Id)).Put(q + "pv", "Value", cell.Value).Call(q, "Execute");
                    }
                    p.Release(q + "pi").Release(q + "pv").Release(q + "ps").Call(q, "Close").Release(q);
                }
            });
    }

    // Reading what each route stored -------------------------------------------------------------------------

    private static void FieldInfo(P p, string rs) =>
        p.GetBind(rs + "f", rs, "Fields")
            .Each(rs + "f", rs + "d", 32, b => b.Get(rs + "d", "Name").Get(rs + "d", "Type").Get(rs + "d", "Size").Get(rs + "d", "Attributes"))
            .Release(rs + "f");

    private static void ReadColumns(P p)
    {
        foreach (Col c in Cols)
        {
            string rs = "r" + c.Name;
            p.OpenRs(rs, "db", $"SELECT ID, {c.Name} FROM W WHERE ID BETWEEN {c.Base} AND {c.Base + 99} ORDER BY ID", Dao.OpenSnapshot);
            FieldInfo(p, rs);
            p.ReadAll(rs, 32, 4).Call(rs, "Close").Release(rs);
        }
    }

    private static IEnumerable<Probe> ReadProbes()
    {
        foreach ((string route, string fixture) in new[] { ("dao", DaoFx), ("sql", SqlFx), ("param", ParamFx) })
        {
            yield return Q($"read-{route}", R("II-02"),
                $"OR-WIRE read-back of fixture {fixture} (route {route}): reopened read-only through DAO, one snapshot per column of W (SELECT ID, column FROM W WHERE ID BETWEEN base AND base+99 ORDER BY ID); " +
                "for each, every Field Name/Type/Size/Attributes, then every row's Field.Value read once by ordinal. Asks, per cell, whether the row exists and its VARIANT tag, exact bits, BSTR byte length and payload, array bytes. " +
                $"Cells: {CellList}",
                fixture, p =>
                {
                    p.EngineLite().OpenDb();
                    ReadColumns(p);
                    p.FinishLite("db");
                });
        }
    }

    // GetRows ---------------------------------------------------------------------------------------------

    private const string Sel300 = "SELECT ID, FLong FROM W WHERE ID BETWEEN 300 AND 311 ORDER BY ID";

    private static IEnumerable<Probe> GetRowsProbes()
    {
        yield return Q("getrows-shape", R("R-DAO-1"),
            "GetRows over a snapshot of SELECT * FROM W WHERE ID BETWEEN 300 AND 311 ORDER BY ID (14 fields): GetRows(1), AbsolutePosition, GetRows(3), AbsolutePosition, GetRows(1000), EOF, AbsolutePosition, GetRows(1) after the end, EOF. " +
            "Asks the returned array's VARIANT tag, bounds (dimension order, lower bounds, extents), element tags and values.",
            DaoFx, p => p.EngineLite().OpenDb().OpenRs("rs", "db", "SELECT * FROM W WHERE ID BETWEEN 300 AND 311 ORDER BY ID", Dao.OpenSnapshot)
                .Call("rs", "GetRows", A.I4(1)).Get("rs", "AbsolutePosition").Call("rs", "GetRows", A.I4(3)).Get("rs", "AbsolutePosition")
                .Call("rs", "GetRows", A.I4(1000)).Get("rs", "EOF").Get("rs", "AbsolutePosition").Call("rs", "GetRows", A.I4(1)).Get("rs", "EOF")
                .Call("rs", "Close").Release("rs").FinishLite("db"));

        yield return Q("getrows-by-type", R("R-DAO-1"),
            "GetRows(100) for each column of W separately (SELECT ID, column FROM W WHERE ID BETWEEN base AND base+99 ORDER BY ID, snapshot), 13 recordsets. Asks the element VARIANT tag, exact value, BSTR byte length and byte-array contents of each cell, " +
            $"and the array bounds; Null cells are included. Cells: {CellList}",
            DaoFx, p =>
            {
                p.EngineLite().OpenDb();
                foreach (Col c in Cols)
                {
                    string rs = "g" + c.Name;
                    p.OpenRs(rs, "db", $"SELECT ID, {c.Name} FROM W WHERE ID BETWEEN {c.Base} AND {c.Base + 99} ORDER BY ID", Dao.OpenSnapshot)
                        .Call(rs, "GetRows", A.I4(100)).Call(rs, "Close").Release(rs);
                }
                p.FinishLite("db");
            });

        Arg[] counts = [A.I4(0), A.I4(-1), A.Missing, A.Empty, A.Null, A.I2(2), A.UI1(2), A.R8(2.5), A.R8(0.5), A.R4(2.0f), A.Str("2"), A.Str("x"), A.Bool(true), A.I4(int.MaxValue), A.I4(int.MinValue)];
        yield return Q("getrows-numrows-args", R("R-DAO-1"),
            "GetRows(NumRows) on a snapshot of SELECT ID, FLong FROM W WHERE ID BETWEEN 300 AND 311, MoveFirst before each: NumRows given as I4 0, I4 -1, Missing, Empty, Null, I2 2, UI1 2, R8 2.5, R8 0.5, R4 2.0, BSTR \"2\", BSTR \"x\", Boolean True, I4 max and I4 min. " +
            "Asks the returned array (tag, bounds, extent) or the error, and AbsolutePosition after each.",
            DaoFx, p =>
            {
                p.EngineLite().OpenDb().OpenRs("rs", "db", Sel300, Dao.OpenSnapshot);
                foreach (Arg count in counts)
                {
                    p.Call("rs", "MoveFirst").Call("rs", "GetRows", count).Get("rs", "AbsolutePosition");
                }
                p.Call("rs", "Close").Release("rs").FinishLite("db");
            });

        yield return Q("getrows-recordset-types", R("R-DAO-1"),
            "GetRows(2) twice on each recordset type over the same rows: table-type on W, and dynaset, snapshot, forward-only of SELECT ID, FLong, FText, FMemo FROM W WHERE ID BETWEEN 300 AND 311 ORDER BY ID. " +
            "Asks per type whether GetRows returns, the array shape and element tags, and the error or AbsolutePosition afterwards.",
            DaoFx, p =>
            {
                const string sel = "SELECT ID, FLong, FText, FMemo FROM W WHERE ID BETWEEN 300 AND 311 ORDER BY ID";
                p.EngineLite().OpenDb().OpenRs("rt", "db", "W", Dao.OpenTable).Call("rt", "GetRows", A.I4(2)).Call("rt", "GetRows", A.I4(2)).Call("rt", "Close").Release("rt");
                foreach ((string bind, int type) in new[] { ("rd", Dao.OpenDynaset), ("rs", Dao.OpenSnapshot), ("rf", Dao.OpenForwardOnly) })
                {
                    p.OpenRs(bind, "db", sel, type).Call(bind, "GetRows", A.I4(2)).Get(bind, "EOF").Call(bind, "GetRows", A.I4(2)).Get(bind, "EOF").Call(bind, "Close").Release(bind);
                }
                p.FinishLite("db");
            });

        yield return Q("getrows-position", R("R-DAO-1"),
            "GetRows interplay with position on a snapshot of SELECT ID, FLong FROM W WHERE ID BETWEEN 300 AND 305 ORDER BY ID: AbsolutePosition, GetRows(2), AbsolutePosition, BOF, EOF, MoveLast, GetRows(2), EOF, MoveFirst, MoveNext, GetRows(1), AbsolutePosition, " +
            "MovePrevious, Fields(\"ID\").Value, then an empty snapshot (WHERE 1 = 0) GetRows(5), BOF, EOF, then GetRows after Recordset.Close. Asks the arrays or errors and the flags.",
            DaoFx, p =>
            {
                p.EngineLite().OpenDb().OpenRs("rs", "db", "SELECT ID, FLong FROM W WHERE ID BETWEEN 300 AND 305 ORDER BY ID", Dao.OpenSnapshot)
                    .GetBind("fl", "rs", "Fields").Item("fid", "fl", A.Str("ID"))
                    .Get("rs", "AbsolutePosition").Call("rs", "GetRows", A.I4(2)).Get("rs", "AbsolutePosition").Get("rs", "BOF").Get("rs", "EOF")
                    .Call("rs", "MoveLast").Call("rs", "GetRows", A.I4(2)).Get("rs", "EOF")
                    .Call("rs", "MoveFirst").Call("rs", "MoveNext").Call("rs", "GetRows", A.I4(1)).Get("rs", "AbsolutePosition")
                    .Call("rs", "MovePrevious").Get("fid", "Value")
                    .OpenRs("re", "db", "SELECT ID, FLong FROM W WHERE 1 = 0", Dao.OpenSnapshot).Call("re", "GetRows", A.I4(5)).Get("re", "BOF").Get("re", "EOF").Call("re", "Close").Release("re")
                    .Release("fid").Release("fl").Call("rs", "Close").Call("rs", "GetRows", A.I4(1)).Release("rs").FinishLite("db");
            });

        yield return Q("getrows-long-values", R("R-DAO-1"),
            "GetRows(100) on a snapshot of SELECT ID, FMemo, FBin, FOle FROM W WHERE ID BETWEEN 900 AND 1199 ORDER BY ID (Memo, Binary and OLE cells including 5,000-character, 8,000-unit, 255-byte, 256-byte and 5,000-byte values). " +
            "Asks the array shape, each element's tag, BSTR byte length and byte-array bytes, and whether any value is shortened.",
            DaoFx, p => p.EngineLite().OpenDb().OpenRs("rs", "db", "SELECT ID, FMemo, FBin, FOle FROM W WHERE ID BETWEEN 900 AND 1199 ORDER BY ID", Dao.OpenSnapshot)
                .Call("rs", "GetRows", A.I4(100)).Get("rs", "EOF").Call("rs", "Close").Release("rs").FinishLite("db"));
    }

    // Tag coercion into each field type -----------------------------------------------------------------------

    private static IEnumerable<Probe> PutTagProbes()
    {
        string labels = string.Join(", ", SourceTags.Select((t, i) => $"{i + 1} {t.Label}"));
        foreach (Col c in Cols)
        {
            yield return Q($"put-tags-{c.Slug}", R(),
                $"Tag matrix into column {c.Name} (own copy of r-wire-blank, read-write): on a DAO table-type recordset, AddNew, Fields(\"ID\").Value = n, Fields(\"{c.Name}\").Value = each source Arg in turn, Update. Rows n: {labels}. " +
                $"Then SELECT ID, {c.Name} FROM W ORDER BY ID read once. Asks, per source tag, whether Put and Update succeed or raise, and which tag and value ACE returns.",
                BlankFx, p =>
                {
                    p.EngineLite().OpenDb(readOnly: false).OpenRs("rs", "db", "W", Dao.OpenTable).GetBind("fl", "rs", "Fields")
                        .Item("fid", "fl", A.Str("ID")).Item("fv", "fl", A.Str(c.Name));
                    for (int i = 0; i < SourceTags.Length; i++)
                    {
                        p.Call("rs", "AddNew").Put("fid", "Value", A.I4(i + 1)).Put("fv", "Value", SourceTags[i].Value).Call("rs", "Update");
                    }
                    p.Release("fid").Release("fv").Release("fl").Call("rs", "Close").Release("rs")
                        .OpenRs("rr", "db", $"SELECT ID, {c.Name} FROM W ORDER BY ID", Dao.OpenSnapshot).ReadAll("rr", 32, 4).Call("rr", "Close").Release("rr")
                        .FinishLite("db");
                });
        }
    }

    // Expression results ------------------------------------------------------------------------------------

    private static void SelectEach(P p, string prefix, string[] exprs, bool typeName)
    {
        for (int i = 0; i < exprs.Length; i++)
        {
            string rs = $"{prefix}{i}";
            p.OpenRs(rs, "db", $"SELECT {exprs[i]} AS V FROM One", Dao.OpenSnapshot);
            FieldInfo(p, rs);
            p.ReadAll(rs, 2, 2).Call(rs, "Close").Release(rs);
            if (typeName)
            {
                string tn = rs + "t";
                p.OpenRs(tn, "db", $"SELECT TypeName({exprs[i]}) AS TN, VarType({exprs[i]}) AS VT FROM One", Dao.OpenSnapshot).ReadAll(tn, 2, 4).Call(tn, "Close").Release(tn);
            }
        }
    }

    private static IEnumerable<Probe> ExprProbes()
    {
        string[] literals =
        [
            "1", "255", "256", "32767", "32768", "-32768", "-32769", "2147483647", "2147483648", "-2147483648", "0.5", "1E0", "1.5E308", "0.1", "4.9E-324",
            "922337203685477.5807", "79228162514264337593543950335", "12345678901234567890", "#1/2/2024 3:04:05 PM#", "#12/30/1899#", "'text'", "''", "True", "False",
            "Null", "&H7FFFFFFF", "&HFFFF", "&H80000000&", "-0.0", "0.0",
        ];
        yield return Q("expr-literal-tags", R("R-EXPR-1"),
            "Tags of SQL literals as returned by ACE: for each of 30 literals (integers around the Byte/Integer/Long limits, decimals, exponents, a 29-digit and a 20-digit integer, date, text, Boolean, Null, hex, signed zero), " +
            "SELECT literal AS V FROM One read (Field Name/Type/Size/Attributes and Value), then SELECT TypeName(literal), VarType(literal) FROM One. Asks the VARIANT tag, exact value, and whether TypeName/VarType exist and agree.",
            BlankFx, p =>
            {
                p.EngineLite().OpenDb();
                SelectEach(p, "l", literals, true);
                p.FinishLite("db");
            });

        string[] conversions =
        [
            "CByte(1)", "CByte(255.5)", "CByte(256)", "CInt(1)", "CInt(32767.5)", "CInt(2.5)", "CLng(1)", "CLng(2147483647.5)", "CLng(0.5)", "CSng(1)", "CSng(0.1)", "CSng(1E39)", "CDbl(CSng(0.1))",
            "CDbl(1)", "CCur(1)", "CCur(922337203685477.58075)", "CCur(0.00005)", "CDate(0.5)", "CDate(-1.5)", "CDate(2958466)", "CStr(1E15)", "CStr(0.1)", "CStr(Null)", "CBool(2)", "CBool(0)", "CBool('x')", "CDec(1)",
            "CVar(1)", "Int(-1.5)", "Fix(-1.5)", "Round(2.5)", "Round(0.5)", "Sgn(-0.0)", "1/3", "2^31", "7 Mod 3", "7 \\ 2",
        ];
        yield return Q("expr-conversion-tags", R("R-EXPR-1"),
            "Tags of conversion and arithmetic results as returned by ACE: for each of 37 expressions (CByte, CInt, CLng, CSng, CDbl, CCur, CDate, CStr, CBool, CDec, Int, Fix, Round, division, power, Mod, integer division, edge arguments), " +
            "SELECT expr AS V FROM One read (Field Type/Size and Value), then TypeName and VarType of the same expression. Asks the VARIANT tag, exact bits or payload, and any error.",
            BlankFx, p =>
            {
                p.EngineLite().OpenDb();
                SelectEach(p, "c", conversions, true);
                p.FinishLite("db");
            });

        string[] decimals =
        [
            "CDec('79228162514264337593543950335')", "CDec('-79228162514264337593543950335')", "CDec('0.0000000000000000000000000001')", "CDec('-0.0000000000000000000000000001')", "CDec('1.10')", "CDec('1.100000')",
            "CDec('-0')", "CDec('-0.00')", "CDec(1) / CDec(3)", "CDec(2) * CDec(3)", "CDec(1.1) + CDec(2.2)", "CDec(0.1) * CDec(3)", "CDec(1E28)", "CDec(79228162514264337593543950335) + 1",
            "CDec(922337203685477.5807)", "CDec(CCur(1.5))", "CDec(Null)", "CDec('0.5E-5')", "CDec(0)", "CDec(-1) * CDec(0)",
        ];
        yield return Q("expr-decimal-fields", R("R-EXPR-1"),
            "Decimal results as returned by ACE: for each of 20 CDec expressions (sign, scale 0 to 28, trailing zeros, negative zero, 29-digit magnitudes, arithmetic and overflow), SELECT expr AS V FROM One read: Field Type/Size and Value. " +
            "Asks the returned VARIANT tag and, for VT_DECIMAL, scale, sign and the three words of the magnitude (nonzero high word), or the error.",
            BlankFx, p =>
            {
                p.EngineLite().OpenDb();
                SelectEach(p, "d", decimals, false);
                p.FinishLite("db");
            });

        string[] errors =
        [
            "CVErr(2007)", "CVErr(0)", "CVErr(2042)", "CVErr(65535)", "IIf(1 = 1, CVErr(2007), 0)", "IsError(CVErr(2007))", "IsNull(Null)", "Null + 1", "Null & 'a'", "1 / 0", "Val('abc')", "Sqr(-1)", "Log(0)",
            "CInt('abc')", "CDate('x')", "Chr(0)", "Len(Chr(0))", "Asc(Chr(0))", "IIf(Null, 1, 2)", "Nz(Null)", "Nz(Null, 'z')",
        ];
        yield return Q("expr-error-null-values", R("R-EXPR-1", "R-ERR-1"),
            "Error, Null and failing-expression values through ACE: for each of 21 expressions (CVErr with several codes, IsError, Null arithmetic, division by zero, bad conversions, Chr(0), Nz), SELECT expr AS V FROM One read (Field Type and Value), then TypeName and VarType. " +
            "Asks the VARIANT tag (VT_ERROR, VT_NULL, ...), the SCODE or value, and which statements fail to open or to read, with the error.",
            BlankFx, p =>
            {
                p.EngineLite().OpenDb();
                SelectEach(p, "e", errors, true);
                p.Errors().FinishLite("db");
            });

        yield return Q("expr-clock-random", R("R-ORACLE-1", "OR-REUSE"),
            "Clock and random values with declared controllability: SELECT Now(), Date(), Time(), Timer() read, a 1,100 ms Pause, the same SELECT read again (uncontrolled clock); " +
            "SELECT Rnd(), Rnd(), Rnd(-1), Rnd(-1), Rnd(0), Rnd(1) AS five columns read twice from two fresh recordsets (unseeded and seeded random). Asks the VARIANT tags and bits, whether the clock advances between the reads and which Rnd values repeat across the two opens.",
            BlankFx, p =>
            {
                const string clock = "SELECT Now() AS N, Date() AS D, Time() AS T, Timer() AS R FROM One";
                const string random = "SELECT Rnd() AS A, Rnd() AS B, Rnd(-1) AS C, Rnd(-1) AS D, Rnd(0) AS E, Rnd(1) AS F FROM One";
                p.EngineLite().OpenDb();
                p.OpenRs("c1", "db", clock, Dao.OpenSnapshot).ReadAll("c1", 2, 4).Call("c1", "Close").Release("c1").Pause(1100)
                    .OpenRs("c2", "db", clock, Dao.OpenSnapshot).ReadAll("c2", 2, 4).Call("c2", "Close").Release("c2")
                    .OpenRs("r1", "db", random, Dao.OpenSnapshot).ReadAll("r1", 2, 8).Call("r1", "Close").Release("r1")
                    .OpenRs("r2", "db", random, Dao.OpenSnapshot).ReadAll("r2", 2, 8).Call("r2", "Close").Release("r2")
                    .FinishLite("db");
            });
    }

    // QueryDef parameters --------------------------------------------------------------------------------------

    /// <summary>One temporary QueryDef <c>PARAMETERS pV type; SELECT pV AS V FROM One</c>, opened once per value.</summary>
    private static void ParamSelect(P p, string q, string paramType, Arg[] values)
    {
        p.CallBind(q, "db", "CreateQueryDef", A.Str(""), A.Str($"PARAMETERS pV {paramType}; SELECT pV AS V FROM One;"))
            .GetBind(q + "ps", q, "Parameters").Item(q + "pv", q + "ps", A.Str("pV")).Get(q + "pv", "Type").Get(q + "pv", "Value");
        for (int i = 0; i < values.Length; i++)
        {
            string rs = $"{q}r{i}";
            p.Put(q + "pv", "Value", values[i]).Get(q + "pv", "Value").CallBind(rs, q, "OpenRecordset", A.I4(Dao.OpenSnapshot)).ReadAll(rs, 2, 2).Call(rs, "Close").Release(rs);
        }
        p.Release(q + "pv").Release(q + "ps").Call(q, "Close").Release(q);
    }

    private static IEnumerable<Probe> ParamProbes()
    {
        Arg[] ints = [A.UI1(0), A.UI1(255), A.I2(-1), A.I2(256), A.I4(65536), A.I4(int.MaxValue), A.I4(int.MinValue), A.R8(1.5), A.R8(2.5), A.Str("7"), A.Str("abc"), A.Null, A.Empty, A.Bool(true)];
        yield return Q("param-select-integers", R("R-SQL-1"),
            "QueryDef parameter round trip, integer types (own copy of r-wire-blank): for each declared type Byte, Short, Long, a temporary QueryDef PARAMETERS pV type; SELECT pV AS V FROM One; Parameter Type and Value read, then for each value (UI1 0 and 255, I2 -1 and 256, I4 65536, max, min, " +
            "R8 1.5 and 2.5, BSTR \"7\" and \"abc\", Null, Empty, Boolean True) Parameters(\"pV\").Value = Arg, Value read back, OpenRecordset(snapshot) and the V field read. Asks what the Parameter returns and what the engine returns, with tags.",
            BlankFx, p =>
            {
                p.EngineLite().OpenDb(readOnly: false);
                ParamSelect(p, "qb", "Byte", ints);
                ParamSelect(p, "qi", "Short", ints);
                ParamSelect(p, "ql", "Long", ints);
                p.FinishLite("db");
            });

        yield return Q("param-select-floats", R("R-SQL-1"),
            "QueryDef parameter round trip, floating types: the same PARAMETERS pV IEEESingle / IEEEDouble; SELECT pV AS V FROM One shape, for each of the r-wire cells of FSingle and FDouble (signed zero, subnormals, normal limits, infinities, quiet/signaling/negative NaN with payloads, widened and narrowed values; " +
            "Arg tag R4 or R8 as in the cell). Asks the Parameter read-back and the V field tag and exact bits.",
            BlankFx, p =>
            {
                p.EngineLite().OpenDb(readOnly: false);
                ParamSelect(p, "qs", "IEEESingle", Values("FSingle"));
                ParamSelect(p, "qd", "IEEEDouble", Values("FDouble"));
                p.FinishLite("db");
            });

        yield return Q("param-select-text", R("R-SQL-1", "II-02"),
            "QueryDef parameter round trip, text types: the same shape with Text(255) and LongText, for each r-wire cell of FText and FMemo (empty, embedded U+0000, astral pair, lone high and low surrogates, reversed pair, U+FFFE, odd-byte BSTRs of 1, 3 and 5 bytes, " +
            "255 and 256 and 300 and 5,000 units, 4,000 pairs, an 8,001-byte BSTR). Asks the Parameter read-back and the V field tag, BSTR byte length and payload.",
            BlankFx, p =>
            {
                p.EngineLite().OpenDb(readOnly: false);
                ParamSelect(p, "qt", "Text(255)", Values("FText"));
                ParamSelect(p, "qm", "LongText", Values("FMemo"));
                p.FinishLite("db");
            });

        yield return Q("param-select-binary", R("R-SQL-1"),
            "QueryDef parameter round trip, binary types: the same shape with Binary and LongBinary, for each r-wire cell of FBin and FOle (empty, leading zeros, all zero, 255, 256 and 5,000 bytes, BSTR \"AB\", odd-byte BSTR, hex literal bytes). " +
            "Asks the Parameter read-back and the V field tag and exact bytes.",
            BlankFx, p =>
            {
                p.EngineLite().OpenDb(readOnly: false);
                ParamSelect(p, "qn", "Binary", Values("FBin"));
                ParamSelect(p, "qo", "LongBinary", Values("FOle"));
                p.FinishLite("db");
            });

        yield return Q("param-select-money-date-bool-decimal", R("R-SQL-1"),
            "QueryDef parameter round trip, Currency, DateTime, Bit and Decimal: the same shape for each r-wire cell of FCurr, FDate, FBool and FDec (Currency limits, rounding and overflow; Date extremes, fractions, NaN and infinity; Boolean tags and texts; " +
            "Decimal strings of 1 to 29 digits and other tags). Asks whether PARAMETERS accepts Decimal, the Parameter read-back and the V field tag and exact value.",
            BlankFx, p =>
            {
                p.EngineLite().OpenDb(readOnly: false);
                ParamSelect(p, "qc", "Currency", Values("FCurr"));
                ParamSelect(p, "qd", "DateTime", Values("FDate"));
                ParamSelect(p, "qb", "Bit", Values("FBool"));
                ParamSelect(p, "qx", "Decimal", Values("FDec"));
                p.FinishLite("db");
            });
    }

    // Optional arguments --------------------------------------------------------------------------------------

    private static void Opt(P p, string prefix, string target, string member, Arg[][] variants, Action<P, string> after)
    {
        for (int i = 0; i < variants.Length; i++)
        {
            string bind = $"{prefix}{i}";
            p.CallBind(bind, target, member, variants[i]);
            after(p, bind);
        }
    }

    private static IEnumerable<Probe> OptionalProbes()
    {
        Arg path = A.In();
        Arg[][] openDb =
        [
            [path], [path, A.Missing], [path, A.Missing, A.Missing], [path, A.Missing, A.Missing, A.Missing], [path, A.Empty, A.Empty, A.Empty], [path, A.Null, A.Null, A.Null],
            [path, A.Bool(false), A.Bool(true)], [path, A.Missing, A.Bool(true)], [path, A.Missing, A.Empty], [path, A.Missing, A.Null], [path, A.I2(0), A.I2(-1)], [path, A.Str("x")],
            [path, A.Missing, A.Missing, A.Str("")], [path, A.Missing, A.Missing, A.Null], [path, A.Missing, A.Missing, A.Empty], [A.Empty], [A.Null], [A.Missing],
        ];
        yield return Q("opt-opendatabase", R("R-DAO-1", "R-ACT-1"),
            "Optional arguments of DBEngine.OpenDatabase(Name, Options, ReadOnly, Connect) on a copy of r-wire-blank: 18 variants (name only; Missing in each optional position and in all; Empty or Null in all; Options Boolean False with ReadOnly True; I2 0 and I2 -1; BSTR \"x\" as Options; " +
            "Connect as empty BSTR, Null, Empty; Name as Empty, Null, Missing). Asks the HRESULT and error of each, and, when it opens, Database.Updatable; each opened database is closed.",
            BlankFx, p =>
            {
                p.EngineLite();
                Opt(p, "o", "eng", "OpenDatabase", openDb, (q, b) => q.Get(b, "Updatable").Call(b, "Close").Release(b));
                p.Files("after-close");
            });

        Arg[][] openRs =
        [
            [A.Str("W")], [A.Str("W"), A.Missing], [A.Str("W"), A.Empty], [A.Str("W"), A.Null], [A.Str("W"), A.I4(Dao.OpenTable)], [A.Str("W"), A.I2(4)], [A.Str("W"), A.R8(4.0)], [A.Str("W"), A.Str("4")],
            [A.Str("W"), A.Bool(true)], [A.Str("W"), A.Missing, A.Missing, A.Missing], [A.Str("W"), A.Empty, A.Empty, A.Empty], [A.Str("W"), A.Null, A.Null, A.Null], [A.Str("W"), A.I4(Dao.OpenDynaset), A.Missing, A.I4(3)],
            [A.Str("SELECT ID FROM W")], [A.Str("SELECT ID FROM W"), A.Missing, A.Missing, A.Missing], [A.Str("SELECT ID FROM W"), A.Empty], [A.Str("SELECT ID FROM W"), A.Null], [A.Str("SELECT ID FROM W"), A.I4(Dao.OpenTable)],
            [A.Empty], [A.Null], [A.Missing],
        ];
        yield return Q("opt-openrecordset", R("R-DAO-1", "R-CURSOR-1"),
            "Optional arguments of Database.OpenRecordset(Source, Type, Options, LockEdit) on a read-write copy of r-wire-blank: 21 variants over source W and over SELECT ID FROM W (Type omitted, Missing, Empty, Null, as I4, I2, R8, BSTR \"4\", Boolean; " +
            "Options and LockEdit Missing, Empty, Null; table-type requested for a SQL source; Source as Empty, Null, Missing). Asks the HRESULT and error of each and, when it opens, Recordset.Type, Updatable and LockEdits; each is closed.",
            BlankFx, p =>
            {
                p.EngineLite().OpenDb(readOnly: false);
                Opt(p, "o", "db", "OpenRecordset", openRs, (q, b) => q.Get(b, "Type").Get(b, "Updatable").Get(b, "LockEdits").Call(b, "Close").Release(b));
                p.FinishLite("db");
            });

        Arg[][] execs =
        [
            [A.Str("INSERT INTO One (ID) VALUES (10)")], [A.Str("INSERT INTO One (ID) VALUES (11)"), A.Missing], [A.Str("INSERT INTO One (ID) VALUES (12)"), A.Empty], [A.Str("INSERT INTO One (ID) VALUES (13)"), A.Null],
            [A.Str("INSERT INTO One (ID) VALUES (14)"), A.I4(Dao.FailOnError)], [A.Str("INSERT INTO One (ID) VALUES (15)"), A.I2(128)], [A.Str("INSERT INTO One (ID) VALUES (16)"), A.Str("128")],
            [A.Str("INSERT INTO One (ID) VALUES (17)"), A.Bool(true)], [A.Str("INSERT INTO One (ID) VALUES (18)"), A.I4(0)], [A.Str("INSERT INTO One (ID) VALUES (19)"), A.R8(128.0)], [A.Empty], [A.Null], [A.Missing],
        ];
        yield return Q("opt-execute", R("R-DAO-1"),
            "Optional Options argument of Database.Execute(Query, Options) on a read-write copy of r-wire-blank: 13 variants (Options omitted, Missing, Empty, Null, I4 128, I2 128, BSTR \"128\", Boolean True, I4 0, R8 128.0; Query as Empty, Null, Missing), each an INSERT into One of a distinct ID. " +
            "Asks the HRESULT and error of each, Database.RecordsAffected after each, then SELECT ID FROM One read.",
            BlankFx, p =>
            {
                p.EngineLite().OpenDb(readOnly: false);
                foreach (Arg[] args in execs)
                {
                    p.Call("db", "Execute", args).Get("db", "RecordsAffected");
                }
                p.OpenRs("rr", "db", "SELECT ID FROM One ORDER BY ID", Dao.OpenSnapshot).ReadAll("rr", 32, 2).Call("rr", "Close").Release("rr").FinishLite("db");
            });

        Arg[][] qdExec = [[], [A.Missing], [A.Empty], [A.Null], [A.I4(Dao.FailOnError)], [A.Str("128")]];
        Arg[][] qdOpen = [[], [A.Missing, A.Missing, A.Missing], [A.Empty, A.Empty, A.Empty], [A.Null, A.Null, A.Null], [A.I4(Dao.OpenSnapshot)], [A.I4(Dao.OpenSnapshot), A.Missing, A.Missing], [A.Missing, A.I4(0)]];
        yield return Q("opt-querydef-calls", R("R-DAO-1", "R-SQL-3"),
            "Optional arguments of QueryDef.Execute(Options) and QueryDef.OpenRecordset(Type, Options, LockEdit) on a read-write copy of r-wire-blank: a temporary INSERT QueryDef executed with no argument, Missing, Empty, Null, I4 128 and BSTR \"128\" (QueryDef.RecordsAffected after each); " +
            "a temporary SELECT ID FROM One QueryDef opened with no arguments, Missing x3, Empty x3, Null x3, snapshot alone, snapshot and Missing, and Missing with Options 0. Asks the HRESULT, error, RecordsAffected and, when open, Recordset.Type and Updatable.",
            BlankFx, p =>
            {
                p.EngineLite().OpenDb(readOnly: false).CallBind("qi", "db", "CreateQueryDef", A.Str(""), A.Str("INSERT INTO One (ID) VALUES (900)"));
                foreach (Arg[] args in qdExec)
                {
                    p.Call("qi", "Execute", args).Get("qi", "RecordsAffected");
                }
                p.Call("qi", "Close").Release("qi").CallBind("qs", "db", "CreateQueryDef", A.Str(""), A.Str("SELECT ID FROM One"));
                Opt(p, "o", "qs", "OpenRecordset", qdOpen, (q, b) => q.Get(b, "Type").Get(b, "Updatable").Call(b, "Close").Release(b));
                p.Call("qs", "Close").Release("qs").FinishLite("db");
            });

        string lang = Dao.LangGeneral;
        Arg[][] createDb =
        [
            [A.Out("o0.accdb"), A.Str(lang)], [A.Out("o1.accdb"), A.Str(lang), A.Missing], [A.Out("o2.accdb"), A.Str(lang), A.Empty], [A.Out("o3.accdb"), A.Str(lang), A.Null],
            [A.Out("o4.accdb"), A.Str(lang), A.I4(Dao.Version120)], [A.Out("o5.accdb"), A.Str(lang), A.I2(128)], [A.Out("o6.accdb"), A.Str(lang), A.Str("128")], [A.Out("o7.accdb"), A.Missing, A.I4(Dao.Version120)],
            [A.Out("o8.accdb"), A.Empty, A.I4(Dao.Version120)], [A.Out("o9.accdb"), A.Null, A.I4(Dao.Version120)], [A.Out("o10.accdb"), A.Str(""), A.I4(Dao.Version120)], [A.Out("o11.accdb")], [A.Out("o12.accdb"), A.Missing, A.Missing],
        ];
        yield return Q("opt-createdatabase", R("R-DAO-1", "R-FMT-1"),
            "Optional arguments of DBEngine.CreateDatabase(Name, Locale, Option) with 13 variants, each creating a distinct new file in the work directory: Option omitted, Missing, Empty, Null, I4 128, I2 128, BSTR \"128\"; Locale as Missing, Empty, Null, empty BSTR; " +
            "Name alone; Locale and Option both Missing. Asks the HRESULT and error of each and, when created, Database.Version; the created files are kept.",
            null, p =>
            {
                p.EngineLite();
                Opt(p, "n", "eng", "CreateDatabase", createDb, (q, b) => q.Get(b, "Version").Call(b, "Close").Release(b));
                p.Files("after-keep", keep: true);
            });

        Arg[][] createTd =
        [
            [A.Str("T0")], [A.Str("T1"), A.Missing, A.Missing, A.Missing], [A.Str("T2"), A.Empty, A.Empty, A.Empty], [A.Str("T3"), A.Null, A.Null, A.Null], [A.Str("T4"), A.I4(0)], [A.Str("T5"), A.I4(0), A.Str("")],
            [A.Str("T6"), A.Str("0")], [A.Empty], [A.Null], [A.Missing],
        ];
        yield return Q("opt-createtabledef", R("R-DAO-1", "R-CAT-1"),
            "Optional arguments of Database.CreateTableDef(Name, Attributes, SourceTableName, Connect) on a read-write copy of r-wire-blank: 10 variants (Name only; Missing, Empty or Null for the three optional arguments; Attributes I4 0 with and without SourceTableName; Attributes as BSTR; Name as Empty, Null, Missing). " +
            "Asks the HRESULT and error of each and, when created, the TableDef Name, Attributes, SourceTableName and Connect; none is appended.",
            BlankFx, p =>
            {
                p.EngineLite().OpenDb(readOnly: false);
                Opt(p, "t", "db", "CreateTableDef", createTd, (q, b) => q.Get(b, "Name").Get(b, "Attributes").Get(b, "SourceTableName").Get(b, "Connect").Release(b));
                p.FinishLite("db");
            });

        Arg[][] createField =
        [
            [A.Str("F0")], [A.Str("F1"), A.Missing, A.Missing], [A.Str("F2"), A.Empty, A.Empty], [A.Str("F3"), A.Null, A.Null], [A.Str("F4"), A.I4(10)], [A.Str("F5"), A.I4(10), A.Missing], [A.Str("F6"), A.I4(10), A.Empty],
            [A.Str("F7"), A.I4(10), A.Null], [A.Str("F8"), A.I4(10), A.I4(20)], [A.Str("F9"), A.Str("10"), A.Str("20")], [A.Str("F10"), A.Missing, A.I4(20)], [A.Str("F11"), A.Empty, A.I4(20)], [A.Str("F12"), A.I4(4), A.I4(20)],
            [A.Str("F13"), A.I4(12), A.I4(5)], [A.Str("F14"), A.I4(11), A.Missing], [A.Empty], [A.Null], [A.Missing],
        ];
        yield return Q("opt-createfield", R("R-DAO-1", "R-CAT-1"),
            "Optional arguments of TableDef.CreateField(Name, Type, Size) on a TableDef created (not appended) by Database.CreateTableDef(\"TX\") on a read-write copy of r-wire-blank: 18 variants (Name only; Type and Size each Missing, Empty, Null, I4, BSTR; " +
            "Type 10 Text with Size 20; Type 4 Long with Size 20; Type 12 Memo; Type 11 LongBinary; Name as Empty, Null, Missing). Asks the HRESULT and error of each and, when created, Field Name, Type, Size and Attributes.",
            BlankFx, p =>
            {
                p.EngineLite().OpenDb(readOnly: false).CallBind("tx", "db", "CreateTableDef", A.Str("TX"));
                Opt(p, "f", "tx", "CreateField", createField, (q, b) => q.Get(b, "Name").Get(b, "Type").Get(b, "Size").Get(b, "Attributes").Release(b));
                p.Release("tx").FinishLite("db");
            });

        Arg[][] moves =
        [
            [A.I4(2)], [A.I4(2), A.Missing], [A.I4(2), A.Empty], [A.I4(2), A.Null], [A.I4(-1)], [A.Missing], [A.Empty], [A.Null], [A.R8(1.5)], [A.R8(2.5)], [A.Str("2")], [A.Bool(true)], [A.I4(2), A.Bytes()], [A.I4(2), A.I4(1)],
        ];
        yield return Q("opt-move", R("R-DAO-1", "R-CURSOR-1"),
            "Optional Start argument of Recordset.Move(Rows, StartBookmark) on a snapshot of SELECT ID FROM W ORDER BY ID, MoveFirst before each: 14 variants (Start omitted, Missing, Empty, Null, I4 1, empty byte array; Rows as I4 -1, Missing, Empty, Null, R8 1.5, R8 2.5, BSTR \"2\", Boolean True). " +
            "Asks the HRESULT and error of each and the Fields(\"ID\").Value, AbsolutePosition, BOF and EOF afterwards.",
            DaoFx, p =>
            {
                p.EngineLite().OpenDb().OpenRs("rs", "db", "SELECT ID FROM W ORDER BY ID", Dao.OpenSnapshot).GetBind("fl", "rs", "Fields").Item("fid", "fl", A.Str("ID"));
                foreach (Arg[] args in moves)
                {
                    p.Call("rs", "MoveFirst").Call("rs", "Move", args).Get("rs", "BOF").Get("rs", "EOF").Get("rs", "AbsolutePosition").Get("fid", "Value");
                }
                p.Release("fid").Release("fl").Call("rs", "Close").Release("rs").FinishLite("db");
            });

        Arg[] criteria = [A.Str("ID = 305"), A.Str(""), A.Null, A.Empty, A.Missing, A.I4(305), A.Str("ID = 'x'"), A.Str("ID = 305 AND"), A.BstrBytes(0x49), A.Str("FText = 'a' & Chr(0) & 'b'"), A.Str("FText = 'alpha'")];
        yield return Q("opt-find-criteria", R("R-DAO-1", "R-CURSOR-1"),
            "Criteria argument of Recordset.FindFirst on a dynaset of SELECT ID, FText FROM W ORDER BY ID: \"ID = 305\", empty BSTR, Null, Empty, Missing, I4 305, \"ID = 'x'\", \"ID = 305 AND\", a 1-byte BSTR, \"FText = 'a' & Chr(0) & 'b'\", \"FText = 'alpha'\"; " +
            "then FindNext, FindPrevious and FindLast each with Null and with Missing. Asks the HRESULT and error, NoMatch and Fields(\"ID\").Value after each.",
            DaoFx, p =>
            {
                p.EngineLite().OpenDb().OpenRs("rs", "db", "SELECT ID, FText FROM W ORDER BY ID", Dao.OpenDynaset).GetBind("fl", "rs", "Fields").Item("fid", "fl", A.Str("ID"));
                foreach (Arg c in criteria)
                {
                    p.Call("rs", "FindFirst", c).Get("rs", "NoMatch").Get("fid", "Value");
                }
                foreach (string method in new[] { "FindNext", "FindPrevious", "FindLast" })
                {
                    p.Call("rs", method, A.Null).Get("rs", "NoMatch").Get("fid", "Value").Call("rs", method, A.Missing).Get("rs", "NoMatch").Get("fid", "Value");
                }
                p.Release("fid").Release("fl").Call("rs", "Close").Release("rs").FinishLite("db");
            });

        Arg[][] seeks =
        [
            [A.Str("="), A.I4(300)], [A.Str("="), A.Str("300")], [A.Str("="), A.R8(300.0)], [A.Str("="), A.I2(300)], [A.Str("="), A.UI1(100)], [A.Str("="), A.Null], [A.Str("="), A.Empty], [A.Str("="), A.Missing],
            [A.Str("="), A.Bool(true)], [A.Str("="), A.I4(300), A.Missing, A.Missing], [A.Str("="), A.I4(300), A.I4(1)], [A.Str(">="), A.I4(300)], [A.Str(">"), A.I4(300)], [A.Str("<="), A.I4(300)], [A.Str("<"), A.I4(300)],
            [A.Str("=="), A.I4(300)], [A.Str(""), A.I4(300)], [A.Null, A.I4(300)], [A.Empty, A.I4(300)], [A.Missing, A.I4(300)], [A.I4(0), A.I4(300)], [A.Str("= "), A.I4(300)], [A.BstrBytes(0x3d), A.I4(300)],
        ];
        yield return Q("opt-seek", R("R-DAO-1", "R-CURSOR-1"),
            "Arguments of Recordset.Seek(Comparison, Key1..Key13) on a table-type recordset of W with Index = \"PK_W\": 23 variants (comparison \"=\" with key as I4 300, BSTR \"300\", R8, I2, UI1, Null, Empty, Missing, Boolean, extra Missing keys, an extra I4 key; " +
            "comparisons \">=\", \">\", \"<=\", \"<\", \"==\", empty BSTR, Null, Empty, Missing, I4 0, \"= \", a 1-byte BSTR \"=\"). Asks the HRESULT and error of each and NoMatch and Fields(\"ID\").Value afterwards.",
            DaoFx, p =>
            {
                p.EngineLite().OpenDb().OpenRs("rs", "db", "W", Dao.OpenTable).Put("rs", "Index", A.Str("PK_W")).GetBind("fl", "rs", "Fields").Item("fid", "fl", A.Str("ID")).Get("rs", "Index");
                foreach (Arg[] args in seeks)
                {
                    p.Call("rs", "Seek", args).Get("rs", "NoMatch").Get("fid", "Value");
                }
                p.Release("fid").Release("fl").Call("rs", "Close").Release("rs").FinishLite("db");
            });

        Arg[][] workspaces =
        [
            [A.Str("w0"), A.Str("admin"), A.Str("")], [A.Str("w1"), A.Str("admin"), A.Str(""), A.Missing], [A.Str("w2"), A.Str("admin"), A.Str(""), A.Empty], [A.Str("w3"), A.Str("admin"), A.Str(""), A.Null],
            [A.Str("w4"), A.Str("admin"), A.Missing], [A.Str("w5"), A.Str("admin"), A.Empty], [A.Str("w6"), A.Str("admin"), A.Null], [A.Str("w7"), A.Missing, A.Str("")], [A.Str("w8"), A.Empty, A.Str("")],
            [A.Str("w9"), A.Null, A.Str("")], [A.Str("w10"), A.Str("admin"), A.Str(""), A.I4(2)], [A.Str("w11"), A.Str("admin"), A.Str(""), A.Str("2")], [A.Str("w12"), A.Str("admin"), A.Str(""), A.I4(1)],
        ];
        yield return Q("opt-createworkspace", R("R-DAO-1", "R-ACT-1"),
            "Optional arguments of DBEngine.CreateWorkspace(Name, UserName, Password, UseType): 13 variants (UseType omitted, Missing, Empty, Null, I4 2, BSTR \"2\", I4 1; Password Missing, Empty, Null; UserName Missing, Empty, Null). " +
            "Asks the HRESULT and error of each and, when created, Workspace Name, UserName and IsolateODBCTrans; none is appended.",
            null, p =>
            {
                p.EngineLite();
                Opt(p, "w", "eng", "CreateWorkspace", workspaces, (q, b) => q.Get(b, "Name").Get(b, "UserName").Get(b, "IsolateODBCTrans").Release(b));
                p.Files("after-close");
            });
    }

    // AppendChunk and GetChunk with binary and text -------------------------------------------------------------

    private static IEnumerable<Probe> ChunkProbes()
    {
        Arg[][] oleChunks =
        [
            [A.Bytes(0x00, 0x00, 0x01)], [A.Bytes()], [A.Bytes(0x00, 0x00, 0x00, 0x00)], [A.BstrBytes(0x01, 0x02, 0x03)], [A.Str("AB")], [A.Bytes(0x01, 0x02, 0x03), A.Bytes(0x00, 0x00), A.Bytes(0xff)],
            [A.Null], [A.Empty], [A.Missing], [A.I4(5)], [A.R8(1.5)], [A.Bytes(Ramp(256, 1))], [A.Bool(true)],
        ];
        yield return Q("chunk-ole-append", R("R-FMT-1"),
            "Field.AppendChunk on a LongBinary field (read-write copy of r-wire-blank): on a dynaset of SELECT ID, FOle FROM W, thirteen rows each AddNew, ID = n, AppendChunk of the given Arg(s), Update: 1 bytes 00 00 01, 2 empty array, 3 four zero bytes, 4 BSTR of 3 odd bytes, " +
            "5 BSTR \"AB\", 6 three arrays in sequence, 7 Null, 8 Empty, 9 Missing, 10 I4, 11 R8, 12 a 256-byte array, 13 Boolean. Then each row re-read through FindFirst with Fields(\"FOle\").FieldSize and GetChunk(0, 300), and the table read once. " +
            "Asks per row the HRESULT of AppendChunk and Update, the stored bytes and sizes.",
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
                    p.Call("rs", "Update");
                }
                for (int i = 0; i < oleChunks.Length; i++)
                {
                    p.Call("rs", "FindFirst", A.Str($"ID = {i + 1}")).Get("rs", "NoMatch").Call("fo", "FieldSize").Call("fo", "GetChunk", A.I4(0), A.I4(300));
                }
                p.Release("fid").Release("fo").Release("fl").Call("rs", "Close").Release("rs")
                    .OpenRs("rr", "db", "SELECT ID, FOle FROM W ORDER BY ID", Dao.OpenSnapshot).ReadAll("rr", 16, 4).Call("rr", "Close").Release("rr").FinishLite("db");
            });

        Arg[][] memoChunks =
        [
            [A.Str("abc")], [A.Str("a\0b")], [A.BstrBytes(0x41)], [A.BstrBytes(0x41, 0x00, 0x42)], [A.BstrUnits("\uD800")], [A.Str("ab"), A.BstrBytes(0x41), A.Str("cd")], [A.Bytes(0x41, 0x00, 0x42, 0x00)],
            [A.Null], [A.Empty], [A.Missing], [A.I4(5)], [A.Str("")], [A.Str(new string('m', 300))],
        ];
        yield return Q("chunk-memo-text-append", R("R-FMT-1", "II-02"),
            "Field.AppendChunk on a Memo field and then on a Text field (read-write copy of r-wire-blank): on a dynaset of SELECT ID, FMemo, FText FROM W, thirteen rows each AddNew, ID = n, AppendChunk on FMemo of: 1 \"abc\", 2 U+0000 inside, 3 BSTR of 1 byte, 4 BSTR of 3 bytes, " +
            "5 lone high surrogate, 6 \"ab\" then odd BSTR then \"cd\", 7 UI1 array of four bytes, 8 Null, 9 Empty, 10 Missing, 11 I4, 12 empty BSTR, 13 300 characters; Update. Rows 21 to 23: AppendChunk on FText of \"abc\", an odd BSTR and a UI1 array. " +
            "Then each Memo row re-read with FieldSize and GetChunk(0, 40) through FindFirst, and the table read once. Asks the HRESULT of each step, stored values, sizes and BSTR byte lengths.",
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
                    p.Call("rs", "Update");
                }
                Arg[] textChunks = [A.Str("abc"), A.BstrBytes(0x41), A.Bytes(0x41, 0x00)];
                for (int i = 0; i < textChunks.Length; i++)
                {
                    p.Call("rs", "AddNew").Put("fid", "Value", A.I4(21 + i)).Call("ft", "AppendChunk", textChunks[i]).Call("rs", "Update");
                }
                for (int i = 0; i < memoChunks.Length; i++)
                {
                    p.Call("rs", "FindFirst", A.Str($"ID = {i + 1}")).Get("rs", "NoMatch").Call("fm", "FieldSize").Call("fm", "GetChunk", A.I4(0), A.I4(40));
                }
                p.Release("fid").Release("fm").Release("ft").Release("fl").Call("rs", "Close").Release("rs")
                    .OpenRs("rr", "db", "SELECT ID, FMemo, FText FROM W ORDER BY ID", Dao.OpenSnapshot).ReadAll("rr", 32, 4).Call("rr", "Close").Release("rr").FinishLite("db");
            });
    }

    // Object identity ---------------------------------------------------------------------------------------

    private static void Names(P p, params string[] binds)
    {
        foreach (string bind in binds)
        {
            p.Get(bind, "Name");
        }
    }

    private static IEnumerable<Probe> IdentityProbes()
    {
        yield return Q("ident-fields-repeat", R("OR-OBS"),
            "Object identity of repeated Fields reads on a snapshot of SELECT ID, FLong FROM W WHERE ID BETWEEN 300 AND 311: Recordset.Fields read as three bound objects, then an unbound read; Count of each; Item(\"ID\") twice on the first, once on the second, Item(0) on the first, " +
            "Item(\"FLong\") and Item(1) on the second; Name of every retained Field; one Field released and Item(\"ID\") read again. Asks which returned objects share one capture identity label and which differ.",
            DaoFx, p =>
            {
                p.EngineLite().OpenDb().OpenRs("rs", "db", Sel300, Dao.OpenSnapshot)
                    .GetBind("c1", "rs", "Fields").GetBind("c2", "rs", "Fields").GetBind("c3", "rs", "Fields").Get("rs", "Fields").Get("c1", "Count").Get("c2", "Count")
                    .Item("a1", "c1", A.Str("ID")).Item("a2", "c1", A.Str("ID")).Item("a3", "c2", A.Str("ID")).Item("a4", "c1", A.I4(0)).Item("a5", "c1", A.Str("FLong")).Item("a6", "c2", A.I4(1));
                Names(p, "a1", "a2", "a3", "a4", "a5", "a6");
                p.Release("a2").Item("a7", "c1", A.Str("ID"));
                Names(p, "a7");
                p.Release("a7").Release("a6").Release("a5").Release("a4").Release("a3").Release("a1").Release("c3").Release("c2").Release("c1").Call("rs", "Close").Release("rs").FinishLite("db");
            });

        yield return Q("ident-item-routes", R("OR-OBS"),
            "Object identity across the ways to reach one Field of a snapshot of SELECT ID, FLong FROM W: Fields.Item(\"FLong\") twice, Item(\"flong\") by another case, Item(1) as I4, as I2 and as UI1, an explicit property get of Item, an explicit method call of Item, " +
            "and the Item(i) of Each over Fields (Name read), all while the earlier objects are held. Asks which routes return the same capture identity label.",
            DaoFx, p =>
            {
                p.EngineLite().OpenDb().OpenRs("rs", "db", "SELECT ID, FLong FROM W WHERE ID BETWEEN 300 AND 311 ORDER BY ID", Dao.OpenSnapshot).GetBind("fl", "rs", "Fields")
                    .Item("n1", "fl", A.Str("FLong")).Item("n2", "fl", A.Str("FLong")).Item("n3", "fl", A.Str("flong")).Item("o1", "fl", A.I4(1)).Item("o2", "fl", A.I2(1)).Item("o3", "fl", A.UI1(1))
                    .GetBind("g1", "fl", "Item", A.Str("FLong")).CallBind("g2", "fl", "Item", A.Str("FLong"));
                Names(p, "n1", "n2", "n3", "o1", "o2", "o3", "g1", "g2");
                p.Each("fl", "e", 8, b => b.Get("e", "Name"));
                p.Release("g2").Release("g1").Release("o3").Release("o2").Release("o1").Release("n3").Release("n2").Release("n1").Release("fl").Call("rs", "Close").Release("rs").FinishLite("db");
            });

        yield return Q("ident-across-movement", R("OR-OBS", "R-CURSOR-1"),
            "Object identity of a Field across movement and Close: on a snapshot of SELECT ID, FLong FROM W WHERE ID BETWEEN 300 AND 311, Fields(\"FLong\") retained as fa; after each of MoveNext, MoveLast, MoveFirst and Requery a fresh Fields and Item(\"FLong\") is read " +
            "and fa and the fresh Field have Value read; a Clone's Fields(\"FLong\") is read; Recordset.Close, then fa Name and Value, a fresh Fields read on the closed recordset; a second snapshot's Fields(\"FLong\"). All objects stay held. Asks which labels match and what the retained Field returns.",
            DaoFx, p =>
            {
                p.EngineLite().OpenDb().OpenRs("rs", "db", Sel300, Dao.OpenSnapshot).GetBind("fl", "rs", "Fields").Item("fa", "fl", A.Str("FLong")).Get("fa", "Value");
                int step = 0;
                foreach (string move in new[] { "MoveNext", "MoveLast", "MoveFirst", "Requery" })
                {
                    string c = $"fl{step}", f = $"fa{step}";
                    step++;
                    p.Call("rs", move).GetBind(c, "rs", "Fields").Item(f, c, A.Str("FLong")).Get("fa", "Value").Get(f, "Value");
                }
                p.CallBind("rc", "rs", "Clone").GetBind("flc", "rc", "Fields").Item("fac", "flc", A.Str("FLong")).Get("fac", "Value")
                    .Call("rs", "Close").Get("fa", "Name").Get("fa", "Value").GetBind("flx", "rs", "Fields")
                    .OpenRs("r2", "db", Sel300, Dao.OpenSnapshot).GetBind("fl2", "r2", "Fields").Item("fa2x", "fl2", A.Str("FLong")).Get("fa2x", "Value");
                p.Call("r2", "Close").Call("rc", "Close").FinishLite("db");
            });

        yield return Q("ident-tabledef-vs-recordset", R("OR-OBS", "R-CAT-1"),
            "Object identity across routes to the Field named ID of table W: db.TableDefs read twice, TableDefs.Item(\"W\") twice from the first and once from the second, TableDef.Fields twice, Item(\"ID\") on each, TableDef.OpenRecordset(snapshot).Fields.Item(\"ID\"), " +
            "db.OpenRecordset(\"W\", snapshot).Fields.Item(\"ID\"), and Each over TableDefs reading Name; every object held. Asks which labels coincide between TableDef, Recordset and repeated reads.",
            DaoFx, p =>
            {
                p.EngineLite().OpenDb().GetBind("s1", "db", "TableDefs").GetBind("s2", "db", "TableDefs").Item("t1", "s1", A.Str("W")).Item("t2", "s1", A.Str("W")).Item("t3", "s2", A.Str("W"))
                    .GetBind("tf1", "t1", "Fields").GetBind("tf2", "t1", "Fields").GetBind("tf3", "t3", "Fields")
                    .Item("ti1", "tf1", A.Str("ID")).Item("ti2", "tf2", A.Str("ID")).Item("ti3", "tf3", A.Str("ID"))
                    .CallBind("r1", "t1", "OpenRecordset", A.I4(Dao.OpenSnapshot)).GetBind("rf1", "r1", "Fields").Item("ri1", "rf1", A.Str("ID"))
                    .OpenRs("r2", "db", "W", Dao.OpenSnapshot).GetBind("rf2", "r2", "Fields").Item("ri2", "rf2", A.Str("ID"));
                Names(p, "t1", "t2", "t3", "ti1", "ti2", "ti3", "ri1", "ri2");
                p.Each("s1", "e", 64, b => b.Get("e", "Name"));
                p.Call("r1", "Close").Call("r2", "Close").FinishLite("db");
            });

        yield return Q("ident-properties", R("OR-OBS", "R-CAT-1"),
            "Object identity of Properties reads: Field(\"ID\").Properties read twice from the same Field and once from the Field reached through the TableDef; Properties.Item(\"Name\") twice, Item(0), and Each over Properties (Name, Type read); " +
            "Database.Properties twice with Item(\"Name\") twice; TableDef.Properties twice; Recordset.Properties once. All objects held. Asks which labels coincide and the property Count of each collection.",
            DaoFx, p =>
            {
                p.EngineLite().OpenDb().OpenRs("rs", "db", Sel300, Dao.OpenSnapshot).GetBind("fl", "rs", "Fields").Item("fa", "fl", A.Str("ID"))
                    .GetBind("p1", "fa", "Properties").GetBind("p2", "fa", "Properties").Get("p1", "Count").Get("p2", "Count")
                    .Item("pn1", "p1", A.Str("Name")).Item("pn2", "p1", A.Str("Name")).Item("pn3", "p2", A.Str("Name")).Item("po", "p1", A.I4(0))
                    .GetBind("tds", "db", "TableDefs").Item("td", "tds", A.Str("W")).GetBind("tfl", "td", "Fields").Item("tfa", "tfl", A.Str("ID")).GetBind("tp", "tfa", "Properties").Item("tpn", "tp", A.Str("Name"))
                    .GetBind("dp1", "db", "Properties").GetBind("dp2", "db", "Properties").Item("dn1", "dp1", A.Str("Name")).Item("dn2", "dp1", A.Str("Name")).Item("dn3", "dp2", A.Str("Name"))
                    .GetBind("tdp1", "td", "Properties").GetBind("tdp2", "td", "Properties").GetBind("rp", "rs", "Properties").Get("rp", "Count");
                Names(p, "pn1", "pn2", "pn3", "po", "tpn", "dn1", "dn2", "dn3");
                p.Each("p1", "e", 64, b => b.Get("e", "Name").Get("e", "Type"));
                p.Call("rs", "Close").FinishLite("db");
            });

        yield return Q("ident-collections", R("OR-OBS", "R-DAO-1"),
            "Object identity of engine, workspace and database collections: DBEngine.Workspaces twice, Workspaces(0) twice, Workspace.Databases twice, Databases(0) against the opened Database, Database.QueryDefs twice, Database.Relations twice, DBEngine.Errors twice, DBEngine.Properties twice; " +
            "a temporary QueryDef (SELECT ID FROM W) with Parameters, Fields and Properties each twice. All held. Asks which labels coincide, and the Count of each collection.",
            DaoFx, p =>
            {
                p.EngineLite().OpenDb().GetBind("ws1", "eng", "Workspaces").GetBind("ws2", "eng", "Workspaces").Item("w1", "ws1", A.I4(0)).Item("w2", "ws1", A.I4(0)).Item("w3", "ws2", A.I4(0))
                    .GetBind("dbs1", "w1", "Databases").GetBind("dbs2", "w1", "Databases").Get("dbs1", "Count").Item("d0", "dbs1", A.I4(0))
                    .GetBind("qd1", "db", "QueryDefs").GetBind("qd2", "db", "QueryDefs").GetBind("rl1", "db", "Relations").GetBind("rl2", "db", "Relations")
                    .GetBind("er1", "eng", "Errors").GetBind("er2", "eng", "Errors").GetBind("ep1", "eng", "Properties").GetBind("ep2", "eng", "Properties")
                    .CallBind("q", "db", "CreateQueryDef", A.Str(""), A.Str("SELECT ID FROM W")).GetBind("qp1", "q", "Parameters").GetBind("qp2", "q", "Parameters")
                    .GetBind("qf1", "q", "Fields").GetBind("qf2", "q", "Fields").GetBind("qr1", "q", "Properties").GetBind("qr2", "q", "Properties");
                foreach (string collection in new[] { "ws1", "dbs1", "qd1", "rl1", "er1", "ep1", "qp1", "qf1", "qr1" })
                {
                    p.Get(collection, "Count");
                }
                p.Call("q", "Close").Get("db", "Name").Get("d0", "Name").FinishLite("db");
            });

        yield return Q("ident-release-reget", R("OR-OBS"),
            "Object identity after release: Fields(\"ID\") of a snapshot read, held, then read a second time (both held); the first released, then Item(\"ID\") read a third time; the Fields collection released while a Field is held, Fields read again and Item(\"ID\") read; " +
            "Recordset.Close then Field Name and Value of a held Field. Asks the capture identity label of each object and whether labels are reused after a release, and what a held Field returns.",
            DaoFx, p =>
            {
                p.EngineLite().OpenDb().OpenRs("rs", "db", Sel300, Dao.OpenSnapshot).GetBind("fl", "rs", "Fields").Item("x1", "fl", A.Str("ID")).Item("x2", "fl", A.Str("ID"))
                    .Release("x1").Item("x3", "fl", A.Str("ID")).Release("fl").GetBind("fl2", "rs", "Fields").Item("x4", "fl2", A.Str("ID")).Get("x2", "Value").Get("x3", "Value").Get("x4", "Value")
                    .Call("rs", "Close").Get("x2", "Name").Get("x2", "Value").Get("x4", "Value");
                p.Release("x4").Release("x3").Release("x2").Release("fl2").Release("rs").FinishLite("db");
            });

        yield return Q("ident-clone-sharing", R("OR-OBS", "R-CURSOR-1"),
            "Object identity and state sharing between a Recordset and its Clone on a snapshot of SELECT ID, FLong FROM W WHERE ID BETWEEN 300 AND 311: Fields and Item(\"ID\") of each, Properties of each, Bookmark of each; " +
            "MoveLast on the original with Value of both IDs, MoveFirst on the clone with Value of both, Close of the original with the clone's Value, Name of each recordset. Asks the identity labels, bookmark arrays and values.",
            DaoFx, p =>
            {
                p.EngineLite().OpenDb().OpenRs("rs", "db", Sel300, Dao.OpenSnapshot).CallBind("rc", "rs", "Clone")
                    .GetBind("sf", "rs", "Fields").GetBind("cf", "rc", "Fields").Item("sid", "sf", A.Str("ID")).Item("cid", "cf", A.Str("ID"))
                    .GetBind("sp", "rs", "Properties").GetBind("cp", "rc", "Properties").Get("rs", "Bookmark").Get("rc", "Bookmark")
                    .Call("rs", "MoveLast").Get("sid", "Value").Get("cid", "Value").Call("rc", "MoveFirst").Get("sid", "Value").Get("cid", "Value")
                    .Get("rs", "Bookmark").Get("rc", "Bookmark").Call("rs", "Close").Get("cid", "Value").Get("rs", "Name").Get("rc", "Name");
                p.Call("rc", "Close").FinishLite("db");
            });
    }

    // ADO ---------------------------------------------------------------------------------------------------

    private static void AdoOpen(P p, bool readOnly)
    {
        p.Create("ADODB.Connection", "cn").Put("cn", "Provider", A.Str(Ace)).GetBind("cnp", "cn", "Properties").Item("cnds", "cnp", A.Str("Data Source")).Put("cnds", "Value", A.In())
            .Release("cnds").Release("cnp");
        if (readOnly)
        {
            p.Put("cn", "Mode", A.I4(1));
        }
        p.Call("cn", "Open").Get("cn", "State");
    }

    private static void AdoErrors(P p) =>
        p.GetBind("cne", "cn", "Errors")
            .Each("cne", "cnx", 16, b => b.Get("cnx", "Number").Get("cnx", "Description").Get("cnx", "Source").Get("cnx", "SQLState").Get("cnx", "NativeError"))
            .Release("cne");

    /// <summary>Client static read-only recordset of the cells of one column: field metadata, then GetRows(-1).</summary>
    private static void AdoColumnRows(P p, string rs, Col c)
    {
        p.Create("ADODB.Recordset", rs).Put(rs, "CursorLocation", A.I4(3))
            .Call(rs, "Open", A.Str($"SELECT ID, {c.Name} FROM W WHERE ID BETWEEN {c.Base} AND {c.Base + 99} ORDER BY ID"), A.Obj("cn"), A.I4(3), A.I4(1), A.I4(1)).Get(rs, "State")
            .GetBind(rs + "f", rs, "Fields")
            .Each(rs + "f", rs + "d", 8, b => b.Get(rs + "d", "Name").Get(rs + "d", "Type").Get(rs + "d", "DefinedSize").Get(rs + "d", "Precision").Get(rs + "d", "NumericScale").Get(rs + "d", "Attributes"))
            .Release(rs + "f").Call(rs, "GetRows", A.I4(-1)).Call(rs, "Close").Release(rs);
    }

    private static IEnumerable<Probe> AdoProbes()
    {
        yield return Q("ado-getrows-tags", R("R-ADO-1", "ADO-VALUES", "ADO-BULK"),
            "ADO read of the r-wire-dao cells (read-only ACE OLE DB connection): for each column of W a client static read-only recordset of SELECT ID, column FROM W WHERE ID BETWEEN base AND base+99 ORDER BY ID; " +
            "Field Name, Type, DefinedSize, Precision, NumericScale and Attributes, then GetRows(adGetRowsRest). Asks the ADO field type, the array bounds, each element's VARIANT tag, exact bits, BSTR bytes and byte arrays (including Null, NaN, infinities, odd BSTRs and Decimal), " +
            $"for comparison with the DAO route. Cells: {CellList}",
            DaoFx, p =>
            {
                p.Modules("before-create");
                AdoOpen(p, true);
                p.Modules("after-open");
                foreach (Col c in Cols)
                {
                    AdoColumnRows(p, "r" + c.Name, c);
                }
                AdoErrors(p);
                p.FinishLite("cn");
            });

        Col[] written = [.. Cols.Where(c => c.Name is "FSingle" or "FDouble" or "FCurr" or "FDate" or "FText" or "FMemo" or "FBin" or "FOle")];
        yield return Q("ado-write-values", R("R-ADO-1", "ADO-EDIT", "ADO-VALUES"),
            "ADO write of the r-wire cells for FSingle, FDouble, FCurr, FDate, FText, FMemo, FBin and FOle (read-write copy of r-wire-blank): a server keyset optimistic adCmdTable recordset on W, per cell AddNew, Fields(\"ID\").Value = id, Fields(column).Value = Arg, Update; " +
            "then, per column, a client static recordset of the stored cells read with field metadata and GetRows(adGetRowsRest). Asks per cell the HRESULT and error of Put and Update and the tag and exact value ADO returns.",
            BlankFx, p =>
            {
                AdoOpen(p, false);
                p.Create("ADODB.Recordset", "ars").Call("ars", "Open", A.Str("W"), A.Obj("cn"), A.I4(1), A.I4(3), A.I4(2)).GetBind("afl", "ars", "Fields").Item("aid", "afl", A.Str("ID"));
                foreach (Col c in written)
                {
                    p.Item("a" + c.Name, "afl", A.Str(c.Name));
                }
                foreach (Cell cell in Cells.Where(x => written.Contains(x.Col)))
                {
                    p.Call("ars", "AddNew").Put("aid", "Value", A.I4(cell.Id)).Put("a" + cell.Col.Name, "Value", cell.Value).Call("ars", "Update");
                }
                p.Release("aid");
                foreach (Col c in written)
                {
                    p.Release("a" + c.Name);
                }
                p.Release("afl").Call("ars", "Close").Release("ars");
                foreach (Col c in written)
                {
                    AdoColumnRows(p, "r" + c.Name, c);
                }
                AdoErrors(p);
                p.FinishLite("cn");
            });

        (string Col, int AdoType, int Size)[] types =
        [
            ("FByte", 17, 0), ("FInt", 2, 0), ("FLong", 3, 0), ("FSingle", 4, 0), ("FDouble", 5, 0), ("FCurr", 6, 0), ("FDate", 7, 0), ("FText", 202, 255), ("FMemo", 203, 10000),
            ("FBin", 204, 255), ("FOle", 205, 10000), ("FBool", 11, 0),
        ];
        yield return Q("ado-command-parameters", R("R-ADO-1", "ADO-PARAM", "ADO-VALUES"),
            "ADO Command parameter values for the r-wire cells (read-write copy of r-wire-blank): per column an ADODB.Command INSERT INTO W (ID, column) VALUES (?, ?) with CreateParameter pI (adInteger) and pV " +
            "(adUnsignedTinyInt, adSmallInt, adInteger, adSingle, adDouble, adCurrency, adDate, adVarWChar 255, adLongVarWChar, adVarBinary 255, adLongVarBinary, adBoolean for FByte..FBool), " +
            "pV.Value = the cell's Arg (tags as in the DAO cells), Execute with adExecuteNoRecords; then the stored cells read per column with field metadata and GetRows(adGetRowsRest). Asks per cell the HRESULT and error and the stored tag and value.",
            BlankFx, p =>
            {
                AdoOpen(p, false);
                foreach ((string colName, int adoType, int size) in types)
                {
                    Col c = Cols.Single(x => x.Name == colName);
                    string cmd = "m" + colName;
                    p.Create("ADODB.Command", cmd).PutRef(cmd, "ActiveConnection", "cn").Put(cmd, "CommandText", A.Str($"INSERT INTO W (ID, {colName}) VALUES (?, ?)")).Put(cmd, "CommandType", A.I4(1))
                        .GetBind(cmd + "ps", cmd, "Parameters")
                        .CallBind(cmd + "pi", cmd, "CreateParameter", A.Str("pI"), A.I4(3), A.I4(1)).Call(cmd + "ps", "Append", A.Obj(cmd + "pi"));
                    if (size > 0)
                    {
                        p.CallBind(cmd + "pv", cmd, "CreateParameter", A.Str("pV"), A.I4(adoType), A.I4(1), A.I4(size));
                    }
                    else
                    {
                        p.CallBind(cmd + "pv", cmd, "CreateParameter", A.Str("pV"), A.I4(adoType), A.I4(1));
                    }
                    p.Call(cmd + "ps", "Append", A.Obj(cmd + "pv"));
                    foreach (Cell cell in Cells.Where(x => x.Col == c))
                    {
                        p.Put(cmd + "pi", "Value", A.I4(cell.Id)).Put(cmd + "pv", "Value", cell.Value).Call(cmd, "Execute", A.Missing, A.Missing, A.I4(0x81));
                    }
                    p.Release(cmd + "pi").Release(cmd + "pv").Release(cmd + "ps").Release(cmd);
                }
                AdoErrors(p);
                foreach ((string colName, int _, int _) in types)
                {
                    AdoColumnRows(p, "r" + colName, Cols.Single(x => x.Name == colName));
                }
                p.FinishLite("cn");
            });
    }
}
