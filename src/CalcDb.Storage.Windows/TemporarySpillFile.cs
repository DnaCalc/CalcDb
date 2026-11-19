namespace CalcDb.Storage.Windows;

/// <summary>
/// The native realization of the spill workspace (II-03 §5.2): an owned temporary file opened exclusively through
/// <see cref="WindowsFile"/>, holding a <see cref="PositionedSpillStore"/>. It is live workspace, never the database
/// and never replayed: disposing closes and deletes it.
/// </summary>
public sealed class TemporarySpillFile : IDisposable
{
    private readonly WindowsFile _file;

    private TemporarySpillFile(string path, WindowsFile file, int payloadSize)
    {
        Path = path;
        _file = file;
        Store = new PositionedSpillStore(file, payloadSize);
    }

    public string Path { get; }

    public PositionedSpillStore Store { get; }

    public static TemporarySpillFile Create(int payloadSize, string? directory = null)
    {
        string path = System.IO.Path.Combine(directory ?? System.IO.Path.GetTempPath(), $"calcdb-spill-{Guid.NewGuid():N}.tmp");
        OpenResult opened = WindowsFile.Create(path, Admission.Exclusive);
        return opened.File is { } file
            ? new TemporarySpillFile(path, file, payloadSize)
            : throw new IOException($"The spill file {path} could not be created (native error {opened.NativeError}).");
    }

    public void Dispose()
    {
        _file.Dispose();
        File.Delete(Path);
    }
}
