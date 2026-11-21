using System.Runtime.InteropServices;

using CalcDb.Com.Com;
using CalcDb.Com.Dao;
using CalcDb.Com.Declarations;
using CalcDb.Com.Host;
using CalcDb.Foundation;
using CalcDb.Values;

namespace CalcDb.Com.Tests;

/// <summary>
/// Verifies DAO query-recordset metadata read from <see cref="CalcDb.Sql.SelectCursor"/> and
/// <see cref="CalcDb.Sql.ResultColumn"/> (register row 3rwk; R-CURSOR-1 CUR-PROV, R-SQL-3 SQL-DR-01..05):
/// <see cref="SelectRecordsetObject"/> <c>Updatable</c> and <see cref="SelectFieldObject"/> <c>DataUpdatable</c>,
/// <c>SourceTable</c>, and <c>SourceField</c>, plus named refusals where no retained record decides a case.
/// </summary>
public sealed unsafe class DaoSelectMetadataTests : IDisposable
{
    private const string DefCursorMain = "e6c0d23cc00a646991cc7c6cabd74305f2bc15ce8267745527e686ac13dd8dc0"; // r-def-cursor-dao-main.accdb
    private const string Cursor1bMain = "47f8fa36bfcd0df6185061dc39e47fd46cdac20f372942f9a8ff3a177a8bfbf6"; // r-cursor-1b-main.accdb
    private const string Sql23Sel = "5d31b97ff489f35a6ad7ed5ffe8265894faeb8f5d3eed2c2d1840bd8ee0bbc34"; // r-sql-2-3-sel-accdb.accdb
    private const string Dao1bRich = "3b512c8f10e3ab22e434d9d6e69435bf231a310044b20cdb1c03069c1be34e95"; // r-dao-1b-rich.accdb

    private readonly List<string> _files = [];
    private readonly List<nint> _cells = [];
    private readonly List<nint> _held = [];
    private readonly EngineState _engine = new(new HostBinding(0xDA0_7E59, Standalone: true, 0, 0, 0, 0, 0));

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

    [Theory]
    // r-fix-cursor-dao/prov-sources-errors-per-call, r-def-cursor-dao/prov-distinct-distinctrow-plain, prov-missing-projected-key
    [InlineData(DefCursorMain, "SELECT N FROM T WHERE N = 40", 2, true)]
    [InlineData(DefCursorMain, "SELECT DISTINCTROW N FROM T WHERE ID IN (4, 5)", 2, true)]
    // r-fix-cursor-dao/pos-query-sources-rw
    [InlineData(DefCursorMain, "SELECT TOP 3 ID, N FROM T ORDER BY N DESC, ID", 2, true)]
    [InlineData(DefCursorMain, "SELECT TOP 3 ID, N FROM T ORDER BY N DESC, ID", 4, false)]
    [InlineData(DefCursorMain, "SELECT N FROM T", 8, false)]
    // r-q-cursor-dao/prov-heap-second-row-edit (heap table H)
    [InlineData(DefCursorMain, "SELECT A, B FROM H", 2, true)]
    // r-def-cursor-dao/prov-distinct-distinctrow-plain, prov-aggregate-groupby, prov-union-crosstab
    [InlineData(DefCursorMain, "SELECT DISTINCT N FROM T WHERE ID IN (4, 5)", 2, false)]
    [InlineData(DefCursorMain, "SELECT N, Count(*) AS Cn, Sum(ID) AS Si FROM T GROUP BY N", 2, false)]
    [InlineData(DefCursorMain, "SELECT ID, N FROM T WHERE ID <= 3 UNION SELECT ID + 10, N FROM T WHERE ID > 6", 2, false)]
    // r-def-cursor-dao/prov-outer-join-absent-side, r-fix-cursor-dao/pos-query-sources-rw
    [InlineData(DefCursorMain, "SELECT PA.ID AS PAID, CH.ID AS CHID FROM PA LEFT JOIN CH ON PA.ID = CH.PID ORDER BY PA.ID, CH.ID", 2, true)]
    [InlineData(DefCursorMain, "SELECT PA.ID AS PAID, CH.ID AS CHID FROM PA LEFT JOIN CH ON PA.ID = CH.PID ORDER BY PA.ID, CH.ID", 4, false)]
    // r-fix-cursor-dao/open-argument-shapes-rw, r-cursor-1b/option-join-updatability
    [InlineData(Cursor1bMain, "SELECT * FROM F", 2, true)]
    [InlineData(Cursor1bMain, "SELECT P2.ID AS PID, P2.N AS PN, C2.ID AS CID, C2.V AS CV FROM P2 INNER JOIN C2 ON P2.ID = C2.PID ORDER BY C2.ID", 2, true)]
    // r-sql-2-3/dr-updatable (plain and DISTINCT over unkeyed inner join)
    [InlineData(Sql23Sel, "SELECT A.Label, A.Hidden FROM A INNER JOIN B ON A.Label = B.Label", 2, false)]
    [InlineData(Sql23Sel, "SELECT DISTINCT A.Label, A.Hidden FROM A INNER JOIN B ON A.Label = B.Label", 2, false)]
    public void SelectRecordset_Updatable_matches_recorded_shapes_on_writable_open(string fixture, string sql, int type, bool expected)
    {
        DatabaseObject db = Open(fixture, readOnly: false);
        SelectRecordsetObject rs = Recover<SelectRecordsetObject>(Ok(Method(db, "OpenRecordset", sql, type)));
        Assert.Equal(expected, BoolValue(Ok(Get(rs, "Updatable"))));
        Ok(Method(rs, "Close"));
    }

