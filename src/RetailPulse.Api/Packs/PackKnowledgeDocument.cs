namespace RetailPulse.Api.Packs;

/// <summary>
/// A single knowledge document supplied by a pack. The loader reads
/// every <c>*.md</c> under the pack's <c>knowledge/</c> directory and
/// materializes one <see cref="PackKnowledgeDocument"/> per file so the
/// existing <see cref="Rag.InMemoryKnowledgeBase"/> ingestion path can
/// treat pack docs the same as any other seeded corpus.
/// </summary>
/// <param name="Title">Display title derived from the document's first
/// H1 header when present, falling back to the filename. Downstream
/// wiring can override.</param>
/// <param name="Source">The provider-side <c>source</c> string used at
/// ingest time. Matches the file name (relative to the pack's
/// <c>knowledge/</c> folder) so named-source bindings in
/// <c>Knowledge:Sources:Named</c> refer to a stable identifier that
/// survives moves between providers.</param>
/// <param name="Content">Raw markdown content of the document.</param>
/// <param name="RelativePath">Path relative to the pack root, kept for
/// diagnostics so validation issues can name the exact file.</param>
public sealed record PackKnowledgeDocument(
    string Title,
    string Source,
    string Content,
    string RelativePath);
