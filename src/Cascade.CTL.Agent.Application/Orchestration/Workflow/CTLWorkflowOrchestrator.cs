using System.Text.Json;
using Cascade.CTL.Agent.Application.Configuration;
using Cascade.CTL.Agent.Application.Resilience;
using Cascade.CTL.Agent.Domain.Contracts;
using Cascade.CTL.Agent.Domain.Enums;
using Cascade.CTL.Agent.Domain.Models;
using Cascade.CTL.Agent.Guardrails;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cascade.CTL.Agent.Application.Orchestration.Workflow;

/// <summary>
/// Workflow-based CTL evaluation orchestrator using Microsoft Agent Framework.
/// Uses AIAgent (via IChatClient.AsAIAgent) inside each Executor node for automatic
/// tool-calling loops and session management. The WorkflowBuilder defines the DAG:
/// PlanningExecutor → InvestigationPhaseExecutor → ReflectionExecutor →
/// VerdictParsingExecutor → QualityGateExecutor → HumanReviewExecutor → output.
/// </summary>
public sealed class CTLWorkflowOrchestrator : ICTLEvaluationOrchestrator
{
    private readonly IChatClient _chatClient;
    private readonly IMcpToolProvider _toolProvider;
    private readonly IAuditService _auditService;
    private readonly IAssetProfileProvider _assetProfileProvider;
    private readonly IHumanReviewService _humanReviewService;
    private readonly ContentSafetyGuard _contentSafetyGuard;
    private readonly TokenBudgetGuard _tokenBudgetGuard;
    private readonly CTLRequestValidator _requestValidator;
    private readonly VerdictGroundednessEvaluator _groundednessEvaluator;
    private readonly QualityGateOptions _qualityGateOptions;
    private readonly VerdictPolicyOptions _verdictPolicyOptions;
    private readonly ResilienceOptions _resilienceOptions;
    private readonly ReflectionDeterminismOptions _determinismOptions;
    private readonly ILogger<CTLWorkflowOrchestrator> _logger;

    private static JsonSerializerOptions JsonOptions => VerdictParser.JsonOptions;

    public CTLWorkflowOrchestrator(
        IChatClient chatClient,
        IMcpToolProvider toolProvider,
        IAuditService auditService,
        IAssetProfileProvider assetProfileProvider,
        IHumanReviewService humanReviewService,
        ContentSafetyGuard contentSafetyGuard,
        TokenBudgetGuard tokenBudgetGuard,
        CTLRequestValidator requestValidator,
        VerdictGroundednessEvaluator groundednessEvaluator,
        IOptions<CTLAgentOptions> agentOptions,
        IOptions<VerdictPolicyOptions> verdictPolicyOptions,
        IOptions<ResilienceOptions> resilienceOptions,
        IOptions<ReflectionDeterminismOptions> determinismOptions,
        ILogger<CTLWorkflowOrchestrator> logger)
    {
        _chatClient = chatClient;
        _toolProvider = toolProvider;
        _auditService = auditService;
        _assetProfileProvider = assetProfileProvider;
        _humanReviewService = humanReviewService;
        _contentSafetyGuard = contentSafetyGuard;
        _tokenBudgetGuard = tokenBudgetGuard;
        _requestValidator = requestValidator;
        _groundednessEvaluator = groundednessEvaluator;
        _qualityGateOptions = agentOptions.Value.QualityGate;
        _verdictPolicyOptions = verdictPolicyOptions.Value;
        _resilienceOptions = resilienceOptions.Value;
        _determinismOptions = determinismOptions.Value;
        _logger = logger;
    }

