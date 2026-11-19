namespace CalcDb.Storage;

/// <summary>The settled state of one effect's reserved receipt (II-08 §17).</summary>
public enum EffectState
{
    Pending,

    /// <summary>Process-local bookkeeping that a request was submitted; not an on-disk record (II-08 §17).</summary>
    Started,

    Completed,

    /// <summary>Definitely not done, with no possible effect beyond <see cref="EffectReceipt.ProvenBytes"/>.</summary>
    Failed,

    /// <summary>A range may have changed: <see cref="EffectReceipt.PossibleEffect"/> names it.</summary>
    Uncertain,
}

/// <summary>
/// The minimal outcome record of one effect, reserved before the first native effect (II-03 §6.2): completed prefix,
/// possible-effect range, last native cause. It stays writable when detailed capture is full.
/// </summary>
public struct EffectReceipt
{
    public EffectState State { get; internal set; }

    /// <summary>Bytes of this effect the adapter proved transferred, from its start.</summary>
    public ulong ProvenBytes { get; internal set; }

    public ByteRange? PossibleEffect { get; internal set; }

    /// <summary>Native requests issued for this effect (a short write continues with another request).</summary>
    public int Requests { get; internal set; }

    public TransferStatus? LastStatus { get; internal set; }

    public int? NativeError { get; internal set; }

    public string? Cause { get; internal set; }

    /// <summary>For an extent effect: the extent the handle reported after it, an observation and no durability claim.</summary>
    public ulong? ObservedExtent { get; internal set; }
}

/// <summary>The state of a program's progress (II-08 §10 <c>ProgressRecord.execution_state</c>).</summary>
public enum ProgramState
{
    Ready,
    Running,
    Completed,

    /// <summary>Stopped on a known failure or uncertainty: the coordinator resolves it from the receipts.</summary>
    NeedsResolution,

    /// <summary>Stopped at a qualified boundary: one owned continuation was transferred; the program never runs again.</summary>
    HandedOff,
}

/// <summary>The cancellation actions of II-08 §17; the classification is exhaustive (T-TXN-34).</summary>
public enum CancelAction
{
    /// <summary>Record the request; it does not stop this phase, and it is not classified again.</summary>
    ContinueSameProgram,

    /// <summary>Retain the request and finish the exact remaining effects up to <see cref="CancelDecision.Boundary"/>.</summary>
    DeferUntil,

    /// <summary>Hand the program and its obligations to the coordinator at this settled boundary.</summary>
    StopAtQualifiedBoundary,
}

public readonly record struct CancelDecision(CancelAction Action, int Boundary = 0, string? Resolution = null)
{
    public static CancelDecision Continue { get; } = new(CancelAction.ContinueSameProgram);

    /// <summary>Defer to the step index <paramref name="boundary"/>; the program's effect count means End.</summary>
    public static CancelDecision Defer(int boundary) => new(CancelAction.DeferUntil, boundary);

    public static CancelDecision Stop(string resolution) => new(CancelAction.StopAtQualifiedBoundary, Resolution: resolution);
}

/// <summary>The coordinator's classification of a cancellation request at a checkpoint between effects (II-08 §17).</summary>
public delegate CancelDecision CancellationClassifier(NativeProgram program);

/// <summary>What happened to a cancellation request (T-TXN-34).</summary>
public sealed record CancellationRecord(int ClassifiedAtStep, CancelDecision Decision)
{
    /// <summary>A DeferUntil(End) request the completion resolver settles.</summary>
    public bool DeferredToEnd { get; init; }
}

/// <summary>
/// The reserved progress of one program (II-08 §10 <c>ProgressRecord</c>): one receipt per effect, the next effect,
/// the stop step and cause, the cancellation record, completed flushes and bounded per-request capture.
/// </summary>
public sealed class ProgramProgress
{
    private readonly EffectReceipt[] _receipts;
    private readonly List<TransferReceipt> _capture = [];
    private readonly List<int> _flushes = [];

    internal ProgramProgress(int effects) => _receipts = new EffectReceipt[effects];

    public EffectReceipt this[int step] => _receipts[step];

    public int Count => _receipts.Length;

    public ProgramState State { get; internal set; }

    /// <summary>The next effect the program would submit; never reset to an earlier one.</summary>
    public int NextEffect { get; internal set; }

