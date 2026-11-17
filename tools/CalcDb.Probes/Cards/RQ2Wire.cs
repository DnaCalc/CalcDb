namespace CalcDb.Probes.Cards;

/// <summary>
/// r-q2-wire: second follow-up card for the qualification of R-ORACLE-1 (OR-WIRE, OR-OBS, OR-REENTRY, OR-FREEZE),
/// R-STRING-1 (STR-BYREF) and R-ACT-1 (ACT-ABI, ACT-CLSID, ACT-FACTORY, ACT-AGGREGATE, ACT-TYPECREATE, ACT-RETAIN,
/// ACT-THREAD sink entry), using the worker operations Enumerate, ByRef, CallNamed, Activate, TypeCreate, Advise and
/// SuspendParticipant. Two kinds of probe only:
///   feff-*              the one remaining F item: the length, content and format trigger of the lost leading U+FEFF.
///   byref-, named-, act-, retain-enum-, events-, obs-, freeze-   T items the new operations can reach.
/// Rules held: no OpenDatabase/CreateDatabase/Compact or Connect with an empty, Missing or Null name; no ODBC; no
/// Recordset.Requery with an argument; no SELECT Array(); CLSCTX_REMOTE_SERVER is never requested; no fixture is created
/// (scratch outputs are named "scratch-*"). A database returned by CreateDatabase is held exclusively by its process,
/// so every probe that is opened by a peer or by a second handle is closed and reopened shared first.
/// </summary>
public sealed class RQ2Wire : ICard
{
    private const string Card = "r-q2-wire";
    private const string Ace = "Microsoft.ACE.OLEDB.12.0";
    private const int Fail = Dao.FailOnError;

    private const string IidUnknown = "{00000000-0000-0000-C000-000000000046}";
    private const string IidDispatch = "{00020400-0000-0000-C000-000000000046}";
    private const string IidClassFactory = "{00000001-0000-0000-C000-000000000046}";
    private const string IidMarshal = "{00000003-0000-0000-C000-000000000046}";
    private const string IidClientSecurity = "{0000013D-0000-0000-C000-000000000046}";
    private const string IidDbEngine = "{00000021-0000-0010-8000-00AA006D2EA4}";
    private const string IidAdoConnection = "{00001550-0000-0010-8000-00AA006D2EA4}";
    private const string IidAdoConnection15 = "{00000515-0000-0010-8000-00AA006D2EA4}";
    private const string IidAdoRecordset = "{00001556-0000-0010-8000-00AA006D2EA4}";
    private const string IidAdoCommand = "{986761E8-7269-4890-AA65-AD7C03697A6D}";
    private const string IidAdoStream = "{00001565-0000-0010-8000-00AA006D2EA4}";
    private const string ClsDbEngine = "{CD7791B9-43FD-42C5-AE42-8DD2811F0419}";
    private const string ClsAdoConnection = "{00000514-0000-0010-8000-00AA006D2EA4}";
    private const string ClsAdoRecordset = "{00000535-0000-0010-8000-00AA006D2EA4}";
    private const string ClsAdoCommand = "{00000507-0000-0010-8000-00AA006D2EA4}";
    private const string ClsAdoStream = "{00000566-0000-0010-8000-00AA006D2EA4}";
    private const string AdoConnectionEvents = "{00001400-0000-0010-8000-00AA006D2EA4}";
    private const string AdoRecordsetEvents = "{00001266-0000-0010-8000-00AA006D2EA4}";

    private const uint CtxInproc = 1, CtxHandler = 2, CtxLocal = 4, CtxAll = 0x17;

    private static readonly string LeadX = "﻿";
    private static readonly string[] AdoProgIds = ["ADODB.Connection", "ADODB.Recordset", "ADODB.Command", "ADODB.Stream"];

