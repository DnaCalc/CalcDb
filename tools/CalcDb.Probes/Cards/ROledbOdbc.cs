namespace CalcDb.Probes.Cards;

/// <summary>
/// R-OLEDB-1 (OLE DB provider) and R-ODBC-1 (ODBC driver), native discovery from ADODB with the ACE provider
/// Microsoft.ACE.OLEDB.12.0 on one STA per participant. What ADO can reach of each arm is asked here: provider
/// identity and service-component selection (OLE-ACT), connection, command and rowset property inventories and
/// requests (OLE-PROP), ordinal/type handling and prepare timing (OLE-ACCESS), typed field results (OLE-BUFFER),
/// batch and move requests (OLE-FETCH), row and bookmark lifetime (OLE-ROWLIFE), long-value chunks and record
/// streams (OLE-STREAM), parameter discovery, order, types and directions (OLE-PARAM), update failures and
/// cross-process visibility, locking and conflicts (OLE-UPDATE), local transactions with held rowsets (OLE-TXN),
/// the Errors channels and their lifetime (OLE-ERROR), the OpenSchema rowsets, and the ADODB, ADOX and JRO type
/// library inventories. The ODBC arm is reached only through MSDASQL connection strings that need no database path.
/// Two fixtures (ACCDB and MDB) are created first. Probes only ask; no answer is expected in code.
/// Deferred (no ADO route, or out of scope): OLE-ACCESS raw IAccessor/DBBINDING behaviour, OLE-BUFFER raw 48-byte
/// row layouts and canaries, OLE-FETCH HROW array ownership, OLE-ROWLIFE AddRefRows/ReleaseRows and retained
/// accessor, OLE-STREAM DBTYPE_IUNKNOWN callbacks, OLE-PARAM multiple parameter sets with explicit stride,
/// OLE-ERROR raw IErrorRecords, OLE-MANAGED (original Framework and .NET 10 clients), aggregation and raw
/// controlling-IUnknown behaviour; ODBC-LOAD direct driver entry, ODBC-ABI, ODBC-STATE, ODBC-DESC, ODBC-ARRAY,
/// ODBC-FETCH, ODBC-GETDATA, ODBC-NEEDDATA, ODBC-TXN, ODBC-CANCEL, ODBC-DIAG and ODBC-CLIENT beyond connection
/// attempts (all need a native ODBC consumer or a Dbq database path that probes cannot build).
/// </summary>
public sealed class ROledbOdbc : ICard
{
    private const string Card = "r-oledb-odbc";
    private const string Ace = "Microsoft.ACE.OLEDB.12.0";
    private const int Wait = 60000;

    // "v2": the first fixtures lost table Child to its unbracketed column name Note (3292; evidence/r-ddl-limits).
    private static readonly string Accdb = Card + "-v2-accdb";
    private static readonly string Mdb = Card + "-v2-mdb";

    /// <summary>ADO constants (values from the ADO type library, inventoried by r-str-ado-act/ado-describe).</summary>
    private static class Ado
    {
        public const int UseServer = 2, UseClient = 3;
        public const int OpenForwardOnly = 0, OpenKeyset = 1, OpenDynamic = 2, OpenStatic = 3;
        public const int LockReadOnly = 1, LockPessimistic = 2, LockOptimistic = 3, LockBatchOptimistic = 4;
        public const int CmdText = 1, CmdTable = 2, CmdStoredProc = 4, CmdTableDirect = 512;
        public const int ExecuteNoRecords = 0x80;
        public const int ModeRead = 1, ModeWrite = 2, ModeReadWrite = 3, ModeDenyRead = 4, ModeDenyWrite = 8, ModeExclusive = 12, ModeDenyNone = 16;
        public const int AffectCurrent = 1, AffectAll = 3;
        public const int ResyncUnderlying = 1, ResyncAll = 2;
        public const int ReadAll = -1, OpenStreamFromRecord = 4;
        public const int SmallInt = 2, Integer = 3, Single = 4, Double = 5, Currency = 6, Date = 7, Boolean = 11, Decimal = 14, UnsignedTinyInt = 17;
        public const int Guid = 72, VarWChar = 202, VarBinary = 204, LongVarBinary = 205;
        public const int ParamInput = 1, ParamOutput = 2, ParamInputOutput = 3, ParamReturnValue = 4;
        public const int CommitRetaining = 0x20000, AbortRetaining = 0x40000;
    }

    private static string[] Ole(params string[] arms) => ["R-OLEDB-1", .. arms];

    private static byte[] Pattern(int count, int step) => [.. Enumerable.Range(0, count).Select(i => (byte)(i * step))];

    public IEnumerable<Probe> Probes() =>
        Creations()
            .Concat(ActivationProbes())
            .Concat(PropertyProbes())
            .Concat(AccessBufferProbes())
            .Concat(FetchRowStreamProbes())
            .Concat(ParameterProbes())
            .Concat(UpdateTransactionProbes())
            .Concat(ErrorProbes())
            .Concat(SchemaProbes())
            .Concat(OdbcProbes());

    // Fixtures -------------------------------------------------------------------------------------------

    private static readonly string[] ItemFields = ["Code", "L", "D", "C", "N", "Dt", "Txt", "M", "B", "Bin", "Y", "Sh", "Bt", "Sg", "G"];

    /// <summary>
    /// Items rows (ID 1..5): a full row, a Null row, an empty-text/empty-binary row, a long-value row with extremes
    /// and a non-BMP-free wide text, and a row with a 50-character text and a ten-byte binary.
    /// </summary>
    private static readonly (string Field, Arg Value)[][] ItemRows =
    [
        [("Code", A.Str("a1")), ("L", A.I4(1)), ("D", A.R8(1.5)), ("C", A.Cy(2.25m)), ("N", A.Str("3.125")), ("Dt", A.Date(45292.0)),
         ("Txt", A.Str("alpha")), ("M", A.Str("memo one")), ("B", A.Bytes(Pattern(10, 1))), ("Bin", A.Bytes(Pattern(7, 3))), ("Y", A.Bool(true)),
         ("Sh", A.I2(7)), ("Bt", A.UI1(200)), ("Sg", A.R4(1.5f)), ("G", A.Str("{00112233-4455-6677-8899-AABBCCDDEEFF}"))],
        [("Code", A.Str("a2"))],
        [("Code", A.Str("b3")), ("L", A.I4(0)), ("D", A.R8(-0.5)), ("C", A.Cy(0m)), ("N", A.Str("0")), ("Dt", A.Date(0.5)),
         ("Txt", A.Str("")), ("M", A.Str("")), ("B", A.Bytes()), ("Bin", A.Bytes()), ("Y", A.Bool(false)), ("Sh", A.I2(0))],
        [("Code", A.Str("b4")), ("L", A.I4(int.MaxValue)), ("D", A.R8(1E300)), ("C", A.Cy(1234.5678m)), ("N", A.Str("-0.0001")), ("Dt", A.Date(2958465.0)),
         ("Txt", A.Str("A\u03A9")), ("M", A.Str(new string('m', 12000))), ("B", A.Bytes(Pattern(12000, 7))), ("Bin", A.Bytes(0xff)), ("Sh", A.I2(short.MinValue))],
        [("Code", A.Str("c5")), ("Txt", A.Str(new string('x', 50))), ("M", A.Str("tail")), ("Bin", A.Bytes(Pattern(10, 11))), ("Bt", A.UI1(0))],
    ];

    private static IEnumerable<Probe> Creations()
    {
        yield return Creation("v2-accdb", "accdb", Dao.Version120);
        yield return Creation("v2-mdb", "mdb", Dao.Version40);
    }

    private static Probe Creation(string name, string ext, int version)
    {
        Arg file = A.Out($"{Card}-{name}.{ext}");
        return new Probe(Card, "create-" + name, Ole("OLE-PROP", "OLE-FETCH", "OLE-PARAM", "OLE-UPDATE", "OLE-ERROR", "OLE-BUFFER"),
            $"R-OLEDB-1 fixture ({ext.ToUpperInvariant()}), created by DBEngine.CreateDatabase(path, dbLangGeneral, {version}) and the closed file kept: " +
            "Items (AutoNumber key, unique Code, Long, Double, Currency, Decimal added by CreateField type 20, Date/Time, Text(50), Memo, Long Binary, Binary(16), Yes/No, Integer, Byte, Single, GUID; a table Description property) with five rows " +
            "(full, Null, empty text/binary, long memo and binary values with extremes, 50-character text and ten-byte binary); Seq (32 rows); Child with a foreign key to Items; Req with a required column and, through ADO, a CHECK constraint; " +
            "saved queries QParam, QMulti (five parameters declared in a different order from their use), QAction, QEmpty and QSel; an index on (L DESC, D); and in ACCDB a table with WITH COMPRESSION columns made through ADO.",
            p =>
            {
                p.Modules("before-create").Create().Modules("after-create").Get("eng", "Version")
                    .CallBind("db", "eng", "CreateDatabase", file, A.Str(Dao.LangGeneral), A.I4(version))
                    .Get("db", "Version")
                    .Call("db", "Execute", A.Str("CREATE TABLE Items (ID COUNTER CONSTRAINT PK_Items PRIMARY KEY, Code TEXT(10) CONSTRAINT UX_Items_Code UNIQUE, L LONG, D DOUBLE, C CURRENCY, " +
                        "Dt DATETIME, Txt TEXT(50), M MEMO, B LONGBINARY, Bin BINARY(16), Y YESNO, Sh SHORT, Bt BYTE, Sg SINGLE, G GUID)"))
                    .GetBind("tdfs", "db", "TableDefs").Item("tdf", "tdfs", A.Str("Items"))
                    .CallBind("fn", "tdf", "CreateField", A.Str("N"), A.I4(20)).GetBind("tfl", "tdf", "Fields").Call("tfl", "Append", A.Obj("fn"))
                    .Release("fn").Release("tfl")
                    .CallBind("tprop", "tdf", "CreateProperty", A.Str("Description"), A.I4(10), A.Str("OLE DB probe items table"))
                    .GetBind("tprops", "tdf", "Properties").Call("tprops", "Append", A.Obj("tprop"))
                    .Release("tprop").Release("tprops").Release("tdf").Release("tdfs");

                p.OpenRs("rs", "db", "Items", Dao.OpenTable).GetBind("fl", "rs", "Fields");
                foreach (string field in ItemFields)
                {
                    p.Item("f_" + field, "fl", A.Str(field));
                }
                foreach ((string Field, Arg Value)[] row in ItemRows)
                {
                    p.Call("rs", "AddNew");
                    foreach ((string field, Arg value) in row)
                    {
                        p.Put("f_" + field, "Value", value);
                    }
                    p.Call("rs", "Update");
                }
                foreach (string field in ItemFields)
                {
                    p.Release("f_" + field);
                }
                p.Release("fl").Call("rs", "Close").Release("rs");

                p.Call("db", "Execute", A.Str("CREATE INDEX IX_Items_LD ON Items (L DESC, D)"))
                    .Call("db", "Execute", A.Str("CREATE TABLE Seq (ID COUNTER CONSTRAINT PK_Seq PRIMARY KEY, V LONG)"))
                    .Call("db", "Execute", A.Str("INSERT INTO Seq (V) VALUES (1)"));
                for (int i = 0; i < 5; i++)
                {
                    p.Call("db", "Execute", A.Str("INSERT INTO Seq (V) SELECT V + 1 FROM Seq"));
                }
                p.Call("db", "Execute", A.Str("CREATE TABLE Child (ID LONG CONSTRAINT PK_Child PRIMARY KEY, TID LONG CONSTRAINT FK_Child_Items REFERENCES Items (ID), [Note] TEXT(20))"))
                    .Call("db", "Execute", A.Str("INSERT INTO Child VALUES (1, 1, 'c1')"))
                    .Call("db", "Execute", A.Str("INSERT INTO Child VALUES (2, 1, 'c2')"))
                    .Call("db", "Execute", A.Str("CREATE TABLE Req (ID LONG CONSTRAINT PK_Req PRIMARY KEY, R TEXT(5) NOT NULL, V LONG)"))
                    .Call("db", "Execute", A.Str("INSERT INTO Req VALUES (1, 'r1', 5)"))
                    .CallBind("q1", "db", "CreateQueryDef", A.Str("QParam"), A.Str("PARAMETERS pCode Text(10); SELECT ID, Code, Txt FROM Items WHERE Code = pCode;"))
                    .CallBind("q2", "db", "CreateQueryDef", A.Str("QMulti"),
                        A.Str("PARAMETERS pB Text(10), pA Long, pC Currency, pD DateTime, pY YesNo; SELECT ID, Code, L, C, Dt, Y FROM Items WHERE L = pA OR Code = pB OR C = pC OR Dt = pD OR Y = pY;"))
                    .CallBind("q3", "db", "CreateQueryDef", A.Str("QAction"), A.Str("PARAMETERS pT Text(50), pId Long; UPDATE Items SET Txt = pT WHERE ID = pId;"))
                    .CallBind("q4", "db", "CreateQueryDef", A.Str("QEmpty"), A.Str("SELECT * FROM Items WHERE 1=0;"))
                    .CallBind("q5", "db", "CreateQueryDef", A.Str("QSel"), A.Str("SELECT ID, Code FROM Items ORDER BY ID;"))
                    .Release("q1").Release("q2").Release("q3").Release("q4").Release("q5")
                    .Call("db", "Close").Release("db").Release("eng").Modules("after-dao-close");

                p.Create("ADODB.Connection", "cn").Put("cn", "Provider", A.Str(Ace));
                SetDataSource(p, "cn", file);
                p.Call("cn", "Open").Get("cn", "State")
                    .Call("cn", "Execute", A.Str("ALTER TABLE Req ADD CONSTRAINT CK_Req_V CHECK (V >= 0)"), A.Missing, A.I4(Ado.CmdText | Ado.ExecuteNoRecords));
                if (ext == "accdb")
                {
                    p.Call("cn", "Execute", A.Str("CREATE TABLE Cmp (ID LONG CONSTRAINT PK_Cmp PRIMARY KEY, Txt TEXT(100) WITH COMPRESSION, M MEMO WITH COMPRESSION)"), A.Missing, A.I4(Ado.CmdText | Ado.ExecuteNoRecords))
                        .Call("cn", "Execute", A.Str("INSERT INTO Cmp VALUES (1, 'compressed text', 'compressed memo')"), A.Missing, A.I4(Ado.CmdText | Ado.ExecuteNoRecords));
                }
                AdoErrors(p, "cn");
                p.Call("cn", "Close").Release("cn").Modules("after-close").Files("after-close");
            })
        { CreatesFixture = true };
    }

    // OLE-ACT and the type library inventories ---------------------------------------------------------------

    private static readonly string[] IdentityProps =
    [
        "Provider Name", "Provider Version", "Provider Friendly Name", "OLE DB Version", "DBMS Name", "DBMS Version", "Active Sessions",
        "Data Source", "Data Source Name", "Initial Catalog", "Current Catalog", "Jet OLEDB:Engine Type", "Locale Identifier", "Mode",
        "Persist Security Info", "OLE DB Services", "Prompt", "Extended Properties", "User ID", "Window Handle",
    ];

