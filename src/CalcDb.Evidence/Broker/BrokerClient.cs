using System.IO.Pipes;

namespace CalcDb.Evidence;

/// <summary>An error frame the broker sent in reply.</summary>
public sealed class BrokerException(string code, string message) : Exception($"{code}: {message}")
{
    public string Code { get; } = code;
}

/// <summary>
/// A client of the broker over its named pipe (oracle service step 2; the platform-neutral client step 4 extends to the
/// relay's Unix socket). Requests and replies are sequential on one connection: a submit's answers are read with
/// <see cref="Receive"/> until its done frame.
/// </summary>
public sealed class BrokerClient : IDisposable
{
    private readonly NamedPipeClientStream _stream;
    private readonly FramePump _pump;

    private BrokerClient(NamedPipeClientStream stream)
    {
        _stream = stream;
        _pump = new FramePump(stream);
    }

    public int BrokerProcessId { get; private set; }

    /// <exception cref="TimeoutException">No broker listens on the pipe.</exception>
    public static BrokerClient Connect(string pipe, TimeSpan timeout, string clientName)
    {
        var stream = new NamedPipeClientStream(".", pipe, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            stream.Connect((int)timeout.TotalMilliseconds);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
        var client = new BrokerClient(stream);
        client.Send(FrameType.Hello, new HelloBody(OracleFrames.Protocol, clientName));
        HelloBody hello = client.Expect(FrameType.Hello, timeout).Parse<HelloBody>();
        if (hello.Protocol != OracleFrames.Protocol)
        {
            client.Dispose();
            throw new OracleProtocolException($"the broker speaks {hello.Protocol}, not {OracleFrames.Protocol}");
        }
        client.BrokerProcessId = hello.ProcessId ?? 0;
        return client;
    }

    public void Send<T>(FrameType type, T body) => OracleFrames.Write(_stream, type, body);

    public Received Receive(TimeSpan wait) => _pump.Receive(wait);

    /// <summary>The next frame, which must be of <paramref name="type"/>; an error frame is a <see cref="BrokerException"/>.</summary>
    public Frame Expect(FrameType type, TimeSpan wait)
    {
        Received r = _pump.Receive(wait);
        if (r.Status != ReceiveStatus.Frame)
        {
            throw new IOException($"no {type} frame from the broker ({r.Status}{(r.Error is null ? "" : ": " + r.Error)})");
        }
        if (r.Frame.Type == FrameType.Error)
        {
            ErrorBody error = r.Frame.Parse<ErrorBody>();
            throw new BrokerException(error.Code, error.Message);
        }
        return r.Frame.Type == type ? r.Frame : throw new OracleProtocolException($"expected {type}, got {r.Frame.Type}");
    }

    public BrokerStatus Status(TimeSpan wait)
    {
        Send(FrameType.Status, new SeqBody(0));
        return Expect(FrameType.Status, wait).Parse<BrokerStatus>();
    }

    public void Shutdown(TimeSpan wait)
    {
        Send(FrameType.Shutdown, new SeqBody(0));
        _ = Expect(FrameType.Done, wait);
    }

    /// <summary>Put content by its SHA-256, resuming where the broker's copy ends; returns the digest.</summary>
    public string Put(byte[] content, TimeSpan wait)
    {
        string sha = Hashing.Sha256Hex(content);
        long offset = 0;
        while (true)
        {
            int length = (int)Math.Min(BrokerFrames.ChunkBytes, content.Length - offset);
            Send(FrameType.Put, new PutBody(sha, offset, content.Length, Convert.ToBase64String(content, (int)offset, length)));
            PutAck ack = Expect(FrameType.Put, wait).Parse<PutAck>();
            if (ack.Complete)
            {
                return sha;
            }
            if (ack.Received < 0 || ack.Received > content.Length)
            {
                throw new OracleProtocolException($"put of {sha}: the broker holds {ack.Received} of {content.Length} bytes");
            }
            offset = ack.Received;
        }
    }

    /// <summary>Get content by SHA-256 in chunks and verify it; null when the broker has none.</summary>
    public byte[]? Get(string sha256, TimeSpan wait)
    {
        using var buffer = new MemoryStream();
        long total = -1;
        while (total < 0 || buffer.Length < total)
        {
            Send(FrameType.Get, new GetBody(sha256, buffer.Length));
            GetChunk chunk;
            try
            {
                chunk = Expect(FrameType.Get, wait).Parse<GetChunk>();
            }
            catch (BrokerException ex) when (ex.Code == "not-found")
            {
                return null;
            }
            total = chunk.Total;
            byte[] data = Convert.FromBase64String(chunk.Data);
            if (chunk.Offset != buffer.Length || (data.Length == 0 && buffer.Length < total))
            {
                throw new OracleProtocolException($"get of {sha256}: chunk at {chunk.Offset} of {total}, expected {buffer.Length}");
            }
            buffer.Write(data);
        }
        byte[] bytes = buffer.ToArray();
        string actual = Hashing.Sha256Hex(bytes);
        return actual == sha256 ? bytes : throw new OracleProtocolException($"get of {sha256}: the content hashes to {actual}");
    }

    public void Dispose()
    {
        try
        {
            if (_stream.IsConnected)
            {
                OracleFrames.Write(_stream, FrameType.Done, new SeqBody(0));
            }
        }
        catch (IOException)
        {
            // Already gone.
        }
        _pump.Dispose();
    }
}
