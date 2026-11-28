using System.Runtime.InteropServices;

using AdoCommandObject = CalcDb.Com.Ado.CommandObject;
using AdoConnectionObject = CalcDb.Com.Ado.ConnectionObject;
using AdoRecordsetObject = CalcDb.Com.Ado.RecordsetObject;
using CalcDb.Com.Com;
using CalcDb.Com.Dao;
using CalcDb.Com.Declarations;
using CalcDb.Com.Host;
using CalcDb.Evidence;
using CalcDb.Foundation;
using CalcDb.Values;

namespace CalcDb.Com.Tests;

/// <summary>
/// Wave 22 targeted COM DAO/ADO parity tests (<c>W-08</c>, <c>W-09</c>, <c>R-DAO-1</c>, <c>R-ADO-1</c>, <c>R-WIRE-1</c>).
/// </summary>
public sealed unsafe class Wave22ComAdoDaoTests : IDisposable
{
    private const string DefCursorMainAccdb = "e6c0d23cc00a646991cc7c6cabd74305f2bc15ce8267745527e686ac13dd8dc0";
    private const string WireBlankAccdb = "6ac3ae9f64871e1a452e80f3164207d81c15619222251c524c527783c76090ef";
    private const string Ado1bMainAccdb = "612dea90cd639ee52fd6abacf9033b19957eb30aa50f187b3774d50a74e33ea7";
    private const string Q2AdoMiscMainAccdb = "dfe5bf3ce6e08aeb6e9f3b4edb71b33feda26e2cd4b1380709b9bfb6ef642c5b";
    private const string DbLangGeneral = ";LANGID=0x0409;CP=1252;COUNTRY=0";
    private const int DbVersion120 = 128;

    private static int s_nextIdentity;
    private readonly HostBinding _daoBinding = new(unchecked((uint)(0xDA02_2000 + Interlocked.Increment(ref s_nextIdentity))), Standalone: true, 0, 0, 0, 0, 0);
    private readonly HostBinding _adoBinding = new(unchecked((uint)(0xAD02_2000 + Interlocked.Increment(ref s_nextIdentity))), Standalone: true, 0, 0, 0, 0, 0);
    private readonly List<string> _files = [];
    private readonly List<DatabaseObject> _databases = [];
    private readonly List<AdoConnectionObject> _adoConnections = [];
    private readonly List<nint> _cells = [];
    private readonly List<OwnedVariant> _ownedVariants = [];
    private readonly List<nint> _held = [];
    private DBEngineObject? _eng;

    public void Dispose()
    {
        for (int i = _databases.Count - 1; i >= 0; i--)
        {
            if (!_databases[i].Closed)
            {
                Method(_databases[i], "Close");
            }
        }
        for (int i = _adoConnections.Count - 1; i >= 0; i--)
        {
            _ = Method(_adoConnections[i], "Close");
        }
        for (int i = _held.Count - 1; i >= 0; i--)
        {
            Marshal.Release(_held[i]);
        }
        foreach (OwnedVariant owned in _ownedVariants)
        {
            owned.Dispose();
        }
        foreach (nint cell in _cells)
        {
            var v = (NativeVariant*)cell;
            if (v->Vt == (ushort)VarType.Bstr && v->Pointer != 0)
            {
                Marshal.FreeBSTR(v->Pointer);
            }
            else if (v->Vt == (ushort)(VarType.Array | VarType.UI1) && v->Pointer != 0)
            {
                using OwnedVariant cleanup = OwnedVariant.TakeArray(v->Pointer, VarType.UI1);
            }
            NativeMemory.Free(v);
        }
        foreach (string path in _files)
        {
            try { File.Delete(path); } catch { }
            try { File.Delete(Path.ChangeExtension(path, ".laccdb")); } catch { }
        }
    }

    [Fact]
    public void Dao_assign_without_edit_by_type_and_explicit_txn_reedit()
    {
        // Replays evidence/r-def-cursor-dao/assign-without-edit-by-type calls 1..77 (specifically Table #1..26 and Dynaset #28..47).
        DatabaseObject db = OpenDao(DefCursorMainAccdb, readOnly: false);

        // Table (type=1): failed put without Edit leaves EditMode=0, then Edit() succeeds even inside implicit/explicit txn.
        RecordsetObject tbl = Recover<RecordsetObject>(Ok(Method(db, "OpenRecordset", "T", 1)));
        AssertDaoError(PutField(tbl, "N", 77), 3020, "DAO.Field", errorInfo: true);
        Assert.Equal((short)0, Int16Value(Ok(Get(tbl, "EditMode"))));
        Ok(Method(tbl, "Edit"));
        Ok(PutField(tbl, "N", 77));
        Ok(Method(tbl, "Update"));
        Assert.Equal(77, Int32Value(Ok(FieldValue(tbl, "N"))));
        AssertDaoError(Method(tbl, "CancelUpdate"), 3020, "DAO.Recordset", errorInfo: false);
        Ok(Method(tbl, "Close"));

        // Dynaset (type=2) inside explicit BeginTrans (call #37): Edit() after failed Value put must succeed (not 3218/blocked).
        DBEngineObject eng = OpenEngine();
        Ok(Method(eng, "BeginTrans"));
        RecordsetObject dyn = Recover<RecordsetObject>(Ok(Method(db, "OpenRecordset", "T", 2)));
        AssertDaoError(PutField(dyn, "N", 88), 3020, "DAO.Field", errorInfo: true);
        Assert.Equal((short)0, Int16Value(Ok(Get(dyn, "EditMode"))));
        Ok(Method(dyn, "Edit"));
        Ok(PutField(dyn, "N", 88));
        Ok(Method(dyn, "Update"));
        Assert.Equal(88, Int32Value(Ok(FieldValue(dyn, "N"))));
        AssertDaoError(Method(dyn, "CancelUpdate"), 3020, "DAO.Recordset", errorInfo: false);
        Ok(Method(dyn, "Close"));
        Ok(Method(eng, "Rollback"));

        // Snapshot (type=4) and ForwardOnly (type=8): put without Edit fails 3027 from DAO.Field, Edit fails 3251 from DAO.Recordset.
        foreach (int cursorType in new[] { 4, 8 })
        {
            RecordsetObject ro = Recover<RecordsetObject>(Ok(Method(db, "OpenRecordset", "T", cursorType)));
            AssertDaoError(PutField(ro, "N", 77), 3027, "DAO.Field", errorInfo: true);
            AssertDaoError(Method(ro, "Edit"), 3251, "DAO.Recordset", errorInfo: false);
            AssertDaoError(Method(ro, "Update"), 3251, "DAO.Recordset", errorInfo: false);
            AssertDaoError(Method(ro, "CancelUpdate"), 3251, "DAO.Recordset", errorInfo: false);
            Ok(Method(ro, "Close"));
        }
    }

