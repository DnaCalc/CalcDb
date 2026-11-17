using System.Text;
using CalcDb.Evidence;

namespace CalcDb.Oracle.Runner;

/// <summary>Compact, human-readable view of a capture for the agent or person inspecting a result.</summary>
internal static class Show
{
    public static string Render(CaptureManifest? manifest, Capture capture, bool allModules)
    {
        var sb = new StringBuilder();
        if (manifest is not null)
        {
            sb.AppendLine($"attempt {manifest.AttemptId}: execution={manifest.Execution} capture={manifest.Capture}");
            foreach (string p in manifest.Problems)
            {
                sb.AppendLine("  problem: " + p);
            }
        }
        EnvironmentReceipt env = capture.Environment;
        sb.AppendLine($"env: {env.Runtime} {env.ProcessArchitecture} {env.ApartmentType} desktop={env.WindowStation}\\{env.Desktop} lcid={env.UserDefaultLcid} acp={env.AnsiCodePage} tooling={env.ToolingRevision}");
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
                    if (i.ErrorInfo is { } ei)
                    {
                        sb.AppendLine(ei.Present
                            ? $"{"",22}IErrorInfo guid={ei.Guid} source={Value(ei.Source)} description={Value(ei.Description)} helpContext={ei.HelpContext}"
                            : $"{"",22}IErrorInfo absent (GetErrorInfo {ei.GetErrorInfoHresult})");
                    }
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
                    sb.AppendLine($"files[{f.Label}]: " + string.Join(", ", f.Files.Select(o => $"{o.RelativePath} {o.Length}B {(o.Sha256 is null ? "unread: " + o.Error : o.Sha256[..12])}")));
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
        string payload = v.Text is not null ? "\"" + v.Text + "\""
            : v.Bytes is { Length: > 0 } raw ? $"bytes {(raw.Length > 96 ? raw[..96] + "…" : raw)} ({v.ByteLength} B)"
            : v.Integer ?? v.Display ?? v.Bits ?? v.Unsupported
                ?? (v.Array is { } a ? $"[{string.Join(",", a.Bounds.Select(b => $"{b.LowerBound}+{b.Count}"))}] {(a.Bytes is { Length: > 96 } h ? h[..96] + "…" : a.Bytes)}" : null)
                ?? (v.Identity is not null ? $"{v.Identity} {v.TypeName} {v.TypeIid}" : "");
        return $"{v.VtName} {payload}".TrimEnd();
    }
}
