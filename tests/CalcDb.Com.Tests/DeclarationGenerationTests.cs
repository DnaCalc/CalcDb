using System.Globalization;
using System.Text;
using CalcDb.Evidence;

namespace CalcDb.Com.Tests;

/// <summary>
/// The DAO and ADO declarations CalcDb.dll lays out and publishes as type information are generated from the captured
/// type libraries (II-13 §2: one reconciled manifest, R-ACT-1 ACT-DECL). This test renders them and requires the
/// checked-in files to match; with CALCDB_REGENERATE=1 it rewrites them instead. Every dual interface, enum, alias and
/// coclass of a library is generated. Not generated: DAO's one module of constants (LanguageConstants), which no
/// declaration references, and ADO's pure dispinterfaces (the event sources ConnectionEvents and RecordsetEvents) and
/// vtable-only interfaces (the *EventsVt sinks and the ADO*Construction interfaces), which CalcDb does not implement
/// yet (ADO-EVENT and the OLE DB construction route; W-09/events-persistence-inventory). A coclass lists only the
/// generated interfaces it implements.
/// </summary>
public sealed class DeclarationGenerationTests
{
    internal const string Record = "evidence/r-dao-1-err-1/manifest-library/r-dao-1-err-1.manifest-library.bab1b7935f.a2.json";

    /// <summary>ADO's library as ADODB.Connection's type information gives it (R-ADO-1, R-ACT-1 ACT-DECL; W-09/construction-policy).</summary>
    internal const string AdoRecord = "evidence/r-str-ado-act/ado-describe/r-str-ado-act.ado-describe.1e70c0bee6.a1.json";

    /// <summary>
    /// The aliased type of each ADO alias. The capture records an alias's name, GUID and flags but not its TYPEATTR
    /// tdescAlias (a capture gap of describeType); the targets are the public SDK header's (adoint.h, Windows x64:
    /// <c>typedef LONGLONG ADO_LONGPTR</c>, <c>typedef ADO_LONGPTR PositionEnum_Param</c>, and SearchDirection is the
    /// enum SearchDirectionEnum). Through IDispatch ADO answers these members as VT_I4 (every RecordCount, Size and
    /// AbsolutePosition the ADO records show), which this table does not change.
    /// </summary>
    private static readonly Dictionary<string, string> AdoAliasTargets = new(StringComparer.Ordinal)
    {
        ["ADO_LONGPTR"] = "VT_I8",
        ["PositionEnum_Param"] = "USERDEFINED ADO_LONGPTR",
        ["SearchDirection"] = "USERDEFINED SearchDirectionEnum",
    };

