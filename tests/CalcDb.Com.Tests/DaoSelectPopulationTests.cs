using System.Runtime.InteropServices;

using CalcDb.Com.Com;
using CalcDb.Com.Dao;
using CalcDb.Com.Declarations;
using CalcDb.Com.Host;
using CalcDb.Foundation;
using CalcDb.Values;

namespace CalcDb.Com.Tests;

/// <summary>
/// DAO SELECT RecordCount population and Requery tests (W-06/W-08; docs/work/qualified.md R-CURSOR-1 CUR-POP):
/// verifies the recorded ACCDB dynaset/snapshot population progression (1 -> 501 -> 2001 -> 2048, ORDER BY snapshot
/// complete at open, query sources DISTINCT/TOP/GROUP BY/LEFT JOIN/UNION), SELECT Requery at depth 0 and depth 1,
/// held Field continuity across Requery, and every named <c>NotModelled.QueryRecordCount.*</c> and
/// <c>NotModelled.QueryRecordsetRequery*</c> refusal.
/// </summary>
public sealed unsafe class DaoSelectPopulationTests : IDisposable
{
    private const string QCursorDaoMain = "37cb1596da09f7b6ba43f90571a8d381a057852eb7d762c73044c0a839134012"; // r-q-cursor-dao-main.accdb
    private const string Q2CursorDaoMain = "1473d5a4c26300ae8206b5c014d9179b424cca71dfbb02144ed21ce064d473c2"; // r-q2-cursor-dao-main.accdb
    private const string Txn1bAccdb = "cde7b712cc54e824851b29bf6e711b875a9018edb83b623b81a13a3798799ddb"; // r-txn-1b-main-accdb.accdb
    private const string Txn1bMdb = "eee913ea990a36212602b75bdfa22b91ca84ed57104f48ca748c310ad79f1e75"; // r-txn-1b-main-mdb.mdb
    private const string DefCursorMain = "e6c0d23cc00a646991cc7c6cabd74305f2bc15ce8267745527e686ac13dd8dc0"; // r-def-cursor-dao-main.accdb

    private readonly List<string> _files = [];
    private readonly List<DatabaseObject> _databases = [];
    private readonly List<nint> _cells = [];
    private readonly List<(ComObject Obj, nint Ptr)> _held = [];
    private readonly EngineState _engine = new(new HostBinding(0xDA0_7E72, Standalone: true, 0, 0, 0, 0, 0));

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
    public void Dynaset_and_snapshot_frontier_population_matches_retained_records()
    {
        // r-q-cursor-dao/pop-frontier-dynaset-big & pop-frontier-snapshot-big
        DatabaseObject qDb = Open(QCursorDaoMain);
        SelectRecordsetObject dyn = OpenSelect(qDb, "SELECT ID, V FROM Big ORDER BY ID", 2);
        Assert.Equal(1, IntValue(Ok(Get(dyn, "RecordCount"))));
        Assert.Equal(1, IntValue(Ok(FieldValue(dyn, "ID"))));
        Assert.Equal(1, IntValue(Ok(Get(dyn, "RecordCount"))));
        Ok(Method(dyn, "MoveNext"));
        Assert.Equal(501, IntValue(Ok(Get(dyn, "RecordCount"))));
        Ok(Method(dyn, "Move", 9));
        Assert.Equal(501, IntValue(Ok(Get(dyn, "RecordCount"))));
        Ok(Method(dyn, "Move", 90));
        Assert.Equal(501, IntValue(Ok(Get(dyn, "RecordCount"))));
        Ok(Method(dyn, "Move", 400));
        Assert.Equal(501, IntValue(Ok(Get(dyn, "RecordCount"))));
        Ok(Method(dyn, "Move", 1500));
        Assert.Equal(2001, IntValue(Ok(Get(dyn, "RecordCount"))));
        Ok(Method(dyn, "MoveLast"));
        Assert.Equal(2048, IntValue(Ok(Get(dyn, "RecordCount"))));
        Ok(Method(dyn, "Close"));

        SelectRecordsetObject snapOrd = OpenSelect(qDb, "SELECT ID, V FROM Big ORDER BY ID", 4);
        Assert.Equal(2048, IntValue(Ok(Get(snapOrd, "RecordCount"))));
        Ok(Method(snapOrd, "Close"));

        // r-q2-cursor-dao/pop-snapshot-select-noorder-big & pop-snapshot-orderby-small
        DatabaseObject q2Db = Open(Q2CursorDaoMain);
        SelectRecordsetObject snapNoOrd = OpenSelect(q2Db, "SELECT ID, V FROM Big", 4);
        Assert.Equal(1, IntValue(Ok(Get(snapNoOrd, "RecordCount"))));
        _ = Ok(FieldValue(snapNoOrd, "ID"));
        Assert.Equal(1, IntValue(Ok(Get(snapNoOrd, "RecordCount"))));
        Ok(Method(snapNoOrd, "MoveNext"));
        Assert.Equal(501, IntValue(Ok(Get(snapNoOrd, "RecordCount"))));
        Ok(Method(snapNoOrd, "Move", 9));
        Assert.Equal(501, IntValue(Ok(Get(snapNoOrd, "RecordCount"))));
        Ok(Method(snapNoOrd, "Move", 90));
        Assert.Equal(501, IntValue(Ok(Get(snapNoOrd, "RecordCount"))));
        Ok(Method(snapNoOrd, "Move", 400));
        Assert.Equal(501, IntValue(Ok(Get(snapNoOrd, "RecordCount"))));
        Ok(Method(snapNoOrd, "Move", 1500));
        Assert.Equal(2001, IntValue(Ok(Get(snapNoOrd, "RecordCount"))));
        Ok(Method(snapNoOrd, "MoveLast"));
        Assert.Equal(2048, IntValue(Ok(Get(snapNoOrd, "RecordCount"))));
        Ok(Method(snapNoOrd, "Close"));

        SelectRecordsetObject snapSmall = OpenSelect(q2Db, "SELECT ID, V FROM Sm ORDER BY ID", 4);
        Assert.Equal(8, IntValue(Ok(Get(snapSmall, "RecordCount"))));
        Ok(Method(snapSmall, "Close"));
    }

