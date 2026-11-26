namespace CalcDb.Probes.Cards;

/// <summary>
/// Wave-3 ready-gate probes (r-q46-dao-ddl-multi):
/// (A1) DAO <c>TableDef.CreateField</c> + <c>TableDefs.Append</c> across non-Long scalar, Binary, GUID, LVAL
///      (<c>Text(n)</c>, <c>Text</c> default, <c>Byte</c>, <c>Integer</c>, <c>Single</c>, <c>Double</c>,
///      <c>Currency</c>, <c>Date</c>, <c>YesNo</c>, <c>Binary(16)</c>, <c>Memo</c>, <c>LongBinary</c>,
///      <c>GUID</c>, and <c>Long</c> with <c>Attributes = dbAutoIncrField</c>) plus representative multi-field
///      tables and attached <c>TableDef.CreateField</c> + <c>Fields.Append</c> (including <c>dbDecimal</c> 20
///      and duplicate 3191) on BOTH ACCDB and Jet 4 MDB;
/// (A2) DAO <c>Database.CreateRelation</c> + <c>Relations.Append</c> (plain enforced, <c>dbRelationUpdateCascade</c>,
///      <c>dbRelationDeleteCascade</c>, both cascades, <c>dbRelationDontEnforce</c>, 2-column compound, and error
///      cases: duplicate 3012, missing parent/child 3011, no fields 3366, orphan 3201) on ACCDB and Jet 4 MDB;
/// (B)  <c>6xzx</c> in-process multi-handle sharing (<c>OpenDatabase</c> twins on same <c>Workspace</c> and across
///      two <c>Workspaces</c>, ACCDB and Jet 4 MDB):
///      (a) RW + RW interleaved <c>INSERT</c>/<c>Update</c> (<c>0xE02</c> cadence, shared session buffer / commit
///          burst, page-1 extent release on first vs last handle close, and transaction visibility/3188 conflict);
///      (b) RW + ReadOnly (<c>OpenDatabase(..., False, True)</c>) visibility, held/fresh cursors, RO write errors,
///          <c>Idle(dbRefreshCache)</c>/reopen, and reverse open order (RO first, RW second);
///      (c) Exclusive (<c>OpenDatabase(..., True)</c>) + second handle open attempts (same vs second Workspace)
///          and closing order;
///      (d) DDL (<c>CREATE TABLE</c>/<c>DROP TABLE</c>) on <c>db1</c> + <c>db2.TableDefs.Refresh</c>.
/// Every writing probe keeps ALL file pages (<c>nativeRead keep:true</c>, catalog pages included) before and after
/// every write / Append / pause / Close.
/// </summary>
public sealed class RQ46DaoDdlMulti : ICard
{
    private const string Card = "r-q46-dao-ddl-multi";
    private const string EmptyAccdb = "r-cat-1-accdb-empty";
    private const string EmptyMdb = "r-cat-1-mdb-empty";
    private const int FullReadBytes = 524288;

    // DAO DataTypeEnum
    private const int DbBoolean = 1;
    private const int DbByte = 2;
    private const int DbInteger = 3;
    private const int DbLong = 4;
    private const int DbCurrency = 5;
    private const int DbSingle = 6;
    private const int DbDouble = 7;
    private const int DbDate = 8;
    private const int DbBinary = 9;
    private const int DbText = 10;
    private const int DbLongBinary = 11;
    private const int DbMemo = 12;
    private const int DbGuid = 15;
    private const int DbDecimal = 20;

    // DAO FieldAttributeEnum / RelationAttributeEnum / IdleEnum
    private const int DbAutoIncrField = 16;
    private const int RelDontEnforce = 2;
    private const int RelUpdateCascade = 256;
    private const int RelDeleteCascade = 4096;
    private const int DbRefreshCache = 8;

    private static readonly string[] CfRefs = ["R-CAT-1", "CAT-X", "R-FMT-1", "R-DAO-1", "II-04", "II-06", "W-07", "W-08"];
    private static readonly string[] RelRefs = ["R-CAT-1", "CAT-D", "CAT-X", "R-DAO-1", "II-06", "W-07", "W-08"];
    private static readonly string[] MultiRefs = ["R-LOCK-1", "EX-PUBLIC", "R-LOCK-2", "RO-INPROC", "R-TXN-1", "TXN-LOCAL", "II-09", "W-05", "W-08"];

    private sealed record FieldSpec(string Slug, string ColName, int DaoType, int? Size = null, int? Attributes = null, string? Label = null);

    private static readonly FieldSpec[] SingleFieldSpecs =
    [
        new("text20", "n", DbText, 20, null, "Text(20)"),
        new("byte", "n", DbByte, null, null, "Byte"),
        new("int", "n", DbInteger, null, null, "Integer"),
        new("single", "n", DbSingle, null, null, "Single"),
        new("double", "n", DbDouble, null, null, "Double"),
        new("currency", "n", DbCurrency, null, null, "Currency"),
        new("date", "n", DbDate, null, null, "Date"),
        new("yesno", "n", DbBoolean, null, null, "YesNo"),
        new("binary16", "n", DbBinary, 16, null, "Binary(16)"),
        new("memo", "n", DbMemo, null, null, "Memo"),
        new("ole", "n", DbLongBinary, null, null, "LongBinary"),
        new("guid", "n", DbGuid, null, null, "GUID"),
        new("autoincr", "n", DbLong, null, DbAutoIncrField, "Long (Attributes=dbAutoIncrField)"),
    ];

    public IEnumerable<Probe> Probes()
    {
        foreach (Probe p in PartACreateFieldProbes()) yield return p;
        foreach (Probe p in PartACreateRelationProbes()) yield return p;
        foreach (Probe p in PartBMultiHandleProbes()) yield return p;
    }

    // ================================================================================================
    // Part A1: DAO TableDef.CreateField + TableDefs.Append (non-Long types, multi-field, attached)

