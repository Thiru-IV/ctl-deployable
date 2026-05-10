using Cascade.CTL.Agent.Domain.Enums;
using Cascade.CTL.Agent.Domain.Models;

namespace Cascade.CTL.AssetService;

/// <summary>
/// In-memory asset catalog used by the Asset Domain service.
/// In production this would be backed by a database / system-of-record.
/// The seed data intentionally mirrors the identifiers exercised by integration tests and demos.
/// </summary>
public interface IAssetRepository
{
    Asset? Find(string assetId);
    IReadOnlyCollection<string> KnownAssetIds { get; }
}

public sealed class InMemoryAssetRepository : IAssetRepository
{
    private static readonly Dictionary<string, Asset> _assets = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ASSET-TX-001"] = new Asset
        {
            AssetId = "ASSET-TX-001",
            AssetType = AssetType.Foreclosure,
            StateCode = "TX",
            County = "Dallas",
            SellerTier = SellerTier.Tier1,
            OccupancyStatus = OccupancyStatus.Vacant,
            ParcelId = "TX-DAL-123456",
            PropertyAddress = "1234 Oak Street, Dallas, TX 75201",
            SellerName = "First National Bank",
            IngestionDate = DateTime.UtcNow.AddDays(-5)
        },
        ["ASSET-CA-002"] = new Asset
        {
            AssetId = "ASSET-CA-002",
            AssetType = AssetType.REO,
            StateCode = "CA",
            County = "Los Angeles",
            SellerTier = SellerTier.Tier2,
            OccupancyStatus = OccupancyStatus.Occupied,
            ParcelId = "CA-LA-789012",
            PropertyAddress = "5678 Sunset Blvd, Los Angeles, CA 90028",
            SellerName = "Pacific Mortgage Corp",
            IngestionDate = DateTime.UtcNow.AddDays(-10)
        },
        ["ASSET-FL-003"] = new Asset
        {
            AssetId = "ASSET-FL-003",
            AssetType = AssetType.NonForeclosure,
            StateCode = "FL",
            County = "Miami-Dade",
            SellerTier = SellerTier.Tier3,
            OccupancyStatus = OccupancyStatus.Unknown,
            ParcelId = "FL-MD-345678",
            PropertyAddress = "910 Palm Ave, Miami, FL 33101",
            SellerName = "Southeast Financial",
            IngestionDate = DateTime.UtcNow.AddDays(-2)
        },
        ["ASSET-NY-004"] = new Asset
        {
            AssetId = "ASSET-NY-004",
            AssetType = AssetType.REO,
            StateCode = "NY",
            County = "Westchester",
            SellerTier = SellerTier.Tier2,
            OccupancyStatus = OccupancyStatus.Vacant,
            ParcelId = "NY-WC-567890",
            PropertyAddress = "42 Maple Drive, White Plains, NY 10601",
            SellerName = "Empire State Mortgage",
            IngestionDate = DateTime.UtcNow.AddDays(-1)
        }
    };

    public Asset? Find(string assetId) =>
        _assets.TryGetValue(assetId, out var a) ? a : null;

    public IReadOnlyCollection<string> KnownAssetIds => _assets.Keys;
}
