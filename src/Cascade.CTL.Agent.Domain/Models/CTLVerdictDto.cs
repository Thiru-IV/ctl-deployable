using Cascade.CTL.Agent.Domain.Enums;

namespace Cascade.CTL.Agent.Domain.Models;

public sealed record CTLVerdictDto
{
    public required CTLVerdict Verdict { get; init; }
    public required double ConfidenceScore { get; init; }
    public required string[] Conditions { get; init; }
    public required string[] EvidenceTrail { get; init; }
    public required string ReflectionLog { get; init; }
    public required string AssetId { get; init; }
    public required DateTime Timestamp { get; init; }
    public required string SessionId { get; init; }
    public LegalFindingsReport? LegalFindings { get; init; }
    public ValuationFindingsReport? ValuationFindings { get; init; }
    public OccupancyFindingsReport? OccupancyFindings { get; init; }
    public CitationEntry[]? Citations { get; init; }

    // ── Reflection determinism v2 audit fields (Phase 1) ──
    // Populated when the LLM Reflection call runs under sampling lockdown + discrete buckets.
    // All optional/nullable so existing producers and consumers remain unaffected.

    /// <summary>Verdict string returned by the LLM before any deterministic post-processing.</summary>
    public string? LlmRawVerdict { get; init; }

    /// <summary>Confidence value returned by the LLM before bucket-snapping (if any).</summary>
    public double? LlmRawConfidence { get; init; }

    /// <summary>Provider model fingerprint (e.g. Azure OpenAI <c>system_fingerprint</c>) when exposed.</summary>
    public string? ModelFingerprint { get; init; }
}

public sealed record CitationEntry
{
    public required string Source { get; init; }
    public string? Reference { get; init; }
    public string? Excerpt { get; init; }
}

public sealed record CTLEvaluationRequest
{
    public required string AssetId { get; init; }
    public string? WorkflowInstanceId { get; init; }
    public DateTime RequestTimestamp { get; init; } = DateTime.UtcNow;
    public string? RequestedBy { get; init; }
}

public sealed record CTLEvaluationResult
{
    public required CTLVerdictDto Verdict { get; init; }
    public required TimeSpan EvaluationDuration { get; init; }
    public required int TotalTokensUsed { get; init; }
    public required int ToolInvocationCount { get; init; }

    /// <summary>Non-null when a human reviewer was consulted (HITL).</summary>
    public HumanReviewDecision? HumanReview { get; init; }

    /// <summary>
    /// When true, Azure ML-grade content safety was unavailable during this evaluation.
    /// Safety screening fell back to local regex detection only — a degraded safety mode.
    /// This should be surfaced in audit trails and evaluation reports.
    /// </summary>
    public bool IsDegradedSafety { get; init; }
}