    public async Task<CTLEvaluationResult> EvaluateAsync(CTLEvaluationRequest request, CancellationToken cancellationToken = default)
    {
        // Validate input at system boundary (same as imperative)
        var validation = _requestValidator.ValidateEvaluationRequest(request);
        if (!validation.IsValid)
        {
            _logger.LogWarning("CTL evaluation request validation failed: {Errors}", string.Join("; ", validation.Errors));
            throw new ArgumentException($"Invalid evaluation request: {string.Join("; ", validation.Errors)}");
        }

        var sessionId = Guid.NewGuid().ToString("N")[..12];
        var startTime = DateTime.UtcNow;

        _logger.LogInformation("═══════════════════════════════════════════════════════════════");
        _logger.LogInformation("CTL Evaluation Starting [WORKFLOW MODE] — Asset: {AssetId} Session: {SessionId}", request.AssetId, sessionId);
        _logger.LogInformation("═══════════════════════════════════════════════════════════════");

        TokenBudgetGuard.CurrentSessionId = sessionId;
        _tokenBudgetGuard.Reset();

        await _auditService.RecordStepAsync(new AuditEntry
        {
            SessionId = sessionId,
            AssetId = request.AssetId,
            AgentName = "CTLWorkflowOrchestrator",
            StepType = "EvaluationStarted",
            Description = $"[Orchestrator · Deterministic] CTL evaluation initiated for asset {request.AssetId}"
        }, cancellationToken);

        // ── Build the workflow graph: Plan → Investigate → Reflect → Parse → QualityGate → HumanReview ──
        var planningPhase = new PlanningExecutor(
            _chatClient, _toolProvider, _auditService, _resilienceOptions, _logger);
        var investigationPhase = new InvestigationPhaseExecutor(
            _chatClient, _toolProvider, _auditService, _resilienceOptions, _logger);
        var reflectionPhase = new ReflectionExecutor(
            _chatClient, _toolProvider, _auditService, _resilienceOptions, _logger, _determinismOptions);
        var verdictParsingPhase = new VerdictParsingExecutor(_auditService, _logger, _verdictPolicyOptions.HumanReviewConfidenceThreshold, _determinismOptions);
        var qualityGatePhase = new QualityGateExecutor(
            _groundednessEvaluator, _auditService, _qualityGateOptions, _logger);
        var humanReviewPhase = new HumanReviewExecutor(
            _humanReviewService, _auditService, _logger);

        var workflow = new WorkflowBuilder(planningPhase)
            .AddEdge(planningPhase, investigationPhase)
            .AddEdge(investigationPhase, reflectionPhase)
            .AddEdge(reflectionPhase, verdictParsingPhase)
            .AddEdge(verdictParsingPhase, qualityGatePhase)
            .AddEdge(qualityGatePhase, humanReviewPhase)
            .WithOutputFrom(humanReviewPhase)
            .Build();

        // ── Execute the entire graph in one call ──
        var assetProfile = await _assetProfileProvider.GetAssetProfileAsync(request.AssetId, cancellationToken);
        var assetProfileJson = JsonSerializer.Serialize(assetProfile, JsonOptions);

        // Screen external asset data for indirect injection before injecting into prompts
        var assetScreenResult = await _contentSafetyGuard.ScreenToolResultAsync(assetProfileJson, cancellationToken);
        var isDegradedSafety = assetScreenResult.IsDegradedSafety;
        if (!assetScreenResult.IsAllowed)
        {
            _logger.LogWarning("Asset profile data blocked by content safety: {Reason}", assetScreenResult.Reason);
            throw new InvalidOperationException($"Asset profile data failed safety screening: {assetScreenResult.Reason}");
        }
        if (isDegradedSafety)
        {
            _logger.LogWarning("⚠ Safety degraded: {Reason}", assetScreenResult.Reason);
        }

        await _auditService.RecordStepAsync(new AuditEntry
        {
            SessionId = sessionId,
            AssetId = request.AssetId,
            AgentName = "CTLWorkflowOrchestrator",
            StepType = "AssetProfileSupplied",
            Description = $"[Orchestrator · Deterministic] Asset profile retrieved from data source and screened for content safety " +
                          $"({assetProfileJson.Length} chars). This profile JSON is supplied to the LLM as grounding context for planning and reflection phases.",
            OutputPayload = assetProfileJson
        }, cancellationToken);

        var input = new PlanRequest { AssetId = request.AssetId, SessionId = sessionId, AssetProfileJson = assetProfileJson };

        HumanReviewResult workflowOutput;
        try
        {
            var run = await InProcessExecution.RunAsync(workflow, input, sessionId: sessionId, cancellationToken: cancellationToken);

            workflowOutput = run.NewEvents
                .OfType<WorkflowOutputEvent>()
                .Select(e => e.Data)
                .OfType<HumanReviewResult>()
                .FirstOrDefault()
                ?? throw new InvalidOperationException(
                    "Workflow graph completed but produced no HumanReviewResult. " +
                    "A phase likely failed silently — check the audit trail for PhaseFailed entries.");
        }
        catch (Exception ex) when (ex is not InvalidOperationException || !ex.Message.Contains("produced no HumanReviewResult"))
        {
            _logger.LogError(ex, "Workflow graph execution failed at phase level");

            await _auditService.RecordStepAsync(new AuditEntry
            {
                SessionId = sessionId,
                AssetId = request.AssetId,
                AgentName = "CTLWorkflowOrchestrator",
                StepType = "EvaluationFailed",
                Description = $"[Orchestrator · Error] Workflow execution failed: {ex.GetType().Name} — {ex.Message}"
            }, cancellationToken);

            throw new InvalidOperationException(
                $"Workflow failed during execution — {ex.GetType().Name}: {ex.Message}. " +
                $"Check the audit trail (session {sessionId}) for the PhaseFailed entry identifying which phase crashed.", ex);
        }

        var verdict = workflowOutput.Verdict;
        var humanReview = workflowOutput.HumanReview;
        var duration = DateTime.UtcNow - startTime;

        await _auditService.RecordStepAsync(new AuditEntry
        {
            SessionId = sessionId,
            AssetId = request.AssetId,
            AgentName = "CTLWorkflowOrchestrator",
            StepType = "EvaluationCompleted",
            Description = BuildEvaluationCompletedDescription(verdict, humanReview),
            TokensUsed = _tokenBudgetGuard.CurrentUsage,
            Duration = duration,
            OutputPayload = workflowOutput.VerdictJson
        }, cancellationToken);

        _logger.LogInformation("═══════════════════════════════════════════════════════════════");
        _logger.LogInformation("CTL Evaluation Complete [WORKFLOW] — Verdict: {Verdict} Confidence: {Confidence:F2} Duration: {Duration}",
            verdict.Verdict, verdict.ConfidenceScore, duration);
        _logger.LogInformation("═══════════════════════════════════════════════════════════════");

        return new CTLEvaluationResult
        {
            Verdict = verdict,
            EvaluationDuration = duration,
            TotalTokensUsed = _tokenBudgetGuard.CurrentUsage,
            ToolInvocationCount = workflowOutput.TotalToolCalls,
            HumanReview = humanReview,
            IsDegradedSafety = isDegradedSafety
        };
    }

