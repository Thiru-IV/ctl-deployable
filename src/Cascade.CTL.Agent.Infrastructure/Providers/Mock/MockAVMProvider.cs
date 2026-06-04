using Cascade.CTL.Agent.Domain.Contracts;
using Cascade.CTL.Agent.Domain.Models;
using Microsoft.Extensions.Logging;

namespace Cascade.CTL.Agent.Infrastructure.Providers.Mock;

public sealed class MockAVMProvider : IAVMProvider
{
    private readonly ILogger<MockAVMProvider> _logger;

    public MockAVMProvider(ILogger<MockAVMProvider> logger)
    {
        _logger = logger;
    }

    public Task<AVMResult> GetValuationAsync(string propertyAddress, string stateCode, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("MockAVMProvider: Getting AVM for {Address} in {StateCode}", propertyAddress, stateCode);

        var result = stateCode switch
        {
            "TX" => new AVMResult
            {
                PropertyAddress = propertyAddress,
                StateCode = stateCode,
                HasAVM = true,
                EstimatedValue = 290000m,
                ConfidenceScore = 0.92,
                VarianceFromBPO = 5000m,
                VariancePercentage = 1.75,
                ValuationDate = DateTime.UtcNow.AddDays(-3),
                AVMProvider = "HouseCanary"
            },
            "CA" => new AVMResult
            {
                PropertyAddress = propertyAddress,
                StateCode = stateCode,
                HasAVM = true,
                EstimatedValue = 695000m,
                ConfidenceScore = 0.78,
                VarianceFromBPO = -30000m,
                VariancePercentage = -4.14,
                ValuationDate = DateTime.UtcNow.AddDays(-5),
                AVMProvider = "Zillow AVM"
            },
            "FL" => new AVMResult
            {
                PropertyAddress = propertyAddress,
                StateCode = stateCode,
                HasAVM = true,
                EstimatedValue = 340000m,
                ConfidenceScore = 0.85,
                ValuationDate = DateTime.UtcNow.AddDays(-7),
                AVMProvider = "CoreLogic"
            },
            // AZ (ASSET-AZ-005): AVM aligns with BPO within 0.5% — strong cross-source corroboration.
            "AZ" => new AVMResult
            {
                PropertyAddress = propertyAddress,
                StateCode = stateCode,
                HasAVM = true,
                EstimatedValue = 382000m,
                ConfidenceScore = 0.96,
                VarianceFromBPO = 2000m,
                VariancePercentage = 0.53,
                ValuationDate = DateTime.UtcNow.AddDays(-2),
                AVMProvider = "HouseCanary"
            },
            _ => new AVMResult
            {
                PropertyAddress = propertyAddress,
                StateCode = stateCode,
                HasAVM = false,
                AVMProvider = "N/A"
            }
        };

        return Task.FromResult(result);
    }
}
