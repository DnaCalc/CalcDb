namespace CalcDb.Probes.Cards;

/// <summary>
/// R-ORACLE-1 OR-DESKTOP dialog handling with the owned Access host (R-UDF-1 UDF-ERROR, R-EXPR-1 EXPR-EVAL): VBA shows
/// a modal dialog whenever generated code raises an unhandled error, fails to compile or calls MsgBox, and no caller
/// option prevents it. The runner describes every dialog of an owned process by UI Automation and answers it only by
/// the question's dialog policy, invoking a button with no mouse, keyboard or focus. The repro probes have no policy:
/// they record what each dialog is. Access is visible; main desktop only (<c>run --desktop main</c>).
/// </summary>
public sealed class RAccessDialogs : ICard
{
    private const string Card = "r-access-dialog";

    private const string Raising = """
        Option Compare Database
        Option Explicit

        Public Function CalcDbRaise(ByVal x As Long) As Long
            If x = 2 Then Err.Raise vbObjectError + 513, "CalcDbGen", "CalcDb fixture error at 2"
            CalcDbRaise = x
        End Function

        """;

    private const string SyntaxError = """
        Option Compare Database
        Option Explicit

        Public Function CalcDbBroken() As Long
            CalcDbBroken = = 1
        End Function

        """;

    private const string Undeclared = """
        Option Compare Database
        Option Explicit

        Public Function CalcDbUndeclared() As Long
            CalcDbUndeclared = y + 1
        End Function

        """;

    private const string Message = """
        Option Compare Database
        Option Explicit

        Public Function CalcDbSay() As Long
            CalcDbSay = MsgBox("CalcDb fixture message", vbOKCancel, "CalcDb fixture")
        End Function

        """;

    private static Probe Make(string name, string[] refs, string what, string module, Action<P> ask, DialogRule[] dialogs) =>
        new(Card, name, ["R-ORACLE-1", "OR-DESKTOP", "OR-OWN", .. refs],
            $"Owned Access host, visible, main desktop; a new current database and a generated module; {what} Every dialog of the host is described by UI Automation{(dialogs.Length == 0 ? "; no dialog policy, so a dialog stays until the deadline." : "; the policy answers it.")}",
            p =>
            {
                RAccessHost.Database(RAccessHost.Host(p, visible: true), $"dialog-{name}.accdb");
                RAccessHost.LoadModule(p, module);
                ask(p);
                RAccessHost.Quit(p).FinishLite();
            })
        { Dialogs = dialogs };

