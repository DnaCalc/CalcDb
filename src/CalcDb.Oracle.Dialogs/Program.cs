using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows.Automation;
using CalcDb.Evidence;

namespace CalcDb.Oracle.Dialogs;

/// <summary>
/// R-ORACLE-1 OR-DESKTOP dialog handling: <c>calcdb-dialogs HWND PID POLICY|- OUT</c>. Describes the dialog window HWND
/// (hex) of the owned process PID by UI Automation: its title, class and every element of its raw tree, in order. When
/// a rule of the policy file matches (title, and an element's name when the rule names a text), it invokes the rule's
/// button through its InvokePattern: no mouse, no keyboard, no focus change, no foreground window. It then waits up to
/// 5 s for the window to close. The observation is written to OUT as JSON. Exit 0 when OUT was written.
/// </summary>
internal static partial class Program
{
    private const int MaxElements = 300;
    private const int MaxDepth = 8;

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length != 4)
        {
            Console.Error.WriteLine("usage: calcdb-dialogs HWND(hex) PID POLICY.json|- OUT.json");
            return 2;
        }
        nint hwnd = (nint)long.Parse(args[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        int pid = int.Parse(args[1], CultureInfo.InvariantCulture);
        List<DialogRule> rules = args[2] == "-" ? [] : EvidenceJson.Read<List<DialogRule>>(args[2]);
        DateTimeOffset seen = DateTimeOffset.UtcNow;
        DialogObservation observation;
        try
        {
            observation = Handle(hwnd, pid, rules, seen);
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or COMException or ArgumentException)
        {
            observation = new DialogObservation
            {
                Window = "0x" + ((long)hwnd).ToString("X", CultureInfo.InvariantCulture),
                ProcessId = pid,
                Title = "",
                ClassName = "",
                Elements = [],
                Outcome = "not described: " + ex.GetType().Name + ": " + ex.Message,
                SeenUtc = seen,
            };
        }
        System.IO.File.WriteAllText(args[3], EvidenceJson.Serialize(observation));
        return 0;
    }

    private static DialogObservation Handle(nint hwnd, int pid, List<DialogRule> rules, DateTimeOffset seen)
    {
        AutomationElement root = AutomationElement.FromHandle(hwnd);
        if (root.Current.ProcessId != pid)
        {
            throw new InvalidOperationException($"window 0x{(long)hwnd:X} belongs to process {root.Current.ProcessId}, not {pid}");
        }
        var elements = new List<string>();
        var names = new List<string>();
        var buttons = new List<AutomationElement>();
        Walk(root, 0, elements, names, buttons);
        // Read before any button is invoked: the element is gone once the dialog closes.
        string title = root.Current.Name;
        string className = root.Current.ClassName;
        DialogRule? rule = rules.FirstOrDefault(r => title.Contains(r.Title, StringComparison.Ordinal)
            && (r.Text is null || names.Any(n => n.Contains(r.Text, StringComparison.Ordinal))));
        string? pressed = null;
        string outcome;
        if (rule is null)
        {
            outcome = rules.Count == 0 ? "described; the question has no dialog policy" : "described; no rule matched";
        }
        else if (buttons.FirstOrDefault(b => Clean(b.Current.Name) == rule.Press) is not { } button)
        {
            outcome = $"rule matched; the dialog has no button '{rule.Press}'";
        }
        else if (!button.Current.IsEnabled)
        {
            outcome = $"rule matched; button '{rule.Press}' is disabled";
        }
        else if (!button.TryGetCurrentPattern(InvokePattern.Pattern, out object pattern))
        {
            outcome = $"rule matched; button '{rule.Press}' has no InvokePattern";
        }
        else
        {
            ((InvokePattern)pattern).Invoke();
            pressed = rule.Press;
            // A dialog closes; a window such as the VBA editor stays and changes its title (break mode ends).
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (IsWindow(hwnd) && TitleOf(hwnd) == title && clock.ElapsedMilliseconds < 5000)
            {
                Thread.Sleep(50);
            }
            outcome = !IsWindow(hwnd)
                ? $"invoked '{rule.Press}'; the dialog closed after {clock.ElapsedMilliseconds} ms"
                : TitleOf(hwnd) != title
                    ? $"invoked '{rule.Press}'; the window title changed after {clock.ElapsedMilliseconds} ms to '{TitleOf(hwnd)}'"
                    : $"invoked '{rule.Press}'; the window is unchanged after 5 s";
        }
        return new DialogObservation
        {
            Window = "0x" + ((long)hwnd).ToString("X", CultureInfo.InvariantCulture),
            ProcessId = pid,
            Title = title,
            ClassName = className,
            Elements = elements,
            Rule = rule,
            Pressed = pressed,
            Outcome = outcome,
            SeenUtc = seen,
        };
    }

    /// <summary>The raw view, depth first, so static texts and buttons of plain Win32 dialogs are all present.</summary>
    private static void Walk(AutomationElement element, int depth, List<string> into, List<string> names, List<AutomationElement> buttons)
    {
        for (AutomationElement? child = TreeWalker.RawViewWalker.GetFirstChild(element); child is not null && into.Count < MaxElements; child = TreeWalker.RawViewWalker.GetNextSibling(child))
        {
            AutomationElement.AutomationElementInformation c = child.Current;
            // UI Automation lists owned top-level windows (a dialog of the VBA editor) as children of their owner. Each
            // top-level window is described and answered on its own, so they are not part of this window's tree.
            if (c.NativeWindowHandle != 0 && GetAncestor(c.NativeWindowHandle, 2 /* GA_ROOT */) == c.NativeWindowHandle)
            {
                continue;
            }
            string type = c.ControlType.ProgrammaticName.Replace("ControlType.", "", StringComparison.Ordinal);
            into.Add($"{new string(' ', depth * 2)}{type} '{c.Name}' [{c.AutomationId}] ({c.ClassName}){(c.IsEnabled ? "" : " disabled")}");
            names.Add(c.Name);
            if (c.ControlType == ControlType.Button)
            {
                buttons.Add(child);
            }
            if (depth < MaxDepth)
            {
                Walk(child, depth + 1, into, names, buttons);
            }
        }
    }

    private static string Clean(string name) => name.Replace("&", "", StringComparison.Ordinal);

    [LibraryImport("user32.dll")]
    private static partial nint GetAncestor(nint hwnd, uint flags);

    private static string TitleOf(nint hwnd)
    {
        char[] text = new char[512];
        int n = GetWindowTextW(hwnd, text, text.Length);
        return new string(text, 0, Math.Max(0, n));
    }

    [LibraryImport("user32.dll", EntryPoint = "GetWindowTextW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int GetWindowTextW(nint hwnd, [Out] char[] text, int max);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindow(nint hwnd);
}
