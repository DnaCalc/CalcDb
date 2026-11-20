using System.ComponentModel;
using System.Runtime.Versioning;
using CalcDb.Evidence;

namespace CalcDb.Oracle.Runner;

internal static partial class OwnedWorker
{
    /// <summary>
    /// A long-lived worker (oracle service step 1) under the same conduct as an attempt's worker (OR-OWN, OR-DESKTOP):
    /// started by this runner, suspended, on the private desktop, put in its own kill-on-close job before it is resumed,
    /// and greeted only then. A worker the job cannot hold is ended before it runs anything. Every receive samples the
    /// window guard; windows and dialogs seen while a question runs go to that question's owner report.
    /// </summary>
    [SupportedOSPlatform("windows")]
    internal sealed class PooledConnection : IWorkerConnection
    {
        private const string Ownership = "retained process handle + job object (kill on close), long-lived worker";
        private readonly JobObject _job;
        private readonly PrivateDesktop.Launched _process;
        private readonly FileStream _stdin;
        private readonly FramePump _pump;
        private readonly string? _dialogsPath;
        private readonly HashSet<int> _pids;
        private readonly Dictionary<int, SortedSet<string>> _onUser = [];
        private readonly Dictionary<int, SortedSet<string>> _onPrivate = [];
        private readonly HashSet<(nint, string)> _described = [];
        private readonly List<DialogObservation> _dialogs = [];
        private string? _attemptDir;

        public PooledConnection(string workerPath, string poolDir, string sessionId, string? dialogsPath)
        {
            Directory.CreateDirectory(poolDir);
            _dialogsPath = dialogsPath;
            _job = JobObject.CreateKillOnClose();
            _process = PrivateDesktop.StartSuspended(workerPath, ["--serve"], poolDir, Path.Combine(poolDir, sessionId + ".log"), PrivateDesktop.Name, pipeStdout: true);
            if (!_job.TryAssign(_process.Process, out int error))
            {
                _process.Terminate(1);
                _ = _process.Wait(Timeout.InfiniteTimeSpan);
                _process.Dispose();
                _job.Dispose();
                throw new Win32Exception(error, "the long-lived worker could not be put in its kill-on-close job; it was ended before it ran");
            }
            _pids = [_process.ProcessId];
            _stdin = new FileStream(_process.StdinWrite, FileAccess.Write);
            _pump = new FramePump(new FileStream(_process.StdoutRead!, FileAccess.Read));
            _process.Resume();
        }

        public int ProcessId => _process.ProcessId;

        public void Begin(string attemptDirectory) => _attemptDir = attemptDirectory;

        public void Send(Frame frame) => OracleFrames.Write(_stdin, frame);

        public Received Receive(TimeSpan wait)
        {
            Sample();
            return _pump.Receive(wait);
        }

        private void Sample()
        {
            (List<string> user, List<string> priv) = PrivateDesktop.WindowsOf(_pids);
            Collect(user, _onUser);
            Collect(priv, _onPrivate);
            if (_attemptDir is not null)
            {
                // No dialog policy is admitted to a long-lived worker: a dialog is described, and the deadline ends it.
                HandleDialogs(_dialogsPath, _attemptDir, PrivateDesktop.Name, null, [], _job, _pids, _described, _dialogs);
            }
        }

        public void Kill()
        {
            _process.Terminate(1);
            _ = _process.Wait(Timeout.InfiniteTimeSpan);
        }

        public bool WaitExit(TimeSpan timeout) => _process.Wait(timeout);

        public OwnerReport Report(bool timedOut, string? channelError)
        {
            Sample();
            bool exited = _process.Wait(TimeSpan.Zero);
            static string[] All(Dictionary<int, SortedSet<string>> seen) => [.. seen.Values.SelectMany(w => w).Order(StringComparer.Ordinal)];
            var report = new OwnerReport
            {
                Launched = true,
                ProcessId = _process.ProcessId,
                Ownership = Ownership,
                ExitCode = exited ? _process.ExitCode : null,
                TimedOut = timedOut,
                Killed = timedOut || channelError is not null,
                ExitObserved = exited,
                ChannelError = channelError,
                Desktop = "WinSta0\\" + PrivateDesktop.Name,
                WindowsOnUserDesktop = All(_onUser),
                WindowsOnPrivateDesktop = All(_onPrivate),
                Dialogs = _dialogs.Count > 0 ? [.. _dialogs] : null,
            };
            _onUser.Clear();
            _onPrivate.Clear();
            _dialogs.Clear();
            _attemptDir = null;
            return report;
        }

        public void Dispose()
        {
            _stdin.Dispose();
            _pump.Dispose();
            _process.Dispose();
            // Closing the job ends anything of the worker still running (kill on close).
            _job.Dispose();
        }
    }
}
