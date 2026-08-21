namespace RetailPulse.Tests.Packs;

/// <summary>
/// Shared repo-root helpers for pack tests. Mirrors the walk-up pattern
/// used by <c>StorageGovernanceContractTests</c> so tests find files
/// relative to <c>RetailPulse.slnx</c> regardless of the test runner's
/// working directory.
/// </summary>
internal static class PackTestPaths
{
    public static string RepoRoot { get; } = FindRepoRoot();

    public static string PacksRoot { get; } = Path.Combine(RepoRoot, "packs");

    private static string FindRepoRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "RetailPulse.slnx")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        throw new InvalidOperationException(
            "Could not locate repo root (RetailPulse.slnx) walking up from " + AppContext.BaseDirectory);
    }

    /// <summary>
    /// Create an isolated, self-cleaning fixture directory under the test
    /// output tree. Tests use this instead of <c>Path.GetTempPath</c> so
    /// artifacts land alongside the test binaries and are trivially
    /// discoverable during CI triage.
    /// </summary>
    public static string CreateFixtureDirectory(string name)
    {
        string dir = Path.Combine(
            AppContext.BaseDirectory,
            "pack-fixtures",
            name + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }
}
