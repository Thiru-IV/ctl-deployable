using Cascade.CTL.Agent.Domain.Enums;
using Cascade.CTL.Agent.Domain.Models;

namespace Cascade.CTL.Agent.Application.Orchestration.Workflow;

/// <summary>Typed messages flowing between workflow graph nodes via edges.</summary>

/// <summary>Initial input to the workflow graph → PlanningExecutor.</summary>
public sealed class PlanRequest
{
    public required string AssetId { get; init; }
    public required string SessionId { get; init; }
    public required string AssetProfileJson { get; init; }
}

/// <summary>PlanningExecutor output → edge → InvestigationPhaseExecutor input.</summary>
public sealed class PlanResult
{
    public required string PlanJson { get; init; }
    public required string AssetProfileJson { get; init; }
    public required HashSet<VerificationDomain> RequiredDomains { get; init; }
    public required int ToolCalls { get; init; }
    public required string AssetId { get; init; }
    public required string SessionId { get; init; }
}

/// <summary>InvestigationPhaseExecutor output → edge → ReflectionExecutor input.</summary>
public sealed class InvestigationPhaseResult
{
    public required string PlanJson { get; init; }
    public required string AssetProfileJson { get; init; }
    public required HashSet<VerificationDomain> RequiredDomains { get; init; }
    public required string LegalFindings { get; init; }
    public required string ValuationFindings { get; init; }
    public required string OccupancyFindings { get; init; }
    public required int TotalToolCalls { get; init; }
    public required string AssetId { get; init; }
    public required string SessionId { get; init; }
}

/// <summary>ReflectionExecutor output → edge → VerdictParsingExecutor input.</summary>
public sealed class ReflectionResult
{
    public required string VerdictJson { get; init; }
    public required string InvestigationFindings { get; init; }
    public required int ToolCalls { get; init; }
    public required int TotalToolCalls { get; init; }
    public required string AssetId { get; init; }
    public required string SessionId { get; init; }
}

/// <summary>VerdictParsingExecutor output → edge → QualityGateExecutor input.</summary>
public sealed class VerdictParsingResult
{
    public required CTLVerdictDto Verdict { get; init; }
    public required string InvestigationFindings { get; init; }
    public required string VerdictJson { get; init; }
    public required int TotalToolCalls { get; init; }
    public required string AssetId { get; init; }
    public required string SessionId { get; init; }
}

/// <summary>QualityGateExecutor output → edge → HumanReviewExecutor input.</summary>
public sealed class QualityGateResult
{
    public required CTLVerdictDto Verdict { get; init; }
    public required string VerdictJson { get; init; }
    public required int TotalToolCalls { get; init; }
    public required string AssetId { get; init; }
    public required string SessionId { get; init; }
}

/// <summary>HumanReviewExecutor output → workflow output event.</summary>
public sealed class HumanReviewResult
{
    public required CTLVerdictDto Verdict { get; init; }
    public required HumanReviewDecision? HumanReview { get; init; }
    public required string VerdictJson { get; init; }
    public required int TotalToolCalls { get; init; }
    public required string AssetId { get; init; }
    public required string SessionId { get; init; }
}