    [Fact]
    public void Dao_chunk_append_binary_roundtrip_and_getrows_match_oracle()
    {
        // Replays evidence/r-def-cursor-dao/{chunk-append-memo-text,chunk-binary-roundtrip,bulk-getrows-memo-binary}.
        DatabaseObject db = OpenDao(DefCursorMainAccdb, readOnly: false);
        DBEngineObject eng = OpenEngine();
        Ok(Method(eng, "BeginTrans"));

        // 1. chunk-append-memo-text
        {
            SelectRecordsetObject rs = Recover<SelectRecordsetObject>(Ok(Method(db, "OpenRecordset", "SELECT ID, Mm, Tx FROM LB WHERE ID = 1", 2)));
            DispatchObject fId = FieldObj(rs, "ID");
            DispatchObject fMm = FieldObj(rs, "Mm");
            DispatchObject fTx = FieldObj(rs, "Tx");

            AssertDaoError(Method(fMm, "AppendChunk", "x"), 3020, "DAO.Field", errorInfo: false);
            Ok(Method(rs, "Edit"));
            Ok(Method(fMm, "AppendChunk", "abc"));
            Ok(Method(fMm, "AppendChunk", "def"));
            Assert.Equal(12, Int32Value(Ok(Get(fMm, "FieldSize"))));
            Assert.Equal("abcdef", TextValue(Ok(Method(fMm, "GetChunk", 0, 10))));
            Assert.Equal("abcdef", TextValue(Ok(Get(fMm, "Value"))));
            Ok(Method(rs, "Update"));
            Assert.Equal("abcdef", TextValue(Ok(Get(fMm, "Value"))));
            Assert.Equal(12, Int32Value(Ok(Get(fMm, "FieldSize"))));

            Ok(Method(rs, "Edit"));
            Ok(Method(fMm, "AppendChunk", "zzz"));
            Ok(Method(rs, "CancelUpdate"));
            Assert.Equal("abcdef", TextValue(Ok(Get(fMm, "Value"))));

            Ok(Method(rs, "Edit"));
            AssertDaoError(Method(fTx, "AppendChunk", "abc"), 3259, "DAO.Field", errorInfo: false);
            AssertDaoError(Get(fTx, "FieldSize"), 3259, "DAO.Field", errorInfo: false);
            Assert.Equal("t1", TextValue(Ok(Get(fTx, "Value"))));
            Ok(Method(rs, "CancelUpdate"));

            Ok(Method(rs, "Edit"));
            AssertDaoError(Method(fId, "AppendChunk", "x"), 3259, "DAO.Field", errorInfo: false);
            Ok(Method(rs, "CancelUpdate"));

            Ok(Method(rs, "Edit"));
            AssertDaoError(Method(fMm, "AppendChunk", DBNull.Value), 3001, "DAO.Field", errorInfo: false);
            AssertDaoError(Method(fMm, "AppendChunk", 5), 3001, "DAO.Field", errorInfo: false);
            AssertDaoError(Method(fMm, "AppendChunk", new byte[] { 0x41, 0x42 }), 3421, "DAO.Field", errorInfo: false);
            Assert.Equal(12, Int32Value(Ok(Get(fMm, "FieldSize"))));
            Assert.Equal("abcdef", TextValue(Ok(Get(fMm, "Value"))));
            Ok(Method(rs, "Update"));
            Ok(Method(rs, "Close"));
        }

        // 2. chunk-binary-roundtrip + bulk-getrows-memo-binary
        {
            SelectRecordsetObject rs = Recover<SelectRecordsetObject>(Ok(Method(db, "OpenRecordset", "SELECT ID, Bn FROM LB ORDER BY ID", 2)));
            DispatchObject fBn = FieldObj(rs, "Bn");
            Assert.Equal(0, Int32Value(Ok(Get(fBn, "FieldSize"))));
            Ok(Method(rs, "MoveNext"));
            Assert.Equal(0, Int32Value(Ok(Get(fBn, "FieldSize"))));
            Ok(Method(rs, "MoveNext"));
            Assert.Equal(0, Int32Value(Ok(Get(fBn, "FieldSize"))));

            byte[] p1 = new byte[32];
            byte[] p2 = new byte[32];
            byte[] full = new byte[64];
            for (int i = 0; i < 32; i++)
            {
                p1[i] = (byte)i;
                p2[i] = (byte)(32 + i);
                full[i] = p1[i];
                full[32 + i] = p2[i];
            }

            Ok(Method(rs, "Edit"));
            Ok(Method(fBn, "AppendChunk", p1));
            Ok(Method(fBn, "AppendChunk", p2));
            Assert.Equal(64, Int32Value(Ok(Get(fBn, "FieldSize"))));
            Assert.Equal(full, ByteArrayValue(Ok(Method(fBn, "GetChunk", 0, 64))));
            Ok(Method(rs, "Update"));
            Assert.Equal(64, Int32Value(Ok(Get(fBn, "FieldSize"))));
            Assert.Equal(full, ByteArrayValue(Ok(Method(fBn, "GetChunk", 0, 64))));
            Assert.Equal(new byte[] { 0x3c, 0x3d, 0x3e, 0x3f }, ByteArrayValue(Ok(Method(fBn, "GetChunk", 60, 10))));
            Assert.Empty(ByteArrayValue(Ok(Method(fBn, "GetChunk", 64, 1))));
            Assert.Equal(full, ByteArrayValue(Ok(Get(fBn, "Value"))));

            Ok(Method(rs, "Edit"));
            Ok(Put(fBn, "Value", new byte[] { 0x01, 0x02, 0x03 }));
            Assert.Equal(3, Int32Value(Ok(Get(fBn, "FieldSize"))));
            Ok(Method(rs, "Update"));
            Assert.Equal(new byte[] { 0x01, 0x02, 0x03 }, ByteArrayValue(Ok(Method(fBn, "GetChunk", 0, 8))));
            Assert.Equal(new byte[] { 0x01, 0x02, 0x03 }, ByteArrayValue(Ok(Get(fBn, "Value"))));

            Ok(Method(rs, "Edit"));
            Ok(Put(fBn, "Value", DBNull.Value));
            Ok(Method(rs, "Update"));
            Assert.Equal(0, Int32Value(Ok(Get(fBn, "FieldSize"))));
            Assert.Equal(VarType.Null, ResultVarType(Ok(Get(fBn, "Value"))));

            // 3. bulk-getrows-memo-binary (depth 0 without BeginTrans, matching retained record)
        }
        Ok(Method(eng, "Rollback"));

        {
            DatabaseObject dbRows = OpenDao(DefCursorMainAccdb, readOnly: false);
            byte[] full = new byte[64];
            for (int i = 0; i < 64; i++)
            {
                full[i] = (byte)i;
            }
            SelectRecordsetObject wr = Recover<SelectRecordsetObject>(Ok(Method(dbRows, "OpenRecordset", "SELECT ID, Bn FROM LB WHERE ID = 3", 2)));
            DispatchObject fBnWr = FieldObj(wr, "Bn");
            Ok(Method(wr, "Edit"));
            Ok(Method(fBnWr, "AppendChunk", full));
            Ok(Method(wr, "Update"));
            Ok(Method(wr, "Close"));

            foreach (int cursorType in new[] { 1, 2, 4 })
            {
                RecordsetObject gr = Recover<RecordsetObject>(Ok(Method(dbRows, "OpenRecordset", "LB", cursorType)));
                CallResult rows = Ok(Method(gr, "GetRows", 3));
                using (rows.Value)
                {
                    NativeVariant rv = rows.HasScalar ? rows.Scalar : *rows.Value!.Pointer;
                    Assert.Equal((ushort)(VarType.Array | VarType.Variant), rv.Vt);
                    (int Lower, int Count)[] shape = SafeArrays.Shape(rv.Pointer);
                    Assert.Equal(2, shape.Length);
                    Assert.Equal((0, 4), shape[0]);
                    Assert.Equal((0, 3), shape[1]);
                }
                Ok(Method(gr, "Close"));
            }

            // Honest refusals on unrecorded AppendChunk boundaries (Snapshot/ForwardOnly table & non-updatable SELECT).
            RecordsetObject snapTbl = Recover<RecordsetObject>(Ok(Method(dbRows, "OpenRecordset", "LB", 4)));
            AssertNotModelled(Method(FieldObj(snapTbl, "Mm"), "AppendChunk", "x"), "NotModelled.TableAppendChunkReadOnly");
            Ok(Method(snapTbl, "Close"));

            SelectRecordsetObject snapSel = Recover<SelectRecordsetObject>(Ok(Method(dbRows, "OpenRecordset", "SELECT ID, Mm FROM LB ORDER BY ID", 4)));
            AssertNotModelled(Method(FieldObj(snapSel, "Mm"), "AppendChunk", "x"), "NotModelled.QueryFieldAppendChunkReadOnly");
            Ok(Method(snapSel, "Close"));
        }
    }

