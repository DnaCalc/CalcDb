using System.Text;

namespace CalcDb.Probes.Cards;

/// <summary>
/// The owned Access host (R-UDF-1, R-EXPR-1 EXPR-SANDBOX / EXPR-DOMAIN / EXPR-EVAL, R-ORACLE-1 OR-OWN): Access.Application
/// activated as a local server, claimed by the window handle of <c>hWndAccessApp</c> so the runner owns, guards and
/// ends it, then driven through raw IDispatch. Each probe makes its own database with <c>NewCurrentDatabase</c> in the
/// attempt folder. <c>AutomationSecurity</c> is set to low on this Access instance only (an instance property, not Trust
/// Center or any machine setting), because the only code it runs is CalcDb-generated fixture VBA (the macro policy of
/// oracle-service.md). The standalone-DAO contrast reopens the same file after Access has quit (UDF-CONTEXT).
/// Until the owner moves it, the card runs on the main desktop: <c>calcdb-oracle run --desktop main</c>.
/// </summary>
public sealed class RAccessHost : ICard
{
    private const string Card = "r-access-host";
    private const string IidDispatch = "{00020400-0000-0000-C000-000000000046}";
    private const uint ClsCtxLocalServer = 4;
    private const int AcModule = 5;
    private const int AcQuitSaveNone = 2;
    private const int AutomationSecurityLow = 1;

    /// <summary>
    /// The generated fixture module (R-UDF-1 Fixture): a typed echo, a type reporter, optional parameters, a
    /// trace-and-return function with an in-memory trace, an error-after-trace function, and a Double-typed parameter for
    /// coercion. No file, registry, network or object creation.
    /// </summary>
    internal const string Module = """
        Option Compare Database
        Option Explicit

        Private mTrace As String
        Private mCount As Long

        Public Function CalcDbEcho(ByVal x As Variant) As Variant
            CalcDbEcho = x
        End Function

        Public Function CalcDbTypeOf(ByVal x As Variant) As String
            CalcDbTypeOf = TypeName(x) & "/" & CStr(VarType(x))
        End Function

        Public Function CalcDbOpt(Optional a As Variant, Optional ByVal b As Long = 7) As String
            CalcDbOpt = CStr(IsMissing(a)) & "|" & TypeName(a) & "|" & CStr(b)
        End Function

        Public Function CalcDbTrace(ByVal x As Variant) As Variant
            mCount = mCount + 1
            mTrace = mTrace & CStr(mCount) & ":" & TypeName(x) & "=" & CStr(Nz(x, "Null")) & ";"
            CalcDbTrace = x
        End Function

        Public Function CalcDbTraceRead() As String
            CalcDbTraceRead = mTrace
        End Function

        Public Function CalcDbTraceReset() As Long
            CalcDbTraceReset = mCount
            mTrace = ""
            mCount = 0
        End Function

        Public Function CalcDbFailAt(ByVal x As Variant, ByVal at As Long) As Variant
            CalcDbTrace x
            If x = at Then Err.Raise vbObjectError + 513, "CalcDbGen", "CalcDb fixture error at " & CStr(at)
            CalcDbFailAt = x
        End Function

        Public Function CalcDbDouble(ByVal x As Double) As Double
            CalcDbDouble = x * 2
        End Function

        """;

    /// <summary>
    /// Activate Access as a local server, claim its process, and lower macro security on this instance only; with
    /// <paramref name="visible"/>, show its window (main-desktop runs only).
    /// </summary>
    internal static P Host(P p, bool visible = false)
    {
        p.Files("before-open").Modules("before-activate")
            .Activate("app", "Access.Application", "cocreate", ClsCtxLocalServer, IidDispatch)
            .Claim("app", "hWndAccessApp", "MSACCESS.EXE")
            .Get("app", "Version").Get("app", "Build").Get("app", "Visible").Get("app", "UserControl")
            .Get("app", "AutomationSecurity");
        p.Put("app", "AutomationSecurity", A.I4(AutomationSecurityLow)).Get("app", "AutomationSecurity");
        return visible ? p.Put("app", "Visible", A.Bool(true)).Get("app", "Visible") : p;
    }

    /// <summary>Write a generated module to the attempt folder and load it as standard module CalcDbGen.</summary>
    internal static P LoadModule(P p, string module) =>
        p.WriteFile("work:CalcDbGen.bas", Encoding.ASCII.GetBytes(module.ReplaceLineEndings("\r\n")))
            .Call("app", "LoadFromText", A.I4(AcModule), A.Str("CalcDbGen"), A.Out("CalcDbGen.bas"));

