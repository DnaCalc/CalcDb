using System.Runtime.InteropServices;

using CalcDb.Com.Com;
using CalcDb.Com.Dao;
using CalcDb.Com.Declarations;
using CalcDb.Com.Host;
using CalcDb.Evidence;
using CalcDb.Foundation;
using CalcDb.Values;

namespace CalcDb.Com.Tests;

/// <summary>
/// Managed COM tests for Wave 14 <c>CalcDb.Com.Dao</c> members and QueryDef/Parameter slices (W-08, R-DAO-1, R-CAT-1):
/// <list type="bullet">
/// <item><c>Database.Recordsets</c> and <c>SelectRecordset.Name</c> (<c>r-dao-1b/db-members-accdb</c>, <c>db-members-mdb</c>, <c>querydef-state-after-execute</c>)</item>
/// <item>Unsupported collection <c>Append</c>/<c>Delete</c> refusals (<c>r-dao-1b/coll-append-delete-unsupported</c>)</item>
/// <item><c>TableDef.Fields.Delete</c> missing/relationship/indexed errors (<c>r-dao-1b/coll-delete-field-indexed</c>)</item>
/// <item><c>QueryDef.Fields</c> COM identity preservation (<c>r-dao-1b/coll-item-query-family</c>)</item>
/// <item><c>Index</c> create readback: pre-refresh <c>DistinctCount</c> 0 -&gt; post-refresh 1 and <c>Fields</c> identity (<c>r-dao-1b/index-create-readback</c>)</item>
/// <item><c>QueryDef.hStmt</c>, inherited <c>QueryDef.Properties</c>, <c>QueryDef.Fields</c> members, <c>Parameter.Properties</c> (<c>r-dao-1b/querydef-members-each</c>, <c>querydef-properties-enumerated</c>, <c>querydef-fields-members</c>, <c>parameter-members-each</c>, <c>parameter-properties-enumerated</c>)</item>
/// <item><c>QueryDef.OpenRecordset</c> on actions (3219), PassThrough run (3151/3065), <c>ReturnsRecords</c> put, and persisted <c>MaxRecords</c> put (<c>r-cat-1b/qd-openrecordset-on-action</c>, <c>qd-passthrough-saved-run</c>, <c>qd-returnsrecords-put</c>, <c>qd-maxrecords-put</c>)</item>
/// </list>
/// </summary>
public sealed unsafe class DaoMembersAndQueryDefTests : IDisposable
{
    private const string RichAccdb = "3b512c8f10e3ab22e434d9d6e69435bf231a310044b20cdb1c03069c1be34e95"; // r-dao-1b-rich.accdb
    private const string RichMdb = "c0c012762114c7d6a0ab6369e1ffac46cea8718f22c86152dce1f8ba3c3a7c75";   // r-dao-1b-rich-mdb.mdb
    private const string QdAccdb = "54d09662048f566a7c2de5f0128cf3b8891a6e0452d18f60ec7a6f247b552616";   // r-cat-1b-qd.accdb

