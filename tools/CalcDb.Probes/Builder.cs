using CalcDb.Evidence;

namespace CalcDb.Probes;

/// <summary>
/// One designed probe: a book card, a name unique within the card, the book IDs it serves, what it asks,
/// the named fixtures it opens (from <c>probes/fixtures.json</c>) and its operation program.
/// </summary>
public sealed record Probe(string Card, string Name, string[] Refs, string Description, Action<P> Program)
{
    /// <summary>Fixture names by role; the first is normally <c>main</c>.</summary>
    public (string Role, string Fixture)[] Fixtures { get; init; } = [];

    /// <summary>Runs before probes of the same card that use its output (fixture creation).</summary>
    public bool CreatesFixture { get; init; }

    /// <summary>
    /// Further participants (b, c, ...), each run at the same time in its own owned worker process on the same
    /// attempt files; order them with Signal/Await only. Participant a (Program) keeps the final files, so it
    /// should await the others' last signal before it ends.
    /// </summary>
    public (string Name, Action<P> Program)[] Participants { get; init; } = [];
}

/// <summary>Typed arguments. Every argument carries its VARIANT tag (T-EVID-01).</summary>
public static class A
{
    public static Arg I2(short v) => new I2Arg(v);

    public static Arg I4(int v) => new I4Arg(v);

    public static Arg UI1(byte v) => new UI1Arg(v);

    public static Arg Bool(bool v) => new BoolArg(v);

    /// <summary>A BSTR of exactly these code units; text with lone surrogates travels as exact bytes (bstrBytes).</summary>
    public static Arg Str(string v) => Hashing.IsWellFormedUtf16(v) ? new BstrArg(v) : BstrUnits(v);

    public static Arg Missing => new MissingArg();

    /// <summary>A BSTR with exactly these payload bytes (odd counts, embedded nulls, lone surrogates).</summary>
    public static Arg BstrBytes(params byte[] bytes) => new BstrBytesArg(Convert.ToHexStringLower(bytes));

    /// <summary>A BSTR with exactly these UTF-16 code units, including lone surrogates.</summary>
    public static Arg BstrUnits(string units) => BstrBytes([.. units.SelectMany(c => new[] { (byte)c, (byte)(c >> 8) })]);

    public static Arg Null => new NullArg();

    public static Arg Empty => new EmptyArg();

    public static Arg Obj(string name) => new ObjectArg(name);

    public static Arg In(string role = "main") => new InputPathArg(role);

    /// <summary>The same input path in another spelling: upper, extended, slashes or dot (EX-ALIAS).</summary>
    public static Arg InAs(string spelling, string role = "main") => new InputPathArg(role) { Spelling = spelling };

    public static Arg Out(string fileName) => new OutputPathArg(fileName);

    public static Arg R8(double v) => new R8Arg(((ulong)BitConverter.DoubleToInt64Bits(v)).ToString("x16"));

    public static Arg R8Bits(ulong bits) => new R8Arg(bits.ToString("x16"));

    public static Arg R4(float v) => new R4Arg(((uint)BitConverter.SingleToInt32Bits(v)).ToString("x8"));

    /// <summary>VT_DATE from an OLE Automation date Double.</summary>
    public static Arg Date(double oaDate) => new DateArg(((ulong)BitConverter.DoubleToInt64Bits(oaDate)).ToString("x16"));

    public static Arg Date(DateTime value) => Date(value.ToOADate());

    /// <summary>VT_CY from a decimal amount (scaled by 10,000; must be exact).</summary>
    public static Arg Cy(decimal amount)
    {
        decimal scaled = amount * 10000m;
        if (scaled != decimal.Truncate(scaled))
        {
            throw new ArgumentException("Currency has at most four decimal places.");
        }
        return new CyArg((long)scaled);
    }

    public static Arg Bytes(params byte[] bytes) => new BytesArg(Convert.ToHexStringLower(bytes));
}

/// <summary>
/// Operation program builder. Names bind objects (<c>eng</c>, <c>db</c>, <c>rs</c>, ...). Every call's return
/// value, HRESULT and error channels are captured by the worker; a failed call is an observation, and later
/// calls on an unbound name are recorded as skipped.
/// </summary>
public sealed class P
{
    internal List<Operation> Ops { get; } = [];

    public P Create(string progId = "DAO.DBEngine.120", string bind = "eng") => Add(new CreateObject(progId, bind));

    public P Modules(string label) => Add(new ObserveModules(label));

    public P Files(string label, bool keep = false) => Add(new ObserveFiles(label) { Keep = keep });