    private static IEnumerable<Probe> ActivationProbes()
    {
        yield return Q("act-provider-identity", Ole("OLE-ACT"),
            "OLE-ACT: on an ADODB.Connection record loaded modules before creation, after creation, after Provider = ACE, after Open, after a second Open of the same file, and after Close/Release; read Version, Provider, State and the " +
            "named identity properties (Provider Name/Version/Friendly Name, OLE DB Version, DBMS Name/Version, Active Sessions, Data Source, Initial Catalog, Jet OLEDB:Engine Type, Locale Identifier, Mode, OLE DB Services, Prompt, Extended Properties, User ID, Window Handle) " +
            "before and after Open, then the whole Properties inventory. Asks the provider module, version, session count and effective initialization values.",
            p =>
            {
                p.Modules("before-create").Create("ADODB.Connection", "cn").Modules("after-create").Get("cn", "Version")
                    .Put("cn", "Provider", A.Str(Ace)).Get("cn", "Provider").Modules("after-provider");
                NamedProps(p, "cn", "provider", IdentityProps);
                SetDataSource(p, "cn", A.In());
                p.Put("cn", "Mode", A.I4(Ado.ModeRead)).Call("cn", "Open").Get("cn", "State").Modules("after-open");
                NamedProps(p, "cn", "open", IdentityProps);
                Props(p, "cn", "open");
                AdoOpen(p, "c2");
                p.Modules("after-second-open");
                NamedProps(p, "cn", "two", ["Active Sessions"]);
                NamedProps(p, "c2", "two", ["Active Sessions"]);
                p.Call("c2", "Close").Release("c2");
                NamedProps(p, "cn", "after-c2-close", ["Active Sessions"]);
                p.Call("cn", "Close").Get("cn", "State").Modules("after-close").Release("cn").Modules("after-release");
            });

        yield return Q("act-services-variants", Ole("OLE-ACT", "OLE-PROP"),
            "OLE-ACT: five Connections open the same file with connection strings 'Provider=ACE;OLE DB Services=<v>;Mode=Read' for v = -1, -2, -4, -5 and 0 (Data Source through Properties). For each: State, loaded modules after Open, " +
            "the properties OLE DB Services, Provider Name and Active Sessions, then a client-cursor static and a server keyset Recordset open on Items (State, RecordCount, loaded modules, Connection Errors), Close and loaded modules. " +
            "Asks which service components the provider selection loads and whether a client cursor still opens.",
            p =>
            {
                (string Bind, string Value)[] services = [("sv1", "-1"), ("sv2", "-2"), ("sv3", "-4"), ("sv4", "-5"), ("sv5", "0")];
                foreach ((string bind, string value) in services)
                {
                    p.Create("ADODB.Connection", bind).Put(bind, "ConnectionString", A.Str($"Provider={Ace};OLE DB Services={value};Mode=Read")).Get(bind, "ConnectionString");
                    SetDataSource(p, bind, A.In());
                    p.Call(bind, "Open", A.Missing).Get(bind, "State").Modules("after-open-" + bind);
                    NamedProps(p, bind, "svc", ["OLE DB Services", "Provider Name", "Active Sessions"]);
                    AdoRs(p, bind + "c", "SELECT ID, Code FROM Items ORDER BY ID", bind, Ado.UseClient, Ado.OpenStatic, Ado.LockReadOnly, Ado.CmdText);
                    p.Get(bind + "c", "RecordCount").Modules("after-client-" + bind).Call(bind + "c", "Close").Release(bind + "c");
                    AdoRs(p, bind + "s", "SELECT ID, Code FROM Items ORDER BY ID", bind, Ado.UseServer, Ado.OpenKeyset, Ado.LockReadOnly, Ado.CmdText);
                    p.Get(bind + "s", "RecordCount").Call(bind + "s", "Close").Release(bind + "s");
                    AdoErrors(p, bind);
                    p.Call(bind, "Close").Release(bind).Modules("after-close-" + bind);
                }
            });

        yield return Q("act-describe-adodb", Ole("OLE-ACT", "OLE-MANAGED"),
            "Type library inventory: create ADODB.Command and describe its whole containing type library through ITypeInfo (every type, interface, dispid, invoke kind, vtable slot and parameter type); then, on an open ACE connection, " +
            "describe the live type of the Properties collection, a Property, a Recordset, its Fields collection, a Field and that Field's Properties, the Errors collection (after a failing Execute) and an Error, and a Command's Parameters collection and a Parameter. " +
            "Asks the declared OLE DB-facing ADO surface and the live interfaces each object returns.",
            p =>
            {
                p.Modules("before-create").Create("ADODB.Command", "cmd0").Modules("after-create").Describe("cmd0", wholeLibrary: true).Release("cmd0");
                AdoOpen(p, "cn");
                p.GetBind("cnp", "cn", "Properties").Describe("cnp").Item("pr", "cnp", A.I4(0)).Describe("pr").Release("pr").Release("cnp");
                AdoRs(p, "rs", "SELECT ID, Code FROM Items ORDER BY ID", "cn", Ado.UseServer, Ado.OpenKeyset, Ado.LockReadOnly, Ado.CmdText);
                p.Describe("rs").GetBind("fl", "rs", "Fields").Describe("fl").Item("f0", "fl", A.I4(0)).Describe("f0")
                    .GetBind("fp", "f0", "Properties").Describe("fp").Release("fp").Release("f0").Release("fl").Call("rs", "Close").Release("rs")
                    .Call("cn", "Execute", A.Str("SELEC bad"), A.Missing, A.I4(Ado.CmdText))
                    .GetBind("errs", "cn", "Errors").Describe("errs").Item("e0", "errs", A.I4(0)).Describe("e0").Release("e0").Release("errs")
                    .Create("ADODB.Command", "cmd").PutRef("cmd", "ActiveConnection", "cn").Put("cmd", "CommandText", A.Str("SELECT ID FROM Items WHERE Code = ?"))
                    .Put("cmd", "CommandType", A.I4(Ado.CmdText))
                    .GetBind("prms", "cmd", "Parameters").Describe("prms")
                    .CallBind("prm", "cmd", "CreateParameter", A.Str("x"), A.I4(Ado.VarWChar), A.I4(Ado.ParamInput), A.I4(10), A.Str("a1")).Describe("prm")
                    .Release("prm").Release("prms").Release("cmd");
                p.Finish("cn");
            });

        yield return Bare("act-describe-companion-libraries", Ole("OLE-ACT", "OLE-MANAGED"),
            "Type library inventory of the two OLE DB companion libraries: create ADOX.Catalog and describe its whole containing type library, then create JRO.JetEngine and describe its whole containing type library, " +
            "recording loaded modules before and after each creation and after release. Asks whether each is creatable on the x64 worker and what it declares (a creation failure is the observation).",
            p => p.Modules("before-adox").Create("ADOX.Catalog", "cat").Modules("after-adox").Describe("cat", wholeLibrary: true).Release("cat").Modules("after-adox-release")
                .Create("JRO.JetEngine", "jro").Modules("after-jro").Describe("jro", wholeLibrary: true).Release("jro").Modules("after-jro-release"));
    }

    // OLE-PROP ------------------------------------------------------------------------------------------------

    private static IEnumerable<Probe> PropertyProbes()
    {
        yield return Q2("prop-connection-inventory", Ole("OLE-PROP", "OLE-ACT"),
            "OLE-PROP: for the ACCDB and the MDB fixture, on a Connection with Provider = ACE read the full Properties inventory (Name, Type, Value, Attributes); set Data Source and Mode = adModeRead and read State, Mode, " +
            "Attributes, IsolationLevel and timeouts; Open and read the inventory again; Close and read it a third time. Asks which initialization, data-source-information and session properties exist and how each differs between ACCDB and MDB and between states.",
            p =>
            {
                foreach (string role in new[] { "main", "mdb" })
                {
                    string cn = "c_" + role;
                    p.Create("ADODB.Connection", cn).Put(cn, "Provider", A.Str(Ace));
                    Props(p, cn, "provider");
                    SetDataSource(p, cn, A.In(role));
                    p.Put(cn, "Mode", A.I4(Ado.ModeRead)).Call(cn, "Open");
                    ConnState(p, cn);
                    Props(p, cn, "open");
                    p.Call(cn, "Close").Get(cn, "State");
                    Props(p, cn, "closed");
                    p.Release(cn);
                }
            });

        yield return Q("prop-modes", Ole("OLE-PROP", "OLE-UPDATE"),
            "OLE-PROP combinations of the Mode connection property (0 unspecified, read, write, read/write, read/write + deny none, read/write + exclusive, read/write + deny write, read + deny read): on a read/write copy a first Connection opens with the mode " +
            "(State, Mode, properties Mode and Jet OLEDB:Database Locking Mode, a no-op UPDATE of Items, Errors), then a second Connection in the same process opens the same file with read/write + deny none (State, Errors, UPDATE attempt, Errors), then both close. " +
            "Asks which mode combinations open, what each records and how the second open and the writes are affected.",
            p =>
            {
                (int Mode, string Label)[] modes =
                [
                    (0, "unspecified"), (Ado.ModeRead, "read"), (Ado.ModeWrite, "write"), (Ado.ModeReadWrite, "readwrite"),
                    (Ado.ModeReadWrite | Ado.ModeDenyNone, "readwrite-denynone"), (Ado.ModeReadWrite | Ado.ModeExclusive, "readwrite-exclusive"),
                    (Ado.ModeReadWrite | Ado.ModeDenyWrite, "readwrite-denywrite"), (Ado.ModeRead | Ado.ModeDenyRead, "read-denyread"),
                ];
                const string noop = "UPDATE Items SET Txt = Txt WHERE ID = 1";
                for (int i = 0; i < modes.Length; i++)
                {
                    string a = "ma" + i, b = "mb" + i;
                    p.Create("ADODB.Connection", a).Put(a, "Provider", A.Str(Ace));
                    SetDataSource(p, a, A.In());
                    if (modes[i].Mode != 0)
                    {
                        p.Put(a, "Mode", A.I4(modes[i].Mode));
                    }
                    p.Call(a, "Open").Get(a, "State").Get(a, "Mode");
                    NamedProps(p, a, "mode", ["Mode", "Jet OLEDB:Database Locking Mode"]);
                    p.Call(a, "Execute", A.Str(noop), A.Missing, A.I4(Ado.CmdText | Ado.ExecuteNoRecords));
                    AdoErrors(p, a);
                    p.Create("ADODB.Connection", b).Put(b, "Provider", A.Str(Ace));
                    SetDataSource(p, b, A.In());
                    p.Put(b, "Mode", A.I4(Ado.ModeReadWrite | Ado.ModeDenyNone)).Call(b, "Open").Get(b, "State");
                    AdoErrors(p, b);
                    p.Call(b, "Execute", A.Str(noop), A.Missing, A.I4(Ado.CmdText | Ado.ExecuteNoRecords));
                    AdoErrors(p, b);
                    p.Call(b, "Close").Release(b).Call(a, "Close").Release(a);
                }
            });

        yield return Q("prop-jet-options", Ole("OLE-PROP"),
            "OLE-PROP provider-specific Jet OLEDB properties requested through the connection string (Database Locking Mode 1 and 0, Transaction Commit Mode 1, Global Partial Bulk Ops 2, Encrypt Database True, Database Password wrongpw and empty, Engine Type 5) " +
            "and through retained Property wrappers set before Open (Database Locking Mode = 1; System database = an absent work file): after Open read the property value, State, Provider and Errors, then Close. " +
            "Asks which requests are accepted, ignored or refused and what value each reads back.",
            p =>
            {
                (string Bind, string Text, string Prop)[] cases =
                [
                    ("j1", "Jet OLEDB:Database Locking Mode=1", "Jet OLEDB:Database Locking Mode"),
                    ("j2", "Jet OLEDB:Database Locking Mode=0", "Jet OLEDB:Database Locking Mode"),
                    ("j3", "Jet OLEDB:Transaction Commit Mode=1", "Jet OLEDB:Transaction Commit Mode"),
                    ("j4", "Jet OLEDB:Global Partial Bulk Ops=2", "Jet OLEDB:Global Partial Bulk Ops"),
                    ("j5", "Jet OLEDB:Encrypt Database=True", "Jet OLEDB:Encrypt Database"),
                    ("j6", "Jet OLEDB:Database Password=wrongpw", "Jet OLEDB:Database Password"),
                    ("j7", "Jet OLEDB:Database Password=", "Jet OLEDB:Database Password"),
                    ("j8", "Jet OLEDB:Engine Type=5", "Jet OLEDB:Engine Type"),
                ];
                foreach ((string bind, string text, string prop) in cases)
                {
                    p.Create("ADODB.Connection", bind).Put(bind, "ConnectionString", A.Str($"Provider={Ace};{text}")).Get(bind, "ConnectionString");
                    SetDataSource(p, bind, A.In());
                    p.Call(bind, "Open", A.Missing).Get(bind, "State");
                    NamedProps(p, bind, "after", [prop]);
                    AdoErrors(p, bind);
                    p.Call(bind, "Close").Release(bind);
                }
                (string Bind, string Prop, Arg Value)[] viaProps =
                [
                    ("j9", "Jet OLEDB:Database Locking Mode", A.I4(1)),
                    ("j10", "Jet OLEDB:System database", A.Out("absent.mdw")),
                ];
                foreach ((string bind, string prop, Arg value) in viaProps)
                {
                    p.Create("ADODB.Connection", bind).Put(bind, "Provider", A.Str(Ace))
                        .GetBind(bind + "p", bind, "Properties").Item(bind + "i", bind + "p", A.Str(prop)).Get(bind + "i", "Attributes").Get(bind + "i", "Value")
                        .Put(bind + "i", "Value", value).Get(bind + "i", "Value");
                    SetDataSource(p, bind, A.In());
                    p.Call(bind, "Open").Get(bind, "State").Get(bind + "i", "Value");
                    AdoErrors(p, bind);
                    p.Release(bind + "i").Release(bind + "p").Call(bind, "Close").Release(bind);
                }
            });

        yield return Q("prop-rowset-requests", Ole("OLE-PROP", "OLE-ERROR"),
            "OLE-PROP and the warning channel: on a read/write copy eight Recordsets are opened with requested (location, cursor type, lock type, options) combinations, including conflicting ones (forward-only with batch optimistic or pessimistic, client batch, adCmdTableDirect keyset). " +
            "For each: State, effective CursorType, LockType and CursorLocation, the Supports options, the whole Recordset.Properties inventory (Name, Type, Value, Attributes), then the Connection Errors, which are cleared. " +
            "Asks the effective rowset properties for each request and whether a downgrade is reported as an Errors entry.",
            p =>
            {
                const string items = "SELECT ID, Code, Txt FROM Items ORDER BY ID";
                (string Bind, int Loc, int Type, int Lock, int Opts, string Src)[] reqs =
                [
                    ("q1", Ado.UseServer, Ado.OpenForwardOnly, Ado.LockReadOnly, Ado.CmdText, items),
                    ("q2", Ado.UseServer, Ado.OpenKeyset, Ado.LockOptimistic, Ado.CmdText, items),
                    ("q3", Ado.UseServer, Ado.OpenStatic, Ado.LockReadOnly, Ado.CmdText, items),
                    ("q4", Ado.UseServer, Ado.OpenDynamic, Ado.LockPessimistic, Ado.CmdText, items),
                    ("q5", Ado.UseClient, Ado.OpenStatic, Ado.LockBatchOptimistic, Ado.CmdText, items),
                    ("q6", Ado.UseServer, Ado.OpenForwardOnly, Ado.LockBatchOptimistic, Ado.CmdText, items),
                    ("q7", Ado.UseServer, Ado.OpenForwardOnly, Ado.LockPessimistic, Ado.CmdText, items),
                    ("q8", Ado.UseServer, Ado.OpenKeyset, Ado.LockOptimistic, Ado.CmdTableDirect, "Items"),
                ];
                AdoOpen(p, "cn", readOnly: false);
                foreach ((string bind, int loc, int type, int lck, int opts, string src) in reqs)
                {
                    AdoRs(p, bind, src, "cn", loc, type, lck, opts);
                    RsState(p, bind);
                    foreach (int option in SupportsOptions)
                    {
                        p.Call(bind, "Supports", A.I4(option));
                    }
                    Props(p, bind, "rs");
                    AdoErrors(p, "cn");
                    ClearErrors(p, "cn");
                    p.Call(bind, "Close").Release(bind);
                }
                p.Finish("cn");
            });

        yield return Q("prop-command-set-execute", Ole("OLE-PROP", "OLE-FETCH"),
            "OLE-PROP requests through a Command: read the Command.Properties inventory before execution; then, in order, set Maximum Rows = 5, 0 and 100, Bookmarkable = True, Hold Rows = True, Others' Changes Visible = True, Updatability = 7, " +
            "Immobile Rows = True, Literal Bookmarks = True and the unknown name Jet OLEDB:Nonexistent through the retained collection, reading Attributes and the value back, and after each Execute of 'SELECT ID, V FROM Seq ORDER BY ID' " +
            "read State, cursor settings, RecordCount, the Supports of adBookmark and every row. Asks which settable properties the Command accepts, which change the result and how many rows are fetched.",
            p =>
            {
                AdoOpen(p, "cn");
                p.Create("ADODB.Command", "cmd").PutRef("cmd", "ActiveConnection", "cn").Put("cmd", "CommandText", A.Str("SELECT ID, V FROM Seq ORDER BY ID"))
                    .Put("cmd", "CommandType", A.I4(Ado.CmdText));
                Props(p, "cmd", "initial");
                p.GetBind("cp", "cmd", "Properties");
                (string Name, Arg Value)[] sets =
                [
                    ("Maximum Rows", A.I4(5)), ("Maximum Rows", A.I4(0)), ("Maximum Rows", A.I4(100)), ("Bookmarkable", A.Bool(true)),
                    ("Hold Rows", A.Bool(true)), ("Others' Changes Visible", A.Bool(true)), ("Updatability", A.I4(7)), ("Immobile Rows", A.Bool(true)),
                    ("Literal Bookmarks", A.Bool(true)), ("Jet OLEDB:Nonexistent", A.I4(1)),
                ];
                for (int i = 0; i < sets.Length; i++)
                {
                    string pr = "pr" + i, rs = "r" + i;
                    p.Item(pr, "cp", A.Str(sets[i].Name)).Get(pr, "Attributes").Put(pr, "Value", sets[i].Value).Get(pr, "Value")
                        .CallBind(rs, "cmd", "Execute");
                    RsState(p, rs);
                    p.Get(rs, "RecordCount").Call(rs, "Supports", A.I4(0x2000));
                    AdoReadAll(p, rs, 40, 2);
                    AdoErrors(p, "cn");
                    p.Release(rs).Release(pr);
                }
                p.Release("cp").Release("cmd").Finish("cn");
            });
    }

