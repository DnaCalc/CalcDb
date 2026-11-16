using System.Text.Json.Serialization;

namespace CalcDb.Evidence;

/// <summary>
/// An immutable oracle question (II-15 §2, F-184 <c>Question</c>): the exact operation program, its
/// starting fixtures, caller surface and observation recipe. Its <see cref="Key"/> is computed over the
/// semantic body only; <see cref="Description"/>, <see cref="Probe"/> and <see cref="BookRefs"/> label the
/// question for people and indexes and do not change which question it is.
/// </summary>
public sealed record Question
{
    public const string CurrentSchema = "calcdb.question/1";
    public const string KeyEncoding = "calcdb.question-key/1";

    public string Schema { get; init; } = CurrentSchema;

    /// <summary>Probe identity, for example <c>r-oracle-1/table-read</c>.</summary>
    public required string Probe { get; init; }

    public required IReadOnlyList<string> BookRefs { get; init; }

    public required string Description { get; init; }

    /// <summary>Caller surface, for example <c>native-idispatch/x64</c> (II-15 F-186 "Caller").</summary>
    public required string CallerSurface { get; init; }

    /// <summary>
    /// The LCID passed to <c>GetIDsOfNames</c> and <c>Invoke</c>. It is part of the question because it
    /// can select diagnostic text and conversions.
    /// </summary>
    public required int InvokeLcid { get; init; }

    /// <summary>
    /// Ordered observation channels, for example <c>clearErrorInfoBefore</c>, <c>excepinfo</c>,
    /// <c>deferredFill</c>, <c>errorInfo</c>. Observation is part of the operation program (II-15 §6, OR-OBS).
    /// </summary>
    public required IReadOnlyList<string> ObservationRecipe { get; init; }

    /// <summary>How fixtures bind to paths. Currently only <c>attempt-local-copy</c>.</summary>
    public string PathBinding { get; init; } = "attempt-local-copy";

    public IReadOnlyList<FixtureInput> Inputs { get; init; } = [];

    public required IReadOnlyList<Operation> Program { get; init; }

    public string Key()
    {
        var w = new CanonicalWriter(KeyEncoding);
        w.Begin("Question")
            .Text("schema", Schema)
            .Text("callerSurface", CallerSurface)
            .Int32("invokeLcid", InvokeLcid)
            .List("observationRecipe", ObservationRecipe, (cw, s) => cw.Text("channel", s))
            .Text("pathBinding", PathBinding)
            .List("inputs", Inputs, (cw, i) => i.Write(cw))
            .List("program", Program, (cw, op) => op.Write(cw));
        return "qk1-" + w.Sha256Hex();
    }

    /// <summary>Rejects questions that the canonical form cannot represent exactly.</summary>
    public void Validate()
    {
        if (Schema != CurrentSchema)
        {
            throw new EvidenceFormatException($"Unsupported question schema '{Schema}'.");
        }
        var roles = new HashSet<string>(StringComparer.Ordinal);
        foreach (FixtureInput input in Inputs)
        {
            if (!roles.Add(input.Role))
            {
                throw new EvidenceFormatException($"Duplicate input role '{input.Role}'.");
            }
            FileNames.RequireSimple(input.FileName);
        }
        foreach (Operation op in Program)
        {
            op.Validate(roles);
        }
    }
}

/// <summary>An immutable starting artifact, identified by content (F-184 starting fixture manifest).</summary>
public sealed record FixtureInput(string Role, string FileName, string Sha256, long Length)
{
    /// <summary>How the fixture was produced: creating attempt, question key and artifact role.</summary>
    public string? Provenance { get; init; }

    internal void Write(CanonicalWriter w) =>
        w.Begin("FixtureInput").Text("role", Role).Text("fileName", FileName).Text("sha256", Sha256).Int64("length", Length);
}

public enum DispatchKind
{
    Method = 1,
    PropertyGet = 2,
    MethodOrGet = 3,

    /// <summary>DISPATCH_PROPERTYPUT: the last argument is the value, passed with DISPID_PROPERTYPUT.</summary>
    PropertyPut = 4,

