# Without MCP — In-Process Tool Wiring Alternative

This shows the code required if the MCP server project were removed entirely, and tools were wired directly in-process using `AIFunctionFactory.Create()` from `Microsoft.Extensions.AI`.

---

## What Gets Deleted

- Entire `Cascade.CTL.Agent.McpServer` project (Program.cs, McpServerRegistration.cs, 5 tool classes)
- `McpToolProvider.cs` (MCP client connection logic)
- MCP NuGet packages (`ModelContextProtocol.Server`, `ModelContextProtocol.Client`)
- MCP config section in `appsettings.json` (`McpServer:Endpoint`, `McpServer:ApiKey`)

---

## What Replaces It

### 1. `InProcessToolProvider.cs` (replaces `McpToolProvider.cs`)

```csharp
using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cascade.CTL.Agent.Domain.Contracts;
using Microsoft.Extensions.AI;

namespace Cascade.CTL.Agent.Application.Orchestration;

public sealed class InProcessToolProvider : IMcpToolProvider
{
    private readonly ITitleSearchProvider _titleProvider;
    private readonly IHOAProvider _hoaProvider;
    private readonly ICodeViolationProvider _codeViolationProvider;
    private readonly IBPOProvider _bpoProvider;
    private readonly IAVMProvider _avmProvider;
    private readonly IOccupancyProvider _occupancyProvider;
    private readonly IAssetProfileProvider _assetProfileProvider;
    private readonly IRAGQueryService _ragService;

    private IReadOnlyList<AITool>? _allTools;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public InProcessToolProvider(
        ITitleSearchProvider titleProvider,
        IHOAProvider hoaProvider,
        ICodeViolationProvider codeViolationProvider,
        IBPOProvider bpoProvider,
        IAVMProvider avmProvider,
        IOccupancyProvider occupancyProvider,
        IAssetProfileProvider assetProfileProvider,
        IRAGQueryService ragService)
    {
        _titleProvider = titleProvider;
        _hoaProvider = hoaProvider;
        _codeViolationProvider = codeViolationProvider;
        _bpoProvider = bpoProvider;
        _avmProvider = avmProvider;
        _occupancyProvider = occupancyProvider;
        _assetProfileProvider = assetProfileProvider;
        _ragService = ragService;
    }

    public Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        _allTools = BuildTools();
        return Task.CompletedTask;
    }

    public IReadOnlyList<AITool> GetToolsForOrchestrator() =>
        _allTools!.Where(t => ToolFilters.IsOrchestratorTool(t.Name)).ToList();

    public IReadOnlyList<AITool> GetToolsForLegalAgent() =>
        _allTools!.Where(t => ToolFilters.IsLegalAgentTool(t.Name)).ToList();

    public IReadOnlyList<AITool> GetToolsForValuationAgent() =>
        _allTools!.Where(t => ToolFilters.IsValuationAgentTool(t.Name)).ToList();

    public IReadOnlyList<AITool> GetToolsForOccupancyAgent() =>
        _allTools!.Where(t => ToolFilters.IsOccupancyAgentTool(t.Name)).ToList();

    public IReadOnlyList<AITool> GetAllTools() => _allTools!;

    private IReadOnlyList<AITool> BuildTools()
    {
        return new List<AITool>
        {
            // --- Asset Profile ---
            AIFunctionFactory.Create(
                async ([Description("The unique asset identifier (e.g., ASSET-TX-001)")] string assetId) =>
                {
                    var asset = await _assetProfileProvider.GetAssetProfileAsync(assetId);
                    return JsonSerializer.Serialize(asset, JsonOptions);
                },
                "GetAssetProfile",
                "Retrieve the full asset profile for a given asset ID. Returns asset type, state, county, seller tier, occupancy status, parcel ID, and property address."),

            // --- Legal: Title Search ---
            AIFunctionFactory.Create(
                async (
                    [Description("County recorder parcel identifier (e.g., TX-DAL-123456)")] string parcelId,
                    [Description("Two-letter US state code (e.g., TX, CA, FL)")] string stateCode) =>
                {
                    var result = await _titleProvider.SearchAsync(parcelId, stateCode.ToUpperInvariant());
                    return JsonSerializer.Serialize(result, JsonOptions);
                },
                "SearchTitle",
                "Search for title defects, open liens, and encumbrances for a property by parcel ID and state code."),

            // --- Legal: HOA Delinquency ---
            AIFunctionFactory.Create(
                async ([Description("Full property address including city, state, and zip")] string propertyAddress) =>
                {
                    var result = await _hoaProvider.CheckDelinquencyAsync(propertyAddress);
                    return JsonSerializer.Serialize(result, JsonOptions);
                },
                "CheckHOADelinquency",
                "Check HOA delinquency status for a property address."),

            // --- Legal: Code Violations ---
            AIFunctionFactory.Create(
                async (
                    [Description("Full property address")] string propertyAddress,
                    [Description("County name (e.g., Dallas, Los Angeles, Miami-Dade)")] string county) =>
                {
                    var result = await _codeViolationProvider.LookupAsync(propertyAddress, county);
                    return JsonSerializer.Serialize(result, JsonOptions);
                },
                "LookupCodeViolations",
                "Look up open code violations for a property address in a specific county."),

            // --- Valuation: BPO ---
            AIFunctionFactory.Create(
                async ([Description("The unique asset identifier")] string assetId) =>
                {
                    var result = await _bpoProvider.RetrieveAsync(assetId);
                    return JsonSerializer.Serialize(result, JsonOptions);
                },
                "RetrieveBPO",
                "Retrieve the Broker Price Opinion (BPO) for an asset. Missing BPO is a CTL blocker."),

            // --- Valuation: AVM ---
            AIFunctionFactory.Create(
                async (
                    [Description("Full property address")] string propertyAddress,
                    [Description("Two-letter US state code")] string stateCode) =>
                {
                    var result = await _avmProvider.GetValuationAsync(propertyAddress, stateCode.ToUpperInvariant());
                    return JsonSerializer.Serialize(result, JsonOptions);
                },
                "GetAVM",
                "Get Automated Valuation Model estimate for a property."),

            // --- Occupancy ---
            AIFunctionFactory.Create(
                async ([Description("The unique asset identifier")] string assetId) =>
                {
                    var result = await _occupancyProvider.GetStatusAsync(assetId);
                    return JsonSerializer.Serialize(result, JsonOptions);
                },
                "GetOccupancyStatus",
                "Get occupancy and property condition status for an asset."),

            // --- RAG: Policy Knowledge Base ---
            AIFunctionFactory.Create(
                async (
                    [Description("Natural language query about CTL policy")] string query,
                    [Description("Optional domain filter: Legal, Valuation, Occupancy, or General")] string? domain) =>
                {
                    var result = await _ragService.QueryAsync(query, domain);
                    return JsonSerializer.Serialize(result, JsonOptions);
                },
                "QueryPolicyKnowledgeBaseViaRAG",
                "Query the policy knowledge base using RAG. Returns relevant policy excerpts with citations.")
        };
    }
}
```

