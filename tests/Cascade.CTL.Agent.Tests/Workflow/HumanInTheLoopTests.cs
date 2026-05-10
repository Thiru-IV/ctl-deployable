using Cascade.CTL.Agent.Domain.Contracts;
using Cascade.CTL.Agent.Domain.Enums;
using Cascade.CTL.Agent.Domain.Models;
using Cascade.CTL.Agent.Infrastructure.Providers.Mock;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;

namespace Cascade.CTL.Agent.Tests.Workflow;

/// <summary>
/// Tests for the Human-in-the-Loop (HITL) feature: domain models, mock service,
/// and integration with CTLEvaluationResult.
/// </summary>
public class HumanInTheLoopTests
{
    // ──────────────────────────────────────────────────────────────────
    // HumanReviewAction enum tests
    // ──────────────────────────────────────────────────────────────────

    [Fact]
    public void HumanReviewAction_ShouldHaveThreeValues()
    {
        Enum.GetValues<HumanReviewAction>().Should().HaveCount(3);
    }

    [Theory]
    [InlineData(HumanReviewAction.Confirm)]
    [InlineData(HumanReviewAction.OverrideVerdict)]
    [InlineData(HumanReviewAction.RequestReEvaluation)]
    public void HumanReviewAction_ShouldContainExpectedValues(HumanReviewAction action)
    {
        Enum.IsDefined(action).Should().BeTrue();
    }

    // ──────────────────────────────────────────────────────────────────
    // HumanReviewRequest model tests
    // ──────────────────────────────────────────────────────────────────

    [Fact]
    public void HumanReviewRequest_ShouldHoldAllFields()
    {
        var verdict = CreateTestVerdict(CTLVerdict.NeedsHumanReview, 0.55);
        var request = new HumanReviewRequest
        {
            SessionId = "sess-001",
            AssetId = "ASSET-CA-002",
            ProposedVerdict = verdict,
            ReflectionOutput = "{\"verdict\":\"NeedsHumanReview\"}"
        };

        request.SessionId.Should().Be("sess-001");
        request.AssetId.Should().Be("ASSET-CA-002");
        request.ProposedVerdict.Verdict.Should().Be(CTLVerdict.NeedsHumanReview);
        request.ProposedVerdict.ConfidenceScore.Should().Be(0.55);
        request.ReflectionOutput.Should().Contain("NeedsHumanReview");
        request.RequestedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
    }

    // ──────────────────────────────────────────────────────────────────
    // HumanReviewDecision model tests
    // ──────────────────────────────────────────────────────────────────

