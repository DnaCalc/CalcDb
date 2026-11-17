using CalcDb.Evidence;

namespace CalcDb.Probes.Cards;

/// <summary>
/// R-DAO-1 (DAO-MANIFEST applicability, DAO-PROP, DAO-META, DAO-COLL, DAO-ID), second batch: member-by-member
/// reads of every readable property of DBEngine, Workspace, Database, TableDef, Field, Index, IndexFields,
/// Relation, QueryDef, Parameter, Recordset (each type), Property, Container and Document objects; the
/// Properties collection of each object enumerated with Name, Type, Inherited and Value; Item lookups,
/// Refresh, Append and Delete behaviour of the collections. Every member is read by its own call, so one refusal
/// hides nothing. Probes ask and record; nothing is asserted. The first questions of the card (inventory,
/// lifecycle, wrappers, drafts, queries) are in <see cref="RDao1Err1"/>; this file adds questions that card does not ask.
/// Two rich fixtures are created first (ACCDB and MDB); every attempt gets fresh copies. One module snapshot per probe.
/// </summary>
public sealed class RDao1B : ICard
{
    private const string Card = "r-dao-1b";
    private const string Rich = Card + "-rich";
    private const string RichMdb = Card + "-rich-mdb";

    // DAO type-library values used as arguments (R-DAO-1 DAO-MANIFEST captures the library itself).
    private const int UseJet = 2, TypeLong = 4, TypeText = 10, RelUpdateCascade = 256;

    private static readonly string[] BaseSql =
    [
        "CREATE TABLE Parent (ID LONG CONSTRAINT PK_Parent PRIMARY KEY, [Name] TEXT(30) NOT NULL, Qty LONG, [Value] DOUBLE)",
        "CREATE UNIQUE INDEX UX_Parent_Name ON Parent ([Name])",
        "CREATE INDEX IX_Parent_QtyDesc ON Parent (Qty DESC, [Value] ASC)",
        "CREATE INDEX IX_Parent_Ignore ON Parent ([Value]) WITH IGNORE NULL",
        "CREATE INDEX IX_Parent_Required ON Parent (Qty) WITH DISALLOW NULL",
        "INSERT INTO Parent VALUES (1, 'one', 10, 1.5)",
        "INSERT INTO Parent VALUES (2, 'two', 20, 2.5)",
        "INSERT INTO Parent VALUES (3, 'three', 30, NULL)",
        "INSERT INTO Parent VALUES (4, 'four', 40, 4.5)",
        "INSERT INTO Parent VALUES (5, 'five', 50, 5.5)",
        "CREATE TABLE Child (CID COUNTER CONSTRAINT PK_Child PRIMARY KEY, PID LONG, [Text] TEXT(10))",
        "INSERT INTO Child (PID, [Text]) VALUES (1, 'a')",
        "INSERT INTO Child (PID, [Text]) VALUES (1, 'b')",
        "INSERT INTO Child (PID, [Text]) VALUES (2, 'c')",
        "INSERT INTO Child (PID, [Text]) VALUES (3, 'd')",
        "CREATE TABLE Grand (GID LONG CONSTRAINT PK_Grand PRIMARY KEY, CID LONG CONSTRAINT FK_Grand REFERENCES Child (CID))",
        "INSERT INTO Grand VALUES (1, 1)",
        "INSERT INTO Grand VALUES (2, 2)",
        "CREATE TABLE Typed (K COUNTER CONSTRAINT PK_Typed PRIMARY KEY, TText TEXT(25), TMemo MEMO, TByte BYTE, TInt SHORT, TLong LONG, " +
        "TSingle SINGLE, TDouble DOUBLE, TCur CURRENCY, TDate DATETIME, TBool YESNO, TOle LONGBINARY, TGuid GUID)",
        "INSERT INTO Typed (TText, TMemo, TByte, TInt, TLong, TSingle, TDouble, TCur, TDate, TBool) " +
        "VALUES ('t', 'm', 7, -3, 70000, 1.5, 2.25, 3.75, #01/02/2026#, True)",
    ];

    private static readonly (string Name, string Sql)[] Queries =
    [
        ("QSel", "SELECT ID, [Name] FROM Parent ORDER BY ID;"),
        ("QParam", "PARAMETERS pId Long, pName Text (20); SELECT ID, [Name] FROM Parent WHERE ID = pId OR [Name] = pName;"),
        ("QUpd", "PARAMETERS pDelta Long; UPDATE Parent SET Qty = Qty + pDelta;"),
        ("QDel", "DELETE FROM Child WHERE CID < 0;"),
        ("QApp", "INSERT INTO Grand (GID) SELECT CID FROM Child WHERE CID < 0;"),
        ("QMake", "SELECT ID, [Name] INTO Made FROM Parent;"),
        ("QCross", "TRANSFORM Sum(Qty) AS SumQty SELECT [Name] FROM Parent GROUP BY [Name] PIVOT ID;"),
        ("QUnion", "SELECT ID FROM Parent UNION SELECT PID FROM Child;"),
    ];

    // Member lists: the documented readable properties of each DAO object, plus a few names the type library may not list.
    private static readonly string[] EngineMembers = ["Name", "Version", "IniPath", "DefaultUser", "DefaultPassword", "LoginTimeout", "SystemDB", "DefaultType"];
    private static readonly string[] EngineCollections = ["Workspaces", "Errors", "Properties"];

    private static readonly string[] WorkspaceMembers = ["Name", "UserName", "Type", "IsolateODBCTrans", "DefaultCursorDriver", "LoginTimeout", "hDbc"];
    private static readonly string[] WorkspaceCollections = ["Databases", "Users", "Groups", "Connections", "Properties"];

    private static readonly string[] DatabaseMembers =
    [
        "CollatingOrder", "Connect", "DesignMasterID", "Name", "QueryTimeout", "RecordsAffected", "ReplicaID", "Transactions", "Updatable",
        "V1xNullBehavior", "Version", "hDbc",
    ];
    private static readonly string[] DatabaseCollections = ["Connections", "Containers", "QueryDefs", "Recordsets", "Relations", "TableDefs", "Properties"];

    private static readonly string[] TableDefMembers =
    [
        "Attributes", "ConflictTable", "Connect", "DateCreated", "LastUpdated", "Name", "RecordCount", "ReplicaFilter", "SourceTableName",
        "Updatable", "ValidationRule", "ValidationText",
    ];
    private static readonly string[] TableDefCollections = ["Fields", "Indexes", "Properties"];

    private static readonly string[] FieldMembers =
    [
        "AllowZeroLength", "Attributes", "CollatingOrder", "CollectionIndex", "ComplexType", "DataUpdatable", "DefaultValue", "Expression",
        "FieldSize", "ForeignName", "Name", "OrdinalPosition", "OriginalValue", "Required", "Size", "SourceField", "SourceTable", "Type",
        "ValidateOnSet", "ValidationRule", "ValidationText", "Value", "VisibleValue",
    ];
    private static readonly string[] FieldCollections = ["Properties"];

    private static readonly string[] IndexMembers = ["Clustered", "DistinctCount", "Foreign", "IgnoreNulls", "Name", "Primary", "Required", "Unique"];
    private static readonly string[] IndexCollections = ["Fields", "Properties"];

    private static readonly string[] RelationMembers = ["Attributes", "ForeignTable", "Name", "PartialReplica", "Table"];
    private static readonly string[] RelationCollections = ["Fields", "Properties"];

    private static readonly string[] QueryDefMembers =
    [
        "CacheSize", "Connect", "DateCreated", "hStmt", "LastUpdated", "LogMessages", "MaxRecords", "Name", "ODBCTimeout", "Prepare", "RecordLocks",
        "RecordsAffected", "ReturnsRecords", "SQL", "StillExecuting", "Type", "Updatable",
    ];
    private static readonly string[] QueryDefCollections = ["Fields", "Parameters", "Properties"];

    private static readonly string[] ParameterMembers = ["Direction", "Name", "Type", "Value"];

    private static readonly string[] RecordsetMembers =
    [
        "AbsolutePosition", "BatchCollisionCount", "BatchCollisions", "BatchSize", "BOF", "Bookmark", "Bookmarkable", "CacheSize", "CacheStart",
        "Connection", "DateCreated", "EditMode", "EOF", "Filter", "hStmt", "Index", "LastModified", "LastUpdated", "LockEdits", "LockType",
        "MaxRecords", "Name", "NoMatch", "PercentPosition", "RecordCount", "RecordStatus", "Restartable", "Sort", "StillExecuting", "Transactions",
        "Type", "Updatable", "UpdateOptions", "ValidationRule", "ValidationText",
    ];
    private static readonly string[] RecordsetCollections = ["Fields", "Properties"];

    private static readonly string[] PropertyMembers = ["Inherited", "Name", "Type", "Value", "Category", "Attributes"];

    private static readonly string[] ContainerMembers = ["AllPermissions", "Inherit", "Name", "Owner", "Permissions", "UserName"];
    private static readonly string[] ContainerCollections = ["Documents", "Properties"];