    [Fact]
    public void Requery_after_rollback_resets_dynaset_to_1_and_snapshot_to_full_count_with_held_field()
    {
        // r-txn-1b/rs-position-after-rollback & r-txn-1b/rs-types-after-rollback (bs, bd)
        DatabaseObject db = Open(Txn1bAccdb);
        WorkspaceObject ws = _engine.DefaultWorkspace;

        SelectRecordsetObject bd = OpenSelect(db, "SELECT ID, V, Z FROM R ORDER BY ID", 2);
        SelectRecordsetObject bs = OpenSelect(db, "SELECT ID, V, Z FROM R ORDER BY ID", 4);
        SelectFieldObject heldBdId = GetField(bd, "ID");
        SelectFieldObject heldBsId = GetField(bs, "ID");

        Assert.Equal(1, IntValue(Ok(Get(bd, "RecordCount"))));
        Assert.Equal(8, IntValue(Ok(Get(bs, "RecordCount"))));

        Ok(Method(bd, "Move", 2));
        Assert.Equal(8, IntValue(Ok(Get(bd, "RecordCount"))));
        Assert.Equal(3, IntValue(Ok(Get(heldBdId, "Value"))));

        Ok(Method(bs, "Move", 2));
        Assert.Equal(3, IntValue(Ok(Get(heldBsId, "Value"))));

        Ok(Method(ws, "BeginTrans"));
        Ok(Method(db, "Execute", "UPDATE R SET V = V + 1 WHERE ID = 3", 128));
        Ok(Method(ws, "Rollback"));

        // Requery returns dynaset to row 1 and RecordCount 1; snapshot to row 1 and RecordCount 8.
        Ok(Method(bd, "Requery"));
        Assert.Equal(1, IntValue(Ok(Get(bd, "RecordCount"))));
        Assert.Equal(1, IntValue(Ok(Get(heldBdId, "Value"))));

        Ok(Method(bs, "Requery"));
        Assert.Equal(8, IntValue(Ok(Get(bs, "RecordCount"))));
        Assert.Equal(1, IntValue(Ok(Get(heldBsId, "Value"))));

        Ok(Method(bd, "Close"));
        Ok(Method(bs, "Close"));
    }

