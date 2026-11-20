using System.Globalization;
using System.Text;
using CalcDb.Evidence;
using CalcDb.Catalog;
using CalcDb.Format;
using CalcDb.Storage;

namespace CalcDb.Oracle.Runner;

/// <summary>What <c>inspect</c> was asked to show of a file.</summary>
internal sealed record InspectRequest(string? Table, bool IndexPages, bool DataPages, uint? Page);

/// <summary>
/// <c>inspect (--attempt ID --file NAME | --path FILE) [--table NAME|PAGE] [--index-pages] [--data-pages] [--page N]</c>: a
/// read-only, compact text view of the structure of a database file an oracle attempt produced or consumed, over
/// <see cref="Inspector"/>: summary (profile, page kinds, tables with columns and indexes), index pages (tree, kind,
/// stored prefix against the entries' common prefix, free space, peers, first and last entry) and data pages (owner,
/// slot directory). Nothing is written; a malformed structure is a problem line, as in the inspector.
/// </summary>
internal static class InspectCommand
{
    private static readonly string[] AttemptFolders = ["inputs", "work", "artifacts"];

    public static int Run(JobStore store, Options options)
    {
        string? path = options.Get("path");
        string label;
        if (path is not null)
        {
            label = path;
        }
        else
        {
            string attempt = options.Require("attempt");
            string name = options.Require("file");
            string dir = store.AttemptDirectory(attempt);
            string[] candidates = [.. AttemptFolders.Select(f => Path.Combine(dir, f)).Where(Directory.Exists)
                .SelectMany(f => Directory.EnumerateFiles(f, "*", SearchOption.AllDirectories)).Order(StringComparer.Ordinal)];
            string[] matches = [.. candidates.Where(c => string.Equals(Path.GetFileName(c), name, StringComparison.OrdinalIgnoreCase))];
            if (matches.Length != 1)
            {
                Console.Error.WriteLine(matches.Length == 0 ? $"no file '{name}' in attempt {attempt}; candidates:" : $"'{name}' is ambiguous in attempt {attempt}:");
                foreach (string c in matches.Length == 0 ? candidates : matches)
                {
                    Console.Error.WriteLine($"  {Path.GetRelativePath(dir, c).Replace('\\', '/')} ({new FileInfo(c).Length} bytes)");
                }
                return 1;
            }
            path = matches[0];
            label = $"{attempt}/{Path.GetRelativePath(dir, path).Replace('\\', '/')}";
        }
        uint? page = options.Get("page") is { } p ? uint.Parse(p, CultureInfo.InvariantCulture) : null;
        Console.Write(Render(File.ReadAllBytes(path), label, new InspectRequest(options.Get("table"), options.Has("index-pages"), options.Has("data-pages"), page)));
        return 0;
    }

    public static string Render(byte[] file, string label, InspectRequest request)
    {
        InspectionReport report = Inspector.Inspect(file);
        var sb = new StringBuilder();
        if (report.Profile is null)
        {
            sb.AppendLine($"{label}: refused {report.Refusal}, {report.Length} bytes, {report.PageCount} pages");
            return sb.ToString();
        }
        FormatDescriptor d = report.Profile.Descriptor;
        Dictionary<uint, string> names = TableNames(file, d);
        string TableLabel(uint tdef) => names.TryGetValue(tdef, out string? n) ? $"{n} (tdef {tdef})" : $"tdef {tdef}";
        List<TableDefinition> tables = SelectTables(report, names, request.Table);

        if (request.IndexPages || request.DataPages || request.Page is not null)
        {
            bool index = request.IndexPages;
            bool data = request.DataPages;
            if (!index && !data && request.Page is { } only)
            {
                index = report.IndexPages.Any(x => x.Page == only);
                data = !index;
            }
            Dictionary<uint, (uint Table, PhysicalIndex Tree)> trees = TreeOwners(report);
            if (index)
            {
                IndexPageLines(sb, report, d, tables, request, trees, TableLabel);
            }
            if (data)
            {
                DataPageLines(sb, report, tables, request, TableLabel);
            }
            return sb.ToString();
        }
        Summary(sb, report, label, d, tables, names, file);
        return sb.ToString();
    }

