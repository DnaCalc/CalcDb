namespace CalcDb.Storage.Tests;

/// <summary>
/// A controlled page source (II-03 §8, "controlled effects"): each image's bytes are a function of its page object,
/// loads can be held at a gate, failed, and watched for the unwanted signal. A load always returns only after it has
/// "settled", like a native request.
/// </summary>
internal sealed class ControlledSource : IPageSource
{
    private int _loads;

    public ManualResetEventSlim? Gate { get; set; }

    public ManualResetEventSlim LoadStarted { get; } = new(false);

    public Func<ImageId, bool> Fails { get; set; } = _ => false;

    public bool SawUnwanted { get; private set; }

    public int Loads => _loads;

    public PageLoad Load(ImageId image, Memory<byte> frame, CancellationToken unwanted)
    {
        Interlocked.Increment(ref _loads);
        LoadStarted.Set();
        if (Gate is { } gate)
        {
            // The "native request" stays outstanding until released, even when unwanted: it must settle first.
            WaitHandle.WaitAny([gate.WaitHandle, unwanted.WaitHandle]);
            SawUnwanted |= unwanted.IsCancellationRequested;
            gate.Wait(CancellationToken.None);
        }
        if (Fails(image))
        {
            return new PageLoad(false, "controlled failure");
        }
        frame.Span.Fill(Marker(image));
        return PageLoad.Complete;
    }

    public static byte Marker(ImageId image) => (byte)((image.Page.PageNumber * 7) + (image.Page.AllocationIncarnation * 3) + 1);
}

/// <summary>A scripted positioned file: each read returns the next scripted step, for ReadFullPage's cases (T-PAGER-06).</summary>
internal sealed class ScriptedFile(params ScriptedFile.Step[] steps) : IPositionedFile
{
    private int _next;

    public sealed record Step(TransferStatus Status, int Bytes);

    public ulong Incarnation => 1;

    public FileIdentity Identity { get; } = new("test", "scripted", "controlled");

    public int Reads => _next;

    public TransferReceipt ReadAt(ulong offset, Memory<byte> buffer, CancellationToken cancellation = default)
    {
        Step step = steps[_next++];
        int bytes = Math.Min(step.Bytes, buffer.Length + 1); // allows a deliberate over-claim for the violation case
        buffer.Span[..Math.Min(bytes, buffer.Length)].Fill(0xAB);
        return new TransferReceipt
        {
            FileIncarnation = 1,
            RequestId = (ulong)_next,
            RequestGeneration = 1,
            Attempted = new ByteRange(offset, offset + (ulong)buffer.Length),
            Status = step.Status,
            BackendReportedCount = (ulong)bytes,
            ProvenPrefixBytes = (ulong)bytes,
        };
    }

    public TransferReceipt WriteAt(ulong offset, ReadOnlyMemory<byte> buffer, CancellationToken cancellation = default) => throw new NotSupportedException();

    public FileOperationResult Flush() => FileOperationResult.Ok("flush");

    public ulong GetExtent() => 0;

    public FileOperationResult SetExtent(ulong extent) => FileOperationResult.Ok("extent");

    public void Dispose()
    {
    }
}