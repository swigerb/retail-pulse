using System.Text.Json;

namespace RetailPulse.Tests.Eval;

/// <summary>
/// Loads the versioned golden dataset from <c>tests/RetailPulse.Tests/Eval/Data/golden-dataset.json</c>.
/// Locates the repository root the same way every other file-anchored contract test in this
/// project does (walk up from <see cref="AppContext.BaseDirectory"/> until <c>RetailPulse.slnx</c>
/// is found).
/// </summary>
public static class GoldenDatasetLoader
{
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>Load the golden dataset from the repo-committed JSON file.</summary>
    public static GoldenDataset Load()
    {
        string path = DatasetPath();
        string json = File.ReadAllText(path);
        GoldenDataset? dataset = JsonSerializer.Deserialize<GoldenDataset>(json, _jsonOptions);
        return dataset ?? throw new InvalidOperationException($"Golden dataset at '{path}' deserialized to null.");
    }

    /// <summary>Repository-anchored path to the primary golden dataset file.</summary>
    public static string DatasetPath() =>
        Path.Combine(EvalDataDirectory(), "golden-dataset.json");

    /// <summary>Repository-anchored path to the versioned baseline file.</summary>
    public static string BaselinePath() =>
        Path.Combine(EvalDataDirectory(), "baseline-v1.json");

    /// <summary>Repository-anchored path to the known-bad self-test fixture.</summary>
    public static string KnownBadPath() =>
        Path.Combine(EvalDataDirectory(), "known-bad-cases.json");

    /// <summary>Absolute path to <c>tests/RetailPulse.Tests/Eval/Data</c>.</summary>
    public static string EvalDataDirectory() =>
        Path.Combine(RepoRoot(), "tests", "RetailPulse.Tests", "Eval", "Data");

    /// <summary>Walk up from the test binary directory until we find <c>RetailPulse.slnx</c>.</summary>
    public static string RepoRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "RetailPulse.slnx")))
        {
            dir = dir.Parent;
        }
        return dir?.FullName
            ?? throw new InvalidOperationException(
                "Could not locate repo root (RetailPulse.slnx) from " + AppContext.BaseDirectory);
    }
}
