using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Text.Json;

namespace CalcDb.Evidence;

/// <summary>
/// Frame types of <c>calcdb-oracle/1</c> (docs/work/oracle-service.md, Protocol; D-ORACLE-1). The long-lived worker of
/// step 1 uses hello, submit, answer, error, ping/pong and done on its stdin and stdout; the broker's endpoint (step 2)
/// uses all of them with the bodies of <see cref="BrokerFrames"/>, plus status and shutdown.
/// </summary>
public enum FrameType : byte
{
    Hello = 1,
    Submit = 2,
    Put = 3,
    Get = 4,
    Answer = 5,
    Env = 6,
    Done = 7,
    Error = 8,
    Ping = 9,
    Pong = 10,
    Wait = 11,

    /// <summary>Client to broker with an empty body; the broker answers with a <see cref="BrokerStatus"/>.</summary>
    Status = 12,

    /// <summary>Client to broker: stop accepting, finish the question each worker is on, retire the workers, exit.</summary>
    Shutdown = 13,
}

/// <summary>One frame: its type and its compact UTF-8 JSON body.</summary>
public readonly record struct Frame(FrameType Type, ReadOnlyMemory<byte> Body)
{
    public static Frame Of<T>(FrameType type, T body) => new(type, JsonSerializer.SerializeToUtf8Bytes(body, EvidenceJson.LineOptions));

    /// <summary>The body as <typeparamref name="T"/>; a body that is not one is a protocol error.</summary>
    public T Parse<T>()
    {
        try
        {
            return JsonSerializer.Deserialize<T>(Body.Span, EvidenceJson.LineOptions) ?? throw new OracleProtocolException($"Empty {Type} body.");
        }
        catch (JsonException ex)
        {
            throw new OracleProtocolException($"{Type} body is not a {typeof(T).Name}: {ex.Message}");
        }
    }
}

/// <summary>
/// The framing of <c>calcdb-oracle/1</c>, taken from Touchstone: a 4-byte big-endian length, then a type byte and the
/// body; the length counts the type byte and the body, and a frame is at most <see cref="MaxFrame"/> bytes. On stdio
/// frames are not compressed (the zlib form, type byte + 0x80, is for the ssh stream of step 4). A frame never carries a
/// capture: the capture stays in its attempt directory behind its returned receipt (OR-SEAL), and the answer frame names
/// its digest.
/// </summary>
public static class OracleFrames
{
    public const string Protocol = "calcdb-oracle/1";

    public const int MaxFrame = 64 * 1024 - 1;

    public static void Write(Stream stream, Frame frame)
    {
        int length = frame.Body.Length + 1;
        if (length > MaxFrame)
        {
            throw new OracleProtocolException($"A {frame.Type} frame of {length} bytes exceeds {MaxFrame}.");
        }
        byte[] buffer = new byte[4 + length];
        BinaryPrimitives.WriteInt32BigEndian(buffer, length);
        buffer[4] = (byte)frame.Type;
        frame.Body.Span.CopyTo(buffer.AsSpan(5));
        stream.Write(buffer);
        stream.Flush();
    }

    public static void Write<T>(Stream stream, FrameType type, T body) => Write(stream, Frame.Of(type, body));

    /// <summary>
    /// The next frame, or null at a clean end of the stream between frames. A stream that ends inside a frame, a length
    /// out of range, an unknown type or a compressed frame is an <see cref="OracleProtocolException"/>: stray text on the
    /// channel (a line some library printed) reads as a length out of range.
    /// </summary>
    public static Frame? Read(Stream stream)
    {
        Span<byte> header = stackalloc byte[4];
        int got = ReadFully(stream, header);
        if (got == 0)
        {
            return null;
        }
        if (got < 4)
        {
            throw new OracleProtocolException($"The stream ended inside a frame header ({got} of 4 bytes).");
        }
        int length = BinaryPrimitives.ReadInt32BigEndian(header);
        if (length < 1 || length > MaxFrame)
        {
            throw new OracleProtocolException($"Frame length {length} (header {Convert.ToHexString(header)}) is out of range 1..{MaxFrame}.");
        }
        byte[] body = new byte[length];
        int read = ReadFully(stream, body);
        if (read < length)
        {
            throw new OracleProtocolException($"The stream ended inside a frame ({read} of {length} bytes).");
        }
        byte type = body[0];
        if ((type & 0x80) != 0)
        {
            throw new OracleProtocolException($"Compressed frame type 0x{type:X2} on an uncompressed channel.");
        }
        if (!Enum.IsDefined((FrameType)type))
        {
            throw new OracleProtocolException($"Unknown frame type {type}.");
        }
        return new Frame((FrameType)type, body.AsMemory(1));
    }

    private static int ReadFully(Stream stream, Span<byte> into)
    {
        int total = 0;
        while (total < into.Length)
        {
            int n = stream.Read(into[total..]);
            if (n == 0)
            {
                break;
            }
            total += n;
        }
        return total;
    }
}

