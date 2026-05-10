using Cascade.CTL.Agent.Domain.Contracts;
using Cascade.CTL.Agent.Domain.Enums;
using Cascade.CTL.Agent.Domain.Models;
using Microsoft.Extensions.Logging;

namespace Cascade.CTL.Agent.Api;

/// <summary>
/// Non-interactive HITL implementation for the deployed HTTP service.
/// When the orchestrator escalates to human review, we keep the verdict as
/// <see cref="HumanReviewAction.Confirm"/> (i.e. "NeedsHumanReview") rather
/// than blocking on console input. The caller (Foundry agent / external client)
/// receives the NeedsHumanReview verdict in the response and can route the
/// asset into a real review queue out-of-band.
/// </summary>
public sealed class AutoApproveHumanReviewService : IHumanReviewService
{
    private readonly ILogger<AutoApproveHumanReviewService> _logger;

    public AutoApproveHumanReviewService(ILogger<AutoApproveHumanReviewService> logger)
    {
        _logger = logger;
    }

    public Task<HumanReviewDecision> RequestReviewAsync(
        HumanReviewRequest request,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "[HITL-Service] Auto-confirming NeedsHumanReview verdict for Asset={AssetId} Session={SessionId} Confidence={Confidence:F2}. " +
            "Caller is responsible for routing to a real review queue.",
            request.AssetId, request.SessionId, request.ProposedVerdict.ConfidenceScore);

        var decision = new HumanReviewDecision
        {
            Action = HumanReviewAction.Confirm,
            ReviewerNotes = $"Auto-confirmed by Agent.Api (service mode). Confidence {request.ProposedVerdict.ConfidenceScore:F2} below threshold; " +
                            "verdict returned to caller as NeedsHumanReview for downstream routing.",
            ReviewedBy = "agent.api.auto"
        };
        return Task.FromResult(decision);
    }
}
