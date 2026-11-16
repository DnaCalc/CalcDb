using System.Text;

namespace CalcDb.Probes.Cards;

/// <summary>
/// R-COMPLEX-1 (multi-value, attachment, calculated and append-only history fields) and R-REBUILD-1 (compact and
/// repair), native discovery through ACE DAO (DAO.DBEngine.120, one STA per participant) plus ADODB with
/// Microsoft.ACE.OLEDB.12.0 where the ADO surface is the question. Fixtures are created by DAO in ACCDB (complex
/// kinds need it) and in both formats for compaction; every complex kind is added through
/// TableDef.CreateField(name, dbComplex*|dbAttachment) and its values are stored through Field2.Value as a child
/// recordset. Attachment source files are written by ADODB.Stream into the work directory. Compaction results
/// are reopened and enumerated in the same probe. The probes ask; they do not judge.
/// Deferred (not expressible with these programs): CX-FAIL and RB-FAIL (resource exhaustion, I/O failure and interruption),
/// RB-REPAIR on damaged files (no byte-writing driver), RB-OPAQUE stored front-end objects (Access only), byte-level attachment
/// wrapper variation, host replacement and volumes in RB-PUBLISH, the Access Compact and Repair lane, and ODBC.
/// </summary>
public sealed class RComplexRebuild : ICard
{
    private const string Card = "r-cplx-rebuild";
    private const string Pwd = "CalcDbProbe1";
    private const int Wait = 30000;

    private static string[] Cx(params string[] ids) => ["R-COMPLEX-1", .. ids];

    private static string[] Rb(params string[] ids) => ["R-REBUILD-1", .. ids];

    private static string Fx(string name) => $"{Card}-{name}";

    /// <summary>The multi-valued kinds: DataTypeEnum value and two typed child values each.</summary>
    internal static readonly (string Name, int Type, Arg First, Arg Second)[] Kinds =
    [
        ("Byte", 102, A.UI1(1), A.UI1(255)),
        ("Integer", 103, A.I2(-1), A.I2(short.MaxValue)),
        ("Long", 104, A.I4(-1), A.I4(int.MaxValue)),
        ("Single", 105, A.R4(1.5f), A.R4(-0.0f)),
        ("Double", 106, A.R8(0.1), A.R8(1e300)),
        ("Guid", 107, A.Str("{00112233-4455-6677-8899-AABBCCDDEEFF}"), A.Str("{FFFFFFFF-FFFF-FFFF-FFFF-FFFFFFFFFFFF}")),
        ("Decimal", 108, A.Str("12345.6789"), A.Str("-0.0001")),
        ("Text", 109, A.Str("alpha"), A.Str("B\u00e4\u20ac")),
    ];

    /// <summary>Calculated fields of the cx-calc fixture: name, DataTypeEnum, text size (0 for none), expression.</summary>
    internal static readonly (string Name, int Type, int Size, string Expression)[] CalcFields =
    [
        ("C_long", 4, 0, "[A]*2"),
        ("C_double", 7, 0, "[B]*2"),
        ("C_div", 7, 0, "[B]/[A]"),
        ("C_text", 10, 40, "[S] & \"-x\""),
        ("C_date", 8, 0, "DateAdd(\"d\",1,[D])"),
        ("C_bool", 1, 0, "[A]>1"),
        ("C_cur", 5, 0, "CCur([A])*1.1"),
        ("C_dec", 20, 0, "[A]/7"),
        ("C_iif", 10, 20, "IIf([A] Is Null,\"null\",\"set\")"),
    ];

    /// <summary>Rows read from the rich ACE fixtures, in a fixed order.</summary>
    internal static readonly string[] RichSelects =
    [
        "SELECT * FROM Parent ORDER BY ID",
        "SELECT * FROM Child ORDER BY ID",
        "SELECT * FROM W ORDER BY ID",
        "SELECT Name FROM Parent ORDER BY Name, ID",
        "SELECT ID, Val FROM Child ORDER BY Val, ID",
    ];

    internal static readonly string[] BloatSelects =
    [
        "SELECT COUNT(*) AS N, MAX(ID) AS MaxId, SUM(LEN(M)) AS L FROM B",
        "SELECT COUNT(*) AS K FROM Keep",
    ];

    private static (string, string)[] RichFixtures => [("main", Fx("rb-rich-accdb")), ("mdb", Fx("rb-rich-mdb"))];

