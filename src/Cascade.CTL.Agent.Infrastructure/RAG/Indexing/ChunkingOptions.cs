namespace Cascade.CTL.Agent.Infrastructure.RAG.Indexing;

/// <summary>
/// Configuration for paragraph-level chunking.
/// </summary>
public sealed class ChunkingOptions
{
    /// <summary>Target max characters per chunk (~500 tokens at ~3 chars/token).</summary>
    public int MaxCharsPerChunk { get; set; } = 1500;

    /// <summary>Character overlap between consecutive chunks to preserve cross-paragraph context.</summary>
    public int OverlapChars { get; set; } = 150;

    /// <summary>Minimum content length (chars) before chunking kicks in; shorter docs produce a single chunk.</summary>
    public int MinCharsToChunk { get; set; } = 1200;
}