    [Fact]
    public void Dao_chunk_memo_and_ole_fieldsize_edge_cases_match_oracle()
    {
        // Replays evidence/r-fix-wire-ado/chunk-memo-text-append-fieldsize & chunk-ole-append-fieldsize.
        DatabaseObject db = OpenDao(WireBlankAccdb, readOnly: false);

        // Memo text edge cases
        {
            SelectRecordsetObject rs = Recover<SelectRecordsetObject>(Ok(Method(db, "OpenRecordset", "SELECT ID, FMemo, FText FROM W ORDER BY ID", 2)));
            DispatchObject fId = FieldObj(rs, "ID");
            DispatchObject fMemo = FieldObj(rs, "FMemo");
            DispatchObject fText = FieldObj(rs, "FText");

            // ID=1: "abc" -> FieldSize 6, GetChunk="abc"
            Ok(Method(rs, "AddNew"));
            Ok(Put(fId, "Value", 1));
            Ok(Method(fMemo, "AppendChunk", "abc"));
            Ok(Method(rs, "Update"));

            // ID=2: embedded NUL "a\0b" -> FieldSize 6, GetChunk="a\0b"
            Ok(Method(rs, "AddNew"));
            Ok(Put(fId, "Value", 2));
            Ok(Method(fMemo, "AppendChunk", "a\0b"));
            Ok(Method(rs, "Update"));

            // ID=3,4: odd-byte BSTR on fresh AddNew -> fails 3702, writes "" so Update succeeds, FieldSize 0, GetChunk=""
            foreach ((int id, byte[] oddBytes) in new[] { (3, new byte[] { 0x41 }), (4, new byte[] { 0x41, 0x00, 0x42 }) })
            {
                Ok(Method(rs, "AddNew"));
                Ok(Put(fId, "Value", id));
                AssertDaoError(RunBstrBytes(fMemo, "AppendChunk", oddBytes), 3702, "DAO.Field", errorInfo: false);
                Ok(Method(rs, "Update"));
            }

            // ID=5: 2-byte unpaired surrogate 0xD800 -> succeeds, FieldSize 2
            Ok(Method(rs, "AddNew"));
            Ok(Put(fId, "Value", 5));
            Ok(RunBstrBytes(fMemo, "AppendChunk", new byte[] { 0x00, 0xd8 }));
            Ok(Method(rs, "Update"));

            // ID=6: "ab" + odd 1-byte (3702) + "cd" -> FieldSize 8, GetChunk="abcd"
            Ok(Method(rs, "AddNew"));
            Ok(Put(fId, "Value", 6));
            Ok(Method(fMemo, "AppendChunk", "ab"));
            AssertDaoError(RunBstrBytes(fMemo, "AppendChunk", new byte[] { 0x41 }), 3702, "DAO.Field", errorInfo: false);
            Ok(Method(fMemo, "AppendChunk", "cd"));
            Ok(Method(rs, "Update"));

            // ID=7..12: Byte[] -> 3421, Null/Empty/Missing/I4 -> 3001, "" -> no-op (remains Null)
            Ok(Method(rs, "AddNew"));
            Ok(Put(fId, "Value", 7));
            AssertDaoError(Method(fMemo, "AppendChunk", new byte[] { 0x41, 0x00, 0x42, 0x00 }), 3421, "DAO.Field", errorInfo: false);
            Ok(Method(rs, "Update"));

            Ok(Method(rs, "AddNew"));
            Ok(Put(fId, "Value", 8));
            AssertDaoError(Method(fMemo, "AppendChunk", DBNull.Value), 3001, "DAO.Field", errorInfo: false);
            Ok(Method(rs, "Update"));

            Ok(Method(rs, "AddNew"));
            Ok(Put(fId, "Value", 9));
            AssertDaoError(Method(fMemo, "AppendChunk", (object?)null), 3001, "DAO.Field", errorInfo: false);
            Ok(Method(rs, "Update"));

            Ok(Method(rs, "AddNew"));
            Ok(Put(fId, "Value", 10));
            AssertDaoError(Method(fMemo, "AppendChunk", Missing()), 3001, "DAO.Field", errorInfo: false);
            Ok(Method(rs, "Update"));

            Ok(Method(rs, "AddNew"));
            Ok(Put(fId, "Value", 11));
            AssertDaoError(Method(fMemo, "AppendChunk", 5), 3001, "DAO.Field", errorInfo: false);
            Ok(Method(rs, "Update"));

            Ok(Method(rs, "AddNew"));
            Ok(Put(fId, "Value", 12));
            Ok(Method(fMemo, "AppendChunk", ""));
            Ok(Method(rs, "Update"));

            // Text column -> 3259
            Ok(Method(rs, "AddNew"));
            Ok(Put(fId, "Value", 21));
            AssertDaoError(Method(fText, "AppendChunk", "abc"), 3259, "DAO.Field", errorInfo: false);
            Ok(Method(rs, "Update"));

            // Read back committed values
            void FindRow(int id)
            {
                Ok(Method(rs, "FindFirst", $"ID = {id}"));
                Assert.False(BoolValue(Ok(Get(rs, "NoMatch"))));
            }

            FindRow(1);
            Assert.Equal(6, Int32Value(Ok(Get(fMemo, "FieldSize"))));
            Assert.Equal("abc", TextValue(Ok(Method(fMemo, "GetChunk", 0, 40))));

            FindRow(2);
            Assert.Equal(6, Int32Value(Ok(Get(fMemo, "FieldSize"))));
            Assert.Equal("a\0b", TextValue(Ok(Method(fMemo, "GetChunk", 0, 40))));

            FindRow(3);
            Assert.Equal(0, Int32Value(Ok(Get(fMemo, "FieldSize"))));
            Assert.Equal("", TextValue(Ok(Method(fMemo, "GetChunk", 0, 40))));

            FindRow(4);
            Assert.Equal(0, Int32Value(Ok(Get(fMemo, "FieldSize"))));
            Assert.Equal("", TextValue(Ok(Method(fMemo, "GetChunk", 0, 40))));

            FindRow(5);
            Assert.Equal(2, Int32Value(Ok(Get(fMemo, "FieldSize"))));
            Assert.Equal("\ud800", TextValue(Ok(Method(fMemo, "GetChunk", 0, 40))));

            FindRow(6);
            Assert.Equal(8, Int32Value(Ok(Get(fMemo, "FieldSize"))));
            Assert.Equal("abcd", TextValue(Ok(Method(fMemo, "GetChunk", 0, 40))));

            foreach (int nullId in new[] { 7, 8, 9, 10, 11, 12 })
            {
                FindRow(nullId);
                Assert.Equal(0, Int32Value(Ok(Get(fMemo, "FieldSize"))));
                Assert.Equal(VarType.Null, ResultVarType(Ok(Method(fMemo, "GetChunk", 0, 40))));
            }

            FindRow(21);
            AssertDaoError(Get(fText, "FieldSize"), 3259, "DAO.Field", errorInfo: false);
            Ok(Method(rs, "Close"));
        }

        // OLE LongBinary edge cases (uses ID >= 100 on the same table to avoid PK collisions)
        {
            SelectRecordsetObject rs = Recover<SelectRecordsetObject>(Ok(Method(db, "OpenRecordset", "SELECT ID, FOle FROM W ORDER BY ID", 2)));
            DispatchObject fId = FieldObj(rs, "ID");
            DispatchObject fOle = FieldObj(rs, "FOle");

            // 101: [0,0,1] -> FieldSize 3
            Ok(Method(rs, "AddNew"));
            Ok(Put(fId, "Value", 101));
            Ok(Method(fOle, "AppendChunk", new byte[] { 0x00, 0x00, 0x01 }));
            Ok(Method(rs, "Update"));

            // 102: empty byte[0] -> no-op, FieldSize 0, GetChunk=Null
            Ok(Method(rs, "AddNew"));
            Ok(Put(fId, "Value", 102));
            Ok(Method(fOle, "AppendChunk", Array.Empty<byte>()));
            Ok(Method(rs, "Update"));

            // 104: odd BSTR bytes [1,2,3] -> succeeds on OLE, 3 bytes
            Ok(Method(rs, "AddNew"));
            Ok(Put(fId, "Value", 104));
            Ok(RunBstrBytes(fOle, "AppendChunk", new byte[] { 0x01, 0x02, 0x03 }));
            Ok(Method(rs, "Update"));

            // 105: BSTR "AB" -> UTF-16LE [0x41,0x00,0x42,0x00] (4 bytes)
            Ok(Method(rs, "AddNew"));
            Ok(Put(fId, "Value", 105));
            Ok(Method(fOle, "AppendChunk", "AB"));
            Ok(Method(rs, "Update"));

            // 106: multi-append [1,2,3] + [0,0] + [0xff] -> 6 bytes
            Ok(Method(rs, "AddNew"));
            Ok(Put(fId, "Value", 106));
            Ok(Method(fOle, "AppendChunk", new byte[] { 0x01, 0x02, 0x03 }));
            Ok(Method(fOle, "AppendChunk", new byte[] { 0x00, 0x00 }));
            Ok(Method(fOle, "AppendChunk", new byte[] { 0xff }));
            Ok(Method(rs, "Update"));

            // 107..111: Null/Empty/Missing/I4/R8/Bool -> 3001
            int nextId = 107;
            foreach (object? bad in new[] { (object?)DBNull.Value, null, Missing(), 5, 1.5, true })
            {
                Ok(Method(rs, "AddNew"));
                Ok(Put(fId, "Value", nextId++));
                AssertDaoError(Method(fOle, "AppendChunk", bad), 3001, "DAO.Field", errorInfo: false);
                Ok(Method(rs, "Update"));
            }

            Ok(Method(rs, "FindFirst", "ID = 101"));
            Assert.Equal(3, Int32Value(Ok(Get(fOle, "FieldSize"))));
            Assert.Equal(new byte[] { 0x00, 0x00, 0x01 }, ByteArrayValue(Ok(Method(fOle, "GetChunk", 0, 300))));

            Ok(Method(rs, "FindFirst", "ID = 102"));
            Assert.Equal(0, Int32Value(Ok(Get(fOle, "FieldSize"))));
            Assert.Equal(VarType.Null, ResultVarType(Ok(Method(fOle, "GetChunk", 0, 300))));

            Ok(Method(rs, "FindFirst", "ID = 104"));
            Assert.Equal(3, Int32Value(Ok(Get(fOle, "FieldSize"))));
            Assert.Equal(new byte[] { 0x01, 0x02, 0x03 }, ByteArrayValue(Ok(Method(fOle, "GetChunk", 0, 300))));

            Ok(Method(rs, "FindFirst", "ID = 105"));
            Assert.Equal(4, Int32Value(Ok(Get(fOle, "FieldSize"))));
            Assert.Equal(new byte[] { 0x41, 0x00, 0x42, 0x00 }, ByteArrayValue(Ok(Method(fOle, "GetChunk", 0, 300))));

            Ok(Method(rs, "FindFirst", "ID = 106"));
            Assert.Equal(6, Int32Value(Ok(Get(fOle, "FieldSize"))));
            Assert.Equal(new byte[] { 0x01, 0x02, 0x03, 0x00, 0x00, 0xff }, ByteArrayValue(Ok(Method(fOle, "GetChunk", 0, 300))));

            Ok(Method(rs, "Close"));
        }
    }

