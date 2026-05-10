namespace Cascade.CTL.Agent.Guardrails;

public sealed record GuardResult
{
    public required bool IsAllowed { get; init; }
    public required string Action { get; init; }
    public string? Reason { get; init; }
    public string[]? DetectedPatterns { get; init; }

    /// <summary>
    /// When true, indicates that ML-grade safety screening was not applied
    /// (Azure service unavailable, circuit breaker open, or timeout).
    /// The result passed using local regex detection only — a degraded safety mode.
    /// </summary>
    public bool IsDegradedSafety { get; init; }

    public static GuardResult Pass() => new() { IsAllowed = true, Action = "Pass" };

    public static GuardResult PassDegraded(string reason) =>
        new() { IsAllowed = true, Action = "Pass", Reason = reason, IsDegradedSafety = true };

    public static GuardResult Block(string reason, string[]? patterns = null) =>
        new() { IsAllowed = false, Action = "Block", Reason = reason, DetectedPatterns = patterns };
    public static GuardResult Flag(string reason, string[]? patterns = null) =>
        new() { IsAllowed = true, Action = "Flag", Reason = reason, DetectedPatterns = patterns };
}
