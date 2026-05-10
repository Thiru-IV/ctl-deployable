namespace Cascade.CTL.Agent.Domain.Models;

public sealed record TitleSearchResult
{
    public required string ParcelId { get; init; }
    public required string StateCode { get; init; }
    public required bool HasClearTitle { get; init; }
    public required string[] OpenLiens { get; init; }
    public required string[] Encumbrances { get; init; }
    public required string[] TitleDefects { get; init; }
    public bool HasHOAFlag { get; init; }
    public DateTime SearchDate { get; init; } = DateTime.UtcNow;
    public required string ProviderReference { get; init; }
}

public sealed record HOAResult
{
    public required string PropertyAddress { get; init; }
    public required bool HasHOA { get; init; }
    public required bool IsDelinquent { get; init; }
    public decimal? DelinquentAmount { get; init; }
    public string? HOAName { get; init; }
    public DateTime? LastPaymentDate { get; init; }
    public required string Status { get; init; }
}

public sealed record CodeViolationResult
{
    public required string PropertyAddress { get; init; }
    public required string County { get; init; }
    public required bool HasOpenViolations { get; init; }
    public required CodeViolation[] Violations { get; init; }
}

public sealed record CodeViolation
{
    public required string ViolationType { get; init; }
    public required string Description { get; init; }
    public required string Severity { get; init; }
    public required DateTime DateIssued { get; init; }
    public string? CaseNumber { get; init; }
}

public sealed record BPOResult
{
    public required string AssetId { get; init; }
    public required bool HasBPO { get; init; }
    public decimal? EstimatedValue { get; init; }
    public DateTime? BPODate { get; init; }
    public required string QualityRating { get; init; }
    public bool IsStale { get; init; }
    public int? DaysSinceBPO { get; init; }
    public string? BPOVendor { get; init; }
    public required string ConditionRating { get; init; }
}

public sealed record AVMResult
{
    public required string PropertyAddress { get; init; }
    public required string StateCode { get; init; }
    public required bool HasAVM { get; init; }
    public decimal? EstimatedValue { get; init; }
    public double? ConfidenceScore { get; init; }
    public decimal? VarianceFromBPO { get; init; }
    public double? VariancePercentage { get; init; }
    public DateTime? ValuationDate { get; init; }
    public string? AVMProvider { get; init; }
}

public sealed record OccupancyStatusResult
{
    public required string PropertyAddress { get; init; }
    public required string OccupancyStatus { get; init; }
    public required bool IsVacant { get; init; }
    public DateTime? LastInspectionDate { get; init; }
    public string? InspectionVendor { get; init; }
    public bool HasEvictionInProgress { get; init; }
    public string? PropertyCondition { get; init; }
    public string[] Notes { get; init; } = [];
}

public sealed record RAGQueryResult
{
    public required string Query { get; init; }
    public required RAGDocument[] Documents { get; init; }
    public required int TotalMatches { get; init; }
}

public sealed record RAGDocument
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public required string Content { get; init; }
    public required double RelevanceScore { get; init; }
    public string? State { get; init; }
    public string? County { get; init; }
    public string? AssetType { get; init; }
    public string? PolicyType { get; init; }
}
