namespace CalcDb.Probes.Cards;

/// <summary>
/// First use of the worker operations added for the troublesome rows of the qualification report: IEnumVARIANT
/// enumeration (R-DAO-1 DAO-ENUM), by-reference and named arguments (R-STRING-1 STR-BYREF, R-ERR-1 ERR-BIND), raw
/// activation and type-library creation (R-ACT-1 ACT-CLSID, ACT-FACTORY, ACT-AGGREGATE, ACT-TYPECREATE), a recording
/// event sink (R-ADO-1 ADO-EVENT) and suspending a participant (R-LOCK-3 READ-PATH, R-CRASH-1 CRASH-CONTROL). Each probe
/// asks one question of ACE through one operation. Scratch databases carry no card prefix.
/// </summary>
public sealed class RWorkerOps : ICard
{
    private const string Card = "r-ops";
    private const string Ace = "Microsoft.ACE.OLEDB.12.0";
    private const string IidDispatch = "{00020400-0000-0000-C000-000000000046}";
    private const string IidUnknown = "{00000000-0000-0000-C000-000000000046}";
    private const string DbEngineClsid = "{CD7791B9-43FD-42C5-AE42-8DD2811F0419}";
    private const string AdoConnectionEvents = "{00001400-0000-0010-8000-00AA006D2EA4}";

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

