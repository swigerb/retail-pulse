using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace RetailPulse.Contracts;

/// <summary>
/// Diagnostic categories reported when a <see cref="SeedManifest"/> cannot be
/// loaded from a pack's <c>seed/scenario.yaml</c>. The API pack loader (issue
/// #108) uses these values to aggregate seed problems with other pack issues,
/// so callers can convert an <see cref="SeedManifestLoadException"/> into a
/// <c>pack.parse-error</c>, <c>pack.section-missing</c>, or
/// <c>pack.duplicate-key</c> validation issue.
/// </summary>
public enum SeedManifestIssueCategory
{
    /// <summary>The <c>seed/scenario.yaml</c> file could not be located.</summary>
    Missing,
    /// <summary>YAML failed to parse (syntax error, duplicate key, etc.).</summary>
    ParseError,
    /// <summary>YAML parsed but a required section was absent or empty.</summary>
    SectionMissing,
}

/// <summary>
/// Thrown when a <c>seed/scenario.yaml</c> cannot be loaded. Carries the
/// original file path, the missing/malformed section (if any), and a
/// classification the pack loader turns into a validation-issue code.
/// </summary>
public sealed class SeedManifestLoadException : Exception
{
    public SeedManifestLoadException(
        SeedManifestIssueCategory category,
        string filePath,
        string? section,
        string message,
        Exception? inner = null)
        : base(message, inner)
    {
        Category = category;
        FilePath = filePath;
        Section = section;
    }

    public SeedManifestIssueCategory Category { get; }
    public string FilePath { get; }
    public string? Section { get; }
}

/// <summary>
/// Loads <see cref="SeedManifest"/> instances from a pack's <c>seed/</c>
/// directory. YAML deserialization is configured to reject duplicate keys
/// (parity with <c>PackLoader</c> for tenant/agents so a copy-paste mistake
/// in scenario.yaml can't silently drop earlier entries), ignore unmatched
/// properties, and use camelCase naming.
/// </summary>
public static class SeedManifestLoader
{
    /// <summary>Conventional filename inside a pack's seed directory.</summary>
    public const string ScenarioFileName = "scenario.yaml";

    /// <summary>
    /// Load and validate the scenario manifest from the given pack seed
    /// directory. Throws <see cref="SeedManifestLoadException"/> for every
    /// failure mode so callers can classify diagnostics.
    /// </summary>
    public static SeedManifest LoadFromDirectory(string seedDir)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(seedDir);

        string filePath = Path.Combine(seedDir, ScenarioFileName);
        if (!File.Exists(filePath))
        {
            throw new SeedManifestLoadException(
                SeedManifestIssueCategory.Missing,
                filePath,
                section: null,
                message: $"Seed manifest not found at '{filePath}'. Every shipped pack must include seed/scenario.yaml.");
        }