    public IEnumerable<Probe> Probes()
    {
        // ------------------------------------------------------------------ R-COMPLEX-1 fixtures
        yield return Create("cx-mv-text", Cx("CX-DESC", "CX-READ", "CX-HOLD", "CX-EDIT"), "accdb",
            "table T(ID COUNTER primary key, Name, six TEXT(255) fillers) and one multi-valued Text field Tags (dbComplexText 109) added through CreateField/Fields.Append; 93 filler parents (a doubling insert of the three named rows), " +
            "parents p1 (alpha, beta), p2 (alpha; row grown by an update to 1,500 characters so that it overflows), p3 (empty group), p4 (same, same: two equal visible values), p5 (inserted then deleted) and p6 (inserted after the deletion; six); children are stored through Field2.Value as a child recordset under parent Edit",
            p =>
            {
                p.Sql("CREATE TABLE T (ID COUNTER CONSTRAINT PK_T PRIMARY KEY, Name TEXT(20), F1 TEXT(255), F2 TEXT(255), F3 TEXT(255), F4 TEXT(255), F5 TEXT(255), F6 TEXT(255))")
                    .AddField("T", "Tags", 109, "a")
                    .Sql("INSERT INTO T (Name) VALUES ('p1')").Sql("INSERT INTO T (Name) VALUES ('p2')").Sql("INSERT INTO T (Name) VALUES ('p3')");
                for (int i = 0; i < 5; i++)
                {
                    p.Sql("INSERT INTO T (Name) SELECT 'filler' FROM T");
                }
                string big = new('x', 250);
                p.Sql("INSERT INTO T (Name) VALUES ('p4')").Sql("INSERT INTO T (Name) VALUES ('p5')").Sql("DELETE FROM T WHERE Name = 'p5'").Sql("INSERT INTO T (Name) VALUES ('p6')")
                    .Sql($"UPDATE T SET F1 = '{big}', F2 = '{big}', F3 = '{big}', F4 = '{big}', F5 = '{big}', F6 = '{big}' WHERE Name = 'p2'")
                    .Fill("T", "p1", "Tags", A.Str("alpha"), A.Str("beta")).Fill("T", "p2", "Tags", A.Str("alpha"))
                    .Fill("T", "p4", "Tags", A.Str("same"), A.Str("same")).Fill("T", "p6", "Tags", A.Str("six"));
            });

        yield return Create("cx-types", Cx("CX-DESC", "CX-READ"), "accdb",
            "eight tables M<Kind>(ID COUNTER primary key, Name) each with one multi-valued field V created through CreateField with dbComplexByte 102, Integer 103, Long 104, Single 105, Double 106, GUID 107, Decimal 108 and Text 109; parent p1 with two typed child values, parent p2 with none",
            p =>
            {
                foreach ((string name, int type, Arg first, Arg second) in Kinds)
                {
                    p.Sql($"CREATE TABLE M{name} (ID COUNTER CONSTRAINT PK_M{name} PRIMARY KEY, Name TEXT(20))")
                        .AddField("M" + name, "V", type, "k" + name)
                        .Sql($"INSERT INTO M{name} (Name) VALUES ('p1')").Sql($"INSERT INTO M{name} (Name) VALUES ('p2')")
                        .Fill("M" + name, "p1", "V", first, second);
                }
            });

        yield return Create("cx-attach", Cx("CX-DESC", "CX-READ", "CX-ATT"), "accdb",
            "table T(ID COUNTER primary key, Name) with one attachment field Att (dbAttachment 101) added through CreateField; source files written by ADODB.Stream into the work directory (att-small.txt 5 bytes, att-page.txt 20,000 patterned bytes, att-noise.bin 20,000 pseudo-random bytes) and loaded through FileData.LoadFromFile: p1 (small), p2 (page-spanning plus small: two attachments), p3 (empty group), p4 (noise), p5 (inserted then deleted), p6 (small, after the deletion)",
            p =>
            {
                p.MakeFile("att-small.txt", Encoding.ASCII.GetBytes("hello")).MakeFile("att-page.txt", Pattern(20000)).MakeFile("att-noise.bin", Noise(20000, 1))
                    .Sql("CREATE TABLE T (ID COUNTER CONSTRAINT PK_T PRIMARY KEY, Name TEXT(20))")
                    .AddField("T", "Att", 101, "a")
                    .Sql("INSERT INTO T (Name) VALUES ('p1')").Sql("INSERT INTO T (Name) VALUES ('p2')").Sql("INSERT INTO T (Name) VALUES ('p3')")
                    .Sql("INSERT INTO T (Name) VALUES ('p4')").Sql("INSERT INTO T (Name) VALUES ('p5')").Sql("DELETE FROM T WHERE Name = 'p5'").Sql("INSERT INTO T (Name) VALUES ('p6')")
                    .FillAtt("T", "p1", "Att", "att-small.txt").FillAtt("T", "p2", "Att", "att-page.txt", "att-small.txt")
                    .FillAtt("T", "p4", "Att", "att-noise.bin").FillAtt("T", "p6", "Att", "att-small.txt");
            });

        yield return Create("cx-calc", Cx("CX-CALC"), "accdb",
            "table T(ID COUNTER primary key, A LONG, B DOUBLE, S TEXT(30), D DATETIME) and nine calculated fields added through CreateField with Field.Expression set before Fields.Append " +
            "(C_long, C_double, C_div, C_text, C_date, C_bool, C_cur, C_dec, C_iif); rows (10, 1.5, 'abc', 2024-01-02), a row of Nulls, (-5, 0, '', 1900-01-01) and (7, -2.5, 'x', 2000-02-29)",
            p =>
            {
                p.Sql("CREATE TABLE T (ID COUNTER CONSTRAINT PK_T PRIMARY KEY, A LONG, B DOUBLE, S TEXT(30), D DATETIME)");
                foreach ((string name, int type, int size, string expression) in CalcFields)
                {
                    p.AddCalc("T", name, type, size, expression, name);
                }
                p.Sql("INSERT INTO T (A, B, S, D) VALUES (10, 1.5, 'abc', #2024-01-02#)").Sql("INSERT INTO T (S) VALUES (Null)")
                    .Sql("INSERT INTO T (A, B, S, D) VALUES (-5, 0, '', #1900-01-01#)").Sql("INSERT INTO T (A, B, S, D) VALUES (7, -2.5, 'x', #2000-02-29#)");
            });

        yield return Create("cx-history", Cx("CX-HISTORY"), "accdb",
            "table T(ID COUNTER primary key, Title) with a Memo field Notes (CreateField dbMemo 12) and the user property AppendOnly (dbBoolean, True) created through CreateProperty/Properties.Append; " +
            "three rows: ID 1 changed three times (v2, v3, v4) by UPDATE, ID 2 changed twice, ID 3 never",
            p => p.Sql("CREATE TABLE T (ID COUNTER CONSTRAINT PK_T PRIMARY KEY, Title TEXT(20))")
                .GetBind("ts", "db", "TableDefs").Item("td", "ts", A.Str("T")).CallBind("fn", "td", "CreateField", A.Str("Notes"), A.I4(12))
                .GetBind("tf", "td", "Fields").Call("tf", "Append", A.Obj("fn"))
                .CallBind("pr", "fn", "CreateProperty", A.Str("AppendOnly"), A.I4(1), A.Bool(true))
                .GetBind("pps", "fn", "Properties").Call("pps", "Append", A.Obj("pr"))
                .Release("pr").Release("pps").Release("tf").Release("fn").Release("td").Release("ts")
                .Sql("INSERT INTO T (Title, Notes) VALUES ('one', 'v1')").Sql("INSERT INTO T (Title, Notes) VALUES ('two', 'w1')").Sql("INSERT INTO T (Title, Notes) VALUES ('three', 't1')")
                .Sql("UPDATE T SET Notes = 'v2' WHERE ID = 1").Sql("UPDATE T SET Notes = 'v3' WHERE ID = 1").Sql("UPDATE T SET Notes = 'v4' WHERE ID = 1")
                .Sql("UPDATE T SET Notes = 'w2' WHERE ID = 2").Sql("UPDATE T SET Notes = 'w3' WHERE ID = 2"));

        // ------------------------------------------------------------------ R-REBUILD-1 fixtures
        foreach (string ext in new[] { "accdb", "mdb" })
        {
            yield return Create("rb-rich-" + ext, Rb("RB-CLEAN", "RB-RELOC", "RB-OPAQUE"), ext,
                "ordinary rows, repeated equal values, long values and several indexes: Parent (AutoNumber key, unique Code, Name, Memo of 20,000 and 5,000 characters, Currency, Date, Yes/No; 20 rows, c20 deleted and reinserted), " +
                "Child (AutoNumber key, foreign key to Parent, indexed Val, a 5,000-byte Long Binary on row 1; 64 rows then IDs above 50 deleted so that the counter exceeds the maximum key), " +
                "W (64 rows of six TEXT(255); row 32 grown to 1,500 characters by an update so that it overflows), index IX_Parent_Name_Born, saved queries QParam (PARAMETERS) and QJoin, " +
                "a database property CalcDbRb, a table Description on Parent and a field Description on Parent.Name",
                RichBody);

            yield return Create("rb-bloat-" + ext, Rb("RB-CLEAN", "RB-RELOC"), ext,
                "free-space history: table B(ID COUNTER primary key, S, M MEMO) filled to 1,024 rows of a 2,000-character memo by doubling, then every row above ID 40 deleted (a large free region), and table Keep with three rows",
                p => p.Sql("CREATE TABLE B (ID COUNTER CONSTRAINT PK_B PRIMARY KEY, S TEXT(50), M MEMO)").Sql("CREATE TABLE Keep (ID COUNTER CONSTRAINT PK_Keep PRIMARY KEY, V TEXT(20))")
                    .Sql($"INSERT INTO B (S, M) VALUES ('row', '{new string('m', 2000)}')")
                    .Repeat(10, "INSERT INTO B (S, M) SELECT S, M FROM B").Sql("DELETE FROM B WHERE ID > 40")
                    .Sql("INSERT INTO Keep (V) VALUES ('k1')").Sql("INSERT INTO Keep (V) VALUES ('k2')").Sql("INSERT INTO Keep (V) VALUES ('k3')"));
        }

        // ------------------------------------------------------------------ R-COMPLEX-1 reads
        yield return Use("read-cx-mv-text", "cx-mv-text", Cx("CX-DESC", "CX-READ"),
            "Opens the multi-value fixture read-only and asks: database and TableDef metadata, every Field2 property of T (including IsComplex and each Properties entry), the TypeInfo of the Tags Field2 and of the child recordset it returns, " +
            "the child recordset's own properties, its Fields (all Field2 properties), cursor movement and RecordCount before and after MoveLast, ParentRecordset, then the group of each parent (p1..p6, a filler, the deleted p5) read to EOF; " +
            "and the catalog: MSysComplexColumns and MSysObjects rows.",
            p =>
            {
                p.Engine().OpenDb().Files("after-open").Meta("db", "T").Catalog("a");
                p.OpenRs("rs", "db", "SELECT * FROM T WHERE Name = 'p1'", Dao.OpenDynaset).GetBind("fl", "rs", "Fields").Item("ft", "fl", A.Str("Tags")).Describe("ft")
                    .GetBind("ch", "ft", "Value").Describe("ch")
                    .Get("ch", "Name").Get("ch", "Type").Get("ch", "Updatable").Get("ch", "Transactions").Get("ch", "Bookmarkable").Get("ch", "RecordCount").Get("ch", "BOF").Get("ch", "EOF")
                    .GetBind("cfl", "ch", "Fields").Each("cfl", "cf", 8, f => f.FieldFull("cf")).Release("cfl")
                    .Call("ch", "MoveLast").Get("ch", "RecordCount").Get("ch", "AbsolutePosition").Call("ch", "MoveFirst").Get("ch", "PercentPosition")
                    .GetBind("par", "ch", "ParentRecordset").Get("par", "Name").Get("par", "RecordCount").Cell("par", "Name", "pa").Release("par")
                    .Rows("ch", 16, 8).Call("ch", "Close").Release("ch").Release("ft").Release("fl").Call("rs", "Close").Release("rs");
                foreach (string name in new[] { "p1", "p2", "p3", "p4", "p5", "p6", "filler" })
                {
                    p.Group("db", "T", name, "Tags", name);
                }
                p.Snap("db", "pl", "SELECT ID, Name, LEN(F1) AS L1 FROM T WHERE Name <> 'filler' ORDER BY ID", 16, 8).Finish("db");
            });

        yield return Use("read-cx-types", "cx-types", Cx("CX-DESC", "CX-READ"),
            "Opens the eight single-kind fixtures read-only and asks: metadata of each table and its Field2 properties (Type, IsComplex, Size, Properties), the catalog (MSysComplexColumns, MSysObjects, including shared type tables), and the group of p1 (two values) and p2 (none) read to EOF with each child field's Name, Type and typed Value.",
            p =>
            {
                p.Engine().OpenDb().Files("after-open").Meta("db", [.. Kinds.Select(k => "M" + k.Name)]).Catalog("t");
                foreach ((string name, _, _, _) in Kinds)
                {
                    p.Group("db", "M" + name, "p1", "V", name + "1").Group("db", "M" + name, "p2", "V", name + "2");
                }
                p.Finish("db");
            });

        yield return Use("read-cx-attach", "cx-attach", Cx("CX-DESC", "CX-READ", "CX-ATT"),
            "Opens the attachment fixture read-only and asks: metadata and Field2 properties of T.Att, the TypeInfo of the Att Field2 and of the child recordset and its FileData field, the catalog rows, the group of every parent read to EOF " +
            "(FileData, FileName, FileType and the other child fields with typed values), then FieldSize, GetChunk(0,16) and SaveToFile of the FileData of p1, the two of p2 and p4 into work files (kept, with file images).",
            p =>
            {
                p.Engine().OpenDb().Files("after-open").Meta("db", "T").Catalog("a");
                p.OpenRs("rs", "db", "SELECT * FROM T WHERE Name = 'p2'", Dao.OpenDynaset).GetBind("fl", "rs", "Fields").Item("ft", "fl", A.Str("Att")).Describe("ft")
                    .GetBind("ch", "ft", "Value").Describe("ch").GetBind("cfl", "ch", "Fields").Each("cfl", "cf", 12, f => f.FieldFull("cf")).Release("cfl")
                    .Call("ch", "Close").Release("ch").Release("ft").Release("fl").Call("rs", "Close").Release("rs");
                foreach (string name in new[] { "p1", "p2", "p3", "p4", "p6" })
                {
                    p.Group("db", "T", name, "Att", name);
                }
                p.Export("db", "T", "p1", "Att", 0, "export-p1.bin", "e1").Export("db", "T", "p2", "Att", 0, "export-p2-first.bin", "e2")
                    .Export("db", "T", "p2", "Att", 1, "export-p2-second.bin", "e3").Export("db", "T", "p4", "Att", 0, "export-p4.bin", "e4")
                    .Files("after-export", keep: true).Finish("db");
            });

        yield return new Probe(Card, "read-cx-sql-surface", Cx("CX-READ"),
            "Runs, through DAO snapshots on the multi-value and attachment fixtures opened read-only, the SQL forms that reach complex fields: the complex column itself, <field>.Value, <field>.FileName/FileType/FileFlags/FileTimeStamp/FileURL/FileData, SELECT *, a predicate on Tags.Value, IS NULL, GROUP BY Tags.Value and COUNT; every statement's rows, value types and errors are recorded.",
            p =>
            {
                p.Engine().OpenDb().OpenDb("da", "att");
                string[] mv =
                [
                    "SELECT ID, Name, Tags FROM T WHERE Name = 'p1'",
                    "SELECT ID, Name, Tags.Value FROM T WHERE Name IN ('p1','p2','p3','p4','p6') ORDER BY ID",
                    "SELECT Name FROM T WHERE Tags.Value = 'alpha' ORDER BY ID",
                    "SELECT COUNT(*) AS N FROM T WHERE Tags.Value IS NULL",
                    "SELECT Tags.Value, COUNT(*) AS N FROM T GROUP BY Tags.Value",
                    "SELECT * FROM T WHERE Name = 'p1'",
                    "SELECT Tags.Value FROM T WHERE Name = 'p4'",
                ];
                for (int i = 0; i < mv.Length; i++)
                {
                    p.Snap("db", "m" + i, mv[i], 32, 12);
                }
                string[] att =
                [
                    "SELECT Name, Att.FileName, Att.FileType FROM T WHERE Name IN ('p1','p2','p3','p4','p6') ORDER BY ID",
                    "SELECT Name, Att.FileName, Att.FileType, Att.FileFlags, Att.FileTimeStamp, Att.FileURL FROM T WHERE Name = 'p2'",
                    "SELECT Name, Att.FileData FROM T WHERE Name = 'p1'",
                    "SELECT COUNT(Att.FileName) AS N FROM T",
                    "SELECT Name, Att FROM T WHERE Name = 'p2'",
                ];
                for (int i = 0; i < att.Length; i++)
                {
                    p.Snap("da", "a" + i, att[i], 32, 12);
                }
                p.Call("da", "Close").Call("db", "Close").Modules("after-close").Files("after-close");
            })
        { Fixtures = [("main", Fx("cx-mv-text")), ("att", Fx("cx-attach"))] };

        yield return new Probe(Card, "read-cx-ado", Cx("CX-READ", "CX-ATT"),
            "Through ADODB.Connection (Provider Microsoft.ACE.OLEDB.12.0, Data Source through Properties, Mode adModeRead) on the multi-value and attachment fixtures asks: Connection.Execute of SELECT of the complex column (Field Type, Value, the child recordset bound from Field.Value, its State, Fields, rows to EOF), " +
            "SELECT <field>.Value, and for attachments the FileName, FileType and FileData values with their ADO VARIANT types.",
            p =>
            {
                p.Files("before-open").Modules("before-create").AdoOpen("cn", "main").AdoOpen("cm", "att").Modules("after-create");
                p.CallBind("a1", "cn", "Execute", A.Str("SELECT ID, Name, Tags FROM T WHERE Name = 'p1'")).Rows("a1", 4, 8)
                    .Call("a1", "Close").Release("a1")
                    .CallBind("a2", "cn", "Execute", A.Str("SELECT ID, Name, Tags FROM T WHERE Name = 'p1'")).GetBind("a2f", "a2", "Fields").Item("a2t", "a2f", A.Str("Tags"))
                    .Get("a2t", "Type").Get("a2t", "Attributes").GetBind("a2c", "a2t", "Value").Get("a2c", "State").Get("a2c", "RecordCount").Rows("a2c", 8, 8)
                    .Call("a2c", "Close").Release("a2c").Release("a2t").Release("a2f").Call("a2", "Close").Release("a2")
                    .CallBind("a3", "cn", "Execute", A.Str("SELECT ID, Tags.Value FROM T WHERE Name IN ('p1','p2','p3','p4','p6') ORDER BY ID")).Rows("a3", 16, 4)
                    .Call("a3", "Close").Release("a3")
                    .CallBind("a4", "cm", "Execute", A.Str("SELECT Name, Att.FileName, Att.FileType, Att.FileData FROM T WHERE Name IN ('p1','p2') ORDER BY ID")).Rows("a4", 8, 8)
                    .Call("a4", "Close").Release("a4")
                    .CallBind("a5", "cm", "Execute", A.Str("SELECT ID, Name, Att FROM T WHERE Name = 'p2'")).GetBind("a5f", "a5", "Fields").Item("a5t", "a5f", A.Str("Att"))
                    .Get("a5t", "Type").GetBind("a5c", "a5t", "Value").Get("a5c", "State").Rows("a5c", 8, 12)
                    .Call("a5c", "Close").Release("a5c").Release("a5t").Release("a5f").Call("a5", "Close").Release("a5")
                    .Call("cn", "Close").Call("cm", "Close").Release("cn").Release("cm").Modules("after-close").Files("after-close");
            })
        { Fixtures = [("main", Fx("cx-mv-text")), ("att", Fx("cx-attach"))] };

        // ------------------------------------------------------------------ CX-HOLD
        const string FourParents = "SELECT * FROM T WHERE Name IN ('p1','p2','p3','p4') ORDER BY ID";
        yield return Change("hold-surfaces-parent-movement", Cx("CX-HOLD"),
            "Holds the child recordset of p1 (c1), a Clone of it (positioned on its last row) and the Tags Field2 of the parent recordset; then moves the parent MoveNext, MoveNext, MoveLast, MoveFirst and after each move " +
            "records the parent's Name, then RecordCount, BOF, EOF, Bookmark and current row of c1 and of the clone, the group obtained again from the held Field2.Value (same fields), and the Name of c1.ParentRecordset.",
            p =>
            {
                p.Hold(FourParents).Peek("c1").CallBind("cl", "c1", "Clone").Call("cl", "MoveLast").Peek("cl");
                string[] moves = ["MoveNext", "MoveNext", "MoveLast", "MoveFirst"];
                for (int i = 0; i < moves.Length; i++)
                {
                    p.Call("rs", moves[i]).Cell("rs", "Name", "m" + i).Peek("c1").Peek("cl").GetBind("n" + i, "ft", "Value").Peek("n" + i)
                        .GetBind("pr" + i, "c1", "ParentRecordset").Cell("pr" + i, "Name", "q" + i).Release("pr" + i).Call("n" + i, "Close").Release("n" + i);
                }
                p.Call("cl", "Close").Shut("c1", "rs", "db");
            });

        yield return Change("hold-parent-requery-delete-close", Cx("CX-HOLD"),
            "Holds the child recordset c1 of p1, then in order: parent Requery, Delete of the current parent row, MoveNext, an INSERT that recreates a parent named p1, Requery, a fresh read of the new p1's group, " +
            "parent Close, release of the parent recordset object, Close of the Database, release of the Database object; after each step c1's RecordCount, BOF, EOF, Bookmark and current row (or error) are recorded.",
            p =>
            {
                p.Hold(FourParents).Peek("c1").Call("rs", "Requery").Peek("c1").Cell("rs", "Name", "a")
                    .Call("rs", "Delete").Errors().Peek("c1").Call("rs", "MoveNext").Cell("rs", "Name", "b").Peek("c1")
                    .Sql("INSERT INTO T (Name) VALUES ('p1')").Peek("c1").Call("rs", "Requery").Peek("c1").Group("db", "T", "p1", "Tags", "h")
                    .Call("rs", "Close").Peek("c1").GetBind("pr", "c1", "ParentRecordset").Release("pr")
                    .Release("rs").Peek("c1").Call("db", "Close").Peek("c1").Release("db").Peek("c1")
                    .Call("c1", "Close").Release("c1").Modules("after-close").Files("after-close", keep: true);
            });

        yield return Change("hold-release-orders", Cx("CX-HOLD"),
            "Four independent open-hold-release sequences on p1's group in one process, each followed by a fresh Database that reads the group: (a) release child, parent recordset and Database objects with no Close; " +
            "(b) Close parent and Database, read the held child, Close the child, release; (c) release the Database then the parent recordset, read the held child, release it; (d) Close child, parent, Database in that order.",
            p =>
            {
                p.Engine();
                p.HeldSeq("a").Release("ca").Release("fa").Release("la").Release("ra").Release("da").Reread("a");
                p.HeldSeq("b").Call("rb", "Close").Call("db", "Close").Peek("cb").Call("cb", "Close").Errors().Release("cb").Release("fb").Release("lb").Release("rb").Release("db").Reread("b");
                p.HeldSeq("c").Release("dc").Release("rc").Peek("cc").Release("cc").Release("fc").Release("lc").Reread("c");
                p.HeldSeq("d").Call("cd", "Close").Call("rd", "Close").Call("dd", "Close").Errors().Release("cd").Release("fd").Release("ld").Release("rd").Release("dd").Reread("d");
                p.Modules("after-close").Files("after-close", keep: true);
            });

        // ------------------------------------------------------------------ CX-EDIT
        yield return Change("edit-child-admission", Cx("CX-EDIT"),
            "On p1's child recordset: (a) with the parent not in an edit state, AddNew/Value/Update then CancelUpdate; (b) after parent Edit, AddNew/Update with a 300-character value, a Null value, a Byte() value and no value at all, each followed by CancelUpdate, then parent CancelUpdate; " +
            "the group is read fresh after (a) and after (b); DBEngine.Errors is read after every failure.",
            p =>
            {
                p.Hold("SELECT * FROM T WHERE Name = 'p1'").ChildField("c1", "x")
                    .Call("c1", "AddNew").Put("cvx", "Value", A.Str("no-parent-edit")).Call("c1", "Update").Errors().Call("c1", "CancelUpdate").Errors()
                    .Peek("c1").Group("db", "T", "p1", "Tags", "a")
                    .Call("rs", "Edit")
                    .Call("c1", "AddNew").Put("cvx", "Value", A.Str(new string('L', 300))).Call("c1", "Update").Errors().Call("c1", "CancelUpdate")
                    .Call("c1", "AddNew").Put("cvx", "Value", A.Null).Call("c1", "Update").Errors().Call("c1", "CancelUpdate")
                    .Call("c1", "AddNew").Put("cvx", "Value", A.Bytes(1, 2, 3)).Call("c1", "Update").Errors().Call("c1", "CancelUpdate")
                    .Call("c1", "AddNew").Call("c1", "Update").Errors().Call("c1", "CancelUpdate")
                    .Call("rs", "CancelUpdate").Errors().Peek("c1").Group("db", "T", "p1", "Tags", "b").Shut("c1", "rs", "db");
            });

        yield return Change("edit-parent-update-vs-cancel", Cx("CX-EDIT"),
            "The same child change under two parent endings: p1 Edit, child AddNew 'new-update' and child Update, parent Update; p2 Edit, child AddNew 'new-cancel' and child Update, parent CancelUpdate; " +
            "the child state is read after each step and the groups of p1 and p2 are read fresh at the end.",
            p =>
            {
                p.Hold("SELECT * FROM T WHERE Name IN ('p1','p2') ORDER BY ID").ChildField("c1", "1")
                    .Call("rs", "Edit").Call("c1", "AddNew").Put("cv1", "Value", A.Str("new-update")).Call("c1", "Update").Peek("c1").Call("rs", "Update").Errors().Peek("c1")
                    .Call("rs", "MoveNext").GetBind("c2", "ft", "Value").ChildField("c2", "2")
                    .Call("rs", "Edit").Call("c2", "AddNew").Put("cv2", "Value", A.Str("new-cancel")).Call("c2", "Update").Peek("c2").Call("rs", "CancelUpdate").Errors().Peek("c2")
                    .Call("c2", "Close").Group("db", "T", "p1", "Tags", "u1").Group("db", "T", "p2", "Tags", "u2").Shut("c1", "rs", "db");
            });

        yield return Change("edit-child-edit-delete", Cx("CX-EDIT"),
            "p1 under parent Edit: child MoveFirst, Edit, Value changed, Update; MoveNext, Delete; parent Update; the group is read fresh. Then p4 without any parent Edit: child Edit, Value changed, Update; Delete; the group is read fresh. Errors are recorded after each step.",
            p =>
            {
                p.Hold("SELECT * FROM T WHERE Name = 'p1'").ChildField("c1", "x")
                    .Call("rs", "Edit").Call("c1", "MoveFirst").Call("c1", "Edit").Put("cvx", "Value", A.Str("changed")).Call("c1", "Update").Errors()
                    .Call("c1", "MoveNext").Call("c1", "Delete").Errors().Peek("c1").Call("rs", "Update").Errors().Peek("c1")
                    .Group("db", "T", "p1", "Tags", "e1")
                    .OpenRs("rs4", "db", "SELECT * FROM T WHERE Name = 'p4'", Dao.OpenDynaset).GetBind("fl4", "rs4", "Fields").Item("ft4", "fl4", A.Str("Tags")).GetBind("c4", "ft4", "Value").ChildField("c4", "4")
                    .Call("c4", "Edit").Put("cv4", "Value", A.Str("changed4")).Call("c4", "Update").Errors().Call("c4", "CancelUpdate")
                    .Call("c4", "MoveFirst").Call("c4", "Delete").Errors().Peek("c4").Call("c4", "Close").Call("rs4", "Close")
                    .Group("db", "T", "p4", "Tags", "e4").Shut("c1", "rs", "db");
            });

        yield return Change("edit-parent-addnew-counter", Cx("CX-EDIT"),
            "Parent AddNew of 'p7' (its ID is read at once), a child AddNew/Update under the unsaved parent, parent Update; then AddNew of 'p8' (ID read) with CancelUpdate; then AddNew of 'p9' (ID read) with Update; " +
            "the IDs of p7..p9 and the groups of p7 and p9 are read fresh.",
            p =>
            {
                p.Engine().OpenRw().OpenRs("rs", "db", "SELECT * FROM T WHERE ID < 4", Dao.OpenDynaset).GetBind("fl", "rs", "Fields")
                    .Item("fid", "fl", A.Str("ID")).Item("fnm", "fl", A.Str("Name")).Item("ft", "fl", A.Str("Tags"))
                    .Call("rs", "AddNew").Put("fnm", "Value", A.Str("p7")).Get("fid", "Value")
                    .GetBind("c7", "ft", "Value").ChildField("c7", "7").Call("c7", "AddNew").Put("cv7", "Value", A.Str("n1")).Call("c7", "Update").Errors().Peek("c7")
                    .Call("rs", "Update").Errors().Peek("c7").Call("c7", "Close")
                    .Call("rs", "AddNew").Put("fnm", "Value", A.Str("p8")).Get("fid", "Value").Call("rs", "CancelUpdate")
                    .Call("rs", "AddNew").Put("fnm", "Value", A.Str("p9")).Get("fid", "Value").Call("rs", "Update").Errors()
                    .Snap("db", "ids", "SELECT ID, Name FROM T WHERE Name IN ('p7','p8','p9') ORDER BY ID", 8, 4)
                    .Group("db", "T", "p7", "Tags", "g7").Group("db", "T", "p9", "Tags", "g9").Shut("rs", "db");
            });

        yield return Change("edit-workspace-transactions", Cx("CX-EDIT", "CX-HOLD"),
            "In Workspaces(0): BeginTrans, p1 Edit, child AddNew 'tx-rollback' and Update, parent Update, held child read, Rollback, held child and fresh group read; " +
            "then BeginTrans twice (nested), the same change as 'tx-inner' on p2, CommitTrans of the inner level and Rollback of the outer, groups read; then BeginTrans, 'tx-commit' on p2, CommitTrans, group read.",
            p =>
            {
                p.Hold("SELECT * FROM T WHERE Name IN ('p1','p2') ORDER BY ID").ChildField("c1", "1").GetBind("ws", "eng", "Workspaces", A.I4(0))
                    .Call("ws", "BeginTrans").Call("rs", "Edit").Call("c1", "AddNew").Put("cv1", "Value", A.Str("tx-rollback")).Call("c1", "Update").Errors().Call("rs", "Update").Errors().Peek("c1")
                    .Call("ws", "Rollback").Errors().Peek("c1").Group("db", "T", "p1", "Tags", "r1")
                    .Call("rs", "MoveNext").GetBind("c2", "ft", "Value").ChildField("c2", "2")
                    .Call("ws", "BeginTrans").Call("ws", "BeginTrans").Call("rs", "Edit").Call("c2", "AddNew").Put("cv2", "Value", A.Str("tx-inner")).Call("c2", "Update").Call("rs", "Update").Errors()
                    .Call("ws", "CommitTrans").Errors().Peek("c2").Group("db", "T", "p2", "Tags", "n1").Call("ws", "Rollback").Errors().Peek("c2").Group("db", "T", "p2", "Tags", "n2")
                    .Call("ws", "BeginTrans").Call("rs", "Edit").Call("c2", "AddNew").Put("cv2", "Value", A.Str("tx-commit")).Call("c2", "Update").Call("rs", "Update").Errors()
                    .Call("ws", "CommitTrans").Errors().Peek("c2").Group("db", "T", "p2", "Tags", "c1").Call("c2", "Close").Shut("c1", "rs", "db");
            });

        // ------------------------------------------------------------------ CX-ATT
        yield return new Probe(Card, "att-load-variants", Cx("CX-ATT"),
            "On the empty group of p3, ten work files written by ADODB.Stream (9,000 patterned bytes named ext.txt, ext.TXT, ext.zip, ext.ZIP, ext.jpg, ext.JPG, ext-noext and ext.bin; empty.txt of 0 bytes; noise.bin of 9,000 pseudo-random bytes) " +
            "are each loaded with child AddNew, FileData.LoadFromFile and Update under one parent Edit; the group is then read fresh (FileName, FileType, FileFlags, FileTimeStamp, FileData) and the closed file image is kept.",
            p =>
            {
                string[] names = ["ext.txt", "ext.TXT", "ext.zip", "ext.ZIP", "ext.jpg", "ext.JPG", "ext-noext", "ext.bin"];
                p.Engine();
                foreach (string n in names)
                {
                    p.MakeFile(n, Pattern(9000));
                }
                p.MakeFile("empty.txt", []).MakeFile("noise.bin", Noise(9000, 7)).Files("after-files");
                p.OpenRw().OpenRs("rs", "db", "SELECT * FROM T WHERE Name = 'p3'", Dao.OpenDynaset).GetBind("fl", "rs", "Fields").Item("ft", "fl", A.Str("Att"))
                    .Call("rs", "Edit").GetBind("ch", "ft", "Value").GetBind("chf", "ch", "Fields").Item("fd", "chf", A.Str("FileData"));
                foreach (string n in names.Append("empty.txt").Append("noise.bin"))
                {
                    p.Call("ch", "AddNew").Call("fd", "LoadFromFile", A.Out(n)).Call("ch", "Update").Errors();
                }
                p.Call("rs", "Update").Errors().Release("fd").Release("chf").Call("ch", "Close").Release("ch").Release("ft").Release("fl")
                    .Group("db", "T", "p3", "Att", "v", 16).Shut("rs", "db");
            })
        { Fixtures = [("main", Fx("cx-attach"))] };

        yield return new Probe(Card, "att-replace-remove", Cx("CX-ATT"),
            "On p1: under parent Edit the first attachment is replaced (child Edit, FileData.LoadFromFile of replace.bin, FileName set to 'renamed.dat', Update); a second is added by assigning FileData bytes and FileName directly; the group is read; " +
            "the first attachment is deleted and the group read; then both attachments of p2 are deleted and its group read. Closed file image kept.",
            p =>
            {
                p.Engine().MakeFile("replace.bin", Pattern(5000)).OpenRw();
                p.OpenRs("rs", "db", "SELECT * FROM T WHERE Name = 'p1'", Dao.OpenDynaset).GetBind("fl", "rs", "Fields").Item("ft", "fl", A.Str("Att"))
                    .Call("rs", "Edit").GetBind("ch", "ft", "Value").GetBind("chf", "ch", "Fields").Item("fd", "chf", A.Str("FileData")).Item("fnm", "chf", A.Str("FileName"))
                    .Call("ch", "MoveFirst").Call("ch", "Edit").Call("fd", "LoadFromFile", A.Out("replace.bin")).Put("fnm", "Value", A.Str("renamed.dat")).Call("ch", "Update").Errors()
                    .Call("ch", "AddNew").Put("fd", "Value", A.Bytes(1, 2, 3)).Put("fnm", "Value", A.Str("bytes.bin")).Call("ch", "Update").Errors()
                    .Call("rs", "Update").Errors().Group("db", "T", "p1", "Att", "r1");
                p.Call("rs", "Edit").Call("ch", "MoveFirst").Call("ch", "Delete").Errors().Call("rs", "Update").Errors().Group("db", "T", "p1", "Att", "r2")
                    .Release("fd").Release("fnm").Release("chf").Call("ch", "Close").Release("ch").Release("ft").Release("fl").Call("rs", "Close").Release("rs")
                    .OpenRs("r2", "db", "SELECT * FROM T WHERE Name = 'p2'", Dao.OpenDynaset).GetBind("fl2", "r2", "Fields").Item("ft2", "fl2", A.Str("Att"))
                    .Call("r2", "Edit").GetBind("c2", "ft2", "Value").Call("c2", "MoveFirst").Call("c2", "Delete").Call("c2", "MoveFirst").Call("c2", "Delete").Errors()
                    .Call("r2", "Update").Errors().Call("c2", "Close").Call("r2", "Close").Group("db", "T", "p2", "Att", "r3").Shut("db");
            })
        { Fixtures = [("main", Fx("cx-attach"))] };

        yield return new Probe(Card, "att-file-failures", Cx("CX-ATT"),
            "File-operation outcomes beside the database outcome: p1's FileData is saved to a new work file; saved onto a work file that already exists (written by ADODB.Stream); LoadFromFile of a source that does not exist; " +
            "SaveToFile on the child of the empty group p3; LoadFromFile whose source is the open database file itself; SaveToFile whose destination is the open database file. Files are observed after each step; the group is read at the end.",
            p =>
            {
                p.Engine().MakeFile("exists.bin", Encoding.ASCII.GetBytes("pre-existing")).OpenRw();
                p.OpenRs("rs", "db", "SELECT * FROM T WHERE Name = 'p1'", Dao.OpenDynaset).GetBind("fl", "rs", "Fields").Item("ft", "fl", A.Str("Att"))
                    .GetBind("ch", "ft", "Value").GetBind("chf", "ch", "Fields").Item("fd", "chf", A.Str("FileData"))
                    .Call("fd", "SaveToFile", A.Out("out-new.bin")).Errors().Files("s1")
                    .Call("fd", "SaveToFile", A.Out("exists.bin")).Errors().Files("s2")
                    .Call("rs", "Edit").Call("ch", "AddNew").Call("fd", "LoadFromFile", A.Out("absent.bin")).Errors().Call("ch", "Update").Errors().Call("ch", "CancelUpdate")
                    .Call("rs", "CancelUpdate").Files("s3")
                    .OpenRs("r3", "db", "SELECT * FROM T WHERE Name = 'p3'", Dao.OpenDynaset).GetBind("fl3", "r3", "Fields").Item("ft3", "fl3", A.Str("Att"))
                    .GetBind("c3", "ft3", "Value").GetBind("c3f", "c3", "Fields").Item("fd3", "c3f", A.Str("FileData")).Call("fd3", "SaveToFile", A.Out("out-eof.bin")).Errors().Files("s4")
                    .Call("rs", "Edit").Call("ch", "AddNew").Call("fd", "LoadFromFile", A.In()).Errors().Call("ch", "Update").Errors().Call("ch", "CancelUpdate").Call("rs", "CancelUpdate").Files("s5")
                    .Call("fd", "SaveToFile", A.In()).Errors().Files("s6", keep: true)
                    .Call("c3", "Close").Call("r3", "Close").Group("db", "T", "p1", "Att", "f").Shut("ch", "rs", "db");
            })
        { Fixtures = [("main", Fx("cx-attach"))] };

        // ------------------------------------------------------------------ CX-CALC
        yield return CalcChange("calc-source-data-change", Cx("CX-CALC"),
            "Holds a recordset on row ID 1 and its C_long, C_text and A Field objects; a source change by Execute UPDATE is made (held values read with and without Requery); then the source is changed through the recordset (value read after Put and after Update); " +
            "then A set to Null, to 2147483647 (A*2 overflow), D to 9999-12-31 (DateAdd overflow), A to 0 (division by zero in C_div) and an INSERT of A = 2147483647; the whole table is read after each, with errors.",
            p =>
            {
                p.OpenRs("rs", "db", "SELECT * FROM T WHERE ID = 1", Dao.OpenDynaset).GetBind("fl", "rs", "Fields")
                    .Item("fa", "fl", A.Str("A")).Item("fcl", "fl", A.Str("C_long")).Item("fct", "fl", A.Str("C_text"))
                    .Get("fcl", "Value").Get("fct", "Value").ReadCalc("c0")
                    .Sql("UPDATE T SET A = 100, S = 'zzz' WHERE ID = 1").Get("fcl", "Value").Get("fct", "Value").Row("rs", 24)
                    .Call("rs", "Requery").Get("fcl", "Value").Row("rs", 24)
                    .Call("rs", "Edit").Put("fa", "Value", A.I4(7)).Get("fcl", "Value").Call("rs", "Update").Errors().Get("fcl", "Value").ReadCalc("c1")
                    .Sql("UPDATE T SET A = Null WHERE ID = 1").ReadCalc("c2")
                    .Sql("UPDATE T SET A = 2147483647 WHERE ID = 1").Errors().ReadCalc("c3")
                    .Sql("UPDATE T SET D = #9999-12-31# WHERE ID = 1").Errors().ReadCalc("c4")
                    .Sql("UPDATE T SET A = 0 WHERE ID = 1").Errors().ReadCalc("c5")
                    .Sql("INSERT INTO T (A, S) VALUES (2147483647, 'big')").Errors().ReadCalc("c6").Shut("rs", "db");
            });

        yield return CalcChange("calc-schema-change", Cx("CX-CALC"),
            "In order: Expression of C_long replaced through Field.Expression; the source column A renamed through Field.Name to A2; source column D dropped by ALTER TABLE; source column B retyped to TEXT(10) by ALTER COLUMN; calculated column C_text dropped; " +
            "a new calculated field C_new ([A2]+1) appended and a row inserted. After each step the TableDef's fields (Name, Type, Expression) and all rows are read, with errors.",
            p =>
            {
                p.CalcDefs("0").ReadCalc("0")
                    .SetField("T", "C_long", "Expression", A.Str("[A]*3")).Errors().CalcDefs("1").ReadCalc("1")
                    .SetField("T", "A", "Name", A.Str("A2")).Errors().CalcDefs("2").ReadCalc("2")
                    .Sql("ALTER TABLE T DROP COLUMN D").Errors().CalcDefs("3").ReadCalc("3")
                    .Sql("ALTER TABLE T ALTER COLUMN B TEXT(10)").Errors().CalcDefs("4").ReadCalc("4")
                    .Sql("ALTER TABLE T DROP COLUMN C_text").Errors().CalcDefs("5").ReadCalc("5")
                    .AddCalc("T", "C_new", 4, 0, "[A2]+1", "new").Errors().Sql("INSERT INTO T (A2, S) VALUES (1, 'new')").Errors().CalcDefs("6").ReadCalc("6").Shut("db");
            });

        yield return CalcChange("calc-admission", Cx("CX-CALC"),
            "For each of seventeen expressions (Now, Date, Rnd, DLookup, DCount, Eval, Len, Switch, a cross-table reference, an undefined function, a self reference, CLng of text, division by zero, Long overflow, Format, Mid, IIf with Null): " +
            "a new table N<i>(ID, A, S) gets a calculated field X with the expression through CreateField/Expression/Fields.Append (append error recorded), then INSERT (A=3, S='abc') and its rows are read; and the same expression is run as a SELECT over T as a query.",
            p =>
            {
                (string Expression, int Type, int Size)[] expressions =
                [
                    ("Now()", 8, 0), ("Date()", 8, 0), ("Rnd()", 7, 0), ("DLookup(\"A\",\"T\",\"ID=1\")", 4, 0), ("DCount(\"*\",\"T\")", 4, 0), ("Eval(\"1+1\")", 4, 0),
                    ("Len([S])", 4, 0), ("Switch([A]>1,\"big\",True,\"small\")", 10, 20), ("[T].[A]", 4, 0), ("NoSuchFunction([A])", 4, 0), ("[X]+1", 4, 0), ("CLng([S])", 4, 0),
                    ("[A]/0", 7, 0), ("[A]*2147483647", 4, 0), ("Format([A],\"000\")", 10, 10), ("Mid([S],2,1)", 10, 5), ("IIf([A]>1,[A],Null)", 4, 0),
                ];
                for (int i = 0; i < expressions.Length; i++)
                {
                    (string expression, int type, int size) = expressions[i];
                    p.Sql($"CREATE TABLE N{i} (ID COUNTER CONSTRAINT PK_N{i} PRIMARY KEY, A LONG, S TEXT(20))")
                        .AddCalc($"N{i}", "X", type, size, expression, "x").Errors()
                        .Sql($"INSERT INTO N{i} (A, S) VALUES (3, 'abc')").Errors()
                        .Snap("db", "nr" + i, $"SELECT * FROM N{i}", 4, 8)
                        .Snap("db", "nq" + i, $"SELECT {expression} AS X FROM T WHERE ID = 1", 4, 4).Errors();
                }
                p.Shut("db");
            });

        // ------------------------------------------------------------------ CX-HISTORY
        yield return new Probe(Card, "history-append-cancel-rollback", Cx("CX-HISTORY"),
            "On the append-only Memo: the table and ColumnHistory('T','Notes','ID=n') for IDs 1 and 2 are read at the start; then an Edit/Update to 'r1'; an Edit with the value 'cancelled' and CancelUpdate; " +
            "BeginTrans with two updates ('t1', 't2') and Rollback; BeginTrans with an update ('t3') and CommitTrans. Both reads are repeated after each step and the closed file image is kept.",
            p =>
            {
                p.Engine().OpenRw().GetBind("ws", "eng", "Workspaces", A.I4(0)).ReadHist("0")
                    .OpenRs("rs", "db", "SELECT * FROM T WHERE ID = 1", Dao.OpenDynaset).GetBind("fl", "rs", "Fields").Item("fn", "fl", A.Str("Notes"))
                    .Call("rs", "Edit").Put("fn", "Value", A.Str("r1")).Call("rs", "Update").Errors().ReadHist("1")
                    .Call("rs", "Edit").Put("fn", "Value", A.Str("cancelled")).Call("rs", "CancelUpdate").Errors().ReadHist("2")
                    .Call("ws", "BeginTrans").Call("rs", "Edit").Put("fn", "Value", A.Str("t1")).Call("rs", "Update").Call("rs", "Edit").Put("fn", "Value", A.Str("t2")).Call("rs", "Update")
                    .Call("ws", "Rollback").Errors().ReadHist("3")
                    .Call("ws", "BeginTrans").Call("rs", "Edit").Put("fn", "Value", A.Str("t3")).Call("rs", "Update").Call("ws", "CommitTrans").Errors().ReadHist("4").Shut("rs", "db");
            })
        { Fixtures = [("main", Fx("cx-history"))] };

        yield return new Probe(Card, "history-toggle-delete", Cx("CX-HISTORY"),
            "AppendOnly is read, set to False through the field's Properties, an UPDATE is made, AppendOnly set back to True and an UPDATE made; then parent row ID 2 is deleted and a row 'two' reinserted. The table and ColumnHistory for IDs 1 and 2 are read after each step.",
            p =>
            {
                p.Engine().OpenRw().ReadHist("0")
                    .GetBind("ts", "db", "TableDefs").Item("td", "ts", A.Str("T")).GetBind("tf", "td", "Fields").Item("fn", "tf", A.Str("Notes")).GetBind("pps", "fn", "Properties").Item("pao", "pps", A.Str("AppendOnly"))
                    .Get("pao", "Value").Put("pao", "Value", A.Bool(false)).Errors().Get("pao", "Value")
                    .Sql("UPDATE T SET Notes = 'off1' WHERE ID = 1").ReadHist("1")
                    .Put("pao", "Value", A.Bool(true)).Errors().Get("pao", "Value")
                    .Sql("UPDATE T SET Notes = 'on1' WHERE ID = 1").ReadHist("2")
                    .Sql("DELETE FROM T WHERE ID = 2").ReadHist("3")
                    .Sql("INSERT INTO T (Title, Notes) VALUES ('two', 'again')").ReadHist("4")
                    .Release("pao").Release("pps").Release("fn").Release("tf").Release("td").Release("ts").Shut("db");
            })
        { Fixtures = [("main", Fx("cx-history"))] };

        // ------------------------------------------------------------------ CX-DESC
        yield return Change("desc-rename-drop", Cx("CX-DESC"),
            "With nothing held, the catalog (MSysComplexColumns, MSysObjects) is read after each DDL step on the multi-value table: Field.Name of Tags changed to Tags2; TableDef.Name of T changed to T2 (the group of p1 read through the new names); " +
            "an Integer multi-value field Extra (dbComplexLong 104) appended and then dropped by ALTER TABLE; Tags2 dropped by ALTER TABLE; table T2 dropped by DROP TABLE.",
            p =>
            {
                p.Engine().OpenRw().Catalog("0")
                    .SetField("T", "Tags", "Name", A.Str("Tags2")).Errors().Catalog("1").Group("db", "T", "p1", "Tags2", "n1")
                    .SetTable("T", "Name", A.Str("T2")).Errors().Catalog("2").Group("db", "T2", "p1", "Tags2", "n2")
                    .AddField("T2", "Extra", 104, "x").Errors().Catalog("3")
                    .Sql("ALTER TABLE T2 DROP COLUMN Extra").Errors().Catalog("4")
                    .Sql("ALTER TABLE T2 DROP COLUMN Tags2").Errors().Catalog("5")
                    .Sql("DROP TABLE T2").Errors().Catalog("6").Shut("db");
            });

        // ------------------------------------------------------------------ compaction of the complex fixtures
        yield return new Probe(Card, "compact-complex-fixtures", [.. Cx("CX-DESC", "CX-ATT", "CX-HISTORY", "CX-CALC"), .. Rb("RB-CLEAN", "RB-OPTIONS", "RB-NEGATIVE")],
            "DBEngine.CompactDatabase of each complex fixture (multi-value text, the eight kinds, attachments, calculated fields, append-only history) twice: with no options and with DstLocale dbLangGeneral and dbVersion40 (an ACCDB-to-Jet4 request carrying complex, calculated or history features). " +
            "Each output that exists is reopened read-only and its catalog, groups, rows and ColumnHistory are read; attachments are exported from the compacted file; DBEngine.Errors is read after each compaction.",
            p =>
            {
                p.Engine();
                (string Role, string Name)[] roles = [("main", "mv"), ("types", "types"), ("att", "att"), ("calc", "calc"), ("hist", "hist")];
                foreach ((string role, string name) in roles)
                {
                    p.Compact(name + "-same", A.In(role), A.Out($"{name}-same.accdb"));
                    p.ComplexReadback(role, name + "same", A.Out($"{name}-same.accdb"));
                    p.Compact(name + "-jet4", A.In(role), A.Out($"{name}-jet4.mdb"), A.Str(Dao.LangGeneral), A.I4(Dao.Version40));
                    p.ComplexReadback(role, name + "jet4", A.Out($"{name}-jet4.mdb"));
                }
                p.Modules("after-close").Files("after-close");
            })
        {
            Fixtures = [("main", Fx("cx-mv-text")), ("types", Fx("cx-types")), ("att", Fx("cx-attach")), ("calc", Fx("cx-calc")), ("hist", Fx("cx-history"))],
        };

        // ------------------------------------------------------------------ R-REBUILD-1
        yield return new Probe(Card, "compact-rich", Rb("RB-CLEAN", "RB-RELOC", "T-COMPACT-1"),
            "DBEngine.CompactDatabase(path, new path) with no options of the rich ACCDB and rich MDB fixtures: public state of the source (version, collating order, properties, TableDefs with fields and indexes, relations, QueryDefs, MSysCompactErrors and every row of Parent, Child and W), " +
            "file images before and after, then the same enumeration of the compacted output.",
            p =>
            {
                p.Engine();
                foreach ((string role, string ext) in new[] { ("main", "accdb"), ("mdb", "mdb") })
                {
                    p.Inspect(role + "src", A.In(role), RichSelects).Files(role + "-before", keep: true)
                        .Compact(role, A.In(role), A.Out($"{role}-compacted.{ext}"))
                        .Inspect(role + "dst", A.Out($"{role}-compacted.{ext}"), RichSelects);
                }
                p.Modules("after-close").Files("after-close", keep: true);
            })
        { Fixtures = RichFixtures };

        yield return new Probe(Card, "compact-bloat-repeated", Rb("RB-CLEAN", "RB-RELOC"),
            "DBEngine.CompactDatabase of the large-free-region ACCDB and MDB fixtures, then compaction of each output again twice (three generations): after each generation the row count, maximum ID, memo length sum and Keep count are read and the file lengths are observed.",
            p =>
            {
                p.Engine();
                foreach ((string role, string ext) in new[] { ("main", "accdb"), ("mdb", "mdb") })
                {
                    p.Brief(role + "g0", A.In(role), BloatSelects).Files(role + "-g0")
                        .Compact(role + "g1", A.In(role), A.Out($"{role}-g1.{ext}")).Brief(role + "g1", A.Out($"{role}-g1.{ext}"), BloatSelects)
                        .Compact(role + "g2", A.Out($"{role}-g1.{ext}"), A.Out($"{role}-g2.{ext}")).Brief(role + "g2", A.Out($"{role}-g2.{ext}"), BloatSelects)
                        .Compact(role + "g3", A.Out($"{role}-g2.{ext}"), A.Out($"{role}-g3.{ext}")).Brief(role + "g3", A.Out($"{role}-g3.{ext}"), BloatSelects);
                }
                p.Modules("after-close").Files("after-close");
            })
        { Fixtures = [("main", Fx("rb-bloat-accdb")), ("mdb", Fx("rb-bloat-mdb"))] };

        (string Role, string Fixture)[] feature =
        [
            ("a-memo", "r-fmt-1-accdb-memo"), ("a-counter", "r-fmt-1-accdb-counter"), ("a-index", "r-fmt-1-accdb-index"), ("a-renamed", "r-fmt-1-accdb-renamed-dropped"),
            ("a-querydef", "r-fmt-1-accdb-querydef"), ("a-properties", "r-fmt-1-accdb-properties"), ("a-calculated", "r-fmt-1-accdb-calculated"), ("a-wide", "r-fmt-1-accdb-wide"),
            ("m-memo", "r-fmt-1-mdb-memo"), ("m-counter", "r-fmt-1-mdb-counter"), ("m-index", "r-fmt-1-mdb-index"), ("m-renamed", "r-fmt-1-mdb-renamed-dropped"), ("m-querydef", "r-fmt-1-mdb-querydef"),
        ];
        yield return new Probe(Card, "compact-fmt1-features", Rb("RB-CLEAN", "RB-RELOC", "RB-OPAQUE"),
            "DBEngine.CompactDatabase with no options of thirteen R-FMT-1 single-feature fixtures (Memo values, AutoNumber with a deleted row, indexes, renamed/dropped/added columns, saved QueryDefs, properties, a calculated field, a 120-column table; ACCDB and MDB): " +
            "the public state and table T's rows of each source and of its output are enumerated.",
            p =>
            {
                p.Engine();
                foreach ((string role, _) in feature)
                {
                    string ext = role.StartsWith('m') ? "mdb" : "accdb";
                    p.Inspect(role + "s", A.In(role), ["SELECT * FROM T"], maxFields: 130).Compact(role, A.In(role), A.Out($"{role}.{ext}")).Inspect(role + "d", A.Out($"{role}.{ext}"), ["SELECT * FROM T"], maxFields: 130);
                }
                p.Modules("after-close").Files("after-close");
            })
        { Fixtures = [.. feature] };

        yield return new Probe(Card, "compact-counters", Rb("RB-CLEAN", "RB-RELOC"),
            "AutoNumber behaviour through compaction: by construction Child's counter has reached 64 and its rows above ID 50 were deleted. The source copy and the compacted copy are each opened read/write, MAX(ID) and COUNT are read, one row is inserted, the new ID is read by @@IDENTITY and by MAX(ID); ACCDB and MDB.",
            p =>
            {
                p.Engine();
                foreach ((string role, string ext) in new[] { ("main", "accdb"), ("mdb", "mdb") })
                {
                    p.Compact(role, A.In(role), A.Out($"{role}-ctr.{ext}"));
                    foreach ((string side, Arg path) in new[] { ("s", A.In(role)), ("d", A.Out($"{role}-ctr.{ext}")) })
                    {
                        string db = role + side;
                        p.CallBind(db, "eng", "OpenDatabase", path, A.Bool(false), A.Bool(false))
                            .Snap(db, db + "0", "SELECT MAX(ID) AS MaxId, COUNT(*) AS N FROM Child", 2, 4)
                            .Sql("INSERT INTO Child (ParentID, Val) VALUES (1, 'after')", db).Errors()
                            .Snap(db, db + "1", "SELECT @@IDENTITY AS Ident", 2, 2).Snap(db, db + "2", "SELECT MAX(ID) AS MaxId, COUNT(*) AS N FROM Child", 2, 4)
                            .Call(db, "Close").Release(db);
                    }
                }
                p.Modules("after-close").Files("after-close", keep: true);
            })
        { Fixtures = RichFixtures };

        yield return new Probe(Card, "options-version", Rb("RB-OPTIONS"),
            "CompactDatabase(source, new path, dbLangGeneral, option) of the rich ACCDB fixture with dbVersion120 (128), 256 (dbVersion140) and dbVersion40 (64), and of the rich MDB fixture with 64, 128 and 256; each output is enumerated (version, properties, tables, relations, QueryDefs and rows) and its length observed.",
            p =>
            {
                p.Engine();
                foreach ((string role, int[] versions) in new[] { ("main", new[] { 128, 256, 64 }), ("mdb", new[] { 64, 128, 256 }) })
                {
                    foreach (int v in versions)
                    {
                        string output = $"{role}-v{v}.{(v == 64 ? "mdb" : "accdb")}";
                        p.Compact($"{role}v{v}", A.In(role), A.Out(output), A.Str(Dao.LangGeneral), A.I4(v)).Inspect($"{role}v{v}", A.Out(output), RichSelects);
                    }
                }
                p.Modules("after-close").Files("after-close");
            })
        { Fixtures = RichFixtures };

        yield return new Probe(Card, "options-collation", Rb("RB-OPTIONS"),
            "CompactDatabase(source, new path, DstLocale) of the rich ACCDB and MDB fixtures with the General (0x0409), Spanish traditional (0x040A) and Swedish (0x041D) sort order locales; each output's CollatingOrder and the orders of Parent.Name and Child.Val (including ch, ll, a-umlaut, o-umlaut and z) are read.",
            p =>
            {
                p.Engine();
                (string Name, string Locale)[] locales = [("general", ";LANGID=0x0409;CP=1252;COUNTRY=0"), ("spanish", ";LANGID=0x040A;CP=1252;COUNTRY=0"), ("swedish", ";LANGID=0x041D;CP=1252;COUNTRY=0")];
                foreach ((string role, string ext) in new[] { ("main", "accdb"), ("mdb", "mdb") })
                {
                    foreach ((string name, string locale) in locales)
                    {
                        string output = $"{role}-{name}.{ext}";
                        p.Compact($"{role}{name}", A.In(role), A.Out(output), A.Str(locale)).Inspect($"{role}{name}", A.Out(output), [RichSelects[3], RichSelects[4]]);
                    }
                }
                p.Modules("after-close").Files("after-close");
            })
        { Fixtures = RichFixtures };

        yield return new Probe(Card, "options-encrypt-password", Rb("RB-OPTIONS", "RB-NEGATIVE"),
            "ACCDB: compaction with dbEncrypt (2); with DstLocale carrying ';pwd=' (a database password) and the output opened without and with the password; compaction of the password file back to a plain file with SrcLocale ';pwd='. " +
            "MDB: dbEncrypt then dbDecrypt (4). Each output is opened read-only and enumerated; errors are read after each step.",
            p =>
            {
                p.Engine();
                p.Compact("e1", A.In(), A.Out("enc.accdb"), A.Str(Dao.LangGeneral), A.I4(2)).Inspect("e1", A.Out("enc.accdb"), RichSelects)
                    .Compact("e2", A.In(), A.Out("pwd.accdb"), A.Str(Dao.LangGeneral + ";pwd=" + Pwd))
                    .Inspect("e2n", A.Out("pwd.accdb"), RichSelects).Errors().Inspect("e2p", A.Out("pwd.accdb"), RichSelects, ";pwd=" + Pwd)
                    .Compact("e3", A.Out("pwd.accdb"), A.Out("unpwd.accdb"), A.Missing, A.Missing, A.Str(";pwd=" + Pwd)).Inspect("e3", A.Out("unpwd.accdb"), RichSelects)
                    .Compact("e4", A.In("mdb"), A.Out("enc.mdb"), A.Str(Dao.LangGeneral), A.I4(2)).Inspect("e4", A.Out("enc.mdb"), RichSelects)
                    .Compact("e5", A.Out("enc.mdb"), A.Out("dec.mdb"), A.Str(Dao.LangGeneral), A.I4(4)).Inspect("e5", A.Out("dec.mdb"), RichSelects)
                    .Modules("after-close").Files("after-close");
            })
        { Fixtures = RichFixtures };

        yield return new Probe(Card, "publish-existing-and-aliases", Rb("RB-PUBLISH"),
            "Destination handling of CompactDatabase on the rich ACCDB fixture: a new destination; the same call again onto the now existing destination; destination equal to the source path; destination equal to an existing file; " +
            "and for each spelling of the input path (upper, extended, slashes, dot) a compaction from that spelling to a new file and a compaction from the plain path onto that spelling of itself. File images are kept after the same-path attempt; counts of Parent are read from each result.",
            p =>
            {
                string[] count = ["SELECT COUNT(*) AS N FROM Parent"];
                p.Engine().Compact("p1", A.In(), A.Out("pub.accdb")).Brief("p1", A.Out("pub.accdb"), count)
                    .Compact("p2", A.In(), A.Out("pub.accdb")).Brief("p2", A.Out("pub.accdb"), count)
                    .Compact("p3", A.In(), A.In()).Files("p3-kept", keep: true).Brief("p3", A.In(), count)
                    .Compact("p4", A.Out("pub.accdb"), A.In()).Brief("p4", A.In(), count);
                foreach (string spelling in new[] { "upper", "extended", "slashes", "dot" })
                {
                    p.Compact("from-" + spelling, A.InAs(spelling), A.Out($"alias-{spelling}.accdb")).Brief("from" + spelling, A.Out($"alias-{spelling}.accdb"), count)
                        .Compact("onto-" + spelling, A.Out("pub.accdb"), A.InAs(spelling)).Brief("onto" + spelling, A.In(), count);
                }
                p.Modules("after-close").Files("after-close", keep: true);
            })
        { Fixtures = [("main", Fx("rb-rich-accdb"))] };

        yield return new Probe(Card, "publish-held-inproc", Rb("RB-PUBLISH", "RB-NEGATIVE"),
            "CompactDatabase while the source is held in the same process: by a shared read/write Database, by an exclusive Database, by an exclusive read-only Database; while the destination is held by a shared Database; and while a native handle holds the source " +
            "(read/write, share none; then read, share read) or the existing destination (read, share none). Errors and file state are recorded after each attempt.",
            p =>
            {
                string[] count = ["SELECT COUNT(*) AS N FROM Parent"];
                p.Engine();
                (string Name, bool Exclusive, bool ReadOnly)[] holds = [("shared", false, false), ("excl", true, false), ("exclro", true, true)];
                foreach ((string name, bool exclusive, bool readOnly) in holds)
                {
                    p.CallBind("h" + name, "eng", "OpenDatabase", A.In(), A.Bool(exclusive), A.Bool(readOnly))
                        .Compact("held-" + name, A.In(), A.Out($"held-{name}.accdb")).Brief("held" + name, A.Out($"held-{name}.accdb"), count)
                        .Call("h" + name, "Close").Release("h" + name);
                }
                p.Compact("dst0", A.In(), A.Out("dst.accdb")).CallBind("hdst", "eng", "OpenDatabase", A.Out("dst.accdb"), A.Bool(false), A.Bool(false))
                    .Compact("dst1", A.In(), A.Out("dst.accdb")).Call("hdst", "Close").Release("hdst");
                p.NativeOpen("n1", "input:main", Win.GenericRead | Win.GenericWrite, Win.ShareNone).Compact("native-rw", A.In(), A.Out("native-rw.accdb")).NativeClose("n1")
                    .NativeOpen("n2", "input:main", Win.GenericRead, Win.ShareRead).Compact("native-r", A.In(), A.Out("native-r.accdb")).NativeClose("n2")
                    .NativeOpen("n3", "work:dst.accdb", Win.GenericRead, Win.ShareNone).Compact("native-dst", A.In(), A.Out("dst.accdb")).NativeClose("n3");
                p.Modules("after-close").Files("after-close");
            })
        { Fixtures = [("main", Fx("rb-rich-accdb"))] };

        {
            (string Role, string Ext, string Fixture)[] formats = [("main", "accdb", Fx("rb-rich-accdb")), ("mdb", "mdb", Fx("rb-rich-mdb"))];
            (string Name, bool Exclusive, bool ReadOnly)[] states = [("shared-rw", false, false), ("excl-rw", true, false), ("excl-ro", true, true), ("shared-ro", false, true)];
            yield return new Probe(Card, "publish-held-xproc", Rb("RB-PUBLISH", "RB-NEGATIVE"),
                "Two processes. Process b opens the source with DAO OpenDatabase in each of four states (shared read/write, exclusive read/write, exclusive read-only, shared read-only), for the rich ACCDB and then the rich MDB; " +
                "while b holds each state process a runs CompactDatabase of that source to a new file and reads DBEngine.Errors and the output's row count; b releases before the next state.",
                a =>
                {
                    a.Engine();
                    int n = 0;
                    foreach ((string role, string ext, _) in formats)
                    {
                        foreach ((string name, _, _) in states)
                        {
                            a.Await($"b-open-{n}", Wait).Compact($"{role}{name}", A.In(role), A.Out($"{role}-{name}.{ext}")).Brief($"{role}{name}", A.Out($"{role}-{name}.{ext}"), ["SELECT COUNT(*) AS N FROM Parent"])
                                .Signal($"a-done-{n}");
                            n++;
                        }
                    }
                    a.Await("b-end", Wait).Modules("after-close").Files("after-close");
                })
            {
                Fixtures = [.. formats.Select(f => (f.Role, f.Fixture))],
                Participants =
                [
                    ("b", b =>
                    {
                        b.Create().Modules("after-create");
                        int n = 0;
                        foreach ((string role, _, _) in formats)
                        {
                            foreach ((string name, bool exclusive, bool readOnly) in states)
                            {
                                b.CallBind("hb", "eng", "OpenDatabase", A.In(role), A.Bool(exclusive), A.Bool(readOnly)).Snap("hb", $"b{n}", "SELECT COUNT(*) AS N FROM Parent", 2, 2)
                                    .Signal($"b-open-{n}").Await($"a-done-{n}", Wait).Call("hb", "Close").Release("hb");
                                n++;
                            }
                        }
                        b.Errors().Files("b-after").Signal("b-end");
                    }),
                ],
            };
        }

        yield return new Probe(Card, "negative-open-and-compact", Rb("RB-NEGATIVE"),
            "Whether refusals stay distinct from corruption: CompactDatabase and OpenDatabase of a missing source, of a text file named .accdb (written by ADODB.Stream) and of an empty file; of a password-protected database with no password and with a wrong password; " +
            "an invalid destination locale; undefined option bits (1024); and the same password file opened with no password and with a wrong one. Errors are read after each attempt.",
            p =>
            {
                p.Engine().MakeFile("notdb.accdb", Encoding.ASCII.GetBytes("this is not a database")).MakeFile("emptyfile.accdb", [])
                    .Compact("n1", A.Out("absent.accdb"), A.Out("n1.accdb"))
                    .Compact("n2", A.Out("notdb.accdb"), A.Out("n2.accdb")).CallBind("o2", "eng", "OpenDatabase", A.Out("notdb.accdb"), A.Bool(false), A.Bool(true)).Errors()
                    .Compact("n3", A.Out("emptyfile.accdb"), A.Out("n3.accdb")).CallBind("o3", "eng", "OpenDatabase", A.Out("emptyfile.accdb"), A.Bool(false), A.Bool(true)).Errors()
                    .Compact("n4", A.In(), A.Out("pw.accdb"), A.Str(Dao.LangGeneral + ";pwd=" + Pwd))
                    .Compact("n5", A.Out("pw.accdb"), A.Out("n5.accdb"))
                    .Compact("n6", A.Out("pw.accdb"), A.Out("n6.accdb"), A.Missing, A.Missing, A.Str(";pwd=wrong"))
                    .Brief("o5", A.Out("pw.accdb"), ["SELECT COUNT(*) AS N FROM Parent"]).Errors()
                    .Brief("o6", A.Out("pw.accdb"), ["SELECT COUNT(*) AS N FROM Parent"], ";pwd=wrong").Errors()
                    .Compact("n7", A.In(), A.Out("n7.accdb"), A.Str(";LANGID=0xFFFF;CP=1252;COUNTRY=0"))
                    .Compact("n8", A.In(), A.Out("n8.accdb"), A.Str(Dao.LangGeneral), A.I4(1024))
                    .Modules("after-close").Files("after-close");
            })
        { Fixtures = [("main", Fx("rb-rich-accdb"))] };

        yield return new Probe(Card, "repair-clean-control", Rb("RB-REPAIR", "RB-NEGATIVE", "T-COMPACT-1"),
            "DBEngine.RepairDatabase on clean copies of the rich ACCDB and MDB fixtures (the only DAO repair entry point): return, errors, file images before and after (kept), and the public state and rows of the repaired file; MSysCompactErrors is read.",
            p =>
            {
                p.Engine();
                foreach (string role in new[] { "main", "mdb" })
                {
                    p.Files(role + "-before", keep: true).Call("eng", "RepairDatabase", A.In(role)).Errors().Files(role + "-after", keep: true).Inspect(role + "rep", A.In(role), RichSelects);
                }
                p.Modules("after-close").Files("after-close");
            })
        { Fixtures = RichFixtures };
    }

