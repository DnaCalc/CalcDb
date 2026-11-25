using System.Runtime.InteropServices;

using CalcDb.Com.Com;
using CalcDb.Com.Dao;
using CalcDb.Com.Declarations;
using CalcDb.Com.Host;
using CalcDb.Foundation;
using CalcDb.Values;

namespace CalcDb.Com.Tests;

/// <summary>
/// Verifies DAO query-recordset metadata read from <see cref="CalcDb.Sql.SelectCursor"/>,
/// <see cref="CalcDb.Sql.ResultColumn"/> and the catalog (register row 3rwk; R-CURSOR-1 CUR-ID, CUR-PROV,
/// R-DAO-1 DAO-PROP, R-SQL-3 SQL-DR-01..05): <see cref="SelectRecordsetObject"/> <c>Updatable</c> and
/// <c>Bookmarkable</c>, and <see cref="SelectFieldObject"/> <c>DataUpdatable</c>, <c>SourceTable</c>,
/// <c>SourceField</c>, <c>Attributes</c>, <c>Required</c>, and <c>Properties</c>, plus named refusals where no
/// retained record decides a case.
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

    [Theory]
    // r-cursor-1b/open-argument-shapes, r-fix-cursor-dao/open-argument-shapes-rw
    [InlineData(Cursor1bMain, "SELECT * FROM F", 2, true, true)]
    [InlineData(Cursor1bMain, "SELECT * FROM F WHERE 1 = 0", 4, true, true)]
    [InlineData(Cursor1bMain, "SELECT * FROM F", 2, false, true)]
    [InlineData(Cursor1bMain, "SELECT * FROM F WHERE 1 = 0", 4, false, true)]
    // r-def-cursor-dao/pos-query-distinct-groupby, pos-query-left-join, pos-query-top, pos-query-union
    [InlineData(DefCursorMain, "SELECT DISTINCT N FROM T", 2, true, true)]
    [InlineData(DefCursorMain, "SELECT N, Count(*) AS Cn FROM T GROUP BY N", 4, true, true)]
    // r-fix-cursor-dao/pos-query-sources-rw
    [InlineData(DefCursorMain, "SELECT TOP 3 ID, N FROM T ORDER BY N DESC, ID", 2, false, true)]
    [InlineData(DefCursorMain, "SELECT PA.ID AS PAID, CH.ID AS CHID FROM PA LEFT JOIN CH ON PA.ID = CH.PID ORDER BY PA.ID, CH.ID", 4, false, true)]
    [InlineData(DefCursorMain, "SELECT ID, N FROM T WHERE ID <= 3 UNION SELECT ID + 10, N FROM T WHERE ID > 6", 2, false, true)]
    // r-def-cursor-dao/open-dynamic-and-option-matrix, CUR-ID
    [InlineData(DefCursorMain, "SELECT ID, N FROM T ORDER BY ID", 8, false, false)]
    public void SelectRecordset_Bookmarkable_matches_recorded_shapes(string fixture, string sql, int type, bool readOnly, bool expected)
    {
        DatabaseObject db = Open(fixture, readOnly);
        SelectRecordsetObject rs = Recover<SelectRecordsetObject>(Ok(Method(db, "OpenRecordset", sql, type)));
        Assert.Equal(expected, BoolValue(Ok(Get(rs, "Bookmarkable"))));
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
        AssertField(rwFields, 0, "ID", attributes: 33, dataUpdatable: true, "T", "ID", required: false);
        AssertField(rwFields, 1, "N1", attributes: 33, dataUpdatable: true, "T", "N", required: false);
        AssertField(rwFields, 2, "N2", attributes: 33, dataUpdatable: true, "T", "N", required: false);
        AssertField(rwFields, 3, "Np", attributes: 1, dataUpdatable: false, "T", "N", required: false);
        Assert.Equal(4, Int32Value(Ok(Get(Object<SelectFieldObject>(rwFields, "Item", 3), "Size"))));
        AssertField(rwFields, 4, "Sx", attributes: 2, dataUpdatable: false, "T", "S", required: false);
        Assert.Equal(0, Int32Value(Ok(Get(Object<SelectFieldObject>(rwFields, "Item", 4), "Size"))));
        AssertField(rwFields, 5, "S", attributes: 34, dataUpdatable: true, "T", "S", required: false);
        Ok(Method(rsRw, "Close"));

        // Read-only open: r-def-cursor-dao/prov-alias-fields-metadata
        DatabaseObject ro = Open(DefCursorMain, readOnly: true);
        SelectRecordsetObject rsRo = Recover<SelectRecordsetObject>(Ok(Method(ro, "OpenRecordset", sql, 2)));
        Assert.False(BoolValue(Ok(Get(rsRo, "Updatable"))));
        SelectFieldsObject roFields = Object<SelectFieldsObject>(rsRo, "Fields");
        AssertField(roFields, 0, "ID", attributes: 1, dataUpdatable: false, "T", "ID", required: false);
        AssertField(roFields, 1, "N1", attributes: 1, dataUpdatable: false, "T", "N", required: false);
        AssertField(roFields, 2, "N2", attributes: 1, dataUpdatable: false, "T", "N", required: false);
        AssertField(roFields, 3, "Np", attributes: 1, dataUpdatable: false, "T", "N", required: false);
        Assert.Equal(4, Int32Value(Ok(Get(Object<SelectFieldObject>(roFields, "Item", 3), "Size"))));
        AssertField(roFields, 4, "Sx", attributes: 2, dataUpdatable: false, "T", "S", required: false);
        Assert.Equal(0, Int32Value(Ok(Get(Object<SelectFieldObject>(roFields, "Item", 4), "Size"))));
        AssertField(roFields, 5, "S", attributes: 2, dataUpdatable: false, "T", "S", required: false);
        Ok(Method(rsRo, "Close"));
    }

    [Fact]
    public void SelectField_metadata_covers_groupby_union_distinct_and_joins()
    {
        DatabaseObject db = Open(DefCursorMain, readOnly: false);

        // r-def-cursor-dao/prov-aggregate-groupby, r-fix-cursor-dao/prov-sources-errors-per-call
        SelectRecordsetObject gb = Recover<SelectRecordsetObject>(Ok(Method(db, "OpenRecordset", "SELECT N, Count(*) AS Cn, Sum(ID) AS Si FROM T GROUP BY N", 2)));
        Assert.False(BoolValue(Ok(Get(gb, "Updatable"))));
        SelectFieldsObject gbFields = Object<SelectFieldsObject>(gb, "Fields");
        AssertField(gbFields, 0, "N", attributes: 1, dataUpdatable: false, "T", "N", required: false);
        AssertField(gbFields, 1, "Cn", attributes: 1, dataUpdatable: false, "", "", required: false);
        AssertField(gbFields, 2, "Si", attributes: 1, dataUpdatable: false, "", "", required: false);
        Ok(Method(gb, "Close"));

        // r-def-cursor-dao/prov-union-crosstab, r-fix-cursor-dao/prov-sources-errors-per-call
        SelectRecordsetObject un = Recover<SelectRecordsetObject>(Ok(Method(db, "OpenRecordset", "SELECT ID, N FROM T WHERE ID <= 3 UNION SELECT ID + 10, N FROM T WHERE ID > 6", 2)));
        Assert.False(BoolValue(Ok(Get(un, "Updatable"))));
        SelectFieldsObject unFields = Object<SelectFieldsObject>(un, "Fields");
        AssertField(unFields, 0, "ID", attributes: 1, dataUpdatable: false, "", "", required: false);
        AssertField(unFields, 1, "N", attributes: 1, dataUpdatable: false, "", "", required: false);
        Ok(Method(un, "Close"));

        // r-def-cursor-dao/prov-distinct-distinctrow-plain, r-fix-cursor-dao/prov-sources-errors-per-call
        SelectRecordsetObject dist = Recover<SelectRecordsetObject>(Ok(Method(db, "OpenRecordset", "SELECT DISTINCT N FROM T WHERE ID IN (4, 5)", 2)));
        Assert.False(BoolValue(Ok(Get(dist, "Updatable"))));
        AssertField(Object<SelectFieldsObject>(dist, "Fields"), 0, "N", attributes: 1, dataUpdatable: false, "T", "N", required: false);
        Ok(Method(dist, "Close"));

        SelectRecordsetObject distRow = Recover<SelectRecordsetObject>(Ok(Method(db, "OpenRecordset", "SELECT DISTINCTROW N FROM T WHERE ID IN (4, 5)", 2)));
        Assert.True(BoolValue(Ok(Get(distRow, "Updatable"))));
        AssertField(Object<SelectFieldsObject>(distRow, "Fields"), 0, "N", attributes: 33, dataUpdatable: true, "T", "N", required: false);
        Ok(Method(distRow, "Close"));

        // r-def-cursor-dao/prov-outer-join-absent-side
        SelectRecordsetObject lj = Recover<SelectRecordsetObject>(Ok(Method(db, "OpenRecordset", "SELECT PA.ID AS PAID, PA.V, CH.ID AS CHID, CH.PID, CH.W FROM PA LEFT JOIN CH ON PA.ID = CH.PID ORDER BY PA.ID, CH.ID", 2)));
        Assert.True(BoolValue(Ok(Get(lj, "Updatable"))));
        SelectFieldsObject ljFields = Object<SelectFieldsObject>(lj, "Fields");
        AssertField(ljFields, 0, "PAID", attributes: 33, dataUpdatable: true, "PA", "ID", required: false);
        AssertField(ljFields, 1, "V", attributes: 34, dataUpdatable: true, "PA", "V", required: false);
        AssertField(ljFields, 2, "CHID", attributes: 33, dataUpdatable: true, "CH", "ID", required: false);
        AssertField(ljFields, 3, "PID", attributes: 33, dataUpdatable: true, "CH", "PID", required: false);
        AssertField(ljFields, 4, "W", attributes: 34, dataUpdatable: true, "CH", "W", required: false);
        Ok(Method(lj, "Close"));

        // r-cursor-1b/option-join-updatability
        DatabaseObject c1b = Open(Cursor1bMain, readOnly: false);
        SelectRecordsetObject ij = Recover<SelectRecordsetObject>(Ok(Method(c1b, "OpenRecordset", "SELECT P2.ID AS PID, P2.N AS PN, C2.ID AS CID, C2.V AS CV FROM P2 INNER JOIN C2 ON P2.ID = C2.PID ORDER BY C2.ID", 2)));
        Assert.True(BoolValue(Ok(Get(ij, "Updatable"))));
        SelectFieldsObject ijFields = Object<SelectFieldsObject>(ij, "Fields");
        AssertField(ijFields, 0, "PID", attributes: 33, dataUpdatable: true, "P2", "ID", required: false);
        AssertField(ijFields, 1, "PN", attributes: 33, dataUpdatable: true, "P2", "N", required: false);
        AssertField(ijFields, 2, "CID", attributes: 33, dataUpdatable: true, "C2", "ID", required: false);
        AssertField(ijFields, 3, "CV", attributes: 34, dataUpdatable: true, "C2", "V", required: false);
        Ok(Method(ij, "Close"));
    }

    [Fact]
    public void SelectField_Attributes_and_Required_match_recorded_aggregates_and_expressions()
    {
        foreach (bool readOnly in (bool[])[true, false])
        {
            DatabaseObject db = Open(DefCursorMain, readOnly);

            // r-def-cursor-dao/query-field-metadata-aggregates, r-fix-cursor-dao/field-metadata-rw
            SelectRecordsetObject agg = Recover<SelectRecordsetObject>(Ok(Method(db, "OpenRecordset",
                "SELECT Count(*) AS Cn, Count(S) AS Cs, Sum(N) AS Sn, Avg(N) AS An, Min(S) AS Ms, Max(D) AS Md, Min(Fl) AS Mf FROM T", 4)));
            SelectFieldsObject aggFields = Object<SelectFieldsObject>(agg, "Fields");
            int[] expectedAggAttr = [1, 1, 1, 1, 2, 1, 1];
            for (int i = 0; i < expectedAggAttr.Length; i++)
            {
                SelectFieldObject f = Object<SelectFieldObject>(aggFields, "Item", i);
                Assert.Equal(expectedAggAttr[i], Int32Value(Ok(Get(f, "Attributes"))));
                Assert.False(BoolValue(Ok(Get(f, "Required"))));
            }
            Ok(Method(agg, "Close"));

            // r-def-cursor-dao/query-field-metadata-expressions, r-fix-cursor-dao/field-metadata-rw,
            // r-expr-1b/meta-conversion-field-types (every recorded expression DAO type: 3, 4, 5, 7, 8, 9, 10, 20)
            SelectRecordsetObject expr = Recover<SelectRecordsetObject>(Ok(Method(db, "OpenRecordset",
                "SELECT CInt(N) AS E3, N + 1 AS E4, CCur(1) + 1 AS E5, CDbl(N) AS E7, CDate(1) AS E8, Null AS E9, S & 'x' AS E10, 1 + 0.5 AS E20 FROM T WHERE ID = 1", 4)));
            SelectFieldsObject exprFields = Object<SelectFieldsObject>(expr, "Fields");
            (short Type, int Size, int Attr)[] expectedExpr =
            [
                (3, 2, 1),
                (4, 4, 1),
                (5, 8, 1),
                (7, 8, 1),
                (8, 8, 1),
                (9, 0, 1),
                (10, 0, 2),
                (20, 16, 1),
            ];
            for (int i = 0; i < expectedExpr.Length; i++)
            {
                SelectFieldObject f = Object<SelectFieldObject>(exprFields, "Item", i);
                Assert.Equal(expectedExpr[i].Type, Int16Value(Ok(Get(f, "Type"))));
                Assert.Equal(expectedExpr[i].Size, Int32Value(Ok(Get(f, "Size"))));
                Assert.Equal(expectedExpr[i].Attr, Int32Value(Ok(Get(f, "Attributes"))));
                Assert.False(BoolValue(Ok(Get(f, "Required"))));
            }
            Ok(Method(expr, "Close"));
        }

        // r-def-expr-sql/str-memo-versus-text-expressions (Memo base column -> Attributes 2)
        DatabaseObject ro = Open(DefCursorMain, readOnly: true);
        SelectRecordsetObject memo = Recover<SelectRecordsetObject>(Ok(Method(ro, "OpenRecordset", "SELECT Mm FROM T WHERE ID = 1", 4)));
        SelectFieldObject mm = Object<SelectFieldObject>(Object<SelectFieldsObject>(memo, "Fields"), "Item", 0);
        Assert.Equal(2, Int32Value(Ok(Get(mm, "Attributes"))));
        Assert.False(BoolValue(Ok(Get(mm, "Required"))));
        Ok(Method(memo, "Close"));
    }

    [Fact]
    public void SelectField_Properties_matches_recordset_field_properties_and_field_metadata_rw()
    {
        // Both r-def-cursor-dao/recordset-field-properties (read-only) and r-fix-cursor-dao/field-metadata-rw (writable)
        // enumerate the 24 built-in properties on single-table Long N, join Text PA.V, and expression Long N + 1 AS Np.
        foreach (bool readOnly in (bool[])[true, false])
        {
            DatabaseObject db = Open(DefCursorMain, readOnly);

            AssertEnumeratedProperties(
                db, "SELECT ID, N FROM T WHERE ID = 3", "N",
                fieldType: 4, expectedValue: 30, expectedAttributes: readOnly ? 1 : 33, expectedOrdinal: 1,
                expectedSize: 4, expectedSourceTable: "T", expectedSourceField: "N", expectedDataUpdatable: !readOnly);

            AssertEnumeratedProperties(
                db, "SELECT PA.ID, PA.V FROM PA INNER JOIN CH ON PA.ID = CH.PID", "V",
                fieldType: 10, expectedValue: "p1", expectedAttributes: readOnly ? 2 : 34, expectedOrdinal: 1,
                expectedSize: 10, expectedSourceTable: "PA", expectedSourceField: "V", expectedDataUpdatable: !readOnly);

            AssertEnumeratedProperties(
                db, "SELECT N + 1 AS Np FROM T WHERE ID = 3", "Np",
                fieldType: 4, expectedValue: 31, expectedAttributes: 1, expectedOrdinal: 0,
                expectedSize: 4, expectedSourceTable: "T", expectedSourceField: "N", expectedDataUpdatable: false);
        }
    }

    [Fact]
    public void SelectField_DataUpdatable_Attributes_Required_Properties_and_source_refuse_unrecorded_cases_by_name()
    {
        DatabaseObject db = Open(DefCursorMain, readOnly: false);

        // Base column on a writable snapshot: DataUpdatable and Attributes are not recorded
        // (NotModelled.FieldDataUpdatable.ReadOnlyCursor), Properties refuses snapshot (NotModelled.FieldProperties.CursorType);
        // typed expressions on the same snapshot report DataUpdatable False and Attributes 1 (r-fix-cursor-dao/field-metadata-rw),
        // while a bare literal whose static type Sql leaves open refuses Size and Attributes with NotModelled.ExpressionFieldType.
        SelectRecordsetObject snap = Recover<SelectRecordsetObject>(Ok(Method(db, "OpenRecordset", "SELECT ID, N + 1 AS A1, CInt(1) AS A5, 7 AS Lit FROM T WHERE ID = 1", 4)));
        Assert.False(BoolValue(Ok(Get(snap, "Updatable"))));
        SelectFieldsObject snapFields = Object<SelectFieldsObject>(snap, "Fields");
        SelectFieldObject snapId = Object<SelectFieldObject>(snapFields, "Item", 0);
        AssertNotModelled(Get(snapId, "DataUpdatable"), "NotModelled.FieldDataUpdatable.ReadOnlyCursor");
        AssertNotModelled(Get(snapId, "Attributes"), "NotModelled.FieldDataUpdatable.ReadOnlyCursor");
        AssertNotModelled(Get(snapId, "Properties"), "NotModelled.FieldProperties.CursorType");
        AssertField(snapFields, 1, "A1", attributes: 1, dataUpdatable: false, "T", "N", required: false);
        AssertField(snapFields, 2, "A5", attributes: 1, dataUpdatable: false, "", "", required: false);
        SelectFieldObject lit = Object<SelectFieldObject>(snapFields, "Item", 3);
        Assert.False(BoolValue(Ok(Get(lit, "DataUpdatable"))));
        Assert.False(BoolValue(Ok(Get(lit, "Required"))));
        AssertNotModelled(Get(lit, "Size"), "NotModelled.ExpressionFieldType");
        AssertNotModelled(Get(lit, "Attributes"), "NotModelled.ExpressionFieldType");
        Ok(Method(snap, "Close"));

        // Multi-column expression: SourceTable/SourceField not identified (NotModelled.FieldSource), Properties refused;
        // DataUpdatable False, Attributes 1, Required False.
        SelectRecordsetObject multi = Recover<SelectRecordsetObject>(Ok(Method(db, "OpenRecordset", "SELECT ID, ID + N AS SumTwo FROM T", 2)));
        SelectFieldObject sumTwo = Object<SelectFieldObject>(Object<SelectFieldsObject>(multi, "Fields"), "Item", 1);
        Assert.False(BoolValue(Ok(Get(sumTwo, "DataUpdatable"))));
        Assert.Equal(1, Int32Value(Ok(Get(sumTwo, "Attributes"))));
        Assert.False(BoolValue(Ok(Get(sumTwo, "Required"))));
        AssertNotModelled(Get(sumTwo, "SourceTable"), "NotModelled.FieldSource");
        AssertNotModelled(Get(sumTwo, "SourceField"), "NotModelled.FieldSource");
        AssertNotModelled(Get(sumTwo, "Properties"), "NotModelled.FieldProperties.ColumnOrigin");
        Ok(Method(multi, "Close"));

        // Derived UNION column: DataUpdatable, Attributes, Required, and Properties refused by name.
        SelectRecordsetObject derived = Recover<SelectRecordsetObject>(Ok(Method(db, "OpenRecordset", "SELECT ID FROM (SELECT ID FROM T UNION ALL SELECT ID FROM PA) AS U", 2)));
        SelectFieldObject derivedId = Object<SelectFieldObject>(Object<SelectFieldsObject>(derived, "Fields"), "Item", 0);
        AssertNotModelled(Get(derivedId, "DataUpdatable"), "NotModelled.FieldDataUpdatable.ColumnOrigin");
        AssertNotModelled(Get(derivedId, "Attributes"), "NotModelled.FieldAttributes.ColumnOrigin");
        AssertNotModelled(Get(derivedId, "Required"), "NotModelled.FieldRequired.ColumnOrigin");
        AssertNotModelled(Get(derivedId, "Properties"), "NotModelled.FieldProperties.ColumnOrigin");
        Assert.Equal("", TextValue(Ok(Get(derivedId, "SourceTable"))));
        Assert.Equal("", TextValue(Ok(Get(derivedId, "SourceField"))));
        Ok(Method(derived, "Close"));

        // Union of Text columns: Attributes refused by name (only Long 4 is recorded for Union columns),
        // Properties refused by name (Union origin not enumerated).
        SelectRecordsetObject textUnion = Recover<SelectRecordsetObject>(Ok(Method(db, "OpenRecordset", "SELECT S FROM T UNION SELECT V FROM PA", 2)));
        SelectFieldObject unionS = Object<SelectFieldObject>(Object<SelectFieldsObject>(textUnion, "Fields"), "Item", 0);
        AssertNotModelled(Get(unionS, "Attributes"), "NotModelled.FieldAttributes.UnionType");
        AssertNotModelled(Get(unionS, "Properties"), "NotModelled.FieldProperties.ColumnOrigin");
        Assert.False(BoolValue(Ok(Get(unionS, "Required"))));
        Ok(Method(textUnion, "Close"));

        // Aggregate and GroupKey: Properties refused by name (not enumerated by any retained record).
        SelectRecordsetObject gb = Recover<SelectRecordsetObject>(Ok(Method(db, "OpenRecordset", "SELECT N, Count(*) AS Cn FROM T GROUP BY N", 2)));
        SelectFieldsObject gbFields = Object<SelectFieldsObject>(gb, "Fields");
        AssertNotModelled(Get(Object<SelectFieldObject>(gbFields, "Item", 0), "Properties"), "NotModelled.FieldProperties.ColumnOrigin");
        AssertNotModelled(Get(Object<SelectFieldObject>(gbFields, "Item", 1), "Properties"), "NotModelled.FieldProperties.ColumnOrigin");
        Ok(Method(gb, "Close"));

        // Base column with Required = True (R.Rq NOT NULL) and stored ValidationRule (R.Vv):
        // Required refuses RequiredBaseColumn; Properties refuses StoredColumnProperties.
        SelectRecordsetObject reqRs = Recover<SelectRecordsetObject>(Ok(Method(db, "OpenRecordset", "SELECT Rq, Vv FROM R", 2)));
        SelectFieldsObject reqFields = Object<SelectFieldsObject>(reqRs, "Fields");
        SelectFieldObject rqField = Object<SelectFieldObject>(reqFields, "Item", 0);
        SelectFieldObject vvField = Object<SelectFieldObject>(reqFields, "Item", 1);
        AssertNotModelled(Get(rqField, "Required"), "NotModelled.FieldRequired.RequiredBaseColumn");
        AssertNotModelled(Get(vvField, "Properties"), "NotModelled.FieldProperties.StoredColumnProperties");
        Ok(Method(reqRs, "Close"));

        // Properties value refusals on unrecorded field types (DateTime Precision, Memo AppendOnly).
        SelectRecordsetObject typesRs = Recover<SelectRecordsetObject>(Ok(Method(db, "OpenRecordset", "SELECT D, Mm FROM T WHERE ID = 1", 2)));
        SelectFieldsObject typesFields = Object<SelectFieldsObject>(typesRs, "Fields");
        PropertiesObject dProps = Object<PropertiesObject>(Object<SelectFieldObject>(typesFields, "Item", 0), "Properties");
        AssertNotModelled(Get(Object<PropertyObject>(dProps, "Item", "Precision"), "Value"), "NotModelled.FieldProperties.Precision");
        PropertiesObject mmProps = Object<PropertiesObject>(Object<SelectFieldObject>(typesFields, "Item", 1), "Properties");
        AssertNotModelled(Get(Object<PropertyObject>(mmProps, "Item", "AppendOnly"), "Value"), "NotModelled.FieldProperties.AppendOnly");
        Ok(Method(typesRs, "Close"));

        // AutoNumber base column on a writable open: DataUpdatable and Attributes refused by name
        // (NotModelled.FieldDataUpdatable.AutoNumber), while a read-only open reports False and 17.
        DatabaseObject richRw = Open(Dao1bRich, readOnly: false);
        SelectRecordsetObject autoRw = Recover<SelectRecordsetObject>(Ok(Method(richRw, "OpenRecordset", "SELECT K, TLong FROM Typed", 2)));
        SelectFieldsObject autoRwFields = Object<SelectFieldsObject>(autoRw, "Fields");
        SelectFieldObject autoK = Object<SelectFieldObject>(autoRwFields, "Item", 0);
        AssertNotModelled(Get(autoK, "DataUpdatable"), "NotModelled.FieldDataUpdatable.AutoNumber");
        AssertNotModelled(Get(autoK, "Attributes"), "NotModelled.FieldDataUpdatable.AutoNumber");
        Assert.True(BoolValue(Ok(Get(Object<SelectFieldObject>(autoRwFields, "Item", 1), "DataUpdatable"))));
        Assert.Equal(33, Int32Value(Ok(Get(Object<SelectFieldObject>(autoRwFields, "Item", 1), "Attributes"))));
        Ok(Method(autoRw, "Close"));

        // Stored user property (Description on Parent.Name in r-dao-1b-rich): Properties refused by name.
        SelectRecordsetObject descRs = Recover<SelectRecordsetObject>(Ok(Method(richRw, "OpenRecordset", "SELECT Name FROM Parent", 2)));
        AssertNotModelled(Get(Object<SelectFieldObject>(Object<SelectFieldsObject>(descRs, "Fields"), "Item", 0), "Properties"), "NotModelled.FieldProperties.StoredColumnProperties");
        Ok(Method(descRs, "Close"));

        DatabaseObject richRo = Open(Dao1bRich, readOnly: true);
        SelectRecordsetObject autoRo = Recover<SelectRecordsetObject>(Ok(Method(richRo, "OpenRecordset", "SELECT K, TLong FROM Typed", 2)));
        SelectFieldObject autoRoK = Object<SelectFieldObject>(Object<SelectFieldsObject>(autoRo, "Fields"), "Item", 0);
        Assert.False(BoolValue(Ok(Get(autoRoK, "DataUpdatable"))));
        Assert.Equal(17, Int32Value(Ok(Get(autoRoK, "Attributes"))));
        Ok(Method(autoRo, "Close"));
    }

    [Fact]
    public void Closed_SelectRecordset_and_Field_metadata_fail_3420()
    {
        DatabaseObject db = Open(DefCursorMain, readOnly: false);
        SelectRecordsetObject rs = Recover<SelectRecordsetObject>(Ok(Method(db, "OpenRecordset", "SELECT ID, N FROM T WHERE ID = 3", 2)));
        SelectFieldObject field = Object<SelectFieldObject>(Object<SelectFieldsObject>(rs, "Fields"), "Item", 1);
        PropertiesObject props = Object<PropertiesObject>(field, "Properties");
        PropertyObject nameProp = Object<PropertyObject>(props, "Item", "Name");
        Ok(Method(rs, "Close"));

        AssertDaoError(Get(rs, "Updatable"), 3420, "DAO.Recordset");
        AssertDaoError(Get(rs, "Bookmarkable"), 3420, "DAO.Recordset");
        AssertDaoError(Get(field, "Attributes"), 3420, "DAO.Field");
        AssertDaoError(Get(field, "DataUpdatable"), 3420, "DAO.Field");
        AssertDaoError(Get(field, "Required"), 3420, "DAO.Field");
        AssertDaoError(Get(field, "SourceTable"), 3420, "DAO.Field");
        AssertDaoError(Get(field, "SourceField"), 3420, "DAO.Field");
        AssertDaoError(Get(field, "Properties"), 3420, "DAO.Field");
        AssertDaoError(Get(props, "Count"), 3420, "DAO.Properties");
        AssertDaoError(Get(nameProp, "Value"), 3420, "DAO.Property");
    }

    [Fact]
    public void SelectRecordset_edit_methods_remain_refused()
    {
        DatabaseObject db = Open(DefCursorMain, readOnly: false);
        SelectRecordsetObject rs = Recover<SelectRecordsetObject>(Ok(Method(db, "OpenRecordset", "SELECT ID, N FROM T WHERE ID = 3", 2)));
        // Updatable single-table SELECT dynaset admits Edit / CancelUpdate in EditMode 1
        // (evidence/r-q42-field-rule-and/rule-and2-ne-accdb rows 395-401, 415-422, 436-443;
        // evidence/r-q42-field-rule-and/rule-and2-ne-mdb rows 395-401, 415-422, 436-443);
        // Update without Edit fails DAO 3020 from DAO.Recordset without IErrorInfo
        // (evidence/r-err-1b/asg-update-without-edit rows 5, 10, 32);
        // CancelUpdate without Edit fails DAO 3020 from DAO.Recordset without IErrorInfo
        // (evidence/r-def-cursor-dao/prov-distinct-distinctrow-plain rows 115, 133-137, 148);
        // AddNew and Delete stay refused.
        Ok(Method(rs, "Edit"));
        Ok(Method(rs, "CancelUpdate"));
        AssertDaoError(Method(rs, "Update"), 3020, "DAO.Recordset");
        AssertDaoError(Method(rs, "CancelUpdate"), 3020, "DAO.Recordset");
        foreach (string method in (string[])["AddNew", "Delete"])
        {
            CallResult result = Method(rs, method);
            Assert.NotNull(result.Failure);
            Assert.Equal(FailureKind.NotImplemented, result.Failure!.Kind);
        }
        Ok(Method(rs, "Close"));

        // Snapshot SELECT keeps Edit refused by name.
        SelectRecordsetObject snap = Recover<SelectRecordsetObject>(Ok(Method(db, "OpenRecordset", "SELECT ID, N FROM T WHERE ID = 3", 4)));
        CallResult snapEdit = Method(snap, "Edit");
        Assert.NotNull(snapEdit.Failure);
        Assert.Equal(FailureKind.NotModelled, snapEdit.Failure!.Kind);
        Ok(Method(snap, "Close"));
    }

    private void AssertEnumeratedProperties(
        DatabaseObject db, string sql, string fieldName,
        short fieldType, object expectedValue, int expectedAttributes, short expectedOrdinal,
        int expectedSize, string expectedSourceTable, string expectedSourceField, bool expectedDataUpdatable)
    {
        SelectRecordsetObject rs = Recover<SelectRecordsetObject>(Ok(Method(db, "OpenRecordset", sql, 2)));
        SelectFieldObject field = Object<SelectFieldObject>(Object<SelectFieldsObject>(rs, "Fields"), "Item", fieldName);
        PropertiesObject props = Object<PropertiesObject>(field, "Properties");
        Assert.Equal(24, Int16Value(Ok(Get(props, "Count"))));

        (string Name, short Type)[] expectedSpecs =
        [
            ("Value", fieldType), ("Attributes", 4), ("CollatingOrder", 3), ("Type", 3), ("Name", 12),
            ("OrdinalPosition", 3), ("Size", 4), ("SourceField", 12), ("SourceTable", 12), ("ValidateOnSet", 1),
            ("DataUpdatable", 1), ("ForeignName", 12), ("DefaultValue", 12), ("ValidationRule", 12),
            ("ValidationText", 12), ("Required", 1), ("AllowZeroLength", 1), ("AppendOnly", 1),
            ("Expression", 12), ("FieldSize", 4), ("OriginalValue", 4), ("VisibleValue", 4),
            ("Precision", fieldType), ("Scale", fieldType),
        ];

        for (int i = 0; i < expectedSpecs.Length; i++)
        {
            PropertyObject p = Object<PropertyObject>(props, "Item", i);
            Assert.Equal(expectedSpecs[i].Name, TextValue(Ok(Get(p, "Name"))));
            Assert.Equal(expectedSpecs[i].Type, Int16Value(Ok(Get(p, "Type"))));
            Assert.False(BoolValue(Ok(Get(p, "Inherited"))));

            CallResult value = Get(p, "Value");
            switch (expectedSpecs[i].Name)
            {
                case "Value":
                    if (expectedValue is int iv)
                    {
                        Assert.Equal(iv, Int32Value(Ok(value)));
                    }
                    else
                    {
                        Assert.Equal((string)expectedValue, TextValue(Ok(value)));
                    }
                    break;
                case "Attributes":
                    Assert.Equal(expectedAttributes, Int32Value(Ok(value)));
                    break;
                case "CollatingOrder":
                    Assert.Equal(1033, Int32Value(Ok(value)));
                    break;
                case "Type":
                    Assert.Equal(fieldType, Int16Value(Ok(value)));
                    break;
                case "Name":
                    Assert.Equal(fieldName, TextValue(Ok(value)));
                    break;
                case "OrdinalPosition":
                    Assert.Equal(expectedOrdinal, Int16Value(Ok(value)));
                    break;
                case "Size":
                    Assert.Equal(expectedSize, Int32Value(Ok(value)));
                    break;
                case "SourceField":
                    Assert.Equal(expectedSourceField, TextValue(Ok(value)));
                    break;
                case "SourceTable":
                    Assert.Equal(expectedSourceTable, TextValue(Ok(value)));
                    break;
                case "ValidateOnSet":
                case "Required":
                case "AllowZeroLength":
                case "AppendOnly":
                    Assert.False(BoolValue(Ok(value)));
                    break;
                case "DataUpdatable":
                    Assert.Equal(expectedDataUpdatable, BoolValue(Ok(value)));
                    break;
                case "DefaultValue":
                case "ValidationRule":
                case "ValidationText":
                case "Expression":
                    Assert.Equal("", TextValue(Ok(value)));
                    break;
                case "ForeignName":
                    AssertDaoError(value, 3219, "DAO.Field");
                    break;
                case "FieldSize":
                    AssertDaoError(value, 3259, "DAO.Field");
                    break;
                case "OriginalValue":
                case "VisibleValue":
                    AssertDaoError(value, 3251, "DAO.Field");
                    break;
                case "Precision":
                    Assert.Equal((short)1033, Int16Value(Ok(value)));
                    break;
                case "Scale":
                    Assert.Equal((short)0, Int16Value(Ok(value)));
                    break;
            }
        }

        Assert.Equal(fieldName, TextValue(Ok(Get(Object<PropertyObject>(props, "Item", "Name"), "Value"))));
        Assert.Equal(fieldType, Int16Value(Ok(Get(Object<PropertyObject>(props, "Item", "Type"), "Value"))));
        AssertDaoError(Get(props, "Item", "Nope"), 3270, "DAO.Properties");
        Ok(Method(rs, "Close"));
    }

    private void AssertField(SelectFieldsObject fields, int ordinal, string name, int attributes, bool dataUpdatable, string sourceTable, string sourceField, bool required)
    {
        SelectFieldObject field = Object<SelectFieldObject>(fields, "Item", ordinal);
        Assert.Equal(name, TextValue(Ok(Get(field, "Name"))));
        Assert.Equal(attributes, Int32Value(Ok(Get(field, "Attributes"))));
        Assert.Equal(dataUpdatable, BoolValue(Ok(Get(field, "DataUpdatable"))));
        Assert.Equal(sourceTable, TextValue(Ok(Get(field, "SourceTable"))));
        Assert.Equal(sourceField, TextValue(Ok(Get(field, "SourceField"))));
        Assert.Equal(required, BoolValue(Ok(Get(field, "Required"))));
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

    private static short Int16Value(CallResult result)
    {
        Assert.True(result.HasScalar && (VarType)result.Scalar.Vt == VarType.I2);
        return (short)result.Scalar.Int64;
    }

    private static int Int32Value(CallResult result)
    {
        Assert.True(result.HasScalar && (VarType)result.Scalar.Vt == VarType.I4);
        return (int)result.Scalar.Int64;
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