    private static readonly string[] DocumentMembers = ["AllPermissions", "Container", "DateCreated", "LastUpdated", "Name", "Owner", "Permissions", "UserName"];

    public IEnumerable<Probe> Probes() =>
    [
        .. Creations(), .. EngineWorkspace(), .. DatabaseProps(), .. TableDefsFields(), .. IndexesRelations(), .. QueryDefs(),
        .. Recordsets(), .. PropertiesContainers(), .. Collections(),
    ];

    // Fixtures ----------------------------------------------------------------------------------------

    private static IEnumerable<Probe> Creations()
    {
        yield return Creation("accdb", Dao.Version120, Rich);
        yield return Creation("mdb", Dao.Version40, RichMdb);
    }

    private static Probe Creation(string ext, int version, string fixture) =>
        new(Card, $"create-rich-{ext}",
            ["R-DAO-1", "DAO-PROP", "DAO-META", "DAO-COLL", "DAO-QUERY", "II-05"],
            $"R-DAO-1 fixture, created in {ext.ToUpperInvariant()} by DBEngine.CreateDatabase(path, dbLangGeneral, {version}): table Parent " +
            "(Long key PK_Parent, Text [Name], Long Qty, Double [Value]; five rows; unique index UX_Parent_Name, descending composite index " +
            "IX_Parent_QtyDesc, IGNORE NULL index IX_Parent_Ignore, DISALLOW NULL index IX_Parent_Required); table Child (AutoNumber CID, PID, [Text]; " +
            "four rows) related to Parent by relation RelMain with update-cascade attribute; table Grand whose CID references Child through FK_Grand; " +
            "table Typed with one field of each common type, one row, and for the ACCDB a Decimal field TDec added through Fields.Append; field " +
            "attribute edits on Typed; eight saved queries (select, parameter, update, delete, append, make-table, crosstab, union); user properties " +
            "of six types on the database, Description on table Parent and on its field [Name]. The closed file is kept.",
            p =>
            {
                p.EngineLite().CallBind("db", "eng", "CreateDatabase", A.Out($"{fixture}.{ext}"), A.Str(Dao.LangGeneral), A.I4(version));
                foreach (string sql in BaseSql)
                {
                    p.Call("db", "Execute", A.Str(sql));
                }
                if (ext == "accdb")
                {
                    p.AddDecimalField("Typed", "TDec");
                }
                p.AddRelation("RelMain", "Parent", "Child", "ID", "PID", RelUpdateCascade)
                    .GetBind("tds", "db", "TableDefs").Call("tds", "Refresh")
                    .Item("tt", "tds", A.Str("Typed")).GetBind("tfl", "tt", "Fields")
                    .Item("fa", "tfl", A.Str("TText")).Put("fa", "AllowZeroLength", A.Bool(true)).Put("fa", "DefaultValue", A.Str("\"dflt\""))
                    .Item("fb", "tfl", A.Str("TLong")).Put("fb", "Required", A.Bool(true))
                    .Item("fc", "tfl", A.Str("TInt")).Put("fc", "ValidationRule", A.Str(">= -10")).Put("fc", "ValidationText", A.Str("TInt must be at least -10"))
                    .Item("fd", "tfl", A.Str("TDouble")).Put("fd", "DefaultValue", A.Str("1.5"))
                    .Release("fd").Release("fc").Release("fb").Release("fa").Release("tfl").Release("tt");
                foreach ((string name, string sql) in Queries)
                {
                    p.CallBind("q", "db", "CreateQueryDef", A.Str(name), A.Str(sql)).Release("q");
                }
                AppendProperty(p, "db", "UPText", 10, A.Str("alpha"), "u1");
                AppendProperty(p, "db", "UPLong", TypeLong, A.I4(42), "u2");
                AppendProperty(p, "db", "UPBool", 1, A.Bool(true), "u3");
                AppendProperty(p, "db", "UPDate", 8, A.Date(new DateTime(2026, 1, 2)), "u4");
                AppendProperty(p, "db", "UPDouble", 7, A.R8(2.5), "u5");
                AppendProperty(p, "db", "UPMemo", 12, A.Str("memo text"), "u6");
                p.Item("tp", "tds", A.Str("Parent"));
                AppendProperty(p, "tp", "Description", TypeText, A.Str("parent table"), "u7");
                p.GetBind("tpf", "tp", "Fields").Item("tpn", "tpf", A.Str("Name"));
                AppendProperty(p, "tpn", "Description", TypeText, A.Str("name column"), "u8");
                p.Release("tpn").Release("tpf").Release("tp").Release("tds").Call("db", "Close").Release("db").Release("eng").Files("after-close");
            })
        { CreatesFixture = true };

    // Helpers -----------------------------------------------------------------------------------------

    /// <summary>A DAO probe: light engine opening, the Database opened on the fixture (read-only unless writable), the body, light closing.</summary>
    private static Probe Make(string name, string[] refs, string description, Action<P> body, string fixture = Rich, bool withDb = true, bool writable = false)
    {
        var probe = new Probe(Card, name, refs, description, p =>
        {
            p.EngineLite();
            if (withDb)
            {
                p.OpenDb(readOnly: !writable);
            }
            body(p);
            if (withDb)
            {
                p.FinishLite("db");
            }
            else
            {
                p.FinishLite();
            }
        });
        return withDb ? probe with { Fixtures = [("main", fixture)] } : probe;
    }

    private static string[] Refs(params string[] ids) => ["R-DAO-1", .. ids];

    private static P Ws0(P p) => p.GetBind("wss", "eng", "Workspaces").Item("ws", "wss", A.I4(0));

    /// <summary>Read each named member of <paramref name="target"/> with its own call.</summary>
    private static P Reads(P p, string target, IEnumerable<string> members)
    {
        foreach (string member in members)
        {
            p.Get(target, member);
        }
        return p;
    }

    /// <summary>Get each named collection of <paramref name="target"/> and read its Count.</summary>
    private static P Counts(P p, string target, string[] collections, string tag)
    {
        foreach (string collection in collections)
        {
            p.GetBind(tag + collection, target, collection).Get(tag + collection, "Count").Release(tag + collection);
        }
        return p;
    }

    private static P Members(P p, string target, string[] members, string[] collections, string tag) =>
        Counts(Reads(p, target, members), target, collections, tag);

    /// <summary>Enumerate <c>owner.Properties</c>: Name, Type, Inherited and Value of each member, each read separately.</summary>
    private static P PropEnum(P p, string owner, string tag) =>
        p.GetBind(tag + "s", owner, "Properties")
            .Each(tag + "s", tag, 96, b => b.Get(tag, "Name").Get(tag, "Type").Get(tag, "Inherited").Get(tag, "Value"))
            .Release(tag + "s");

    /// <summary>Names of every member of a collection (bounded).</summary>
    private static P Names(P p, string collection, string tag, int max = 32) =>
        p.Each(collection, tag, max, b => b.Get(tag, "Name"));

    private static P AppendProperty(P p, string owner, string name, int type, Arg value, string tag) =>
        p.CallBind(tag + "n", owner, "CreateProperty", A.Str(name), A.I4(type), value)
            .GetBind(tag + "c", owner, "Properties").Call(tag + "c", "Append", A.Obj(tag + "n")).Release(tag + "c").Release(tag + "n");

    /// <summary><c>coll.Item(index)</c> for each index variant, then Name of each result.</summary>
    private static P ItemSweep(P p, string collection, string tag, params string[] names)
    {
        var indexes = new List<(string Label, Arg Index)> { ("0", A.I4(0)), ("99", A.I4(99)), ("neg1", A.I4(-1)) };
        indexes.AddRange(names.Select(n => ($"s-{n}", A.Str(n))));
        foreach ((string label, Arg index) in indexes)
        {
            p.Item($"{tag}-{label}", collection, index).Get($"{tag}-{label}", "Name");
        }
        return p;
    }

    private static P Table(P p, string bind, string table) =>
        p.GetBind("tds", "db", "TableDefs").Item(bind, "tds", A.Str(table));

    // DBEngine and Workspace --------------------------------------------------------------------------