    /// <summary>CursorOptionEnum values asked through Recordset.Supports.</summary>
    private static readonly int[] SupportsOptions = [0x1000400, 0x4000, 0x2000, 0x1000800, 0x80000, 0x100, 0x100000, 0x200, 0x40000, 0x20000, 0x400000, 0x1008000, 0x10000];

    // OLE-ACCESS and OLE-BUFFER --------------------------------------------------------------------------------

    private static IEnumerable<Probe> AccessBufferProbes()
    {
        yield return Q("access-ordinals", Ole("OLE-ACCESS"),
            "OLE-ACCESS as projected by ADO: on 'SELECT ID, ID, Code AS Dup, Code AS Dup, Txt FROM Items' ask Fields.Item for ordinals 0, 4, 5, -1, 999, names ID, id, Dup, NoSuch and empty, and Null, Empty, a Double 1.0, the string \"1\", an Integer 2 and a Boolean as the index (Name, Type and Value of each); " +
            "then Properties.Item with -1, 999, an empty name and an unknown name, Errors.Item(0) on an empty collection, Parameters.Item on a Command without parameters, Fields.Item on the closed Recordset, and the field names of a join with repeated column names. " +
            "Asks which indexes bind, which fail and with what errors.",
            p =>
            {
                AdoOpen(p, "cn");
                p.CallBind("r", "cn", "Execute", A.Str("SELECT ID, ID, Code AS Dup, Code AS Dup, Txt FROM Items ORDER BY ID"), A.Missing, A.I4(Ado.CmdText))
                    .GetBind("fl", "r", "Fields").Get("fl", "Count");
                (string Bind, Arg Index)[] indexes =
                [
                    ("o1", A.I4(0)), ("o2", A.I4(4)), ("o3", A.I4(5)), ("o4", A.I4(-1)), ("o5", A.I4(999)), ("o6", A.Str("ID")), ("o7", A.Str("id")),
                    ("o8", A.Str("Dup")), ("o9", A.Str("NoSuch")), ("o10", A.Str("")), ("o11", A.Null), ("o12", A.Empty), ("o13", A.R8(1.0)),
                    ("o14", A.Str("1")), ("o15", A.I2(2)), ("o16", A.Bool(true)),
                ];
                foreach ((string bind, Arg index) in indexes)
                {
                    p.Item(bind, "fl", index).Get(bind, "Name").Get(bind, "Type").Get(bind, "Value").Release(bind);
                }
                p.GetBind("cp", "cn", "Properties");
                (string Bind, Arg Index)[] propIndexes = [("p1", A.I4(-1)), ("p2", A.I4(999)), ("p3", A.Str("")), ("p4", A.Str("NoSuchProperty"))];
                foreach ((string bind, Arg index) in propIndexes)
                {
                    p.Item(bind, "cp", index).Get(bind, "Name").Release(bind);
                }
                p.Release("cp").GetBind("ce", "cn", "Errors").Get("ce", "Count").Item("ee", "ce", A.I4(0)).Get("ee", "Number").Release("ce")
                    .Create("ADODB.Command", "cmd").PutRef("cmd", "ActiveConnection", "cn").Put("cmd", "CommandText", A.Str("SELECT ID FROM Items")).Put("cmd", "CommandType", A.I4(Ado.CmdText))
                    .GetBind("cprm", "cmd", "Parameters").Get("cprm", "Count").Item("pa", "cprm", A.I4(0)).Item("pb", "cprm", A.Str("x")).Release("cprm")
                    .Call("r", "Close").Item("oc", "fl", A.I4(0)).Get("oc", "Name").Release("oc").Release("fl").Release("r")
                    .CallBind("j", "cn", "Execute", A.Str("SELECT * FROM Items INNER JOIN Child ON Items.ID = Child.TID"), A.Missing, A.I4(Ado.CmdText))
                    .GetBind("jf", "j", "Fields").Each("jf", "jc", 32, b => b.Get("jc", "Name").Get("jc", "Type"))
                    .Item("j1", "jf", A.Str("ID")).Get("j1", "Value").Item("j2", "jf", A.Str("Child.ID")).Get("j2", "Value").Release("j1").Release("j2").Release("jf").Release("j");
                AdoErrors(p, "cn");
                p.Finish("cn");
            });

        yield return Q("access-prepared-deferred", Ole("OLE-ACCESS", "OLE-ERROR"),
            "OLE-ACCESS immediate versus deferred validation: Commands with Prepared False/True and text 'SELEC bad', a missing table, and a valid parameter marker, where Prepared and CommandText are set, then Parameters.Count is read (forcing parameter description) in some cases, then Execute; " +
            "after each step the Connection Errors are read. A prepared Command with valid text is executed, its text changed to another valid text and executed again, then changed to bad text and executed, then back. " +
            "Asks at which call each error first surfaces and whether re-execution after a text change re-describes.",
            p =>
            {
                AdoOpen(p, "cn");
                (string Bind, bool Prepared, string Sql, bool CountFirst)[] cases =
                [
                    ("pa", false, "SELEC bad", false), ("pb", true, "SELEC bad", false), ("pc", true, "SELECT * FROM NoSuchTable", false),
                    ("pd", true, "SELECT * FROM NoSuchTable", true), ("pe", false, "SELECT * FROM NoSuchTable", true), ("pf", true, "SELECT ID FROM Items WHERE Code = ?", true),
                ];
                foreach ((string bind, bool prepared, string sql, bool countFirst) in cases)
                {
                    p.Create("ADODB.Command", bind).PutRef(bind, "ActiveConnection", "cn").Put(bind, "Prepared", A.Bool(prepared)).Get(bind, "Prepared")
                        .Put(bind, "CommandText", A.Str(sql)).Put(bind, "CommandType", A.I4(Ado.CmdText));
                    AdoErrors(p, "cn");
                    if (countFirst)
                    {
                        p.GetBind(bind + "p", bind, "Parameters").Get(bind + "p", "Count").Release(bind + "p");
                        AdoErrors(p, "cn");
                    }
                    p.CallBind(bind + "r", bind, "Execute").Get(bind + "r", "State");
                    AdoErrors(p, "cn");
                    ClearErrors(p, "cn");
                    p.Release(bind + "r").Release(bind);
                }
                p.Create("ADODB.Command", "pg").PutRef("pg", "ActiveConnection", "cn").Put("pg", "Prepared", A.Bool(true)).Put("pg", "CommandType", A.I4(Ado.CmdText));
                (string Sql, string Bind)[] texts =
                [
                    ("SELECT ID FROM Seq WHERE ID < 4 ORDER BY ID", "g1"), ("SELECT ID FROM Seq WHERE ID > 30 ORDER BY ID", "g2"),
                    ("SELEC bad", "g3"), ("SELECT ID FROM Seq WHERE ID < 4 ORDER BY ID", "g4"),
                ];
                foreach ((string sql, string bind) in texts)
                {
                    p.Put("pg", "CommandText", A.Str(sql)).CallBind(bind, "pg", "Execute").Get(bind, "State");
                    AdoReadAll(p, bind, 8, 1);
                    AdoErrors(p, "cn");
                    ClearErrors(p, "cn");
                    p.Release(bind);
                }
                p.Release("pg").Finish("cn");
            });

        yield return Q("buffer-typed-server", Ole("OLE-BUFFER"),
            "OLE-BUFFER typed results through Connection.Execute (default forward-only server cursor): every Field's Name, Type, DefinedSize, Precision, NumericScale and Attributes of 'SELECT * FROM Items'; the column-level Properties of ID, Code, N, Txt, M and B; " +
            "then Value and ActualSize of every field of each row (Null row, empty text and binary, 12,000-character memo and binary, wide text, ten-byte binary). Asks the VARIANT tag, length and metadata the provider publishes for each column type.",
            p =>
            {
                AdoOpen(p, "cn");
                p.CallBind("r", "cn", "Execute", A.Str("SELECT * FROM Items ORDER BY ID"), A.Missing, A.I4(Ado.CmdText)).Get("r", "State");
                BufferRead(p, "r");
                p.Finish("cn");
            });

        yield return Q("buffer-typed-client", Ole("OLE-BUFFER"),
            "OLE-BUFFER typed results through a client-cursor static read-only Recordset on 'SELECT * FROM Items': the same field metadata, column-level properties, and Value and ActualSize of every field of every row as buffer-typed-server. " +
            "Asks whether the client cursor service changes any tag, length or metadata compared with the provider's own rowset.",
            p =>
            {
                AdoOpen(p, "cn");
                AdoRs(p, "r", "SELECT * FROM Items ORDER BY ID", "cn", Ado.UseClient, Ado.OpenStatic, Ado.LockReadOnly, Ado.CmdText);
                p.Get("r", "RecordCount");
                BufferRead(p, "r");
                p.Finish("cn");
            });
    }

    private static void BufferRead(P p, string rs)
    {
        p.GetBind("rf", rs, "Fields").Get("rf", "Count")
            .Each("rf", "fd", 32, b => b.Get("fd", "Name").Get("fd", "Type").Get("fd", "DefinedSize").Get("fd", "Precision").Get("fd", "NumericScale").Get("fd", "Attributes"));
        foreach (string name in new[] { "ID", "Code", "N", "Txt", "M", "B" })
        {
            string fb = "fp_" + name;
            p.Item(fb, "rf", A.Str(name));
            Props(p, fb, "col");
            p.Release(fb);
        }
        p.Release("rf");
        AdoReadAll(p, rs, 8, 32, actualSize: true);
        p.Call(rs, "Close").Release(rs);
    }

    // OLE-FETCH, OLE-ROWLIFE and OLE-STREAM -------------------------------------------------------------------

