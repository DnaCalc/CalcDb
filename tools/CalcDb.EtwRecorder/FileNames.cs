namespace CalcDb.EtwRecorder;

/// <summary>
/// R-CRASH-1 CRASH-ORDER: which file a Kernel-File event belongs to. An event names its file only on Create, NameCreate
/// and rundown; Read, Write, Cleanup and Close carry a FileObject and (except Close) a FileKey. The system's paging
/// writes (the lazy writer, flushes, the mapped-page writer) come on the cache manager's stream file object, which is
/// never created through IRP_MJ_CREATE and so never named; it shares the FileKey (the file's FsContext) with the file
/// objects the callers opened. So the file key is the reliable identity, and the file object only a fallback.
/// Two rules keep a stale name from misattributing an event:
/// <list type="bullet">
/// <item>The key is looked up before the object. A freed FileObject address is soon reused, notably by a stream file
/// object, and would otherwise resolve to the name of the previous owner.</item>
/// <item>A Close drops the object's name, because the object is gone.</item>
/// </list>
/// The class holds no kernel types, so the decision is unit-testable on synthetic sequences.
/// </summary>
internal sealed class FileNames
{
    private readonly Dictionary<ulong, string> _byObject = [];
    private readonly Dictionary<ulong, string> _byKey = [];

    /// <summary>Events whose file could not be named, by reason.</summary>
    public int Unnamed { get; private set; }

    /// <summary>The unnamed events that were writes (cached or paging): the part of a trace that would be missing.</summary>
    public int UnnamedWrites { get; private set; }

    /// <summary>The name of the file the event belongs to, or null when no event has named it yet.</summary>
    /// <param name="eventName">The event's name, as the provider's manifest gives it (Create, Write, Close, ...).</param>
    /// <param name="fileObject">The event's FileObject, or 0 if it has none.</param>
    /// <param name="fileKey">The event's FileKey, or 0 if it has none.</param>
    /// <param name="name">The FileName the event itself carries, or null.</param>
    public string? Resolve(string eventName, ulong fileObject, ulong fileKey, string? name)
    {
        string? known = name;
        if (name is not null)
        {
            // An event that names a file is authoritative; it replaces whatever the address meant before.
            if (fileObject != 0)
            {
                _byObject[fileObject] = name;
            }
            if (fileKey != 0)
            {
                _byKey[fileKey] = name;
            }
        }
        else
        {
            known = fileKey != 0 && _byKey.TryGetValue(fileKey, out string? byKey) ? byKey
                : fileObject != 0 && _byObject.TryGetValue(fileObject, out string? byObject) ? byObject
                : null;
            if (known is not null)
            {
                // Learn the other identity of the same file: a stream file object, or a key first seen on a Write.
                if (fileKey != 0)
                {
                    _byKey.TryAdd(fileKey, known);
                }
                if (fileObject != 0)
                {
                    _byObject[fileObject] = known;
                }
            }
        }
        if (fileObject != 0 && string.Equals(eventName, "Close", StringComparison.OrdinalIgnoreCase))
        {
            _byObject.Remove(fileObject);
        }
        if (known is null)
        {
            Unnamed++;
            if (string.Equals(eventName, "Write", StringComparison.OrdinalIgnoreCase))
            {
                UnnamedWrites++;
            }
        }
        return known;
    }

    /// <summary>
    /// <c>calcdb-etw-recorder --selftest</c>: the decision on synthetic sequences (the tools have no test project).
    /// Returns the first failure, or null.
    /// </summary>
    internal static string? SelfTest()
    {
        const string db = @"\Device\HarddiskVolume3\w\a.accdb";
        const string log = @"\Device\HarddiskVolume3\w\receipts\trace.jsonl";
        string? Check(string what, string? got, string? want) => got == want ? null : $"{what}: got {got ?? "null"}, want {want ?? "null"}";

        // 1. The lazy writer's stream file object is unnamed, shares the key of a named file object, and resolves by key.
        var n = new FileNames();
        n.Resolve("Create", 0x10, 0, db);
        n.Resolve("Write", 0x10, 0xA0, null); // first Write teaches the key
        string? r = Check("stream object resolves by key", n.Resolve("Write", 0x99, 0xA0, null), db);
        if (r is not null)
        {
            return r;
        }

        // 2. A NameCreate (key and name, no object) names the key before any caller event.
        n = new FileNames();
        n.Resolve("NameCreate", 0, 0xB0, db);
        if ((r = Check("NameCreate names the key", n.Resolve("Write", 0x77, 0xB0, null), db)) is not null)
        {
            return r;
        }

        // 3. A stale object name is not used for a stream file object that reuses the address: key wins, and Close forgets it.
        n = new FileNames();
        n.Resolve("Create", 0x55, 0, log);
        n.Resolve("Write", 0x55, 0xC0, null);
        n.Resolve("Close", 0x55, 0, null);
        n.Resolve("NameCreate", 0, 0xD0, db);
        if ((r = Check("reused object address after Close", n.Resolve("Write", 0x55, 0xD0, null), db)) is not null)
        {
            return r;
        }
        n = new FileNames();
        n.Resolve("Create", 0x56, 0, log);
        n.Resolve("Write", 0x56, 0xC1, null);
        n.Resolve("NameCreate", 0, 0xD1, db);
        if ((r = Check("reused object address without Close: key first", n.Resolve("Write", 0x56, 0xD1, null), db)) is not null)
        {
            return r;
        }

        // 4. A file never named stays unnamed and is counted.
        n = new FileNames();
        if ((r = Check("never named", n.Resolve("Write", 0x1, 0xE0, null), null)) is not null || n.Unnamed != 1 || n.UnnamedWrites != 1)
        {
            return r ?? "unnamed count";
        }

        // 5. A key first seen on a Write with a named object is learnt for later unnamed objects.
        n = new FileNames();
        n.Resolve("Create", 0x20, 0, db);
        n.Resolve("Read", 0x20, 0xF0, null);
        n.Resolve("Close", 0x20, 0, null);
        return Check("key survives the caller's close", n.Resolve("Write", 0x30, 0xF0, null), db);
    }
}
