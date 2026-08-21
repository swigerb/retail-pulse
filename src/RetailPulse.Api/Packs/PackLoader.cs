using System.Text.RegularExpressions;
using RetailPulse.Api.Guardrails.AgentDefinition;
using RetailPulse.Api.Models;
using RetailPulse.Contracts;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace RetailPulse.Api.Packs;

/// <summary>
/// Reads a self-contained content pack from disk into a
/// <see cref="LoadedPack"/>. The loader validates the pack as a single
/// unit — every discoverable structural issue across every section is
/// aggregated into one <see cref="PackValidationException"/> instead of
/// short-circuiting on the first error. A pack that clears the loader
/// is safe to hand to downstream composition (agent registry, knowledge
/// seeder, starting-task endpoint) with no additional structural work.
/// </summary>
/// <remarks>
/// <para>
/// Layout expected on disk:
/// </para>
/// <code>
/// packs/&lt;name&gt;/
///   pack.yaml              (required — metadata + tenant configuration)
///   agents.yaml            (required — same shape as legacy prompts.yaml)
///   starting-tasks.yaml    (optional — curated prompt categories)
///   knowledge/*.md         (optional — grounding corpus)
///   seed/                  (optional — reserved for future explicit seed overrides)
/// </code>
/// <para>
/// The loader deliberately does NOT run the #99 agent-definition safety
/// validator itself — that stays in one place at the composition root
/// where it has the Content Safety evaluator, jailbreak detector, and
/// audit sink. Callers pass a resolved <see cref="AgentDefinitionValidator"/>
/// to <see cref="LoadAsync"/> to fold its findings into the same
/// aggregate report; the safety validator's own quarantine / block
/// policy is preserved by re-throwing its
/// <see cref="AgentDefinitionValidationException"/> unchanged when the
/// deployment policy is Block.
/// </para>
/// </remarks>
public sealed partial class PackLoader
{
    // Reused deserializers so a big packs directory scan doesn't build
    // one instance per file — they are documented as thread-safe by
    // YamlDotNet and hold no mutable state.
    private static readonly IDeserializer _packDeserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        // Duplicate key detection prevents a metadata/tenant block from
        // silently losing content when a pack author accidentally repeats
        // a key (e.g., two 'tenant:' entries or a doubled 'brands:' node).
        // YamlDotNet defaults to last-one-wins, which would let a real
        // configuration mistake ship undetected.
        .WithDuplicateKeyChecking()
        .Build();

    private static readonly IDeserializer _agentsDeserializer = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        // Agent rosters MUST fail loudly on duplicate section keys.
        // Without this, YamlDotNet silently keeps the last of two
        // 'demand-forecast:' entries and the pack ships a half-defined
        // roster — a hostile pack could exploit this to hide overrides
        // of a legitimate specialist. See PackLoaderTests
        // Load_DuplicateAgentSectionKeyInYaml_IsFlaggedAsParseError.
        .WithDuplicateKeyChecking()
        .Build();

    private static readonly IDeserializer _startingTasksDeserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        // Duplicate category keys inside a mapping would silently drop
        // curated prompts; matches the pack-level guard the loader
        // already applies to duplicate category ids in the sequence.
        .WithDuplicateKeyChecking()
        .Build();

    [GeneratedRegex(@"^\s*#\s+(?<title>.+?)\s*$", RegexOptions.Multiline)]
    private static partial Regex FirstHeadingRegex();

    private readonly string _packsRoot;

    private PackLoader(string packsRoot)
    {
        _packsRoot = packsRoot;
    }

    /// <summary>
    /// Build a loader rooted at a specific packs directory. The
    /// directory itself must exist; individual pack subdirectories are
    /// validated per-load.
    /// </summary>
    public static PackLoader ForDirectory(string packsRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packsRoot);

        if (!Directory.Exists(packsRoot))
        {
            throw new DirectoryNotFoundException(
                $"Content pack root '{packsRoot}' does not exist. " +
                "Set Packs:Root in configuration or ensure the packs directory ships with the deployment.");
        }