    /// <summary>A new database in the attempt folder as Access's current database, with T (ID, X Double, S Text, N Long) and three rows.</summary>
    internal static P Database(P p, string file) =>
        p.Call("app", "NewCurrentDatabase", A.Out(file)).CallBind("db", "app", "CurrentDb")
            .Call("db", "Execute", A.Str("CREATE TABLE T (ID LONG CONSTRAINT PK_T PRIMARY KEY, X DOUBLE, S TEXT(20), N LONG)"))
            .Call("db", "Execute", A.Str("INSERT INTO T VALUES (1, 1.5, 'a', Null)"))
            .Call("db", "Execute", A.Str("INSERT INTO T VALUES (2, 2.5, 'b', 7)"))
            .Call("db", "Execute", A.Str("INSERT INTO T VALUES (3, -0.5, Null, 3)"));

    /// <summary>Close the current database, quit without saving, release, and give the server time to end.</summary>
    internal static P Quit(P p) =>
        p.Call("db", "Close").Release("db").Call("app", "CloseCurrentDatabase").Call("app", "Quit", A.I4(AcQuitSaveNone)).Release("app").Pause(3000);

    internal static P Query(P p, string db, string sql) =>
        p.OpenRs("rs", db, sql, Dao.OpenSnapshot).ReadAll("rs").Call("rs", "Close").Release("rs");

    /// <summary>After Access quit: open the same file with standalone DAO and run the same SQL (UDF-CONTEXT).</summary>
    private static P Standalone(P p, string file, params string[] sqls)
    {
        p.Create().Modules("after-create").CallBind("sdb", "eng", "OpenDatabase", A.Out(file), A.Bool(false), A.Bool(true));
        foreach (string sql in sqls)
        {
            Query(p, "sdb", sql).Errors();
        }
        return p.Call("sdb", "Close").Release("sdb").FinishLite();
    }

    private static Probe Make(string name, string[] refs, string what, Action<P> program) =>
        new(Card, name, ["R-ORACLE-1", "OR-OWN", .. refs], "Owned Access host (Access.Application, local server, claimed by hWndAccessApp; AutomationSecurity low on this instance; generated fixture code only): " + what, program);

