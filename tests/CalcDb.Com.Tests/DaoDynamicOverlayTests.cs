using System.Runtime.InteropServices;

using CalcDb.Com.Com;
using CalcDb.Com.Dao;
using CalcDb.Com.Declarations;
using CalcDb.Com.Host;
using CalcDb.Foundation;
using CalcDb.Values;

namespace CalcDb.Com.Tests;

/// <summary>
/// Register row hm8k (W-05/edit-frames; R-TXN-1 TXN-VIEW, TXN-CLOSE): DAO recordsets bind their ISAM cursors to a
/// dynamic <see cref="CalcDb.Isam.PageSource"/> so a cursor opened before a level's first write sees its own
/// Workspace's overlay during the level and committed pages after CommitTrans or Rollback, while snapshots and
/// cross-Database unpublished changes remain refused by name.
/// </summary>
public sealed unsafe class DaoDynamicOverlayTests : IDisposable
{
    private const string EditFixture = "71c42e613e63fe3e63f039644361c9de802a35d12605ec28ea0daa75f697f72b"; // r-cursor-1-edit.accdb
    private const string NavFixture = "49288e9c099e3b28218a88a1c5a28a0053476f2e6d078898dd091510bc93be7d"; // r-cursor-1-nav.accdb

    private readonly List<string> _files = [];
    private readonly List<DatabaseObject> _databases = [];
    private readonly List<nint> _cells = [];
    private readonly List<(ComObject Obj, nint Ptr)> _held = [];
    private readonly EngineState _engine = new(new HostBinding(0xDA0_7E70, Standalone: true, 0, 0, 0, 0, 0));

