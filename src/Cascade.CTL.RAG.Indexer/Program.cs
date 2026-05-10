using Cascade.CTL.Agent.Infrastructure.RAG;
using Cascade.CTL.Agent.Infrastructure.RAG.Indexing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Cascade.CTL.RAG.Indexer;

/// <summary>
/// Console app that ingests policy JSON files from disk into Azure AI Search.
/// </summary>
/// <remarks>
/// Usage:
///   dotnet run --project src/Cascade.CTL.RAG.Indexer -- \
///     --knowledge-path ./config/rag-knowledge \
///     --recreate-index
///
/// Configuration precedence (highest to lowest):
///   1. Command-line args (--knowledge-path, --index-name, --recreate-index)
///   2. Environment variables prefixed CTLRAG_ (e.g. CTLRAG__CTLAgent__RAG__AzureSearch__Endpoint)
///   3. appsettings.json (next to the executable)
///
/// Exit codes:
///   0 = success; non-zero = unhandled exception.
/// </remarks>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var parsed = CliArgs.Parse(args);

        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
            .AddJsonFile(parsed.ConfigFile ?? "appsettings.json", optional: true, reloadOnChange: false)
            .AddEnvironmentVariables(prefix: "CTLRAG_")
            .Build();

        using var loggerFactory = LoggerFactory.Create(b =>
        {
            b.AddSimpleConsole(o =>
            {
                o.SingleLine = true;
                o.TimestampFormat = "HH:mm:ss ";
            });
            b.SetMinimumLevel(LogLevel.Information);
        });
        var logger = loggerFactory.CreateLogger("Indexer");

        try
        {
            var options = new AzureSearchRAGOptions();
            configuration.GetSection(AzureSearchRAGOptions.SectionName).Bind(options);
            ApplyCliOverrides(options, parsed);
            ValidateOptions(options);

            logger.LogInformation("Indexer: Target index='{Index}' (endpoint={Endpoint}, model={Model}, dim={Dim})",
                options.IndexName, options.Endpoint, options.EmbeddingDeployment, options.EmbeddingDimensions);

            // Force admin key path when running the indexer (index creation requires admin rights).
            var indexClient = AzureSearchClientFactory.CreateIndexClient(options);
            var searchClient = AzureSearchClientFactory.CreateSearchClient(options);
            var embeddingClient = AzureSearchClientFactory.CreateEmbeddingClient(options);

            var pipeline = new RAGIndexPipeline(
                indexClient,
                searchClient,
                new AzureOpenAIEmbeddingGenerator(embeddingClient),
                options,
                loggerFactory.CreateLogger<RAGIndexPipeline>());

            var knowledgePath = parsed.KnowledgePath ?? configuration["CTLAgent:RAG:KnowledgePath"] ?? "./config/rag-knowledge";
            var report = await pipeline.RunAsync(
                knowledgeRootPath: Path.GetFullPath(knowledgePath),
                recreateIndex: parsed.RecreateIndex,
                chunkingOptions: new ChunkingOptions(),
                cancellationToken: CancellationToken.None);

            logger.LogInformation(
                "Indexer complete — docs={Docs} chunks={Chunks} uploaded={Uploaded} batches={Batches} recreated={Recreated} elapsed={Elapsed}",
                report.DocumentsRead, report.ChunksProduced, report.ChunksUploaded,
                report.BatchesUploaded, report.IndexRecreated, report.Elapsed);

            return 0;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Indexer failed: {Message}", ex.Message);
            return 1;
        }
    }

    private static void ApplyCliOverrides(AzureSearchRAGOptions options, CliArgs parsed)
    {
        if (!string.IsNullOrWhiteSpace(parsed.IndexName))
            options.IndexName = parsed.IndexName!;
        if (!string.IsNullOrWhiteSpace(parsed.Endpoint))
            options.Endpoint = parsed.Endpoint!;
        if (!string.IsNullOrWhiteSpace(parsed.AzureOpenAIEndpoint))
            options.AzureOpenAIEndpoint = parsed.AzureOpenAIEndpoint!;
        if (!string.IsNullOrWhiteSpace(parsed.EmbeddingDeployment))
            options.EmbeddingDeployment = parsed.EmbeddingDeployment!;
        if (parsed.UseAzureIdentity.HasValue)
            options.UseAzureIdentity = parsed.UseAzureIdentity.Value;
        if (!string.IsNullOrWhiteSpace(parsed.AdminKey))
            options.AdminKey = parsed.AdminKey;
    }

    private static void ValidateOptions(AzureSearchRAGOptions o)
    {
        if (string.IsNullOrWhiteSpace(o.Endpoint))
            throw new InvalidOperationException(
                "Azure AI Search endpoint is required. Set CTLAgent:RAG:AzureSearch:Endpoint, CTLRAG__CTLAgent__RAG__AzureSearch__Endpoint env var, or --endpoint.");
        if (string.IsNullOrWhiteSpace(o.AzureOpenAIEndpoint))
            throw new InvalidOperationException(
                "Azure OpenAI endpoint is required. Set CTLAgent:RAG:AzureSearch:AzureOpenAIEndpoint or --aoai-endpoint.");
        if (!o.UseAzureIdentity && string.IsNullOrWhiteSpace(o.AdminKey))
            throw new InvalidOperationException(
                "AdminKey is required when UseAzureIdentity=false.");
    }
}
