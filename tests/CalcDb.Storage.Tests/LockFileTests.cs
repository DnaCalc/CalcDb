using CalcDb.Storage.Windows;

namespace CalcDb.Storage.Tests;

/// <summary>
/// W-10/native-readers-only: the lock-file slot protocol ACE's shared sessions use (w-10-readers-only/lock-ranges-*):
/// slot bytes at 0x10000000 + n, 64-byte records, departed records kept, the last leaver deletes the file.
/// </summary>
public sealed class LockFileTests
{
    [Fact]
    public void Two_sessions_take_slots_one_and_two_and_the_last_to_leave_deletes_the_file()
    {
        string dir = Path.Combine(Path.GetTempPath(), "calcdb-lockfile-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string database = Path.Combine(dir, "t.accdb");
        File.WriteAllBytes(database, new byte[4096]);
        try
        {
            LockFile a = LockFile.Join(database, "HOST-A", "admin", out _)!;
            LockFile b = LockFile.Join(database, "HOST-B", "admin", out _)!;
            Assert.Equal((1, 2), (a.Slot, b.Slot));
            Assert.Equal(Path.Combine(dir, "t.laccdb"), a.Path);
            byte[] image = ReadShared(a.Path);
            Assert.Equal(128, image.Length);
            Assert.Equal(LockFile.Record("HOST-A", "admin"), image[..64]);
            Assert.Equal(LockFile.Record("HOST-B", "admin"), image[64..]);
            b.Dispose();
            Assert.True(File.Exists(a.Path)); // a still holds its slot; b's record stays
            Assert.Equal(128, ReadShared(a.Path).Length);
            a.Dispose();
            Assert.False(File.Exists(a.Path));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void The_record_is_two_nul_terminated_space_padded_32_byte_names()
    {
        byte[] r = LockFile.Record("GOVERT-OMEN", "admin");
        Assert.Equal("474F564552542D4F4D454E00" + string.Concat(Enumerable.Repeat("20", 20)) + "61646D696E00" + string.Concat(Enumerable.Repeat("20", 26)), Convert.ToHexString(r));
    }

    private static byte[] ReadShared(string path)
    {
        using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        byte[] buffer = new byte[s.Length];
        s.ReadExactly(buffer);
        return buffer;
    }
}