### 2. DI Registration Change in `ServiceRegistration.cs`

```csharp
// BEFORE (with MCP):
services.AddSingleton<IMcpToolProvider>(sp =>
    new McpToolProvider(
        sp.GetRequiredService<ILogger<McpToolProvider>>(),
        new Dictionary<string, string> { ["Default"] = mcpEndpoint },
        resilienceOptions,
        apiKey));

// AFTER (without MCP):
services.AddSingleton<IMcpToolProvider, InProcessToolProvider>();
```

---

## What Stays the Same

- `IMcpToolProvider` interface — unchanged
- `ToolFilters.cs` — unchanged  
- `CTLWorkflowExecutors.cs` — unchanged (still calls `_toolProvider.GetToolsForLegalAgent()` etc.)
- `CTLWorkflowOrchestrator.cs` — unchanged
- All domain interfaces (`ITitleSearchProvider`, etc.) — unchanged
- All mock/real provider implementations — unchanged

---

## Comparison

| Aspect | With MCP (current) | Without MCP (above) |
|--------|-------------------|---------------------|
| Projects | 2 (McpServer + Application) | 1 (Application only) |
| Network hop | localhost:5100 HTTP per tool call | None — in-process method call |
| Latency | +1-5ms per tool call | Zero overhead |
| Process isolation | Yes (MCP server can crash independently) | No (tool bug crashes host) |
| External consumability | Any MCP client can connect | Only this app can use these tools |
| Input validation | In MCP tool classes (manual) | You'd add it inside the lambdas or a wrapper |
| Error handling | In MCP tool classes (try/catch → JSON error) | Same — needs equivalent try/catch |
| Schema generation | Automatic from `[McpServerTool]` + `[Description]` attributes | Automatic from `AIFunctionFactory.Create()` + `[Description]` attributes |
| Tool discovery | `ListToolsAsync()` over MCP protocol | Already in memory — `BuildTools()` |
| Startup | Must start MCP server first, then host | Single process — just start host |
| DLL lock issue | MCP server locks DLLs during build | Gone |
| Lines of code (tools) | ~250 (5 tool classes) | ~100 (one provider class with lambdas) |
| Lines of code (wiring) | ~120 (McpToolProvider + MCP registration) | ~5 (one DI line) |

---

## Verdict

For **this solution today** (single team, single codebase, mock providers), the in-process approach is simpler: fewer projects, no network hop, no DLL lock headache, fewer lines of code, same schema generation capability.

MCP earns its keep when the tools eventually become **real services owned by different teams**, or when **external consumers** (VS Code Copilot, other agents) need to use the same tools.
