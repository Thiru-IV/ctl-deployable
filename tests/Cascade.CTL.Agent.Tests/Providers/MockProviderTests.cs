using Cascade.CTL.Agent.Domain.Enums;
using Cascade.CTL.Agent.Infrastructure.Providers.Mock;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;

namespace Cascade.CTL.Agent.Tests.Providers;

public class MockProviderTests
{
    [Fact]
    public async Task MockAssetProfileProvider_ShouldReturnTexasForeclosure()
    {
        var provider = new MockAssetProfileProvider(Substitute.For<ILogger<MockAssetProfileProvider>>());
        var asset = await provider.GetAssetProfileAsync("ASSET-TX-001");

        asset.AssetId.Should().Be("ASSET-TX-001");
        asset.StateCode.Should().Be("TX");
        asset.County.Should().Be("Dallas");
        asset.AssetType.Should().Be(AssetType.Foreclosure);
        asset.OccupancyStatus.Should().Be(OccupancyStatus.Vacant);
    }

    [Fact]
    public async Task MockAssetProfileProvider_ShouldReturnCaliforniaREO()
    {
        var provider = new MockAssetProfileProvider(Substitute.For<ILogger<MockAssetProfileProvider>>());
        var asset = await provider.GetAssetProfileAsync("ASSET-CA-002");

        asset.AssetId.Should().Be("ASSET-CA-002");
        asset.StateCode.Should().Be("CA");
        asset.AssetType.Should().Be(AssetType.REO);
        asset.OccupancyStatus.Should().Be(OccupancyStatus.Occupied);
    }

    [Fact]
    public async Task MockAssetProfileProvider_ShouldThrowForUnknownAsset()
    {
        var provider = new MockAssetProfileProvider(Substitute.For<ILogger<MockAssetProfileProvider>>());
        var act = () => provider.GetAssetProfileAsync("UNKNOWN");
        await act.Should().ThrowAsync<KeyNotFoundException>();
    }

    [Fact]
    public async Task MockTitleSearchProvider_ShouldReturnClearTitleForTexas()
    {
        var provider = new MockTitleSearchProvider(Substitute.For<ILogger<MockTitleSearchProvider>>());
        var result = await provider.SearchAsync("TX-DAL-123456", "TX");

        result.HasClearTitle.Should().BeTrue();
        result.OpenLiens.Should().BeEmpty();
        result.HasHOAFlag.Should().BeFalse();
    }

    [Fact]
    public async Task MockTitleSearchProvider_ShouldReturnLiensForCalifornia()
    {
        var provider = new MockTitleSearchProvider(Substitute.For<ILogger<MockTitleSearchProvider>>());
        var result = await provider.SearchAsync("CA-LA-789012", "CA");

        result.HasClearTitle.Should().BeFalse();
        result.OpenLiens.Should().NotBeEmpty();
        result.HasHOAFlag.Should().BeTrue();
    }

    [Fact]
    public async Task MockHOAProvider_ShouldReturnDelinquentForLA()
    {
        var provider = new MockHOAProvider(Substitute.For<ILogger<MockHOAProvider>>());
        var result = await provider.CheckDelinquencyAsync("5678 Sunset Blvd, Los Angeles, CA 90028");

        result.HasHOA.Should().BeTrue();
        result.IsDelinquent.Should().BeTrue();
        result.DelinquentAmount.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task MockBPOProvider_ShouldReturnCurrentBPOForTexas()
    {
        var provider = new MockBPOProvider(Substitute.For<ILogger<MockBPOProvider>>());
        var result = await provider.RetrieveAsync("ASSET-TX-001");

        result.HasBPO.Should().BeTrue();
        result.IsStale.Should().BeFalse();
        result.EstimatedValue.Should().BeGreaterThan(0);
        result.QualityRating.Should().Be("High");
    }

    [Fact]
    public async Task MockBPOProvider_ShouldReturnStaleBPOForCalifornia()
    {
        var provider = new MockBPOProvider(Substitute.For<ILogger<MockBPOProvider>>());
        var result = await provider.RetrieveAsync("ASSET-CA-002");

        result.HasBPO.Should().BeTrue();
        result.IsStale.Should().BeTrue();
        result.DaysSinceBPO.Should().BeGreaterThan(90);
    }

    [Fact]
    public async Task MockAVMProvider_ShouldReturnHighConfidenceForTexas()
    {
        var provider = new MockAVMProvider(Substitute.For<ILogger<MockAVMProvider>>());
        var result = await provider.GetValuationAsync("1234 Oak Street, Dallas, TX 75201", "TX");

        result.HasAVM.Should().BeTrue();
        result.ConfidenceScore.Should().BeGreaterThan(0.90);
        result.VariancePercentage.Should().BeLessThan(5.0);
    }

    [Fact]
    public async Task MockOccupancyProvider_ShouldReturnVacantForDallas()
    {
        var provider = new MockOccupancyProvider(Substitute.For<ILogger<MockOccupancyProvider>>());
        var result = await provider.GetStatusAsync("1234 Oak Street, Dallas, TX 75201");

        result.IsVacant.Should().BeTrue();
        result.OccupancyStatus.Should().Be("Vacant");
        result.HasEvictionInProgress.Should().BeFalse();
    }

    [Fact]
    public async Task MockOccupancyProvider_ShouldReturnOccupiedForLA()
    {
        var provider = new MockOccupancyProvider(Substitute.For<ILogger<MockOccupancyProvider>>());
        var result = await provider.GetStatusAsync("5678 Sunset Blvd, Los Angeles, CA 90028");

        result.IsVacant.Should().BeFalse();
        result.HasEvictionInProgress.Should().BeTrue();
    }

    [Fact]
    public async Task MockCodeViolationProvider_ShouldReturnNoViolationsForDallas()
    {
        var provider = new MockCodeViolationProvider(Substitute.For<ILogger<MockCodeViolationProvider>>());
        var result = await provider.LookupAsync("1234 Oak Street, Dallas, TX 75201", "Dallas");

        result.HasOpenViolations.Should().BeFalse();
        result.Violations.Should().BeEmpty();
    }

    [Fact]
    public async Task MockCodeViolationProvider_ShouldReturnViolationsForMiami()
    {
        var provider = new MockCodeViolationProvider(Substitute.For<ILogger<MockCodeViolationProvider>>());
        var result = await provider.LookupAsync("910 Palm Ave, Miami, FL 33101", "Miami-Dade");

        result.HasOpenViolations.Should().BeTrue();
        result.Violations.Should().HaveCountGreaterThan(0);
        result.Violations.Should().Contain(v => v.Severity == "Critical");
    }
}