    /// <summary>Method call; the result is captured, and bound when <paramref name="bind"/> is given.</summary>
    public P Call(string target, string member, params Arg[] args) => Add(new Invoke(target, member, DispatchKind.Method, args));

    public P CallBind(string bind, string target, string member, params Arg[] args) =>
        Add(new Invoke(target, member, DispatchKind.Method, args) { Bind = bind });

    /// <summary>Property get (with optional index arguments).</summary>
    public P Get(string target, string member, params Arg[] args) => Add(new Invoke(target, member, DispatchKind.PropertyGet, args));

    public P GetBind(string bind, string target, string member, params Arg[] args) =>
        Add(new Invoke(target, member, DispatchKind.PropertyGet, args) { Bind = bind });

    /// <summary>Method-or-get, as VB late binding does (for example <c>Fields(0)</c> or <c>Item("x")</c>).</summary>
    public P Item(string bind, string target, Arg index) =>
        Add(new Invoke(target, "Item", DispatchKind.MethodOrGet, [index]) { Bind = bind });

    /// <summary>Property put: <c>target.member(index...) = value</c>.</summary>
    public P Put(string target, string member, Arg value, params Arg[] index) =>
        Add(new Invoke(target, member, DispatchKind.PropertyPut, [.. index, value]));

    /// <summary>Property put of an object reference (<c>Set target.member = obj</c>).</summary>
    public P PutRef(string target, string member, string obj) =>
        Add(new Invoke(target, member, DispatchKind.PropertyPutRef, [new ObjectArg(obj)]));

    public P Release(string target) => Add(new Release(target));

    public P Describe(string target, bool wholeLibrary = false) => Add(new DescribeType(target) { WholeLibrary = wholeLibrary });

    /// <summary>Mark a barrier point as reached for the other participants.</summary>
    public P Signal(string name) => Add(new Signal(name));

    /// <summary>Wait for another participant's signal (the wait and any timeout are recorded).</summary>
    public P Await(string name, int timeoutMs = 30000) => Add(new Await(name, timeoutMs));

    /// <summary>
    /// W-PROBE: CreateFileW with exact access/share/disposition on <c>input:&lt;role&gt;</c>, <c>lock:&lt;role&gt;</c> or
    /// <c>work:&lt;name&gt;</c>; see <see cref="Win"/> for the constants.
    /// </summary>
    public P NativeOpen(string bind, string file, uint access, uint share, uint disposition = Win.OpenExisting) =>
        Add(new NativeOpen(bind, file, access, share, disposition));

    public P NativeRead(string handle, long offset, int count) => Add(new NativeRead(handle, offset, count));

    public P NativeClose(string handle) => Add(new NativeClose(handle));

    /// <summary>Set or clear the read-only attribute of an attempt file (input:/lock:/work: reference).</summary>
    public P SetReadOnly(string file, bool readOnly = true) => Add(new SetReadOnly(file, readOnly));

    /// <summary>Create or overwrite an attempt file with exactly these bytes.</summary>
    public P WriteFile(string file, params byte[] bytes) => Add(new WriteFileBytes(file, Convert.ToHexStringLower(bytes)));

    /// <summary>Wait a fixed time, for example to let a killed participant's process settle.</summary>
    public P Pause(int ms) => Add(new Pause(ms));

    /// <summary>Requested process death without any release or close (R-CRASH-1 control; not power loss).</summary>
    public P TerminateSelf(int exitCode = 0x7E57) => Add(new TerminateSelf(exitCode));

    /// <summary>For each item of a collection (Count then Item(i), i from 0), bound to <paramref name="bind"/>.</summary>
    public P Each(string collection, string bind, int max, Action<P> body)
    {
        var inner = new P();
        body(inner);
        return Add(new ForEachItem(collection, bind, max, inner.Ops));
    }

    /// <summary>Repeat the body until the Boolean property reads True (for example <c>rs.EOF</c>).</summary>
    public P Until(string target, string member, int max, Action<P> body)
    {
        var inner = new P();
        body(inner);
        return Add(new LoopUntil(target, member, max, inner.Ops));
    }

    // DAO conveniences --------------------------------------------------------------------------------