    private static IEnumerable<Probe> EngineWorkspace()
    {
        yield return Make("eng-members-each", Refs("DAO-MANIFEST", "DAO-BOOT"),
            "DAO-MANIFEST applicability: on a fresh DBEngine, each of Name, Version, IniPath, DefaultUser, DefaultPassword, LoginTimeout, " +
            "SystemDB and DefaultType is read by its own call, then Count of Workspaces, Errors and Properties. Which members answer, " +
            "which are refused and with what HRESULT, in this first-touch order.",
            p => Counts(Reads(p, "eng", EngineMembers), "eng", EngineCollections, "e"), withDb: false);

        yield return Make("eng-properties-enumerated", Refs("DAO-PROP"),
            "DAO-PROP: DBEngine.Properties enumerated, Name, Type, Inherited and Value of each member read by separate calls.",
            p => PropEnum(p, "eng", "ep"), withDb: false);

        yield return Make("ws-members-default", Refs("DAO-MANIFEST", "DAO-BOOT"),
            "DAO-MANIFEST applicability: on Workspaces(0), each of Name, UserName, Type, IsolateODBCTrans, DefaultCursorDriver, LoginTimeout and hDbc " +
            "by its own call, then Count of Databases, Users, Groups, Connections and Properties.",
            p => Members(Ws0(p), "ws", WorkspaceMembers, WorkspaceCollections, "w"));

        yield return Make("ws-members-explicit", Refs("DAO-MANIFEST", "DAO-BOOT"),
            "DAO-MANIFEST applicability: a Workspace from CreateWorkspace('WX', 'admin', '', dbUseJet), first unappended and then appended " +
            "to Workspaces. The same member and collection-count reads as for Workspaces(0), once before and once after Append; Workspaces.Count after.",
            p =>
            {
                p.CallBind("w", "eng", "CreateWorkspace", A.Str("WX"), A.Str("admin"), A.Str(""), A.I4(UseJet));
                Members(p, "w", WorkspaceMembers, WorkspaceCollections, "wa");
                p.GetBind("wss", "eng", "Workspaces").Call("wss", "Append", A.Obj("w")).Get("wss", "Count");
                Members(p, "w", WorkspaceMembers, WorkspaceCollections, "wb");
                p.Call("w", "Close");
            });

        yield return Make("ws-properties-enumerated", Refs("DAO-PROP"),
            "DAO-PROP: Workspaces(0).Properties enumerated, Name, Type, Inherited and Value of each member read by separate calls.",
            p => PropEnum(Ws0(p), "ws", "wp"));

        yield return Make("users-groups-members", Refs("DAO-MANIFEST", "DAO-SEC"),
            "DAO-MANIFEST/DAO-SEC recognition: Workspaces(0).Users and Groups enumerated; each User by Name, Password, PID, Groups (Count and " +
            "names) and Properties Count; each Group by Name, PID, Users (Count and names) and Properties Count. Item lookups of 'Admin', " +
            "'ADMIN', 'Nobody' in Users and 'Admins', 'admins', 'Users', 'Guests', 'Nobody' in Groups, Name read from each.",
            p =>
            {
                Ws0(p).GetBind("us", "ws", "Users")
                    .Each("us", "u", 16, b => Reads(b, "u", ["Name", "Password", "PID"]).GetBind("ug", "u", "Groups").Get("ug", "Count")
                        .Each("ug", "ugi", 8, c => c.Get("ugi", "Name")).Release("ug").GetBind("up", "u", "Properties").Get("up", "Count").Release("up"))
                    .GetBind("gs", "ws", "Groups")
                    .Each("gs", "g", 16, b => Reads(b, "g", ["Name", "PID"]).GetBind("gu", "g", "Users").Get("gu", "Count")
                        .Each("gu", "gui", 8, c => c.Get("gui", "Name")).Release("gu").GetBind("gp", "g", "Properties").Get("gp", "Count").Release("gp"));
                foreach (string name in new[] { "Admin", "ADMIN", "Nobody" })
                {
                    p.Item("ul-" + name, "us", A.Str(name)).Get("ul-" + name, "Name");
                }
                foreach (string name in new[] { "Admins", "admins", "Users", "Guests", "Nobody" })
                {
                    p.Item("gl-" + name, "gs", A.Str(name)).Get("gl-" + name, "Name");
                }
            });
    }

    // Database ----------------------------------------------------------------------------------------

    private static IEnumerable<Probe> DatabaseProps()
    {
        yield return Make("db-members-accdb", Refs("DAO-MANIFEST", "DAO-PROP"),
            "DAO-MANIFEST applicability on the rich ACCDB: each of CollatingOrder, Connect, DesignMasterID, Name, QueryTimeout, RecordsAffected, " +
            "ReplicaID, Transactions, Updatable, V1xNullBehavior, Version and hDbc by its own call, then Count of Connections, Containers, " +
            "QueryDefs, Recordsets, Relations, TableDefs and Properties.",
            p => Members(p, "db", DatabaseMembers, DatabaseCollections, "d"));

        yield return Make("db-members-mdb", Refs("DAO-MANIFEST", "DAO-PROP"),
            "DAO-MANIFEST applicability on the rich MDB (Jet 4 format): the same member and collection-count reads as for the ACCDB Database.",
            p => Members(p, "db", DatabaseMembers, DatabaseCollections, "d"), fixture: RichMdb);

        yield return Make("db-properties-enumerated-accdb", Refs("DAO-PROP"),
            "DAO-PROP on the rich ACCDB: Database.Properties enumerated, Name, Type, Inherited and Value of each member read by separate calls.",
            p => PropEnum(p, "db", "dp"));

        yield return Make("db-properties-enumerated-mdb", Refs("DAO-PROP"),
            "DAO-PROP on the rich MDB: Database.Properties enumerated, Name, Type, Inherited and Value of each member read by separate calls.",
            p => PropEnum(p, "db", "dp"), fixture: RichMdb);

        string[] names =
        [
            "Name", "Connect", "Version", "CollatingOrder", "Transactions", "Updatable", "QueryTimeout", "V1xNullBehavior", "RecordsAffected",
            "ReplicaID", "DesignMasterID", "UPText", "UPLong", "UPBool", "UPDate", "UPDouble", "UPMemo",
        ];
        yield return Make("db-direct-vs-properties-item", Refs("DAO-PROP"),
            "DAO-PROP: for each of the built-in names Name, Connect, Version, CollatingOrder, Transactions, Updatable, QueryTimeout, " +
            "V1xNullBehavior, RecordsAffected, ReplicaID, DesignMasterID and the appended user properties UPText, UPLong, UPBool, UPDate, UPDouble " +
            "and UPMemo: the direct getter on Database, then Properties.Item(name).Value. Each call has its own record.",
            p =>
            {
                p.GetBind("dprops", "db", "Properties");
                for (int i = 0; i < names.Length; i++)
                {
                    p.Get("db", names[i]).Item("pi" + i, "dprops", A.Str(names[i])).Get("pi" + i, "Value");
                }
            });
    }

    // TableDef and Field ------------------------------------------------------------------------------

    private static IEnumerable<Probe> TableDefsFields()
    {
        yield return Make("tabledef-members-each", Refs("DAO-MANIFEST", "DAO-META"),
            "DAO-MANIFEST applicability: every TableDef in TableDefs (bounded at 32, in collection order, system and linked entries included if listed): " +
            "each of Attributes, ConflictTable, Connect, DateCreated, LastUpdated, Name, RecordCount, ReplicaFilter, SourceTableName, Updatable, " +
            "ValidationRule and ValidationText by its own call, then Count of Fields, Indexes and Properties.",
            p => p.GetBind("tds", "db", "TableDefs")
                .Each("tds", "t", 32, b => Members(b, "t", TableDefMembers, TableDefCollections, "t")).Release("tds"));

        yield return Make("tabledef-properties-enumerated", Refs("DAO-PROP"),
            "DAO-PROP: Properties of TableDef Parent (with the appended user property Description), Child (a relation partner) and Typed, " +
            "Name, Type, Inherited and Value of each member read by separate calls.",
            p =>
            {
                foreach (string table in new[] { "Parent", "Child", "Typed" })
                {
                    Table(p, "tx" + table, table);
                    PropEnum(p, "tx" + table, "tp" + table);
                }
            });

        yield return Make("field-members-typed-each", Refs("DAO-MANIFEST", "DAO-ID"),
            "DAO-MANIFEST applicability: every Field of table Typed (bounded at 24; one field of each common type, with attribute edits on " +
            "TText, TLong, TInt and TDouble): each of AllowZeroLength, Attributes, CollatingOrder, CollectionIndex, ComplexType, DataUpdatable, " +
            "DefaultValue, Expression, FieldSize, ForeignName, Name, OrdinalPosition, OriginalValue, Required, Size, SourceField, SourceTable, Type, " +
            "ValidateOnSet, ValidationRule, ValidationText, Value and VisibleValue by its own call, then Properties Count.",
            p => Table(p, "t", "Typed").GetBind("tfl", "t", "Fields")
                .Each("tfl", "f", 24, b => Members(b, "f", FieldMembers, FieldCollections, "f")));

        (string Table, string Field)[] fields =
        [
            ("Typed", "K"), ("Typed", "TText"), ("Typed", "TMemo"), ("Typed", "TLong"), ("Typed", "TInt"), ("Typed", "TDate"), ("Typed", "TOle"),
            ("Typed", "TDec"), ("Parent", "Name"),
        ];
        yield return Make("field-properties-enumerated", Refs("DAO-PROP", "DAO-META"),
            "DAO-PROP: Properties of the table-level Fields Typed.K (AutoNumber), TText, TMemo, TLong, TInt (validation rule), TDate, TOle, TDec " +
            "(Decimal, ACCDB only) and Parent.Name (appended user property Description): Name, Type, Inherited and Value of each member, " +
            "each read separately.",
            p =>
            {
                for (int i = 0; i < fields.Length; i++)
                {
                    Table(p, "ft" + i, fields[i].Table).GetBind("ffl" + i, "ft" + i, "Fields").Item("ff" + i, "ffl" + i, A.Str(fields[i].Field));
                    PropEnum(p, "ff" + i, "fp" + i);
                }
            });
    }

