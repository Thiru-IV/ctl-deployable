using Cascade.CTL.Agent.Domain.Contracts;
using Cascade.CTL.Agent.Domain.Models;
using Microsoft.Extensions.Logging;

namespace Cascade.CTL.Agent.Infrastructure.Providers.Mock;

public sealed class MockOccupancyProvider : IOccupancyProvider
{
    private readonly ILogger<MockOccupancyProvider> _logger;

    public MockOccupancyProvider(ILogger<MockOccupancyProvider> logger)
    {
        _logger = logger;
    }

    public Task<OccupancyStatusResult> GetStatusAsync(string propertyAddress, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("MockOccupancyProvider: Getting occupancy status for {Address}", propertyAddress);

        var result = propertyAddress switch
        {
            var a when a.Contains("Dallas", StringComparison.OrdinalIgnoreCase) => new OccupancyStatusResult
            {
                PropertyAddress = propertyAddress,
                OccupancyStatus = "Vacant",
                IsVacant = true,
                LastInspectionDate = DateTime.UtcNow.AddDays(-7),
                InspectionVendor = "Safeguard Properties",
                HasEvictionInProgress = false,
                PropertyCondition = "Good — property secured, utilities off, no damage observed",
                Notes = ["Property winterized", "Lock box installed"]
            },
            var a when a.Contains("Los Angeles", StringComparison.OrdinalIgnoreCase) => new OccupancyStatusResult
            {
                PropertyAddress = propertyAddress,
                OccupancyStatus = "Occupied",
                IsVacant = false,
                LastInspectionDate = DateTime.UtcNow.AddDays(-14),
                InspectionVendor = "MCS Field Services",
                HasEvictionInProgress = true,
                PropertyCondition = "Fair — occupied, cannot fully inspect interior",
                Notes = ["Tenant claims lease agreement", "Eviction filed 2026-02-15", "Cash-for-keys offered"]
            },
            var a when a.Contains("Miami", StringComparison.OrdinalIgnoreCase) => new OccupancyStatusResult
            {
                PropertyAddress = propertyAddress,
                OccupancyStatus = "Unknown",
                IsVacant = false,
                LastInspectionDate = DateTime.UtcNow.AddDays(-30),
                InspectionVendor = "Cyprexx Services",
                HasEvictionInProgress = false,
                PropertyCondition = "Unknown — access denied during last inspection",
                Notes = ["Unable to determine occupancy", "Neighbor reports occasional activity"]
            },
            _ => new OccupancyStatusResult
            {
                PropertyAddress = propertyAddress,
                OccupancyStatus = "Unknown",
                IsVacant = false,
                PropertyCondition = "Unknown"
            }
        };

        return Task.FromResult(result);
    }
}
