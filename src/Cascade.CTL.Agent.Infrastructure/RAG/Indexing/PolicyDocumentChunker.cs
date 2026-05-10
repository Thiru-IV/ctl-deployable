using Cascade.CTL.Agent.Domain.Models;

namespace Cascade.CTL.Agent.Infrastructure.RAG.Indexing;

/// <summary>
/// Deterministic paragraph-aware chunker. No embeddings, no LLM calls — pure string logic so it is trivial to unit test.
/// </summary>
/// <remarks>
/// Strategy:
/// 1. Split content on blank lines (paragraph boundary).
/// 2. Pack paragraphs greedily until <see cref="ChunkingOptions.MaxCharsPerChunk"/> is reached.
/// 3. When a single paragraph exceeds the cap, split on sentence boundaries (period/exclamation/question followed by space).
/// 4. When sentences still exceed the cap, hard-split at character boundary.
/// 5. Overlap: the last <see cref="ChunkingOptions.OverlapChars"/> characters of each chunk are prepended to the next,
///    preserving cross-boundary semantic context for retrieval.
/// 6. If the document is under <see cref="ChunkingOptions.MinCharsToChunk"/>, a single chunk is returned (common for small policies).
/// </remarks>
public static class PolicyDocumentChunker
{
    public static IReadOnlyList<PolicyChunk> Chunk(RAGDocument document, ChunkingOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        options ??= new ChunkingOptions();

        if (options.MaxCharsPerChunk <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "MaxCharsPerChunk must be positive.");
        if (options.OverlapChars < 0 || options.OverlapChars >= options.MaxCharsPerChunk)
            throw new ArgumentOutOfRangeException(nameof(options), "OverlapChars must be in [0, MaxCharsPerChunk).");

        var content = document.Content ?? string.Empty;

        if (content.Length <= options.MinCharsToChunk)
        {
            return
            [
                new PolicyChunk
                {
                    ChunkId = $"{document.Id}__c000",
                    ParentId = document.Id,
                    ChunkIndex = 0,
                    Title = document.Title,
                    Content = content,
                    State = document.State,
                    County = document.County,
                    AssetType = document.AssetType,
                    PolicyType = document.PolicyType,
                }
            ];
        }

        var segments = SplitToSegments(content, options.MaxCharsPerChunk);
        var chunks = Pack(segments, options.MaxCharsPerChunk, options.OverlapChars);

        var result = new List<PolicyChunk>(chunks.Count);
        for (var i = 0; i < chunks.Count; i++)
        {
            result.Add(new PolicyChunk
            {
                ChunkId = $"{document.Id}__c{i:D3}",
                ParentId = document.Id,
                ChunkIndex = i,
                Title = document.Title,
                Content = chunks[i],
                State = document.State,
                County = document.County,
                AssetType = document.AssetType,
                PolicyType = document.PolicyType,
            });
        }
        return result;
    }

    private static List<string> SplitToSegments(string content, int maxChars)
    {
        // Paragraphs first, then oversize paragraphs → sentences, then oversize sentences → hard split.
        var paragraphs = content
            .Split(["\r\n\r\n", "\n\n"], StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Trim())
            .Where(p => p.Length > 0)
            .ToList();

        // Fallback: no blank-line paragraphs present — split on single newlines.
        if (paragraphs.Count <= 1)
        {
            paragraphs = content
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(p => p.Trim())
                .Where(p => p.Length > 0)
                .ToList();
        }

        var segments = new List<string>();
        foreach (var para in paragraphs)
        {
            if (para.Length <= maxChars)
            {
                segments.Add(para);
                continue;
            }

            foreach (var sentence in SplitOnSentences(para))
            {
                if (sentence.Length <= maxChars)
                {
                    segments.Add(sentence);
                }
                else
                {
                    for (var i = 0; i < sentence.Length; i += maxChars)
                    {
                        segments.Add(sentence.Substring(i, Math.Min(maxChars, sentence.Length - i)));
                    }
                }
            }
        }
        return segments;
    }

    private static IEnumerable<string> SplitOnSentences(string paragraph)
    {
        var current = new System.Text.StringBuilder();
        for (var i = 0; i < paragraph.Length; i++)
        {
            current.Append(paragraph[i]);
            if (i + 1 < paragraph.Length && (paragraph[i] is '.' or '!' or '?') && paragraph[i + 1] == ' ')
            {
                yield return current.ToString().Trim();
                current.Clear();
            }
        }
        if (current.Length > 0)
            yield return current.ToString().Trim();
    }

    private static List<string> Pack(List<string> segments, int maxChars, int overlapChars)
    {
        var chunks = new List<string>();
        var buffer = new System.Text.StringBuilder();

        foreach (var seg in segments)
        {
            var separator = buffer.Length == 0 ? string.Empty : "\n\n";
            if (buffer.Length + separator.Length + seg.Length > maxChars && buffer.Length > 0)
            {
                var finalized = buffer.ToString();
                chunks.Add(finalized);

                // Seed next buffer with overlap tail for context preservation.
                buffer.Clear();
                if (overlapChars > 0 && finalized.Length > overlapChars)
                {
                    buffer.Append(finalized.AsSpan(finalized.Length - overlapChars));
                    buffer.Append("\n\n");
                }
            }
            buffer.Append(separator).Append(seg);
        }
        if (buffer.Length > 0)
            chunks.Add(buffer.ToString());

        return chunks;
    }
}
