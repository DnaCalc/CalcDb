using System.Runtime.InteropServices;

using CalcDb.Com.Com;
using CalcDb.Com.Dao;
using CalcDb.Com.Declarations;
using CalcDb.Com.Host;
using CalcDb.Foundation;
using CalcDb.Values;

namespace CalcDb.Com.Tests;

/// <summary>
/// R-CURSOR-1 CUR-ID (register rows mbfm and hr7t; evidence/r-q6-bookmarks, evidence/r-q24-bookmarks):
/// DAO bookmark bytes for AddNew <c>LastModified</c> on dynasets, unconditional 0x0200 dynaset and 1-/2-LONG snapshot
/// bases, per-engine temp-page slot allocator (<c>0x0300 + 0x0300 * slot</c>) with clone sharing, gap reuse and Requery
/// reacquisition, recorded <c>Bookmark</c> put variant shapes, and named <c>NotModelled.*</c> refusals for unrecorded
/// schemas (DECIMAL, VARBINARY), multi-page neighbours/snapshots, and in-transaction AddNew rollback.
/// </summary>
public sealed unsafe class DaoBookmarkTests : IDisposable
{
    private const string Nav = "49288e9c099e3b28218a88a1c5a28a0053476f2e6d078898dd091510bc93be7d"; // r-cursor-1-nav.accdb
    private const string Cursor1bMain = "47f8fa36bfcd0df6185061dc39e47fd46cdac20f372942f9a8ff3a177a8bfbf6"; // r-cursor-1b-main.accdb
    private const string QMain = "37cb1596da09f7b6ba43f90571a8d381a057852eb7d762c73044c0a839134012"; // r-q-cursor-dao-main.accdb
    private const string RichFixture = "3b512c8f10e3ab22e434d9d6e69435bf231a310044b20cdb1c03069c1be34e95"; // r-dao-1b-rich.accdb

