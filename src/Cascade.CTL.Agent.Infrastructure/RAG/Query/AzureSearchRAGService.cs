using Cascade.CTL.Agent.Domain.Contracts;
using Cascade.CTL.Agent.Domain.Models;
using Cascade.CTL.Agent.Infrastructure.RAG;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cascade.CTL.Agent.Infrastructure.RAG.Query;

/// <summary>
/// Azure AI Search-backed implementation of <see cref="IRAGQueryService"/>.
/// Performs hybrid retrieval (vector ANN + BM25 keyword) with metadata pre-filtering on state / county / assetType.
/// </summary>
/// <remarks>
/// <para>
/// The service is decoupled from the Azure SDK via <see cref="IAzureSearchExecutor"/> and
/// <see cref="IRAGEmbeddingGenerator"/> so it can be unit-tested without real Azure resources.
/// </para>
/// <para>
/// Filter semantics match <see cref="InMemoryRAGService"/>: a value of <c>ALL</c> stored on the document
/// is treated as matching any incoming filter value (e.g. a national policy flagged <c>state = ALL</c>
/// is returned regardless of the caller's <c>stateCode</c>).
/// </para>
/// </remarks>
public sealed class AzureSearchRAGService : IRAGQueryService
{
    private readonly IAzureSearchExecutor _executor;
    private readonly IRAGEmbeddingGenerator _embeddings;
    private readonly AzureSearchRAGOptions _options;
    private readonly ILogger<AzureSearchRAGService> _logger;

    internal AzureSearchRAGService(
        IAzureSearchExecutor executor,
        IRAGEmbeddingGenerator embeddings,
        IOptions<AzureSearchRAGOptions> options,
        ILogger<AzureSearchRAGService> logger)
    {
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
        _embeddings = embeddings ?? throw new ArgumentNullException(nameof(embeddings));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<RAGQueryResult> QueryAsync(
        string query,
        string? stateCode = null,
        string? county = null,
        string? assetType = null,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "AzureSearchRAG: Querying '{Query}' [State={State}, County={County}, AssetType={AssetType}]",
            query, stateCode, county, assetType);

        var queryVector = await _embeddings.EmbedAsync(query, cancellationToken).ConfigureAwait(false);
        var filter = BuildODataFilter(stateCode, county, assetType);

        var topK = _options.TopK > 0 ? _options.TopK : 5;

        var hits = await _executor
            .HybridSearchAsync(query, queryVector, filter, topK, cancellationToken)
            .ConfigureAwait(false);

        var documents = hits.Select(ToRagDocument).ToArray();

        _logger.LogInformation("AzureSearchRAG: Found {Count} matching chunks", documents.Length);

        return new RAGQueryResult
        {
            Query = query,
            Documents = documents,
            TotalMatches = documents.Length,
        };
    }

    /// <summary>
    /// Builds an OData <c>$filter</c> enforcing the "ALL" tolerance semantics for state/county/assetType.
    /// Returns null when no filters apply.
    /// </summary>
    internal static string? BuildODataFilter(string? stateCode, string? county, string? assetType)
    {
        var clauses = new List<string>(capacity: 3);
        if (!string.IsNullOrWhiteSpace(stateCode))
            clauses.Add($"(state eq '{Escape(stateCode)}' or state eq 'ALL' or state eq '')");
        if (!string.IsNullOrWhiteSpace(county))
            clauses.Add($"(county eq '{Escape(county)}' or county eq 'ALL' or county eq '')");
        if (!string.IsNullOrWhiteSpace(assetType))
            clauses.Add($"(assetType eq '{Escape(assetType)}' or assetType eq 'ALL' or assetType eq '')");

        return clauses.Count == 0 ? null : string.Join(" and ", clauses);
    }

    private static string Escape(string value) => value.Replace("'", "''");

    private static RAGDocument ToRagDocument(PolicySearchHit hit) => new()
    {
        Id = $"{hit.ParentId}#c{hit.ChunkIndex}",
        Title = hit.Title,
        Content = hit.Content,
        RelevanceScore = Math.Clamp(hit.Score, 0.0, 1.0),
        State = hit.State,
        County = hit.County,
        AssetType = hit.AssetType,
        PolicyType = hit.PolicyType,
    };
}