    // Index, IndexFields and Relation -----------------------------------------------------------------

    private static IEnumerable<Probe> IndexesRelations()
    {
        yield return Make("index-members-each", Refs("DAO-MANIFEST", "II-05"),
            "DAO-MANIFEST applicability / II-05: every Index of Parent, Child, Typed and Grand (bounded at 16 each, including indexes the engine " +
            "created for primary keys and relationships): each of Clustered, DistinctCount, Foreign, IgnoreNulls, Name, Primary, Required and " +
            "Unique by its own call, then Count of Fields and Properties.",
            p =>
            {
                foreach (string table in new[] { "Parent", "Child", "Typed", "Grand" })
                {
                    Table(p, "ti" + table, table).GetBind("ix" + table, "ti" + table, "Indexes")
                        .Each("ix" + table, "x", 16, b => Members(b, "x", IndexMembers, IndexCollections, "x")).Release("ix" + table);
                }
            });

        yield return Make("index-properties-enumerated", Refs("DAO-PROP", "II-05"),
            "DAO-PROP: Properties of every Index of Parent and of Child (bounded at 16 each): Name, Type, Inherited and Value of each member, " +
            "each read separately.",
            p =>
            {
                foreach (string table in new[] { "Parent", "Child" })
                {
                    Table(p, "ti" + table, table).GetBind("ix" + table, "ti" + table, "Indexes")
                        .Each("ix" + table, "x", 16, b => PropEnum(b, "x", "xp")).Release("ix" + table);
                }
            });

        yield return Make("indexfield-members-each", Refs("DAO-MANIFEST", "II-05"),
            "DAO-MANIFEST applicability / II-05: for each Index of Parent and of Child, Name, then each member of Index.Fields (bounded at 8) with " +
            "each of AllowZeroLength, Attributes, CollatingOrder, CollectionIndex, ComplexType, DataUpdatable, DefaultValue, Expression, FieldSize, " +
            "ForeignName, Name, OrdinalPosition, OriginalValue, Required, Size, SourceField, SourceTable, Type, ValidateOnSet, ValidationRule, " +
            "ValidationText, Value and VisibleValue by its own call, then Properties Count.",
            p =>
            {
                foreach (string table in new[] { "Parent", "Child" })
                {
                    Table(p, "ti" + table, table).GetBind("ix" + table, "ti" + table, "Indexes")
                        .Each("ix" + table, "x", 16, b => b.Get("x", "Name").GetBind("xf", "x", "Fields")
                            .Each("xf", "y", 8, c => Members(c, "y", FieldMembers, FieldCollections, "y")).Release("xf")).Release("ix" + table);
                }
            });

        yield return Make("index-create-readback", Refs("DAO-DRAFT", "DAO-COLL", "II-05"),
            "DAO-DRAFT/II-05 on a writable copy: on table Typed, an Index IX_Typed_New with fields TText (Attributes dbDescending 1) and TLong, " +
            "IgnoreNulls True and Unique True set on the draft, is appended. The draft's and the re-fetched Index's members (Clustered, " +
            "DistinctCount, Foreign, IgnoreNulls, Name, Primary, Required, Unique) are read, then the Name and Attributes of each Index.Fields " +
            "member, then Indexes.Refresh and DistinctCount again.",
            p =>
            {
                Table(p, "t", "Typed").GetBind("idxs", "t", "Indexes")
                    .CallBind("nx", "t", "CreateIndex", A.Str("IX_Typed_New"))
                    .CallBind("nxa", "nx", "CreateField", A.Str("TText")).Put("nxa", "Attributes", A.I4(1))
                    .CallBind("nxb", "nx", "CreateField", A.Str("TLong"))
                    .GetBind("nxf", "nx", "Fields").Call("nxf", "Append", A.Obj("nxa")).Call("nxf", "Append", A.Obj("nxb"))
                    .Put("nx", "IgnoreNulls", A.Bool(true)).Put("nx", "Unique", A.Bool(true))
                    .Call("idxs", "Append", A.Obj("nx"));
                Reads(p, "nx", IndexMembers).Item("rx", "idxs", A.Str("IX_Typed_New"));
                Reads(p, "rx", IndexMembers).GetBind("rxf", "rx", "Fields").Each("rxf", "y", 8, b => b.Get("y", "Name").Get("y", "Attributes"))
                    .Call("idxs", "Refresh").Get("rx", "DistinctCount").Get("nx", "DistinctCount");
            },
            writable: true);

        yield return Make("relation-members-each", Refs("DAO-MANIFEST", "DAO-META"),
            "DAO-MANIFEST applicability: every Relation (bounded at 16; RelMain with the update-cascade attribute, FK_Grand from REFERENCES, and any " +
            "engine-visible others): each of Attributes, ForeignTable, Name, PartialReplica and Table by its own call, then Count of Fields and Properties.",
            p => p.GetBind("rels", "db", "Relations")
                .Each("rels", "r", 16, b => Members(b, "r", RelationMembers, RelationCollections, "r")).Release("rels"));

        yield return Make("relationfield-members-each", Refs("DAO-MANIFEST", "DAO-META"),
            "DAO-MANIFEST applicability: every member of Relation.Fields (bounded at 8) of every Relation, with each of AllowZeroLength, Attributes, " +
            "CollatingOrder, CollectionIndex, ComplexType, DataUpdatable, DefaultValue, Expression, FieldSize, ForeignName, Name, OrdinalPosition, " +
            "OriginalValue, Required, Size, SourceField, SourceTable, Type, ValidateOnSet, ValidationRule, ValidationText, Value and VisibleValue " +
            "by its own call, then Properties Count.",
            p => p.GetBind("rels", "db", "Relations")
                .Each("rels", "r", 16, b => b.Get("r", "Name").GetBind("rf", "r", "Fields")
                    .Each("rf", "y", 8, c => Members(c, "y", FieldMembers, FieldCollections, "y")).Release("rf")).Release("rels"));

        yield return Make("relation-properties-enumerated", Refs("DAO-PROP"),
            "DAO-PROP: Properties of every Relation (bounded at 16): Name, Type, Inherited and Value of each member, each read separately.",
            p => p.GetBind("rels", "db", "Relations").Each("rels", "r", 16, b => PropEnum(b.Get("r", "Name"), "r", "rp")).Release("rels"));
    }

    // QueryDef and Parameter --------------------------------------------------------------------------