    public IEnumerable<Probe> Probes()
    {
        // ======================================================= kind 1: U+FEFF trigger (OR-WIRE / R-STRING-1)
        int[] lengths = [1, 2, 3, 4, 5, 6, 8, 10, 12, 16, 20, 32, 50, 64, 80, 100];
        (string, string)[] asciiRows = [.. lengths.Select(n => ($"x{n}", LeadX + new string('x', n)))];
        yield return FeffTable("len-ascii-accdb", "accdb", asciiRows, true,
            $"U+FEFF residual F: the length trigger. TEXT and MEMO rows hold U+FEFF followed by n ASCII x, n = {string.Join(", ", lengths)} (ID is the row order); written by dynaset put, reopened, read by DAO (Value, Len, AscW of positions 1-3) and ADO (Len). Asks the largest n whose leading U+FEFF is lost or re-decoded and the smallest n that survives (n = 100 survived, n = 2 did not), one length at a time.");
        yield return FeffTable("len-ascii-mdb", "mdb", asciiRows, true,
            "U+FEFF residual F: the same sixteen lengths as len-ascii-accdb in an MDB (format variation of the trigger).");
        (string, string)[] contentRows =
        [
            ("e3", LeadX + new string('é', 3)), ("e20", LeadX + new string('é', 20)), ("e100", LeadX + new string('é', 100)),
            ("eur3", LeadX + new string('€', 3)), ("eur20", LeadX + new string('€', 20)), ("eur100", LeadX + new string('€', 100)),
            ("x-eur-x2", LeadX + "x€x"), ("x99-eur", LeadX + new string('x', 99) + "€"), ("eur-x99", LeadX + "€" + new string('x', 99)),
            ("ab-lead-x100", "ab" + LeadX + new string('x', 100)), ("lead2-x100", LeadX + LeadX + new string('x', 100)),
            ("lead-fffe-x100", LeadX + "￾" + new string('x', 100)), ("x100-plain", new string('x', 100)),
        ];
        yield return FeffTable("len-content", "accdb", contentRows, true,
            "U+FEFF residual F: content against length. Rows after a leading U+FEFF: 3, 20 and 100 U+00E9; 3, 20 and 100 euro; x euro x; 99 x then euro; euro then 99 x; a non-leading U+FEFF then 100 x; two leading U+FEFF then 100 x; U+FEFF U+FFFE then 100 x; a plain 100 x control. Asks whether the trigger depends on length, on the characters after the U+FEFF, or on the first non-Latin-1 unit.");
        (string, string)[] memoRows =
        [
            ("m255", LeadX + new string('x', 255)), ("m256", LeadX + new string('x', 256)), ("m500", LeadX + new string('x', 500)),
            ("m1000", LeadX + new string('x', 1000)), ("m4000", LeadX + new string('x', 4000)), ("m8000", LeadX + new string('x', 8000)),
            ("eur500", LeadX + new string('€', 500)), ("x100-eur", LeadX + new string('x', 100) + "€"),
        ];
        yield return FeffTable("memo-long", "accdb", memoRows, false,
            "U+FEFF residual F: values beyond TEXT(255). MEMO-only table; U+FEFF then 255, 256, 500, 1000, 4000 and 8000 x, 500 euro and 100 x then euro. Asks whether the leading U+FEFF of a long Memo survives at every length (the stored form of long values differs).");
        yield return new Probe(Card, "feff-compare-copy", ["R-STRING-1", "R-ORACLE-1", "OR-WIRE"],
            "U+FEFF residual F, write against read: table W(ID, T TEXT(255), M MEMO) holds lone U+FEFF, U+FEFF ab, U+FEFF euro and ab (SQL literals under dbFailOnError). After reopening, asks: which IDs match T = 'ab', T = ChrW(65279) & 'ab', Len(T) = 2; StrComp(T, 'ab', 0) and StrComp(T, ChrW(65279) & 'ab', 0) per row; the rows of W2 after INSERT INTO W2 SELECT FROM W (read decoded, written again), reopened; and Len(T) and the value after UPDATE W SET T = T & 'z'. Asks whether the engine's comparison and copy see the stored bytes or the decoded value.",
            p =>
            {
                string file = "scratch-feff-compare.accdb";
                string[] v = [LeadX, LeadX + "ab", LeadX + "€", "ab"];
                p.EngineLite().CallBind("db", "eng", "CreateDatabase", A.Out(file), A.Str(Dao.LangGeneral), A.I4(Dao.Version120))
                    .Call("db", "Execute", A.Str("CREATE TABLE W (ID LONG CONSTRAINT PK_W PRIMARY KEY, T TEXT(255), M MEMO)"))
                    .Call("db", "Execute", A.Str("CREATE TABLE W2 (ID LONG CONSTRAINT PK_W2 PRIMARY KEY, T TEXT(255), M MEMO)"));
                for (int i = 0; i < v.Length; i++)
                {
                    p.Call("db", "Execute", A.Str($"INSERT INTO W (ID, T, M) VALUES ({i + 1}, '{v[i]}', '{v[i]}')"), A.I4(Fail));
                }
                p.Call("db", "Close").Release("db");
                p.CallBind("db", "eng", "OpenDatabase", A.Out(file), A.Bool(false), A.Bool(false));
                foreach (string sql in new[]
                {
                    "SELECT ID FROM W WHERE T = 'ab' ORDER BY ID",
                    "SELECT ID FROM W WHERE T = ChrW(65279) & 'ab' ORDER BY ID",
                    "SELECT ID FROM W WHERE Len(T) = 2 ORDER BY ID",
                    "SELECT ID, StrComp(T, 'ab', 0) AS c1, StrComp(T, ChrW(65279) & 'ab', 0) AS c2, Len(T) AS lt FROM W ORDER BY ID",
                })
                {
                    p.OpenRs("rs", "db", sql, Dao.OpenSnapshot).ReadAll("rs", 8).Call("rs", "Close").Release("rs");
                }
                p.Call("db", "Execute", A.Str("INSERT INTO W2 (ID, T, M) SELECT ID, T, M FROM W"), A.I4(Fail))
                    .Call("db", "Execute", A.Str("UPDATE W SET T = T & 'z'"), A.I4(Fail))
                    .Call("db", "Close").Release("db");
                p.CallBind("db", "eng", "OpenDatabase", A.Out(file), A.Bool(false), A.Bool(true))
                    .OpenRs("r2", "db", "SELECT ID, T, Len(T) AS lt, Len(M) AS lm FROM W2 ORDER BY ID", Dao.OpenSnapshot).ReadAll("r2", 8).Call("r2", "Close").Release("r2")
                    .OpenRs("r1", "db", "SELECT ID, T, Len(T) AS lt FROM W ORDER BY ID", Dao.OpenSnapshot).ReadAll("r1", 8).Call("r1", "Close").Release("r1");
                p.FinishLite("db");
            });

        // ======================================================= STR-BYREF (R-STRING-1) and OR-WIRE by-reference inputs
        yield return new Probe(Card, "byref-dao-openrecordset-args", ["R-STRING-1", "STR-BYREF", "R-ORACLE-1", "OR-WIRE", "ACT-ABI"],
            "STR-BYREF, DAO input snapshot: Database.OpenRecordset(Name, Type, Options) with the Name passed as VT_BYREF|VT_VARIANT holding a BSTR; with the Type as typed VT_BYREF|VT_I4; Name as typed VT_BYREF|VT_BSTR with Type a by-reference variant I4 2 and Options typed by-reference I4 0; Type as by-reference variant Empty; Type as by-reference variant Null. The recordset Type and RecordCount are read and each by-reference value after the call is captured. Asks whether DAO reads through the reference, rejects it or writes anything back.",
            p =>
            {
                Scratch(p, "scratch-q2-byref-dao.accdb");
                p.CallBind("r1", "db", "OpenRecordset", A.ByRef(A.Str("T")), A.I4(4)).Get("r1", "Type").Get("r1", "RecordCount").Call("r1", "Close").Release("r1");
                p.CallBind("r2", "db", "OpenRecordset", A.Str("T"), A.ByRef(A.I4(4), typed: true)).Get("r2", "Type").Call("r2", "Close").Release("r2");
                p.CallBind("r3", "db", "OpenRecordset", A.ByRef(A.Str("T"), typed: true), A.ByRef(A.I4(2)), A.ByRef(A.I4(0), typed: true)).Get("r3", "Type").Call("r3", "Close").Release("r3");
                p.CallBind("r4", "db", "OpenRecordset", A.Str("T"), A.ByRef(A.Empty));
                p.CallBind("r5", "db", "OpenRecordset", A.Str("T"), A.ByRef(A.Null));
                p.FinishLite("db");
            });
        yield return new Probe(Card, "byref-dao-put-values", ["R-STRING-1", "STR-BYREF", "R-ORACLE-1", "OR-WIRE"],
            "STR-BYREF, DAO Field.Value put through a reference: W(ID LONG key, T TEXT(50), L LONG, D DOUBLE, B BINARY(8)). Row 1 puts by-reference variants (BSTR 'abc', I4 7, R8 2.5, a 3-byte odd BSTR 41 00 42); row 2 puts typed by-reference values (BSTR 'def', I4 8, R8 3.5, the same odd BSTR); row 3 puts a by-reference variant Null into T and Empty into L. Each field's Value is read back after the put and again after reopening. Asks whether a reference is dereferenced, how an odd BSTR travels through VT_BYREF|VT_BSTR, and what a referenced Null or Empty stores.",
            p =>
            {
                string file = "scratch-q2-byref-put.accdb";
                p.EngineLite().CallBind("db", "eng", "CreateDatabase", A.Out(file), A.Str(Dao.LangGeneral), A.I4(Dao.Version120))
                    .Call("db", "Execute", A.Str("CREATE TABLE W (ID LONG CONSTRAINT PK_W PRIMARY KEY, T TEXT(50), L LONG, D DOUBLE, B BINARY(8))"))
                    .OpenRs("w", "db", "W", Dao.OpenDynaset).GetBind("wf", "w", "Fields");
                Arg odd = A.BstrBytes(0x41, 0x00, 0x42);
                (string, Arg, Arg, Arg, Arg)[] rows =
                [
                    ("v", A.ByRef(A.Str("abc")), A.ByRef(A.I4(7)), A.ByRef(A.R8(2.5)), A.ByRef(odd)),
                    ("t", A.ByRef(A.Str("def"), typed: true), A.ByRef(A.I4(8), typed: true), A.ByRef(A.R8(3.5), typed: true), A.ByRef(odd, typed: true)),
                    ("n", A.ByRef(A.Null), A.ByRef(A.Empty), A.ByRef(A.Null), A.ByRef(A.Null)),
                ];
                for (int i = 0; i < rows.Length; i++)
                {
                    (_, Arg t, Arg l, Arg d, Arg b) = rows[i];
                    p.Call("w", "AddNew").Item("fi", "wf", A.Str("ID")).Put("fi", "Value", A.I4(i + 1)).Release("fi");
                    foreach ((string f, Arg v) in new[] { ("T", t), ("L", l), ("D", d), ("B", b) })
                    {
                        p.Item("ff", "wf", A.Str(f)).Put("ff", "Value", v).Get("ff", "Value").Release("ff");
                    }
                    p.Call("w", "Update");
                }
                p.Release("wf").Call("w", "Close").Release("w").Call("db", "Close").Release("db");
                p.CallBind("db", "eng", "OpenDatabase", A.Out(file), A.Bool(false), A.Bool(true))
                    .OpenRs("rs", "db", "SELECT ID, T, L, D, B FROM W ORDER BY ID", Dao.OpenSnapshot).ReadAll("rs", 8).Call("rs", "Close").Release("rs");
                p.FinishLite("db");
            });
        yield return new Probe(Card, "byref-ado-records-affected-counts", ["R-STRING-1", "STR-BYREF", "R-ADO-1", "ADO-RESULTS"],
            "STR-BYREF, ADO writeback contract: Connection.Execute(CommandText, RecordsAffected, Options) with RecordsAffected passed by reference (variant holding Empty, typed I4 -7, typed BSTR 'x', variant I4 -7) for UPDATE of 2 rows, UPDATE of 0 rows, INSERT of 1 row, DELETE of 1 row, a SELECT and a DDL CREATE TABLE. The by-reference value is captured after each call. Asks the written-back count per statement kind, whether the initial value or type matters, and what a by-reference BSTR does.",
            p =>
            {
                Scratch(p, "scratch-q2-byref-ado.accdb");
                p.Call("db", "Close").Release("db");
                OpenAdo(p, "scratch-q2-byref-ado.accdb");
                (string Sql, Arg Ref)[] steps =
                [
                    ("UPDATE T SET N = 'u'", A.ByRef(A.Empty)),
                    ("UPDATE T SET N = 'u' WHERE ID = 99", A.ByRef(A.I4(-7), typed: true)),
                    ("INSERT INTO T VALUES (3, 'c')", A.ByRef(A.Empty)),
                    ("DELETE FROM T WHERE ID = 3", A.ByRef(A.I4(-7))),
                    ("SELECT * FROM T", A.ByRef(A.I4(-7), typed: true)),
                    ("CREATE TABLE T2 (A LONG)", A.ByRef(A.Empty)),
                    ("UPDATE T SET N = 'v'", A.ByRef(A.Str("x"), typed: true)),
                ];
                foreach ((string sql, Arg r) in steps)
                {
                    p.Call("cn", "Execute", A.Str(sql), r, A.I4(1));
                }
                p.FinishLite("cn");
            });
        yield return new Probe(Card, "byref-ado-command-execute", ["R-STRING-1", "STR-BYREF", "R-ADO-1", "ACT-ABI"],
            "STR-BYREF, ADO Command: Command.Execute(RecordsAffected, Parameters, Options) with RecordsAffected a by-reference variant Empty and Parameters Missing; with both by-reference variants Empty; with RecordsAffected typed I4 -7 and Options 129 (adCmdText | adExecuteNoRecords). The Command is bound to the connection with a put by reference of ActiveConnection and CommandText UPDATE T SET N = 'c'. Asks what is written back for the Command route and how a by-reference Parameters Empty is treated.",
            p =>
            {
                Scratch(p, "scratch-q2-byref-cmd.accdb");
                p.Call("db", "Close").Release("db");
                OpenAdo(p, "scratch-q2-byref-cmd.accdb");
                p.Create("ADODB.Command", "cmd").PutRef("cmd", "ActiveConnection", "cn").Put("cmd", "CommandText", A.Str("UPDATE T SET N = 'c'"));
                p.Call("cmd", "Execute", A.ByRef(A.Empty), A.Missing, A.I4(1));
                p.Call("cmd", "Execute", A.ByRef(A.Empty), A.ByRef(A.Empty), A.I4(1));
                p.Call("cmd", "Execute", A.ByRef(A.I4(-7), typed: true), A.Missing, A.I4(129));
                p.Release("cmd").FinishLite("cn");
            });

        // ======================================================= named arguments (ACT-ABI, OR-WIRE)
        yield return new Probe(Card, "named-dao-methods", ["R-ACT-1", "ACT-ABI", "R-ORACLE-1", "OR-WIRE", "R-ERR-1", "ERR-BIND"],
            "ACT-ABI named arguments, DAO: Database.Execute(Query:=, Options:=) both named and in reverse order; Execute with Query positional and Options:= named; DBEngine.CreateWorkspace(UserName:=, Name:=, Password:=) in non-positional order; DBEngine.CreateDatabase(Option:=, Name:=, Locale:=) in reverse order to a scratch file; Execute with a misspelled name (Quer:=) and with Options named twice. Asks how DAO resolves names to positions and refuses unknown or repeated ones.",
            p =>
            {
                Scratch(p, "scratch-q2-named-dao.accdb");
                p.CallNamed(null, "db", "Execute", [], ("Query", A.Str("UPDATE T SET N = 'a' WHERE ID = 1")), ("Options", A.I4(Fail)));
                p.CallNamed(null, "db", "Execute", [], ("Options", A.I4(Fail)), ("Query", A.Str("UPDATE T SET N = 'b' WHERE ID = 1")));
                p.CallNamed(null, "db", "Execute", [A.Str("UPDATE T SET N = 'c' WHERE ID = 99")], ("Options", A.I4(Fail)));
                p.CallNamed("w1", "eng", "CreateWorkspace", [], ("UserName", A.Str("admin")), ("Name", A.Str("rq2ws")), ("Password", A.Str(""))).Get("w1", "Name").Get("w1", "UserName");
                p.CallNamed("d2", "eng", "CreateDatabase", [], ("Option", A.I4(Dao.Version120)), ("Name", A.Out("scratch-q2-named-created.accdb")), ("Locale", A.Str(Dao.LangGeneral)));
                p.CallNamed(null, "db", "Execute", [], ("Quer", A.Str("UPDATE T SET N = 'd'")));
                p.CallNamed(null, "db", "Execute", [A.Str("UPDATE T SET N = 'e'")], ("Options", A.I4(Fail)), ("Options", A.I4(0)));
                p.Release("d2").Release("w1").FinishLite("db");
            });
        yield return new Probe(Card, "named-ado-methods", ["R-ACT-1", "ACT-ABI", "R-ADO-1", "R-ERR-1", "ERR-BIND"],
            "ACT-ABI named arguments, ADO: Connection.Execute with CommandText positional and Options:= named, skipping RecordsAffected; both named (Options:=, CommandText:=); with RecordsAffected:= named as a by-reference variant; Recordset.Open(Source:=, ActiveConnection:=, CursorType:=, LockType:=, Options:=) fully named in a different order; with a misspelled name (ActiveConn:=). Asks how ADO's dispatch resolves names, skipped optionals and by-reference named arguments.",
            p =>
            {
                Scratch(p, "scratch-q2-named-ado.accdb");
                p.Call("db", "Close").Release("db");
                OpenAdo(p, "scratch-q2-named-ado.accdb");
                p.CallNamed(null, "cn", "Execute", [A.Str("UPDATE T SET N = 'a'")], ("Options", A.I4(129)));
                p.CallNamed(null, "cn", "Execute", [], ("Options", A.I4(129)), ("CommandText", A.Str("UPDATE T SET N = 'b'")));
                p.CallNamed(null, "cn", "Execute", [A.Str("UPDATE T SET N = 'c'")], ("RecordsAffected", A.ByRef(A.Empty)), ("Options", A.I4(129)));
                p.Create("ADODB.Recordset", "ar");
                p.CallNamed(null, "ar", "Open", [], ("LockType", A.I4(1)), ("Source", A.Str("SELECT ID, N FROM T ORDER BY ID")), ("Options", A.I4(1)),
                    ("ActiveConnection", A.Obj("cn")), ("CursorType", A.I4(3)));
                p.Get("ar", "State").ReadAll("ar", 4).Call("ar", "Close");
                p.CallNamed(null, "ar", "Open", [A.Str("SELECT ID FROM T")], ("ActiveConn", A.Obj("cn")));
                p.Release("ar").FinishLite("cn");
            });

        // ======================================================= ACT-CLSID, ACT-FACTORY, ACT-AGGREGATE, ACT-TYPECREATE
        yield return new Probe(Card, "act-dao-clsctx-iids", ["R-ACT-1", "ACT-CLSID"],
            "ACT-CLSID, DAO engine: CoCreateInstance of DAO.DBEngine.120 with CLSCTX_INPROC_SERVER and the IIDs _DBEngine, IMarshal, IClassFactory and IClientSecurity (each a requested IID, one varied at a time); with CLSCTX_INPROC_HANDLER and IDispatch; with INPROC|HANDLER; with CLSCTX_ALL minus remote (0x17) and IDispatch; with CLSCTX_LOCAL_SERVER and IUnknown. IDispatch results read Version. Asks which context flags and requested IIDs select ACEDAO and which refuse, with the HRESULTs.",
            p =>
            {
                p.Files("before-open");
                p.Activate("a1", "DAO.DBEngine.120", "cocreate", CtxInproc, IidDbEngine);
                p.Activate("a2", "DAO.DBEngine.120", "cocreate", CtxInproc, IidMarshal);
                p.Activate("a3", "DAO.DBEngine.120", "cocreate", CtxInproc, IidClassFactory);
                p.Activate("a4", "DAO.DBEngine.120", "cocreate", CtxInproc, IidClientSecurity);
                p.Activate("a5", "DAO.DBEngine.120", "cocreate", CtxHandler, IidDispatch);
                p.Activate("a6", "DAO.DBEngine.120", "cocreate", CtxInproc | CtxHandler, IidDispatch).Get("a6", "Version");
                p.Activate("a7", "DAO.DBEngine.120", "cocreate", CtxAll, IidDispatch).Get("a7", "Version");
                p.Activate("a8", "DAO.DBEngine.120", "cocreate", CtxLocal, IidUnknown);
                p.Modules("after-create").Files("after-close");
            });
        yield return new Probe(Card, "act-ado-clsctx-iids", ["R-ACT-1", "ACT-CLSID", "R-ADO-1"],
            "ACT-CLSID, ADO classes: CoCreateInstance with CLSCTX_INPROC_SERVER of ADODB.Connection for _Connection, Connection15, IDispatch, IUnknown and IMarshal; with CLSCTX_LOCAL_SERVER and IDispatch; with 0x17 and IDispatch; of Recordset for _Recordset, of Command for _Command, of Stream for _Stream; and of Connection for _Recordset (a wrong interface of the right class). Version or State is read on the IDispatch results. Asks which requested IIDs the ADO classes answer and the HRESULT of a refused one.",
            p =>
            {
                p.Files("before-open");
                p.Activate("c1", "ADODB.Connection", "cocreate", CtxInproc, IidAdoConnection);
                p.Activate("c2", "ADODB.Connection", "cocreate", CtxInproc, IidAdoConnection15);
                p.Activate("c3", "ADODB.Connection", "cocreate", CtxInproc, IidDispatch).Get("c3", "Version");
                p.Activate("c4", "ADODB.Connection", "cocreate", CtxInproc, IidUnknown);
                p.Activate("c5", "ADODB.Connection", "cocreate", CtxInproc, IidMarshal);
                p.Activate("c6", "ADODB.Connection", "cocreate", CtxLocal, IidDispatch);
                p.Activate("c7", "ADODB.Connection", "cocreate", CtxAll, IidDispatch).Get("c7", "Version");
                p.Activate("r1", "ADODB.Recordset", "cocreate", CtxInproc, IidAdoRecordset);
                p.Activate("m1", "ADODB.Command", "cocreate", CtxInproc, IidAdoCommand);
                p.Activate("s1", "ADODB.Stream", "cocreate", CtxInproc, IidAdoStream);
                p.Activate("w1", "ADODB.Connection", "cocreate", CtxInproc, IidAdoRecordset);
                p.Modules("after-create").Files("after-close");
            });
        yield return new Probe(Card, "act-factory-dao-identity", ["R-ACT-1", "ACT-FACTORY", "ACT-THREAD", "ENGINE-IDENTITY"],
            "ACT-FACTORY, DAO: DAO.DBEngine.120 activated by CoCreateInstance (a1), then by CoGetClassObject with LockServer and CreateInstance twice (b1, b2), then by the ordinary Create (eng). A Workspace 'rq2ws' is appended through a1; Workspaces.Count and names are read through a1, b1, b2 and eng. Asks whether the class factory hands out one engine or several, and whether all routes share its state.",
            p =>
            {
                p.Files("before-open");
                p.Activate("a1", "DAO.DBEngine.120", "cocreate", CtxInproc, IidDispatch);
                p.Activate("b1", ClsDbEngine, "classobject", CtxInproc, IidDispatch);
                p.Activate("b2", ClsDbEngine, "classobject", CtxInproc, IidDispatch);
                p.Create().Modules("after-create");
                p.GetBind("wa", "a1", "Workspaces").Get("wa", "Count")
                    .CallBind("wsx", "a1", "CreateWorkspace", A.Str("rq2ws"), A.Str("admin"), A.Str("")).Call("wa", "Append", A.Obj("wsx")).Get("wa", "Count").Release("wsx");
                foreach (string e in new[] { "a1", "b1", "b2", "eng" })
                {
                    p.GetBind("wq", e, "Workspaces").Get("wq", "Count").Each("wq", "wi", 8, b => b.Get("wi", "Name")).Release("wq");
                }
                p.Files("after-close");
            });
        yield return new Probe(Card, "act-factory-ado-classes", ["R-ACT-1", "ACT-FACTORY", "R-ADO-1"],
            "ACT-FACTORY, ADO: CoGetClassObject with LockServer and CreateInstance (IDispatch) for ADODB.Connection twice, Recordset, Command and Stream by CLSID; Version or State is read; Provider is set on the first Connection and read on both. Asks whether the ADO factories hand out independent objects and what the factory accounting records.",
            p =>
            {
                p.Files("before-open");
                p.Activate("k1", ClsAdoConnection, "classobject", CtxInproc, IidDispatch).Get("k1", "Version");
                p.Activate("k2", ClsAdoConnection, "classobject", CtxInproc, IidDispatch).Get("k2", "Version");
                p.Put("k1", "Provider", A.Str(Ace)).Get("k1", "Provider").Get("k2", "Provider");
                p.Activate("k3", ClsAdoRecordset, "classobject", CtxInproc, IidDispatch).Get("k3", "State");
                p.Activate("k4", ClsAdoCommand, "classobject", CtxInproc, IidDispatch).Get("k4", "CommandTimeout");
                p.Activate("k5", ClsAdoStream, "classobject", CtxInproc, IidDispatch).Get("k5", "State");
                p.Modules("after-create").Files("after-close");
            });
        yield return new Probe(Card, "act-aggregate-ado-cocreate", ["R-ACT-1", "ACT-AGGREGATE", "R-ADO-1"],
            "ACT-AGGREGATE, ADO by CoCreateInstance: for each of ADODB.Connection, Recordset, Command and Stream, a non-null controlled outer with IID_IUnknown, then a non-null outer with IID_IDispatch. The outer records what it is asked for. Asks the HRESULT per class (CLASS_E_NOAGGREGATION, E_NOINTERFACE or success) and whether an aggregated object calls the outer.",
            p =>
            {
                p.Files("before-open");
                foreach (string prog in AdoProgIds)
                {
                    string tag = prog.Split('.')[1].ToLowerInvariant();
                    p.Activate(tag + "-u", prog, "cocreate", CtxInproc, IidUnknown, aggregate: true);
                    p.Activate(tag + "-d", prog, "cocreate", CtxInproc, IidDispatch, aggregate: true);
                }
                p.Files("after-close");
            });
        yield return new Probe(Card, "act-aggregate-classobject", ["R-ACT-1", "ACT-AGGREGATE", "ACT-FACTORY"],
            "ACT-AGGREGATE through the class factory: CoGetClassObject then CreateInstance with a non-null controlled outer and IID_IUnknown for DAO.DBEngine.120 and for ADODB.Connection, Recordset, Command and Stream; and with IID_IDispatch for the engine and the Connection. Asks which classes accept an outer through the factory (the smoke run gave E_UNEXPECTED 0x8000FFFF for the engine) and what the outer is asked.",
            p =>
            {
                p.Files("before-open");
                p.Activate("d-u", ClsDbEngine, "classobject", CtxInproc, IidUnknown, aggregate: true);
                p.Activate("d-d", ClsDbEngine, "classobject", CtxInproc, IidDispatch, aggregate: true);
                p.Activate("c-u", ClsAdoConnection, "classobject", CtxInproc, IidUnknown, aggregate: true);
                p.Activate("c-d", ClsAdoConnection, "classobject", CtxInproc, IidDispatch, aggregate: true);
                p.Activate("r-u", ClsAdoRecordset, "classobject", CtxInproc, IidUnknown, aggregate: true);
                p.Activate("m-u", ClsAdoCommand, "classobject", CtxInproc, IidUnknown, aggregate: true);
                p.Activate("s-u", ClsAdoStream, "classobject", CtxInproc, IidUnknown, aggregate: true);
                p.Files("after-close");
            });
        yield return new Probe(Card, "act-typecreate-ado-coclasses", ["R-ACT-1", "ACT-TYPECREATE", "R-ADO-1"],
            "ACT-TYPECREATE, ADO: from an ADODB.Connection's type information, ITypeInfo::CreateInstance of the coclasses Connection, Recordset, Command and Stream for IDispatch (State, Version or CommandTimeout read), and of Connection for IUnknown. Asks whether type-information creation reaches the same classes as CoCreateInstance and which interfaces it hands out.",
            p =>
            {
                p.Files("before-open");
                p.Create("ADODB.Connection", "cn").Modules("after-create");
                p.TypeCreate("t1", "cn", ClsAdoConnection, IidDispatch).Get("t1", "Version");
                p.TypeCreate("t2", "cn", ClsAdoRecordset, IidDispatch).Get("t2", "State");
                p.TypeCreate("t3", "cn", ClsAdoCommand, IidDispatch).Get("t3", "CommandTimeout");
                p.TypeCreate("t4", "cn", ClsAdoStream, IidDispatch).Get("t4", "State");
                p.TypeCreate("t5", "cn", ClsAdoConnection, IidUnknown);
                p.Files("after-close");
            });
        yield return new Probe(Card, "act-typecreate-dao-identity", ["R-ACT-1", "ACT-TYPECREATE", "ACT-THREAD", "ENGINE-IDENTITY"],
            "ACT-TYPECREATE, DAO: an engine created by the ordinary Create gets a Workspace 'rq2ws' appended; ITypeInfo::CreateInstance of the DBEngine coclass from its type information (IDispatch) gives t1, and again t2. Workspaces.Count and names are read through all three. Asks whether type-information creation returns the engine the process already has or a new one.",
            p =>
            {
                p.Files("before-open").Create().Modules("after-create");
                p.GetBind("wss", "eng", "Workspaces").CallBind("wsx", "eng", "CreateWorkspace", A.Str("rq2ws"), A.Str("admin"), A.Str(""))
                    .Call("wss", "Append", A.Obj("wsx")).Release("wsx").Release("wss");
                p.TypeCreate("t1", "eng", ClsDbEngine, IidDispatch).TypeCreate("t2", "eng", ClsDbEngine, IidDispatch);
                foreach (string e in new[] { "eng", "t1", "t2" })
                {
                    p.GetBind("wq", e, "Workspaces").Get("wq", "Count").Each("wq", "wi", 8, b => b.Get("wi", "Name")).Release("wq");
                }
                p.Files("after-close");
            });

        // ======================================================= ACT-RETAIN: enumerators outliving the parent
        yield return new Probe(Card, "retain-enum-dao-after-close", ["R-ACT-1", "ACT-RETAIN", "R-DAO-1", "DAO-ENUM"],
            "ACT-RETAIN, DAO enumerators: while the TableDefs enumerator (after Refresh) is mid-walk, Database.Close is called inside the loop body after the first item; the walk continues with Next, Next past the end, Reset, Next, Skip(1), Clone. The same for the Fields enumerator of T with Recordset.Close of a dynaset on T inside the body. Asks what an enumerator and its remaining items answer after the parent is closed (3420, S_FALSE or items).",
            p =>
            {
                string file = "scratch-q2-retain-enum.accdb";
                Scratch(p, file);
                p.Call("db", "Close").Release("db");
                p.CallBind("db", "eng", "OpenDatabase", A.Out(file), A.Bool(false), A.Bool(false));
                p.GetBind("tds", "db", "TableDefs").Call("tds", "Refresh")
                    .Enumerate("tds", "t", 8, b => b.Get("t", "Name").Call("db", "Close")).Release("tds");
                p.CallBind("db2", "eng", "OpenDatabase", A.Out(file), A.Bool(false), A.Bool(false))
                    .OpenRs("w", "db2", "T", Dao.OpenDynaset).GetBind("fs", "w", "Fields")
                    .Enumerate("fs", "f", 8, b => b.Get("f", "Name").Call("w", "Close")).Release("fs");
                p.Errors().Call("db2", "Close").Files("after-close");
            });
        yield return new Probe(Card, "retain-enum-ado-after-close", ["R-ACT-1", "ACT-RETAIN", "R-ADO-1"],
            "ACT-RETAIN, ADO enumerators: while the Fields enumerator of an ADO Recordset on T is mid-walk, Recordset.Close is called in the loop body after the first item; then, with a second Recordset, Connection.Close. The walk continues with Next, Next past the end, Reset, Next, Skip(1), Clone. Asks what an ADO enumerator and its remaining items answer after the parent is closed.",
            p =>
            {
                string file = "scratch-q2-retain-enum-ado.accdb";
                Scratch(p, file);
                p.Call("db", "Close").Release("db");
                OpenAdo(p, file);
                p.Create("ADODB.Recordset", "ar").Call("ar", "Open", A.Str("SELECT ID, N FROM T ORDER BY ID"), A.Obj("cn"), A.I4(3), A.I4(1), A.I4(1))
                    .GetBind("af", "ar", "Fields").Enumerate("af", "f", 8, b => b.Get("f", "Name").Call("ar", "Close")).Release("af").Release("ar");
                p.Create("ADODB.Recordset", "ar2").Call("ar2", "Open", A.Str("SELECT ID, N FROM T ORDER BY ID"), A.Obj("cn"), A.I4(3), A.I4(1), A.I4(1))
                    .GetBind("af2", "ar2", "Fields").Enumerate("af2", "f2", 8, b => b.Get("f2", "Name").Call("cn", "Close")).Release("af2").Release("ar2");
                p.Release("cn").Files("after-close");
            });

        // ======================================================= sink entry and callbacks (ACT-THREAD, OR-REENTRY)
        foreach ((string where, string? apartment) in new[] { ("main-sta", (string?)null), ("new-mta", "mta") })
        {
            string file = $"scratch-q2-events-{where}.accdb";
            yield return new Probe(Card, $"events-ado-connection-{where}", ["R-ACT-1", "ACT-THREAD", "MTA-CREATE", "R-ORACLE-1", "OR-REENTRY", "R-ADO-1", "ADO-EVENT"],
                $"ACT-THREAD sink entry and OR-REENTRY callbacks: a recording sink on an ADODB.Connection's ConnectionEvents, the connection created, advised and used on {(apartment is null ? "the main STA" : "a new MTA thread")}: Open, Execute of an UPDATE and of a failing statement, BeginTrans and CommitTrans, Close. Asks, per event, the order, the arguments, the thread of the callback against the thread of the call, and whether the original failing call's error survives the ExecuteComplete callback.",
                p =>
                {
                    Scratch(p, file);
                    p.Call("db", "Close").Release("db");
                    Action<P> body = q => q.Create("ADODB.Connection", "cn").Advise("cn", AdoConnectionEvents, "ev").Put("cn", "Provider", A.Str(Ace))
                        .GetBind("cnp", "cn", "Properties").Item("cds", "cnp", A.Str("Data Source")).Put("cds", "Value", A.Out(file)).Release("cds").Release("cnp")
                        .Call("cn", "Open").Call("cn", "Execute", A.Str("UPDATE T SET N = 'z'")).Call("cn", "Execute", A.Str("SELECT * FROM NoSuchTable"))
                        .GetBind("cerrs", "cn", "Errors").Get("cerrs", "Count").Release("cerrs")
                        .Call("cn", "BeginTrans").Call("cn", "CommitTrans").Call("cn", "Close").Unadvise("ev").Release("cn");
                    if (apartment is null)
                    {
                        body(p);
                    }
                    else
                    {
                        p.OnThread(apartment, body);
                    }
                    p.Files("after-close");
                });
        }
        foreach ((string where, string? apartment) in new[] { ("main-sta", (string?)null), ("new-mta", "mta") })
        {
            string file = $"scratch-q2-async-{where}.accdb";
            yield return new Probe(Card, $"events-ado-async-{where}", ["R-ACT-1", "ACT-THREAD", "OWNER-BUSY", "R-ORACLE-1", "OR-REENTRY", "R-ADO-1", "ADO-EVENT"],
                $"ACT-THREAD OWNER-BUSY approximation and OR-REENTRY: a recording sink on ConnectionEvents; on {(apartment is null ? "the main STA" : "a new MTA thread")} Connection.Execute(a 3-way cross join count, Options 17 = adCmdText | adAsyncExecute) returns, the state is read, the thread then waits 2 s (no pump on the STA) and reads the state and Errors again, then Close. Asks which thread the ExecuteComplete callback runs on, whether it arrives at all while the owner thread is not pumping, and what the connection state is meanwhile.",
                p =>
                {
                    Scratch(p, file);
                    p.Call("db", "Execute", A.Str("CREATE TABLE S (n LONG)"));
                    for (int i = 0; i < 40; i++)
                    {
                        p.Call("db", "Execute", A.Str($"INSERT INTO S VALUES ({i})"));
                    }
                    p.Call("db", "Close").Release("db");
                    Action<P> body = q => q.Create("ADODB.Connection", "cn").Advise("cn", AdoConnectionEvents, "ev").Put("cn", "Provider", A.Str(Ace))
                        .GetBind("cnp", "cn", "Properties").Item("cds", "cnp", A.Str("Data Source")).Put("cds", "Value", A.Out(file)).Release("cds").Release("cnp")
                        .Call("cn", "Open")
                        .Call("cn", "Execute", A.Str("SELECT COUNT(*) FROM S AS a, S AS b, S AS c"), A.Missing, A.I4(17)).Get("cn", "State")
                        .Pause(2000).Get("cn", "State")
                        .GetBind("cerrs", "cn", "Errors").Get("cerrs", "Count").Release("cerrs")
                        .Call("cn", "Close").Unadvise("ev").Release("cn");
                    if (apartment is null)
                    {
                        body(p);
                    }
                    else
                    {
                        p.OnThread(apartment, body);
                    }
                    p.Files("after-close");
                });
        }
        yield return new Probe(Card, "events-ado-recordset", ["R-ORACLE-1", "OR-REENTRY", "R-ADO-1", "ADO-EVENT"],
            "OR-REENTRY callbacks from a Recordset: a recording sink on RecordsetEvents of an ADO keyset optimistic Recordset on T (opened over a connection), then Open, MoveNext twice, an edit of N and Update, MovePrevious, and Close. Asks which RecordsetEvents ADO raises for each call, in which order and with which arguments, and whether the sink is called while the call is still in flight.",
            p =>
            {
                string file = "scratch-q2-events-rs.accdb";
                Scratch(p, file);
                p.Call("db", "Close").Release("db");
                OpenAdo(p, file);
                p.Create("ADODB.Recordset", "ar").Advise("ar", AdoRecordsetEvents, "evr")
                    .Call("ar", "Open", A.Str("T"), A.Obj("cn"), A.I4(1), A.I4(3), A.I4(2))
                    .Call("ar", "MoveNext").Call("ar", "MoveNext").Call("ar", "MovePrevious")
                    .GetBind("af", "ar", "Fields").Item("fn", "af", A.Str("N")).Put("fn", "Value", A.Str("edited")).Release("fn").Release("af")
                    .Call("ar", "Update").Call("ar", "Close").Unadvise("evr").Release("ar");
                p.FinishLite("cn");
            });

        // ======================================================= OR-OBS: the enumeration observer
        yield return new Probe(Card, "obs-errors-foreach-enumeration", ["R-ORACLE-1", "OR-OBS", "R-ERR-1", "ERR-SNAPSHOT"],
            "OR-OBS observer variant, collection sequence: after a failed DROP TABLE of a missing table, DBEngine.Errors is read by Count and Item(0) and then by _NewEnum/IEnumVARIANT (the For Each route of VBA and managed callers); a second failure (OpenRecordset of a missing table) is read by enumeration first and by Count after; a successful call follows and the collection is enumerated again. Asks whether enumerating the Errors collection changes or clears it, and whether the enumerated items equal the indexed ones.",
            p =>
            {
                Scratch(p, "scratch-q2-obs-enum.accdb");
                p.Call("db", "Execute", A.Str("DROP TABLE Rq2NoSuchMarker"), A.I4(Fail));
                p.GetBind("errs", "eng", "Errors").Get("errs", "Count").Item("e0", "errs", A.I4(0)).Get("e0", "Number").Release("e0");
                p.Enumerate("errs", "e", 8, b => b.Get("e", "Number").Get("e", "Description")).Get("errs", "Count").Release("errs");
                p.Call("db", "OpenRecordset", A.Str("Rq2NoSuchTable"), A.I4(Dao.OpenTable));
                p.GetBind("errs2", "eng", "Errors").Enumerate("errs2", "e", 8, b => b.Get("e", "Number")).Get("errs2", "Count").Release("errs2");
                p.OpenRs("ok", "db", "SELECT ID FROM T", Dao.OpenSnapshot).Call("ok", "Close").Release("ok");
                p.GetBind("errs3", "eng", "Errors").Enumerate("errs3", "e", 8, b => b.Get("e", "Number")).Get("errs3", "Count").Release("errs3");
                p.FinishLite("db");
            });

        // ======================================================= OR-FREEZE: a live writer frozen after a return
        yield return FreezeProbe("after-commit-return", "explicit transaction: BeginTrans on the default Workspace, INSERT INTO T SELECT 2048 rows of 2000 characters, CommitTrans returned",
            commit: true, closeFirst: false);
        yield return FreezeProbe("after-implicit-return", "implicit transaction: Database.Execute of INSERT INTO T SELECT 2048 rows of 2000 characters returned",
            commit: false, closeFirst: false);
        yield return FreezeProbe("after-close", "implicit transaction, then Database.Close returned (the normal Close boundary)",
            commit: false, closeFirst: true);
    }