        string yaml = File.ReadAllText(filePath);
        return LoadFromYaml(yaml, filePath);
    }

    /// <summary>
    /// Parse a scenario manifest from a raw YAML string. Exposed for tests and
    /// for internal reuse by the pack loader so parse-time diagnostics stay
    /// consistent across call sites.
    /// </summary>
    public static SeedManifest LoadFromYaml(string yaml, string sourcePath)
    {
        ArgumentNullException.ThrowIfNull(yaml);

        SeedManifest? manifest;
        try
        {
            IDeserializer deserializer = new DeserializerBuilder()
                .WithNamingConvention(CamelCaseNamingConvention.Instance)
                .IgnoreUnmatchedProperties()
                .WithDuplicateKeyChecking()
                .Build();

            manifest = deserializer.Deserialize<SeedManifest>(yaml);
        }
        catch (YamlException ex)
        {
            throw new SeedManifestLoadException(
                SeedManifestIssueCategory.ParseError,
                sourcePath,
                section: null,
                message: $"Failed to parse seed manifest '{sourcePath}': {ex.Message}",
                inner: ex);
        }
        catch (Exception ex) when (ex is not SeedManifestLoadException)
        {
            throw new SeedManifestLoadException(
                SeedManifestIssueCategory.ParseError,
                sourcePath,
                section: null,
                message: $"Failed to parse seed manifest '{sourcePath}': {ex.Message}",
                inner: ex);
        }

        if (manifest is null)
        {
            throw new SeedManifestLoadException(
                SeedManifestIssueCategory.ParseError,
                sourcePath,
                section: null,
                message: $"Seed manifest '{sourcePath}' deserialized to null.");
        }

        Validate(manifest, sourcePath);
        return manifest;
    }

    private static void Validate(SeedManifest manifest, string sourcePath)
    {
        // Each required section throws its own SeedManifestLoadException so the
        // pack loader can name (pack, section) precisely in one diagnostic.
        RequireDict("seasonality.factors", manifest.Seasonality.FactorsMap, sourcePath);
        foreach (KeyValuePair<string, List<SeasonalMonthFactor>> pair in manifest.Seasonality.FactorsMap)
        {
            RequireList($"seasonality.factors.{pair.Key}", pair.Value, sourcePath);
            foreach (SeasonalMonthFactor factor in pair.Value)
            {
                if (factor.Month < 1 || factor.Month > 12)
                {
                    throw new SeedManifestLoadException(
                        SeedManifestIssueCategory.SectionMissing,
                        sourcePath,
                        $"seasonality.factors.{pair.Key}",
                        $"Seed manifest '{sourcePath}' section 'seasonality.factors.{pair.Key}' contains month {factor.Month}; months must be 1..12.");
                }
            }
        }

        RequireDict("competitive.competitorsByCategory", manifest.Competitive.CompetitorsByCategoryMap, sourcePath);
        foreach (KeyValuePair<string, List<string>> pair in manifest.Competitive.CompetitorsByCategoryMap)
        {
            RequireList($"competitive.competitorsByCategory.{pair.Key}", pair.Value, sourcePath);
        }
        RequireList("competitive.pricingSources", manifest.Competitive.PricingSourcesList, sourcePath);
        RequireList("competitive.shareSources", manifest.Competitive.ShareSourcesList, sourcePath);
        RequireList("competitive.activityTypes", manifest.Competitive.ActivityTypesList, sourcePath);
        RequireList("competitive.impactLevels", manifest.Competitive.ImpactLevelsList, sourcePath);
        RequireList("competitive.activityTemplates", manifest.Competitive.ActivityTemplatesList, sourcePath);

        RequireList("promos.types", manifest.Promos.TypesList, sourcePath);
        RequireList("promos.successRatings", manifest.Promos.SuccessRatingsList, sourcePath);
        foreach (PromoTypeConfig type in manifest.Promos.TypesList)
        {
            if (string.IsNullOrWhiteSpace(type.Name))
            {
                throw new SeedManifestLoadException(
                    SeedManifestIssueCategory.SectionMissing,
                    sourcePath,
                    "promos.types",
                    $"Seed manifest '{sourcePath}' contains a promo type with an empty 'name'.");
            }
        }

        RequireList("supply.disruptionTypes", manifest.Supply.DisruptionTypesList, sourcePath);
        RequireList("supply.disruptionSeverities", manifest.Supply.DisruptionSeveritiesList, sourcePath);
        RequireDict("supply.disruptionDescriptions", manifest.Supply.DisruptionDescriptionsMap, sourcePath);
        foreach (KeyValuePair<string, List<string>> pair in manifest.Supply.DisruptionDescriptionsMap)
        {
            RequireList($"supply.disruptionDescriptions.{pair.Key}", pair.Value, sourcePath);
        }

        RequireList("stores.types", manifest.Stores.TypesList, sourcePath);

        RequireList("margin.driverCategories", manifest.Margin.DriverCategoriesList, sourcePath);
        RequireList("margin.trendLabels", manifest.Margin.TrendLabelsList, sourcePath);
    }

    private static void RequireList<T>(string section, IReadOnlyCollection<T>? list, string sourcePath)
    {
        if (list is null || list.Count == 0)
        {
            throw new SeedManifestLoadException(
                SeedManifestIssueCategory.SectionMissing,
                sourcePath,
                section,
                $"Seed manifest '{sourcePath}' is missing required section '{section}'.");
        }
    }

    private static void RequireDict<TValue>(string section, IReadOnlyDictionary<string, TValue>? dict, string sourcePath)
    {
        if (dict is null || dict.Count == 0)
        {
            throw new SeedManifestLoadException(
                SeedManifestIssueCategory.SectionMissing,
                sourcePath,
                section,
                $"Seed manifest '{sourcePath}' is missing required section '{section}'.");
        }
    }

    /// <summary>
    /// Hash the physical contents of every file under a pack's seed
    /// directory so <see cref="PackContentFingerprint"/> style callers can
    /// mix seed content into their fingerprint alongside pack.yaml. Files
    /// are combined in sorted-by-relative-path order to make the digest
    /// deterministic across filesystems.
    /// </summary>
    public static byte[] HashSeedDirectory(string seedDir)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(seedDir);

        using System.Security.Cryptography.SHA256 sha = System.Security.Cryptography.SHA256.Create();
        using var stream = new MemoryStream();

        if (Directory.Exists(seedDir))
        {
            List<string> files = [.. Directory.EnumerateFiles(seedDir, "*", SearchOption.AllDirectories)];
            files.Sort(StringComparer.Ordinal);
            foreach (string file in files)
            {
                string relative = Path.GetRelativePath(seedDir, file).Replace('\\', '/');
                byte[] header = System.Text.Encoding.UTF8.GetBytes(relative + "\n");
                stream.Write(header, 0, header.Length);
                byte[] data = File.ReadAllBytes(file);
                stream.Write(data, 0, data.Length);
                stream.WriteByte((byte)'\n');
            }
        }

        stream.Position = 0;
        return sha.ComputeHash(stream);
    }
}