    private static List<TableDefinition> SelectTables(InspectionReport report, Dictionary<uint, string> names, string? selector)
    {
        if (selector is null)
        {
            return [.. report.TableDefs];
        }
        List<TableDefinition> found = [.. report.TableDefs.Where(t =>
            (names.TryGetValue(t.FirstPage, out string? n) && string.Equals(n, selector, StringComparison.OrdinalIgnoreCase))
            || t.FirstPage.ToString(CultureInfo.InvariantCulture) == selector)];
        if (found.Count == 0)
        {
            throw new ArgumentException($"No table '{selector}'; tables: " + string.Join(", ", report.TableDefs.Select(t => names.GetValueOrDefault(t.FirstPage) ?? t.FirstPage.ToString(CultureInfo.InvariantCulture))));
        }
        return found;
    }

    /// <summary>
    /// TableDef page to table name, from the one catalog reader (CalcDb.Catalog's CatalogBootstrap over MSysObjects). A table
    /// whose name cannot be read is absent; it then shows by its page.
    /// </summary>
    private static Dictionary<uint, string> TableNames(byte[] file, FormatDescriptor d)
    {
        var image = new ImagePageSource(file, d.PageSize);
        var cache = new PageCache(image, 1, d.PageSize, 64);
        CatalogSnapshot? catalog = CatalogBootstrap.Open(cache, d, image.PageCount, out CatalogFailure? _);
        var names = new Dictionary<uint, string>();
        foreach (CatalogEntry entry in catalog?.Entries ?? [])
        {
            if (entry.Type == 1)
            {
                names.TryAdd((uint)entry.Id, entry.Name);
            }
        }
        return names;
    }

    /// <summary>
    /// The physical index's 12-byte counter block on the TableDef's first page (II-05 §1.1), as three little-endian words:
    /// word1 is UniqueEntryCount; the book leaves word0 and word2 uninterpreted (qualified.md R-FMT-2 statistics words).
    /// </summary>
    private static string CounterWords(byte[] file, TableDefinition t, int physical, FormatDescriptor d)
    {
        long at = (long)t.FirstPage * d.PageSize + d.TableDef.RealIndexRecordsOffset + (long)physical * d.TableDef.RealIndexRecordBytes;
        if (d.TableDef.RealIndexRecordsOffset + (physical + 1) * d.TableDef.RealIndexRecordBytes > d.PageSize || at + 12 > file.Length)
        {
            return "";
        }
        ReadOnlySpan<byte> b = file.AsSpan((int)at, 12);
        return $" counter=({BitConverter.ToUInt32(b[..4])}, {BitConverter.ToUInt32(b[4..8])}, {BitConverter.ToUInt32(b[8..12])})";
    }

