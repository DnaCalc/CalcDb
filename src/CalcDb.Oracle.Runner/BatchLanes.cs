using System.Runtime.ExceptionServices;
using CalcDb.Evidence;

namespace CalcDb.Oracle.Runner;

/// <summary>
/// <c>batch --parallel N</c> (W-01/corpus-comparison, task mvgk): up to N questions of a candidate batch run at once, and
/// their results come back in file order, so the batch reports, appends and diffs exactly as one at a time. Only candidate
/// attempts (<see cref="ImplementationSelection.CalcDbEnvironment"/>) run in parallel: they touch no ACE, and each has its
/// own job file, attempt directory, input copies and worker job object. ACE attempts always run one at a time.
/// <para>What a question shares with the others, and how each is kept apart:</para>
/// <list type="bullet">
/// <item><b>Its token.</b> Questions of one token (the same probe and question key) run in file order in one lane, so a
/// later one sees the earlier one's attempt, as in a sequential run.</item>
/// <item><b>The store's records.</b> Job and question records are created by an atomic rename
/// (<see cref="EvidenceJson.TryCreateNewText"/>), so two questions of one key never see a partly written question record;
/// the content cache keeps the first copy of a hash (<see cref="ArtifactStore.Locate"/>, <see cref="JobStore.ImportContent"/>).
/// The ledgers (<c>comparisons.jsonl</c>, the compare-set run) are written by the reporting thread alone, in file order.</item>
/// <item><b>Worker launch.</b> Workers inherit only their own pipe and log handles (<see cref="PrivateDesktop.StartSuspended"/>).</item>
/// <item><b>What runs alone</b> (<see cref="RunsAlone"/>): a question with further participants orders processes in time
/// (suspends, kills, lock waits), ETW recording goes through the one recorder queue, and dialog policies and claimed servers
/// reach desktop-wide UI and machine-wide COM servers. These run one at a time after the parallel ones, with nothing beside them.</item>
/// </list>
/// </summary>
internal static class BatchLanes
{
    /// <summary>
    /// The default degree for candidate batches. Measured on the 8-core, 16-thread, 32 GB oracle host on 2026-11-21 (task
    /// mvgk): a full compare set took 1,024 s at N=1, 377 s at N=4, 268-341 s at N=6 and 319 s at N=8, with other
    /// sessions' builds and compare runs beside it. A worker holds 35-70 MB, so memory does not bound N; the CPU does, and
    /// the 13 questions that run alone add about 55 s at the end. Past 6 nothing measurable was gained, and 6 leaves
    /// cores to the builds beside it.
    /// </summary>
    public const int DefaultDegree = 6;