    private static IEnumerable<Probe> FetchRowStreamProbes()
    {
        yield return Q("fetch-batches", Ole("OLE-FETCH"),
            "OLE-FETCH on Seq (32 rows), for a server forward-only and a server keyset read-only Recordset: read CacheSize, set it to 1, 2, 0, 40, -1 and 3 reading it back, then GetRows(0), GetRows(1), GetRows(2), GetRows(100) and GetRows(1) at the end, " +
            "reading EOF, BOF and AbsolutePosition after each, then RecordCount; the Connection Errors at the end. Asks the actual batch sizes accepted, the array returned past the end and the cursor transition.",
            p =>
            {
                AdoOpen(p, "cn");
                (string Bind, int Type)[] cursors = [("fw", Ado.OpenForwardOnly), ("ks", Ado.OpenKeyset)];
                foreach ((string bind, int type) in cursors)
                {
                    AdoRs(p, bind, "SELECT ID FROM Seq ORDER BY ID", "cn", Ado.UseServer, type, Ado.LockReadOnly, Ado.CmdText);
                    p.Get(bind, "CacheSize");
                    foreach (int size in new[] { 1, 2, 0, 40, -1, 3 })
                    {
                        p.Put(bind, "CacheSize", A.I4(size)).Get(bind, "CacheSize");
                    }
                    foreach (int rows in new[] { 0, 1, 2, 100, 1 })
                    {
                        p.Call(bind, "GetRows", A.I4(rows)).Get(bind, "EOF").Get(bind, "BOF").Get(bind, "AbsolutePosition");
                    }
                    p.Get(bind, "RecordCount");
                    AdoErrors(p, "cn");
                    p.Call(bind, "Close").Release(bind);
                }
                p.Finish("cn");
            });

        yield return Q("fetch-moves", Ole("OLE-FETCH"),
            "OLE-FETCH move requests on Seq, for a server forward-only and a server keyset read-only Recordset: a fixed sequence of MoveNext, MovePrevious, MoveFirst, Move(n), MoveLast and moves past either end, " +
            "reading BOF, EOF, AbsolutePosition and the ID value after each step. Asks which requests the cursor accepts, how restart and backward requests behave and the cursor transition at the ends.",
            p =>
            {
                AdoOpen(p, "cn");
                (string Member, Arg[] Args)[] forward =
                [
                    ("MoveNext", []), ("MoveNext", []), ("MoveNext", []), ("MovePrevious", []), ("MoveFirst", []), ("Move", [A.I4(2)]), ("Move", [A.I4(-1)]),
                    ("Move", [A.I4(100)]), ("MoveLast", []), ("MoveNext", []), ("MoveFirst", []),
                ];
                (string Member, Arg[] Args)[] keyset =
                [
                    ("MoveNext", []), ("Move", [A.I4(5)]), ("Move", [A.I4(-2)]), ("Move", [A.I4(100)]), ("MovePrevious", []), ("MoveLast", []), ("MoveNext", []),
                    ("MovePrevious", []), ("MovePrevious", []), ("MoveFirst", []), ("MovePrevious", []), ("MovePrevious", []), ("Move", [A.I4(0)]),
                ];
                foreach ((string bind, int type, (string Member, Arg[] Args)[] steps) in new[] { ("fw", Ado.OpenForwardOnly, forward), ("ks", Ado.OpenKeyset, keyset) })
                {
                    AdoRs(p, bind, "SELECT ID FROM Seq ORDER BY ID", "cn", Ado.UseServer, type, Ado.LockReadOnly, Ado.CmdText);
                    p.GetBind(bind + "f", bind, "Fields").Item(bind + "id", bind + "f", A.Str("ID")).Get(bind + "id", "Value");
                    foreach ((string member, Arg[] args) in steps)
                    {
                        p.Call(bind, member, args).Get(bind, "BOF").Get(bind, "EOF").Get(bind, "AbsolutePosition").Get(bind + "id", "Value");
                    }
                    AdoErrors(p, "cn");
                    p.Release(bind + "id").Release(bind + "f").Call(bind, "Close").Release(bind);
                }
                p.Finish("cn");
            });

        yield return Q("rowlife-clone-delete-requery", Ole("OLE-ROWLIFE", "OLE-UPDATE"),
            "OLE-ROWLIFE on a read/write copy: a server keyset optimistic Recordset on Items with Fields ID retained, Clone moved independently (ID of each); the original deletes row 2 (Status, ID of the deleted row, MoveNext, ID, MovePrevious, ID); " +
            "the clone moves across the deleted row; Resync(adAffectAll, adResyncAllValues), RecordCount, ID; Requery, RecordCount, retained ID; the clone after the original's Requery; then the original Closes and the retained Field, the clone's State and ID are read. " +
            "Asks row identity and validity of retained field wrappers, deleted-row access and clone behaviour.",
            p =>
            {
                AdoOpen(p, "cn", readOnly: false);
                AdoRs(p, "rs", "SELECT ID, Code, Txt FROM Items ORDER BY ID", "cn", Ado.UseServer, Ado.OpenKeyset, Ado.LockOptimistic, Ado.CmdText);
                p.GetBind("fl", "rs", "Fields").Item("fid", "fl", A.Str("ID")).Get("fid", "Value")
                    .CallBind("rc", "rs", "Clone").GetBind("cf", "rc", "Fields").Item("cid", "cf", A.Str("ID")).Call("rc", "MoveLast").Get("cid", "Value").Get("fid", "Value")
                    .Call("rs", "MoveNext").Call("rs", "Delete").Get("rs", "Status").Get("fid", "Value").Call("rs", "MoveNext").Get("fid", "Value")
                    .Call("rs", "MovePrevious").Get("fid", "Value").Get("rs", "BOF").Get("rs", "EOF")
                    .Call("rc", "MoveFirst").Call("rc", "MoveNext").Get("rc", "Status").Get("cid", "Value").Call("rc", "MoveNext").Get("cid", "Value")
                    .Call("rs", "Resync", A.I4(Ado.AffectAll), A.I4(Ado.ResyncAll)).Get("rs", "RecordCount").Get("fid", "Value")
                    .Call("rs", "Requery").Get("rs", "RecordCount").Get("fid", "Value").Get("cid", "Value").Get("rc", "State")
                    .Call("rs", "Close").Get("fid", "Value").Get("rc", "State").Get("cid", "Value");
                AdoErrors(p, "cn");
                p.Release("cid").Release("cf").Release("rc").Release("fid").Release("fl").Release("rs").Finish("cn");
            });

        yield return Q("stream-getchunk", Ole("OLE-STREAM", "OLE-BUFFER"),
            "OLE-STREAM output as projected by ADO, on a server forward-only and a client static read-only Recordset of 'SELECT ID, B, M FROM Items': for row 1 read ActualSize and GetChunk(3) five times of the ten-byte binary then Value; rows 2 and 3 (Null, empty) GetChunk(3); " +
            "row 4 (12,000-byte binary, 12,000-character memo) GetChunk(4096) and GetChunk(5000) interleaved between the two fields until exhausted, then GetChunk(0), GetChunk(-1), GetChunk(65536) and Value. Asks the piece sizes, the exhausted-call result and whether reading chunks interferes with Value.",
            p =>
            {
                AdoOpen(p, "cn");
                (string Bind, int Loc, int Type)[] kinds = [("sf", Ado.UseServer, Ado.OpenForwardOnly), ("sc", Ado.UseClient, Ado.OpenStatic)];
                foreach ((string bind, int loc, int type) in kinds)
                {
                    string fb = bind + "b", fm = bind + "m";
                    AdoRs(p, bind, "SELECT ID, B, M FROM Items ORDER BY ID", "cn", loc, type, Ado.LockReadOnly, Ado.CmdText);
                    p.GetBind(bind + "f", bind, "Fields").Item(fb, bind + "f", A.Str("B")).Item(fm, bind + "f", A.Str("M")).Get(fb, "ActualSize");
                    for (int i = 0; i < 5; i++)
                    {
                        p.Call(fb, "GetChunk", A.I4(3));
                    }
                    p.Get(fb, "Value");
                    for (int row = 2; row <= 3; row++)
                    {
                        p.Call(bind, "MoveNext").Get(fb, "ActualSize").Call(fb, "GetChunk", A.I4(3)).Get(fb, "Value");
                    }
                    p.Call(bind, "MoveNext").Get(fb, "ActualSize").Get(fm, "ActualSize");
                    for (int i = 0; i < 4; i++)
                    {
                        p.Call(fb, "GetChunk", A.I4(4096)).Call(fm, "GetChunk", A.I4(5000));
                    }
                    p.Call(fb, "GetChunk", A.I4(0)).Call(fb, "GetChunk", A.I4(-1)).Call(fb, "GetChunk", A.I4(65536)).Get(fb, "Value").Get(fm, "Value");
                    p.Call(bind, "MoveNext").Call(fb, "GetChunk", A.I4(3)).Get(fb, "ActualSize");
                    AdoErrors(p, "cn");
                    p.Release(fb).Release(fm).Release(bind + "f").Call(bind, "Close").Release(bind);
                }
                p.Finish("cn");
            });

        yield return Q("stream-appendchunk", Ole("OLE-STREAM", "OLE-UPDATE"),
            "OLE-STREAM input as projected by ADO, on a read/write copy and a server keyset optimistic Recordset of 'SELECT ID, Code, Txt, B, M FROM Items': AddNew with AppendChunk of 3 then 4 bytes (ActualSize and Value before and after Update); " +
            "AddNew with AppendChunk then CancelUpdate; AppendChunk of 2 bytes onto the existing ten-byte value of row 1 (no Edit call) then Update; AppendChunk on the Text(50) field; AddNew with two memo AppendChunks and a Null AppendChunk; " +
            "AddNew with AppendChunk then MoveNext without Update; AddNew with an empty AppendChunk; then every stored row. Asks the accepted pieces, EditMode transitions, partial input on abandonment and the stored values.",
            p =>
            {
                AdoOpen(p, "cn", readOnly: false);
                AdoRs(p, "rs", "SELECT ID, Code, Txt, B, M FROM Items ORDER BY ID", "cn", Ado.UseServer, Ado.OpenKeyset, Ado.LockOptimistic, Ado.CmdText);
                p.GetBind("fl", "rs", "Fields").Item("fid", "fl", A.Str("ID")).Item("fcode", "fl", A.Str("Code")).Item("ftxt", "fl", A.Str("Txt"))
                    .Item("fb", "fl", A.Str("B")).Item("fm", "fl", A.Str("M"))
                    .Call("rs", "AddNew").Put("fcode", "Value", A.Str("ch1")).Call("fb", "AppendChunk", A.Bytes(1, 2, 3)).Call("fb", "AppendChunk", A.Bytes(4, 5, 6, 7))
                    .Get("fb", "ActualSize").Get("fb", "Value").Call("rs", "Update").Get("fid", "Value").Get("fb", "ActualSize").Get("fb", "Value")
                    .Call("rs", "AddNew").Put("fcode", "Value", A.Str("ch2")).Call("fb", "AppendChunk", A.Bytes(9, 9, 9)).Call("rs", "CancelUpdate").Get("rs", "EditMode").Get("rs", "Status")
                    .Call("rs", "MoveFirst").Call("fb", "AppendChunk", A.Bytes(0xaa, 0xbb)).Get("rs", "EditMode").Call("rs", "Update").Get("fb", "ActualSize").Get("fb", "Value")
                    .Call("ftxt", "AppendChunk", A.Str("x")).Get("rs", "EditMode").Call("rs", "CancelUpdate")
                    .Call("rs", "AddNew").Put("fcode", "Value", A.Str("ch3")).Call("fm", "AppendChunk", A.Str("ab")).Call("fm", "AppendChunk", A.Str("cd")).Call("fb", "AppendChunk", A.Null)
                    .Call("rs", "Update").Get("fm", "Value").Get("fb", "Value")
                    .Call("rs", "AddNew").Put("fcode", "Value", A.Str("ch4")).Call("fb", "AppendChunk", A.Bytes(1, 2, 3)).Call("rs", "MoveNext").Get("rs", "EditMode")
                    .Call("rs", "MoveLast").Get("fcode", "Value").Get("fb", "Value")
                    .Call("rs", "AddNew").Put("fcode", "Value", A.Str("ch5")).Call("fb", "AppendChunk", A.Bytes()).Call("rs", "Update").Get("fb", "ActualSize").Get("fb", "Value");
                AdoErrors(p, "cn");
                p.Release("fid").Release("fcode").Release("ftxt").Release("fb").Release("fm").Release("fl").Call("rs", "Close").Release("rs");
                p.CallBind("rk", "cn", "Execute", A.Str("SELECT ID, Code, B, M FROM Items ORDER BY ID"), A.Missing, A.I4(Ado.CmdText));
                AdoReadAll(p, "rk", 16, 4, actualSize: true);
                p.Finish("cn");
            });

        yield return Q("stream-record-from-row", Ole("OLE-STREAM", "OLE-ROWLIFE"),
            "OLE-STREAM and the row object: open an ADODB.Record on a server Recordset of 'SELECT ID, Code FROM Items' (State, RecordType, ParentURL, Mode and each Field's Name, Type and Value), " +
            "then open an ADODB.Stream on that Record with adModeRead and adOpenStreamFromRecord (State, Size, Type, Charset, ReadText, Close), and open a Record on the connection. Asks whether the provider serves a row object or a default stream.",
            p =>
            {
                AdoOpen(p, "cn");
                AdoRs(p, "rs", "SELECT ID, Code FROM Items ORDER BY ID", "cn", Ado.UseServer, Ado.OpenKeyset, Ado.LockReadOnly, Ado.CmdText);
                p.Create("ADODB.Record", "rec").Call("rec", "Open", A.Obj("rs")).Get("rec", "State").Get("rec", "RecordType").Get("rec", "ParentURL").Get("rec", "Mode")
                    .GetBind("rf", "rec", "Fields").Each("rf", "rfd", 8, b => b.Get("rfd", "Name").Get("rfd", "Type").Get("rfd", "Value")).Release("rf")
                    .Create("ADODB.Stream", "st").Call("st", "Open", A.Obj("rec"), A.I4(Ado.ModeRead), A.I4(Ado.OpenStreamFromRecord))
                    .Get("st", "State").Get("st", "Size").Get("st", "Type").Get("st", "Charset").Call("st", "ReadText", A.I4(Ado.ReadAll)).Call("st", "Close").Release("st")
                    .Call("rec", "Close").Release("rec")
                    .Create("ADODB.Record", "rec2").Call("rec2", "Open", A.Str("Items"), A.Obj("cn")).Get("rec2", "State").Get("rec2", "RecordType").Release("rec2");
                AdoErrors(p, "cn");
                p.Call("rs", "Close").Release("rs").Finish("cn");
            });
    }

    // OLE-PARAM -----------------------------------------------------------------------------------------------

