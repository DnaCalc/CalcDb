namespace CalcDb.Probes.Cards;

/// <summary>
/// R-ADO-1, second batch: ADODB with the ACE OLE DB provider from one in-process program on one STA per
/// participant, on the attempt's own copies of two fixtures (ACCDB and MDB). It adds questions that
/// <see cref="RStrAdoAct"/> and <see cref="ROledbOdbc"/> do not ask: connection-string and Open-argument forms
/// and what can change while a Connection is open (ADO-CONN), Command and Parameter construction, mutation and
/// connection changes (ADO-PARAM), requested versus effective cursor tuples across lock types and sources,
/// same-connection cache visibility and raw Bookmark forms (ADO-CURSOR/ADO-BOOK), implicit and explicit
/// update arguments, edits pending during other operations, batch staging, scope, cancellation, conflict,
/// resubmission, Update Criteria and Update Resync (ADO-EDIT/ADO-BATCH/ADO-BATCH-SCOPE/ADO-VALUES), Filter,
/// Sort and Find grammar on client cursors (ADO-VIEW), Save and reopen of ADTG and XML through files and
/// Streams, reconnect and malformed files (ADO-PERSIST), Field metadata for expressions and MDB, assignment
/// conversion and Fields.Refresh (ADO-SCHEMA/ADO-BUFFER), transactions on the Connection with objects and caches
/// (ADO-TXN) and the Errors collection after each failing route (R-ERR-1). Probes only ask; no answer is
/// expected in code. Every probe uses the light DAO opening so each capture identifies the loaded modules.
/// Deferred: see the answer list of the batch.
/// </summary>
public sealed class RAdo1B : ICard
{
    private const string Card = "r-ado-1b";
    private const string Ace = "Microsoft.ACE.OLEDB.12.0";

    private static readonly string Main = Card + "-main";
    private static readonly string Mdb = Card + "-mdb";

    private const string SelS = "SELECT ID, Code, L, S FROM T ORDER BY ID";
    private const string SelW = "SELECT ID, Code, V, V2, S FROM W ORDER BY ID";
    private const string StoredW = "SELECT ID, Code, V, V2, S FROM W ORDER BY ID";
    private const string StoredT = "SELECT ID, Code, L, S FROM T ORDER BY ID";

    /// <summary>ADO constants (values from the ADO type library, inventoried by r-str-ado-act/ado-describe).</summary>
    private static class Ado
    {
        public const int UseServer = 2, UseClient = 3;
        public const int OpenForwardOnly = 0, OpenKeyset = 1, OpenDynamic = 2, OpenStatic = 3;
        public const int LockReadOnly = 1, LockPessimistic = 2, LockOptimistic = 3, LockBatchOptimistic = 4;
        public const int CmdUnknown = 8, CmdText = 1, CmdTable = 2, CmdStoredProc = 4, CmdFile = 256, CmdTableDirect = 512;
        public const int AsyncConnect = 16, ExecuteNoRecords = 0x80;
        public const int ModeRead = 1;
        public const int AffectCurrent = 1, AffectGroup = 2, AffectAll = 3;
        public const int ResyncUnderlying = 1, ResyncAll = 2;
        public const int FilterNone = 0, FilterPending = 1, FilterAffected = 2, FilterFetched = 3, FilterConflicting = 5;
        public const int PersistAdtg = 0, PersistXml = 1;
        public const int SaveCreateNotExist = 1, SaveCreateOverWrite = 2;
        public const int ReadAll = -1;
        public const int Integer = 3, Currency = 6, Date = 7, Boolean = 11, Decimal = 14, Guid = 72, VarWChar = 202, LongVarWChar = 203, VarBinary = 204;
        public const int ParamInput = 1, ParamOutput = 2, ParamReturnValue = 4;
        public const int XactSerializable = 0x100000;

        /// <summary>CursorOptionEnum values asked through Recordset.Supports.</summary>
        public const int SupAddNew = 0x1000400, SupDelete = 0x1000800, SupUpdate = 0x1008000, SupUpdateBatch = 0x10000, SupBookmark = 0x2000, SupFind = 0x80000;
    }

    private static string[] Refs(params string[] ids) => ["R-ADO-1", .. ids];

    private static byte[] Ascii(string text) => System.Text.Encoding.ASCII.GetBytes(text);

    public IEnumerable<Probe> Probes() =>
        Creations()
            .Concat(ConnectionProbes())
            .Concat(CommandProbes())
            .Concat(CursorProbes())
            .Concat(EditProbes())
            .Concat(BatchProbes())
            .Concat(ViewProbes())
            .Concat(PersistProbes())
            .Concat(FieldProbes())
            .Concat(TransactionProbes())
            .Concat(ErrorProbes());

    // Fixtures -------------------------------------------------------------------------------------------

    private static IEnumerable<Probe> Creations()
    {
        yield return Creation("main", "accdb", Dao.Version120);
        yield return Creation("mdb", "mdb", Dao.Version40);
    }

    private static Probe Creation(string name, string ext, int version)
    {
        Arg file = A.Out($"{Card}-{name}.{ext}");
        return new Probe(Card, "create-" + name, Refs("D-OBJECT-1"),
            $"R-ADO-1B fixture ({ext.ToUpperInvariant()}), created by DBEngine.CreateDatabase(path, dbLangGeneral, {version}) and the closed file kept: " +
            "T(ID COUNTER key, unique Code, Long, Double, Currency, Date/Time, Text(50), Memo, Long Binary, Yes/No, Integer, Byte, Single, GUID, and a Decimal added by CreateField type 20) with seven rows " +
            "(full, mixed-case text, Null date, empty memo, an all-Null row, an apostrophe text, an empty text); W(ID key, unique Code, V, V2, S) with twelve rows; P(ID key, Nm) with three rows; " +
            "Ch(ID key, PID with a foreign key to P, S) with two rows; saved queries QByL (one Long parameter), QSet (action, two parameters) and QAll.",
            p =>
            {
                p.EngineLite().CallBind("db", "eng", "CreateDatabase", file, A.Str(Dao.LangGeneral), A.I4(version)).Get("db", "Version")
                    .Call("db", "Execute", A.Str("CREATE TABLE T (ID COUNTER CONSTRAINT PK_T PRIMARY KEY, Code TEXT(10) CONSTRAINT UX_T_Code UNIQUE, L LONG, D DOUBLE, C CURRENCY, " +
                        "Dt DATETIME, S TEXT(50), M MEMO, B LONGBINARY, Y YESNO, Sh SHORT, Bt BYTE, Sg SINGLE, G GUID)"))
                    .AddDecimalField("T", "N")
                    .Call("db", "Execute", A.Str("INSERT INTO T (Code, L, D, C, N, Dt, S, M, Y, Sh, Bt, Sg) VALUES ('a1', 10, 1.5, 2.25, 3.125, #2024-01-02#, 'alpha', 'memo one', True, 7, 200, 1.5)"))
                    .Call("db", "Execute", A.Str("INSERT INTO T (Code, L, D, C, N, Dt, S, M, Y, Sh) VALUES ('b2', 20, -0.5, 0, 0, #2024-03-04 12:00:00#, 'Beta', '', False, 0)"))
                    .Call("db", "Execute", A.Str("INSERT INTO T (Code, L, D, C, N, Dt, S, M, Y) VALUES ('c3', 30, 0, 1234.5678, -0.0001, #1999-12-31#, 'gamma', 'x', True)"))
                    .Call("db", "Execute", A.Str("INSERT INTO T (Code, L, D, S) VALUES ('d4', 20, 2.5, 'alpha')"))
                    .Call("db", "Execute", A.Str("INSERT INTO T (Code) VALUES ('e5')"))
                    .Call("db", "Execute", A.Str("INSERT INTO T (Code, L, S) VALUES ('f6', 50, 'it''s')"))
                    .Call("db", "Execute", A.Str("INSERT INTO T (Code, L, S) VALUES ('g7', 5, '')"))
                    .OpenRs("rk", "db", "SELECT B FROM T WHERE Code = 'a1'", Dao.OpenDynaset)
                    .GetBind("rkf", "rk", "Fields").Item("rkb", "rkf", A.I4(0))
                    .Call("rk", "Edit").Put("rkb", "Value", A.Bytes(0x00, 0x01, 0x02, 0xff, 0x00)).Call("rk", "Update")
                    .Release("rkb").Release("rkf").Call("rk", "Close").Release("rk")
                    .Call("db", "Execute", A.Str("CREATE TABLE W (ID LONG CONSTRAINT PK_W PRIMARY KEY, Code TEXT(10) CONSTRAINT UX_W_Code UNIQUE, V LONG, V2 LONG, S TEXT(20))"));
                for (int i = 1; i <= 12; i++)
                {
                    p.Call("db", "Execute", A.Str($"INSERT INTO W VALUES ({i}, 'w{i}', {i * 10}, {i}, 's{i}')"));
                }
                p.Call("db", "Execute", A.Str("CREATE TABLE P (ID LONG CONSTRAINT PK_P PRIMARY KEY, Nm TEXT(10))"))
                    .Call("db", "Execute", A.Str("INSERT INTO P VALUES (1, 'p1')"))
                    .Call("db", "Execute", A.Str("INSERT INTO P VALUES (2, 'p2')"))
                    .Call("db", "Execute", A.Str("INSERT INTO P VALUES (3, 'p3')"))
                    .Call("db", "Execute", A.Str("CREATE TABLE Ch (ID LONG CONSTRAINT PK_Ch PRIMARY KEY, PID LONG CONSTRAINT FK_Ch_P REFERENCES P (ID), S TEXT(10))"))
                    .Call("db", "Execute", A.Str("INSERT INTO Ch VALUES (1, 1, 'c1')"))
                    .Call("db", "Execute", A.Str("INSERT INTO Ch VALUES (2, 2, 'c2')"))
                    .CallBind("q1", "db", "CreateQueryDef", A.Str("QByL"), A.Str("PARAMETERS pL Long; SELECT ID, Code, L FROM T WHERE L = pL;"))
                    .CallBind("q2", "db", "CreateQueryDef", A.Str("QSet"), A.Str("PARAMETERS pS Text(50), pI Long; UPDATE T SET S = pS WHERE ID = pI;"))
                    .CallBind("q3", "db", "CreateQueryDef", A.Str("QAll"), A.Str("SELECT ID, Code, L, S FROM T ORDER BY ID;"))
                    .Release("q1").Release("q2").Release("q3").FinishLite("db");
            })
        { CreatesFixture = true };
    }

    // ADO-CONN --------------------------------------------------------------------------------------------

    private static IEnumerable<Probe> ConnectionProbes()
    {
        yield return Q("conn-cs-bare-path", Refs("ADO-CONN", "ADO-PROVIDER"),
            "ADO-CONN: three fresh Connections, each given only the fixture path: (a) ConnectionString = the bare file path (no Provider key), read ConnectionString and Provider, then Open() with no arguments; " +
            "(b) Provider = ACE first, then ConnectionString = the bare path, read Provider and ConnectionString, then Open(); (c) Open(path, \"Admin\", \"\") on a Connection with nothing else set. " +
            "After each Open read State, Provider, ConnectionString and the Errors collection, then Close. Asks which forms open the file, which provider each ends with and what ConnectionString reads before and after.",
            p =>
            {
                p.EngineLite();
                p.Create("ADODB.Connection", "ca").Put("ca", "ConnectionString", A.In()).Get("ca", "ConnectionString").Get("ca", "Provider").Call("ca", "Open");
                OpenedState(p, "ca");
                p.Create("ADODB.Connection", "cb").Put("cb", "Provider", A.Str(Ace)).Put("cb", "ConnectionString", A.In()).Get("cb", "Provider").Get("cb", "ConnectionString").Call("cb", "Open");
                OpenedState(p, "cb");
                p.Create("ADODB.Connection", "cc").Call("cc", "Open", A.In(), A.Str("Admin"), A.Str(""));
                OpenedState(p, "cc");
                p.FinishLite();
            });

        yield return Q("conn-datasource-spellings", Refs("ADO-CONN", "R-LOCK-1"),
            "ADO-CONN path spellings: five Connections with Provider = ACE and Mode = adModeRead, each Properties(\"Data Source\") set to the same fixture file in another spelling " +
            "(as given, upper case, extended \\\\?\\ prefix, forward slashes, a \".\" path element), then Open(). After each Open read State, the Data Source property, ConnectionString and Errors, then Close. " +
            "Asks which spellings the provider accepts and what value the property and ConnectionString hold afterwards.",
            p =>
            {
                p.EngineLite();
                (string Tag, Arg Path)[] spellings =
                [
                    ("plain", A.In()), ("upper", A.InAs("upper")), ("extended", A.InAs("extended")), ("slashes", A.InAs("slashes")), ("dot", A.InAs("dot")),
                ];
                foreach ((string tag, Arg path) in spellings)
                {
                    string c = "sp_" + tag;
                    p.Create("ADODB.Connection", c).Put(c, "Provider", A.Str(Ace));
                    SetDataSource(p, c, path);
                    p.Put(c, "Mode", A.I4(Ado.ModeRead)).Call(c, "Open");
                    p.GetBind(c + "Pr", c, "Properties").Item(c + "Dx", c + "Pr", A.Str("Data Source")).Get(c + "Dx", "Value").Release(c + "Dx").Release(c + "Pr");
                    OpenedState(p, c);
                }
                p.FinishLite();
            });

        yield return Q("conn-open-args", Refs("ADO-CONN"),
            "ADO-CONN Open arguments: seven Connections with Provider (or a ConnectionString), Data Source through Properties and Mode = adModeRead, each opened with a different (ConnectionString, UserID, Password, Options) argument list: " +
            "(UserID \"Admin\", empty Password), (\"Admin\", \"pw\"), (\"nouser\", empty), Options -1, Options 99, UserID and Password Null, and a ConnectionString property holding \"User ID=Admin\" with UserID argument \"other\". " +
            "After each Open read State, ConnectionString and Errors, then Close. Asks which arguments the provider and ADO accept and how a string value and an argument combine.",
            p =>
            {
                p.EngineLite();
                (string Bind, string? Cs, Arg[] Args)[] cases =
                [
                    ("oa", null, [A.Missing, A.Str("Admin"), A.Str("")]),
                    ("ob", null, [A.Missing, A.Str("Admin"), A.Str("pw")]),
                    ("oc", null, [A.Missing, A.Str("nouser"), A.Str("")]),
                    ("od", null, [A.Missing, A.Missing, A.Missing, A.I4(-1)]),
                    ("oe", null, [A.Missing, A.Missing, A.Missing, A.I4(99)]),
                    ("of", null, [A.Missing, A.Null, A.Null]),
                    ("og", $"Provider={Ace};User ID=Admin", [A.Missing, A.Str("other"), A.Str("")]),
                ];
                foreach ((string bind, string? cs, Arg[] args) in cases)
                {
                    p.Create("ADODB.Connection", bind);
                    if (cs is null)
                    {
                        p.Put(bind, "Provider", A.Str(Ace));
                    }
                    else
                    {
                        p.Put(bind, "ConnectionString", A.Str(cs));
                    }
                    SetDataSource(p, bind, A.In());
                    p.Put(bind, "Mode", A.I4(Ado.ModeRead)).Call(bind, "Open", args);
                    OpenedState(p, bind);
                }
                p.FinishLite();
            });

        yield return Q("conn-open-async-connect", Refs("ADO-CONN", "ADO-ASYNC", "D-CALL-1"),
            "ADO-ASYNC for connection: Open(Missing, Missing, Missing, adAsyncConnect) on the fixture without an event sink; read State immediately, pause 500 ms, read State again, read Errors, then Execute a SELECT of T (State, rows) and Close; " +
            "then the same Open on a nonexistent work file (State immediately, after the pause, Errors). Asks whether Open returns before the connection is complete, what State reads and where the failure surfaces.",
            p =>
            {
                p.EngineLite().Create("ADODB.Connection", "ca").Put("ca", "Provider", A.Str(Ace));
                SetDataSource(p, "ca", A.In());
                p.Put("ca", "Mode", A.I4(Ado.ModeRead)).Call("ca", "Open", A.Missing, A.Missing, A.Missing, A.I4(Ado.AsyncConnect)).Get("ca", "State")
                    .Pause(500).Get("ca", "State");
                AdoErrors(p, "ca");
                ExecRead(p, "ca", "ra", "SELECT ID, Code FROM T ORDER BY ID");
                p.Call("ca", "Close").Release("ca");
                p.Create("ADODB.Connection", "cb").Put("cb", "Provider", A.Str(Ace));
                SetDataSource(p, "cb", A.Out("absent-async.accdb"));
                p.Call("cb", "Open", A.Missing, A.Missing, A.Missing, A.I4(Ado.AsyncConnect)).Get("cb", "State").Pause(500).Get("cb", "State");
                AdoErrors(p, "cb");
                p.Call("cb", "Close").Release("cb").FinishLite();
            });

        yield return Q("conn-set-while-open", Refs("ADO-CONN", "ADO-PROPS"),
            "ADO-CONN assignment on an open read/write Connection: read the state; then, one at a time with the value read back and the Errors read and cleared after each, assign ConnectionString (another string), Provider (MSDASQL), Mode (adModeRead), " +
            "CursorLocation (client), CommandTimeout (5), IsolationLevel (serializable), DefaultDatabase (\"x\"), Attributes (retaining) and the properties Data Source (a work file) and Jet OLEDB:Database Locking Mode (1); " +
            "then Close, read the state again, Open() again and read the state. Asks which assignments an open Connection accepts or refuses and which values survive Close and Open.",
            p =>
            {
                p.EngineLite();
                AdoOpen(p, "cn", readOnly: false);
                ConnState(p, "cn");
                PutGet(p, "cn", "ConnectionString", A.Str($"Provider={Ace};Mode=Read"));
                ErrClear(p, "cn");
                PutGet(p, "cn", "Provider", A.Str("MSDASQL"));
                ErrClear(p, "cn");
                PutGet(p, "cn", "Mode", A.I4(Ado.ModeRead));
                ErrClear(p, "cn");
                PutGet(p, "cn", "CursorLocation", A.I4(Ado.UseClient));
                ErrClear(p, "cn");
                PutGet(p, "cn", "CommandTimeout", A.I4(5));
                ErrClear(p, "cn");
                PutGet(p, "cn", "IsolationLevel", A.I4(Ado.XactSerializable));
                ErrClear(p, "cn");
                PutGet(p, "cn", "DefaultDatabase", A.Str("x"));
                ErrClear(p, "cn");
                PutGet(p, "cn", "Attributes", A.I4(0x20000));
                ErrClear(p, "cn");
                PropPut(p, "cn", "Data Source", A.Out("other.accdb"), "w1");
                ErrClear(p, "cn");
                PropPut(p, "cn", "Jet OLEDB:Database Locking Mode", A.I4(1), "w2");
                ErrClear(p, "cn");
                ConnState(p, "cn");
                p.Call("cn", "Close");
                ConnState(p, "cn");
                p.Call("cn", "Open").Get("cn", "State");
                ConnState(p, "cn");
                AdoErrors(p, "cn");
                p.FinishLite("cn");
            });

        yield return Q("conn-cursorlocation-inherit", Refs("ADO-CONN", "ADO-CURSOR"),
            "ADO-CURSOR defaults from the Connection: Connection.CursorLocation = client before Open; a Recordset with no CursorLocation set reads its CursorLocation before Open, then opens a forward-only read-only request on T " +
            "(State, effective CursorType, LockType, CursorLocation, RecordCount, Supports adBookmark); a second Recordset sets server explicitly first; Connection.CursorLocation is then set to server and a third Recordset opens; " +
            "a keyset request on the client Connection; a Connection.Execute result and a Command.Execute result on each setting. Asks how a Recordset takes its location and what the effective tuple is in each case.",
            p =>
            {
                p.EngineLite().Create("ADODB.Connection", "cn").Put("cn", "Provider", A.Str(Ace));
                SetDataSource(p, "cn", A.In());
                p.Put("cn", "Mode", A.I4(Ado.ModeRead)).Get("cn", "CursorLocation").Put("cn", "CursorLocation", A.I4(Ado.UseClient)).Call("cn", "Open").Get("cn", "CursorLocation");
                void Inherit(string bind, int type, int? location)
                {
                    p.Create("ADODB.Recordset", bind).Get(bind, "CursorLocation");
                    if (location is int l)
                    {
                        p.Put(bind, "CursorLocation", A.I4(l));
                    }
                    p.Call(bind, "Open", A.Str("SELECT ID, S FROM T ORDER BY ID"), A.Obj("cn"), A.I4(type), A.I4(Ado.LockReadOnly), A.I4(Ado.CmdText));
                    RsState(p, bind);
                    p.Get(bind, "RecordCount").Call(bind, "Supports", A.I4(Ado.SupBookmark)).Call(bind, "Close").Release(bind);
                }
                Inherit("i1", Ado.OpenForwardOnly, null);
                Inherit("i2", Ado.OpenForwardOnly, Ado.UseServer);
                Inherit("i3", Ado.OpenKeyset, null);
                p.CallBind("e1", "cn", "Execute", A.Str("SELECT ID, S FROM T"), A.Missing, A.I4(Ado.CmdText));
                RsState(p, "e1");
                NewCmd(p, "cmd", "cn", "SELECT ID, S FROM T");
                p.CallBind("e2", "cmd", "Execute");
                RsState(p, "e2");
                p.Release("e1").Release("e2").Release("cmd").Put("cn", "CursorLocation", A.I4(Ado.UseServer)).Get("cn", "CursorLocation");
                Inherit("i4", Ado.OpenForwardOnly, null);
                Inherit("i5", Ado.OpenKeyset, null);
                p.CallBind("e3", "cn", "Execute", A.Str("SELECT ID, S FROM T"), A.Missing, A.I4(Ado.CmdText));
                RsState(p, "e3");
                p.Release("e3");
                AdoErrors(p, "cn");
                p.FinishLite("cn");
            });
    }

