using Cascade.CTL.Agent.Domain.Models;

namespace Cascade.CTL.Agent.Domain.Contracts;

/// <summary>
/// Human-in-the-Loop service that pauses agent execution and waits for
/// a human reviewer's decision before finalizing a NeedsHumanReview verdict.
/// </summary>
public interface IHumanReviewService
{
    /// <summary>
    /// Sends a review request to a human and awaits their decision.
    /// Implementations may queue the request, call an external approval API,
    /// or (in mock/test mode) return an immediate simulated decision.
    /// </summary>
    Task<HumanReviewDecision> RequestReviewAsync(
        HumanReviewRequest request,
        CancellationToken cancellationToken = default);
}
