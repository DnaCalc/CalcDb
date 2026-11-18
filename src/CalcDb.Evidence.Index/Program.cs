using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CalcDb.Evidence;
using Microsoft.Office.Interop.Access.Dao;

namespace CalcDb.Evidence.Index;

/// <summary>
/// <c>calcdb-evidence-index</c>: builds the derived Access evidence index from the retained JSON records
/// (The Plan §2.5). The index is an analysis view, not a source of truth, and is never committed.
/// <list type="bullet">
/// <item><c>build [--out FILE]</c>: create a fresh .accdb and load every retained record.</item>
/// <item><c>verify [--db FILE]</c>: read the index back and compare every row with a fresh derivation
/// from the JSON; equality shows the index can be regenerated without losing those observations.</item>
/// <item><c>query --sql TEXT [--db FILE]</c>: run a query against the index and print the rows.</item>
/// </list>
/// The index holds what is queried across records: records, error channels, modules and artifacts. The individual
/// calls of a capture are read from its record (calcdb-oracle show): indexing every call (1.25 million rows) made the
/// build and verify take most of an hour. Each table loads in one transaction with its Field objects fetched once, and
/// verify reads it back in GetRows chunks. DAO is used through the primary interop assembly (package
/// ExcelDna.Interop.Dao, types embedded): this tool is a consumer of ACE, not an oracle capture. (The Text ISAM bulk
/// import is refused in this process with 3251, so ACE's text import is not used.)
/// </summary>
internal static class Program
{
    private const string LangGeneral = ";LANGID=0x0409;CP=1252;COUNTRY=0";