    // ADO-PARAM and Command ------------------------------------------------------------------------------

    private static IEnumerable<Probe> CommandProbes()
    {
        yield return Q("cmd-before-connection", Refs("ADO-PARAM", "ADO-CONN", "ADO-PROPS"),
            "ADO-PARAM before a connection exists: on a fresh Command read State, CommandType, CommandText, CommandTimeout, Prepared, NamedParameters and ActiveConnection, Parameters.Count and Parameters.Refresh; " +
            "CreateParameter and Append; Execute; set CommandText with a '?' marker and Refresh, Count and Execute; open a Connection and set ActiveConnection by reference; read State, the retained Parameters wrapper's Count, " +
            "a fresh Parameters wrapper's Count and Refresh, then Execute and read State. Asks which calls need a connection, which refuse and whether the retained wrapper follows the connection.",
            p =>
            {
                p.EngineLite().Create("ADODB.Command", "cmd").Get("cmd", "State").Get("cmd", "CommandType").Get("cmd", "CommandText").Get("cmd", "CommandTimeout")
                    .Get("cmd", "Prepared").Get("cmd", "NamedParameters").Get("cmd", "ActiveConnection")
                    .GetBind("prs", "cmd", "Parameters").Get("prs", "Count").Call("prs", "Refresh")
                    .CallBind("px", "cmd", "CreateParameter", A.Str("x"), A.I4(Ado.Integer), A.I4(Ado.ParamInput), A.Missing, A.I4(1)).Call("prs", "Append", A.Obj("px")).Get("prs", "Count")
                    .Call("cmd", "Execute")
                    .Put("cmd", "CommandText", A.Str("SELECT ID FROM T WHERE L = ?")).Call("prs", "Refresh").Get("prs", "Count").Call("cmd", "Execute");
                AdoOpen(p, "cn");
                p.PutRef("cmd", "ActiveConnection", "cn").Get("cmd", "State").Get("prs", "Count").GetBind("prs2", "cmd", "Parameters").Get("prs2", "Count").Call("prs2", "Refresh").Get("prs2", "Count")
                    .CallBind("r1", "cmd", "Execute").Get("r1", "State");
                AdoErrors(p, "cn");
                p.Release("r1").Release("prs2").Release("px").Release("prs").Release("cmd").FinishLite("cn");
            });

        yield return Q("cmd-execute-args", Refs("ADO-RESULTS", "ADO-PARAM"),
            "ADO-RESULTS argument slots: a Command on 'SELECT ID, Code FROM T WHERE L = ?' executed with Execute(RecordsAffected, Parameters, Options) for nine argument lists: (Missing, Long 20), (Missing, String \"20\"), (Missing, Null), (Missing, Empty), " +
            "(Long 0 in the RecordsAffected slot, Long 20), (String \"x\" in the RecordsAffected slot, Long 20), (Missing, Long 20, adExecuteNoRecords), (Missing, Long 20, adCmdUnknown), (Missing, Long 20, an invalid Options value 12345); " +
            "each reads State, then all rows, then Errors cleared. Then Connection.Execute of an UPDATE with Long 0, Null and Empty in the RecordsAffected slot. Asks which scalar values the Parameters and RecordsAffected slots accept when sent by value.",
            p =>
            {
                p.EngineLite();
                AdoOpen(p, "cn", readOnly: false);
                NewCmd(p, "cmd", "cn", "SELECT ID, Code FROM T WHERE L = ?");
                (Arg Affected, Arg Params, int Options)[] cases =
                [
                    (A.Missing, A.I4(20), Ado.CmdText), (A.Missing, A.Str("20"), Ado.CmdText), (A.Missing, A.Null, Ado.CmdText), (A.Missing, A.Empty, Ado.CmdText),
                    (A.I4(0), A.I4(20), Ado.CmdText), (A.Str("x"), A.I4(20), Ado.CmdText), (A.Missing, A.I4(20), Ado.CmdText | Ado.ExecuteNoRecords),
                    (A.Missing, A.I4(20), Ado.CmdUnknown), (A.Missing, A.I4(20), 12345),
                ];
                int n = 0;
                foreach ((Arg affected, Arg prm, int options) in cases)
                {
                    string r = "x" + n++;
                    p.CallBind(r, "cmd", "Execute", affected, prm, A.I4(options)).Get(r, "State");
                    AdoReadAll(p, r, 8, 2);
                    p.Release(r);
                    ErrClear(p, "cn");
                }
                foreach (Arg affected in new[] { A.I4(0), A.Null, A.Empty })
                {
                    p.Call("cn", "Execute", A.Str("UPDATE T SET S = S WHERE ID <= 3"), affected, A.I4(Ado.CmdText | Ado.ExecuteNoRecords));
                    ErrClear(p, "cn");
                }
                p.Release("cmd").FinishLite("cn");
            });

        yield return Q("param-create-forms", Refs("ADO-PARAM"),
            "ADO-PARAM construction: for twenty CreateParameter argument lists on a Command with one '?' marker (no arguments; name only; Integer with and without a value; VarWChar without Size, with Size 2 and a six-character value, with Size 0 and with Size -1; LongVarWChar 100; " +
            "Decimal; Currency; Date; Boolean; VarBinary 3 bytes; GUID; an undefined Type 9999; an undefined Direction 99; Output and ReturnValue directions with no value; Integer with Size 8), read Name, Type, Direction, Size, Precision, NumericScale, Attributes and Value of the new Parameter, " +
            "Append it to a fresh Command's collection, read Count and the Errors. Asks the defaults CreateParameter assigns and which arguments are refused at CreateParameter or at Append.",
            p =>
            {
                p.EngineLite();
                AdoOpen(p, "cn");
                (string Tag, Arg[] Args)[] forms =
                [
                    ("none", []),
                    ("name", [A.Str("x")]),
                    ("int-value", [A.Str("x"), A.I4(Ado.Integer), A.I4(Ado.ParamInput), A.Missing, A.I4(20)]),
                    ("int-no-value", [A.Str("x"), A.I4(Ado.Integer), A.I4(Ado.ParamInput)]),
                    ("wchar-no-size", [A.Str("x"), A.I4(Ado.VarWChar), A.I4(Ado.ParamInput), A.Missing, A.Str("abc")]),
                    ("wchar-size2", [A.Str("x"), A.I4(Ado.VarWChar), A.I4(Ado.ParamInput), A.I4(2), A.Str("abcdef")]),
                    ("wchar-size0", [A.Str("x"), A.I4(Ado.VarWChar), A.I4(Ado.ParamInput), A.I4(0), A.Str("abc")]),
                    ("wchar-size-1", [A.Str("x"), A.I4(Ado.VarWChar), A.I4(Ado.ParamInput), A.I4(-1), A.Str("abc")]),
                    ("longwchar", [A.Str("x"), A.I4(Ado.LongVarWChar), A.I4(Ado.ParamInput), A.I4(100), A.Str("abc")]),
                    ("decimal", [A.Str("x"), A.I4(Ado.Decimal), A.I4(Ado.ParamInput), A.Missing, A.Str("1.5")]),
                    ("currency", [A.Str("x"), A.I4(Ado.Currency), A.I4(Ado.ParamInput), A.Missing, A.Cy(1.5m)]),
                    ("date", [A.Str("x"), A.I4(Ado.Date), A.I4(Ado.ParamInput), A.Missing, A.Date(45292.0)]),
                    ("boolean", [A.Str("x"), A.I4(Ado.Boolean), A.I4(Ado.ParamInput), A.Missing, A.Bool(true)]),
                    ("varbinary", [A.Str("x"), A.I4(Ado.VarBinary), A.I4(Ado.ParamInput), A.I4(3), A.Bytes(1, 2, 3)]),
                    ("guid", [A.Str("x"), A.I4(Ado.Guid), A.I4(Ado.ParamInput), A.Missing, A.Str("{00112233-4455-6677-8899-AABBCCDDEEFF}")]),
                    ("type-9999", [A.Str("x"), A.I4(9999), A.I4(Ado.ParamInput)]),
                    ("direction-99", [A.Str("x"), A.I4(Ado.Integer), A.I4(99)]),
                    ("output", [A.Str("x"), A.I4(Ado.Integer), A.I4(Ado.ParamOutput), A.Missing, A.Missing]),
                    ("return", [A.Str("x"), A.I4(Ado.Integer), A.I4(Ado.ParamReturnValue)]),
                    ("int-size8", [A.Str("x"), A.I4(Ado.Integer), A.I4(Ado.ParamInput), A.I4(8), A.I4(5)]),
                ];
                foreach ((string _, Arg[] args) in forms)
                {
                    NewCmd(p, "cm", "cn", "SELECT ID FROM T WHERE L = ?");
                    p.CallBind("pc", "cm", "CreateParameter", args);
                    ParamMeta(p, "pc");
                    p.GetBind("cps", "cm", "Parameters").Call("cps", "Append", A.Obj("pc")).Get("cps", "Count");
                    ErrClear(p, "cn");
                    p.Release("cps").Release("pc").Release("cm");
                }
                p.FinishLite("cn");
            });

        yield return Q("param-mutate-after-append", Refs("ADO-PARAM"),
            "ADO-PARAM live binding: a Command on 'SELECT ID, Code FROM T WHERE L = ?' with one appended Integer input Parameter (20), executed; then, each followed by Execute, the rows and the Errors: Value = 30; Type = VarWChar with Size 5 and Value \"20\"; " +
            "Name = \"y\" (Parameters.Item by the old and by the new name read); Direction = Output and back to Input; Value = Null; Value = Empty; Attributes = 64 and 16 (read back); NumericScale 3 and Precision 9 on the text parameter (read back). " +
            "Asks whether the appended Parameter is the object the Command binds at Execute and which metadata changes are accepted after Append.",
            p =>
            {
                p.EngineLite();
                AdoOpen(p, "cn");
                NewCmd(p, "cmd", "cn", "SELECT ID, Code FROM T WHERE L = ?");
                p.CallBind("px", "cmd", "CreateParameter", A.Str("x"), A.I4(Ado.Integer), A.I4(Ado.ParamInput), A.Missing, A.I4(20)).GetBind("prs", "cmd", "Parameters").Call("prs", "Append", A.Obj("px"));
                void Run(string tag)
                {
                    p.CallBind("r" + tag, "cmd", "Execute");
                    AdoReadAll(p, "r" + tag, 8, 2);
                    p.Release("r" + tag);
                    ErrClear(p, "cn");
                }
                Run("0");
                p.Put("px", "Value", A.I4(30)).Get("px", "Value");
                Run("1");
                p.Put("px", "Type", A.I4(Ado.VarWChar)).Put("px", "Size", A.I4(5)).Put("px", "Value", A.Str("20")).Get("px", "Type").Get("px", "Size");
                Run("2");
                p.Put("px", "Name", A.Str("y")).Get("px", "Name").Item("pi1", "prs", A.Str("x")).Get("pi1", "Name").Release("pi1").Item("pi2", "prs", A.Str("y")).Get("pi2", "Name").Release("pi2");
                ErrClear(p, "cn");
                Run("3");
                p.Put("px", "Direction", A.I4(Ado.ParamOutput)).Get("px", "Direction");
                Run("4");
                p.Put("px", "Direction", A.I4(Ado.ParamInput)).Put("px", "Value", A.Null);
                Run("5");
                p.Put("px", "Value", A.Empty).Get("px", "Value");
                Run("6");
                p.Put("px", "Attributes", A.I4(64)).Get("px", "Attributes").Put("px", "Attributes", A.I4(16)).Get("px", "Attributes");
                Run("7");
                p.Put("px", "NumericScale", A.I4(3)).Put("px", "Precision", A.I4(9)).Get("px", "NumericScale").Get("px", "Precision");
                Run("8");
                p.Release("px").Release("prs").Release("cmd").FinishLite("cn");
            });

        yield return Q("param-refresh-after-text-change", Refs("ADO-PARAM"),
            "ADO-PARAM discovery after changes: a Command on 'SELECT ID FROM T WHERE L = ?' with Parameters.Refresh and the inventory (Count; Name, Type, Direction, Size, Precision, NumericScale, Value); then CommandText with a second marker (inventory without Refresh, Execute, Refresh, inventory); " +
            "CommandType = adCmdStoredProc with CommandText \"QByL\" (inventory, Refresh, inventory); Prepared = True (inventory, then Execute with Parameters(0) = 20); CommandText = 'SELECT ID FROM T' (inventory, Execute); CommandText = \"QSet\" (Refresh, inventory); " +
            "each step also reads a fresh Parameters.Count beside the retained wrapper. Asks when the collection is rebuilt, kept or emptied and what Execute does with a stale collection.",
            p =>
            {
                p.EngineLite();
                AdoOpen(p, "cn");
                NewCmd(p, "cmd", "cn", "SELECT ID FROM T WHERE L = ?");
                p.GetBind("prs", "cmd", "Parameters").Call("prs", "Refresh");
                ParamInventory(p, "prs");
                void Fresh() => p.GetBind("pf", "cmd", "Parameters").Get("pf", "Count").Release("pf");
                p.Put("cmd", "CommandText", A.Str("SELECT ID FROM T WHERE L = ? AND S = ?"));
                ParamInventory(p, "prs");
                Fresh();
                p.CallBind("r1", "cmd", "Execute");
                ErrClear(p, "cn");
                p.Call("prs", "Refresh");
                ParamInventory(p, "prs");
                p.Put("cmd", "CommandType", A.I4(Ado.CmdStoredProc)).Put("cmd", "CommandText", A.Str("QByL"));
                ParamInventory(p, "prs");
                Fresh();
                p.Call("prs", "Refresh");
                ParamInventory(p, "prs");
                p.Put("cmd", "Prepared", A.Bool(true));
                ParamInventory(p, "prs");
                p.Item("p0", "prs", A.I4(0)).Put("p0", "Value", A.I4(20)).CallBind("r2", "cmd", "Execute");
                AdoReadAll(p, "r2", 8, 3);
                p.Release("p0").Release("r2");
                ErrClear(p, "cn");
                p.Put("cmd", "CommandType", A.I4(Ado.CmdText)).Put("cmd", "CommandText", A.Str("SELECT ID FROM T"));
                ParamInventory(p, "prs");
                Fresh();
                p.CallBind("r3", "cmd", "Execute").Release("r3");
                ErrClear(p, "cn");
                p.Put("cmd", "CommandType", A.I4(Ado.CmdStoredProc)).Put("cmd", "CommandText", A.Str("QSet")).Call("prs", "Refresh");
                ParamInventory(p, "prs");
                Fresh();
                p.Release("r1").Release("prs").Release("cmd").FinishLite("cn");
            });

        yield return Q("param-connection-change", Refs("ADO-PARAM", "ADO-SESSION"),
            "ADO-PARAM against connection changes: a Command on 'SELECT ID, Code FROM T WHERE L = ?' with ActiveConnection cn1 and a refreshed Parameters inventory; ActiveConnection set by reference to a second open Connection (State, inventory, Parameters(0) = 20, Execute and rows); " +
            "the first Connection closed (State, ActiveConnection, inventory, Execute); the second closed (inventory, Refresh, Execute and the second's Errors); ActiveConnection set to a third Connection that was never opened (State, Execute, inventory). " +
            "Asks what the Command and its collection keep when its connection changes or closes.",
            p =>
            {
                p.EngineLite();
                AdoOpen(p, "cn1");
                AdoOpen(p, "cn2");
                NewCmd(p, "cmd", "cn1", "SELECT ID, Code FROM T WHERE L = ?");
                p.GetBind("prs", "cmd", "Parameters").Call("prs", "Refresh");
                ParamInventory(p, "prs");
                p.PutRef("cmd", "ActiveConnection", "cn2").Get("cmd", "State");
                ParamInventory(p, "prs");
                p.Item("p0", "prs", A.I4(0)).Put("p0", "Value", A.I4(20)).CallBind("r1", "cmd", "Execute");
                AdoReadAll(p, "r1", 8, 2);
                p.Release("r1").Call("cn1", "Close").Get("cmd", "State").Get("cmd", "ActiveConnection");
                ParamInventory(p, "prs");
                p.CallBind("r2", "cmd", "Execute");
                AdoReadAll(p, "r2", 8, 2);
                p.Release("r2").Call("cn2", "Close");
                ParamInventory(p, "prs");
                p.Call("prs", "Refresh").Call("cmd", "Execute");
                AdoErrors(p, "cn2");
                p.Create("ADODB.Connection", "cn3").PutRef("cmd", "ActiveConnection", "cn3").Get("cmd", "State").Call("cmd", "Execute");
                ParamInventory(p, "prs");
                AdoErrors(p, "cn3");
                p.Release("p0").Release("prs").Release("cmd").FinishLite("cn1", "cn2", "cn3");
            });

        yield return Q("param-value-tag-matrix", Refs("ADO-PARAM", "R-ERR-1"),
            "ADO-PARAM value conversion into one Integer input parameter of 'SELECT ID FROM T WHERE L = ?': for each value tag (Long 20, Integer 20, Byte 20, String \"20\", Double 20.0, Double 20.5, Single 20, Boolean True, Currency 20, Date 20.0, String \"2e1\", \" 20 \", \"0x14\", \"\", \"abc\", Null, Empty, a one-byte array) " +
            "assign Parameters value, read it back, Execute, read all rows and the Errors (cleared). Asks which tags convert, which are refused at assignment or at Execute and which rows match.",
            p =>
            {
                p.EngineLite();
                AdoOpen(p, "cn");
                NewCmd(p, "cmd", "cn", "SELECT ID FROM T WHERE L = ?");
                p.CallBind("px", "cmd", "CreateParameter", A.Str("x"), A.I4(Ado.Integer), A.I4(Ado.ParamInput), A.Missing, A.I4(20)).GetBind("prs", "cmd", "Parameters").Call("prs", "Append", A.Obj("px"));
                Arg[] values =
                [
                    A.I4(20), A.I2(20), A.UI1(20), A.Str("20"), A.R8(20.0), A.R8(20.5), A.R4(20f), A.Bool(true), A.Cy(20m), A.Date(20.0),
                    A.Str("2e1"), A.Str(" 20 "), A.Str("0x14"), A.Str(""), A.Str("abc"), A.Null, A.Empty, A.Bytes(0x14),
                ];
                for (int i = 0; i < values.Length; i++)
                {
                    string r = "v" + i;
                    p.Put("px", "Value", values[i]).Get("px", "Value").CallBind(r, "cmd", "Execute");
                    AdoReadAll(p, r, 8, 1);
                    p.Release(r);
                    ErrClear(p, "cn");
                }
                p.Release("px").Release("prs").Release("cmd").FinishLite("cn");
            });

        yield return Q("param-decimal-scale", Refs("ADO-PARAM", "R-FMT-1"),
            "ADO-PARAM Decimal precision and scale: for each (Precision, NumericScale) of (default, default), (18, 3), (5, 2), (28, 10), (1, 0), a Command on 'SELECT ID FROM T WHERE N = ?' with an appended Decimal input parameter, " +
            "read its metadata, then for each value (String \"3.125\", Double 3.125, Currency 3.125, String \"3.1251\", Long 3) assign, Execute, read all rows and the Errors (cleared). " +
            "Asks how precision and scale change conversion and matching against the Decimal column.",
            p =>
            {
                p.EngineLite();
                AdoOpen(p, "cn");
                (int? Prec, int? Scale)[] shapes = [(null, null), (18, 3), (5, 2), (28, 10), (1, 0)];
                Arg[] values = [A.Str("3.125"), A.R8(3.125), A.Cy(3.125m), A.Str("3.1251"), A.I4(3)];
                for (int s = 0; s < shapes.Length; s++)
                {
                    NewCmd(p, "cmd", "cn", "SELECT ID FROM T WHERE N = ?");
                    p.CallBind("px", "cmd", "CreateParameter", A.Str("x"), A.I4(Ado.Decimal), A.I4(Ado.ParamInput), A.Missing, A.Str("3.125"));
                    if (shapes[s].Prec is int prec)
                    {
                        p.Put("px", "Precision", A.I4(prec));
                    }
                    if (shapes[s].Scale is int scale)
                    {
                        p.Put("px", "NumericScale", A.I4(scale));
                    }
                    ParamMeta(p, "px");
                    p.GetBind("prs", "cmd", "Parameters").Call("prs", "Append", A.Obj("px"));
                    for (int v = 0; v < values.Length; v++)
                    {
                        string r = "d" + s + "_" + v;
                        p.Put("px", "Value", values[v]).CallBind(r, "cmd", "Execute");
                        AdoReadAll(p, r, 8, 1);
                        p.Release(r);
                        ErrClear(p, "cn");
                    }
                    p.Release("prs").Release("px").Release("cmd");
                }
                p.FinishLite("cn");
            });
    }