    public int? StoppedAt { get; internal set; }

    public string? Cause { get; internal set; }

    public CancellationRecord? Cancellation { get; internal set; }

    /// <summary>Flush barriers that completed, in order. A later failure never removes one (T-TXN-33).</summary>
    public IReadOnlyList<int> CompletedFlushes => _flushes;

    /// <summary>Every primitive transfer receipt, in issue order, up to the policy's capture limit.</summary>
    public IReadOnlyList<TransferReceipt> Capture => _capture;

    /// <summary>Capture was cut at its limit: it cannot count as a complete replay (II-08 §20).</summary>
    public bool CaptureTruncated { get; private set; }

    internal ref EffectReceipt Receipt(int step) => ref _receipts[step];

    internal void Record(TransferReceipt receipt, int limit)
    {
        if (_capture.Count < limit)
        {
            _capture.Add(receipt);
        }
        else
        {
            CaptureTruncated = true;
        }
    }

    internal void FlushCompleted(int step) => _flushes.Add(step);
}

public enum NativeOutcomeKind
{
    Completed,

    /// <summary>A definite failure: the stopping effect changed nothing beyond its proven bytes.</summary>
    KnownFailure,

    /// <summary>A range may have changed; the native source of the affected pages is uncertain.</summary>
    Uncertain,

    HandedOff,
}

/// <summary>The program's outcome: its kind, the stopping step and cause, and a handed-off continuation.</summary>
public sealed record NativeOutcome(NativeOutcomeKind Kind, int? Step, string? Cause, ProgramContinuation? Continuation = null)
{
    public bool Completed => Kind == NativeOutcomeKind.Completed;
}

/// <summary>
/// The one owned continuation of a program stopped at a qualified boundary (II-08 §19 <c>HandedOff</c>): its receiver
/// takes it once. It asserts nothing about the abandoned suffix, and the original program never restarts.
/// </summary>
public sealed class ProgramContinuation
{
    private int _taken;

    internal ProgramContinuation(NativeProgram program, string resolution)
    {
        Program = program;
        Resolution = resolution;
    }

    public NativeProgram Program { get; }

    public string Resolution { get; }

    /// <summary>Take ownership; true exactly once.</summary>
    public bool TryTake() => Interlocked.Exchange(ref _taken, 1) == 0;
}

/// <summary>
/// The generic native-program executor (W-03/native-program-executor; II-03 §6.2 <c>WriteAuthorizedSpan</c>, II-08 §17
/// <c>RunNativeProgram</c>). It runs a compiled program one effect at a time through a positioned file: a short
/// successful write continues its exact authorized suffix; any failure, uncertainty or adapter violation stops ordinary
/// advancement with every receipt retained; a flush is its own effect, never implied by a write; cancellation is
/// classified only at checkpoints between settled effects. It performs no implicit flush, commit, retry or rollback.
/// </summary>
public static class NativeProgramExecutor
{
    /// <summary>
    /// Run <paramref name="program"/> from its first effect. A program runs once: one that started, stopped or was
    /// handed off is never restarted (no semantic retry, T-PAGER-13). <paramref name="cancellation"/> is observed at
    /// checkpoints and never passed into a native request: a request in flight settles first (II-08 §20).
    /// </summary>
    public static NativeOutcome Run(NativeProgram program, IPositionedFile file, CancellationClassifier? classify = null, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(program);
        ArgumentNullException.ThrowIfNull(file);
        ProgramProgress progress = program.Progress;
        lock (progress)
        {
            if (progress.State != ProgramState.Ready)
            {
                throw new InvalidOperationException($"The program is {progress.State}: a program runs once and is never restarted.");
            }
            if (file.Incarnation != program.FileIncarnation)
            {
                throw new ArgumentException($"The program was compiled for file incarnation {program.FileIncarnation}, not {file.Incarnation}.", nameof(file));
            }
            progress.State = ProgramState.Running;
        }
        try
        {
            return RunCore(program, file, classify, cancellation);
        }
        catch (Exception exception)
        {
            // An unexpected error after effects began takes the retained-attempt path (II-08 §19): the current
            // effect's range may have changed; the progress is kept, never reset.
            int step = progress.NextEffect;
            if (step < program.Effects.Count && progress[step].State == EffectState.Started)
            {
                ref EffectReceipt receipt = ref progress.Receipt(step);
                receipt.State = EffectState.Uncertain;
                receipt.PossibleEffect = Remaining(program.Effects[step], receipt.ProvenBytes);
                receipt.Cause = "The adapter threw: " + exception.Message;
            }
            Stop(progress, step, "The executor stopped on an exception: " + exception.Message);
            throw;
        }
    }

