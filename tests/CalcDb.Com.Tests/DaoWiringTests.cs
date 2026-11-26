using System.Runtime.InteropServices;

using CalcDb.Com.Com;
using CalcDb.Com.Dao;
using CalcDb.Com.Declarations;
using CalcDb.Com.Host;
using CalcDb.Expressions;
using CalcDb.Format;
using CalcDb.Foundation;
using CalcDb.Isam.Transactions;
using CalcDb.Storage;
using CalcDb.Values;

namespace CalcDb.Com.Tests;

/// <summary>
/// The DAO side of engine pieces built elsewhere, driven in process over copies of retained fixtures: QueryDef
/// Parameters, Fields and OpenRecordset over the binder (R-DAO-1 DAO-QUERY; W-06/parser-binder-scan), LockEdits and
/// SetOption 57/63 (R-TXN-1 TXN-LOCAL, CUR-EDIT; W-05/local-lockers-rebase), the recordset after a Rollback (R-TXN-1
/// TXN-CLOSE) and TableDef.Indexes (R-FMT-1 FMT1-INDEX-ENUM, task 6fsf). Each expected answer is the retained record's;
/// the compare set checks the same routes against ACE.
/// </summary>
public sealed unsafe class DaoWiringTests : IDisposable
{
    private const string EditFixture = "71c42e613e63fe3e63f039644361c9de802a35d12605ec28ea0daa75f697f72b"; // r-cursor-1-edit.accdb
    private const string BaseFixture = "814d9ec44538034c82755f043907e7bbff8c9bcaf872ad732fdfd5680c97e248"; // r-dao-1-err-1-accdb-base.accdb
    private const string RichFixture = "3b512c8f10e3ab22e434d9d6e69435bf231a310044b20cdb1c03069c1be34e95"; // r-dao-1b-rich.accdb