    private readonly List<string> _files = [];
    private readonly List<nint> _cells = [];
    private readonly List<nint> _held = [];
    private readonly EngineState _engine = new(new HostBinding(0xDA0_1402, Standalone: true, 0, 0, 0, 0, 0));

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
            if (File.Exists(file))
            {
                File.Delete(file);
            }
        }
    }

    private string CopyFixture(string sha256, string extension)
    {
        string path = Path.Combine(Path.GetTempPath(), $"calcdb-w14-{Guid.NewGuid():N}{extension}");
        File.WriteAllBytes(path, ArtifactStore.ForRepository(DeclarationGenerationTests.Root()).ReadAll(sha256));
        _files.Add(path);
        return path;
    }

    private DatabaseObject OpenPath(string path, bool readOnly = false) =>
        Recover<DatabaseObject>(Ok(_engine.DefaultWorkspace.OpenDatabase([Arg(path), Arg(false), Arg(readOnly)])));

    private DatabaseObject Open(string fixture, string extension = ".accdb", bool readOnly = true) =>
        OpenPath(CopyFixture(fixture, extension), readOnly);

    private nint Arg(object? value)
    {
        var v = (NativeVariant*)NativeMemory.AllocZeroed((nuint)sizeof(NativeVariant));
        switch (value)
        {
            case int i:
                v->Vt = (ushort)VarType.I4;
                v->Int64 = (uint)i;
                break;
            case short s:
                v->Vt = (ushort)VarType.I2;
                v->Int64 = (ushort)s;
                break;
            case bool b:
                v->Vt = (ushort)VarType.Bool;
                v->Int64 = b ? 0xFFFF : 0;
                break;
            case string text:
                v->Vt = (ushort)VarType.Bstr;
                v->Pointer = Marshal.StringToBSTR(text);
                break;
            case ComObject obj:
                v->Vt = (ushort)VarType.Dispatch;
                nint ptr = 0;
                Assert.Equal(0, obj.QueryInterface(NativeRoute.IidIDispatch, &ptr));
                _held.Add(ptr);
                v->Pointer = ptr;
                break;
        }
        _cells.Add((nint)v);
        return (nint)v;
    }

    private nint[] Pack(params object?[] values) => [.. values.Select(Arg)];

    private CallResult Call(DispatchObject target, string name, InvokeKind kind, params object?[] args) =>
        target.Run(target.Declaration.Member(name, kind), Pack(args));

    private CallResult Get(DispatchObject target, string name, params object?[] args) =>
        Call(target, name, InvokeKind.PropertyGet, args);

    private CallResult Put(DispatchObject target, string name, object? value) =>
        Call(target, name, InvokeKind.PropertyPut, value);

    private CallResult Method(DispatchObject target, string name, params object?[] args) =>
        Call(target, name, InvokeKind.Method, args);

    private static CallResult Ok(CallResult result)
    {
        Assert.Null(result.Failure);
        return result;
    }

    private static Failure FailDao(CallResult result, int number, string source, string? description = null, bool? publishesErrorInfo = null)
    {
        Failure f = Assert.IsType<Failure>(result.Failure);
        Assert.Equal(FailureKind.Dao, f.Kind);
        Assert.Equal(number, f.Number);
        Assert.Equal(source, f.Source);
        if (description is not null)
        {
            Assert.Equal(description, f.Description);
        }
        if (publishesErrorInfo is bool expectedEi)
        {
            Assert.Equal(expectedEi, f.PublishesErrorInfo);
        }
        return f;
    }

    private T Recover<T>(CallResult result) where T : ComObject
    {
        Assert.Null(result.Failure);
        nint ptr;
        if (result.HasScalar)
        {
            Assert.Equal((ushort)VarType.Dispatch, result.Scalar.Vt);
            ptr = result.Scalar.Pointer;
            _held.Add(ptr);
            return ComObject.Recover<T>(ptr);
        }
        Assert.NotNull(result.Value);
        using OwnedVariant v = result.Value!;
        Assert.Equal((ushort)VarType.Dispatch, v.Pointer->Vt);
        ptr = v.Pointer->Pointer;
        Marshal.AddRef(ptr);
        _held.Add(ptr);
        return ComObject.Recover<T>(ptr);
    }

    private static (VarType Vt, long Raw) Scalar(CallResult result)
    {
        Assert.Null(result.Failure);
        VarType vt;
        long raw;
        if (result.HasScalar)
        {
            vt = (VarType)result.Scalar.Vt;
            raw = result.Scalar.Int64;
        }
        else
        {
            Assert.NotNull(result.Value);
            using OwnedVariant v = result.Value!;
            vt = (VarType)v.Pointer->Vt;
            raw = v.Pointer->Int64;
        }
        if (vt is VarType.Bool or VarType.I2)
        {
            raw = (short)raw;
        }
        return (vt, raw);
    }

    private static string Text(CallResult result)
    {
        Assert.Null(result.Failure);
        if (result.HasScalar)
        {
            Assert.Equal((ushort)VarType.Bstr, result.Scalar.Vt);
            string s = Marshal.PtrToStringBSTR(result.Scalar.Pointer);
            result.ReleaseUntaken();
            return s;
        }
        Assert.NotNull(result.Value);
        using OwnedVariant v = result.Value!;
        Assert.Equal((ushort)VarType.Bstr, v.Pointer->Vt);
        return Marshal.PtrToStringBSTR(v.Pointer->Pointer);
    }

    [Theory]
    [InlineData(RichAccdb, ".accdb", "12.0")]
    [InlineData(RichMdb, ".mdb", "4.0")]
    public void Database_Recordsets_counts_open_recordsets_and_exposes_saved_query_name(string fixture, string ext, string expectedVersion)
    {
        // r-dao-1b/db-members-accdb (call 25-26), r-dao-1b/db-members-mdb (call 25-26),
        // r-dao-1b/querydef-state-after-execute (calls 22-29).
        DatabaseObject db = Open(fixture, ext, readOnly: true);
        Assert.Equal(expectedVersion, Text(Get(db, "Version")));

        DaoObject rsets = Recover<DaoObject>(Get(db, "Recordsets"));
        Assert.Equal((VarType.I2, 0L), Scalar(Get(rsets, "Count")));

        DaoObject qds = Recover<DaoObject>(Get(db, "QueryDefs"));
        QueryDefObject qsel = Recover<QueryDefObject>(Get(qds, "Item", "QSel"));
        SelectRecordsetObject srs = Recover<SelectRecordsetObject>(Method(qsel, "OpenRecordset", 4));

        Assert.Equal((VarType.I2, 1L), Scalar(Get(rsets, "Count")));
        Assert.Equal("QSel", Text(Get(srs, "Name")));
        Assert.Contains("NotModelled.RecordsetsItem", Get(rsets, "Item", 0).Failure?.Description);

        Ok(Method(srs, "Close"));
        Assert.Equal((VarType.I2, 0L), Scalar(Get(rsets, "Count")));
        Ok(Method(db, "Close"));
        Assert.Contains("NotModelled.RecordsetsClosed", Get(rsets, "Count").Failure?.Description);
    }

    [Fact]
    public void Unsupported_collection_Append_and_Delete_match_ACE_errors()
    {
        // r-dao-1b/coll-append-delete-unsupported calls 31-38.
        DatabaseObject db = Open(RichAccdb, ".accdb", readOnly: false);

        nint engPtr = 0;
        Assert.Equal(0, DBEngineObject.Create(_engine.Binding, 0, NativeRoute.IidIDispatch, &engPtr));
        _held.Add(engPtr);
        DBEngineObject eng = ComObject.Recover<DBEngineObject>(engPtr);
        WorkspacesObject wss = Recover<WorkspacesObject>(Get(eng, "Workspaces"));
        FailDao(Method(wss, "Delete", "x"), 3273, "DAO.Workspaces", DaoErrors.MethodNotApplicable, publishesErrorInfo: false);
        FailDao(Method(wss, "Append", db), 3219, "DAO.Workspaces", DaoErrors.InvalidOperation, publishesErrorInfo: false);

        DaoObject tds = Recover<DaoObject>(Get(db, "TableDefs"));
        TableDefObject parent = Recover<TableDefObject>(Get(tds, "Item", "Parent"));
        DaoObject newField = Recover<DaoObject>(Method(parent, "CreateField", "Z", 4));

        RecordsetObject rs = Recover<RecordsetObject>(Method(db, "OpenRecordset", "Parent", 4));
        FieldsObject rfl = Recover<FieldsObject>(Get(rs, "Fields"));
        FailDao(Method(rfl, "Append", newField), 3273, "DAO.Fields", DaoErrors.MethodNotApplicable, publishesErrorInfo: false);
        FailDao(Method(rfl, "Delete", "ID"), 3273, "DAO.Fields", DaoErrors.MethodNotApplicable, publishesErrorInfo: false);

        DaoObject ti = Recover<DaoObject>(Get(parent, "Indexes"));
        DaoObject pk = Recover<DaoObject>(Get(ti, "Item", "PK_Parent"));
        DaoObject pkf = Recover<DaoObject>(Get(pk, "Fields"));
        FailDao(Method(pkf, "Delete", "ID"), 3219, "DAO.Fields", DaoErrors.InvalidOperation, publishesErrorInfo: false);

        DaoObject rels = Recover<DaoObject>(Get(db, "Relations"));
        DaoObject rm = Recover<DaoObject>(Get(rels, "Item", "RelMain"));
        DaoObject rmf = Recover<DaoObject>(Get(rm, "Fields"));
        FailDao(Method(rmf, "Delete", "ID"), 3219, "DAO.Fields", DaoErrors.InvalidOperation, publishesErrorInfo: false);

        DaoObject qds = Recover<DaoObject>(Get(db, "QueryDefs"));
        QueryDefObject qs = Recover<QueryDefObject>(Get(qds, "Item", "QSel"));
        QueryFieldsObject qsf = Recover<QueryFieldsObject>(Get(qs, "Fields"));
        FailDao(Method(qsf, "Delete", "ID"), 3273, "DAO.Fields", DaoErrors.MethodNotApplicable, publishesErrorInfo: false);

        Ok(Method(rs, "Close"));
        Ok(Method(db, "Close"));
    }

    [Fact]
    public void TableDef_Fields_Delete_enforces_missing_relationship_and_index_precedence()
    {
        // r-dao-1b/coll-delete-field-indexed calls 19, 27, 35, 43, 51.
        DatabaseObject db = Open(RichAccdb, ".accdb", readOnly: false);
        DaoObject tds = Recover<DaoObject>(Get(db, "TableDefs"));
        TableDefObject parent = Recover<TableDefObject>(Get(tds, "Item", "Parent"));
        TableDefObject child = Recover<TableDefObject>(Get(tds, "Item", "Child"));
        DaoObject pf = Recover<DaoObject>(Get(parent, "Fields"));
        DaoObject cf = Recover<DaoObject>(Get(child, "Fields"));

        FailDao(Method(pf, "Delete", "Qty"), 3280, "DAO.Fields", DaoErrors.CannotDeleteIndexedField, publishesErrorInfo: false);
        FailDao(Method(pf, "Delete", "NoSuchField"), 3265, "DAO.Fields", DaoErrors.ItemNotFound, publishesErrorInfo: false);
        FailDao(Method(pf, "Delete", "Name"), 3280, "DAO.Fields", DaoErrors.CannotDeleteIndexedField, publishesErrorInfo: false);
        FailDao(Method(pf, "Delete", "ID"), 3303, "DAO.Fields", DaoErrors.CannotDeleteFieldInRelation, publishesErrorInfo: false);
        FailDao(Method(cf, "Delete", "PID"), 3303, "DAO.Fields", DaoErrors.CannotDeleteFieldInRelation, publishesErrorInfo: false);

        Ok(Method(db, "Close"));
    }

    [Fact]
    public void QueryDef_Fields_Item_preserves_COM_identity_and_Properties_ordinal_miss_is_3265()
    {
        // r-dao-1b/coll-item-query-family calls 39, 45, 51, 53, 59.
        DatabaseObject db = Open(RichAccdb, ".accdb", readOnly: true);
        DaoObject qds = Recover<DaoObject>(Get(db, "QueryDefs"));
        QueryDefObject qsel = Recover<QueryDefObject>(Get(qds, "Item", "QSel"));
        QueryFieldsObject qsf = Recover<QueryFieldsObject>(Get(qsel, "Fields"));

        QueryFieldObject f0 = Recover<QueryFieldObject>(Get(qsf, "Item", 0));
        QueryFieldObject fByName = Recover<QueryFieldObject>(Get(qsf, "Item", "ID"));
        Assert.Same(f0, fByName);
        Assert.Equal("ID", Text(Get(f0, "Name")));

        PropertiesObject dprops = Recover<PropertiesObject>(Get(db, "Properties"));
        FailDao(Get(dprops, "Item", 99), 3265, "DAO.Properties", DaoErrors.ItemNotFound, publishesErrorInfo: false);
        FailDao(Get(dprops, "Item", -1), 3265, "DAO.Properties", DaoErrors.ItemNotFound, publishesErrorInfo: false);
        FailDao(Get(dprops, "Item", "NoSuchProp"), 3270, "DAO.Properties", DaoErrors.PropertyNotFound, publishesErrorInfo: false);

        Ok(Method(db, "Close"));
    }

    [Fact]
    public void Index_create_readback_keeps_DistinctCount_zero_until_Refresh_and_preserves_Fields_identity()
    {
        // r-dao-1b/index-create-readback calls 7-38.
        DatabaseObject db = Open(RichAccdb, ".accdb", readOnly: false);
        DaoObject tds = Recover<DaoObject>(Get(db, "TableDefs"));
        TableDefObject typed = Recover<TableDefObject>(Get(tds, "Item", "Typed"));
        DaoObject idxs = Recover<DaoObject>(Get(typed, "Indexes"));

        NewIndexObject nx = Recover<NewIndexObject>(Method(typed, "CreateIndex", "IX_Typed_New"));
        NewIndexFieldObject nxa = Recover<NewIndexFieldObject>(Method(nx, "CreateField", "TText"));
        Ok(Put(nxa, "Attributes", 1));
        NewIndexFieldObject nxb = Recover<NewIndexFieldObject>(Method(nx, "CreateField", "TLong"));
        NewIndexFieldsObject nxf = Recover<NewIndexFieldsObject>(Get(nx, "Fields"));
        Ok(Method(nxf, "Append", nxa));
        Ok(Method(nxf, "Append", nxb));
        Ok(Put(nx, "IgnoreNulls", true));
        Ok(Put(nx, "Unique", true));
        Ok(Method(idxs, "Append", nx));

        Assert.Equal((VarType.I4, 0L), Scalar(Get(nx, "DistinctCount")));
        NewIndexObject rx = Recover<NewIndexObject>(Get(idxs, "Item", "IX_Typed_New"));
        Assert.Same(nx, rx);
        Assert.Equal((VarType.I4, 0L), Scalar(Get(rx, "DistinctCount")));

        NewIndexFieldsObject rxf = Recover<NewIndexFieldsObject>(Get(rx, "Fields"));
        Assert.Same(nxf, rxf);
        Assert.Equal((VarType.I2, 2L), Scalar(Get(rxf, "Count")));
        NewIndexFieldObject f0 = Recover<NewIndexFieldObject>(Get(rxf, "Item", 0));
        NewIndexFieldObject f1 = Recover<NewIndexFieldObject>(Get(rxf, "Item", 1));
        Assert.Same(nxa, f0);
        Assert.Same(nxb, f1);
        Assert.Equal("TText", Text(Get(f0, "Name")));
        Assert.Equal((VarType.I4, 1L), Scalar(Get(f0, "Attributes")));
        Assert.Equal("TLong", Text(Get(f1, "Name")));
        Assert.Equal((VarType.I4, 0L), Scalar(Get(f1, "Attributes")));

        Ok(Method(idxs, "Refresh"));
        Assert.Equal((VarType.I4, 1L), Scalar(Get(rx, "DistinctCount")));
        Assert.Equal((VarType.I4, 1L), Scalar(Get(nx, "DistinctCount")));
        Assert.Contains("NotModelled.AttachedIndexFieldPut", Put(f0, "Name", "Other").Failure?.Description);

        Ok(Method(db, "Close"));
        FailDao(Get(f0, "Name"), 3420, "DAO.Field", DaoErrors.ObjectInvalid, publishesErrorInfo: false);
    }

    [Fact]
    public void QueryDef_hStmt_and_inherited_Properties_match_sealed_records()
    {
        // r-dao-1b/querydef-members-each (hStmt 3251), r-dao-1b/querydef-properties-enumerated (14 vs 15 with Inherited=True).
        DatabaseObject db = Open(RichAccdb, ".accdb", readOnly: true);
        DaoObject qds = Recover<DaoObject>(Get(db, "QueryDefs"));

        QueryDefObject qsel = Recover<QueryDefObject>(Get(qds, "Item", "QSel"));
        FailDao(Get(qsel, "hStmt"), 3251, "DAO.QueryDef", DaoErrors.NotSupportedForType, publishesErrorInfo: false);

        PropertiesObject qselProps = Recover<PropertiesObject>(Get(qsel, "Properties"));
        Assert.Equal((VarType.I2, 15L), Scalar(Get(qselProps, "Count")));
        PropertyObject desc = Recover<PropertyObject>(Get(qselProps, "Item", 14));
        Assert.Equal("Description", Text(Get(desc, "Name")));
        Assert.Equal((VarType.I2, 10L), Scalar(Get(desc, "Type")));
        Assert.Equal((VarType.Bool, -1L), Scalar(Get(desc, "Inherited")));
        Assert.Equal("parent table", Text(Get(desc, "Value")));

        QueryDefObject qcross = Recover<QueryDefObject>(Get(qds, "Item", "QCross"));
        PropertiesObject qcrossProps = Recover<PropertiesObject>(Get(qcross, "Properties"));
        Assert.Equal((VarType.I2, 15L), Scalar(Get(qcrossProps, "Count")));

        QueryDefObject qunion = Recover<QueryDefObject>(Get(qds, "Item", "QUnion"));
        PropertiesObject qunionProps = Recover<PropertiesObject>(Get(qunion, "Properties"));
        Assert.Equal((VarType.I2, 14L), Scalar(Get(qunionProps, "Count")));

        Ok(Method(db, "Close"));
    }

    [Fact]
    public void QueryDef_Fields_members_match_QSel_QParam_and_QUnion_records()
    {
        // r-dao-1b/querydef-fields-members calls 7, 11, 15.
        DatabaseObject db = Open(RichAccdb, ".accdb", readOnly: true);
        DaoObject qds = Recover<DaoObject>(Get(db, "QueryDefs"));

        // QSel: ID (Long, Attributes 33, DataUpdatable -1, Required 0, Properties 24),
        //       Name (Text 30, Attributes 34, DataUpdatable -1, Required -1, Properties 25).
        QueryDefObject qsel = Recover<QueryDefObject>(Get(qds, "Item", "QSel"));
        QueryFieldsObject qsf = Recover<QueryFieldsObject>(Get(qsel, "Fields"));
        Assert.Equal((VarType.I2, 2L), Scalar(Get(qsf, "Count")));

        QueryFieldObject fId = Recover<QueryFieldObject>(Get(qsf, "Item", 0));
        Assert.Equal((VarType.Bool, 0L), Scalar(Get(fId, "AllowZeroLength")));
        Assert.Equal((VarType.I4, 33L), Scalar(Get(fId, "Attributes")));
        Assert.Equal((VarType.I4, 1033L), Scalar(Get(fId, "CollatingOrder")));
        Assert.Equal((VarType.I2, 0L), Scalar(Get(fId, "CollectionIndex")));
        Assert.Equal((VarType.Bool, -1L), Scalar(Get(fId, "DataUpdatable")));
        Assert.Equal((VarType.Bool, 0L), Scalar(Get(fId, "Required")));
        Assert.Equal((VarType.I4, 4L), Scalar(Get(fId, "Size")));
        Assert.Equal("ID", Text(Get(fId, "SourceField")));
        Assert.Equal("Parent", Text(Get(fId, "SourceTable")));
        Assert.Equal((VarType.I2, 4L), Scalar(Get(fId, "Type")));
        FailDao(Get(fId, "ComplexType"), 3219, "DAO.Field", DaoErrors.InvalidOperation, publishesErrorInfo: false);
        FailDao(Get(fId, "FieldSize"), 3267, "DAO.Field", DaoErrors.RecordsetFieldOnly, publishesErrorInfo: false);
        FailDao(Get(fId, "OriginalValue"), 3251, "DAO.Field", DaoErrors.NotSupportedForType, publishesErrorInfo: false);
        FailDao(Get(fId, "Value"), 3219, "DAO.Field", DaoErrors.InvalidOperation, publishesErrorInfo: true);
        PropertiesObject fIdProps = Recover<PropertiesObject>(Get(fId, "Properties"));
        Assert.Equal((VarType.I2, 24L), Scalar(Get(fIdProps, "Count")));
        Assert.Contains("NotModelled.PropertyEntries", Get(fIdProps, "Item", 0).Failure?.Description);

        QueryFieldObject fName = Recover<QueryFieldObject>(Get(qsf, "Item", 1));
        Assert.Equal((VarType.I4, 34L), Scalar(Get(fName, "Attributes")));
        Assert.Equal((VarType.I2, 0L), Scalar(Get(fName, "CollectionIndex")));
        Assert.Equal((VarType.I2, 1L), Scalar(Get(fName, "OrdinalPosition")));
        Assert.Equal((VarType.Bool, -1L), Scalar(Get(fName, "Required")));
        Assert.Equal((VarType.I4, 30L), Scalar(Get(fName, "Size")));
        Assert.Equal((VarType.I2, 25L), Scalar(Get(Recover<PropertiesObject>(Get(fName, "Properties")), "Count")));

        // QUnion: ID (Long, Attributes 1, DataUpdatable 0, SourceField "", SourceTable "", Properties 24).
        QueryDefObject qunion = Recover<QueryDefObject>(Get(qds, "Item", "QUnion"));
        QueryFieldsObject quf = Recover<QueryFieldsObject>(Get(qunion, "Fields"));
        Assert.Equal((VarType.I2, 1L), Scalar(Get(quf, "Count")));
        QueryFieldObject uId = Recover<QueryFieldObject>(Get(quf, "Item", 0));
        Assert.Equal((VarType.I4, 1L), Scalar(Get(uId, "Attributes")));
        Assert.Equal((VarType.Bool, 0L), Scalar(Get(uId, "DataUpdatable")));
        Assert.Equal("", Text(Get(uId, "SourceField")));
        Assert.Equal("", Text(Get(uId, "SourceTable")));
        Assert.Equal((VarType.I2, 24L), Scalar(Get(Recover<PropertiesObject>(Get(uId, "Properties")), "Count")));

        Ok(Method(db, "Close"));
    }

    [Fact]
    public void Parameter_Properties_enumerates_four_builtins_with_parameter_type()
    {
        // r-dao-1b/parameter-members-each, r-dao-1b/parameter-properties-enumerated.
        DatabaseObject db = Open(RichAccdb, ".accdb", readOnly: true);
        DaoObject qds = Recover<DaoObject>(Get(db, "QueryDefs"));
        QueryDefObject qparam = Recover<QueryDefObject>(Get(qds, "Item", "QParam"));
        DaoObject pms = Recover<DaoObject>(Get(qparam, "Parameters"));

        ParameterObject pId = Recover<ParameterObject>(Get(pms, "Item", 0));
        PropertiesObject props0 = Recover<PropertiesObject>(Get(pId, "Properties"));
        Assert.Equal((VarType.I2, 4L), Scalar(Get(props0, "Count")));
        Assert.Equal("Value", Text(Get(Recover<PropertyObject>(Get(props0, "Item", 0)), "Name")));
        Assert.Equal((VarType.I2, 4L), Scalar(Get(Recover<PropertyObject>(Get(props0, "Item", 0)), "Type")));
        Assert.Equal((VarType.Empty, 0L), Scalar(Get(Recover<PropertyObject>(Get(props0, "Item", 0)), "Value")));
        Assert.Equal("Name", Text(Get(Recover<PropertyObject>(Get(props0, "Item", 1)), "Name")));
        Assert.Equal("pId", Text(Get(Recover<PropertyObject>(Get(props0, "Item", 1)), "Value")));
        Assert.Equal("Type", Text(Get(Recover<PropertyObject>(Get(props0, "Item", 2)), "Name")));
        Assert.Equal((VarType.I2, 4L), Scalar(Get(Recover<PropertyObject>(Get(props0, "Item", 2)), "Value")));
        Assert.Equal("Direction", Text(Get(Recover<PropertyObject>(Get(props0, "Item", 3)), "Name")));
        Assert.Equal((VarType.I2, 1L), Scalar(Get(Recover<PropertyObject>(Get(props0, "Item", 3)), "Value")));

        ParameterObject pName = Recover<ParameterObject>(Get(pms, "Item", 1));
        PropertiesObject props1 = Recover<PropertiesObject>(Get(pName, "Properties"));
        Assert.Equal((VarType.I2, 10L), Scalar(Get(Recover<PropertyObject>(Get(props1, "Item", 0)), "Type")));

        Ok(Method(db, "Close"));
    }

    [Fact]
    public void QueryDef_OpenRecordset_on_action_and_PassThrough_saved_run_match_ACE()
    {
        // r-cat-1b/qd-openrecordset-on-action (calls 7, 17, 27, 37, 47),
        // r-cat-1b/qd-passthrough-saved-run (calls 11, 17).
        DatabaseObject db = Open(QdAccdb, ".accdb", readOnly: false);
        DaoObject qds = Recover<DaoObject>(Get(db, "QueryDefs"));

        foreach (string actionName in new[] { "QUpd", "QDel", "QApp", "QMake", "QDdl" })
        {
            QueryDefObject q = Recover<QueryDefObject>(Get(qds, "Item", actionName));
            FailDao(Method(q, "OpenRecordset", 4), 3219, "DAO.Database", DaoErrors.InvalidOperation, publishesErrorInfo: false);
        }

        QueryDefObject qpass = Recover<QueryDefObject>(Get(qds, "Item", "QPass"));
        FailDao(Method(qpass, "OpenRecordset", 4), 3151, "DAO.Database", "ODBC--connection to 'CalcDbProbeNoSuchDsn' failed.", publishesErrorInfo: false);
        FailDao(Method(qpass, "Execute"), 3065, "DAO.QueryDef", "Cannot execute a select query.", publishesErrorInfo: false);
        Assert.Equal((VarType.Bool, -1L), Scalar(Get(qpass, "ReturnsRecords")));

        Ok(Method(db, "Close"));
    }

    [Fact]
    public void QueryDef_ReturnsRecords_put_and_MaxRecords_put_persist_across_reopen()
    {
        // r-cat-1b/qd-returnsrecords-put (calls 7-9, 24-27, 44-45),
        // r-cat-1b/qd-maxrecords-put (calls 7-15, 20, 28).
        string path = CopyFixture(QdAccdb, ".accdb");
        DatabaseObject db = OpenPath(path, readOnly: false);
        DaoObject qds = Recover<DaoObject>(Get(db, "QueryDefs"));

        QueryDefObject qsel = Recover<QueryDefObject>(Get(qds, "Item", "QSel"));
        Ok(Put(qsel, "ReturnsRecords", false));
        Assert.Equal((VarType.Bool, -1L), Scalar(Get(qsel, "ReturnsRecords")));
        Assert.Equal((VarType.I2, 0L), Scalar(Get(qsel, "Type")));

        Assert.Equal((VarType.I4, 0L), Scalar(Get(qsel, "MaxRecords")));
        Ok(Put(qsel, "MaxRecords", 2));
        Assert.Equal((VarType.I4, 2L), Scalar(Get(qsel, "MaxRecords")));
        Ok(Put(qsel, "MaxRecords", 0));
        Assert.Equal((VarType.I4, 0L), Scalar(Get(qsel, "MaxRecords")));
        Ok(Put(qsel, "MaxRecords", 2));
        Ok(Method(db, "Close"));

        DatabaseObject db2 = OpenPath(path, readOnly: true);
        DaoObject qds2 = Recover<DaoObject>(Get(db2, "QueryDefs"));
        QueryDefObject qsel2 = Recover<QueryDefObject>(Get(qds2, "Item", "QSel"));
        Assert.Equal((VarType.I4, 2L), Scalar(Get(qsel2, "MaxRecords")));
        Assert.Equal((VarType.Bool, -1L), Scalar(Get(qsel2, "ReturnsRecords")));
        Ok(Method(db2, "Close"));
    }
}
