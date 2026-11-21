namespace CalcDb.Storage.Windows;

/// <summary>
/// The native realization of the spill workspace (II-03 §5.2): an owned temporary file opened exclusively through
/// <see cref="WindowsFile"/>, holding a <see cref="PositionedSpillStore"/>. It is live workspace, never the database
/// and never replayed: disposing closes and deletes it. A deferred store (<see cref="Deferred"/>) creates its file at the
/// first spill, so a session that never spills never touches the temporary folder; a file that cannot be created is a
/// failed spill write, which leaves the resident image authoritative.
/// </summary>
public sealed class TemporarySpillFile : ISpillStore, IDisposable
{
    private readonly Lock _lock = new();
    private WindowsFile? _file;
    private PositionedSpillStore? _store;
    private bool _disposed;

    private TemporarySpillFile(string path, int payloadSize)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(payloadSize, 1);
        Path = path;
        PayloadSize = payloadSize;
    }

    /// <summary>The file's path, chosen at construction; the file exists from the first spill (or from <see cref="Create"/>).</summary>
    public string Path { get; }

    public int PayloadSize { get; }

    public int LiveRecords
    {
        get
        {
            lock (_lock)
            {
                return _store?.LiveRecords ?? 0;
            }
        }
    }

    /// <summary>A store whose file exists from now on.</summary>
    public static TemporarySpillFile Create(int payloadSize, string? directory = null)
    {
        TemporarySpillFile spill = Deferred(payloadSize, directory);
        if (spill.Open() is { } failure)
        {
            throw new IOException(failure);
        }
        return spill;
    }

    /// <summary>A store whose file is created at the first spill write.</summary>
    public static TemporarySpillFile Deferred(int payloadSize, string? directory = null) =>
        new(System.IO.Path.Combine(directory ?? System.IO.Path.GetTempPath(), $"calcdb-spill-{Guid.NewGuid():N}.tmp"), payloadSize);

    public SpillWriteResult Write(in SpillRecordIdentity identity, ReadOnlyMemory<byte> payload)
    {
        PositionedSpillStore store;
        lock (_lock)
        {
            if (Open() is { } failure)
            {
                return new SpillWriteResult(null, failure);
            }
            store = _store!;
        }
        return store.Write(identity, payload);
    }

    public SpillReadResult Read(SpillLocator locator, in SpillRecordIdentity expected, Memory<byte> payload)
    {
        PositionedSpillStore? store;
        lock (_lock)
        {
            store = _store;
        }
        return store is null
            ? new SpillReadResult(SpillReadStatus.UnknownLocator, $"No live spill record at slot {locator.Slot}: the spill file was never created.")
            : store.Read(locator, expected, payload);
    }

    public void Release(SpillLocator locator)
    {
        PositionedSpillStore? store;
        lock (_lock)
        {
            store = _store;
        }
        store?.Release(locator);
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            if (_file is not null)
            {
                _file.Dispose();
                File.Delete(Path);
            }
        }
    }

    /// <summary>Create the file if it does not exist yet; null on success, else the reason. Under the lock or before publication.</summary>
    private string? Open()
    {
        if (_disposed)
        {
            return "The spill file is disposed.";
        }
        if (_store is not null)
        {
            return null;
        }
        OpenResult opened = WindowsFile.Create(Path, Admission.Exclusive);
        if (opened.File is not { } file)
        {
            return $"The spill file {Path} could not be created (native error {opened.NativeError}).";
        }
        _file = file;
        _store = new PositionedSpillStore(file, PayloadSize);
        return null;
    }
}
