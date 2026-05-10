using Cascade.CTL.Agent.Application.Orchestration;
using FluentAssertions;
using Xunit;

namespace Cascade.CTL.Agent.Tests.Orchestration;

/// <summary>
/// Tests for <see cref="ToolFilters"/> — the per-agent MCP tool visibility matrix.
/// Guards the design decision to exclude <c>GetAssetProfile</c> from the orchestrator agent
/// (the orchestrator pre-fetches the profile deterministically via <c>IAssetProfileProvider</c>).
/// </summary>
public sealed class ToolFiltersTests
{
    [Fact]
    public void OrchestratorTools_ExcludesGetAssetProfile()
    {
        ToolFilters.IsOrchestratorTool("GetAssetProfile").Should().BeFalse(
            "the orchestrator pre-fetches the asset profile via IAssetProfileProvider; " +
            "re-exposing it as an agent tool would cause redundant tool-call round trips");
    }

    [Fact]
    public void OrchestratorTools_IncludesQueryPolicyKnowledgeBaseViaRAG()
    {
        ToolFilters.IsOrchestratorTool("QueryPolicyKnowledgeBaseViaRAG").Should().BeTrue();
    }

    [Theory]
    [InlineData("SearchTitle")]
    [InlineData("RetrieveBPO")]
    [InlineData("GetAVM")]
    [InlineData("GetOccupancyStatus")]
    [InlineData("CheckHOADelinquency")]
    [InlineData("LookupCodeViolations")]
    [InlineData("NonExistentTool")]
    public void OrchestratorTools_ExcludesDomainAndUnknownTools(string toolName)
    {
        ToolFilters.IsOrchestratorTool(toolName).Should().BeFalse();
    }

    // ── snake_case MCP wire-format tests ──────────────────────────

    [Fact]
    public void OrchestratorTools_IncludesSnakeCaseQueryPolicyKnowledgeBaseViaRAG()
    {
        ToolFilters.IsOrchestratorTool("query_policy_knowledge_base_via_rag").Should().BeTrue();
    }

    [Theory]
    [InlineData("search_title", true)]
    [InlineData("check_hoa_delinquency", true)]
    [InlineData("lookup_code_violations", true)]
    [InlineData("query_policy_knowledge_base_via_rag", true)]
    [InlineData("get_asset_profile", false)]
    [InlineData("retrieve_bpo", false)]
    [InlineData("get_occupancy_status", false)]
    public void LegalAgentTools_IncludesSnakeCaseNames(string toolName, bool expected)
    {
        ToolFilters.IsLegalAgentTool(toolName).Should().Be(expected);
    }

    [Theory]
    [InlineData("retrieve_bpo", true)]
    [InlineData("get_avm", true)]
    [InlineData("query_policy_knowledge_base_via_rag", true)]
    [InlineData("get_asset_profile", false)]
    [InlineData("search_title", false)]
    [InlineData("get_occupancy_status", false)]
    public void ValuationAgentTools_IncludesSnakeCaseNames(string toolName, bool expected)
    {
        ToolFilters.IsValuationAgentTool(toolName).Should().Be(expected);
    }

    [Theory]
    [InlineData("get_occupancy_status", true)]
    [InlineData("query_policy_knowledge_base_via_rag", true)]
    [InlineData("get_asset_profile", false)]
    [InlineData("search_title", false)]
    [InlineData("retrieve_bpo", false)]
    public void OccupancyAgentTools_IncludesSnakeCaseNames(string toolName, bool expected)
    {
        ToolFilters.IsOccupancyAgentTool(toolName).Should().Be(expected);
    }

    [Theory]
    [InlineData("SearchTitle", true)]
    [InlineData("CheckHOADelinquency", true)]
    [InlineData("LookupCodeViolations", true)]
    [InlineData("QueryPolicyKnowledgeBaseViaRAG", true)]
    [InlineData("GetAssetProfile", false)]
    [InlineData("RetrieveBPO", false)]
    [InlineData("GetOccupancyStatus", false)]
    public void LegalAgentTools_IncludesOnlyLegalDomainAndRag(string toolName, bool expected)
    {
        ToolFilters.IsLegalAgentTool(toolName).Should().Be(expected);
    }

    [Theory]
    [InlineData("RetrieveBPO", true)]
    [InlineData("GetAVM", true)]
    [InlineData("QueryPolicyKnowledgeBaseViaRAG", true)]
    [InlineData("GetAssetProfile", false)]
    [InlineData("SearchTitle", false)]
    [InlineData("GetOccupancyStatus", false)]
    public void ValuationAgentTools_IncludesOnlyValuationDomainAndRag(string toolName, bool expected)
    {
        ToolFilters.IsValuationAgentTool(toolName).Should().Be(expected);
    }

    [Theory]
    [InlineData("GetOccupancyStatus", true)]
    [InlineData("QueryPolicyKnowledgeBaseViaRAG", true)]
    [InlineData("GetAssetProfile", false)]
    [InlineData("SearchTitle", false)]
    [InlineData("RetrieveBPO", false)]
    public void OccupancyAgentTools_IncludesOnlyOccupancyDomainAndRag(string toolName, bool expected)
    {
        ToolFilters.IsOccupancyAgentTool(toolName).Should().Be(expected);
    }
}
