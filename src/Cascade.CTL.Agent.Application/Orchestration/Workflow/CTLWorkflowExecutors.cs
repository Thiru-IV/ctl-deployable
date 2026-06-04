using Cascade.CTL.Agent.Application.Configuration;
using Cascade.CTL.Agent.Application.Prompts;
using Cascade.CTL.Agent.Application.Resilience;
using Cascade.CTL.Agent.Domain.Contracts;
using Cascade.CTL.Agent.Domain.Enums;
using Cascade.CTL.Agent.Domain.Models;
using Cascade.CTL.Agent.Guardrails;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Polly;
using System.Text.Json;

namespace Cascade.CTL.Agent.Application.Orchestration.Workflow;

// ══════════════════════════════════════════════════════════════════
// AIAgent-based workflow executors using Microsoft Agent Framework.
// Each executor wraps an AIAgent (via IChatClient.AsAIAgent) which
// handles tool-calling loops, multi-turn reasoning, and session
// management automatically — eliminating manual retry/tool logic.
// ══════════════════════════════════════════════════════════════════

// ──────────────────────────────────────────────────────────────────
// Base class: shared dependencies and AIAgent execution logic
// ──────────────────────────────────────────────────────────────────

internal abstract class CTLExecutorBase : Executor
{
    protected readonly IChatClient _chatClient;
    protected readonly IMcpToolProvider _toolProvider;
    protected readonly IAuditService _auditService;
    protected readonly ResilienceOptions _resilienceOptions;
    protected readonly ILogger _logger;

    protected CTLExecutorBase(
        string name,
        IChatClient chatClient,
        IMcpToolProvider toolProvider,
        IAuditService auditService,
        ResilienceOptions resilienceOptions,
        ILogger logger) : base(name)
    {
        _chatClient = chatClient;
        _toolProvider = toolProvider;
        _auditService = auditService;
        _resilienceOptions = resilienceOptions;
        _logger = logger;
    }

    protected async Task<(string text, int toolCalls)> RunAgentAsync(
        string instructions, IReadOnlyList<AITool> tools, string userMessage,
        CancellationToken cancellationToken, string? phaseName = null)
    {
        if (phaseName != null) GuardrailsContext.CurrentPhase = phaseName;

        var agent = _chatClient.AsAIAgent(instructions: instructions, tools: [.. tools]);
        var session = await agent.CreateSessionAsync(cancellationToken: cancellationToken);
        var runOptions = new ChatClientAgentRunOptions(new ChatOptions { Temperature = 0.0f });

        using var phaseCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        phaseCts.CancelAfter(TimeSpan.FromSeconds(_resilienceOptions.OrchestratorPhaseTimeoutSeconds));

        var response = await agent.RunAsync(userMessage, session, runOptions, phaseCts.Token);
        var toolCalls = WorkflowAgentResponseHelper.CountToolCalls(response);

        return (response.Text ?? "No response generated", toolCalls);
    }

    /// <summary>
    /// Runs an AIAgent and returns both the text result and the full response
    /// so callers can extract individual tool calls for granular auditing.
    /// </summary>
    protected async Task<(string text, int toolCalls, AgentResponse response)> RunAgentWithResponseAsync(
        string instructions, IReadOnlyList<AITool> tools, string userMessage,
        CancellationToken cancellationToken, string? phaseName = null)
    {
        if (phaseName != null) GuardrailsContext.CurrentPhase = phaseName;

        var agent = _chatClient.AsAIAgent(instructions: instructions, tools: [.. tools]);
        var session = await agent.CreateSessionAsync(cancellationToken: cancellationToken);
        var runOptions = new ChatClientAgentRunOptions(new ChatOptions { Temperature = 0.0f });

        using var phaseCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        phaseCts.CancelAfter(TimeSpan.FromSeconds(_resilienceOptions.OrchestratorPhaseTimeoutSeconds));

        var response = await agent.RunAsync(userMessage, session, runOptions, phaseCts.Token);
        var toolCalls = WorkflowAgentResponseHelper.CountToolCalls(response);

        return (response.Text ?? "No response generated", toolCalls, response);
    }

    /// <summary>
    /// Overload that accepts caller-built <see cref="ChatOptions"/> so the Reflection phase can
    /// pass a deterministic options bundle (seed + temperature + response_format) built by
    /// <see cref="ReflectionDeterminismFactory"/>.
    /// </summary>
    protected async Task<(string text, int toolCalls, AgentResponse response)> RunAgentWithResponseAsync(
        string instructions, IReadOnlyList<AITool> tools, string userMessage,
        ChatOptions chatOptions,
        CancellationToken cancellationToken, string? phaseName = null)
    {
        if (phaseName != null) GuardrailsContext.CurrentPhase = phaseName;

        var agent = _chatClient.AsAIAgent(instructions: instructions, tools: [.. tools]);
        var session = await agent.CreateSessionAsync(cancellationToken: cancellationToken);
        var runOptions = new ChatClientAgentRunOptions(chatOptions);

        using var phaseCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        phaseCts.CancelAfter(TimeSpan.FromSeconds(_resilienceOptions.OrchestratorPhaseTimeoutSeconds));

        var response = await agent.RunAsync(userMessage, session, runOptions, phaseCts.Token);
        var toolCalls = WorkflowAgentResponseHelper.CountToolCalls(response);

        return (response.Text ?? "No response generated", toolCalls, response);
    }

