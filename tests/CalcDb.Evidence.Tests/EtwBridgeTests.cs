using CalcDb.Oracle.Runner;

namespace CalcDb.Evidence.Tests;

/// <summary>
/// vqs3 (R-CRASH-1 CRASH-ORDER, The Plan §2.3): the recorder creates <c>done-ID</c> and holds it open while it writes its
/// line, so <see cref="EtwBridge.Stop"/> must wait for it, and when it never comes the status names the reason (the sealer
/// turns any status but "recorder: ok" into an Incomplete capture) instead of an exception leaving the attempt Running.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class EtwBridgeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "etwbridge-" + Guid.NewGuid().ToString("N"));

    public EtwBridgeTests() => Directory.CreateDirectory(Path.Combine(_root, "queue"));

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Status() => File.ReadAllText(Path.Combine(_root, "attempt", "artifacts", EtwBridge.StatusFile));

    private EtwBridge Bridge(TimeSpan timeout) => EtwBridge.Attach(Path.Combine(_root, "attempt"), Path.Combine(_root, "queue"), "abc", timeout);

    private string Done => Path.Combine(_root, "queue", "done-abc");

    [Fact]
    public async Task Stop_waits_while_the_recorder_still_holds_the_done_file_and_then_reads_it()
    {
        EtwBridge bridge = Bridge(TimeSpan.FromSeconds(20));
        // Like File.WriteAllText in the recorder: the file exists, empty and denying readers, before its line is written.
        var held = new FileStream(Done, FileMode.Create, FileAccess.Write, FileShare.None);
        var releaser = Task.Run(() =>
        {
            Thread.Sleep(500);
            held.Write("ok 12 events (0 unnamed)"u8);
            held.Dispose();
        });
        var clock = System.Diagnostics.Stopwatch.StartNew();
        bridge.Stop();
        await releaser;
        Assert.True(clock.ElapsedMilliseconds >= 400, "Stop returned before the recorder released the file");
        Assert.Equal("recorder: ok 12 events (0 unnamed)", Status());
    }

    [Fact]
    public async Task Stop_waits_for_the_line_when_the_file_is_open_for_sharing_but_still_empty()
    {
        EtwBridge bridge = Bridge(TimeSpan.FromSeconds(20));
        var held = new FileStream(Done, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
        var writer = Task.Run(() =>
        {
            Thread.Sleep(300);
            held.Write("ok 3 events (0 unnamed)"u8);
            held.Dispose();
        });
        bridge.Stop();
        await writer;
        Assert.Equal("recorder: ok 3 events (0 unnamed)", Status());
    }

    [Fact]
    public void Stop_that_never_gets_the_file_writes_a_named_reason_and_does_not_throw()
    {
        EtwBridge bridge = Bridge(TimeSpan.FromMilliseconds(600));
        using var held = new FileStream(Done, FileMode.Create, FileAccess.Write, FileShare.None);
        bridge.Stop();
        string status = Status();
        Assert.StartsWith("recorder did not finish in 0.6 s after stop (done file stayed locked:", status, StringComparison.Ordinal);
        Assert.False(status.StartsWith("recorder: ok", StringComparison.Ordinal)); // the sealer's test for a complete capture
    }

    [Fact]
    public void Stop_without_a_done_file_names_the_missing_file()
    {
        Bridge(TimeSpan.FromMilliseconds(300)).Stop();
        Assert.Contains("done file never appeared", Status(), StringComparison.Ordinal);
    }
}