    // DAO is apartment-threaded: on an MTA thread every call is marshalled to a host STA (about 0.5 ms a row here).
    [STAThread]
    private static int Main(string[] args)
    {
        string repo = RepositoryRoot();
        string evidence = Path.Combine(repo, "evidence");
        string defaultDb = Path.Combine(repo, ".oracle-runs", "index", "evidence-index.accdb");
        string command = args.Length > 0 ? args[0] : "build";
        string? Option(string name)
        {
            int i = Array.IndexOf(args, "--" + name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }

        // --evidence DIR indexes one part of the evidence (for a quick check); the default is all of evidence/.
        IReadOnlyList<Table> Tables() => Derive(Option("evidence") is { } part ? Path.GetFullPath(part) : evidence, repo);
        switch (command)
        {
            case "build":
                Build(Option("out") ?? defaultDb, Tables());
                return 0;
            case "verify":
                return Verify(Option("db") ?? defaultDb, Option("table") is { } only ? [.. Tables().Where(t => t.Name == only)] : Tables());
            case "query":
                // query reads the built index only; it never derives from the JSON.
                try
                {
                    Query(Option("db") ?? defaultDb, Option("sql") ?? throw new ArgumentException("--sql is required."));
                    return 0;
                }
                catch (System.Runtime.InteropServices.COMException ex)
                {
                    Console.Error.WriteLine($"query refused: 0x{ex.HResult:X8} {ex.Message}");
                    return 1;
                }
            default:
                Console.Error.WriteLine("usage: calcdb-evidence-index build|verify|query [--out|--db FILE] [--sql TEXT]");
                return 2;
        }
    }

    private sealed record Column(string Name, string Ddl);

    private sealed record Table(string Name, IReadOnlyList<Column> Columns, IReadOnlyList<string> Key, List<object?[]> Rows);

    /// <summary>
    /// The index reads only what it stores. These types project the evidence schema (EvidenceRecord, Capture and its
    /// events, as written by EvidenceJson) onto those fields; the parser skips the rest of each record, which is most of
    /// its 1.1 GB. Names follow the schema's camel-case JSON names; anything absent stays null.
    /// </summary>
    private static readonly JsonSerializerOptions Projection = new(EvidenceJson.Options) { RespectRequiredConstructorParameters = false, RespectNullableAnnotations = false };

    private sealed class IndexedRecord
    {
        public string RecordId { get; set; } = "";
        public string Probe { get; set; } = "";
        public List<string> BookRefs { get; set; } = [];
        public string Description { get; set; } = "";
        public string QuestionKey { get; set; } = "";
        public string Token { get; set; } = "";
        public IndexedManifest Manifest { get; set; } = new();
        public IndexedQuestion Question { get; set; } = new();
        public IndexedCapture? Capture { get; set; }
        public List<IndexedArtifact> Artifacts { get; set; } = [];
    }

    private sealed class IndexedManifest
    {
        public string Execution { get; set; } = "";
        public string Capture { get; set; } = "";
        public List<string> Problems { get; set; } = [];
    }

    private sealed class IndexedQuestion
    {
        public string CallerSurface { get; set; } = "";
    }

    private sealed class IndexedCapture
    {
        public IndexedEnvironment Environment { get; set; } = new();
        public List<IndexedEvent> Events { get; set; } = [];
    }

    private sealed class IndexedEnvironment
    {
        public string? ToolingRevision { get; set; }
        public string? BookRevision { get; set; }
    }

    private sealed class IndexedEvent
    {
        public string Event { get; set; } = "";
        public int Seq { get; set; }
        public IndexedChannel? ExcepInfo { get; set; }
        public IndexedChannel? ErrorInfo { get; set; }
        public string? Label { get; set; }
        public bool Complete { get; set; }
        public bool Deliberate { get; set; }
        public List<IndexedModule>? Identities { get; set; }
    }

    private sealed class IndexedChannel
    {
        public string? Scode { get; set; }
        public string? GetErrorInfoHresult { get; set; }
        public bool Present { get; set; }
        public IndexedText? Source { get; set; }
        public IndexedText? Description { get; set; }
        public uint? HelpContext { get; set; }
    }

    private sealed class IndexedText
    {
        public string? Text { get; set; }
    }

    private sealed class IndexedModule
    {
        public string Path { get; set; } = "";
        public string? FileVersionNumber { get; set; }
        public string? Sha256 { get; set; }
    }

    private sealed class IndexedArtifact
    {
        public string Role { get; set; } = "";
        public string Kind { get; set; } = "";
        public long Length { get; set; }
        public string Sha256 { get; set; } = "";
    }

    /// <summary>The whole transformation from retained JSON to index rows, in one place.</summary>
    private static IReadOnlyList<Table> Derive(string evidenceDir, string repo)
    {
        var records = new Table("Records",
            [new("RecordId", "TEXT(255)"), new("Probe", "TEXT(255)"), new("BookRefs", "LONGTEXT"), new("Description", "LONGTEXT"),
             new("QuestionKey", "TEXT(80)"), new("Token", "TEXT(120)"), new("Execution", "TEXT(40)"), new("CaptureState", "TEXT(40)"),
             new("Problems", "LONGTEXT"), new("CallerSurface", "TEXT(120)"), new("ToolingRevision", "TEXT(80)"), new("BookRevision", "TEXT(255)"),
             new("RecordPath", "TEXT(255)"), new("RecordSha256", "TEXT(64)")],
            ["RecordId"], []);
        var errors = new Table("ErrorChannels",
            [new("RecordId", "TEXT(255)"), new("Seq", "LONG"), new("Channel", "TEXT(40)"), new("Code", "TEXT(12)"),
             new("Source", "TEXT(255)"), new("Description", "LONGTEXT"), new("HelpContext", "LONG")],
            ["RecordId", "Seq", "Channel"], []);
        // A module observation names its module set; each distinct set is stored once (most records load the same set).
        var observations = new Table("ModuleObservations",
            [new("RecordId", "TEXT(255)"), new("Seq", "LONG"), new("Label", "TEXT(80)"), new("ObservationComplete", "YESNO"), new("ModuleSet", "TEXT(32)")],
            ["RecordId", "Seq"], []);
        var moduleSets = new Table("ModuleSets",
            [new("ModuleSet", "TEXT(32)"), new("Path", "TEXT(255)"), new("FileVersion", "TEXT(80)"), new("Sha256", "TEXT(64)")],
            ["ModuleSet", "Path"], []);
        var knownSets = new HashSet<string>(StringComparer.Ordinal);
        var artifacts = new Table("Artifacts",
            [new("RecordId", "TEXT(255)"), new("Role", "TEXT(255)"), new("Kind", "TEXT(80)"), new("Sha256", "TEXT(64)"), new("ByteLength", "DOUBLE")],
            ["RecordId", "Role"], []);

        string[] files = Directory.Exists(evidenceDir)
            ? [.. Directory.EnumerateFiles(evidenceDir, "*.json", SearchOption.AllDirectories).Where(f => !f.Contains(Path.DirectorySeparatorChar + "artifacts" + Path.DirectorySeparatorChar, StringComparison.Ordinal)).Order(StringComparer.Ordinal)]
            : [];
        // Reading and hashing the records is most of the derivation: it runs in parallel, the rows are then made in file order.
        var parsed = new (IndexedRecord Record, string Sha256)[files.Length];
        Parallel.For(0, files.Length, i =>
        {
            byte[] bytes = File.ReadAllBytes(files[i]);
            parsed[i] = (JsonSerializer.Deserialize<IndexedRecord>(bytes, Projection)!, Convert.ToHexStringLower(SHA256.HashData(bytes)));
        });
        for (int f = 0; f < files.Length; f++)
        {
            string file = files[f];
            IndexedRecord record = parsed[f].Record;
            string id = record.RecordId;
            records.Rows.Add([id, record.Probe, string.Join(" ", record.BookRefs), record.Description, record.QuestionKey, record.Token,
                record.Manifest.Execution, record.Manifest.Capture, string.Join("\n", record.Manifest.Problems), record.Question.CallerSurface,
                record.Capture?.Environment.ToolingRevision, record.Capture?.Environment.BookRevision,
                Path.GetRelativePath(repo, file).Replace('\\', '/'), parsed[f].Sha256]);
            foreach (IndexedArtifact a in record.Artifacts)
            {
                artifacts.Rows.Add([id, a.Role, a.Kind, a.Sha256, (double)a.Length]);
            }
            foreach (IndexedEvent e in record.Capture?.Events ?? [])
            {
                switch (e.Event)
                {
                    case "invoke":
                        if (e.ExcepInfo is { } x)
                        {
                            errors.Rows.Add([id, e.Seq, "excepinfo", x.Scode, x.Source?.Text, x.Description?.Text, (int)x.HelpContext!.Value]);
                        }
                        if (e.ErrorInfo is { Present: true } ei)
                        {
                            errors.Rows.Add([id, e.Seq, "errorInfo", ei.GetErrorInfoHresult, ei.Source?.Text, ei.Description?.Text, ei.HelpContext is { } hc ? (int)hc : null]);
                        }
                        break;
                    case "modules" when !e.Deliberate:
                    {
                        IndexedEvent m = e;
                        string set = Hashing.Sha256Hex(Encoding.UTF8.GetBytes(string.Join("\n", m.Identities!.Select(mi => $"{mi.Path}|{mi.FileVersionNumber}|{mi.Sha256}"))))[..32];
                        observations.Rows.Add([id, e.Seq, m.Label, m.Complete, set]);
                        if (knownSets.Add(set))
                        {
                            foreach (IndexedModule mi in m.Identities!)
                            {
                                moduleSets.Rows.Add([set, mi.Path, mi.FileVersionNumber, mi.Sha256]);
                            }
                        }
                        break;
                    }
                }
            }
        }
        return [records, errors, observations, moduleSets, artifacts];
    }

    private static void Build(string path, IReadOnlyList<Table> tables)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
        var engine = new DBEngine();
        // One transaction per table instead of an implicit one per Update; the lock limit must admit a whole table.
        engine.SetOption((int)SetOptionEnum.dbMaxLocksPerFile, 2_000_000_000);
        Workspace ws = engine.Workspaces[0];
        Database db = engine.CreateDatabase(path, LangGeneral, DatabaseTypeEnum.dbVersion120);
        try
        {
            foreach (Table t in tables)
            {
                var clock = System.Diagnostics.Stopwatch.StartNew();
                db.Execute($"CREATE TABLE [{t.Name}] ({string.Join(", ", t.Columns.Select(c => $"[{c.Name}] {c.Ddl}"))})");
                ws.BeginTrans();
                Recordset rs = db.OpenRecordset(t.Name, RecordsetTypeEnum.dbOpenTable, RecordsetOptionEnum.dbAppendOnly);
                // Field objects are fetched once; each row is then AddNew, one Value put per column, Update.
                Field[] fields = [.. t.Columns.Select(c => rs.Fields[c.Name])];
                foreach (object?[] row in t.Rows)
                {
                    rs.AddNew();
                    for (int i = 0; i < fields.Length; i++)
                    {
                        fields[i].Value = row[i] ?? DBNull.Value;
                    }
                    rs.Update();
                }
                rs.Close();
                ws.CommitTrans();
                Console.WriteLine($"{t.Name}: {t.Rows.Count} rows ({clock.Elapsed.TotalSeconds:F1} s)");
            }
        }
        finally
        {
            db.Close();
        }
        Console.WriteLine(path);
    }

