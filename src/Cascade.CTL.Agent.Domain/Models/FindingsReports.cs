using Cascade.CTL.Agent.Domain.Enums;

namespace Cascade.CTL.Agent.Domain.Models;

public sealed record LegalFindingsReport
{
    public required CTLVerdict DomainVerdict { get; init; }
    public required double Confidence { get; init; }
    public required string[] Findings { get; init; }
    public required string[] UnverifiedFields { get; init; }
    public required string Summary { get; init; }
    public TitleSearchResult? TitleResult { get; init; }
    public HOAResult? HOAResult { get; init; }
    public CodeViolationResult? CodeViolationResult { get; init; }
}

public sealed record ValuationFindingsReport
{
    public required CTLVerdict DomainVerdict { get; init; }
    public required double Confidence { get; init; }
    public required string[] Findings { get; init; }
    public required string[] UnverifiedFields { get; init; }
    public required string Summary { get; init; }
    public BPOResult? BPOResult { get; init; }
    public AVMResult? AVMResult { get; init; }
}

public sealed record OccupancyFindingsReport
{
    public required CTLVerdict DomainVerdict { get; init; }
    public required double Confidence { get; init; }
    public required string[] Findings { get; init; }
    public required string[] UnverifiedFields { get; init; }
    public required string Summary { get; init; }
    public OccupancyStatusResult? OccupancyResult { get; init; }
}