    /// <summary>DISPATCH_PROPERTYPUTREF, for assigning an object.</summary>
    PropertyPutRef = 8,
}

/// <summary>One step of an oracle operation program, interpreted by the worker in order.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "op")]
[JsonDerivedType(typeof(CreateObject), "create")]
[JsonDerivedType(typeof(Invoke), "invoke")]
[JsonDerivedType(typeof(LoopUntil), "loopUntil")]
[JsonDerivedType(typeof(ForEachItem), "forEachItem")]
[JsonDerivedType(typeof(Release), "release")]
[JsonDerivedType(typeof(ObserveFiles), "observeFiles")]
[JsonDerivedType(typeof(ObserveModules), "observeModules")]
[JsonDerivedType(typeof(DescribeType), "describeType")]
public abstract record Operation
{
    internal abstract void Write(CanonicalWriter w);

    internal virtual void Validate(IReadOnlySet<string> inputRoles)
    {
    }
}

/// <summary>Activate a class by ProgID through <c>CLSIDFromProgID</c> and <c>CoCreateInstance</c>.</summary>
public sealed record CreateObject(string ProgId, string Bind) : Operation
{
    internal override void Write(CanonicalWriter w) => w.Begin("create").Text("progId", ProgId).Text("bind", Bind);
}

/// <summary>
/// <c>IDispatch::GetIDsOfNames</c> then <c>IDispatch::Invoke</c> on a bound object. A returned object is
/// bound to <see cref="Bind"/> when given.
/// </summary>
public sealed record Invoke(string Target, string Member, DispatchKind Kind, IReadOnlyList<Arg> Args) : Operation
{
    public string? Bind { get; init; }

    internal override void Write(CanonicalWriter w)
    {
        w.Begin("invoke").Text("target", Target).Text("member", Member).Int32("kind", (int)Kind)
            .List("args", Args, (cw, a) => a.Write(cw)).Text("bind", Bind);
    }

    internal override void Validate(IReadOnlySet<string> inputRoles)
    {
        foreach (Arg arg in Args)
        {
            arg.Validate(inputRoles);
        }
    }
}

/// <summary>Repeat <see cref="Body"/> until the Boolean property <see cref="Member"/> of the target reads True.</summary>
public sealed record LoopUntil(string Target, string Member, int MaxIterations, IReadOnlyList<Operation> Body) : Operation
{
    internal override void Write(CanonicalWriter w)
    {
        w.Begin("loopUntil").Text("target", Target).Text("member", Member).Int32("max", MaxIterations)
            .List("body", Body, (cw, op) => op.Write(cw));
    }

    internal override void Validate(IReadOnlySet<string> inputRoles)
    {
        foreach (Operation op in Body)
        {
            op.Validate(inputRoles);
        }
    }
}

/// <summary>Read <c>Count</c>, then bind <c>Item(i)</c> for i = 0..Count-1 and run the body for each.</summary>
public sealed record ForEachItem(string Target, string Bind, int MaxItems, IReadOnlyList<Operation> Body) : Operation
{
    internal override void Write(CanonicalWriter w)
    {
        w.Begin("forEachItem").Text("target", Target).Text("bind", Bind).Int32("max", MaxItems)
            .List("body", Body, (cw, op) => op.Write(cw));
    }

    internal override void Validate(IReadOnlySet<string> inputRoles)
    {
        foreach (Operation op in Body)
        {
            op.Validate(inputRoles);
        }
    }
}

/// <summary>Release the worker's reference to a bound object, recording the returned count.</summary>
public sealed record Release(string Target) : Operation
{
    internal override void Write(CanonicalWriter w) => w.Begin("release").Text("target", Target);
}

/// <summary>
/// Observe the attempt's input and work files: lengths, hashes, lock files. With <see cref="Keep"/>, the
/// bytes of every database file at this point are kept as artifacts (R-FMT-1/2 before/after images).
/// </summary>
public sealed record ObserveFiles(string Label) : Operation
{
    public bool Keep { get; init; }