    /// <summary>
    /// Extracts individual tool calls from an AIAgent response and records each as a
    /// separate audit entry with tool name, arguments summary, and result preview.
    /// This gives human reviewers full transparency into the agent's reasoning chain.
    /// </summary>
    protected async Task RecordToolCallAuditEntriesAsync(
        AgentResponse response, string sessionId, string assetId, string agentName,
        CancellationToken cancellationToken)
    {
        if (response.Messages is null) return;

        // Build a map of call-id → result for matching calls to their responses
        var resultMap = new Dictionary<string, string>();
        foreach (var msg in response.Messages)
        {
            foreach (var content in msg.Contents.OfType<FunctionResultContent>())
            {
                if (content.CallId is not null)
                    resultMap[content.CallId] = TruncateForAudit(content.Result?.ToString() ?? "(no result)", 500);
            }
        }

        int callIndex = 0;
        foreach (var msg in response.Messages)
        {
            foreach (var call in msg.Contents.OfType<FunctionCallContent>())
            {
                callIndex++;
                var toolName = call.Name ?? "unknown-tool";
                var args = call.Arguments != null
                    ? string.Join(", ", call.Arguments.Select(kv => $"{kv.Key}={TruncateForAudit(kv.Value?.ToString() ?? "", 100)}"))
                    : "(no arguments)";
                var result = call.CallId != null && resultMap.TryGetValue(call.CallId, out var r) ? r : "(pending)";

                await _auditService.RecordStepAsync(new AuditEntry
                {
                    SessionId = sessionId,
                    AssetId = assetId,
                    AgentName = agentName,
                    StepType = "ToolCallExecuted",
                    Description = $"[LLM Tool Call · Non-Deterministic] {toolName}({args}) — tool selected and invoked by the LLM during this phase",
                    OutputPayload = result
                }, cancellationToken);
            }
        }
    }

    private static string TruncateForAudit(string text, int maxLength)
    {
        if (text.Length <= maxLength) return text;
        return text[..maxLength] + "…";
    }
}

// ──────────────────────────────────────────────────────────────────
// PHASE 1: Planning Executor
// Input: PlanRequest (workflow entry)
// Output: PlanResult → flows via edge → InvestigationPhaseExecutor
// ──────────────────────────────────────────────────────────────────

internal sealed class PlanningExecutor : CTLExecutorBase
{
    public PlanningExecutor(
        IChatClient chatClient,
        IMcpToolProvider toolProvider,
        IAuditService auditService,
        ResilienceOptions resilienceOptions,
        ILogger logger)
        : base("PlanningExecutor", chatClient, toolProvider, auditService, resilienceOptions, logger) { }

    protected override ProtocolBuilder ConfigureProtocol(ProtocolBuilder protocolBuilder) =>
        protocolBuilder.ConfigureRoutes(routes =>
            routes.AddHandler<PlanRequest, PlanResult>(HandleAsync, overwrite: false));

    internal async ValueTask<PlanResult> HandleAsync(PlanRequest request, IWorkflowContext context, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("▶ PHASE 1 : Planning - Building verification plan via AIAgent...");

        await _auditService.RecordStepAsync(new AuditEntry
        {
            SessionId = request.SessionId,
            AssetId = request.AssetId,
            AgentName = "CTLOrchestrator-Planning",
            StepType = "PhaseStarted",
            Description = "[Orchestrator · Deterministic] Planning phase started — the LLM (GPT-4o) will now generate a verification plan. " +
                          "It will retrieve the asset profile and query the RAG knowledge base to identify which domains (Legal, Valuation, Occupancy) need investigation."
        }, cancellationToken);

        try
        {
            var (planJson, toolCalls, planResponse) = await RunAgentWithResponseAsync(
                OrchestratorPrompts.PlanningSystemPrompt,
                _toolProvider.GetToolsForOrchestrator(),
                $"Build a CTL verification plan for asset ID: {request.AssetId}. Retrieve the asset profile first, then query the knowledge base for relevant policies.",
                cancellationToken,
                phaseName: "Planning");

            var requiredDomains = PlanParser.ParseRequiredDomains(planJson);

            _logger.LogInformation("  Plan requires domains: {Domains}", string.Join(", ", requiredDomains));

            // Record individual tool calls for transparency
            await RecordToolCallAuditEntriesAsync(planResponse, request.SessionId, request.AssetId, "CTLOrchestrator-Planning", cancellationToken);

            await _auditService.RecordStepAsync(new AuditEntry
            {
                SessionId = request.SessionId,
                AssetId = request.AssetId,
                AgentName = "CTLOrchestrator-Planning",
                StepType = "PlanGenerated",
                Description = $"[LLM · Non-Deterministic] Verification plan generated by LLM (GPT-4o) — domains: {string.Join(", ", requiredDomains)}. " +
                              $"Payload below is the LLM's own response text (not hardcoded).",
                OutputPayload = planJson
            }, cancellationToken);

            return new PlanResult
            {
                PlanJson = planJson,
                AssetProfileJson = request.AssetProfileJson,
                RequiredDomains = requiredDomains,
                ToolCalls = toolCalls,
                AssetId = request.AssetId,
                SessionId = request.SessionId
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "✖ PHASE 1 FAILED: Planning phase encountered an error");

            await _auditService.RecordStepAsync(new AuditEntry
            {
                SessionId = request.SessionId,
                AssetId = request.AssetId,
                AgentName = "CTLOrchestrator-Planning",
                StepType = "PhaseFailed",
                Description = $"[Orchestrator · Error] Planning phase failed: {ex.GetType().Name} — {ex.Message}"
            }, cancellationToken);

            throw;
        }
    }
}

// ──────────────────────────────────────────────────────────────────
// PHASE 2: Investigation Phase Executor
// Input: PlanResult (from PlanningExecutor edge)
// Output: InvestigationPhaseResult → flows via edge → ReflectionExecutor
// Internally dispatches required domain AIAgents in parallel (Task.WhenAll)
// ──────────────────────────────────────────────────────────────────

internal sealed class InvestigationPhaseExecutor : CTLExecutorBase
{
    public InvestigationPhaseExecutor(
        IChatClient chatClient,
        IMcpToolProvider toolProvider,
        IAuditService auditService,
        ResilienceOptions resilienceOptions,
        ILogger logger)
        : base("InvestigationPhaseExecutor", chatClient, toolProvider, auditService, resilienceOptions, logger) { }

    protected override ProtocolBuilder ConfigureProtocol(ProtocolBuilder protocolBuilder) =>
        protocolBuilder.ConfigureRoutes(routes =>
            routes.AddHandler<PlanResult, InvestigationPhaseResult>(HandleAsync, overwrite: false));