    private static void Summary(StringBuilder sb, InspectionReport report, string label, FormatDescriptor d, List<TableDefinition> tables, Dictionary<uint, string> names, byte[] file)
    {
        sb.AppendLine($"{label}: {report.Profile!.Format} version={report.Profile.VersionCode:X2} length={report.Length} pages={report.PageCount} trailing={report.TrailingBytes} problems={report.Problems.Count}");
        sb.AppendLine("kinds: " + string.Join(" ", report.PageKinds.Select(k => $"{KindName(k.Key, d)}={k.Value}")));
        foreach (string problem in report.Problems)
        {
            sb.AppendLine("  problem: " + problem);
        }
        foreach (TableDefinition t in tables)
        {
            string? name = names.GetValueOrDefault(t.FirstPage);
            if (name is not null && name.StartsWith("MSys", StringComparison.OrdinalIgnoreCase) && tables.Count > 1)
            {
                continue; // system tables show only when asked for by --table
            }
            sb.AppendLine($"table {name ?? "?"} first={t.FirstPage} rows={t.RowCount} columns={string.Join(", ", t.Columns.Select(c => $"{c.Name}:{TypeName(c.TypeCode)}{(c.IsFixed ? "" : $"({c.Length})")}"))}");
            foreach (PhysicalIndex p in t.PhysicalIndexes)
            {
                string cols = string.Join(",", p.Columns.Select(c => t.Columns.FirstOrDefault(x => x.PhysicalNumber == c.PhysicalNumber)?.Name + (c.Ascending ? "" : " desc")));
                sb.AppendLine($"  physical {p.Number}: columns={cols} root={p.RootPage} flags={p.Flags:X2}{(p.Unique(d.IndexDescriptor) ? " unique" : "")} entries={p.UniqueEntryCount}{CounterWords(file, t, p.Number, d)}");
            }
            foreach (LogicalIndex l in t.LogicalIndexes)
            {
                sb.AppendLine($"  logical {l.Number} '{l.Name}': physical={l.PhysicalNumber} type={l.IndexType:X2}");
            }
        }
    }

    /// <summary>Every index page reachable from a physical index's root, with the table and tree it belongs to.</summary>
    private static Dictionary<uint, (uint Table, PhysicalIndex Tree)> TreeOwners(InspectionReport report)
    {
        var byPage = report.IndexPages.ToDictionary(x => x.Page, x => x.Index);
        var owners = new Dictionary<uint, (uint, PhysicalIndex)>();
        foreach (TableDefinition t in report.TableDefs)
        {
            foreach (PhysicalIndex tree in t.PhysicalIndexes)
            {
                var pending = new Stack<uint>([tree.RootPage]);
                while (pending.TryPop(out uint page))
                {
                    if (!byPage.TryGetValue(page, out IndexPage? ip) || !owners.TryAdd(page, (t.FirstPage, tree)))
                    {
                        continue;
                    }
                    if (!ip.IsLeaf)
                    {
                        pending.Push(ip.TailChild);
                        for (int i = 0; i < ip.Count; i++)
                        {
                            pending.Push(ip.Entry(i).ChildPage ?? 0);
                        }
                    }
                }
            }
        }
        return owners;
    }

    private static void IndexPageLines(StringBuilder sb, InspectionReport report, FormatDescriptor d, List<TableDefinition> tables, InspectRequest request,
        Dictionary<uint, (uint Table, PhysicalIndex Tree)> trees, Func<uint, string> tableLabel)
    {
        var selected = tables.Select(t => t.FirstPage).ToHashSet();
        foreach ((uint page, IndexPage ip) in report.IndexPages.OrderBy(x => x.Page))
        {
            bool known = trees.TryGetValue(page, out (uint Table, PhysicalIndex Tree) owner);
            if (request.Page is { } only ? page != only : request.Table is not null && !(known ? selected.Contains(owner.Table) : selected.Contains(ip.Owner)))
            {
                continue;
            }
            TableDefinition? table = report.TableDefs.FirstOrDefault(t => t.FirstPage == (known ? owner.Table : ip.Owner));
            string tree = known
                ? $"{tableLabel(owner.Table)} physical {owner.Tree.Number}" + LogicalNames(table, owner.Tree)
                : $"{tableLabel(ip.Owner)} tree unknown (unreachable from any index root)";
            byte[][] entries = IndexPages.Entries(ip);
            byte[] common = CommonPrefix(entries);
            sb.AppendLine($"index page {page}: {tree} {(ip.IsLeaf ? "leaf" : "intermediate")} level={ip.Level} entries={ip.Count} " +
                $"prefix={ip.PrefixLength} [{Hex(ip.Prefix)}] common={common.Length} [{Hex(common)}] free={ip.FreeCount} prev={ip.PreviousPeer} next={ip.NextPeer}" +
                (ip.IsLeaf ? "" : $" tail={ip.TailChild}"));
            int[] shown = request.Page is not null ? [.. Enumerable.Range(0, ip.Count)] : ip.Count == 0 ? [] : ip.Count == 1 ? [0] : [0, ip.Count - 1];
            foreach (int i in shown)
            {
                IndexEntryView e = ip.Entry(i);
                int keyLength = e.Key.Length;
                string where = ip.IsLeaf ? $"data {e.DataPage} slot {e.RowSlot}" : $"data {e.DataPage} slot {e.RowSlot} child {e.ChildPage}";
                string role = request.Page is not null ? $"[{i}]" : i == 0 ? "first" : "last";
                sb.AppendLine($"  {role} key[{keyLength}]={Hex(entries[i].AsSpan(0, keyLength))} -> {where}");
            }
        }
    }

