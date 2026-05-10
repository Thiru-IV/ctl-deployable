using Cascade.CTL.Agent.Domain.Enums;

namespace Cascade.CTL.Agent.Domain.Models;

/// <summary>
/// Request sent to a human reviewer when the agent produces a NeedsHumanReview verdict.
/// Contains all evidence the reviewer needs to make a decision.
/// </summary>
public sealed record HumanReviewRequest
{
    public required string SessionId { get; init; }
    public required string AssetId { get; init; }
    public required CTLVerdictDto ProposedVerdict { get; init; }
    public required string ReflectionOutput { get; init; }
    public DateTime RequestedAt { get; init; } = DateTime.UtcNow;
}

/// <summary>
/// Decision returned by a human reviewer after inspecting the agent's escalated verdict.
/// </summary>
public sealed record HumanReviewDecision
{
    public required HumanReviewAction Action { get; init; }

    /// <summary>
    /// When Action is OverrideVerdict, the reviewer's chosen verdict.
    /// Null when Action is Confirm or RequestReEvaluation.
    /// </summary>
    public CTLVerdict? OverriddenVerdict { get; init; }

    /// <summary>
    /// When Action is OverrideVerdict, the reviewer's adjusted confidence score.
    /// Null when Action is Confirm or RequestReEvaluation.
    /// </summary>
    public double? OverriddenConfidence { get; init; }

    /// <summary>
    /// Reviewer's notes explaining the decision (required for audit trail).
    /// </summary>
    public required string ReviewerNotes { get; init; }

    /// <summary>Identifier of the human reviewer.</summary>
    public required string ReviewedBy { get; init; }

    public DateTime ReviewedAt { get; init; } = DateTime.UtcNow;
}
