using System.Text;

namespace CalcDb.Storage.Windows;

/// <summary>
/// The lock-file slot protocol as ACE uses it for shared sessions (W-10/native-readers-only; R-LOCK-2 RO-NATIVE and
/// RO-PAIR, observed in w-10-readers-only/lock-ranges-shared-readonly and lock-ranges-two-readers): the companion
/// file is the database name with <c>.laccdb</c> (ACCDB) or <c>.ldb</c> (MDB) beside it; a session holds a one-byte
/// lock at <see cref="SlotLockBase"/> + n for its slot n (from 1) and writes its 64-byte record at (n - 1) * 64: the
/// computer name and the user name, each a NUL-terminated ANSI string space-padded to 32 bytes. A departed session's
/// record stays and its slot is reused once its lock byte is free; the last session to leave deletes the file.
/// Exclusive opens use no lock file (R-LOCK-1 EX-PROBE). The database file itself carries no byte-range locks.
/// Not yet: the table and page locks a recordset adds (0x40005401 and 0x50005401 for a table-type recordset, one
/// observation), the MDB form (unobserved), and folder permissions (T-LOCK-11).
/// </summary>
public sealed class LockFile : IDisposable
{
    public const ulong SlotLockBase = 0x1000_0000;
    public const int RecordBytes = 64;
    public const int NameBytes = 32;

    /// <summary>The slots a session may take; the observed protocol counts from 1 and has no observed upper bound.</summary>
    public const int MaxSlots = 255;

    private readonly WindowsFile _file;
    private readonly LockReceipt _slotLock;

    private LockFile(string path, WindowsFile file, int slot, LockReceipt slotLock)
    {
        Path = path;
        _file = file;
        Slot = slot;
        _slotLock = slotLock;
    }

    public string Path { get; }

    /// <summary>The slot this session holds, from 1.</summary>
    public int Slot { get; }

    public static string PathFor(string databasePath) =>
        System.IO.Path.ChangeExtension(databasePath, databasePath.EndsWith(".mdb", StringComparison.OrdinalIgnoreCase) ? ".ldb" : ".laccdb");

    /// <summary>
    /// Join the database's lock file: open or create it, take the first slot whose lock byte is free, and write this
    /// session's record there. Null with the Win32 error when the file cannot be opened or written, or no slot is free.
    /// </summary>
    public static LockFile? Join(string databasePath, string computerName, string userName, out int? nativeError)
    {
        nativeError = null;
        string path = PathFor(databasePath);
        OpenResult opened = WindowsFile.OpenOrCreate(path, Admission.SharedReadWrite);
        if (opened.File is not { } file)
        {
            nativeError = opened.NativeError;
            return null;
        }
        for (int slot = 1; slot <= MaxSlots; slot++)
        {
            LockReceipt receipt = file.Lock(SlotLockBase + (ulong)slot, 1, exclusive: true, failImmediately: true);
            if (receipt.Outcome != LockOutcome.Granted)
            {
                continue;
            }
            byte[] record = Record(computerName, userName);
            TransferReceipt written = file.WriteAt((ulong)((slot - 1) * RecordBytes), record);
            if (written.Status != TransferStatus.Complete)
            {
                file.Unlock(receipt);
                file.Dispose();
                nativeError = written.NativeError ?? -1;
                return null;
            }
            return new LockFile(path, file, slot, receipt);
        }
        file.Dispose();
        nativeError = -1;
        return null;
    }

    /// <summary>The 64-byte record: two NUL-terminated ANSI names, each space-padded to 32 bytes (longer names are cut).</summary>
    public static byte[] Record(string computerName, string userName)
    {
        byte[] record = new byte[RecordBytes];
        Array.Fill(record, (byte)' ');
        Field(record.AsSpan(0, NameBytes), computerName);
        Field(record.AsSpan(NameBytes, NameBytes), userName);
        return record;
    }

    private static void Field(Span<byte> field, string name)
    {
        byte[] bytes = Encoding.Latin1.GetBytes(name);
        int n = Math.Min(bytes.Length, field.Length - 1);
        bytes.AsSpan(0, n).CopyTo(field);
        field[n] = 0;
    }

    /// <summary>
    /// Leave: release the slot lock and close; when no other slot is held, delete the file, as the last ACE session does.
    /// A file that cannot be deleted (another opener holds it) is left in place.
    /// </summary>
    public void Dispose()
    {
        _file.Unlock(_slotLock);
        bool othersHold = false;
        for (int slot = 1; slot <= MaxSlots && !othersHold; slot++)
        {
            LockReceipt probe = _file.Lock(SlotLockBase + (ulong)slot, 1, exclusive: true, failImmediately: true);
            if (probe.Outcome == LockOutcome.Granted)
            {
                _file.Unlock(probe);
            }
            else
            {
                othersHold = true;
            }
        }
        _file.Dispose();
        if (!othersHold)
        {
            try
            {
                File.Delete(Path);
            }
            catch (IOException)
            {
                // Another session opened the file between the check and the delete: it stays, as theirs.
            }
            catch (UnauthorizedAccessException)
            {
                // A read-only lock file or folder: left in place (T-LOCK-11, EX-PERM).
            }
        }
    }
}