    [Fact]
    public void SelectRecordset_DistinctRow_over_join_refuses_open_until_modelled()
    {
        // r-sql-2-3/dr-updatable decides this dynaset's Updatable as True (BoundSelect.TryUpdatable already returns true),
        // once SelectCursor.Open models DISTINCTROW over a join (currently refused as NotModelled.DistinctRow).
        DatabaseObject db = Open(Sql23Sel, readOnly: false);
        CallResult result = Method(db, "OpenRecordset", "SELECT DISTINCTROW A.Label, A.Hidden FROM A INNER JOIN B ON A.Label = B.Label", 2);
        AssertNotModelled(result, "NotModelled.DistinctRow");
    }

    [Theory]
    // r-cursor-1b/open-argument-shapes, r-def-cursor-dao/pos-query-*, prov-alias-fields-metadata
    [InlineData(Cursor1bMain, "SELECT * FROM F", 2)]
    [InlineData(Cursor1bMain, "SELECT * FROM F WHERE 1 = 0", 4)]
    [InlineData(DefCursorMain, "SELECT TOP 3 ID, N FROM T ORDER BY N DESC, ID", 2)]
    [InlineData(DefCursorMain, "SELECT PA.ID AS PAID, CH.ID AS CHID FROM PA LEFT JOIN CH ON PA.ID = CH.PID ORDER BY PA.ID, CH.ID", 2)]
    [InlineData(DefCursorMain, "SELECT ID, N AS N1, N + 1 AS Np, T.S FROM T WHERE ID <= 3 ORDER BY ID", 2)]
    public void SelectRecordset_Updatable_is_false_on_readonly_database(string fixture, string sql, int type)
    {
        DatabaseObject db = Open(fixture, readOnly: true);
        SelectRecordsetObject rs = Recover<SelectRecordsetObject>(Ok(Method(db, "OpenRecordset", sql, type)));
        Assert.False(BoolValue(Ok(Get(rs, "Updatable"))));
        Ok(Method(rs, "Close"));
    }