    /// <summary>The degree a batch runs with: <c>--parallel N</c>, default <see cref="DefaultDegree"/> for candidate attempts, 1 for ACE.</summary>
    public static int Degree(Options options, bool candidate)
    {
        if (options.Get("parallel") is not { } text)
        {
            return candidate ? Math.Min(DefaultDegree, Environment.ProcessorCount) : 1;
        }
        if (!int.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int degree) || degree < 1)
        {
            throw new ArgumentException($"--parallel {text}: give a number of questions at once, 1 or more.");
        }
        if (degree > 1 && !candidate)
        {
            throw new ArgumentException("--parallel: ACE attempts run one at a time; only candidate (windows-x64-calcdb-local) attempts run in parallel.");
        }
        return degree;
    }

    /// <summary>
    /// Whether a question runs with nothing beside it: further participants (their order in time is the question), ETW
    /// recording on non-candidate runs (one recorder queue for the host; candidate runs skip ETW and may run in parallel),
    /// a dialog policy or a claimed out-of-process server (UI and COM servers outside the attempt).
    /// </summary>
    public static bool RunsAlone(Question question) => RunsAlone(question, candidate: true);

    public static bool RunsAlone(Question question, bool candidate) =>
        question.Peers.Count > 0
        || (!candidate && question.ObservationRecipe.Contains("etwFileIo")) // EtwBridge.Channel, which is Windows-only
        || question.DialogPolicy is { Count: > 0 }
        || ClaimsProcess(question.Program);

    private static bool ClaimsProcess(IReadOnlyList<Operation> program) => program.Any(op => op switch
    {
        ClaimProcess => true,
        LoopUntil loop => ClaimsProcess(loop.Body),
        ForEachItem each => ClaimsProcess(each.Body),
        Enumerate enumerate => ClaimsProcess(enumerate.Body),
        OnThread thread => ClaimsProcess(thread.Body),
        _ => false,
    });

    /// <summary>
    /// Run <paramref name="run"/> for every question, up to <paramref name="degree"/> at once, and yield the results in
    /// question order as each becomes available. A failure stops new questions from starting; the questions already running
    /// finish and seal, and the failure is thrown where a sequential run would have thrown it (at its question), or at the
    /// first question that did not run.
    /// </summary>
    public static IEnumerable<T> Run<T>(IReadOnlyList<Question> questions, Func<Question, string> tokenOf, int degree, Func<Question, T> run)
        where T : class
    {
        int count = questions.Count;
        var results = new T?[count];
        var errors = new ExceptionDispatchInfo?[count];
        var done = new ManualResetEventSlim[count];
        for (int i = 0; i < count; i++)
        {
            done[i] = new ManualResetEventSlim();
        }
        // GroupBy keeps the order of each token's first question, and file order inside a group.
        List<int[]> groups = [.. Enumerable.Range(0, count).GroupBy(i => tokenOf(questions[i]), StringComparer.Ordinal).Select(g => g.ToArray())];
        List<int[]> shared = [.. groups.Where(g => !g.Any(i => RunsAlone(questions[i])))];
        List<int[]> alone = [.. groups.Where(g => g.Any(i => RunsAlone(questions[i])))];
        ExceptionDispatchInfo? first = null;
        int stopped = 0;

        void RunGroup(int[] group)
        {
            foreach (int i in group)
            {
                try
                {
                    if (Volatile.Read(ref stopped) == 0)
                    {
                        results[i] = run(questions[i]);
                    }
                }
                catch (Exception e)
                {
                    errors[i] = ExceptionDispatchInfo.Capture(e);
                    _ = Interlocked.CompareExchange(ref first, errors[i], null);
                    Volatile.Write(ref stopped, 1);
                }
                finally
                {
                    done[i].Set();
                }
            }
        }

        int next = -1;
        void Lane()
        {
            for (int k = Interlocked.Increment(ref next); k < shared.Count; k = Interlocked.Increment(ref next))
            {
                RunGroup(shared[k]);
            }
        }

        var coordinator = new Thread(() =>
        {
            Thread[] lanes = [.. Enumerable.Range(0, Math.Min(degree, Math.Max(1, shared.Count))).Select(n => new Thread(Lane) { IsBackground = true, Name = $"batch lane {n + 1}" })];
            foreach (Thread lane in lanes)
            {
                lane.Start();
            }
            foreach (Thread lane in lanes)
            {
                lane.Join();
            }
            foreach (int[] group in alone)
            {
                RunGroup(group);
            }
        })
        { IsBackground = true, Name = "batch lanes" };
        coordinator.Start();
        try
        {
            for (int i = 0; i < count; i++)
            {
                done[i].Wait();
                if (errors[i] is { } error)
                {
                    coordinator.Join();
                    error.Throw();
                }
                if (results[i] is not { } result)
                {
                    // Not started because a later question failed first.
                    coordinator.Join();
                    first!.Throw();
                    yield break;
                }
                yield return result;
            }
        }
        finally
        {
            // An abandoned or failed enumeration still lets the running attempts end and seal before the batch returns.
            Volatile.Write(ref stopped, 1);
            coordinator.Join();
            foreach (ManualResetEventSlim d in done)
            {
                d.Dispose();
            }
        }
    }
}