    // ADO-CURSOR, ADO-BOOK ------------------------------------------------------------------------------

    private static IEnumerable<Probe> CursorProbes()
    {
        yield return Q("rs-lock-matrix", Refs("ADO-CURSOR", "ADO-EDIT"),
            "ADO-CURSOR requested versus effective tuple on a read/write Connection, SELECT ID, S FROM T: twelve Recordset requests (location, CursorType, LockType): server keyset with each of the four lock types, client static with each of the four, " +
            "server static batch, client keyset optimistic, client dynamic optimistic and client forward-only optimistic. For each read State, effective CursorType, LockType, CursorLocation, RecordCount, Supports of adUpdate, adAddNew, adDelete and adUpdateBatch, " +
            "then the Errors (cleared) and Close. Asks the effective tuple and edit capabilities for each lock type and location.",
            p =>
            {
                p.EngineLite();
                AdoOpen(p, "cn", readOnly: false);
                (int Loc, int Type, int Lock)[] cases =
                [
                    (Ado.UseServer, Ado.OpenKeyset, Ado.LockReadOnly), (Ado.UseServer, Ado.OpenKeyset, Ado.LockPessimistic),
                    (Ado.UseServer, Ado.OpenKeyset, Ado.LockOptimistic), (Ado.UseServer, Ado.OpenKeyset, Ado.LockBatchOptimistic),
                    (Ado.UseClient, Ado.OpenStatic, Ado.LockReadOnly), (Ado.UseClient, Ado.OpenStatic, Ado.LockPessimistic),
                    (Ado.UseClient, Ado.OpenStatic, Ado.LockOptimistic), (Ado.UseClient, Ado.OpenStatic, Ado.LockBatchOptimistic),
                    (Ado.UseServer, Ado.OpenStatic, Ado.LockBatchOptimistic), (Ado.UseClient, Ado.OpenKeyset, Ado.LockOptimistic),
                    (Ado.UseClient, Ado.OpenDynamic, Ado.LockOptimistic), (Ado.UseClient, Ado.OpenForwardOnly, Ado.LockOptimistic),
                ];
                for (int i = 0; i < cases.Length; i++)
                {
                    string r = "m" + i;
                    AdoRs(p, r, "SELECT ID, S FROM T ORDER BY ID", "cn", cases[i].Loc, cases[i].Type, cases[i].Lock, Ado.CmdText);
                    RsState(p, r);
                    p.Get(r, "RecordCount").Call(r, "Supports", A.I4(Ado.SupUpdate)).Call(r, "Supports", A.I4(Ado.SupAddNew)).Call(r, "Supports", A.I4(Ado.SupDelete))
                        .Call(r, "Supports", A.I4(Ado.SupUpdateBatch));
                    ErrClear(p, "cn");
                    p.Call(r, "Close").Release(r);
                }
                p.FinishLite("cn");
            });

        yield return Q("rs-readonly-mode-edit", Refs("ADO-CURSOR", "ADO-EDIT", "R-LOCK-2"),
            "ADO-EDIT on a Mode=Read Connection: (a) a server keyset optimistic Recordset on SELECT ID, S FROM T: State, effective LockType, Supports adUpdate, set S = \"ro\", Update, EditMode, Errors, CancelUpdate, AddNew with S, Update, Delete; " +
            "(b) a server keyset pessimistic request (effective LockType, edit of S, Update); (c) a client static batch optimistic Recordset (effective tuple, set S, UpdateBatch, Status, Errors); then the stored rows through Execute. " +
            "Asks whether a read-mode connection refuses at open, at edit, at Update or UpdateBatch, and what the tuple reports.",
            p =>
            {
                p.EngineLite();
                AdoOpen(p, "cn");
                foreach ((string bind, int loc, int type, int lck) in new[]
                    { ("ka", Ado.UseServer, Ado.OpenKeyset, Ado.LockOptimistic), ("kb", Ado.UseServer, Ado.OpenKeyset, Ado.LockPessimistic) })
                {
                    AdoRs(p, bind, "SELECT ID, S FROM T ORDER BY ID", "cn", loc, type, lck, Ado.CmdText);
                    RsState(p, bind);
                    p.Call(bind, "Supports", A.I4(Ado.SupUpdate)).GetBind(bind + "f", bind, "Fields").Item(bind + "s", bind + "f", A.Str("S"))
                        .Put(bind + "s", "Value", A.Str("ro-" + bind)).Get(bind, "EditMode").Call(bind, "Update").Get(bind, "EditMode");
                    ErrClear(p, "cn");
                    p.Call(bind, "CancelUpdate").Call(bind, "AddNew").Put(bind + "s", "Value", A.Str("ro-new")).Call(bind, "Update");
                    ErrClear(p, "cn");
                    p.Call(bind, "CancelUpdate").Call(bind, "Delete");
                    ErrClear(p, "cn");
                    p.Release(bind + "s").Release(bind + "f").Call(bind, "Close").Release(bind);
                }
                AdoRs(p, "kc", "SELECT ID, S FROM T ORDER BY ID", "cn", Ado.UseClient, Ado.OpenStatic, Ado.LockBatchOptimistic, Ado.CmdText);
                RsState(p, "kc");
                p.GetBind("kcf", "kc", "Fields").Item("kcs", "kcf", A.Str("S")).Put("kcs", "Value", A.Str("ro-batch")).Call("kc", "UpdateBatch", A.I4(Ado.AffectAll)).Get("kc", "Status");
                AdoErrors(p, "cn");
                p.Release("kcs").Release("kcf").Call("kc", "Close").Release("kc");
                ExecRead(p, "cn", "st", StoredT);
                p.FinishLite("cn");
            });

        yield return Q("rs-source-kinds", Refs("ADO-CURSOR", "ADO-PARAM", "ADO-RESULTS"),
            "ADO-CURSOR source and Options crossing: server keyset read-only Recordset.Open of the sources T, QAll, QByL (parameter unset), 'SELECT ID FROM T', 'EXEC QAll', '{call QAll}' and 'NoSuchThing' with Options adCmdUnknown, adCmdText, adCmdTable, adCmdStoredProc and adCmdTableDirect as listed " +
            "(fifteen combinations); each reads State, Fields.Count, the Source property, RecordCount and the Errors (cleared). Then a Command (QAll as stored procedure) is passed as Source with and without the ActiveConnection argument (State, Source, ActiveConnection). " +
            "Asks which source forms each Options value accepts and what Source reads back.",
            p =>
            {
                p.EngineLite();
                AdoOpen(p, "cn");
                (string Src, int Options)[] cases =
                [
                    ("T", Ado.CmdUnknown), ("T", Ado.CmdTable), ("T", Ado.CmdTableDirect), ("T", Ado.CmdText),
                    ("QAll", Ado.CmdUnknown), ("QAll", Ado.CmdStoredProc), ("QAll", Ado.CmdTable), ("QAll", Ado.CmdText),
                    ("QByL", Ado.CmdUnknown), ("SELECT ID FROM T", Ado.CmdUnknown), ("SELECT ID FROM T", Ado.CmdTable), ("SELECT ID FROM T", Ado.CmdStoredProc),
                    ("EXEC QAll", Ado.CmdText), ("{call QAll}", Ado.CmdText), ("NoSuchThing", Ado.CmdUnknown),
                ];
                for (int i = 0; i < cases.Length; i++)
                {
                    string r = "k" + i;
                    p.Create("ADODB.Recordset", r).Call(r, "Open", A.Str(cases[i].Src), A.Obj("cn"), A.I4(Ado.OpenKeyset), A.I4(Ado.LockReadOnly), A.I4(cases[i].Options)).Get(r, "State")
                        .GetBind(r + "f", r, "Fields").Get(r + "f", "Count").Release(r + "f").Get(r, "Source").Get(r, "RecordCount");
                    ErrClear(p, "cn");
                    p.Call(r, "Close").Release(r);
                }
                NewCmd(p, "cmd", "cn", "QAll", Ado.CmdStoredProc);
                p.Create("ADODB.Recordset", "rc1").Call("rc1", "Open", A.Obj("cmd"), A.Missing, A.I4(Ado.OpenKeyset), A.I4(Ado.LockReadOnly), A.Missing).Get("rc1", "State")
                    .Get("rc1", "Source").Get("rc1", "ActiveConnection");
                ErrClear(p, "cn");
                p.Create("ADODB.Recordset", "rc2").Call("rc2", "Open", A.Obj("cmd"), A.Obj("cn"), A.I4(Ado.OpenKeyset), A.I4(Ado.LockReadOnly), A.Missing).Get("rc2", "State")
                    .Get("rc2", "Source").Get("rc2", "ActiveConnection");
                ErrClear(p, "cn");
                p.Release("rc1").Release("rc2").Release("cmd").FinishLite("cn");
            });

        yield return Q("rs-open-by-properties", Refs("ADO-CURSOR", "ADO-CONN"),
            "ADO-CURSOR properties versus arguments: a Recordset with ActiveConnection set by reference, Source = 'SELECT ID, S FROM T ORDER BY ID', CursorType keyset, LockType optimistic, then Open() with no arguments (State, Source, ActiveConnection, effective tuple); " +
            "while open, assign CursorType, LockType, Source, ActiveConnection (a second Connection), CursorLocation and MaxRecords (each read back), a second Open, Close, then read Source, ActiveConnection and CursorType and Open() again. " +
            "A second Recordset: Open with an empty-Source request, with no ActiveConnection, and with Source Missing after ActiveConnection is set. Asks which properties may change while open and what survives Close.",
            p =>
            {
                p.EngineLite();
                AdoOpen(p, "cn");
                AdoOpen(p, "cn2");
                p.Create("ADODB.Recordset", "rs").PutRef("rs", "ActiveConnection", "cn").Get("rs", "ActiveConnection").Put("rs", "Source", A.Str("SELECT ID, S FROM T ORDER BY ID"))
                    .Put("rs", "CursorType", A.I4(Ado.OpenKeyset)).Put("rs", "LockType", A.I4(Ado.LockOptimistic)).Call("rs", "Open").Get("rs", "State").Get("rs", "Source").Get("rs", "ActiveConnection");
                RsState(p, "rs");
                PutGet(p, "rs", "CursorType", A.I4(Ado.OpenStatic));
                PutGet(p, "rs", "LockType", A.I4(Ado.LockReadOnly));
                PutGet(p, "rs", "Source", A.Str("SELECT 1 AS One"));
                p.PutRef("rs", "ActiveConnection", "cn2").Get("rs", "ActiveConnection");
                PutGet(p, "rs", "CursorLocation", A.I4(Ado.UseClient));
                PutGet(p, "rs", "MaxRecords", A.I4(2));
                p.Call("rs", "Open").Call("rs", "Close").Get("rs", "State").Get("rs", "Source").Get("rs", "ActiveConnection").Get("rs", "CursorType")
                    .Call("rs", "Open").Get("rs", "State");
                RsState(p, "rs");
                AdoErrors(p, "cn");
                p.Call("rs", "Close").Release("rs");
                p.Create("ADODB.Recordset", "r2").Call("r2", "Open", A.Str(""), A.Obj("cn")).Get("r2", "State").Call("r2", "Open", A.Str("SELECT ID FROM T")).Get("r2", "State");
                p.PutRef("r2", "ActiveConnection", "cn").Call("r2", "Open", A.Missing).Get("r2", "State");
                AdoErrors(p, "cn");
                p.Release("r2").FinishLite("cn", "cn2");
            });

        yield return Q("rs-paging", Refs("ADO-CURSOR"),
            "ADO-CURSOR paging and MaxRecords on SELECT ID, S FROM T ORDER BY ID: a client static read-only Recordset reads PageSize, PageCount, AbsolutePage and RecordCount, then PageSize = 3 (PageCount), AbsolutePage = 2, 3, 99, -1 and 0 (current ID, AbsolutePosition, BOF, EOF after each), " +
            "PageSize 0, -1 and 100 (read back, PageCount); then Recordsets with MaxRecords = 4, 0 and -1 set before Open on server keyset and client static: RecordCount, all rows, MaxRecords read back. " +
            "Asks the paging model and whether MaxRecords limits the rows each cursor returns.",
            p =>
            {
                p.EngineLite();
                AdoOpen(p, "cn");
                AdoRs(p, "rs", "SELECT ID, S FROM T ORDER BY ID", "cn", Ado.UseClient, Ado.OpenStatic, Ado.LockReadOnly, Ado.CmdText);
                p.Get("rs", "PageSize").Get("rs", "PageCount").Get("rs", "AbsolutePage").Get("rs", "RecordCount");
                PutGet(p, "rs", "PageSize", A.I4(3));
                p.Get("rs", "PageCount");
                foreach (int page in new[] { 2, 3, 99, -1, 0 })
                {
                    p.Put("rs", "AbsolutePage", A.I4(page)).Get("rs", "AbsolutePage").Get("rs", "AbsolutePosition").Get("rs", "BOF").Get("rs", "EOF");
                    Cur(p, "rs", "ID");
                }
                foreach (int size in new[] { 0, -1, 100 })
                {
                    PutGet(p, "rs", "PageSize", A.I4(size));
                    p.Get("rs", "PageCount");
                }
                p.Call("rs", "Close").Release("rs");
                foreach ((int loc, int max) in new[] { (Ado.UseServer, 4), (Ado.UseServer, 0), (Ado.UseServer, -1), (Ado.UseClient, 4), (Ado.UseClient, 0), (Ado.UseClient, -1) })
                {
                    string r = $"mx{loc}_{max + 1}";
                    p.Create("ADODB.Recordset", r).Put(r, "CursorLocation", A.I4(loc)).Put(r, "MaxRecords", A.I4(max)).Get(r, "MaxRecords")
                        .Call(r, "Open", A.Str("SELECT ID, S FROM T ORDER BY ID"), A.Obj("cn"), A.I4(loc == Ado.UseServer ? Ado.OpenKeyset : Ado.OpenStatic), A.I4(Ado.LockReadOnly), A.I4(Ado.CmdText))
                        .Get(r, "State").Get(r, "RecordCount");
                    AdoReadAll(p, r, 16, 2);
                    p.Get(r, "MaxRecords").Call(r, "Close").Release(r);
                }
                AdoErrors(p, "cn");
                p.FinishLite("cn");
            });

        yield return Q("rs-cache-same-connection", Refs("ADO-CURSOR", "ADO-RESYNC"),
            "ADO-CURSOR same-connection visibility on a read/write Connection: a server keyset, a server static and a client static read-only Recordset of SELECT ID, S FROM T ORDER BY ID are opened and read (RecordCount); then, through the same Connection, " +
            "UPDATE S of ID 1, INSERT a row and DELETE ID 2 (Errors); each Recordset is read again from MoveFirst without refresh (rows, RecordCount), then Resync(adAffectAll, adResyncAllValues) and read, then Requery and read. " +
            "Asks which cursors show the connection's own update, insert and delete when, and which refresh calls each accepts.",
            p =>
            {
                p.EngineLite();
                AdoOpen(p, "cn", readOnly: false);
                (string Bind, int Loc, int Type)[] sets = [("c1", Ado.UseServer, Ado.OpenKeyset), ("c2", Ado.UseServer, Ado.OpenStatic), ("c3", Ado.UseClient, Ado.OpenStatic)];
                foreach ((string bind, int loc, int type) in sets)
                {
                    AdoRs(p, bind, "SELECT ID, S FROM T ORDER BY ID", "cn", loc, type, Ado.LockReadOnly, Ado.CmdText);
                    p.Get(bind, "RecordCount");
                    ReadCols(p, bind, 16, "ID", "S");
                }
                ExecNo(p, "cn", "UPDATE T SET S = 'chg' WHERE ID = 1");
                ExecNo(p, "cn", "INSERT INTO T (Code, S) VALUES ('n9', 'new')");
                ExecNo(p, "cn", "DELETE FROM T WHERE ID = 2");
                ErrClear(p, "cn");
                foreach ((string bind, int _, int _) in sets)
                {
                    p.Call(bind, "MoveFirst");
                    ReadCols(p, bind, 16, "ID", "S");
                    p.Get(bind, "RecordCount");
                }
                foreach ((string bind, int _, int _) in sets)
                {
                    p.Call(bind, "Resync", A.I4(Ado.AffectAll), A.I4(Ado.ResyncAll)).Call(bind, "MoveFirst");
                    ReadCols(p, bind, 16, "ID", "S");
                    p.Get(bind, "RecordCount");
                    ErrClear(p, "cn");
                }
                foreach ((string bind, int _, int _) in sets)
                {
                    p.Call(bind, "Requery");
                    ReadCols(p, bind, 16, "ID", "S");
                    p.Get(bind, "RecordCount").Call(bind, "Close").Release(bind);
                    ErrClear(p, "cn");
                }
                p.FinishLite("cn");
            });

        yield return Q("book-raw-forms", Refs("ADO-BOOK", "ADO-CURSOR"),
            "ADO-BOOK raw forms on SELECT ID, S FROM T ORDER BY ID for a server forward-only, a server keyset, a server static and a client static read-only Recordset: Supports adBookmark; the raw Bookmark value (tag, SAFEARRAY or scalar) at row 1, after MoveNext, at MoveLast, at EOF and at BOF; " +
            "then, from MoveFirst, Bookmark assigned a Long 2, a Long 3, the String \"x\", a four-byte array, Empty, Null and a Double 2.0 (current ID, AbsolutePosition, BOF and EOF after each, and the Errors). " +
            "Asks the Bookmark representation per cursor and which constant forms a Bookmark assignment accepts.",
            p =>
            {
                p.EngineLite();
                AdoOpen(p, "cn");
                (string Bind, int Loc, int Type)[] sets =
                [
                    ("b1", Ado.UseServer, Ado.OpenForwardOnly), ("b2", Ado.UseServer, Ado.OpenKeyset), ("b3", Ado.UseServer, Ado.OpenStatic), ("b4", Ado.UseClient, Ado.OpenStatic),
                ];
                foreach ((string bind, int loc, int type) in sets)
                {
                    AdoRs(p, bind, "SELECT ID, S FROM T ORDER BY ID", "cn", loc, type, Ado.LockReadOnly, Ado.CmdText);
                    p.Call(bind, "Supports", A.I4(Ado.SupBookmark)).Get(bind, "Bookmark").Call(bind, "MoveNext").Get(bind, "Bookmark").Call(bind, "MoveLast").Get(bind, "Bookmark")
                        .Call(bind, "MoveNext").Get(bind, "EOF").Get(bind, "Bookmark").Call(bind, "MoveFirst").Call(bind, "MovePrevious").Get(bind, "BOF").Get(bind, "Bookmark");
                    foreach (Arg value in new[] { A.I4(2), A.I4(3), A.Str("x"), A.Bytes(1, 0, 0, 0), A.Empty, A.Null, A.R8(2.0) })
                    {
                        p.Call(bind, "MoveFirst").Put(bind, "Bookmark", value).Get(bind, "BOF").Get(bind, "EOF").Get(bind, "AbsolutePosition");
                        Cur(p, bind, "ID");
                        ErrClear(p, "cn");
                    }
                    p.Call(bind, "Close").Release(bind);
                }
                p.FinishLite("cn");
            });
    }