    [Theory]
    [InlineData("SELECT Count(*) AS Cn FROM T", "NotModelled.QueryUpdatable.UngroupedAggregate")]
    [InlineData("SELECT 1 + 2 AS V FROM T", "NotModelled.QueryUpdatable.NoBaseColumn")]
    [InlineData("SELECT ID FROM (SELECT ID FROM T) AS D", "NotModelled.QueryUpdatable.DerivedTable")]
    [InlineData("SELECT PA.ID, CH.ID FROM PA RIGHT JOIN CH ON PA.ID = CH.PID", "NotModelled.QueryUpdatable.JoinShape")]
    [InlineData("SELECT PA.ID, CH.ID FROM PA, CH WHERE PA.ID = CH.PID", "NotModelled.QueryUpdatable.Shape")]
    public void SelectRecordset_Updatable_refuses_unrecorded_dynaset_shapes_by_name(string sql, string expectedName)
    {
        DatabaseObject db = Open(DefCursorMain, readOnly: false);
        SelectRecordsetObject rs = Recover<SelectRecordsetObject>(Ok(Method(db, "OpenRecordset", sql, 2)));
        CallResult result = Get(rs, "Updatable");
        AssertNotModelled(result, expectedName);
        Ok(Method(rs, "Close"));
    }

    [Fact]
    public void SelectField_metadata_matches_field_metadata_rw_and_prov_alias_fields_metadata()
    {
        const string sql = "SELECT ID, N AS N1, N AS N2, N + 1 AS Np, S & 'x' AS Sx, T.S FROM T WHERE ID <= 3 ORDER BY ID";

        // Writable open: r-fix-cursor-dao/field-metadata-rw
        DatabaseObject rw = Open(DefCursorMain, readOnly: false);
        SelectRecordsetObject rsRw = Recover<SelectRecordsetObject>(Ok(Method(rw, "OpenRecordset", sql, 2)));
        Assert.True(BoolValue(Ok(Get(rsRw, "Updatable"))));
        SelectFieldsObject rwFields = Object<SelectFieldsObject>(rsRw, "Fields");
        AssertField(rwFields, 0, "ID", dataUpdatable: true, "T", "ID");
        AssertField(rwFields, 1, "N1", dataUpdatable: true, "T", "N");
        AssertField(rwFields, 2, "N2", dataUpdatable: true, "T", "N");
        AssertField(rwFields, 3, "Np", dataUpdatable: false, "T", "N");
        AssertField(rwFields, 4, "Sx", dataUpdatable: false, "T", "S");
        AssertField(rwFields, 5, "S", dataUpdatable: true, "T", "S");
        Ok(Method(rsRw, "Close"));

        // Read-only open: r-def-cursor-dao/prov-alias-fields-metadata
        DatabaseObject ro = Open(DefCursorMain, readOnly: true);
        SelectRecordsetObject rsRo = Recover<SelectRecordsetObject>(Ok(Method(ro, "OpenRecordset", sql, 2)));
        Assert.False(BoolValue(Ok(Get(rsRo, "Updatable"))));
        SelectFieldsObject roFields = Object<SelectFieldsObject>(rsRo, "Fields");
        AssertField(roFields, 0, "ID", dataUpdatable: false, "T", "ID");
        AssertField(roFields, 1, "N1", dataUpdatable: false, "T", "N");
        AssertField(roFields, 2, "N2", dataUpdatable: false, "T", "N");
        AssertField(roFields, 3, "Np", dataUpdatable: false, "T", "N");
        AssertField(roFields, 4, "Sx", dataUpdatable: false, "T", "S");
        AssertField(roFields, 5, "S", dataUpdatable: false, "T", "S");
        Ok(Method(rsRo, "Close"));
    }

