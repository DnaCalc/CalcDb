using System.Text;
using CalcDb.Evidence;

namespace CalcDb.Oracle.Runner;

/// <summary>Compact, human-readable view of a capture for the agent or person inspecting a result.</summary>
internal static class Show
{
    /// <summary>The manifest's verdict, problems, claimed servers, dialogs and desktop, shown with or without a capture.</summary>
    public static string Header(CaptureManifest manifest)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"attempt {manifest.AttemptId}: execution={manifest.Execution} capture={manifest.Capture}");
        foreach (string p in manifest.Problems)
        {
            sb.AppendLine("  problem: " + p);
        }
        OwnerReport[] owners = [manifest.Owner, .. manifest.Participants.Select(x => x.Owner)];
        foreach (OwnerReport owner in owners)
        {
            foreach (string child in owner.ClaimedChildren ?? [])
            {
                sb.AppendLine($"  claimed by {owner.Participant}: {child}");
            }
        }
        foreach (OwnerReport owner in owners)
        {
            foreach (DialogObservation d in owner.Dialogs ?? [])
            {
                sb.AppendLine($"  dialog of {owner.Participant}: {d.Window} pid {d.ProcessId} '{d.Title}' ({d.ClassName}) at {d.SeenUtc:HH:mm:ss.fff}: {d.Outcome}");
                foreach (string element in d.Elements)
                {
                    sb.AppendLine("      " + Visible(element));
                }
            }
        }
        if (manifest.Owner.MainDesktopInstruction is { } instruction)
        {
            sb.AppendLine("  desktop: main (" + instruction + ")");
        }
        return sb.ToString();
    }

    /// <summary>
    /// The capture's events. A type event shows its library and type count; <paramref name="typeName"/> lists that type's
    /// members (and its dual interface's vtable view) wherever it appears; "*" lists every type on one line each.
    /// </summary>
    public static string Render(CaptureManifest? manifest, Capture capture, bool allModules, string? typeName = null)
    {
        var sb = new StringBuilder();
        if (manifest is not null)
        {
            sb.Append(Header(manifest));
        }
        EnvironmentReceipt env = capture.Environment;
        sb.AppendLine($"env: {env.Runtime} {env.ProcessArchitecture} {env.ApartmentType} desktop={env.WindowStation}\\{env.Desktop} lcid={env.UserDefaultLcid} acp={env.AnsiCodePage} tooling={env.ToolingRevision}");
        var previousPages = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (CaptureEvent e in capture.Events)
        {
            sb.Append($"{e.Seq,4} {e.ProgramPath,-16} ");
            switch (e)
            {
                case CreateEvent c:
                    sb.AppendLine($"create {c.ProgId} clsid={c.Clsid} hr={c.CoCreateInstanceHresult} -> {Value(c.Object)}; registered {c.Registration.InprocServer32} ({c.Registration.ThreadingModel})");
                    break;
                case InvokeEvent i:
                    sb.Append($"{i.Target}.{i.Member}({string.Join(", ", i.Args.Select(Value))}) hr={i.InvokeHresult ?? "GetIDsOfNames " + i.GetIdsOfNamesHresult}");
                    if (i.Result is not null)
                    {
                        sb.Append(" -> " + Value(i.Result));
                    }
                    sb.AppendLine();
                    if (i.ExcepInfo is { } x)
                    {
                        sb.AppendLine($"{"",22}EXCEPINFO scode={x.Scode} wCode={x.WCode} source={Value(x.Source)} description={Value(x.Description)} helpFile={Value(x.HelpFile)} helpContext={x.HelpContext} deferredFill={x.DeferredFillPresent}");
                    }
                    if (i.NamedArgNames is { } names)
                    {
                        sb.AppendLine($"{"",22}named {string.Join(", ", names.Select((n, k) => $"{n}=dispid {(i.NamedDispIds is { } ids && k < ids.Count ? ids[k] : 0)}"))}");
                    }
                    foreach (ByRefWriteback b in i.ByRefAfter ?? [])
                    {
                        sb.AppendLine($"{"",22}byref arg {b.Index} after the call: {Value(b.After)}");
                    }
                    if (i.ErrorInfo is { } ei)
                    {
                        sb.AppendLine(ei.Present
                            ? $"{"",22}IErrorInfo guid={ei.Guid} source={Value(ei.Source)} description={Value(ei.Description)} helpContext={ei.HelpContext}"
                            : $"{"",22}IErrorInfo absent (GetErrorInfo {ei.GetErrorInfoHresult})");
                    }
                    break;
                case EnumEvent en:
                    sb.AppendLine(en.Steps.Count == 0
                        ? $"{en.Target}._NewEnum hr={en.NewEnumHresult} -> {Value(en.NewEnumResult)}; IEnumVARIANT {en.EnumVariantHresult}"
                        : $"{en.Target} enum: " + string.Join("; ", en.Steps.Select(st => $"{st.Op} {st.Hresult}{(st.Fetched is { } f ? $" fetched {f}" : "")}{(st.Item is null ? "" : " -> " + Value(st.Item))}")));
                    break;
                case ActivationEvent ac:
                    sb.AppendLine($"activate {ac.Route} {ac.Class} {ac.Clsid} ctx=0x{ac.ClsCtx:X} iid={ac.Iid}{(ac.Aggregate ? " aggregate" : "")}: {string.Join("; ", ac.Steps)}{(ac.OuterQueries is { Count: > 0 } q ? " | outer asked for " + string.Join(", ", q) : "")}{(ac.Object is null ? "" : " -> " + Value(ac.Object))}");
                    break;
                case ClaimEvent cl:
                    sb.AppendLine($"claim {cl.Target}.{cl.Member} window {cl.Window} -> pid {cl.ProcessId} {cl.ImagePath} {cl.ImageFileVersion} started {cl.StartedUtc:O}: {cl.Outcome}");
                    break;
                case ConnectionEvent ce:
                    sb.AppendLine($"{ce.Op} {ce.Name} {ce.Target} {ce.EventIid}: {string.Join("; ", ce.Steps)}{(ce.Cookie is { } ck ? $" cookie {ck}" : "")}{(ce.CallsRecorded is { } calls ? $", {calls} calls recorded" : "")}");
                    break;
                case SinkCallEvent sc:
                    sb.AppendLine($"event {sc.Connection} dispid {sc.DispId} flags {sc.Flags}: ({string.Join(", ", sc.Args.Select((a, k) => (sc.ByRef[k] ? "byref " : "") + Value(a)))}) thread {sc.ThreadId}");
                    break;
                case SyncEvent s:
                    sb.AppendLine($"{s.Kind} {s.Name}{(s.WaitedMs is { } w ? $" waited {w} ms" : "")}{(s.TimedOut ? " TIMED OUT" : "")} at {s.AtUtc:HH:mm:ss.fff}");
                    break;
                case HandleEvent h:
                    sb.AppendLine($"native {h.Op} {h.Handle} {h.RelativePath} access=0x{h.Access:X} share={h.Share} -> {(h.Success ? "ok" : "Win32 error " + h.Win32Error)}{(h.FileId is null ? "" : " fileId " + h.FileId[^8..])}{(h.BytesRead is { } n ? $" read {n} bytes" : "")} at {h.AtUtc:HH:mm:ss.fff}");
                    break;
                case ReleaseEvent r:
                    sb.AppendLine($"release {r.Target}{(r.Implicit ? " (implicit)" : "")} -> {r.ReturnedCount}");
                    break;
                case SkippedEvent s:
                    sb.AppendLine("note: " + s.Reason);
                    break;
                case FilesEvent f:
                    sb.AppendLine($"files[{f.Label}]: " + string.Join(", ", f.Files.Select(o => $"{o.RelativePath} {o.Length}B {(o.Sha256 is null ? "unread: " + o.Error : o.Sha256[..12])}{PageDelta(o, previousPages)}")));
                    foreach (FileObservation o in f.Files.Where(o => o.PageHashes is not null))
                    {
                        previousPages[o.RelativePath] = o.PageHashes!;
                    }
                    break;
                case TypeEvent t:
                    sb.AppendLine($"types {t.Target}: {(t.Library is { } lib ? $"library {lib.Name} {lib.Guid} {lib.Major}.{lib.Minor} lcid {lib.Lcid}, " : "")}{t.Types.Count} types{(t.Errors.Count == 0 ? "" : ", errors: " + string.Join("; ", t.Errors))}");
                    if (typeName == "*")
                    {
                        foreach (TypeView v in t.Types)
                        {
                            sb.AppendLine($"{"",22}{v.Name} {v.Guid} kind {v.TypeKind} flags 0x{v.TypeFlags:X} functions {v.Functions.Count} variables {v.Variables.Count}{(v.DualInterface is { } d ? $" dual vtable {d.VtableBytes}B" : "")} implements {string.Join(", ", v.Implements)}");
                        }
                    }
                    foreach (TypeView v in t.Types.Where(v => typeName is not null && string.Equals(v.Name, typeName, StringComparison.OrdinalIgnoreCase)))
                    {
                        TypeMembers(sb, v);
                        if (v.DualInterface is { } dual)
                        {
                            TypeMembers(sb, dual);
                        }
                    }
                    break;
                case ModulesEvent m:
                    sb.AppendLine($"modules[{m.Label}]: complete={m.Complete} rounds={m.Rounds} loaded={m.ModulePaths.Count} identified={m.Identities.Count} changedDuring={m.ChangedDuringCollection.Count}{(m.Error is null ? "" : " error=" + m.Error)}");
                    foreach (ModuleIdentity id in m.Identities.Where(id => allModules || IsEngine(id.Path)))
                    {
                        sb.AppendLine($"{"",22}{id.Path} {id.FileVersionNumber} {(id.Sha256 is null ? "unhashed: " + id.Error : "sha256 " + id.Sha256[..16])}");
                    }
                    break;
            }
        }
        foreach (string err in capture.CaptureErrors)
        {
            sb.AppendLine("capture error: " + err);
        }
        foreach (ArtifactRef a in capture.Artifacts)
        {
            sb.AppendLine($"artifact {a.Role} {a.Length}B sha256 {a.Sha256}");
        }
        return sb.ToString();
    }

    private static void TypeMembers(StringBuilder sb, TypeView v)
    {
        sb.AppendLine($"{"",22}{v.Name} {v.Guid} kind {v.TypeKind} flags 0x{v.TypeFlags:X} vtable {v.VtableBytes}B implements {string.Join(", ", v.Implements)}");
        foreach (FunctionInfo fn in v.Functions)
        {
            string parameters = string.Join(", ", fn.Parameters.Select(p => $"{p.Name}:{p.Type}/0x{p.Flags:X}{(p.Default is null ? "" : "=" + Value(p.Default))}"));
            sb.AppendLine($"{"",24}id {fn.MemberId} {fn.Name} invoke {fn.InvokeKind} funckind {fn.FuncKind} vft {fn.VtableOffset} -> {fn.ReturnType} ({parameters})");
        }
        foreach (VariableInfo va in v.Variables)
        {
            sb.AppendLine($"{"",24}var {va.MemberId} {va.Name}: {va.Type}{(va.Value is null ? "" : " = " + Value(va.Value))}");
        }
    }

    /// <summary>For a page-hashed file: its page count, and the pages that differ from its previous page-hashed observation.</summary>
    private static string PageDelta(FileObservation o, Dictionary<string, IReadOnlyList<string>> previous)
    {
        if (o.PageHashes is not { } pages)
        {
            return "";
        }
        if (!previous.TryGetValue(o.RelativePath, out IReadOnlyList<string>? before))
        {
            return $" ({pages.Count} pages of {o.PageSize})";
        }
        var changed = Enumerable.Range(0, Math.Max(pages.Count, before.Count)).Where(i => i >= pages.Count || i >= before.Count || pages[i] != before[i]).ToList();
        return changed.Count == 0 ? $" ({pages.Count} pages, none changed)" : $" ({pages.Count} pages, changed: {string.Join(",", changed.Take(40))}{(changed.Count > 40 ? $", … {changed.Count - 40} more" : "")})";
    }

    private static bool IsEngine(string path)
    {
        string name = Path.GetFileName(path);
        return name.StartsWith("ACE", StringComparison.OrdinalIgnoreCase) || name.Contains("DAO", StringComparison.OrdinalIgnoreCase) || name.Equals("mso.dll", StringComparison.OrdinalIgnoreCase);
    }

    private static string Value(CapturedValue? v)
    {
        if (v is null)
        {
            return "-";
        }
        string payload = v.Text is not null ? "\"" + Visible(v.Text) + "\""
            : v.Bytes is { Length: > 0 } raw ? $"bytes {(raw.Length > 96 ? raw[..96] + "…" : raw)} ({v.ByteLength} B)"
            : v.Integer ?? v.Display ?? v.Bits ?? v.Unsupported
                ?? (v.Decimal is { } d ? DecimalText(d) : null)
                ?? (v.Array is { } a ? ArrayText(a) : null)
                ?? (v.Identity is not null ? $"{v.Identity} {v.TypeName} {v.TypeIid}" : "");
        return $"{v.VtName} {payload}".TrimEnd();
    }

    /// <summary>
    /// Display text with invisible or unprintable code units as \uXXXX: controls, format characters such as U+FEFF,
    /// surrogates, and the noncharacters U+FFFE and U+FFFF. A U+FEFF read as a blank once passed for U+FFFE.
    /// </summary>
    private static string Visible(string text)
    {
        var sb = new System.Text.StringBuilder(text.Length);
        foreach (char ch in text)
        {
            System.Globalization.UnicodeCategory cat = char.GetUnicodeCategory(ch);
            bool hide = ch < (char)0x20 || ch >= (char)0xFFFE || cat is System.Globalization.UnicodeCategory.Format or System.Globalization.UnicodeCategory.Surrogate or System.Globalization.UnicodeCategory.OtherNotAssigned;
            _ = hide ? sb.Append((char)0x5C).Append('u').Append(((int)ch).ToString("X4", System.Globalization.CultureInfo.InvariantCulture)) : sb.Append(ch);
        }
        return sb.ToString();
    }

    /// <summary>A VT_DECIMAL as its exact value and its stored fields (OR-WIRE: the fields are the authority).</summary>
    private static string DecimalText(DecimalParts d)
    {
        var magnitude = (new System.Numerics.BigInteger(d.Hi32) << 64) | new System.Numerics.BigInteger(d.Lo64);
        string digits = magnitude.ToString(System.Globalization.CultureInfo.InvariantCulture).PadLeft(d.Scale + 1, '0');
        string text = d.Scale == 0 ? digits : digits[..^d.Scale] + "." + digits[^d.Scale..];
        string sign = (d.Sign & 0x80) != 0 ? "-" : "";
        return $"{sign}{text} (scale {d.Scale} sign 0x{d.Sign:X2} hi32 0x{d.Hi32:X8} mid32 0x{d.Lo64 >> 32:X8} lo32 0x{d.Lo64 & 0xFFFFFFFF:X8})";
    }

    /// <summary>Array bounds, then UI1 bytes or the first elements in the recorded order.</summary>
    private static string ArrayText(ArrayValue a)
    {
        string bounds = $"[{string.Join(",", a.Bounds.Select(b => $"{b.LowerBound}+{b.Count}"))}]";
        if (a.Bytes is not null)
        {
            return $"{bounds} {(a.Bytes.Length > 96 ? a.Bytes[..96] + "…" : a.Bytes)}";
        }
        if (a.Elements is not { Count: > 0 } e)
        {
            return bounds;
        }
        const int Shown = 24;
        string more = e.Count > Shown ? $", … {e.Count - Shown} more" : "";
        return $"{bounds} {a.ElementOrder}: {string.Join(", ", e.Take(Shown).Select(Value))}{more}";
    }
}