    private readonly List<string> _files = [];
    private readonly List<DatabaseObject> _databases = [];
    private readonly List<nint> _cells = [];
    private readonly List<OwnedVariant> _ownedVariants = [];
    private readonly List<(ComObject Obj, nint Ptr)> _held = [];
    private readonly EngineState _engine = new(new HostBinding(0xDA0_7E61, Standalone: true, 0, 0, 0, 0, 0));

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
        foreach (OwnedVariant owned in _ownedVariants)
        {
            owned.Dispose();
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
    public void LastModified_after_AddNew_on_unpopulated_dynaset_and_after_Requery_gives_appended_keyset_bookmark()
    {
        // r-cursor-1b/lastmodified-addnew (calls 46-51 -> 08020000, Bookmark stays 00020000) and
        // r-cursor-1b/lastmodified-clone-family (calls 33-36 -> rp 08020000 after Requery, rc keeps 01020000).
        DatabaseObject db = Open(Cursor1bMain, readOnly: false);
        RecordsetObject rp = OpenRecordset(db, "F", 2);
        RecordsetObject rc = Recover<RecordsetObject>(Ok(Method(rp, "Clone")));

        // Edit row 0 on rp, row 1 on rc.
        Ok(Method(rp, "Edit"));
        Ok(PutFieldValue(rp, "S", "edited-p"));
        Ok(Method(rp, "Update"));
        Assert.Equal("00020000", Hex(BytesValue(Ok(Get(rp, "LastModified")))));

        Ok(Method(rc, "MoveNext"));
        Ok(Method(rc, "Edit"));
        Ok(PutFieldValue(rc, "S", "edited-c"));
        Ok(Method(rc, "Update"));
        Assert.Equal("01020000", Hex(BytesValue(Ok(Get(rc, "LastModified")))));

        // Requery resets store to 1 populated row; AddNew+Update gives ordinal 8 (08020000) on rp, rc stays 01020000.
        Ok(Method(rp, "Requery"));
        Ok(Method(rp, "AddNew"));
        Ok(PutFieldValue(rp, "ID", 99));
        Ok(PutFieldValue(rp, "N", 99));
        Ok(PutFieldValue(rp, "S", "added"));
        Ok(Method(rp, "Update"));

        Assert.Equal("08020000", Hex(BytesValue(Ok(Get(rp, "LastModified")))));
        Assert.Equal("00020000", Hex(BytesValue(Ok(Get(rp, "Bookmark")))));
        Assert.Equal("01020000", Hex(BytesValue(Ok(Get(rc, "LastModified")))));

        Ok(Method(rp, "MoveLast"));
        Assert.Equal(99, Int32Value(Ok(FieldValue(rp, "ID"))));
        Assert.Equal("08020000", Hex(BytesValue(Ok(Get(rp, "Bookmark")))));

        Ok(Method(rc, "Close"));
        Ok(Method(rp, "Close"));
    }

    [Fact]
    public void Dynasets_number_from_0x0200_unconditionally_beside_any_neighbour()
    {
        // r-cursor-1/id-bookmark-shape, r-cursor-1b/clone-bookmark-sharing, r-q24-bookmarks/q4-dyn-beside-snap-0300,
        // r-q24-bookmarks/q4-dyn-beside-forwardonly, r-q24-bookmarks/q4-dyn-beside-select-dynaset,
        // r-q24-bookmarks/q4-dyn-beside-select-snapshot.
        DatabaseObject db = Open(Cursor1bMain, readOnly: true);

        RecordsetObject snap = OpenRecordset(db, "F", 4);
        Assert.Equal("00030000", Hex(BytesValue(Ok(Get(snap, "Bookmark")))));

        RecordsetObject fo = OpenRecordset(db, "F", 8);
        DispatchObject qDyn = Recover<DispatchObject>(Ok(Method(db, "OpenRecordset", "SELECT ID, S FROM F", 2)));
        DispatchObject qSnap = Recover<DispatchObject>(Ok(Method(db, "OpenRecordset", "SELECT ID, S FROM F ORDER BY ID", 4)));

        RecordsetObject d1 = OpenRecordset(db, "F", 2);
        RecordsetObject c1 = Recover<RecordsetObject>(Ok(Method(d1, "Clone")));
        RecordsetObject d2 = OpenRecordset(db, "F", 2);
        Ok(Method(d2, "Move", 2));

        Assert.Equal("00020000", Hex(BytesValue(Ok(Get(d1, "Bookmark")))));
        Assert.Equal("00020000", Hex(BytesValue(Ok(Get(c1, "Bookmark")))));
        Assert.Equal("02020000", Hex(BytesValue(Ok(Get(d2, "Bookmark")))));

        Ok(Method(d2, "Close"));
        Ok(Method(c1, "Close"));
        Ok(Method(d1, "Close"));
        Ok(Method(qSnap, "Close"));
        Ok(Method(qDyn, "Close"));
        Ok(Method(fo, "Close"));
        Ok(Method(snap, "Close"));
    }

    [Fact]
    public void Snapshots_over_1_or_2_LONG_or_COUNTER_columns_use_0x0200_beside_any_neighbour_and_occupy_a_slot()
    {
        // r-q6-bookmarks/snapshot-base-neighbours, r-q24-bookmarks/q2-snap-long1-beside-moved-dynaset,
        // r-q24-bookmarks/q2-snap-long2-beside-dynaset, r-q24-bookmarks/r2-q2-snap-long1-beside-fo-query-snapw,
        // r-q24-bookmarks/r2-q2-snap-long2-beside-fo-query-snapw, r-q24-bookmarks/r2-q1-snap-w-beside-l1-neighbours.
        DatabaseObject db = Open(Nav, readOnly: false);
        Ok(Method(db, "Execute", "CREATE TABLE L1 (n LONG)", 128));
        Ok(Method(db, "Execute", "INSERT INTO L1 (n) VALUES (1)", 128));
        Ok(Method(db, "Execute", "INSERT INTO L1 (n) VALUES (2)", 128));
        Ok(Method(db, "Execute", "CREATE TABLE L2 (c1 LONG, c2 LONG)", 128));
        Ok(Method(db, "Execute", "INSERT INTO L2 VALUES (1, 10)", 128));
        Ok(Method(db, "Execute", "INSERT INTO L2 VALUES (2, 20)", 128));
        Ok(Method(db, "Execute", "CREATE TABLE W (n LONG, t TEXT(10))", 128));
        Ok(Method(db, "Execute", "INSERT INTO W VALUES (1, 'a')", 128));
        Ok(Method(db, "Execute", "INSERT INTO W VALUES (2, 'b')", 128));

        // 1-LONG snapshot occupies slot 0 -> W snapshot beside it gets slot 1 (0x0600).
        RecordsetObject sl1 = OpenRecordset(db, "L1", 4);
        Assert.Equal("00020000", Hex(BytesValue(Ok(Get(sl1, "Bookmark")))));
        RecordsetObject sw1 = OpenRecordset(db, "W", 4);
        Assert.Equal("00060000", Hex(BytesValue(Ok(Get(sw1, "Bookmark")))));

        // 1-LONG and 2-LONG snapshots still get 0x0200 beside forward-only, SELECT queries, dynaset, and W snapshot.
        RecordsetObject fo = OpenRecordset(db, "W", 8);
        DispatchObject qDyn = Recover<DispatchObject>(Ok(Method(db, "OpenRecordset", "SELECT n, t FROM W", 2)));
        RecordsetObject dyn = OpenRecordset(db, "W", 2);
        Ok(Method(dyn, "MoveNext"));

        RecordsetObject sl1b = OpenRecordset(db, "L1", 4);
        RecordsetObject sl2 = OpenRecordset(db, "L2", 4);
        Assert.Equal("00020000", Hex(BytesValue(Ok(Get(sl1b, "Bookmark")))));
        Assert.Equal("00020000", Hex(BytesValue(Ok(Get(sl2, "Bookmark")))));

        Ok(Method(sl2, "Close"));
        Ok(Method(sl1b, "Close"));
        Ok(Method(dyn, "Close"));
        Ok(Method(qDyn, "Close"));
        Ok(Method(fo, "Close"));
        Ok(Method(sw1, "Close"));
        Ok(Method(sl1, "Close"));
    }

    [Fact]
    public void Snapshot_bookmark_base_covers_recorded_schemas_and_refuses_DECIMAL_and_VARBINARY_by_name()
    {
        // r-q6-bookmarks/first-snapshot-column-bit, r-q24-bookmarks/q3-schema-1col-bit, r-q24-bookmarks/q3-schema-1col-short,
        // r-q24-bookmarks/q3-schema-1col-single, r-q24-bookmarks/q3-schema-2col-long-single,
        // r-q24-bookmarks/q3-schema-idx-long1-pk, r-q24-bookmarks/q3-schema-idx-long2-pk,
        // r-q24-bookmarks/r2-q3-1col-binary4, r-q24-bookmarks/r2-q3-1col-counter, r-q24-bookmarks/r2-q3-2col-counter-long,
        // r-q24-bookmarks/r2-q3-2col-short-short, r-q24-bookmarks/r2-q3-idx-text-pk:
        // 1- and 2-LONG/COUNTER (indexed or unindexed) -> 0x0200; all other probed column types -> 0x0300;
        // unprobed DECIMAL and VARBINARY still refuse with NotModelled.SnapshotBookmarkBaseSchema.
        DatabaseObject db = Open(Nav, readOnly: false);
        Ok(Method(db, "Execute", "CREATE TABLE IndexedLong (c1 LONG CONSTRAINT PK_IL PRIMARY KEY)", 128));
        Ok(Method(db, "Execute", "INSERT INTO IndexedLong VALUES (1)", 128));
        Ok(Method(db, "Execute", "CREATE TABLE IndexedTwoLongs (c1 LONG CONSTRAINT PK_I2L PRIMARY KEY, c2 LONG)", 128));
        Ok(Method(db, "Execute", "INSERT INTO IndexedTwoLongs VALUES (1, 10)", 128));
        Ok(Method(db, "Execute", "CREATE TABLE Counter1 (c1 COUNTER)", 128));
        Ok(Method(db, "Execute", "INSERT INTO Counter1 (c1) VALUES (1)", 128));
        Ok(Method(db, "Execute", "CREATE TABLE CounterLong (c1 COUNTER, c2 LONG)", 128));
        Ok(Method(db, "Execute", "INSERT INTO CounterLong (c2) VALUES (10)", 128));

        Ok(Method(db, "Execute", "CREATE TABLE LongBit (c1 LONG, c2 BIT)", 128));
        Ok(Method(db, "Execute", "INSERT INTO LongBit VALUES (1, 1)", 128));
        Ok(Method(db, "Execute", "CREATE TABLE OnlyShort (c1 SHORT)", 128));
        Ok(Method(db, "Execute", "INSERT INTO OnlyShort VALUES (1)", 128));
        Ok(Method(db, "Execute", "CREATE TABLE OnlySingle (c1 SINGLE)", 128));
        Ok(Method(db, "Execute", "INSERT INTO OnlySingle VALUES (1)", 128));
        Ok(Method(db, "Execute", "CREATE TABLE OnlyBinary4 (c1 BINARY(4))", 128));
        Ok(Method(db, "Execute", "INSERT INTO OnlyBinary4 VALUES (NULL)", 128));
        Ok(Method(db, "Execute", "CREATE TABLE TwoShorts (c1 SHORT, c2 SHORT)", 128));
        Ok(Method(db, "Execute", "INSERT INTO TwoShorts VALUES (1, 2)", 128));
        Ok(Method(db, "Execute", "CREATE TABLE LongSingle (c1 LONG, c2 SINGLE)", 128));
        Ok(Method(db, "Execute", "INSERT INTO LongSingle VALUES (1, 2)", 128));
        Ok(Method(db, "Execute", "CREATE TABLE IndexedText (c1 LONG CONSTRAINT PK_IT PRIMARY KEY, c2 TEXT(10))", 128));
        Ok(Method(db, "Execute", "INSERT INTO IndexedText VALUES (1, 'a')", 128));
        Ok(Method(db, "Execute", "CREATE TABLE LongVarBinary (c1 LONG, c2 VARBINARY(4))", 128));
        Ok(Method(db, "Execute", "INSERT INTO LongVarBinary VALUES (1, NULL)", 128));

        AssertStandaloneSnapshotBookmark(db, "IndexedLong", "00020000");
        AssertStandaloneSnapshotBookmark(db, "IndexedTwoLongs", "00020000");
        AssertStandaloneSnapshotBookmark(db, "Counter1", "00020000");
        AssertStandaloneSnapshotBookmark(db, "CounterLong", "00020000");

        AssertStandaloneSnapshotBookmark(db, "LongBit", "00030000");
        AssertStandaloneSnapshotBookmark(db, "OnlyShort", "00030000");
        AssertStandaloneSnapshotBookmark(db, "OnlySingle", "00030000");
        AssertStandaloneSnapshotBookmark(db, "OnlyBinary4", "00030000");
        AssertStandaloneSnapshotBookmark(db, "TwoShorts", "00030000");
        AssertStandaloneSnapshotBookmark(db, "LongSingle", "00030000");
        AssertStandaloneSnapshotBookmark(db, "IndexedText", "00030000");

        AssertStandaloneSnapshotRefusal(db, "LongVarBinary", "NotModelled.SnapshotBookmarkBaseSchema");

        // DECIMAL column (r-dao-1b-rich.accdb Typed.TDec) stays refused by name.
        DatabaseObject rich = Open(RichFixture, readOnly: true);
        AssertStandaloneSnapshotRefusal(rich, "Typed", "NotModelled.SnapshotBookmarkBaseSchema");
    }

    [Fact]
    public void Per_engine_temp_slot_allocator_assigns_0x0300_stride_shares_across_clones_reuses_gaps_and_requeries()
    {
        // r-q24-bookmarks/q1-snap-w-alone, r-q24-bookmarks/q1d-snap-w-beside-table,
        // r-q24-bookmarks/q1e-snap-w-beside-forwardonly, r-q24-bookmarks/r2-q1-snap-w-beside-queries,
        // r-q24-bookmarks/r2-q1-snap-clone-neighbour, r-q24-bookmarks/r2-q1-snap-then-dyn-then-snap,
        // r-q24-bookmarks/r2-q1-requery-partial-close, r-cursor-1b/clone-bookmark-sharing.
        DatabaseObject db = Open(Cursor1bMain, readOnly: true);

        // Table-type, forward-only, and SELECT query recordsets take no slot.
        RecordsetObject tbl = OpenRecordset(db, "F", 1);
        RecordsetObject fo = OpenRecordset(db, "F", 8);
        DispatchObject qDyn = Recover<DispatchObject>(Ok(Method(db, "OpenRecordset", "SELECT ID, S FROM F", 2)));
        DispatchObject qSnap = Recover<DispatchObject>(Ok(Method(db, "OpenRecordset", "SELECT ID, S FROM F ORDER BY ID", 4)));

        // r-q24-bookmarks/r2-q1-snap-clone-neighbour: clone shares s1's slot 0 -> s2 takes slot 1 (0x0600).
        RecordsetObject s1 = OpenRecordset(db, "F", 4);
        RecordsetObject sc1 = Recover<RecordsetObject>(Ok(Method(s1, "Clone")));
        RecordsetObject s2 = OpenRecordset(db, "F", 4);
        Assert.Equal("00030000", Hex(BytesValue(Ok(Get(s1, "Bookmark")))));
        Assert.Equal("00030000", Hex(BytesValue(Ok(Get(sc1, "Bookmark")))));
        Assert.Equal("00060000", Hex(BytesValue(Ok(Get(s2, "Bookmark")))));
        Ok(Method(s2, "Close"));
        Ok(Method(sc1, "Close"));
        Ok(Method(s1, "Close"));

        // r-q24-bookmarks/r2-q1-snap-then-dyn-then-snap + r-cursor-1b/clone-bookmark-sharing:
        // s1 (slot 0 -> 0x0300), d1 (slot 1), s2 (slot 2 -> 0x0900, Move 2 -> 02090000, shared with clone s2c);
        // close s1 -> s3 reuses slot 0 (0x0300); close d1 -> s4 reuses slot 1 (0x0600).
        RecordsetObject m1 = OpenRecordset(db, "F", 4);
        Assert.Equal("00030000", Hex(BytesValue(Ok(Get(m1, "Bookmark")))));
        RecordsetObject d1 = OpenRecordset(db, "F", 2);
        RecordsetObject m2 = OpenRecordset(db, "F", 4);
        Ok(Method(m2, "Move", 2));
        Assert.Equal("02090000", Hex(BytesValue(Ok(Get(m2, "Bookmark")))));
        RecordsetObject m2c = Recover<RecordsetObject>(Ok(Method(m2, "Clone")));
        Ok(Method(m2c, "Move", 2));
        Assert.Equal("02090000", Hex(BytesValue(Ok(Get(m2c, "Bookmark")))));
        Ok(Method(m2c, "Close"));

        Ok(Method(m1, "Close"));
        RecordsetObject m3 = OpenRecordset(db, "F", 4);
        Assert.Equal("00030000", Hex(BytesValue(Ok(Get(m3, "Bookmark")))));

        Ok(Method(d1, "Close"));
        RecordsetObject m4 = OpenRecordset(db, "F", 4);
        Assert.Equal("00060000", Hex(BytesValue(Ok(Get(m4, "Bookmark")))));

        Ok(Method(m4, "Close"));
        Ok(Method(m3, "Close"));
        Ok(Method(m2, "Close"));

        // r-q24-bookmarks/r2-q1-requery-partial-close: d1 (slot 0), d2 (slot 1), rs (slot 2 -> 0x0900);
        // close d1 -> rs.Requery reacquires slot 0 (0x0300); close d2 -> rs.Requery stays on slot 0 (0x0300).
        RecordsetObject rd1 = OpenRecordset(db, "F", 2);
        RecordsetObject rd2 = OpenRecordset(db, "F", 2);
        RecordsetObject rs = OpenRecordset(db, "F", 4);
        Assert.Equal("00090000", Hex(BytesValue(Ok(Get(rs, "Bookmark")))));
        Ok(Method(rd1, "Close"));
        Ok(Method(rs, "Requery"));
        Assert.Equal("00030000", Hex(BytesValue(Ok(Get(rs, "Bookmark")))));
        Ok(Method(rd2, "Close"));
        Ok(Method(rs, "Requery"));
        Assert.Equal("00030000", Hex(BytesValue(Ok(Get(rs, "Bookmark")))));

        Ok(Method(rs, "Close"));
        Ok(Method(qSnap, "Close"));
        Ok(Method(qDyn, "Close"));
        Ok(Method(fo, "Close"));
        Ok(Method(tbl, "Close"));
    }

    [Fact]
    public void Multi_page_neighbours_and_multi_page_0x0300_snapshot_bookmarks_refuse_by_name()
    {
        // r-q24-bookmarks/r2-q1-snap-t-beside-big-dynaset (0x0a00),
        // r-q24-bookmarks/r2-q1-snap-t-beside-big-snapshot (0x4200, sbig 073f0000),
        // r-q24-bookmarks/r2-q4-dyn-beside-big-snapshot (dt 0x0200).
        DatabaseObject db = Open(QMain, readOnly: true);

        // Multi-page dynaset (2048 rows > 453) shifts T snapshot past 0x0600 -> refuse by name.
        RecordsetObject dbig = OpenRecordset(db, "Big", 2);
        Ok(Method(dbig, "MoveLast"));
        Assert.Equal("eb0a0000", Hex(BytesValue(Ok(Get(dbig, "Bookmark")))));
        RecordsetObject stBesideDyn = OpenRecordset(db, "T", 4);
        AssertNotModelled(Get(stBesideDyn, "Bookmark"), "NotModelled.SnapshotBookmarkBaseWithMultiPageNeighbour");
        AssertNotModelled(Put(stBesideDyn, "Bookmark", new byte[] { 0x00, 0x0a, 0x00, 0x00 }), "NotModelled.SnapshotBookmarkBaseWithMultiPageNeighbour");
        Ok(Method(stBesideDyn, "Close"));
        Ok(Method(dbig, "Close"));

        // Multi-page 0x0300 snapshot (2048 rows): row 0 is 00030000, ordinal 2047 refuses by name,
        // T snapshot beside it refuses by name, and T dynaset beside it still uses 0x0200.
        RecordsetObject sbig = OpenRecordset(db, "Big", 4);
        Assert.Equal("00030000", Hex(BytesValue(Ok(Get(sbig, "Bookmark")))));
        Ok(Method(sbig, "MoveLast"));
        AssertNotModelled(Get(sbig, "Bookmark"), "NotModelled.SnapshotBookmarkPastFirstPage");
        AssertNotModelled(Put(sbig, "Bookmark", new byte[] { 0x07, 0x3f, 0x00, 0x00 }), "NotModelled.SnapshotBookmarkPastFirstPage");

        RecordsetObject stBesideSnap = OpenRecordset(db, "T", 4);
        AssertNotModelled(Get(stBesideSnap, "Bookmark"), "NotModelled.SnapshotBookmarkBaseWithMultiPageNeighbour");
        Ok(Method(stBesideSnap, "Close"));

        RecordsetObject dtBesideSnap = OpenRecordset(db, "T", 2);
        Assert.Equal("00020000", Hex(BytesValue(Ok(Get(dtBesideSnap, "Bookmark")))));
        Ok(Method(dtBesideSnap, "Move", 2));
        Assert.Equal("02020000", Hex(BytesValue(Ok(Get(dtBesideSnap, "Bookmark")))));
        Ok(Method(dtBesideSnap, "Close"));
        Ok(Method(sbig, "Close"));
    }

    [Fact]
    public void Dynaset_with_in_transaction_AddNew_refuses_reads_after_Rollback_by_name()
    {
        // r-q24-bookmarks/r2-q5-dyn-big-begintrans-rollback-detail: ACE reverts keyset rows populated during a rolled-back
        // transaction and preserves the pre-transaction current row; refuse by name instead of answering 3021 or stale state.
        DatabaseObject db = Open(Cursor1bMain, readOnly: false);
        WorkspaceObject ws = _engine.DefaultWorkspace;
        RecordsetObject d = OpenRecordset(db, "F", 2);
        FieldsObject fields = Recover<FieldsObject>(Ok(Get(d, "Fields")));
        FieldObject idField = Recover<FieldObject>(Ok(Get(fields, "Item", "ID")));

        Ok(Method(ws, "BeginTrans"));
        Ok(Method(d, "AddNew"));
        Ok(PutFieldValue(d, "ID", 9001));
        Ok(PutFieldValue(d, "N", 9001));
        Ok(PutFieldValue(d, "S", "added"));
        Ok(Method(d, "Update"));
        Ok(Method(ws, "Rollback"));

        AssertNotModelled(Get(d, "RecordCount"), "NotModelled.Txn.RecordsetAfterRollback");
        AssertNotModelled(Get(d, "AbsolutePosition"), "NotModelled.Txn.RecordsetAfterRollback");
        AssertNotModelled(Get(d, "Bookmark"), "NotModelled.Txn.RecordsetAfterRollback");
        AssertNotModelled(Get(d, "Fields"), "NotModelled.Txn.RecordsetAfterRollback");
        AssertNotModelled(Get(idField, "Value"), "NotModelled.Txn.RecordsetAfterRollback");
        AssertNotModelled(Method(d, "MoveFirst"), "NotModelled.Txn.RecordsetAfterRollback");
        Ok(Method(d, "Close"));
    }

    [Fact]
    public void Bookmark_put_matches_recorded_variant_shapes_from_id_bookmark_shape()
    {
        // r-cursor-1/id-bookmark-shape calls 48, 57, 66, 75, 84, 93:
        // empty byte array & empty BSTR succeed without moving; 00000000 -> 3159 without IErrorInfo;
        // VT_I4, VT_NULL, VT_EMPTY -> 3421 with IErrorInfo; non-empty BSTR refuses by name.
        DatabaseObject db = Open(Nav, readOnly: true);
        RecordsetObject rs = OpenRecordset(db, "T", 2);
        Ok(Method(rs, "MoveNext"));

        Ok(Put(rs, "Bookmark", Array.Empty<byte>()));
        Assert.Equal("01020000", Hex(BytesValue(Ok(Get(rs, "Bookmark")))));

        CallResult bad = Put(rs, "Bookmark", new byte[] { 0, 0, 0, 0 });
        Assert.True(bad.Failure is { Kind: FailureKind.Dao, Number: 3159, Source: "DAO.Recordset", PublishesErrorInfo: false });

        Ok(Put(rs, "Bookmark", ""));
        Assert.Equal(1, Int32Value(Ok(Get(rs, "AbsolutePosition"))));

        CallResult i4 = Put(rs, "Bookmark", 1);
        Assert.True(i4.Failure is { Kind: FailureKind.Dao, Number: 3421, Source: "DAO.Recordset", PublishesErrorInfo: true });

        CallResult dbNull = Put(rs, "Bookmark", DBNull.Value);
        Assert.True(dbNull.Failure is { Kind: FailureKind.Dao, Number: 3421, Source: "DAO.Recordset", PublishesErrorInfo: true });

        CallResult empty = Put(rs, "Bookmark", (object?)null);
        Assert.True(empty.Failure is { Kind: FailureKind.Dao, Number: 3421, Source: "DAO.Recordset", PublishesErrorInfo: true });

        AssertNotModelled(Put(rs, "Bookmark", "abc"), "NotModelled.BookmarkPutNonEmptyBstr");

        Ok(Method(rs, "Close"));
    }

    private void AssertStandaloneSnapshotBookmark(DatabaseObject db, string table, string expectedHex)
    {
        RecordsetObject snap = OpenRecordset(db, table, 4);
        Assert.Equal(expectedHex, Hex(BytesValue(Ok(Get(snap, "Bookmark")))));
        Ok(Method(snap, "Close"));
    }

    private void AssertStandaloneSnapshotRefusal(DatabaseObject db, string table, string expectedNotModelled)
    {
        RecordsetObject snap = OpenRecordset(db, table, 4);
        AssertNotModelled(Get(snap, "Bookmark"), expectedNotModelled);
        Ok(Method(snap, "Close"));
    }

    private static void AssertNotModelled(CallResult result, string expectedName)
    {
        Assert.NotNull(result.Failure);
        Assert.Equal(FailureKind.NotModelled, result.Failure!.Kind);
        Assert.StartsWith(expectedName + ":", result.Failure.Description);
    }

    private DatabaseObject Open(string fixture, bool readOnly)
    {
        string path = Path.Combine(Path.GetTempPath(), $"calcdb-dao-bookmark-{Guid.NewGuid():N}.accdb");
        File.WriteAllBytes(path, CalcDb.Evidence.ArtifactStore.ForRepository(DeclarationGenerationTests.Root()).ReadAll(fixture));
        _files.Add(path);
        DatabaseObject db = Recover<DatabaseObject>(Ok(_engine.DefaultWorkspace.OpenDatabase([Arg(path), Arg(false), Arg(readOnly)])));
        _databases.Add(db);
        return db;
    }

    private RecordsetObject OpenRecordset(DatabaseObject db, string source, int type) =>
        Recover<RecordsetObject>(Ok(Method(db, "OpenRecordset", source, type)));

    private CallResult FieldValue(DispatchObject rs, string name)
    {
        FieldsObject fields = Recover<FieldsObject>(Ok(Get(rs, "Fields")));
        FieldObject field = Recover<FieldObject>(Ok(Get(fields, "Item", name)));
        return Get(field, "Value");
    }

    private CallResult PutFieldValue(DispatchObject rs, string name, object? value)
    {
        FieldsObject fields = Recover<FieldsObject>(Ok(Get(rs, "Fields")));
        FieldObject field = Recover<FieldObject>(Ok(Get(fields, "Item", name)));
        return Put(field, "Value", value);
    }

    private nint Arg(object? value)
    {
        var v = (NativeVariant*)NativeMemory.AllocZeroed((nuint)sizeof(NativeVariant));
        switch (value)
        {
            case null:
                v->Vt = (ushort)VarType.Empty;
                break;
            case DBNull:
                v->Vt = (ushort)VarType.Null;
                break;
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
            case byte[] bytes:
                nint psa = SafeArrays.Create(VarType.UI1, (0, bytes.Length));
                if (bytes.Length > 0)
                {
                    bytes.CopyTo(new Span<byte>(SafeArrays.ElementPointer(psa, 0), bytes.Length));
                }
                _ownedVariants.Add(OwnedVariant.TakeArray(psa, VarType.UI1));
                v->Vt = (ushort)(VarType.Array | VarType.UI1);
                v->Pointer = psa;
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

    private static int Int32Value(CallResult result)
    {
        Assert.True(result.HasScalar && (VarType)result.Scalar.Vt == VarType.I4);
        return (int)result.Scalar.Int64;
    }

    private static byte[] BytesValue(CallResult result)
    {
        NativeVariant v = result.HasScalar ? result.Scalar : *result.Value!.Pointer;
        Assert.Equal(VarType.Array | VarType.UI1, (VarType)v.Vt);
        Assert.NotEqual(0, v.Pointer);
        (int lower, int count) = SafeArrays.Shape(v.Pointer)[0];
        byte[] bytes = count <= 0 ? [] : new Span<byte>(SafeArrays.ElementPointer(v.Pointer, lower), count).ToArray();
        result.ReleaseUntaken();
        return bytes;
    }

    private static string Hex(byte[] bytes) => Convert.ToHexStringLower(bytes);

    private T Recover<T>(CallResult result) where T : ComObject
    {
        Assert.True(result.HasScalar && (VarType)result.Scalar.Vt == VarType.Dispatch, result.Failure?.Description);
        T obj = ComObject.Recover<T>(result.Scalar.Pointer);
        _held.Add((obj, result.Scalar.Pointer));
        return obj;
    }
}