    [Fact]
    public void SelectField_metadata_covers_groupby_union_distinct_and_joins()
    {
        DatabaseObject db = Open(DefCursorMain, readOnly: false);

        // r-def-cursor-dao/prov-aggregate-groupby
        SelectRecordsetObject gb = Recover<SelectRecordsetObject>(Ok(Method(db, "OpenRecordset", "SELECT N, Count(*) AS Cn, Sum(ID) AS Si FROM T GROUP BY N", 2)));
        Assert.False(BoolValue(Ok(Get(gb, "Updatable"))));
        SelectFieldsObject gbFields = Object<SelectFieldsObject>(gb, "Fields");
        AssertField(gbFields, 0, "N", dataUpdatable: false, "T", "N");
        AssertField(gbFields, 1, "Cn", dataUpdatable: false, "", "");
        AssertField(gbFields, 2, "Si", dataUpdatable: false, "", "");
        Ok(Method(gb, "Close"));

        // r-def-cursor-dao/prov-union-crosstab
        SelectRecordsetObject un = Recover<SelectRecordsetObject>(Ok(Method(db, "OpenRecordset", "SELECT ID, N FROM T WHERE ID <= 3 UNION SELECT ID + 10, N FROM T WHERE ID > 6", 2)));
        Assert.False(BoolValue(Ok(Get(un, "Updatable"))));
        SelectFieldsObject unFields = Object<SelectFieldsObject>(un, "Fields");
        AssertField(unFields, 0, "ID", dataUpdatable: false, "", "");
        AssertField(unFields, 1, "N", dataUpdatable: false, "", "");
        Ok(Method(un, "Close"));

        // r-def-cursor-dao/prov-distinct-distinctrow-plain
        SelectRecordsetObject dist = Recover<SelectRecordsetObject>(Ok(Method(db, "OpenRecordset", "SELECT DISTINCT N FROM T WHERE ID IN (4, 5)", 2)));
        Assert.False(BoolValue(Ok(Get(dist, "Updatable"))));
        AssertField(Object<SelectFieldsObject>(dist, "Fields"), 0, "N", dataUpdatable: false, "T", "N");
        Ok(Method(dist, "Close"));

        // r-def-cursor-dao/prov-outer-join-absent-side
        SelectRecordsetObject lj = Recover<SelectRecordsetObject>(Ok(Method(db, "OpenRecordset", "SELECT PA.ID AS PAID, PA.V, CH.ID AS CHID, CH.PID, CH.W FROM PA LEFT JOIN CH ON PA.ID = CH.PID ORDER BY PA.ID, CH.ID", 2)));
        Assert.True(BoolValue(Ok(Get(lj, "Updatable"))));
        SelectFieldsObject ljFields = Object<SelectFieldsObject>(lj, "Fields");
        AssertField(ljFields, 0, "PAID", dataUpdatable: true, "PA", "ID");
        AssertField(ljFields, 1, "V", dataUpdatable: true, "PA", "V");
        AssertField(ljFields, 2, "CHID", dataUpdatable: true, "CH", "ID");
        AssertField(ljFields, 3, "PID", dataUpdatable: true, "CH", "PID");
        AssertField(ljFields, 4, "W", dataUpdatable: true, "CH", "W");
        Ok(Method(lj, "Close"));

        // r-cursor-1b/option-join-updatability
        DatabaseObject c1b = Open(Cursor1bMain, readOnly: false);
        SelectRecordsetObject ij = Recover<SelectRecordsetObject>(Ok(Method(c1b, "OpenRecordset", "SELECT P2.ID AS PID, P2.N AS PN, C2.ID AS CID, C2.V AS CV FROM P2 INNER JOIN C2 ON P2.ID = C2.PID ORDER BY C2.ID", 2)));
        Assert.True(BoolValue(Ok(Get(ij, "Updatable"))));
        SelectFieldsObject ijFields = Object<SelectFieldsObject>(ij, "Fields");
        AssertField(ijFields, 0, "PID", dataUpdatable: true, "P2", "ID");
        AssertField(ijFields, 1, "PN", dataUpdatable: true, "P2", "N");
        AssertField(ijFields, 2, "CID", dataUpdatable: true, "C2", "ID");
        AssertField(ijFields, 3, "CV", dataUpdatable: true, "C2", "V");
        Ok(Method(ij, "Close"));
    }

