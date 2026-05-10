using Cascade.CTL.Agent.Domain.Contracts;
using Cascade.CTL.Agent.Domain.Models;
using Microsoft.Extensions.Logging;

namespace Cascade.CTL.Agent.Infrastructure.Providers.Mock;

public sealed class MockHOAProvider : IHOAProvider
{
    private readonly ILogger<MockHOAProvider> _logger;

    public MockHOAProvider(ILogger<MockHOAProvider> logger)
    {
        _logger = logger;
    }

    public Task<HOAResult> CheckDelinquencyAsync(string propertyAddress, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("MockHOAProvider: Checking HOA delinquency for {Address}", propertyAddress);

        var result = propertyAddress switch
        {
            var a when a.Contains("Dallas", StringComparison.OrdinalIgnoreCase) => new HOAResult
            {
                PropertyAddress = propertyAddress,
                HasHOA = false,
                IsDelinquent = false,
                Status = "NoHOA"
            },
            var a when a.Contains("Los Angeles", StringComparison.OrdinalIgnoreCase) => new HOAResult
            {
                PropertyAddress = propertyAddress,
                HasHOA = true,
                IsDelinquent = true,
                DelinquentAmount = 2850.00m,
                HOAName = "Sunset Hills Homeowners Association",
                LastPaymentDate = DateTime.UtcNow.AddMonths(-8),
                Status = "Delinquent"
            },
            var a when a.Contains("Miami", StringComparison.OrdinalIgnoreCase) => new HOAResult
            {
                PropertyAddress = propertyAddress,
                HasHOA = true,
                IsDelinquent = false,
                HOAName = "Palm Gardens Community Association",
                LastPaymentDate = DateTime.UtcNow.AddMonths(-1),
                Status = "Current"
            },
            _ => new HOAResult
            {
                PropertyAddress = propertyAddress,
                HasHOA = false,
                IsDelinquent = false,
                Status = "NoHOA"
            }
        };

        return Task.FromResult(result);
    }
}