    // ---------------------------------------------------------------------------------------------- helpers

    private static void Scratch(P p, string file)
    {
        p.EngineLite().CallBind("db", "eng", "CreateDatabase", A.Out(file), A.Str(Dao.LangGeneral), A.I4(Dao.Version120))
            .Call("db", "Execute", A.Str("CREATE TABLE T (ID LONG CONSTRAINT PK_T PRIMARY KEY, N TEXT(10))"))
            .Call("db", "Execute", A.Str("INSERT INTO T VALUES (1, 'a')")).Call("db", "Execute", A.Str("INSERT INTO T VALUES (2, 'b')"));
    }

    private static void OpenAdo(P p, string file) =>
        p.Create("ADODB.Connection", "cn").Put("cn", "Provider", A.Str(Ace))
            .GetBind("cnp", "cn", "Properties").Item("cds", "cnp", A.Str("Data Source")).Put("cds", "Value", A.Out(file))
            .Release("cds").Release("cnp").Call("cn", "Open");

    private static Probe FeffTable(string name, string ext, (string Tag, string Value)[] rows, bool withText, string description)
    {
        int version = ext == "mdb" ? Dao.Version40 : Dao.Version120;
        string file = $"scratch-q2-feff-{name}.{ext}";
        string ddl = withText
            ? "CREATE TABLE W (ID LONG CONSTRAINT PK_W PRIMARY KEY, T TEXT(255), M MEMO)"
            : "CREATE TABLE W (ID LONG CONSTRAINT PK_W PRIMARY KEY, M MEMO)";
        string daoSelect = withText
            ? "SELECT ID, T, M, Len(T) AS LT, Len(M) AS LM, AscW(Mid(T,1,1)) AS T1, AscW(Mid(T,2,1)) AS T2, AscW(Mid(T,3,1)) AS T3, AscW(Mid(M,1,1)) AS M1, AscW(Mid(M,2,1)) AS M2 FROM W ORDER BY ID"
            : "SELECT ID, M, Len(M) AS LM, AscW(Mid(M,1,1)) AS M1, AscW(Mid(M,2,1)) AS M2, AscW(Mid(M,3,1)) AS M3 FROM W ORDER BY ID";
        string adoSelect = withText ? "SELECT ID, T, M, Len(T) AS LT, Len(M) AS LM FROM W ORDER BY ID" : "SELECT ID, M, Len(M) AS LM FROM W ORDER BY ID";
        return new Probe(Card, "feff-" + name, ["R-STRING-1", "R-ORACLE-1", "OR-WIRE"],
            description + $" Rows: {string.Join(", ", rows.Select((r, i) => $"ID {i + 1} {r.Tag}"))}.",
            p =>
            {
                p.EngineLite().CallBind("db", "eng", "CreateDatabase", A.Out(file), A.Str(Dao.LangGeneral), A.I4(version)).Call("db", "Execute", A.Str(ddl))
                    .OpenRs("w", "db", "W", Dao.OpenDynaset).GetBind("wf", "w", "Fields");
                for (int i = 0; i < rows.Length; i++)
                {
                    p.Call("w", "AddNew").Item("fi", "wf", A.Str("ID")).Put("fi", "Value", A.I4(i + 1)).Release("fi");
                    if (withText)
                    {
                        p.Item("ft", "wf", A.Str("T")).Put("ft", "Value", A.Str(rows[i].Value)).Release("ft");
                    }
                    p.Item("fm", "wf", A.Str("M")).Put("fm", "Value", A.Str(rows[i].Value)).Release("fm").Call("w", "Update");
                }
                p.Release("wf").Call("w", "Close").Release("w").Errors().Call("db", "Close").Release("db");
                p.CallBind("db", "eng", "OpenDatabase", A.Out(file), A.Bool(false), A.Bool(true))
                    .OpenRs("rs", "db", daoSelect, Dao.OpenSnapshot).ReadAll("rs", 24).Call("rs", "Close").Release("rs").Errors().Call("db", "Close").Release("db");
                OpenAdo(p, file);
                p.Create("ADODB.Recordset", "ar").Call("ar", "Open", A.Str(adoSelect), A.Obj("cn"), A.I4(3), A.I4(1), A.I4(1))
                    .ReadAll("ar", 24).Call("ar", "Close").Release("ar");
                p.Call("cn", "Close").Release("cn").Files("after-close");
            });
    }