    [Fact]
    public void Dao_edit_conflict_cancelupdate_without_edit_and_restore_in_txn()
    {
        // Replays evidence/r-def-cursor-dao/edit-conflict-restore-value calls 5..68.
        DatabaseObject db = OpenDao(DefCursorMainAccdb, readOnly: false);
        DBEngineObject eng = OpenEngine();
        Ok(Method(eng, "BeginTrans"));

        SelectRecordsetObject r1 = Recover<SelectRecordsetObject>(Ok(Method(db, "OpenRecordset", "SELECT ID, N FROM T WHERE ID = 3", 2)));
        Ok(Put(r1, "LockEdits", false));
        SelectRecordsetObject r2 = Recover<SelectRecordsetObject>(Ok(Method(db, "OpenRecordset", "SELECT ID, N FROM T WHERE ID = 3", 2)));

        Ok(Method(r1, "Edit"));
        Assert.Equal(30, Int32Value(Ok(FieldValue(r1, "N"))));
        Ok(Method(r2, "Edit"));
        Ok(PutField(r2, "N", 31));
        Ok(Method(r2, "Update"));
        Ok(Method(r2, "Edit"));
        Ok(PutField(r2, "N", 30));
        Ok(Method(r2, "Update"));

        Ok(PutField(r1, "N", 35));
        Ok(Method(r1, "Update"));
        Assert.Equal(35, Int32Value(Ok(FieldValue(r1, "N"))));
        Assert.Equal((short)0, Int16Value(Ok(Get(r1, "EditMode"))));
        // Call #31: CancelUpdate when EditMode=0 inside BeginTrans must fail 3020 from DAO.Recordset (not silent success!).
        AssertDaoError(Method(r1, "CancelUpdate"), 3020, "DAO.Recordset", errorInfo: false);

        Ok(Method(r1, "Edit"));
        Ok(Method(r2, "Edit"));
        Ok(PutField(r2, "N", 31));
        Ok(Method(r2, "Update"));

        Ok(PutField(r1, "N", 36));
        AssertDaoError(Method(r1, "Update"), 3197, "DAO.Recordset", errorInfo: false);
        Assert.Equal(36, Int32Value(Ok(FieldValue(r1, "N"))));
        Assert.Equal((short)1, Int16Value(Ok(Get(r1, "EditMode"))));
        Ok(Method(r1, "CancelUpdate"));
        Ok(Method(r1, "Requery"));
        Assert.Equal(31, Int32Value(Ok(FieldValue(r1, "N"))));

        Ok(Method(r2, "Close"));
        Ok(Method(r1, "Close"));
        Ok(Method(eng, "Rollback"));
    }

    [Fact]
    public void Dao_err_dup_field_and_index_append_check_table_in_use_before_duplicates()
    {
        // Replays evidence/r-def-cursor-dao/{err-dup-field-append,err-dup-index-append}.
        DatabaseObject db = OpenDao(DefCursorMainAccdb, readOnly: false);

        DispatchObject tdT = Item(Get(db, "TableDefs"), "T");
        DispatchObject dupField = Recover<DispatchObject>(Ok(Method(tdT, "CreateField", "N", 4)));
        DispatchObject tdFields = Recover<DispatchObject>(Ok(Get(tdT, "Fields")));
        AssertDaoError(Method(tdFields, "Append", dupField), 3191, "DAO.TableDef", errorInfo: false);

        DispatchObject dupIx = Recover<DispatchObject>(Ok(Method(tdT, "CreateIndex", "IX_TN")));
        DispatchObject ixField = Recover<DispatchObject>(Ok(Method(dupIx, "CreateField", "N")));
        DispatchObject ixFields = Recover<DispatchObject>(Ok(Get(dupIx, "Fields")));
        Ok(Method(ixFields, "Append", ixField));
        DispatchObject tdIndexes = Recover<DispatchObject>(Ok(Get(tdT, "Indexes")));
        AssertDaoError(Method(tdIndexes, "Append", dupIx), 3284, "DAO.Indexes", errorInfo: false);

        // Open a Snapshot on T: Fields.Append and Indexes.Append now fail 3211 before duplicate check, without IErrorInfo.
        SelectRecordsetObject sr = Recover<SelectRecordsetObject>(Ok(Method(db, "OpenRecordset", "SELECT ID, N FROM T ORDER BY ID", 4)));
        DispatchObject dupField2 = Recover<DispatchObject>(Ok(Method(tdT, "CreateField", "N", 4)));
        AssertDaoError(Method(tdFields, "Append", dupField2), 3211, "DAO.TableDef", errorInfo: false);

        DispatchObject dupIx2 = Recover<DispatchObject>(Ok(Method(tdT, "CreateIndex", "IX_TN")));
        DispatchObject ixField2 = Recover<DispatchObject>(Ok(Method(dupIx2, "CreateField", "N")));
        DispatchObject ixFields2 = Recover<DispatchObject>(Ok(Get(dupIx2, "Fields")));
        Ok(Method(ixFields2, "Append", ixField2));
        AssertDaoError(Method(tdIndexes, "Append", dupIx2), 3211, "DAO.Indexes", errorInfo: false);

        Ok(Method(sr, "Close"));
    }

