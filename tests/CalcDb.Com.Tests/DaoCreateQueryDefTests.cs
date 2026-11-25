using System.Runtime.InteropServices;

using CalcDb.Com.Com;
using CalcDb.Com.Dao;
using CalcDb.Com.Declarations;
using CalcDb.Com.Host;
using CalcDb.Foundation;
using CalcDb.Values;

namespace CalcDb.Com.Tests;

/// <summary>
/// DAO-level tests for <c>Database.CreateQueryDef</c>, draft <c>QueryDef</c> + <c>QueryDefs.Append</c>, and
/// temporary <c>CreateQueryDef("", sql)</c> (<c>#Temporary QueryDef#</c>) (W-07/saved-definition, task zsfm):
/// cites <c>r-q39-querydef-bytes</c>, <c>r-dao-1-err-1/draft-querydef</c>, <c>r-cat-1/temporary-querydef</c>,
/// <c>r-cat-1b/qd-view-*</c>, <c>r-cat-1b/qd-create-invalid-sql</c>, <c>r-def-cursor-dao/err-badcol-createquerydef</c>,
/// <c>r-fix-expr-sql/querydef-name-space-then-plain</c>, <c>r-cat-1/msys-add-objects</c>, and <c>r-q-wire/feff-accdb-param</c>.
/// </summary>
public sealed unsafe class DaoCreateQueryDefTests : IDisposable
{
    private const string DbLangGeneral = ";LANGID=0x0409;CP=1252;COUNTRY=0";
    private const int DbVersion120 = 128;
    private const int DbFailOnError = 128;
    private const int DbOpenSnapshot = 4;

    private readonly string _dir = Directory.CreateTempSubdirectory("calcdb-dao-qdef-").FullName;
    private readonly List<nint> _cells = [];
    private readonly List<nint> _held = [];
    private readonly EngineState _engine = new(new HostBinding(0xDA0_C7E8, Standalone: true, 0, 0, 0, 0, 0));

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

    private void ReleaseHeld(nint ptr)
    {
        int idx = _held.LastIndexOf(ptr);
        Assert.True(idx >= 0, "Expected COM pointer to be tracked in _held.");
        _held.RemoveAt(idx);
        Marshal.Release(ptr);
    }