public sealed class OracleProtocolException(string message) : Exception(message);

/// <summary>hello, both ways: the protocol version and who speaks it.</summary>
public sealed record HelloBody(string Protocol, string Client)
{
    public int? ProcessId { get; init; }

    public string? WorkerSha256 { get; init; }
}

/// <summary>
/// submit, runner to worker: the admission of one prepared attempt (II-15 §3: the runner persists the attempt and marks
/// it running before it hands over the instruction that can run the first native call). <see cref="Session"/> is the
/// prehistory the runner expects the worker to have; the worker refuses the question when its own record differs.
/// </summary>
public sealed record SubmitBody(int Seq, string AttemptDirectory, string AttemptId, int WorkerGeneration, WorkerSession Session);

/// <summary>answer, worker to runner: the question settled, its capture's digest, and the reset that followed it.</summary>
public sealed record AnswerBody(int Seq, string AttemptId, int WorkerGeneration, string Execution, string CaptureSha256, long CaptureLength, ResetReport Reset);

/// <summary>
/// error, either way. Codes: <c>refused</c>, <c>malformed</c>, <c>protocol</c>, <c>worker-down</c>; from the broker also
/// <c>conflict</c> (a resent request id and sequence with other questions), <c>shutdown</c>, <c>not-found</c>.
/// </summary>
public sealed record ErrorBody(string Code, string Message)
{
    public int? Seq { get; init; }

    /// <summary>The client request id the error is about (broker endpoint).</summary>
    public string? Id { get; init; }
}

/// <summary>ping, pong and done.</summary>
public sealed record SeqBody(int Seq);

/// <summary>
/// The declared resets of a long-lived worker (R-ORACLE-1 OR-REUSE, II-15 §3). A reset names exactly what it does; what
/// it does not name stays, and OR-REUSE evidence decides whether that matters for a card. Until a reset is qualified for
/// a card, a capture taken after another question in the same worker is not eligible for cross-attempt answer reuse.
/// </summary>
public static class WorkerReset
{
    /// <summary>
    /// <c>release-bindings/1</c>. After each question the worker checks that the program released every object it bound
    /// and closed every native handle (the interpreter releases what is left, as in a fresh worker), clears the thread's
    /// IErrorInfo slot, restores the worker's own current directory, and compares the thread locale with its value before
    /// the question. It does not reset, and this reset does not claim to reset: the DBEngine class factory and any
    /// engine-wide defaults (DefaultType, IniPath, SystemDB, the Workspaces and Errors collections of an engine object
    /// ACE may share between activations), ACE's process-wide caches and loaded modules, random state, or the module
    /// identity cache of the worker (OR-IDENT is still observed per capture). A locale change or an object left bound is a
    /// reset failure: the runner retires the worker instead of adding cleanup calls (II-15 §3).
    /// </summary>
    public const string ReleaseBindings = "release-bindings/1";
}

/// <summary>What the worker found and did in its reset after one question.</summary>
public sealed record ResetReport(string Reset, int ResidualBindings, string ClearErrorInfoHresult, int ThreadLocaleBefore, int ThreadLocaleAfter)
{
    /// <summary>Why the reset failed, or null; a failed reset retires the worker.</summary>
    public string? Problem { get; init; }
}

/// <summary>One question that ran earlier in the same worker process, in order.</summary>
public sealed record PriorQuestion(int Position, string AttemptId, string QuestionKey, string Execution, ResetReport Reset);

/// <summary>
/// R-ORACLE-1 OR-REUSE: the worker a question ran in and everything that ran there before it since the worker started
/// (II-15 §3: stateful reuse records the full prehistory from a declared reset). <see cref="Position"/> 0 is a fresh
/// long-lived worker; a capture without a session ran in the one-attempt-per-process mode of tier 2. The runner writes
/// the session it expects to <c>receipts/session.json</c> before admission, the worker refuses a question whose
/// expected session differs from its own record, and the sealer checks that the capture names the same session, so a
/// reused answer is never taken for a fresh one.
/// </summary>
public sealed record WorkerSession
{
    public string Protocol { get; init; } = OracleFrames.Protocol;

    /// <summary>The runner's name for this worker process (<c>&lt;run&gt;.w&lt;n&gt;</c>).</summary>
    public required string SessionId { get; init; }

    public required int ProcessId { get; init; }

    /// <summary>How many questions ran in this worker before this one.</summary>
    public required int Position { get; init; }

    /// <summary>The reset declared between questions (<see cref="WorkerReset"/>).</summary>
    public required string Reset { get; init; }

    public required IReadOnlyList<PriorQuestion> Prehistory { get; init; }

    /// <summary>The exact form two sessions are compared in (records with lists compare by reference).</summary>
    public string Canonical() => EvidenceJson.SerializeLine(this);
}

