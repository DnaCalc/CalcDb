using System.Runtime.InteropServices;

using CalcDb.Catalog;
using CalcDb.Com.Com;
using CalcDb.Com.Dao;
using CalcDb.Com.Declarations;
using CalcDb.Com.Host;
using CalcDb.Format;
using CalcDb.Foundation;
using CalcDb.Isam;
using CalcDb.Storage;
using CalcDb.Values;

namespace CalcDb.Com.Tests;

/// <summary>
/// DAO-level replays of:
/// <list type="bullet">
/// <item><c>nv5x</c>: direct page-1 frees on SQL DELETE freeing a chained long value
/// (<c>evidence/r-q22-lv-open/lv-free-timing-delete-chain</c>, <c>evidence/r-q19-growth-open/lv-op-delete-row-chain</c>)
/// do not trigger a Close commit (<c>page 0 +3586</c> rises by <c>+1</c> and Close writes nothing).</item>
/// <item><c>zfrp</c> stretch 6: SQL UPDATE freeing a chained long value
/// (<c>evidence/r-q22-lv-open/lv-free-timing-update-chain-smaller</c>, <c>lv-free-timing-update-chain-to-single</c>,
/// <c>evidence/r-q19-growth-open/lv-op-chain-larger</c>, <c>lv-op-chain-smaller</c>, <c>lv-op-chain-to-single</c>,
/// <c>lv-op-chain-null</c>) frees the old chain in the LV owned map and page 1 during the statement, takes the 4-byte
/// page-1 reservation while open (<c>+1</c>), and returns the unused reserved bits at Close (<c>+2</c> total).</item>
/// <item><c>jh6s</c>: <c>Database.Close</c> consults <c>TransactionCoordinator.NeedsCloseCommit(Cache)</c> per
/// session cache rather than comparing page-1 images at open vs close (per-database isolation in one Workspace,
/// rolled-back allocations do not trigger a Close commit, and direct page-1 frees do not trigger a Close commit).</item>
/// <item>Refusal by name: DAO dynaset <c>Edit</c>/<c>Update</c> freeing a chained Memo refuses by name without
/// modifying the file (<c>R-FMT-2 Open</c>, <c>r-fmt-2b/mm-many-pages-*</c>).</item>
/// </list>
/// </summary>
public sealed unsafe class DaoLongValueAndCloseCommitTests : IDisposable
{
    private const string DbLangGeneral = ";LANGID=0x0409;CP=1252;COUNTRY=0";
    private const int DbVersion120 = 128;
    private const int DbFailOnError = 128;
    private const int DbOpenDynaset = 2;
    private static readonly FormatDescriptor D = FormatDescriptor.Jet4Family;

    private readonly string _dir = Directory.CreateTempSubdirectory("calcdb-dao-lv-close-").FullName;
    private readonly List<nint> _cells = [];
    private readonly List<nint> _held = [];
    private readonly EngineState _engine = new(new HostBinding(0xDA0_C7E6, Standalone: true, 0, 0, 0, 0, 0));