    /// <summary>
    /// OR-FREEZE: participant a writes 2,048 rows and signals after the named return; participant b suspends a at once,
    /// records the database and lock-file state three times while a is frozen (the third time with the files kept), then
    /// resumes a and records them again. Asks whether the on-disk image is stable while the writer is frozen after a
    /// return, and what changes after it resumes (the write-behind window), with no Close, flush or lock transition added.
    /// </summary>
    private static Probe FreezeProbe(string name, string boundary, bool commit, bool closeFirst)
    {
        string file = $"scratch-q2-freeze-{name}.accdb";
        return new Probe(Card, "freeze-" + name, ["R-ORACLE-1", "OR-FREEZE", "R-CRASH-1", "CRASH-CONTROL", "R-TXN-1", "TXN-FLUSH-LIVE"],
            $"OR-FREEZE: participant a creates {file} with S(16 rows) and T(ID, M MEMO), closes and reopens it shared, then runs {boundary}, and signals. Participant b suspends a 0 ms after the signal, records the database and lock files at once, after 1.5 s and after 4 s (the last two with the files kept), resumes a, waits 2.5 s and records them again. Asks whether the image a live writer leaves is stable while it is frozen at the {name} boundary and what changes after the resume, with no added Close, flush or lock transition.",
            p =>
            {
                p.EngineLite().CallBind("db", "eng", "CreateDatabase", A.Out(file), A.Str(Dao.LangGeneral), A.I4(Dao.Version120))
                    .Call("db", "Execute", A.Str("CREATE TABLE S (n LONG)"));
                for (int i = 0; i < 16; i++)
                {
                    p.Call("db", "Execute", A.Str($"INSERT INTO S VALUES ({i})"));
                }
                p.Call("db", "Execute", A.Str("CREATE TABLE T (ID LONG CONSTRAINT PK_T PRIMARY KEY, M MEMO)"))
                    .Call("db", "Close").Release("db")
                    .CallBind("db", "eng", "OpenDatabase", A.Out(file), A.Bool(false), A.Bool(false));
                const string insert = "INSERT INTO T (ID, M) SELECT a.n * 256 + b.n * 16 + c.n, String(2000, 'x') FROM S AS a, S AS b, S AS c WHERE a.n < 8";
                if (commit)
                {
                    p.GetBind("wss", "eng", "Workspaces").Item("ws", "wss", A.I4(0)).Call("ws", "BeginTrans")
                        .Call("db", "Execute", A.Str(insert)).Call("ws", "CommitTrans");
                }
                else
                {
                    p.Call("db", "Execute", A.Str(insert));
                }
                if (closeFirst)
                {
                    p.Call("db", "Close").Release("db");
                }
                p.Signal("returned").Await("b-done", 60000);
                if (!closeFirst)
                {
                    p.OpenRs("rc", "db", "SELECT COUNT(*) AS n FROM T", Dao.OpenSnapshot).ReadRow("rc").Call("rc", "Close").Release("rc").Call("db", "Close").Release("db");
                }
                p.Files("after-close");
            })
        {
            Participants =
            [
                ("b", q =>
                {
                    q.EngineLite().Await("returned", 60000).SuspendParticipant("a", 0)
                        .Files("frozen-0s").Pause(1500).Files("frozen-1.5s", keep: true).Pause(2500).Files("frozen-4s", keep: true)
                        .ResumeParticipant("a").Pause(2500).Files("resumed-2.5s", keep: true).Signal("b-done");
                }),
            ],
        };
    }
}