    [Fact]
    public void Requery_inside_depth1_transaction_sees_overlay_delete_and_after_commit_sees_committed()
    {
        // r-def-cursor-dao/delete-by-other-cursor-current-row (calls 71..75, 110..114)
        DatabaseObject db = Open(DefCursorMain);
        WorkspaceObject ws = _engine.DefaultWorkspace;

        Ok(Method(ws, "BeginTrans"));
        SelectRecordsetObject dyn = OpenSelect(db, "SELECT ID, N FROM T ORDER BY ID", 2);
        SelectRecordsetObject snap = OpenSelect(db, "SELECT ID, N FROM T ORDER BY ID", 4);
        Assert.Equal(1, IntValue(Ok(Get(dyn, "RecordCount"))));
        Assert.Equal(8, IntValue(Ok(Get(snap, "RecordCount"))));

        Ok(Method(dyn, "Move", 2));
        Assert.Equal(8, IntValue(Ok(Get(dyn, "RecordCount"))));
        Ok(Method(db, "Execute", "DELETE FROM T WHERE ID = 3", 128));

        Ok(Method(dyn, "Requery"));
        Assert.Equal(1, IntValue(Ok(Get(dyn, "RecordCount"))));
        Assert.Equal(1, IntValue(Ok(FieldValue(dyn, "ID"))));
        Ok(Method(dyn, "MoveLast"));
        Assert.Equal(7, IntValue(Ok(Get(dyn, "RecordCount"))));

        Ok(Method(snap, "Requery"));
        Assert.Equal(7, IntValue(Ok(Get(snap, "RecordCount"))));
        Assert.Equal(1, IntValue(Ok(FieldValue(snap, "ID"))));

        Ok(Method(dyn, "Close"));
        Ok(Method(snap, "Close"));
        Ok(Method(ws, "Rollback"));

        // r-def-cursor-dao/edit-under-commit: depth-0 dynaset Requery after CommitTrans succeeds and sees the committed state
        // (post-Requery RecordCount reset to 1 is established by r-txn-1b/rs-position-after-rollback and delete-by-other-cursor-current-row).
        SelectRecordsetObject committed = OpenSelect(db, "SELECT ID, N FROM T ORDER BY ID", 2);
        Ok(Method(committed, "MoveLast"));
        Assert.Equal(8, IntValue(Ok(Get(committed, "RecordCount"))));
        Assert.Equal(8, IntValue(Ok(FieldValue(committed, "ID"))));
        Ok(Method(ws, "BeginTrans"));
        Ok(Method(db, "Execute", "DELETE FROM T WHERE ID = 8", 128));
        Ok(Method(ws, "CommitTrans"));

        Ok(Method(committed, "Requery"));
        Assert.Equal(1, IntValue(Ok(FieldValue(committed, "ID"))));
        Assert.Equal(1, IntValue(Ok(Get(committed, "RecordCount"))));
        Ok(Method(committed, "MoveLast"));
        Assert.Equal(7, IntValue(Ok(FieldValue(committed, "ID"))));
        Assert.Equal(7, IntValue(Ok(Get(committed, "RecordCount"))));
        Ok(Method(committed, "Close"));
    }

