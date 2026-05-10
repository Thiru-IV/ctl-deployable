using Cascade.CTL.Agent.Application.Configuration;
using Cascade.CTL.Agent.Application.Orchestration;
using Cascade.CTL.Agent.Application.Resilience;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;

namespace Cascade.CTL.Agent.Tests.Providers;

public class McpToolProviderTests
{
    private readonly ILogger<McpToolProvider> _logger = Substitute.For<ILogger<McpToolProvider>>();

    // ─── Constructor: Multi-endpoint ──────────────────────────────────────

    [Fact]
    public void Constructor_WithMultipleEndpoints_ShouldAcceptDictionary()
    {
        var endpoints = new Dictionary<string, string>
        {
            ["Legal"] = "http://legal-server:5200",
            ["Valuation"] = "http://valuation-server:5201",
            ["Occupancy"] = "http://occupancy-server:5202"
        };

        var provider = new McpToolProvider(_logger, endpoints);

        provider.Should().NotBeNull();
        provider.Should().BeAssignableTo<IMcpToolProvider>();
    }

    [Fact]
    public void Constructor_WithSingleEndpoint_BackwardCompatible()
    {
        var provider = new McpToolProvider(_logger, "http://localhost:5100");

        provider.Should().NotBeNull();
        provider.Should().BeAssignableTo<IMcpToolProvider>();
    }

    [Fact]
    public void Constructor_WithEmptyDictionary_ShouldThrow()
    {
        var act = () => new McpToolProvider(_logger, new Dictionary<string, string>());

        act.Should().Throw<ArgumentException>()
            .WithMessage("*At least one MCP server endpoint*");
    }

    [Fact]
    public void Constructor_WithNullDictionary_ShouldThrow()
    {
        var act = () => new McpToolProvider(_logger, (Dictionary<string, string>)null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Constructor_WithResilienceOptions_ShouldAccept()
    {
        var endpoints = new Dictionary<string, string> { ["Default"] = "http://localhost:5100" };
        var resilience = new ResilienceOptions { McpInitTimeoutSeconds = 10 };

        var provider = new McpToolProvider(_logger, endpoints, resilience);

        provider.Should().NotBeNull();
    }

    // ─── EnsureInitialized guard ──────────────────────────────────────────

    [Fact]
    public void GetToolsForOrchestrator_BeforeInit_ShouldThrow()
    {
        var provider = new McpToolProvider(_logger, "http://localhost:5100");

        var act = () => provider.GetToolsForOrchestrator();

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*not been initialized*");
    }

    [Fact]
    public void GetToolsForLegalAgent_BeforeInit_ShouldThrow()
    {
        var provider = new McpToolProvider(_logger, "http://localhost:5100");

        var act = () => provider.GetToolsForLegalAgent();

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*not been initialized*");
    }

    [Fact]
    public void GetToolsForValuationAgent_BeforeInit_ShouldThrow()
    {
        var provider = new McpToolProvider(_logger, "http://localhost:5100");

        var act = () => provider.GetToolsForValuationAgent();

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*not been initialized*");
    }

    [Fact]
    public void GetToolsForOccupancyAgent_BeforeInit_ShouldThrow()
    {
        var provider = new McpToolProvider(_logger, "http://localhost:5100");

        var act = () => provider.GetToolsForOccupancyAgent();

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*not been initialized*");
    }

    [Fact]
    public void GetAllTools_BeforeInit_ShouldThrow()
    {
        var provider = new McpToolProvider(_logger, "http://localhost:5100");

        var act = () => provider.GetAllTools();

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*not been initialized*");
    }

    // ─── IAsyncDisposable ─────────────────────────────────────────────────

    [Fact]
    public void ShouldImplementIAsyncDisposable()
    {
        var provider = new McpToolProvider(_logger, "http://localhost:5100");

        provider.Should().BeAssignableTo<IAsyncDisposable>();
    }

    [Fact]
    public async Task DisposeAsync_BeforeInit_ShouldNotThrow()
    {
        var provider = new McpToolProvider(_logger, "http://localhost:5100");

        var act = async () => await provider.DisposeAsync();

        await act.Should().NotThrowAsync();
    }

    // ─── Config model ─────────────────────────────────────────────────────

    [Fact]
    public void CTLAgentOptions_ShouldHaveMcpServer()
    {
        var options = new CTLAgentOptions();

        options.McpServer.Should().NotBeNull();
        options.McpServer.Endpoint.Should().Be("http://localhost:5100");
    }

    // ─── Multi-endpoint deduplication logic ───────────────────────────────

    [Fact]
    public void Constructor_WithDuplicateEndpoints_ShouldAccept()
    {
        // In dev, all logical servers share the same endpoint — should be accepted
        var endpoints = new Dictionary<string, string>
        {
            ["Legal"] = "http://localhost:5100",
            ["Valuation"] = "http://localhost:5100",
            ["Occupancy"] = "http://localhost:5100",
            ["AssetProfile"] = "http://localhost:5100",
            ["KnowledgeBase"] = "http://localhost:5100"
        };

        var provider = new McpToolProvider(_logger, endpoints);

        provider.Should().NotBeNull();
    }

    [Fact]
    public void Constructor_WithMixedEndpoints_ShouldAccept()
    {
        // Production scenario: separate vendor servers
        var endpoints = new Dictionary<string, string>
        {
            ["Legal"] = "http://vendor-legal:5200",
            ["Valuation"] = "http://vendor-valuation:5201",
            ["Occupancy"] = "http://vendor-occupancy:5202",
            ["AssetProfile"] = "http://localhost:5100",
            ["KnowledgeBase"] = "http://localhost:5100"
        };

        var provider = new McpToolProvider(_logger, endpoints);

        provider.Should().NotBeNull();
    }
}