    internal override void Write(CanonicalWriter w)
    {
        w.Begin("observeFiles").Text("label", Label);
        if (Keep)
        {
            w.Bool("keep", true); // written only when set, so earlier question keys are unchanged
        }
    }
}

/// <summary>
/// Record the type information of a bound object through ITypeInfo: its type library, interface name and
/// IID, flags, and every function and variable with dispid, invoke kind, vtable offset and parameter types,
/// for dual interfaces also the vtable view (R-DAO-1 inventory, R-ACT-1 ACT-DECL). Public COM metadata only.
/// </summary>
public sealed record DescribeType(string Target) : Operation
{
    /// <summary>Describe every type of the object's containing type library, not only the object's own type.</summary>
    public bool WholeLibrary { get; init; }

    internal override void Write(CanonicalWriter w) => w.Begin("describeType").Text("target", Target).Bool("wholeLibrary", WholeLibrary);
}

/// <summary>Observe loaded module identities (OR-IDENT).</summary>
public sealed record ObserveModules(string Label) : Operation
{
    internal override void Write(CanonicalWriter w) => w.Begin("observeModules").Text("label", Label);
}

/// <summary>A typed argument. The tag is part of the question key (T-EVID-01).</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(I2Arg), "i2")]
[JsonDerivedType(typeof(I4Arg), "i4")]
[JsonDerivedType(typeof(BoolArg), "bool")]
[JsonDerivedType(typeof(BstrArg), "bstr")]
[JsonDerivedType(typeof(MissingArg), "missing")]
[JsonDerivedType(typeof(ObjectArg), "object")]
[JsonDerivedType(typeof(InputPathArg), "inputPath")]
[JsonDerivedType(typeof(OutputPathArg), "outputPath")]
[JsonDerivedType(typeof(UI1Arg), "ui1")]
[JsonDerivedType(typeof(R4Arg), "r4")]
[JsonDerivedType(typeof(R8Arg), "r8")]
[JsonDerivedType(typeof(DateArg), "date")]
[JsonDerivedType(typeof(CyArg), "cy")]
[JsonDerivedType(typeof(BytesArg), "bytes")]
[JsonDerivedType(typeof(NullArg), "null")]
[JsonDerivedType(typeof(EmptyArg), "empty")]
public abstract record Arg
{
    internal abstract void Write(CanonicalWriter w);

    internal virtual void Validate(IReadOnlySet<string> inputRoles)
    {
    }
}

public sealed record I2Arg(short Value) : Arg
{
    internal override void Write(CanonicalWriter w) => w.Begin("i2").Int16("v", Value);
}

public sealed record I4Arg(int Value) : Arg
{
    internal override void Write(CanonicalWriter w) => w.Begin("i4").Int32("v", Value);
}

/// <summary>VT_BOOL; True is VARIANT_TRUE (-1).</summary>
public sealed record BoolArg(bool Value) : Arg
{
    internal override void Write(CanonicalWriter w) => w.Begin("bool").Bool("v", Value);
}

/// <summary>A BSTR holding exactly the code units of <see cref="Text"/>.</summary>
public sealed record BstrArg(string Text) : Arg
{
    internal override void Write(CanonicalWriter w) => w.Begin("bstr").Text("v", Text);

    internal override void Validate(IReadOnlySet<string> inputRoles)
    {
        // JSON cannot carry a lone surrogate faithfully; such arguments need a hex form that this
        // schema version does not define yet. Refuse rather than store a replaced character.
        if (!Hashing.IsWellFormedUtf16(Text))
        {
            throw new EvidenceFormatException("BSTR argument is not well-formed UTF-16; calcdb.question/1 cannot represent it.");
        }
    }
}

public sealed record UI1Arg(byte Value) : Arg
{
    internal override void Write(CanonicalWriter w) => w.Begin("ui1").Int32("v", Value);
}

/// <summary>VT_R4 given by its exact IEEE bits as 8 lowercase hex digits (most significant first).</summary>
public sealed record R4Arg(string Bits) : Arg
{
    internal override void Write(CanonicalWriter w) => w.Begin("r4").Text("bits", Bits);