    internal static string Root()
    {
        string root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "CalcDb.slnx")))
        {
            root = Path.GetDirectoryName(root)!;
        }
        return root;
    }

    [Fact]
    public void The_generated_declarations_match_the_captured_type_library() =>
        Check(Record, "DAO", "DaoTypes", "Dao.g.cs", new Dictionary<string, string>());

    [Fact]
    public void The_generated_ADO_declarations_match_the_captured_type_library() =>
        Check(AdoRecord, "ADO", "AdoTypes", "Ado.g.cs", AdoAliasTargets);

    private static void Check(string recordPath, string title, string className, string file, IReadOnlyDictionary<string, string> aliasTargets)
    {
        string root = Root();
        EvidenceRecord record = EvidenceJson.Read<EvidenceRecord>(Path.Combine(root, recordPath));
        TypeEvent types = record.Capture!.Events.OfType<TypeEvent>().Single(t => t.Library is not null && t.Types.Count > 1);
        string rendered = Render(types, recordPath, title, className, aliasTargets);
        string target = Path.Combine(root, "src", "CalcDb.Com", "Declarations", file);
        if (Environment.GetEnvironmentVariable("CALCDB_REGENERATE") == "1")
        {
            File.WriteAllText(target, rendered);
        }
        Assert.Equal(rendered, File.ReadAllText(target).ReplaceLineEndings("\n"));
    }

    private static string Id(string name) => name.TrimStart('_');

    private static (string Name, int Flags) Impl(string text)
    {
        // "IDispatch (flags 0)"
        int open = text.IndexOf(" (flags ", StringComparison.Ordinal);
        return (text[..open], int.Parse(text[(open + 8)..^1], CultureInfo.InvariantCulture));
    }

    private static string Str(string s) => "\"" + s.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    private static string Params(IEnumerable<ParameterInfo> ps) => string.Join(", ", ps.Select(p => $"new({Str(p.Name ?? "")}, {Str(p.Type)}, 0x{p.Flags:X}{Default(p.Default)})"));

    private static string Default(CapturedValue? v)
    {
        if (v is null)
        {
            return "";
        }
        string integer = v.Integer is null ? "null" : v.Integer + "L";
        string bits = v.Bits is null ? "null" : "0x" + v.Bits + "UL";
        string text = v.Text is null ? "null" : Str(v.Text);
        if (v.Bytes is not null && v.Text is null)
        {
            throw new InvalidOperationException("a default BSTR without text: " + v.Bytes);
        }
        return $", new({v.Vt}, {integer}, {bits}, {text})";
    }

    private static string Render(TypeEvent types, string recordPath, string title, string className, IReadOnlyDictionary<string, string> aliasTargets)
    {
        TypeLibraryInfo lib = types.Library!;
        TypeView[] dispatches = types.Types.Where(t => t.TypeKind == 4).ToArray();
        TypeView[] duals = dispatches.Where(d => d.DualInterface is not null).ToArray();
        string[] skipped = [.. dispatches.Where(d => d.DualInterface is null).Select(d => d.Name),
            .. types.Types.Where(t => t.TypeKind == 3 && !duals.Any(d => d.Name == t.Name)).Select(t => t.Name)];
        if (title == "DAO")
        {
            Assert.Empty(skipped); // every dispinterface of DAO is dual
        }
        TypeView[] aliases = types.Types.Where(t => t.TypeKind == 6).ToArray();
        var generated = new HashSet<string>(duals.Select(d => d.Name), StringComparer.Ordinal);
        var sb = new StringBuilder();
        sb.Append("// <auto-generated/>\n");
        sb.Append($"// Generated by DeclarationGenerationTests from the captured {title} type library (R-ACT-1 ACT-DECL):\n");
        sb.Append($"// {recordPath}\n");
        sb.Append($"// library {lib.Name} {lib.Guid} {lib.Major}.{lib.Minor}. Do not edit; regenerate with CALCDB_REGENERATE=1.\n");
        if (skipped.Length > 0)
        {
            sb.Append($"// Not generated (not dual): {string.Join(", ", skipped.Distinct())}.\n");
        }
        sb.Append("using System.Runtime.InteropServices;\n\nnamespace CalcDb.Com.Declarations;\n\n");
        sb.Append("#pragma warning disable CA1707, IDE1006\n");
        sb.Append($"internal static unsafe partial class {className}\n{{\n");
        var stubs = new StringBuilder();
        foreach (TypeView dispatch in duals)
        {
            TypeView dual = dispatch.DualInterface!;
            FunctionInfo[] members = dispatch.Functions.Where(f => f.VtableOffset >= 56).ToArray();
            foreach (FunctionInfo v in dual.Functions)
            {
                // The vtable view must agree with the dispatch view on every slot it lists.
                Assert.Contains(members, m => m.MemberId == v.MemberId && m.InvokeKind == v.InvokeKind && m.VtableOffset == v.VtableOffset);
            }
            string id = Id(dispatch.Name);
            string baseName = Impl(dual.Implements.Single()).Name;
            sb.Append($"    public static readonly InterfaceDecl {id} = new({Str(dispatch.Name)}, new Guid({Str(dispatch.Guid.Trim('{', '}'))}), {dual.VtableBytes}, {Str(baseName)}, 0x{dispatch.TypeFlags:X}, 0x{dual.TypeFlags:X},\n    [\n");
            foreach (FunctionInfo f in members)
            {
                sb.Append($"        new({f.MemberId}, {Str(f.Name)}, (InvokeKind){f.InvokeKind}, {f.VtableOffset}, {Str(f.ReturnType)}, [{Params(f.Parameters)}]),\n");
            }
            sb.Append("    ],\n    [\n");
            foreach (FunctionInfo f in dual.Functions.OrderBy(f => f.VtableOffset))
            {
                sb.Append($"        new({f.MemberId}, {Str(f.Name)}, (InvokeKind){f.InvokeKind}, {f.VtableOffset}, 0x{f.Flags:X}, {Str(f.ReturnType)}, [{Params(f.Parameters)}]),\n");
            }
            sb.Append($"    ], &Fill_{id});\n\n");
            stubs.Append($"    private static void Fill_{id}(nint* table)\n    {{\n");
            var body = new StringBuilder();
            foreach (FunctionInfo f in members)
            {
                int slot = f.VtableOffset / 8;
                bool hasRetval = f.ReturnType != "VT_VOID";
                string[] native = f.Parameters.Select(p => NativeType(p.Type)).ToArray();
                string sig = "nint, " + string.Concat(native.Select(t => t + ", ")) + (hasRetval ? "nint, " : "") + "int";
                stubs.Append($"        table[{slot}] = (nint)(delegate* unmanaged<{sig}>)&U_{id}_{slot};\n");
                string args = string.Concat(native.Select((t, k) => $", {t} a{k}")) + (hasRetval ? ", nint r" : "");
                string packing = native.Length == 0 ? "" : $"ulong* raw = stackalloc ulong[{native.Length}]; " + string.Concat(native.Select((t, k) => $"raw[{k}] = {Raw(t, "a" + k)}; "));
                string call = $"Com.TypedCall.Run(self, {id}, {slot}, {(native.Length == 0 ? "null" : "raw")}, {native.Length}, {(hasRetval ? "r" : "0")})";
                body.Append($"\n    [UnmanagedCallersOnly]\n    private static int U_{id}_{slot}(nint self{args})\n    {{\n        {packing}return {call};\n    }}\n");
            }
            stubs.Append("    }\n").Append(body).Append('\n');
        }
        foreach (TypeView e in types.Types.Where(t => t.TypeKind == 0))
        {
            string constants = string.Join(", ", e.Variables.Select(v => $"({Str(v.Name)}, {v.MemberId}, {int.Parse(v.Value!.Integer!, CultureInfo.InvariantCulture)})"));
            sb.Append($"    public static readonly EnumDecl {Id(e.Name)}Decl = new({Str(e.Name)}, new Guid({Str(e.Guid.Trim('{', '}'))}), [{constants}]);\n");
        }
        foreach (TypeView a in aliases)
        {
            string target = aliasTargets.TryGetValue(a.Name, out string? t) ? t : throw new InvalidOperationException("no aliased type for " + a.Name);
            sb.Append($"    public static readonly AliasDecl {Id(a.Name)}Alias = new({Str(a.Name)}, new Guid({Str(a.Guid.Trim('{', '}'))}), 0x{a.TypeFlags:X}, {Str(target)});\n");
        }
        sb.Append('\n');
        foreach (TypeView c in types.Types.Where(t => t.TypeKind == 5))
        {
            string impls = string.Join(", ", c.Implements.Select(Impl).Where(i => generated.Contains(i.Name)).Select(i => $"({Str(i.Name)}, {i.Flags})"));
            sb.Append($"    public static readonly CoclassDecl {c.Name}Class = new({Str(c.Name)}, new Guid({Str(c.Guid.Trim('{', '}'))}), 0x{c.TypeFlags:X}, [{impls}]);\n");
        }
        sb.Append($"\n    public static readonly LibraryDecl Library = new({Str(lib.Name!)}, new Guid({Str(lib.Guid.Trim('{', '}'))}), {lib.Major}, {lib.Minor}, {lib.Lcid},\n");
        sb.Append($"        [{string.Join(", ", duals.Select(d => Id(d.Name)))}],\n");
        sb.Append($"        [{string.Join(", ", types.Types.Where(t => t.TypeKind == 0).Select(e => Id(e.Name) + "Decl"))}],\n");
        sb.Append($"        [{string.Join(", ", types.Types.Where(t => t.TypeKind == 5).Select(c => c.Name + "Class"))}]");
        sb.Append(aliases.Length == 0 ? ");\n\n" : $",\n        [{string.Join(", ", aliases.Select(a => Id(a.Name) + "Alias"))}]);\n\n");
        sb.Append(stubs.ToString().TrimEnd('\n')).Append('\n');
        sb.Append("}\n");
        return sb.ToString();
    }

    /// <summary>The native parameter type of a generated entry: floating values travel in XMM registers, the rest as integers.</summary>
    private static string NativeType(string type) => type switch
    {
        "VT_R4" => "float",
        "VT_R8" or "VT_DATE" => "double",
        _ => "nint",
    };

    private static string Raw(string nativeType, string name) => nativeType switch
    {
        "float" => $"(uint)BitConverter.SingleToInt32Bits({name})",
        "double" => $"(ulong)BitConverter.DoubleToInt64Bits({name})",
        _ => $"(ulong){name}",
    };
}