    private static NativeOutcome RunCore(NativeProgram program, IPositionedFile file, CancellationClassifier? classify, CancellationToken cancellation)
    {
        ProgramProgress progress = program.Progress;
        IReadOnlyList<NativeEffect> effects = program.Effects;
        while (progress.NextEffect < effects.Count)
        {
            int step = progress.NextEffect;
            NativeEffect effect = effects[step];
            if (Checkpoint(program, step, classify, cancellation) is { } handedOff)
            {
                return handedOff;
            }
            foreach (int predecessor in effect.Predecessors)
            {
                if (progress[predecessor].State != EffectState.Completed)
                {
                    throw new InvalidOperationException($"Step {step} would be submitted before its predecessor {predecessor} settled.");
                }
            }
            ref EffectReceipt receipt = ref progress.Receipt(step);
            receipt.State = EffectState.Started;
            switch (effect.Kind)
            {
                case EffectKind.Flush:
                    Flush(file, ref receipt, progress, step);
                    break;
                case EffectKind.ResizeExtent when effect.Growth == ExtentGrowth.SetEndOfFile:
                    SetExtent(file, effect, ref receipt);
                    break;
                default:
                    Transfer(file, effect, ref receipt, progress, program.Policy.CaptureLimit);
                    break;
            }
            if (receipt.State != EffectState.Completed)
            {
                Stop(progress, step, receipt.Cause);
                return new NativeOutcome(receipt.State == EffectState.Uncertain ? NativeOutcomeKind.Uncertain : NativeOutcomeKind.KnownFailure, step, receipt.Cause);
            }
            progress.NextEffect = step + 1;
        }
        lock (progress)
        {
            if (progress.Cancellation is { Decision.Action: CancelAction.DeferUntil } deferred && deferred.Decision.Boundary >= effects.Count)
            {
                progress.Cancellation = deferred with { DeferredToEnd = true };
            }
            progress.State = ProgramState.Completed;
        }
        return new NativeOutcome(NativeOutcomeKind.Completed, null, null);
    }

    /// <summary>The cancellation checkpoint before submission of <paramref name="step"/>; a hand-off when the program stops here.</summary>
    private static NativeOutcome? Checkpoint(NativeProgram program, int step, CancellationClassifier? classify, CancellationToken cancellation)
    {
        ProgramProgress progress = program.Progress;
        if (progress.Cancellation is { Decision: { Action: CancelAction.DeferUntil, Boundary: int boundary } } && boundary == step)
        {
            return HandOff(program, step, "The deferred cancellation reached its boundary.");
        }
        if (!cancellation.IsCancellationRequested || progress.Cancellation is not null)
        {
            return null; // no request, or the request was already classified: never rediscovered in a loop
        }
        // Without a coordinator the surface admits no cancellation here: the request is recorded and the phase continues.
        CancelDecision decision = classify?.Invoke(program) ?? CancelDecision.Continue;
        if (decision.Action == CancelAction.DeferUntil && (decision.Boundary <= step || decision.Boundary > program.Effects.Count))
        {
            throw new ArgumentOutOfRangeException(nameof(classify), $"DeferUntil({decision.Boundary}) at step {step}: the boundary must be a later step or End ({program.Effects.Count}).");
        }
        progress.Cancellation = new CancellationRecord(step, decision);
        return decision.Action == CancelAction.StopAtQualifiedBoundary
            ? HandOff(program, step, decision.Resolution ?? "A qualified stop.")
            : null; // ContinueSameProgram and DeferUntil fall through to this exact effect's submission
    }

    private static NativeOutcome HandOff(NativeProgram program, int step, string resolution)
    {
        ProgramProgress progress = program.Progress;
        lock (progress)
        {
            progress.State = ProgramState.HandedOff;
            progress.StoppedAt = step;
            progress.Cause = resolution;
        }
        return new NativeOutcome(NativeOutcomeKind.HandedOff, step, resolution, new ProgramContinuation(program, resolution));
    }

