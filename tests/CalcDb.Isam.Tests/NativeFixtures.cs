namespace CalcDb.Isam.Tests;

/// <summary>Retained oracle fixtures (evidence/artifacts) by SHA-256, from the repository the tests run in.</summary>
internal static class NativeFixtures
{
    public static byte[] Read(string sha256) => CalcDb.Evidence.ArtifactStore.ForRepository(Repository()).ReadAll(sha256);

    private static string Repository()
    {
        for (string? dir = AppContext.BaseDirectory; dir is not null; dir = Path.GetDirectoryName(dir))
        {
            if (File.Exists(Path.Combine(dir, "CalcDb.slnx")))
            {
                return dir;
            }
        }
        throw new InvalidOperationException("Repository root not found.");
    }
}
