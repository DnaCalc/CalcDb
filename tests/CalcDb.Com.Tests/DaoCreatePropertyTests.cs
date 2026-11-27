using System.Buffers.Binary;
using System.Runtime.InteropServices;

using CalcDb.Com.Com;
using CalcDb.Com.Dao;
using CalcDb.Com.Declarations;
using CalcDb.Com.Host;
using CalcDb.Foundation;
using CalcDb.Values;

namespace CalcDb.Com.Tests;

/// <summary>
/// COM DAO &amp; ADO tests for:
/// <list type="bullet">
/// <item>DAO <c>CreateProperty</c> / <c>Properties.Append</c> / <c>Property.Value</c> put / <c>Properties.Delete</c>
/// lifecycle, LVAL persistence across reopen, held-object 3420 invalidation, and 0-byte error matrix across Database,
/// TableDef, and Field on ACCDB &amp; Jet 4 MDB (<c>r-q51-codecs-props-compact/prop-db-lifecycle-accdb</c>,
/// <c>prop-db-lifecycle-mdb</c>, <c>prop-tdf-lifecycle-accdb</c>, <c>prop-tdf-lifecycle-mdb</c>,
/// <c>prop-fld-lifecycle-accdb</c>, <c>prop-fld-lifecycle-mdb</c>, <c>prop-errors-accdb</c>, <c>prop-errors-mdb</c>).</item>
/// <item><c>CreateQueryDef</c> deferred commit followed immediately by SQL <c>CREATE TABLE</c>, DAO
/// <c>TableDefs.Append</c>, or SQL <c>DROP TABLE</c> on ACCDB &amp; Jet 4 MDB
/// (<c>r-q54-excl-ddl-ix-cmd/qd-deferred-then-ddl-accdb</c>, <c>qd-deferred-then-ddl-mdb</c>).</item>
/// <item>ADO <c>CREATE VIEW</c> / <c>DROP VIEW</c> <c>NeedsCloseCommit</c> page-0 <c>0xE02 +1</c> at <c>Close()</c>
/// (<c>r-q50-jk44-ddl-vis/ddl-view-create-drop-accdb</c>, <c>ddl-view-create-drop-mdb</c>).</item>
/// </list>
/// </summary>
public sealed unsafe class DaoCreatePropertyTests : IDisposable
{
    private const string DbLangGeneral = ";LANGID=0x0409;CP=1252;COUNTRY=0";
    private const int DbVersion40 = 64;
    private const int DbVersion120 = 128;
    private const int DbFailOnError = 128;

    private readonly string _dir = Directory.CreateTempSubdirectory("calcdb-dao-prop-").FullName;
    private readonly List<nint> _cells = [];
    private readonly List<nint> _held = [];
    private readonly Dictionary<ComObject, nint> _ptrs = new(ReferenceEqualityComparer.Instance);
    private readonly EngineState _engine = new(new HostBinding(0xDA0_051B, Standalone: true, 0, 0, 0, 0, 0));

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

    private DBEngineObject CreateEngine()
    {
        nint engine;
        Assert.Equal(0, DBEngineObject.Create(_engine.Binding, 0, NativeRoute.IidIDispatch, &engine));
        _held.Add(engine);
        return ComObject.Recover<DBEngineObject>(engine);
    }

    private CalcDb.Com.Ado.ConnectionObject CreateConnection()
    {
        nint conn;
        Assert.Equal(0, CalcDb.Com.Ado.ConnectionObject.Create(_engine.Binding, 0, NativeRoute.IidIDispatch, &conn));
        _held.Add(conn);
        return ComObject.Recover<CalcDb.Com.Ado.ConnectionObject>(conn);
    }