    [Fact]
    public void Dao_byref_put_values_handles_odd_byte_bstr_for_binary_and_preserves_caller_cell()
    {
        // Replays evidence/r-q2-wire/byref-dao-put-values (#24 odd 3-byte BSTR in Binary(8) via ByRef Variant).
        string path = TempPath(".accdb");
        DBEngineObject eng = OpenEngine();
        DatabaseObject db = Recover<DatabaseObject>(Ok(Method(eng, "CreateDatabase", path, DbLangGeneral, DbVersion120)));
        _databases.Add(db);
        Ok(Method(db, "Execute", "CREATE TABLE W (ID LONG CONSTRAINT PK_W PRIMARY KEY, T TEXT(50), L LONG, D DOUBLE, B BINARY(8))"));

        RecordsetObject w = Recover<RecordsetObject>(Ok(Method(db, "OpenRecordset", "W", 2)));
        Ok(Method(w, "AddNew"));
        Ok(PutField(w, "ID", 1));
        Ok(PutField(w, "T", "abc"));
        Ok(PutField(w, "L", 7));
        Ok(PutField(w, "D", 2.5));

        DispatchObject fB = FieldObj(w, "B");
        using OwnedVariant oddBstrOwned = OwnedVariant.FromBstrBytes([0x41, 0x00, 0x42]);
        nint oddBstr = oddBstrOwned.Pointer->Pointer;
        NativeVariant inner = new() { Vt = (ushort)VarType.Bstr, Pointer = oddBstr };
        NativeVariant byRef = new() { Vt = (ushort)(VarType.ByRef | VarType.Variant), Pointer = (nint)(&inner) };
        int dispId = fB.Declaration.Member("Value", InvokeKind.PropertyPut).MemberId;
        int propPut = -3;
        var dp = new DispParams { Args = (nint)(&byRef), NamedArgs = (nint)(&propPut), ArgCount = 1, NamedArgCount = 1 };
        NativeVariant res = default;
        ExcepInfo ex = default;
        uint argErr = 0;
        Assert.Equal(HResults.S_OK, fB.Invoke(dispId, (ushort)InvokeKind.PropertyPut, &dp, &res, &ex, &argErr));
        Assert.Equal((ushort)VarType.Bstr, inner.Vt);
        Assert.Equal(oddBstr, inner.Pointer);
        Assert.Equal(new byte[] { 0x41, 0x00, 0x42, 0, 0, 0, 0, 0 }, ByteArrayValue(Ok(Get(fB, "Value"))));

        Ok(Method(w, "Update"));
        Ok(Method(w, "AddNew"));
        Ok(PutField(w, "ID", 3));
        Ok(PutField(w, "L", null)); // VT_EMPTY via Value put -> Null
        Ok(Method(w, "Update"));
        Ok(Method(w, "Close"));
        Ok(Method(db, "Close"));

        DatabaseObject ro = Recover<DatabaseObject>(Ok(Method(eng, "OpenDatabase", path, false, true)));
        _databases.Add(ro);
        SelectRecordsetObject check = Recover<SelectRecordsetObject>(Ok(Method(ro, "OpenRecordset", "SELECT ID, T, L, D, B FROM W ORDER BY ID", 4)));
        Assert.Equal(new byte[] { 0x41, 0x00, 0x42, 0, 0, 0, 0, 0 }, ByteArrayValue(Ok(FieldValue(check, "B"))));
        Ok(Method(check, "MoveNext"));
        Assert.Equal(VarType.Null, ResultVarType(Ok(FieldValue(check, "L"))));
        Ok(Method(check, "Close"));
    }

    [Fact]
    public void Ado_fields_meta_expressions_e1_to_e17_pass_while_now_and_cdec_stay_refused_in_expressions()
    {
        // Replays evidence/r-ado-1b/fields-meta-expressions calls 15..164 (e1..e17 match oracle, while Now() AS e18
        // and CDec(L) AS x1 stop cleanly in unowned CalcDb.Expressions with NotModelled.Function; AdoFields.FromSelectColumn
        // already maps a bound Now() call AST to adDate(7), DefinedSize=8, Prec=255, Scale=255, Attr=114).
        AdoConnectionObject cn = OpenAdo(Ado1bMainAccdb);
        AdoRecordsetObject rs = CreateAdoRecordset();
        Ok(Put(rs, "CursorLocation", 2)); // adUseServer

        const string sql17 = "SELECT L + 1 AS e1, L * 1.5 AS e2, D / 2 AS e3, S & 'x' AS e4, Len(S) AS e5, "
            + "IIf(L > 15, 'a', 'b') AS e6, CCur(L) AS e7, CDbl(L) AS e8, CInt(L) AS e9, CBool(L) AS e10, "
            + "CDate(0) AS e11, Null AS e12, 1 AS e13, 1.5 AS e14, 'lit' AS e15, #2024-01-02# AS e16, "
            + "True AS e17 FROM T WHERE ID = 1";
        Ok(Method(rs, "Open", sql17, cn, 0, 1, 1));
        DispatchObject fields = Recover<DispatchObject>(Ok(Get(rs, "Fields")));
        Assert.Equal(17, Int32Value(Ok(Get(fields, "Count"))));
        DispatchObject e17 = Item(fields, 16);
        Assert.Equal("e17", TextValue(Ok(Get(e17, "Name"))));
        Assert.Equal(2, Int32Value(Ok(Get(e17, "Type"))));
        Assert.Equal(114, Int32Value(Ok(Get(e17, "Attributes"))));
        Ok(Method(rs, "Close"));

        // Full 18-column query with Now() AS e18 stays honestly refused in unowned CalcDb.Expressions (NotModelled.Function).
        // Once CalcDb.Expressions supports Now(), AdoFieldDescriptor.FromSelectColumn already resolves Now() to adDate(7).
        AdoRecordsetObject rsNow = CreateAdoRecordset();
        Ok(Put(rsNow, "CursorLocation", 2));
        CallResult nowOpen = Method(rsNow, "Open", "SELECT Now() AS e18 FROM T WHERE ID = 1", cn, 0, 1, 1);
        Assert.NotNull(nowOpen.Failure);
        Assert.Equal(FailureKind.NotModelled, nowOpen.Failure!.Kind);
        Assert.StartsWith("NotModelled.Function:", nowOpen.Failure.Description);

        // Calls 83 & 100: SELECT CDec(L) AS x1 FROM T WHERE ID = 1 -> stays cleanly refused in unowned CalcDb.Expressions.
        foreach ((int loc, int cur) in new[] { (2, 0), (3, 3) })
        {
            AdoRecordsetObject x2 = CreateAdoRecordset();
            Ok(Put(x2, "CursorLocation", loc));
            CallResult fail = Method(x2, "Open", "SELECT CDec(L) AS x1 FROM T WHERE ID = 1", cn, cur, 1, 1);
            Assert.NotNull(fail.Failure);
        }
    }

