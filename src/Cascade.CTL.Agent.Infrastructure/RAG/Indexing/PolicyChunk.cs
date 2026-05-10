namespace Cascade.CTL.Agent.Infrastructure.RAG.Indexing;

/// <summary>
/// A single chunk of a policy document after splitting.
/// Carries parent metadata so chunks remain individually filterable/queryable.
/// </summary>
public sealed record PolicyChunk
{
    public required string ChunkId { get; init; }
    public required string ParentId { get; init; }
    public required int ChunkIndex { get; init; }
    public required string Title { get; init; }
    public required string Content { get; init; }
    public string? State { get; init; }
    public string? County { get; init; }
    public string? AssetType { get; init; }
    public string? PolicyType { get; init; }
}
