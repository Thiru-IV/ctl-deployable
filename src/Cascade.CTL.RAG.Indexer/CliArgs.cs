namespace Cascade.CTL.RAG.Indexer;

/// <summary>
/// Minimal CLI argument parser — supports <c>--flag</c> (boolean) and <c>--key value</c>.
/// Keeps the indexer free of heavy CLI parsing dependencies.
/// </summary>
internal sealed record CliArgs
{
    public string? KnowledgePath { get; init; }
    public string? ConfigFile { get; init; }
    public string? IndexName { get; init; }
    public string? Endpoint { get; init; }
    public string? AzureOpenAIEndpoint { get; init; }
    public string? EmbeddingDeployment { get; init; }
    public bool? UseAzureIdentity { get; init; }
    public string? AdminKey { get; init; }
    public bool RecreateIndex { get; init; }

    public static CliArgs Parse(string[] args)
    {
        var result = new CliArgs();

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            switch (arg)
            {
                case "--knowledge-path" when i + 1 < args.Length:
                    result = result with { KnowledgePath = args[++i] }; break;
                case "--config" when i + 1 < args.Length:
                    result = result with { ConfigFile = args[++i] }; break;
                case "--index-name" when i + 1 < args.Length:
                    result = result with { IndexName = args[++i] }; break;
                case "--endpoint" when i + 1 < args.Length:
                    result = result with { Endpoint = args[++i] }; break;
                case "--aoai-endpoint" when i + 1 < args.Length:
                    result = result with { AzureOpenAIEndpoint = args[++i] }; break;
                case "--embedding-model" when i + 1 < args.Length:
                    result = result with { EmbeddingDeployment = args[++i] }; break;
                case "--admin-key" when i + 1 < args.Length:
                    result = result with { AdminKey = args[++i] }; break;
                case "--use-azure-identity":
                    result = result with { UseAzureIdentity = true }; break;
                case "--use-key-auth":
                    result = result with { UseAzureIdentity = false }; break;
                case "--recreate-index":
                    result = result with { RecreateIndex = true }; break;
                case "--help" or "-h":
                    PrintHelp();
                    Environment.Exit(0);
                    break;
            }
        }
        return result;
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
            Cascade.CTL.RAG.Indexer — ingest policy JSONs into Azure AI Search

            Options:
              --knowledge-path <dir>       Directory containing *.json policy files (default: ./config/rag-knowledge)
              --config <file>              Path to appsettings.json override
              --endpoint <url>             Azure AI Search endpoint
              --index-name <name>          Index name (default: ctl-policy-knowledge)
              --aoai-endpoint <url>        Azure OpenAI endpoint
              --embedding-model <id>       Embedding deployment (default: text-embedding-3-small)
              --use-azure-identity         Use DefaultAzureCredential (default)
              --use-key-auth               Use admin key auth
              --admin-key <key>            Admin key for index management
              --recreate-index             Delete and recreate the index before upload
              --help | -h                  Show this help
            """);
    }
}
