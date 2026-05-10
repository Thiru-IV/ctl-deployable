using Cascade.CTL.Agent.Domain.Contracts;
using Cascade.CTL.Agent.Domain.Models;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using System.Runtime.CompilerServices;

namespace Cascade.CTL.Agent.Guardrails;

public sealed class GuardrailsMiddleware : DelegatingChatClient
{
    private readonly ContentSafetyGuard _contentSafetyGuard;
    private readonly TokenBudgetGuard _tokenBudgetGuard;
    private readonly PiiFilter _piiFilter;
    private readonly IAuditService _auditService;
    private readonly ILogger<GuardrailsMiddleware> _logger;

    public GuardrailsMiddleware(
        IChatClient innerClient,
        ContentSafetyGuard contentSafetyGuard,
        TokenBudgetGuard tokenBudgetGuard,
        PiiFilter piiFilter,
        IAuditService auditService,
        ILogger<GuardrailsMiddleware> logger) : base(innerClient)
    {
        _contentSafetyGuard = contentSafetyGuard;
        _tokenBudgetGuard = tokenBudgetGuard;
        _piiFilter = piiFilter;
        _auditService = auditService;
        _logger = logger;
    }

    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        if (!_tokenBudgetGuard.IsWithinBudget)
        {
            _logger.LogWarning("Token budget exceeded — blocking LLM call");
            await RecordGuardrailAuditAsync("TokenBudgetExceeded",
                "[Guardrail · Deterministic] Token budget exceeded — LLM call blocked. Evaluation must be escalated for human review.",
                cancellationToken);
            var budgetMsg = new ChatMessage(ChatRole.Assistant,
                "Token budget exceeded. This CTL evaluation must be escalated for human review.");
            return new ChatResponse([budgetMsg]);
        }

        // Record that token budget check passed
        var remaining = _tokenBudgetGuard.Budget - _tokenBudgetGuard.CurrentUsage;
        await RecordGuardrailAuditAsync("TokenBudgetChecked",
            $"[Guardrail · Deterministic] Token budget check passed — {_tokenBudgetGuard.CurrentUsage:N0} of {_tokenBudgetGuard.Budget:N0} tokens consumed ({remaining:N0} remaining)",
            cancellationToken);

        var screened = new List<ChatMessage>();
        foreach (var message in messages)
        {
            if (message.Role == ChatRole.User || message.Role == ChatRole.Tool)
            {
                var text = message.Text;
                if (!string.IsNullOrEmpty(text))
                {
                    // Tool messages use indirect injection screening (documents parameter);
                    // User messages use direct injection screening (userPrompt parameter)
                    var guardResult = message.Role == ChatRole.Tool
                        ? await _contentSafetyGuard.ScreenToolResultAsync(text, cancellationToken)//indirect injection screening
                        : await _contentSafetyGuard.ScreenInputAsync(text, cancellationToken); //direct injection screening

                    if (!guardResult.IsAllowed)
                    {
                        _logger.LogWarning("Content safety blocked {Role} input: {Reason}", message.Role, guardResult.Reason);
                        await RecordGuardrailAuditAsync("ContentSafetyBlocked",
                            $"[Guardrail · Azure AI Content Safety] Blocked {message.Role} message: {guardResult.Reason}",
                            cancellationToken);
                        var blockedMsg = new ChatMessage(ChatRole.Assistant,
                            $"Input blocked by content safety: {guardResult.Reason}. This evaluation requires human review.");
                        return new ChatResponse([blockedMsg]);
                    }

                    if (guardResult.IsDegradedSafety)
                    {
                        await RecordGuardrailAuditAsync("SafetyDegraded",
                            $"[Guardrail · Degraded] Azure Content Safety unavailable — using local regex detection only. Reason: {guardResult.Reason}",
                            cancellationToken);
                    }
                    else if (guardResult.Action == "Flag")
                    {
                        await RecordGuardrailAuditAsync("ContentSafetyFlagged",
                            $"[Guardrail · Azure AI Content Safety] Flagged (allowed but noted): {guardResult.Reason}",
                            cancellationToken);
                    }
                    else
                    {
                        // Record that content safety screening passed cleanly
                        await RecordGuardrailAuditAsync("ContentSafetyPassed",
                            $"[Guardrail · Azure AI Content Safety] Screening passed for {message.Role} message ({text.Length} chars)",
                            cancellationToken);
                    }

                    // BUG FIX: Skip PII masking during QualityGate phase.
                    // The QualityGate LLM-as-Judge call is an internal LLM-to-LLM evaluation — no user data
                    // leaves the system. PII masking replaces organization names (e.g., "Cascade", "Xome") with
                    // "[Organization]" placeholders, which corrupts the evidence the judge needs to score
                    // groundedness accurately. This caused systematic groundedness failures (2/5) and
                    // unnecessary escalations to human review. Content Safety screening still runs.
                    // See: audit logs showing "[Organization] policy" in QG reasoning.
                    if (IsQualityGatePhase())
                    {
                        await RecordGuardrailAuditAsync("PiiSkipped",
                            $"[Guardrail · PII Filter] [Pre-LLM] PII masking skipped for QualityGate phase — " +
                            $"internal LLM-as-Judge call does not expose data externally. Content Safety screening still applied.",
                            cancellationToken);
                    }
                    else
                    {
                        // Mask PII before sending to LLM (Tier 1 regex + Tier 2 Azure AI Language)
                        var masked = await _piiFilter.MaskPiiAsync(text, cancellationToken);
                        if (masked != text)
                        {
                            await RecordGuardrailAuditAsync("PiiMasked",
                                $"[Guardrail · PII Filter] [Pre-LLM] PII detected and masked in {message.Role} message ({text.Length} chars → {masked.Length} chars)",
                                cancellationToken);
                            screened.Add(new ChatMessage(message.Role, masked));
                            continue;
                        }
                        else
                        {
                            await RecordGuardrailAuditAsync("PiiScreened",
                                $"[Guardrail · PII Filter] [Pre-LLM] PII screening completed for {message.Role} message ({text.Length} chars) — no PII detected",
                                cancellationToken);
                        }
                    }
                }
            }
            screened.Add(message);
        }

