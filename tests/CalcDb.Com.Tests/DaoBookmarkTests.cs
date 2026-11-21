using System.Runtime.InteropServices;

using CalcDb.Com.Com;
using CalcDb.Com.Dao;
using CalcDb.Com.Declarations;
using CalcDb.Com.Host;
using CalcDb.Foundation;
using CalcDb.Values;

namespace CalcDb.Com.Tests;

/// <summary>
/// R-CURSOR-1 CUR-ID (register rows mbfm and hr7t): DAO bookmark bytes for AddNew <c>LastModified</c> on dynasets,
/// sequential and concurrent dynaset bookmark bases, standalone and 1-LONG snapshot bookmark bases, recorded
/// <c>Bookmark</c> put variant shapes, and named <c>NotModelled.*</c> refusals for unrecorded open-neighbour states
/// and unrecorded snapshot schemas.
/// </summary>
public sealed unsafe class DaoBookmarkTests : IDisposable
{
    private const string Nav = "49288e9c099e3b28218a88a1c5a28a0053476f2e6d078898dd091510bc93be7d"; // r-cursor-1-nav.accdb
    private const string Cursor1bMain = "47f8fa36bfcd0df6185061dc39e47fd46cdac20f372942f9a8ff3a177a8bfbf6"; // r-cursor-1b-main.accdb

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
    public void Sequential_and_concurrent_dynasets_number_from_0x0200_and_standalone_snapshot_after_closed_dynaset_numbers_from_0x0300()
    {
        // r-cursor-1/id-bookmark-shape (calls 16-34, 46), r-cursor-1b/bookmark-walk-types (calls 48-72),
        // r-cursor-1b/clone-bookmark-sharing (calls 15-17 -> rq 02020000 beside open rp+rc),
        // r-fix-cursor-dao/id-bookmark-assign-fresh (sequential dynasets all 0x0200).
        DatabaseObject db = Open(Cursor1bMain, readOnly: true);

        // Two concurrent dynasets plus a clone all use 0x0200.
        RecordsetObject d1 = OpenRecordset(db, "F", 2);
        RecordsetObject c1 = Recover<RecordsetObject>(Ok(Method(d1, "Clone")));
        RecordsetObject d2 = OpenRecordset(db, "F", 2);
        Ok(Method(d2, "Move", 2));
        Assert.Equal("00020000", Hex(BytesValue(Ok(Get(d1, "Bookmark")))));
        Assert.Equal("00020000", Hex(BytesValue(Ok(Get(c1, "Bookmark")))));
        Assert.Equal("02020000", Hex(BytesValue(Ok(Get(d2, "Bookmark")))));

        // Close all dynasets; a standalone snapshot over F now uses 0x0300.
        Ok(Method(d2, "Close"));
        Ok(Method(c1, "Close"));
        Ok(Method(d1, "Close"));

        RecordsetObject snap = OpenRecordset(db, "F", 4);
        Assert.Equal("00030000", Hex(BytesValue(Ok(Get(snap, "Bookmark")))));
        Ok(Method(snap, "MoveNext"));
        Assert.Equal("01030000", Hex(BytesValue(Ok(Get(snap, "Bookmark")))));
        Ok(Method(snap, "Close"));

        // A dynaset opened after the snapshot closed uses 0x0200 again.
        RecordsetObject d3 = OpenRecordset(db, "F", 2);
        Assert.Equal("00020000", Hex(BytesValue(Ok(Get(d3, "Bookmark")))));
        Ok(Method(d3, "Close"));
    }

    [Fact]
    public void Snapshot_over_one_unindexed_LONG_uses_0x0200_alone_and_beside_dynasets_table_and_snapshot()
    {
        // r-q6-bookmarks/snapshot-base-neighbours (calls 9-64: S (n LONG) -> 00020000 throughout).
        DatabaseObject db = Open(Nav, readOnly: false);
        Ok(Method(db, "Execute", "CREATE TABLE SL (n LONG)", 128));
        Ok(Method(db, "Execute", "INSERT INTO SL (n) VALUES (1)", 128));
        Ok(Method(db, "Execute", "INSERT INTO SL (n) VALUES (2)", 128));

        RecordsetObject s1 = OpenRecordset(db, "SL", 4);
        Assert.Equal("00020000", Hex(BytesValue(Ok(Get(s1, "Bookmark")))));
        Ok(Method(s1, "Close"));

        RecordsetObject d1 = OpenRecordset(db, "SL", 2);
        RecordsetObject d2 = OpenRecordset(db, "SL", 2);
        Assert.Equal("00020000", Hex(BytesValue(Ok(Get(d1, "Bookmark")))));
        Assert.Equal("00020000", Hex(BytesValue(Ok(Get(d2, "Bookmark")))));

        RecordsetObject s2 = OpenRecordset(db, "SL", 4);
        Assert.Equal("00020000", Hex(BytesValue(Ok(Get(s2, "Bookmark")))));
        Ok(Method(s2, "Close"));
        Ok(Method(d2, "Close"));
        Ok(Method(d1, "Close"));

        // Two concurrent 1-LONG snapshots both use 0x0200.
        RecordsetObject s7a = OpenRecordset(db, "SL", 4);
        RecordsetObject s7 = OpenRecordset(db, "SL", 4);
        Assert.Equal("00020000", Hex(BytesValue(Ok(Get(s7a, "Bookmark")))));
        Assert.Equal("00020000", Hex(BytesValue(Ok(Get(s7, "Bookmark")))));
        Ok(Method(s7, "Close"));
        Ok(Method(s7a, "Close"));
    }

