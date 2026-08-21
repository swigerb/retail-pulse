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
///   seed/scenario.yaml     (required — MCP scenario seed manifest, issue #108)
/// </code>
/// <para>
/// The loader is the single caller of the #99 agent-definition safety
/// validator on the pack-load path — issue #108 aggregates its findings
/// with structural issues instead of letting the validator throw its
/// own <see cref="AgentDefinitionValidationException"/> ahead of the
/// pack's own report. Under <c>QuarantineOffender</c> the validator
/// still mutates the roster in place; under <c>RefuseStartup</c> its
/// violations are captured, converted to
/// <see cref="PackValidationIssue"/> rows, and thrown together with
/// every other discoverable problem in a single
/// <see cref="PackValidationException"/> so operators see the entire
/// fix list at once.
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

        return !Directory.Exists(packsRoot)
            ? throw new DirectoryNotFoundException(
                $"Content pack root '{packsRoot}' does not exist. " +
                "Set Packs:Root in configuration or ensure the packs directory ships with the deployment.")
            : new PackLoader(Path.GetFullPath(packsRoot));
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
        LoadedPack pack = LoadInternal(packName, packRoot, issues);

        return issues.Count > 0 ? throw new PackValidationException(packName, issues) : pack;
    }

    /// <summary>
    /// Load the pack and (when a validator is supplied) run the #99
    /// agent-definition safety validator against the pack's agent
    /// roster. Structural issues from the loader are aggregated with
    /// safety findings so operators see one report. The safety
    /// validator's own failure policy is preserved: under
    /// <c>QuarantineOffender</c> the loader returns the pack with the
    /// offending agent removed from <see cref="LoadedPack.Agents"/>;
    /// under <c>RefuseStartup</c> the safety violations become
    /// <c>pack.agents.safety.&lt;ruleId&gt;</c> issues and are thrown
    /// alongside every structural issue in a single
    /// <see cref="PackValidationException"/>.
    /// </summary>
    public async Task<LoadedPack> LoadAsync(
        string packName,
        AgentDefinitionValidator? safetyValidator,
        CancellationToken cancellationToken = default)
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
        LoadedPack pack = LoadInternal(packName, packRoot, issues);

        // Run the #99 safety validator against every structurally-
        // parseable agent — even when non-agent sections (e.g., seed or
        // pack.yaml) already have issues — so the caller receives a
        // single aggregate diagnostic. Under RefuseStartup the validator
        // throws its own aggregate exception; we catch it here and
        // translate each violation into a pack-scoped issue so the pack
        // is the single reporting surface. Under QuarantineOffender the
        // validator returns normally after removing offenders from the
        // roster — no throw to translate, and the caller sees the same
        // Quarantine semantics it does today.
        if (safetyValidator is not null && pack.Agents.Agents.Count > 0)
        {
            try
            {
                _ = await safetyValidator
                    .ValidateAsync(pack.Agents, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (AgentDefinitionValidationException ex)
            {
                foreach (AgentDefinitionViolation v in ex.Violations)
                {
                    issues.Add(new PackValidationIssue(
                        packName,
                        $"agents.yaml#{v.AgentKey}",
                        $"[{v.Field}] {v.Message}",
                        $"pack.agents.safety.{v.RuleId}"));
                }
            }
        }

        return issues.Count > 0 ? throw new PackValidationException(packName, issues) : pack;
    }

    /// <summary>
    /// Structural load shared by <see cref="Load(string)"/> and
    /// <see cref="LoadAsync"/>. Every discoverable structural issue is
    /// appended to <paramref name="issues"/>; the returned pack is
    /// always non-null so callers can still hand it to downstream code
    /// (agent safety validator) for aggregate reporting even when
    /// non-agent sections already reported problems.
    /// </summary>
    private static LoadedPack LoadInternal(string packName, string packRoot, List<PackValidationIssue> issues)
    {
        (PackDocument? _, PackMetadata metadata, TenantConfiguration tenant) =
            LoadPackDocument(packName, packRoot, issues);

        PromptConfiguration agents = LoadAgents(packName, packRoot, issues);
        IReadOnlyList<PackStartingTaskCategory> startingTasks =
            LoadStartingTasks(packName, packRoot, issues);
        IReadOnlyList<PackKnowledgeDocument> knowledgeDocs =
            LoadKnowledgeDocuments(packName, packRoot, issues);
        SeedManifest seed = LoadSeedManifest(packName, packRoot, issues);

        ValidateAgentRoster(packName, agents, issues);

        return new LoadedPack(
            packName,
            packRoot,
            metadata,
            tenant,
            agents,
            knowledgeDocs,
            startingTasks,
            seed);
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
        var withOrder = new List<(int SourceIndex, PackStartingTaskCategory Category)>();
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

            PackStartingTaskCategory normalized = NormalizeCategoryTasks(packName, label, cat, issues);
            withOrder.Add((i, normalized));
        }

        // Issue #109 — explicit ordering. Category-level `order:` places a
        // category at the requested position; ties break on source-array
        // index so an author who leaves the field off entirely still gets
        // deterministic behavior.
        return [.. withOrder
            .OrderBy(x => x.Category.Order ?? int.MaxValue)
            .ThenBy(x => x.SourceIndex)
            .Select(x => x.Category)];
    }

    private static PackStartingTaskCategory NormalizeCategoryTasks(
        string packName,
        string label,
        PackStartingTaskCategory cat,
        List<PackValidationIssue> issues)
    {
        List<PackStartingTask> declaredTasks = cat.Tasks ?? [];
        List<string> legacyPrompts = cat.Prompts ?? [];

        if (declaredTasks.Count == 0 && legacyPrompts.Count == 0)
        {
            issues.Add(new PackValidationIssue(packName, label,
                $"starting-tasks category '{cat.Id}' has no tasks. Declare 'tasks:' (preferred) or the legacy 'prompts:' list.",
                "pack.starting-tasks.tasks-empty"));
            return cat;
        }

        var effective = new List<PackStartingTask>();
        if (declaredTasks.Count > 0)
        {
            // Structured tasks are the source of truth when declared — they
            // carry strictly more information (display name + capability)
            // than the legacy prompt-string list.
            for (int t = 0; t < declaredTasks.Count; t++)
            {
                PackStartingTask task = declaredTasks[t];
                string taskLabel = $"{label}.tasks[{t}]";

                if (string.IsNullOrWhiteSpace(task.Prompt))
                {
                    issues.Add(new PackValidationIssue(packName, taskLabel,
                        $"starting-tasks category '{cat.Id}' task[{t}] is missing 'prompt'.",
                        "pack.starting-tasks.task-prompt-missing"));
                    continue;
                }
                if (string.IsNullOrWhiteSpace(task.Name))
                {
                    issues.Add(new PackValidationIssue(packName, taskLabel,
                        $"starting-tasks category '{cat.Id}' task[{t}] is missing 'name'.",
                        "pack.starting-tasks.task-name-missing"));
                    continue;
                }

                if (task.Capability is not null)
                {
                    ValidateCapability(packName, taskLabel, cat.Id, t, task.Capability, issues);
                }

                effective.Add(task);
            }
        }
        else
        {
            // Legacy shape: each prompt string becomes a task where the
            // display name equals the submitted prompt. Preserves pre-#109
            // behavior verbatim for packs that have not been re-authored.
            for (int p = 0; p < legacyPrompts.Count; p++)
            {
                string prompt = legacyPrompts[p];
                if (string.IsNullOrWhiteSpace(prompt))
                {
                    issues.Add(new PackValidationIssue(packName, $"{label}.prompts[{p}]",
                        $"starting-tasks category '{cat.Id}' prompt[{p}] is empty.",
                        "pack.starting-tasks.legacy-prompt-empty"));
                    continue;
                }
                effective.Add(new PackStartingTask { Name = prompt, Prompt = prompt });
            }
        }

        if (effective.Count == 0)
        {
            issues.Add(new PackValidationIssue(packName, label,
                $"starting-tasks category '{cat.Id}' has no valid tasks after normalization.",
                "pack.starting-tasks.tasks-empty"));
        }

        List<PackStartingTask> sorted = [.. effective
            .Select((task, idx) => (task, idx))
            .OrderBy(x => x.task.Order ?? int.MaxValue)
            .ThenBy(x => x.idx)
            .Select(x => x.task)];

        return new PackStartingTaskCategory
        {
            Id = cat.Id,
            Label = cat.Label,
            Emoji = cat.Emoji,
            Order = cat.Order,
            Tasks = sorted,
            // The derived Prompts list keeps the legacy shape available so
            // clients still on the old contract keep working; the ordered
            // Tasks list is the primary surface.
            Prompts = [.. sorted.Select(t => t.Prompt)],
        };
    }

    private static readonly HashSet<string> _validCapabilityKinds = new(StringComparer.OrdinalIgnoreCase)
    {
        "prose",
        "chart",
        "plan",
    };

    private static void ValidateCapability(
        string packName,
        string taskLabel,
        string categoryId,
        int taskIndex,
        PackStartingTaskCapability capability,
        List<PackValidationIssue> issues)
    {
        if (string.IsNullOrWhiteSpace(capability.Kind))
        {
            issues.Add(new PackValidationIssue(packName, taskLabel,
                $"starting-tasks category '{categoryId}' task[{taskIndex}] capability is missing 'kind'.",
                "pack.starting-tasks.capability-kind-missing"));
            return;
        }
        if (!_validCapabilityKinds.Contains(capability.Kind))
        {
            issues.Add(new PackValidationIssue(packName, taskLabel,
                $"starting-tasks category '{categoryId}' task[{taskIndex}] capability kind '{capability.Kind}' is not one of prose|chart|plan.",
                "pack.starting-tasks.capability-kind-unknown"));
            return;
        }
        if (capability.Kind.Equals("chart", StringComparison.OrdinalIgnoreCase)
            && string.IsNullOrWhiteSpace(capability.ChartType))
        {
            issues.Add(new PackValidationIssue(packName, taskLabel,
                $"starting-tasks category '{categoryId}' task[{taskIndex}] capability kind='chart' requires a 'chartType'.",
                "pack.starting-tasks.capability-chart-type-missing"));
        }
        if (capability.Kind.Equals("plan", StringComparison.OrdinalIgnoreCase)
            && string.IsNullOrWhiteSpace(capability.PlanPath))
        {
            issues.Add(new PackValidationIssue(packName, taskLabel,
                $"starting-tasks category '{categoryId}' task[{taskIndex}] capability kind='plan' requires a 'planPath'.",
                "pack.starting-tasks.capability-plan-path-missing"));
        }
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

    /// <summary>
    /// Load and validate the pack's scenario seed manifest at
    /// <c>seed/scenario.yaml</c>. Every discoverable seed issue —
    /// missing file, YAML parse error, missing required section — is
    /// appended to <paramref name="issues"/> so seed problems arrive at
    /// the caller in the same aggregate as agents.yaml and pack.yaml
    /// problems.
    /// </summary>
    private static SeedManifest LoadSeedManifest(
        string packName,
        string packRoot,
        List<PackValidationIssue> issues)
    {
        string seedDir = Path.Combine(packRoot, "seed");
        string scenarioPath = Path.Combine(seedDir, SeedManifestLoader.ScenarioFileName);
        string section = $"seed/{SeedManifestLoader.ScenarioFileName}";

        if (!File.Exists(scenarioPath))
        {
            issues.Add(new PackValidationIssue(
                packName,
                section,
                $"Required section '{section}' is missing. Every pack must ship a machine-readable scenario seed manifest for MCP seeding.",
                "pack.section-missing"));
            return new SeedManifest();
        }

        try
        {
            return SeedManifestLoader.LoadFromDirectory(seedDir);
        }
        catch (SeedManifestLoadException ex)
        {
            string subSection = string.IsNullOrEmpty(ex.Section)
                ? section
                : $"{section}#{ex.Section}";
            string code = ex.Category switch
            {
                SeedManifestIssueCategory.ParseError => "pack.parse-error",
                SeedManifestIssueCategory.SectionMissing => "pack.section-missing",
                SeedManifestIssueCategory.Missing => throw new NotImplementedException(),
                _ => "pack.section-missing",
            };
            issues.Add(new PackValidationIssue(packName, subSection, ex.Message, code));
            return new SeedManifest();
        }
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
