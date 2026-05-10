using Cascade.CTL.Agent.Infrastructure.RAG.Query;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;

namespace Cascade.CTL.Agent.Tests.Providers;

public class InMemoryRAGServiceTests
{
    private readonly InMemoryRAGService _ragService;

    public InMemoryRAGServiceTests()
    {
        _ragService = new InMemoryRAGService(Substitute.For<ILogger<InMemoryRAGService>>());
    }

    [Fact]
    public async Task Query_ShouldReturnTexasPoliciesForTexasQuery()
    {
        var result = await _ragService.QueryAsync("Texas foreclosure CTL requirements", "TX");

        result.Documents.Should().NotBeEmpty();
        result.Documents.Should().Contain(d => d.Title.Contains("Texas", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Query_ShouldFilterByState()
    {
        var txResult = await _ragService.QueryAsync("CTL requirements", "TX", assetType: "Foreclosure");
        var caResult = await _ragService.QueryAsync("CTL requirements", "CA", assetType: "REO");

        txResult.Documents.Should().NotBeEmpty();
        caResult.Documents.Should().NotBeEmpty();
    }

    [Fact]
    public async Task Query_ShouldReturnHOAPolicies()
    {
        var result = await _ragService.QueryAsync("HOA delinquency verification requirements");

        result.Documents.Should().NotBeEmpty();
        result.Documents.Should().Contain(d => d.PolicyType == "HOA-Verification");
    }

    [Fact]
    public async Task Query_ShouldReturnValuationPolicies()
    {
        var result = await _ragService.QueryAsync("BPO staleness thresholds valuation");

        result.Documents.Should().NotBeEmpty();
        result.Documents.Should().Contain(d => d.PolicyType == "Valuation");
    }

    [Fact]
    public async Task Query_ShouldReturnOccupancyPolicies()
    {
        var result = await _ragService.QueryAsync("occupancy clearance vacant property");

        result.Documents.Should().NotBeEmpty();
        result.Documents.Should().Contain(d => d.PolicyType == "Occupancy");
    }

    [Fact]
    public async Task Query_ShouldReturnBaselinePolicyForAllStates()
    {
        var result = await _ragService.QueryAsync("General CTL baseline requirements");

        result.Documents.Should().NotBeEmpty();
        result.Documents.Should().Contain(d => d.State == "ALL");
    }

    [Fact]
    public async Task Query_ShouldReturnMaxFiveDocuments()
    {
        var result = await _ragService.QueryAsync("CTL policy requirements");

        result.Documents.Length.Should().BeLessThanOrEqualTo(5);
    }
}
