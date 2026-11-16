using System.Globalization;
using System.Text;
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
/// The index is written through the DAO primary interop assembly (package ExcelDna.Interop.Dao, types
/// embedded): it is a consumer of ACE, not an oracle capture.
/// </summary>
internal static class Program
{
    private const string LangGeneral = ";LANGID=0x0409;CP=1252;COUNTRY=0";

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

        IReadOnlyList<Table> tables = Derive(evidence, repo);
        switch (command)
        {
            case "build":
                Build(Option("out") ?? defaultDb, tables);
                return 0;
            case "verify":
                return Verify(Option("db") ?? defaultDb, tables);
            case "query":
                Query(Option("db") ?? defaultDb, Option("sql") ?? throw new ArgumentException("--sql is required."));
                return 0;
            default:
                Console.Error.WriteLine("usage: calcdb-evidence-index build|verify|query [--out|--db FILE] [--sql TEXT]");
                return 2;
        }
    }

    private sealed record Column(string Name, string Ddl);

    private sealed record Table(string Name, IReadOnlyList<Column> Columns, IReadOnlyList<string> Key, List<object?[]> Rows);

    /// <summary>The whole transformation from retained JSON to index rows, in one place.</summary>
    private static IReadOnlyList<Table> Derive(string evidenceDir, string repo)
    {
        var records = new Table("Records",
            [new("RecordId", "TEXT(255)"), new("Probe", "TEXT(255)"), new("BookRefs", "LONGTEXT"), new("Description", "LONGTEXT"),
             new("QuestionKey", "TEXT(80)"), new("Token", "TEXT(120)"), new("Execution", "TEXT(40)"), new("CaptureState", "TEXT(40)"),
             new("Problems", "LONGTEXT"), new("CallerSurface", "TEXT(120)"), new("ToolingRevision", "TEXT(80)"), new("BookRevision", "TEXT(255)"),
             new("RecordPath", "TEXT(255)"), new("RecordSha256", "TEXT(64)")],
            ["RecordId"], []);
        var invocations = new Table("Invocations",
            [new("RecordId", "TEXT(255)"), new("Seq", "LONG"), new("ProgramPath", "TEXT(120)"), new("Target", "TEXT(120)"), new("Member", "TEXT(120)"),
             new("InvokeHresult", "TEXT(12)"), new("ResultVt", "TEXT(40)"), new("ResultValue", "LONGTEXT"), new("ResultBytes", "LONGTEXT")],
            ["RecordId", "Seq"], []);
        var errors = new Table("ErrorChannels",
            [new("RecordId", "TEXT(255)"), new("Seq", "LONG"), new("Channel", "TEXT(40)"), new("Code", "TEXT(12)"),
             new("Source", "LONGTEXT"), new("Description", "LONGTEXT"), new("HelpContext", "LONG")],
            ["RecordId", "Seq", "Channel"], []);
        var modules = new Table("Modules",
            [new("RecordId", "TEXT(255)"), new("Seq", "LONG"), new("Label", "TEXT(80)"), new("Path", "LONGTEXT"), new("FileVersion", "TEXT(80)"),
             new("Sha256", "TEXT(64)"), new("ObservationComplete", "YESNO")],
            ["RecordId", "Seq", "Path"], []);
        var artifacts = new Table("Artifacts",
            [new("RecordId", "TEXT(255)"), new("Role", "TEXT(255)"), new("Kind", "TEXT(80)"), new("Sha256", "TEXT(64)"), new("ByteLength", "DOUBLE")],
            ["RecordId", "Role"], []);

        foreach (string file in Directory.Exists(evidenceDir)
                     ? Directory.EnumerateFiles(evidenceDir, "*.json", SearchOption.AllDirectories).Where(f => !f.Contains(Path.DirectorySeparatorChar + "artifacts" + Path.DirectorySeparatorChar, StringComparison.Ordinal)).Order(StringComparer.Ordinal)
                     : Enumerable.Empty<string>())
        {
            EvidenceRecord record = EvidenceJson.Read<EvidenceRecord>(file);
            string id = record.RecordId;
            records.Rows.Add([id, record.Probe, string.Join(" ", record.BookRefs), record.Description, record.QuestionKey, record.Token,
                record.Manifest.Execution, record.Manifest.Capture, string.Join("\n", record.Manifest.Problems), record.Question.CallerSurface,
                record.Capture?.Environment.ToolingRevision, record.Capture?.Environment.BookRevision,
                Path.GetRelativePath(repo, file).Replace('\\', '/'), Hashing.Sha256File(file)]);
            foreach (RetainedArtifact a in record.Artifacts)
            {
                artifacts.Rows.Add([id, a.Role, a.Kind, a.Sha256, (double)a.Length]);
            }
            foreach (CaptureEvent e in record.Capture?.Events ?? [])
            {
                switch (e)
                {
                    case InvokeEvent inv:
                        invocations.Rows.Add([id, e.Seq, e.ProgramPath, inv.Target, inv.Member, inv.InvokeHresult ?? inv.GetIdsOfNamesHresult,
                            inv.Result?.VtName, Render(inv.Result), inv.Result?.Bytes]);
                        if (inv.ExcepInfo is { } x)
                        {
                            errors.Rows.Add([id, e.Seq, "excepinfo", x.Scode, x.Source?.Text, x.Description?.Text, (int)x.HelpContext]);
                        }
                        if (inv.ErrorInfo is { Present: true } ei)
                        {
                            errors.Rows.Add([id, e.Seq, "errorInfo", ei.GetErrorInfoHresult, ei.Source?.Text, ei.Description?.Text, ei.HelpContext is { } hc ? (int)hc : null]);
                        }
                        break;
                    case ModulesEvent m when !m.Deliberate:
                        foreach (ModuleIdentity mi in m.Identities)
                        {
                            modules.Rows.Add([id, e.Seq, m.Label, mi.Path, mi.FileVersionNumber, mi.Sha256, m.Complete]);
                        }
                        break;
                }
            }
        }
        return [records, invocations, errors, modules, artifacts];
    }

    private static string? Render(CapturedValue? v) =>
        v is null ? null : v.Integer ?? v.Text ?? v.Display ?? v.Bits ?? v.Identity ?? v.Unsupported;

    private static void Build(string path, IReadOnlyList<Table> tables)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
        var engine = new DBEngine();
        Database db = engine.CreateDatabase(path, LangGeneral, DatabaseTypeEnum.dbVersion120);
        try
        {
            foreach (Table t in tables)
            {
                string columns = string.Join(", ", t.Columns.Select(c => $"[{c.Name}] {c.Ddl}"));
                db.Execute($"CREATE TABLE [{t.Name}] ({columns})");
                Recordset rs = db.OpenRecordset(t.Name, RecordsetTypeEnum.dbOpenDynaset);
                foreach (object?[] row in t.Rows)
                {
                    rs.AddNew();
                    for (int i = 0; i < t.Columns.Count; i++)
                    {
                        rs.Fields[t.Columns[i].Name].Value = row[i] ?? DBNull.Value;
                    }
                    rs.Update();
                }
                rs.Close();
                Console.WriteLine($"{t.Name}: {t.Rows.Count} rows");
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
                List<string> expected = t.Rows.Select(r => Canonical(r)).Order(StringComparer.Ordinal).ToList();
                var actual = new List<string>();
                Recordset rs = db.OpenRecordset($"SELECT * FROM [{t.Name}]", RecordsetTypeEnum.dbOpenSnapshot);
                while (!rs.EOF)
                {
                    var row = new object?[t.Columns.Count];
                    for (int i = 0; i < t.Columns.Count; i++)
                    {
                        object value = rs.Fields[t.Columns[i].Name].Value;
                        row[i] = value is DBNull ? null : value;
                    }
                    actual.Add(Canonical(row));
                    rs.MoveNext();
                }
                rs.Close();
                actual.Sort(StringComparer.Ordinal);
                bool same = expected.SequenceEqual(actual, StringComparer.Ordinal);
                Console.WriteLine($"{t.Name}: {actual.Count} rows in index, {expected.Count} derived from JSON: {(same ? "identical" : "DIFFERENT")}");
                if (!same)
                {
                    mismatches++;
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
