using System.Text.Json;
using Azure.Search.Documents;
using Azure.Search.Documents.Indexes;
using Cascade.CTL.Agent.Domain.Models;
using Cascade.CTL.Agent.Infrastructure.RAG;
using Microsoft.Extensions.Logging;

namespace Cascade.CTL.Agent.Infrastructure.RAG.Indexing;

/// <summary>
/// End-to-end ingestion pipeline: reads local policy JSON files, chunks them, generates embeddings,
/// (re)creates the Azure AI Search index if requested, and uploads chunk documents in batches.
/// </summary>
/// <remarks>
/// Intentionally lives in Infrastructure so both the dedicated indexer console and any future
/// scheduled job can reuse it. No <c>Azure.Search</c>-indexer / skillset is used — chunking and
/// embedding happen client-side, which keeps dependencies minimal on Azure Free tier (no Blob Storage,
/// no cognitive skillsets).
/// </remarks>
public sealed class RAGIndexPipeline
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private const int UploadBatchSize = 100;

    private readonly SearchIndexClient _indexClient;
    private readonly SearchClient _searchClient;
    private readonly IRAGEmbeddingGenerator _embeddings;
    private readonly AzureSearchRAGOptions _options;
    private readonly ILogger<RAGIndexPipeline> _logger;

    public RAGIndexPipeline(
        SearchIndexClient indexClient,
        SearchClient searchClient,
        IRAGEmbeddingGenerator embeddings,
        AzureSearchRAGOptions options,
        ILogger<RAGIndexPipeline> logger)
    {
        _indexClient = indexClient ?? throw new ArgumentNullException(nameof(indexClient));
        _searchClient = searchClient ?? throw new ArgumentNullException(nameof(searchClient));
        _embeddings = embeddings ?? throw new ArgumentNullException(nameof(embeddings));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public sealed record IndexingReport(
        int DocumentsRead,
        int ChunksProduced,
        int ChunksUploaded,
        int BatchesUploaded,
        TimeSpan Elapsed,
        bool IndexRecreated);

    public async Task<IndexingReport> RunAsync(
        string knowledgeRootPath,
        bool recreateIndex,
        ChunkingOptions? chunkingOptions = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(knowledgeRootPath))
            throw new ArgumentException("knowledgeRootPath is required.", nameof(knowledgeRootPath));
        if (!Directory.Exists(knowledgeRootPath))
            throw new DirectoryNotFoundException($"RAG knowledge path not found: {knowledgeRootPath}");

        var start = DateTimeOffset.UtcNow;

        var indexRecreated = await EnsureIndexAsync(recreateIndex, cancellationToken).ConfigureAwait(false);

        var documents = LoadDocuments(knowledgeRootPath);
        _logger.LogInformation("RAGIndexPipeline: Loaded {Count} source documents from {Path}", documents.Count, knowledgeRootPath);

        var chunks = documents.SelectMany(d => PolicyDocumentChunker.Chunk(d, chunkingOptions)).ToList();
        _logger.LogInformation("RAGIndexPipeline: Produced {Count} chunks (avg {Avg} chunks/doc)",
            chunks.Count, documents.Count == 0 ? 0 : chunks.Count / Math.Max(documents.Count, 1));

        var uploaded = 0;
        var batches = 0;

        foreach (var batch in Batch(chunks, UploadBatchSize))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var indexDocs = await BuildIndexDocumentsAsync(batch, cancellationToken).ConfigureAwait(false);

            var response = await _searchClient
                .MergeOrUploadDocumentsAsync(indexDocs, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            var succeeded = response.Value.Results.Count(r => r.Succeeded);
            var failed = response.Value.Results.Count - succeeded;
            uploaded += succeeded;
            batches++;

            _logger.LogInformation("RAGIndexPipeline: Batch {Batch} uploaded {Succeeded}/{Total} (failures: {Failed})",
                batches, succeeded, response.Value.Results.Count, failed);

            if (failed > 0)
            {
                foreach (var f in response.Value.Results.Where(r => !r.Succeeded))
                    _logger.LogWarning("  Failed key={Key} status={Status} error={Error}", f.Key, f.Status, f.ErrorMessage);
            }
        }

        var elapsed = DateTimeOffset.UtcNow - start;
        _logger.LogInformation("RAGIndexPipeline: Completed — {Docs} docs, {Chunks} chunks, {Uploaded} uploaded in {Elapsed}",
            documents.Count, chunks.Count, uploaded, elapsed);

        return new IndexingReport(documents.Count, chunks.Count, uploaded, batches, elapsed, indexRecreated);
    }

    private async Task<bool> EnsureIndexAsync(bool recreate, CancellationToken ct)
    {
        var indexName = _options.IndexName;
        var existing = await TryGetIndexAsync(indexName, ct).ConfigureAwait(false);

        if (existing is not null && !recreate)
        {
            _logger.LogInformation("RAGIndexPipeline: Reusing existing index '{Index}'", indexName);
            return false;
        }

        if (existing is not null && recreate)
        {
            _logger.LogWarning("RAGIndexPipeline: Deleting existing index '{Index}' before recreate", indexName);
            await _indexClient.DeleteIndexAsync(indexName, ct).ConfigureAwait(false);
        }

        var definition = SearchIndexSchema.BuildIndex(indexName, _options.EmbeddingDimensions);
        await _indexClient.CreateOrUpdateIndexAsync(definition, cancellationToken: ct).ConfigureAwait(false);
        _logger.LogInformation("RAGIndexPipeline: Created index '{Index}' with vector dim={Dim}", indexName, _options.EmbeddingDimensions);
        return true;
    }

    private async Task<object?> TryGetIndexAsync(string name, CancellationToken ct)
    {
        try
        {
            var resp = await _indexClient.GetIndexAsync(name, ct).ConfigureAwait(false);
            return resp.Value;
        }
        catch (Azure.RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    private async Task<List<PolicyKnowledgeIndexDocument>> BuildIndexDocumentsAsync(
        IReadOnlyList<PolicyChunk> chunks,
        CancellationToken ct)
    {
        var vectors = await _embeddings
            .EmbedBatchAsync(chunks.Select(c => c.Content).ToList(), ct)
            .ConfigureAwait(false);

        if (vectors.Count != chunks.Count)
            throw new InvalidOperationException(
                $"Embedding count mismatch: expected {chunks.Count}, received {vectors.Count}.");

        var docs = new List<PolicyKnowledgeIndexDocument>(chunks.Count);
        for (var i = 0; i < chunks.Count; i++)
        {
            var c = chunks[i];
            docs.Add(new PolicyKnowledgeIndexDocument
            {
                Id = SanitizeKey(c.ChunkId),
                ParentId = c.ParentId,
                ChunkIndex = c.ChunkIndex,
                Title = c.Title,
                Content = c.Content,
                ContentVector = vectors[i].ToArray(),
                State = c.State ?? string.Empty,
                County = c.County ?? string.Empty,
                AssetType = c.AssetType ?? string.Empty,
                PolicyType = c.PolicyType ?? string.Empty,
            });
        }
        return docs;
    }

    private List<RAGDocument> LoadDocuments(string path)
    {
        var docs = new List<RAGDocument>();
        foreach (var file in Directory.GetFiles(path, "*.json"))
        {
            try
            {
                var json = File.ReadAllText(file);
                var doc = JsonSerializer.Deserialize<RAGDocument>(json, JsonOptions);
                if (doc is null)
                {
                    _logger.LogWarning("RAGIndexPipeline: Skipped empty/null document {File}", file);
                    continue;
                }
                docs.Add(doc);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "RAGIndexPipeline: Failed to parse {File}", file);
            }
        }
        return docs;
    }

    /// <summary>
    /// Azure AI Search keys must be URL-safe. Replace characters not allowed in keys with '_'.
    /// </summary>
    internal static string SanitizeKey(string raw)
    {
        var chars = new char[raw.Length];
        for (var i = 0; i < raw.Length; i++)
        {
            var ch = raw[i];
            chars[i] = char.IsLetterOrDigit(ch) || ch is '_' or '-' or '=' ? ch : '_';
        }
        return new string(chars);
    }

    private static IEnumerable<IReadOnlyList<T>> Batch<T>(IReadOnlyList<T> source, int size)
    {
        for (var i = 0; i < source.Count; i += size)
            yield return source.Skip(i).Take(size).ToList();
    }
}
