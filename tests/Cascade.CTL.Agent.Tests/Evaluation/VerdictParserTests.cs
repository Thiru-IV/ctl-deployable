using Cascade.CTL.Agent.Application.Orchestration;
using Cascade.CTL.Agent.Domain.Enums;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;

namespace Cascade.CTL.Agent.Tests.Evaluation;

public class VerdictParserTests
{
    private readonly ILogger _logger = Substitute.For<ILogger>();

    // ──────────────────────────────────────────────────────────────────
    // Basic parsing
    // ──────────────────────────────────────────────────────────────────

    [Fact]
    public void ParseVerdict_ShouldParseClearVerdict()
    {
        var json = """
        {
            "verdict": "Clear",
            "confidenceScore": 0.95,
            "conditions": [],
            "evidenceTrail": ["Title is clear"],
            "reflectionLog": "All checks passed"
        }
        """;

        var result = VerdictParser.ParseVerdict(json, "ASSET-001", "session-1", _logger);

        result.Verdict.Should().Be(CTLVerdict.Clear);
        result.ConfidenceScore.Should().Be(0.95);
        result.Conditions.Should().BeEmpty();
    }

    [Fact]
    public void ParseVerdict_ShouldParseClearWithConditions()
    {
        var json = """
        {
            "verdict": "ClearWithConditions",
            "confidenceScore": 0.88,
            "conditions": ["HOA lien must be resolved at closing"],
            "evidenceTrail": ["Title has minor lien"],
            "reflectionLog": "Mostly clear with conditions"
        }
        """;

        var result = VerdictParser.ParseVerdict(json, "ASSET-001", "session-1", _logger);

        result.Verdict.Should().Be(CTLVerdict.ClearWithConditions);
        result.ConfidenceScore.Should().Be(0.88);
        result.Conditions.Should().ContainSingle().Which.Should().Contain("HOA");
    }

    // ──────────────────────────────────────────────────────────────────
    // Confidence→Verdict remap: NeedsHumanReview at high confidence
    // ──────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(0.75)]
    [InlineData(0.85)]
    [InlineData(0.95)]
    public void ParseVerdict_ShouldRemapNeedsHumanReview_WhenConfidenceAboveThreshold(double confidence)
    {
        var json = $$"""
        {
            "verdict": "NeedsHumanReview",
            "confidenceScore": {{confidence}},
            "conditions": [],
            "evidenceTrail": ["Occupancy unresolved"],
            "reflectionLog": "Flagged for review"
        }
        """;

        var result = VerdictParser.ParseVerdict(json, "ASSET-001", "session-1", _logger, humanReviewThreshold: 0.75);

        result.Verdict.Should().Be(CTLVerdict.ClearWithConditions,
            "NeedsHumanReview with confidence >= 0.75 should be remapped to ClearWithConditions");
    }

    [Fact]
    public void ParseVerdict_ShouldKeepNeedsHumanReview_WhenConfidenceBelowThreshold()
    {
        var json = """
        {
            "verdict": "NeedsHumanReview",
            "confidenceScore": 0.60,
            "conditions": [],
            "evidenceTrail": ["Major issues found"],
            "reflectionLog": "Low confidence"
        }
        """;

        var result = VerdictParser.ParseVerdict(json, "ASSET-001", "session-1", _logger, humanReviewThreshold: 0.75);

        result.Verdict.Should().Be(CTLVerdict.NeedsHumanReview,
            "NeedsHumanReview with confidence < 0.75 should NOT be remapped");
    }

    // ──────────────────────────────────────────────────────────────────
    // Issue #5: Remap MUST add a condition explaining WHY it was remapped
    // ──────────────────────────────────────────────────────────────────

    [Fact]
    public void ParseVerdict_ShouldAddRemapCondition_WhenRemappingToFromNeedsHumanReview()
    {
        var json = """
        {
            "verdict": "NeedsHumanReview",
            "confidenceScore": 0.85,
            "conditions": [],
            "evidenceTrail": ["Occupancy status unresolved"],
            "reflectionLog": "Remapped"
        }
        """;

        var result = VerdictParser.ParseVerdict(json, "ASSET-001", "session-1", _logger, humanReviewThreshold: 0.75);

        result.Verdict.Should().Be(CTLVerdict.ClearWithConditions);
        result.Conditions.Should().ContainSingle()
            .Which.Should().Contain("remapped from NeedsHumanReview",
                "the remap condition should explain why the verdict was changed so HITL reviewers see it");
    }

    [Fact]
    public void ParseVerdict_ShouldPreserveExistingConditions_WhenRemapping()
    {
        var json = """
        {
            "verdict": "NeedsHumanReview",
            "confidenceScore": 0.85,
            "conditions": ["HOA verification pending", "Occupancy status unknown"],
            "evidenceTrail": ["Two unresolved items"],
            "reflectionLog": "Remapped with existing conditions"
        }
        """;

        var result = VerdictParser.ParseVerdict(json, "ASSET-001", "session-1", _logger, humanReviewThreshold: 0.75);

        result.Verdict.Should().Be(CTLVerdict.ClearWithConditions);
        result.Conditions.Should().HaveCount(3, "original 2 conditions + 1 remap explanation condition");
        result.Conditions.Should().Contain(c => c.Contains("HOA verification pending"));
        result.Conditions.Should().Contain(c => c.Contains("Occupancy status unknown"));
        result.Conditions.Should().Contain(c => c.Contains("remapped from NeedsHumanReview"));
    }

