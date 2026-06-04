using Cascade.CTL.Agent.Domain.Contracts;
using Cascade.CTL.Agent.Domain.Models;
using Microsoft.Extensions.Logging;

namespace Cascade.CTL.Agent.Infrastructure.Providers.Mock;

public sealed class MockTitleSearchProvider : ITitleSearchProvider
{
    private readonly ILogger<MockTitleSearchProvider> _logger;

    public MockTitleSearchProvider(ILogger<MockTitleSearchProvider> logger)
    {
        _logger = logger;
    }

    public Task<TitleSearchResult> SearchAsync(string parcelId, string stateCode, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("MockTitleSearchProvider: Searching title for parcel {ParcelId} in {StateCode}", parcelId, stateCode);

        var result = parcelId switch
        {
            "TX-DAL-123456" => new TitleSearchResult
            {
                ParcelId = parcelId,
                StateCode = stateCode,
                HasClearTitle = true,
                OpenLiens = [],
                Encumbrances = [],
                TitleDefects = [],
                HasHOAFlag = false,
                ProviderReference = "MOCK-TITLE-TX-001"
            },
            "CA-LA-789012" => new TitleSearchResult
            {
                ParcelId = parcelId,
                StateCode = stateCode,
                HasClearTitle = false,
                OpenLiens = ["Property Tax Lien - $4,200 - Los Angeles County"],
                Encumbrances = ["Second mortgage - Pacific Credit Union"],
                TitleDefects = [],
                HasHOAFlag = true,
                ProviderReference = "MOCK-TITLE-CA-002"
            },
            "FL-MD-345678" => new TitleSearchResult
            {
                ParcelId = parcelId,
                StateCode = stateCode,
                HasClearTitle = true,
                OpenLiens = [],
                Encumbrances = [],
                TitleDefects = ["Minor boundary dispute - pending survey"],
                HasHOAFlag = true,
                ProviderReference = "MOCK-TITLE-FL-003"
            },
            "AZ-MAR-901234" => new TitleSearchResult
            {
                ParcelId = parcelId,
                StateCode = stateCode,
                HasClearTitle = true,
                OpenLiens = [],
                Encumbrances = [],
                TitleDefects = [],
                HasHOAFlag = false,
                ProviderReference = "MOCK-TITLE-AZ-005"
            },
            _ => new TitleSearchResult
            {
                ParcelId = parcelId,
                StateCode = stateCode,
                HasClearTitle = true,
                OpenLiens = [],
                Encumbrances = [],
                TitleDefects = [],
                HasHOAFlag = false,
                ProviderReference = $"MOCK-TITLE-DEFAULT-{parcelId}"
            }
        };

        return Task.FromResult(result);
    }
}
