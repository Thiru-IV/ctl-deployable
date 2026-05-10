using Cascade.CTL.Agent.Domain.Contracts;
using Cascade.CTL.Agent.Domain.Enums;
using Cascade.CTL.Agent.Domain.Models;
using Microsoft.Extensions.Logging;

namespace Cascade.CTL.Agent.Infrastructure.Providers.Mock;

public sealed class MockAssetProfileProvider : IAssetProfileProvider
{
    private readonly ILogger<MockAssetProfileProvider> _logger;
    private static readonly Dictionary<string, Asset> _assets = new()
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
        }
    };

    public MockAssetProfileProvider(ILogger<MockAssetProfileProvider> logger)
    {
        _logger = logger;
    }

    public Task<Asset> GetAssetProfileAsync(string assetId, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("MockAssetProfileProvider: Retrieving asset profile for {AssetId}", assetId);

        if (_assets.TryGetValue(assetId, out var asset))
        {
            return Task.FromResult(asset);
        }

        throw new KeyNotFoundException($"Asset '{assetId}' not found. Available: {string.Join(", ", _assets.Keys)}");
    }
}