    private static IEnumerable<Probe> ParameterProbes()
    {
        yield return Q("param-derived", Ole("OLE-PARAM"),
            "OLE-PARAM discovery: for Commands on the saved queries QMulti (five parameters declared as pB, pA, pC, pD, pY), QParam, QAction, QEmpty and QSel (adCmdStoredProc) and on text commands with three '?' markers, an inline PARAMETERS clause, an INSERT with two markers and a SELECT with one marker, " +
            "read NamedParameters and the Parameters inventory (Count; Name, Type, Direction, Size, Precision, NumericScale, Attributes, Value) before and after an explicit Refresh, and the Connection Errors. Asks which commands describe their parameters, in what order and with which types.",
            p =>
            {
                AdoOpen(p, "cn");
                (string Bind, string Text, int Type)[] cmds =
                [
                    ("d1", "QMulti", Ado.CmdStoredProc), ("d2", "QParam", Ado.CmdStoredProc), ("d3", "QAction", Ado.CmdStoredProc), ("d4", "QEmpty", Ado.CmdStoredProc),
                    ("d5", "QSel", Ado.CmdStoredProc), ("d6", "SELECT ID FROM Items WHERE Code = ? AND L > ? AND C = ?", Ado.CmdText),
                    ("d7", "PARAMETERS pX Long, pY Text(5); SELECT ID FROM Items WHERE L = pX AND Code = pY;", Ado.CmdText),
                    ("d8", "INSERT INTO Items (Code, L) VALUES (?, ?)", Ado.CmdText), ("d9", "SELECT ID FROM Items WHERE Code = ?", Ado.CmdText),
                ];
                foreach ((string bind, string text, int type) in cmds)
                {
                    p.Create("ADODB.Command", bind).PutRef(bind, "ActiveConnection", "cn").Put(bind, "CommandText", A.Str(text)).Put(bind, "CommandType", A.I4(type))
                        .Get(bind, "NamedParameters").GetBind(bind + "p", bind, "Parameters");
                    ParamInventory(p, bind + "p");
                    p.Call(bind + "p", "Refresh");
                    ParamInventory(p, bind + "p");
                    AdoErrors(p, "cn");
                    ClearErrors(p, "cn");
                    p.Release(bind + "p").Release(bind);
                }
                p.Finish("cn");
            });

        yield return Q("param-order-binding", Ole("OLE-PARAM"),
            "OLE-PARAM names versus positions with different parameter types: (a) QMulti with its discovered parameters set by name in an order different from declaration (pC, pY, pA, pD, pB), executed, then with a string and an integer swapped between pA and pB; " +
            "(b) 'SELECT ID, Code FROM Items WHERE Code = ? AND ID = ?' with parameters appended integer-named-pID first then text-named-pCode, then in marker order, then with NamedParameters = True; " +
            "(c) 'PARAMETERS pB Text(10), pA Long; SELECT ID, Code FROM Items WHERE L = pA AND Code = pB;' with manual parameters appended pA first, and again appended pB first. Each is executed and its rows and Errors read. Asks whether binding is by name or by position and what a mismatch does.",
            p =>
            {
                AdoOpen(p, "cn");
                p.Create("ADODB.Command", "oa").PutRef("oa", "ActiveConnection", "cn").Put("oa", "CommandText", A.Str("QMulti")).Put("oa", "CommandType", A.I4(Ado.CmdStoredProc))
                    .GetBind("oap", "oa", "Parameters");
                (string Name, Arg Value)[] first = [("pC", A.Cy(1234.5678m)), ("pY", A.Bool(true)), ("pA", A.I4(1)), ("pD", A.Date(0.5)), ("pB", A.Str("b3"))];
                for (int i = 0; i < first.Length; i++)
                {
                    p.Item("oa" + i, "oap", A.Str(first[i].Name)).Put("oa" + i, "Value", first[i].Value);
                }
                p.CallBind("ra", "oa", "Execute");
                AdoReadAll(p, "ra", 8, 8);
                AdoErrors(p, "cn");
                ClearErrors(p, "cn");
                p.Item("oas1", "oap", A.Str("pA")).Put("oas1", "Value", A.Str("a1")).Item("oas2", "oap", A.Str("pB")).Put("oas2", "Value", A.I4(2))
                    .CallBind("rs", "oa", "Execute");
                AdoReadAll(p, "rs", 8, 8);
                AdoErrors(p, "cn");
                ClearErrors(p, "cn");
                p.Release("oas1").Release("oas2").Release("ra").Release("rs").Release("oap").Release("oa");

                const string marked = "SELECT ID, Code FROM Items WHERE Code = ? AND ID = ?";
                (string Bind, bool IntFirst, bool Named)[] markers = [("ob", true, false), ("oc", false, false), ("od", true, true)];
                foreach ((string bind, bool intFirst, bool named) in markers)
                {
                    p.Create("ADODB.Command", bind).PutRef(bind, "ActiveConnection", "cn").Put(bind, "CommandText", A.Str(marked)).Put(bind, "CommandType", A.I4(Ado.CmdText));
                    if (named)
                    {
                        p.Put(bind, "NamedParameters", A.Bool(true)).Get(bind, "NamedParameters");
                    }
                    p.GetBind(bind + "p", bind, "Parameters");
                    if (intFirst)
                    {
                        p.CallBind(bind + "i", bind, "CreateParameter", A.Str("pID"), A.I4(Ado.Integer), A.I4(Ado.ParamInput), A.I4(0), A.I4(2))
                            .CallBind(bind + "c", bind, "CreateParameter", A.Str("pCode"), A.I4(Ado.VarWChar), A.I4(Ado.ParamInput), A.I4(10), A.Str("a2"));
                    }
                    else
                    {
                        p.CallBind(bind + "c", bind, "CreateParameter", A.Str("pCode"), A.I4(Ado.VarWChar), A.I4(Ado.ParamInput), A.I4(10), A.Str("a2"))
                            .CallBind(bind + "i", bind, "CreateParameter", A.Str("pID"), A.I4(Ado.Integer), A.I4(Ado.ParamInput), A.I4(0), A.I4(2));
                    }
                    p.Call(bind + "p", "Append", A.Obj(intFirst ? bind + "i" : bind + "c")).Call(bind + "p", "Append", A.Obj(intFirst ? bind + "c" : bind + "i"));
                    ParamInventory(p, bind + "p");
                    p.CallBind(bind + "r", bind, "Execute");
                    AdoReadAll(p, bind + "r", 8, 2);
                    AdoErrors(p, "cn");
                    ClearErrors(p, "cn");
                    p.Release(bind + "r").Release(bind + "i").Release(bind + "c").Release(bind + "p").Release(bind);
                }

                const string inline = "PARAMETERS pB Text(10), pA Long; SELECT ID, Code FROM Items WHERE L = pA AND Code = pB;";
                foreach ((string bind, bool aFirst) in new[] { ("oe", true), ("of", false) })
                {
                    p.Create("ADODB.Command", bind).PutRef(bind, "ActiveConnection", "cn").Put(bind, "CommandText", A.Str(inline)).Put(bind, "CommandType", A.I4(Ado.CmdText))
                        .GetBind(bind + "p", bind, "Parameters")
                        .CallBind(bind + "a", bind, "CreateParameter", A.Str("pA"), A.I4(Ado.Integer), A.I4(Ado.ParamInput), A.I4(0), A.I4(1))
                        .CallBind(bind + "b", bind, "CreateParameter", A.Str("pB"), A.I4(Ado.VarWChar), A.I4(Ado.ParamInput), A.I4(10), A.Str("b3"))
                        .Call(bind + "p", "Append", A.Obj(aFirst ? bind + "a" : bind + "b")).Call(bind + "p", "Append", A.Obj(aFirst ? bind + "b" : bind + "a"));
                    ParamInventory(p, bind + "p");
                    p.CallBind(bind + "r", bind, "Execute");
                    AdoReadAll(p, bind + "r", 8, 2);
                    AdoErrors(p, "cn");
                    ClearErrors(p, "cn");
                    p.Release(bind + "r").Release(bind + "a").Release(bind + "b").Release(bind + "p").Release(bind);
                }
                p.Finish("cn");
            });

        yield return Q("param-types-insert", Ole("OLE-PARAM", "OLE-UPDATE", "OLE-BUFFER"),
            "OLE-PARAM types and conversion timing on a read/write copy: one INSERT into Items with fourteen '?' markers (Code, L, D, C, N, Dt, Txt, B, Bin, Y, Sh, Bt, Sg, G) executed five times from fresh Commands with typed manual parameters " +
            "(adVarWChar, adInteger, adDouble, adCurrency, adDecimal with Precision 18/Scale 4, adDate, adLongVarBinary, adVarBinary, adBoolean, adSmallInt, adUnsignedTinyInt, adSingle, adGUID): typical values; Null for all but Code; a string \"abc\" for the Long; a Double 3,000,000,000 for the Long; " +
            "a 60-character text and a 120-byte binary. After each Execute the Connection Errors are read and cleared, then every inserted row is read with Value and ActualSize. Asks where each conversion or size failure surfaces and what is stored.",
            p =>
            {
                AdoOpen(p, "cn", readOnly: false);
                const string insert = "INSERT INTO Items (Code, L, D, C, N, Dt, Txt, B, Bin, Y, Sh, Bt, Sg, G) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)";
                int[] types = [Ado.VarWChar, Ado.Integer, Ado.Double, Ado.Currency, Ado.Decimal, Ado.Date, Ado.VarWChar, Ado.LongVarBinary, Ado.VarBinary, Ado.Boolean, Ado.SmallInt, Ado.UnsignedTinyInt, Ado.Single, Ado.Guid];
                int[] sizes = [10, 0, 0, 0, 0, 0, 50, 200, 100, 0, 0, 0, 0, 0];
                Arg[] typical =
                [
                    A.Str("p1"), A.I4(7), A.R8(2.5), A.Cy(12.3456m), A.Str("1.2345"), A.Date(45000.5), A.Str("inserted"), A.Bytes(1, 2, 3), A.Bytes(4, 5, 6, 7, 8, 9, 10),
                    A.Bool(true), A.I2(-3), A.UI1(250), A.R4(0.5f), A.Str("{AABBCCDD-0000-1111-2222-334455667788}"),
                ];
                Arg[] AllNull(string code) => [A.Str(code), .. Enumerable.Repeat(A.Null, 13)];
                Arg[] Variant(string code, int index, Arg value)
                {
                    Arg[] copy = [.. typical];
                    copy[0] = A.Str(code);
                    copy[index] = value;
                    return copy;
                }
                Arg[][] sets =
                [
                    typical,
                    AllNull("p2"),
                    Variant("p3", 1, A.Str("abc")),
                    Variant("p4", 1, A.R8(3000000000.0)),
                    Variant("p5", 6, A.Str(new string('t', 60))),
                ];
                for (int s = 0; s < sets.Length; s++)
                {
                    string cmd = "ic" + s;
                    p.Create("ADODB.Command", cmd).PutRef(cmd, "ActiveConnection", "cn").Put(cmd, "CommandText", A.Str(insert)).Put(cmd, "CommandType", A.I4(Ado.CmdText))
                        .GetBind(cmd + "p", cmd, "Parameters");
                    for (int i = 0; i < types.Length; i++)
                    {
                        string prm = $"{cmd}_{i}";
                        if (types[i] == Ado.Decimal)
                        {
                            p.CallBind(prm, cmd, "CreateParameter", A.Str("q" + i), A.I4(types[i]), A.I4(Ado.ParamInput), A.I4(sizes[i]), A.Missing)
                                .Put(prm, "Precision", A.UI1(18)).Put(prm, "NumericScale", A.UI1(4)).Put(prm, "Value", sets[s][i]);
                        }
                        else
                        {
                            p.CallBind(prm, cmd, "CreateParameter", A.Str("q" + i), A.I4(types[i]), A.I4(Ado.ParamInput), A.I4(sizes[i]), sets[s][i]);
                        }
                        p.Call(cmd + "p", "Append", A.Obj(prm));
                    }
                    ParamInventory(p, cmd + "p");
                    p.Call(cmd, "Execute", A.Missing, A.Missing, A.I4(Ado.CmdText | Ado.ExecuteNoRecords));
                    AdoErrors(p, "cn");
                    ClearErrors(p, "cn");
                    for (int i = 0; i < types.Length; i++)
                    {
                        p.Release($"{cmd}_{i}");
                    }
                    p.Release(cmd + "p").Release(cmd);
                }
                p.CallBind("rk", "cn", "Execute", A.Str("SELECT ID, Code, L, D, C, N, Dt, Txt, B, Bin, Y, Sh, Bt, Sg, G FROM Items WHERE ID > 5 ORDER BY ID"), A.Missing, A.I4(Ado.CmdText));
                AdoReadAll(p, "rk", 8, 16, actualSize: true);
                p.Finish("cn");
            });

        yield return Q("param-edge", Ole("OLE-PARAM", "OLE-ERROR"),
            "OLE-PARAM on a read/write copy: QParam executed with no value set, with Null, with 'a1' and then reused with 'b3' on the same Command; a text command with one parameter declared as adParamOutput, adParamInputOutput and adParamReturnValue (rows, the parameter's Value after, Errors); " +
            "QAction executed with its discovered pT and pId set (then the updated row read); and Parameters.Item(9), Item('NoSuch'), Delete(9), Delete('NoSuch') and Append of a second parameter with a duplicate name. Asks the outcome and error of each.",
            p =>
            {
                AdoOpen(p, "cn", readOnly: false);
                p.Create("ADODB.Command", "ea").PutRef("ea", "ActiveConnection", "cn").Put("ea", "CommandText", A.Str("QParam")).Put("ea", "CommandType", A.I4(Ado.CmdStoredProc))
                    .GetBind("eap", "ea", "Parameters").Item("e0", "eap", A.I4(0)).Get("e0", "Value")
                    .CallBind("r1", "ea", "Execute").Get("r1", "State");
                AdoErrors(p, "cn");
                ClearErrors(p, "cn");
                p.Release("r1").Put("e0", "Value", A.Null).CallBind("r2", "ea", "Execute");
                AdoReadAll(p, "r2", 8, 3);
                p.Release("r2").Put("e0", "Value", A.Str("a1")).CallBind("r3", "ea", "Execute");
                AdoReadAll(p, "r3", 8, 3);
                p.Release("r3").Put("e0", "Value", A.Str("b3")).CallBind("r4", "ea", "Execute");
                AdoReadAll(p, "r4", 8, 3);
                AdoErrors(p, "cn");
                ClearErrors(p, "cn");
                p.Release("r4").Release("e0").Release("eap").Release("ea");

                (string Bind, int Direction)[] directions = [("ed1", Ado.ParamOutput), ("ed2", Ado.ParamInputOutput), ("ed3", Ado.ParamReturnValue)];
                foreach ((string bind, int direction) in directions)
                {
                    p.Create("ADODB.Command", bind).PutRef(bind, "ActiveConnection", "cn").Put(bind, "CommandText", A.Str("SELECT ID FROM Items WHERE Code = ?")).Put(bind, "CommandType", A.I4(Ado.CmdText))
                        .GetBind(bind + "p", bind, "Parameters")
                        .CallBind(bind + "q", bind, "CreateParameter", A.Str("p"), A.I4(Ado.VarWChar), A.I4(direction), A.I4(10), A.Str("a1"))
                        .Call(bind + "p", "Append", A.Obj(bind + "q")).CallBind(bind + "r", bind, "Execute");
                    AdoReadAll(p, bind + "r", 4, 1);
                    p.Get(bind + "q", "Value").Get(bind + "q", "Direction");
                    AdoErrors(p, "cn");
                    ClearErrors(p, "cn");
                    p.Release(bind + "r").Release(bind + "q").Release(bind + "p").Release(bind);
                }

                p.Create("ADODB.Command", "ee").PutRef("ee", "ActiveConnection", "cn").Put("ee", "CommandText", A.Str("QAction")).Put("ee", "CommandType", A.I4(Ado.CmdStoredProc))
                    .GetBind("eep", "ee", "Parameters").Item("ept", "eep", A.Str("pT")).Put("ept", "Value", A.Str("act-updated")).Item("epi", "eep", A.Str("pId")).Put("epi", "Value", A.I4(1))
                    .Call("ee", "Execute", A.Missing, A.Missing, A.I4(Ado.ExecuteNoRecords));
                AdoErrors(p, "cn");
                ClearErrors(p, "cn");
                p.CallBind("rv", "cn", "Execute", A.Str("SELECT ID, Txt FROM Items WHERE ID = 1"), A.Missing, A.I4(Ado.CmdText));
                AdoReadAll(p, "rv", 2, 2);
                p.Item("x1", "eep", A.I4(9)).Get("x1", "Name").Item("x2", "eep", A.Str("NoSuch")).Get("x2", "Name")
                    .Call("eep", "Delete", A.I4(9)).Call("eep", "Delete", A.Str("NoSuch"))
                    .CallBind("dup", "ee", "CreateParameter", A.Str("pT"), A.I4(Ado.VarWChar), A.I4(Ado.ParamInput), A.I4(50), A.Str("dup"))
                    .Call("eep", "Append", A.Obj("dup")).Get("eep", "Count");
                AdoErrors(p, "cn");
                p.Release("dup").Release("ept").Release("epi").Release("eep").Release("ee").Release("rv").Finish("cn");
            });
    }

    // OLE-UPDATE and OLE-TXN ----------------------------------------------------------------------------------