    private static IEnumerable<Probe> QueryDefs()
    {
        yield return Make("querydef-members-each", Refs("DAO-MANIFEST", "DAO-QUERY"),
            "DAO-MANIFEST applicability: every saved QueryDef (bounded at 16; select, parameter, update, delete, append, make-table, crosstab and " +
            "union queries): each of CacheSize, Connect, DateCreated, hStmt, LastUpdated, LogMessages, MaxRecords, Name, ODBCTimeout, Prepare, " +
            "RecordLocks, RecordsAffected, ReturnsRecords, SQL, StillExecuting, Type and Updatable by its own call, then Count of Fields, " +
            "Parameters and Properties.",
            p => p.GetBind("qds", "db", "QueryDefs")
                .Each("qds", "q", 16, b => Members(b, "q", QueryDefMembers, QueryDefCollections, "q")).Release("qds"));

        yield return Make("querydef-fields-members", Refs("DAO-MANIFEST", "DAO-QUERY", "DAO-ID"),
            "DAO-MANIFEST applicability: QueryDef.Fields of QSel, QParam (parameters untouched), QUnion and QCross read without opening a Recordset: " +
            "Count, then for each member (bounded at 16) each of AllowZeroLength, Attributes, CollatingOrder, CollectionIndex, ComplexType, " +
            "DataUpdatable, DefaultValue, Expression, FieldSize, ForeignName, Name, OrdinalPosition, OriginalValue, Required, Size, SourceField, " +
            "SourceTable, Type, ValidateOnSet, ValidationRule, ValidationText, Value and VisibleValue by its own call, then Properties Count.",
            p =>
            {
                p.GetBind("qds", "db", "QueryDefs");
                foreach (string query in new[] { "QSel", "QParam", "QUnion", "QCross" })
                {
                    p.Item("qd" + query, "qds", A.Str(query)).GetBind("qf" + query, "qd" + query, "Fields")
                        .Each("qf" + query, "f", 16, b => Members(b, "f", FieldMembers, FieldCollections, "f")).Release("qf" + query);
                }
            });

        yield return Make("querydef-properties-enumerated", Refs("DAO-PROP", "DAO-QUERY"),
            "DAO-PROP: Properties of every saved QueryDef (bounded at 16): Name, Type, Inherited and Value of each member, each read separately.",
            p => p.GetBind("qds", "db", "QueryDefs").Each("qds", "q", 16, b => PropEnum(b.Get("q", "Name"), "q", "qp")).Release("qds"));

        yield return Make("parameter-members-each", Refs("DAO-MANIFEST", "DAO-QUERY"),
            "DAO-MANIFEST applicability: Parameters of QParam (pId, pName), QUpd (pDelta) and QSel (none), each member (bounded at 8) with Direction, " +
            "Name, Type and Value by its own call, then Properties Count; Parameters.Count first. Nothing is set.",
            p =>
            {
                p.GetBind("qds", "db", "QueryDefs");
                foreach (string query in new[] { "QParam", "QUpd", "QSel" })
                {
                    p.Item("qd" + query, "qds", A.Str(query)).GetBind("qp" + query, "qd" + query, "Parameters").Get("qp" + query, "Count")
                        .Each("qp" + query, "pm", 8, b => Members(b, "pm", ParameterMembers, FieldCollections, "pm")).Release("qp" + query);
                }
            });

        yield return Make("parameter-properties-enumerated", Refs("DAO-PROP", "DAO-QUERY"),
            "DAO-PROP: Properties of each Parameter of QParam and QUpd (bounded at 8): Name, Type, Inherited and Value of each member, each read separately.",
            p =>
            {
                p.GetBind("qds", "db", "QueryDefs");
                foreach (string query in new[] { "QParam", "QUpd" })
                {
                    p.Item("qd" + query, "qds", A.Str(query)).GetBind("qp" + query, "qd" + query, "Parameters")
                        .Each("qp" + query, "pm", 8, b => PropEnum(b.Get("pm", "Name"), "pm", "pp")).Release("qp" + query);
                }
            });

        yield return Make("querydef-state-after-execute", Refs("DAO-QUERY", "DAO-MANIFEST"),
            "DAO-QUERY on a writable copy: QUpd's state members RecordsAffected, StillExecuting, LastUpdated, ReturnsRecords and Type are read, " +
            "pDelta is set to 1, Execute is called, and the same members are read again together with Database.RecordsAffected. Then QSel opens " +
            "a snapshot; RecordsAffected, StillExecuting and ReturnsRecords of QSel, Recordsets Count of the Database and the snapshot's Name are read.",
            p =>
            {
                string[] state = ["RecordsAffected", "StillExecuting", "LastUpdated", "ReturnsRecords", "Type"];
                p.GetBind("qds", "db", "QueryDefs").Item("qu", "qds", A.Str("QUpd")).GetBind("qpm", "qu", "Parameters").Item("pd", "qpm", A.Str("pDelta"));
                Reads(p, "qu", state).Put("pd", "Value", A.I4(1)).Call("qu", "Execute");
                Reads(p, "qu", state).Get("db", "RecordsAffected");
                p.Item("qs", "qds", A.Str("QSel")).CallBind("srs", "qs", "OpenRecordset", A.I4(Dao.OpenSnapshot));
                Reads(p, "qs", ["RecordsAffected", "StillExecuting", "ReturnsRecords"]);
                Counts(p, "db", ["Recordsets"], "d").Get("srs", "Name").Call("srs", "Close");
            },
            writable: true);
    }

    // Recordset ---------------------------------------------------------------------------------------

    private static Probe RecordsetMembersProbe(string name, string description, Action<P> open, Action<P>? between = null) =>
        Make(name, Refs("DAO-MANIFEST", "II-07", "DAO-ID"), description,
            p =>
            {
                open(p);
                Members(p, "rs", RecordsetMembers, RecordsetCollections, "r");
                between?.Invoke(p);
                p.Call("rs", "Close");
            });

    private static IEnumerable<Probe> Recordsets()
    {
        const string list =
            "each of AbsolutePosition, BatchCollisionCount, BatchCollisions, BatchSize, BOF, Bookmark, Bookmarkable, CacheSize, CacheStart, " +
            "Connection, DateCreated, EditMode, EOF, Filter, hStmt, Index, LastModified, LastUpdated, LockEdits, LockType, MaxRecords, Name, " +
            "NoMatch, PercentPosition, RecordCount, RecordStatus, Restartable, Sort, StillExecuting, Transactions, Type, Updatable, UpdateOptions, " +
            "ValidationRule and ValidationText by its own call, then Count of Fields and Properties";

        yield return RecordsetMembersProbe("recordset-members-table",
            "DAO-MANIFEST applicability: a table-type Recordset on Parent, " + list + ". Then Index is put to 'PK_Parent' and Index, Bookmark, " +
            "AbsolutePosition, PercentPosition, RecordCount, BOF, EOF and NoMatch are read again.",
            p => p.OpenRs("rs", "db", "Parent", Dao.OpenTable),
            p => Reads(p.Put("rs", "Index", A.Str("PK_Parent")), "rs", ["Index", "Bookmark", "AbsolutePosition", "PercentPosition", "RecordCount", "BOF", "EOF", "NoMatch"]));

        yield return RecordsetMembersProbe("recordset-members-dynaset",
            "DAO-MANIFEST applicability: a dynaset Recordset on Parent, at its first row, " + list + ".",
            p => p.OpenRs("rs", "db", "Parent", Dao.OpenDynaset));

        yield return RecordsetMembersProbe("recordset-members-snapshot",
            "DAO-MANIFEST applicability: a snapshot Recordset on Parent, at its first row, " + list + ".",
            p => p.OpenRs("rs", "db", "Parent", Dao.OpenSnapshot));

        yield return RecordsetMembersProbe("recordset-members-forward-only",
            "DAO-MANIFEST applicability: a forward-only Recordset on Parent, at its first row, " + list + ".",
            p => p.OpenRs("rs", "db", "Parent", Dao.OpenForwardOnly));

        yield return RecordsetMembersProbe("recordset-members-query-snapshot",
            "DAO-MANIFEST applicability: a snapshot opened through QueryDef.OpenRecordset on the union query QUnion, at its first row, " + list + ".",
            p => p.GetBind("qds", "db", "QueryDefs").Item("qd", "qds", A.Str("QUnion")).CallBind("rs", "qd", "OpenRecordset", A.I4(Dao.OpenSnapshot)));

        yield return RecordsetMembersProbe("recordset-members-at-eof",
            "DAO-MANIFEST applicability: a dynaset on Parent after MoveLast and one MoveNext (cursor past the end), " + list + ".",
            p => p.OpenRs("rs", "db", "Parent", Dao.OpenDynaset).Call("rs", "MoveLast").Call("rs", "MoveNext"));

        yield return Make("recordset-fields-members-each", Refs("DAO-MANIFEST", "DAO-ID", "II-07"),
            "DAO-MANIFEST applicability: Fields of a dynaset on Typed at its first row, each member (bounded at 24) with each of AllowZeroLength, " +
            "Attributes, CollatingOrder, CollectionIndex, ComplexType, DataUpdatable, DefaultValue, Expression, FieldSize, ForeignName, Name, " +
            "OrdinalPosition, OriginalValue, Required, Size, SourceField, SourceTable, Type, ValidateOnSet, ValidationRule, ValidationText, Value " +
            "and VisibleValue by its own call, then Properties Count.",
            p => p.OpenRs("rs", "db", "Typed", Dao.OpenDynaset).GetBind("rfl", "rs", "Fields")
                .Each("rfl", "f", 24, b => Members(b, "f", FieldMembers, FieldCollections, "f")).Call("rs", "Close"));

        foreach ((string label, int type) in new[] { ("dynaset", Dao.OpenDynaset), ("snapshot", Dao.OpenSnapshot), ("table", Dao.OpenTable), ("forward-only", Dao.OpenForwardOnly) })
        {
            yield return Make("recordset-properties-" + label, Refs("DAO-PROP", "II-07"),
                $"DAO-PROP: Properties of a {label} Recordset on Parent, at its first row: Name, Type, Inherited and Value of each member, each read separately.",
                p => PropEnum(p.OpenRs("rs", "db", "Parent", type), "rs", "rp").Call("rs", "Close"));
        }
    }

    // Property, Container and Document ----------------------------------------------------------------

