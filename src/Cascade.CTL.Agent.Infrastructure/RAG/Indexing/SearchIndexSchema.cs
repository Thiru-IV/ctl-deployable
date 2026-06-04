using Azure.Search.Documents.Indexes;
using Azure.Search.Documents.Indexes.Models;

namespace Cascade.CTL.Agent.Infrastructure.RAG.Indexing;

/// <summary>
/// Factory for the <see cref="SearchIndex"/> definition used to store chunked CTL policy documents.
/// Fields are designed for:
/// <list type="bullet">
///   <item>Keyword (BM25) search on <c>title</c> and <c>content</c>.</item>
///   <item>Vector ANN search on <c>contentVector</c> using HNSW.</item>
///   <item>L2 semantic reranking via an attached semantic configuration that surfaces <c>title</c> as the title field and <c>content</c> as the body field.</item>
///   <item>Metadata pre-filtering on <c>state</c>, <c>county</c>, <c>assetType</c>, <c>policyType</c>, <c>parentId</c>.</item>
/// </list>
/// </summary>
public static class SearchIndexSchema
{
    public const string VectorSearchProfileName = "ctl-vector-profile";
    public const string HnswConfigurationName = "ctl-hnsw";
    public const string SemanticConfigurationName = "ctl-semantic-config";

    public static SearchIndex BuildIndex(string indexName, int vectorDimensions)
    {
        if (string.IsNullOrWhiteSpace(indexName))
            throw new ArgumentException("indexName is required.", nameof(indexName));
        if (vectorDimensions <= 0)
            throw new ArgumentOutOfRangeException(nameof(vectorDimensions));

        var fields = new List<SearchField>
        {
            new SimpleField("id", SearchFieldDataType.String) { IsKey = true, IsFilterable = true },
            new SimpleField("parentId", SearchFieldDataType.String) { IsFilterable = true, IsFacetable = false },
            new SimpleField("chunkIndex", SearchFieldDataType.Int32) { IsFilterable = true, IsSortable = true },
            new SearchableField("title") { IsFilterable = false, AnalyzerName = LexicalAnalyzerName.EnMicrosoft },
            new SearchableField("content") { IsFilterable = false, AnalyzerName = LexicalAnalyzerName.EnMicrosoft },
            new VectorSearchField("contentVector", vectorDimensions, VectorSearchProfileName),
            new SimpleField("state", SearchFieldDataType.String) { IsFilterable = true, IsFacetable = true },
            new SimpleField("county", SearchFieldDataType.String) { IsFilterable = true, IsFacetable = true },
            new SimpleField("assetType", SearchFieldDataType.String) { IsFilterable = true, IsFacetable = true },
            new SimpleField("policyType", SearchFieldDataType.String) { IsFilterable = true, IsFacetable = true },
        };

        var index = new SearchIndex(indexName)
        {
            VectorSearch = new VectorSearch
            {
                Algorithms =
                {
                    new HnswAlgorithmConfiguration(HnswConfigurationName)
                    {
                        Parameters = new HnswParameters
                        {
                            Metric = VectorSearchAlgorithmMetric.Cosine,
                            M = 4,
                            EfConstruction = 400,
                            EfSearch = 500,
                        }
                    }
                },
                Profiles =
                {
                    new VectorSearchProfile(VectorSearchProfileName, HnswConfigurationName)
                }
            },
            SemanticSearch = new SemanticSearch
            {
                Configurations =
                {
                    new SemanticConfiguration(
                        SemanticConfigurationName,
                        new SemanticPrioritizedFields
                        {
                            TitleField = new SemanticField("title"),
                            ContentFields = { new SemanticField("content") },
                        })
                }
            }
        };

        foreach (var field in fields)
            index.Fields.Add(field);

        return index;
    }
}