    private static string LogicalNames(TableDefinition? table, PhysicalIndex tree)
    {
        string[] logical = [.. table?.LogicalIndexes.Where(l => l.PhysicalNumber == tree.Number).Select(l => l.Name) ?? []];
        return logical.Length == 0 ? "" : " (" + string.Join(", ", logical) + ")";
    }

    private static void DataPageLines(StringBuilder sb, InspectionReport report, List<TableDefinition> tables, InspectRequest request, Func<uint, string> tableLabel)
    {
        var selected = tables.Select(t => t.FirstPage).ToHashSet();
        foreach (DataPageSummary page in report.DataPages)
        {
            if (request.Page is { } only ? page.Page != only : request.Table is not null && (page.IsLval || !selected.Contains(page.Owner)))
            {
                continue;
            }
            sb.AppendLine($"data page {page.Page}: {(page.IsLval ? "long-value page" : "owner " + tableLabel(page.Owner))} slots={page.SlotCount} free={page.FreeCount}");
            foreach (SlotView s in page.Slots)
            {
                sb.AppendLine($"  slot {s.Slot}: offset={s.Start} length={s.Length}{(s.Deleted ? " deleted" : "")}{(s.Overflow ? " overflow" : "")}");
            }
        }
    }

    private static byte[] CommonPrefix(byte[][] entries)
    {
        if (entries.Length == 0)
        {
            return [];
        }
        int n = entries[0].Length;
        foreach (byte[] e in entries.Skip(1))
        {
            int i = 0;
            while (i < n && i < e.Length && e[i] == entries[0][i])
            {
                i++;
            }
            n = i;
        }
        return entries[0][..n];
    }

    private static string Hex(ReadOnlySpan<byte> bytes) => Convert.ToHexString(bytes).ToLowerInvariant();

    private static string KindName(byte kind, FormatDescriptor d) =>
        kind == d.IndexPage.LeafKind ? "index-leaf(" + kind.ToString("X2", CultureInfo.InvariantCulture) + ")"
        : kind == d.IndexPage.InteriorKind ? "index-intermediate(" + kind.ToString("X2", CultureInfo.InvariantCulture) + ")"
        : kind switch { 0x01 => "data(01)", 0x02 => "tabledef(02)", _ => kind.ToString("X2", CultureInfo.InvariantCulture) };

    private static string TypeName(byte code) => code switch
    {
        NativeTypes.Boolean => "Boolean",
        NativeTypes.Byte => "Byte",
        NativeTypes.Integer => "Integer",
        NativeTypes.Long => "Long",
        NativeTypes.Currency => "Currency",
        NativeTypes.Single => "Single",
        NativeTypes.Double => "Double",
        NativeTypes.DateTime => "DateTime",
        NativeTypes.Binary => "Binary",
        NativeTypes.Text => "Text",
        NativeTypes.Ole => "Ole",
        NativeTypes.Memo => "Memo",
        NativeTypes.Guid => "Guid",
        NativeTypes.Numeric => "Numeric",
        NativeTypes.ExtendedDateTime => "ExtendedDateTime",
        _ => "type" + code.ToString("X2", CultureInfo.InvariantCulture),
    };
}