    internal override void Validate(IReadOnlySet<string> inputRoles) => HexBits.Require(Bits, 8);
}

/// <summary>VT_R8 given by its exact IEEE bits as 16 lowercase hex digits (most significant first).</summary>
public sealed record R8Arg(string Bits) : Arg
{
    internal override void Write(CanonicalWriter w) => w.Begin("r8").Text("bits", Bits);

    internal override void Validate(IReadOnlySet<string> inputRoles) => HexBits.Require(Bits, 16);
}

/// <summary>VT_DATE given by the exact bits of its OLE Automation Double, 16 lowercase hex digits.</summary>
public sealed record DateArg(string Bits) : Arg
{
    internal override void Write(CanonicalWriter w) => w.Begin("date").Text("bits", Bits);

    internal override void Validate(IReadOnlySet<string> inputRoles) => HexBits.Require(Bits, 16);
}

/// <summary>VT_CY given by its scaled 64-bit integer (value times 10,000).</summary>
public sealed record CyArg(long Scaled) : Arg
{
    internal override void Write(CanonicalWriter w) => w.Begin("cy").Int64("v", Scaled);
}

/// <summary>A one-dimensional zero-based VT_ARRAY|VT_UI1 holding exactly these bytes (lowercase hex).</summary>
public sealed record BytesArg(string Hex) : Arg
{
    internal override void Write(CanonicalWriter w) => w.Begin("bytes").Text("hex", Hex);

    internal override void Validate(IReadOnlySet<string> inputRoles)
    {
        if (Hex.Length % 2 != 0 || Hex.Any(c => !Uri.IsHexDigit(c) || char.IsUpper(c)))
        {
            throw new EvidenceFormatException("bytes must be lowercase hex with an even number of digits.");
        }
    }
}

public sealed record NullArg : Arg
{
    internal override void Write(CanonicalWriter w) => w.Begin("null");
}

public sealed record EmptyArg : Arg
{
    internal override void Write(CanonicalWriter w) => w.Begin("empty");
}

internal static class HexBits
{
    public static void Require(string bits, int digits)
    {
        if (bits.Length != digits || bits.Any(c => !Uri.IsHexDigit(c) || char.IsUpper(c)))
        {
            throw new EvidenceFormatException($"'{bits}' must be exactly {digits} lowercase hex digits.");
        }
    }
}

/// <summary>An omitted optional argument: VT_ERROR with DISP_E_PARAMNOTFOUND.</summary>
public sealed record MissingArg : Arg
{
    internal override void Write(CanonicalWriter w) => w.Begin("missing");
}

/// <summary>A previously bound object, passed as VT_DISPATCH.</summary>
public sealed record ObjectArg(string Name) : Arg
{
    internal override void Write(CanonicalWriter w) => w.Begin("object").Text("name", Name);
}

/// <summary>The full path of the attempt-local copy of an input fixture, as a BSTR.</summary>
public sealed record InputPathArg(string Role) : Arg
{
    internal override void Write(CanonicalWriter w) => w.Begin("inputPath").Text("role", Role);

    internal override void Validate(IReadOnlySet<string> inputRoles)
    {
        if (!inputRoles.Contains(Role))
        {
            throw new EvidenceFormatException($"Input path refers to unknown role '{Role}'.");
        }
    }
}

/// <summary>The full path of a file in the attempt's work directory, as a BSTR. Files there are captured.</summary>
public sealed record OutputPathArg(string FileName) : Arg
{
    internal override void Write(CanonicalWriter w) => w.Begin("outputPath").Text("fileName", FileName);

    internal override void Validate(IReadOnlySet<string> inputRoles) => FileNames.RequireSimple(FileName);
}

public sealed class EvidenceFormatException(string message) : Exception(message);

internal static class FileNames
{
    public static void RequireSimple(string name)
    {
        if (string.IsNullOrEmpty(name) || name != Path.GetFileName(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name is "." or "..")
        {
            throw new EvidenceFormatException($"'{name}' is not a simple file name.");
        }
    }
}
