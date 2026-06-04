using System.Text.Json.Serialization;

namespace Cascade.CTL.Agent.Infrastructure.RAG;

/// <summary>
/// Document shape stored in Azure AI Search. Property names match the field names in <see cref="SearchIndexSchema"/>.
/// </summary>
internal sealed class PolicyKnowledgeIndexDocument
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("parentId")]
    public string ParentId { get; set; } = string.Empty;

    [JsonPropertyName("chunkIndex")]
    public int ChunkIndex { get; set; }

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("content")]
    public string Content { get; set; } = string.Empty;

    [JsonPropertyName("contentVector")]
    public IReadOnlyList<float> ContentVector { get; set; } = Array.Empty<float>();

    [JsonPropertyName("state")]
    public string State { get; set; } = string.Empty;

    [JsonPropertyName("county")]
    public string County { get; set; } = string.Empty;

    [JsonPropertyName("assetType")]
    public string AssetType { get; set; } = string.Empty;

    [JsonPropertyName("policyType")]
    public string PolicyType { get; set; } = string.Empty;
}

/// <summary>
/// Thin result DTO returned by <see cref="IAzureSearchExecutor"/> — decouples <see cref="AzureSearchRAGService"/> from Azure SDK types
/// so the service can be unit-tested with a substituted executor.
/// </summary>
/// <param name="Score">L1 hybrid score (RRF-fused BM25 + vector). Always populated.</param>
/// <param name="RerankerScore">
/// L2 semantic reranker score (Azure AI Search cross-encoder, raw range ~0.0–4.0). Populated only
/// when the executor was invoked with semantic reranking enabled; <c>null</c> otherwise.
/// </param>
public sealed record PolicySearchHit(
    double Score,
    string ParentId,
    int ChunkIndex,
    string Title,
    string Content,
    string? State,
    string? County,
    string? AssetType,
    string? PolicyType,
    double? RerankerScore = null);

/// <summary>
/// Executes a hybrid (vector + BM25) search against Azure AI Search, optionally followed by the L2
/// semantic reranker. Abstracted so tests can mock it.
/// </summary>
public interface IAzureSearchExecutor
{
    /// <summary>
    /// Executes hybrid retrieval. When <paramref name="semanticConfiguration"/> is non-null, the
    /// implementation enables Azure AI Search semantic ranking and populates
    /// <see cref="PolicySearchHit.RerankerScore"/> on returned hits.
    /// </summary>
    Task<IReadOnlyList<PolicySearchHit>> HybridSearchAsync(
        string queryText,
        ReadOnlyMemory<float> queryVector,
        string? oDataFilter,
        int topK,
        string? semanticConfiguration,
        CancellationToken cancellationToken);
}

/// <summary>
/// Embedding abstraction decoupling tests from Azure OpenAI SDK types.
/// </summary>
public interface IRAGEmbeddingGenerator
{
    Task<ReadOnlyMemory<float>> EmbedAsync(string text, CancellationToken cancellationToken);

    Task<IReadOnlyList<ReadOnlyMemory<float>>> EmbedBatchAsync(
        IReadOnlyList<string> texts,
        CancellationToken cancellationToken);
}