    private static void Stop(ProgramProgress progress, int step, string? cause)
    {
        lock (progress)
        {
            progress.State = ProgramState.NeedsResolution;
            progress.StoppedAt = step;
            progress.Cause = cause;
        }
    }

    /// <summary>
    /// WriteAuthorizedSpan (II-03 §6.2): transfer the effect's exact bytes, continuing only the authorized suffix after a
    /// short successful write. O(span length) bytes and O(completions) bookkeeping, with no copy of the source.
    /// </summary>
    private static void Transfer(IPositionedFile file, NativeEffect effect, ref EffectReceipt receipt, ProgramProgress progress, int captureLimit)
    {
        ulong length = effect.FileRange.Length;
        ulong done = 0;
        while (done < length)
        {
            TransferReceipt r = file.WriteAt(effect.FileRange.Start + done, effect.Source[(int)done..]);
            receipt.Requests++;
            receipt.LastStatus = r.Status;
            receipt.NativeError = r.NativeError;
            progress.Record(r, captureLimit);
            if (r.FileIncarnation != file.Incarnation || r.ProvenPrefixBytes > length - done)
            {
                receipt.State = EffectState.Uncertain;
                receipt.PossibleEffect = Remaining(effect, done);
                receipt.Cause = $"The adapter's receipt is inconsistent (incarnation {r.FileIncarnation}, {r.ProvenPrefixBytes} bytes proven of {length - done}).";
                return;
            }
            done += r.ProvenPrefixBytes;
            receipt.ProvenBytes = done;
            if (r.Status != TransferStatus.Complete || r.PossibleEffect is not null)
            {
                if (r.PossibleEffect is { } possible)
                {
                    receipt.State = EffectState.Uncertain;
                    receipt.PossibleEffect = possible;
                    receipt.Cause = $"The write of {effect.FileRange} stopped after {done} bytes ({r.Status}, error {r.NativeError}); {possible} may have changed.";
                }
                else
                {
                    receipt.State = EffectState.Failed;
                    receipt.Cause = $"The write of {effect.FileRange} failed after {done} bytes with no possible effect ({r.Status}, error {r.NativeError}).";
                }
                return;
            }
            if (r.ProvenPrefixBytes == 0)
            {
                receipt.State = EffectState.Failed;
                receipt.Cause = $"The write of {effect.FileRange} made no progress after {done} bytes.";
                return;
            }
        }
        receipt.State = EffectState.Completed;
    }

    private static void Flush(IPositionedFile file, ref EffectReceipt receipt, ProgramProgress progress, int step)
    {
        receipt.Requests++;
        FileOperationResult r = file.Flush();
        receipt.NativeError = r.NativeError;
        if (r.Succeeded)
        {
            receipt.State = EffectState.Completed;
            progress.FlushCompleted(step);
            return;
        }
        // T-TXN-33: the transfers stay completed and earlier flushes stay completed; this barrier's durability is not
        // established, and no later reread can establish it.
        receipt.State = EffectState.Failed;
        receipt.Cause = $"{r.Operation} failed (error {r.NativeError}): durability of the preceding transfers is not established.";
    }

    private static void SetExtent(IPositionedFile file, NativeEffect effect, ref EffectReceipt receipt)
    {
        receipt.Requests++;
        FileOperationResult r = file.SetExtent(effect.NewExtent);
        receipt.NativeError = r.NativeError;
        if (r.Succeeded)
        {
            receipt.State = EffectState.Completed;
            return;
        }
        // A failed extent change may still have changed the extent (II-08 §17): the range between the two extents is
        // uncertain. The handle's current extent is recorded as an observation, not as proof of what survives.
        try
        {
            receipt.ObservedExtent = file.GetExtent();
        }
        catch (IOException)
        {
            receipt.ObservedExtent = null;
        }
        receipt.State = EffectState.Uncertain;
        receipt.PossibleEffect = effect.FileRange;
        receipt.Cause = $"{r.Operation} to 0x{effect.NewExtent:X} failed (error {r.NativeError}); the extent between {effect.FileRange} is uncertain.";
    }

    private static ByteRange Remaining(NativeEffect effect, ulong proven) => new(effect.FileRange.Start + proven, effect.FileRange.End);
}