    [Fact]
    public void HumanReviewDecision_Confirm_ShouldNotRequireOverrideFields()
    {
        var decision = new HumanReviewDecision
        {
            Action = HumanReviewAction.Confirm,
            ReviewerNotes = "Confirmed — field inspection required.",
            ReviewedBy = "jane@cascade.com"
        };

        decision.Action.Should().Be(HumanReviewAction.Confirm);
        decision.OverriddenVerdict.Should().BeNull();
        decision.OverriddenConfidence.Should().BeNull();
        decision.ReviewedBy.Should().Be("jane@cascade.com");
        decision.ReviewedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void HumanReviewDecision_Override_ShouldCarryNewVerdictAndConfidence()
    {
        var decision = new HumanReviewDecision
        {
            Action = HumanReviewAction.OverrideVerdict,
            OverriddenVerdict = CTLVerdict.ClearWithConditions,
            OverriddenConfidence = 0.80,
            ReviewerNotes = "Manually verified liens are resolved.",
            ReviewedBy = "john@cascade.com"
        };

        decision.Action.Should().Be(HumanReviewAction.OverrideVerdict);
        decision.OverriddenVerdict.Should().Be(CTLVerdict.ClearWithConditions);
        decision.OverriddenConfidence.Should().Be(0.80);
    }

    [Fact]
    public void HumanReviewDecision_RequestReEvaluation_ShouldHaveNotes()
    {
        var decision = new HumanReviewDecision
        {
            Action = HumanReviewAction.RequestReEvaluation,
            ReviewerNotes = "Need updated BPO before proceeding.",
            ReviewedBy = "reviewer@cascade.com"
        };

        decision.Action.Should().Be(HumanReviewAction.RequestReEvaluation);
        decision.OverriddenVerdict.Should().BeNull();
        decision.ReviewerNotes.Should().Contain("BPO");
    }

    // ──────────────────────────────────────────────────────────────────
    // CTLEvaluationResult HITL integration
    // ──────────────────────────────────────────────────────────────────

    [Fact]
    public void CTLEvaluationResult_WithoutHITL_ShouldHaveNullHumanReview()
    {
        var result = new CTLEvaluationResult
        {
            Verdict = CreateTestVerdict(CTLVerdict.Clear, 0.95),
            EvaluationDuration = TimeSpan.FromSeconds(10),
            TotalTokensUsed = 5000,
            ToolInvocationCount = 12
        };

        result.HumanReview.Should().BeNull();
    }

    [Fact]
    public void CTLEvaluationResult_WithHITL_ShouldCarryDecision()
    {
        var humanDecision = new HumanReviewDecision
        {
            Action = HumanReviewAction.OverrideVerdict,
            OverriddenVerdict = CTLVerdict.ClearWithConditions,
            OverriddenConfidence = 0.78,
            ReviewerNotes = "Cleared after manual review.",
            ReviewedBy = "reviewer@cascade.com"
        };

        var result = new CTLEvaluationResult
        {
            Verdict = CreateTestVerdict(CTLVerdict.ClearWithConditions, 0.78),
            EvaluationDuration = TimeSpan.FromSeconds(30),
            TotalTokensUsed = 8000,
            ToolInvocationCount = 15,
            HumanReview = humanDecision
        };

        result.HumanReview.Should().NotBeNull();
        result.HumanReview!.Action.Should().Be(HumanReviewAction.OverrideVerdict);
        result.HumanReview.OverriddenVerdict.Should().Be(CTLVerdict.ClearWithConditions);
        result.HumanReview.ReviewedBy.Should().Be("reviewer@cascade.com");
    }

    // ──────────────────────────────────────────────────────────────────
    // IHumanReviewService interface tests
    // ──────────────────────────────────────────────────────────────────

    [Fact]
    public void IHumanReviewService_ShouldBeResolvable()
    {
        var mock = Substitute.For<IHumanReviewService>();
        mock.Should().NotBeNull();
        mock.Should().BeAssignableTo<IHumanReviewService>();
    }

    [Fact]
    public async Task IHumanReviewService_ShouldReturnDecision()
    {
        var mock = Substitute.For<IHumanReviewService>();
        var expectedDecision = new HumanReviewDecision
        {
            Action = HumanReviewAction.Confirm,
            ReviewerNotes = "Confirmed.",
            ReviewedBy = "test@cascade.com"
        };
        mock.RequestReviewAsync(Arg.Any<HumanReviewRequest>(), Arg.Any<CancellationToken>())
            .Returns(expectedDecision);

        var result = await mock.RequestReviewAsync(new HumanReviewRequest
        {
            SessionId = "s1",
            AssetId = "A1",
            ProposedVerdict = CreateTestVerdict(CTLVerdict.NeedsHumanReview, 0.40),
            ReflectionOutput = "{}"
        });

        result.Action.Should().Be(HumanReviewAction.Confirm);
    }

    // ──────────────────────────────────────────────────────────────────
    // MockHumanReviewService behavior tests
    // ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task MockHumanReviewService_HighConfidence_ShouldOverride()
    {
        // Confidence 0.65 >= 0.60 threshold → mock reviewer overrides to ClearWithConditions
        var service = CreateMockService();
        var request = CreateReviewRequest(confidence: 0.65);

        var decision = await service.RequestReviewAsync(request);

        decision.Action.Should().Be(HumanReviewAction.OverrideVerdict);
        decision.OverriddenVerdict.Should().Be(CTLVerdict.ClearWithConditions);
        decision.OverriddenConfidence.Should().Be(0.78);
        decision.ReviewedBy.Should().Be("mock-reviewer@cascade.com");
    }

    [Fact]
    public async Task MockHumanReviewService_BorderlineConfidence_ShouldOverride()
    {
        // Confidence 0.60 exactly at threshold → mock reviewer overrides
        var service = CreateMockService();
        var request = CreateReviewRequest(confidence: 0.60);

        var decision = await service.RequestReviewAsync(request);

        decision.Action.Should().Be(HumanReviewAction.OverrideVerdict);
        decision.OverriddenVerdict.Should().Be(CTLVerdict.ClearWithConditions);
    }

    [Fact]
    public async Task MockHumanReviewService_LowConfidence_ShouldConfirm()
    {
        // Confidence 0.45 < 0.60 threshold → mock reviewer confirms NeedsHumanReview
        var service = CreateMockService();
        var request = CreateReviewRequest(confidence: 0.45);

        var decision = await service.RequestReviewAsync(request);

        decision.Action.Should().Be(HumanReviewAction.Confirm);
        decision.OverriddenVerdict.Should().BeNull();
        decision.OverriddenConfidence.Should().BeNull();
        decision.ReviewedBy.Should().Be("mock-reviewer@cascade.com");
    }

    [Fact]
    public async Task MockHumanReviewService_VeryLowConfidence_ShouldConfirm()
    {
        // Confidence 0.10 → too risky — confirmed
        var service = CreateMockService();
        var request = CreateReviewRequest(confidence: 0.10);

        var decision = await service.RequestReviewAsync(request);

        decision.Action.Should().Be(HumanReviewAction.Confirm);
        decision.ReviewerNotes.Should().Contain("0.10");
    }

    [Fact]
    public async Task MockHumanReviewService_ZeroConfidence_ShouldConfirm()
    {
        var service = CreateMockService();
        var request = CreateReviewRequest(confidence: 0.0);

        var decision = await service.RequestReviewAsync(request);

        decision.Action.Should().Be(HumanReviewAction.Confirm);
    }

    [Fact]
    public async Task MockHumanReviewService_OverrideDecision_ShouldIncludeReviewerNotes()
    {
        var service = CreateMockService();
        var request = CreateReviewRequest(confidence: 0.70);

        var decision = await service.RequestReviewAsync(request);

        decision.ReviewerNotes.Should().NotBeNullOrWhiteSpace();
        decision.ReviewerNotes.Should().Contain("0.70");
    }

    [Fact]
    public async Task MockHumanReviewService_ConfirmDecision_ShouldIncludeReviewerNotes()
    {
        var service = CreateMockService();
        var request = CreateReviewRequest(confidence: 0.30);

        var decision = await service.RequestReviewAsync(request);

        decision.ReviewerNotes.Should().NotBeNullOrWhiteSpace();
        decision.ReviewerNotes.Should().Contain("0.30");
    }

    [Fact]
    public async Task MockHumanReviewService_ShouldSetReviewedAtTimestamp()
    {
        var service = CreateMockService();
        var request = CreateReviewRequest(confidence: 0.50);

        var before = DateTime.UtcNow;
        var decision = await service.RequestReviewAsync(request);

        decision.ReviewedAt.Should().BeOnOrAfter(before);
        decision.ReviewedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
    }

    // ──────────────────────────────────────────────────────────────────
    // Verdict override flow tests (simulating orchestrator logic)
    // ──────────────────────────────────────────────────────────────────

    [Fact]
    public void VerdictOverride_ShouldApplyNewVerdictAndConfidence()
    {
        var originalVerdict = CreateTestVerdict(CTLVerdict.NeedsHumanReview, 0.55);

        var humanDecision = new HumanReviewDecision
        {
            Action = HumanReviewAction.OverrideVerdict,
            OverriddenVerdict = CTLVerdict.ClearWithConditions,
            OverriddenConfidence = 0.78,
            ReviewerNotes = "Liens resolved after manual check.",
            ReviewedBy = "reviewer@cascade.com"
        };

        // Simulate the orchestrator's override logic
        var overriddenVerdict = originalVerdict with
        {
            Verdict = humanDecision.OverriddenVerdict!.Value,
            ConfidenceScore = humanDecision.OverriddenConfidence ?? originalVerdict.ConfidenceScore,
            Conditions = [.. originalVerdict.Conditions, $"Human override by {humanDecision.ReviewedBy}: {humanDecision.ReviewerNotes}"],
            ReflectionLog = originalVerdict.ReflectionLog + $"\n\n[HUMAN REVIEW] {humanDecision.Action} by {humanDecision.ReviewedBy}: {humanDecision.ReviewerNotes}"
        };

        overriddenVerdict.Verdict.Should().Be(CTLVerdict.ClearWithConditions);
        overriddenVerdict.ConfidenceScore.Should().Be(0.78);
        overriddenVerdict.Conditions.Should().Contain(c => c.Contains("Human override"));
        overriddenVerdict.ReflectionLog.Should().Contain("[HUMAN REVIEW]");
        // Original fields preserved
        overriddenVerdict.AssetId.Should().Be(originalVerdict.AssetId);
        overriddenVerdict.SessionId.Should().Be(originalVerdict.SessionId);
    }

    [Fact]
    public void VerdictConfirm_ShouldPreserveOriginalVerdict()
    {
        var originalVerdict = CreateTestVerdict(CTLVerdict.NeedsHumanReview, 0.40);

        var humanDecision = new HumanReviewDecision
        {
            Action = HumanReviewAction.Confirm,
            ReviewerNotes = "Confirmed — too risky.",
            ReviewedBy = "reviewer@cascade.com"
        };

        // When confirmed, verdict stays as-is
        originalVerdict.Verdict.Should().Be(CTLVerdict.NeedsHumanReview);
        originalVerdict.ConfidenceScore.Should().Be(0.40);
    }

    [Fact]
    public void ClearVerdict_ShouldNotTriggerHITL()
    {
        // HITL only triggers when verdict == NeedsHumanReview
        var verdict = CreateTestVerdict(CTLVerdict.Clear, 0.95);
        var shouldTriggerHITL = verdict.Verdict == CTLVerdict.NeedsHumanReview;

        shouldTriggerHITL.Should().BeFalse();
    }

    [Fact]
    public void ClearWithConditionsVerdict_ShouldNotTriggerHITL()
    {
        var verdict = CreateTestVerdict(CTLVerdict.ClearWithConditions, 0.80);
        var shouldTriggerHITL = verdict.Verdict == CTLVerdict.NeedsHumanReview;

        shouldTriggerHITL.Should().BeFalse();
    }

    [Fact]
    public void NotClearVerdict_ShouldNotTriggerHITL()
    {
        var verdict = CreateTestVerdict(CTLVerdict.NotClear, 0.30);
        var shouldTriggerHITL = verdict.Verdict == CTLVerdict.NeedsHumanReview;

        shouldTriggerHITL.Should().BeFalse();
    }

    [Fact]
    public void NeedsHumanReviewVerdict_ShouldTriggerHITL()
    {
        var verdict = CreateTestVerdict(CTLVerdict.NeedsHumanReview, 0.55);
        var shouldTriggerHITL = verdict.Verdict == CTLVerdict.NeedsHumanReview;

        shouldTriggerHITL.Should().BeTrue();
    }

    // ──────────────────────────────────────────────────────────────────
    // Helpers
    // ──────────────────────────────────────────────────────────────────

    private static CTLVerdictDto CreateTestVerdict(CTLVerdict verdict, double confidence) =>
        new()
        {
            Verdict = verdict,
            ConfidenceScore = confidence,
            Conditions = verdict == CTLVerdict.NeedsHumanReview
                ? ["Low confidence — manual review required"]
                : [],
            EvidenceTrail = ["Test evidence"],
            ReflectionLog = "Test reflection log",
            AssetId = "ASSET-TEST-001",
            Timestamp = DateTime.UtcNow,
            SessionId = "test-session"
        };

    private static MockHumanReviewService CreateMockService() =>
        new(Substitute.For<ILogger<MockHumanReviewService>>());

    private static HumanReviewRequest CreateReviewRequest(double confidence) =>
        new()
        {
            SessionId = "test-session",
            AssetId = "ASSET-TEST-001",
            ProposedVerdict = CreateTestVerdict(CTLVerdict.NeedsHumanReview, confidence),
            ReflectionOutput = $"{{\"verdict\":\"NeedsHumanReview\",\"confidenceScore\":{confidence}}}"
        };
}