    // ---------------------------------------------------------------------------------------------- builders

    private static Probe Create(string name, string[] refs, string ext, string what, Action<P> body)
    {
        int version = ext == "mdb" ? Dao.Version40 : Dao.Version120;
        return new Probe(Card, "create-" + name, refs,
            $"Fixture {Fx(name)}: {what}. Created in {ext.ToUpperInvariant()} by DBEngine.CreateDatabase(path, dbLangGeneral, {version}) and DAO/DDL calls; every call is recorded and the closed file is kept.",
            p =>
            {
                p.Modules("before-create").Create().Modules("after-create").Get("eng", "Version")
                    .CallBind("db", "eng", "CreateDatabase", A.Out($"{Fx(name)}.{ext}"), A.Str(Dao.LangGeneral), A.I4(version)).Get("db", "Version");
                body(p);
                p.Call("db", "Close").Release("db").Release("eng").Modules("after-close").Files("after-close");
            })
        { CreatesFixture = true };
    }

    /// <summary>A read-only probe on one fixture.</summary>
    private static Probe Use(string name, string fixture, string[] refs, string what, Action<P> body) =>
        new(Card, name, refs, what, body) { Fixtures = [("main", Fx(fixture))] };

    /// <summary>A probe on the attempt's own copy of the multi-value fixture (read/write).</summary>
    private static Probe Change(string name, string[] refs, string what, Action<P> body) =>
        new(Card, name, refs, what, body) { Fixtures = [("main", Fx("cx-mv-text"))] };