    private static int Verify(string path, IReadOnlyList<Table> tables)
    {
        Database db = new DBEngine().OpenDatabase(path, false, true);
        int mismatches = 0;
        try
        {
            foreach (Table t in tables)
            {
                var clock = System.Diagnostics.Stopwatch.StartNew();
                List<string> expected = t.Rows.Select(r => Canonical(r)).Order(StringComparer.Ordinal).ToList();
                var actual = new List<string>(t.Rows.Count);
                string list = string.Join(", ", t.Columns.Select(c => $"[{c.Name}]"));
                Recordset rs = db.OpenRecordset($"SELECT {list} FROM [{t.Name}]", RecordsetTypeEnum.dbOpenForwardOnly);
                // GetRows returns a chunk as one SAFEARRAY [column, row]: one call per chunk instead of three per cell.
                while (!rs.EOF)
                {
                    var chunk = (object[,])rs.GetRows(50_000);
                    for (int r = 0; r < chunk.GetLength(1); r++)
                    {
                        var row = new object?[t.Columns.Count];
                        for (int i = 0; i < t.Columns.Count; i++)
                        {
                            object value = chunk[i, r];
                            row[i] = value is DBNull ? null : value;
                        }
                        actual.Add(Canonical(row));
                    }
                }
                rs.Close();
                actual.Sort(StringComparer.Ordinal);
                bool same = expected.SequenceEqual(actual, StringComparer.Ordinal);
                Console.WriteLine($"{t.Name}: {actual.Count} rows in index, {expected.Count} derived from JSON: {(same ? "identical" : "DIFFERENT")} ({clock.Elapsed.TotalSeconds:F1} s)");
                if (!same)
                {
                    mismatches++;
                    // Show where they differ: the first rows present on one side only.
                    var inIndex = new HashSet<string>(actual, StringComparer.Ordinal);
                    var inJson = new HashSet<string>(expected, StringComparer.Ordinal);
                    foreach (string row in expected.Where(x => !inIndex.Contains(x)).Take(5))
                    {
                        Console.WriteLine("  only derived: " + Shorten(row));
                    }
                    foreach (string row in actual.Where(x => !inJson.Contains(x)).Take(5))
                    {
                        Console.WriteLine("  only in index: " + Shorten(row));
                    }
                    Console.WriteLine($"  rows differing: {expected.Count(x => !inIndex.Contains(x))} derived, {actual.Count(x => !inJson.Contains(x))} in index");
                }
            }
        }
        finally
        {
            db.Close();
        }
        return mismatches == 0 ? 0 : 1;
    }

