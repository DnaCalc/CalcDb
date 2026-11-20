using System.IO.Pipes;

namespace CalcDb.Evidence;

/// <summary>
/// The broker's endpoint: a named pipe (<c>\\.\pipe\calcdb-oracle</c> on Windows) carrying <c>calcdb-oracle/1</c> frames,
/// open to the current user only (oracle-service.md, Topology: "lane on Windows -- named pipe --> broker"). One thread per
/// connection reads frames; answers are written by the broker's threads through the connection's sink.
/// <list type="bullet">
/// <item>hello → hello (protocol, broker process id); submit → env, wait, answer frames and done, or error;</item>
/// <item>put → put (<see cref="PutAck"/>); get → get (<see cref="GetChunk"/>) or error <c>not-found</c>;</item>
/// <item>ping → pong; status → status (<see cref="BrokerStatus"/>); shutdown → done, then the broker stops; done ends the connection.</item>
/// </list>
/// </summary>
public sealed class BrokerEndpoint : IDisposable
{
    private readonly OracleBroker _broker;
    private readonly string _pipe;
    private readonly CancellationTokenSource _stop = new();
    private readonly ManualResetEventSlim _shutdown = new();
    private Task? _accept;

    public BrokerEndpoint(OracleBroker broker, string pipe)
    {
        _broker = broker;
        _pipe = pipe;
    }

    /// <summary>Set when a client asked the broker to shut down.</summary>
    public WaitHandle ShutdownRequested => _shutdown.WaitHandle;

    /// <summary>Listen. On Windows the first instance is exclusive, so a second broker on the same name fails here.</summary>
    /// <exception cref="IOException">Another broker already listens on the name.</exception>
    public void Start()
    {
        NamedPipeServerStream first;
        try
        {
            first = Create(first: true);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new IOException($"a broker already listens on pipe '{_pipe}'", ex);
        }
        _accept = Task.Run(() => AcceptLoop(first));
    }

    /// <summary>Raise <see cref="ShutdownRequested"/> as a shutdown frame would (Ctrl+C at the broker's console).</summary>
    public void RequestShutdown() => _shutdown.Set();

    private NamedPipeServerStream Create(bool first)
    {
        PipeOptions options = PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly;
        if (first && OperatingSystem.IsWindows())
        {
            options |= PipeOptions.FirstPipeInstance;
        }
        return new NamedPipeServerStream(_pipe, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, options);
    }

    private async Task AcceptLoop(NamedPipeServerStream server)
    {
        while (true)
        {
            try
            {
                await server.WaitForConnectionAsync(_stop.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                await server.DisposeAsync().ConfigureAwait(false);
                return;
            }
            catch (IOException)
            {
                // A client that connected and vanished at once: listen again.
                await server.DisposeAsync().ConfigureAwait(false);
                server = Create(first: false);
                continue;
            }
            NamedPipeServerStream connected = server;
            new Thread(() => Serve(connected)) { IsBackground = true, Name = "broker connection" }.Start();
            server = Create(first: false);
        }
    }

    private void Serve(NamedPipeServerStream stream)
    {
        var sink = new ConnectionSink(stream);
        try
        {
            while (true)
            {
                Frame? read = OracleFrames.Read(stream);
                if (read is not { } frame || frame.Type == FrameType.Done)
                {
                    return;
                }
                try
                {
                    switch (frame.Type)
                    {
                        case FrameType.Hello:
                            sink.Send(FrameType.Hello, new HelloBody(OracleFrames.Protocol, "calcdb-oracle broker") { ProcessId = Environment.ProcessId });
                            break;
                        case FrameType.Submit:
                            if (_broker.Submit(frame.Parse<ClientSubmitBody>(), sink) is { } error)
                            {
                                sink.Send(FrameType.Error, error);
                            }
                            break;
                        case FrameType.Put:
                            sink.Send(FrameType.Put, _broker.Put(frame.Parse<PutBody>()));
                            break;
                        case FrameType.Get:
                            GetBody get = frame.Parse<GetBody>();
                            if (_broker.Get(get) is { } chunk)
                            {
                                sink.Send(FrameType.Get, chunk);
                            }
                            else
                            {
                                sink.Send(FrameType.Error, new ErrorBody("not-found", $"no content {get.Sha256}"));
                            }
                            break;
                        case FrameType.Ping:
                            sink.Send(FrameType.Pong, frame.Parse<SeqBody>());
                            break;
                        case FrameType.Status:
                            sink.Send(FrameType.Status, _broker.Status());
                            break;
                        case FrameType.Shutdown:
                            _shutdown.Set();
                            sink.Send(FrameType.Done, new DoneBody("shutdown", 0, 0));
                            break;
                        default:
                            sink.Send(FrameType.Error, new ErrorBody("protocol", $"unexpected {frame.Type} frame"));
                            return;
                    }
                }
                catch (Exception ex) when (ex is OracleProtocolException or FormatException or EvidenceFormatException)
                {
                    sink.Send(FrameType.Error, new ErrorBody("malformed", ex.Message));
                }
            }
        }
        catch (OracleProtocolException ex)
        {
            sink.Send(FrameType.Error, new ErrorBody("malformed", ex.Message));
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // The client went away; answers for it are kept and a resend gets them.
        }
        finally
        {
            sink.Close();
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        try
        {
            _accept?.Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException)
        {
            // The accept loop ended with the cancellation.
        }
        _stop.Dispose();
        _shutdown.Dispose();
    }

    /// <summary>One connection's writer: frames from several threads, one at a time; environment entries once each.</summary>
    private sealed class ConnectionSink(Stream stream) : IAnswerSink
    {
        private readonly Lock _lock = new();
        private readonly HashSet<string> _envs = new(StringComparer.Ordinal);
        private bool _closed;

        public void Send<T>(FrameType type, T body)
        {
            lock (_lock)
            {
                Write(type, body);
            }
        }

        public void Answer(BrokerAnswer answer, EnvironmentEntry? environment)
        {
            lock (_lock)
            {
                if (environment is not null && answer.EnvId is { } envId && !_closed && _envs.Add(envId))
                {
                    Write(FrameType.Env, new EnvBody(envId, environment));
                }
                Write(FrameType.Answer, answer);
            }
        }

        public void Wait(WaitBody wait) => Send(FrameType.Wait, wait);

        public void Done(DoneBody done) => Send(FrameType.Done, done);

        private void Write<T>(FrameType type, T body)
        {
            if (_closed)
            {
                return;
            }
            try
            {
                try
                {
                    OracleFrames.Write(stream, type, body);
                }
                catch (OracleProtocolException ex)
                {
                    // A body over the frame limit: say so instead.
                    OracleFrames.Write(stream, FrameType.Error, new ErrorBody("protocol", ex.Message));
                }
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                _closed = true;
            }
        }

        public void Close()
        {
            lock (_lock)
            {
                _closed = true;
                stream.Dispose();
            }
        }
    }
}