    // ADO-EDIT -------------------------------------------------------------------------------------------

    private static IEnumerable<Probe> EditProbes()
    {
        yield return Q("edit-update-args", Refs("ADO-EDIT"),
            "ADO-EDIT Update and AddNew with Fields/Values arguments sent as scalars, on a server keyset optimistic Recordset of SELECT ID, Code, L, S FROM T ORDER BY ID (read/write copy): " +
            "Update(\"S\", \"u1\") on row 1 with no prior edit; MoveNext and Update(\"S\", \"u2\"); AddNew(\"Code\", \"n9\"); AddNew with Code set through the Field then Update(\"S\", \"u3\"); Update(\"NoSuch\", \"x\"); Update(Missing, \"x\"); " +
            "Update(\"L\", \"abc\"); Update(\"S\", Missing); Update(Empty, Empty); AddNew(\"Code,S\", \"n10\"). After each read EditMode, the current ID and S, the Errors (cleared); finally the stored rows through Execute. " +
            "Asks which argument forms are accepted, whether Update with arguments posts or only stages, and the resulting stored rows.",
            p =>
            {
                p.EngineLite();
                AdoOpen(p, "cn", readOnly: false);
                AdoRs(p, "rs", SelS, "cn", Ado.UseServer, Ado.OpenKeyset, Ado.LockOptimistic, Ado.CmdText);
                void Act(string member, params Arg[] args)
                {
                    p.Call("rs", member, args).Get("rs", "EditMode");
                    Cur(p, "rs", "ID");
                    Cur(p, "rs", "S");
                    ErrClear(p, "cn");
                }
                Act("Update", A.Str("S"), A.Str("u1"));
                p.Call("rs", "MoveNext");
                Act("Update", A.Str("S"), A.Str("u2"));
                Act("AddNew", A.Str("Code"), A.Str("n9"));
                p.GetBind("fl", "rs", "Fields").Item("fc", "fl", A.Str("Code"));
                p.Call("rs", "AddNew").Put("fc", "Value", A.Str("n11"));
                Act("Update", A.Str("S"), A.Str("u3"));
                Act("Update", A.Str("NoSuch"), A.Str("x"));
                Act("Update", A.Missing, A.Str("x"));
                Act("Update", A.Str("L"), A.Str("abc"));
                Act("Update", A.Str("S"), A.Missing);
                Act("Update", A.Empty, A.Empty);
                Act("AddNew", A.Str("Code,S"), A.Str("n10"));
                p.Release("fc").Release("fl").Call("rs", "Close").Release("rs");
                ExecRead(p, "cn", "st", StoredT);
                p.FinishLite("cn");
            });

        yield return Q("edit-pending-ops", Refs("ADO-EDIT", "ADO-VIEW"),
            "ADO-EDIT with an edit pending, on eleven fresh client static optimistic (immediate mode) Recordsets of SELECT ID, S FROM T ORDER BY ID (read/write copy): S of row 1 is set to \"p-<operation>\" with no Update, then the operation runs " +
            "(Find \"ID = 3\", Filter \"ID = 3\", Sort \"ID DESC\", Clone, Requery, Resync(adAffectAll, adResyncAllValues), MoveNext, MoveFirst, AddNew, Delete, Close); " +
            "after it read EditMode, Status, the current ID and S, the Errors (cleared); then the first three stored rows through Execute. Asks which operations post, cancel, refuse or keep the pending edit and what reaches the file.",
            p =>
            {
                p.EngineLite();
                AdoOpen(p, "cn", readOnly: false);
                (string Name, Action<P> Act)[] ops =
                [
                    ("find", q => q.Call("rs", "Find", A.Str("ID = 3"))),
                    ("filter", q => q.Put("rs", "Filter", A.Str("ID = 3"))),
                    ("sort", q => q.Put("rs", "Sort", A.Str("ID DESC"))),
                    ("clone", q => q.CallBind("rc", "rs", "Clone")),
                    ("requery", q => q.Call("rs", "Requery")),
                    ("resync", q => q.Call("rs", "Resync", A.I4(Ado.AffectAll), A.I4(Ado.ResyncAll))),
                    ("movenext", q => q.Call("rs", "MoveNext")),
                    ("movefirst", q => q.Call("rs", "MoveFirst")),
                    ("addnew", q => q.Call("rs", "AddNew")),
                    ("delete", q => q.Call("rs", "Delete")),
                    ("close", q => q.Call("rs", "Close")),
                ];
                foreach ((string name, Action<P> act) in ops)
                {
                    AdoRs(p, "rs", "SELECT ID, S FROM T ORDER BY ID", "cn", Ado.UseClient, Ado.OpenStatic, Ado.LockOptimistic, Ado.CmdText);
                    p.GetBind("fl", "rs", "Fields").Item("fs", "fl", A.Str("S")).Put("fs", "Value", A.Str("p-" + name)).Get("rs", "EditMode");
                    act(p);
                    p.Get("rs", "EditMode").Get("rs", "Status");
                    Cur(p, "rs", "ID");
                    Cur(p, "rs", "S");
                    ErrClear(p, "cn");
                    p.Release("rc").Release("fs").Release("fl").Call("rs", "Close").Release("rs");
                    ExecRead(p, "cn", "st_" + name, "SELECT ID, S FROM T WHERE ID <= 3 ORDER BY ID");
                }
                p.FinishLite("cn");
            });
    }

    // ADO-BATCH, ADO-BATCH-SCOPE, ADO-VALUES -------------------------------------------------------------