    [Fact]
    public void SelectField_DataUpdatable_and_source_refuse_unrecorded_cases_by_name()
    {
        DatabaseObject db = Open(DefCursorMain, readOnly: false);

        // Base column on a writable snapshot: DataUpdatable is not recorded (NotModelled.FieldDataUpdatable.ReadOnlyCursor),
        // while an expression or literal column on the same snapshot is False (r-fix-cursor-dao/field-metadata-rw).
        SelectRecordsetObject snap = Recover<SelectRecordsetObject>(Ok(Method(db, "OpenRecordset", "SELECT ID, N + 1 AS A1, 7 AS A12 FROM T WHERE ID = 1", 4)));
        Assert.False(BoolValue(Ok(Get(snap, "Updatable"))));
        SelectFieldsObject snapFields = Object<SelectFieldsObject>(snap, "Fields");
        SelectFieldObject snapId = Object<SelectFieldObject>(snapFields, "Item", 0);
        AssertNotModelled(Get(snapId, "DataUpdatable"), "NotModelled.FieldDataUpdatable.ReadOnlyCursor");
        AssertField(snapFields, 1, "A1", dataUpdatable: false, "T", "N");
        AssertField(snapFields, 2, "A12", dataUpdatable: false, "", "");
        Ok(Method(snap, "Close"));

        // Multi-column expression: SourceTable/SourceField not identified (NotModelled.FieldSource); DataUpdatable is False.
        SelectRecordsetObject multi = Recover<SelectRecordsetObject>(Ok(Method(db, "OpenRecordset", "SELECT ID, ID + N AS SumTwo FROM T", 2)));
        SelectFieldObject sumTwo = Object<SelectFieldObject>(Object<SelectFieldsObject>(multi, "Fields"), "Item", 1);
        Assert.False(BoolValue(Ok(Get(sumTwo, "DataUpdatable"))));
        AssertNotModelled(Get(sumTwo, "SourceTable"), "NotModelled.FieldSource");
        AssertNotModelled(Get(sumTwo, "SourceField"), "NotModelled.FieldSource");
        Ok(Method(multi, "Close"));

        // Derived UNION column: DataUpdatable refused by name (NotModelled.FieldDataUpdatable.ColumnOrigin).
        SelectRecordsetObject derived = Recover<SelectRecordsetObject>(Ok(Method(db, "OpenRecordset", "SELECT ID FROM (SELECT ID FROM T UNION ALL SELECT ID FROM PA) AS U", 2)));
        SelectFieldObject derivedId = Object<SelectFieldObject>(Object<SelectFieldsObject>(derived, "Fields"), "Item", 0);
        AssertNotModelled(Get(derivedId, "DataUpdatable"), "NotModelled.FieldDataUpdatable.ColumnOrigin");
        Assert.Equal("", TextValue(Ok(Get(derivedId, "SourceTable"))));
        Assert.Equal("", TextValue(Ok(Get(derivedId, "SourceField"))));
        Ok(Method(derived, "Close"));

        // AutoNumber base column on a writable open: refused by name (NotModelled.FieldDataUpdatable.AutoNumber),
        // while a read-only open reports False.
        DatabaseObject richRw = Open(Dao1bRich, readOnly: false);
        SelectRecordsetObject autoRw = Recover<SelectRecordsetObject>(Ok(Method(richRw, "OpenRecordset", "SELECT K, TLong FROM Typed", 2)));
        SelectFieldsObject autoRwFields = Object<SelectFieldsObject>(autoRw, "Fields");
        AssertNotModelled(Get(Object<SelectFieldObject>(autoRwFields, "Item", 0), "DataUpdatable"), "NotModelled.FieldDataUpdatable.AutoNumber");
        Assert.True(BoolValue(Ok(Get(Object<SelectFieldObject>(autoRwFields, "Item", 1), "DataUpdatable"))));
        Ok(Method(autoRw, "Close"));

        DatabaseObject richRo = Open(Dao1bRich, readOnly: true);
        SelectRecordsetObject autoRo = Recover<SelectRecordsetObject>(Ok(Method(richRo, "OpenRecordset", "SELECT K, TLong FROM Typed", 2)));
        Assert.False(BoolValue(Ok(Get(Object<SelectFieldObject>(Object<SelectFieldsObject>(autoRo, "Fields"), "Item", 0), "DataUpdatable"))));
        Ok(Method(autoRo, "Close"));
    }