    private nint Arg(object? value)
    {
        var v = (NativeVariant*)NativeMemory.AllocZeroed((nuint)sizeof(NativeVariant));
        switch (value)
        {
            case int i: v->Vt = (ushort)VarType.I4; v->Int64 = (uint)i; break;
            case bool b: v->Vt = (ushort)VarType.Bool; v->Int64 = b ? 0xFFFF : 0; break;
            case double d: v->Vt = (ushort)VarType.Date; v->Int64 = BitConverter.DoubleToInt64Bits(d); break;
            case string text: v->Vt = (ushort)VarType.Bstr; v->Pointer = Marshal.StringToBSTR(text); break;
            case DBNull: v->Vt = (ushort)VarType.Null; break;
            case DispatchObject obj:
                nint ptr = _ptrs[obj];
                Marshal.AddRef(ptr);
                _held.Add(ptr);
                v->Vt = (ushort)VarType.Dispatch;
                v->Pointer = ptr;
                break;
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

    private T As<T>(CallResult r) where T : ComObject
    {
        Assert.True(r.Failure is null, r.Failure?.Description);
        nint ptr = r.OwnsScalar ? r.Scalar.Pointer : r.Value!.Pointer->Pointer;
        _held.Add(ptr);
        T obj = ComObject.Recover<T>(ptr);
        _ptrs[obj] = ptr;
        return obj;
    }

    private static string TextOf(CallResult r)
    {
        Assert.True(r.Failure is null, r.Failure?.Description);
        nint ptr = r.HasScalar ? r.Scalar.Pointer : r.Value!.Pointer->Pointer;
        string s = Marshal.PtrToStringBSTR(ptr);
        r.ReleaseUntaken();
        return s;
    }

    private static int IntOf(CallResult r)
    {
        Assert.True(r.Failure is null, r.Failure?.Description);
        int v = (int)(r.HasScalar ? r.Scalar.Int64 : r.Value!.Pointer->Int64);
        r.ReleaseUntaken();
        return v;
    }

    private static double DateOf(CallResult r)
    {
        Assert.True(r.Failure is null, r.Failure?.Description);
        long bits = r.HasScalar ? r.Scalar.Int64 : r.Value!.Pointer->Int64;
        r.ReleaseUntaken();
        return BitConverter.Int64BitsToDouble(bits);
    }

    private static ushort ReadCommitCounter(string path)
    {
        byte[] page0 = new byte[4096];
        using FileStream fs = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        fs.ReadExactly(page0);
        return BinaryPrimitives.ReadUInt16LittleEndian(page0.AsSpan(0x0E02, 2));
    }

    private string CreateFreshDb(DBEngineObject engine, string name, bool mdb)
    {
        string path = Path.Combine(_dir, name + (mdb ? ".mdb" : ".accdb"));
        DatabaseObject db = As<DatabaseObject>(Method(engine, "CreateDatabase", path, DbLangGeneral, mdb ? DbVersion40 : DbVersion120));
        Assert.Null(Method(db, "Close").Failure);
        return path;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Dao_Database_CreateProperty_Append_Update_Delete_Persists_Across_Reopen(bool mdb)
    {
        // r-q51-codecs-props-compact/prop-db-lifecycle-accdb, prop-db-lifecycle-mdb
        DBEngineObject engine = CreateEngine();
        string path = CreateFreshDb(engine, mdb ? "db-prop-mdb" : "db-prop-accdb", mdb);
        ushort c0 = ReadCommitCounter(path);

        // Session 1: append inline Boolean property (no page-1 allocation, +1 on 0xE02).
        DatabaseObject db = As<DatabaseObject>(Method(engine, "OpenDatabase", path, false, false));
        PropertiesObject prs = As<PropertiesObject>(Get(db, "Properties"));
        PropertyObject pBool = As<PropertyObject>(Method(db, "CreateProperty", "PBool", 1, true));
        Assert.Null(Method(prs, "Append", pBool).Failure);
        Assert.Null(Method(db, "Close").Failure);
        Assert.Equal((ushort)(c0 + 1), ReadCommitCounter(path));

        // Session 2: append Long, Text, Date (promotes LvProp to LVAL + returns extent at Close = +4).
        db = As<DatabaseObject>(Method(engine, "OpenDatabase", path, false, false));
        prs = As<PropertiesObject>(Get(db, "Properties"));
        Assert.Null(Method(prs, "Append", As<PropertyObject>(Method(db, "CreateProperty", "PLong", 4, 42))).Failure);
        Assert.Null(Method(prs, "Append", As<PropertyObject>(Method(db, "CreateProperty", "PText", 10, "hello-db"))).Failure);
        Assert.Null(Method(prs, "Append", As<PropertyObject>(Method(db, "CreateProperty", "PDate", 8, 46086.0))).Failure);
        Assert.Null(Method(db, "Close").Failure);
        Assert.Equal((ushort)(c0 + 5), ReadCommitCounter(path));

        // Session 3: update PLong and PText (+2 on 0xE02, 0 at Close).
        db = As<DatabaseObject>(Method(engine, "OpenDatabase", path, false, false));
        prs = As<PropertiesObject>(Get(db, "Properties"));
        PropertyObject pLong = As<PropertyObject>(Get(prs, "Item", "PLong"));
        Assert.Null(Put(pLong, "Value", 99).Failure);
        Assert.Equal(99, IntOf(Get(pLong, "Value")));
        string longText = "updated-" + new string('x', 200);
        PropertyObject pText = As<PropertyObject>(Get(prs, "Item", "PText"));
        Assert.Null(Put(pText, "Value", longText).Failure);
        Assert.Equal(longText, TextOf(Get(pText, "Value")));
        Assert.Null(Method(db, "Close").Failure);
        Assert.Equal((ushort)(c0 + 7), ReadCommitCounter(path));

        // Session 4: delete PBool and PDate (+2 on 0xE02, 0 at Close).
        db = As<DatabaseObject>(Method(engine, "OpenDatabase", path, false, false));
        prs = As<PropertiesObject>(Get(db, "Properties"));
        Assert.Null(Method(prs, "Delete", "PBool").Failure);
        Assert.Null(Method(prs, "Delete", "PDate").Failure);
        Assert.Null(Method(db, "Close").Failure);
        Assert.Equal((ushort)(c0 + 9), ReadCommitCounter(path));

        // Session 5: reopen read-only and verify surviving user properties.
        db = As<DatabaseObject>(Method(engine, "OpenDatabase", path, false, true));
        prs = As<PropertiesObject>(Get(db, "Properties"));
        Assert.Equal(13, IntOf(Get(prs, "Count"))); // 11 built-ins + PLong + PText
        Assert.Equal(99, IntOf(Get(As<PropertyObject>(Get(prs, "Item", "PLong")), "Value")));
        Assert.Equal(longText, TextOf(Get(As<PropertyObject>(Get(prs, "Item", "PText")), "Value")));
        Assert.Null(Method(db, "Close").Failure);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Dao_TableDef_And_Field_CreateProperty_Lifecycle_Persists_Across_Reopen(bool mdb)
    {
        // r-q51-codecs-props-compact/prop-tdf-lifecycle-accdb, prop-tdf-lifecycle-mdb,
        // prop-fld-lifecycle-accdb, prop-fld-lifecycle-mdb
        DBEngineObject engine = CreateEngine();
        string path = CreateFreshDb(engine, mdb ? "tdf-fld-prop-mdb" : "tdf-fld-prop-accdb", mdb);

        DatabaseObject db = As<DatabaseObject>(Method(engine, "OpenDatabase", path, false, false));
        Assert.Null(Method(db, "Execute", "CREATE TABLE TP (ID LONG CONSTRAINT PK_TP PRIMARY KEY, Title TEXT(40))", DbFailOnError).Failure);
        Assert.Null(Method(db, "Close").Failure);

        // Append, update, and delete on TableDef and Field.
        db = As<DatabaseObject>(Method(engine, "OpenDatabase", path, false, false));
        TableDefObject td = As<TableDefObject>(Get(As<DefinitionCollection>(Get(db, "TableDefs")), "Item", "TP"));
        PropertiesObject tdp = As<PropertiesObject>(Get(td, "Properties"));
        Assert.Null(Method(tdp, "Append", As<PropertyObject>(Method(td, "CreateProperty", "Description", 10, "Table desc"))).Failure);
        Assert.Null(Method(tdp, "Append", As<PropertyObject>(Method(td, "CreateProperty", "TDate", 8, 46022.0))).Failure);
        Assert.Null(Put(As<PropertyObject>(Get(tdp, "Item", "Description")), "Value", "Table desc 2").Failure);
        Assert.Null(Method(tdp, "Delete", "Description").Failure);

        DefinitionFieldObject fld = As<DefinitionFieldObject>(Get(As<DefinitionCollection>(Get(td, "Fields")), "Item", "Title"));
        PropertiesObject flp = As<PropertiesObject>(Get(fld, "Properties"));
        Assert.Null(Method(flp, "Append", As<PropertyObject>(Method(fld, "CreateProperty", "Caption", 10, "Title Cap"))).Failure);
        Assert.Null(Method(flp, "Append", As<PropertyObject>(Method(fld, "CreateProperty", "FBool", 1, true))).Failure);
        Assert.Null(Put(As<PropertyObject>(Get(flp, "Item", "Caption")), "Value", "Title Cap 2").Failure);
        Assert.Null(Method(flp, "Delete", "FBool").Failure);
        Assert.Null(Method(db, "Close").Failure);

        // Reopen read-only and verify persistence.
        db = As<DatabaseObject>(Method(engine, "OpenDatabase", path, false, true));
        td = As<TableDefObject>(Get(As<DefinitionCollection>(Get(db, "TableDefs")), "Item", "TP"));
        tdp = As<PropertiesObject>(Get(td, "Properties"));
        Assert.Equal(13, IntOf(Get(tdp, "Count"))); // 12 built-ins + TDate
        Assert.Equal(46022.0, DateOf(Get(As<PropertyObject>(Get(tdp, "Item", "TDate")), "Value")));

        fld = As<DefinitionFieldObject>(Get(As<DefinitionCollection>(Get(td, "Fields")), "Item", "Title"));
        flp = As<PropertiesObject>(Get(fld, "Properties"));
        Assert.Equal(25, IntOf(Get(flp, "Count"))); // 24 built-ins + Caption
        Assert.Equal("Title Cap 2", TextOf(Get(As<PropertyObject>(Get(flp, "Item", "Caption")), "Value")));
        Assert.Null(Method(db, "Close").Failure);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Dao_CreateProperty_Error_Matrix_Writes_Zero_Bytes_And_Invalidates_Held_On_Delete(bool mdb)
    {
        // r-q51-codecs-props-compact/prop-errors-accdb, prop-errors-mdb
        DBEngineObject engine = CreateEngine();
        string path = CreateFreshDb(engine, mdb ? "prop-err-mdb" : "prop-err-accdb", mdb);

        // Seed table + 1 user property on Database, TableDef, Field.
        DatabaseObject db = As<DatabaseObject>(Method(engine, "OpenDatabase", path, false, false));
        Assert.Null(Method(db, "Execute", "CREATE TABLE TE (ID LONG CONSTRAINT PK_TE PRIMARY KEY, S TEXT(20))", DbFailOnError).Failure);
        PropertiesObject dbp = As<PropertiesObject>(Get(db, "Properties"));
        Assert.Null(Method(dbp, "Append", As<PropertyObject>(Method(db, "CreateProperty", "SeedDb", 4, 1))).Failure);
        TableDefObject td = As<TableDefObject>(Get(As<DefinitionCollection>(Get(db, "TableDefs")), "Item", "TE"));
        PropertiesObject tdp = As<PropertiesObject>(Get(td, "Properties"));
        Assert.Null(Method(tdp, "Append", As<PropertyObject>(Method(td, "CreateProperty", "SeedTd", 10, "ok"))).Failure);
        DefinitionFieldObject f = As<DefinitionFieldObject>(Get(As<DefinitionCollection>(Get(td, "Fields")), "Item", "S"));
        PropertiesObject fp = As<PropertiesObject>(Get(f, "Properties"));
        Assert.Null(Method(fp, "Append", As<PropertyObject>(Method(f, "CreateProperty", "SeedFl", 1, true))).Failure);
        Assert.Null(Method(db, "Close").Failure);

        byte[] afterSeed = File.ReadAllBytes(path);

        // Run full refusal matrix and assert 0 bytes changed.
        db = As<DatabaseObject>(Method(engine, "OpenDatabase", path, false, false));
        dbp = As<PropertiesObject>(Get(db, "Properties"));
        td = As<TableDefObject>(Get(As<DefinitionCollection>(Get(db, "TableDefs")), "Item", "TE"));
        tdp = As<PropertiesObject>(Get(td, "Properties"));
        f = As<DefinitionFieldObject>(Get(As<DefinitionCollection>(Get(td, "Fields")), "Item", "S"));
        fp = As<PropertiesObject>(Get(f, "Properties"));

        // 3385: empty string or Null value at CreateProperty
        AssertDaoError(Method(db, "CreateProperty", "EEmpty", 10, ""), 3385, "DAO.Property");
        AssertDaoError(Method(db, "CreateProperty", "ENull", 10, DBNull.Value), 3385, "DAO.Property");
        AssertDaoError(Method(td, "CreateProperty", "ETdNull", 4, DBNull.Value), 3385, "DAO.Property");
        AssertDaoError(Method(f, "CreateProperty", "EFlEmpty", 10, ""), 3385, "DAO.Property");

        // 3386: Append before Value set
        AssertDaoError(Method(dbp, "Append", As<PropertyObject>(Method(db, "CreateProperty", "ENoValDb", 4))), 3386, "DAO.Property");
        AssertDaoError(Method(tdp, "Append", As<PropertyObject>(Method(td, "CreateProperty", "ENoValTd", 10))), 3386, "DAO.Property");
        AssertDaoError(Method(fp, "Append", As<PropertyObject>(Method(f, "CreateProperty", "ENoValFl", 1))), 3386, "DAO.Property");

        // 3367: duplicate name
        AssertDaoError(Method(dbp, "Append", As<PropertyObject>(Method(db, "CreateProperty", "SeedDb", 4, 2))), 3367, "DAO.Property");
        AssertDaoError(Method(tdp, "Append", As<PropertyObject>(Method(td, "CreateProperty", "SeedTd", 10, "dup"))), 3367, "DAO.Property");
        AssertDaoError(Method(fp, "Append", As<PropertyObject>(Method(f, "CreateProperty", "SeedFl", 1, false))), 3367, "DAO.Property");

        // 3219: Type put on appended Property
        AssertDaoError(Put(As<PropertyObject>(Get(dbp, "Item", "SeedDb")), "Type", 10), 3219, "DAO.Property");
        AssertDaoError(Put(As<PropertyObject>(Get(tdp, "Item", "SeedTd")), "Type", 4), 3219, "DAO.Property");
        AssertDaoError(Put(As<PropertyObject>(Get(fp, "Item", "SeedFl")), "Type", 4), 3219, "DAO.Property");

        // 3219: detached TableDef (DAO.Properties) and detached Field (DAO.Property)
        NewTableDefObject dtdf = As<NewTableDefObject>(Method(db, "CreateTableDef", "DetachedT"));
        PropertiesObject dtdfp = As<PropertiesObject>(Get(dtdf, "Properties"));
        AssertDaoError(Method(dtdfp, "Append", As<PropertyObject>(Method(dtdf, "CreateProperty", "DP1", 10, "x"))), 3219, "DAO.Properties");
        NewFieldObject dfld = As<NewFieldObject>(Method(dtdf, "CreateField", "DF", 4));
        PropertiesObject dfldp = As<PropertiesObject>(Get(dfld, "Properties"));
        AssertDaoError(Method(dfldp, "Append", As<PropertyObject>(Method(dfld, "CreateProperty", "DP2", 10, "y"))), 3219, "DAO.Property");

        // 3384: built-in delete; 3265: missing delete; 3270: missing Item
        AssertDaoError(Method(dbp, "Delete", "Version"), 3384, "DAO.Properties");
        AssertDaoError(Method(tdp, "Delete", "Name"), 3384, "DAO.Properties");
        AssertDaoError(Method(fp, "Delete", "Name"), 3384, "DAO.Properties");
        AssertDaoError(Method(dbp, "Delete", "NoSuchProp"), 3265, "DAO.Properties");
        AssertDaoError(Get(dbp, "Item", "NoSuchProp"), 3270, "DAO.Properties");
        Assert.Null(Method(db, "Close").Failure);

        Assert.Equal(afterSeed, File.ReadAllBytes(path));

        // Held-object invalidation (3420) + second delete (3265)
        db = As<DatabaseObject>(Method(engine, "OpenDatabase", path, false, false));
        dbp = As<PropertiesObject>(Get(db, "Properties"));
        PropertyObject heldP = As<PropertyObject>(Get(dbp, "Item", "SeedDb"));
        Assert.Null(Method(dbp, "Delete", "SeedDb").Failure);
        AssertDaoError(Get(heldP, "Name"), 3420, "DAO.Property");
        AssertDaoError(Get(heldP, "Value"), 3420, "DAO.Property");
        AssertDaoError(Method(dbp, "Delete", "SeedDb"), 3265, "DAO.Properties");
        Assert.Null(Method(db, "Close").Failure);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Dao_CreateQueryDef_Deferred_Followed_By_Sql_And_Dao_Ddl_Matches_Counter_Progression(bool mdb)
    {
        // r-q54-excl-ddl-ix-cmd/qd-deferred-then-ddl-accdb, qd-deferred-then-ddl-mdb
        DBEngineObject engine = CreateEngine();
        string path = CreateFreshDb(engine, mdb ? "qd-ddl-mdb" : "qd-ddl-accdb", mdb);

        DatabaseObject db = As<DatabaseObject>(Method(engine, "OpenDatabase", path, false, false));
        Assert.Null(Method(db, "Execute", "CREATE TABLE Base (ID LONG CONSTRAINT PK_Base PRIMARY KEY, V LONG)", DbFailOnError).Failure);
        Assert.Null(Method(db, "Execute", "INSERT INTO Base VALUES (1, 10)", DbFailOnError).Failure);
        Assert.Null(Method(db, "Close").Failure);
        ushort cSetup = ReadCommitCounter(path);

        // Session 1: CreateQueryDef (+4) -> SQL CREATE TABLE (+2) -> Close (+1)
        db = As<DatabaseObject>(Method(engine, "OpenDatabase", path, false, false));
        _ = As<QueryDefObject>(Method(db, "CreateQueryDef", "Q1", "SELECT ID, V FROM Base"));
        Assert.Equal((ushort)(cSetup + 4), ReadCommitCounter(path));
        Assert.Null(Method(db, "Execute", "CREATE TABLE T1 (ID LONG CONSTRAINT PK_T1 PRIMARY KEY, X LONG)", DbFailOnError).Failure);
        Assert.Equal((ushort)(cSetup + 6), ReadCommitCounter(path));
        Assert.Null(Method(db, "Close").Failure);
        Assert.Equal((ushort)(cSetup + 7), ReadCommitCounter(path));

        // Session 2: CreateQueryDef (+4) -> DAO TableDefs.Append (+1) -> Close (+2)
        db = As<DatabaseObject>(Method(engine, "OpenDatabase", path, false, false));
        _ = As<QueryDefObject>(Method(db, "CreateQueryDef", "Q2", "SELECT ID FROM T1"));
        Assert.Equal((ushort)(cSetup + 11), ReadCommitCounter(path));
        NewTableDefObject td2 = As<NewTableDefObject>(Method(db, "CreateTableDef", "T2"));
        NewFieldObject f2 = As<NewFieldObject>(Method(td2, "CreateField", "ID", 4));
        Assert.Null(Method(As<NewFieldsObject>(Get(td2, "Fields")), "Append", f2).Failure);
        Assert.Null(Method(As<DefinitionCollection>(Get(db, "TableDefs")), "Append", td2).Failure);
        Assert.Equal((ushort)(cSetup + 12), ReadCommitCounter(path));
        Assert.Null(Method(db, "Close").Failure);
        Assert.Equal((ushort)(cSetup + 14), ReadCommitCounter(path));

        // Session 3: CreateQueryDef (+4) -> SQL DROP TABLE (+2) -> Close (+1)
        db = As<DatabaseObject>(Method(engine, "OpenDatabase", path, false, false));
        _ = As<QueryDefObject>(Method(db, "CreateQueryDef", "Q3", "SELECT ID, V FROM Base WHERE V > 0"));
        Assert.Equal((ushort)(cSetup + 18), ReadCommitCounter(path));
        Assert.Null(Method(db, "Execute", "DROP TABLE T2", DbFailOnError).Failure);
        Assert.Equal((ushort)(cSetup + 20), ReadCommitCounter(path));
        Assert.Null(Method(db, "Close").Failure);
        Assert.Equal((ushort)(cSetup + 21), ReadCommitCounter(path));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Ado_DropView_Steps_Commit_Counter_At_Close(bool mdb)
    {
        // r-q50-jk44-ddl-vis/ddl-view-create-drop-accdb, ddl-view-create-drop-mdb
        DBEngineObject engine = CreateEngine();
        string path = CreateFreshDb(engine, mdb ? "view-drop-mdb" : "view-drop-accdb", mdb);

        DatabaseObject db = As<DatabaseObject>(Method(engine, "OpenDatabase", path, false, false));
        Assert.Null(Method(db, "Execute", "CREATE TABLE T (ID LONG CONSTRAINT PK_T PRIMARY KEY, A TEXT(20))", DbFailOnError).Failure);
        Assert.Null(Method(db, "Close").Failure);

        CalcDb.Com.Ado.ConnectionObject cn1 = CreateConnection();
        Assert.Null(Put(cn1, "Provider", "Microsoft.ACE.OLEDB.12.0").Failure);
        Assert.Null(Put(cn1, "Mode", 3).Failure);
        Assert.Null(Method(cn1, "Open", $"Data Source={path}", "", "", -1).Failure);
        Assert.Null(Method(cn1, "Execute", "CREATE VIEW V1 AS SELECT ID, A FROM T", null, 129).Failure);
        Assert.Null(Method(cn1, "Close").Failure);

        ushort beforeDrop = ReadCommitCounter(path);
        CalcDb.Com.Ado.ConnectionObject cn2 = CreateConnection();
        Assert.Null(Put(cn2, "Provider", "Microsoft.ACE.OLEDB.12.0").Failure);
        Assert.Null(Put(cn2, "Mode", 3).Failure);
        Assert.Null(Method(cn2, "Open", $"Data Source={path}", "", "", -1).Failure);
        Assert.Null(Method(cn2, "Execute", "DROP VIEW V1", null, 129).Failure);
        Assert.Equal((ushort)(beforeDrop + 1), ReadCommitCounter(path));
        Assert.Null(Method(cn2, "Close").Failure);
        Assert.Equal((ushort)(beforeDrop + 2), ReadCommitCounter(path));
    }

    private static void AssertDaoError(CallResult r, int expectedNumber, string expectedSource)
    {
        Assert.NotNull(r.Failure);
        Assert.Equal(FailureKind.Dao, r.Failure!.Kind);
        Assert.Equal(expectedNumber, r.Failure.Number);
        Assert.Equal(expectedSource, r.Failure.Source);
    }
}