    private static IEnumerable<Probe> BatchProbes()
    {
        yield return Q("batch-status-staging", Refs("ADO-BATCH", "ADO-EDIT", "ADO-VALUES"),
            "ADO-BATCH staging on a client static batch-optimistic Recordset of SELECT ID, Code, L, S FROM T ORDER BY ID: read Status at row 1; set S and read Status and EditMode before and after Update; AddNew, set Code, read Status and EditMode, Update, read Status; " +
            "Delete of row 3 (Status, EditMode, reading S, MoveNext, Status, RecordCount); then every row's Status and ID, again under Filter = adFilterPendingRecords (RecordCount), and after CancelBatch(adAffectAll) (rows, RecordCount). " +
            "Asks the Status values of staged, new and deleted rows and what a deleted row exposes.",
            p =>
            {
                p.EngineLite();
                AdoOpen(p, "cn", readOnly: false);
                AdoRs(p, "rs", SelS, "cn", Ado.UseClient, Ado.OpenStatic, Ado.LockBatchOptimistic, Ado.CmdText);
                p.GetBind("fl", "rs", "Fields").Item("fs", "fl", A.Str("S")).Item("fc", "fl", A.Str("Code")).Get("rs", "Status")
                    .Put("fs", "Value", A.Str("st1")).Get("rs", "Status").Get("rs", "EditMode").Call("rs", "Update").Get("rs", "Status").Get("rs", "EditMode")
                    .Call("rs", "MoveNext").Call("rs", "AddNew").Get("rs", "Status").Get("rs", "EditMode").Put("fc", "Value", A.Str("n9")).Call("rs", "Update").Get("rs", "Status")
                    .Call("rs", "MoveFirst").Call("rs", "Move", A.I4(2)).Call("rs", "Delete").Get("rs", "Status").Get("rs", "EditMode").Get("fs", "Value").Call("rs", "MoveNext").Get("rs", "Status").Get("rs", "RecordCount")
                    .Call("rs", "MoveFirst");
                StatusRows(p, "rs");
                p.Put("rs", "Filter", A.I4(Ado.FilterPending)).Get("rs", "RecordCount");
                StatusRows(p, "rs");
                p.Put("rs", "Filter", A.I4(Ado.FilterNone)).Call("rs", "CancelBatch", A.I4(Ado.AffectAll)).Call("rs", "MoveFirst");
                StatusRows(p, "rs");
                p.Get("rs", "RecordCount");
                AdoErrors(p, "cn");
                p.Release("fs").Release("fc").Release("fl").Call("rs", "Close").Release("rs").FinishLite("cn");
            });

        yield return Q("batch-mixed-generated", Refs("ADO-BATCH", "ADO-VALUES", "R-FMT-1"),
            "ADO-BATCH generated key: on a client static batch-optimistic Recordset of SELECT ID, Code, L, S FROM T ORDER BY ID (read/write copy) stage AddNew Code \"n9\", S of row 1, Delete of row 2 and AddNew Code \"n10\"; read each row's Status and ID (the new rows' ID values); " +
            "read the Update Resync property; UpdateBatch(adAffectAll) and the Errors; read each row's Status and ID again, then Requery and read ID and Code; then the stored rows through Execute. " +
            "Asks when and whether the generated AutoNumber values reach the cached rows, and the order the file received the changes.",
            p =>
            {
                p.EngineLite();
                AdoOpen(p, "cn", readOnly: false);
                AdoRs(p, "rs", SelS, "cn", Ado.UseClient, Ado.OpenStatic, Ado.LockBatchOptimistic, Ado.CmdText);
                p.GetBind("fl", "rs", "Fields").Item("fc", "fl", A.Str("Code")).Item("fs", "fl", A.Str("S"))
                    .Call("rs", "AddNew").Put("fc", "Value", A.Str("n9")).Call("rs", "Update")
                    .Call("rs", "MoveFirst").Put("fs", "Value", A.Str("mix1")).Call("rs", "MoveNext").Call("rs", "Delete")
                    .Call("rs", "AddNew").Put("fc", "Value", A.Str("n10")).Call("rs", "Update").Call("rs", "MoveFirst");
                StatusRows(p, "rs");
                PropPut(p, "rs", "Update Resync", A.I4(0), "ur");
                p.Call("rs", "UpdateBatch", A.I4(Ado.AffectAll));
                AdoErrors(p, "cn");
                p.Call("rs", "MoveFirst");
                StatusRows(p, "rs");
                p.Call("rs", "Requery");
                ReadCols(p, "rs", 16, "ID", "Code");
                p.Release("fc").Release("fs").Release("fl").Call("rs", "Close").Release("rs");
                ExecRead(p, "cn", "st", StoredT);
                p.FinishLite("cn");
            });

        yield return Q("batch-cancel", Refs("ADO-BATCH", "ADO-BATCH-SCOPE", "ADO-EDIT"),
            "ADO-BATCH cancellation on a client static batch-optimistic Recordset of SELECT ID, Code, V, V2, S FROM W ORDER BY ID (read/write copy): stage S of rows 1 and 2, Delete of row 3 and an inserted row (ID 50, Code \"w50\"); read each row's Status and S; " +
            "CancelBatch(adAffectCurrent) at row 1 (rows again); CancelUpdate with no edit pending (EditMode, Errors); Filter \"ID = 2\" with CancelBatch(adAffectGroup) then Filter none (rows); CancelBatch(adAffectAll) (rows, RecordCount); CancelBatch again with nothing pending; " +
            "then, on a server keyset optimistic Recordset, set S, CancelBatch and CancelUpdate (EditMode, S). Asks what each scope cancels, what an empty cancel reports and how a server cursor answers CancelBatch.",
            p =>
            {
                p.EngineLite();
                AdoOpen(p, "cn", readOnly: false);
                AdoRs(p, "rs", SelW, "cn", Ado.UseClient, Ado.OpenStatic, Ado.LockBatchOptimistic, Ado.CmdText);
                p.GetBind("fl", "rs", "Fields").Item("fs", "fl", A.Str("S")).Item("fid", "fl", A.Str("ID")).Item("fc", "fl", A.Str("Code"))
                    .Put("fs", "Value", A.Str("x1")).Call("rs", "MoveNext").Put("fs", "Value", A.Str("x2")).Call("rs", "MoveNext").Call("rs", "Delete")
                    .Call("rs", "AddNew").Put("fid", "Value", A.I4(50)).Put("fc", "Value", A.Str("w50")).Call("rs", "Update").Call("rs", "MoveFirst");
                BatchValues(p, "rs", "S");
                p.Call("rs", "MoveFirst").Call("rs", "CancelBatch", A.I4(Ado.AffectCurrent)).Call("rs", "MoveFirst");
                BatchValues(p, "rs", "S");
                p.Call("rs", "CancelUpdate").Get("rs", "EditMode");
                ErrClear(p, "cn");
                p.Put("rs", "Filter", A.Str("ID = 2")).Call("rs", "CancelBatch", A.I4(Ado.AffectGroup)).Put("rs", "Filter", A.I4(Ado.FilterNone)).Call("rs", "MoveFirst");
                BatchValues(p, "rs", "S");
                p.Call("rs", "CancelBatch", A.I4(Ado.AffectAll)).Call("rs", "MoveFirst");
                BatchValues(p, "rs", "S");
                p.Get("rs", "RecordCount").Call("rs", "CancelBatch", A.I4(Ado.AffectAll));
                ErrClear(p, "cn");
                p.Release("fs").Release("fid").Release("fc").Release("fl").Call("rs", "Close").Release("rs");
                AdoRs(p, "rk", SelW, "cn", Ado.UseServer, Ado.OpenKeyset, Ado.LockOptimistic, Ado.CmdText);
                p.GetBind("kf", "rk", "Fields").Item("ks", "kf", A.Str("S")).Put("ks", "Value", A.Str("k1")).Call("rk", "CancelBatch").Get("rk", "EditMode");
                ErrClear(p, "cn");
                p.Call("rk", "CancelUpdate").Get("rk", "EditMode").Get("ks", "Value").Release("ks").Release("kf").Call("rk", "Close").Release("rk");
                p.FinishLite("cn");
            });

        yield return Q("batch-scope-group", Refs("ADO-BATCH-SCOPE", "ADO-VIEW"),
            "ADO-BATCH-SCOPE on a client static batch-optimistic Recordset of SELECT ID, Code, V, V2, S FROM W ORDER BY ID (read/write copy): S of rows 1 and 2 staged and S of row 3 set with the edit still pending; Filter = adFilterPendingRecords (RecordCount, rows); " +
            "Filter \"ID <= 2\" (RecordCount) then UpdateBatch(adAffectGroup) (Errors, stored rows through Execute); Filter none and every row's Status and S; Filter = adFilterAffectedRecords (RecordCount); Filter none, MoveFirst, Move 2, UpdateBatch(adAffectCurrent) (Errors, stored rows); " +
            "Filter \"ID > 99\" then UpdateBatch(adAffectGroup) (Errors). Asks which rows each scope submits, whether an unposted current edit is included and how an empty group answers.",
            p =>
            {
                p.EngineLite();
                AdoOpen(p, "cn", readOnly: false);
                AdoRs(p, "rs", SelW, "cn", Ado.UseClient, Ado.OpenStatic, Ado.LockBatchOptimistic, Ado.CmdText);
                p.GetBind("fl", "rs", "Fields").Item("fs", "fl", A.Str("S")).Put("fs", "Value", A.Str("g1")).Call("rs", "MoveNext").Put("fs", "Value", A.Str("g2"))
                    .Call("rs", "MoveNext").Put("fs", "Value", A.Str("g3"))
                    .Put("rs", "Filter", A.I4(Ado.FilterPending)).Get("rs", "RecordCount");
                BatchValues(p, "rs", "S");
                p.Put("rs", "Filter", A.Str("ID <= 2")).Get("rs", "RecordCount").Call("rs", "UpdateBatch", A.I4(Ado.AffectGroup));
                ErrClear(p, "cn");
                ExecRead(p, "cn", "s1", StoredW);
                p.Put("rs", "Filter", A.I4(Ado.FilterNone)).Call("rs", "MoveFirst");
                BatchValues(p, "rs", "S");
                p.Put("rs", "Filter", A.I4(Ado.FilterAffected)).Get("rs", "RecordCount").Put("rs", "Filter", A.I4(Ado.FilterNone)).Call("rs", "MoveFirst").Call("rs", "Move", A.I4(2))
                    .Call("rs", "UpdateBatch", A.I4(Ado.AffectCurrent));
                ErrClear(p, "cn");
                ExecRead(p, "cn", "s2", StoredW);
                p.Put("rs", "Filter", A.Str("ID > 99")).Get("rs", "RecordCount").Call("rs", "UpdateBatch", A.I4(Ado.AffectGroup));
                ErrClear(p, "cn");
                p.Release("fs").Release("fl").Call("rs", "Close").Release("rs").FinishLite("cn");
            });

        yield return Conflict("batch-conflict-first", "UPDATE W SET S = 'ext' WHERE ID = 1", "another connection of the same process updates S of the first staged row");
        yield return Conflict("batch-conflict-middle", "UPDATE W SET S = 'ext' WHERE ID = 2", "another connection of the same process updates S of the middle staged row");
        yield return Conflict("batch-conflict-delete", "DELETE FROM W WHERE ID = 2", "another connection of the same process deletes the middle staged row");

        yield return Q("batch-fk-failure", Refs("ADO-BATCH", "R-SQL-3"),
            "ADO-BATCH constraint failures across two client batch-optimistic Recordsets (read/write copy): one on Ch (ID, PID, S) with an inserted orphan (ID 9, PID 99) and S of row 1 changed, one on P (ID, Nm) with Delete of the referenced row 1 and Nm of row 3 changed; " +
            "UpdateBatch(adAffectAll) on the Ch Recordset (Errors, each row's Status, stored Ch rows), then on the P Recordset (Errors, Status, stored P rows), then Filter = adFilterConflictingRecords RecordCount of each. " +
            "Asks which staged changes reach the file, the failing row's Status and the error set.",
            p =>
            {
                p.EngineLite();
                AdoOpen(p, "cn", readOnly: false);
                AdoRs(p, "rp", "SELECT ID, Nm FROM P ORDER BY ID", "cn", Ado.UseClient, Ado.OpenStatic, Ado.LockBatchOptimistic, Ado.CmdText);
                AdoRs(p, "rc", "SELECT ID, PID, S FROM Ch ORDER BY ID", "cn", Ado.UseClient, Ado.OpenStatic, Ado.LockBatchOptimistic, Ado.CmdText);
                p.GetBind("pf", "rp", "Fields").Item("pn", "pf", A.Str("Nm")).Call("rp", "Delete").Call("rp", "MoveLast").Put("pn", "Value", A.Str("q3"))
                    .GetBind("cf", "rc", "Fields").Item("cid", "cf", A.Str("ID")).Item("cpid", "cf", A.Str("PID")).Item("cs", "cf", A.Str("S"))
                    .Call("rc", "AddNew").Put("cid", "Value", A.I4(9)).Put("cpid", "Value", A.I4(99)).Put("cs", "Value", A.Str("orph")).Call("rc", "Update")
                    .Call("rc", "MoveFirst").Put("cs", "Value", A.Str("e1"))
                    .Call("rc", "UpdateBatch", A.I4(Ado.AffectAll));
                ErrClear(p, "cn");
                p.Call("rc", "MoveFirst");
                BatchValues(p, "rc", "S");
                ExecRead(p, "cn", "sc", "SELECT ID, PID, S FROM Ch ORDER BY ID");
                p.Call("rp", "UpdateBatch", A.I4(Ado.AffectAll));
                ErrClear(p, "cn");
                p.Call("rp", "MoveFirst");
                BatchValues(p, "rp", "Nm");
                ExecRead(p, "cn", "sp", "SELECT ID, Nm FROM P ORDER BY ID");
                p.Put("rc", "Filter", A.I4(Ado.FilterConflicting)).Get("rc", "RecordCount").Put("rp", "Filter", A.I4(Ado.FilterConflicting)).Get("rp", "RecordCount");
                p.Release("pn").Release("pf").Release("cid").Release("cpid").Release("cs").Release("cf").Call("rp", "Close").Call("rc", "Close").Release("rp").Release("rc").FinishLite("cn");
            });

        yield return Q("batch-resubmit", Refs("ADO-BATCH"),
            "ADO-BATCH resubmission on a client static batch-optimistic Recordset of SELECT ID, Code, V, V2, S FROM W ORDER BY ID (read/write copy): stage an inserted row (ID 20, Code \"w20\", S \"ins\"), Code of row 2 set to the duplicate \"w1\" and S of row 3; " +
            "UpdateBatch(adAffectAll) (Errors, each row's Status and Code, stored rows for IDs 1-3 and 20); then Find ID 2, set its Code to \"w2x\", UpdateBatch(adAffectAll) again (Errors, Status, stored rows); and a third UpdateBatch with nothing pending. " +
            "Asks which staged rows the first submission applied, whether accepted rows are sent again and what the resubmission reports.",
            p =>
            {
                p.EngineLite();
                AdoOpen(p, "cn", readOnly: false);
                AdoRs(p, "rs", SelW, "cn", Ado.UseClient, Ado.OpenStatic, Ado.LockBatchOptimistic, Ado.CmdText);
                p.GetBind("fl", "rs", "Fields").Item("fid", "fl", A.Str("ID")).Item("fc", "fl", A.Str("Code")).Item("fs", "fl", A.Str("S"))
                    .Call("rs", "AddNew").Put("fid", "Value", A.I4(20)).Put("fc", "Value", A.Str("w20")).Put("fs", "Value", A.Str("ins")).Call("rs", "Update")
                    .Call("rs", "MoveFirst").Call("rs", "MoveNext").Put("fc", "Value", A.Str("w1")).Call("rs", "MoveNext").Put("fs", "Value", A.Str("okS"))
                    .Call("rs", "UpdateBatch", A.I4(Ado.AffectAll));
                ErrClear(p, "cn");
                p.Call("rs", "MoveFirst");
                BatchValues(p, "rs", "Code");
                ExecRead(p, "cn", "s1", "SELECT ID, Code, S FROM W WHERE ID <= 3 OR ID = 20 ORDER BY ID");
                p.Call("rs", "MoveFirst").Call("rs", "Find", A.Str("ID = 2")).Put("fc", "Value", A.Str("w2x")).Call("rs", "UpdateBatch", A.I4(Ado.AffectAll));
                ErrClear(p, "cn");
                p.Call("rs", "MoveFirst");
                BatchValues(p, "rs", "Code");
                ExecRead(p, "cn", "s2", "SELECT ID, Code, S FROM W WHERE ID <= 3 OR ID = 20 ORDER BY ID");
                p.Call("rs", "UpdateBatch", A.I4(Ado.AffectAll));
                ErrClear(p, "cn");
                p.Release("fid").Release("fc").Release("fs").Release("fl").Call("rs", "Close").Release("rs").FinishLite("cn");
            });

        yield return Q("batch-update-criteria", Refs("ADO-BATCH-SCOPE", "ADO-VALUES"),
            "ADO-BATCH-SCOPE Update Criteria: for each value 0, 1, 2 and 3 of the Recordset property \"Update Criteria\" (set after Open on a client static batch-optimistic Recordset of SELECT ID, Code, V, V2, S FROM W ORDER BY ID, read back), " +
            "a second Connection of the process changes V2 of row k (k = value + 1), then S of row k is set in the Recordset and UpdateBatch(adAffectAll) is called; read the Errors (cleared), the row's Status and S, then Close. Finally the stored rows through Execute. " +
            "Asks whether a change to another column conflicts under each criteria value.",
            p =>
            {
                p.EngineLite();
                AdoOpen(p, "cn", readOnly: false);
                AdoOpen(p, "cn2", readOnly: false);
                for (int v = 0; v < 4; v++)
                {
                    string r = "uc" + v;
                    AdoRs(p, r, SelW, "cn", Ado.UseClient, Ado.OpenStatic, Ado.LockBatchOptimistic, Ado.CmdText);
                    PropPut(p, r, "Update Criteria", A.I4(v), "pc" + v);
                    ExecNo(p, "cn2", $"UPDATE W SET V2 = V2 + 100 WHERE ID = {v + 1}");
                    p.Call(r, "MoveFirst");
                    if (v > 0)
                    {
                        p.Call(r, "Move", A.I4(v));
                    }
                    p.GetBind(r + "f", r, "Fields").Item(r + "s", r + "f", A.Str("S")).Put(r + "s", "Value", A.Str("uc" + v)).Call(r, "UpdateBatch", A.I4(Ado.AffectAll));
                    ErrClear(p, "cn");
                    p.Get(r, "Status").Get(r + "s", "Value").Release(r + "s").Release(r + "f").Call(r, "Close").Release(r);
                }
                ExecRead(p, "cn", "st", "SELECT ID, V2, S FROM W WHERE ID <= 4 ORDER BY ID");
                p.FinishLite("cn", "cn2");
            });

        yield return Q("batch-update-resync", Refs("ADO-BATCH-SCOPE", "ADO-VALUES", "R-FMT-1"),
            "ADO-BATCH-SCOPE Update Resync: for each value 0, 1, 2, 4, 8 and 15 of the Recordset property \"Update Resync\" (set after Open, read back) on a client static batch-optimistic Recordset of SELECT ID, Code, L, S FROM T ORDER BY ID (read/write copy), " +
            "AddNew with Code \"r<value>\" and S \"ins\", Update, read the new row's ID, UpdateBatch(adAffectAll) and the Errors, then read the new row's ID, Status and S, and Close; finally the stored rows through Execute. " +
            "Asks which values make the generated key and server-side values visible in the cached row.",
            p =>
            {
                p.EngineLite();
                AdoOpen(p, "cn", readOnly: false);
                foreach (int v in new[] { 0, 1, 2, 4, 8, 15 })
                {
                    string r = "ur" + v;
                    AdoRs(p, r, SelS, "cn", Ado.UseClient, Ado.OpenStatic, Ado.LockBatchOptimistic, Ado.CmdText);
                    PropPut(p, r, "Update Resync", A.I4(v), "pr" + v);
                    p.GetBind(r + "f", r, "Fields").Item(r + "c", r + "f", A.Str("Code")).Item(r + "s", r + "f", A.Str("S")).Item(r + "i", r + "f", A.Str("ID"))
                        .Call(r, "AddNew").Put(r + "c", "Value", A.Str("r" + v)).Put(r + "s", "Value", A.Str("ins")).Call(r, "Update").Get(r + "i", "Value")
                        .Call(r, "UpdateBatch", A.I4(Ado.AffectAll));
                    ErrClear(p, "cn");
                    p.Get(r + "i", "Value").Get(r, "Status").Get(r + "s", "Value").Release(r + "i").Release(r + "s").Release(r + "c").Release(r + "f").Call(r, "Close").Release(r);
                }
                ExecRead(p, "cn", "st", StoredT);
                p.FinishLite("cn");
            });

        yield return Q("values-keyset-conflict", Refs("ADO-VALUES", "ADO-EDIT"),
            "ADO-VALUES on a server keyset optimistic Recordset of SELECT ID, V, S FROM W ORDER BY ID (read/write copy), field V of row 1: set 100 and Update; set 120 (proposal) and read Value, OriginalValue, UnderlyingValue, Status and EditMode; " +
            "a second Connection of the process then sets V = 110; read the three values again; Update (Errors); the three values, Status and EditMode; CancelUpdate and the values; Resync(adAffectCurrent, adResyncUnderlyingValues) and the values; " +
            "set 130 and Update (Errors, values); then the stored row. Asks what each state reports for Value, OriginalValue and UnderlyingValue and whether the stale Update is refused.",
            p =>
            {
                p.EngineLite();
                AdoOpen(p, "cn", readOnly: false);
                AdoOpen(p, "cn2", readOnly: false);
                AdoRs(p, "rs", "SELECT ID, V, S FROM W ORDER BY ID", "cn", Ado.UseServer, Ado.OpenKeyset, Ado.LockOptimistic, Ado.CmdText);
                p.GetBind("fl", "rs", "Fields").Item("fv", "fl", A.Str("V"));
                ValueTriple(p, "fv");
                p.Put("fv", "Value", A.I4(100)).Call("rs", "Update");
                ValueTriple(p, "fv");
                p.Put("fv", "Value", A.I4(120));
                ValueTriple(p, "fv");
                p.Get("rs", "Status").Get("rs", "EditMode");
                ExecNo(p, "cn2", "UPDATE W SET V = 110 WHERE ID = 1");
                ValueTriple(p, "fv");
                p.Call("rs", "Update");
                ErrClear(p, "cn");
                ValueTriple(p, "fv");
                p.Get("rs", "Status").Get("rs", "EditMode").Call("rs", "CancelUpdate");
                ValueTriple(p, "fv");
                p.Call("rs", "Resync", A.I4(Ado.AffectCurrent), A.I4(Ado.ResyncUnderlying));
                ErrClear(p, "cn");
                ValueTriple(p, "fv");
                p.Put("fv", "Value", A.I4(130)).Call("rs", "Update");
                ErrClear(p, "cn");
                ValueTriple(p, "fv");
                p.Get("rs", "Status").Release("fv").Release("fl").Call("rs", "Close").Release("rs");
                ExecRead(p, "cn", "st", "SELECT ID, V FROM W WHERE ID = 1");
                p.FinishLite("cn", "cn2");
            });

        yield return Q("values-client-batch", Refs("ADO-VALUES", "ADO-BATCH"),
            "ADO-VALUES on a client static batch-optimistic Recordset of SELECT ID, V, S FROM W ORDER BY ID (read/write copy), field V of row 1: set 100 and Update (cache) and UpdateBatch; set 120, Update and read Value, OriginalValue, UnderlyingValue and Status; " +
            "a second Connection of the process sets V = 110; the three values; UpdateBatch (Errors); the values and Status; CancelBatch and the values; Resync(adAffectAll, adResyncUnderlyingValues) and the values; set 130, Update, UpdateBatch (Errors, values); " +
            "then the stored row. Asks how the client cache separates original, staged and source values and whether the stale batch is refused.",
            p =>
            {
                p.EngineLite();
                AdoOpen(p, "cn", readOnly: false);
                AdoOpen(p, "cn2", readOnly: false);
                AdoRs(p, "rs", "SELECT ID, V, S FROM W ORDER BY ID", "cn", Ado.UseClient, Ado.OpenStatic, Ado.LockBatchOptimistic, Ado.CmdText);
                p.GetBind("fl", "rs", "Fields").Item("fv", "fl", A.Str("V"));
                ValueTriple(p, "fv");
                p.Put("fv", "Value", A.I4(100)).Call("rs", "Update").Call("rs", "UpdateBatch", A.I4(Ado.AffectAll));
                ErrClear(p, "cn");
                ValueTriple(p, "fv");
                p.Put("fv", "Value", A.I4(120)).Call("rs", "Update");
                ValueTriple(p, "fv");
                p.Get("rs", "Status");
                ExecNo(p, "cn2", "UPDATE W SET V = 110 WHERE ID = 1");
                ValueTriple(p, "fv");
                p.Call("rs", "UpdateBatch", A.I4(Ado.AffectAll));
                ErrClear(p, "cn");
                ValueTriple(p, "fv");
                p.Get("rs", "Status").Call("rs", "CancelBatch", A.I4(Ado.AffectAll));
                ValueTriple(p, "fv");
                p.Call("rs", "Resync", A.I4(Ado.AffectAll), A.I4(Ado.ResyncUnderlying));
                ErrClear(p, "cn");
                ValueTriple(p, "fv");
                p.Put("fv", "Value", A.I4(130)).Call("rs", "Update").Call("rs", "UpdateBatch", A.I4(Ado.AffectAll));
                ErrClear(p, "cn");
                ValueTriple(p, "fv");
                p.Get("rs", "Status").Release("fv").Release("fl").Call("rs", "Close").Release("rs");
                ExecRead(p, "cn", "st", "SELECT ID, V FROM W WHERE ID = 1");
                p.FinishLite("cn", "cn2");
            });

        yield return Q("disconnect-close-connection", Refs("ADO-DISCONNECT", "ADO-BATCH", "ADO-SESSION"),
            "ADO-DISCONNECT on a read/write copy: a client static batch-optimistic and a server keyset optimistic Recordset of SELECT ID, Code, V, V2, S FROM W ORDER BY ID are opened on one Connection and the first row's ID of each is read; the Connection is Closed; " +
            "each Recordset reads State, ActiveConnection, RecordCount and every row (ID, S); the client one sets S of row 1 and calls Update and UpdateBatch (Errors of the closed Connection), the server one sets S and calls Update (Errors); " +
            "a second Connection is opened and set as the client Recordset's ActiveConnection by reference (State) and UpdateBatch is called again (Errors); the stored rows are read through the second Connection. " +
            "Asks which reads, edits and submissions survive the Connection's Close and whether a reconnect to the same file submits the staged change.",
            p =>
            {
                p.EngineLite();
                AdoOpen(p, "cn", readOnly: false);
                AdoRs(p, "rc", SelW, "cn", Ado.UseClient, Ado.OpenStatic, Ado.LockBatchOptimistic, Ado.CmdText);
                AdoRs(p, "rk", SelW, "cn", Ado.UseServer, Ado.OpenKeyset, Ado.LockOptimistic, Ado.CmdText);
                Cur(p, "rc", "ID");
                Cur(p, "rk", "ID");
                p.Call("cn", "Close");
                foreach (string r in new[] { "rc", "rk" })
                {
                    p.Get(r, "State").Get(r, "ActiveConnection").Get(r, "RecordCount").Call(r, "MoveFirst");
                    ReadCols(p, r, 16, "ID", "S");
                    ErrClear(p, "cn");
                }
                p.Call("rc", "MoveFirst").GetBind("cf", "rc", "Fields").Item("cs", "cf", A.Str("S")).Put("cs", "Value", A.Str("disc")).Call("rc", "Update").Call("rc", "UpdateBatch", A.I4(Ado.AffectAll));
                ErrClear(p, "cn");
                p.Call("rk", "MoveFirst").GetBind("kf", "rk", "Fields").Item("ks", "kf", A.Str("S")).Put("ks", "Value", A.Str("disc2")).Call("rk", "Update");
                ErrClear(p, "cn");
                AdoOpen(p, "cn2", readOnly: false);
                p.PutRef("rc", "ActiveConnection", "cn2").Get("rc", "State").Call("rc", "UpdateBatch", A.I4(Ado.AffectAll));
                ErrClear(p, "cn2");
                ExecRead(p, "cn2", "st", StoredW);
                p.Release("cs").Release("cf").Release("ks").Release("kf").Call("rc", "Close").Call("rk", "Close").Release("rc").Release("rk").FinishLite("cn2", "cn");
            });
    }

    /// <summary>
    /// ADO-BATCH conflict position: three S changes staged on rows 1-3 of a client batch Recordset of W after another
    /// Connection of the same process has changed the source; UpdateBatch, then the per-row values, the conflicting
    /// filter, Resync of the underlying values and a second UpdateBatch.
    /// </summary>
    private static Probe Conflict(string name, string externalSql, string what) =>
        Q(name, Refs("ADO-BATCH", "ADO-VALUES"),
            $"ADO-BATCH conflict ({what}): a client static batch-optimistic Recordset of SELECT ID, Code, V, V2, S FROM W ORDER BY ID is opened on a read/write copy; a second Connection runs \"{externalSql}\"; " +
            "S of rows 1, 2 and 3 is then set to \"c1\", \"c2\", \"c3\"; UpdateBatch(adAffectAll) and the Errors; each row's Status, ID, S, OriginalValue and UnderlyingValue; Filter = adFilterConflictingRecords (RecordCount, rows) and Filter none; " +
            "Resync(adAffectAll, adResyncUnderlyingValues), the rows again, a second UpdateBatch (Errors) and the rows; then the stored rows through Execute. Asks which staged changes reach the file, the per-row reporting and what a second submission does.",
            p =>
            {
                p.EngineLite();
                AdoOpen(p, "cn", readOnly: false);
                AdoOpen(p, "cn2", readOnly: false);
                AdoRs(p, "rs", SelW, "cn", Ado.UseClient, Ado.OpenStatic, Ado.LockBatchOptimistic, Ado.CmdText);
                ExecNo(p, "cn2", externalSql);
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
                ExecRead(p, "cn", "st", "SELECT ID, Code, S FROM W WHERE ID <= 4 ORDER BY ID");
                p.FinishLite("cn", "cn2");
            });