        //actual LLM invocation line
        var response = await base.GetResponseAsync(screened, options, cancellationToken);

        // Record that the LLM call completed so the audit shows the boundary between Pre-LLM and Post-LLM
        var responseMessageCount = response.Messages?.Count(m => !string.IsNullOrEmpty(m.Text)) ?? 0;
        var tokenInfo = response.Usage?.TotalTokenCount is long t and > 0 ? $", {t:N0} tokens used" : "";
        await RecordGuardrailAuditAsync("LlmCallCompleted",
            $"[LLM · Non-Deterministic] LLM call completed — returned {responseMessageCount} message(s){tokenInfo}. " +
            $"Output will now be screened for PII before being returned to the orchestrator.",
            cancellationToken);

        // Screen LLM output for PII before returning (Tier 1 regex + Tier 2 Azure AI Language)
        // Note: Content Safety screening is NOT applied to LLM outputs — by design it only screens
        // inputs to detect prompt injection attacks. PII filter runs on both inputs AND outputs to
        // catch hallucinated PII, memorized PII, or echoed system-prompt data.
        if (response.Messages != null)
        {
            var totalMessages = response.Messages.Count(m => !string.IsNullOrEmpty(m.Text));
            var messageIndex = 0;

            foreach (var msg in response.Messages)
            {
                var text = msg.Text;
                if (!string.IsNullOrEmpty(text))
                {
                    messageIndex++;
                    var msgLabel = totalMessages > 1 ? $" (message {messageIndex} of {totalMessages})" : "";

                    // BUG FIX: Skip PII masking on QualityGate and Reflection phase outputs.
                    // QualityGate: the judge's reasoning text must not be corrupted before flowing into
                    // audit logs and downstream verdict-escalation logic.
                    // Reflection: the LLM's structured JSON verdict must not be corrupted before it reaches
                    // VerdictParser — Azure AI Language PII detection replaces domain terms (e.g., "Legal")
                    // with "[Organization]" placeholders, which can break JSON parsing and cause
                    // systematic fallbacks to NeedsHumanReview with 0.00 confidence.
                    if (IsInternalPhase())
                    {
                        await RecordGuardrailAuditAsync("PiiSkipped",
                            $"[Guardrail · PII Filter] [Post-LLM] PII masking skipped for {GuardrailsContext.CurrentPhase} phase — " +
                            $"internal output stays within the system and must not be corrupted.",
                            cancellationToken);
                    }
                    else
                    {
                        //Hallucinated PII, memorized PII, echoed system-prompt data, names/addresses missed by input regex
                        var masked = await _piiFilter.MaskPiiAsync(text, cancellationToken);
                        if (masked != text)
                        {
                            await RecordGuardrailAuditAsync("PiiMasked",
                                $"[Guardrail · PII Filter] [Post-LLM] PII detected and masked in LLM output{msgLabel} ({text.Length} chars → {masked.Length} chars). " +
                                $"Note: only PII screening runs on outputs — Content Safety is input-only (prompt injection detection).",
                                cancellationToken);
                            msg.Contents.Clear();
                            msg.Contents.Add(new TextContent(masked));
                        }
                        else
                        {
                            await RecordGuardrailAuditAsync("PiiScreened",
                                $"[Guardrail · PII Filter] [Post-LLM] PII screening completed for LLM output{msgLabel} ({text.Length} chars) — no PII detected. " +
                                $"Note: only PII screening runs on outputs — Content Safety is input-only (prompt injection detection).",
                                cancellationToken);
                        }
                    }
                }
            }
        }

        if (response.Usage?.TotalTokenCount is long totalTokens and > 0)
        {
            _tokenBudgetGuard.TryConsumeTokens((int)Math.Min(totalTokens, int.MaxValue));
        }

        return response;
    }

    /// <summary>
    /// Returns true when the current ambient phase is QualityGate.
    /// Used to skip PII masking on internal LLM-as-Judge calls where masking corrupts evidence.
    /// </summary>
    private static bool IsQualityGatePhase() =>
        string.Equals(GuardrailsContext.CurrentPhase, "QualityGate", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Returns true for phases whose LLM output is consumed internally (by code or by another LLM)
    /// and must not be corrupted by PII masking. Used to skip post-LLM PII masking only.
    /// Pre-LLM input masking still applies to Reflection (to avoid sending PII to the LLM).
    /// </summary>
    private static bool IsInternalPhase() =>
        IsQualityGatePhase() ||
        string.Equals(GuardrailsContext.CurrentPhase, "Reflection", StringComparison.OrdinalIgnoreCase);

    private async Task RecordGuardrailAuditAsync(string stepType, string description, CancellationToken cancellationToken)
    {
        var sessionId = TokenBudgetGuard.CurrentSessionId ?? "unknown";
        var phase = GuardrailsContext.CurrentPhase;
        var agentName = string.IsNullOrEmpty(phase)
            ? "GuardrailsMiddleware"
            : $"GuardrailsMiddleware [{phase}]";

        await _auditService.RecordStepAsync(new AuditEntry
        {
            SessionId = sessionId,
            AssetId = "—",
            AgentName = agentName,
            StepType = stepType,
            Description = description
        }, cancellationToken);
    }
}