    private static IEnumerable<Probe> UpdateTransactionProbes()
    {
        yield return Q("update-errors", Ole("OLE-UPDATE", "OLE-ERROR"),
            "OLE-UPDATE failures on a read/write copy through a server keyset optimistic Recordset of 'SELECT ID, Code, L, Txt FROM Items': for each attempt the edit, then Update, EditMode, Status, the Connection Errors, CancelUpdate and EditMode. " +
            "Attempts: L = \"abc\"; L = 3,000,000,000; a 60-character Txt; on row 2 Code = 'a1' (unique conflict); Code of 16 characters; AddNew with Code 'a1'; Delete of row 1 referenced by Child; " +
            "on Req: AddNew with the required column unset, with V = -1 against the CHECK constraint, with a duplicate key and with a 8-character R. Finally the stored rows are read. Asks where each failure surfaces, the per-record status and what reaches the file.",
            p =>
            {
                AdoOpen(p, "cn", readOnly: false);
                AdoRs(p, "rs", "SELECT ID, Code, L, Txt FROM Items ORDER BY ID", "cn", Ado.UseServer, Ado.OpenKeyset, Ado.LockOptimistic, Ado.CmdText);
                p.GetBind("fl", "rs", "Fields").Item("fcode", "fl", A.Str("Code")).Item("fln", "fl", A.Str("L")).Item("ftxt", "fl", A.Str("Txt"));
                void Attempt(string rs, Action<P> edit)
                {
                    edit(p);
                    p.Call(rs, "Update").Get(rs, "EditMode").Get(rs, "Status");
                    AdoErrors(p, "cn");
                    p.Call(rs, "CancelUpdate").Get(rs, "EditMode");
                    ClearErrors(p, "cn");
                }
                Attempt("rs", e => e.Put("fln", "Value", A.Str("abc")));
                Attempt("rs", e => e.Put("fln", "Value", A.R8(3000000000.0)));
                Attempt("rs", e => e.Put("ftxt", "Value", A.Str(new string('t', 60))));
                p.Call("rs", "MoveNext");
                Attempt("rs", e => e.Put("fcode", "Value", A.Str("a1")));
                Attempt("rs", e => e.Put("fcode", "Value", A.Str("sixteen-chars-cod")));
                p.Call("rs", "AddNew");
                Attempt("rs", e => e.Put("fcode", "Value", A.Str("a1")));
                p.Call("rs", "MoveFirst").Call("rs", "Delete").Get("rs", "Status");
                AdoErrors(p, "cn");
                ClearErrors(p, "cn");
                p.Release("fcode").Release("fln").Release("ftxt").Release("fl").Call("rs", "Close").Release("rs");

                AdoRs(p, "rq", "SELECT ID, R, V FROM Req ORDER BY ID", "cn", Ado.UseServer, Ado.OpenKeyset, Ado.LockOptimistic, Ado.CmdText);
                p.GetBind("ql", "rq", "Fields").Item("qid", "ql", A.Str("ID")).Item("qr", "ql", A.Str("R")).Item("qv", "ql", A.Str("V"));
                p.Call("rq", "AddNew");
                Attempt("rq", e => e.Put("qid", "Value", A.I4(2)));
                p.Call("rq", "AddNew");
                Attempt("rq", e => e.Put("qid", "Value", A.I4(3)).Put("qr", "Value", A.Str("r3")).Put("qv", "Value", A.I4(-1)));
                p.Call("rq", "AddNew");
                Attempt("rq", e => e.Put("qid", "Value", A.I4(1)).Put("qr", "Value", A.Str("x")));
                p.Call("rq", "AddNew");
                Attempt("rq", e => e.Put("qid", "Value", A.I4(4)).Put("qr", "Value", A.Str("toolong8")));
                p.Release("qid").Release("qr").Release("qv").Release("ql").Call("rq", "Close").Release("rq");

                p.CallBind("rk", "cn", "Execute", A.Str("SELECT ID, Code, L, Txt FROM Items ORDER BY ID"), A.Missing, A.I4(Ado.CmdText));
                AdoReadAll(p, "rk", 16, 4);
                p.CallBind("rr", "cn", "Execute", A.Str("SELECT ID, R, V FROM Req ORDER BY ID"), A.Missing, A.I4(Ado.CmdText));
                AdoReadAll(p, "rr", 8, 3);
                p.Finish("cn");
            });

        yield return Multi("update-others-visible", Ole("OLE-UPDATE", "OLE-ROWLIFE", "OLE-FETCH"),
            "OLE-UPDATE visibility across processes: process A opens a read/write Connection and four read-only Recordsets of 'SELECT ID, Code, Txt FROM Items' (server keyset, server static, server dynamic, client static), reads row 1 Txt and the properties Others' Changes Visible, Own Changes Visible and Remove Deleted Rows; " +
            "process B then UPDATEs row 1, INSERTs a row and DELETEs row 2 through its own Connection and closes. A reads each Recordset without refreshing (Value, UnderlyingValue, RecordCount, next row), then Resync(adAffectAll, adResyncAllValues), then Requery with RecordCount and all rows. " +
            "Asks which cursor types see another process's update, insert and delete and when.",
            a =>
            {
                AdoOpen(a, "cn", readOnly: false);
                (string Bind, int Loc, int Type)[] kinds = [("k1", Ado.UseServer, Ado.OpenKeyset), ("k2", Ado.UseServer, Ado.OpenStatic), ("k3", Ado.UseServer, Ado.OpenDynamic), ("k4", Ado.UseClient, Ado.OpenStatic)];
                foreach ((string bind, int loc, int type) in kinds)
                {
                    AdoRs(a, bind, "SELECT ID, Code, Txt FROM Items ORDER BY ID", "cn", loc, type, Ado.LockReadOnly, Ado.CmdText);
                    a.GetBind(bind + "f", bind, "Fields").Item(bind + "t", bind + "f", A.Str("Txt")).Get(bind + "t", "Value").Get(bind, "RecordCount");
                    NamedProps(a, bind, "vis", ["Others' Changes Visible", "Own Changes Visible", "Remove Deleted Rows"]);
                }
                a.Files("a-open").Signal("a-open").Await("b-done", Wait);
                foreach ((string bind, _, _) in kinds)
                {
                    string t = bind + "t";
                    a.Get(t, "Value").Get(t, "UnderlyingValue").Get(bind, "RecordCount").Call(bind, "MoveNext").Get(t, "Value")
                        .Call(bind, "Resync", A.I4(Ado.AffectAll), A.I4(Ado.ResyncAll)).Call(bind, "MoveFirst").Get(t, "Value")
                        .Call(bind, "Requery").Get(bind, "RecordCount").Get(t, "Value");
                    AdoReadAll(a, bind, 12, 3);
                    a.Release(t).Release(bind + "f").Call(bind, "Close").Release(bind);
                }
                AdoErrors(a, "cn");
                a.Finish("cn");
            },
            b =>
            {
                b.Await("a-open", Wait);
                AdoOpen(b, "cn", readOnly: false);
                b.Call("cn", "Execute", A.Str("UPDATE Items SET Txt = 'byB' WHERE ID = 1"), A.Missing, A.I4(Ado.CmdText | Ado.ExecuteNoRecords))
                    .Call("cn", "Execute", A.Str("INSERT INTO Items (Code, Txt) VALUES ('bnew', 'new')"), A.Missing, A.I4(Ado.CmdText | Ado.ExecuteNoRecords))
                    .Call("cn", "Execute", A.Str("DELETE FROM Items WHERE ID = 2"), A.Missing, A.I4(Ado.CmdText | Ado.ExecuteNoRecords));
                AdoErrors(b, "cn");
                b.Call("cn", "Close").Release("cn").Signal("b-done");
            });

        yield return Multi("update-pessimistic-lock", Ole("OLE-UPDATE", "OLE-ERROR"),
            "OLE-UPDATE pessimistic locking across processes: process A opens a server keyset pessimistic Recordset on Items and sets Txt on row 1 (EditMode), then B runs UPDATEs of row 1 and of row 3 and reads row 1 (Errors with Number, Description, SQLState, NativeError after each); " +
            "A calls Update and B retries row 1; A then starts an edit of row 2 and B updates row 2; A calls CancelUpdate and B retries row 2. Asks which statements are refused, how long they wait, the error channels and when the lock is released.",
            a =>
            {
                AdoOpen(a, "cn", readOnly: false);
                AdoRs(a, "rs", "SELECT ID, Code, Txt FROM Items ORDER BY ID", "cn", Ado.UseServer, Ado.OpenKeyset, Ado.LockPessimistic, Ado.CmdText);
                a.GetBind("fl", "rs", "Fields").Item("ft", "fl", A.Str("Txt")).Put("ft", "Value", A.Str("A-edit")).Get("rs", "EditMode").Signal("a-locked").Await("b-tried-1", Wait)
                    .Call("rs", "Update").Get("rs", "EditMode");
                AdoErrors(a, "cn");
                a.Signal("a-updated").Await("b-tried-2", Wait).Call("rs", "MoveNext").Put("ft", "Value", A.Str("A-edit2")).Get("rs", "EditMode")
                    .Signal("a-locked2").Await("b-tried-3", Wait).Call("rs", "CancelUpdate").Get("rs", "EditMode").Signal("a-cancelled").Await("b-tried-4", Wait);
                AdoErrors(a, "cn");
                a.Release("ft").Release("fl").Call("rs", "Close").Release("rs").Finish("cn");
            },
            b =>
            {
                void Try(string sql)
                {
                    b.Call("cn", "Execute", A.Str(sql), A.Missing, A.I4(Ado.CmdText | Ado.ExecuteNoRecords));
                    AdoErrors(b, "cn");
                    ClearErrors(b, "cn");
                }
                b.Await("a-locked", Wait);
                AdoOpen(b, "cn", readOnly: false);
                Try("UPDATE Items SET Txt = 'B-blocked' WHERE ID = 1");
                Try("UPDATE Items SET Txt = 'B-other' WHERE ID = 3");
                ExecRead(b, "cn", "rb", "SELECT ID, Txt FROM Items WHERE ID = 1");
                b.Signal("b-tried-1").Await("a-updated", Wait);
                Try("UPDATE Items SET Txt = 'B-after' WHERE ID = 1");
                b.Signal("b-tried-2").Await("a-locked2", Wait);
                Try("UPDATE Items SET Txt = 'B-on-locked2' WHERE ID = 2");
                b.Signal("b-tried-3").Await("a-cancelled", Wait);
                Try("UPDATE Items SET Txt = 'B-after-cancel' WHERE ID = 2");
                ExecRead(b, "cn", "rc", "SELECT ID, Txt FROM Items WHERE ID < 4 ORDER BY ID");
                b.Call("cn", "Close").Release("cn").Signal("b-tried-4");
            });

        yield return Multi("update-optimistic-conflict", Ole("OLE-UPDATE", "OLE-ERROR"),
            "OLE-UPDATE optimistic conflicts across processes: process A reads row 1 through a server keyset optimistic Recordset; B changes row 1's Txt and deletes row 3; A sets Txt on row 1 and reads Value, OriginalValue and UnderlyingValue, calls Update (EditMode, Status, Errors), CancelUpdate, " +
            "Resync(adAffectCurrent, adResyncUnderlyingValues) and the values again, then edits and updates a second time; A then edits the deleted row 3, Updates, reads Status and Errors, CancelUpdate and Resync. Asks how a stale update and a deleted row are reported and what the values read as.",
            a =>
            {
                AdoOpen(a, "cn", readOnly: false);
                AdoRs(a, "rs", "SELECT ID, Code, Txt FROM Items ORDER BY ID", "cn", Ado.UseServer, Ado.OpenKeyset, Ado.LockOptimistic, Ado.CmdText);
                a.GetBind("fl", "rs", "Fields").Item("ft", "fl", A.Str("Txt")).Get("ft", "Value").Signal("a-read").Await("b-changed", Wait)
                    .Get("ft", "Value").Get("ft", "UnderlyingValue").Put("ft", "Value", A.Str("A-value")).Get("ft", "Value").Get("ft", "OriginalValue").Get("ft", "UnderlyingValue")
                    .Call("rs", "Update").Get("rs", "EditMode").Get("rs", "Status");
                AdoErrors(a, "cn");
                ClearErrors(a, "cn");
                a.Call("rs", "CancelUpdate").Get("ft", "Value").Get("ft", "OriginalValue").Get("ft", "UnderlyingValue")
                    .Call("rs", "Resync", A.I4(Ado.AffectCurrent), A.I4(Ado.ResyncUnderlying)).Get("ft", "Value").Get("ft", "OriginalValue").Get("ft", "UnderlyingValue")
                    .Put("ft", "Value", A.Str("A-retry")).Call("rs", "Update").Get("rs", "EditMode").Get("rs", "Status");
                AdoErrors(a, "cn");
                ClearErrors(a, "cn");
                a.Call("rs", "MoveNext").Call("rs", "MoveNext").Put("ft", "Value", A.Str("A-on-deleted")).Call("rs", "Update").Get("rs", "EditMode").Get("rs", "Status");
                AdoErrors(a, "cn");
                ClearErrors(a, "cn");
                a.Call("rs", "CancelUpdate").Call("rs", "Resync", A.I4(Ado.AffectCurrent), A.I4(Ado.ResyncUnderlying)).Get("rs", "Status").Get("ft", "Value");
                AdoErrors(a, "cn");
                a.Release("ft").Release("fl").Call("rs", "Close").Release("rs");
                ExecRead(a, "cn", "rk", "SELECT ID, Code, Txt FROM Items ORDER BY ID");
                a.Finish("cn");
            },
            b =>
            {
                b.Await("a-read", Wait);
                AdoOpen(b, "cn", readOnly: false);
                b.Call("cn", "Execute", A.Str("UPDATE Items SET Txt = 'B-value' WHERE ID = 1"), A.Missing, A.I4(Ado.CmdText | Ado.ExecuteNoRecords))
                    .Call("cn", "Execute", A.Str("DELETE FROM Items WHERE ID = 3"), A.Missing, A.I4(Ado.CmdText | Ado.ExecuteNoRecords));
                AdoErrors(b, "cn");
                b.Call("cn", "Close").Release("cn").Signal("b-changed");
            });

        yield return Q("txn-held-rows", Ole("OLE-TXN", "OLE-ROWLIFE"),
            "OLE-TXN on a read/write copy: read the session properties Transaction DDL, Isolation Levels, Isolation Retention and Jet OLEDB:Transaction Commit Mode and the rowset properties Preserve on Commit and Preserve on Abort; hold a server keyset optimistic, a server forward-only and a client static Recordset; " +
            "BeginTrans, edit and Update a row, CommitTrans, then read State, EOF, field values and MoveNext on each held Recordset; repeat with RollbackTrans; then set Attributes to adXactCommitRetaining|adXactAbortRetaining, BeginTrans, Update, CommitTrans and RollbackTrans again, reading Attributes, IsolationLevel and Errors. " +
            "Asks the transaction level returned, whether held rowsets survive each transition and what the retaining attributes do.",
            p =>
            {
                AdoOpen(p, "cn", readOnly: false);
                NamedProps(p, "cn", "txn", ["Transaction DDL", "Isolation Levels", "Isolation Retention", "Jet OLEDB:Transaction Commit Mode", "Isolation Level"]);
                AdoRs(p, "h1", "SELECT ID, Code, Txt FROM Items ORDER BY ID", "cn", Ado.UseServer, Ado.OpenKeyset, Ado.LockOptimistic, Ado.CmdText);
                AdoRs(p, "h2", "SELECT ID, V FROM Seq ORDER BY ID", "cn", Ado.UseServer, Ado.OpenForwardOnly, Ado.LockReadOnly, Ado.CmdText);
                AdoRs(p, "h3", "SELECT ID, Code, Txt FROM Items ORDER BY ID", "cn", Ado.UseClient, Ado.OpenStatic, Ado.LockReadOnly, Ado.CmdText);
                foreach (string h in new[] { "h1", "h2", "h3" })
                {
                    NamedProps(p, h, "pres", ["Preserve on Commit", "Preserve on Abort"]);
                }
                p.GetBind("f1", "h1", "Fields").Item("t1", "f1", A.Str("Txt")).GetBind("f2", "h2", "Fields").Item("i2", "f2", A.Str("ID"))
                    .GetBind("f3", "h3", "Fields").Item("t3", "f3", A.Str("Txt")).Get("t1", "Value").Get("i2", "Value").Get("t3", "Value");
                void Held(string label)
                {
                    p.Get("h1", "State").Get("h1", "EOF").Get("t1", "Value").Call("h1", "MoveNext").Get("t1", "Value")
                        .Get("h2", "State").Get("h2", "EOF").Get("i2", "Value").Call("h2", "MoveNext").Get("i2", "Value")
                        .Get("h3", "State").Get("h3", "RecordCount").Get("t3", "Value").Call("h3", "MoveNext").Get("t3", "Value");
                    AdoErrors(p, "cn");
                    ClearErrors(p, "cn");
                    p.Call("h1", "MoveFirst").Call("h3", "MoveFirst").Get("t1", "Value").Get("t3", "Value").Modules("held-" + label);
                }
                p.Call("cn", "BeginTrans").Put("t1", "Value", A.Str("tx1")).Call("h1", "Update").Call("cn", "CommitTrans");
                Held("commit");
                p.Call("cn", "BeginTrans").Put("t1", "Value", A.Str("tx2")).Call("h1", "Update").Get("t1", "UnderlyingValue").Call("cn", "RollbackTrans");
                Held("rollback");
                p.Put("cn", "Attributes", A.I4(Ado.CommitRetaining | Ado.AbortRetaining)).Get("cn", "Attributes").Call("cn", "BeginTrans")
                    .Put("t1", "Value", A.Str("tx3")).Call("h1", "Update").Call("cn", "CommitTrans").Get("cn", "IsolationLevel");
                AdoErrors(p, "cn");
                p.Put("t1", "Value", A.Str("tx4")).Call("h1", "Update").Call("cn", "RollbackTrans").Call("cn", "RollbackTrans");
                AdoErrors(p, "cn");
                p.Release("t1").Release("f1").Release("i2").Release("f2").Release("t3").Release("f3");
                foreach (string h in new[] { "h1", "h2", "h3" })
                {
                    p.Call(h, "Close").Release(h);
                }
                ExecRead(p, "cn", "rk", "SELECT ID, Code, Txt FROM Items ORDER BY ID");
                p.Finish("cn");
            });

        yield return Q("txn-ddl-rollback", Ole("OLE-TXN", "OLE-ACT"),
            "OLE-TXN with DDL on a read/write copy: BeginTrans, CREATE TABLE TxnDdl, INSERT a row, ALTER TABLE Items ADD COLUMN Extra TEXT(5), read the table names of OpenSchema(adSchemaTables) and the field count of 'SELECT * FROM Items', RollbackTrans, and read both again with a SELECT COUNT(*) FROM TxnDdl; " +
            "then the same sequence ending with CommitTrans. Asks whether DDL is transactional, what the schema rowset shows inside and after the transaction and the errors.",
            p =>
            {
                AdoOpen(p, "cn", readOnly: false);
                void Inspect(string tag)
                {
                    p.CallBind("s" + tag, "cn", "OpenSchema", A.I4(20)).Get("s" + tag, "State");
                    AdoReadAll(p, "s" + tag, 40, 4);
                    p.Release("s" + tag).CallBind("w" + tag, "cn", "Execute", A.Str("SELECT * FROM Items WHERE 1 = 0"), A.Missing, A.I4(Ado.CmdText))
                        .GetBind("wf" + tag, "w" + tag, "Fields").Get("wf" + tag, "Count").Release("wf" + tag).Release("w" + tag);
                    p.CallBind("c" + tag, "cn", "Execute", A.Str("SELECT COUNT(*) AS Cnt FROM TxnDdl"), A.Missing, A.I4(Ado.CmdText));
                    AdoReadAll(p, "c" + tag, 2, 1);
                    AdoErrors(p, "cn");
                    ClearErrors(p, "cn");
                    p.Release("c" + tag);
                }
                void Ddl(string suffix, string ending)
                {
                    p.Call("cn", "BeginTrans")
                        .Call("cn", "Execute", A.Str("CREATE TABLE TxnDdl (ID LONG)"), A.Missing, A.I4(Ado.CmdText | Ado.ExecuteNoRecords))
                        .Call("cn", "Execute", A.Str("INSERT INTO TxnDdl VALUES (1)"), A.Missing, A.I4(Ado.CmdText | Ado.ExecuteNoRecords))
                        .Call("cn", "Execute", A.Str("ALTER TABLE Items ADD COLUMN Extra" + suffix + " TEXT(5)"), A.Missing, A.I4(Ado.CmdText | Ado.ExecuteNoRecords));
                    AdoErrors(p, "cn");
                    Inspect("in" + suffix);
                    p.Call("cn", ending);
                    AdoErrors(p, "cn");
                    Inspect("after" + suffix);
                }
                Ddl("1", "RollbackTrans");
                Ddl("2", "CommitTrans");
                p.Finish("cn");
            });

        yield return Multi("txn-two-process", Ole("OLE-TXN", "OLE-UPDATE", "OLE-ERROR"),
            "OLE-TXN visibility and locking across processes: A BeginTrans, UPDATEs row 1 and INSERTs a row without committing; B reads all rows, UPDATEs row 1 and then row 3 (Errors) and reads again; A RollbackTrans and B reads; A BeginTrans, UPDATEs row 1 and CommitTrans and B reads. " +
            "Asks what an uncommitted transaction shows to another process, which statements are refused and the state after rollback and commit.",
            a =>
            {
                AdoOpen(a, "cn", readOnly: false);
                a.Call("cn", "BeginTrans").Call("cn", "Execute", A.Str("UPDATE Items SET Txt = 'txA' WHERE ID = 1"), A.Missing, A.I4(Ado.CmdText | Ado.ExecuteNoRecords))
                    .Call("cn", "Execute", A.Str("INSERT INTO Items (Code, Txt) VALUES ('txnew', 'x')"), A.Missing, A.I4(Ado.CmdText | Ado.ExecuteNoRecords))
                    .Signal("a-uncommitted").Await("b-tried-1", Wait).Call("cn", "RollbackTrans").Signal("a-rolled").Await("b-tried-2", Wait)
                    .Call("cn", "BeginTrans").Call("cn", "Execute", A.Str("UPDATE Items SET Txt = 'txA2' WHERE ID = 1"), A.Missing, A.I4(Ado.CmdText | Ado.ExecuteNoRecords))
                    .Call("cn", "CommitTrans").Signal("a-committed").Await("b-tried-3", Wait);
                AdoErrors(a, "cn");
                ExecRead(a, "cn", "rk", "SELECT ID, Code, Txt FROM Items ORDER BY ID");
                a.Finish("cn");
            },
            b =>
            {
                const string all = "SELECT ID, Code, Txt FROM Items ORDER BY ID";
                b.Await("a-uncommitted", Wait);
                AdoOpen(b, "cn", readOnly: false);
                ExecRead(b, "cn", "r1", all);
                b.Call("cn", "Execute", A.Str("UPDATE Items SET Txt = 'txB' WHERE ID = 1"), A.Missing, A.I4(Ado.CmdText | Ado.ExecuteNoRecords));
                AdoErrors(b, "cn");
                ClearErrors(b, "cn");
                b.Call("cn", "Execute", A.Str("UPDATE Items SET Txt = 'txB3' WHERE ID = 3"), A.Missing, A.I4(Ado.CmdText | Ado.ExecuteNoRecords));
                AdoErrors(b, "cn");
                ClearErrors(b, "cn");
                ExecRead(b, "cn", "r2", all);
                b.Signal("b-tried-1").Await("a-rolled", Wait);
                ExecRead(b, "cn", "r3", all);
                b.Signal("b-tried-2").Await("a-committed", Wait);
                ExecRead(b, "cn", "r4", all);
                b.Call("cn", "Close").Release("cn").Signal("b-tried-3");
            });
    }

