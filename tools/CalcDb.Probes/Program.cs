using System.Reflection;
using CalcDb.Evidence;

namespace CalcDb.Probes;

/// <summary>A book card's probes. Each card lives in its own file under Cards/.</summary>
public interface ICard
{
    IEnumerable<Probe> Probes();
}

/// <summary>
/// <c>calcdb-probes generate [--card ID]</c>: writes every defined probe as
/// <c>probes/&lt;card&gt;/&lt;name&gt;.question.json</c>, resolving named fixtures from <c>probes/fixtures.json</c>.
/// A probe whose fixture is not frozen yet is reported and skipped. Files are rewritten only when their
/// content changes; a changed question has a new key, so it becomes a new job, never a rerun of an old one.
/// <c>calcdb-probes list</c> prints the probes.
/// </summary>
internal static class Program
{
    public static readonly string[] Recipe = ["clearErrorInfoBefore", "excepinfo", "deferredFill", "errorInfo"];

    private static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        string repo = RepositoryRoot();
        string probesDir = Path.Combine(repo, "probes");
        string? onlyCard = args.SkipWhile(a => a != "--card").Skip(1).FirstOrDefault();
        IReadOnlyDictionary<string, FixtureInput> fixtures = LoadFixtures(probesDir);
        List<Probe> probes = AllProbes().Where(p => onlyCard is null || p.Card == onlyCard).ToList();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (Probe p in probes)
        {
            if (!names.Add(p.Card + "/" + p.Name))
            {
                Console.Error.WriteLine($"duplicate probe {p.Card}/{p.Name}");
                return 2;
            }
        }
        string command = args.Length > 0 ? args[0] : "generate";
        if (command == "list")
        {
            foreach (Probe p in probes)
            {
                Console.WriteLine($"{p.Card}/{p.Name}  [{string.Join(" ", p.Refs)}]");
            }
            return 0;
        }

        int written = 0, unchanged = 0, waiting = 0;
        foreach (Probe p in probes)
        {
            var missing = p.Fixtures.Where(f => !fixtures.ContainsKey(f.Fixture)).Select(f => f.Fixture).ToList();
            if (missing.Count > 0)
            {
                Console.WriteLine($"waiting {p.Card}/{p.Name}: fixture {string.Join(", ", missing)} not frozen yet");
                waiting++;
                continue;
            }
            var builder = new P();
            p.Program(builder);
            var question = new Question
            {
                Probe = p.Card + "/" + p.Name,
                BookRefs = p.Refs,
                Description = p.Description,
                CallerSurface = "native-idispatch/x64",
                InvokeLcid = 1033,
                ObservationRecipe = Recipe,
                Inputs = p.Fixtures.Select(f => fixtures[f.Fixture] with { Role = f.Role }).ToList(),
                Program = builder.Ops,
            };
            question.Validate();
            string dir = Path.Combine(probesDir, p.Card);
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, p.Name + ".question.json");
            string json = EvidenceJson.Serialize(question);
            if (File.Exists(path) && File.ReadAllText(path) == json)
            {
                unchanged++;
                continue;
            }
            File.WriteAllText(path, json);
            written++;
        }
        Console.WriteLine($"{written} written, {unchanged} unchanged, {waiting} waiting for fixtures");
        return 0;
    }

    private static IEnumerable<Probe> AllProbes() =>
        typeof(Program).Assembly.GetTypes()
            .Where(t => typeof(ICard).IsAssignableFrom(t) && t is { IsInterface: false, IsAbstract: false })
            .OrderBy(t => t.Name, StringComparer.Ordinal)
            .SelectMany(t => ((ICard)Activator.CreateInstance(t)!).Probes());

    private static IReadOnlyDictionary<string, FixtureInput> LoadFixtures(string probesDir)
    {
        string path = Path.Combine(probesDir, "fixtures.json");
        return File.Exists(path)
            ? EvidenceJson.Read<Dictionary<string, FixtureInput>>(path)
            : new Dictionary<string, FixtureInput>();
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
        throw new InvalidOperationException("Run calcdb-probes inside the CalcDb repository.");
    }
}