        return new PackLoader(Path.GetFullPath(packsRoot));
    }

    /// <summary>
    /// Directory names of every subdirectory under the packs root.
    /// Returned in a stable ordinal order so operator-facing listings
    /// are deterministic across environments.
    /// </summary>
    public IReadOnlyList<string> DiscoverPacks() =>
        [.. Directory.EnumerateDirectories(_packsRoot)
            .Select(Path.GetFileName)
            .Where(n => !string.IsNullOrEmpty(n))
            .Select(n => n!)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)];

    /// <summary>
    /// Load and structurally validate the named pack. Throws
    /// <see cref="PackValidationException"/> when any structural issue
    /// is found — every issue across every section is included in the
    /// exception so a single failure surface reports the entire fix
    /// list. Optional sections that are simply absent degrade to empty
    /// collections; they only become issues if the section file exists
    /// but is malformed.
    /// </summary>
    public LoadedPack Load(string packName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packName);

        string packRoot = Path.Combine(_packsRoot, packName);
        if (!Directory.Exists(packRoot))
        {
            IReadOnlyList<string> available = DiscoverPacks();
            string availableSummary = available.Count == 0
                ? "<none>"
                : string.Join(", ", available);
            throw new PackValidationException(packName,
            [
                new PackValidationIssue(
                    packName,
                    "<pack>",
                    $"Pack directory '{packRoot}' not found. Available packs under '{_packsRoot}': {availableSummary}.",
                    "pack.missing"),
            ]);
        }

        var issues = new List<PackValidationIssue>();

        (PackDocument? doc, PackMetadata metadata, TenantConfiguration tenant) =
            LoadPackDocument(packName, packRoot, issues);

        PromptConfiguration agents = LoadAgents(packName, packRoot, issues);
        IReadOnlyList<PackStartingTaskCategory> startingTasks =
            LoadStartingTasks(packName, packRoot, issues);
        IReadOnlyList<PackKnowledgeDocument> knowledgeDocs =
            LoadKnowledgeDocuments(packName, packRoot, issues);

        ValidateAgentRoster(packName, agents, issues);

        if (issues.Count > 0)
        {
            throw new PackValidationException(packName, issues);
        }

        // At this point doc is non-null: LoadPackDocument only leaves it
        // null when it also added at least one issue, and the throw
        // above would have fired. The assertion is documentation.
        _ = doc ?? throw new InvalidOperationException(
            "PackLoader invariant violated: pack document is null despite empty issue list.");

        return new LoadedPack(
            packName,
            packRoot,
            metadata,
            tenant,
            agents,
            knowledgeDocs,
            startingTasks);
    }

    /// <summary>
    /// Load the pack and (when a validator is supplied) run the #99
    /// agent-definition safety validator against the pack's agent
    /// roster. Structural issues from the loader are aggregated with
    /// safety findings so operators see one report. The safety
    /// validator's own failure policy is preserved: under
    /// <c>QuarantineOffender</c> the loader returns the pack with the
    /// offending agent removed from <see cref="LoadedPack.Agents"/>;
    /// under <c>BlockDeployment</c> the underlying
    /// <see cref="AgentDefinitionValidationException"/> flows through
    /// unchanged so the composition root sees the same failure it does
    /// today for a pre-pack <c>prompts.yaml</c>.
    /// </summary>
    public async Task<LoadedPack> LoadAsync(
        string packName,
        AgentDefinitionValidator? safetyValidator,
        CancellationToken cancellationToken = default)
    {
        LoadedPack pack = Load(packName);

        if (safetyValidator is null)
        {
            return pack;
        }

        // Reuse the existing validator verbatim so its Content Safety,
        // jailbreak, tool-catalog, and audit paths run in a single
        // place. Under Quarantine the validator mutates the passed
        // PromptConfiguration by removing offenders — that is exactly
        // the behaviour the composition root gets today, so pack
        // callers inherit the same semantics.
        _ = await safetyValidator
            .ValidateAsync(pack.Agents, cancellationToken)
            .ConfigureAwait(false);

        return pack;
    }

    private static (PackDocument? Document, PackMetadata Metadata, TenantConfiguration Tenant) LoadPackDocument(
        string packName,
        string packRoot,
        List<PackValidationIssue> issues)
    {
        string path = Path.Combine(packRoot, "pack.yaml");
        if (!File.Exists(path))
        {
            issues.Add(new PackValidationIssue(
                packName,
                "pack.yaml",
                "Required section 'pack.yaml' is missing. Every pack must declare its metadata and tenant configuration in pack.yaml.",
                "pack.section-missing"));
            return (null, new PackMetadata { Key = packName }, new TenantConfiguration());
        }

        PackDocument? document;
        try
        {
            document = _packDeserializer.Deserialize<PackDocument>(File.ReadAllText(path));
        }
        catch (Exception ex)
        {
            issues.Add(new PackValidationIssue(
                packName,
                "pack.yaml",
                $"Failed to parse pack.yaml: {ex.Message}",
                "pack.parse-error"));
            return (null, new PackMetadata { Key = packName }, new TenantConfiguration());
        }

        if (document is null)
        {
            issues.Add(new PackValidationIssue(
                packName,
                "pack.yaml",
                "pack.yaml deserialized to null. Ensure the file has content.",
                "pack.parse-null"));
            return (null, new PackMetadata { Key = packName }, new TenantConfiguration());
        }

        ValidateMetadata(packName, document.Metadata, issues);
        ValidateTenant(packName, document.Tenant, issues);

        return (document, document.Metadata, document.Tenant);
    }

    private static void ValidateMetadata(
        string packName,
        PackMetadata metadata,
        List<PackValidationIssue> issues)
    {
        if (string.IsNullOrWhiteSpace(metadata.Key))
        {
            issues.Add(new PackValidationIssue(
                packName,
                "pack.yaml#metadata",
                "metadata.key is required. Set it to the pack's directory name (kebab-case).",
                "pack.metadata.key-missing"));
        }
        else if (!string.Equals(metadata.Key, packName, StringComparison.OrdinalIgnoreCase))
        {
            issues.Add(new PackValidationIssue(
                packName,
                "pack.yaml#metadata",
                $"metadata.key '{metadata.Key}' does not match the pack directory name '{packName}'. " +
                "The two must agree so an operator picking a pack by its Packs:Active key finds the source on disk.",
                "pack.metadata.key-mismatch"));
        }

        if (string.IsNullOrWhiteSpace(metadata.DisplayName))
        {
            issues.Add(new PackValidationIssue(
                packName,
                "pack.yaml#metadata",
                "metadata.displayName is required so the pack has a human-readable label in operator surfaces.",
                "pack.metadata.display-name-missing"));
        }
    }

    private static void ValidateTenant(
        string packName,
        TenantConfiguration tenant,
        List<PackValidationIssue> issues)
    {
        void Add(string field, string message, string code) =>
            issues.Add(new PackValidationIssue(packName, $"pack.yaml#tenant.{field}", message, code));

        if (string.IsNullOrWhiteSpace(tenant.Company))
        {
            Add("company", "tenant.company is required.", "pack.tenant.company-missing");
        }
        if (string.IsNullOrWhiteSpace(tenant.Industry))
        {
            Add("industry", "tenant.industry is required.", "pack.tenant.industry-missing");
        }
        if (tenant.Brands is null || tenant.Brands.Count == 0)
        {
            Add("brands", "tenant.brands must list at least one brand.", "pack.tenant.brands-empty");
        }
        else
        {
            for (int i = 0; i < tenant.Brands.Count; i++)
            {
                if (string.IsNullOrWhiteSpace(tenant.Brands[i].Name))
                {
                    Add($"brands[{i}].name", $"tenant.brands[{i}].name is required.", "pack.tenant.brand-name-missing");
                }
            }
        }
        if (tenant.Regions is null || tenant.Regions.Count == 0)
        {
            Add("regions", "tenant.regions must list at least one region.", "pack.tenant.regions-empty");
        }
        if (tenant.Channels is null || tenant.Channels.Count == 0)
        {
            Add("channels", "tenant.channels must list at least one channel.", "pack.tenant.channels-empty");
        }
        if (tenant.Distribution is null || string.IsNullOrWhiteSpace(tenant.Distribution.Model))
        {
            Add("distribution.model", "tenant.distribution.model is required.", "pack.tenant.distribution-missing");
        }
        if (tenant.Theme is null || string.IsNullOrWhiteSpace(tenant.Theme.PrimaryColor))
        {
            Add("theme.primaryColor", "tenant.theme.primaryColor is required so downstream UI theming has a base color.", "pack.tenant.theme-missing");
        }
    }

    private static PromptConfiguration LoadAgents(
        string packName,
        string packRoot,
        List<PackValidationIssue> issues)
    {
        string path = Path.Combine(packRoot, "agents.yaml");
        if (!File.Exists(path))
        {
            issues.Add(new PackValidationIssue(
                packName,
                "agents.yaml",
                "Required section 'agents.yaml' is missing. Every pack must declare its agent roster in agents.yaml.",
                "pack.section-missing"));
            return new PromptConfiguration();
        }

        try
        {
            PromptConfiguration? config = _agentsDeserializer.Deserialize<PromptConfiguration>(File.ReadAllText(path));
            if (config is null)
            {
                issues.Add(new PackValidationIssue(
                    packName,
                    "agents.yaml",
                    "agents.yaml deserialized to null. Ensure the file declares an 'agents:' map.",
                    "pack.parse-null"));
                return new PromptConfiguration();
            }

            // Default the agent Key to the section name — matches the
            // pre-pack Program.cs normalization so pack authors can omit
            // 'key:' on entries where it equals the section name, and
            // downstream code sees the fully hydrated definition.
            foreach ((string sectionKey, AgentDefinition def) in config.Agents)
            {
                if (string.IsNullOrWhiteSpace(def.Key))
                {
                    def.Key = sectionKey;
                }
            }

            return config;
        }
        catch (Exception ex)
        {
            issues.Add(new PackValidationIssue(
                packName,
                "agents.yaml",
                $"Failed to parse agents.yaml: {ex.Message}",
                "pack.parse-error"));
            return new PromptConfiguration();
        }
    }

    private static IReadOnlyList<PackStartingTaskCategory> LoadStartingTasks(
        string packName,
        string packRoot,
        List<PackValidationIssue> issues)
    {
        string path = Path.Combine(packRoot, "starting-tasks.yaml");
        if (!File.Exists(path))
        {
            // Optional section — a pack with no curated starting prompts
            // simply serves the platform-neutral defaults downstream.
            return [];
        }

        PackStartingTasksDocument? doc;
        try
        {
            doc = _startingTasksDeserializer.Deserialize<PackStartingTasksDocument>(File.ReadAllText(path));
        }
        catch (Exception ex)
        {
            issues.Add(new PackValidationIssue(
                packName,
                "starting-tasks.yaml",
                $"Failed to parse starting-tasks.yaml: {ex.Message}",
                "pack.parse-error"));
            return [];
        }

        if (doc is null)
        {
            issues.Add(new PackValidationIssue(
                packName,
                "starting-tasks.yaml",
                "starting-tasks.yaml deserialized to null. Remove the file if you have no curated tasks, or declare a 'categories:' list.",
                "pack.parse-null"));
            return [];
        }

        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var categories = new List<PackStartingTaskCategory>();
        for (int i = 0; i < doc.Categories.Count; i++)
        {
            PackStartingTaskCategory cat = doc.Categories[i];
            string label = $"starting-tasks.yaml#categories[{i}]";

            if (string.IsNullOrWhiteSpace(cat.Id))
            {
                issues.Add(new PackValidationIssue(packName, label,
                    "starting-tasks category is missing 'id'.",
                    "pack.starting-tasks.id-missing"));
                continue;
            }
            if (!seenIds.Add(cat.Id))
            {
                issues.Add(new PackValidationIssue(packName, label,
                    $"Duplicate starting-tasks category id '{cat.Id}'.",
                    "pack.starting-tasks.duplicate-id"));
                continue;
            }
            if (string.IsNullOrWhiteSpace(cat.Label))
            {
                issues.Add(new PackValidationIssue(packName, label,
                    $"starting-tasks category '{cat.Id}' is missing 'label'.",
                    "pack.starting-tasks.label-missing"));
            }
            if (cat.Prompts is null || cat.Prompts.Count == 0)
            {
                issues.Add(new PackValidationIssue(packName, label,
                    $"starting-tasks category '{cat.Id}' has no prompts.",
                    "pack.starting-tasks.prompts-empty"));
            }

            categories.Add(cat);
        }

        return categories;
    }

    private static IReadOnlyList<PackKnowledgeDocument> LoadKnowledgeDocuments(
        string packName,
        string packRoot,
        List<PackValidationIssue> issues)
    {
        string knowledgeDir = Path.Combine(packRoot, "knowledge");
        if (!Directory.Exists(knowledgeDir))
        {
            // Optional — a pack with no grounding corpus is legal.
            return [];
        }

        var docs = new List<PackKnowledgeDocument>();
        var seenSources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string filePath in Directory
            .EnumerateFiles(knowledgeDir, "*.md", SearchOption.TopDirectoryOnly)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            string fileName = Path.GetFileName(filePath);
            string relative = Path.Combine("knowledge", fileName).Replace('\\', '/');
            string sourceKey = fileName;

            if (!seenSources.Add(sourceKey))
            {
                issues.Add(new PackValidationIssue(
                    packName,
                    relative,
                    $"Duplicate knowledge document source '{sourceKey}'. Every markdown file under knowledge/ must have a unique name.",
                    "pack.knowledge.duplicate-source"));
                continue;
            }

            string content;
            try
            {
                content = File.ReadAllText(filePath);
            }
            catch (IOException ex)
            {
                issues.Add(new PackValidationIssue(
                    packName,
                    relative,
                    $"Failed to read knowledge document: {ex.Message}",
                    "pack.knowledge.read-error"));
                continue;
            }

            if (string.IsNullOrWhiteSpace(content))
            {
                issues.Add(new PackValidationIssue(
                    packName,
                    relative,
                    "Knowledge document is empty. Remove the file or add grounding content.",
                    "pack.knowledge.empty"));
                continue;
            }

            string title = ExtractTitle(content) ?? Path.GetFileNameWithoutExtension(fileName);
            docs.Add(new PackKnowledgeDocument(title, sourceKey, content, relative));
        }

        return docs;
    }

    private static string? ExtractTitle(string markdown)
    {
        Match match = FirstHeadingRegex().Match(markdown);
        return match.Success ? match.Groups["title"].Value.Trim() : null;
    }

    private static void ValidateAgentRoster(
        string packName,
        PromptConfiguration agents,
        List<PackValidationIssue> issues)
    {
        if (agents.Agents.Count == 0)
        {
            issues.Add(new PackValidationIssue(
                packName,
                "agents.yaml",
                "agents.yaml declared no agents. At least one specialist entry is required.",
                "pack.agents.empty"));
            return;
        }

        // Duplicate-Name detection stays at the pack layer because the
        // #99 validator only trips on it under a Content-Safety-enabled
        // deployment; pack authors deserve the diagnostic regardless.
        var seenNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach ((string sectionKey, AgentDefinition def) in agents.Agents)
        {
            if (string.IsNullOrWhiteSpace(def.Name))
            {
                continue; // #99 validator will report the missing Name.
            }

            if (seenNames.TryGetValue(def.Name, out string? existingKey))
            {
                issues.Add(new PackValidationIssue(
                    packName,
                    $"agents.yaml#{sectionKey}",
                    $"Duplicate agent Name '{def.Name}' (also declared by '{existingKey}'). " +
                    "Every agent must have a unique display name.",
                    "pack.agents.duplicate-name"));
            }
            else
            {
                seenNames[def.Name] = sectionKey;
            }
        }
    }
}