    private static IEnumerable<Probe> PartACreateFieldProbes()
    {
        // 1-field draft CreateField + TableDefs.Append across all non-Long types on ACCDB and Jet 4 MDB
        foreach ((string ext, string fixture, string fmt) in new[] { ("accdb", EmptyAccdb, "ACCDB"), ("mdb", EmptyMdb, "Jet 4 MDB") })
        {
            foreach (FieldSpec spec in SingleFieldSpecs)
            {
                yield return DraftSingleField(spec, ext, fixture, fmt);
            }
        }

        // Default-size dbText (size omitted) on ACCDB
        yield return DraftSingleField(new FieldSpec("text-default", "n", DbText, null, null, "Text (size omitted)"), "accdb", EmptyAccdb, "ACCDB");

        // Multi-field draft covering all 12 requested non-Long/AutoIncr types in one table (ACCDB & MDB)
        yield return DraftMultiMixed("cf-multi-mixed-accdb", EmptyAccdb, "ACCDB");
        yield return DraftMultiMixed("cf-multi-mixed-mdb", EmptyMdb, "Jet 4 MDB");

        // Attached TableDef.CreateField + Fields.Append (scalar/Text/Memo/duplicate 3191, and Decimal(20)) on ACCDB & MDB
        yield return AttachedCreateFieldScalar("cf-attached-scalar-accdb", EmptyAccdb, "ACCDB");
        yield return AttachedCreateFieldScalar("cf-attached-scalar-mdb", EmptyMdb, "Jet 4 MDB");
        yield return AttachedCreateFieldDecimal("cf-attached-decimal-accdb", EmptyAccdb, "ACCDB");
        yield return AttachedCreateFieldDecimal("cf-attached-decimal-mdb", EmptyMdb, "Jet 4 MDB");
    }