    public IEnumerable<Probe> Probes()
    {
        yield return Make("launch-quit", ["R-UDF-1", "UDF-CONTEXT", "R-ACT-1"],
            "activate, claim, read Version, Build, Visible, UserControl and AutomationSecurity before and after setting it, the host's DBEngine.Version, then Quit(acQuitSaveNone). Asks the host identity, its default visibility and control state, and whether it ends after Quit and Release.",
            p => Host(p).GetBind("aeng", "app", "DBEngine").Get("aeng", "Version").Release("aeng")
                .Call("app", "Quit", A.I4(AcQuitSaveNone)).Release("app").Pause(3000).Modules("end"));

        string[] contextSql =
        [
            "SELECT ID, Nz(N, -1) AS a, DLookup(\"S\", \"T\", \"ID = \" & ID) AS b FROM T ORDER BY ID",
            "SELECT Len(Environ(\"TEMP\")) > 0 AS e, Len(CurDir()) > 0 AS c FROM T WHERE ID = 1",
        ];
        yield return Make("eval-context", ["R-EXPR-1", "EXPR-SANDBOX", "EXPR-DOMAIN", "EXPR-EVAL", "R-UDF-1", "UDF-CONTEXT"],
            "a new current database with T; Application.Eval of constants, Nz, domain functions (DLookup, DCount, DSum), CDec, nested Eval, a division by zero, a failed conversion, an unknown function and the sandbox-listed Environ and CurDir (reduced to Booleans); the same Nz/DLookup and Environ/CurDir SQL through CurrentDb; then, after Quit, the same SQL through standalone DAO on the same file. Asks which functions each context admits, the exact values and subtypes, and the error channels of each refusal.",
            p =>
            {
                Database(Host(p), "host-eval.accdb");
                foreach (string expression in new[]
                {
                    "1+1", "Nz(Null, 5)", "DLookup(\"X\", \"T\", \"ID = 2\")", "DCount(\"*\", \"T\")", "DSum(\"N\", \"T\")",
                    "DLookup(\"X\", \"T\", \"ID = 99\")", "CDec(\"1.5\")", "Eval(\"2*3\")", "1/0", "CLng(\"x\")", "NoSuchFunction(1)",
                    "Len(Environ(\"TEMP\")) > 0", "Len(CurDir()) > 0",
                })
                {
                    p.Call("app", "Eval", A.Str(expression));
                }
                foreach (string sql in contextSql)
                {
                    Query(p, "db", sql);
                }
                Standalone(Quit(p), "host-eval.accdb", contextSql);
            });

        string[] udfSql =
        [
            "SELECT ID, CalcDbTrace(X) AS t, CalcDbTypeOf(N) AS ty, CalcDbEcho(S) AS e FROM T ORDER BY ID",
            "SELECT ID, CalcDbTrace(1) AS c FROM T ORDER BY ID",
            "SELECT ID FROM T WHERE CalcDbTrace(ID) > 1 ORDER BY ID",
        ];
        yield return Make("generated-module", ["R-UDF-1", "UDF-NAME", "UDF-SIGNATURE", "UDF-CONTEXT", "R-EXPR-1", "EXPR-EVAL"],
            "a new current database with T; a generated standard module CalcDbGen loaded by LoadFromText(acModule); Application.Run and Application.Eval of its functions with omitted, Null, Empty and typed arguments, a qualified name, a missing name and coercion failures into a Double parameter; three CurrentDb queries calling them (row-dependent, constant and in WHERE), each followed by the in-memory trace; then, after Quit, the first query through standalone DAO. Asks name resolution, argument mapping and conversion, how often and in which order a query calls a host function, and what standalone DAO says.",
            p =>
            {
                Database(Host(p), "host-udf.accdb")
                    .WriteFile("work:CalcDbGen.bas", Encoding.ASCII.GetBytes(Module.ReplaceLineEndings("\r\n")))
                    .Call("app", "LoadFromText", A.I4(AcModule), A.Str("CalcDbGen"), A.Out("CalcDbGen.bas"))
                    .Call("app", "Run", A.Str("CalcDbEcho"), A.I4(21))
                    .Call("app", "Run", A.Str("CalcDbOpt"))
                    .Call("app", "Run", A.Str("CalcDbOpt"), A.Null)
                    .Call("app", "Run", A.Str("CalcDbOpt"), A.Empty, A.I4(3))
                    .Call("app", "Run", A.Str("CalcDbTypeOf"), A.Str("x"))
                    .Call("app", "Run", A.Str("CalcDbDouble"), A.Str("2"))
                    .Call("app", "Run", A.Str("CalcDbDouble"), A.Str("abc"))
                    .Call("app", "Run", A.Str("CalcDbMissing"), A.I4(1));
                foreach (string expression in new[]
                {
                    "CalcDbEcho(42)", "CalcDbTypeOf(1.5)", "CalcDbTypeOf(Null)", "CalcDbOpt()", "CalcDbOpt(Null)", "CalcDbOpt(1, 2)",
                    "CalcDbGen.CalcDbEcho(5)", "CalcDbDouble(\"2\")", "CalcDbDouble(\"abc\")", "CalcDbMissing(1)",
                })
                {
                    p.Call("app", "Eval", A.Str(expression));
                }
                foreach (string sql in udfSql)
                {
                    p.Call("app", "Run", A.Str("CalcDbTraceReset"));
                    Query(p, "db", sql).Call("app", "Eval", A.Str("CalcDbTraceRead()"));
                }
                Standalone(Quit(p), "host-udf.accdb", udfSql[0]);
            });

        yield return Make("udf-error", ["R-UDF-1", "UDF-ERROR", "UDF-CONTEXT"],
            "a new current database with T and the generated module; CalcDbFailAt raises vbObjectError + 513 after tracing when its argument is 2: through Application.Run, Application.Eval and a CurrentDb query over three rows, then the trace and the host DBEngine.Errors. Asks the error channels per route, whether the query stops at the failing row, and which calls happened before it. A VBA error dialog, if Access shows one, is recorded by the window guard.",
            p =>
            {
                Database(Host(p), "host-err.accdb")
                    .WriteFile("work:CalcDbGen.bas", Encoding.ASCII.GetBytes(Module.ReplaceLineEndings("\r\n")))
                    .Call("app", "LoadFromText", A.I4(AcModule), A.Str("CalcDbGen"), A.Out("CalcDbGen.bas"))
                    .Call("app", "Run", A.Str("CalcDbFailAt"), A.I4(2), A.I4(2))
                    .Call("app", "Eval", A.Str("CalcDbFailAt(2, 2)"))
                    .Call("app", "Eval", A.Str("CalcDbTraceRead()"))
                    .Call("app", "Run", A.Str("CalcDbTraceReset"));
                Query(p, "db", "SELECT ID, CalcDbFailAt(ID, 2) AS f FROM T ORDER BY ID")
                    .Call("app", "Eval", A.Str("CalcDbTraceRead()"))
                    .GetBind("eng", "app", "DBEngine").Errors().Release("eng");
                Quit(p).FinishLite();
            });

        // The udf-error record shows that Application.Run of a raising function opens the modal VBA run-time error
        // dialog and blocks the caller; the other routes are asked separately so that dialog cannot hide them.
        foreach ((string route, string what, Action<P> ask) in new (string, string, Action<P>)[]
        {
            ("eval", "Application.Eval(\"CalcDbFailAt(2, 2)\"), then Eval of CalcDbTraceRead()",
                p => p.Call("app", "Eval", A.Str("CalcDbFailAt(2, 2)")).Call("app", "Eval", A.Str("CalcDbTraceRead()"))),
            ("query", "a CurrentDb snapshot of SELECT ID, CalcDbFailAt(ID, 2) FROM T ORDER BY ID read to the end, then the trace and the host DBEngine.Errors",
                p => Query(p, "db", "SELECT ID, CalcDbFailAt(ID, 2) AS f FROM T ORDER BY ID").Call("app", "Eval", A.Str("CalcDbTraceRead()"))
                    .GetBind("eng", "app", "DBEngine").Errors().Release("eng")),
        })
        {
            yield return Make($"udf-error-{route}", ["R-UDF-1", "UDF-ERROR", "UDF-CONTEXT"],
                $"a new current database with T and the generated module; CalcDbFailAt raises vbObjectError + 513 after tracing when its argument is 2; {what}. Asks this route's error channels, whether it shows the VBA error dialog the Run route shows, and which calls happened before the error.",
                p =>
                {
                    Database(Host(p), $"host-err-{route}.accdb")
                        .WriteFile("work:CalcDbGen.bas", Encoding.ASCII.GetBytes(Module.ReplaceLineEndings("\r\n")))
                        .Call("app", "LoadFromText", A.I4(AcModule), A.Str("CalcDbGen"), A.Out("CalcDbGen.bas"));
                    ask(p);
                    Quit(p).FinishLite();
                });
        }

        // EXPR-SANDBOX control: Environ and CurDir are refused by name in Eval and in CurrentDb SQL (eval-context). The
        // same functions called inside generated VBA show whether they exist in this host at all, so the refusal is
        // the expression service's, not a missing function.
        yield return Make("sandbox-control", ["R-EXPR-1", "EXPR-SANDBOX", "R-UDF-1", "UDF-CONTEXT"],
            "a new current database with T and a generated module whose CalcDbEnvOk() returns Len(Environ(\"TEMP\")) > 0 and CalcDbDirOk() Len(CurDir()) > 0; Application.Run and Application.Eval of both, and both in a CurrentDb query. Asks whether the functions the expression service refuses by name run when VBA calls them, through each route.",
            p =>
            {
                string module = Module + """

                    Public Function CalcDbEnvOk() As Boolean
                        CalcDbEnvOk = Len(Environ("TEMP")) > 0
                    End Function

                    Public Function CalcDbDirOk() As Boolean
                        CalcDbDirOk = Len(CurDir()) > 0
                    End Function

                    """;
                Database(Host(p), "host-sandbox.accdb")
                    .WriteFile("work:CalcDbGen.bas", Encoding.ASCII.GetBytes(module.ReplaceLineEndings("\r\n")))
                    .Call("app", "LoadFromText", A.I4(AcModule), A.Str("CalcDbGen"), A.Out("CalcDbGen.bas"))
                    .Call("app", "Run", A.Str("CalcDbEnvOk")).Call("app", "Run", A.Str("CalcDbDirOk"))
                    .Call("app", "Eval", A.Str("CalcDbEnvOk()")).Call("app", "Eval", A.Str("CalcDbDirOk()"));
                Query(p, "db", "SELECT ID, CalcDbEnvOk() AS e, CalcDbDirOk() AS c FROM T WHERE ID = 1");
                Quit(p).FinishLite();
            });
    }
}