    [Fact]
    public void Ado_fields_refresh_ddl_3211_and_resync_insufficient_base_table_info()
    {
        // Replays evidence/r-ado-1b/fields-refresh-ddl call 28 and evidence/r-ado-1b/rs-cache-same-connection calls 48, 58, 68.
        AdoConnectionObject cn = OpenAdo(Ado1bMainAccdb);
        AdoRecordsetObject rk = CreateAdoRecordset();
        Ok(Put(rk, "CursorLocation", 2));
        Ok(Method(rk, "Open", "SELECT * FROM W ORDER BY ID", cn, 1, 1, 1));

        CallResult alter = Method(cn, "Execute", "ALTER TABLE W ADD COLUMN X TEXT(10)", Missing(), 129);
        Assert.NotNull(alter.Failure);
        Assert.Equal(unchecked((int)0x80040E14), alter.Failure!.Scode);
        Assert.Equal("Microsoft Access Database Engine", alter.Failure.Source);
        // Unrecorded held-table DDL column types remain refused honestly by name instead of being swallowed into 3211.
        AssertNotModelled(Method(cn, "Execute", "ALTER TABLE W ADD COLUMN X0 TEXT(0)", Missing(), 129), "NotModelled.DdlTableInUsePrecedence");
        AssertNotModelled(Method(cn, "Execute", "ALTER TABLE W ADD COLUMN XD DOUBLE", Missing(), 129), "NotModelled.DdlTableInUsePrecedence");
        DispatchObject errs = Recover<DispatchObject>(Ok(Get(cn, "Errors")));
        Assert.Equal(1, Int32Value(Ok(Get(errs, "Count"))));
        DispatchObject err0 = Item(errs, 0);
        Assert.Equal("3211", TextValue(Ok(Get(err0, "SQLState"))));
        Assert.Equal(-534316312, Int32Value(Ok(Get(err0, "NativeError"))));
        Ok(Method(rk, "Close"));

        // rs-cache-same-connection: c1 server keyset Resync -> 0x800A0CB3, c3 client Resync -> 0x80004005 Microsoft Cursor Engine
        AdoRecordsetObject c1 = CreateAdoRecordset();
        Ok(Put(c1, "CursorLocation", 2));
        Ok(Method(c1, "Open", "SELECT ID, S FROM T ORDER BY ID", cn, 1, 1, 1));
        CallResult r1 = Method(c1, "Resync", 3, 2);
        Assert.NotNull(r1.Failure);
        Assert.Equal(unchecked((int)0x800A0CB3), r1.Failure!.Scode);
        Assert.Equal("ADODB.Recordset", r1.Failure.Source);
        Ok(Method(c1, "Close"));

        AdoRecordsetObject c3 = CreateAdoRecordset();
        Ok(Put(c3, "CursorLocation", 3));
        Ok(Method(c3, "Open", "SELECT ID, S FROM T ORDER BY ID", cn, 3, 1, 1));
        CallResult r3 = Method(c3, "Resync", 3, 2);
        Assert.NotNull(r3.Failure);
        Assert.Equal(unchecked((int)0x80004005), r3.Failure!.Scode);
        Assert.Equal("Microsoft Cursor Engine", r3.Failure.Source);
        Assert.Equal("Insufficient base table information for updating or refreshing.", r3.Failure.Description);
        Assert.Equal(Guid.Empty, r3.Failure.ErrorInfoIid);
        DispatchObject errs3 = Recover<DispatchObject>(Ok(Get(cn, "Errors")));
        Assert.Equal(1, Int32Value(Ok(Get(errs3, "Count"))));
        DispatchObject e30 = Item(errs3, 0);
        Assert.Equal(1006, Int32Value(Ok(Get(e30, "NativeError"))));
        Assert.Equal("", TextValue(Ok(Get(e30, "SQLState"))));
        Ok(Method(c3, "Close"));
    }

    [Fact]
    public void Ado_named_arguments_repeated_parameter_returns_0x80020004_without_errorinfo()
    {
        // Replays evidence/r-q2-ado-misc/ado-named-arguments calls 12, 16, 27, 36, 46, 54 via IDispatch::GetIDsOfNames + Invoke.
        AdoConnectionObject cn = OpenAdo(Q2AdoMiscMainAccdb);
        nint cnPtr = DispatchPtrOf(cn);

        // Call #16: n2.Open(Source:=..., ActiveConnection:=cn, CursorType:=1, LockType:=1) named out-of-order
        AdoRecordsetObject n2 = CreateAdoRecordset();
        nint n2Ptr = DispatchPtrOf(n2);
        Assert.Equal(HResults.S_OK, InvokeNamed(
            n2Ptr,
            ["Open", "LockType", "CursorType", "ActiveConnection", "Source"],
            [1, 1, new DispatchWrap(cnPtr), "SELECT ID, Code, T, L FROM K ORDER BY ID"],
            out _));
        Assert.Equal(1, Int32Value(Ok(Get(n2, "State"))));
        Assert.Equal(10, Int32Value(Ok(Get(n2, "RecordCount"))));
        Ok(Method(n2, "Close"));

        // Call #27: unknown named parameter Sorce -> DISP_E_UNKNOWNNAME (0x80020006)
        Assert.Equal(unchecked((int)0x80020006), GetNamedIds(n2Ptr, ["Open", "Sorce"], out _));

        // Call #36: positional arg 0 + named Source -> 0x80020004 (DISP_E_PARAMNOTFOUND) without IErrorInfo
        AdoRecordsetObject n4 = CreateAdoRecordset();
        nint n4Ptr = DispatchPtrOf(n4);
        int hr36 = InvokeNamedWithPositional(
            n4Ptr,
            "Open",
            namedNames: ["Source"],
            argsBackwardsInDispParams: ["SELECT ID, Code, T, L FROM K ORDER BY ID", "SELECT ID, Code, T, L FROM K ORDER BY ID"],
            out bool err36);
        Assert.Equal(unchecked((int)0x80020004), hr36);
        Assert.False(err36);
        Assert.Equal(0, Int32Value(Ok(Get(n4, "State"))));

        // Call #54: Command.CreateParameter with positional arg 0 + named Name -> 0x80020004 without IErrorInfo
        AdoCommandObject cmd = CreateAdoCommand();
        nint cmdPtr = DispatchPtrOf(cmd);
        int hr54 = InvokeNamedWithPositional(
            cmdPtr,
            "CreateParameter",
            namedNames: ["Name"],
            argsBackwardsInDispParams: ["pY", "pX"],
            out bool err54);
        Assert.Equal(unchecked((int)0x80020004), hr54);
        Assert.False(err54);
    }

    [Fact]
    public void Ado_static_server_bookmark_failure_reads_empty_and_unknown_update_col_stays_refused()
    {
        // 1. evidence/r-q2-ado-misc/book-static-exchange calls 116..125:
        // server static cursor failed Bookmark put makes Field.Value return VT_EMPTY.
        AdoConnectionObject cn = OpenAdo(Q2AdoMiscMainAccdb);
        AdoRecordsetObject rs = CreateAdoRecordset();
        Ok(Put(rs, "CursorLocation", 2));
        Ok(Method(rs, "Open", "SELECT ID, T FROM K WHERE ID <= 6 ORDER BY ID", cn, 3, 1, 1));
        CallResult badBm = Put(rs, "Bookmark", 3.0);
        Assert.NotNull(badBm.Failure);
        Assert.Equal(unchecked((int)0x80040E0E), badBm.Failure!.Scode);
        Assert.Equal(VarType.Empty, ResultVarType(Ok(FieldValue(rs, "ID"))));
        Ok(Method(rs, "Close"));

        // 2. evidence/r-q2-ado-misc/ado-recordsaffected-byref calls 110 & 117:
        // UPDATE K SET NoSuchColumn = 1 returns ACE 0x80040E10 "No value given for one or more required parameters." without modifying recAff.
        NativeVariant recAff = new() { Vt = (ushort)VarType.Empty };
        NativeVariant byRefRecAff = new() { Vt = (ushort)(VarType.ByRef | VarType.Variant), Pointer = (nint)(&recAff) };
        int dispId = GetDispId(cn, "Execute");
        nint sqlBstr = Marshal.StringToBSTR("UPDATE K SET NoSuchColumn = 1");
        NativeVariant* args = stackalloc NativeVariant[3];
        args[2] = new NativeVariant { Vt = (ushort)VarType.Bstr, Pointer = sqlBstr };
        args[1] = byRefRecAff;
        args[0] = new NativeVariant { Vt = (ushort)VarType.I4, Int64 = 1 };
        try
        {
            var dp = new DispParams { Args = (nint)args, ArgCount = 3 };
            NativeVariant res = default;
            ExcepInfo ex = default;
            uint argErr = 0;
            int hr = cn.Invoke(dispId, (ushort)InvokeKind.Method, &dp, &res, &ex, &argErr);
            Assert.Equal(HResults.DISP_E_EXCEPTION, hr);
            int scode = ex.Scode;
            uint helpContext = ex.HelpContext;
            string src = Marshal.PtrToStringBSTR(ex.Source);
            string desc = Marshal.PtrToStringBSTR(ex.Description);
            if (ex.Source != 0) Marshal.FreeBSTR(ex.Source);
            if (ex.Description != 0) Marshal.FreeBSTR(ex.Description);
            if (ex.HelpFile != 0) Marshal.FreeBSTR(ex.HelpFile);
            if (NativeRoute.GetErrorInfo(0, out nint ei) == 0 && ei != 0) Marshal.Release(ei);
            Assert.Equal(unchecked((int)0x80040E10), scode);
            Assert.Equal("Microsoft Access Database Engine", src);
            Assert.Equal("No value given for one or more required parameters.", desc);
            Assert.Equal(5000000u, helpContext);
            Assert.Equal((ushort)VarType.Empty, recAff.Vt);
        }
        finally
        {
            Marshal.FreeBSTR(sqlBstr);
        }
    }

