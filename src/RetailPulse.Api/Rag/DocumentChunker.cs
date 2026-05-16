namespace RetailPulse.Api.Rag;

/// <summary>
/// Splits documents into ~500-token chunks with 50-token overlap.
/// Preserves section headers for citation context.
/// </summary>
public static class DocumentChunker
{
    private const int _targetTokens = 500;
    private const int _overlapTokens = 50;

    public record DocumentChunk(string Text, int Index, string? SectionHeader);

    /// <summary>
    /// Chunk a document into overlapping segments.
    /// Simple tokenization: split on whitespace, normalize to lowercase for search.
    /// </summary>
    public static IReadOnlyList<DocumentChunk> Chunk(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return [];

        List<(string Text, string? Header)> paragraphs = SplitIntoParagraphs(content);
        List<(string Text, string? Header)> merged = MergeParagraphs(paragraphs);
        return CreateOverlappingChunks(merged);
    }

    internal static int CountTokens(string text) =>
        text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

    private static List<(string Text, string? Header)> SplitIntoParagraphs(string content)
    {
        string[] lines = content.Split('\n');
        var paragraphs = new List<(string Text, string? Header)>();
        string? currentHeader = null;
        var currentParagraph = new System.Text.StringBuilder();

        foreach (string line in lines)
        {
            string trimmed = line.TrimStart();

            // Detect markdown headers
            if (trimmed.StartsWith('#'))
            {
                // Flush current paragraph
                if (currentParagraph.Length > 0)
                {
                    paragraphs.Add((currentParagraph.ToString().Trim(), currentHeader));
                    currentParagraph.Clear();
                }
                currentHeader = trimmed.TrimStart('#').Trim();
                continue;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                if (currentParagraph.Length > 0)
                {
                    paragraphs.Add((currentParagraph.ToString().Trim(), currentHeader));
                    currentParagraph.Clear();
                }
            }
            else
            {
                if (currentParagraph.Length > 0)
                    currentParagraph.Append(' ');
                currentParagraph.Append(trimmed);
            }
        }

        if (currentParagraph.Length > 0)
            paragraphs.Add((currentParagraph.ToString().Trim(), currentHeader));

        return paragraphs;
    }

    private static List<(string Text, string? Header)> MergeParagraphs(List<(string Text, string? Header)> paragraphs)
    {
        var merged = new List<(string Text, string? Header)>();
        var currentText = new System.Text.StringBuilder();
        string? currentHeader = null;

        foreach ((string? text, string? header) in paragraphs)
        {
            int tokens = CountTokens(text);

            if (currentText.Length > 0 && CountTokens(currentText.ToString()) + tokens > _targetTokens)
            {
                merged.Add((currentText.ToString().Trim(), currentHeader));
                currentText.Clear();
                currentHeader = header;
            }

            if (currentText.Length == 0)
                currentHeader = header;

            if (currentText.Length > 0)
                currentText.Append(' ');
            currentText.Append(text);
        }

        if (currentText.Length > 0)
            merged.Add((currentText.ToString().Trim(), currentHeader));

        return merged;
    }

    private static IReadOnlyList<DocumentChunk> CreateOverlappingChunks(List<(string Text, string? Header)> merged)
    {
        var chunks = new List<DocumentChunk>();

        for (int i = 0; i < merged.Count; i++)
        {
            (string? text, string? header) = merged[i];
            string[] words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

            if (words.Length <= _targetTokens + _overlapTokens || i == merged.Count - 1)
            {
                // Add overlap from previous chunk
                string chunkText;
                if (i > 0 && chunks.Count > 0)
                {
                    string[] prevWords = merged[i - 1].Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                    string[] overlapWords = prevWords.Length > _overlapTokens
                        ? prevWords[^_overlapTokens..]
                        : prevWords;
                    chunkText = string.Join(' ', overlapWords) + " " + text;
                }
                else
                {
                    chunkText = text;
                }

                chunks.Add(new DocumentChunk(chunkText.Trim(), chunks.Count, header));
            }
            else
            {
                // Split large merged block into multiple chunks
                int pos = 0;
                while (pos < words.Length)
                {
                    int take = Math.Min(_targetTokens, words.Length - pos);
                    string[] chunkWords = words[pos..(pos + take)];

                    string chunkText;
                    if (pos > 0)
                    {
                        int overlapStart = Math.Max(0, pos - _overlapTokens);
                        string[] overlapWords = words[overlapStart..pos];
                        chunkText = string.Join(' ', overlapWords) + " " + string.Join(' ', chunkWords);
                    }
                    else if (i > 0 && chunks.Count > 0)
                    {
                        string[] prevWords = merged[i - 1].Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                        string[] overlapWords = prevWords.Length > _overlapTokens
                            ? prevWords[^_overlapTokens..]
                            : prevWords;
                        chunkText = string.Join(' ', overlapWords) + " " + string.Join(' ', chunkWords);
                    }
                    else
                    {
                        chunkText = string.Join(' ', chunkWords);
                    }

                    chunks.Add(new DocumentChunk(chunkText.Trim(), chunks.Count, header));
                    pos += take;
                }
            }
        }

        return chunks;
    }
}