    /// <summary>
    /// Builds a descriptive summary for the EvaluationCompleted audit entry that explains
    /// the final confidence score, including how it got there if it was changed by
    /// human review or other post-processing steps.
    /// </summary>
    private static string BuildEvaluationCompletedDescription(CTLVerdictDto verdict, HumanReviewDecision? humanReview)
    {
        var desc = $"[Orchestrator · Deterministic] CTL evaluation completed: {verdict.Verdict} (confidence: {verdict.ConfidenceScore:F2}).";

        if (humanReview?.Action == HumanReviewAction.OverrideVerdict && humanReview.OverriddenConfidence.HasValue)
        {
            desc += $" Final confidence {verdict.ConfidenceScore:F2} was set by human reviewer " +
                    $"'{humanReview.ReviewedBy}' (overriding original LLM score). " +
                    $"Reason: {humanReview.ReviewerNotes}";
        }
        else if (humanReview?.Action == HumanReviewAction.Confirm)
        {
            desc += $" Verdict confirmed by human reviewer '{humanReview.ReviewedBy}'. " +
                    $"Confidence {verdict.ConfidenceScore:F2} is unchanged from LLM reflection.";
        }
        else
        {
            desc += $" No human review was needed — confidence {verdict.ConfidenceScore:F2} is from LLM reflection (non-deterministic).";
        }

        return desc;
    }
}