    /// <summary>A probe on the attempt's own copy of the calculated-field fixture, opened read/write before the body runs.</summary>
    private static Probe CalcChange(string name, string[] refs, string what, Action<P> body) =>
        new(Card, name, refs, what, p => body(p.Engine().OpenRw()))
        { Fixtures = [("main", Fx("cx-calc"))] };

    private static void RichBody(P p)
    {
        string[] names = ["a", "ch", "c", "d", "ll", "l", "m", "\u00e4", "z", "\u00f6", "same", "same", "same", "b", "e", "f", "g", "h", "i", "j"];
        p.Sql("CREATE TABLE Parent (ID COUNTER CONSTRAINT PK_Parent PRIMARY KEY, Code TEXT(20) CONSTRAINT UX_Parent_Code UNIQUE, Name TEXT(50), Note MEMO, Amount CURRENCY, Born DATETIME, Flag YESNO)")
            .Sql("CREATE TABLE Child (ID COUNTER CONSTRAINT PK_Child PRIMARY KEY, ParentID LONG, Val TEXT(30), Blob LONGBINARY)")
            .Sql("CREATE TABLE W (ID COUNTER CONSTRAINT PK_W PRIMARY KEY, A1 TEXT(255), A2 TEXT(255), A3 TEXT(255), A4 TEXT(255), A5 TEXT(255), A6 TEXT(255))")
            .Sql("ALTER TABLE Child ADD CONSTRAINT FK_Child_Parent FOREIGN KEY (ParentID) REFERENCES Parent (ID)")
            .Sql("CREATE INDEX IX_Parent_Name_Born ON Parent (Name, Born DESC)").Sql("CREATE INDEX IX_Child_Val ON Child (Val)");
        for (int i = 0; i < names.Length; i++)
        {
            p.Sql($"INSERT INTO Parent (Code, Name, Note, Amount, Born, Flag) VALUES ('c{i + 1}', '{names[i]}', 'short {i + 1}', {i + 1}.25, #2024-01-{i + 1:D2}#, {(i % 2 == 0 ? "True" : "False")})");
        }
        p.Sql($"UPDATE Parent SET Note = '{new string('m', 20000)}' WHERE Code = 'c1'").Sql($"UPDATE Parent SET Note = '{new string('n', 5000)}' WHERE Code = 'c2'")
            .Sql("DELETE FROM Parent WHERE Code = 'c20'").Sql("INSERT INTO Parent (Code, Name, Note, Amount, Born, Flag) VALUES ('c20', 'j', 'again', 20.25, #2024-01-20#, True)")
            .OpenRs("crs", "db", "Child", Dao.OpenTable).GetBind("cfl", "crs", "Fields").Item("cpid", "cfl", A.Str("ParentID")).Item("cval", "cfl", A.Str("Val")).Item("cblob", "cfl", A.Str("Blob"))
            .Call("crs", "AddNew").Put("cpid", "Value", A.I4(1)).Put("cval", "Value", A.Str("blob")).Put("cblob", "Value", A.Bytes([.. Enumerable.Range(0, 5000).Select(i => (byte)(i * 7))])).Call("crs", "Update")
            .Release("cpid").Release("cval").Release("cblob").Release("cfl").Call("crs", "Close").Release("crs")
            .Sql("INSERT INTO Child (ParentID, Val) VALUES (1, 'v1')").Sql("INSERT INTO Child (ParentID, Val) VALUES (2, 'v2')").Sql("INSERT INTO Child (ParentID, Val) VALUES (3, 'v1')")
            .Repeat(4, "INSERT INTO Child (ParentID, Val) SELECT ParentID, Val FROM Child").Sql("DELETE FROM Child WHERE ID > 50")
            .Sql("INSERT INTO W (A1) VALUES ('x')").Repeat(6, "INSERT INTO W (A1) SELECT A1 FROM W");
        string big = new('w', 250);
        p.Sql($"UPDATE W SET A1 = '{big}', A2 = '{big}', A3 = '{big}', A4 = '{big}', A5 = '{big}', A6 = '{big}' WHERE ID = 32")
            .CallBind("q1", "db", "CreateQueryDef", A.Str("QParam"), A.Str("PARAMETERS pMin Currency; SELECT * FROM Parent WHERE Amount >= pMin;"))
            .CallBind("q2", "db", "CreateQueryDef", A.Str("QJoin"), A.Str("SELECT P.Name, C.Val FROM Parent AS P INNER JOIN Child AS C ON P.ID = C.ParentID;"))
            .Release("q1").Release("q2")
            .CallBind("prop", "db", "CreateProperty", A.Str("CalcDbRb"), A.I4(10), A.Str("value of a user property"))
            .GetBind("props", "db", "Properties").Call("props", "Append", A.Obj("prop")).Release("prop").Release("props")
            .GetBind("tdfs", "db", "TableDefs").Item("tdf", "tdfs", A.Str("Parent"))
            .CallBind("tprop", "tdf", "CreateProperty", A.Str("Description"), A.I4(10), A.Str("parent table description"))
            .GetBind("tprops", "tdf", "Properties").Call("tprops", "Append", A.Obj("tprop")).Release("tprop").Release("tprops")
            .GetBind("tfl", "tdf", "Fields").Item("fname", "tfl", A.Str("Name"))
            .CallBind("fprop", "fname", "CreateProperty", A.Str("Description"), A.I4(10), A.Str("name field description"))
            .GetBind("fprops", "fname", "Properties").Call("fprops", "Append", A.Obj("fprop"))
            .Release("fprop").Release("fprops").Release("fname").Release("tfl").Release("tdf").Release("tdfs");
    }

