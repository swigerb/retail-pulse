namespace RetailPulse.Api.Configuration;

/// <summary>
/// Configuration-backed catalog of named knowledge sources for per-agent
/// knowledge binding (issue #105). A named source maps a logical name (used by
/// <c>AgentDefinition.KnowledgeBaseName</c>) to the concrete document
/// <see cref="Contracts.Rag.SearchResult.Source"/> values that
/// belong to it. Multiple agents may reference the same name; unknown
/// references fail startup.
///
/// Bound to the <c>Knowledge:Sources</c> configuration section:
/// <code>
/// "Knowledge": {
///   "Sources": {
///     "Named": {
///       "planogram-shelf-set": {
///         "Documents": [ "apex-planogram-and-shelf-set.md" ]
///       },
///       "supplier-service-levels": {
///         "Documents": [ "apex-supplier-service-levels.md" ]
///       }
///     }
///   }
/// }
/// </code>
///
/// The registry never validates document presence in the underlying corpus —
/// that would make a freshly deployed cloud index unusable. Validation is
/// purely configuration-name based: an agent referencing an unregistered name
/// fails startup with a clear message.
/// </summary>
public sealed class KnowledgeSourcesOptions
{
    public const string SectionName = "Knowledge:Sources";

    /// <summary>
    /// Logical source name → definition. Case-insensitive keys so a YAML
    /// binding name is not silently mismatched by a capitalization drift.
    /// </summary>
    public Dictionary<string, KnowledgeSourceDefinition> Named { get; set; }
        = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// A single named source's document members. The <see cref="Documents"/>
/// values are compared verbatim against the <c>source</c> string persisted by
/// <see cref="Contracts.Rag.IKnowledgeBase.IngestDocumentAsync"/>
/// (typically the document's file name).
/// </summary>
public sealed class KnowledgeSourceDefinition
{
    public List<string> Documents { get; set; } = [];
}