    internal async ValueTask<InvestigationPhaseResult> HandleAsync(PlanResult plan, IWorkflowContext context, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("▶ PHASE 2 : Investigation Phase via AIAgent - Running {Count} investigation AIAgents in parallel...", plan.RequiredDomains.Count);

        await _auditService.RecordStepAsync(new AuditEntry
        {
            SessionId = plan.SessionId,
            AssetId = plan.AssetId,
            AgentName = "CTLOrchestrator-Investigation",
            StepType = "PhaseStarted",
            Description = $"[Orchestrator · Deterministic] Investigation phase started — launching {plan.RequiredDomains.Count} LLM sub-agents in parallel: " +
                          $"{string.Join(", ", plan.RequiredDomains)}. Each sub-agent calls MCP tools to gather domain-specific evidence."
        }, cancellationToken);

        var tasks = plan.RequiredDomains.Select(domain =>
            RunDomainAgentAsync(domain, plan, cancellationToken));

        var domainResults = await Task.WhenAll(tasks);
        var resultMap = domainResults.ToDictionary(r => r.domain, r => (r.findings, r.toolCalls));

        int investigationToolCalls = domainResults.Sum(r => r.toolCalls);

        return new InvestigationPhaseResult
        {
            PlanJson = plan.PlanJson,
            AssetProfileJson = plan.AssetProfileJson,
            RequiredDomains = plan.RequiredDomains,
            LegalFindings = resultMap.GetValueOrDefault(VerificationDomain.Legal).findings
                            ?? "Legal Domain not evaluated — not required by verification plan.",
            ValuationFindings = resultMap.GetValueOrDefault(VerificationDomain.Valuation).findings
                                ?? "Valuation Domain not evaluated — not required by verification plan.",
            OccupancyFindings = resultMap.GetValueOrDefault(VerificationDomain.Occupancy).findings
                                ?? "Occupancy Domain not evaluated — not required by verification plan.",
            TotalToolCalls = plan.ToolCalls + investigationToolCalls,
            AssetId = plan.AssetId,
            SessionId = plan.SessionId
        };
    }

    private async Task<(VerificationDomain domain, string findings, int toolCalls)> RunDomainAgentAsync(
        VerificationDomain domain, PlanResult plan, CancellationToken cancellationToken)
    {
        var (agentName, systemPrompt, userMessage, tools) = domain switch
        {
            VerificationDomain.Legal => (
                "Legal & Title",
                InvestigationAgentPrompts.LegalAgentSystemPrompt,
                $"Evaluate legal and title clearance for the asset. Context from planning phase:\n{plan.PlanJson}",
                _toolProvider.GetToolsForLegalAgent()),
            VerificationDomain.Valuation => (
                "Valuation Readiness",
                InvestigationAgentPrompts.ValuationAgentSystemPrompt,
                $"Evaluate valuation readiness for the asset. Context from planning phase:\n{plan.PlanJson}",
                _toolProvider.GetToolsForValuationAgent()),
            VerificationDomain.Occupancy => (
                "Occupancy & Condition",
                InvestigationAgentPrompts.OccupancyAgentSystemPrompt,
                $"Evaluate occupancy and property condition for the asset. Context from planning phase:\n{plan.PlanJson}",
                _toolProvider.GetToolsForOccupancyAgent()),
            _ => throw new ArgumentOutOfRangeException(nameof(domain))
        };

        _logger.LogInformation("  ├─ Starting AIAgent: {AgentName}", agentName);

        var (findings, toolCalls, response) = await RunDomainAgentWithRetryAsync(
            agentName, systemPrompt, userMessage, tools, plan.SessionId, plan.AssetId, cancellationToken);

        _logger.LogInformation("  ├─ Completed AIAgent: {AgentName} ({Length} chars, {ToolCalls} tool calls)",
            agentName, findings.Length, toolCalls);

        // Record each individual tool call as a separate audit entry for full transparency
        await RecordToolCallAuditEntriesAsync(response, plan.SessionId, plan.AssetId, agentName, cancellationToken);

        // Extract a brief summary from the findings for the audit description
        var findingsSummary = ExtractFindingsSummary(findings, agentName);

        // Record the consolidated investigation summary
        await _auditService.RecordStepAsync(new AuditEntry
        {
            SessionId = plan.SessionId,
            AssetId = plan.AssetId,
            AgentName = agentName,
            StepType = "InvestigationFindings",
            Description = $"[LLM Sub-Agent · Non-Deterministic] {agentName} investigation completed ({toolCalls} tool calls, {findings.Length} chars). " +
                          $"{findingsSummary} Payload is the LLM sub-agent's response.",
            OutputPayload = findings
        }, cancellationToken);

        return (domain, findings, toolCalls);
    }

    private async Task<(string findings, int toolCalls, AgentResponse response)> RunDomainAgentWithRetryAsync(
        string agentName, string systemPrompt, string userMessage,
        IReadOnlyList<AITool> tools, string sessionId, string assetId, CancellationToken cancellationToken)
    {
        var pipeline = ResiliencePipelineFactory.CreateAgentRetryPipeline(_resilienceOptions, _logger);

        try
        {
            return await pipeline.ExecuteAsync(
                async ct => await RunAgentWithResponseAsync(systemPrompt, tools, userMessage, ct, phaseName: agentName),
                cancellationToken);
        }
        catch (Exception ex)
        {
            var maxAttempts = _resilienceOptions.AgentMaxRetryAttempts + 1;
            var (failureLabel, _) = ClassifyAgentFailure(ex);
            _logger.LogError(ex, "  ├─ {AgentName} AIAgent failed after {Max} attempt(s): {Label}", agentName, maxAttempts, failureLabel);
            await _auditService.RecordStepAsync(new AuditEntry
            {
                SessionId = sessionId,
                AssetId = assetId,
                AgentName = agentName,
                StepType = "AgentExhaustedRetries",
                Description = $"[LLM Sub-Agent · Error] {agentName} failed after {maxAttempts} attempts: {failureLabel}"
            }, cancellationToken);

            // Return an empty AgentResponse for the failure case
            var failureResponse = new AgentResponse();
            return ($$$"""{"domainVerdict":"NeedsHumanReview","confidence":0.0,"findings":["Agent {{{agentName}}} failed after {{{maxAttempts}}} attempts"],"unverifiedFields":["all"],"summary":"Agent execution failed — human review required"}""", 0, failureResponse);
        }
    }

