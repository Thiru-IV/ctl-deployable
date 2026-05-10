using Cascade.CTL.Agent.Domain.Enums;

namespace Cascade.CTL.Agent.Domain.Models;

public sealed record Asset
{
    public required string AssetId { get; init; }
    public required AssetType AssetType { get; init; }
    public required string StateCode { get; init; }
    public required string County { get; init; }
    public required SellerTier SellerTier { get; init; }
    public required OccupancyStatus OccupancyStatus { get; init; }
    public required string ParcelId { get; init; }
    public required string PropertyAddress { get; init; }
    public string? SellerName { get; init; }
    public DateTime? IngestionDate { get; init; }
}