    // OLE-ERROR -----------------------------------------------------------------------------------------------

    private static IEnumerable<Probe> ErrorProbes()
    {
        yield return Q("error-record-channels", Ole("OLE-ERROR"),
            "OLE-ERROR on a read/write copy: execute fourteen failing statements through Connection.Execute (syntax, missing table, missing column, unique key, foreign key insert and delete, required Null, CHECK, 1/0, a text into a Long, a 16-character code, a parameter marker with no parameter, DROP of a referenced table) " +
            "and after each read every Errors entry (Number, Description, Source, SQLState, NativeError, HelpFile, HelpContext), without clearing for the first three and clearing afterwards. Asks the record count and order, the field values per entry and whether entries accumulate.",
            p =>
            {
                AdoOpen(p, "cn", readOnly: false);
                string[] statements =
                [
                    "SELEC * FROM Items", "SELECT * FROM NoSuchTable", "SELECT NoSuch FROM Items",
                    "INSERT INTO Items (Code) VALUES ('a1')", "INSERT INTO Child (ID, TID) VALUES (9, 999)", "DELETE FROM Items WHERE ID = 1",
                    "INSERT INTO Req (ID) VALUES (7)", "INSERT INTO Req (ID, R, V) VALUES (8, 'r', -5)", "SELECT 1/0 AS Z FROM Items",
                    "INSERT INTO Items (Code, L) VALUES ('x9', 'abc')", "INSERT INTO Items (Code) VALUES ('this-code-is-too-long')",
                    "SELECT ID FROM Items WHERE Code = ?", "UPDATE Items SET L = L + 1; DELETE FROM Items", "DROP TABLE Items",
                ];
                for (int i = 0; i < statements.Length; i++)
                {
                    p.Call("cn", "Execute", A.Str(statements[i]), A.Missing, A.I4(Ado.CmdText));
                    AdoErrors(p, "cn");
                    p.GetBind("ec" + i, "cn", "Errors").Get("ec" + i, "Count").Release("ec" + i);
                    if (i >= 2)
                    {
                        ClearErrors(p, "cn");
                    }
                }
                p.Finish("cn");
            });

        yield return Q("error-retained-lifetime", Ole("OLE-ERROR"),
            "OLE-ERROR lifetime on a read/write copy: a Command's failing INSERT (duplicate key) is executed; the Errors collection is retained with Item(0) and Item(1) (Number, Description, SQLState); then Errors.Clear and the retained Error's Number; release of the Command and the retained Error's Description; " +
            "a successful SELECT and the retained Number; Connection Close and the retained Number; release of the Connection and the collection and the retained Number, Description and SQLState. A second Connection fails, retains its Error and is released without Close. " +
            "Asks what a retained error object returns after each owner transition.",
            p =>
            {
                AdoOpen(p, "cn", readOnly: false);
                p.Create("ADODB.Command", "cmd").PutRef("cmd", "ActiveConnection", "cn").Put("cmd", "CommandText", A.Str("INSERT INTO Items (Code) VALUES ('a1')"))
                    .Put("cmd", "CommandType", A.I4(Ado.CmdText)).Call("cmd", "Execute", A.Missing, A.Missing, A.I4(Ado.CmdText | Ado.ExecuteNoRecords))
                    .GetBind("errs", "cn", "Errors").Get("errs", "Count").Item("e0", "errs", A.I4(0)).Get("e0", "Number").Get("e0", "Description").Get("e0", "SQLState")
                    .Item("e1", "errs", A.I4(1)).Get("e1", "Number").Get("e1", "Description")
                    .Call("errs", "Clear").Get("errs", "Count").Get("e0", "Number").Release("cmd").Get("e0", "Description")
                    .CallBind("ok", "cn", "Execute", A.Str("SELECT ID FROM Items"), A.Missing, A.I4(Ado.CmdText)).Get("e0", "Number").Release("ok")
                    .Call("cn", "Close").Get("e0", "Number").Release("errs").Release("cn").Get("e0", "Number").Get("e0", "Description").Get("e0", "SQLState").Release("e1").Release("e0");
                AdoOpen(p, "c2", readOnly: false);
                p.Call("c2", "Execute", A.Str("SELEC bad"), A.Missing, A.I4(Ado.CmdText)).GetBind("errs2", "c2", "Errors").Item("x0", "errs2", A.I4(0))
                    .Release("errs2").Release("c2").Get("x0", "Number").Get("x0", "Description").Get("x0", "SQLState").Release("x0");
            });

        yield return Q("error-open-failures", Ole("OLE-ERROR", "OLE-ACT"),
            "OLE-ERROR on Open: separate Connections attempt (a) a Data Source that does not exist, (b) no Data Source, (c) an empty Data Source, (d) Extended Properties=\"Excel 12.0 Xml;HDR=Yes\" against the Access file, (e) Extended Properties=Text, (f) User ID Admin with Password wrongpw, " +
            "(g) Mode 99, and (h) in one process a first read/write exclusive Connection with a second read/write Connection, then a first shared with a second exclusive. After each: State, Provider, ConnectionString and every Errors entry; then Close and release. Asks the failure channel and values of each refusal.",
            p =>
            {
                (string Bind, string Text, Arg? Source)[] cases =
                [
                    ("fa", "", A.Out("absent.accdb")), ("fb", "", null), ("fc", "", A.Str("")),
                    ("fd", "Extended Properties=\"Excel 12.0 Xml;HDR=Yes\"", A.In()), ("fe", "Extended Properties=Text", A.In()),
                    ("ff", "User ID=Admin;Password=wrongpw", A.In()),
                ];
                foreach ((string bind, string text, Arg? source) in cases)
                {
                    p.Create("ADODB.Connection", bind).Put(bind, "ConnectionString", A.Str($"Provider={Ace};{text}"));
                    if (source is not null)
                    {
                        SetDataSource(p, bind, source);
                    }
                    p.Call(bind, "Open", A.Missing);
                    OpenedState(p, bind);
                }
                p.Create("ADODB.Connection", "fg").Put("fg", "Provider", A.Str(Ace)).Put("fg", "Mode", A.I4(99));
                SetDataSource(p, "fg", A.In());
                p.Call("fg", "Open").Get("fg", "Mode");
                OpenedState(p, "fg");
                (string First, string Second, int FirstMode, int SecondMode)[] pairs =
                [
                    ("fh1", "fh2", Ado.ModeReadWrite | Ado.ModeExclusive, Ado.ModeReadWrite),
                    ("fh3", "fh4", Ado.ModeReadWrite | Ado.ModeDenyNone, Ado.ModeReadWrite | Ado.ModeExclusive),
                ];
                foreach ((string first, string second, int firstMode, int secondMode) in pairs)
                {
                    AdoOpen(p, first, readOnly: false, mode: firstMode);
                    AdoOpen(p, second, readOnly: false, mode: secondMode);
                    AdoErrors(p, second);
                    p.Call(second, "Close").Release(second).Call(first, "Close").Release(first);
                }
            });

        yield return Q("error-ado-origin", Ole("OLE-ERROR"),
            "OLE-ERROR channels of ADO-level failures: after each of MoveFirst on a closed Recordset, Execute on a closed Connection, Command.Execute with no ActiveConnection, Recordset.Open of a nonexistent table with adCmdTable, Fields.Item out of range, " +
            "an invalid Execute option value and, as a baseline, a provider failure (bad SQL), read the open Connection's Errors count and entries and the closed Connection's. Asks whether failures raised by ADO itself populate the extended error collection or only the Automation error.",
            p =>
            {
                AdoOpen(p, "cn");
                p.Create("ADODB.Connection", "cx");
                p.Create("ADODB.Recordset", "rc").Call("rc", "MoveFirst");
                AdoErrors(p, "cn");
                p.Call("cx", "Execute", A.Str("SELECT 1 AS A"), A.Missing, A.I4(Ado.CmdText));
                AdoErrors(p, "cx");
                p.Create("ADODB.Command", "cmd").Put("cmd", "CommandText", A.Str("SELECT ID FROM Items")).Call("cmd", "Execute");
                AdoErrors(p, "cn");
                p.Create("ADODB.Recordset", "rt").Call("rt", "Open", A.Str("NoSuchTable"), A.Obj("cn"), A.I4(Ado.OpenKeyset), A.I4(Ado.LockReadOnly), A.I4(Ado.CmdTable));
                AdoErrors(p, "cn");
                p.CallBind("rs", "cn", "Execute", A.Str("SELECT ID FROM Items"), A.Missing, A.I4(Ado.CmdText)).GetBind("fl", "rs", "Fields").Item("fx", "fl", A.I4(99));
                AdoErrors(p, "cn");
                p.Call("cn", "Execute", A.Str("SELECT ID FROM Items"), A.Missing, A.I4(123456));
                AdoErrors(p, "cn");
                p.Call("cn", "Execute", A.Str("SELEC bad"), A.Missing, A.I4(Ado.CmdText));
                AdoErrors(p, "cn");
                AdoErrors(p, "cx");
                p.Release("fl").Release("rs").Release("rt").Release("cmd").Release("rc").Release("cx").Finish("cn");
            });
    }

