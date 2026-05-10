namespace Cascade.CTL.Agent.Application.Resilience;

/// <summary>
/// Configuration for resilience policies across the CTL Agent solution.
/// Bind from appsettings "Resilience" section.
/// </summary>
public sealed class ResilienceOptions
{
    public const string SectionName = "Resilience";

    /// <summary>Timeout in seconds for a single LLM (Azure OpenAI) call.</summary>
    public int LlmCallTimeoutSeconds { get; set; } = 60;

    /// <summary>Max retry attempts for transient LLM failures (429, 5xx, timeout).</summary>
    public int LlmMaxRetryAttempts { get; set; } = 3;

    /// <summary>Timeout in seconds for MCP Tool Provider initialization.</summary>
    public int McpInitTimeoutSeconds { get; set; } = 30;

    /// <summary>Max retry attempts for MCP init (server may be starting).</summary>
    public int McpInitMaxRetryAttempts { get; set; } = 3;

    /// <summary>Timeout in seconds for a single orchestrator phase (plan/reflect).</summary>
    public int OrchestratorPhaseTimeoutSeconds { get; set; } = 90;

    /// <summary>Max retry attempts for a failed investigation agent before fallback.</summary>
    public int AgentMaxRetryAttempts { get; set; } = 2;
}