    private static IEnumerable<Probe> PropertiesContainers()
    {
        yield return Make("property-members-user-types", Refs("DAO-PROP"),
            "DAO-PROP: the appended user properties UPText, UPLong, UPBool, UPDate, UPDouble and UPMemo of the Database, Description of TableDef " +
            "Parent and of its Field [Name]; for each, found by Properties.Item(name), each of Inherited, Name, Type, Value, Category and " +
            "Attributes (the last two are names the type library may not list) by its own call.",
            p =>
            {
                p.GetBind("dprops", "db", "Properties");
                foreach (string name in new[] { "UPText", "UPLong", "UPBool", "UPDate", "UPDouble", "UPMemo" })
                {
                    Reads(p.Item("up" + name, "dprops", A.Str(name)), "up" + name, PropertyMembers);
                }
                Table(p, "t", "Parent").GetBind("tprops", "t", "Properties").Item("upTd", "tprops", A.Str("Description"));
                Reads(p, "upTd", PropertyMembers);
                p.GetBind("tfl", "t", "Fields").Item("tf", "tfl", A.Str("Name")).GetBind("fprops", "tf", "Properties")
                    .Item("upFd", "fprops", A.Str("Description"));
                Reads(p, "upFd", PropertyMembers);
            });

        (string Label, int Type, Arg Value)[] types =
        [
            ("bool", 1, A.Bool(true)), ("byte", 2, A.UI1(200)), ("integer", 3, A.I2(-300)), ("long", 4, A.I4(70000)), ("currency", 5, A.Cy(12.3456m)),
            ("single", 6, A.R4(1.5f)), ("double", 7, A.R8(2.5)), ("date", 8, A.Date(new DateTime(2026, 1, 2, 3, 4, 5))), ("binary", 9, A.Bytes(1, 2, 3)),
            ("text", 10, A.Str("txt")), ("longbinary", 11, A.Bytes(4, 5)), ("memo", 12, A.Str("memo")),
            ("guid", 15, A.Bytes(0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF, 0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77, 0x88, 0x99, 0x00)),
            ("bigint", 16, A.I4(5)), ("decimal", 20, A.R8(1.25)),
        ];
        yield return Make("property-append-all-types", Refs("DAO-PROP", "DAO-DRAFT"),
            "DAO-PROP on a writable copy: for each DAO property type Boolean 1, Byte 2, Integer 3, Long 4, Currency 5, Single 6, Double 7, Date 8, " +
            "Binary 9, Text 10, LongBinary 11, Memo 12, GUID 15, BigInt 16 and Decimal 20, a Property AP_<type> is created on the Database with a " +
            "constant of the matching VARIANT type; Inherited, Type and Value of the draft are read; it is appended; Type, Value and Inherited " +
            "are read again from the held object and from Properties.Item(name). Each append's own result is in its call record.",
            p =>
            {
                p.GetBind("dprops", "db", "Properties");
                foreach ((string label, int type, Arg value) in types)
                {
                    string n = "ap" + label;
                    p.CallBind(n, "db", "CreateProperty", A.Str("AP_" + label), A.I4(type), value).Get(n, "Inherited").Get(n, "Type").Get(n, "Value")
                        .Call("dprops", "Append", A.Obj(n)).Get(n, "Type").Get(n, "Value").Get(n, "Inherited")
                        .Item(n + "i", "dprops", A.Str("AP_" + label)).Get(n + "i", "Type").Get(n + "i", "Value");
                }
            },
            writable: true);

        yield return Make("containers-members-each", Refs("DAO-MANIFEST", "DAO-META"),
            "DAO-MANIFEST applicability / DAO-META: every Container (bounded at 16): each of AllPermissions, Inherit, Name, Owner, Permissions " +
            "and UserName by its own call, then Count of Documents and Properties.",
            p => p.GetBind("ctrs", "db", "Containers")
                .Each("ctrs", "c", 16, b => Members(b, "c", ContainerMembers, ContainerCollections, "c")).Release("ctrs"));

        yield return Make("containers-properties-enumerated", Refs("DAO-PROP", "DAO-META"),
            "DAO-PROP: Properties of every Container (bounded at 16): Name, Type, Inherited and Value of each member, each read separately.",
            p => p.GetBind("ctrs", "db", "Containers").Each("ctrs", "c", 16, b => PropEnum(b.Get("c", "Name"), "c", "cp")).Release("ctrs"));

        yield return Make("documents-members-each", Refs("DAO-MANIFEST", "DAO-META"),
            "DAO-MANIFEST applicability / DAO-META: for every Container (bounded at 16), Name, then every Document (bounded at 48): each of " +
            "AllPermissions, Container, DateCreated, LastUpdated, Name, Owner, Permissions and UserName by its own call, then Properties Count.",
            p => p.GetBind("ctrs", "db", "Containers")
                .Each("ctrs", "c", 16, b => b.Get("c", "Name").GetBind("cd", "c", "Documents")
                    .Each("cd", "d", 48, c => Members(c, "d", DocumentMembers, FieldCollections, "d")).Release("cd")).Release("ctrs"));

        yield return Make("documents-properties-enumerated", Refs("DAO-PROP", "DAO-META"),
            "DAO-PROP: Properties of every Document of the Tables container (bounded at 48): Name, Type, Inherited and Value of each member, " +
            "each read separately.",
            p => p.GetBind("ctrs", "db", "Containers").Item("ct", "ctrs", A.Str("Tables")).GetBind("cd", "ct", "Documents")
                .Each("cd", "d", 48, b => PropEnum(b.Get("d", "Name"), "d", "dp")).Release("cd"));

        yield return Make("documents-lookup-by-live-names", Refs("DAO-META", "DAO-COLL"),
            "DAO-META: Documents.Item(name), with Name and Container read, in the Tables container for the table Parent, the saved queries QSel " +
            "and QMake, the system table MSysObjects, the make-table target Made (never created), and a lower-case 'parent'; in the Relationships " +
            "container for RelMain, FK_Grand and a missing name; in the Databases container for 'MSysDb' and a missing name.",
            p =>
            {
                p.GetBind("ctrs", "db", "Containers");
                foreach ((string container, string[] documents) in new (string, string[])[]
                {
                    ("Tables", ["Parent", "QSel", "QMake", "MSysObjects", "Made", "parent"]),
                    ("Relationships", ["RelMain", "FK_Grand", "NoSuchRelation"]),
                    ("Databases", ["MSysDb", "NoSuchDocument"]),
                })
                {
                    p.Item("c" + container, "ctrs", A.Str(container)).GetBind("d" + container, "c" + container, "Documents");
                    foreach (string document in documents)
                    {
                        string b = $"{container}-{document}";
                        p.Item(b, "d" + container, A.Str(document)).Get(b, "Name").Get(b, "Container");
                    }
                }
            });
    }

    // Collections: Item, Refresh, Append and Delete -----------------------------------------------------