    public void Dispose()
    {
        for (int i = _held.Count - 1; i >= 0; i--)
        {
            Marshal.Release(_held[i]);
        }
        foreach (nint cell in _cells)
        {
            var v = (NativeVariant*)cell;
            if ((VarType)v->Vt == VarType.Bstr)
            {
                Marshal.FreeBSTR(v->Pointer);
            }
            NativeMemory.Free((void*)cell);
        }
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private DBEngineObject CreateEngine()
    {
        nint engine;
        Assert.Equal(0, DBEngineObject.Create(_engine.Binding, 0, NativeRoute.IidIDispatch, &engine));
        _held.Add(engine);
        return ComObject.Recover<DBEngineObject>(engine);
    }

    private nint Arg(object? value)
    {
        var v = (NativeVariant*)NativeMemory.AllocZeroed((nuint)sizeof(NativeVariant));
        switch (value)
        {
            case int i:
                v->Vt = (ushort)VarType.I4;
                v->Int64 = (uint)i;
                break;
            case bool b:
                v->Vt = (ushort)VarType.Bool;
                v->Int64 = b ? 0xFFFF : 0;
                break;
            case string text:
                v->Vt = (ushort)VarType.Bstr;
                v->Pointer = Marshal.StringToBSTR(text);
                break;
            case DBNull:
                v->Vt = (ushort)VarType.Null;
                break;
        }
        _cells.Add((nint)v);
        return (nint)v;
    }

    private CallResult Call(DispatchObject target, string member, InvokeKind kind, params object?[] args)
    {
        nint[] cells = [.. args.Select(Arg)];
        return target.Run(target.Declaration.Member(member, kind), cells);
    }

    private CallResult Get(DispatchObject target, string member, params object?[] args) => Call(target, member, InvokeKind.PropertyGet, args);

    private CallResult Put(DispatchObject target, string member, params object?[] args) => Call(target, member, InvokeKind.PropertyPut, args);

    private CallResult Method(DispatchObject target, string member, params object?[] args) => Call(target, member, InvokeKind.Method, args);

    private static CallResult Ok(CallResult result)
    {
        Assert.True(result.Failure is null, result.Failure?.Description);
        return result;
    }

    private T Recover<T>(CallResult result) where T : ComObject
    {
        Assert.True(result.HasScalar && (VarType)result.Scalar.Vt == VarType.Dispatch, result.Failure?.Description);
        _held.Add(result.Scalar.Pointer);
        return ComObject.Recover<T>(result.Scalar.Pointer);
    }

    private static int RecordsAffected(DatabaseObject db)
    {
        CallResult result = Ok(db.Run(db.Declaration.Member("RecordsAffected", InvokeKind.PropertyGet), []));
        Assert.Equal(VarType.I4, (VarType)result.Scalar.Vt);
        return (int)result.Scalar.Int64;
    }

    private static byte[] ReadPage(PageCache cache, ulong page)
    {
        using PagePin pin = cache.Pin(page).Pin!;
        return pin.Bytes.ToArray();
    }

    private static byte[] SnapshotOpenFile(DatabaseObject db)
    {
        ulong pages = db.PageCount;
        byte[] image = new byte[(int)pages * D.PageSize];
        for (ulong p = 0; p < pages; p++)
        {
            using PagePin pin = db.Cache.Pin(p).Pin!;
            pin.Bytes.CopyTo(image.AsSpan((int)p * D.PageSize, D.PageSize));
        }
        return image;
    }

    private static bool MapBit(ReadOnlySpan<byte> page, ushort slot, uint pageNumber)
    {
        Assert.True(MapPages.TryInlineBit(page, slot, pageNumber, D, out int offset, out byte mask, out string? failure), failure);
        return (page[offset] & mask) != 0;
    }

    private static bool GlobalFreeBit(ReadOnlySpan<byte> page1, uint pageNumber) =>
        MapBit(page1, D.Map.GlobalFreeSlot, pageNumber);

    private static uint CommitCounter(ReadOnlySpan<byte> page0) =>
        BitConverter.ToUInt32(page0.Slice(D.Header.CommitCounter.Offset, 4));

    /// <summary>
    /// Build a database with table <c>V (ID LONG, V MEMO)</c>, <c>PRIMARY KEY (ID)</c>, and three rows matching the
    /// <c>r-q19-growth-open</c> / <c>r-q22-lv-open</c> shape: row 1 (50 × '€', single-row LVAL), row 2 (1000 × '€',
    /// same LVAL page), row 3 (2500 × '€', 2-page LVAL chain). Closes Session 1 and returns the path and closed layout
    /// metadata (with <c>firstFree % 8 == 1</c>, matching the records' page 49).
    /// </summary>
    private (string Path, RowLocator LvOwnedMap, uint[] ChainPages, uint SingleLvPage, uint FirstFree) CreateChainedMemoFixture(DBEngineObject eng, string fileName)
    {
        string path = Path.Combine(_dir, fileName);
        DatabaseObject db = Recover<DatabaseObject>(Ok(Method(eng, "CreateDatabase", path, DbLangGeneral, DbVersion120)));
        Ok(Method(db, "Execute", "CREATE TABLE V (ID LONG, V MEMO)", DbFailOnError));
        Ok(Method(db, "Execute", "CREATE UNIQUE INDEX PK_V ON V (ID) WITH PRIMARY", DbFailOnError));
        Ok(Method(db, "Execute", $"INSERT INTO V (ID, V) VALUES (1, '{new string('€', 50)}')", DbFailOnError));
        Ok(Method(db, "Execute", $"INSERT INTO V (ID, V) VALUES (2, '{new string('€', 1000)}')", DbFailOnError));
        Ok(Method(db, "Execute", $"INSERT INTO V (ID, V) VALUES (3, '{new string('€', 2500)}')", DbFailOnError));
        Ok(Method(db, "Close"));

        // Inspect the committed layout through a read-only open so tests verify exact chain / map pages without
        // hard-coding page numbers that depend on CreateDatabase's catalog size.
        DatabaseObject inspect = Recover<DatabaseObject>(Ok(Method(eng, "OpenDatabase", path, false, true)));
        CatalogEntry entry = inspect.Catalog.LocalTables(D).Single(e => e.Name == "V");
        TableDefinition table = TableDefDecoder.Decode(
            TableDefSegmentView.Open((uint)entry.Id, p => ReadPage(inspect.Cache, p), D, 8, out _)!,
            (uint)entry.Id, D, out _)!;
        ColumnDescriptor memo = table.Columns.Single(c => c.Name == "V");
        LvalColumnMapSpec lvMaps = table.LongValueMaps.Single(m => m.PhysicalNumber == memo.PhysicalNumber);
        uint firstFree = (uint)inspect.PageCount;

        IReadOnlyList<uint> lvOwned = UsageMaps.Pages(inspect.Cache, D, lvMaps.OwnedMap, inspect.PageCount, out TraversalFailure? ownedErr)!;
        Assert.Null(ownedErr);
        IReadOnlyList<uint> lvFree = UsageMaps.Pages(inspect.Cache, D, lvMaps.FreeMap, inspect.PageCount, out TraversalFailure? freeErr)!;
        Assert.Null(freeErr);
        Assert.Single(lvFree);
        uint singleLvPage = lvFree[0];
        uint[] chainPages = [.. lvOwned.Where(p => p != singleLvPage)];
        Ok(Method(inspect, "Close"));

        Assert.Equal(2, chainPages.Length);
        Assert.Equal(1u, firstFree % 8);
        return (path, lvMaps.OwnedMap, chainPages, singleLvPage, firstFree);
    }

    private List<(int Id, string? Text)> ReadAllRows(DBEngineObject eng, string path)
    {
        DatabaseObject db = Recover<DatabaseObject>(Ok(Method(eng, "OpenDatabase", path, false, true)));
        DispatchObject rs = Recover<DispatchObject>(Ok(Method(db, "OpenRecordset", "SELECT ID, V FROM V ORDER BY ID", DbOpenDynaset)));
        DispatchObject fields = Recover<DispatchObject>(Ok(Get(rs, "Fields")));
        DispatchObject idField = Recover<DispatchObject>(Ok(Get(fields, "Item", "ID")));
        DispatchObject vField = Recover<DispatchObject>(Ok(Get(fields, "Item", "V")));

        List<(int, string?)> rows = [];
        while (!IsTrue(Get(rs, "EOF")))
        {
            int id = (int)Ok(Get(idField, "Value")).Scalar.Int64;
            CallResult vRes = Ok(Get(vField, "Value"));
            NativeVariant v = vRes.HasScalar ? vRes.Scalar : *vRes.Value!.Pointer;
            string? text = (VarType)v.Vt == VarType.Null ? null : Marshal.PtrToStringBSTR(v.Pointer);
            rows.Add((id, text));
            Ok(Method(rs, "MoveNext"));
        }
        Ok(Method(rs, "Close"));
        Ok(Method(db, "Close"));
        return rows;
    }

    private static bool IsTrue(CallResult result)
    {
        Ok(result);
        return result.Scalar.Int64 != 0;
    }

    [Fact]
    public void Delete_freeing_chained_Memo_frees_page1_immediately_and_Close_writes_nothing()
    {
        // nv5x + jh6s (evidence/r-q22-lv-open/lv-free-timing-delete-chain, evidence/r-q19-growth-open/lv-op-delete-row-chain):
        // SQL DELETE freeing a 2-page LV chain clears the LV owned map and sets page 1's free bits during the statement
        // (+1 on page 0 +3586); because no global-map bit was cleared and nothing is queued for ReleasedAtClose,
        // Database.Close writes nothing (total +1, not +2).
        DBEngineObject eng = CreateEngine();
        var (path, lvOwnedMap, chainPages, singleLvPage, _) = CreateChainedMemoFixture(eng, "delete-chain.accdb");
        byte[] before = File.ReadAllBytes(path);
        uint counterBefore = CommitCounter(before.AsSpan(0, D.PageSize));

        DatabaseObject db = Recover<DatabaseObject>(Ok(Method(eng, "OpenDatabase", path, false, false)));
        Ok(Method(db, "Execute", "DELETE FROM V WHERE ID = 3", DbFailOnError));
        Assert.Equal(1, RecordsAffected(db));

        // While still open: counter rose by +1, chain pages are free in page 1 and cleared in LV owned map,
        // and NeedsCloseCommit is false (direct page-1 frees with Clear == 0 do not schedule a Close commit).
        byte[] openSnapshot = SnapshotOpenFile(db);
        Assert.Equal(counterBefore + 1, CommitCounter(openSnapshot.AsSpan(0, D.PageSize)));
        ReadOnlySpan<byte> openPage1 = openSnapshot.AsSpan(D.PageSize, D.PageSize);
        ReadOnlySpan<byte> openLvMap = openSnapshot.AsSpan((int)lvOwnedMap.Page * D.PageSize, D.PageSize);
        foreach (uint p in chainPages)
        {
            Assert.True(GlobalFreeBit(openPage1, p), $"page {p} should be free in page 1 immediately after DELETE");
            Assert.False(MapBit(openLvMap, lvOwnedMap.Row, p), $"page {p} should be cleared in LV owned map immediately after DELETE");
        }
        Assert.False(GlobalFreeBit(openPage1, singleLvPage));
        Assert.True(MapBit(openLvMap, lvOwnedMap.Row, singleLvPage));
        Assert.False(db.Workspace.Transactions.NeedsCloseCommit(db.Cache));

        // Close must write nothing: the closed file is byte-for-byte identical to the open snapshot.
        Ok(Method(db, "Close"));
        byte[] afterClose = File.ReadAllBytes(path);
        Assert.Equal(counterBefore + 1, CommitCounter(afterClose.AsSpan(0, D.PageSize)));
        Assert.Equal(openSnapshot, afterClose);

        // Reopen and verify rows 1 and 2 remain intact while row 3 is gone.
        List<(int Id, string? Text)> rows = ReadAllRows(eng, path);
        Assert.Equal(2, rows.Count);
        Assert.Equal((1, new string('€', 50)), rows[0]);
        Assert.Equal((2, new string('€', 1000)), rows[1]);
    }

    [Theory]
    [InlineData("lv-op-chain-larger", 3000, true, false)]
    [InlineData("lv-free-timing-update-chain-smaller", 2250, true, true)]
    [InlineData("lv-free-timing-update-chain-to-single", 50, false, true)]
    [InlineData("lv-op-chain-null", -1, false, false)]
    public void Sql_Update_freeing_chained_Memo_reserves_page1_extent_while_open_and_returns_unused_bits_at_Close(
        string caseName, int newCharCount, bool allocatesNewChain, bool hasOpenStateRecord)
    {
        // zfrp stretch 6 (evidence/r-q22-lv-open/lv-free-timing-update-chain-*, evidence/r-q19-growth-open/lv-op-chain-*):
        // SQL UPDATE freeing a chained LV frees the old chain in LV owned map and page 1 immediately during the statement
        // (+1 on page 0 +3586); for the lv-free-timing-* records (which keep open-after-op), verify the open-state
        // 4-byte page-1 reservation bits; for all four records verify the closed-state +2 counter, returned reserved
        // bits, and row readback.
        DBEngineObject eng = CreateEngine();
        var (path, lvOwnedMap, oldChain, singleLvPage, firstFree) = CreateChainedMemoFixture(eng, $"{caseName}.accdb");
        byte[] before = File.ReadAllBytes(path);
        uint counterBefore = CommitCounter(before.AsSpan(0, D.PageSize));

        string? expectedText = newCharCount < 0 ? null : new string('€', newCharCount);
        string sqlValue = expectedText is null ? "NULL" : $"'{expectedText}'";

        DatabaseObject db = Recover<DatabaseObject>(Ok(Method(eng, "OpenDatabase", path, false, false)));
        Ok(Method(db, "Execute", $"UPDATE V SET V = {sqlValue} WHERE ID = 3", DbFailOnError));
        Assert.Equal(1, RecordsAffected(db));

        // While open: counter rose by +1 and NeedsCloseCommit is true.
        byte[] openSnapshot = SnapshotOpenFile(db);
        Assert.Equal(counterBefore + 1, CommitCounter(openSnapshot.AsSpan(0, D.PageSize)));
        Assert.True(db.Workspace.Transactions.NeedsCloseCommit(db.Cache));

        uint newChainPages = allocatesNewChain ? 2u : 0u;
        uint reserveEnd = ((firstFree / 8) * 8) + 32;
        if (hasOpenStateRecord)
        {
            ReadOnlySpan<byte> openPage1 = openSnapshot.AsSpan(D.PageSize, D.PageSize);
            ReadOnlySpan<byte> openLvMap = openSnapshot.AsSpan((int)lvOwnedMap.Page * D.PageSize, D.PageSize);

            foreach (uint p in oldChain)
            {
                Assert.True(GlobalFreeBit(openPage1, p), $"{caseName}: old chain page {p} must be free in page 1 while open");
                Assert.False(MapBit(openLvMap, lvOwnedMap.Row, p), $"{caseName}: old chain page {p} must be cleared in LV owned map while open");
            }
            Assert.True(MapBit(openLvMap, lvOwnedMap.Row, singleLvPage));

            if (allocatesNewChain)
            {
                Assert.Equal(firstFree / 8, (firstFree + newChainPages - 1) / 8);
                for (uint p = firstFree; p < firstFree + newChainPages; p++)
                {
                    Assert.False(GlobalFreeBit(openPage1, p), $"{caseName}: newly allocated chain page {p} must be occupied in page 1");
                    Assert.True(MapBit(openLvMap, lvOwnedMap.Row, p), $"{caseName}: newly allocated chain page {p} must be set in LV owned map");
                }
                for (uint p = firstFree + newChainPages; p < reserveEnd; p++)
                {
                    Assert.False(GlobalFreeBit(openPage1, p), $"{caseName}: reserved page {p} must be cleared (0) in page 1 while open");
                }
            }
            else
            {
                Assert.True(GlobalFreeBit(openPage1, firstFree), $"{caseName}: firstFree {firstFree} stays free (1) when no new page was allocated");
                for (uint p = firstFree + 1; p < reserveEnd; p++)
                {
                    Assert.False(GlobalFreeBit(openPage1, p), $"{caseName}: reserved page {p} must be cleared (0) in page 1 while open");
                }
            }
            Assert.True(GlobalFreeBit(openPage1, reserveEnd), $"{caseName}: page {reserveEnd} beyond the 4-byte reservation stays free (1)");
        }

        // Close writes the closing commit (+2 total) and returns all unused reserved bits to free (1).
        Ok(Method(db, "Close"));
        byte[] afterClose = File.ReadAllBytes(path);
        Assert.Equal(counterBefore + 2, CommitCounter(afterClose.AsSpan(0, D.PageSize)));
        ReadOnlySpan<byte> closedPage1 = afterClose.AsSpan(D.PageSize, D.PageSize);
        ReadOnlySpan<byte> closedLvMap = afterClose.AsSpan((int)lvOwnedMap.Page * D.PageSize, D.PageSize);
        foreach (uint p in oldChain)
        {
            Assert.True(GlobalFreeBit(closedPage1, p));
            Assert.False(MapBit(closedLvMap, lvOwnedMap.Row, p));
        }
        Assert.True(MapBit(closedLvMap, lvOwnedMap.Row, singleLvPage));
        for (uint p = firstFree; p < firstFree + newChainPages; p++)
        {
            Assert.False(GlobalFreeBit(closedPage1, p), $"{caseName}: allocated chain page {p} stays occupied after Close");
            Assert.True(MapBit(closedLvMap, lvOwnedMap.Row, p));
        }
        for (uint p = firstFree + newChainPages; p < reserveEnd; p++)
        {
            Assert.True(GlobalFreeBit(closedPage1, p), $"{caseName}: reserved page {p} must be returned to free (1) after Close");
        }

        // Reopen and verify every row reads back identically.
        List<(int Id, string? Text)> rows = ReadAllRows(eng, path);
        Assert.Equal(3, rows.Count);
        Assert.Equal((1, new string('€', 50)), rows[0]);
        Assert.Equal((2, new string('€', 1000)), rows[1]);
        Assert.Equal((3, expectedText), rows[2]);
    }

    [Fact]
    public void Close_commit_is_tracked_per_database_cache_and_not_triggered_by_another_database_or_rolled_back_allocation()
    {
        // jh6s: TransactionCoordinator tracks global-map allocations and ReleasedAtClose per PageCache.
        // 1) In one Workspace with two open Databases, an allocation on dbA triggers Close commit only on dbA;
        //    a same-width UPDATE on dbB (no global-map allocation) writes nothing at dbB.Close.
        // 2) An allocation inside BeginTrans followed by Rollback never publishes and does not trigger a Close commit.
        DBEngineObject eng = CreateEngine();
        string pathA = Path.Combine(_dir, "sess-a.accdb");
        string pathB = Path.Combine(_dir, "sess-b.accdb");

        foreach (string p in new[] { pathA, pathB })
        {
            DatabaseObject init = Recover<DatabaseObject>(Ok(Method(eng, "CreateDatabase", p, DbLangGeneral, DbVersion120)));
            Ok(Method(init, "Execute", "CREATE TABLE T (ID LONG, N LONG)", DbFailOnError));
            Ok(Method(init, "Execute", "INSERT INTO T (ID, N) VALUES (1, 10)", DbFailOnError));
            Ok(Method(init, "Close"));
        }

        uint counterABefore = CommitCounter(File.ReadAllBytes(pathA).AsSpan(0, D.PageSize));
        uint counterBBefore = CommitCounter(File.ReadAllBytes(pathB).AsSpan(0, D.PageSize));

        DatabaseObject dbA = Recover<DatabaseObject>(Ok(Method(eng, "OpenDatabase", pathA, false, false)));
        DatabaseObject dbB = Recover<DatabaseObject>(Ok(Method(eng, "OpenDatabase", pathB, false, false)));

        Ok(Method(dbA, "Execute", "CREATE TABLE Extra (ID LONG)", DbFailOnError));
        Ok(Method(dbB, "Execute", "UPDATE T SET N = 99 WHERE ID = 1", DbFailOnError));

        Assert.True(dbA.Workspace.Transactions.NeedsCloseCommit(dbA.Cache));
        Assert.False(dbB.Workspace.Transactions.NeedsCloseCommit(dbB.Cache));
        byte[] dbBOpen = SnapshotOpenFile(dbB);

        Ok(Method(dbA, "Close"));
        Ok(Method(dbB, "Close"));

        Assert.Equal(counterABefore + 2, CommitCounter(File.ReadAllBytes(pathA).AsSpan(0, D.PageSize)));
        byte[] dbBAfterClose = File.ReadAllBytes(pathB);
        Assert.Equal(counterBBefore + 1, CommitCounter(dbBAfterClose.AsSpan(0, D.PageSize)));
        Assert.Equal(dbBOpen, dbBAfterClose);

        // Part 2: rolled-back allocation inside BeginTrans / Rollback leaves NeedsCloseCommit false and file untouched.
        var (lvPath, _, _, _, _) = CreateChainedMemoFixture(eng, "rollback-alloc.accdb");
        byte[] beforeRollback = File.ReadAllBytes(lvPath);
        DatabaseObject dbR = Recover<DatabaseObject>(Ok(Method(eng, "OpenDatabase", lvPath, false, false)));
        WorkspaceObject ws = dbR.Workspace;
        Ok(Method(ws, "BeginTrans"));
        Ok(Method(dbR, "Execute", $"UPDATE V SET V = '{new string('€', 2500)}' WHERE ID = 1", DbFailOnError));
        Ok(Method(ws, "Rollback"));

        Assert.False(ws.Transactions.NeedsCloseCommit(dbR.Cache));
        Ok(Method(dbR, "Close"));
        Assert.Equal(beforeRollback, File.ReadAllBytes(lvPath));
    }

    [Fact]
    public void Dynaset_Edit_Update_freeing_chained_Memo_refuses_by_name_without_touching_the_file()
    {
        // R-FMT-2 Open (r-fmt-2b/mm-many-pages-to-short, mm-many-pages-to-1page): DAO recordset Edit/Update
        // freeing a chained long value follows a different rule than SQL UPDATE and is refused by name.
        DBEngineObject eng = CreateEngine();
        var (path, _, _, _, _) = CreateChainedMemoFixture(eng, "dynaset-refuse.accdb");
        byte[] before = File.ReadAllBytes(path);

        DatabaseObject db = Recover<DatabaseObject>(Ok(Method(eng, "OpenDatabase", path, false, false)));
        DispatchObject rs = Recover<DispatchObject>(Ok(Method(db, "OpenRecordset", "V", DbOpenDynaset)));
        DispatchObject fields = Recover<DispatchObject>(Ok(Get(rs, "Fields")));
        DispatchObject idField = Recover<DispatchObject>(Ok(Get(fields, "Item", "ID")));
        DispatchObject vField = Recover<DispatchObject>(Ok(Get(fields, "Item", "V")));
        while (!IsTrue(Get(rs, "EOF")) && (int)Ok(Get(idField, "Value")).Scalar.Int64 != 3)
        {
            Ok(Method(rs, "MoveNext"));
        }
        Assert.False(IsTrue(Get(rs, "EOF")));

        Ok(Method(rs, "Edit"));
        Ok(Put(vField, "Value", "twenty chars of text"));
        CallResult update = Method(rs, "Update");
        Assert.NotNull(update.Failure);
        Assert.Equal(FailureKind.NotModelled, update.Failure!.Kind);
        Assert.Contains("recordset Edit/Update", update.Failure.Description, StringComparison.Ordinal);

        Assert.False(db.Workspace.Transactions.NeedsCloseCommit(db.Cache));
        Ok(Method(rs, "Close"));
        Ok(Method(db, "Close"));
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public void Refused_chain_freeing_Update_leaves_PageCount_untouched_and_allows_a_valid_Update_in_the_same_session()
    {
        // Finding 1: PageAllocator.Allocate must not advance PageCache.KnownExtentPages before publication.
        // A refused chain-freeing UPDATE (2,500B single-row payload allocating a new LVAL page past page 45's
        // remaining room) must leave db.PageCount and KnownExtentPages unchanged so a subsequent valid chain-freeing
        // UPDATE in the same session succeeds and keeps the exact expected PageCount.
        DBEngineObject eng = CreateEngine();
        var (path, _, _, _, firstFree) = CreateChainedMemoFixture(eng, "refused-then-valid.accdb");

        DatabaseObject db = Recover<DatabaseObject>(Ok(Method(eng, "OpenDatabase", path, false, false)));
        ulong pageCountBefore = db.PageCount;
        Assert.Equal(firstFree, pageCountBefore);

        CallResult refused = Method(db, "Execute", $"UPDATE V SET V = '{new string('€', 1250)}' WHERE ID = 3", DbFailOnError);
        Assert.NotNull(refused.Failure);
        Assert.Equal(FailureKind.NotModelled, refused.Failure!.Kind);
        Assert.Contains("allocates a new LVAL page", refused.Failure.Description, StringComparison.Ordinal);
        Assert.Equal(pageCountBefore, db.PageCount);
        Assert.True(db.Cache.KnownExtentPages <= pageCountBefore);
        Assert.False(db.Workspace.Transactions.NeedsCloseCommit(db.Cache));

        // A valid chain-freeing UPDATE in the same session must still succeed cleanly and keep PageCount accurate.
        Ok(Method(db, "Execute", "UPDATE V SET V = NULL WHERE ID = 3", DbFailOnError));
        Assert.Equal(1, RecordsAffected(db));
        Assert.Equal(pageCountBefore, db.PageCount);
        Assert.True(db.Workspace.Transactions.NeedsCloseCommit(db.Cache));

        Ok(Method(db, "Close"));
        List<(int Id, string? Text)> rows = ReadAllRows(eng, path);
        Assert.Equal(3, rows.Count);
        Assert.Null(rows[2].Text);
    }

    [Fact]
    public void Drop_table_defers_page1_free_to_Close_so_Drop_only_session_commits_at_Close_and_same_session_Create_takes_fresh_pages()
    {
        // Finding 3 (evidence/r-q15-ddl-open/counter-drop-pages, drop-then-create):
        // 1) A DROP TABLE-only session leaves page 1 untouched while open (+1 on page 0 +3586), queues the dropped
        //    table's TableDef and map pages in ReleasedAtClose (NeedsCloseCommit == true), and frees them at Close (+2).
        // 2) CREATE T; DROP T; CREATE U in one session allocates fresh pages for U rather than reusing T's pages,
        //    then frees T's pages in page 1 at Close.
        DBEngineObject eng = CreateEngine();
        string dropOnlyPath = Path.Combine(_dir, "drop-only.accdb");
        DatabaseObject init = Recover<DatabaseObject>(Ok(Method(eng, "CreateDatabase", dropOnlyPath, DbLangGeneral, DbVersion120)));
        Ok(Method(init, "Execute", "CREATE TABLE T (ID LONG)", DbFailOnError));
        Ok(Method(init, "Close"));

        DatabaseObject dropDb = Recover<DatabaseObject>(Ok(Method(eng, "OpenDatabase", dropOnlyPath, false, false)));
        CatalogEntry tEntry = dropDb.Catalog.LocalTables(D).Single(e => e.Name == "T");
        TableDefinition tDef = TableDefDecoder.Decode(
            TableDefSegmentView.Open((uint)tEntry.Id, p => ReadPage(dropDb.Cache, p), D, 8, out _)!,
            (uint)tEntry.Id, D, out _)!;
        uint[] tPages = [tDef.FirstPage, tDef.OwnedMap.Page];
        uint counterBefore = CommitCounter(ReadPage(dropDb.Cache, 0));

        Ok(Method(dropDb, "Execute", "DROP TABLE T", DbFailOnError));

        byte[] openSnap = SnapshotOpenFile(dropDb);
        Assert.Equal(counterBefore + 1, CommitCounter(openSnap.AsSpan(0, D.PageSize)));
        Assert.True(dropDb.Workspace.Transactions.ReleasesAtClose(dropDb.Cache));
        Assert.True(dropDb.Workspace.Transactions.NeedsCloseCommit(dropDb.Cache));
        foreach (uint p in tPages)
        {
            Assert.False(GlobalFreeBit(openSnap.AsSpan(D.PageSize, D.PageSize), p), $"dropped page {p} stays occupied in page 1 until Close");
        }

        Ok(Method(dropDb, "Close"));
        byte[] closedBytes = File.ReadAllBytes(dropOnlyPath);
        Assert.Equal(counterBefore + 2, CommitCounter(closedBytes.AsSpan(0, D.PageSize)));
        foreach (uint p in tPages)
        {
            Assert.True(GlobalFreeBit(closedBytes.AsSpan(D.PageSize, D.PageSize), p), $"dropped page {p} is freed in page 1 at Close");
        }

        // Part 2: CREATE T; DROP T; CREATE U in one session (r-q15-ddl-open/drop-then-create).
        string dropThenCreatePath = Path.Combine(_dir, "drop-then-create.accdb");
        DatabaseObject s2 = Recover<DatabaseObject>(Ok(Method(eng, "CreateDatabase", dropThenCreatePath, DbLangGeneral, DbVersion120)));
        Ok(Method(s2, "Execute", "CREATE TABLE T (ID LONG)", DbFailOnError));
        CatalogEntry t2Entry = s2.Catalog.LocalTables(D).Single(e => e.Name == "T");
        TableDefinition t2Def = TableDefDecoder.Decode(
            TableDefSegmentView.Open((uint)t2Entry.Id, p => ReadPage(s2.Cache, p), D, 8, out _)!,
            (uint)t2Entry.Id, D, out _)!;
        uint[] t2Pages = [t2Def.FirstPage, t2Def.OwnedMap.Page];

        Ok(Method(s2, "Execute", "DROP TABLE T", DbFailOnError));
        Ok(Method(s2, "Execute", "CREATE TABLE U (ID LONG)", DbFailOnError));

        CatalogEntry uEntry = s2.Catalog.LocalTables(D).Single(e => e.Name == "U");
        TableDefinition uDef = TableDefDecoder.Decode(
            TableDefSegmentView.Open((uint)uEntry.Id, p => ReadPage(s2.Cache, p), D, 8, out _)!,
            (uint)uEntry.Id, D, out _)!;
        uint[] uPages = [uDef.FirstPage, uDef.OwnedMap.Page];
        Assert.DoesNotContain(uPages[0], t2Pages);
        Assert.DoesNotContain(uPages[1], t2Pages);

        Ok(Method(s2, "Close"));
        byte[] afterDropCreate = File.ReadAllBytes(dropThenCreatePath);
        ReadOnlySpan<byte> page1After = afterDropCreate.AsSpan(D.PageSize, D.PageSize);
        foreach (uint p in t2Pages)
        {
            Assert.True(GlobalFreeBit(page1After, p), $"T's dropped page {p} must be free in page 1 after Close");
        }
        foreach (uint p in uPages)
        {
            Assert.False(GlobalFreeBit(page1After, p), $"U's page {p} must stay occupied in page 1 after Close");
        }
    }
}