    public IEnumerable<Probe> Probes()
    {
        yield return new Probe(Card, "enum-dao-collections", ["R-DAO-1", "DAO-ENUM", "R-ERR-1", "ERR-SNAPSHOT"],
            "On a new ACCDB with table T (2 rows), enumerates through _NewEnum and IEnumVARIANT: DBEngine.Workspaces, Database.TableDefs after Refresh (reading each Name), the Fields of T (each Name), Database.Properties (each Name), and DBEngine.Errors after a failed DROP TABLE of a missing table (each Number). Each walk ends with Next past the end, Reset, Next, Skip(1) and Clone. Asks what _NewEnum returns for each DAO collection and how its enumerator answers.",
            p =>
            {
                Scratch(p, "scratch-enum.accdb");
                p.GetBind("wss", "eng", "Workspaces").Enumerate("wss", "w", 8, b => b.Get("w", "Name")).Release("wss");
                p.GetBind("tds", "db", "TableDefs").Call("tds", "Refresh").Enumerate("tds", "t", 32, b => b.Get("t", "Name")).Release("tds");
                p.GetBind("tds2", "db", "TableDefs").Item("td", "tds2", A.Str("T")).GetBind("fs", "td", "Fields").Enumerate("fs", "f", 8, b => b.Get("f", "Name")).Release("fs").Release("td").Release("tds2");
                p.GetBind("dps", "db", "Properties").Enumerate("dps", "pp", 40, b => b.Get("pp", "Name")).Release("dps");
                p.Call("db", "Execute", A.Str("DROP TABLE NoSuchTable")).GetBind("errs", "eng", "Errors").Enumerate("errs", "e", 8, b => b.Get("e", "Number")).Release("errs");
                p.FinishLite("db");
            });

        yield return new Probe(Card, "byref-ado-execute-records-affected", ["R-STRING-1", "STR-BYREF", "R-ADO-1", "ADO-RESULTS"],
            "On a new ACCDB with table T (2 rows), opens an ADO connection (Provider ACE OLE DB) and calls Connection.Execute(CommandText, RecordsAffected, Options) with RecordsAffected passed by reference: as VT_BYREF|VT_VARIANT holding Empty, as typed VT_BYREF|VT_I4 holding 0, and by value (I4 0). The statement is UPDATE T SET N = 'x', then UPDATE T SET N = 'y' WHERE ID = 99. Asks what ADO writes back into a by-reference RecordsAffected and what it does with a by-value one.",
            p =>
            {
                Scratch(p, "scratch-byref.accdb");
                p.Call("db", "Close").Release("db");
                OpenAdo(p, "scratch-byref.accdb");
                p.Call("cn", "Execute", A.Str("UPDATE T SET N = 'x'"), A.ByRef(A.Empty), A.I4(1));
                p.Call("cn", "Execute", A.Str("UPDATE T SET N = 'x'"), A.ByRef(A.I4(0), typed: true), A.I4(1));
                p.Call("cn", "Execute", A.Str("UPDATE T SET N = 'y' WHERE ID = 99"), A.ByRef(A.I4(-5), typed: true), A.I4(1));
                p.Call("cn", "Execute", A.Str("UPDATE T SET N = 'x'"), A.I4(0), A.I4(1));
                p.FinishLite("cn");
            });

        yield return new Probe(Card, "named-dao-openrecordset", ["R-ERR-1", "ERR-BIND", "R-DAO-1", "DAO-ID"],
            "On a new ACCDB with table T (2 rows), calls Database.OpenRecordset with named arguments resolved by one GetIDsOfNames: Name positional and Type:=4 named; Name:= and Type:= both named; Type:=4 and Options:=0 named in reverse order; a misspelled name Typ:=4; and the same name twice. Each opened recordset's RecordCount and Type are read. Asks which DISPIDs DAO's names have and how DAO binds and refuses named arguments.",
            p =>
            {
                Scratch(p, "scratch-named.accdb");
                p.CallNamed("r1", "db", "OpenRecordset", [A.Str("T")], ("Type", A.I4(4))).Get("r1", "Type").Get("r1", "RecordCount").Call("r1", "Close").Release("r1");
                p.CallNamed("r2", "db", "OpenRecordset", [], ("Name", A.Str("T")), ("Type", A.I4(2))).Get("r2", "Type").Call("r2", "Close").Release("r2");
                p.CallNamed("r3", "db", "OpenRecordset", [A.Str("T")], ("Options", A.I4(0)), ("Type", A.I4(4))).Get("r3", "Type").Call("r3", "Close").Release("r3");
                p.CallNamed("r4", "db", "OpenRecordset", [A.Str("T")], ("Typ", A.I4(4)));
                p.CallNamed("r5", "db", "OpenRecordset", [A.Str("T")], ("Type", A.I4(4)), ("Type", A.I4(2)));
                p.FinishLite("db");
            });

        yield return new Probe(Card, "activation-routes", ["R-ACT-1", "ACT-CLSID", "ACT-FACTORY", "ACT-AGGREGATE", "ACT-TYPECREATE"],
            "Activates DAO.DBEngine.120 by raw routes: CoCreateInstance with CLSCTX_INPROC_SERVER for IDispatch and for IUnknown; with CLSCTX_LOCAL_SERVER; with CLSCTX_INPROC_SERVER and a controlled outer IUnknown for IUnknown and for IDispatch (aggregation); CoGetClassObject for IClassFactory with LockServer and CreateInstance, plain and aggregated; and ITypeInfo::CreateInstance of the DBEngine coclass found in an engine's type library. Each object that answers IDispatch reads Version. Asks how DAO's class answers each activation route, whether it can be aggregated, and what an outer is asked for.",
            p =>
            {
                p.Files("before-open");
                p.Activate("e1", "DAO.DBEngine.120", "cocreate", 1, IidDispatch).Get("e1", "Version");
                p.Activate("e2", "DAO.DBEngine.120", "cocreate", 1, IidUnknown).Get("e2", "Version");
                p.Activate("e3", "DAO.DBEngine.120", "cocreate", 4, IidDispatch);
                p.Activate("e4", "DAO.DBEngine.120", "cocreate", 1, IidUnknown, aggregate: true);
                p.Activate("e5", "DAO.DBEngine.120", "cocreate", 1, IidDispatch, aggregate: true);
                p.Activate("e6", DbEngineClsid, "classobject", 1, IidDispatch).Get("e6", "Version");
                p.Activate("e7", DbEngineClsid, "classobject", 1, IidUnknown, aggregate: true);
                p.Modules("after-create");
                p.TypeCreate("e8", "e1", DbEngineClsid, IidDispatch).Get("e8", "Version");
                p.Files("after-close");
            });

        yield return new Probe(Card, "ado-connection-events", ["R-ADO-1", "ADO-EVENT", "ADO-REENTRY"],
            "On a new ACCDB with table T (2 rows), connects the worker's recording sink to an ADODB.Connection's ConnectionEvents connection point before Provider and Data Source are set, then opens the connection, executes UPDATE T SET N = 'z' and a failing statement (SELECT * FROM NoSuchTable), begins and commits a transaction, closes, and disconnects. Asks which ConnectionEvents ADO raises for each call, in which order, with which arguments and on which thread.",
            p =>
            {
                Scratch(p, "scratch-events.accdb");
                p.Call("db", "Close").Release("db");
                p.Create("ADODB.Connection", "cn").Advise("cn", AdoConnectionEvents, "ev").Put("cn", "Provider", A.Str(Ace))
                    .GetBind("cnp", "cn", "Properties").Item("cds", "cnp", A.Str("Data Source")).Put("cds", "Value", A.Out("scratch-events.accdb"))
                    .Release("cds").Release("cnp").Call("cn", "Open");
                p.Call("cn", "Execute", A.Str("UPDATE T SET N = 'z'"));
                p.Call("cn", "Execute", A.Str("SELECT * FROM NoSuchTable"));
                p.Call("cn", "BeginTrans").Call("cn", "CommitTrans");
                p.Call("cn", "Close").Unadvise("ev");
                p.Files("after-close");
            });

        yield return new Probe(Card, "suspend-writer-mid-insert", ["R-LOCK-3", "READ-PATH", "R-CRASH-1", "CRASH-CONTROL"],
            "Participant a creates an ACCDB with table T (ID LONG primary key, M MEMO), signals, then runs INSERT INTO T SELECT for 2048 rows with 2000-character memos. Participant b, 30 ms after a's signal, suspends a; while a is suspended it observes the database and lock files, tries a DAO shared read-only open of the database and reads COUNT(*), then resumes a. a reports its RecordsAffected and the final count. Asks what another process sees of a database and its lock file while a writer is frozen inside a statement.",
            p =>
            {
                p.EngineLite().CallBind("db", "eng", "CreateDatabase", A.Out("scratch-suspend.accdb"), A.Str(Dao.LangGeneral), A.I4(Dao.Version120))
                    .Call("db", "Execute", A.Str("CREATE TABLE S (n LONG)"));
                for (int i = 0; i < 16; i++)
                {
                    p.Call("db", "Execute", A.Str($"INSERT INTO S VALUES ({i})"));
                }
                p.Call("db", "Execute", A.Str("CREATE TABLE T (ID LONG CONSTRAINT PK_T PRIMARY KEY, M MEMO)"))
                    .Signal("ready").Await("b-opened", 30000)
                    .Call("db", "Execute", A.Str("INSERT INTO T (ID, M) SELECT a.n * 256 + b.n * 16 + c.n, String(2000, 'x') FROM S AS a, S AS b, S AS c WHERE a.n < 8"))
                    .Signal("inserted").Await("b-done", 60000)
                    .OpenRs("rc", "db", "SELECT COUNT(*) AS n FROM T", Dao.OpenSnapshot).ReadRow("rc").Call("rc", "Close").Release("rc");
                p.FinishLite("db");
            })
        {
            Participants =
            [
                ("b", p =>
                {
                    p.EngineLite().Await("ready", 30000).Signal("b-opened").SuspendParticipant("a", 30)
                        .Files("while-suspended")
                        .CallBind("dbb", "eng", "OpenDatabase", A.Out("scratch-suspend.accdb"), A.Bool(false), A.Bool(true))
                        .OpenRs("rb", "dbb", "SELECT COUNT(*) AS n FROM T", Dao.OpenSnapshot).ReadRow("rb").Call("rb", "Close").Release("rb")
                        .Call("dbb", "Close").Release("dbb")
                        .Pause(500).Files("still-suspended").ResumeParticipant("a")
                        .Await("inserted", 60000).Files("after-resume").Signal("b-done");
                }),
            ],
        };
    }
}