    // OpenSchema ----------------------------------------------------------------------------------------------

    private static IEnumerable<Probe> SchemaProbes()
    {
        yield return Q2("schema-tables", Ole("OLE-PROP", "OLE-ACCESS"),
            "Schema rowset adSchemaTables (20) with no restrictions, for the ACCDB and the MDB fixture: State, every column's Name and Type, and every row (user, system, linked and view entries). Asks the rowset shape and which table types and properties the provider reports.",
            p =>
            {
                foreach (string role in new[] { "main", "mdb" })
                {
                    AdoOpen(p, "cn_" + role, role: role);
                    SchemaDump(p, "cn_" + role, "t_" + role, 20, 60, 16);
                    p.Call("cn_" + role, "Close").Release("cn_" + role);
                }
            });

        yield return Q2("schema-columns", Ole("OLE-PROP", "OLE-BUFFER"),
            "Schema rowset adSchemaColumns (4) with no restrictions, for the ACCDB and the MDB fixture: State, every column's Name and Type, and every row of every table (data type codes, sizes, precision, scale, flags, defaults, ordinals). " +
            "Asks the data type and flag the provider publishes for each Items column in both formats, the Decimal metadata and the compressed columns.",
            p =>
            {
                foreach (string role in new[] { "main", "mdb" })
                {
                    AdoOpen(p, "cn_" + role, role: role);
                    SchemaDump(p, "cn_" + role, "c_" + role, 4, 80, 32);
                    p.Call("cn_" + role, "Close").Release("cn_" + role);
                }
            });

        yield return Q("schema-keys-indexes", Ole("OLE-PROP", "OLE-ACCESS"),
            "Schema rowsets for indexes and constraints on the ACCDB fixture, each with no restrictions: adSchemaIndexes (12), PrimaryKeys (28), ForeignKeys (27), KeyColumnUsage (8), TableConstraints (10), ReferentialConstraints (9), CheckConstraints (5), " +
            "ConstraintColumnUsage (6) and ConstraintTableUsage (7): State, column Name and Type and every row. Asks which are supported, their shapes and how the unique constraint, the descending index, the foreign key and the CHECK constraint appear.",
            p =>
            {
                AdoOpen(p, "cn");
                int n = 0;
                foreach (int schema in new[] { 12, 28, 27, 8, 10, 9, 5, 6, 7 })
                {
                    SchemaDump(p, "cn", "k" + n++, schema, 40, 32);
                }
                AdoErrors(p, "cn");
                p.Finish("cn");
            });

        yield return Q("schema-procedures-views", Ole("OLE-PROP", "OLE-PARAM"),
            "Schema rowsets for saved queries on the ACCDB fixture, each with no restrictions: adSchemaProcedures (16), Views (23), ProcedureParameters (26), ProcedureColumns (29), ViewColumnUsage (24), ViewTableUsage (25): " +
            "State, column Name and Type and every row. Asks which saved queries appear as procedures and which as views, and the parameter order, types and ordinals published for QMulti and QAction.",
            p =>
            {
                AdoOpen(p, "cn");
                int n = 0;
                foreach (int schema in new[] { 16, 23, 26, 29, 24, 25 })
                {
                    SchemaDump(p, "cn", "v" + n++, schema, 40, 32);
                }
                AdoErrors(p, "cn");
                p.Finish("cn");
            });

        yield return Q("schema-others", Ole("OLE-PROP", "OLE-ACCESS", "OLE-ERROR"),
            "Remaining OpenSchema requests on the ACCDB fixture: the rows of adSchemaProviderTypes (22), DBInfoLiterals (31), DBInfoKeywords (30) and SQLLanguages (18); State and field count of every other defined SchemaEnum value (1, 2, 3, 6, 7, 11, 13, 14, 15, 17, 19, 21 and 32-43) and of 9999 and adSchemaProviderSpecific (-1) with and without a Criteria; " +
            "and adSchemaTables with a restriction argument of Empty, Null, Missing, an Integer 0, the string \"Items\" and a byte array, and adSchemaColumns with the string \"Items\". Errors are read at the end. Asks which schemas are supported and how each restriction form is handled.",
            p =>
            {
                AdoOpen(p, "cn");
                int n = 0;
                foreach ((int schema, int rows, int fields) in new[] { (22, 60, 20), (31, 80, 12), (30, 500, 2), (18, 8, 8) })
                {
                    SchemaDump(p, "cn", "o" + n++, schema, rows, fields);
                }
                foreach (int schema in new[] { 1, 2, 3, 6, 7, 11, 13, 14, 15, 17, 19, 21, 32, 33, 34, 35, 36, 37, 38, 39, 40, 41, 42, 43, 9999 })
                {
                    string b = "u" + schema;
                    p.CallBind(b, "cn", "OpenSchema", A.I4(schema)).Get(b, "State").GetBind(b + "f", b, "Fields").Get(b + "f", "Count").Release(b + "f").Get(b, "EOF").Release(b);
                }
                p.CallBind("sp1", "cn", "OpenSchema", A.I4(-1)).Get("sp1", "State").Release("sp1")
                    .CallBind("sp2", "cn", "OpenSchema", A.I4(-1), A.Missing, A.Str("{00000000-0000-0000-0000-000000000000}")).Get("sp2", "State").Release("sp2");
                (string Bind, int Schema, Arg Restriction)[] restrictions =
                [
                    ("rx1", 20, A.Empty), ("rx2", 20, A.Null), ("rx3", 20, A.Missing), ("rx4", 20, A.I4(0)), ("rx5", 20, A.Str("Items")),
                    ("rx6", 20, A.Bytes(1, 2, 3)), ("rx7", 4, A.Str("Items")),
                ];
                foreach ((string bind, int schema, Arg restriction) in restrictions)
                {
                    p.CallBind(bind, "cn", "OpenSchema", A.I4(schema), restriction).Get(bind, "State").GetBind(bind + "f", bind, "Fields").Get(bind + "f", "Count")
                        .Release(bind + "f").Get(bind, "EOF").Release(bind);
                }
                AdoErrors(p, "cn");
                p.Finish("cn");
            });
    }

    // ODBC ----------------------------------------------------------------------------------------------------

    private static IEnumerable<Probe> OdbcProbes()
    {
        yield return Bare("odbc-path-free-connections", ["R-ODBC-1", "ODBC-LOAD", "ODBC-DIAG"],
            "ODBC-LOAD and ODBC-DIAG through ADODB and the MSDASQL provider, using connection strings that need no database path: (a) the Access driver with no Dbq, (b) the Access driver with a Dbq in a folder that does not exist, " +
            "(c) the legacy driver name '(*.mdb)' with such a Dbq, (d) a driver name that is not installed, (e) a DSN that does not exist, (f) the Access driver with no Provider key, and (g) the same driver in Extended Properties. " +
            "For each: Provider after assignment, Open outcome, State, loaded modules after Open, the properties Provider Name, Provider Friendly Name, DBMS Name and DBMS Version and every Errors entry (Number, Description, Source, SQLState, NativeError). " +
            "Asks the Driver Manager and driver identities reached, the diagnostics returned and any modules loaded; success against a real database is deferred because the probe cannot build a path.",
            p =>
            {
                const string driver = "Driver={Microsoft Access Driver (*.mdb, *.accdb)};";
                const string absent = @"Dbq=C:\CalcDbProbe\absent\absent.accdb;";
                (string Bind, string Text)[] cases =
                [
                    ("o1", "Provider=MSDASQL;" + driver),
                    ("o2", "Provider=MSDASQL;" + driver + absent),
                    ("o3", @"Provider=MSDASQL;Driver={Microsoft Access Driver (*.mdb)};Dbq=C:\CalcDbProbe\absent\absent.mdb;"),
                    ("o4", "Provider=MSDASQL;Driver={CalcDb No Such Driver};"),
                    ("o5", "Provider=MSDASQL;DSN=CalcDbNoSuchDsn;"),
                    ("o6", driver + absent),
                    ("o7", "Provider=MSDASQL;Extended Properties=\"" + driver + absent + "\""),
                ];
                p.Modules("before-odbc");
                foreach ((string bind, string text) in cases)
                {
                    p.Create("ADODB.Connection", bind).Put(bind, "ConnectionString", A.Str(text)).Get(bind, "Provider").Call(bind, "Open", A.Missing).Get(bind, "State").Modules("after-" + bind);
                    NamedProps(p, bind, "odbc", ["Provider Name", "Provider Friendly Name", "DBMS Name", "DBMS Version"]);
                    AdoErrors(p, bind);
                    p.Call(bind, "Close").Release(bind);
                }
                p.Modules("after-odbc");
            });
    }

    // Helpers -------------------------------------------------------------------------------------------------

    private static Probe Q(string name, string[] refs, string description, Action<P> program) =>
        new(Card, name, refs, description, program) { Fixtures = [("main", Accdb)] };

    private static Probe Q2(string name, string[] refs, string description, Action<P> program) =>
        new(Card, name, refs, description, program) { Fixtures = [("main", Accdb), ("mdb", Mdb)] };

    private static Probe Bare(string name, string[] refs, string description, Action<P> program) => new(Card, name, refs, description, program);

    private static Probe Multi(string name, string[] refs, string description, Action<P> a, Action<P> b) =>
        new(Card, name, refs, description, a) { Fixtures = [("main", Accdb)], Participants = [("b", b)] };

    /// <summary><c>cn.Properties("Data Source").Value = path</c> through a wrapper released at once.</summary>
    private static void SetDataSource(P p, string cn, Arg path) =>
        p.GetBind(cn + "Props", cn, "Properties").Item(cn + "Ds", cn + "Props", A.Str("Data Source")).Put(cn + "Ds", "Value", path)
            .Release(cn + "Ds").Release(cn + "Props");

    /// <summary>ACE OLE DB connection to the input with this role: Provider, Data Source property, optional mode (read when read-only), Open.</summary>
    private static void AdoOpen(P p, string cn, bool readOnly = true, string role = "main", int mode = 0)
    {
        p.Create("ADODB.Connection", cn).Put(cn, "Provider", A.Str(Ace));
        SetDataSource(p, cn, A.In(role));
        if (mode != 0)
        {
            p.Put(cn, "Mode", A.I4(mode));
        }
        else if (readOnly)
        {
            p.Put(cn, "Mode", A.I4(Ado.ModeRead));
        }
        p.Call(cn, "Open").Get(cn, "State");
    }

    /// <summary>Every Errors entry: Number, Description, Source, SQLState, NativeError, HelpFile, HelpContext.</summary>
    private static void AdoErrors(P p, string cn) =>
        p.GetBind(cn + "Errs", cn, "Errors")
            .Each(cn + "Errs", cn + "Err", 16, b => b.Get(cn + "Err", "Number").Get(cn + "Err", "Description").Get(cn + "Err", "Source")
                .Get(cn + "Err", "SQLState").Get(cn + "Err", "NativeError").Get(cn + "Err", "HelpFile").Get(cn + "Err", "HelpContext"))
            .Release(cn + "Errs");

    private static void ClearErrors(P p, string cn) =>
        p.GetBind(cn + "Ecl", cn, "Errors").Call(cn + "Ecl", "Clear").Get(cn + "Ecl", "Count").Release(cn + "Ecl");

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

    /// <summary>Execute a SELECT on the connection and read its rows (at most 16 rows of 4 fields).</summary>
    private static void ExecRead(P p, string cn, string bind, string sql)
    {
        p.CallBind(bind, cn, "Execute", A.Str(sql), A.Missing, A.I4(Ado.CmdText));
        AdoReadAll(p, bind, 16, 4);
        p.Release(bind);
    }

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

    /// <summary>The whole Properties inventory of a Connection, Command, Recordset or Field: Name, Type, Value, Attributes.</summary>
    private static void Props(P p, string owner, string label) =>
        p.GetBind($"{owner}Props_{label}", owner, "Properties")
            .Each($"{owner}Props_{label}", $"{owner}Pr_{label}", 256, b => b.Get($"{owner}Pr_{label}", "Name").Get($"{owner}Pr_{label}", "Type")
                .Get($"{owner}Pr_{label}", "Value").Get($"{owner}Pr_{label}", "Attributes"))
            .Release($"{owner}Props_{label}");

    /// <summary>The named properties of an owner's Properties collection: Value and Attributes of each (a missing name is an observation).</summary>
    private static void NamedProps(P p, string owner, string label, string[] names)
    {
        string coll = $"{owner}Np_{label}";
        p.GetBind(coll, owner, "Properties");
        for (int i = 0; i < names.Length; i++)
        {
            string item = $"{coll}_{i}";
            p.Item(item, coll, A.Str(names[i])).Get(item, "Value").Get(item, "Attributes").Release(item);
        }
        p.Release(coll);
    }

    private static void ParamInventory(P p, string prms) =>
        p.Get(prms, "Count").Each(prms, prms + "P", 16, b => b.Get(prms + "P", "Name").Get(prms + "P", "Type").Get(prms + "P", "Direction")
            .Get(prms + "P", "Size").Get(prms + "P", "Precision").Get(prms + "P", "NumericScale").Get(prms + "P", "Attributes").Get(prms + "P", "Value"));

    /// <summary>One OpenSchema rowset: State, column Name and Type, then the rows (read-only; no restriction arguments).</summary>
    private static void SchemaDump(P p, string cn, string bind, int schema, int maxRows, int maxFields)
    {
        p.CallBind(bind, cn, "OpenSchema", A.I4(schema)).Get(bind, "State")
            .GetBind(bind + "f", bind, "Fields").Get(bind + "f", "Count")
            .Each(bind + "f", bind + "c", maxFields, b => b.Get(bind + "c", "Name").Get(bind + "c", "Type")).Release(bind + "f");
        AdoReadAll(p, bind, maxRows, maxFields);
        p.Release(bind);
    }
}
