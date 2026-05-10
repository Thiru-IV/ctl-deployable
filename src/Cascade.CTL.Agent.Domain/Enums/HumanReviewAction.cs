namespace Cascade.CTL.Agent.Domain.Enums;

/// <summary>
/// Action taken by a human reviewer when the agent escalates a verdict.
/// </summary>
public enum HumanReviewAction
{
    /// <summary>Human confirms the agent's NeedsHumanReview verdict as-is.</summary>
    Confirm,

    /// <summary>Human overrides the verdict (e.g., changes NeedsHumanReview → ClearWithConditions).</summary>
    OverrideVerdict,

    /// <summary>Human requests the agent to re-evaluate with additional guidance.</summary>
    RequestReEvaluation
}