    // ──────────────────────────────────────────────────────────────────
    // Reverse safety net: Clear/ClearWithConditions at LOW confidence
    // ──────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Clear")]
    [InlineData("ClearWithConditions")]
    public void ParseVerdict_ShouldForceNeedsHumanReview_WhenClearAtLowConfidence(string verdictString)
    {
        var json = $$"""
        {
            "verdict": "{{verdictString}}",
            "confidenceScore": 0.60,
            "conditions": [],
            "evidenceTrail": ["Some evidence"],
            "reflectionLog": "Low confidence clear"
        }
        """;

        var result = VerdictParser.ParseVerdict(json, "ASSET-001", "session-1", _logger, humanReviewThreshold: 0.75);

        result.Verdict.Should().Be(CTLVerdict.NeedsHumanReview,
            $"'{verdictString}' at confidence 0.60 (< 0.75) should be forced to NeedsHumanReview");
    }

    [Fact]
    public void ParseVerdict_ShouldNotForceNeedsHumanReview_WhenClearAtHighConfidence()
    {
        var json = """
        {
            "verdict": "Clear",
            "confidenceScore": 0.92,
            "conditions": [],
            "evidenceTrail": ["All clear"],
            "reflectionLog": "High confidence"
        }
        """;

        var result = VerdictParser.ParseVerdict(json, "ASSET-001", "session-1", _logger, humanReviewThreshold: 0.75);

        result.Verdict.Should().Be(CTLVerdict.Clear);
    }

    // ──────────────────────────────────────────────────────────────────
    // Issue #3: Configurable threshold
    // ──────────────────────────────────────────────────────────────────

    [Fact]
    public void ParseVerdict_ShouldRespectCustomThreshold()
    {
        var json = """
        {
            "verdict": "NeedsHumanReview",
            "confidenceScore": 0.82,
            "conditions": [],
            "evidenceTrail": ["Evidence"],
            "reflectionLog": "Test threshold"
        }
        """;

        // With threshold 0.85, confidence 0.82 is BELOW → should keep NeedsHumanReview
        var result = VerdictParser.ParseVerdict(json, "ASSET-001", "session-1", _logger, humanReviewThreshold: 0.85);
        result.Verdict.Should().Be(CTLVerdict.NeedsHumanReview,
            "0.82 < 0.85 threshold → should stay NeedsHumanReview");

        // With threshold 0.80, confidence 0.82 is ABOVE → should remap to ClearWithConditions
        var result2 = VerdictParser.ParseVerdict(json, "ASSET-001", "session-1", _logger, humanReviewThreshold: 0.80);
        result2.Verdict.Should().Be(CTLVerdict.ClearWithConditions,
            "0.82 >= 0.80 threshold → should remap to ClearWithConditions");
    }

    // ──────────────────────────────────────────────────────────────────
    // Edge cases
    // ──────────────────────────────────────────────────────────────────

    [Fact]
    public void ParseVerdict_ShouldHandleNotClearVerdict_WithoutRemapping()
    {
        var json = """
        {
            "verdict": "NotClear",
            "confidenceScore": 0.90,
            "conditions": ["Title has fatal defects"],
            "evidenceTrail": ["Unreleased mortgage"],
            "reflectionLog": "Not clear"
        }
        """;

        var result = VerdictParser.ParseVerdict(json, "ASSET-001", "session-1", _logger);

        result.Verdict.Should().Be(CTLVerdict.NotClear,
            "NotClear verdict with confidence >= threshold should not be remapped");
    }

    [Fact]
    public void ParseVerdict_ShouldRemapNotClear_WhenConfidenceBelowThreshold()
    {
        var json = """
        {
            "verdict": "NotClear",
            "confidenceScore": 0.30,
            "conditions": ["Title has fatal defects"],
            "evidenceTrail": ["Unreleased mortgage"],
            "reflectionLog": "Not clear but low confidence"
        }
        """;

        var result = VerdictParser.ParseVerdict(json, "ASSET-001", "session-1", _logger);

        result.Verdict.Should().Be(CTLVerdict.NeedsHumanReview,
            "NotClear with low confidence should be remapped to NeedsHumanReview for human oversight");
        result.ConfidenceScore.Should().Be(0.30);
        result.Conditions.Should().Contain(c => c.Contains("Verdict remapped from NotClear"));
    }

    [Fact]
    public void ParseVerdict_ShouldHandleJsonWithSurroundingText()
    {
        var json = """
        Here is my analysis:
        ```json
        {
            "verdict": "Clear",
            "confidenceScore": 0.92,
            "conditions": [],
            "evidenceTrail": [],
            "reflectionLog": "Clean"
        }
        ```
        """;

        var result = VerdictParser.ParseVerdict(json, "ASSET-001", "session-1", _logger);

        result.Verdict.Should().Be(CTLVerdict.Clear);
        result.ConfidenceScore.Should().Be(0.92);
    }

    [Fact]
    public void ParseVerdict_ShouldReturnFallback_WhenJsonIsMalformed()
    {
        var json = "This is not JSON at all";

        var result = VerdictParser.ParseVerdict(json, "ASSET-001", "session-1", _logger);

        result.Verdict.Should().Be(CTLVerdict.NeedsHumanReview,
            "malformed JSON should fallback to NeedsHumanReview for safety");
    }

    // ──────────────────────────────────────────────────────────────────
    // Boundary: confidence exactly at threshold
    // ──────────────────────────────────────────────────────────────────

    [Fact]
    public void ParseVerdict_ShouldRemapAtExactThreshold()
    {
        var json = """
        {
            "verdict": "NeedsHumanReview",
            "confidenceScore": 0.75,
            "conditions": [],
            "evidenceTrail": [],
            "reflectionLog": "At boundary"
        }
        """;

        var result = VerdictParser.ParseVerdict(json, "ASSET-001", "session-1", _logger, humanReviewThreshold: 0.75);

        result.Verdict.Should().Be(CTLVerdict.ClearWithConditions,
            "confidence exactly at threshold (>=) should trigger remap");
    }
}
