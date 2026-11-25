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
    private const int DbVersion40 = 64;
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

    private static bool IsEmptyVariant(CallResult result)
    {
        Assert.True(result.Failure is null, result.Failure?.Description);
        bool isEmpty = (VarType)result.Scalar.Vt == VarType.Empty;
        result.ReleaseUntaken();
        return isEmpty;
    }

    private (DBEngineObject Engine, DatabaseObject Db, string Path) CreateSeededDb(string fileName = "qdef.accdb", int version = DbVersion120)
    {
        string path = Path.Combine(_dir, fileName);
        DBEngineObject eng = CreateEngine();
        DatabaseObject db = Recover<DatabaseObject>(Method(eng, "CreateDatabase", path, DbLangGeneral, version));
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

        // Out-of-stretch refusals: saved QueryDef.Name/Connect put, ReturnsRecords = False, undecided SQL shapes.
        CallResult namePut = Put(qSpace, "Name", "QRenamed");
        Assert.Equal(unchecked((int)0x80004001), namePut.Failure!.Scode);

        CallResult connPut = Put(qSpace, "Connect", "ODBC;DSN=Test;");
        Assert.Equal(unchecked((int)0x80004001), connPut.Failure!.Scode);

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

    [Theory]
    [InlineData("sql-put.accdb", DbVersion120)]
    [InlineData("sql-put.mdb", DbVersion40)]
    public void Saved_querydef_sql_put_updates_query_preserves_session_type_and_reopens_cleanly(string fileName, int version)
    {
        (DBEngineObject eng, DatabaseObject db, string path) = CreateSeededDb(fileName, version);
        Assert.Null(Method(db, "CreateQueryDef", "Q1", "SELECT ID FROM T").Failure);
        Assert.Null(Method(db, "Close").Failure);

        // Reopen and rewrite SELECT -> SELECT with WHERE; SQL returns verbatim text and OpenRecordset runs new SQL.
        DatabaseObject db2 = Recover<DatabaseObject>(Method(eng, "OpenDatabase", path, false, false));
        DefinitionCollection qdfs2 = Recover<DefinitionCollection>(Get(db2, "QueryDefs"));
        QueryDefObject q1 = Recover<QueryDefObject>(Get(qdfs2, "Item", "Q1"));
        Assert.Equal(0, IntOf(Get(q1, "Type")));
        Assert.Equal(1, IntOf(Get(Recover<DispatchObject>(Get(q1, "Fields")), "Count")));

        const string verbatimSelect = "SELECT ID, V FROM T WHERE V > 10";
        Assert.Null(Put(q1, "SQL", verbatimSelect).Failure);
        Assert.Equal(verbatimSelect, TextOf(Get(q1, "SQL")));
        Assert.Equal(0, IntOf(Get(q1, "Type")));
        Assert.Equal(2, IntOf(Get(Recover<DispatchObject>(Get(q1, "Fields")), "Count")));

        DispatchObject rs = Recover<DispatchObject>(Method(q1, "OpenRecordset", DbOpenSnapshot));
        Assert.Equal(2, IntOf(Get(Recover<DispatchObject>(Get(Recover<DispatchObject>(Get(rs, "Fields")), "Item", "ID")), "Value")));
        Assert.Equal(20, IntOf(Get(Recover<DispatchObject>(Get(Recover<DispatchObject>(Get(rs, "Fields")), "Item", "V")), "Value")));
        Assert.Null(Method(rs, "Close").Failure);

        // Same-session type change SELECT -> UPDATE keeps Type = 0 until reopen (r-cat-1b/qd-sql-change-type-tracking).
        const string verbatimUpdate = "UPDATE T SET V = 77 WHERE ID = 1";
        Assert.Null(Put(q1, "SQL", verbatimUpdate).Failure);
        Assert.Equal(verbatimUpdate, TextOf(Get(q1, "SQL")));
        Assert.Equal(0, IntOf(Get(q1, "Type")));
        Assert.Equal(0, IntOf(Get(Recover<DispatchObject>(Get(q1, "Fields")), "Count")));
        Assert.Null(Method(db2, "Close").Failure);

        // After reopen, Type is 48 (dbQAction / Update), SQL is re-rendered from MSysQueries, and Execute updates the row.
        DatabaseObject db3 = Recover<DatabaseObject>(Method(eng, "OpenDatabase", path, false, false));
        DefinitionCollection qdfs3 = Recover<DefinitionCollection>(Get(db3, "QueryDefs"));
        QueryDefObject q1Re = Recover<QueryDefObject>(Get(qdfs3, "Item", "Q1"));
        Assert.Equal(48, IntOf(Get(q1Re, "Type")));
        Assert.Contains("UPDATE T SET", TextOf(Get(q1Re, "SQL")), StringComparison.OrdinalIgnoreCase);
        Assert.Null(Method(q1Re, "Execute", DbFailOnError).Failure);
        Assert.Equal(1, IntOf(Get(q1Re, "RecordsAffected")));

        DispatchObject check = Recover<DispatchObject>(Method(db3, "OpenRecordset", "SELECT V FROM T WHERE ID = 1", DbOpenSnapshot));
        Assert.Equal(77, IntOf(Get(Recover<DispatchObject>(Get(Recover<DispatchObject>(Get(check, "Fields")), "Item", 0)), "Value")));
        Assert.Null(Method(check, "Close").Failure);
        Assert.Null(Method(db3, "Close").Failure);
    }

    [Fact]
    public void Saved_querydef_sql_put_parameter_identity_invalidation_and_error_sources_match_ace_records()
    {
        (DBEngineObject eng, DatabaseObject db, string path) = CreateSeededDb("sql-put-params.accdb");
        Assert.Null(Method(db, "CreateQueryDef", "Q1", "PARAMETERS pId Long; SELECT ID, V FROM T WHERE ID = pId").Failure);
        Assert.Null(Method(db, "Close").Failure);

        DatabaseObject db2 = Recover<DatabaseObject>(Method(eng, "OpenDatabase", path, false, false));
        DefinitionCollection qdfs2 = Recover<DefinitionCollection>(Get(db2, "QueryDefs"));
        QueryDefObject q1 = Recover<QueryDefObject>(Get(qdfs2, "Item", "Q1"));

        // Capture Parameters collection and pId COM pointers before rewrite (r-cat-1/held-sql-replace).
        (DefinitionCollection ps1, nint ps1Ptr) = RecoverWithPtr<DefinitionCollection>(Get(q1, "Parameters"));
        (ParameterObject pId1, nint pId1Ptr) = RecoverWithPtr<ParameterObject>(Get(ps1, "Item", "pId"));
        Assert.Equal(4, IntOf(Get(pId1, "Type")));
        Assert.Null(Put(pId1, "Value", 1).Failure);
        Assert.Equal(1, IntOf(Get(pId1, "Value")));

        // Syntax errors fail from DAO.QueryDef and leave SQL and Parameters intact (r-dao-1-err-1/query-sql-rewrite).
        CallResult badParam = Put(q1, "SQL", "PARAMETERS ; SELECT ID FROM T");
        Assert.Equal(3139, badParam.Failure!.Scode & 0xFFFF);
        Assert.Equal("DAO.QueryDef", badParam.Failure.Source);

        CallResult badLead = Put(q1, "SQL", "SELEC ID FROM T");
        Assert.Equal(3129, badLead.Failure!.Scode & 0xFFFF);
        Assert.Equal("DAO.QueryDef", badLead.Failure.Source);

        CallResult badAfter = Put(q1, "SQL", "SELECT ID FROM T; EXTRA");
        Assert.Equal(3142, badAfter.Failure!.Scode & 0xFFFF);
        Assert.Equal("DAO.QueryDef", badAfter.Failure.Source);

        CallResult badExpr = Put(q1, "SQL", "SELECT ID FROM T WHERE (ID > 1;");
        Assert.Equal(3075, badExpr.Failure!.Scode & 0xFFFF);
        Assert.Equal("DAO.QueryDef", badExpr.Failure.Source);

        Assert.Equal(1, IntOf(Get(pId1, "Value")));
        Assert.Equal(1, IntOf(Get(ps1, "Count")));

        // Rewrite SQL: pB is new, pId survives with changed declared type (Text(10) = 10).
        // ACE keeps Parameters COM identity, places surviving held pId first with its OLD Type (4) and Value (1), then new pB (4, Empty).
        const string sql2 = "PARAMETERS pB Long, pId Text(10); SELECT ID FROM T WHERE V = pB";
        Assert.Null(Put(q1, "SQL", sql2).Failure);
        Assert.Equal(sql2, TextOf(Get(q1, "SQL")));

        (DefinitionCollection ps2, nint ps2Ptr) = RecoverWithPtr<DefinitionCollection>(Get(q1, "Parameters"));
        Assert.Same(ps1, ps2);
        Assert.Equal(ps1Ptr, ps2Ptr);
        Assert.Equal(2, IntOf(Get(ps2, "Count")));

        (ParameterObject p0, nint p0Ptr) = RecoverWithPtr<ParameterObject>(Get(ps2, "Item", 0));
        ParameterObject p1 = Recover<ParameterObject>(Get(ps2, "Item", 1));
        Assert.Same(pId1, p0);
        Assert.Equal(pId1Ptr, p0Ptr);
        Assert.Equal("pId", TextOf(Get(p0, "Name")));
        Assert.Equal(4, IntOf(Get(p0, "Type")));
        Assert.Equal(1, IntOf(Get(p0, "Value")));
        Assert.Equal("pB", TextOf(Get(p1, "Name")));
        Assert.Equal(4, IntOf(Get(p1, "Type")));
        Assert.True(IsEmptyVariant(Get(p1, "Value")));

        // Drop all parameters: previously held pId1 and p1 fail 3420 from DAO.Parameter permanently (r-dao-1-err-1/query-sql-rewrite).
        Assert.Null(Put(q1, "SQL", "SELECT ID FROM T").Failure);
        Assert.Equal(0, IntOf(Get(ps1, "Count")));

        CallResult deadName = Get(pId1, "Name");
        Assert.Equal(3420, deadName.Failure!.Scode & 0xFFFF);
        Assert.Equal("DAO.Parameter", deadName.Failure.Source);

        CallResult deadVal = Get(p1, "Value");
        Assert.Equal(3420, deadVal.Failure!.Scode & 0xFFFF);
        Assert.Equal("DAO.Parameter", deadVal.Failure.Source);

        // Even after reintroducing pId in SQL, the previously dropped handle stays dead (3420).
        Assert.Null(Put(q1, "SQL", "PARAMETERS pB Long, pId Text(10); SELECT ID FROM T WHERE V = pB").Failure);
        CallResult stillDead = Get(pId1, "Name");
        Assert.Equal(3420, stillDead.Failure!.Scode & 0xFFFF);
        Assert.Equal("DAO.Parameter", stillDead.Failure.Source);
        Assert.Equal(2, IntOf(Get(ps1, "Count")));

        // Refusals: UNION and >32-char Expression LVAL refuse 0x80004001 without altering SQL.
        string beforeRefusal = TextOf(Get(q1, "SQL"));
        CallResult unionRefusal = Put(q1, "SQL", "SELECT ID FROM T UNION ALL SELECT V FROM T");
        Assert.Equal(unchecked((int)0x80004001), unionRefusal.Failure!.Scode);
        Assert.Equal(beforeRefusal, TextOf(Get(q1, "SQL")));

        CallResult lvalRefusal = Put(q1, "SQL", "SELECT ID FROM T WHERE W = '" + new string('a', 40) + "'");
        Assert.Equal(unchecked((int)0x80004001), lvalRefusal.Failure!.Scode);
        Assert.Equal(beforeRefusal, TextOf(Get(q1, "SQL")));

        Assert.Null(Method(db2, "Close").Failure);

        // After reopen, parameters appear in SQL-declared order [pB(4), pId(10)] (r-cat-1/held-sql-replace).
        DatabaseObject db3 = Recover<DatabaseObject>(Method(eng, "OpenDatabase", path, false, false));
        QueryDefObject q1Re = Recover<QueryDefObject>(Get(Recover<DefinitionCollection>(Get(db3, "QueryDefs")), "Item", "Q1"));
        DefinitionCollection psRe = Recover<DefinitionCollection>(Get(q1Re, "Parameters"));
        Assert.Equal(2, IntOf(Get(psRe, "Count")));
        Assert.Equal("pB", TextOf(Get(Recover<ParameterObject>(Get(psRe, "Item", 0)), "Name")));
        Assert.Equal(4, IntOf(Get(Recover<ParameterObject>(Get(psRe, "Item", 0)), "Type")));
        Assert.Equal("pId", TextOf(Get(Recover<ParameterObject>(Get(psRe, "Item", 1)), "Name")));
        Assert.Equal(10, IntOf(Get(Recover<ParameterObject>(Get(psRe, "Item", 1)), "Type")));
        Assert.Null(Method(db3, "Close").Failure);

        // Read-only database refuses SQL put cleanly.
        DatabaseObject dbRo = Recover<DatabaseObject>(Method(eng, "OpenDatabase", path, false, true));
        QueryDefObject qRo = Recover<QueryDefObject>(Get(Recover<DefinitionCollection>(Get(dbRo, "QueryDefs")), "Item", "Q1"));
        CallResult roPut = Put(qRo, "SQL", "SELECT ID FROM T");
        Assert.Equal(unchecked((int)0x80004001), roPut.Failure!.Scode);
        Assert.Null(Method(dbRo, "Close").Failure);
    }

    [Theory]
    [InlineData("delete-qdef.accdb", DbVersion120)]
    [InlineData("delete-qdef.mdb", DbVersion40)]
    public void QueryDefs_delete_and_delete_then_create_match_ace_lifetime_and_refusals(string fileName, int version)
    {
        (DBEngineObject eng, DatabaseObject db, string path) = CreateSeededDb(fileName, version);
        Assert.Null(Method(db, "CreateQueryDef", "Q1", "PARAMETERS pId Long; SELECT ID, V FROM T WHERE ID = pId").Failure);
        Assert.Null(Method(db, "Close").Failure);

        DatabaseObject db2 = Recover<DatabaseObject>(Method(eng, "OpenDatabase", path, false, false));
        DefinitionCollection qdfs2 = Recover<DefinitionCollection>(Get(db2, "QueryDefs"));
        Assert.Equal(1, IntOf(Get(qdfs2, "Count")));

        // Missing name and table name fail 3265 from DAO.QueryDefs.
        CallResult delMissing = Method(qdfs2, "Delete", "NoSuchQuery");
        Assert.Equal(3265, delMissing.Failure!.Scode & 0xFFFF);
        Assert.Equal("DAO.QueryDefs", delMissing.Failure.Source);

        CallResult delTable = Method(qdfs2, "Delete", "T");
        Assert.Equal(3265, delTable.Failure!.Scode & 0xFFFF);
        Assert.Equal("DAO.QueryDefs", delTable.Failure.Source);

        // Capture held QueryDef, Fields, Field, Parameters, Parameter, Properties before Delete (r-cat-1/held-querydef-lifetime).
        QueryDefObject heldQd = Recover<QueryDefObject>(Get(qdfs2, "Item", "Q1"));
        DispatchObject heldFields = Recover<DispatchObject>(Get(heldQd, "Fields"));
        DispatchObject heldField = Recover<DispatchObject>(Get(heldFields, "Item", 0));
        DispatchObject heldParams = Recover<DispatchObject>(Get(heldQd, "Parameters"));
        ParameterObject heldParam = Recover<ParameterObject>(Get(heldParams, "Item", 0));
        DispatchObject heldProps = Recover<DispatchObject>(Get(heldQd, "Properties"));

        // BeginTrans refuses Delete without deleting.
        Assert.Null(Method(eng, "BeginTrans").Failure);
        CallResult delInTxn = Method(qdfs2, "Delete", "Q1");
        Assert.Equal(unchecked((int)0x80004001), delInTxn.Failure!.Scode);
        Assert.Null(Method(eng, "Rollback").Failure);
        Assert.Equal(1, IntOf(Get(qdfs2, "Count")));

        // Delete Q1.
        Assert.Null(Method(qdfs2, "Delete", "Q1").Failure);
        Assert.Equal(0, IntOf(Get(qdfs2, "Count")));

        // Held QueryDef and all its child objects fail 3420 from their ACE sources.
        CallResult qdName = Get(heldQd, "Name");
        Assert.Equal(3420, qdName.Failure!.Scode & 0xFFFF);
        Assert.Equal("DAO.QueryDef", qdName.Failure.Source);

        CallResult qdSql = Get(heldQd, "SQL");
        Assert.Equal(3420, qdSql.Failure!.Scode & 0xFFFF);
        Assert.Equal("DAO.QueryDef", qdSql.Failure.Source);

        CallResult qdOpen = Method(heldQd, "OpenRecordset", DbOpenSnapshot);
        Assert.Equal(3420, qdOpen.Failure!.Scode & 0xFFFF);
        Assert.Equal("DAO.QueryDef", qdOpen.Failure.Source);

        CallResult fName = Get(heldField, "Name");
        Assert.Equal(3420, fName.Failure!.Scode & 0xFFFF);
        Assert.Equal("DAO.Field", fName.Failure.Source);

        CallResult fsCount = Get(heldFields, "Count");
        Assert.Equal(3420, fsCount.Failure!.Scode & 0xFFFF);

        CallResult pName = Get(heldParam, "Name");
        Assert.Equal(3420, pName.Failure!.Scode & 0xFFFF);
        Assert.Equal("DAO.Parameter", pName.Failure.Source);

        CallResult psCount = Get(heldParams, "Count");
        Assert.Equal(3420, psCount.Failure!.Scode & 0xFFFF);

        CallResult propsCount = Get(heldProps, "Count");
        Assert.Equal(3420, propsCount.Failure!.Scode & 0xFFFF);

        // Containers("Tables").Documents.Refresh drops Q1.
        DispatchObject tablesContainer = Recover<DispatchObject>(Get(Recover<DispatchObject>(Get(db2, "Containers")), "Item", "Tables"));
        DispatchObject docs = Recover<DispatchObject>(Get(tablesContainer, "Documents"));
        Assert.Null(Method(docs, "Refresh").Failure);
        CallResult docQ1 = Get(docs, "Item", "Q1");
        Assert.Equal(3265, docQ1.Failure!.Scode & 0xFFFF);

        // Recreate Q1 in the same session (delete-then-create); old heldQd remains dead (3420).
        QueryDefObject q1New = Recover<QueryDefObject>(Method(db2, "CreateQueryDef", "Q1", "SELECT V FROM T WHERE ID = 2"));
        Assert.Equal(1, IntOf(Get(qdfs2, "Count")));
        Assert.Equal(3420, Get(heldQd, "Name").Failure!.Scode & 0xFFFF);
        Assert.Equal("Q1", TextOf(Get(q1New, "Name")));
        Assert.Null(Method(db2, "Close").Failure);

        // After reopen, recreated Q1 runs and returns V = 20.
        DatabaseObject db3 = Recover<DatabaseObject>(Method(eng, "OpenDatabase", path, false, false));
        DefinitionCollection qdfs3 = Recover<DefinitionCollection>(Get(db3, "QueryDefs"));
        Assert.Equal(1, IntOf(Get(qdfs3, "Count")));
        DispatchObject rs = Recover<DispatchObject>(Method(db3, "OpenRecordset", "Q1", DbOpenSnapshot));
        Assert.Equal(20, IntOf(Get(Recover<DispatchObject>(Get(Recover<DispatchObject>(Get(rs, "Fields")), "Item", "V")), "Value")));
        Assert.Null(Method(rs, "Close").Failure);
        Assert.Null(Method(db3, "Close").Failure);

        // Read-only database refuses Delete cleanly.
        DatabaseObject dbRo = Recover<DatabaseObject>(Method(eng, "OpenDatabase", path, false, true));
        DefinitionCollection qdfsRo = Recover<DefinitionCollection>(Get(dbRo, "QueryDefs"));
        CallResult delRo = Method(qdfsRo, "Delete", "Q1");
        Assert.Equal(unchecked((int)0x80004001), delRo.Failure!.Scode);
        Assert.Null(Method(dbRo, "Close").Failure);
    }

    [Fact]
    public void Saved_querydef_sql_put_refetch_field_invalidation_passthrough_and_deferred_folding()
    {
        (DBEngineObject eng, DatabaseObject db, string path) = CreateSeededDb("sql-put-edge-cases.accdb");

        // Create Q1 and a PassThrough query QPass in one session, then immediately rewrite Q1 before Close (folds priorDeferred).
        (QueryDefObject q1Create, nint q1CreatePtr) = RecoverWithPtr<QueryDefObject>(Method(db, "CreateQueryDef", "Q1", "SELECT ID, V FROM T"));
        Assert.Null(Put(q1Create, "SQL", "SELECT ID, V, W FROM T").Failure);

        // Hold a QueryFieldObject at ordinal 2 (W), then shrink to 1 column; held field must refuse cleanly (not throw ArgumentOutOfRangeException).
        DispatchObject heldFields = Recover<DispatchObject>(Get(q1Create, "Fields"));
        DispatchObject heldFieldW = Recover<DispatchObject>(Get(heldFields, "Item", 2));
        Assert.Equal("W", TextOf(Get(heldFieldW, "Name")));

        Assert.Null(Put(q1Create, "SQL", "UPDATE T SET V = 55 WHERE ID = 2").Failure);
        CallResult staleFieldName = Get(heldFieldW, "Name");
        Assert.NotNull(staleFieldName.Failure);
        Assert.Equal(unchecked((int)0x80004001), staleFieldName.Failure!.Scode);

        // Release Q1 and re-fetch it from QueryDefs in the same session: re-fetched QueryDef must see the rewritten UPDATE plan and execute it!
        DefinitionCollection qdfs = Recover<DefinitionCollection>(Get(db, "QueryDefs"));
        ReleaseHeld(q1CreatePtr);
        QueryDefObject q1Refetched = Recover<QueryDefObject>(Get(qdfs, "Item", "Q1"));
        Assert.Equal("UPDATE T SET V = 55 WHERE ID = 2", TextOf(Get(q1Refetched, "SQL")));
        Assert.Equal(0, IntOf(Get(q1Refetched, "Type")));
        Assert.Equal(0, IntOf(Get(Recover<DispatchObject>(Get(q1Refetched, "Fields")), "Count")));
        // Note: Execute of Q1Refetched is guarded by the pending deferred commit only on data write (expected refusal before Close);
        // verify it does NOT fail with 3065 (Cannot execute a select query) which would indicate a stale SELECT SavedQuery!
        CallResult execPending = Method(q1Refetched, "Execute", DbFailOnError);
        Assert.NotNull(execPending.Failure);
        Assert.Equal(unchecked((int)0x80004001), execPending.Failure!.Scode);

        // Create a saved PassThrough query via draft + Append.
        (QueryDefObject qPassDraft, nint qPassDraftPtr) = RecoverWithPtr<QueryDefObject>(Method(db, "CreateQueryDef"));
        Assert.Null(Put(qPassDraft, "Name", "QPass").Failure);
        Assert.Null(Put(qPassDraft, "Connect", "ODBC;DSN=CalcDbProbeNoSuchDsn;").Failure);
        Assert.Null(Put(qPassDraft, "SQL", "SELECT 1").Failure);
        Assert.Null(Method(qdfs, "Append", new DispatchArg(qPassDraftPtr)).Failure);
        Assert.Null(Method(db, "Close").Failure);

        // Reopen: Q1 persisted as UPDATE (Type 48); QPass SQL put with backend T-SQL refuses by name (0x80004001, not 3129).
        DatabaseObject db2 = Recover<DatabaseObject>(Method(eng, "OpenDatabase", path, false, false));
        DefinitionCollection qdfs2 = Recover<DefinitionCollection>(Get(db2, "QueryDefs"));
        QueryDefObject q1Re = Recover<QueryDefObject>(Get(qdfs2, "Item", "Q1"));
        Assert.Equal(48, IntOf(Get(q1Re, "Type")));
        Assert.Null(Method(q1Re, "Execute", DbFailOnError).Failure);
        Assert.Equal(1, IntOf(Get(q1Re, "RecordsAffected")));

        QueryDefObject qPass = Recover<QueryDefObject>(Get(qdfs2, "Item", "QPass"));
        CallResult ptPut = Put(qPass, "SQL", "EXEC dbo.sp_foo");
        Assert.NotNull(ptPut.Failure);
        Assert.Equal(unchecked((int)0x80004001), ptPut.Failure!.Scode);

        // Unheld parameter value survives same-type SQL put, and type-changing rewrite refuses OpenRecordset cleanly.
        Assert.Null(Put(q1Re, "SQL", "PARAMETERS pId Long; SELECT ID, V FROM T WHERE ID = pId").Failure);
        (DefinitionCollection ps, nint _) = RecoverWithPtr<DefinitionCollection>(Get(q1Re, "Parameters"));
        (ParameterObject pTransient, nint pTransientPtr) = RecoverWithPtr<ParameterObject>(Get(ps, "Item", "pId"));
        Assert.Null(Put(pTransient, "Value", 2).Failure);
        ReleaseHeld(pTransientPtr);

        // Rewrite keeping pId as Long: unheld parameter's Value (2) survives!
        Assert.Null(Put(q1Re, "SQL", "PARAMETERS pId Long; SELECT V FROM T WHERE ID = pId").Failure);
        Assert.Equal(2, IntOf(Get(Recover<ParameterObject>(Get(ps, "Item", "pId")), "Value")));
        DispatchObject rs2 = Recover<DispatchObject>(Method(q1Re, "OpenRecordset", DbOpenSnapshot));
        Assert.Equal(55, IntOf(Get(Recover<DispatchObject>(Get(Recover<DispatchObject>(Get(rs2, "Fields")), "Item", "V")), "Value")));
        Assert.Null(Method(rs2, "Close").Failure);

        // Rewrite changing pId from Long -> Text(10): OpenRecordset refuses cleanly (0x80004001) instead of feeding VT_I4 into Text.
        Assert.Null(Put(q1Re, "SQL", "PARAMETERS pId Text(10); SELECT ID FROM T WHERE W = pId").Failure);
        CallResult mismatchOpen = Method(q1Re, "OpenRecordset", DbOpenSnapshot);
        Assert.NotNull(mismatchOpen.Failure);
        Assert.Equal(unchecked((int)0x80004001), mismatchOpen.Failure!.Scode);

        // Same-session SQL put followed immediately by Delete folds priorDeferred cleanly (on a fresh single-query DB).
        Assert.Null(Method(db2, "Close").Failure);

        (DBEngineObject eng2, DatabaseObject dbSolo, string pathSolo) = CreateSeededDb("sql-put-then-delete.accdb");
        Assert.Null(Method(dbSolo, "CreateQueryDef", "QSolo", "SELECT ID FROM T").Failure);
        Assert.Null(Method(dbSolo, "Close").Failure);

        DatabaseObject dbSolo2 = Recover<DatabaseObject>(Method(eng2, "OpenDatabase", pathSolo, false, false));
        DefinitionCollection qdfsSolo = Recover<DefinitionCollection>(Get(dbSolo2, "QueryDefs"));
        QueryDefObject qSolo = Recover<QueryDefObject>(Get(qdfsSolo, "Item", "QSolo"));
        Assert.Null(Put(qSolo, "SQL", "UPDATE T SET V = 1 WHERE ID = 1").Failure);
        Assert.Null(Method(qdfsSolo, "Delete", "QSolo").Failure);
        Assert.Equal(0, IntOf(Get(qdfsSolo, "Count")));
        Assert.Null(Method(dbSolo2, "Close").Failure);

        DatabaseObject dbSolo3 = Recover<DatabaseObject>(Method(eng2, "OpenDatabase", pathSolo, false, false));
        Assert.Equal(0, IntOf(Get(Recover<DefinitionCollection>(Get(dbSolo3, "QueryDefs")), "Count")));
        Assert.Null(Method(dbSolo3, "Close").Failure);
    }
}