    internal static byte[] Pattern(int count) => [.. Enumerable.Range(0, count).Select(i => (byte)(i % 251))];

    internal static byte[] Noise(int count, uint seed)
    {
        var bytes = new byte[count];
        uint state = seed;
        for (int i = 0; i < count; i++)
        {
            state = unchecked((state * 1664525u) + 1013904223u);
            bytes[i] = (byte)(state >> 24);
        }
        return bytes;
    }
}

/// <summary>Reusable program steps of <see cref="RComplexRebuild"/>.</summary>
internal static class ComplexSteps
{
    public static P Sql(this P p, string sql, string db = "db") => p.Call(db, "Execute", A.Str(sql));

    public static P Repeat(this P p, int times, string sql)
    {
        for (int i = 0; i < times; i++)
        {
            p.Sql(sql);
        }
        return p;
    }

    public static P OpenRw(this P p, string bind = "db", string role = "main") =>
        p.CallBind(bind, "eng", "OpenDatabase", A.In(role), A.Bool(false), A.Bool(false));

    /// <summary>Close the named objects, release the engine, then keep the closed file image.</summary>
    public static P Shut(this P p, params string[] close)
    {
        foreach (string name in close)
        {
            p.Call(name, "Close");
        }
        return p.Modules("after-close").Files("after-close", keep: true);
    }