/// <summary>
/// Which questions a long-lived worker admits (docs/work/oracle-service.md, Admission policy; step 1). Questions do not
/// yet declare <c>effects</c> and <c>isolation</c> (step 2); until then the shape of the program decides, and a refusal
/// names its reason. Admitted: one participant, read-only native opens, no output files, no process control, no
/// dialogs, no ETW recording, the native installation. Effects through DAO calls themselves (an Execute, an AddNew) are
/// not visible here: they stay on the attempt's own input copy, and the caller names such questions out.
/// </summary>
public static class PooledAdmission
{
    /// <summary>The job environment of the native installation, the only one a long-lived worker runs.</summary>
    public const string NativeEnvironment = "windows-x64-ace-local";

    private const uint GenericWrite = 0x40000000;
    private const uint GenericAll = 0x10000000;

    public static string? Refusal(Question question, string environment)
    {
        if (environment != NativeEnvironment)
        {
            return $"environment '{environment}': the long-lived worker runs only the native installation";
        }
        if (question.Peers.Count > 0)
        {
            return "a question with further participants needs one owned process per participant (tier 2)";
        }
        if (question.DialogPolicy is { Count: > 0 })
        {
            return "a dialog policy belongs to an owned Access-hosted attempt (tier 2)";
        }
        if (question.ObservationRecipe.Contains("etwFileIo"))
        {
            return "ETW file-I/O recording is per attempt (tier 2)";
        }
        return Walk(question.Program);
    }

    private static string? Walk(IReadOnlyList<Operation> program)
    {
        foreach (Operation op in program)
        {
            string? why = op switch
            {
                ClaimProcess => "claimProcess starts an out-of-process server",
                TerminateSelf => "terminateSelf ends the worker",
                KillParticipant or SuspendParticipant or ResumeParticipant => "participant control needs further participants",
                Signal or Await => "signal and await order further participants",
                WriteFileBytes => "writeFileBytes changes a file",
                SetReadOnly => "setReadOnly changes a file's attributes",
                NativeOpen open when (open.Access & (GenericWrite | GenericAll)) != 0 => "a native open for writing",
                Invoke invoke when invoke.Args.Concat(invoke.Named?.Select(n => n.Value) ?? []).Any(WritesOutput) => $"{invoke.Member} names an output file",
                LoopUntil loop => Walk(loop.Body),
                ForEachItem each => Walk(each.Body),
                Enumerate enumerate => Walk(enumerate.Body),
                OnThread thread => Walk(thread.Body),
                _ => null,
            };
            if (why is not null)
            {
                return why;
            }
        }
        return null;
    }

    private static bool WritesOutput(Arg arg) => arg switch
    {
        OutputPathArg => true,
        ByRefArg byRef => WritesOutput(byRef.Inner),
        _ => false,
    };
}

/// <summary>What one receive on a worker channel gave.</summary>
public enum ReceiveStatus
{
    Frame,
    Timeout,

    /// <summary>The worker closed its output (it exited or died).</summary>
    Closed,

    /// <summary>The bytes were not a frame of <c>calcdb-oracle/1</c>; the channel is unusable after this.</summary>
    Malformed,
}

public readonly record struct Received(ReceiveStatus Status, Frame Frame = default, string? Error = null);

/// <summary>
/// Reads frames from a worker's output on a thread of its own, so the runner can wait with a deadline and keep its window
/// guard sampling meanwhile. The first end of stream or malformed frame is the last thing it gives.
/// </summary>
public sealed class FramePump : IDisposable
{
    private readonly BlockingCollection<Received> _received = new();
    private readonly Stream _stream;
    private bool _ended;

    public FramePump(Stream stream)
    {
        _stream = stream;
        var thread = new Thread(Pump) { IsBackground = true, Name = "calcdb-oracle frame pump" };
        thread.Start();
    }

    private void Pump()
    {
        try
        {
            while (true)
            {
                Frame? frame = OracleFrames.Read(_stream);
                if (frame is null)
                {
                    _received.Add(new Received(ReceiveStatus.Closed));
                    return;
                }
                _received.Add(new Received(ReceiveStatus.Frame, frame.Value));
            }
        }
        catch (OracleProtocolException ex)
        {
            _received.Add(new Received(ReceiveStatus.Malformed, Error: ex.Message));
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            _received.Add(new Received(ReceiveStatus.Closed, Error: ex.Message));
        }
        finally
        {
            _received.CompleteAdding();
        }
    }

    public Received Receive(TimeSpan wait)
    {
        if (_ended)
        {
            return new Received(ReceiveStatus.Closed, Error: "the channel already ended");
        }
        if (!_received.TryTake(out Received r, wait))
        {
            return _received.IsCompleted ? new Received(ReceiveStatus.Closed) : new Received(ReceiveStatus.Timeout);
        }
        _ended = r.Status is ReceiveStatus.Closed or ReceiveStatus.Malformed;
        return r;
    }

    /// <summary>Closes the stream; the pump thread ends at its next read. The queue stays, so a late frame has a home.</summary>
    public void Dispose() => _stream.Dispose();
}