    [Fact]
    public void Query_sources_RecordCount_at_open_partial_move_and_movelast()
    {
        // r-def-cursor-dao/pos-query-distinct-groupby, pos-query-top, pos-query-left-join, pos-query-union
        DatabaseObject db = Open(DefCursorMain);

        foreach (int type in new[] { 2, 4 })
        {
            SelectRecordsetObject distinct = OpenSelect(db, "SELECT DISTINCT N FROM T ORDER BY N", type);
            Assert.Equal(1, IntValue(Ok(Get(distinct, "RecordCount"))));
            Ok(Method(distinct, "MoveNext"));
            AssertNotModelled(Get(distinct, "RecordCount"), "NotModelled.QueryRecordCount.DistinctPartialMove");
            Ok(Method(distinct, "MoveLast"));
            Assert.Equal(7, IntValue(Ok(Get(distinct, "RecordCount"))));
            Ok(Method(distinct, "Close"));

            SelectRecordsetObject top3 = OpenSelect(db, "SELECT TOP 3 ID, N FROM T ORDER BY N DESC, ID", type);
            Assert.Equal(1, IntValue(Ok(Get(top3, "RecordCount"))));
            Ok(Method(top3, "MoveNext"));
            AssertNotModelled(Get(top3, "RecordCount"), "NotModelled.QueryRecordCount.TopPartialMove");
            Ok(Method(top3, "MoveLast"));
            Assert.Equal(3, IntValue(Ok(Get(top3, "RecordCount"))));
            Ok(Method(top3, "Close"));

            SelectRecordsetObject groupBy = OpenSelect(db, "SELECT N, Count(*) AS C FROM T GROUP BY N ORDER BY N", type);
            Assert.Equal(7, IntValue(Ok(Get(groupBy, "RecordCount"))));
            Ok(Method(groupBy, "Close"));

            SelectRecordsetObject leftJoin = OpenSelect(db, "SELECT PA.ID AS PAID, CH.ID AS CHID FROM PA LEFT JOIN CH ON PA.ID = CH.PID ORDER BY PA.ID, CH.ID", type);
            Assert.Equal(4, IntValue(Ok(Get(leftJoin, "RecordCount"))));
            Ok(Method(leftJoin, "Close"));

            SelectRecordsetObject union = OpenSelect(db, "SELECT ID, N FROM T WHERE ID <= 3 UNION SELECT ID + 10, N FROM T WHERE ID > 6", type);
            Assert.Equal(5, IntValue(Ok(Get(union, "RecordCount"))));
            Ok(Method(union, "Close"));
        }
    }

    [Fact]
    public void Unrecorded_RecordCount_and_Requery_cases_refuse_by_name()
    {
        DatabaseObject accdb = Open(Txn1bAccdb);
        DatabaseObject mdb = Open(Txn1bMdb, ".mdb");
        WorkspaceObject ws = _engine.DefaultWorkspace;

        // MDB single-table SELECT RecordCount (r-q53-conv-abspos-subq-loc/abspos-get-put-read-mdb) and depth-0 Requery
        // (r-q50-jk44-ddl-vis/vis-ado-txn-dao-read-mdb call 105, vis-cross-cursors-dml-mdb calls 81, 83) are recorded.
        SelectRecordsetObject mdbDyn = OpenSelect(mdb, "SELECT ID, V FROM R ORDER BY ID", 2);
        Assert.True(IntValue(Ok(Get(mdbDyn, "RecordCount"))) > 0);
        Ok(Method(mdbDyn, "Requery"));
        Ok(Method(mdbDyn, "Close"));

        // ForwardOnly RecordCount and Requery are unrecorded.
        SelectRecordsetObject fwd = OpenSelect(accdb, "SELECT ID, V FROM R ORDER BY ID", 8);
        AssertNotModelled(Get(fwd, "RecordCount"), "NotModelled.QueryRecordCount.ForwardOnly");
        AssertNotModelled(Method(fwd, "Requery"), "NotModelled.QueryRecordsetRequery");
        Ok(Method(fwd, "Close"));

        // Requery with a NewQueryDef argument is refused by name.
        SelectRecordsetObject ord = OpenSelect(accdb, "SELECT ID, V FROM R ORDER BY ID", 2);
        AssertNotModelled(Method(ord, "Requery", 1), "NotModelled.QueryRecordsetRequeryArg");

        // Requery at mismatched transaction depth (opened at 0, called inside BeginTrans) is refused by name.
        Ok(Method(ws, "BeginTrans"));
        AssertNotModelled(Method(ord, "Requery"), "NotModelled.QueryRecordsetRequery");

        // Recordset opened inside a rolled-back level refuses Requery via TransactionRefusal.
        SelectRecordsetObject inner = OpenSelect(accdb, "SELECT ID, V FROM R ORDER BY ID", 2);
        Ok(Method(ws, "Rollback"));
        AssertNotModelled(Method(inner, "Requery"), "NotModelled.Txn.RecordsetAfterRollback");
        Ok(Method(inner, "Close"));
        Ok(Method(ord, "Close"));

        // Unordered single-table SELECT dynaset: RecordCount is 1 at open (r-cursor-1b/open-argument-shapes),
        // post-move RecordCount and Requery are refused by name.
        SelectRecordsetObject noOrd = OpenSelect(accdb, "SELECT ID, V FROM R", 2);
        Assert.Equal(1, IntValue(Ok(Get(noOrd, "RecordCount"))));
        Ok(Method(noOrd, "MoveNext"));
        AssertNotModelled(Get(noOrd, "RecordCount"), "NotModelled.QueryRecordCount.UnorderedDynasetMove");
        AssertNotModelled(Method(noOrd, "Requery"), "NotModelled.QueryRecordsetRequery");
        Ok(Method(noOrd, "Close"));

        SelectRecordsetObject distinct = OpenSelect(accdb, "SELECT DISTINCT V FROM R ORDER BY V", 2);
        AssertNotModelled(Method(distinct, "Requery"), "NotModelled.QueryRecordsetRequery");
        Ok(Method(distinct, "Close"));

        // Requery hitting an ACE failure during reopen refuses by name without publishing DAO.Database or dropping the cursor.
        SelectRecordsetObject failOnRequery = OpenSelect(accdb, "SELECT ID, V FROM R ORDER BY 100 / (V + 1000), ID", 4);
        Assert.Equal(8, IntValue(Ok(Get(failOnRequery, "RecordCount"))));
        int firstId = IntValue(Ok(FieldValue(failOnRequery, "ID")));
        Ok(Method(accdb, "Execute", "UPDATE R SET V = -1000 WHERE ID = 1", 128));
        AssertNotModelled(Method(failOnRequery, "Requery"), "NotModelled.QueryRecordsetRequeryFailure");
        Assert.Equal(8, IntValue(Ok(Get(failOnRequery, "RecordCount"))));
        Assert.Equal(firstId, IntValue(Ok(FieldValue(failOnRequery, "ID"))));
        Ok(Method(failOnRequery, "Close"));
    }