    private sealed record DispatchWrap(nint Pointer);

    private DBEngineObject OpenEngine()
    {
        if (_eng is not null)
        {
            return _eng;
        }
        nint ptr;
        Assert.Equal(HResults.S_OK, DBEngineObject.Create(_daoBinding, 0, ComIids.IDispatch, &ptr));
        _held.Add(ptr);
        _eng = ComObject.Recover<DBEngineObject>(ptr);
        return _eng;
    }

    private DatabaseObject OpenDao(string sha256, bool readOnly)
    {
        string path = CopyFixture(sha256, ".accdb");
        Ok(DatabaseObject.Open(OpenEngine().Engine.DefaultWorkspace, path, exclusive: false, readOnly: readOnly, out DatabaseObject? db));
        Assert.NotNull(db);
        _databases.Add(db!);
        return db!;
    }

    private AdoConnectionObject OpenAdo(string sha256)
    {
        string path = CopyFixture(sha256, ".accdb");
        nint ptr;
        Assert.Equal(HResults.S_OK, AdoConnectionObject.Create(_adoBinding, 0, ComIids.IDispatch, &ptr));
        _held.Add(ptr);
        AdoConnectionObject cn = ComObject.Recover<AdoConnectionObject>(ptr);
        Ok(Method(cn, "Open", $"Provider=Microsoft.ACE.OLEDB.12.0;Data Source={path};"));
        _adoConnections.Add(cn);
        return cn;
    }

    private AdoRecordsetObject CreateAdoRecordset()
    {
        nint ptr;
        Assert.Equal(HResults.S_OK, AdoRecordsetObject.Create(_adoBinding, 0, ComIids.IDispatch, &ptr));
        _held.Add(ptr);
        return ComObject.Recover<AdoRecordsetObject>(ptr);
    }

    private AdoCommandObject CreateAdoCommand()
    {
        nint ptr;
        Assert.Equal(HResults.S_OK, AdoCommandObject.Create(_adoBinding, 0, ComIids.IDispatch, &ptr));
        _held.Add(ptr);
        return ComObject.Recover<AdoCommandObject>(ptr);
    }

    private nint DispatchPtrOf(ComObject obj)
    {
        nint disp;
        Assert.Equal(HResults.S_OK, obj.QueryInterface(ComIids.IDispatch, &disp));
        _held.Add(disp);
        return disp;
    }

    private string CopyFixture(string sha256, string ext)
    {
        ArtifactStore store = ArtifactStore.ForRepository(RepoRoot());
        string path = TempPath(ext);
        File.WriteAllBytes(path, store.ReadAll(sha256));
        return path;
    }

    private string TempPath(string ext)
    {
        string path = Path.Combine(Path.GetTempPath(), $"calcdb-w22-{Guid.NewGuid():N}{ext}");
        _files.Add(path);
        return path;
    }