    /// <summary>
    /// Add a Decimal field (DAO type 20) to an existing table through TableDef.CreateField and Fields.Append, on
    /// database <paramref name="db"/>. DAO DDL refuses DECIMAL (3292, R-FMT-1); this route gives DAO's default
    /// precision and scale, which the read-back records.
    /// </summary>
    public P AddDecimalField(string table, string field, string db = "db") =>
        GetBind("dtdfs", db, "TableDefs").Call("dtdfs", "Refresh").Item("dtdf", "dtdfs", A.Str(table))
            .CallBind("dfld", "dtdf", "CreateField", A.Str(field), A.I4(20)).GetBind("dfls", "dtdf", "Fields").Call("dfls", "Append", A.Obj("dfld"))
            .Release("dfls").Release("dfld").Release("dtdf").Release("dtdfs");

    /// <summary>
    /// Create a relationship through DAO (Database.CreateRelation, Relation.CreateField with ForeignName,
    /// Relations.Append). DAO DDL accepts REFERENCES in column-level, table-level and ALTER TABLE form
    /// (evidence/r-ddl-limits); this is the object-model route to the same relationship.
    /// </summary>
    public P AddRelation(string name, string table, string foreignTable, string field, string foreignField, int attributes = 0, string db = "db") =>
        CallBind("rel", db, "CreateRelation", A.Str(name), A.Str(table), A.Str(foreignTable), A.I4(attributes))
            .CallBind("relf", "rel", "CreateField", A.Str(field)).Put("relf", "ForeignName", A.Str(foreignField))
            .GetBind("relfs", "rel", "Fields").Call("relfs", "Append", A.Obj("relf"))
            .GetBind("rels", db, "Relations").Call("rels", "Append", A.Obj("rel"))
            .Release("rels").Release("relfs").Release("relf").Release("rel");

    /// <summary>The standard opening: file state, module identity, DBEngine, identity after activation.</summary>
    public P Engine()
    {
        Files("before-open");
        Modules("before-create");
        Create();
        Modules("after-create");
        return this;
    }

    /// <summary><c>Set db = eng.OpenDatabase(path, exclusive, readOnly)</c> on the input with this role.</summary>
    public P OpenDb(string bind = "db", string role = "main", bool exclusive = false, bool readOnly = true) =>
        CallBind(bind, "eng", "OpenDatabase", A.In(role), A.Bool(exclusive), A.Bool(readOnly));

    /// <summary><c>Set rs = db.OpenRecordset(source, type[, options[, lockEdit]])</c>.</summary>
    public P OpenRs(string bind, string db, string source, int type, params Arg[] more) =>
        CallBind(bind, db, "OpenRecordset", [A.Str(source), A.I4(type), .. more]);

    /// <summary>Read every field's Name and Value of the current record.</summary>
    public P ReadRow(string rs, int maxFields = 32) =>
        GetBind("flds", rs, "Fields").Each("flds", "f", maxFields, b => b.Get("f", "Value")).Release("flds");

    /// <summary>Read all rows to EOF (at most <paramref name="maxRows"/>).</summary>
    public P ReadAll(string rs, int maxRows = 64, int maxFields = 32) =>
        Until(rs, "EOF", maxRows, b => b.ReadRow(rs, maxFields).Call(rs, "MoveNext"));

    /// <summary>Read DBEngine.Errors: Number, Description, Source of each error.</summary>
    public P Errors() =>
        GetBind("errs", "eng", "Errors").Each("errs", "e", 16, b => b.Get("e", "Number").Get("e", "Description").Get("e", "Source")).Release("errs");

    /// <summary>The standard closing: close named objects in order, release everything, final identity and files.</summary>
    public P Finish(params string[] close)
    {
        foreach (string name in close)
        {
            Call(name, "Close");
        }
        Modules("after-close");
        Files("after-close");
        return this;
    }

    private P Add(Operation op)
    {
        Ops.Add(op);
        return this;
    }
}

/// <summary>Win32 CreateFile constants for the W-PROBE native driver.</summary>
public static class Win
{
    public const uint GenericRead = 0x80000000, GenericWrite = 0x40000000, Delete = 0x00010000, ReadAttributes = 0x80;
    public const uint ShareNone = 0, ShareRead = 1, ShareWrite = 2, ShareDelete = 4, ShareAll = 7;
    public const uint OpenExisting = 3;
}

/// <summary>DAO constants used by probes (values from the DAO type library, also captured by R-DAO-1).</summary>
public static class Dao
{
    public const int OpenTable = 1, OpenDynaset = 2, OpenSnapshot = 4, OpenForwardOnly = 8, OpenDynamic = 16;
    public const int Version40 = 64, Version120 = 128;
    public const string LangGeneral = ";LANGID=0x0409;CP=1252;COUNTRY=0";
    public const int FailOnError = 128, SeeChanges = 512, ReadOnly = 4, AppendOnly = 8, ForwardOnlyOpt = 256;
}
