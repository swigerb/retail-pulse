using RetailPulse.Api.Models;
using RetailPulse.Contracts;

namespace RetailPulse.Api.Packs;

/// <summary>
/// Result produced by <see cref="PackLoader"/>. Owns every artifact a
/// pack contributes to the runtime: tenant configuration, agent
/// definitions, knowledge documents, and starting-task categories. A
/// downstream host (composition root) wires each section into the
/// existing subsystems (<see cref="Rag.InMemoryKnowledgeBase"/>, the
/// agent composition graph, the web PromptLibrary endpoint) so the
/// pack is the single source of truth for a scenario.
/// </summary>
public sealed class LoadedPack
{
    /// <summary>Pack directory name — matches <see cref="PackMetadata.Key"/>.</summary>
    public string Name { get; }

    /// <summary>Absolute path of the pack directory on disk.</summary>
    public string RootPath { get; }

    public PackMetadata Metadata { get; }
    public TenantConfiguration Tenant { get; }
    public PromptConfiguration Agents { get; }
    public IReadOnlyList<PackKnowledgeDocument> KnowledgeDocuments { get; }
    public IReadOnlyList<PackStartingTaskCategory> StartingTasks { get; }

    public LoadedPack(
        string name,
        string rootPath,
        PackMetadata metadata,
        TenantConfiguration tenant,
        PromptConfiguration agents,
        IReadOnlyList<PackKnowledgeDocument> knowledgeDocuments,
        IReadOnlyList<PackStartingTaskCategory> startingTasks)
    {
        Name = name;
        RootPath = rootPath;
        Metadata = metadata;
        Tenant = tenant;
        Agents = agents;
        KnowledgeDocuments = knowledgeDocuments;
        StartingTasks = startingTasks;
    }
}
