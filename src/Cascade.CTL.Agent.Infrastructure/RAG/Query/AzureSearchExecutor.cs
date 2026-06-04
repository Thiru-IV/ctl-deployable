using Azure;
using Azure.Search.Documents;
using Azure.Search.Documents.Models;
using Cascade.CTL.Agent.Infrastructure.RAG;

namespace Cascade.CTL.Agent.Infrastructure.RAG.Query;

/// <summary>
/// Concrete implementation of <see cref="IAzureSearchExecutor"/> backed by <see cref="SearchClient"/>.
/// Performs hybrid search: BM25 on text fields combined with vector ANN on <c>contentVector</c>.
/// </summary>
internal sealed class AzureSearchExecutor : IAzureSearchExecutor
{
    private readonly SearchClient _searchClient;

    public AzureSearchExecutor(SearchClient searchClient)
    {
        _searchClient = searchClient ?? throw new ArgumentNullException(nameof(searchClient));
    }

    public async Task<IReadOnlyList<PolicySearchHit>> HybridSearchAsync(
        string queryText,
        ReadOnlyMemory<float> queryVector,
        string? oDataFilter,
        int topK,
        string? semanticConfiguration,
        CancellationToken cancellationToken)
    {
        var options = new SearchOptions
        {
            Size = topK,
            Filter = oDataFilter,
            QueryType = SearchQueryType.Simple,
            VectorSearch = new VectorSearchOptions()
        };

        options.VectorSearch.Queries.Add(new VectorizedQuery(queryVector)
        {
            KNearestNeighborsCount = topK,
            Fields = { "contentVector" }
        });

        // Enable L2 semantic reranking when a configuration name is supplied. Azure AI Search will
        // take the top hybrid candidates (up to 50) and re-order them with the Microsoft cross-encoder.
        if (!string.IsNullOrWhiteSpace(semanticConfiguration))
        {
            options.QueryType = SearchQueryType.Semantic;
            options.SemanticSearch = new SemanticSearchOptions
            {
                SemanticConfigurationName = semanticConfiguration,
            };
        }

        options.Select.Add("parentId");
        options.Select.Add("chunkIndex");
        options.Select.Add("title");
        options.Select.Add("content");
        options.Select.Add("state");
        options.Select.Add("county");
        options.Select.Add("assetType");
        options.Select.Add("policyType");

        Response<SearchResults<PolicyKnowledgeIndexDocument>> response =
            await _searchClient.SearchAsync<PolicyKnowledgeIndexDocument>(queryText, options, cancellationToken)
                .ConfigureAwait(false);

        var hits = new List<PolicySearchHit>(capacity: topK);
        await foreach (var result in response.Value.GetResultsAsync().WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            var doc = result.Document;
            hits.Add(new PolicySearchHit(
                Score: result.Score ?? 0.0,
                ParentId: doc.ParentId,
                ChunkIndex: doc.ChunkIndex,
                Title: doc.Title,
                Content: doc.Content,
                State: NullIfEmpty(doc.State),
                County: NullIfEmpty(doc.County),
                AssetType: NullIfEmpty(doc.AssetType),
                PolicyType: NullIfEmpty(doc.PolicyType),
                RerankerScore: result.SemanticSearch?.RerankerScore));
        }
        return hits;
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
}