    /// <summary>Every field's Name, Type and Value of the current record.</summary>
    public static P Row(this P p, string rs, int maxFields = 32)
    {
        string fl = "fl_" + rs, f = "f_" + rs;
        return p.GetBind(fl, rs, "Fields").Each(fl, f, maxFields, b => b.Get(f, "Name").Get(f, "Type").Get(f, "Value")).Release(fl);
    }

    public static P Rows(this P p, string rs, int maxRows = 64, int maxFields = 32) =>
        p.Until(rs, "EOF", maxRows, b => b.Row(rs, maxFields).Call(rs, "MoveNext"));

    /// <summary>One field's Value of the current record.</summary>
    public static P Cell(this P p, string rs, string field, string tag) =>
        p.GetBind("cl" + tag, rs, "Fields").Item("ce" + tag, "cl" + tag, A.Str(field)).Get("ce" + tag, "Value").Release("ce" + tag).Release("cl" + tag);

    public static P Snap(this P p, string db, string tag, string sql, int maxRows = 64, int maxFields = 32) =>
        p.OpenRs(tag, db, sql, Dao.OpenSnapshot).Rows(tag, maxRows, maxFields).Call(tag, "Close").Release(tag);

    /// <summary>
    /// Opens parent <paramref name="name"/> of <paramref name="table"/> and reads its group in <paramref name="field"/> through
    /// Field2.Value as a child recordset: RecordCount, BOF, EOF, every row's fields, RecordCount again.
    /// </summary>
    public static P Group(this P p, string db, string table, string name, string field, string tag, int maxRows = 16)
    {
        string rs = "gp" + tag, fl = "gf" + tag, f = "gc" + tag, ch = "gh" + tag;
        return p.OpenRs(rs, db, $"SELECT * FROM {table} WHERE Name = '{name}'", Dao.OpenDynaset).GetBind(fl, rs, "Fields").Item(f, fl, A.Str(field)).GetBind(ch, f, "Value")
            .Get(ch, "RecordCount").Get(ch, "BOF").Get(ch, "EOF").Rows(ch, maxRows, 12).Get(ch, "RecordCount")
            .Call(ch, "Close").Release(ch).Release(f).Release(fl).Call(rs, "Close").Release(rs);
    }