    // ADO-VIEW ------------------------------------------------------------------------------------------

    private static IEnumerable<Probe> ViewProbes()
    {
        yield return Q("view-filter-operators", Refs("ADO-VIEW"),
            "ADO-VIEW Filter grammar on a client static read-only Recordset of SELECT ID, L, S FROM T ORDER BY ID: twenty criteria strings (comparison operators, AND, OR, NOT, parentheses, LIKE with '%', '*' and no wildcard, IN, BETWEEN, a malformed AND OR, '==' and '!=', " +
            "a differently cased string value, a bracketed and a table-qualified field name, a function call and an arithmetic expression); for each assign Filter, read it back, RecordCount and the matching IDs, then Filter none. Asks which constructs the filter grammar accepts and which rows each selects.",
            p =>
            {
                p.EngineLite();
                AdoOpen(p, "cn");
                AdoRs(p, "rs", "SELECT ID, L, S FROM T ORDER BY ID", "cn", Ado.UseClient, Ado.OpenStatic, Ado.LockReadOnly, Ado.CmdText);
                string[] criteria =
                [
                    "L > 15", "L >= 20 AND S = 'alpha'", "L = 20 OR L = 30", "S <> 'alpha'", "NOT L = 20", "(L = 20 OR L = 30) AND S <> 'alpha'",
                    "S LIKE '%a'", "S LIKE '*ha*'", "S LIKE 'alpha'", "L IN (20, 30)", "L BETWEEN 20 AND 30", "L = 20 AND OR S = 'x'", "L == 20", "L != 20",
                    "S = 'ALPHA'", "S = 'beta'", "[S] = 'alpha'", "T.S = 'alpha'", "UPPER(S) = 'ALPHA'", "L + 10 = 30",
                ];
                FilterRun(p, "rs", criteria);
                AdoErrors(p, "cn");
                p.Call("rs", "Close").Release("rs").FinishLite("cn");
            });

        yield return Q("view-filter-literals", Refs("ADO-VIEW"),
            "ADO-VIEW Filter literals on a client static read-only Recordset of SELECT ID, Code, L, D, C, Dt, S, Y FROM T ORDER BY ID: eighteen criteria (L = NULL, L <> NULL, L IS NULL, S = '', S = 'it''s', S = \"alpha\", Dt = #2024-01-02#, Dt = #1/2/2024#, Dt > #2024-03-01#, " +
            "C = 2.25, D = 1.5, D = 1.50, L = '20', L = 20.0, Y = True, Y = 1, Y = -1, Code = 'a1' AND Dt = #2024-01-02#); for each assign Filter, read RecordCount and the matching IDs, then Filter none. Asks which literal forms the filter grammar accepts and how it compares Null, dates, booleans and quoting.",
            p =>
            {
                p.EngineLite();
                AdoOpen(p, "cn");
                AdoRs(p, "rs", "SELECT ID, Code, L, D, C, Dt, S, Y FROM T ORDER BY ID", "cn", Ado.UseClient, Ado.OpenStatic, Ado.LockReadOnly, Ado.CmdText);
                string[] criteria =
                [
                    "L = NULL", "L <> NULL", "L IS NULL", "S = ''", "S = 'it''s'", "S = \"alpha\"", "Dt = #2024-01-02#", "Dt = #1/2/2024#", "Dt > #2024-03-01#",
                    "C = 2.25", "D = 1.5", "D = 1.50", "L = '20'", "L = 20.0", "Y = True", "Y = 1", "Y = -1", "Code = 'a1' AND Dt = #2024-01-02#",
                ];
                FilterRun(p, "rs", criteria);
                AdoErrors(p, "cn");
                p.Call("rs", "Close").Release("rs").FinishLite("cn");
            });

        yield return Q("view-filter-constants", Refs("ADO-VIEW", "ADO-BATCH-SCOPE"),
            "ADO-VIEW Filter forms on a client static batch-optimistic Recordset of SELECT ID, Code, L, S FROM T ORDER BY ID (read/write copy) with S of row 1 changed, row 2 deleted and one row added: Filter assigned fourteen values " +
            "(adFilterPendingRecords 1, adFilterAffectedRecords 2, adFilterFetchedRecords 3, adFilterConflictingRecords 5, adFilterNone 0, 99, -1, Integer 1, String \"1\", Empty, Null, \"\", Double 1.0, Boolean True): each reads Filter back, RecordCount, BOF, EOF and the matching IDs, then Filter none; " +
            "the whole sequence runs before and after UpdateBatch(adAffectAll). Asks which constants and value tags are accepted and which sets they select before and after submission.",
            p =>
            {
                p.EngineLite();
                AdoOpen(p, "cn", readOnly: false);
                AdoRs(p, "rs", SelS, "cn", Ado.UseClient, Ado.OpenStatic, Ado.LockBatchOptimistic, Ado.CmdText);
                p.GetBind("fl", "rs", "Fields").Item("fs", "fl", A.Str("S")).Item("fc", "fl", A.Str("Code"))
                    .Put("fs", "Value", A.Str("fc1")).Call("rs", "Update").Call("rs", "MoveNext").Call("rs", "Delete").Call("rs", "AddNew").Put("fc", "Value", A.Str("n9")).Call("rs", "Update");
                Arg[] filters =
                [
                    A.I4(Ado.FilterPending), A.I4(Ado.FilterAffected), A.I4(Ado.FilterFetched), A.I4(Ado.FilterConflicting), A.I4(Ado.FilterNone), A.I4(99), A.I4(-1),
                    A.I2(1), A.Str("1"), A.Empty, A.Null, A.Str(""), A.R8(1.0), A.Bool(true),
                ];
                for (int pass = 0; pass < 2; pass++)
                {
                    for (int i = 0; i < filters.Length; i++)
                    {
                        p.Put("rs", "Filter", filters[i]).Get("rs", "Filter").Get("rs", "RecordCount").Get("rs", "BOF").Get("rs", "EOF");
                        ReadCols(p, "rs", 16, "ID");
                        p.Put("rs", "Filter", A.I4(Ado.FilterNone));
                    }
                    if (pass == 0)
                    {
                        p.Call("rs", "UpdateBatch", A.I4(Ado.AffectAll));
                        ErrClear(p, "cn");
                    }
                }
                p.Release("fs").Release("fc").Release("fl").Call("rs", "Close").Release("rs").FinishLite("cn");
            });

        yield return Q("view-sort-grammar", Refs("ADO-VIEW"),
            "ADO-VIEW Sort grammar on a client static read-only Recordset of SELECT ID, L, S, Dt, Y, M, B FROM T: nineteen Sort strings (L, L ASC, L DESC, S, S DESC, \"S, L DESC\", \"L DESC, S ASC\", [S], a trailing comma, an unknown field, \"L DESC DESC\", lower-case \"l desc\", " +
            "extra spaces, Dt, Y, the Memo M, the Binary B, an empty string, Null); each assigned, read back, and the ID, L, S of every row read in cursor order. Asks which sort expressions are accepted, how Null, case and Memo/Binary fields order, and what an empty Sort restores.",
            p =>
            {
                p.EngineLite();
                AdoOpen(p, "cn");
                AdoRs(p, "rs", "SELECT ID, L, S, Dt, Y, M, B FROM T", "cn", Ado.UseClient, Ado.OpenStatic, Ado.LockReadOnly, Ado.CmdText);
                Arg[] sorts =
                [
                    A.Str("L"), A.Str("L ASC"), A.Str("L DESC"), A.Str("S"), A.Str("S DESC"), A.Str("S, L DESC"), A.Str("L DESC, S ASC"), A.Str("[S]"), A.Str("S ASC, "),
                    A.Str("NoSuch"), A.Str("L DESC DESC"), A.Str("l desc"), A.Str("  L   DESC  "), A.Str("Dt"), A.Str("Y"), A.Str("M"), A.Str("B"), A.Str(""), A.Null,
                ];
                foreach (Arg sort in sorts)
                {
                    p.Put("rs", "Sort", sort).Get("rs", "Sort").Call("rs", "MoveFirst");
                    ReadCols(p, "rs", 16, "ID", "L", "S");
                }
                AdoErrors(p, "cn");
                p.Call("rs", "Close").Release("rs").FinishLite("cn");
            });

        yield return Q("view-sort-server", Refs("ADO-VIEW", "ADO-CURSOR"),
            "ADO-VIEW Sort and Filter on non-client cursors, SELECT ID, L, S FROM T ORDER BY ID: for a server forward-only, keyset, static and dynamic read-only Recordset assign Sort = \"L DESC\" (read back, ID order from MoveFirst) and Filter = \"L = 20\" (RecordCount, IDs); " +
            "and a Recordset from Connection.Execute (default cursor) with the same. Asks which cursors accept Sort and Filter and with what errors.",
            p =>
            {
                p.EngineLite();
                AdoOpen(p, "cn");
                foreach ((string bind, int type) in new[] { ("v1", Ado.OpenForwardOnly), ("v2", Ado.OpenKeyset), ("v3", Ado.OpenStatic), ("v4", Ado.OpenDynamic) })
                {
                    AdoRs(p, bind, "SELECT ID, L, S FROM T ORDER BY ID", "cn", Ado.UseServer, type, Ado.LockReadOnly, Ado.CmdText);
                    SortFilterTry(p, bind);
                    p.Call(bind, "Close").Release(bind);
                }
                p.CallBind("v5", "cn", "Execute", A.Str("SELECT ID, L, S FROM T ORDER BY ID"), A.Missing, A.I4(Ado.CmdText));
                SortFilterTry(p, "v5");
                AdoErrors(p, "cn");
                p.Release("v5").FinishLite("cn");
            });

        yield return Q("view-find-client", Refs("ADO-VIEW", "ADO-BOOK"),
            "ADO-VIEW Find(criteria, SkipRows, SearchDirection, Start) on a client static read-only Recordset of SELECT ID, L, S FROM T ORDER BY ID: from MoveFirst Find \"L = 20\" then twice more with SkipRows 1; from MoveLast Find \"L = 20\" backward (SkipRows 0, then 1); " +
            "Start = 1 (adBookmarkFirst), 2 (adBookmarkLast) and 0 (adBookmarkCurrent) with Find \"ID = 2\"; Find \"L IS NULL\", \"S LIKE 'a*'\", \"S = 'it''s'\", Find with SkipRows -1, with criteria \"\" and Null, and a forward Find issued at EOF. " +
            "After each read EOF, BOF, AbsolutePosition and the current ID and the Errors. Asks the cursor position after a match and after no match for each direction, skip and start.",
            p =>
            {
                p.EngineLite();
                AdoOpen(p, "cn");
                AdoRs(p, "rs", "SELECT ID, L, S FROM T ORDER BY ID", "cn", Ado.UseClient, Ado.OpenStatic, Ado.LockReadOnly, Ado.CmdText);
                void Look()
                {
                    p.Get("rs", "EOF").Get("rs", "BOF").Get("rs", "AbsolutePosition");
                    Cur(p, "rs", "ID");
                    ErrClear(p, "cn");
                }
                p.Call("rs", "MoveFirst").Call("rs", "Find", A.Str("L = 20"));
                Look();
                p.Call("rs", "Find", A.Str("L = 20"), A.I4(1));
                Look();
                p.Call("rs", "Find", A.Str("L = 20"), A.I4(1));
                Look();
                p.Call("rs", "MoveLast").Call("rs", "Find", A.Str("L = 20"), A.I4(0), A.I4(-1));
                Look();
                p.Call("rs", "Find", A.Str("L = 20"), A.I4(1), A.I4(-1));
                Look();
                foreach (int start in new[] { 1, 2, 0 })
                {
                    p.Call("rs", "MoveLast").Call("rs", "Find", A.Str("ID = 2"), A.I4(0), A.I4(1), A.I4(start));
                    Look();
                }
                foreach (string crit in new[] { "L IS NULL", "S LIKE 'a*'", "S = 'it''s'" })
                {
                    p.Call("rs", "MoveFirst").Call("rs", "Find", A.Str(crit));
                    Look();
                }
                p.Call("rs", "MoveFirst").Call("rs", "Find", A.Str("L = 20"), A.I4(-1));
                Look();
                p.Call("rs", "MoveFirst").Call("rs", "Find", A.Str(""));
                Look();
                p.Call("rs", "MoveFirst").Call("rs", "Find", A.Null);
                Look();
                p.Call("rs", "MoveLast").Call("rs", "MoveNext").Call("rs", "Find", A.Str("ID = 1"));
                Look();
                p.Call("rs", "Close").Release("rs").FinishLite("cn");
            });

        yield return Q("view-filter-addnew", Refs("ADO-VIEW", "ADO-EDIT", "ADO-BATCH-SCOPE"),
            "ADO-VIEW membership under edits (read/write copy): on a client static batch-optimistic Recordset of SELECT ID, Code, L, S FROM T ORDER BY ID with Filter \"L = 20\" read RecordCount; AddNew with L 99 and Update (RecordCount, EOF, current ID, Status); " +
            "AddNew with L 20 and Update (RecordCount, current ID, AbsolutePosition); change L of the current row to 21 and Update (RecordCount, current ID, AbsolutePosition, EOF); Delete the current row (RecordCount, current Status); Filter none (RecordCount and every ID and Status). " +
            "Asks how added, changed and deleted rows enter or leave a filtered view and where the cursor moves.",
            p =>
            {
                p.EngineLite();
                AdoOpen(p, "cn", readOnly: false);
                AdoRs(p, "rs", SelS, "cn", Ado.UseClient, Ado.OpenStatic, Ado.LockBatchOptimistic, Ado.CmdText);
                p.GetBind("fl", "rs", "Fields").Item("fl1", "fl", A.Str("L")).Item("fc", "fl", A.Str("Code")).Put("rs", "Filter", A.Str("L = 20")).Get("rs", "RecordCount");
                p.Call("rs", "AddNew").Put("fl1", "Value", A.I4(99)).Put("fc", "Value", A.Str("n9")).Call("rs", "Update").Get("rs", "RecordCount").Get("rs", "EOF").Get("rs", "Status");
                Cur(p, "rs", "ID");
                p.Call("rs", "AddNew").Put("fl1", "Value", A.I4(20)).Put("fc", "Value", A.Str("n10")).Call("rs", "Update").Get("rs", "RecordCount").Get("rs", "AbsolutePosition");
                Cur(p, "rs", "ID");
                p.Put("fl1", "Value", A.I4(21)).Call("rs", "Update").Get("rs", "RecordCount").Get("rs", "AbsolutePosition").Get("rs", "EOF");
                Cur(p, "rs", "ID");
                p.Call("rs", "Delete").Get("rs", "RecordCount").Get("rs", "Status");
                p.Put("rs", "Filter", A.I4(Ado.FilterNone)).Get("rs", "RecordCount").Call("rs", "MoveFirst");
                StatusRows(p, "rs");
                AdoErrors(p, "cn");
                p.Release("fl1").Release("fc").Release("fl").Call("rs", "Close").Release("rs").FinishLite("cn");
            });
    }

    // ADO-PERSIST ------------------------------------------------------------------------------------------