    private static void Query(string path, string sql)
    {
        Database db = new DBEngine().OpenDatabase(path, false, true);
        try
        {
            Recordset rs = db.OpenRecordset(sql, RecordsetTypeEnum.dbOpenSnapshot);
            int count = rs.Fields.Count;
            var header = new List<string>();
            for (int i = 0; i < count; i++)
            {
                header.Add(rs.Fields[i].Name);
            }
            Console.WriteLine(string.Join(" | ", header));
            while (!rs.EOF)
            {
                var cells = new List<string>();
                for (int i = 0; i < count; i++)
                {
                    object v = rs.Fields[i].Value;
                    cells.Add(v is DBNull ? "Null" : Convert.ToString(v, CultureInfo.InvariantCulture) ?? "");
                }
                Console.WriteLine(string.Join(" | ", cells));
                rs.MoveNext();
            }
            rs.Close();
        }
        finally
        {
            db.Close();
        }
    }

    private static string Shorten(string row)
    {
        string escaped = string.Concat(row.Select(c => c < ' ' || char.IsSurrogate(c) ? $"\\u{(int)c:X4}" : c.ToString()));
        return escaped.Length > 400 ? escaped[..400] + "…" : escaped;
    }

    /// <summary>Type-tagged canonical row text; the index's typed values must equal the derivation.</summary>
    private static string Canonical(object?[] row)
    {
        var sb = new StringBuilder();
        foreach (object? v in row)
        {
            sb.Append(v switch
            {
                null => "N;",
                string s => "S" + s.Length.ToString(CultureInfo.InvariantCulture) + ":" + s + ";",
                bool b => b ? "B1;" : "B0;",
                int i => "I" + i.ToString(CultureInfo.InvariantCulture) + ";",
                double d => "D" + BitConverter.DoubleToInt64Bits(d).ToString("X16", CultureInfo.InvariantCulture) + ";",
                _ => "?" + v.GetType().Name + ":" + Convert.ToString(v, CultureInfo.InvariantCulture) + ";",
            });
        }
        return sb.ToString();
    }

    private static string RepositoryRoot()
    {
        for (string? dir = Directory.GetCurrentDirectory(); dir is not null; dir = Path.GetDirectoryName(dir))
        {
            if (File.Exists(Path.Combine(dir, "CalcDb.slnx")))
            {
                return dir;
            }
        }
        throw new InvalidOperationException("Run calcdb-evidence-index inside the CalcDb repository.");
    }
}