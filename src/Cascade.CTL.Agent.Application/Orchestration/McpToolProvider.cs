using Cascade.CTL.Agent.Application.Resilience;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using Polly;

namespace Cascade.CTL.Agent.Application.Orchestration;

/// <summary>
/// Connects to one or more MCP servers and aggregates tools for agent consumption.
/// Deduplicates connections when multiple logical servers share the same endpoint
/// (e.g., development mode where a single monolithic MCP server hosts all tools).
/// </summary>
public sealed class McpToolProvider : IMcpToolProvider, IAsyncDisposable
{
    private readonly ILogger<McpToolProvider> _logger;
    private readonly Dictionary<string, string> _serverEndpoints; // logical name → endpoint URL
    private readonly ResilienceOptions _resilienceOptions;
    private readonly string? _apiKey;
    private readonly Dictionary<string, McpClient> _connectedClients = new(); // endpoint URL → client (deduplicated)
    private IList<McpClientTool>? _tools;

    /// <summary>
    /// Creates a multi-endpoint MCP tool provider.
    /// Each entry maps a logical server name (e.g., "Legal", "Valuation") to its endpoint URL.
    /// </summary>
    public McpToolProvider(ILogger<McpToolProvider> logger, Dictionary<string, string> serverEndpoints, ResilienceOptions? resilienceOptions = null, string? apiKey = null)
    {
        _logger = logger;
        _serverEndpoints = serverEndpoints ?? throw new ArgumentNullException(nameof(serverEndpoints));
        _resilienceOptions = resilienceOptions ?? new ResilienceOptions();
        _apiKey = apiKey;

        if (_serverEndpoints.Count == 0)
            throw new ArgumentException("At least one MCP server endpoint must be configured.", nameof(serverEndpoints));
    }

    /// <summary>
    /// Backward-compatible constructor for single-endpoint scenarios.
    /// </summary>
    public McpToolProvider(ILogger<McpToolProvider> logger, string mcpServerEndpoint, ResilienceOptions? resilienceOptions = null)
        : this(logger, new Dictionary<string, string> { ["Default"] = mcpServerEndpoint }, resilienceOptions)
    {
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var allTools = new List<McpClientTool>();
        var uniqueEndpoints = _serverEndpoints
            .GroupBy(kv => kv.Value, StringComparer.OrdinalIgnoreCase)
            .ToList();

        _logger.LogInformation("Initializing MCP connections to {ServerCount} logical server(s) across {EndpointCount} unique endpoint(s)",
            _serverEndpoints.Count, uniqueEndpoints.Count);

        foreach (var endpointGroup in uniqueEndpoints)
        {
            var endpoint = endpointGroup.Key;
            var serverNames = endpointGroup.Select(kv => kv.Key).ToList();

            _logger.LogInformation("Connecting to MCP endpoint {Endpoint} (serves: {Servers})",
                endpoint, string.Join(", ", serverNames));

            var client = await ConnectToMCPServerAsync(endpoint, serverNames, cancellationToken);
            _connectedClients[endpoint] = client;

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(_resilienceOptions.McpInitTimeoutSeconds));

            var tools = await client.ListToolsAsync(cancellationToken: timeoutCts.Token);
            allTools.AddRange(tools);

            _logger.LogInformation("MCP endpoint {Endpoint} provides {ToolCount} tools: {ToolNames}",
                endpoint, tools.Count, string.Join(", ", tools.Select(t => t.Name)));
        }

        _tools = allTools;

        _logger.LogInformation("MCP initialization complete. {TotalTools} tools available across {Endpoints} endpoint(s)",
            _tools.Count, _connectedClients.Count);
    }

    private async Task<McpClient> ConnectToMCPServerAsync(string endpoint, IReadOnlyList<string> serverNames, CancellationToken cancellationToken)
    {
        var pipeline = ResiliencePipelineFactory.CreateMcpInitPipeline(_resilienceOptions, _logger);

        return await pipeline.ExecuteAsync(async ct =>
        {
            _logger.LogInformation(
                "Connecting to MCP server at {Endpoint} (serves: {Servers})",
                endpoint, string.Join(", ", serverNames));

            var transportOptions = new HttpClientTransportOptions
            {
                Endpoint = new Uri(endpoint),
                TransportMode = HttpTransportMode.StreamableHttp,
                Name = $"CTLAgent-{string.Join("-", serverNames)}"
            };

            if (!string.IsNullOrEmpty(_apiKey))
            {
                transportOptions.AdditionalHeaders = new Dictionary<string, string>
                {
                    ["X-Api-Key"] = _apiKey
                };
            }

            var transport = new HttpClientTransport(transportOptions);

            return await McpClient.CreateAsync(transport, cancellationToken: ct);
        }, cancellationToken);
    }

    /// <summary>
    /// Returns the tools available to the Planning/Reflection orchestrator agent.
    /// </summary>
    /// <remarks>
    /// <c>GetAssetProfile</c> is intentionally excluded. The orchestrator pre-fetches the asset
    /// profile via <c>IAssetProfileProvider</c> (HTTP or mock) and injects the full JSON into the
    /// planning and reflection prompts. Re-exposing the same data as an agent tool would create
    /// redundant tool-call round trips, inflate token usage, and risk the LLM skipping the pre-fetched
    /// grounding in favor of a tool call (see Agentic_AI_Threat_Catalog.md, T-LLM-TOOL-SKIP).
    /// </remarks>
    public IReadOnlyList<AITool> GetToolsForOrchestrator()
    {
        EnsureInitialized();
        return _tools!
            .Where(t => ToolFilters.IsOrchestratorTool(t.Name))
            .Cast<AITool>()
            .ToList();
    }

    public IReadOnlyList<AITool> GetToolsForLegalAgent()
    {
        EnsureInitialized();
        return _tools!
            .Where(t => ToolFilters.IsLegalAgentTool(t.Name))
            .Cast<AITool>()
            .ToList();
    }

    public IReadOnlyList<AITool> GetToolsForValuationAgent()
    {
        EnsureInitialized();
        return _tools!
            .Where(t => ToolFilters.IsValuationAgentTool(t.Name))
            .Cast<AITool>()
            .ToList();
    }

    public IReadOnlyList<AITool> GetToolsForOccupancyAgent()
    {
        EnsureInitialized();
        return _tools!
            .Where(t => ToolFilters.IsOccupancyAgentTool(t.Name))
            .Cast<AITool>()
            .ToList();
    }

    public IReadOnlyList<AITool> GetAllTools()
    {
        EnsureInitialized();
        return _tools!.Cast<AITool>().ToList();
    }

    private void EnsureInitialized()
    {
        if (_tools == null || _connectedClients.Count == 0)
            throw new InvalidOperationException("McpToolProvider has not been initialized. Call InitializeAsync() first.");
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var client in _connectedClients.Values)
        {
            await client.DisposeAsync();
        }
        _connectedClients.Clear();
    }
}