    private static IEnumerable<Probe> PersistProbes()
    {
        yield return Q("persist-roundtrip-typed", Refs("ADO-PERSIST", "ADO-BUFFER", "R-FMT-1"),
            "ADO-PERSIST typed round trip: a client static read-only Recordset of SELECT * FROM T ORDER BY ID is saved as ADTG and as XML (both files kept); the original's Field metadata (Name, Type, DefinedSize, Precision, NumericScale, Attributes) is read; " +
            "each file is reopened connectionless with adCmdFile (State, RecordCount, ActiveConnection, the same metadata, then Value and ActualSize of every field of every row). Asks what schema, types, sizes and values each format restores.",
            p =>
            {
                p.EngineLite();
                AdoOpen(p, "cn");
                AdoRs(p, "rs", "SELECT * FROM T ORDER BY ID", "cn", Ado.UseClient, Ado.OpenStatic, Ado.LockReadOnly, Ado.CmdText);
                FieldMeta(p, "rs");
                p.Call("rs", "Save", A.Out("t.adtg"), A.I4(Ado.PersistAdtg)).Call("rs", "Save", A.Out("t.xml"), A.I4(Ado.PersistXml)).Files("after-save", keep: true)
                    .Call("rs", "Close").Release("rs");
                foreach ((string bind, string file) in new[] { ("ra", "t.adtg"), ("rx", "t.xml") })
                {
                    p.Create("ADODB.Recordset", bind).Call(bind, "Open", A.Out(file), A.Missing, A.I4(Ado.OpenStatic), A.I4(Ado.LockReadOnly), A.I4(Ado.CmdFile))
                        .Get(bind, "State").Get(bind, "RecordCount").Get(bind, "ActiveConnection");
                    FieldMeta(p, bind);
                    AdoReadAll(p, bind, 8, 20, actualSize: true);
                    p.Call(bind, "Close").Release(bind);
                }
                p.FinishLite("cn");
            });

        yield return Q("persist-overwrite-flags", Refs("ADO-PERSIST"),
            "ADO-PERSIST Save forms on a client static read-only Recordset of SELECT ID, Code, L, S FROM T ORDER BY ID: Save(path) with the format omitted (reopen with adCmdFile: State, RecordCount, then the file kept); Save to the same path again with no flag; with adSaveCreateNotExist; " +
            "with adSaveCreateOverWrite; with adSaveCreateOverWrite and adPersistXML on the same path (reopen); Save with Format 99; Save with Destination omitted; Save after Close. " +
            "Each reads the Errors (cleared). Asks the default format, which flags overwrite and which forms are refused.",
            p =>
            {
                p.EngineLite();
                AdoOpen(p, "cn");
                AdoRs(p, "rs", SelS, "cn", Ado.UseClient, Ado.OpenStatic, Ado.LockReadOnly, Ado.CmdText);
                void Reopen(string bind, string file)
                {
                    p.Create("ADODB.Recordset", bind).Call(bind, "Open", A.Out(file), A.Missing, A.I4(Ado.OpenStatic), A.I4(Ado.LockReadOnly), A.I4(Ado.CmdFile)).Get(bind, "State")
                        .Get(bind, "RecordCount").Call(bind, "Close").Release(bind);
                }
                p.Call("rs", "Save", A.Out("o.dat"));
                ErrClear(p, "cn");
                Reopen("o1", "o.dat");
                p.Files("after-first", keep: true).Call("rs", "Save", A.Out("o.dat"));
                ErrClear(p, "cn");
                p.Call("rs", "Save", A.Out("o.dat"), A.I4(Ado.PersistAdtg), A.I4(Ado.SaveCreateNotExist));
                ErrClear(p, "cn");
                p.Call("rs", "Save", A.Out("o.dat"), A.I4(Ado.PersistAdtg), A.I4(Ado.SaveCreateOverWrite));
                ErrClear(p, "cn");
                p.Call("rs", "Save", A.Out("o.dat"), A.I4(Ado.PersistXml), A.I4(Ado.SaveCreateOverWrite));
                ErrClear(p, "cn");
                Reopen("o2", "o.dat");
                p.Call("rs", "Save", A.Out("f99.dat"), A.I4(99));
                ErrClear(p, "cn");
                p.Call("rs", "Save").Files("after-all", keep: true);
                ErrClear(p, "cn");
                p.Call("rs", "Close").Call("rs", "Save", A.Out("closed.dat"), A.I4(Ado.PersistAdtg));
                ErrClear(p, "cn");
                p.Release("rs").FinishLite("cn");
            });

        yield return Q("persist-stream-roundtrip", Refs("ADO-PERSIST", "ADO-STREAM"),
            "ADO-PERSIST through Streams on a client static read-only Recordset of SELECT ID, Code, L, S FROM T ORDER BY ID: Save into a new unopened Stream as ADTG (Stream State, Size, Position, Type), Position 0 and Read(adReadAll) (the bytes); Save into a second new Stream as XML " +
            "(State, Size, Type, Charset, Position 0, ReadText(adReadAll)); Recordset.Open with each Stream as Source (State, RecordCount, all rows); SaveToFile of the ADTG Stream, a third Stream opened, loaded with LoadFromFile of that file and opened as a Source; " +
            "and a Recordset.Open of the saved file path with adCmdFile. Asks what Save leaves in a Stream and which Stream forms Open restores.",
            p =>
            {
                p.EngineLite();
                AdoOpen(p, "cn");
                AdoRs(p, "rs", SelS, "cn", Ado.UseClient, Ado.OpenStatic, Ado.LockReadOnly, Ado.CmdText);
                p.Create("ADODB.Stream", "sa").Call("rs", "Save", A.Obj("sa"), A.I4(Ado.PersistAdtg)).Get("sa", "State").Get("sa", "Size").Get("sa", "Position").Get("sa", "Type")
                    .Put("sa", "Position", A.I4(0)).Call("sa", "Read", A.I4(Ado.ReadAll));
                p.Create("ADODB.Stream", "sx").Call("rs", "Save", A.Obj("sx"), A.I4(Ado.PersistXml)).Get("sx", "State").Get("sx", "Size").Get("sx", "Type").Get("sx", "Charset")
                    .Put("sx", "Position", A.I4(0)).Call("sx", "ReadText", A.I4(Ado.ReadAll));
                ErrClear(p, "cn");
                foreach (string s in new[] { "sa", "sx" })
                {
                    string r = "r_" + s;
                    p.Create("ADODB.Recordset", r).Put(s, "Position", A.I4(0)).Call(r, "Open", A.Obj(s), A.Missing, A.I4(Ado.OpenStatic), A.I4(Ado.LockReadOnly), A.Missing).Get(r, "State").Get(r, "RecordCount");
                    AdoReadAll(p, r, 8, 4);
                    p.Call(r, "Close").Release(r);
                }
                p.Put("sa", "Position", A.I4(0)).Call("sa", "SaveToFile", A.Out("s.adtg"), A.I4(Ado.SaveCreateOverWrite)).Files("after-stream-save", keep: true)
                    .Create("ADODB.Stream", "sl").Put("sl", "Type", A.I4(1)).Call("sl", "Open").Call("sl", "LoadFromFile", A.Out("s.adtg")).Get("sl", "Size").Put("sl", "Position", A.I4(0))
                    .Create("ADODB.Recordset", "r3").Call("r3", "Open", A.Obj("sl"), A.Missing, A.I4(Ado.OpenStatic), A.I4(Ado.LockReadOnly), A.Missing).Get("r3", "State").Get("r3", "RecordCount");
                AdoReadAll(p, "r3", 8, 4);
                p.Call("r3", "Close").Release("r3")
                    .Create("ADODB.Recordset", "r4").Call("r4", "Open", A.Out("s.adtg"), A.Missing, A.I4(Ado.OpenStatic), A.I4(Ado.LockReadOnly), A.I4(Ado.CmdFile)).Get("r4", "State").Get("r4", "RecordCount");
                AdoErrors(p, "cn");
                p.Call("r4", "Close").Release("r4").Call("sl", "Close").Call("sa", "Close").Call("sx", "Close").Release("sl").Release("sa").Release("sx").Call("rs", "Close").Release("rs").FinishLite("cn");
            });

        yield return Reconnect("persist-reconnect-adtg", "adtg", Ado.PersistAdtg, "ADTG");
        yield return Reconnect("persist-reconnect-xml", "xml", Ado.PersistXml, "XML");

        yield return Q("persist-malformed-files", Refs("ADO-PERSIST", "R-ERR-1"),
            "ADO-PERSIST failure shapes: ten files written with exact bytes (empty; one byte; three ASCII bytes; 512 zero bytes; \"<xml\"; a UTF-8 BOM alone; \"<xml/>\"; an XML declaration alone; an empty persisted-recordset root with the schema namespaces; a nonexistent path), " +
            "each opened connectionless with Recordset.Open(path, Missing, static, batch optimistic, adCmdFile): State, Fields.Count where open, then Close. Asks which malformed inputs are refused at Open, with which HRESULT and description, and which open as empty recordsets.",
            p =>
            {
                p.EngineLite();
                (string Name, byte[] Bytes)[] files =
                [
                    ("empty", []), ("one-byte", [0x41]), ("three-ascii", Ascii("abc")), ("zeros-512", new byte[512]), ("xml-open-tag", Ascii("<xml")),
                    ("bom-only", [0xEF, 0xBB, 0xBF]), ("empty-xml-root", Ascii("<xml/>")), ("xml-declaration-only", Ascii("<?xml version=\"1.0\"?>")),
                    ("empty-rowset-root", Ascii("<xml xmlns:s=\"uuid:BDC6E3F0-6DA3-11d1-A2A3-00AA00C14882\" xmlns:z=\"#RowsetSchema\"></xml>")),
                ];
                for (int i = 0; i < files.Length; i++)
                {
                    string file = $"mal-{i}.dat";
                    string r = "mf" + i;
                    p.WriteFile("work:" + file, files[i].Bytes).Create("ADODB.Recordset", r)
                        .Call(r, "Open", A.Out(file), A.Missing, A.I4(Ado.OpenStatic), A.I4(Ado.LockBatchOptimistic), A.I4(Ado.CmdFile)).Get(r, "State")
                        .GetBind(r + "f", r, "Fields").Get(r + "f", "Count").Release(r + "f").Call(r, "Close").Release(r);
                }
                p.Create("ADODB.Recordset", "mfx").Call("mfx", "Open", A.Out("mal-absent.dat"), A.Missing, A.I4(Ado.OpenStatic), A.I4(Ado.LockBatchOptimistic), A.I4(Ado.CmdFile)).Get("mfx", "State")
                    .Release("mfx").Files("after-open", keep: true).FinishLite();
            });
    }

    /// <summary>
    /// ADO-PERSIST reconnect: pending edit, delete and insert of a client batch Recordset of W are saved, the file reopened
    /// connectionless, given an ActiveConnection and submitted.
    /// </summary>
    private static Probe Reconnect(string name, string ext, int format, string label) =>
        Q(name, Refs("ADO-PERSIST", "ADO-DISCONNECT", "ADO-BATCH"),
            $"ADO-PERSIST/ADO-DISCONNECT with {label}: a client static batch-optimistic Recordset of SELECT ID, Code, V, V2, S FROM W ORDER BY ID (read/write copy) has S of row 1 changed, row 2 deleted and a row inserted (ID 40, Code \"w40\"), " +
            $"and is saved as {label} (file kept) and closed; the file is reopened connectionless with adCmdFile (State, RecordCount, each row's Status, S, OriginalValue and UnderlyingValue); ActiveConnection is set by reference to the read/write Connection (State) and UpdateBatch(adAffectAll) is called (Errors); " +
            "then the Recordset property \"Unique Table\" is set to \"W\" and UpdateBatch called again (Errors); the rows again and the stored rows through Execute. Asks whether pending changes survive the file and whether a reopened recordset can submit them.",
            p =>
            {
                p.EngineLite();
                AdoOpen(p, "cn", readOnly: false);
                AdoRs(p, "rs", SelW, "cn", Ado.UseClient, Ado.OpenStatic, Ado.LockBatchOptimistic, Ado.CmdText);
                p.GetBind("fl", "rs", "Fields").Item("fs", "fl", A.Str("S")).Item("fid", "fl", A.Str("ID")).Item("fc", "fl", A.Str("Code")).Put("fs", "Value", A.Str("pr1")).Call("rs", "MoveNext").Call("rs", "Delete")
                    .Call("rs", "AddNew").Put("fid", "Value", A.I4(40)).Put("fc", "Value", A.Str("w40")).Call("rs", "Update")
                    .Call("rs", "Save", A.Out("pending." + ext), A.I4(format)).Files("after-save", keep: true)
                    .Release("fs").Release("fid").Release("fc").Release("fl").Call("rs", "Close").Release("rs");
                p.Create("ADODB.Recordset", "r2").Call("r2", "Open", A.Out("pending." + ext), A.Missing, A.I4(Ado.OpenStatic), A.I4(Ado.LockBatchOptimistic), A.I4(Ado.CmdFile))
                    .Get("r2", "State").Get("r2", "RecordCount").Call("r2", "MoveFirst");
                BatchValues(p, "r2", "S");
                p.PutRef("r2", "ActiveConnection", "cn").Get("r2", "State").Call("r2", "UpdateBatch", A.I4(Ado.AffectAll));
                ErrClear(p, "cn");
                PropPut(p, "r2", "Unique Table", A.Str("W"), "ut");
                p.Call("r2", "UpdateBatch", A.I4(Ado.AffectAll));
                ErrClear(p, "cn");
                p.Call("r2", "MoveFirst");
                BatchValues(p, "r2", "S");
                p.Call("r2", "Close").Release("r2");
                ExecRead(p, "cn", "st", "SELECT ID, Code, S FROM W ORDER BY ID");
                p.FinishLite("cn");
            });

    // Fields ---------------------------------------------------------------------------------------------

    private static IEnumerable<Probe> FieldProbes()
    {
        yield return Q("fields-meta-expressions", Refs("ADO-BUFFER", "ADO-SCHEMA"),
            "ADO-BUFFER metadata of computed columns: for four SELECT lists over T (arithmetic and string expressions, IIf, CCur, CDbl, CInt, CBool, CDate, Null, integer, decimal and string and date and Boolean literals, Now(); " +
            "aggregates Count, Sum, Avg, Min, Max and First over Long, Double, Currency, Integer, Text, Date and Memo; CDec of a Long; Format of a date) each opened as a server forward-only and a client static read-only Recordset: State and, for every field, Name, Type, DefinedSize, Precision, NumericScale and Attributes, and the first row's Value and ActualSize. " +
            "Asks the type and size ADO publishes for expressions and aggregates and whether a failing expression refuses the whole open.",
            p =>
            {
                p.EngineLite();
                AdoOpen(p, "cn");
                string[] sqls =
                [
                    "SELECT L + 1 AS e1, L * 1.5 AS e2, D / 2 AS e3, S & 'x' AS e4, Len(S) AS e5, IIf(L > 15, 'a', 'b') AS e6, CCur(L) AS e7, CDbl(L) AS e8, CInt(L) AS e9, CBool(L) AS e10, CDate(0) AS e11, " +
                    "Null AS e12, 1 AS e13, 1.5 AS e14, 'lit' AS e15, #2024-01-02# AS e16, True AS e17, Now() AS e18 FROM T WHERE ID = 1",
                    "SELECT Count(*) AS a1, Sum(L) AS a2, Avg(D) AS a3, Min(S) AS a4, Max(Dt) AS a5, Sum(C) AS a6, Sum(Sh) AS a7, First(M) AS a8, Count(L) AS a9 FROM T",
                    "SELECT CDec(L) AS x1 FROM T WHERE ID = 1",
                    "SELECT Format(Dt, 'yyyy') AS x1, Format(L, '000') AS x2 FROM T WHERE ID = 1",
                ];
                for (int i = 0; i < sqls.Length; i++)
                {
                    foreach ((string tag, int loc, int type) in new[] { ("s", Ado.UseServer, Ado.OpenForwardOnly), ("c", Ado.UseClient, Ado.OpenStatic) })
                    {
                        string r = $"x{i}{tag}";
                        AdoRs(p, r, sqls[i], "cn", loc, type, Ado.LockReadOnly, Ado.CmdText);
                        FieldMeta(p, r);
                        AdoReadAll(p, r, 1, 20, actualSize: true);
                        ErrClear(p, "cn");
                        p.Call(r, "Close").Release(r);
                    }
                }
                p.FinishLite("cn");
            });

        yield return Q("fields-meta-mdb", Refs("ADO-BUFFER", "R-FMT-1", "ADO-SCHEMA"),
            "ADO-BUFFER on the MDB fixture opened through the ACE provider: SELECT * FROM T ORDER BY ID as a server forward-only and as a client static read-only Recordset; for every field Name, Type, DefinedSize, Precision, NumericScale and Attributes, " +
            "then Value and ActualSize of every field of every row. Asks the types, sizes and values the provider publishes for the Jet 4 format (the table was written through DAO).",
            p =>
            {
                p.EngineLite();
                AdoOpen(p, "cn");
                foreach ((string r, int loc, int type) in new[] { ("ms", Ado.UseServer, Ado.OpenForwardOnly), ("mc", Ado.UseClient, Ado.OpenStatic) })
                {
                    AdoRs(p, r, "SELECT * FROM T ORDER BY ID", "cn", loc, type, Ado.LockReadOnly, Ado.CmdText);
                    FieldMeta(p, r);
                    AdoReadAll(p, r, 8, 20, actualSize: true);
                    p.Call(r, "Close").Release(r);
                }
                AdoErrors(p, "cn");
                p.FinishLite("cn");
            }, Mdb);

        yield return Q("fields-assign-conversion", Refs("ADO-EDIT", "ADO-VALUES", "R-ERR-1"),
            "ADO-EDIT conversion at assignment: on a server keyset optimistic Recordset of SELECT * FROM T ORDER BY ID (read/write copy), thirty-eight (field, value) pairs assigned to Field.Value of row 1 with the Value read back and CancelUpdate after each, with no Update " +
            "(for example String \"12\", Double 12.7, Boolean, String \"1e2\", Null and Empty into Long; Long, Double, Boolean, Date, Currency and a byte array into Text; String and numbers into Date/Time; Long, String and Double into Yes/No; overflow values into Integer, Byte and Single; " +
            "strings into Currency, Double and Decimal; a valid and an invalid GUID string; a String into Binary; a Long into Memo; and a Long into the AutoNumber key). Asks which assignments convert at once, which are refused at assignment and what each field reads back.",
            p =>
            {
                p.EngineLite();
                AdoOpen(p, "cn", readOnly: false);
                AdoRs(p, "rs", "SELECT * FROM T ORDER BY ID", "cn", Ado.UseServer, Ado.OpenKeyset, Ado.LockOptimistic, Ado.CmdText);
                (string Field, Arg Value)[] pairs =
                [
                    ("L", A.Str("12")), ("L", A.R8(12.7)), ("L", A.Bool(true)), ("L", A.Str("1e2")), ("L", A.Null), ("L", A.Empty),
                    ("S", A.I4(5)), ("S", A.R8(1.5)), ("S", A.Bool(true)), ("S", A.Date(45292.0)), ("S", A.Cy(1.5m)), ("S", A.Bytes(1, 2)),
                    ("Dt", A.Str("2024-05-06")), ("Dt", A.Str("abc")), ("Dt", A.I4(5)), ("Dt", A.R8(1.5)),
                    ("Y", A.I4(5)), ("Y", A.Str("1")), ("Y", A.Str("true")), ("Y", A.R8(0.0)),
                    ("D", A.Str("1.5")), ("D", A.Bool(true)), ("C", A.Str("1.23456")), ("C", A.R8(1.23456)), ("N", A.Str("1.5")), ("N", A.R8(1.5)), ("N", A.Cy(1.5m)),
                    ("G", A.Str("{00112233-4455-6677-8899-AABBCCDDEEFF}")), ("G", A.Str("not a guid")),
                    ("Sh", A.I4(40000)), ("Bt", A.I4(300)), ("Bt", A.I4(-1)), ("Sg", A.R8(1e300)),
                    ("B", A.Str("abc")), ("M", A.I4(5)), ("ID", A.I4(77)), ("L", A.Str("")), ("Code", A.Null),
                ];
                p.GetBind("fl", "rs", "Fields");
                foreach ((string field, Arg value) in pairs)
                {
                    p.Item("fx", "fl", A.Str(field)).Put("fx", "Value", value).Get("fx", "Value").Release("fx").Call("rs", "CancelUpdate");
                }
                AdoErrors(p, "cn");
                p.Release("fl").Call("rs", "Close").Release("rs");
                ExecRead(p, "cn", "st", StoredT);
                p.FinishLite("cn");
            });

        yield return Q("fields-refresh-ddl", Refs("ADO-SCHEMA", "ADO-RESYNC", "R-CAT-1"),
            "ADO-SCHEMA Fields.Refresh against a definition change (read/write copy): a server keyset read-only and a client static read-only Recordset of SELECT * FROM W each read Fields.Count and call Fields.Refresh (Count); " +
            "ALTER TABLE W ADD COLUMN X TEXT(10) executes on the Connection (Errors); each Recordset reads Count, calls Fields.Refresh and reads Count, Requery and Count, and reads Fields.Item(\"X\") Name; " +
            "a new Recordset of SELECT * FROM W reads Count; the first Recordset then closes and Fields.Refresh is called on the retained collection. Asks whether Refresh, Requery or neither picks up the new column and what a closed collection answers.",
            p =>
            {
                p.EngineLite();
                AdoOpen(p, "cn", readOnly: false);
                AdoRs(p, "rk", "SELECT * FROM W ORDER BY ID", "cn", Ado.UseServer, Ado.OpenKeyset, Ado.LockReadOnly, Ado.CmdText);
                AdoRs(p, "rc", "SELECT * FROM W ORDER BY ID", "cn", Ado.UseClient, Ado.OpenStatic, Ado.LockReadOnly, Ado.CmdText);
                foreach (string r in new[] { "rk", "rc" })
                {
                    p.GetBind(r + "f", r, "Fields").Get(r + "f", "Count").Call(r + "f", "Refresh").Get(r + "f", "Count");
                }
                ExecNo(p, "cn", "ALTER TABLE W ADD COLUMN X TEXT(10)");
                ErrClear(p, "cn");
                foreach (string r in new[] { "rk", "rc" })
                {
                    p.Get(r + "f", "Count").Call(r + "f", "Refresh").Get(r + "f", "Count").Call(r, "Requery").Get(r + "f", "Count").Item(r + "x", r + "f", A.Str("X")).Get(r + "x", "Name").Release(r + "x");
                    ErrClear(p, "cn");
                }
                AdoRs(p, "rn", "SELECT * FROM W ORDER BY ID", "cn", Ado.UseServer, Ado.OpenKeyset, Ado.LockReadOnly, Ado.CmdText);
                p.GetBind("rnf", "rn", "Fields").Get("rnf", "Count").Release("rnf").Call("rk", "Close").Call("rkf", "Refresh").Get("rkf", "Count");
                ErrClear(p, "cn");
                p.Release("rkf").Release("rcf").Call("rc", "Close").Call("rn", "Close").Release("rk").Release("rc").Release("rn").FinishLite("cn");
            });
    }

    // ADO-TXN --------------------------------------------------------------------------------------------