    public void Dispose()
    {
        for (int i = _databases.Count - 1; i >= 0; i--)
        {
            if (!_databases[i].Closed)
            {
                _ = Method(_databases[i], "Close");
            }
        }
        for (int i = _held.Count - 1; i >= 0; i--)
        {
            Marshal.Release(_held[i].Ptr);
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
        foreach (string file in _files)
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void Dynaset_opened_inside_level_reads_own_overlay_and_committed_pages_after_CommitTrans()
    {
        // r-cursor-1/edit-commit-opened-inside: dynaset opened inside BeginTrans before first write updates row 2,
        // moves and reads through the overlay, then after CommitTrans reads committed pages without ResolvedOverlay.
        DatabaseObject db = Open(EditFixture);
        WorkspaceObject ws = _engine.DefaultWorkspace;
        Ok(Method(ws, "BeginTrans"));

        RecordsetObject rs = OpenRecordset(db, "G", 2);
        Ok(Method(rs, "MoveNext"));
        Ok(Method(rs, "Edit"));
        Ok(PutFieldValue(rs, "U", "u9"));
        Ok(Method(rs, "Update"));
        Assert.Equal("u9", TextValue(Ok(FieldValue(rs, "U"))));

        Ok(Method(rs, "MoveFirst"));
        Assert.Equal("u1", TextValue(Ok(FieldValue(rs, "U"))));
        Ok(Method(rs, "MoveNext"));
        Assert.Equal("u9", TextValue(Ok(FieldValue(rs, "U"))));

        Ok(Method(ws, "CommitTrans"));

        // After CommitTrans, the overlay is gone: cursor reads committed pages cleanly.
        Assert.Equal("u9", TextValue(Ok(FieldValue(rs, "U"))));
        Ok(Method(rs, "Move", 0));
        Ok(Method(rs, "MoveFirst"));
        Assert.Equal("u1", TextValue(Ok(FieldValue(rs, "U"))));
        Ok(Method(rs, "MoveNext"));
        Assert.Equal("u9", TextValue(Ok(FieldValue(rs, "U"))));
        Ok(Method(rs, "Close"));
    }

    [Fact]
    public void Dynaset_opened_before_level_reads_overlay_inside_level_loses_current_on_Rollback_and_reads_committed_after_MoveFirst()
    {
        // r-cursor-1/edit-rollback-opened-before: dynaset opened at depth 0 sees its own edit inside BeginTrans,
        // enters LostCurrentRecord (3021) on Rollback, and reads committed "u1" after MoveFirst.
        DatabaseObject db = Open(EditFixture);
        WorkspaceObject ws = _engine.DefaultWorkspace;
        RecordsetObject rs = OpenRecordset(db, "G", 2);
        FieldObject u = GetField(rs, "U");
        Assert.Equal("u1", TextValue(Ok(Get(u, "Value"))));

        Ok(Method(ws, "BeginTrans"));
        Ok(Method(rs, "Edit"));
        Ok(Put(u, "Value", "u9"));
        Ok(Method(rs, "Update"));
        Assert.Equal("u9", TextValue(Ok(Get(u, "Value"))));
        Ok(Method(rs, "MoveNext"));
        Assert.Equal("u2", TextValue(Ok(Get(u, "Value"))));

        Ok(Method(ws, "Rollback"));
        AssertDaoError(Get(u, "Value"), 3021, "DAO.Field");
        AssertDaoError(Method(rs, "Move", 0), 3021, "DAO.Recordset");

        Ok(Method(rs, "MoveFirst"));
        Assert.Equal("u1", TextValue(Ok(Get(u, "Value"))));
        Ok(Method(rs, "Close"));
    }

    [Fact]
    public void Table_type_opened_before_level_sees_overlay_and_reads_committed_directly_after_Rollback()
    {
        // r-txn-1b/rs-types-after-rollback: table-type opened at depth 0 reads uncommitted edits inside the level
        // and directly reads committed pages after Rollback without losing position.
        DatabaseObject db = Open(EditFixture);
        WorkspaceObject ws = _engine.DefaultWorkspace;
        RecordsetObject tb = OpenRecordset(db, "G", 1);
        Assert.Equal("u1", TextValue(Ok(FieldValue(tb, "U"))));

        Ok(Method(ws, "BeginTrans"));
        Ok(Method(tb, "Edit"));
        Ok(PutFieldValue(tb, "U", "u9"));
        Ok(Method(tb, "Update"));
        Assert.Equal("u9", TextValue(Ok(FieldValue(tb, "U"))));

        Ok(Method(ws, "Rollback"));
        Assert.Equal("u1", TextValue(Ok(FieldValue(tb, "U"))));
        Ok(Method(tb, "Close"));
    }

    [Fact]
    public void Dynaset_clone_closed_inside_transaction_reads_overlay_and_commits()
    {
        // r-cursor-1/life-clone-close-transaction: dynaset and clone opened inside BeginTrans before first write;
        // edit + update + clone close + read + CommitTrans.
        DatabaseObject db = Open(NavFixture);
        WorkspaceObject ws = _engine.DefaultWorkspace;
        Ok(Method(ws, "BeginTrans"));

        RecordsetObject rs = OpenRecordset(db, "T", 2);
        RecordsetObject cl = Recover<RecordsetObject>(Ok(Method(rs, "Clone")));
        Ok(Method(rs, "Edit"));
        Ok(PutFieldValue(rs, "V", "txn"));
        Ok(Method(rs, "Update"));
        Assert.Equal("txn", TextValue(Ok(FieldValue(cl, "V"))));

        Ok(Method(cl, "Close"));
        Assert.Equal("txn", TextValue(Ok(FieldValue(rs, "V"))));
        Ok(Method(ws, "CommitTrans"));
        Assert.Equal("txn", TextValue(Ok(FieldValue(rs, "V"))));
        Ok(Method(rs, "Close"));
    }

    [Fact]
    public void Snapshot_over_unpublished_overlay_stays_refused_and_second_Database_of_the_Workspace_sees_it()
    {
        // Snapshots over an active overlay stay named with NotModelled.Edit.UnpublishedRows
        // (r-def-cursor-dao/assign-without-edit-by-type call 56). A second Database of the same Workspace on the same file
        // shares the pooled session and the Workspace's overlay, so it reads the uncommitted change (W-05 6xzx:
        // r-q46-dao-ddl-multi/mh-rwrw-samews-accdb calls 65-66 for snapshot visibility, and
        // r-q2-cursor-dao/life-clone-close-second-database-enlisted calls 9-26 for same-Workspace dynasets).
        string path = CopyFixture(EditFixture);
        DatabaseObject db1 = OpenPath(path);
        DatabaseObject db2 = OpenPath(path);
        WorkspaceObject ws = _engine.DefaultWorkspace;

        Ok(Method(ws, "BeginTrans"));
        RecordsetObject r1 = OpenRecordset(db1, "G", 2);
        Ok(Method(r1, "Edit"));
        Ok(PutFieldValue(r1, "U", "u9"));
        Ok(Method(r1, "Update"));
        Assert.Equal("u9", TextValue(Ok(FieldValue(r1, "U"))));

        RecordsetObject snap = OpenRecordset(db1, "G", 4);
        AssertNotModelled(FieldValue(snap, "U"), "NotModelled.Edit.UnpublishedRows");

        Assert.Same(db1.Cache, db2.Cache);
        RecordsetObject r2 = OpenRecordset(db2, "G", 2);
        Assert.Equal("u9", TextValue(Ok(FieldValue(r2, "U"))));

        Ok(Method(ws, "Rollback"));
        Ok(Method(r2, "Close"));
        Ok(Method(snap, "Close"));
        Ok(Method(r1, "Close"));
    }

    private string CopyFixture(string fixture)
    {
        string path = Path.Combine(Path.GetTempPath(), $"calcdb-dao-dynamic-overlay-{Guid.NewGuid():N}.accdb");
        File.WriteAllBytes(path, CalcDb.Evidence.ArtifactStore.ForRepository(DeclarationGenerationTests.Root()).ReadAll(fixture));
        _files.Add(path);
        return path;
    }

    private DatabaseObject Open(string fixture) => OpenPath(CopyFixture(fixture));

    private DatabaseObject OpenPath(string path)
    {
        DatabaseObject db = Recover<DatabaseObject>(Ok(_engine.DefaultWorkspace.OpenDatabase([Arg(path), Arg(false), Arg(false)])));
        _databases.Add(db);
        return db;
    }

    private RecordsetObject OpenRecordset(DatabaseObject db, string source, int type) =>
        Recover<RecordsetObject>(Ok(Method(db, "OpenRecordset", source, type)));

    private FieldObject GetField(DispatchObject rs, string name)
    {
        FieldsObject fields = Recover<FieldsObject>(Ok(Get(rs, "Fields")));
        return Recover<FieldObject>(Ok(Get(fields, "Item", name)));
    }

    private CallResult FieldValue(DispatchObject rs, string name) => Get(GetField(rs, name), "Value");

    private CallResult PutFieldValue(DispatchObject rs, string name, object? value) => Put(GetField(rs, name), "Value", value);

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
            case string s:
                v->Vt = (ushort)VarType.Bstr;
                v->Pointer = Marshal.StringToBSTR(s);
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
        Assert.Null(result.Failure);
        return result;
    }

    private static void AssertNotModelled(CallResult result, string expectedName)
    {
        Assert.NotNull(result.Failure);
        Assert.Equal(FailureKind.NotModelled, result.Failure!.Kind);
        Assert.StartsWith(expectedName + ":", result.Failure.Description);
    }

    private static void AssertDaoError(CallResult result, int expectedNumber, string expectedSource)
    {
        Assert.NotNull(result.Failure);
        Assert.Equal(FailureKind.Dao, result.Failure!.Kind);
        Assert.Equal(expectedNumber, result.Failure.Number);
        Assert.Equal(expectedSource, result.Failure.Source);
    }

    private static string TextValue(CallResult result)
    {
        NativeVariant v = result.HasScalar ? result.Scalar : *result.Value!.Pointer;
        Assert.Equal(VarType.Bstr, (VarType)v.Vt);
        string text = Marshal.PtrToStringBSTR(v.Pointer);
        result.ReleaseUntaken();
        return text;
    }

    private T Recover<T>(CallResult result) where T : ComObject
    {
        Assert.True(result.HasScalar && (VarType)result.Scalar.Vt == VarType.Dispatch, result.Failure?.Description);
        T obj = ComObject.Recover<T>(result.Scalar.Pointer);
        _held.Add((obj, result.Scalar.Pointer));
        return obj;
    }
}