    private static Probe DraftSingleField(FieldSpec spec, string ext, string fixture, string fmt) => new(
        Card,
        $"cf-{spec.Slug}-{ext}",
        CfRefs,
        $"{fmt}: DAO CreateTableDef('T') + CreateField('{spec.ColName}', {spec.DaoType}{(spec.Size is int sz ? $", {sz}" : "")})" +
        $"{(spec.Attributes is int at ? $" with Attributes={at}" : "")} ({spec.Label}) + Fields.Append + TableDefs.Append. " +
        "Keeps all pages before, at-return of Append, after 1500ms pause (deferred write), and after Close; then read-only reopen reads Field metadata.",
        p =>
        {
            KeepAllPages(p, "before", "pg0");
            p.Create("DAO.DBEngine.120", "eng").Modules("after-create")
                .CallBind("db", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false))
                .CallBind("td", "db", "CreateTableDef", A.Str("T"));
            Arg[] cfArgs = spec.Size is int size
                ? [A.Str(spec.ColName), A.I4(spec.DaoType), A.I4(size)]
                : [A.Str(spec.ColName), A.I4(spec.DaoType)];
            p.CallBind("tf", "td", "CreateField", cfArgs)
                .Get("tf", "Name").Get("tf", "Type").Get("tf", "Size").Get("tf", "Attributes");
            if (spec.Attributes is int attr)
            {
                p.Put("tf", "Attributes", A.I4(attr)).Get("tf", "Attributes");
            }
            p.GetBind("tfs", "td", "Fields").Call("tfs", "Append", A.Obj("tf")).Release("tfs").Release("tf")
                .GetBind("tds", "db", "TableDefs").Call("tds", "Append", A.Obj("td")).Errors()
                .Release("tds").Release("td");
            KeepAllPages(p, "after-append-return", "pg1");
            p.Pause(1500);
            KeepAllPages(p, "after-append-1500ms", "pg2");
            p.Call("db", "Close").Release("db");
            KeepAllPages(p, "after-close", "pg3");
            ReadBackTableFields(p, "T", 4);
            p.Release("eng");
        })
    {
        Fixtures = [("main", fixture)],
    };

    private static Probe DraftMultiMixed(string name, string fixture, string fmt) => new(
        Card,
        name,
        CfRefs,
        $"{fmt}: DAO CreateTableDef('T') with 12 mixed fields (ID Long+dbAutoIncrField, B Byte, I Integer, S Single, D Double, C Currency, Dt Date, Yn YesNo, T Text(30), M Memo, O LongBinary, G GUID) + TableDefs.Append. " +
        "Keeps all pages before, at-return, after 1500ms pause, and after Close; then read-only reopen reads every Field's metadata.",
        p =>
        {
            KeepAllPages(p, "before", "pg0");
            p.Create("DAO.DBEngine.120", "eng").Modules("after-create")
                .CallBind("db", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false))
                .CallBind("td", "db", "CreateTableDef", A.Str("T"))
                .GetBind("tfs", "td", "Fields");
            (string Col, int Type, int? Size, int? Attr)[] cols =
            [
                ("ID", DbLong, null, DbAutoIncrField),
                ("B", DbByte, null, null),
                ("I", DbInteger, null, null),
                ("S", DbSingle, null, null),
                ("D", DbDouble, null, null),
                ("C", DbCurrency, null, null),
                ("Dt", DbDate, null, null),
                ("Yn", DbBoolean, null, null),
                ("T", DbText, 30, null),
                ("M", DbMemo, null, null),
                ("O", DbLongBinary, null, null),
                ("G", DbGuid, null, null),
            ];
            for (int i = 0; i < cols.Length; i++)
            {
                var c = cols[i];
                string fb = $"f{i}";
                Arg[] args = c.Size is int sz ? [A.Str(c.Col), A.I4(c.Type), A.I4(sz)] : [A.Str(c.Col), A.I4(c.Type)];
                p.CallBind(fb, "td", "CreateField", args);
                if (c.Attr is int at)
                {
                    p.Put(fb, "Attributes", A.I4(at));
                }
                p.Call("tfs", "Append", A.Obj(fb)).Release(fb);
            }
            p.Release("tfs")
                .GetBind("tds", "db", "TableDefs").Call("tds", "Append", A.Obj("td")).Errors()
                .Release("tds").Release("td");
            KeepAllPages(p, "after-append-return", "pg1");
            p.Pause(1500);
            KeepAllPages(p, "after-append-1500ms", "pg2");
            p.Call("db", "Close").Release("db");
            KeepAllPages(p, "after-close", "pg3");
            ReadBackTableFields(p, "T", 16);
            p.Release("eng");
        })
    {
        Fixtures = [("main", fixture)],
    };

    private static Probe AttachedCreateFieldScalar(string name, string fixture, string fmt) => new(
        Card,
        name,
        CfRefs,
        $"{fmt}: session 1 creates T (ID LONG CONSTRAINT PK PRIMARY KEY) with 1 row and closes; session 2 opens attached TableDefs('T'), appends ZLong (dbLong, Required=True), ZTxt (dbText, 20, AllowZeroLength=True), ZMemo (dbMemo), and attempts duplicate 'ZLong' (3191) via TableDef.CreateField + Fields.Append. " +
        "Keeps all pages after setup, after each Append at-return, after 1500ms pause, after dup 3191, and after Close.",
        p =>
        {
            p.Files("before-open").Create("DAO.DBEngine.120", "eng").Modules("after-create")
                .CallBind("db0", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false))
                .Call("db0", "Execute", A.Str("CREATE TABLE T (ID LONG CONSTRAINT PK PRIMARY KEY)"), A.I4(Dao.FailOnError))
                .Call("db0", "Execute", A.Str("INSERT INTO T VALUES (1)"), A.I4(Dao.FailOnError))
                .Call("db0", "Close").Release("db0");
            KeepAllPages(p, "after-setup", "pg0");

            p.CallBind("db", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false))
                .GetBind("tds", "db", "TableDefs").Item("td", "tds", A.Str("T")).GetBind("fls", "td", "Fields")
                .CallBind("f1", "td", "CreateField", A.Str("ZLong"), A.I4(DbLong))
                .Put("f1", "Required", A.Bool(true))
                .Call("fls", "Append", A.Obj("f1")).Errors().Release("f1");
            KeepAllPages(p, "after-add-long-return", "pg1");

            p.CallBind("f2", "td", "CreateField", A.Str("ZTxt"), A.I4(DbText), A.I4(20))
                .Put("f2", "AllowZeroLength", A.Bool(true))
                .Call("fls", "Append", A.Obj("f2")).Errors().Release("f2");
            KeepAllPages(p, "after-add-text-return", "pg2");

            p.CallBind("f3", "td", "CreateField", A.Str("ZMemo"), A.I4(DbMemo))
                .Call("fls", "Append", A.Obj("f3")).Errors().Release("f3");
            KeepAllPages(p, "after-add-memo-return", "pg3");

            p.Pause(1500);
            KeepAllPages(p, "after-adds-1500ms", "pg4");

            p.CallBind("fdup", "td", "CreateField", A.Str("zlong"), A.I4(DbLong))
                .Call("fls", "Append", A.Obj("fdup")).Errors().Release("fdup");
            KeepAllPages(p, "after-dup-err", "pg5");

            p.Release("fls").Release("td").Release("tds").Call("db", "Close").Release("db");
            KeepAllPages(p, "after-close", "pg6");
            ReadBackTableFields(p, "T", 8);
            p.Release("eng");
        })
    {
        Fixtures = [("main", fixture)],
    };

    private static Probe AttachedCreateFieldDecimal(string name, string fixture, string fmt) => new(
        Card,
        name,
        CfRefs,
        $"{fmt}: session 1 creates T (ID LONG CONSTRAINT PK PRIMARY KEY, V LONG) and closes; session 2 calls attached TableDefs('T').CreateField('FDec', 20) + Fields.Append (exact pattern of r-ado-1b/r-wire/r-dec-read). " +
        "Keeps all pages after setup, at-return, after 1500ms pause, and after Close; read-only reopen reads Fields metadata.",
        p =>
        {
            p.Files("before-open").Create("DAO.DBEngine.120", "eng").Modules("after-create")
                .CallBind("db0", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false))
                .Call("db0", "Execute", A.Str("CREATE TABLE T (ID LONG CONSTRAINT PK PRIMARY KEY, V LONG)"), A.I4(Dao.FailOnError))
                .Call("db0", "Close").Release("db0");
            KeepAllPages(p, "after-setup", "pg0");

            p.CallBind("db", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false))
                .GetBind("tds", "db", "TableDefs").Call("tds", "Refresh").Item("td", "tds", A.Str("T"))
                .CallBind("dfld", "td", "CreateField", A.Str("FDec"), A.I4(DbDecimal))
                .Get("dfld", "Name").Get("dfld", "Type").Get("dfld", "Size").Get("dfld", "Attributes")
                .GetBind("dfls", "td", "Fields").Call("dfls", "Append", A.Obj("dfld")).Errors()
                .Release("dfls").Release("dfld").Release("td").Release("tds");
            KeepAllPages(p, "after-add-dec-return", "pg1");
            p.Pause(1500);
            KeepAllPages(p, "after-add-dec-1500ms", "pg2");
            p.Call("db", "Close").Release("db");
            KeepAllPages(p, "after-close", "pg3");
            ReadBackTableFields(p, "T", 8);
            p.Release("eng");
        })
    {
        Fixtures = [("main", fixture)],
    };

    // ================================================================================================
    // Part A2: DAO Database.CreateRelation + Relations.Append (ACCDB & Jet 4 MDB)

    private static IEnumerable<Probe> PartACreateRelationProbes()
    {
        yield return CreateRelationSingle("rel-plain-accdb", EmptyAccdb, "ACCDB", "R_Plain", 0);
        yield return CreateRelationSingle("rel-upd-cascade-accdb", EmptyAccdb, "ACCDB", "R_Upd", RelUpdateCascade);
        yield return CreateRelationSingle("rel-del-cascade-accdb", EmptyAccdb, "ACCDB", "R_Del", RelDeleteCascade);
        yield return CreateRelationSingle("rel-both-cascade-accdb", EmptyAccdb, "ACCDB", "R_Both", RelUpdateCascade | RelDeleteCascade);
        yield return CreateRelationSingle("rel-noenforce-accdb", EmptyAccdb, "ACCDB", "R_NoEnf", RelDontEnforce);

        yield return CreateRelationSingle("rel-plain-mdb", EmptyMdb, "Jet 4 MDB", "R_Plain", 0);
        yield return CreateRelationSingle("rel-upd-cascade-mdb", EmptyMdb, "Jet 4 MDB", "R_Upd", RelUpdateCascade);
        yield return CreateRelationSingle("rel-del-cascade-mdb", EmptyMdb, "Jet 4 MDB", "R_Del", RelDeleteCascade);

        // Compound 2-column relation on ACCDB
        yield return new Probe(
            Card,
            "rel-compound-accdb",
            RelRefs,
            "ACCDB: session 1 creates P2(A LONG, B LONG, PK(A,B)) and C2(ID LONG PK, PA LONG, PB LONG) and closes; session 2 creates 2-column Relation 'R_Comp' via CreateRelation + 2x Relation.CreateField/ForeignName + Relations.Append (Attributes=0). Keeps all pages after setup, at-return, after 1500ms, and after Close.",
            p =>
            {
                p.Files("before-open").Create("DAO.DBEngine.120", "eng").Modules("after-create")
                    .CallBind("db0", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false))
                    .Call("db0", "Execute", A.Str("CREATE TABLE P2 (A LONG, B LONG, CONSTRAINT PK_P2 PRIMARY KEY (A, B))"), A.I4(Dao.FailOnError))
                    .Call("db0", "Execute", A.Str("CREATE TABLE C2 (ID LONG CONSTRAINT PK_C2 PRIMARY KEY, PA LONG, PB LONG)"), A.I4(Dao.FailOnError))
                    .Call("db0", "Close").Release("db0");
                KeepAllPages(p, "after-setup", "pg0");

                p.CallBind("db", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false))
                    .CallBind("rel", "db", "CreateRelation", A.Str("R_Comp"), A.Str("P2"), A.Str("C2"), A.I4(0))
                    .Get("rel", "Name").Get("rel", "Table").Get("rel", "ForeignTable").Get("rel", "Attributes")
                    .CallBind("rf1", "rel", "CreateField", A.Str("A")).Put("rf1", "ForeignName", A.Str("PA"))
                    .CallBind("rf2", "rel", "CreateField", A.Str("B")).Put("rf2", "ForeignName", A.Str("PB"))
                    .GetBind("rfs", "rel", "Fields").Call("rfs", "Append", A.Obj("rf1")).Call("rfs", "Append", A.Obj("rf2"))
                    .Release("rfs").Release("rf2").Release("rf1")
                    .GetBind("rels", "db", "Relations").Call("rels", "Append", A.Obj("rel")).Errors()
                    .Release("rels").Release("rel");
                KeepAllPages(p, "after-append-return", "pg1");
                p.Pause(1500);
                KeepAllPages(p, "after-append-1500ms", "pg2");
                p.Call("db", "Close").Release("db");
                KeepAllPages(p, "after-close", "pg3");
                ReadBackRelationsAndIndexes(p, "P2", "C2");
                p.Release("eng");
            })
        {
            Fixtures = [("main", EmptyAccdb)],
        };

        // Relation error cases on ACCDB and Jet 4 MDB
        yield return CreateRelationErrors("rel-errors-accdb", EmptyAccdb, "ACCDB");
        yield return CreateRelationErrors("rel-errors-mdb", EmptyMdb, "Jet 4 MDB");
    }

    private static Probe CreateRelationSingle(string name, string fixture, string fmt, string relName, int attributes) => new(
        Card,
        name,
        RelRefs,
        $"{fmt}: session 1 creates P(ID LONG PK, V LONG) and C(ID LONG PK, PID LONG) and closes; session 2 calls CreateRelation('{relName}', 'P', 'C', {attributes}) + CreateField('ID')/ForeignName='PID' + Relations.Append. " +
        "Keeps all pages after setup, at-return of Append, after 1500ms pause, and after Close; read-only reopen reads Relations, Relation.Fields, and P/C Indexes.",
        p =>
        {
            p.Files("before-open").Create("DAO.DBEngine.120", "eng").Modules("after-create")
                .CallBind("db0", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false))
                .Call("db0", "Execute", A.Str("CREATE TABLE P (ID LONG CONSTRAINT PK_P PRIMARY KEY, V LONG)"), A.I4(Dao.FailOnError))
                .Call("db0", "Execute", A.Str("CREATE TABLE C (ID LONG CONSTRAINT PK_C PRIMARY KEY, PID LONG)"), A.I4(Dao.FailOnError))
                .Call("db0", "Close").Release("db0");
            KeepAllPages(p, "after-setup", "pg0");

            p.CallBind("db", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false))
                .CallBind("rel", "db", "CreateRelation", A.Str(relName), A.Str("P"), A.Str("C"), A.I4(attributes))
                .Get("rel", "Name").Get("rel", "Table").Get("rel", "ForeignTable").Get("rel", "Attributes")
                .CallBind("rf", "rel", "CreateField", A.Str("ID")).Put("rf", "ForeignName", A.Str("PID"))
                .Get("rf", "Name").Get("rf", "ForeignName")
                .GetBind("rfs", "rel", "Fields").Call("rfs", "Append", A.Obj("rf")).Release("rfs").Release("rf")
                .GetBind("rels", "db", "Relations").Call("rels", "Append", A.Obj("rel")).Errors()
                .Release("rels").Release("rel");
            KeepAllPages(p, "after-append-return", "pg1");
            p.Pause(1500);
            KeepAllPages(p, "after-append-1500ms", "pg2");
            p.Call("db", "Close").Release("db");
            KeepAllPages(p, "after-close", "pg3");
            ReadBackRelationsAndIndexes(p, "P", "C");
            p.Release("eng");
        })
    {
        Fixtures = [("main", fixture)],
    };

    private static Probe CreateRelationErrors(string name, string fixture, string fmt) => new(
        Card,
        name,
        RelRefs,
        $"{fmt}: session 1 creates P(ID PK), C(ID PK, PID), COrph(ID PK, PID) with orphan row (1, 99) and relation R1(P->C), and closes; session 2 tests 5 refused Relations.Append cases: (1) duplicate name 'r1' (3012), (2) missing parent 'NoSuchP' (3011), (3) missing child 'NoSuchC' (3011), (4) no fields (3366), (5) existing orphan row in COrph (3201). " +
        "Keeps all pages after setup, after each refused Append, and after Close.",
        p =>
        {
            p.Files("before-open").Create("DAO.DBEngine.120", "eng").Modules("after-create")
                .CallBind("db0", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false))
                .Call("db0", "Execute", A.Str("CREATE TABLE P (ID LONG CONSTRAINT PK_P PRIMARY KEY)"), A.I4(Dao.FailOnError))
                .Call("db0", "Execute", A.Str("CREATE TABLE C (ID LONG CONSTRAINT PK_C PRIMARY KEY, PID LONG)"), A.I4(Dao.FailOnError))
                .Call("db0", "Execute", A.Str("CREATE TABLE COrph (ID LONG CONSTRAINT PK_CO PRIMARY KEY, PID LONG)"), A.I4(Dao.FailOnError))
                .Call("db0", "Execute", A.Str("INSERT INTO COrph VALUES (1, 99)"), A.I4(Dao.FailOnError))
                .AddRelation("R1", "P", "C", "ID", "PID", 0, "db0")
                .Call("db0", "Close").Release("db0");
            KeepAllPages(p, "after-setup", "pg0");

            p.CallBind("db", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false));

            // 1. Duplicate name (case-insensitive)
            p.AddRelation("r1", "P", "C", "ID", "PID", 0, "db").Errors();
            KeepAllPages(p, "after-err-dup", "pg1");

            // 2. Missing parent table
            p.AddRelation("R_BadP", "NoSuchP", "C", "ID", "PID", 0, "db").Errors();
            KeepAllPages(p, "after-err-badp", "pg2");

            // 3. Missing child table
            p.AddRelation("R_BadC", "P", "NoSuchC", "ID", "PID", 0, "db").Errors();
            KeepAllPages(p, "after-err-badc", "pg3");

            // 4. No fields appended to Relation
            p.CallBind("rnf", "db", "CreateRelation", A.Str("R_NoFields"), A.Str("P"), A.Str("C"), A.I4(0))
                .GetBind("rels4", "db", "Relations").Call("rels4", "Append", A.Obj("rnf")).Errors()
                .Release("rels4").Release("rnf");
            KeepAllPages(p, "after-err-nofields", "pg4");

            // 5. Enforced relation over existing orphan row
            p.AddRelation("R_Orph", "P", "COrph", "ID", "PID", 0, "db").Errors();
            KeepAllPages(p, "after-err-orphan", "pg5");

            p.Call("db", "Close").Release("db");
            KeepAllPages(p, "after-close", "pg6");
            ReadBackRelationsAndIndexes(p, "P", "C");
            p.Release("eng");
        })
    {
        Fixtures = [("main", fixture)],
    };

    // ================================================================================================
    // Part B: 6xzx in-process multi-handle sharing (OpenDatabase twins, ACCDB & Jet 4 MDB)

    private static IEnumerable<Probe> PartBMultiHandleProbes()
    {
        foreach ((string ext, string fixture, string fmt) in new[] { ("accdb", EmptyAccdb, "ACCDB"), ("mdb", EmptyMdb, "Jet 4 MDB") })
        {
            // (a) RW + RW interleaved INSERT/Update (same Workspace & two Workspaces)
            yield return MultiRwRw($"mh-rwrw-samews-{ext}", fixture, fmt, twoWorkspaces: false);
            yield return MultiRwRw($"mh-rwrw-twows-{ext}", fixture, fmt, twoWorkspaces: true);

            // (b) RW + ReadOnly visibility, blocked RO writes, Refresh/reopen, reverse open order
            yield return MultiRwRo($"mh-rwro-samews-{ext}", fixture, fmt, twoWorkspaces: false);
            yield return MultiRwRo($"mh-rwro-twows-{ext}", fixture, fmt, twoWorkspaces: true);

            // (c) Exclusive + second handle open attempts (same WS vs second WS) and closing order
            yield return MultiExclusive($"mh-excl-samews-{ext}", fixture, fmt, twoWorkspaces: false);
            yield return MultiExclusive($"mh-excl-twows-{ext}", fixture, fmt, twoWorkspaces: true);

            // (d) DDL (CREATE TABLE / DROP TABLE) on db1 + db2.TableDefs.Refresh (same WS & two WS)
            yield return MultiDdlRefresh($"mh-ddl-refresh-samews-{ext}", fixture, fmt, twoWorkspaces: false);
            yield return MultiDdlRefresh($"mh-ddl-refresh-twows-{ext}", fixture, fmt, twoWorkspaces: true);
        }
    }

    private static Probe MultiRwRw(string name, string fixture, string fmt, bool twoWorkspaces) => new(
        Card,
        name,
        MultiRefs,
        $"{fmt} ({(twoWorkspaces ? "two Workspaces" : "same Workspace")}): two RW OpenDatabase handles (db1, db2) on table T(ID PK, V, S) seeded with rows 1,2. " +
        "db1 INSERTs row 10; db2 immediately reads T (visibility) and INSERTs row 20 (same burst?); db1 edits row 1 via dynaset Edit/Update; pause 1500ms; " +
        (twoWorkspaces
            ? "ws0.BeginTrans + db1 INSERT row 30 -> db2 reads T (uncommitted visibility) and attempts UPDATE row 1 (3188?) -> ws0.CommitTrans -> db2 reads T; "
            : "ws0.BeginTrans + db1 INSERT row 30 -> db2 (same ws0) reads T and UPDATEs row 30 -> ws0.CommitTrans; ") +
        "close db1 (keep pages: extent/0xE02), then close db2 (keep pages). Keeps all pages at every step.",
        p =>
        {
            SetupSeedTable(p);
            p.GetBind("ws0", "eng", "Workspaces", A.I4(0));
            if (twoWorkspaces)
            {
                p.CallBind("ws2", "eng", "CreateWorkspace", A.Str("w2"), A.Str("Admin"), A.Str(""));
            }
            string wsSecond = twoWorkspaces ? "ws2" : "ws0";

            p.CallBind("db1", "ws0", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false))
                .CallBind("db2", wsSecond, "OpenDatabase", A.In(), A.Bool(false), A.Bool(false));
            KeepAllPages(p, "after-both-open", "pg1");

            // Interleaved rapid writes (within burst)
            p.Call("db1", "Execute", A.Str("INSERT INTO T VALUES (10, 100, 'd1')"), A.I4(Dao.FailOnError)).Get("db1", "RecordsAffected");
            KeepAllPages(p, "after-db1-ins10-live", "pg2");
            ReadRows(p, "db2", "r2a", "SELECT ID, V, S FROM T ORDER BY ID", 6);

            p.Call("db2", "Execute", A.Str("INSERT INTO T VALUES (20, 200, 'd2')"), A.I4(Dao.FailOnError)).Get("db2", "RecordsAffected");
            KeepAllPages(p, "after-db2-ins20-live", "pg3");
            ReadRows(p, "db1", "r1a", "SELECT ID, V, S FROM T ORDER BY ID", 6);

            // Dynaset Edit/Update on db1
            p.OpenRs("ed1", "db1", "SELECT ID, V, S FROM T WHERE ID = 1", Dao.OpenDynaset)
                .Call("ed1", "Edit")
                .GetBind("ed1f", "ed1", "Fields").Item("ed1v", "ed1f", A.Str("V")).Put("ed1v", "Value", A.I4(111))
                .Call("ed1", "Update").Errors()
                .Release("ed1v").Release("ed1f").Call("ed1", "Close").Release("ed1");
            KeepAllPages(p, "after-db1-upd1-live", "pg4");
            p.Pause(1500);
            KeepAllPages(p, "after-pause-live", "pg5");

            // Transaction test across the two handles
            p.Call("ws0", "BeginTrans")
                .Call("db1", "Execute", A.Str("INSERT INTO T VALUES (30, 300, 'txn')"), A.I4(Dao.FailOnError)).Get("db1", "RecordsAffected");
            ReadRows(p, "db2", "r2t", "SELECT ID, V, S FROM T ORDER BY ID", 8);
            p.Call("db2", "Execute", A.Str("UPDATE T SET V = 999 WHERE ID = 1"), A.I4(Dao.FailOnError)).Get("db2", "RecordsAffected").Errors();
            p.Call("ws0", "CommitTrans");
            KeepAllPages(p, "after-commit-live", "pg6");
            ReadRows(p, "db2", "r2c", "SELECT ID, V, S FROM T ORDER BY ID", 8);

            // Closing order: close db1 while db2 remains open
            p.Call("db1", "Close").Release("db1");
            KeepAllPages(p, "after-db1-close-live", "pg7");
            p.Call("db2", "Close").Release("db2");
            if (twoWorkspaces)
            {
                p.Call("ws2", "Close").Release("ws2");
            }
            p.Release("ws0");
            KeepAllPages(p, "after-both-close", "pg8");
            ReadBackTable(p, "T", 8);
            p.Release("eng");
        })
    {
        Fixtures = [("main", fixture)],
    };

    private static Probe MultiRwRo(string name, string fixture, string fmt, bool twoWorkspaces) => new(
        Card,
        name,
        MultiRefs,
        $"{fmt} ({(twoWorkspaces ? "two Workspaces" : "same Workspace")}): Part 1: db1 RW opened first, db2 ReadOnly (False, True) opened second; checks Updatable, held snapshot & dynaset on db2 across db1 INSERT+UPDATE (immediate, Move 0, Idle(dbRefreshCache), Requery, fresh snapshot), and blocked writes on db2 (Execute INSERT, dynaset Edit -> errors); closes db1 then db2. " +
        "Part 2: reverse open order — dbRo opened first, dbRw opened second, dbRw INSERTs row 20, dbRo reads and attempts write. Keeps all pages at every step.",
        p =>
        {
            SetupSeedTable(p);
            p.GetBind("ws0", "eng", "Workspaces", A.I4(0));
            if (twoWorkspaces)
            {
                p.CallBind("ws2", "eng", "CreateWorkspace", A.Str("w2"), A.Str("Admin"), A.Str(""));
            }
            string wsSecond = twoWorkspaces ? "ws2" : "ws0";

            // Part 1: RW first, RO second
            p.CallBind("db1", "ws0", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false))
                .CallBind("db2", wsSecond, "OpenDatabase", A.In(), A.Bool(false), A.Bool(true))
                .Get("db1", "Updatable").Get("db2", "Updatable")
                .OpenRs("rs2s", "db2", "SELECT ID, V, S FROM T ORDER BY ID", Dao.OpenSnapshot).ReadRow("rs2s")
                .OpenRs("rs2d", "db2", "SELECT ID, V, S FROM T ORDER BY ID", Dao.OpenDynaset).ReadRow("rs2d");

            p.Call("db1", "Execute", A.Str("INSERT INTO T VALUES (10, 100, 'rw')"), A.I4(Dao.FailOnError))
                .Call("db1", "Execute", A.Str("UPDATE T SET V = 11 WHERE ID = 1"), A.I4(Dao.FailOnError));
            KeepAllPages(p, "after-rw-writes-live", "pg1");

            // Held cursors on db2 before/after Move 0, Idle(dbRefreshCache), Requery + fresh snapshot
            p.Call("rs2d", "Move", A.I4(0)).ReadRow("rs2d")
                .Call("eng", "Idle", A.I4(DbRefreshCache))
                .Call("rs2d", "Move", A.I4(0)).ReadRow("rs2d")
                .Call("rs2s", "Move", A.I4(0)).ReadRow("rs2s")
                .Call("rs2d", "Requery").ReadAll("rs2d", 6, 4)
                .Call("rs2s", "Requery").ReadAll("rs2s", 6, 4);
            ReadRows(p, "db2", "rs2f", "SELECT ID, V, S FROM T ORDER BY ID", 6);

            // Blocked write attempts on RO handle db2
            p.Call("db2", "Execute", A.Str("INSERT INTO T VALUES (99, 999, 'ro')"), A.I4(Dao.FailOnError)).Errors()
                .Call("rs2d", "MoveFirst").Call("rs2d", "Edit").Errors()
                .Call("rs2d", "Close").Release("rs2d")
                .Call("rs2s", "Close").Release("rs2s");
            KeepAllPages(p, "after-ro-blocked-live", "pg2");

            p.Call("db1", "Close").Release("db1");
            KeepAllPages(p, "after-db1-close-live", "pg3");
            ReadRows(p, "db2", "rs2after", "SELECT ID, V, S FROM T ORDER BY ID", 6);
            p.Call("db2", "Close").Release("db2");
            KeepAllPages(p, "after-part1-close", "pg4");

            // Part 2: RO first, RW second
            p.CallBind("dbRo", "ws0", "OpenDatabase", A.In(), A.Bool(false), A.Bool(true))
                .CallBind("dbRw", wsSecond, "OpenDatabase", A.In(), A.Bool(false), A.Bool(false))
                .Get("dbRo", "Updatable").Get("dbRw", "Updatable")
                .Call("dbRw", "Execute", A.Str("INSERT INTO T VALUES (20, 200, 'rw2')"), A.I4(Dao.FailOnError)).Errors();
            KeepAllPages(p, "after-rev-rw-ins-live", "pg5");
            ReadRows(p, "dbRo", "rrev", "SELECT ID, V, S FROM T ORDER BY ID", 6);
            p.Call("dbRo", "Execute", A.Str("INSERT INTO T VALUES (98, 998, 'ro2')"), A.I4(Dao.FailOnError)).Errors()
                .Call("dbRo", "Close").Release("dbRo")
                .Call("dbRw", "Close").Release("dbRw");
            if (twoWorkspaces)
            {
                p.Call("ws2", "Close").Release("ws2");
            }
            p.Release("ws0");
            KeepAllPages(p, "after-part2-close", "pg6");
            p.Release("eng");
        })
    {
        Fixtures = [("main", fixture)],
    };

    private static Probe MultiExclusive(string name, string fixture, string fmt, bool twoWorkspaces) => new(
        Card,
        name,
        MultiRefs,
        $"{fmt} ({(twoWorkspaces ? "two Workspaces" : "same Workspace")}): " +
        "Phase 1: db1 Exclusive RW (True, False) writes row 10; second handle attempts Shared RW, Exclusive RW, Shared RO; if Shared RW opens, db1 closes first while second handle stays open, writes row 20, then closes. " +
        "Phase 2: Exclusive RO (True, True) first + second attempts Shared RW, Exclusive RW, Shared RO. " +
        "Phase 3: Shared RO (False, True) first + second attempts Exclusive RW (3704 ACCDB / 3045 MDB?). Keeps all pages after each phase.",
        p =>
        {
            SetupSeedTable(p);
            p.GetBind("ws0", "eng", "Workspaces", A.I4(0));
            if (twoWorkspaces)
            {
                p.CallBind("ws2", "eng", "CreateWorkspace", A.Str("w2"), A.Str("Admin"), A.Str(""));
            }
            string wsSecond = twoWorkspaces ? "ws2" : "ws0";

            // Phase 1: Exclusive RW first
            p.CallBind("e1", "ws0", "OpenDatabase", A.In(), A.Bool(true), A.Bool(false))
                .Call("e1", "Execute", A.Str("INSERT INTO T VALUES (10, 100, 'ex1')"), A.I4(Dao.FailOnError)).Errors()
                .CallBind("e2srw", wsSecond, "OpenDatabase", A.In(), A.Bool(false), A.Bool(false)).Errors()
                .CallBind("e2erw", wsSecond, "OpenDatabase", A.In(), A.Bool(true), A.Bool(false)).Errors()
                .CallBind("e2sro", wsSecond, "OpenDatabase", A.In(), A.Bool(false), A.Bool(true)).Errors();
            // Close e2erw and e2sro if opened, then close e1 while keeping e2srw open
            p.Call("e2erw", "Close").Release("e2erw")
                .Call("e2sro", "Close").Release("e2sro")
                .Call("e1", "Close").Release("e1");
            // Check whether e2srw stays usable and writable after exclusive opener e1 closed
            p.Call("e2srw", "Execute", A.Str("INSERT INTO T VALUES (20, 200, 'after-e1')"), A.I4(Dao.FailOnError)).Errors()
                .Call("e2srw", "Close").Release("e2srw");
            KeepAllPages(p, "after-phase1-close", "pg1");

            // Phase 2: Exclusive RO first
            p.CallBind("ero1", "ws0", "OpenDatabase", A.In(), A.Bool(true), A.Bool(true)).Errors()
                .CallBind("ero2srw", wsSecond, "OpenDatabase", A.In(), A.Bool(false), A.Bool(false)).Errors()
                .CallBind("ero2erw", wsSecond, "OpenDatabase", A.In(), A.Bool(true), A.Bool(false)).Errors()
                .CallBind("ero2sro", wsSecond, "OpenDatabase", A.In(), A.Bool(false), A.Bool(true)).Errors()
                .Call("ero2srw", "Close").Release("ero2srw")
                .Call("ero2erw", "Close").Release("ero2erw")
                .Call("ero2sro", "Close").Release("ero2sro")
                .Call("ero1", "Close").Release("ero1");
            KeepAllPages(p, "after-phase2-close", "pg2");

            // Phase 3: Shared RO first -> Exclusive RW second
            p.CallBind("sro1", "ws0", "OpenDatabase", A.In(), A.Bool(false), A.Bool(true)).Errors()
                .CallBind("sro2erw", wsSecond, "OpenDatabase", A.In(), A.Bool(true), A.Bool(false)).Errors()
                .Call("sro2erw", "Close").Release("sro2erw")
                .Call("sro1", "Close").Release("sro1");
            if (twoWorkspaces)
            {
                p.Call("ws2", "Close").Release("ws2");
            }
            p.Release("ws0");
            KeepAllPages(p, "after-phase3-close", "pg3");
            ReadBackTable(p, "T", 6);
            p.Release("eng");
        })
    {
        Fixtures = [("main", fixture)],
    };

    private static Probe MultiDdlRefresh(string name, string fixture, string fmt, bool twoWorkspaces) => new(
        Card,
        name,
        MultiRefs,
        $"{fmt} ({(twoWorkspaces ? "two Workspaces" : "same Workspace")}): db1 and db2 both open RW. db2 holds TableDefs (tds2), checks Count and Item('U') (3265). " +
        "db1 executes CREATE TABLE U (ID PK, V) + INSERT (1, 42). db2 checks tds2.Count and Item('U') BEFORE Refresh, calls tds2.Refresh, checks Count and Item('U') AFTER Refresh, and reads SELECT * FROM U. " +
        "Then db1 executes DROP TABLE U; db2 checks tds2.Count/Item('U') before and after Refresh; then closes db1 and db2. Keeps all pages at every step.",
        p =>
        {
            SetupSeedTable(p);
            p.GetBind("ws0", "eng", "Workspaces", A.I4(0));
            if (twoWorkspaces)
            {
                p.CallBind("ws2", "eng", "CreateWorkspace", A.Str("w2"), A.Str("Admin"), A.Str(""));
            }
            string wsSecond = twoWorkspaces ? "ws2" : "ws0";

            p.CallBind("db1", "ws0", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false))
                .CallBind("db2", wsSecond, "OpenDatabase", A.In(), A.Bool(false), A.Bool(false))
                .GetBind("tds2", "db2", "TableDefs")
                .Get("tds2", "Count")
                .Item("u0", "tds2", A.Str("U")).Errors().Release("u0");

            p.Call("db1", "Execute", A.Str("CREATE TABLE U (ID LONG CONSTRAINT PK_U PRIMARY KEY, V LONG)"), A.I4(Dao.FailOnError))
                .Call("db1", "Execute", A.Str("INSERT INTO U VALUES (1, 42)"), A.I4(Dao.FailOnError));
            KeepAllPages(p, "after-create-u-live", "pg1");

            // Check stale vs refreshed TableDefs on db2
            p.Get("tds2", "Count")
                .Item("u1", "tds2", A.Str("U")).Errors().Release("u1")
                .Call("tds2", "Refresh")
                .Get("tds2", "Count")
                .Item("u2", "tds2", A.Str("U")).Errors()
                .Get("u2", "Name").Get("u2", "RecordCount").Release("u2");
            ReadRows(p, "db2", "ru", "SELECT ID, V FROM U ORDER BY ID", 4);

            // Drop U on db1 and check db2 before/after Refresh
            p.Call("db1", "Execute", A.Str("DROP TABLE U"), A.I4(Dao.FailOnError)).Errors();
            KeepAllPages(p, "after-drop-u-live", "pg2");

            p.Get("tds2", "Count")
                .Item("u3", "tds2", A.Str("U")).Errors().Release("u3")
                .Call("tds2", "Refresh")
                .Get("tds2", "Count")
                .Item("u4", "tds2", A.Str("U")).Errors().Release("u4")
                .Release("tds2");

            p.Call("db1", "Close").Release("db1");
            KeepAllPages(p, "after-db1-close-live", "pg3");
            p.Call("db2", "Close").Release("db2");
            if (twoWorkspaces)
            {
                p.Call("ws2", "Close").Release("ws2");
            }
            p.Release("ws0");
            KeepAllPages(p, "after-both-close", "pg4");
            p.Release("eng");
        })
    {
        Fixtures = [("main", fixture)],
    };

    // ================================================================================================
    // Shared helpers

    private static void SetupSeedTable(P p)
    {
        p.Files("before-open").Create("DAO.DBEngine.120", "eng").Modules("after-create")
            .CallBind("db0", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false))
            .Call("db0", "Execute", A.Str("CREATE TABLE T (ID LONG CONSTRAINT PK_T PRIMARY KEY, V LONG, S TEXT(50))"), A.I4(Dao.FailOnError))
            .Call("db0", "Execute", A.Str("INSERT INTO T VALUES (1, 10, 'one')"), A.I4(Dao.FailOnError))
            .Call("db0", "Execute", A.Str("INSERT INTO T VALUES (2, 20, 'two')"), A.I4(Dao.FailOnError))
            .Call("db0", "Close").Release("db0");
        KeepAllPages(p, "after-setup", "pg0");
    }

    private static void ReadRows(P p, string db, string rs, string sql, int maxRows) =>
        p.OpenRs(rs, db, sql, Dao.OpenSnapshot).ReadAll(rs, maxRows, 6).Call(rs, "Close").Release(rs);

    private static void ReadBackTable(P p, string table, int maxRows) =>
        p.CallBind("dbro", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(true))
            .OpenRs("rsro", "dbro", $"SELECT * FROM {table} ORDER BY ID", Dao.OpenSnapshot)
            .ReadAll("rsro", maxRows, 8)
            .Call("rsro", "Close").Release("rsro")
            .Call("dbro", "Close").Release("dbro");

    private static void ReadBackTableFields(P p, string table, int maxFields) =>
        p.CallBind("dbro", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(true))
            .GetBind("tdsr", "dbro", "TableDefs").Item("tdr", "tdsr", A.Str(table))
            .GetBind("flsr", "tdr", "Fields").Get("flsr", "Count")
            .Each("flsr", "fr", maxFields, b => b
                .Get("fr", "Name").Get("fr", "Type").Get("fr", "Size")
                .Get("fr", "Attributes").Get("fr", "Required").Get("fr", "AllowZeroLength"))
            .Release("flsr").Release("tdr").Release("tdsr")
            .Call("dbro", "Close").Release("dbro");

    private static void ReadBackRelationsAndIndexes(P p, string parentTable, string childTable) =>
        p.CallBind("dbro", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(true))
            .GetBind("relsr", "dbro", "Relations").Get("relsr", "Count")
            .Each("relsr", "rr", 8, b => b
                .Get("rr", "Name").Get("rr", "Table").Get("rr", "ForeignTable").Get("rr", "Attributes")
                .GetBind("rfsr", "rr", "Fields").Get("rfsr", "Count")
                .Each("rfsr", "rfr", 4, f => f.Get("rfr", "Name").Get("rfr", "ForeignName"))
                .Release("rfsr"))
            .Release("relsr")
            .GetBind("tdsr", "dbro", "TableDefs")
            .Item("tdp", "tdsr", A.Str(parentTable)).GetBind("ixp", "tdp", "Indexes").Get("ixp", "Count")
            .Each("ixp", "ixpr", 8, ix => ix.Get("ixpr", "Name").Get("ixpr", "Primary").Get("ixpr", "Unique").Get("ixpr", "Foreign"))
            .Release("ixp").Release("tdp")
            .Item("tdc", "tdsr", A.Str(childTable)).GetBind("ixc", "tdc", "Indexes").Get("ixc", "Count")
            .Each("ixc", "ixcr", 8, ix => ix.Get("ixcr", "Name").Get("ixcr", "Primary").Get("ixcr", "Unique").Get("ixcr", "Foreign"))
            .Release("ixc").Release("tdc")
            .Release("tdsr")
            .Call("dbro", "Close").Release("dbro");

    private static void KeepAllPages(P p, string label, string handle) =>
        p.FilePages(label, 4096)
            .NativeOpen(handle, "input:main", Win.GenericRead, Win.ShareAll, Win.OpenExisting)
            .NativeRead(handle, 0, FullReadBytes, keep: true)
            .NativeClose(handle);
}