    private sealed record DispatchArg(nint Pointer);

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
            case int i: v->Vt = (ushort)VarType.I4; v->Int64 = (uint)i; break;
            case short s: v->Vt = (ushort)VarType.I2; v->Int64 = (ushort)s; break;
            case bool b: v->Vt = (ushort)VarType.Bool; v->Int64 = b ? 0xFFFF : 0; break;
            case string text: v->Vt = (ushort)VarType.Bstr; v->Pointer = Marshal.StringToBSTR(text); break;
            case DispatchArg d: v->Vt = (ushort)VarType.Dispatch; v->Pointer = d.Pointer; break;
            case DBNull: v->Vt = (ushort)VarType.Null; break;
        }
        _cells.Add((nint)v);
        return (nint)v;
    }

    private CallResult Get(DispatchObject target, string member, params object?[] args) =>
        target.Run(target.Declaration.Member(member, InvokeKind.PropertyGet), [.. args.Select(Arg)]);

    private CallResult Put(DispatchObject target, string member, params object?[] args) =>
        target.Run(target.Declaration.Member(member, InvokeKind.PropertyPut), [.. args.Select(Arg)]);

    private CallResult Method(DispatchObject target, string member, params object?[] args) =>
        target.Run(target.Declaration.Member(member, InvokeKind.Method), [.. args.Select(Arg)]);

    private (T Obj, nint Ptr) RecoverWithPtr<T>(CallResult result) where T : ComObject
    {
        Assert.True(result.Failure is null, result.Failure?.Description);
        nint p = result.OwnsScalar ? result.Scalar.Pointer : result.Value!.Pointer->Pointer;
        _held.Add(p);
        return (ComObject.Recover<T>(p), p);
    }

    private T Recover<T>(CallResult result) where T : ComObject => RecoverWithPtr<T>(result).Obj;

    private static string TextOf(CallResult result)
    {
        Assert.True(result.Failure is null, result.Failure?.Description);
        string s = Marshal.PtrToStringBSTR(result.Scalar.Pointer);
        result.ReleaseUntaken();
        return s;
    }

    private static int IntOf(CallResult result)
    {
        Assert.True(result.Failure is null, result.Failure?.Description);
        int value = (VarType)result.Scalar.Vt == VarType.I2 ? unchecked((short)result.Scalar.Int64) : unchecked((int)result.Scalar.Int64);
        result.ReleaseUntaken();
        return value;
    }

    private (DBEngineObject Engine, DatabaseObject Db, string Path) CreateSeededDb(string fileName = "qdef.accdb")
    {
        string path = Path.Combine(_dir, fileName);
        DBEngineObject eng = CreateEngine();
        DatabaseObject db = Recover<DatabaseObject>(Method(eng, "CreateDatabase", path, DbLangGeneral, DbVersion120));
        Assert.Null(Method(db, "Execute", "CREATE TABLE T (ID LONG CONSTRAINT PK_T PRIMARY KEY, V LONG, W TEXT(40))", DbFailOnError).Failure);
        Assert.Null(Method(db, "Execute", "INSERT INTO T (ID, V, W) VALUES (1, 10, 'alpha')", DbFailOnError).Failure);
        Assert.Null(Method(db, "Execute", "INSERT INTO T (ID, V, W) VALUES (2, 20, 'beta')", DbFailOnError).Failure);
        return (eng, db, path);
    }

    [Fact]
    public void Named_create_returns_type_zero_in_session_and_true_type_after_reopen_across_families()
    {
        (DBEngineObject eng, DatabaseObject db, string path) = CreateSeededDb("families.accdb");

        // Named creates across single-table families (r-cat-1b/qd-view-*).
        QueryDefObject qSel = Recover<QueryDefObject>(Method(db, "CreateQueryDef", "QSel", "SELECT ID, V FROM T WHERE V > 10"));
        QueryDefObject qUpd = Recover<QueryDefObject>(Method(db, "CreateQueryDef", "QUpd", "UPDATE T SET V = 99 WHERE ID = 1"));
        QueryDefObject qDel = Recover<QueryDefObject>(Method(db, "CreateQueryDef", "QDel", "DELETE FROM T WHERE ID = 99"));
        QueryDefObject qIns = Recover<QueryDefObject>(Method(db, "CreateQueryDef", "QIns", "INSERT INTO T (ID, V, W) VALUES (3, 30, 'gamma')"));
        QueryDefObject qMake = Recover<QueryDefObject>(Method(db, "CreateQueryDef", "QMake", "SELECT ID, V INTO T2 FROM T"));
        QueryDefObject qDdl = Recover<QueryDefObject>(Method(db, "CreateQueryDef", "QDdl", "CREATE TABLE T3 (A LONG)"));
        QueryDefObject qUni = Recover<QueryDefObject>(Method(db, "CreateQueryDef", "QUni", "SELECT ID FROM T UNION ALL SELECT V FROM T"));
        QueryDefObject qXtab = Recover<QueryDefObject>(Method(db, "CreateQueryDef", "QXtab", "TRANSFORM Count(ID) AS Cnt SELECT V FROM T GROUP BY V PIVOT W"));

        // Draft pass-through with Connect and ReturnsRecords = True (r-cat-1b/qd-view-passthrough, r-q39/passthrough-accdb).
        (QueryDefObject qPass, nint qPassPtr) = RecoverWithPtr<QueryDefObject>(Method(db, "CreateQueryDef"));
        Assert.Null(Put(qPass, "Name", "QPass").Failure);
        Assert.Null(Put(qPass, "Connect", "ODBC;DSN=CalcDbProbeNoSuchDsn;").Failure);
        Assert.Null(Put(qPass, "ReturnsRecords", true).Failure);
        Assert.Null(Put(qPass, "SQL", "SELECT 1").Failure);
        DefinitionCollection qdfs = Recover<DefinitionCollection>(Get(db, "QueryDefs"));
        Assert.Null(Method(qdfs, "Append", new DispatchArg(qPassPtr)).Failure);

        // In the creating session every family reads Type == 0 (dbQSelect) (r-cat-1b/qd-view-*).
        foreach (QueryDefObject q in new[] { qSel, qUpd, qDel, qIns, qMake, qDdl, qUni, qXtab, qPass })
        {
            Assert.Equal(0, IntOf(Get(q, "Type")));
        }

        // Non-row-returning families expose Fields.Count == 0; DDL and PassThrough expose Parameters.Count == 0.
        foreach (QueryDefObject q in new[] { qUpd, qDel, qIns, qMake, qDdl, qPass })
        {
            DefinitionCollection fields = Recover<DefinitionCollection>(Get(q, "Fields"));
            Assert.Equal(0, IntOf(Get(fields, "Count")));
        }
        foreach (QueryDefObject q in new[] { qDdl, qPass })
        {
            DefinitionCollection parms = Recover<DefinitionCollection>(Get(q, "Parameters"));
            Assert.Equal(0, IntOf(Get(parms, "Count")));
        }

        // Containers("Tables").Documents.Refresh() sees newly created QueryDefs (r-cat-1/msys-add-objects).
        DefinitionCollection containers = Recover<DefinitionCollection>(Get(db, "Containers"));
        ContainerObject tables = Recover<ContainerObject>(Get(containers, "Item", "Tables"));
        DefinitionCollection docs = Recover<DefinitionCollection>(Get(tables, "Documents"));
        Assert.Null(Method(docs, "Refresh").Failure);
        Assert.Null(Get(docs, "Item", "QSel").Failure);
        Assert.Null(Get(docs, "Item", "QPass").Failure);

        Assert.Null(Method(db, "Close").Failure);

        // Reopen and verify true QueryDef.Type for every family (r-cat-1b/qd-view-*).
        DatabaseObject db2 = Recover<DatabaseObject>(Method(eng, "OpenDatabase", path, false, false));
        DefinitionCollection qdfs2 = Recover<DefinitionCollection>(Get(db2, "QueryDefs"));
        Assert.Equal(9, IntOf(Get(qdfs2, "Count")));
        Assert.Equal(0, IntOf(Get(Recover<QueryDefObject>(Get(qdfs2, "Item", "QSel")), "Type")));
        Assert.Equal(48, IntOf(Get(Recover<QueryDefObject>(Get(qdfs2, "Item", "QUpd")), "Type")));
        Assert.Equal(32, IntOf(Get(Recover<QueryDefObject>(Get(qdfs2, "Item", "QDel")), "Type")));
        Assert.Equal(64, IntOf(Get(Recover<QueryDefObject>(Get(qdfs2, "Item", "QIns")), "Type")));
        Assert.Equal(80, IntOf(Get(Recover<QueryDefObject>(Get(qdfs2, "Item", "QMake")), "Type")));
        Assert.Equal(96, IntOf(Get(Recover<QueryDefObject>(Get(qdfs2, "Item", "QDdl")), "Type")));
        Assert.Equal(128, IntOf(Get(Recover<QueryDefObject>(Get(qdfs2, "Item", "QUni")), "Type")));
        Assert.Equal(16, IntOf(Get(Recover<QueryDefObject>(Get(qdfs2, "Item", "QXtab")), "Type")));
        Assert.Equal(112, IntOf(Get(Recover<QueryDefObject>(Get(qdfs2, "Item", "QPass")), "Type")));
        Assert.Equal("ODBC;DSN=CalcDbProbeNoSuchDsn;", TextOf(Get(Recover<QueryDefObject>(Get(qdfs2, "Item", "QPass")), "Connect")));
        Assert.Null(Method(db2, "Close").Failure);
    }

    [Fact]
    public void Draft_append_preserves_com_identity_writes_nothing_before_append_and_persists_after_close()
    {
        (DBEngineObject eng, DatabaseObject db, string path) = CreateSeededDb("draft.accdb");
        Assert.Null(Method(db, "Close").Failure);
        byte[] before = File.ReadAllBytes(path);

        DatabaseObject db2 = Recover<DatabaseObject>(Method(eng, "OpenDatabase", path, false, false));
        DefinitionCollection qdfs = Recover<DefinitionCollection>(Get(db2, "QueryDefs"));
        Assert.Equal(0, IntOf(Get(qdfs, "Count")));

        // Draft QueryDef writes zero bytes until Append (r-q39/draft-append-accdb, r-dao-1-err-1/draft-querydef).
        (QueryDefObject draft, nint draftPtr) = RecoverWithPtr<QueryDefObject>(Method(db2, "CreateQueryDef"));
        Assert.Equal("", TextOf(Get(draft, "Name")));
        Assert.Equal("", TextOf(Get(draft, "SQL")));
        Assert.Equal("", TextOf(Get(draft, "Connect")));
        Assert.Null(Put(draft, "Name", "QDraft").Failure);
        Assert.Null(Put(draft, "SQL", "SELECT ID, V FROM T WHERE ID = 2").Failure);
        Assert.Equal("QDraft", TextOf(Get(draft, "Name")));
        Assert.Equal("SELECT ID, V FROM T WHERE ID = 2", TextOf(Get(draft, "SQL")));
        Assert.Equal(0, IntOf(Get(qdfs, "Count")));
        Assert.Equal(before, File.ReadAllBytes(path));

        // Append writes the query and preserves the exact same QueryDefObject COM wrapper (r-dao-1-err-1/draft-querydef).
        Assert.Null(Method(qdfs, "Append", new DispatchArg(draftPtr)).Failure);
        Assert.Equal(1, IntOf(Get(qdfs, "Count")));
        (QueryDefObject looked, nint lookedPtr) = RecoverWithPtr<QueryDefObject>(Get(qdfs, "Item", "QDraft"));
        Assert.Same(draft, looked);
        Assert.Equal(draftPtr, lookedPtr);
        Assert.Contains("SELECT ID, V", TextOf(Get(draft, "SQL")), StringComparison.Ordinal);

        Assert.Null(Method(db2, "Close").Failure);

        // Reopen and execute the saved query via OpenRecordset.
        DatabaseObject db3 = Recover<DatabaseObject>(Method(eng, "OpenDatabase", path, false, false));
        DispatchObject rs = Recover<DispatchObject>(Method(db3, "OpenRecordset", "QDraft", DbOpenSnapshot));
        DispatchObject flds = Recover<DispatchObject>(Get(rs, "Fields"));
        Assert.Equal(2, IntOf(Get(Recover<DispatchObject>(Get(flds, "Item", "ID")), "Value")));
        Assert.Equal(20, IntOf(Get(Recover<DispatchObject>(Get(flds, "Item", "V")), "Value")));
        Assert.Null(Method(rs, "Close").Failure);
        Assert.Null(Method(db3, "Close").Failure);
    }

    [Fact]
    public void Temporary_querydef_writes_no_bytes_rejects_rename_and_append_and_runs_select_and_action()
    {
        (DBEngineObject eng, DatabaseObject db, string path) = CreateSeededDb("temp.accdb");
        Assert.Null(Method(db, "Close").Failure);
        byte[] before = File.ReadAllBytes(path);

        DatabaseObject db2 = Recover<DatabaseObject>(Method(eng, "OpenDatabase", path, false, false));
        DefinitionCollection qdfs = Recover<DefinitionCollection>(Get(db2, "QueryDefs"));

        // Temporary SELECT QueryDef: #Temporary QueryDef#, raw SQL verbatim, OpenRecordset works (r-cat-1/temporary-querydef).
        (QueryDefObject tq, nint tqPtr) = RecoverWithPtr<QueryDefObject>(Method(db2, "CreateQueryDef", "", "SELECT ID, W FROM T WHERE ID = 1"));
        Assert.Equal("#Temporary QueryDef#", TextOf(Get(tq, "Name")));
        Assert.Equal(0, IntOf(Get(tq, "Type")));
        Assert.Equal("SELECT ID, W FROM T WHERE ID = 1", TextOf(Get(tq, "SQL")));
        Assert.Equal(0, IntOf(Get(qdfs, "Count")));

        // Renaming fails 3219 from DAO.QueryDef; appending fails 3219 from DAO.QueryDefs (r-dao-1-err-1/draft-querydef).
        CallResult renameErr = Put(tq, "Name", "QNo");
        Assert.NotNull(renameErr.Failure);
        Assert.Equal(3219, renameErr.Failure!.Scode & 0xFFFF);
        Assert.Equal("DAO.QueryDef", renameErr.Failure.Source);

        CallResult appendErr = Method(qdfs, "Append", new DispatchArg(tqPtr));
        Assert.NotNull(appendErr.Failure);
        Assert.Equal(3219, appendErr.Failure!.Scode & 0xFFFF);
        Assert.Equal("DAO.QueryDefs", appendErr.Failure.Source);

        DispatchObject rs = Recover<DispatchObject>(Method(tq, "OpenRecordset", DbOpenSnapshot));
        DispatchObject flds = Recover<DispatchObject>(Get(rs, "Fields"));
        Assert.Equal("alpha", TextOf(Get(Recover<DispatchObject>(Get(flds, "Item", "W")), "Value")));
        Assert.Null(Method(rs, "Close").Failure);

        // SQL put works on temporary QueryDef and rebinds Parameters.
        Assert.Null(Put(tq, "SQL", "PARAMETERS pId Long; SELECT ID, W FROM T WHERE ID = pId").Failure);
        DefinitionCollection parms = Recover<DefinitionCollection>(Get(tq, "Parameters"));
        Assert.Equal(1, IntOf(Get(parms, "Count")));
        DispatchObject pId = Recover<DispatchObject>(Get(parms, "Item", "pId"));
        Assert.Null(Put(pId, "Value", 2).Failure);
        DispatchObject rs2 = Recover<DispatchObject>(Method(tq, "OpenRecordset", DbOpenSnapshot));
        DispatchObject flds2 = Recover<DispatchObject>(Get(rs2, "Fields"));
        Assert.Equal("beta", TextOf(Get(Recover<DispatchObject>(Get(flds2, "Item", "W")), "Value")));
        Assert.Null(Method(rs2, "Close").Failure);

        Assert.Null(Method(tq, "Close").Failure);
        Assert.Null(Method(db2, "Close").Failure);

        // Zero bytes written by temporary SELECT QueryDefs (r-q39/temp-querydef-accdb).
        Assert.Equal(before, File.ReadAllBytes(path));

        // Temporary parameterized INSERT QueryDef executes and writes the row (r-q-wire/feff-accdb-param).
        DatabaseObject db3 = Recover<DatabaseObject>(Method(eng, "OpenDatabase", path, false, false));
        QueryDefObject tIns = Recover<QueryDefObject>(Method(db3, "CreateQueryDef", "", "PARAMETERS pId Long, pW Text(40); INSERT INTO T (ID, V, W) VALUES (pId, 30, pW)"));
        DefinitionCollection insParms = Recover<DefinitionCollection>(Get(tIns, "Parameters"));
        Assert.Null(Put(Recover<DispatchObject>(Get(insParms, "Item", "pId")), "Value", 3).Failure);
        Assert.Null(Put(Recover<DispatchObject>(Get(insParms, "Item", "pW")), "Value", "gamma").Failure);
        Assert.Null(Method(tIns, "Execute", DbFailOnError).Failure);
        Assert.Equal(1, IntOf(Get(tIns, "RecordsAffected")));
        Assert.Equal(0, IntOf(Get(Recover<DefinitionCollection>(Get(db3, "QueryDefs")), "Count")));
        Assert.Null(Method(db3, "Close").Failure);
    }

    [Fact]
    public void Error_codes_trailing_spaces_missing_tables_and_named_refusals_match_ace_records()
    {
        (_, DatabaseObject db, _) = CreateSeededDb("errors.accdb");

        // Missing table succeeds at CreateQueryDef time (r-def-cursor-dao/err-badcol-createquerydef).
        Assert.Null(Method(db, "CreateQueryDef", "QMissing", "SELECT BadCol FROM NoSuchTable").Failure);

        // Trailing space preserved in Name, collides with plain name (3012 DAO.QueryDefs), and found by OpenRecordset("q", 4) (r-fix-expr-sql/querydef-name-space-then-plain).
        QueryDefObject qSpace = Recover<QueryDefObject>(Method(db, "CreateQueryDef", "QSp ", "SELECT ID FROM T"));
        Assert.Equal("QSp ", TextOf(Get(qSpace, "Name")));

        CallResult dupSpace = Method(db, "CreateQueryDef", "QSp", "SELECT ID FROM T");
        Assert.Equal(3012, dupSpace.Failure!.Scode & 0xFFFF);
        Assert.Equal("DAO.QueryDefs", dupSpace.Failure.Source);

        CallResult dupTable = Method(db, "CreateQueryDef", "t", "SELECT ID FROM T");
        Assert.Equal(3012, dupTable.Failure!.Scode & 0xFFFF);
        Assert.Equal("DAO.QueryDefs", dupTable.Failure.Source);

        DispatchObject rsSp = Recover<DispatchObject>(Method(db, "OpenRecordset", "QSp", DbOpenSnapshot));
        Assert.Null(Method(rsSp, "Close").Failure);

        // Invalid name -> 3125 DAO.QueryDefs (r-dao-1-err-1/err-invalid-names: punctuation and control chars < 0x20).
        CallResult badName = Method(db, "CreateQueryDef", "bad!name", "SELECT ID FROM T");
        Assert.Equal(3125, badName.Failure!.Scode & 0xFFFF);
        Assert.Equal("DAO.QueryDefs", badName.Failure.Source);

        CallResult badCtrl = Method(db, "CreateQueryDef", "bad\tname", "SELECT ID FROM T");
        Assert.Equal(3125, badCtrl.Failure!.Scode & 0xFFFF);
        Assert.Equal("DAO.QueryDefs", badCtrl.Failure.Source);

        // Name > 64 chars, 1-arg CreateQueryDef(name), and temporary non-SELECT Type refuse by name (F6, F8).
        CallResult longName = Method(db, "CreateQueryDef", new string('Q', 65), "SELECT ID FROM T");
        Assert.Equal(unchecked((int)0x80004001), longName.Failure!.Scode);

        CallResult oneArgNamed = Method(db, "CreateQueryDef", "QNoSql");
        Assert.Equal(unchecked((int)0x80004001), oneArgNamed.Failure!.Scode);

        QueryDefObject tempUpd = Recover<QueryDefObject>(Method(db, "CreateQueryDef", "", "UPDATE T SET V = 1"));
        CallResult tempUpdType = Get(tempUpd, "Type");
        Assert.Equal(unchecked((int)0x80004001), tempUpdType.Failure!.Scode);

        // NFC/NFD + trailing-space collision -> 3012 DAO.QueryDefs (r-cat-1/names, r-fix-expr-sql/querydef-name-space-then-plain).
        Assert.Null(Method(db, "CreateQueryDef", "Q\u00E9 ", "SELECT ID FROM T").Failure);
        CallResult dupComposed = Method(db, "CreateQueryDef", "Qe\u0301", "SELECT ID FROM T");
        Assert.Equal(3012, dupComposed.Failure!.Scode & 0xFFFF);
        Assert.Equal("DAO.QueryDefs", dupComposed.Failure.Source);

        // Syntax errors -> 3129 / 3142 / 3075 from DAO.QueryDefs (r-cat-1b/qd-create-invalid-sql).
        CallResult badLead = Method(db, "CreateQueryDef", "QBad1", "SELEC ID FROM T");
        Assert.Equal(3129, badLead.Failure!.Scode & 0xFFFF);
        Assert.Equal("DAO.QueryDefs", badLead.Failure.Source);

        CallResult emptySql = Method(db, "CreateQueryDef", "QBad2", "");
        Assert.Equal(3129, emptySql.Failure!.Scode & 0xFFFF);
        Assert.Equal("DAO.QueryDefs", emptySql.Failure.Source);

        CallResult afterEnd = Method(db, "CreateQueryDef", "QBad3", "SELECT ID FROM T; EXTRA");
        Assert.Equal(3142, afterEnd.Failure!.Scode & 0xFFFF);
        Assert.Equal("DAO.QueryDefs", afterEnd.Failure.Source);

        CallResult badExpr = Method(db, "CreateQueryDef", "QBad4", "SELECT ID FROM T WHERE (ID > 1;");
        Assert.Equal(3075, badExpr.Failure!.Scode & 0xFFFF);
        Assert.Equal("DAO.QueryDefs", badExpr.Failure.Source);

        // Out-of-stretch refusals: saved QueryDef.SQL put, QueryDefs.Delete, ReturnsRecords = False, undecided SQL shapes.
        CallResult sqlPut = Put(qSpace, "SQL", "SELECT V FROM T");
        Assert.Equal(unchecked((int)0x80004001), sqlPut.Failure!.Scode);

        DefinitionCollection qdfs = Recover<DefinitionCollection>(Get(db, "QueryDefs"));
        CallResult del = Method(qdfs, "Delete", "QSp ");
        Assert.Equal(unchecked((int)0x80004001), del.Failure!.Scode);

        QueryDefObject draft = Recover<QueryDefObject>(Method(db, "CreateQueryDef"));
        CallResult rrFalse = Put(draft, "ReturnsRecords", false);
        Assert.Equal(unchecked((int)0x80004001), rrFalse.Failure!.Scode);

        CallResult joinRefused = Method(db, "CreateQueryDef", "QJoin", "SELECT T.ID FROM T INNER JOIN T AS T2 ON T.ID = T2.ID");
        Assert.Equal(unchecked((int)0x80004001), joinRefused.Failure!.Scode);

        Assert.Null(Method(db, "Close").Failure);
    }

    [Fact]
    public void CreateQueryDef_before_reading_QueryDefs_preserves_identity_and_session_type_and_guards_interleaved_writes()
    {
        (DBEngineObject eng, DatabaseObject db, string path) = CreateSeededDb("identity-and-deferred.accdb");
        Assert.Null(Method(db, "Close").Failure);

        // Reopen fresh without touching db.QueryDefs before CreateQueryDef (F1).
        DatabaseObject db2 = Recover<DatabaseObject>(Method(eng, "OpenDatabase", path, false, false));
        (QueryDefObject created, nint createdPtr) = RecoverWithPtr<QueryDefObject>(Method(db2, "CreateQueryDef", "QUpd", "UPDATE T SET V = V + 1 WHERE ID = 1"));
        Assert.Equal(0, IntOf(Get(created, "Type")));

        DefinitionCollection qdfs = Recover<DefinitionCollection>(Get(db2, "QueryDefs"));
        (QueryDefObject looked, nint lookedPtr) = RecoverWithPtr<QueryDefObject>(Get(qdfs, "Item", "QUpd"));
        Assert.Same(created, looked);
        Assert.Equal(createdPtr, lookedPtr);
        Assert.Equal(0, IntOf(Get(looked, "Type")));

        // Release both refs to QUpd; re-fetching in the same session still reports Type = 0 (r-cat-1b/qd-view-update).
        ReleaseHeld(lookedPtr);
        ReleaseHeld(createdPtr);
        QueryDefObject refetched = Recover<QueryDefObject>(Get(qdfs, "Item", "QUpd"));
        Assert.Equal(0, IntOf(Get(refetched, "Type")));

        // While the deferred 5th commit is pending, BeginTrans and interleaved DML/DDL writes refuse cleanly (F5).
        CallResult beginTrans = Method(eng, "BeginTrans");
        Assert.NotNull(beginTrans.Failure);
        Assert.Equal(unchecked((int)0x80004001), beginTrans.Failure!.Scode);

        CallResult dml = Method(db2, "Execute", "UPDATE T SET V = 99 WHERE ID = 1", DbFailOnError);
        Assert.NotNull(dml.Failure);
        Assert.Equal(unchecked((int)0x80004001), dml.Failure!.Scode);

        CallResult ddl = Method(db2, "Execute", "CREATE TABLE U (ID LONG)", DbFailOnError);
        Assert.NotNull(ddl.Failure);
        Assert.Equal(unchecked((int)0x80004001), ddl.Failure!.Scode);

        Assert.Null(Method(db2, "Close").Failure);

        // After Close + reopen, Type reads 48 (dbQAction / Update) and Execute runs the saved query (also with trailing space).
        DatabaseObject db3 = Recover<DatabaseObject>(Method(eng, "OpenDatabase", path, false, false));
        DefinitionCollection qdfs3 = Recover<DefinitionCollection>(Get(db3, "QueryDefs"));
        Assert.Equal(48, IntOf(Get(Recover<QueryDefObject>(Get(qdfs3, "Item", "QUpd")), "Type")));
        Assert.Null(Method(db3, "Execute", "QUpd ", DbFailOnError).Failure);
        Assert.Null(Method(db3, "Close").Failure);
    }
}