    /// <summary>
    /// Turns a sub-agent retry-pipeline exception into a human-readable audit label so the
    /// audit JSONL records "HTTP 429 (Azure OpenAI rate limit ...)" instead of an opaque
    /// "ClientResultException". Walks inner exceptions and reflects on a public <c>Status</c>
    /// int property (matches <c>System.ClientModel.ClientResultException</c>) without taking a
    /// hard SDK dependency. Falls back to a message scan for "too_many_requests" before giving
    /// up and returning the exception type name.
    /// </summary>
    internal static (string label, int? status) ClassifyAgentFailure(Exception ex)
    {
        // Walk the exception chain looking for an HTTP status code we can label.
        for (var current = ex; current is not null; current = current.InnerException)
        {
            int? status = current switch
            {
                HttpRequestException http when http.StatusCode is { } sc => (int)sc,
                _ => TryReadIntStatus(current)
            };

            if (status is int code)
                return (LabelForHttpStatus(code), code);
        }

        // Cancellation / timeout — no HTTP status, dedicated label.
        if (ex is OperationCanceledException)
            return ("Cancelled or timed out", null);

        // Last-resort message scan covers SDK exceptions whose Status property we cannot reach.
        for (var current = ex; current is not null; current = current.InnerException)
        {
            var msg = current.Message;
            if (!string.IsNullOrEmpty(msg) &&
                msg.Contains("too_many_requests", StringComparison.OrdinalIgnoreCase))
            {
                return (LabelForHttpStatus(429), 429);
            }
        }

        return (ex.GetType().Name, null);
    }

    private static int? TryReadIntStatus(Exception ex)
    {
        // Match the public surface of System.ClientModel.ClientResultException without a hard dep.
        var prop = ex.GetType().GetProperty("Status",
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
        if (prop is null || prop.PropertyType != typeof(int)) return null;
        try
        {
            return (int?)prop.GetValue(ex);
        }
        catch
        {
            return null;
        }
    }

    private static string LabelForHttpStatus(int status) => status switch
    {
        429 => "HTTP 429 (Azure OpenAI rate limit / too_many_requests)",
        >= 500 and <= 599 => $"HTTP {status} (upstream service error)",
        _ => $"HTTP {status}"
    };

    /// <summary>
    /// Extracts a brief human-readable summary from investigation findings for the audit description.
    /// Looks for key indicators (verdict keywords, risk phrases) so business users can scan the trail.
    /// </summary>
    private static string ExtractFindingsSummary(string findings, string agentName)
    {
        if (string.IsNullOrWhiteSpace(findings)) return "No findings produced.";

        // Extract the first substantive sentence (skip headings and blank lines)
        var lines = findings.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        foreach (var line in lines)
        {
            var trimmed = line.Trim().TrimStart('#', '*', '-', ' ');
            if (trimmed.Length > 30 && !trimmed.StartsWith("```") && !trimmed.StartsWith("{"))
            {
                return trimmed.Length > 200 ? trimmed[..200] + "…" : trimmed;
            }
        }

        return $"Findings produced ({findings.Length} chars). Review payload for details.";
    }
}

// ──────────────────────────────────────────────────────────────────
// PHASE 3: Reflection Executor
// Input: InvestigationPhaseResult (from InvestigationPhaseExecutor edge)
// Output: ReflectionResult → workflow output
// ──────────────────────────────────────────────────────────────────

internal sealed class ReflectionExecutor : CTLExecutorBase
{
    private readonly ReflectionDeterminismOptions? _determinismOptions;

    public ReflectionExecutor(
        IChatClient chatClient,
        IMcpToolProvider toolProvider,
        IAuditService auditService,
        ResilienceOptions resilienceOptions,
        ILogger logger,
        ReflectionDeterminismOptions? determinismOptions = null)
        : base("ReflectionExecutor", chatClient, toolProvider, auditService, resilienceOptions, logger)
    {
        _determinismOptions = determinismOptions;
    }

    protected override ProtocolBuilder ConfigureProtocol(ProtocolBuilder protocolBuilder) =>
        protocolBuilder.ConfigureRoutes(routes =>
            routes.AddHandler<InvestigationPhaseResult, ReflectionResult>(HandleAsync, overwrite: false));

