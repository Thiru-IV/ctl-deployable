using Azure;
using Azure.AI.OpenAI;
using Azure.Identity;
using Azure.Search.Documents;
using Azure.Search.Documents.Indexes;
using OpenAI.Embeddings;

namespace Cascade.CTL.Agent.Infrastructure.RAG;

/// <summary>
/// Centralises construction of Azure SDK clients from <see cref="AzureSearchRAGOptions"/>.
/// Used by both the runtime query service and the indexer console.
/// </summary>
public static class AzureSearchClientFactory
{
    public static SearchIndexClient CreateIndexClient(AzureSearchRAGOptions options)
    {
        ValidateEndpoint(options.Endpoint, nameof(options.Endpoint));
        var endpoint = new Uri(options.Endpoint);

        if (options.UseAzureIdentity)
            return new SearchIndexClient(endpoint, new DefaultAzureCredential());

        if (string.IsNullOrWhiteSpace(options.AdminKey))
            throw new InvalidOperationException(
                "AzureSearchRAG: AdminKey is required for index management when UseAzureIdentity=false.");

        return new SearchIndexClient(endpoint, new AzureKeyCredential(options.AdminKey));
    }

    public static SearchClient CreateSearchClient(AzureSearchRAGOptions options)
    {
        ValidateEndpoint(options.Endpoint, nameof(options.Endpoint));
        var endpoint = new Uri(options.Endpoint);

        if (options.UseAzureIdentity)
            return new SearchClient(endpoint, options.IndexName, new DefaultAzureCredential());

        // Runtime reads prefer QueryKey (minimum privilege). Fall back to AdminKey if QueryKey missing (dev convenience).
        var key = !string.IsNullOrWhiteSpace(options.QueryKey) ? options.QueryKey : options.AdminKey;
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException(
                "AzureSearchRAG: QueryKey (or AdminKey) is required when UseAzureIdentity=false.");

        return new SearchClient(endpoint, options.IndexName, new AzureKeyCredential(key));
    }

    public static EmbeddingClient CreateEmbeddingClient(AzureSearchRAGOptions options)
    {
        ValidateEndpoint(options.AzureOpenAIEndpoint, nameof(options.AzureOpenAIEndpoint));
        if (string.IsNullOrWhiteSpace(options.EmbeddingDeployment))
            throw new InvalidOperationException("AzureSearchRAG: EmbeddingDeployment is required.");

        var aoaiEndpoint = new Uri(options.AzureOpenAIEndpoint);

        AzureOpenAIClient aoai = !string.IsNullOrWhiteSpace(options.AzureOpenAIApiKey)
            ? new AzureOpenAIClient(aoaiEndpoint, new AzureKeyCredential(options.AzureOpenAIApiKey))
            : new AzureOpenAIClient(aoaiEndpoint, new DefaultAzureCredential());

        return aoai.GetEmbeddingClient(options.EmbeddingDeployment);
    }

    private static void ValidateEndpoint(string endpoint, string paramName)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
            throw new InvalidOperationException($"AzureSearchRAG: {paramName} is required.");
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out _))
            throw new InvalidOperationException($"AzureSearchRAG: {paramName} ('{endpoint}') is not a valid absolute URI.");
    }
}
