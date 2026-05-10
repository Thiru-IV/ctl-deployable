using Cascade.CTL.Agent.Domain.Contracts;
using Cascade.CTL.Agent.Domain.Enums;
using Cascade.CTL.Agent.Domain.Models;
using Microsoft.Extensions.Logging;

namespace Cascade.CTL.Agent.Infrastructure.Providers.Mock;

/// <summary>
/// Mock Human-in-the-Loop service for development and testing.
/// Simulates a human reviewer who inspects the agent's verdict and makes a decision
/// based on the confidence score — demonstrating the HITL pause/decide/resume pattern.
/// </summary>
public sealed class MockHumanReviewService : IHumanReviewService
{
    private readonly ILogger<MockHumanReviewService> _logger;

    public MockHumanReviewService(ILogger<MockHumanReviewService> logger)
    {
        _logger = logger;
    }

    public Task<HumanReviewDecision> RequestReviewAsync(
        HumanReviewRequest request,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "[HITL] Human review requested — Asset: {AssetId}, Confidence: {Confidence:F2}, Session: {SessionId}",
            request.AssetId, request.ProposedVerdict.ConfidenceScore, request.SessionId);

        // Simulate reviewer decision logic based on confidence and conditions:
        // - Confidence 0.60–0.74: close enough — reviewer overrides to ClearWithConditions
        // - Confidence < 0.60: too risky — reviewer confirms NeedsHumanReview
        var decision = request.ProposedVerdict.ConfidenceScore >= 0.60
            ? new HumanReviewDecision
            {
                Action = HumanReviewAction.OverrideVerdict,
                OverriddenVerdict = CTLVerdict.ClearWithConditions,
                OverriddenConfidence = 0.78,
                ReviewerNotes = $"Confidence {request.ProposedVerdict.ConfidenceScore:F2} is borderline. " +
                                "After manual review of evidence trail, asset cleared with conditions.",
                ReviewedBy = "mock-reviewer@cascade.com"
            }
            : new HumanReviewDecision
            {
                Action = HumanReviewAction.Confirm,
                ReviewerNotes = $"Confidence {request.ProposedVerdict.ConfidenceScore:F2} is too low. " +
                                "Confirmed NeedsHumanReview — field inspection required before proceeding.",
                ReviewedBy = "mock-reviewer@cascade.com"
            };

        _logger.LogInformation(
            "[HITL] Reviewer decision: {Action} — {Notes}",
            decision.Action, decision.ReviewerNotes);

        return Task.FromResult(decision);
    }
}