    internal async ValueTask<ReflectionResult> HandleAsync(InvestigationPhaseResult input, IWorkflowContext context, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("▶ PHASE 3 :  Reflection via AIAgent - do cross domain analysis & identify contradictions ...");

        await _auditService.RecordStepAsync(new AuditEntry
        {
            SessionId = input.SessionId,
            AssetId = input.AssetId,
            AgentName = "CTLOrchestrator-Reflection",
            StepType = "PhaseStarted",
            Description = "[Orchestrator · Deterministic] Reflection phase started — the LLM (GPT-4o) will now review all investigation evidence, " +
                          "perform cross-domain analysis, identify contradictions, and propose a verdict with confidence score."
        }, cancellationToken);

        try
        {
            var reflectionInput = OrchestratorPrompts.BuildReflectionInput(
                input.AssetProfileJson,
                input.LegalFindings,
                input.ValuationFindings,
                input.OccupancyFindings,
                input.PlanJson,
                input.RequiredDomains);

            // Build deterministic ChatOptions (seed derived from AssetId, temp=0, optional
            // strict JSON schema). Falls back to plain { Temperature=0 } when disabled.
            var reflectionChatOptions = ReflectionDeterminismFactory.Build(
                _determinismOptions, input.AssetId, input.SessionId);

            var (verdictJson, toolCalls, reflectionResponse) = await RunAgentWithResponseAsync(
                OrchestratorPrompts.ReflectionSystemPrompt,
                _toolProvider.GetToolsForOrchestrator(),
                reflectionInput,
                reflectionChatOptions,
                cancellationToken,
                phaseName: "Reflection");

            var totalToolCalls = input.TotalToolCalls + toolCalls;

            // Record individual tool calls for transparency
            await RecordToolCallAuditEntriesAsync(reflectionResponse, input.SessionId, input.AssetId, "CTLOrchestrator-Reflection", cancellationToken);

            // Extract a meaningful summary from the reflection output for the audit description
            var reflectionSummary = ExtractReflectionSummary(verdictJson);

            await _auditService.RecordStepAsync(new AuditEntry
            {
                SessionId = input.SessionId,
                AssetId = input.AssetId,
                AgentName = "CTLOrchestrator-Reflection",
                StepType = "ReflectionCompleted",
                Description = $"[LLM · Non-Deterministic] Reflection completed by LLM (GPT-4o) ({toolCalls} tool calls). {reflectionSummary}. " +
                              $"Payload is the LLM's full reflection and proposed verdict (may be adjusted by deterministic post-processing).",
                OutputPayload = verdictJson
            }, cancellationToken);

            // Concatenate investigation findings for the quality gate evaluator
            var investigationFindings = string.Join("\n\n",
                new[] { input.LegalFindings, input.ValuationFindings, input.OccupancyFindings }
                    .Where(f => !string.IsNullOrWhiteSpace(f)));

            return new ReflectionResult
            {
                VerdictJson = verdictJson,
                InvestigationFindings = investigationFindings,
                ToolCalls = toolCalls,
                TotalToolCalls = totalToolCalls,
                AssetId = input.AssetId,
                SessionId = input.SessionId
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "✖ PHASE 3 FAILED: Reflection phase encountered an error");

            await _auditService.RecordStepAsync(new AuditEntry
            {
                SessionId = input.SessionId,
                AssetId = input.AssetId,
                AgentName = "CTLOrchestrator-Reflection",
                StepType = "PhaseFailed",
                Description = $"[Orchestrator · Error] Reflection phase failed: {ex.GetType().Name} — {ex.Message}"
            }, cancellationToken);

            throw;
        }
    }