    [Fact]
    public void Snapshot_bookmark_base_covers_recorded_column_shapes_and_refuses_unrecorded_schemas_by_name()
    {
        // r-q6-bookmarks rounds 2-5: 2 unindexed LONGs -> 0x0200; recorded 1- and 2-column shapes -> 0x0300;
        // unrecorded column types (SHORT, SINGLE) and indexed 1..2 LONG tables refuse by name instead of generalizing.
        DatabaseObject db = Open(Nav, readOnly: false);
        Ok(Method(db, "Execute", "CREATE TABLE TwoLongs (c1 LONG, c2 LONG)", 128));
        Ok(Method(db, "Execute", "INSERT INTO TwoLongs VALUES (1, 2)", 128));
        Ok(Method(db, "Execute", "CREATE TABLE OnlyByte (c1 BYTE)", 128));
        Ok(Method(db, "Execute", "INSERT INTO OnlyByte VALUES (1)", 128));
        Ok(Method(db, "Execute", "CREATE TABLE LongAndText (c1 LONG, c2 TEXT(10))", 128));
        Ok(Method(db, "Execute", "INSERT INTO LongAndText VALUES (1, 'a')", 128));
        Ok(Method(db, "Execute", "CREATE TABLE OnlyShort (c1 SHORT)", 128));
        Ok(Method(db, "Execute", "INSERT INTO OnlyShort VALUES (1)", 128));
        Ok(Method(db, "Execute", "CREATE TABLE LongSingle (c1 LONG, c2 SINGLE)", 128));
        Ok(Method(db, "Execute", "INSERT INTO LongSingle VALUES (1, 2)", 128));
        Ok(Method(db, "Execute", "CREATE TABLE IndexedLong (c1 LONG CONSTRAINT PK_IL PRIMARY KEY)", 128));
        Ok(Method(db, "Execute", "INSERT INTO IndexedLong VALUES (1)", 128));

        AssertStandaloneSnapshotBookmark(db, "TwoLongs", "00020000");
        AssertStandaloneSnapshotBookmark(db, "OnlyByte", "00030000");
        AssertStandaloneSnapshotBookmark(db, "LongAndText", "00030000");

        AssertStandaloneSnapshotRefusal(db, "OnlyShort", "NotModelled.SnapshotBookmarkBaseSchema");
        AssertStandaloneSnapshotRefusal(db, "LongSingle", "NotModelled.SnapshotBookmarkBaseSchema");
        AssertStandaloneSnapshotRefusal(db, "IndexedLong", "NotModelled.SnapshotBookmarkBaseSchema");
    }

    [Fact]
    public void Unrecorded_concurrent_neighbour_bookmark_bases_refuse_by_name()
    {
        // Snapshot over 0x0300 table beside open dynaset (r-cursor-1/life-requery 0x0600, r-cursor-1b/clone-bookmark-sharing 0x0900)
        // and dynaset opened beside an open snapshot are refused by name rather than guessed.
        DatabaseObject db = Open(Nav, readOnly: true);

        RecordsetObject rd = OpenRecordset(db, "T", 2);
        RecordsetObject snapBesideDynaset = OpenRecordset(db, "T", 4);
        AssertNotModelled(Get(snapBesideDynaset, "Bookmark"), "NotModelled.SnapshotBookmarkBaseWithOpenNeighbour");
        AssertNotModelled(Put(snapBesideDynaset, "Bookmark", new byte[] { 0x00, 0x06, 0x00, 0x00 }), "NotModelled.SnapshotBookmarkBaseWithOpenNeighbour");
        Ok(Method(snapBesideDynaset, "Close"));
        Ok(Method(rd, "Close"));

        RecordsetObject snap = OpenRecordset(db, "T", 4);
        Assert.Equal("00030000", Hex(BytesValue(Ok(Get(snap, "Bookmark")))));
        RecordsetObject dynBesideSnap = OpenRecordset(db, "T", 2);
        AssertNotModelled(Get(dynBesideSnap, "Bookmark"), "NotModelled.DynasetBookmarkBaseWithOpenNeighbour");
        AssertNotModelled(Put(dynBesideSnap, "Bookmark", new byte[] { 0x00, 0x02, 0x00, 0x00 }), "NotModelled.DynasetBookmarkBaseWithOpenNeighbour");
        Ok(Method(dynBesideSnap, "Close"));
        Ok(Method(snap, "Close"));
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