    private static IEnumerable<Probe> Collections()
    {
        yield return Make("coll-item-top-level", Refs("DAO-COLL", "DAO-ID", "ERR-BIND"),
            "DAO-COLL: Item(0), Item(99), Item(-1) and Item by name, with Name read from each result, on Workspaces ('#Default Workspace#', its " +
            "lower-case form, 'NoSuchWs'), Databases ('NoSuchDb'), Users ('Admin', 'ADMIN', 'NoSuchUser'), Groups ('Admins', 'admins', 'NoSuchGroup') " +
            "and Containers ('Tables', 'tables', 'Relationships', 'NoSuchContainer'). The Item call's own record shows how each lookup resolved.",
            p =>
            {
                Ws0(p).GetBind("dbs", "ws", "Databases").GetBind("us", "ws", "Users").GetBind("gs", "ws", "Groups").GetBind("ctrs", "db", "Containers");
                ItemSweep(p, "wss", "w", "#Default Workspace#", "#default workspace#", "NoSuchWs");
                ItemSweep(p, "dbs", "d", "NoSuchDb");
                ItemSweep(p, "us", "u", "Admin", "ADMIN", "NoSuchUser");
                ItemSweep(p, "gs", "g", "Admins", "admins", "NoSuchGroup");
                ItemSweep(p, "ctrs", "c", "Tables", "tables", "Relationships", "NoSuchContainer");
            });

        yield return Make("coll-item-table-family", Refs("DAO-COLL", "DAO-ID", "ERR-BIND"),
            "DAO-COLL: Item(0), Item(99), Item(-1) and Item by name, with Name read from each result, on the Fields of Parent ('Name', 'name', " +
            "'NoSuchField'), the Indexes of Parent ('PK_Parent', 'pk_parent', 'NoSuchIndex'), the Fields of Index UX_Parent_Name ('Name', " +
            "'NoSuchField'), the Relations of the Database ('RelMain', 'relmain', 'NoSuchRelation') and the Fields of Relation RelMain ('ID', 'PID', " +
            "'NoSuchField').",
            p =>
            {
                Table(p, "t", "Parent").GetBind("tf", "t", "Fields").GetBind("ti", "t", "Indexes").Item("ux", "ti", A.Str("UX_Parent_Name"))
                    .GetBind("uf", "ux", "Fields").GetBind("rels", "db", "Relations").Item("rm", "rels", A.Str("RelMain")).GetBind("rf", "rm", "Fields");
                ItemSweep(p, "tf", "f", "Name", "name", "NoSuchField");
                ItemSweep(p, "ti", "i", "PK_Parent", "pk_parent", "NoSuchIndex");
                ItemSweep(p, "uf", "if", "Name", "NoSuchField");
                ItemSweep(p, "rels", "r", "RelMain", "relmain", "NoSuchRelation");
                ItemSweep(p, "rf", "rf", "ID", "PID", "NoSuchField");
            });

        yield return Make("coll-item-query-family", Refs("DAO-COLL", "DAO-ID", "DAO-QUERY", "ERR-BIND"),
            "DAO-COLL: Item(0), Item(99), Item(-1) and Item by name, with Name read from each result, on the QueryDefs ('QSel', 'qsel', 'NoSuchQuery'), " +
            "the Parameters of QParam ('pId', 'PID', 'NoSuchParam'), the Fields of QSel ('ID', 'NoSuchField'), the Database's Properties ('Version', " +
            "'version', 'NoSuchProp'), the Documents of the Tables container ('Parent', 'NoSuchDocument') and the Fields of a snapshot on Parent " +
            "('Name', 'name', 'Parent.Name', 'NoSuchField').",
            p =>
            {
                p.GetBind("qds", "db", "QueryDefs").Item("qp", "qds", A.Str("QParam")).GetBind("pms", "qp", "Parameters")
                    .Item("qs", "qds", A.Str("QSel")).GetBind("qsf", "qs", "Fields").GetBind("dprops", "db", "Properties")
                    .GetBind("ctrs", "db", "Containers").Item("ct", "ctrs", A.Str("Tables")).GetBind("cd", "ct", "Documents")
                    .OpenRs("rs", "db", "Parent", Dao.OpenSnapshot).GetBind("rfl", "rs", "Fields");
                ItemSweep(p, "qds", "q", "QSel", "qsel", "NoSuchQuery");
                ItemSweep(p, "pms", "p", "pId", "PID", "NoSuchParam");
                ItemSweep(p, "qsf", "f", "ID", "NoSuchField");
                ItemSweep(p, "dprops", "pr", "Version", "version", "NoSuchProp");
                ItemSweep(p, "cd", "d", "Parent", "NoSuchDocument");
                ItemSweep(p, "rfl", "r", "Name", "name", "Parent.Name", "NoSuchField");
                p.Call("rs", "Close");
            });

        yield return Make("coll-newenum-get", Refs("DAO-COLL", "DAO-ENUM"),
            "DAO-ENUM: the hidden member _NewEnum read as a property get on each collection: Workspaces, Databases, TableDefs, Fields of Parent, " +
            "Indexes of Parent, Index.Fields, Relations, QueryDefs, Parameters of QParam, Properties of the Database, Containers, Documents of Tables, " +
            "Errors, Users and Groups. Only the returned VARIANT (type, interface pointer) is captured; no enumerator method is called.",
            p =>
            {
                Ws0(p).GetBind("dbs", "ws", "Databases").GetBind("us", "ws", "Users").GetBind("gs", "ws", "Groups").GetBind("errs", "eng", "Errors")
                    .GetBind("tds", "db", "TableDefs").Item("t", "tds", A.Str("Parent")).GetBind("tf", "t", "Fields").GetBind("ti", "t", "Indexes")
                    .Item("ux", "ti", A.Str("UX_Parent_Name")).GetBind("uf", "ux", "Fields").GetBind("rels", "db", "Relations")
                    .GetBind("qds", "db", "QueryDefs").Item("qp", "qds", A.Str("QParam")).GetBind("pms", "qp", "Parameters")
                    .GetBind("dprops", "db", "Properties").GetBind("ctrs", "db", "Containers").Item("ct", "ctrs", A.Str("Tables"))
                    .GetBind("cd", "ct", "Documents");
                foreach (string collection in new[] { "wss", "dbs", "tds", "tf", "ti", "uf", "rels", "qds", "pms", "dprops", "ctrs", "cd", "errs", "us", "gs" })
                {
                    p.Get(collection, "_NewEnum");
                }
            });

        yield return Make("coll-refresh-each", Refs("DAO-COLL", "DAO-ENUM"),
            "DAO-COLL: for each collection (Workspaces, Databases, TableDefs, Fields of Parent, Indexes of Parent, Index.Fields of UX_Parent_Name, " +
            "Relations, Relation.Fields of RelMain, QueryDefs, Parameters of QParam, the Database's Properties, Containers, Documents of Tables, " +
            "Errors, Users, Groups and the Fields of a snapshot): Count, Refresh, Count. No change is made between the reads.",
            p =>
            {
                Ws0(p).GetBind("dbs", "ws", "Databases").GetBind("us", "ws", "Users").GetBind("gs", "ws", "Groups").GetBind("errs", "eng", "Errors")
                    .GetBind("tds", "db", "TableDefs").Item("t", "tds", A.Str("Parent")).GetBind("tf", "t", "Fields").GetBind("ti", "t", "Indexes")
                    .Item("ux", "ti", A.Str("UX_Parent_Name")).GetBind("uf", "ux", "Fields").GetBind("rels", "db", "Relations")
                    .Item("rm", "rels", A.Str("RelMain")).GetBind("rf", "rm", "Fields")
                    .GetBind("qds", "db", "QueryDefs").Item("qp", "qds", A.Str("QParam")).GetBind("pms", "qp", "Parameters")
                    .GetBind("dprops", "db", "Properties").GetBind("ctrs", "db", "Containers").Item("ct", "ctrs", A.Str("Tables"))
                    .GetBind("cd", "ct", "Documents").OpenRs("rs", "db", "Parent", Dao.OpenSnapshot).GetBind("rfl", "rs", "Fields");
                foreach (string collection in new[] { "wss", "dbs", "tds", "tf", "ti", "uf", "rels", "rf", "qds", "pms", "dprops", "ctrs", "cd", "errs", "us", "gs", "rfl" })
                {
                    p.Get(collection, "Count").Call(collection, "Refresh").Get(collection, "Count");
                }
                p.Call("rs", "Close");
            });

        yield return Make("coll-querydefs-cross-db-refresh", Refs("DAO-COLL", "DAO-QUERY"),
            "DAO-COLL on a writable copy: two writable Databases on one file in Workspaces(0). A QueryDefs held from db is read (Count, Item('QDel'), " +
            "names). Through db2 a QueryDef QNew2 is created and QDel is deleted. The held collection is read again (Count, names, Item('QNew2'), " +
            "Item('QDel')), then Refresh, then read again. A fresh QueryDefs from db is read last.",
            p =>
            {
                p.OpenDb("db2", readOnly: false).GetBind("qds", "db", "QueryDefs").Get("qds", "Count").Item("dq", "qds", A.Str("QDel")).Get("dq", "Name");
                Names(p, "qds", "n0");
                p.GetBind("qds2", "db2", "QueryDefs").CallBind("qn", "db2", "CreateQueryDef", A.Str("QNew2"), A.Str("SELECT ID FROM Parent;"))
                    .Call("qds2", "Delete", A.Str("QDel"));
                p.Get("qds", "Count");
                Names(p, "qds", "n1");
                p.Item("qa", "qds", A.Str("QNew2")).Get("qa", "Name").Item("qb", "qds", A.Str("QDel")).Get("qb", "Name")
                    .Call("qds", "Refresh").Get("qds", "Count");
                Names(p, "qds", "n2");
                p.Item("qc", "qds", A.Str("QNew2")).Get("qc", "Name").GetBind("qds3", "db", "QueryDefs").Get("qds3", "Count");
                Names(p, "qds3", "n3");
                p.Call("db2", "Close");
            },
            writable: true);

        yield return Make("coll-append-delete-unsupported", Refs("DAO-COLL", "DAO-DRAFT", "DAO-ID"),
            "DAO-COLL on a writable copy: Append and Delete called on collections not documented to support them: Containers.Delete('Tables') and " +
            "Containers.Append(a Container), Documents.Delete('Parent') and Documents.Append(a Container), Databases.Delete('x') and " +
            "Databases.Append(db), Workspaces.Delete('x') and Workspaces.Append(db), Errors.Append(db), the Fields of a snapshot on Parent " +
            "(Append of a draft Field Z, Delete('ID')), Index.Fields of PK_Parent (Delete('ID')), Relation.Fields of RelMain (Delete('ID')) and " +
            "QueryDef.Fields of QSel (Delete('ID')). Counts of Containers, Documents, TableDefs and the Fields are read afterwards.",
            p =>
            {
                Ws0(p).GetBind("dbs", "ws", "Databases").GetBind("errs", "eng", "Errors").GetBind("ctrs", "db", "Containers")
                    .Item("ct", "ctrs", A.Str("Tables")).GetBind("cd", "ct", "Documents").GetBind("tds", "db", "TableDefs")
                    .Item("t", "tds", A.Str("Parent")).GetBind("ti", "t", "Indexes").Item("pk", "ti", A.Str("PK_Parent")).GetBind("pkf", "pk", "Fields")
                    .GetBind("rels", "db", "Relations").Item("rm", "rels", A.Str("RelMain")).GetBind("rmf", "rm", "Fields")
                    .GetBind("qds", "db", "QueryDefs").Item("qs", "qds", A.Str("QSel")).GetBind("qsf", "qs", "Fields")
                    .OpenRs("rs", "db", "Parent", Dao.OpenSnapshot).GetBind("rfl", "rs", "Fields")
                    .CallBind("zf", "t", "CreateField", A.Str("Z"), A.I4(TypeLong));
                p.Call("ctrs", "Delete", A.Str("Tables")).Call("ctrs", "Append", A.Obj("ct"))
                    .Call("cd", "Delete", A.Str("Parent")).Call("cd", "Append", A.Obj("ct"))
                    .Call("dbs", "Delete", A.Str("x")).Call("dbs", "Append", A.Obj("db"))
                    .Call("wss", "Delete", A.Str("x")).Call("wss", "Append", A.Obj("db"))
                    .Call("errs", "Append", A.Obj("db"))
                    .Call("rfl", "Append", A.Obj("zf")).Call("rfl", "Delete", A.Str("ID"))
                    .Call("pkf", "Delete", A.Str("ID")).Call("rmf", "Delete", A.Str("ID")).Call("qsf", "Delete", A.Str("ID"))
                    .Get("ctrs", "Count").Get("cd", "Count").Get("tds", "Count").Get("rfl", "Count").Get("pkf", "Count").Get("rmf", "Count").Get("qsf", "Count");
                p.Call("rs", "Close");
            },
            writable: true);

        yield return Make("coll-delete-index-in-relation", Refs("DAO-COLL", "DAO-DRAFT", "II-05"),
            "DAO-COLL/II-05 on a writable copy: on Parent, Indexes.Delete('PK_Parent') (the key RelMain refers to), then Delete('UX_Parent_Name'), " +
            "Delete('NoSuchIndex'), Refresh; then Relations.Delete('RelMain') and Indexes.Delete('PK_Parent') again. After each step: Parent Indexes " +
            "Count and names, Relations Count, and the Indexes of Child with Name and Foreign.",
            p =>
            {
                Table(p, "t", "Parent").GetBind("ti", "t", "Indexes").Item("c", "tds", A.Str("Child")).GetBind("ci", "c", "Indexes")
                    .GetBind("rels", "db", "Relations");
                void Snapshot(string tag) =>
                    Names(p.Get("ti", "Count").Get("rels", "Count"), "ti", "pi" + tag, 16)
                        .Each("ci", "cx" + tag, 16, b => b.Get("cx" + tag, "Name").Get("cx" + tag, "Foreign"));
                Snapshot("0");
                p.Call("ti", "Delete", A.Str("PK_Parent"));
                Snapshot("1");
                p.Call("ti", "Delete", A.Str("UX_Parent_Name")).Call("ti", "Delete", A.Str("NoSuchIndex")).Call("ti", "Refresh");
                Snapshot("2");
                p.Call("rels", "Delete", A.Str("RelMain"));
                Snapshot("3");
                p.Call("ti", "Delete", A.Str("PK_Parent")).Call("ti", "Refresh").Call("ci", "Refresh").Call("rels", "Refresh");
                Snapshot("4");
            },
            writable: true);

        yield return Make("coll-delete-field-indexed", Refs("DAO-COLL", "DAO-DRAFT", "II-05"),
            "DAO-COLL/II-05 on a writable copy: Fields.Delete on Parent of 'Qty' (in two indexes), 'NoSuchField', 'Name' (in the unique index) and " +
            "'ID' (the key RelMain refers to), then Refresh; on Child Fields.Delete('PID') (RelMain's foreign column). After each step: Fields Count " +
            "and names of the table, Indexes Count and names, Relations Count.",
            p =>
            {
                Table(p, "t", "Parent").GetBind("tf", "t", "Fields").GetBind("ti", "t", "Indexes").Item("c", "tds", A.Str("Child"))
                    .GetBind("cf", "c", "Fields").GetBind("cx", "c", "Indexes").GetBind("rels", "db", "Relations");
                void Snapshot(string tag) =>
                    Names(Names(p.Get("tf", "Count").Get("ti", "Count").Get("cf", "Count").Get("rels", "Count"), "tf", "f" + tag, 16), "ti", "i" + tag, 16)
                        .Each("cx", "x" + tag, 16, b => b.Get("x" + tag, "Name"));
                Snapshot("0");
                int step = 1;
                foreach ((string collection, string name) in new[] { ("tf", "Qty"), ("tf", "NoSuchField"), ("tf", "Name"), ("tf", "ID"), ("cf", "PID") })
                {
                    p.Call(collection, "Delete", A.Str(name));
                    Snapshot(step++.ToString(System.Globalization.CultureInfo.InvariantCulture));
                }
                p.Call("tf", "Refresh").Call("ti", "Refresh").Call("cf", "Refresh").Call("cx", "Refresh").Call("rels", "Refresh");
                Snapshot(step.ToString(System.Globalization.CultureInfo.InvariantCulture));
            },
            writable: true);

        yield return Make("coll-delete-builtin-property", Refs("DAO-PROP", "DAO-COLL"),
            "DAO-PROP on a writable copy: Properties.Delete of built-in properties: on the Database 'Name', 'Version', 'CollatingOrder'; on TableDef " +
            "Parent 'Name'; on its Field [Name] 'Type'; each followed by the owner's Properties Count and the member read directly. Then deletion " +
            "of the appended user properties UPText on the Database (twice) and Description on Parent and on Field [Name], each followed by the " +
            "Count and Item of the deleted name.",
            p =>
            {
                Table(p, "t", "Parent").GetBind("tprops", "t", "Properties").GetBind("tf", "t", "Fields").Item("tn", "tf", A.Str("Name"))
                    .GetBind("fprops", "tn", "Properties").GetBind("dprops", "db", "Properties");
                foreach ((string owner, string props, string name) in new[]
                {
                    ("db", "dprops", "Name"), ("db", "dprops", "Version"), ("db", "dprops", "CollatingOrder"), ("t", "tprops", "Name"), ("tn", "fprops", "Type"),
                })
                {
                    p.Call(props, "Delete", A.Str(name)).Get(props, "Count").Get(owner, name);
                }
                foreach ((string props, string name) in new[] { ("dprops", "UPText"), ("dprops", "UPText"), ("tprops", "Description"), ("fprops", "Description") })
                {
                    string b = $"gone-{props}-{name}";
                    p.Call(props, "Delete", A.Str(name)).Get(props, "Count").Item(b, props, A.Str(name)).Get(b, "Name");
                }
            },
            writable: true);

        yield return Make("coll-append-field-existing-rows", Refs("DAO-COLL", "DAO-DRAFT"),
            "DAO-COLL/DAO-DRAFT on a writable copy: on Parent (five rows), a Required Long field ZReq (no default), an optional Long ZOpt and a Text " +
            "field ZTxt (size 10, AllowZeroLength True) are appended one after another. After each: Fields Count, then each Field's Name, " +
            "OrdinalPosition, Required and AllowZeroLength. Then Refresh and the same reads; then a snapshot of ID, ZOpt, ZTxt is read.",
            p =>
            {
                Table(p, "t", "Parent").GetBind("tf", "t", "Fields");
                void Snapshot(string tag) =>
                    p.Get("tf", "Count").Each("tf", "f" + tag, 16, b => b.Get("f" + tag, "Name").Get("f" + tag, "OrdinalPosition")
                        .Get("f" + tag, "Required").Get("f" + tag, "AllowZeroLength"));
                p.CallBind("zr", "t", "CreateField", A.Str("ZReq"), A.I4(TypeLong)).Put("zr", "Required", A.Bool(true)).Call("tf", "Append", A.Obj("zr"));
                Snapshot("0");
                p.CallBind("zo", "t", "CreateField", A.Str("ZOpt"), A.I4(TypeLong)).Call("tf", "Append", A.Obj("zo"));
                Snapshot("1");
                p.CallBind("zt", "t", "CreateField", A.Str("ZTxt"), A.I4(TypeText), A.I4(10)).Put("zt", "AllowZeroLength", A.Bool(true)).Call("tf", "Append", A.Obj("zt"));
                Snapshot("2");
                p.Call("tf", "Refresh");
                Snapshot("3");
                p.OpenRs("rs", "db", "SELECT ID, ZOpt, ZTxt FROM Parent ORDER BY ID", Dao.OpenSnapshot).ReadAll("rs", 8, 4).Call("rs", "Close");
            },
            writable: true);

        yield return Make("coll-fields-ordinalposition", Refs("DAO-COLL", "DAO-DRAFT"),
            "DAO-COLL on a writable copy: the Fields of Typed read with Name and OrdinalPosition (bounded at 16); OrdinalPosition of TLong is put to " +
            "0, then of TText to 99, then of K to -1, then of TInt to 2; after each put, the same read, and after the last put also after " +
            "Fields.Refresh and from a fresh Fields collection. A snapshot on Typed then reads the field order.",
            p =>
            {
                Table(p, "t", "Typed").GetBind("tf", "t", "Fields");
                void Snapshot(string tag) =>
                    p.Each("tf", "f" + tag, 16, b => b.Get("f" + tag, "Name").Get("f" + tag, "OrdinalPosition"));
                Snapshot("0");
                int step = 1;
                foreach ((string field, int position) in new[] { ("TLong", 0), ("TText", 99), ("K", -1), ("TInt", 2) })
                {
                    p.Item("o" + field, "tf", A.Str(field)).Put("o" + field, "OrdinalPosition", A.I4(position));
                    Snapshot(step++.ToString(System.Globalization.CultureInfo.InvariantCulture));
                }
                p.Call("tf", "Refresh");
                Snapshot("r");
                p.GetBind("tf2", "t", "Fields").Each("tf2", "g", 16, b => b.Get("g", "Name").Get("g", "OrdinalPosition")).Release("tf2");
                p.OpenRs("rs", "db", "Typed", Dao.OpenSnapshot).GetBind("rfl", "rs", "Fields").Each("rfl", "h", 16, b => b.Get("h", "Name")).Call("rs", "Close");
            },
            writable: true);
    }
}
