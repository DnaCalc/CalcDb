using System.Text.RegularExpressions;

namespace CalcDb.Evidence;

/// <summary>Where the broker runs a question.</summary>
public enum BrokerRoute
{
    /// <summary>Tier 1: a pooled long-lived worker after its declared reset.</summary>
    Pooled,

    /// <summary>Tier 1: a new long-lived worker for this question alone.</summary>
    FreshWorker,

    /// <summary>Tier 2: the owned-attempt runner, one fresh worker process per attempt.</summary>
    Owned,

    /// <summary>Not run.</summary>
    Refused,
}

/// <summary>The route admission chose and why. <see cref="Experiment"/> marks a pooled answer admitted only for OR-REUSE.</summary>
public readonly record struct Admission(BrokerRoute Route, string Reason, bool Experiment = false)
{
    public string RouteName => Route switch
    {
        BrokerRoute.Pooled => "pooled",
        BrokerRoute.FreshWorker => "fresh-worker",
        BrokerRoute.Owned => "owned",
        _ => "refused",
    };
}

/// <summary>
/// The broker's admission policy (docs/work/oracle-service.md, Two tiers and Admission policy; oracle service step 2,
/// D-ORACLE-1, R-ORACLE-1 OR-REUSE). In order:
/// <list type="number">
/// <item>An unknown purpose is refused.</item>
/// <item>Another environment than the native installation, or effects other than a declared <c>none</c> (undeclared
/// included), go to tier 2.</item>
/// <item>A question declared <c>effects: none</c> whose text arguments name a function with external effects (the deny
/// list <see cref="DeniedFunctions"/>) is refused, naming the function.</item>
/// <item><c>isolation: fresh-attempt</c> goes to tier 2; so does a program of a tier-2 shape
/// (<see cref="PooledAdmission"/>), whatever it declared.</item>
/// <item><c>isolation: fresh-worker</c> gets a new tier-1 worker.</item>
/// <item>Otherwise (<c>pooled</c> or undeclared isolation) it is pooled when the declared reset is qualified for its
/// card, or when the request's purpose is the OR-REUSE experiment; else it goes to tier 2, so no answer of an
/// unqualified card carries a prehistory unless it was asked for one.</item>
/// </list>
/// </summary>
public static partial class BrokerAdmission
{
    public const string ReusePurpose = "or-reuse";

    /// <summary>
    /// Functions with external effects that an <c>effects: none</c> question may not name in SQL or expression text
    /// (oracle-service.md: "refused at admission, by a deny list that names each refused function").
    /// </summary>
    public static readonly IReadOnlyList<string> DeniedFunctions =
        ["Shell", "CreateObject", "GetObject", "SendKeys", "SaveSetting", "DeleteSetting", "Kill", "FileCopy", "MkDir", "RmDir", "ChDir", "ChDrive", "DDEInitiate", "DDEExecute", "DDEPoke", "DDESend", "DDERequest", "DDE"];

    /// <summary>The card a question belongs to: the first segment of its probe (<c>r-cursor-1</c> of <c>r-cursor-1/pos-find</c>).</summary>
    public static string CardOf(Question question) => question.Probe.Split('/')[0];

    public static Admission Admit(Question question, string environment, string? purpose, IReadOnlySet<string> qualifiedCards)
    {
        if (purpose is not null && purpose != ReusePurpose)
        {
            return new(BrokerRoute.Refused, $"unknown purpose '{purpose}' (only {ReusePurpose})");
        }
        if (environment != PooledAdmission.NativeEnvironment)
        {
            return new(BrokerRoute.Owned, $"environment '{environment}' runs in tier 2");
        }
        if (question.Effects != "none")
        {
            return new(BrokerRoute.Owned, question.Effects is null ? "effects undeclared" : $"effects {question.Effects}");
        }
        if (DeniedFunction(question) is { } denied)
        {
            return new(BrokerRoute.Refused, $"declared effects none but names {denied}, a function with external effects");
        }
        if (question.Isolation == "fresh-attempt")
        {
            return new(BrokerRoute.Owned, "isolation fresh-attempt");
        }
        if (PooledAdmission.Refusal(question, environment) is { } shape)
        {
            return new(BrokerRoute.Owned, "tier-2 shape: " + shape);
        }
        if (question.Isolation == "fresh-worker")
        {
            return new(BrokerRoute.FreshWorker, "isolation fresh-worker");
        }
        string card = CardOf(question);
        if (qualifiedCards.Contains(card))
        {
            return new(BrokerRoute.Pooled, $"{WorkerReset.ReleaseBindings} qualified for {card}");
        }
        if (purpose == ReusePurpose)
        {
            return new(BrokerRoute.Pooled, $"OR-REUSE experiment: {WorkerReset.ReleaseBindings} not qualified for {card}", Experiment: true);
        }
        return new(BrokerRoute.Owned, $"{WorkerReset.ReleaseBindings} is not qualified for {card} (OR-REUSE)");
    }

    /// <summary>The first denied function a text argument of the program calls, or null.</summary>
    public static string? DeniedFunction(Question question)
    {
        foreach (string text in Texts(question.Program))
        {
            Match m = DeniedCall().Match(text);
            if (m.Success)
            {
                return DeniedFunctions.First(f => string.Equals(f, m.Groups[1].Value, StringComparison.OrdinalIgnoreCase));
            }
        }
        return null;
    }

    private static IEnumerable<string> Texts(IReadOnlyList<Operation> program)
    {
        foreach (Operation op in program)
        {
            IEnumerable<string> texts = op switch
            {
                Invoke invoke => invoke.Args.Concat(invoke.Named?.Select(n => n.Value) ?? []).SelectMany(Texts),
                LoopUntil loop => Texts(loop.Body),
                ForEachItem each => Texts(each.Body),
                Enumerate enumerate => Texts(enumerate.Body),
                OnThread thread => Texts(thread.Body),
                _ => [],
            };
            foreach (string text in texts)
            {
                yield return text;
            }
        }
    }

    private static IEnumerable<string> Texts(Arg arg) => arg switch
    {
        BstrArg bstr => [bstr.Text],
        ByRefArg byRef => Texts(byRef.Inner),
        _ => [],
    };

    [GeneratedRegex(@"\b(Shell|CreateObject|GetObject|SendKeys|SaveSetting|DeleteSetting|Kill|FileCopy|MkDir|RmDir|ChDir|ChDrive|DDEInitiate|DDEExecute|DDEPoke|DDESend|DDERequest|DDE)\s*\(", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DeniedCall();
}