    /// <summary>The state of a held child recordset: RecordCount, BOF, EOF, Bookmark and the current row.</summary>
    public static P Peek(this P p, string child) =>
        p.Get(child, "RecordCount").Get(child, "BOF").Get(child, "EOF").Get(child, "Bookmark").Row(child, 12);

    /// <summary>The field "Value" of a child recordset, bound to <c>cv&lt;tag&gt;</c>.</summary>
    public static P ChildField(this P p, string child, string tag) =>
        p.GetBind("cf" + tag, child, "Fields").Item("cv" + tag, "cf" + tag, A.Str("Value"));

    /// <summary>A read/write engine, a dynaset on <paramref name="sql"/> (bound rs), its Tags Field2 (ft) and the child recordset of the current row (c1).</summary>
    public static P Hold(this P p, string sql) =>
        p.Engine().OpenRw().OpenRs("rs", "db", sql, Dao.OpenDynaset).GetBind("fl", "rs", "Fields").Item("ft", "fl", A.Str("Tags")).GetBind("c1", "ft", "Value");

    /// <summary>The same hold with every name suffixed (<paramref name="s"/>): d, r, l, f and c.</summary>
    public static P HeldSeq(this P p, string s) =>
        p.OpenRw("d" + s).OpenRs("r" + s, "d" + s, "SELECT * FROM T WHERE Name = 'p1'", Dao.OpenDynaset).GetBind("l" + s, "r" + s, "Fields")
            .Item("f" + s, "l" + s, A.Str("Tags")).GetBind("c" + s, "f" + s, "Value").Peek("c" + s);

    /// <summary>A fresh Database (not the held one) reads the group of p1 and closes.</summary>
    public static P Reread(this P p, string s) =>
        p.OpenRw("z" + s).Group("z" + s, "T", "p1", "Tags", "z" + s).Call("z" + s, "Close").Release("z" + s).Files("after-" + s);

    /// <summary>Appends a complex or attachment field (a DataTypeEnum value) to an existing table.</summary>
    public static P AddField(this P p, string table, string field, int type, string tag) =>
        p.GetBind("ts" + tag, "db", "TableDefs").Item("td" + tag, "ts" + tag, A.Str(table)).CallBind("fn" + tag, "td" + tag, "CreateField", A.Str(field), A.I4(type))
            .GetBind("tf" + tag, "td" + tag, "Fields").Call("tf" + tag, "Append", A.Obj("fn" + tag))
            .Release("fn" + tag).Release("tf" + tag).Release("td" + tag).Release("ts" + tag);

    /// <summary>Appends a calculated field: Field.Expression is set before Fields.Append.</summary>
    public static P AddCalc(this P p, string table, string field, int type, int size, string expression, string tag)
    {
        p.GetBind("ts" + tag, "db", "TableDefs").Item("td" + tag, "ts" + tag, A.Str(table));
        if (size > 0)
        {
            p.CallBind("fn" + tag, "td" + tag, "CreateField", A.Str(field), A.I4(type), A.I4(size));
        }
        else
        {
            p.CallBind("fn" + tag, "td" + tag, "CreateField", A.Str(field), A.I4(type));
        }
        return p.Put("fn" + tag, "Expression", A.Str(expression)).GetBind("tf" + tag, "td" + tag, "Fields").Call("tf" + tag, "Append", A.Obj("fn" + tag))
            .Release("fn" + tag).Release("tf" + tag).Release("td" + tag).Release("ts" + tag);
    }

    /// <summary>Property put on an existing field of a table.</summary>
    public static P SetField(this P p, string table, string field, string member, Arg value) =>
        p.GetBind("sts", "db", "TableDefs").Item("std", "sts", A.Str(table)).GetBind("stf", "std", "Fields").Item("sfd", "stf", A.Str(field)).Put("sfd", member, value)
            .Release("sfd").Release("stf").Release("std").Release("sts");

    public static P SetTable(this P p, string table, string member, Arg value) =>
        p.GetBind("sts", "db", "TableDefs").Item("std", "sts", A.Str(table)).Put("std", member, value).Release("std").Release("sts");

    /// <summary>Child records of a parent: parent Edit, one child AddNew/Value/Update per value, parent Update.</summary>
    public static P Fill(this P p, string table, string name, string field, params Arg[] values)
    {
        p.OpenRs("fp", "db", $"SELECT * FROM {table} WHERE Name = '{name}'", Dao.OpenDynaset).Call("fp", "Edit").GetBind("ff", "fp", "Fields").Item("fc", "ff", A.Str(field))
            .GetBind("fh", "fc", "Value").GetBind("fhf", "fh", "Fields").Item("fv", "fhf", A.Str("Value"));
        foreach (Arg v in values)
        {
            p.Call("fh", "AddNew").Put("fv", "Value", v).Call("fh", "Update");
        }
        return p.Release("fv").Release("fhf").Call("fh", "Close").Release("fh").Release("fc").Release("ff").Call("fp", "Update").Call("fp", "Close").Release("fp");
    }

    /// <summary>Attachments of a parent loaded with FileData.LoadFromFile from work files.</summary>
    public static P FillAtt(this P p, string table, string name, string field, params string[] files)
    {
        p.OpenRs("fp", "db", $"SELECT * FROM {table} WHERE Name = '{name}'", Dao.OpenDynaset).Call("fp", "Edit").GetBind("ff", "fp", "Fields").Item("fc", "ff", A.Str(field))
            .GetBind("fh", "fc", "Value").GetBind("fhf", "fh", "Fields").Item("fd", "fhf", A.Str("FileData"));
        foreach (string file in files)
        {
            p.Call("fh", "AddNew").Call("fd", "LoadFromFile", A.Out(file)).Call("fh", "Update");
        }
        return p.Release("fd").Release("fhf").Call("fh", "Close").Release("fh").Release("fc").Release("ff").Call("fp", "Update").Call("fp", "Close").Release("fp");
    }

