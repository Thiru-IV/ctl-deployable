namespace Cascade.CTL.Agent.Infrastructure.RAG;

/// <summary>
/// Configuration for Azure AI Search-backed RAG service.
/// Bound from configuration section <c>CTLAgent:RAG:AzureSearch</c>.
/// </summary>
public sealed class AzureSearchRAGOptions
{
    public const string SectionName = "CTLAgent:RAG:AzureSearch";

    /// <summary>When true and <see cref="Endpoint"/> configured, production Azure AI Search service is used; otherwise in-memory fallback.</summary>
    public bool Enabled { get; set; } = false;

    /// <summary>Azure AI Search endpoint, e.g. <c>https://my-search.search.windows.net</c>.</summary>
    public string Endpoint { get; set; } = string.Empty;

    /// <summary>Index name for chunked policy knowledge.</summary>
    public string IndexName { get; set; } = "ctl-policy-knowledge";

    /// <summary>When true, authenticate via <c>DefaultAzureCredential</c> (Managed Identity / developer login). When false, <see cref="AdminKey"/>/<see cref="QueryKey"/> used.</summary>
    public bool UseAzureIdentity { get; set; } = true;

    /// <summary>Admin key for index management (used by indexer console only). Leave empty when <see cref="UseAzureIdentity"/> = true.</summary>
    public string? AdminKey { get; set; }

    /// <summary>Query key for runtime reads (used by the agent). Leave empty when <see cref="UseAzureIdentity"/> = true.</summary>
    public string? QueryKey { get; set; }

    /// <summary>Max documents returned from hybrid search.</summary>
    public int TopK { get; set; } = 5;

    /// <summary>Azure OpenAI endpoint hosting the embedding deployment (may be same as chat endpoint).</summary>
    public string AzureOpenAIEndpoint { get; set; } = string.Empty;

    /// <summary>Embedding model deployment name (e.g. <c>text-embedding-3-small</c>).</summary>
    public string EmbeddingDeployment { get; set; } = "text-embedding-3-small";

    /// <summary>Embedding vector dimensions. 1536 for <c>text-embedding-3-small</c>, 3072 for <c>text-embedding-3-large</c>.</summary>
    public int EmbeddingDimensions { get; set; } = 1536;

    /// <summary>Optional API key for Azure OpenAI (leave empty to use <c>DefaultAzureCredential</c>).</summary>
    public string? AzureOpenAIApiKey { get; set; }

    /// <summary>
    /// When true, queries enable Azure AI Search <em>semantic ranker</em> (L2 cross-encoder rerank) on top
    /// of the hybrid (BM25 + vector) result set. Requires the index to declare a semantic configuration
    /// (see <see cref="SemanticConfigurationName"/>) and the search service to be on a tier that supports
    /// semantic ranking (Basic and above; free tier excluded).
    /// </summary>
    public bool SemanticRankerEnabled { get; set; } = true;

    /// <summary>
    /// Semantic configuration name attached to the index. The configuration declares which fields the
    /// reranker should treat as title vs. content. Default keeps configuration co-located with the index.
    /// </summary>
    public string SemanticConfigurationName { get; set; } = "ctl-semantic-config";

    /// <summary>
    /// Number of candidates passed from L1 hybrid retrieval into the L2 semantic reranker. Larger values
    /// give the reranker more candidates to re-order at the cost of latency. Azure caps this at 50.
    /// </summary>
    public int RerankCandidateCount { get; set; } = 25;
}