    private readonly List<string> _files = [];
    private readonly List<nint> _cells = [];
    private readonly List<nint> _held = [];
    private readonly EngineState _engine = new(new HostBinding(0xDA0_7E57, Standalone: true, 0, 0, 0, 0, 0));

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
        foreach (string file in _files)
        {
            File.Delete(file);
        }
    }

    private DatabaseObject Open(string fixture, bool readOnly = false)
    {
        string path = Path.Combine(Path.GetTempPath(), $"calcdb-dao-wiring-{Guid.NewGuid():N}.accdb");
        File.WriteAllBytes(path, CalcDb.Evidence.ArtifactStore.ForRepository(DeclarationGenerationTests.Root()).ReadAll(fixture));
        _files.Add(path);
        return Recover<DatabaseObject>(Ok(_engine.DefaultWorkspace.OpenDatabase([Arg(path), Arg(false), Arg(readOnly)])));
    }

    /// <summary>A borrowed argument cell: int (VT_I4), bool (VT_BOOL), string (VT_BSTR), DBNull (VT_NULL), null (VT_EMPTY).</summary>
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
            case DBNull:
                v->Vt = (ushort)VarType.Null;
                break;
            case Held held:
                v->Vt = (ushort)VarType.Dispatch;
                v->Pointer = held.Pointer;
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

    private CallResult Put(DispatchObject target, string member, object? value) => Call(target, member, InvokeKind.PropertyPut, value);

    private CallResult Method(DispatchObject target, string member, params object?[] args) => Call(target, member, InvokeKind.Method, args);

    private static CallResult Ok(CallResult result)
    {
        Assert.True(result.Failure is null, result.Failure?.Description);
        return result;
    }

    /// <summary>The CalcDb object a successful call returned; this test keeps its reference until the end.</summary>
    private T Recover<T>(CallResult result) where T : ComObject
    {
        Assert.True(result.HasScalar && (VarType)result.Scalar.Vt == VarType.Dispatch, result.Failure?.Description);
        _held.Add(result.Scalar.Pointer);
        return ComObject.Recover<T>(result.Scalar.Pointer);
    }

    private T Object<T>(DispatchObject target, string member, params object?[] args) where T : ComObject => Recover<T>(Ok(Get(target, member, args)));

    private static NativeVariant Value(CallResult result) => result.HasScalar ? result.Scalar : *result.Value!.Pointer;

    private static string Text(CallResult result)
    {
        NativeVariant v = Value(Ok(result));
        Assert.Equal(VarType.Bstr, (VarType)v.Vt);
        string text = Marshal.PtrToStringBSTR(v.Pointer);
        result.ReleaseUntaken();
        return text;
    }

    private static (VarType Type, long Payload) Scalar(CallResult result)
    {
        NativeVariant v = Value(Ok(result));
        return ((VarType)v.Vt, v.Int64);
    }

    private static void Fails(CallResult result, int number, string source, bool errorInfo)
    {
        Assert.NotNull(result.Failure);
        Assert.Equal((FailureKind.Dao, number, source, errorInfo), (result.Failure!.Kind, result.Failure.Number, result.Failure.Source, result.Failure.PublishesErrorInfo));
    }

    private static void Named(CallResult result) => Assert.Equal(FailureKind.NotModelled, result.Failure?.Kind);

    private static bool NullArray(CallResult result)
    {
        NativeVariant v = Value(Ok(result));
        return (VarType)v.Vt == (VarType.Array | VarType.UI1) && v.Pointer == 0;
    }

    [Fact]
    public void A_recordset_opened_inside_the_rolled_back_level_is_invalid_but_answers_EditMode_LastModified_and_Close()
    {
        // r-cursor-1/edit-rollback-cancel-only: BeginTrans; dynaset G; MoveNext; Edit; U = "B"; CancelUpdate; Rollback.
        DatabaseObject db = Open(EditFixture);
        Ok(_engine.DefaultWorkspace.BeginTrans());
        RecordsetObject rs = Recover<RecordsetObject>(Ok(Method(db, "OpenRecordset", "G", 2)));
        FieldObject u = Object<FieldObject>(Object<FieldsObject>(rs, "Fields"), "Item", "U");
        Ok(Method(rs, "MoveNext"));
        Ok(Method(rs, "Edit"));
        Ok(Put(u, "Value", "B"));
        Ok(Method(rs, "CancelUpdate"));
        Ok(_engine.DefaultWorkspace.Rollback());

        Assert.Equal((VarType.I2, 0L), Scalar(Get(rs, "EditMode")));
        Fails(Get(u, "Value"), 3420, "DAO.Field", errorInfo: true);
        Fails(Method(rs, "Move", 0), 3420, "DAO.Recordset", errorInfo: false);
        Fails(Method(rs, "MoveFirst"), 3420, "DAO.Recordset", errorInfo: false);
        Assert.True(NullArray(Get(rs, "LastModified")));
        Fails(Get(rs, "Bookmark"), 3420, "DAO.Recordset", errorInfo: false);
        Named(Method(rs, "MoveNext")); // not in the records
        Ok(Method(rs, "Close"));
    }

    [Fact]
    public void A_dynaset_opened_before_the_level_loses_its_current_record_until_MoveFirst()
    {
        // r-cursor-1/edit-rollback-opened-before, without the Update (an own read inside a level is not built: mpvx).
        DatabaseObject db = Open(EditFixture);
        RecordsetObject rs = Recover<RecordsetObject>(Ok(Method(db, "OpenRecordset", "G", 2)));
        FieldObject u = Object<FieldObject>(Object<FieldsObject>(rs, "Fields"), "Item", "U");
        Ok(_engine.DefaultWorkspace.BeginTrans());
        Ok(Method(rs, "MoveNext"));
        Ok(Method(rs, "Edit"));
        Ok(Put(u, "Value", "B"));
        Ok(Method(rs, "CancelUpdate"));
        Ok(_engine.DefaultWorkspace.Rollback());

        Assert.Equal((VarType.I2, 0L), Scalar(Get(rs, "EditMode")));
        Fails(Get(u, "Value"), 3021, "DAO.Field", errorInfo: true);
        Fails(Method(rs, "Move", 0), 3021, "DAO.Recordset", errorInfo: false);
        Ok(Method(rs, "MoveFirst"));
        Assert.Equal("u1", Text(Get(u, "Value")));
        Assert.True(NullArray(Get(rs, "LastModified")));
        Ok(Get(rs, "Bookmark")).ReleaseUntaken();
        Ok(Method(rs, "Close"));
    }

    [Fact]
    public void CommitTrans_keeps_a_recordset_opened_inside_the_level_valid_on_its_row()
    {
        DatabaseObject db = Open(EditFixture);
        Ok(_engine.DefaultWorkspace.BeginTrans());
        RecordsetObject rs = Recover<RecordsetObject>(Ok(Method(db, "OpenRecordset", "G", 2)));
        FieldObject u = Object<FieldObject>(Object<FieldsObject>(rs, "Fields"), "Item", "U");
        Ok(Method(rs, "MoveNext"));
        Ok(_engine.DefaultWorkspace.CommitTrans(0));
        Assert.Equal(RecordsetAfterRollback.Valid, rs.AfterRollback);
        Assert.Equal("u2", Text(Get(u, "Value")));
        Ok(Method(rs, "Move", 0));
        Assert.Equal("u2", Text(Get(u, "Value")));
    }

    [Fact]
    public void A_snapshot_opened_inside_the_rolled_back_level_is_named_except_Close()
    {
        DatabaseObject db = Open(EditFixture);
        Ok(_engine.DefaultWorkspace.BeginTrans());
        RecordsetObject rs = Recover<RecordsetObject>(Ok(Method(db, "OpenRecordset", "G", 4)));
        Ok(_engine.DefaultWorkspace.Rollback());
        Named(Method(rs, "MoveFirst"));
        Named(Get(rs, "EditMode"));
        Ok(Method(rs, "Close"));
    }

    [Fact]
    public void Cache_Filter_Sort_Index_hStmt_and_LastModified_answer_by_recordset_type()
    {
        // r-dao-1b/recordset-members-*, r-cursor-1b/cache-size-properties, cache-fill-and-start, filter-sort-other-types.
        DatabaseObject db = Open(EditFixture);
        RecordsetObject dynaset = Recover<RecordsetObject>(Ok(Method(db, "OpenRecordset", "G", 2)));
        Assert.Equal((VarType.I4, 0L), Scalar(Get(dynaset, "CacheSize")));
        Ok(Put(dynaset, "CacheSize", 1201));
        Assert.Equal((VarType.I4, 1201L), Scalar(Get(dynaset, "CacheSize")));
        Assert.True(NullArray(Get(dynaset, "CacheStart")));
        Ok(Put(dynaset, "CacheStart", ""));
        Fails(Put(dynaset, "CacheStart", 1), 3421, "DAO.Recordset", errorInfo: true);
        Ok(Method(dynaset, "FillCache", 5));
        Fails(Method(dynaset, "FillCache", "a"), 3421, "DAO.Recordset", errorInfo: false);
        Ok(Put(dynaset, "Filter", "N > 20"));
        Ok(Put(dynaset, "Sort", "N DESC"));
        Assert.Equal("N > 20", Text(Get(dynaset, "Filter")));
        Assert.Equal("N DESC", Text(Get(dynaset, "Sort")));
        Fails(Get(dynaset, "Index"), 3251, "DAO.Recordset", errorInfo: false);
        Fails(Get(dynaset, "hStmt"), 3251, "DAO.Recordset", errorInfo: false);
        Assert.Equal((VarType.Bool, 0xFFFFL), Scalar(Get(dynaset, "Restartable")));
        Assert.True(NullArray(Get(dynaset, "LastModified")));

        RecordsetObject table = Recover<RecordsetObject>(Ok(Method(db, "OpenRecordset", "G", 1)));
        Fails(Get(table, "CacheSize"), 3251, "DAO.Recordset", errorInfo: false);
        Fails(Put(table, "Filter", "N > 20"), 3251, "DAO.Recordset", errorInfo: false);
        Fails(Method(table, "FillCache"), 3219, "DAO.Recordset", errorInfo: false);
        Assert.True(NullArray(Get(table, "LastModified")));

        RecordsetObject snapshot = Recover<RecordsetObject>(Ok(Method(db, "OpenRecordset", "G", 4)));
        Fails(Get(snapshot, "LastModified"), 3251, "DAO.Recordset", errorInfo: false);
        Assert.Equal((VarType.Bool, 0xFFFFL), Scalar(Get(snapshot, "Restartable")));
    }

    [Fact]
    public void GetRows_returns_fields_by_rows_converts_its_count_and_stops_at_EOF()
    {
        // qualified.md R-CURSOR-1 CUR-BULK; r-cursor-1b/getrows-count-arguments, getrows-beyond-eof, getrows-edit-buffer.
        DatabaseObject db = Open(EditFixture);
        RecordsetObject rs = Recover<RecordsetObject>(Ok(Method(db, "OpenRecordset", "G", 1)));
        FieldsObject fields = Object<FieldsObject>(rs, "Fields");
        int columns = (int)Scalar(Get(fields, "Count")).Payload;
        int u = (int)Scalar(Get(Object<FieldObject>(fields, "Item", "U"), "OrdinalPosition")).Payload;
        Fails(Method(rs, "GetRows", 0), 3001, "DAO.Recordset", errorInfo: false);
        Fails(Method(rs, "GetRows", "abc"), 3421, "DAO.Recordset", errorInfo: false);
        Fails(Method(rs, "GetRows", DBNull.Value), 3421, "DAO.Recordset", errorInfo: false);

        Ok(Method(rs, "Edit"));
        Ok(Put(Object<FieldObject>(fields, "Item", "U"), "Value", "edited"));
        CallResult two = Ok(Method(rs, "GetRows", "2"));
        nint psa = Value(two).Pointer;
        Assert.Equal(new (int, int)[] { (0, columns), (0, 2) }, SafeArrays.Shape(psa));
        var cells = (NativeVariant*)SafeArrays.ElementPointer(psa, 0, 0);
        Assert.Equal("edited", Marshal.PtrToStringBSTR(cells[u].Pointer)); // the pending Edit's buffer is the first row
        Assert.Equal("u2", Marshal.PtrToStringBSTR(cells[columns + u].Pointer));
        two.Value?.Dispose();
        Assert.Equal((VarType.I2, 0L), Scalar(Get(rs, "EditMode")));

        RecordsetObject all = Recover<RecordsetObject>(Ok(Method(db, "OpenRecordset", "G", 2)));
        CallResult rest = Ok(Method(all, "GetRows", 1_000_000)); // no row ceiling: a short return of every row
        Assert.Equal(2, SafeArrays.Shape(Value(rest).Pointer)[1].Count);
        rest.Value?.Dispose();
        Assert.Equal((VarType.Bool, 0xFFFFL), Scalar(Get(all, "EOF")));
        Fails(Method(all, "GetRows"), 3021, "DAO.Recordset", errorInfo: false);
    }

    [Fact]
    public void A_dbReadOnly_recordset_refuses_edits_as_ACE_does_by_type()
    {
        // r-cursor-1b/option-readonly-write: ("F", 2, 4) and ("F", 1, 4) on a writable open.
        DatabaseObject db = Open(EditFixture);
        RecordsetObject dynaset = Recover<RecordsetObject>(Ok(Method(db, "OpenRecordset", "G", 2, 4)));
        FieldObject n = Object<FieldObject>(Object<FieldsObject>(dynaset, "Fields"), "Item", "N");
        Assert.Equal((VarType.Bool, 0L), Scalar(Get(n, "DataUpdatable")));
        Fails(Method(dynaset, "Edit"), 3027, "DAO.Recordset", errorInfo: false);
        Fails(Put(n, "Value", 5), 3027, "DAO.Field", errorInfo: true);
        Fails(Method(dynaset, "Update"), 3020, "DAO.Recordset", errorInfo: false);
        Fails(Method(dynaset, "AddNew"), 3027, "DAO.Recordset", errorInfo: false);
        Fails(Method(dynaset, "Delete"), 3027, "DAO.Recordset", errorInfo: false);
        Fails(Method(dynaset, "CancelUpdate"), 3020, "DAO.Recordset", errorInfo: false);

        RecordsetObject table = Recover<RecordsetObject>(Ok(Method(db, "OpenRecordset", "G", 1, 4)));
        Fails(Put(Object<FieldObject>(Object<FieldsObject>(table, "Fields"), "Item", "N"), "Value", 5), 3020, "DAO.Field", errorInfo: true);

        RecordsetObject writable = Recover<RecordsetObject>(Ok(Method(db, "OpenRecordset", "G", 2)));
        FieldObject w = Object<FieldObject>(Object<FieldsObject>(writable, "Fields"), "Item", "N");
        Assert.Equal((VarType.Bool, 0xFFFFL), Scalar(Get(w, "DataUpdatable")));
        Assert.Equal(32, (int)Scalar(Get(w, "Attributes")).Payload & 32);
    }

    [Fact]
    public void Field_members_of_definitions_and_recordsets_follow_the_rich_fixture()
    {
        // r-dao-1b/field-members-typed-each, recordset-fields-members-each, indexfield-members-each (read-only open).
        DatabaseObject db = Open(RichFixture, readOnly: true);
        DispatchObject typed = Object<DispatchObject>(Object<DispatchObject>(db, "TableDefs"), "Item", "Typed");
        DispatchObject tableFields = Object<DispatchObject>(typed, "Fields");
        (string Name, int Attributes, int Collating)[] expected =
            [("K", 17, 1033), ("TText", 2, 1033), ("TMemo", 2, 1033), ("TByte", 1, 1033), ("TBool", 1, 1033), ("TOle", 2, 1033), ("TGuid", 2, 1033), ("TDec", 1, 38)];
        foreach ((string name, int attributes, int collating) in expected)
        {
            DispatchObject f = Object<DispatchObject>(tableFields, "Item", name);
            Assert.Equal((name, attributes, collating, 0), (name, (int)Scalar(Get(f, "Attributes")).Payload, (int)Scalar(Get(f, "CollatingOrder")).Payload, (int)(short)Scalar(Get(f, "CollectionIndex")).Payload));
            Fails(Get(f, "ComplexType"), 3219, "DAO.Field", errorInfo: false);
        }

        RecordsetObject rs = Recover<RecordsetObject>(Ok(Method(db, "OpenRecordset", "Typed", 2)));
        FieldsObject fields = Object<FieldsObject>(rs, "Fields");
        FieldObject memo = Object<FieldObject>(fields, "Item", "TMemo");
        Assert.Equal((VarType.I2, 2L), Scalar(Get(memo, "CollectionIndex")));
        Assert.Equal((VarType.I4, 2L), Scalar(Get(memo, "FieldSize"))); // twice the units of the one-character Memo
        Assert.Equal((VarType.I4, 0L), Scalar(Get(Object<FieldObject>(fields, "Item", "TOle"), "FieldSize")));
        Fails(Get(Object<FieldObject>(fields, "Item", "K"), "FieldSize"), 3259, "DAO.Field", errorInfo: false);
        Assert.Equal((VarType.Bool, 0xFFFFL), Scalar(Get(Object<FieldObject>(fields, "Item", "TText"), "AllowZeroLength")));
        Assert.Equal((VarType.I2, 24L), Scalar(Get(Object<PropertiesObject>(memo, "Properties"), "Count")));

        DispatchObject index = Object<DispatchObject>(Object<DispatchObject>(typed, "Indexes"), "Item", 0);
        DispatchObject indexField = Object<DispatchObject>(Object<DispatchObject>(index, "Fields"), "Item", 0);
        Fails(Get(indexField, "Type"), 3219, "DAO.Field", errorInfo: false);
        Fails(Get(indexField, "Value"), 3219, "DAO.Field", errorInfo: true);
        Assert.Equal((VarType.I2, 0L), Scalar(Get(indexField, "CollectionIndex")));
        PropertiesObject indexProperties = Object<PropertiesObject>(indexField, "Properties");
        Assert.Equal((VarType.I2, 24L), Scalar(Get(indexProperties, "Count")));
        Named(Get(indexProperties, "Item", 0)); // only the Count is recorded

        Assert.Equal((VarType.Bool, 0L), Scalar(Get(memo, "DataUpdatable"))); // read-only open: every family False
        Named(Put(rs, "Collect", "K")); // recorded on a SELECT dynaset only
    }

    /// <summary>
    /// evidence/r-q16-overflow/grow-row2-plus-30 through DAO: row 2's T1 grown by 30 characters overflows onto page 122,
    /// a free page of the file, so page 1 changes and Close commits once more (R-FMT-2 Overflow, Page 0; R-CRASH-1
    /// CRASH-ORDER Close commit); the file then is ACE's after-image byte for byte.
    /// </summary>
    [Fact]
    public void A_row_publication_that_takes_a_free_page_makes_Close_commit_once_more()
    {
        const string WAccdb = "4f42d3faa7c46ac394eaa6c8a62df0cbec4bbf1af93e84ea7f794fb2b98303be"; // r-fmt-2b-w-accdb.accdb
        DatabaseObject db = Open(WAccdb);
        string path = db.Name;
        RecordsetObject rs = Recover<RecordsetObject>(Ok(Method(db, "OpenRecordset", "Pg", 2)));
        Ok(Method(rs, "FindFirst", "ID = 2"));
        Ok(Method(rs, "Edit"));
        Ok(Put(Object<FieldObject>(Object<FieldsObject>(rs, "Fields"), "Item", "T1"), "Value", new string('€', 91)));
        Ok(Method(rs, "Update"));
        Ok(Method(rs, "Close"));
        Ok(Method(db, "Close"));
        Assert.Equal("6f22c95aced354a9d56dc4de6edb053d3de6d359822e2bb4c3ca1b39285fad1d", Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))));
    }

    /// <summary>
    /// Isam dynamic PageSource (tasks rf2n, hm8k): a non-snapshot recordset opened before or after its Workspace changed
    /// a table inside a level reads the change through the Workspace's live overlay, and reads the committed pages after
    /// the level resolves; a snapshot stays named over unpublished changes.
    /// </summary>
    [Fact]
    public void A_recordset_opened_before_or_after_the_levels_change_reads_it_through_the_overlay()
    {
        DatabaseObject db = Open(EditFixture);
        RecordsetObject atDepthZero = Recover<RecordsetObject>(Ok(Method(db, "OpenRecordset", "G", 1)));
        FieldObject u0 = Object<FieldObject>(Object<FieldsObject>(atDepthZero, "Fields"), "Item", "U");
        Assert.Equal("u1", Text(Get(u0, "Value")));

        Ok(_engine.DefaultWorkspace.BeginTrans());
        RecordsetObject before = Recover<RecordsetObject>(Ok(Method(db, "OpenRecordset", "G", 2)));
        FieldObject u = Object<FieldObject>(Object<FieldsObject>(before, "Fields"), "Item", "U");
        Ok(Method(before, "Edit"));
        Ok(Put(u, "Value", "u9")); // same width as "u1" (a shrinking row is refused by name)
        Ok(Method(before, "Update"));
        Assert.Equal("u9", Text(Get(u, "Value"))); // opened before the first change: follows the live overlay
        Assert.Equal("u9", Text(Get(u0, "Value"))); // opened at depth 0: follows its Workspace's live overlay

        RecordsetObject after = Recover<RecordsetObject>(Ok(Method(db, "OpenRecordset", "G", 2)));
        Assert.Equal("u9", Text(Get(Object<FieldObject>(Object<FieldsObject>(after, "Fields"), "Item", "U"), "Value")));
        RecordsetObject snapshot = Recover<RecordsetObject>(Ok(Method(db, "OpenRecordset", "G", 4)));
        Named(Get(Object<FieldObject>(Object<FieldsObject>(snapshot, "Fields"), "Item", "U"), "Value"));

        Ok(_engine.DefaultWorkspace.Rollback());
        Assert.Equal("u1", Text(Get(u0, "Value"))); // after Rollback: reads committed pages directly (r-txn-1b/rs-types-after-rollback)
        Ok(Method(atDepthZero, "Close"));
        Ok(Method(before, "Close"));
        Ok(Method(after, "Close"));
        Ok(Method(snapshot, "Close"));
    }

    [Fact]
    public void LockEdits_put_reaches_the_edit_frame()
    {
        // r-cursor-1/edit-conflict-optimistic: ra.LockEdits = False on a dynaset before any edit.
        DatabaseObject db = Open(EditFixture);
        RecordsetObject rs = Recover<RecordsetObject>(Ok(Method(db, "OpenRecordset", "G", 2)));
        Ok(Put(rs, "LockEdits", false));
        Assert.Equal((VarType.Bool, 0L), Scalar(Get(rs, "LockEdits")));
        Ok(Method(rs, "Edit"));
        Assert.False(rs.FrameIfMade!.LockEdits);
        Named(Put(rs, "LockEdits", true)); // with an edit pending: not recorded
        Ok(Method(rs, "CancelUpdate"));
        Ok(Put(rs, "LockEdits", true));
        Assert.True(rs.FrameIfMade!.LockEdits);
        RecordsetObject snapshot = Recover<RecordsetObject>(Ok(Method(db, "OpenRecordset", "G", 4)));
        Named(Put(snapshot, "LockEdits", false));
    }

    [Fact]
    public void SetOption_57_and_63_set_the_wait_of_every_Workspace_of_the_engine()
    {
        // r-txn-1/local-option-retry: SetOption(dbLockRetry, 1), SetOption(dbLockDelay, 1) before opening.
        DBEngineObject engine = Engine();
        WorkspaceObject before = engine.Engine.DefaultWorkspace;
        Ok(Method(engine, "SetOption", 57, 1));
        Ok(Method(engine, "SetOption", 63, 1));
        WorkspaceObject after = Recover<WorkspaceObject>(Ok(Method(engine, "CreateWorkspace", "second", "admin", "", 2)));
        var expected = new LockWaitPolicy(1, TimeSpan.FromMilliseconds(1));
        Assert.Equal(expected, before.Transactions.WaitPolicy);
        Assert.Equal(expected, after.Transactions.WaitPolicy);
        Ok(Method(engine, "SetOption", 63, 300));
        Assert.Equal(new LockWaitPolicy(1, TimeSpan.FromMilliseconds(300)), after.Transactions.WaitPolicy);
        // r-q51-codecs-props-compact/setoption-matrix-accdb, setoption-matrix-mdb: unknown option codes outside
        // SetOptionEnum (tested with 999, 0, -1; 3 is likewise outside SetOptionEnum) -> 3001; dbLockRetry 0 succeeds;
        // unrecorded valid SetOptionEnum code 60 (dbExclusiveAsyncDelay) stays NotModelled.EngineOption.
        Fails(Method(engine, "SetOption", 3, 100), 3001, "DAO.DbEngine", errorInfo: false);
        Ok(Method(engine, "SetOption", 57, 0));
        Assert.Equal(new LockWaitPolicy(0, TimeSpan.FromMilliseconds(300)), after.Transactions.WaitPolicy);
        Named(Method(engine, "SetOption", 60, 100));
    }

    private DBEngineObject Engine()
    {
        nint engine;
        Assert.Equal(0, DBEngineObject.Create(_engine.Binding, 0, NativeRoute.IidIDispatch, &engine));
        _held.Add(engine);
        return ComObject.Recover<DBEngineObject>(engine);
    }

    [Fact]
    public void A_saved_parameter_query_takes_values_through_its_Parameters_and_opens_with_them()
    {
        // r-dao-1-err-1/query-parameter-values: QParam declares pId Long and returns the row pId selects.
        DatabaseObject db = Open(BaseFixture, readOnly: true);
        QueryDefObject qd = Object<QueryDefObject>(Object<DispatchObject>(db, "QueryDefs"), "Item", "QParam");
        DefinitionCollection ps = Object<DefinitionCollection>(qd, "Parameters");
        Assert.Equal((VarType.I2, 1L), Scalar(Get(ps, "Count")));
        ParameterObject pm = Object<ParameterObject>(ps, "Item", 0);
        Assert.Equal("pId", Text(Get(pm, "Name")));
        Assert.Equal((VarType.I2, 4L), Scalar(Get(pm, "Type")));
        Assert.Equal((VarType.I2, 1L), Scalar(Get(pm, "Direction")));
        Assert.Equal(VarType.Empty, Scalar(Get(pm, "Value")).Type);
        Fails(Method(qd, "OpenRecordset", 4), 3061, "DAO.Database", errorInfo: false);
        Ok(Put(pm, "Value", null));
        Assert.Equal((VarType.I4, 0L), Scalar(Get(pm, "Value")));
        Ok(Put(pm, "Value", DBNull.Value));
        Assert.Equal(VarType.Null, Scalar(Get(pm, "Value")).Type);
        Fails(Put(pm, "Value", "abc"), 3421, "DAO.Parameter", errorInfo: false);
        Assert.Equal(VarType.Null, Scalar(Get(pm, "Value")).Type);
        Ok(Put(pm, "Value", "2"));
        Assert.Equal((VarType.I4, 2L), Scalar(Get(pm, "Value")));
        Assert.Same(pm, Object<ParameterObject>(ps, "Item", "pId"));

        QueryFieldsObject before = Object<QueryFieldsObject>(qd, "Fields");
        Assert.Equal((VarType.I2, 2L), Scalar(Get(before, "Count")));

        SelectRecordsetObject rs = Recover<SelectRecordsetObject>(Ok(Method(qd, "OpenRecordset", 4)));
        Assert.Equal((VarType.I4, 2L), Scalar(Get(Object<SelectFieldObject>(Object<SelectFieldsObject>(rs, "Fields"), "Item", 0), "Value")));
        Assert.Equal("two", Text(Get(Object<SelectFieldObject>(Object<SelectFieldsObject>(rs, "Fields"), "Item", 1), "Value")));
        Named(Get(before, "Count")); // QueryDef.Fields after an open is not recorded
        Fails(Method(qd, "Execute"), 3065, "DAO.QueryDef", errorInfo: false);
    }

    [Fact]
    public void TableDef_Indexes_list_by_name_without_the_primary_side_relation_entries()
    {
        // r-dao-1b/index-members-each: Parent lists five indexes without ".rF"; Child PK_Child and RelMain without ".rB".
        DatabaseObject db = Open(RichFixture, readOnly: true);
        DispatchObject tables = Object<DispatchObject>(db, "TableDefs");
        Assert.Equal(["IX_Parent_Ignore", "IX_Parent_QtyDesc", "IX_Parent_Required", "PK_Parent", "UX_Parent_Name"], IndexNames(Object<DispatchObject>(tables, "Item", "Parent")));
        Assert.Equal(["PK_Child", "RelMain"], IndexNames(Object<DispatchObject>(tables, "Item", "Child")));
    }

    private string[] IndexNames(DispatchObject table)
    {
        DispatchObject indexes = Object<DispatchObject>(table, "Indexes");
        int count = (int)Scalar(Get(indexes, "Count")).Payload;
        return [.. Enumerable.Range(0, count).Select(i => Text(Get(Object<DispatchObject>(indexes, "Item", i), "Name")))];
    }

    [Fact]
    public void Index_names_sort_in_the_General_order_underscore_digits_then_letters_case_together()
    {
        // qualified.md R-FMT-1 FMT1-INDEX-ENUM (r-q-fmt-sort/index-enum-order-accdb), created Z_ix, M_ix, a_ix, B_ix, _first, 0_ix.
        IndexDescriptorRules rules = FormatDescriptor.Jet4Family.IndexDescriptor;
        LogicalIndex[] stored = [.. CreationOrder.Select((n, i) => new LogicalIndex(n, i, i, 0, 0, uint.MaxValue, 0)),
            new LogicalIndex(".rF", 7, 0, rules.ForeignKeyType, 1, 2, 50)];
        DatabaseCollation general = Open(EditFixture, readOnly: true).Collation!; // an ACCDB in the General order
        LogicalIndex[] listed = ListedIndexes.Of(stored, rules, general);
        Assert.Equal(["_first", "0_ix", "a_ix", "B_ix", "M_ix", "PK_T", "Z_ix"], listed.Select(i => i.Name));
    }

    private static readonly string[] CreationOrder = ["PK_T", "Z_ix", "M_ix", "a_ix", "B_ix", "_first", "0_ix"];

    [Fact]
    public void SessionLocale_reads_GetUserDefaultLCID_and_ignores_thread_culture()
    {
        nint kernel32 = NativeLibrary.Load("kernel32.dll", typeof(DaoWiringTests).Assembly, DllImportSearchPath.System32);
        uint expected = ((delegate* unmanaged<uint>)NativeLibrary.GetExport(kernel32, "GetUserDefaultLCID"))();
        uint lcid = SessionLocale.Lcid;
        Assert.NotEqual(0u, lcid);
        Assert.Equal(expected, lcid);

        var previous = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("de-DE");
            Assert.Equal(lcid, SessionLocale.Lcid);
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = previous;
        }
    }

    [Fact]
    public void Text_put_Find_and_SQL_use_the_user_default_LCID_when_thread_culture_is_de_DE()
    {
        // qualified.md R-EXPR-1 EXPR-LOCALE (5zh9): under user default 7177 (en-ZA, '.' decimal separator) with the
        // thread culture set to de-DE (1031), text puts, Find criteria and SQL expressions still convert under the
        // user default locale, never Thread.CurrentCulture.
        var previous = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("de-DE");

            DatabaseObject db = Open(RichFixture);

            // 1. Text put into a Double field: "1.5" puts and reads 1.5; "1,5" fails 3421 from DAO.Field and leaves 1.5.
            RecordsetObject typed = Recover<RecordsetObject>(Ok(Method(db, "OpenRecordset", "Typed", 2)));
            FieldObject tDouble = Object<FieldObject>(Object<FieldsObject>(typed, "Fields"), "Item", "TDouble");
            Ok(Method(typed, "Edit"));
            Ok(Put(tDouble, "Value", "1.5"));
            NativeVariant read = Value(Ok(Get(tDouble, "Value")));
            Assert.Equal(VarType.R8, (VarType)read.Vt);
            Assert.Equal(1.5, read.Double);
            Fails(Put(tDouble, "Value", "1,5"), 3421, "DAO.Field", errorInfo: true);
            read = Value(Ok(Get(tDouble, "Value")));
            Assert.Equal(VarType.R8, (VarType)read.Vt);
            Assert.Equal(1.5, read.Double);
            Ok(Method(typed, "CancelUpdate"));

            // 2. FindFirst criterion: CDbl('1.5') finds Parent ID 1 (Value = 1.5); CDbl('1,5') and '1,5' fail 3464.
            RecordsetObject parent = Recover<RecordsetObject>(Ok(Method(db, "OpenRecordset", "Parent", 2)));
            FieldObject parentId = Object<FieldObject>(Object<FieldsObject>(parent, "Fields"), "Item", "ID");
            Ok(Method(parent, "FindFirst", "[Value] = CDbl('1.5')"));
            Assert.Equal((VarType.Bool, 0L), Scalar(Get(parent, "NoMatch")));
            Assert.Equal((VarType.I4, 1L), Scalar(Get(parentId, "Value")));
            Fails(Method(parent, "FindFirst", "[Value] = CDbl('1,5')"), 3464, "DAO.Recordset", errorInfo: false);
            Fails(Method(parent, "FindFirst", "[Value] = '1,5'"), 3464, "DAO.Recordset", errorInfo: false);

            // 3. SQL expression: CDbl('1.5') = 1.5 and CStr(1.5) = "1.5"; CDbl('1,5') fails 3464 from DAO.Database.
            SelectRecordsetObject sql = Recover<SelectRecordsetObject>(Ok(Method(db, "OpenRecordset", "SELECT CDbl('1.5') AS N, CStr(1.5) AS S FROM Parent WHERE ID = 1", 4)));
            SelectFieldsObject sqlFields = Object<SelectFieldsObject>(sql, "Fields");
            NativeVariant sqlNum = Value(Ok(Get(Object<SelectFieldObject>(sqlFields, "Item", "N"), "Value")));
            Assert.Equal(VarType.R8, (VarType)sqlNum.Vt);
            Assert.Equal(1.5, sqlNum.Double);
            Assert.Equal("1.5", Text(Get(Object<SelectFieldObject>(sqlFields, "Item", "S"), "Value")));
            Fails(Method(db, "OpenRecordset", "SELECT CDbl('1,5') AS N FROM Parent WHERE ID = 1", 4), 3464, "DAO.Database", errorInfo: false);
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = previous;
        }
    }

    /// <summary>An object argument: the IDispatch a successful call returned, which the test holds.</summary>
    private readonly record struct Held(nint Pointer);

    private Held Hold(CallResult result)
    {
        Assert.True(result.HasScalar && (VarType)result.Scalar.Vt == VarType.Dispatch, result.Failure?.Description);
        _held.Add(result.Scalar.Pointer);
        return new Held(result.Scalar.Pointer);
    }

    /// <summary>
    /// W-07/native-metadata-write on the DAO route (r-q11-ddl-closure/append-dao): CreateTableDef, CreateField(dbLong),
    /// Fields.Append and TableDefs.Append write the table to the file at once; Close adds the session's closing commit
    /// (page-0 offset 3586 ends at 3 as ACE's does); a reopened Database lists T with its one Long field. Appending it
    /// again is 3010 from DAO.TableDefs; a draft without fields 3264 (r-dao-1-err-1/draft-tabledef-field).
    /// </summary>
    [Fact]
    public void TableDefs_Append_of_a_draft_writes_the_table_and_a_reopen_lists_it()
    {
        const string EmptyAccdb = "ea467b4c6d6472bc6819b0ddbe535612db76cc9d6bf2bf581d08f149c3765df8"; // r-cat-1-accdb-empty.accdb
        DatabaseObject db = Open(EmptyAccdb);
        string path = db.Name;
        Held td = Hold(Method(db, "CreateTableDef", "T"));
        var draft = ComObject.Recover<NewTableDefObject>(td.Pointer);
        Held field = Hold(Method(draft, "CreateField", "n", 4));
        Held fields = Hold(Get(draft, "Fields"));
        Ok(Method(ComObject.Recover<NewFieldsObject>(fields.Pointer), "Append", field));
        DefinitionCollection tableDefs = Object<DefinitionCollection>(db, "TableDefs");
        int before = (int)Scalar(Get(tableDefs, "Count")).Payload;
        // r-dao-1-err-1/draft-tabledef-field: a field in one draft's Fields is 3367 in another's; a draft's Name put reads back.
        Held other = Hold(Method(db, "CreateTableDef", "O"));
        Held otherFields = Hold(Get(ComObject.Recover<NewTableDefObject>(other.Pointer), "Fields"));
        Fails(Method(ComObject.Recover<NewFieldsObject>(otherFields.Pointer), "Append", field), 3367, "DAO.Fields", errorInfo: false);
        Ok(Put(draft, "Name", "T"));
        Assert.Equal("T", Text(Get(draft, "Name")));
        Ok(Method(tableDefs, "Append", td));
        // r-q15-ddl-open/lv-rewrite-byte-n: the one TableDefs object lists the new table after the Append.
        Assert.Same(tableDefs, Object<DefinitionCollection>(db, "TableDefs"));
        Assert.Equal(before + 1, (int)Scalar(Get(tableDefs, "Count")).Payload);
        Ok(Get(tableDefs, "Item", "T")).ReleaseUntaken();
        CallResult again = Method(Object<DefinitionCollection>(db, "TableDefs"), "Append", td);
        Assert.Equal(3010, again.Failure?.Number);
        Held empty = Hold(Method(db, "CreateTableDef", "E"));
        Assert.Equal(3264, Method(tableDefs, "Append", empty).Failure?.Number);
        Ok(Method(db, "Close"));

        byte[] file = File.ReadAllBytes(path);
        Assert.Equal(44 * 4096, file.Length);
        Assert.Equal(3, file[3586]);
        DatabaseObject reopened = Recover<DatabaseObject>(Ok(_engine.DefaultWorkspace.OpenDatabase([Arg(path), Arg(false), Arg(true)])));
        DispatchObject t = Object<DispatchObject>(Object<DispatchObject>(reopened, "TableDefs"), "Item", "T");
        DispatchObject n = Object<DispatchObject>(Object<DispatchObject>(t, "Fields"), "Item", 0);
        Assert.Equal("n", Text(Get(n, "Name")));
        Ok(Method(reopened, "Close"));
    }

    private void AssertDatabaseVersion(string fixture, string extension, byte expectedCode, FileFormat expectedFormat, string expectedVersion)
    {
        string path = Path.Combine(Path.GetTempPath(), $"calcdb-dao-ver-{Guid.NewGuid():N}{extension}");
        File.WriteAllBytes(path, CalcDb.Evidence.ArtifactStore.ForRepository(DeclarationGenerationTests.Root()).ReadAll(fixture));
        _files.Add(path);
        DatabaseObject db = Recover<DatabaseObject>(Ok(_engine.DefaultWorkspace.OpenDatabase([Arg(path), Arg(false), Arg(true)])));
        Assert.Equal(expectedCode, db.Profile.VersionCode);
        Assert.Equal(expectedFormat, db.Profile.Format);
        Assert.Equal(expectedVersion, Text(Get(db, "Version")));
        PropertiesObject props = Object<PropertiesObject>(db, "Properties");
        PropertyObject verProp = Object<PropertyObject>(props, "Item", "Version");
        Assert.Equal(expectedVersion, Text(Get(verProp, "Value")));
        Ok(Method(db, "Close"));
    }

    [Fact]
    public void Database_Version_code_1_Jet4_answers_4_0()
    {
        // r-fmt-1/read-mdb-empty call 6 (db.Version = "4.0") and call 44 (Properties("Version").Value = "4.0") on r-fmt-1-mdb-empty.mdb (page 0 byte 20 = 1).
        AssertDatabaseVersion("6b0a3d10ba22784f2e52995246efb394d3303b9a1019434f68a4e318aa503369", ".mdb", 1, FileFormat.Jet4, "4.0");
    }

    [Fact]
    public void Database_Version_code_2_Ace2007_answers_12_0()
    {
        // r-fmt-1/read-accdb-empty call 6 (db.Version = "12.0") and call 44 (Properties("Version").Value = "12.0") on r-fmt-1-accdb-empty.accdb (page 0 byte 20 = 2).
        AssertDatabaseVersion("c1e6c777efb0a240a6929e3d9d410e7ef32152e60cd01e620b0b0cfa71d44559", ".accdb", 2, FileFormat.Ace2007, "12.0");
    }

    [Fact]
    public void Database_Version_code_3_Ace2010_answers_14_0()
    {
        // r-fmt-1/read-accdb-calculated call 6 (db.Version = "14.0") and call 44 (Properties("Version").Value = "14.0") on r-fmt-1-accdb-calculated.accdb (page 0 byte 20 = 3).
        AssertDatabaseVersion("434dad97c7467e3f184605cdf99af3b007d2a99fa8ab8f4c892c8484a8dbcad3", ".accdb", 3, FileFormat.Ace2010, "14.0");
    }

    [Fact]
    public void Database_Version_code_5_Ace2016_answers_16_7()
    {
        // r-fmt-1/read-accdb-bigint call 6 (db.Version = "16.7") and call 44 (Properties("Version").Value = "16.7") on r-fmt-1-accdb-bigint.accdb (page 0 byte 20 = 5);
        // also r-expr-1/a-null-empty-error-literals call 7 ("16.7") on r-expr-1-main.accdb (page 0 byte 20 = 5), while eng.Version at call 5 stays "16.0".
        AssertDatabaseVersion("b3d92d52ac8e5ec84585b25d656969a06c0eda71bbdadfe9244a637a4f54c606", ".accdb", 5, FileFormat.Ace2016, "16.7");
        AssertDatabaseVersion("04a50670cf8352110bd1d9ed48ff1033bfb94340e524c5dcc16b01c6a5475a54", ".accdb", 5, FileFormat.Ace2016, "16.7");
        Assert.Equal("16.0", DBEngineObject.AceVersion);
    }

    [Fact]
    public void Database_Version_code_6_Ace2019_answers_32_0()
    {
        // r-fmt-1/read-accdb-datetime2 call 6 (db.Version = "32.0") and call 44 (Properties("Version").Value = "32.0") on r-fmt-1-accdb-datetime2.accdb (page 0 byte 20 = 6);
        // also r-fmt-2-pres-1/pres-catalog-ladder call 6 ("32.0") on r-fmt-2-pres-1-pres3-accdb.accdb (page 0 byte 20 = 6).
        AssertDatabaseVersion("dd114816e817de98fc5378b765bf543836b73e3d41e88b2645ef3f2de85c4a1f", ".accdb", 6, FileFormat.Ace2019, "32.0");
        AssertDatabaseVersion("45038559262fb66ba8c466cd3e03d03bb2b4dee0e46a33e99e1edee9d13e0e99", ".accdb", 6, FileFormat.Ace2019, "32.0");
    }

    [Fact]
    public void Database_Version_refuses_an_unrecorded_format_code_by_name()
    {
        CallResult unrecorded = DatabaseObject.VersionResult((FileFormat)99);
        Assert.NotNull(unrecorded.Failure);
        Assert.Equal(FailureKind.NotModelled, unrecorded.Failure!.Kind);
        Assert.StartsWith("NotModelled.DatabaseVersionFormat:", unrecorded.Failure.Description);

        // An unrecorded page-0 format code (e.g. code 4) is also refused at OpenDatabase with UnknownVersion.
        byte[] copy = CalcDb.Evidence.ArtifactStore.ForRepository(DeclarationGenerationTests.Root()).ReadAll("c1e6c777efb0a240a6929e3d9d410e7ef32152e60cd01e620b0b0cfa71d44559");
        copy[20] = 4;
        string path = Path.Combine(Path.GetTempPath(), $"calcdb-dao-ver-code4-{Guid.NewGuid():N}.accdb");
        File.WriteAllBytes(path, copy);
        _files.Add(path);
        CallResult opened = _engine.DefaultWorkspace.OpenDatabase([Arg(path), Arg(false), Arg(true)]);
        Assert.NotNull(opened.Failure);
        Assert.Equal(FailureKind.NotImplemented, opened.Failure!.Kind);
        Assert.Contains("UnknownVersion", opened.Failure.Description);
    }
}