    public IEnumerable<Probe> Probes()
    {
        // Reproduce: what each dialog is, unanswered.
        yield return Make("repro-runtime-run", ["R-UDF-1", "UDF-ERROR"], "Application.Run(\"CalcDbRaise\", 2), which raises vbObjectError + 513.", Raising,
            p => p.Call("app", "Run", A.Str("CalcDbRaise"), A.I4(2)), []);
        yield return Make("repro-syntax-eval", ["R-UDF-1", "UDF-ERROR", "R-EXPR-1", "EXPR-EVAL"], "a module with a syntax error (CalcDbBroken = = 1), then Application.Eval(\"CalcDbBroken()\").", SyntaxError,
            p => p.Call("app", "Eval", A.Str("CalcDbBroken()")), []);
        yield return Make("repro-undeclared-eval", ["R-UDF-1", "UDF-ERROR", "R-EXPR-1", "EXPR-EVAL"], "a module using an undeclared variable under Option Explicit, then Application.Eval(\"CalcDbUndeclared()\").", Undeclared,
            p => p.Call("app", "Eval", A.Str("CalcDbUndeclared()")), []);
        yield return Make("repro-msgbox-run", ["R-UDF-1", "UDF-ENTRY"], "Application.Run(\"CalcDbSay\"), which calls MsgBox with OK and Cancel.", Message,
            p => p.Call("app", "Run", A.Str("CalcDbSay")), []);

        // Answered: the run-time error dialog (repro-runtime-run: Continue disabled, End, Debug, Help) is ended with End,
        // so the route returns to the caller. Each probe then evaluates 1+1 to show whether the host is still usable.
        DialogRule[] end = [new DialogRule("Microsoft Visual Basic", "End") { Text = "Run-time error" }];
        yield return Make("runtime-run-end", ["R-UDF-1", "UDF-ERROR"], "Application.Run(\"CalcDbRaise\", 2), the run-time error dialog answered End; then Run(\"CalcDbRaise\", 1) and Eval(\"1+1\"). Asks what error Run returns once the dialog is ended, and whether the module still runs.", Raising,
            p => p.Call("app", "Run", A.Str("CalcDbRaise"), A.I4(2)).Call("app", "Run", A.Str("CalcDbRaise"), A.I4(1)).Call("app", "Eval", A.Str("1+1")), end);
        yield return Make("runtime-eval-end", ["R-UDF-1", "UDF-ERROR", "R-EXPR-1", "EXPR-EVAL"], "Application.Eval(\"CalcDbRaise(2)\"), the dialog answered End; then Eval(\"CalcDbRaise(1)\"). Asks the Eval route's error once the dialog is ended.", Raising,
            p => p.Call("app", "Eval", A.Str("CalcDbRaise(2)")).Call("app", "Eval", A.Str("CalcDbRaise(1)")), end);
        yield return Make("runtime-query-end", ["R-UDF-1", "UDF-ERROR", "UDF-CONTEXT"], "a CurrentDb snapshot of SELECT ID, CalcDbRaise(ID) FROM T read to the end, the dialog answered End at each failing row; then the host DBEngine.Errors. Asks what the field read returns once the dialog is ended, and whether the scan goes on.", Raising,
            p => RAccessHost.Query(p, "db", "SELECT ID, CalcDbRaise(ID) AS r FROM T ORDER BY ID").GetBind("eng", "app", "DBEngine").Errors().Release("eng"), end);
        // Compile errors (repro-syntax-eval, repro-undeclared-eval) show "Microsoft Visual Basic for Applications" with
        // "Compile error:" and the reason, buttons OK and Help. After OK, VBA opens its editor in break mode (title
        // "... [break] - [CalcDbGen (Code)]") and the call stays blocked: the editor's Reset button ends it.
        DialogRule[] ok =
        [
            new DialogRule("Microsoft Visual Basic for Applications", "OK") { Text = "Compile error" },
            new DialogRule("[break]", "Reset"),
        ];
        yield return Make("syntax-eval-ok", ["R-UDF-1", "UDF-ERROR", "UDF-NAME", "R-EXPR-1", "EXPR-EVAL"], "a module with a syntax error, then Application.Eval(\"CalcDbBroken()\") with the compile-error dialog answered OK; then Eval(\"CalcDbBroken()\") again and Eval(\"1+1\"). Asks the error Eval returns for a function that does not compile, and whether the second call shows the dialog again.", SyntaxError,
            p => p.Call("app", "Eval", A.Str("CalcDbBroken()")).Call("app", "Eval", A.Str("CalcDbBroken()")).Call("app", "Eval", A.Str("1+1")), ok);
        yield return Make("undeclared-run-ok", ["R-UDF-1", "UDF-ERROR", "UDF-NAME"], "a module using an undeclared variable under Option Explicit, then Application.Run(\"CalcDbUndeclared\") with the compile-error dialog answered OK. Asks the error Run returns for a module that does not compile.", Undeclared,
            p => p.Call("app", "Run", A.Str("CalcDbUndeclared")).Call("app", "Eval", A.Str("1+1")), ok);
        yield return Make("msgbox-run-cancel", ["R-UDF-1", "UDF-ENTRY"], "Application.Run(\"CalcDbSay\"), its MsgBox answered Cancel. Asks that MsgBox returns vbCancel (2) through Run when the runner answers it.", Message,
            p => p.Call("app", "Run", A.Str("CalcDbSay")), [new DialogRule("CalcDb fixture", "Cancel") { Text = "CalcDb fixture message" }]);
    }
}
