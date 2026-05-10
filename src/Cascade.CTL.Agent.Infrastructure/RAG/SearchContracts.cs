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
public sealed record PolicySearchHit(
    double Score,
    string ParentId,
    int ChunkIndex,
    string Title,
    string Content,
    string? State,
    string? County,
    string? AssetType,
    string? PolicyType);

/// <summary>
/// Executes a hybrid (vector + BM25) search against Azure AI Search. Abstracted so tests can mock it.
/// </summary>
public interface IAzureSearchExecutor
{
    Task<IReadOnlyList<PolicySearchHit>> HybridSearchAsync(
        string queryText,
        ReadOnlyMemory<float> queryVector,
        string? oDataFilter,
        int topK,
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
