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
}
