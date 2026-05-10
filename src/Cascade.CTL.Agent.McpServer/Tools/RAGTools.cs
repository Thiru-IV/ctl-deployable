using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cascade.CTL.Agent.Domain.Contracts;
using ModelContextProtocol.Server;

namespace Cascade.CTL.Agent.McpServer.Tools;

[McpServerToolType]
public sealed class RAGTools
{
    private readonly IRAGQueryService _ragService;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public RAGTools(IRAGQueryService ragService)
    {
        _ragService = ragService;
    }

    [McpServerTool, Description("Query the CTL policy knowledge base via RAG (Retrieval-Augmented Generation) using Azure AI Search vector retrieval. Retrieves relevant policies, regulations, and requirements grounded in indexed documents. Filters by state, county, and asset type. Use this to ground decisions in documented policies rather than general knowledge.")]
    public async Task<string> QueryPolicyKnowledgeBaseViaRAG(
        [Description("Natural language search query describing what policy information you need")] string query,
        [Description("Two-letter US state code to filter policies (optional, e.g., TX, CA)")] string? stateCode = null,
        [Description("County name to filter policies (optional, e.g., Dallas, Los Angeles)")] string? county = null,
        [Description("Asset type to filter policies (optional: Foreclosure, REO, NonForeclosure, ShortSale)")] string? assetType = null)
    {
        if (string.IsNullOrWhiteSpace(query))
            return """{"error": "query is required"}""";
        if (query.Length > 2000)
            return """{"error": "query exceeds maximum length of 2000 characters"}""";

        try
        {
            var result = await _ragService.QueryAsync(query, stateCode, county, assetType);
            return JsonSerializer.Serialize(result, JsonOptions);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = "Knowledge base query failed", transient = IsTransient(ex), detail = ex.GetType().Name }, JsonOptions);
        }
    }

    private static bool IsTransient(Exception ex) => ex is
        HttpRequestException or TimeoutException or IOException or System.Net.Sockets.SocketException
        or TaskCanceledException
        || (ex.InnerException != null && IsTransient(ex.InnerException));
}