    private static IEnumerable<Probe> TransactionProbes()
    {
        yield return Q("txn-rollback-scope-objects", Refs("ADO-TXN", "R-TXN-1"),
            "ADO-TXN scope on a read/write Connection: BeginTrans; a Connection.Execute UPDATE of S of ID 1, a Command.Execute UPDATE of S of ID 2, a server keyset optimistic Recordset edit and Update of S of ID 3 and a client static batch-optimistic Recordset edit and UpdateBatch of S of ID 4 " +
            "(all of them opened on the Connection before BeginTrans); the stored rows are read inside the transaction through Execute; RollbackTrans; the stored rows are read again, and independently through DAO OpenDatabase. Asks which of the four routes the transaction covers and what rolls back.",
            p =>
            {
                p.EngineLite();
                AdoOpen(p, "cn", readOnly: false);
                NewCmd(p, "cmd", "cn", "UPDATE T SET S = 'tx-cmd' WHERE ID = 2");
                AdoRs(p, "rk", "SELECT ID, S FROM T ORDER BY ID", "cn", Ado.UseServer, Ado.OpenKeyset, Ado.LockOptimistic, Ado.CmdText);
                AdoRs(p, "rb", "SELECT ID, S FROM T ORDER BY ID", "cn", Ado.UseClient, Ado.OpenStatic, Ado.LockBatchOptimistic, Ado.CmdText);
                p.Call("cn", "BeginTrans");
                ExecNo(p, "cn", "UPDATE T SET S = 'tx-cn' WHERE ID = 1");
                p.Call("cmd", "Execute", A.Missing, A.Missing, A.I4(Ado.CmdText | Ado.ExecuteNoRecords));
                p.GetBind("kf", "rk", "Fields").Item("ks", "kf", A.Str("S")).Call("rk", "MoveFirst").Call("rk", "Move", A.I4(2)).Put("ks", "Value", A.Str("tx-rk")).Call("rk", "Update");
                p.GetBind("bf", "rb", "Fields").Item("bs", "bf", A.Str("S")).Call("rb", "MoveFirst").Call("rb", "Move", A.I4(3)).Put("bs", "Value", A.Str("tx-rb")).Call("rb", "UpdateBatch", A.I4(Ado.AffectAll));
                AdoErrors(p, "cn");
                ExecRead(p, "cn", "in", "SELECT ID, S FROM T WHERE ID <= 4 ORDER BY ID");
                p.Call("cn", "RollbackTrans");
                AdoErrors(p, "cn");
                ExecRead(p, "cn", "out", "SELECT ID, S FROM T WHERE ID <= 4 ORDER BY ID");
                p.OpenDb().OpenRs("dr", "db", "SELECT ID, S FROM T WHERE ID <= 4 ORDER BY ID", Dao.OpenSnapshot).ReadAll("dr", 8, 2).Call("dr", "Close").Release("dr").Call("db", "Close").Release("db");
                p.Release("ks").Release("kf").Release("bs").Release("bf").Call("rk", "Close").Call("rb", "Close").Release("rk").Release("rb").Release("cmd").FinishLite("cn");
            });

        yield return Q("txn-client-cache-rollback", Refs("ADO-TXN", "ADO-CURSOR", "ADO-RESYNC"),
            "ADO-TXN client cache after rollback (read/write copy): a server keyset optimistic, a server static read-only, a client static optimistic and a client static batch-optimistic Recordset of SELECT ID, S FROM T ORDER BY ID are opened; BeginTrans; the Connection UPDATEs S of ID 1, the keyset and the client optimistic Recordsets edit and Update S of ID 2 and the batch Recordset edits S of ID 3 and calls UpdateBatch; " +
            "each Recordset reads S of IDs 1-3 and RecordCount; RollbackTrans; each Recordset reads the same again without refresh, then after Resync(adAffectAll, adResyncAllValues), then after Requery; the stored rows through Execute. " +
            "Asks what each cursor's cache shows after the transaction rolls back and which refresh restores the stored values.",
            p =>
            {
                p.EngineLite();
                AdoOpen(p, "cn", readOnly: false);
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
                ExecNo(p, "cn", "UPDATE T SET S = 'txc' WHERE ID = 1");
                foreach ((string bind, string id, int row) in new[] { ("t1", "2", 1), ("t3", "2", 1), ("t4", "3", 2) })
                {
                    p.GetBind(bind + "f", bind, "Fields").Item(bind + "s", bind + "f", A.Str("S")).Call(bind, "MoveFirst");
                    if (row > 0)
                    {
                        p.Call(bind, "Move", A.I4(row));
                    }
                    p.Put(bind + "s", "Value", A.Str("txr-" + id));
                    p.Call(bind, bind == "t4" ? "UpdateBatch" : "Update");
                    p.Release(bind + "s").Release(bind + "f");
                }
                ErrClear(p, "cn");
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
                    ReadCols(p, bind, 16, "ID", "S");
                    ErrClear(p, "cn");
                }
                foreach ((string bind, int _, int _, int _) in sets)
                {
                    p.Call(bind, "Requery");
                    ReadCols(p, bind, 16, "ID", "S");
                    p.Call(bind, "Close").Release(bind);
                    ErrClear(p, "cn");
                }
                ExecRead(p, "cn", "st", "SELECT ID, S FROM T WHERE ID <= 4 ORDER BY ID");
                p.FinishLite("cn");
            });

        yield return Q("txn-nesting-three", Refs("ADO-TXN"),
            "ADO-TXN nesting on a read/write Connection: BeginTrans three times, each returned level read and S of ID 1, 2 and 3 updated to \"L1\", \"L2\", \"L3\" after the matching level; CommitTrans (level 3), RollbackTrans (level 2), stored rows read; " +
            "CommitTrans (level 1), stored rows read; a fresh pair BeginTrans, BeginTrans, UPDATE of S of ID 4, CommitTrans, RollbackTrans, stored rows read; then a CommitTrans of an outer level while the inner is still open (stored rows, Errors). " +
            "Asks the returned levels, what a committed inner level contributes to an outer rollback and how out-of-order resolution answers.",
            p =>
            {
                p.EngineLite();
                AdoOpen(p, "cn", readOnly: false);
                const string rows = "SELECT ID, S FROM T WHERE ID <= 4 ORDER BY ID";
                for (int level = 1; level <= 3; level++)
                {
                    p.Call("cn", "BeginTrans");
                    ExecNo(p, "cn", $"UPDATE T SET S = 'L{level}' WHERE ID = {level}");
                }
                p.Call("cn", "CommitTrans").Call("cn", "RollbackTrans");
                ErrClear(p, "cn");
                ExecRead(p, "cn", "a", rows);
                p.Call("cn", "CommitTrans");
                ErrClear(p, "cn");
                ExecRead(p, "cn", "b", rows);
                p.Call("cn", "BeginTrans").Call("cn", "BeginTrans");
                ExecNo(p, "cn", "UPDATE T SET S = 'inner' WHERE ID = 4");
                p.Call("cn", "CommitTrans").Call("cn", "RollbackTrans");
                ErrClear(p, "cn");
                ExecRead(p, "cn", "c", rows);
                p.Call("cn", "BeginTrans").Call("cn", "BeginTrans");
                ExecNo(p, "cn", "UPDATE T SET S = 'outer-first' WHERE ID = 4");
                p.Call("cn", "CommitTrans");
                ErrClear(p, "cn");
                ExecRead(p, "cn", "d", rows);
                p.Call("cn", "RollbackTrans").Call("cn", "RollbackTrans");
                ErrClear(p, "cn");
                p.FinishLite("cn");
            });

        yield return Q("txn-release-without-close", Refs("ADO-TXN", "R-TXN-1", "R-LOCK-3"),
            "ADO-TXN abandonment on a read/write Connection: BeginTrans and UPDATE S of ID 1 to \"rel\"; the Connection object is released with no CommitTrans, RollbackTrans or Close; after a 300 ms pause the stored row is read through a second ADO Connection, " +
            "through DAO OpenDatabase and through a third ADO Connection after another pause (State and Errors of each); the file and lock-file state is recorded. Asks whether releasing an open transaction commits, rolls back or leaves it pending, and what a later opener sees.",
            p =>
            {
                p.EngineLite();
                AdoOpen(p, "cn", readOnly: false);
                p.Call("cn", "BeginTrans");
                ExecNo(p, "cn", "UPDATE T SET S = 'rel' WHERE ID = 1");
                p.Release("cn").Pause(300);
                AdoOpen(p, "c2", readOnly: false);
                ExecRead(p, "c2", "r2", "SELECT ID, S FROM T WHERE ID <= 2 ORDER BY ID");
                AdoErrors(p, "c2");
                p.OpenDb().OpenRs("dr", "db", "SELECT ID, S FROM T WHERE ID <= 2 ORDER BY ID", Dao.OpenSnapshot).ReadAll("dr", 4, 2).Call("dr", "Close").Release("dr").Call("db", "Close").Release("db");
                p.Call("c2", "Close").Release("c2").Pause(300);
                AdoOpen(p, "c3");
                ExecRead(p, "c3", "r3", "SELECT ID, S FROM T WHERE ID <= 2 ORDER BY ID");
                AdoErrors(p, "c3");
                p.FinishLite("c3");
            });
    }

    // R-ERR-1 -------------------------------------------------------------------------------------------

    private static IEnumerable<Probe> ErrorProbes()
    {
        yield return Q("errors-by-route", Refs("R-ERR-1", "ADO-RESULTS", "ADO-BATCH"),
            "R-ERR-1 channels per route (read/write copy): the same duplicate-key INSERT INTO T (Code) VALUES ('a1') through Connection.Execute, Command.Execute, Recordset.Open of the SQL text, and a server keyset AddNew with Code \"a1\" and Update, " +
            "a client batch AddNew with Code \"a1\" and UpdateBatch, and an edit of row 2's Code to \"a1\" with Update; then DELETE of the referenced P row 1 through Connection.Execute, the saved action query QSet executed by Command with no parameter values, and Execute of the missing name \"QNoSuch\". " +
            "The Errors collection is cleared before each route and read after it (Number, Description, Source, SQLState, NativeError); the call's own HRESULT and EXCEPINFO are captured by the call. Asks the Errors entries and the Automation error each route produces for the same cause.",
            p =>
            {
                p.EngineLite();
                AdoOpen(p, "cn", readOnly: false);
                const string dup = "INSERT INTO T (Code) VALUES ('a1')";
                ClearErrors(p, "cn");
                ExecNo(p, "cn", dup);
                AdoErrors(p, "cn");
                ClearErrors(p, "cn");
                NewCmd(p, "cmd", "cn", dup);
                p.Call("cmd", "Execute", A.Missing, A.Missing, A.I4(Ado.CmdText | Ado.ExecuteNoRecords));
                AdoErrors(p, "cn");
                ClearErrors(p, "cn");
                p.Create("ADODB.Recordset", "ro").Call("ro", "Open", A.Str(dup), A.Obj("cn"), A.I4(Ado.OpenForwardOnly), A.I4(Ado.LockReadOnly), A.I4(Ado.CmdText)).Get("ro", "State");
                AdoErrors(p, "cn");
                ClearErrors(p, "cn");
                AdoRs(p, "rk", SelS, "cn", Ado.UseServer, Ado.OpenKeyset, Ado.LockOptimistic, Ado.CmdText);
                p.GetBind("kf", "rk", "Fields").Item("kc", "kf", A.Str("Code")).Call("rk", "AddNew").Put("kc", "Value", A.Str("a1")).Call("rk", "Update");
                AdoErrors(p, "cn");
                ClearErrors(p, "cn");
                p.Call("rk", "CancelUpdate").Call("rk", "MoveFirst").Call("rk", "MoveNext").Put("kc", "Value", A.Str("a1")).Call("rk", "Update");
                AdoErrors(p, "cn");
                ClearErrors(p, "cn");
                AdoRs(p, "rb", SelS, "cn", Ado.UseClient, Ado.OpenStatic, Ado.LockBatchOptimistic, Ado.CmdText);
                p.GetBind("bf", "rb", "Fields").Item("bc", "bf", A.Str("Code")).Call("rb", "AddNew").Put("bc", "Value", A.Str("a1")).Call("rb", "Update").Call("rb", "UpdateBatch", A.I4(Ado.AffectAll));
                AdoErrors(p, "cn");
                ClearErrors(p, "cn");
                ExecNo(p, "cn", "DELETE FROM P WHERE ID = 1");
                AdoErrors(p, "cn");
                ClearErrors(p, "cn");
                NewCmd(p, "cq", "cn", "QSet", Ado.CmdStoredProc);
                p.Call("cq", "Execute", A.Missing, A.Missing, A.I4(Ado.CmdStoredProc | Ado.ExecuteNoRecords));
                AdoErrors(p, "cn");
                ClearErrors(p, "cn");
                p.Call("cn", "Execute", A.Str("QNoSuch"), A.Missing, A.I4(Ado.CmdStoredProc));
                AdoErrors(p, "cn");
                p.Release("kc").Release("kf").Release("bc").Release("bf").Call("rk", "Close").Call("rb", "Close").Release("rk").Release("rb").Release("ro").Release("cmd").Release("cq").FinishLite("cn");
            });

        yield return Q("errors-lock-timeout", Refs("R-ERR-1", "ADO-RESULTS", "R-LOCK-4"),
            "R-ERR-1 and ADO-RESULTS lock wait inside one process (read/write copy): a second Connection BeginTrans and UPDATEs S of W row 1 without committing; the first Connection (CommandTimeout 2) then runs an UPDATE of the same row through Execute, through a Command (CommandTimeout 1), " +
            "and through a server keyset optimistic Recordset edit and Update of row 1; each reads State, the Errors (Number, Description, Source, SQLState, NativeError, cleared); " +
            "the second Connection then RollbackTrans and the first UPDATE runs again; the stored row is read. Asks the lock-failure error for each route, whether CommandTimeout bounds the wait and what is held afterwards.",
            p =>
            {
                p.EngineLite();
                AdoOpen(p, "cn", readOnly: false);
                AdoOpen(p, "cn2", readOnly: false);
                p.Call("cn2", "BeginTrans");
                ExecNo(p, "cn2", "UPDATE W SET S = 'lk' WHERE ID = 1");
                p.Put("cn", "CommandTimeout", A.I4(2)).Get("cn", "CommandTimeout");
                ExecNo(p, "cn", "UPDATE W SET S = 'mine' WHERE ID = 1");
                ErrClear(p, "cn");
                NewCmd(p, "cmd", "cn", "UPDATE W SET S = 'mine2' WHERE ID = 1");
                p.Put("cmd", "CommandTimeout", A.I4(1)).Call("cmd", "Execute", A.Missing, A.Missing, A.I4(Ado.CmdText | Ado.ExecuteNoRecords));
                ErrClear(p, "cn");
                AdoRs(p, "rs", "SELECT ID, S FROM W ORDER BY ID", "cn", Ado.UseServer, Ado.OpenKeyset, Ado.LockOptimistic, Ado.CmdText);
                p.GetBind("fl", "rs", "Fields").Item("fs", "fl", A.Str("S")).Put("fs", "Value", A.Str("mine3")).Call("rs", "Update").Get("rs", "EditMode");
                ErrClear(p, "cn");
                p.Call("rs", "CancelUpdate").Release("fs").Release("fl").Call("rs", "Close").Release("rs");
                p.Call("cn2", "RollbackTrans");
                ErrClear(p, "cn2");
                ExecNo(p, "cn", "UPDATE W SET S = 'mine4' WHERE ID = 1");
                ErrClear(p, "cn");
                ExecRead(p, "cn", "st", "SELECT ID, S FROM W WHERE ID <= 2 ORDER BY ID");
                p.Release("cmd").FinishLite("cn", "cn2");
            });
    }

    // Helpers ----------------------------------------------------------------------------------------------

    private static Probe Q(string name, string[] refs, string description, Action<P> program, string? fixture = null) =>
        new(Card, name, refs, description, program) { Fixtures = [("main", fixture ?? Main)] };

    /// <summary><c>cn.Properties("Data Source").Value = path</c> through a wrapper released at once.</summary>
    private static void SetDataSource(P p, string cn, Arg path) =>
        p.GetBind(cn + "Props", cn, "Properties").Item(cn + "Ds", cn + "Props", A.Str("Data Source")).Put(cn + "Ds", "Value", path)
            .Release(cn + "Ds").Release(cn + "Props");

    /// <summary>ACE OLE DB connection to the input: Provider, Data Source property, adModeRead when read-only, Open, State.</summary>
    private static void AdoOpen(P p, string cn, bool readOnly = true)
    {
        p.Create("ADODB.Connection", cn).Put(cn, "Provider", A.Str(Ace));
        SetDataSource(p, cn, A.In());
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

    /// <summary>Status and ID of every remaining row.</summary>
    private static void StatusRows(P p, string rs) =>
        p.Until(rs, "EOF", 16, b => b.Get(rs, "Status").GetBind(rs + "Sf", rs, "Fields").Item(rs + "Si", rs + "Sf", A.Str("ID")).Get(rs + "Si", "Value")
            .Release(rs + "Si").Release(rs + "Sf").Call(rs, "MoveNext"));

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

    /// <summary>State, Provider, ConnectionString and Errors after an Open attempt, then Close and release.</summary>
    private static void OpenedState(P p, string cn)
    {
        p.Get(cn, "State").Get(cn, "Provider").Get(cn, "ConnectionString");
        AdoErrors(p, cn);
        p.Call(cn, "Close").Release(cn);
    }

    private static void PutGet(P p, string target, string member, Arg value) => p.Put(target, member, value).Get(target, member);

    /// <summary><c>owner.Properties(name).Value</c> read, assigned and read again through a wrapper released at once.</summary>
    private static void PropPut(P p, string owner, string name, Arg value, string tag) =>
        p.GetBind(tag + "Ps", owner, "Properties").Item(tag + "Px", tag + "Ps", A.Str(name)).Get(tag + "Px", "Value").Put(tag + "Px", "Value", value)
            .Get(tag + "Px", "Value").Release(tag + "Px").Release(tag + "Ps");

    private static void NewCmd(P p, string cmd, string cn, string sql, int type = Ado.CmdText) =>
        p.Create("ADODB.Command", cmd).PutRef(cmd, "ActiveConnection", cn).Put(cmd, "CommandText", A.Str(sql)).Put(cmd, "CommandType", A.I4(type));

    private static void ParamMeta(P p, string prm) =>
        p.Get(prm, "Name").Get(prm, "Type").Get(prm, "Direction").Get(prm, "Size").Get(prm, "Precision").Get(prm, "NumericScale").Get(prm, "Attributes").Get(prm, "Value");

    private static void ParamInventory(P p, string prms) =>
        p.Get(prms, "Count").Each(prms, prms + "P", 8, b => b.Get(prms + "P", "Name").Get(prms + "P", "Type").Get(prms + "P", "Direction")
            .Get(prms + "P", "Size").Get(prms + "P", "Precision").Get(prms + "P", "NumericScale").Get(prms + "P", "Value"));

    private static void ExecNo(P p, string cn, string sql) =>
        p.Call(cn, "Execute", A.Str(sql), A.Missing, A.I4(Ado.CmdText | Ado.ExecuteNoRecords));

    private static void ExecRead(P p, string cn, string bind, string sql)
    {
        p.CallBind(bind, cn, "Execute", A.Str(sql), A.Missing, A.I4(Ado.CmdText));
        AdoReadAll(p, bind, 16, 6);
        p.Release(bind);
    }

    /// <summary>Assign each criteria string to Filter (read back), read RecordCount and the matching IDs, then clear the filter.</summary>
    private static void FilterRun(P p, string rs, string[] criteria)
    {
        foreach (string crit in criteria)
        {
            p.Put(rs, "Filter", A.Str(crit)).Get(rs, "Filter").Get(rs, "RecordCount");
            ReadCols(p, rs, 16, "ID");
            p.Put(rs, "Filter", A.I4(Ado.FilterNone));
        }
    }

    private static void SortFilterTry(P p, string rs)
    {
        p.Put(rs, "Sort", A.Str("L DESC")).Get(rs, "Sort").Call(rs, "MoveFirst");
        ReadCols(p, rs, 16, "ID");
        p.Put(rs, "Filter", A.Str("L = 20")).Get(rs, "RecordCount");
        ReadCols(p, rs, 16, "ID");
    }
}