    private string CopyFixture(string fixture, string extension = ".accdb")
    {
        string path = Path.Combine(Path.GetTempPath(), $"calcdb-dao-select-pop-{Guid.NewGuid():N}{extension}");
        File.WriteAllBytes(path, CalcDb.Evidence.ArtifactStore.ForRepository(DeclarationGenerationTests.Root()).ReadAll(fixture));
        _files.Add(path);
        return path;
    }

    private DatabaseObject Open(string fixture, string extension = ".accdb")
    {
        string path = CopyFixture(fixture, extension);
        DatabaseObject db = Recover<DatabaseObject>(Ok(_engine.DefaultWorkspace.OpenDatabase([Arg(path), Arg(false), Arg(false)])));
        _databases.Add(db);
        return db;
    }

    private SelectRecordsetObject OpenSelect(DatabaseObject db, string sql, int type) =>
        Recover<SelectRecordsetObject>(Ok(Method(db, "OpenRecordset", sql, type)));

    private SelectFieldObject GetField(DispatchObject rs, string name)
    {
        SelectFieldsObject fields = Recover<SelectFieldsObject>(Ok(Get(rs, "Fields")));
        return Recover<SelectFieldObject>(Ok(Get(fields, "Item", name)));
    }

    private CallResult FieldValue(DispatchObject rs, string name)
    {
        CallResult fields = Get(rs, "Fields");
        if (fields.Failure is not null)
        {
            return fields;
        }
        SelectFieldsObject f = Recover<SelectFieldsObject>(fields);
        CallResult item = Get(f, "Item", name);
        if (item.Failure is not null)
        {
            return item;
        }
        return Get(Recover<SelectFieldObject>(item), "Value");
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

    private static int IntValue(CallResult result)
    {
        NativeVariant v = result.HasScalar ? result.Scalar : *result.Value!.Pointer;
        Assert.Equal(VarType.I4, (VarType)v.Vt);
        int value = unchecked((int)v.Int64);
        result.ReleaseUntaken();
        return value;
    }

    private T Recover<T>(CallResult result) where T : ComObject
    {
        Assert.True(result.HasScalar && (VarType)result.Scalar.Vt == VarType.Dispatch, result.Failure?.Description);
        T obj = ComObject.Recover<T>(result.Scalar.Pointer);
        _held.Add((obj, result.Scalar.Pointer));
        return obj;
    }
}
