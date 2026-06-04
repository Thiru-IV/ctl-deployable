using Cascade.CTL.Agent.Domain.Contracts;
using Cascade.CTL.Agent.Domain.Models;
using Microsoft.Extensions.Logging;

namespace Cascade.CTL.Agent.Infrastructure.Providers.Mock;

public sealed class MockCodeViolationProvider : ICodeViolationProvider
{
    private readonly ILogger<MockCodeViolationProvider> _logger;

    public MockCodeViolationProvider(ILogger<MockCodeViolationProvider> logger)
    {
        _logger = logger;
    }

    public Task<CodeViolationResult> LookupAsync(string propertyAddress, string county, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("MockCodeViolationProvider: Looking up code violations for {Address} in {County}", propertyAddress, county);

        var result = county switch
        {
            "Dallas" => new CodeViolationResult
            {
                PropertyAddress = propertyAddress,
                County = county,
                HasOpenViolations = false,
                Violations = []
            },
            "Los Angeles" => new CodeViolationResult
            {
                PropertyAddress = propertyAddress,
                County = county,
                HasOpenViolations = true,
                Violations =
                [
                    new CodeViolation
                    {
                        ViolationType = "Property Maintenance",
                        Description = "Overgrown vegetation and debris in front yard",
                        Severity = "Minor",
                        DateIssued = DateTime.UtcNow.AddDays(-45),
                        CaseNumber = "CV-2026-LA-4521"
                    }
                ]
            },
            "Miami-Dade" => new CodeViolationResult
            {
                PropertyAddress = propertyAddress,
                County = county,
                HasOpenViolations = true,
                Violations =
                [
                    new CodeViolation
                    {
                        ViolationType = "Structural",
                        Description = "Damaged roof section requiring repair — hurricane damage",
                        Severity = "Major",
                        DateIssued = DateTime.UtcNow.AddDays(-90),
                        CaseNumber = "CV-2025-MD-8901"
                    },
                    new CodeViolation
                    {
                        ViolationType = "Safety",
                        Description = "Non-functional smoke detectors",
                        Severity = "Critical",
                        DateIssued = DateTime.UtcNow.AddDays(-60),
                        CaseNumber = "CV-2026-MD-1234"
                    }
                ]
            },
            "Maricopa" => new CodeViolationResult
            {
                PropertyAddress = propertyAddress,
                County = county,
                HasOpenViolations = false,
                Violations = []
            },
            _ => new CodeViolationResult
            {
                PropertyAddress = propertyAddress,
                County = county,
                HasOpenViolations = false,
                Violations = []
            }
        };

        return Task.FromResult(result);
    }
}
