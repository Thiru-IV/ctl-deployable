using Cascade.CTL.Agent.Domain.Contracts;
using Cascade.CTL.Agent.Domain.Models;
using Microsoft.Extensions.Logging;

namespace Cascade.CTL.Agent.Infrastructure.Providers.Mock;

public sealed class MockBPOProvider : IBPOProvider
{
    private readonly ILogger<MockBPOProvider> _logger;

    public MockBPOProvider(ILogger<MockBPOProvider> logger)
    {
        _logger = logger;
    }

    public Task<BPOResult> RetrieveAsync(string assetId, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("MockBPOProvider: Retrieving BPO for asset {AssetId}", assetId);

        var result = assetId switch
        {
            "ASSET-TX-001" => new BPOResult
            {
                AssetId = assetId,
                HasBPO = true,
                EstimatedValue = 285000m,
                BPODate = DateTime.UtcNow.AddDays(-15),
                QualityRating = "High",
                IsStale = false,
                DaysSinceBPO = 15,
                BPOVendor = "Clear Capital",
                ConditionRating = "Good"
            },
            "ASSET-CA-002" => new BPOResult
            {
                AssetId = assetId,
                HasBPO = true,
                EstimatedValue = 725000m,
                BPODate = DateTime.UtcNow.AddDays(-120),
                QualityRating = "Medium",
                IsStale = true,
                DaysSinceBPO = 120,
                BPOVendor = "CoreLogic",
                ConditionRating = "Fair"
            },
            "ASSET-FL-003" => new BPOResult
            {
                AssetId = assetId,
                HasBPO = false,
                QualityRating = "None",
                ConditionRating = "Unknown"
            },
            _ => new BPOResult
            {
                AssetId = assetId,
                HasBPO = false,
                QualityRating = "None",
                ConditionRating = "Unknown"
            }
        };

        return Task.FromResult(result);
    }
}