    private static string RepoRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "CalcDb.slnx")))
        {
            dir = dir.Parent;
        }
        return dir?.FullName ?? throw new DirectoryNotFoundException("CalcDb.slnx not found");
    }

    private static object Missing() => MissingArg.Value;

    private sealed class MissingArg
    {
        public static readonly MissingArg Value = new();
    }

    private nint Arg(object? value)
    {
        NativeVariant* cell = (NativeVariant*)NativeMemory.AllocZeroed((nuint)sizeof(NativeVariant));
        _cells.Add((nint)cell);
        switch (value)
        {
            case MissingArg:
                cell->Vt = (ushort)VarType.Error;
                cell->Int64 = unchecked((int)0x80020004);
                break;
            case null:
                cell->Vt = (ushort)VarType.Empty;
                break;
            case DBNull:
                cell->Vt = (ushort)VarType.Null;
                break;
            case bool b:
                cell->Vt = (ushort)VarType.Bool;
                cell->Int64 = b ? -1 : 0;
                break;
            case short s:
                cell->Vt = (ushort)VarType.I2;
                cell->Int64 = s;
                break;
            case int i:
                cell->Vt = (ushort)VarType.I4;
                cell->Int64 = i;
                break;
            case double d:
                cell->Vt = (ushort)VarType.R8;
                cell->Double = d;
                break;
            case string text:
                cell->Vt = (ushort)VarType.Bstr;
                cell->Pointer = Marshal.StringToBSTR(text);
                break;
            case byte[] bytes:
                nint psa = SafeArrays.Create(VarType.UI1, (0, bytes.Length));
                if (bytes.Length > 0)
                {
                    bytes.CopyTo(new Span<byte>(SafeArrays.ElementPointer(psa, 0), bytes.Length));
                }
                cell->Vt = (ushort)(VarType.Array | VarType.UI1);
                cell->Pointer = psa;
                break;
            case ComObject obj:
                nint disp;
                Assert.Equal(HResults.S_OK, obj.QueryInterface(ComIids.IDispatch, &disp));
                _held.Add(disp);
                cell->Vt = (ushort)VarType.Dispatch;
                cell->Pointer = disp;
                break;
            case DispatchWrap wrap:
                cell->Vt = (ushort)VarType.Dispatch;
                cell->Pointer = wrap.Pointer;
                break;
            default:
                throw new NotSupportedException(value.GetType().Name);
        }
        return (nint)cell;
    }

    private CallResult Call(DispatchObject target, string member, InvokeKind kind, params object?[] args)
    {
        MemberDecl decl = target.Declaration.Member(member, kind);
        nint[] cells = new nint[decl.Parameters.Count];
        for (int i = 0; i < args.Length; i++)
        {
            cells[i] = Arg(args[i]);
        }
        return target.Run(decl, cells);
    }

    private CallResult RunBstrBytes(DispatchObject target, string member, byte[] bstrBytes)
    {
        OwnedVariant ov = OwnedVariant.FromBstrBytes(bstrBytes);
        _ownedVariants.Add(ov);
        return target.Run(target.Declaration.Member(member, InvokeKind.Method), new[] { (nint)ov.Pointer });
    }

    private CallResult Get(DispatchObject target, string member, params object?[] args) => Call(target, member, InvokeKind.PropertyGet, args);
    private CallResult Put(DispatchObject target, string member, params object?[] args) => Call(target, member, InvokeKind.PropertyPut, args);
    private CallResult Method(DispatchObject target, string member, params object?[] args) => Call(target, member, InvokeKind.Method, args);

    private DispatchObject Item(CallResult collectionResult, object key) =>
        Item(Recover<DispatchObject>(Ok(collectionResult)), key);

    private DispatchObject Item(DispatchObject coll, object key) =>
        Recover<DispatchObject>(Ok(Get(coll, "Item", key)));

    private DispatchObject FieldObj(DispatchObject rs, string name) => Item(Get(rs, "Fields"), name);
    private CallResult FieldValue(DispatchObject rs, string name) => Get(FieldObj(rs, name), "Value");
    private CallResult PutField(DispatchObject rs, string name, object? value) => Put(FieldObj(rs, name), "Value", value);

    private static int GetDispId(DispatchObject target, string member) => target.Declaration.Member(member, InvokeKind.Method).MemberId;

    private static int GetNamedIds(nint dispPtr, string[] names, out int[] ids)
    {
        ids = new int[names.Length];
        nint* namePtrs = stackalloc nint[names.Length];
        for (int i = 0; i < names.Length; i++)
        {
            namePtrs[i] = Marshal.StringToHGlobalUni(names[i]);
        }
        try
        {
            Guid iidNull = Guid.Empty;
            fixed (int* pIds = ids)
            {
                nint* vt = *(nint**)dispPtr;
                return ((delegate* unmanaged<nint, Guid*, char**, uint, uint, int*, int>)vt[5])(
                    dispPtr, &iidNull, (char**)namePtrs, (uint)names.Length, 1033, pIds);
            }
        }
        finally
        {
            for (int i = 0; i < names.Length; i++)
            {
                Marshal.FreeHGlobal(namePtrs[i]);
            }
        }
    }

    private int InvokeNamed(nint dispPtr, string[] memberAndParamNames, object?[] values, out bool hasErrorInfo)
    {
        Assert.Equal(HResults.S_OK, GetNamedIds(dispPtr, memberAndParamNames, out int[] ids));
        NativeVariant* raw = stackalloc NativeVariant[values.Length];
        for (int i = 0; i < values.Length; i++)
        {
            raw[i] = *(NativeVariant*)Arg(values[i]);
        }
        int namedCount = ids.Length - 1;
        fixed (int* pNamed = &ids[1])
        {
            return RawInvoke(dispPtr, ids[0], raw, (uint)values.Length, pNamed, (uint)namedCount, out hasErrorInfo);
        }
    }

    private int InvokeNamedWithPositional(nint dispPtr, string member, string[] namedNames, object?[] argsBackwardsInDispParams, out bool hasErrorInfo)
    {
        string[] all = [member, .. namedNames];
        Assert.Equal(HResults.S_OK, GetNamedIds(dispPtr, all, out int[] ids));
        NativeVariant* raw = stackalloc NativeVariant[argsBackwardsInDispParams.Length];
        for (int i = 0; i < argsBackwardsInDispParams.Length; i++)
        {
            raw[i] = *(NativeVariant*)Arg(argsBackwardsInDispParams[i]);
        }
        fixed (int* pNamed = &ids[1])
        {
            return RawInvoke(dispPtr, ids[0], raw, (uint)argsBackwardsInDispParams.Length, pNamed, (uint)namedNames.Length, out hasErrorInfo);
        }
    }

    private static int RawInvoke(nint dispPtr, int dispId, NativeVariant* raw, uint argCount, int* namedIds, uint namedCount, out bool hasErrorInfo)
    {
        if (NativeRoute.GetErrorInfo(0, out nint stale) == 0 && stale != 0)
        {
            Marshal.Release(stale);
        }
        Guid iidNull = Guid.Empty;
        DispParams dp = new()
        {
            Args = (nint)raw,
            NamedArgs = (nint)namedIds,
            ArgCount = argCount,
            NamedArgCount = namedCount,
        };
        NativeVariant result = default;
        ExcepInfo ex = default;
        uint argErr = 0;
        nint* vt = *(nint**)dispPtr;
        int hr = ((delegate* unmanaged<nint, int, Guid*, uint, ushort, DispParams*, NativeVariant*, ExcepInfo*, uint*, int>)vt[6])(
            dispPtr, dispId, &iidNull, 1033, (ushort)InvokeKind.Method, &dp, &result, &ex, &argErr);
        hasErrorInfo = NativeRoute.GetErrorInfo(0, out nint ei) == 0 && ei != 0;
        if (hasErrorInfo)
        {
            Marshal.Release(ei);
        }
        if (ex.Source != 0) Marshal.FreeBSTR(ex.Source);
        if (ex.Description != 0) Marshal.FreeBSTR(ex.Description);
        if (ex.HelpFile != 0) Marshal.FreeBSTR(ex.HelpFile);
        return hr;
    }

    private static CallResult Ok(CallResult result)
    {
        Assert.True(result.Failure is null, result.Failure?.Description);
        return result;
    }

    private T Recover<T>(CallResult result) where T : ComObject
    {
        Assert.True(result.HasScalar && (VarType)result.Scalar.Vt == VarType.Dispatch, result.Failure?.Description);
        T obj = ComObject.Recover<T>(result.Scalar.Pointer);
        _held.Add(result.Scalar.Pointer);
        return obj;
    }

    private static void AssertDaoError(CallResult result, int number, string source, bool errorInfo)
    {
        Assert.NotNull(result.Failure);
        Assert.True(result.Failure!.Kind == FailureKind.Dao, $"Expected Dao {number}, got {result.Failure.Kind}: {result.Failure.Description}");
        Assert.Equal(number, result.Failure.Number);
        Assert.Equal(source, result.Failure.Source);
        Assert.Equal(errorInfo, result.Failure.PublishesErrorInfo);
    }

    private static void AssertNotModelled(CallResult result, string expectedName)
    {
        Assert.NotNull(result.Failure);
        Assert.Equal(FailureKind.NotModelled, result.Failure!.Kind);
        Assert.StartsWith(expectedName + ":", result.Failure.Description);
    }

    private static VarType ResultVarType(CallResult result) =>
        (VarType)(result.HasScalar ? result.Scalar.Vt : result.Value!.Pointer->Vt);

    private static int Int32Value(CallResult result)
    {
        Assert.True(result.HasScalar && (VarType)result.Scalar.Vt == VarType.I4);
        return unchecked((int)result.Scalar.Int64);
    }

    private static short Int16Value(CallResult result)
    {
        Assert.True(result.HasScalar && (VarType)result.Scalar.Vt == VarType.I2);
        return unchecked((short)result.Scalar.Int64);
    }

    private static byte ByteValue(CallResult result)
    {
        Assert.True(result.HasScalar && (VarType)result.Scalar.Vt == VarType.UI1);
        return (byte)result.Scalar.Int64;
    }

    private static bool BoolValue(CallResult result)
    {
        Assert.True(result.HasScalar && (VarType)result.Scalar.Vt == VarType.Bool);
        return (short)result.Scalar.Int64 != 0;
    }

    private static string TextValue(CallResult result)
    {
        NativeVariant v = result.HasScalar ? result.Scalar : *result.Value!.Pointer;
        Assert.Equal(VarType.Bstr, (VarType)v.Vt);
        string text = Bstrs.Chars(v.Pointer).ToString();
        result.ReleaseUntaken();
        return text;
    }

    private static byte[] ByteArrayValue(CallResult result)
    {
        NativeVariant v = result.HasScalar ? result.Scalar : *result.Value!.Pointer;
        Assert.Equal((ushort)(VarType.Array | VarType.UI1), v.Vt);
        if (v.Pointer == 0)
        {
            return [];
        }
        int count = SafeArrays.Shape(v.Pointer)[0].Count;
        byte[] copy = new byte[count];
        if (count > 0)
        {
            new ReadOnlySpan<byte>(SafeArrays.ElementPointer(v.Pointer, 0), count).CopyTo(copy);
        }
        result.ReleaseUntaken();
        result.Value?.Dispose();
        return copy;
    }
}