    [Fact]
    public void Closed_SelectRecordset_and_Field_metadata_fail_3420()
    {
        DatabaseObject db = Open(DefCursorMain, readOnly: false);
        SelectRecordsetObject rs = Recover<SelectRecordsetObject>(Ok(Method(db, "OpenRecordset", "SELECT ID, N FROM T", 2)));
        SelectFieldObject field = Object<SelectFieldObject>(Object<SelectFieldsObject>(rs, "Fields"), "Item", 0);
        Ok(Method(rs, "Close"));

        AssertDaoError(Get(rs, "Updatable"), 3420, "DAO.Recordset");
        AssertDaoError(Get(field, "DataUpdatable"), 3420, "DAO.Field");
        AssertDaoError(Get(field, "SourceTable"), 3420, "DAO.Field");
        AssertDaoError(Get(field, "SourceField"), 3420, "DAO.Field");
    }

    private void AssertField(SelectFieldsObject fields, int ordinal, string name, bool dataUpdatable, string sourceTable, string sourceField)
    {
        SelectFieldObject field = Object<SelectFieldObject>(fields, "Item", ordinal);
        Assert.Equal(name, TextValue(Ok(Get(field, "Name"))));
        Assert.Equal(dataUpdatable, BoolValue(Ok(Get(field, "DataUpdatable"))));
        Assert.Equal(sourceTable, TextValue(Ok(Get(field, "SourceTable"))));
        Assert.Equal(sourceField, TextValue(Ok(Get(field, "SourceField"))));
    }

    private static void AssertNotModelled(CallResult result, string expectedName)
    {
        Assert.NotNull(result.Failure);
        Assert.Equal(FailureKind.NotModelled, result.Failure!.Kind);
        Assert.StartsWith(expectedName + ":", result.Failure.Description);
    }

    private static void AssertDaoError(CallResult result, int number, string source)
    {
        Assert.NotNull(result.Failure);
        Assert.Equal(FailureKind.Dao, result.Failure!.Kind);
        Assert.Equal(number, result.Failure.Number);
        Assert.Equal(source, result.Failure.Source);
    }

    private DatabaseObject Open(string fixture, bool readOnly)
    {
        string path = Path.Combine(Path.GetTempPath(), $"calcdb-dao-select-meta-{Guid.NewGuid():N}.accdb");
        File.WriteAllBytes(path, CalcDb.Evidence.ArtifactStore.ForRepository(DeclarationGenerationTests.Root()).ReadAll(fixture));
        _files.Add(path);
        return Recover<DatabaseObject>(Ok(_engine.DefaultWorkspace.OpenDatabase([Arg(path), Arg(false), Arg(readOnly)])));
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
        Assert.True(result.Failure is null, result.Failure?.Description);
        return result;
    }

    private static bool BoolValue(CallResult result)
    {
        Assert.True(result.HasScalar && (VarType)result.Scalar.Vt == VarType.Bool);
        return result.Scalar.Int64 != 0;
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
        _held.Add(result.Scalar.Pointer);
        return ComObject.Recover<T>(result.Scalar.Pointer);
    }

    private T Object<T>(DispatchObject target, string member, params object?[] args) where T : ComObject => Recover<T>(Ok(Get(target, member, args)));
}