    /// <summary>
    /// Extracts a meaningful 1-2 line summary from the reflection output so
    /// business users can understand the verdict reasoning at a glance without
    /// reading the full payload.
    /// </summary>
    private static string ExtractReflectionSummary(string verdictJson)
    {
        if (string.IsNullOrWhiteSpace(verdictJson)) return "No reflection output produced.";

        // Try to find the JSON block and extract verdict + confidence
        var verdictMatch = System.Text.RegularExpressions.Regex.Match(verdictJson,
            @"""verdict""\s*:\s*""([^""]+)""");
        var confidenceMatch = System.Text.RegularExpressions.Regex.Match(verdictJson,
            @"""confidenceScore""\s*:\s*([\d.]+)");
        var reflectionLogMatch = System.Text.RegularExpressions.Regex.Match(verdictJson,
            @"""reflectionLog""\s*:\s*""([^""]{10,})""");

        var parts = new List<string>();

        if (verdictMatch.Success && confidenceMatch.Success)
            parts.Add($"Proposed verdict: {verdictMatch.Groups[1].Value} (confidence: {confidenceMatch.Groups[1].Value})");

        if (reflectionLogMatch.Success)
        {
            var log = reflectionLogMatch.Groups[1].Value;
            parts.Add(log.Length > 200 ? log[..200] + "…" : log);
        }

        if (parts.Count > 0) return string.Join(". ", parts);

        // Fallback: extract first substantive line
        var lines = verdictJson.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        foreach (var line in lines)
        {
            var trimmed = line.Trim().TrimStart('#', '*', '-', ' ');
            if (trimmed.Length > 30 && !trimmed.StartsWith("```") && !trimmed.StartsWith("{"))
                return trimmed.Length > 200 ? trimmed[..200] + "…" : trimmed;
        }

        return "Reflection produced output. Review payload for verdict details.";
    }
}

// ──────────────────────────────────────────────────────────────────
// PHASE 4: Verdict Parsing Executor
// Input: ReflectionResult (from ReflectionExecutor edge)
// Output: VerdictParsingResult → flows via edge → QualityGateExecutor
// Deterministic — parses raw LLM JSON into a structured CTLVerdictDto.
// ──────────────────────────────────────────────────────────────────

internal sealed class VerdictParsingExecutor : Executor
{
    private readonly IAuditService _auditService;
    private readonly ILogger _logger;
    private readonly double _humanReviewThreshold;
    private readonly ReflectionDeterminismOptions? _determinismOptions;

    public VerdictParsingExecutor(
        IAuditService auditService,
        ILogger logger,
        double humanReviewThreshold = 0.75,
        ReflectionDeterminismOptions? determinismOptions = null)
        : base("VerdictParsingExecutor")
    {
        _auditService = auditService;
        _logger = logger;
        _humanReviewThreshold = humanReviewThreshold;
        _determinismOptions = determinismOptions;
    }

    protected override ProtocolBuilder ConfigureProtocol(ProtocolBuilder protocolBuilder) =>
        protocolBuilder.ConfigureRoutes(routes =>
            routes.AddHandler<ReflectionResult, VerdictParsingResult>(HandleAsync, overwrite: false));

    internal async ValueTask<VerdictParsingResult> HandleAsync(ReflectionResult input, IWorkflowContext context, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("▶ PHASE 4 : Parsing verdict - Analyse and understand the verdict...");

        await _auditService.RecordStepAsync(new AuditEntry
        {
            SessionId = input.SessionId,
            AssetId = input.AssetId,
            AgentName = "CTLWorkflowOrchestrator-VerdictParsing",
            StepType = "PhaseStarted",
            Description = "[Orchestrator · Deterministic] Verdict parsing phase started — rule-based code (no LLM) will now parse the LLM's JSON output, " +
                          "validate verdict-confidence consistency, and apply remap rules if needed (threshold: " + _humanReviewThreshold.ToString("F2") + ")."
        }, cancellationToken);

        var verdict = VerdictParser.ParseVerdict(input.VerdictJson, input.AssetId, input.SessionId, _logger, _humanReviewThreshold, _determinismOptions);

        // Record the deterministic parsing result so the audit trail shows what happened
        var remapCondition = verdict.Conditions.FirstOrDefault(c => c.Contains("Verdict remapped from"));
        var remapNote = remapCondition != null
            ? $"Deterministic remap rule applied — {remapCondition}"
            : $"No remap rules triggered — LLM verdict accepted as-is (confidence {verdict.ConfidenceScore:F2} is within expected bounds for {verdict.Verdict})";

        await _auditService.RecordStepAsync(new AuditEntry
        {
            SessionId = input.SessionId,
            AssetId = input.AssetId,
            AgentName = "CTLWorkflowOrchestrator-VerdictParsing",
            StepType = "VerdictParsed",
            Description = $"[Orchestrator · Deterministic] Parsed verdict: {verdict.Verdict} (confidence: {verdict.ConfidenceScore:F2}). {remapNote}. " +
                          $"Note: VerdictParser is rule-based code (not LLM) — it only changes the verdict label, never the confidence score. Confidence always comes from the LLM."
        }, cancellationToken);

        // Detect domain verdict conflicts: parse each sub-agent's domainVerdict and flag
        // any that disagree with the final verdict so the audit trail explains the discrepancy
        var domainConflicts = DetectDomainVerdictConflicts(input.InvestigationFindings, verdict.Verdict);
        if (domainConflicts.Count > 0)
        {
            var conflictLines = string.Join(" ", domainConflicts.Select(c =>
                $"{c.domain} sub-agent proposed '{c.domainVerdict}' but final verdict is '{verdict.Verdict}'."));

            await _auditService.RecordStepAsync(new AuditEntry
            {
                SessionId = input.SessionId,
                AssetId = input.AssetId,
                AgentName = "CTLWorkflowOrchestrator-VerdictParsing",
                StepType = "DomainVerdictConflict",
                Description = $"[Orchestrator · Deterministic] Domain verdict conflict detected: {conflictLines} " +
                              $"This is expected — the Reflection LLM (non-deterministic) performs cross-domain synthesis and may override " +
                              $"individual domain assessments when the overall evidence supports a different conclusion. " +
                              $"See the ReflectionCompleted payload for the LLM's reasoning."
            }, cancellationToken);
        }

        return new VerdictParsingResult
        {
            Verdict = verdict,
            InvestigationFindings = input.InvestigationFindings,
            VerdictJson = input.VerdictJson,
            TotalToolCalls = input.TotalToolCalls,
            AssetId = input.AssetId,
            SessionId = input.SessionId
        };
    }

    /// <summary>
    /// Parses domain verdicts from investigation findings text and identifies any that conflict
    /// with the final verdict. Domain findings contain JSON like "domainVerdict": "NeedsHumanReview"
    /// which is dynamically extracted and compared.
    /// </summary>
    private static List<(string domain, string domainVerdict)> DetectDomainVerdictConflicts(
        string investigationFindings, CTLVerdict finalVerdict)
    {
        var conflicts = new List<(string domain, string domainVerdict)>();
        if (string.IsNullOrWhiteSpace(investigationFindings)) return conflicts;

        // The investigation findings are concatenated with \n\n separators.
        // Each domain's JSON contains "domainVerdict": "XYZ"
        var domainVerdictPattern = new System.Text.RegularExpressions.Regex(
            @"""domainVerdict""\s*:\s*""([^""]+)""",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        // Also try to identify which domain the finding belongs to
        var domainNamePattern = new System.Text.RegularExpressions.Regex(
            @"""domain""\s*:\s*""([^""]+)""",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        // Split findings into domain sections (separated by double newlines)
        var sections = investigationFindings.Split(["\n\n"], StringSplitOptions.RemoveEmptyEntries);

        foreach (var section in sections)
        {
            var verdictMatch = domainVerdictPattern.Match(section);
            if (!verdictMatch.Success) continue;

            var domainVerdictStr = verdictMatch.Groups[1].Value;

            // Try to get domain name from the same section
            var domainMatch = domainNamePattern.Match(section);
            var domainName = domainMatch.Success ? domainMatch.Groups[1].Value : "Unknown";

            // Normalize and compare: does this domain verdict differ from the final?
            if (!string.Equals(domainVerdictStr, finalVerdict.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                conflicts.Add((domainName, domainVerdictStr));
            }
        }

        return conflicts;
    }
}

// ──────────────────────────────────────────────────────────────────
// PHASE 5: Quality Gate Executor
// Input: VerdictParsingResult (from VerdictParsingExecutor edge)
// Output: QualityGateResult → flows via edge → HumanReviewExecutor
// Uses LLM-as-judge to verify the verdict is grounded in findings.
// Conditional: skips evaluation if already NeedsHumanReview or disabled.
// ──────────────────────────────────────────────────────────────────

internal sealed class QualityGateExecutor : Executor
{
    private readonly VerdictGroundednessEvaluator _evaluator;
    private readonly IAuditService _auditService;
    private readonly QualityGateOptions _options;
    private readonly ILogger _logger;

    public QualityGateExecutor(
        VerdictGroundednessEvaluator evaluator,
        IAuditService auditService,
        QualityGateOptions options,
        ILogger logger) : base("QualityGateExecutor")
    {
        _evaluator = evaluator;
        _auditService = auditService;
        _options = options;
        _logger = logger;
    }

    protected override ProtocolBuilder ConfigureProtocol(ProtocolBuilder protocolBuilder) =>
        protocolBuilder.ConfigureRoutes(routes =>
            routes.AddHandler<VerdictParsingResult, QualityGateResult>(HandleAsync, overwrite: false));

    internal async ValueTask<QualityGateResult> HandleAsync(VerdictParsingResult input, IWorkflowContext context, CancellationToken cancellationToken = default)
    {
        var verdict = input.Verdict;

        if (_options.Enabled && verdict.Verdict != CTLVerdict.NeedsHumanReview)
        {
            _logger.LogInformation("▶ PHASE 5 : Quality Gate using LLM — evaluating verdict groundedness (min score: {MinScore})...",
                _options.MinGroundednessScore);

            await _auditService.RecordStepAsync(new AuditEntry
            {
                SessionId = input.SessionId,
                AssetId = input.AssetId,
                AgentName = "CTLWorkflowOrchestrator-QualityGate",
                StepType = "PhaseStarted",
                Description = $"[Orchestrator · Deterministic] Quality Gate phase started — an LLM-as-Judge (separate LLM call, no tools) will score " +
                              $"how well the reflection verdict is grounded in investigation evidence. Minimum passing score: {_options.MinGroundednessScore}/5."
            }, cancellationToken);

            // Set phase context so GuardrailsMiddleware tags its audit entries with this phase
            GuardrailsContext.CurrentPhase = "QualityGate";

            var groundednessResult = await _evaluator.EvaluateAsync(
                input.InvestigationFindings, verdict, cancellationToken);

            var passedGate = groundednessResult.Passes(_options.MinGroundednessScore);

            await _auditService.RecordStepAsync(new AuditEntry
            {
                SessionId = input.SessionId,
                AssetId = input.AssetId,
                AgentName = "CTLWorkflowOrchestrator-QualityGate",
                StepType = "QualityGateEvaluated",
                Description = $"[LLM-as-Judge · Non-Deterministic] Groundedness score: {groundednessResult.Score}/5 (threshold: {_options.MinGroundednessScore}). " +
                              $"Passed: {passedGate}. " +
                              $"Reasoning: {groundednessResult.Reasoning}"
            }, cancellationToken);

            if (!passedGate)
            {
                var previousVerdict = verdict.Verdict;

                _logger.LogWarning("  Quality Gate FAILED — groundedness {Score}/5 below threshold {Threshold}. Escalating to NeedsHumanReview.",
                    groundednessResult.Score, _options.MinGroundednessScore);

                verdict = verdict with
                {
                    Verdict = CTLVerdict.NeedsHumanReview,
                    ReflectionLog = verdict.ReflectionLog +
                        $"\n\n[QUALITY GATE] Verdict failed groundedness check (score: {groundednessResult.Score}/5, " +
                        $"threshold: {_options.MinGroundednessScore}). Reason: {groundednessResult.Reasoning}"
                };

                // Record the verdict change so audit shows the confidence was preserved but verdict escalated
                await _auditService.RecordStepAsync(new AuditEntry
                {
                    SessionId = input.SessionId,
                    AssetId = input.AssetId,
                    AgentName = "CTLWorkflowOrchestrator-QualityGate",
                    StepType = "VerdictEscalated",
                    Description = $"[Orchestrator · Deterministic] Quality Gate escalated verdict: {previousVerdict} → NeedsHumanReview. " +
                                  $"Confidence unchanged at {verdict.ConfidenceScore:F2}. " +
                                  $"Reason: groundedness score {groundednessResult.Score}/5 failed threshold {_options.MinGroundednessScore}."
                }, cancellationToken);
            }
            else
            {
                _logger.LogInformation("  Quality Gate PASSED — groundedness {Score}/5 (threshold: {Threshold})",
                    groundednessResult.Score, _options.MinGroundednessScore);
            }
        }
        else if (!_options.Enabled)
        {
            _logger.LogInformation("▶ PHASE 5 : Quality Gate skipped (disabled via config).");

            await _auditService.RecordStepAsync(new AuditEntry
            {
                SessionId = input.SessionId,
                AssetId = input.AssetId,
                AgentName = "CTLWorkflowOrchestrator-QualityGate",
                StepType = "QualityGateEvaluated",
                Description = $"[Orchestrator · Deterministic] Quality Gate skipped — disabled via configuration. No groundedness check performed. Verdict: {verdict.Verdict} (confidence: {verdict.ConfidenceScore:F2}) passes through unchanged."
            }, cancellationToken);
        }
        else
        {
            _logger.LogInformation("▶ PHASE 5: Quality Gate skipped (verdict already NeedsHumanReview).");

            await _auditService.RecordStepAsync(new AuditEntry
            {
                SessionId = input.SessionId,
                AssetId = input.AssetId,
                AgentName = "CTLWorkflowOrchestrator-QualityGate",
                StepType = "QualityGateEvaluated",
                Description = $"[Orchestrator · Deterministic] Quality Gate skipped — verdict is already {verdict.Verdict} (confidence: {verdict.ConfidenceScore:F2}), groundedness check not required. Confidence unchanged."
            }, cancellationToken);
        }

        return new QualityGateResult
        {
            Verdict = verdict,
            VerdictJson = input.VerdictJson,
            TotalToolCalls = input.TotalToolCalls,
            AssetId = input.AssetId,
            SessionId = input.SessionId
        };
    }
}

// ──────────────────────────────────────────────────────────────────
// PHASE 6: Human Review Executor
// Input: QualityGateResult (from QualityGateExecutor edge)
// Output: HumanReviewResult → workflow output
// Conditional: invokes IHumanReviewService only when verdict is
// NeedsHumanReview; otherwise passes through unchanged.
// ──────────────────────────────────────────────────────────────────

internal sealed class HumanReviewExecutor : Executor
{
    private readonly IHumanReviewService _humanReviewService;
    private readonly IAuditService _auditService;
    private readonly ILogger _logger;

    public HumanReviewExecutor(
        IHumanReviewService humanReviewService,
        IAuditService auditService,
        ILogger logger) : base("HumanReviewExecutor")
    {
        _humanReviewService = humanReviewService;
        _auditService = auditService;
        _logger = logger;
    }

    protected override ProtocolBuilder ConfigureProtocol(ProtocolBuilder protocolBuilder) =>
        protocolBuilder.ConfigureRoutes(routes =>
            routes.AddHandler<QualityGateResult, HumanReviewResult>(HandleAsync, overwrite: false));

    internal async ValueTask<HumanReviewResult> HandleAsync(QualityGateResult input, IWorkflowContext context, CancellationToken cancellationToken = default)
    {
        var verdict = input.Verdict;
        HumanReviewDecision? humanReview = null;

        // Both NeedsHumanReview and NotClear require human review:
        // - NeedsHumanReview: agent explicitly flagged for human oversight
        // - NotClear: denying listing is a high-impact decision that also requires human oversight
        if (verdict.Verdict is CTLVerdict.NeedsHumanReview or CTLVerdict.NotClear)
        {
            _logger.LogInformation("▶ PHASE 6: Human Review — Verdict is {Verdict} (confidence: {Confidence:F2}) — requesting human review...",
                verdict.Verdict, verdict.ConfidenceScore);

            await _auditService.RecordStepAsync(new AuditEntry
            {
                SessionId = input.SessionId,
                AssetId = input.AssetId,
                AgentName = "CTLWorkflowOrchestrator-HITL",
                StepType = "PhaseStarted",
                Description = $"[Orchestrator · Deterministic] Human Review phase started — verdict is {verdict.Verdict} (confidence: {verdict.ConfidenceScore:F2}), " +
                              $"which requires human oversight. Waiting for human reviewer input via interactive CLI prompt."
            }, cancellationToken);

            var reviewRequest = new HumanReviewRequest
            {
                SessionId = input.SessionId,
                AssetId = input.AssetId,
                ProposedVerdict = verdict,
                ReflectionOutput = input.VerdictJson
            };

            humanReview = await _humanReviewService.RequestReviewAsync(reviewRequest, cancellationToken);

            var verdictChanged = humanReview.OverriddenVerdict.HasValue
                && humanReview.OverriddenVerdict.Value != verdict.Verdict;
            var confidenceChanged = humanReview.OverriddenConfidence.HasValue
                && Math.Abs(humanReview.OverriddenConfidence.Value - verdict.ConfidenceScore) > 0.0001;

            if (verdictChanged || confidenceChanged)
            {
                var previousVerdict = verdict.Verdict;
                var previousConfidence = verdict.ConfidenceScore;
                var newVerdict = humanReview.OverriddenVerdict ?? verdict.Verdict;
                var newConfidence = humanReview.OverriddenConfidence ?? verdict.ConfidenceScore;

                _logger.LogInformation("  Human review applied: {Original} → {Override} (confidence: {OldConf:F2} → {NewConf:F2})",
                    previousVerdict, newVerdict, previousConfidence, newConfidence);

                verdict = verdict with
                {
                    Verdict = newVerdict,
                    ConfidenceScore = newConfidence,
                    Conditions = [.. verdict.Conditions, $"Human review by {humanReview.ReviewedBy}: {humanReview.ReviewerNotes}"],
                    ReflectionLog = verdict.ReflectionLog + $"\n\n[HUMAN REVIEW] {humanReview.Action} by {humanReview.ReviewedBy}: {humanReview.ReviewerNotes}"
                };

                await _auditService.RecordStepAsync(new AuditEntry
                {
                    SessionId = input.SessionId,
                    AssetId = input.AssetId,
                    AgentName = "CTLWorkflowOrchestrator-HITL",
                    StepType = "HumanReviewCompleted",
                    Description = $"[Human Reviewer · Manual Input] Human review: {humanReview.Action} by {humanReview.ReviewedBy}. " +
                                  $"Verdict changed: {previousVerdict} → {verdict.Verdict}. " +
                                  $"Confidence changed: {previousConfidence:F2} → {verdict.ConfidenceScore:F2} (reviewer-assigned). " +
                                  $"Reason: {humanReview.ReviewerNotes}"
                }, cancellationToken);
            }
            else
            {
                _logger.LogInformation("  Human confirmed {Verdict} verdict (confidence unchanged at {Confidence:F2}).",
                    verdict.Verdict, verdict.ConfidenceScore);

                await _auditService.RecordStepAsync(new AuditEntry
                {
                    SessionId = input.SessionId,
                    AssetId = input.AssetId,
                    AgentName = "CTLWorkflowOrchestrator-HITL",
                    StepType = "HumanReviewCompleted",
                    Description = $"[Human Reviewer · Manual Input] Human review: Confirmed by {humanReview.ReviewedBy}. " +
                                  $"Verdict unchanged: {verdict.Verdict} (confidence: {verdict.ConfidenceScore:F2}). " +
                                  $"Reason: {humanReview.ReviewerNotes}"
                }, cancellationToken);
            }
        }
        else
        {
            _logger.LogInformation("▶ PHASE 6: Human Review —  not required — verdict is {Verdict} (confidence: {Confidence:F2}).",
                verdict.Verdict, verdict.ConfidenceScore);

            await _auditService.RecordStepAsync(new AuditEntry
            {
                SessionId = input.SessionId,
                AssetId = input.AssetId,
                AgentName = "CTLWorkflowOrchestrator-HITL",
                StepType = "HumanReviewCompleted",
                Description = $"[Orchestrator · Deterministic] Human review not required — verdict is {verdict.Verdict} (confidence: {verdict.ConfidenceScore:F2}). " +
                              $"HITL is only triggered for NeedsHumanReview or NotClear verdicts."
            }, cancellationToken);
        }

        return new HumanReviewResult
        {
            Verdict = verdict,
            HumanReview = humanReview,
            VerdictJson = input.VerdictJson,
            TotalToolCalls = input.TotalToolCalls,
            AssetId = input.AssetId,
            SessionId = input.SessionId
        };
    }
}

// ──────────────────────────────────────────────────────────────────
// Shared helper: count tool calls from AgentResponse
// ──────────────────────────────────────────────────────────────────

internal static class WorkflowAgentResponseHelper
{
    /// <summary>
    /// Count actual tool invocations from an AgentResponse's message history.
    /// </summary>
    internal static int CountToolCalls(AgentResponse response)
    {
        if (response.Messages is null) return 0;

        return response.Messages
            .SelectMany(m => m.Contents)
            .OfType<FunctionCallContent>()
            .Count();
    }
}