    /// <summary>Exports one attachment (the child at <paramref name="index"/>): FieldSize, GetChunk(0,16) and SaveToFile into a work file.</summary>
    public static P Export(this P p, string db, string table, string name, string field, int index, string file, string tag)
    {
        string rs = "xr" + tag, fl = "xl" + tag, ft = "xt" + tag, ch = "xc" + tag, cf = "xf" + tag, fd = "xd" + tag;
        p.OpenRs(rs, db, $"SELECT * FROM {table} WHERE Name = '{name}'", Dao.OpenDynaset).GetBind(fl, rs, "Fields").Item(ft, fl, A.Str(field)).GetBind(ch, ft, "Value");
        for (int i = 0; i < index; i++)
        {
            p.Call(ch, "MoveNext");
        }
        return p.GetBind(cf, ch, "Fields").Item(fd, cf, A.Str("FileData")).Get(fd, "FieldSize").Call(fd, "GetChunk", A.I4(0), A.I4(16)).Call(fd, "SaveToFile", A.Out(file)).Errors()
            .Release(fd).Release(cf).Call(ch, "Close").Release(ch).Release(ft).Release(fl).Call(rs, "Close").Release(rs);
    }

    /// <summary>A work file with exactly these bytes, written by ADODB.Stream (binary type, SaveToFile overwrite).</summary>
    public static P MakeFile(this P p, string name, byte[] content)
    {
        string st = "mk" + string.Concat(name.Where(char.IsLetterOrDigit));
        p.Create("ADODB.Stream", st).Put(st, "Type", A.I4(1)).Call(st, "Open");
        if (content.Length > 0)
        {
            p.Call(st, "Write", A.Bytes(content));
        }
        return p.Call(st, "SaveToFile", A.Out(name), A.I4(2)).Call(st, "Close").Release(st);
    }

    /// <summary>Every Field2 property of a bound field object, then each entry of its Properties collection.</summary>
    public static P FieldFull(this P p, string f) =>
        p.Get(f, "Name").Get(f, "Type").Get(f, "Size").Get(f, "Attributes").Get(f, "OrdinalPosition").Get(f, "Required").Get(f, "AllowZeroLength")
            .Get(f, "DataUpdatable").Get(f, "SourceTable").Get(f, "SourceField").Get(f, "IsComplex").Get(f, "Expression").Get(f, "ValidationRule").Get(f, "DefaultValue")
            .GetBind(f + "pr", f, "Properties").Each(f + "pr", f + "pp", 48, b => b.Get(f + "pp", "Name").Get(f + "pp", "Type").Get(f + "pp", "Value")).Release(f + "pr");

    /// <summary>Database version and properties, every TableDef's name and attributes, and the fields, indexes and properties of the named tables.</summary>
    public static P Meta(this P p, string db, params string[] tables)
    {
        p.Get(db, "Version").Get(db, "CollatingOrder").Get(db, "Updatable")
            .GetBind("dprops", db, "Properties").Each("dprops", "dp", 64, b => b.Get("dp", "Name").Get("dp", "Type").Get("dp", "Value")).Release("dprops")
            .GetBind("mtds", db, "TableDefs").Each("mtds", "mtd", 64, t => t.Get("mtd", "Name").Get("mtd", "Attributes").Get("mtd", "RecordCount")).Release("mtds");
        foreach (string table in tables)
        {
            p.GetBind("mts", db, "TableDefs").Item("mtt", "mts", A.Str(table)).GetBind("mfl", "mtt", "Fields").Each("mfl", "mfd", 48, f => f.FieldFull("mfd")).Release("mfl")
                .GetBind("mix", "mtt", "Indexes").Each("mix", "mxi", 16, i => i.Get("mxi", "Name").Get("mxi", "Primary").Get("mxi", "Unique").Get("mxi", "Foreign")
                    .GetBind("mif", "mxi", "Fields").Each("mif", "mxf", 8, x => x.Get("mxf", "Name").Get("mxf", "Attributes")).Release("mif")).Release("mix")
                .GetBind("mtp", "mtt", "Properties").Each("mtp", "mtq", 32, b => b.Get("mtq", "Name").Get("mtq", "Type").Get("mtq", "Value")).Release("mtp")
                .Release("mtt").Release("mts");
        }
        return p;
    }

    /// <summary>The native catalog: MSysComplexColumns and MSysObjects (names, types, flags, parents).</summary>
    public static P Catalog(this P p, string tag, string db = "db") =>
        p.Snap(db, "ca" + tag, "SELECT * FROM MSysComplexColumns", 32, 12)
            .Snap(db, "cb" + tag, "SELECT Id, ParentId, Name, Type, Flags, ForeignName FROM MSysObjects ORDER BY Id", 128, 8);

    /// <summary>All rows of T.</summary>
    public static P ReadCalc(this P p, string tag) => p.Snap("db", "rc" + tag, "SELECT * FROM T ORDER BY ID", 16, 32);

    /// <summary>The Name, Type and Expression of every field of table T.</summary>
    public static P CalcDefs(this P p, string tag) =>
        p.GetBind("cdt" + tag, "db", "TableDefs").Item("cdd" + tag, "cdt" + tag, A.Str("T")).GetBind("cdf" + tag, "cdd" + tag, "Fields")
            .Each("cdf" + tag, "cdx" + tag, 40, f => f.Get("cdx" + tag, "Name").Get("cdx" + tag, "Type").Get("cdx" + tag, "Expression"))
            .Release("cdf" + tag).Release("cdd" + tag).Release("cdt" + tag);

    /// <summary>The append-only table and ColumnHistory for IDs 1 and 2.</summary>
    public static P ReadHist(this P p, string tag) =>
        p.Snap("db", "hn" + tag, "SELECT ID, Title, Notes FROM T ORDER BY ID", 8, 8)
            .Snap("db", "h1" + tag, "SELECT ColumnHistory('T','Notes','ID=1') AS H FROM T WHERE ID = 1", 2, 2)
            .Snap("db", "h2" + tag, "SELECT ColumnHistory('T','Notes','ID=2') AS H FROM T WHERE ID = 2", 2, 2);

    private static P OpenRo(this P p, string d, Arg path, string? connect) =>
        connect is null
            ? p.CallBind(d, "eng", "OpenDatabase", path, A.Bool(false), A.Bool(true))
            : p.CallBind(d, "eng", "OpenDatabase", path, A.Bool(false), A.Bool(true), A.Str(connect));

    /// <summary>Open a database read-only, read a few counts through the given SELECTs, close.</summary>
    public static P Brief(this P p, string tag, Arg path, string[] selects, string? connect = null)
    {
        string d = "b" + tag;
        p.OpenRo(d, path, connect).Get(d, "Version").Get(d, "CollatingOrder");
        for (int i = 0; i < selects.Length; i++)
        {
            p.Snap(d, $"{d}q{i}", selects[i], 4, 8);
        }
        return p.Call(d, "Close").Release(d);
    }

    /// <summary>
    /// Open a database read-only and read its public state: version, collating order, properties, every TableDef with fields,
    /// indexes and properties, relations, QueryDefs, MSysCompactErrors and the rows of each given SELECT; then close it.
    /// </summary>
    public static P Inspect(this P p, string tag, Arg path, string[] selects, string? connect = null, int maxFields = 16)
    {
        string d = "i" + tag;
        p.OpenRo(d, path, connect).Get(d, "Version").Get(d, "CollatingOrder").Get(d, "Name")
            .GetBind(d + "dp", d, "Properties").Each(d + "dp", d + "dq", 64, b => b.Get(d + "dq", "Name").Get(d + "dq", "Type").Get(d + "dq", "Value")).Release(d + "dp")
            .GetBind(d + "ts", d, "TableDefs")
            .Each(d + "ts", d + "td", 64, t => t.Get(d + "td", "Name").Get(d + "td", "Attributes").Get(d + "td", "RecordCount")
                .GetBind(d + "tf", d + "td", "Fields")
                .Each(d + "tf", d + "tx", 160, f => f.Get(d + "tx", "Name").Get(d + "tx", "Type").Get(d + "tx", "Size").Get(d + "tx", "Attributes").Get(d + "tx", "OrdinalPosition"))
                .Release(d + "tf")
                .GetBind(d + "ti", d + "td", "Indexes")
                .Each(d + "ti", d + "tj", 16, i => i.Get(d + "tj", "Name").Get(d + "tj", "Primary").Get(d + "tj", "Unique").Get(d + "tj", "Foreign")
                    .GetBind(d + "tk", d + "tj", "Fields").Each(d + "tk", d + "tl", 8, x => x.Get(d + "tl", "Name").Get(d + "tl", "Attributes")).Release(d + "tk"))
                .Release(d + "ti")
                .GetBind(d + "tp", d + "td", "Properties").Each(d + "tp", d + "tq", 32, b => b.Get(d + "tq", "Name").Get(d + "tq", "Type").Get(d + "tq", "Value")).Release(d + "tp"))
            .Release(d + "ts")
            .GetBind(d + "rl", d, "Relations")
            .Each(d + "rl", d + "rx", 16, r => r.Get(d + "rx", "Name").Get(d + "rx", "Table").Get(d + "rx", "ForeignTable").Get(d + "rx", "Attributes")
                .GetBind(d + "rf", d + "rx", "Fields").Each(d + "rf", d + "ry", 8, x => x.Get(d + "ry", "Name").Get(d + "ry", "ForeignName")).Release(d + "rf"))
            .Release(d + "rl")
            .GetBind(d + "qs", d, "QueryDefs").Each(d + "qs", d + "qd", 16, q => q.Get(d + "qd", "Name").Get(d + "qd", "Type").Get(d + "qd", "SQL")).Release(d + "qs")
            .Snap(d, "mce" + tag, "SELECT * FROM MSysCompactErrors", 16, 8);
        for (int i = 0; i < selects.Length; i++)
        {
            p.Snap(d, $"q{tag}{i}", selects[i], 120, maxFields);
        }
        return p.Call(d, "Close").Release(d);
    }

    /// <summary>DBEngine.CompactDatabase(source, destination[, options...]), then its errors and the file state.</summary>
    public static P Compact(this P p, string tag, Arg source, Arg destination, params Arg[] options) =>
        p.Call("eng", "CompactDatabase", [source, destination, .. options]).Errors().Files("compact-" + tag);

    /// <summary>Reopen a compacted complex fixture read-only and read what its kind holds.</summary>
    public static P ComplexReadback(this P p, string role, string tag, Arg path)
    {
        string d = "d" + tag;
        p.OpenRo(d, path, null).Get(d, "Version").Catalog(tag, d);
        switch (role)
        {
            case "main":
                foreach (string name in new[] { "p1", "p2", "p3", "p4", "p6" })
                {
                    p.Group(d, "T", name, "Tags", tag + name);
                }
                break;
            case "types":
                foreach ((string name, _, _, _) in RComplexRebuild.Kinds)
                {
                    p.Group(d, "M" + name, "p1", "V", tag + name);
                }
                break;
            case "att":
                p.Group(d, "T", "p1", "Att", tag + "a1").Group(d, "T", "p2", "Att", tag + "a2").Export(d, "T", "p2", "Att", 1, $"export-{tag}.bin", tag + "x");
                break;
            case "calc":
                p.Snap(d, "rc" + tag, "SELECT * FROM T ORDER BY ID", 16, 32);
                break;
            default:
                p.Snap(d, "hn" + tag, "SELECT ID, Title, Notes FROM T ORDER BY ID", 8, 8)
                    .Snap(d, "h1" + tag, "SELECT ColumnHistory('T','Notes','ID=1') AS H FROM T WHERE ID = 1", 2, 2)
                    .Snap(d, "h2" + tag, "SELECT ColumnHistory('T','Notes','ID=2') AS H FROM T WHERE ID = 2", 2, 2);
                break;
        }
        return p.Call(d, "Close").Release(d);
    }

    /// <summary>ADO: Provider (ACE OLE DB 12), Data Source through Properties for the input of this role, Mode adModeRead, Open.</summary>
    public static P AdoOpen(this P p, string cn, string role) =>
        p.Create("ADODB.Connection", cn).Put(cn, "Provider", A.Str("Microsoft.ACE.OLEDB.12.0"))
            .GetBind(cn + "Props", cn, "Properties").Item(cn + "Ds", cn + "Props", A.Str("Data Source")).Put(cn + "Ds", "Value", A.In(role)).Release(cn + "Ds").Release(cn + "Props")
            .Put(cn, "Mode", A.I4(1)).Call(cn, "Open").Get(cn, "State");
}
