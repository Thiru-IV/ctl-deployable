namespace Cascade.CTL.Agent.Application.Orchestration;

/// <summary>
/// Per-agent tool visibility rules for the MCP tool catalog.
/// Extracted from <see cref="McpToolProvider"/> so the membership matrix can be unit-tested
/// without instantiating a live MCP client connection.
/// </summary>
/// <remarks>
/// <para>
/// The orchestrator agent is intentionally restricted to <c>QueryPolicyKnowledgeBaseViaRAG</c> only.
/// <c>GetAssetProfile</c> is NOT exposed because the orchestrator pre-fetches the asset profile
/// via <c>IAssetProfileProvider</c> and injects the full JSON directly into the planning / reflection
/// prompts. Offering the same data as an agent tool would cause redundant tool-call round trips,
/// inflate token usage, and risk the LLM skipping the pre-fetched grounding in favor of a tool call
/// (see docs/Agentic_AI_Threat_Catalog.md).
/// </para>
/// </remarks>
public static class ToolFilters
{
    public static bool IsOrchestratorTool(string toolName) =>
        toolName is "query_policy_knowledge_base_via_rag" or "QueryPolicyKnowledgeBaseViaRAG";

    public static bool IsLegalAgentTool(string toolName) =>
        toolName is "search_title" or "SearchTitle"
            or "check_hoa_delinquency" or "CheckHOADelinquency"
            or "lookup_code_violations" or "LookupCodeViolations"
            or "query_policy_knowledge_base_via_rag" or "QueryPolicyKnowledgeBaseViaRAG";

    public static bool IsValuationAgentTool(string toolName) =>
        toolName is "retrieve_bpo" or "RetrieveBPO"
            or "get_avm" or "GetAVM"
            or "query_policy_knowledge_base_via_rag" or "QueryPolicyKnowledgeBaseViaRAG";

    public static bool IsOccupancyAgentTool(string toolName) =>
        toolName is "get_occupancy_status" or "GetOccupancyStatus"
            or "query_policy_knowledge_base_via_rag" or "QueryPolicyKnowledgeBaseViaRAG";
}
