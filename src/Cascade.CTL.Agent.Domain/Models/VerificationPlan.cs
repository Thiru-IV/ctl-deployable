using Cascade.CTL.Agent.Domain.Enums;

namespace Cascade.CTL.Agent.Domain.Models;

public sealed record VerificationPlan
{
    public required string AssetId { get; init; }
    public required VerificationDomain[] RequiredDomains { get; init; }
    public required string[] RelevantPolicies { get; init; }
    public required string AssetProfileSummary { get; init; }
    public required string PlanRationale { get; init; }
}
