using Cascade.CTL.Agent.Domain.Enums;
using Cascade.CTL.Agent.Domain.Models;
using FluentAssertions;
using Xunit;

namespace Cascade.CTL.Agent.Tests.Domain;

public class AssetModelTests
{
    [Fact]
    public void Asset_ShouldCreateWithRequiredProperties()
    {
        var asset = new Asset
        {
            AssetId = "ASSET-TX-001",
            AssetType = AssetType.Foreclosure,
            StateCode = "TX",
            County = "Dallas",
            SellerTier = SellerTier.Tier1,
            OccupancyStatus = OccupancyStatus.Vacant,
            ParcelId = "TX-DAL-123456",
            PropertyAddress = "1234 Oak Street, Dallas, TX 75201"
        };

        asset.AssetId.Should().Be("ASSET-TX-001");
        asset.AssetType.Should().Be(AssetType.Foreclosure);
        asset.StateCode.Should().Be("TX");
        asset.County.Should().Be("Dallas");
        asset.SellerTier.Should().Be(SellerTier.Tier1);
        asset.OccupancyStatus.Should().Be(OccupancyStatus.Vacant);
    }

    [Fact]
    public void CTLVerdictDto_ShouldContainAllRequiredFields()
    {
        var verdict = new CTLVerdictDto
        {
            Verdict = CTLVerdict.ClearWithConditions,
            ConfidenceScore = 0.85,
            Conditions = ["HOA delinquency must be resolved"],
            EvidenceTrail = ["Title clear", "BPO current", "HOA delinquent $2,500"],
            ReflectionLog = "Title and valuation are clear but HOA delinquency needs resolution",
            AssetId = "ASSET-CA-002",
            Timestamp = DateTime.UtcNow,
            SessionId = "test-session-123"
        };

        verdict.Verdict.Should().Be(CTLVerdict.ClearWithConditions);
        verdict.ConfidenceScore.Should().BeInRange(0.0, 1.0);
        verdict.Conditions.Should().HaveCount(1);
        verdict.EvidenceTrail.Should().HaveCount(3);
    }

    [Fact]
    public void VerificationPlan_ShouldIncludeAllDomains()
    {
        var plan = new VerificationPlan
        {
            AssetId = "ASSET-TX-001",
            RequiredDomains = [VerificationDomain.Legal, VerificationDomain.Valuation, VerificationDomain.Occupancy],
            RelevantPolicies = ["Texas Foreclosure CTL Requirements", "General CTL Requirements"],
            AssetProfileSummary = "Foreclosure in TX-Dallas, Tier 1, Vacant",
            PlanRationale = "All three domains required for Tier 1 foreclosure"
        };

        plan.RequiredDomains.Should().HaveCount(3);
        plan.RequiredDomains.Should().Contain(VerificationDomain.Legal);
        plan.RequiredDomains.Should().Contain(VerificationDomain.Valuation);
        plan.RequiredDomains.Should().Contain(VerificationDomain.Occupancy);
    }

    [Fact]
    public void CTLEvaluationRequest_ShouldSetDefaults()
    {
        var request = new CTLEvaluationRequest
        {
            AssetId = "ASSET-TX-001"
        };

        request.RequestTimestamp.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
        request.WorkflowInstanceId.Should().BeNull();
    }

    [Theory]
    [InlineData(0.95, CTLVerdict.Clear)]
    [InlineData(0.90, CTLVerdict.Clear)]
    [InlineData(0.85, CTLVerdict.ClearWithConditions)]
    [InlineData(0.75, CTLVerdict.ClearWithConditions)]
    [InlineData(0.70, CTLVerdict.NeedsHumanReview)]
    [InlineData(0.50, CTLVerdict.NeedsHumanReview)]
    public void ConfidenceThresholdPolicy_ShouldMapCorrectly(double confidence, CTLVerdict expectedVerdict)
    {
        var verdict = ApplyThresholdPolicy(confidence);
        verdict.Should().Be(expectedVerdict);
    }

    private static CTLVerdict ApplyThresholdPolicy(double confidence)
    {
        if (confidence >= 0.90) return CTLVerdict.Clear;
        if (confidence >= 0.75) return CTLVerdict.ClearWithConditions;
        return CTLVerdict.NeedsHumanReview;
    }
}
