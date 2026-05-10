using Cascade.CTL.Agent.Application.Configuration;
using Cascade.CTL.Agent.Application.Orchestration;
using Cascade.CTL.Agent.Application.Orchestration.Workflow;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Cascade.CTL.Agent.Tests.Workflow;

/// <summary>
/// Phase-1 verdict-determinism v2 tests. Cover the two layers shipped in this round:
///   • Sampling lockdown (temp=0, top-p=1, deterministic per-asset seed)
///   • Discrete-bucket confidence snapping (preserving raw value for audit)
/// All assertions are deterministic — no LLM calls, no network, no flakiness.
/// </summary>
public class ReflectionDeterminismTests
{
    // ──────────────────────────────────────────────────────────────────
    // ReflectionDeterminismFactory.Build — sampling lockdown
    // ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Build_NullOptions_ReturnsVanillaTemperatureZeroChatOptions()
    {
        var chatOptions = ReflectionDeterminismFactory.Build(null, "ASSET-X", "SESSION-Y");

        chatOptions.Temperature.Should().Be(0.0f);
        // No additional properties when feature disabled — preserves prior behaviour exactly.
        (chatOptions.AdditionalProperties is null || chatOptions.AdditionalProperties.Count == 0)
            .Should().BeTrue();
    }

    [Fact]
    public void Build_DisabledOptions_ReturnsVanillaTemperatureZeroChatOptions()
    {
        var options = new ReflectionDeterminismOptions { Enabled = false };

        var chatOptions = ReflectionDeterminismFactory.Build(options, "ASSET-X", "SESSION-Y");

        chatOptions.Temperature.Should().Be(0.0f);
        (chatOptions.AdditionalProperties is null || chatOptions.AdditionalProperties.Count == 0)
            .Should().BeTrue();
    }

    [Fact]
    public void Build_EnabledOptions_AppliesTemperatureTopPAndSeed()
    {
        var options = new ReflectionDeterminismOptions
        {
            Enabled = true,
            Temperature = 0.0f,
            TopP = 1.0f,
            SeedStrategy = SeedStrategy.AssetIdHash
        };

        var chatOptions = ReflectionDeterminismFactory.Build(options, "ASSET-TX-001", "session-abc");

        chatOptions.Temperature.Should().Be(0.0f);
        chatOptions.TopP.Should().Be(1.0f);
        chatOptions.AdditionalProperties.Should().NotBeNull();
        chatOptions.AdditionalProperties!.Should().ContainKey(ReflectionDeterminismFactory.SeedPropertyKey);
        chatOptions.AdditionalProperties![ReflectionDeterminismFactory.SeedPropertyKey]
            .Should().BeOfType<long>().And.NotBe(0L);
    }

    [Fact]
    public void Build_SeedStrategyNone_DoesNotSetSeedProperty()
    {
        var options = new ReflectionDeterminismOptions { Enabled = true, SeedStrategy = SeedStrategy.None };

        var chatOptions = ReflectionDeterminismFactory.Build(options, "ASSET-X", "SESSION-Y");

        var hasSeed = chatOptions.AdditionalProperties is { } p
            && p.ContainsKey(ReflectionDeterminismFactory.SeedPropertyKey);
        hasSeed.Should().BeFalse();
    }

    [Fact]
    public void Build_SeedStrategyFixed_UsesConfiguredFixedSeed()
    {
        var options = new ReflectionDeterminismOptions
        {
            Enabled = true,
            SeedStrategy = SeedStrategy.Fixed,
            FixedSeed = 7777L
        };

        var chatOptions = ReflectionDeterminismFactory.Build(options, "ASSET-X", "SESSION-Y");

        chatOptions.AdditionalProperties![ReflectionDeterminismFactory.SeedPropertyKey].Should().Be(7777L);
    }

    [Fact]
    public void HashToSeed_SameInputs_AlwaysProducesSameSeed()
    {
        var seed1 = ReflectionDeterminismFactory.HashToSeed("ASSET-TX-001", sessionId: null);
        var seed2 = ReflectionDeterminismFactory.HashToSeed("ASSET-TX-001", sessionId: null);

        seed1.Should().Be(seed2);
        seed1.Should().BePositive(); // 63-bit positive guarantee
    }

    [Fact]
    public void HashToSeed_DifferentAssets_ProduceDifferentSeeds()
    {
        var seedA = ReflectionDeterminismFactory.HashToSeed("ASSET-TX-001", sessionId: null);
        var seedB = ReflectionDeterminismFactory.HashToSeed("ASSET-NY-004", sessionId: null);

        seedA.Should().NotBe(seedB);
    }

    [Fact]
    public void HashToSeed_AssetIdOnly_IsStableAcrossSessions()
    {
        // Critical for "rerun same asset → same verdict" — the default reproducibility goal.
        var seedA = ReflectionDeterminismFactory.HashToSeed("ASSET-TX-001", sessionId: null);
        var seedB = ReflectionDeterminismFactory.HashToSeed("ASSET-TX-001", sessionId: null);

        seedA.Should().Be(seedB);
    }

    [Fact]
    public void HashToSeed_WithSessionId_VariesBySession()
    {
        // Opt-in mode (IncludeSessionInSeed=true) — diversifies sampling across sessions on purpose.
        var seedA = ReflectionDeterminismFactory.HashToSeed("ASSET-TX-001", sessionId: "session-1");
        var seedB = ReflectionDeterminismFactory.HashToSeed("ASSET-TX-001", sessionId: "session-2");

        seedA.Should().NotBe(seedB);
    }

    [Fact]
    public void Build_AssetIdHashStrategy_DefaultDoesNotIncludeSession_SoSeedIsStableAcrossSessions()
    {
        var options = new ReflectionDeterminismOptions
        {
            Enabled = true,
            SeedStrategy = SeedStrategy.AssetIdHash
            // IncludeSessionInSeed defaults to false
        };

        var seedA = (long)ReflectionDeterminismFactory.Build(options, "ASSET-TX-001", "session-A")
            .AdditionalProperties![ReflectionDeterminismFactory.SeedPropertyKey]!;
        var seedB = (long)ReflectionDeterminismFactory.Build(options, "ASSET-TX-001", "session-B")
            .AdditionalProperties![ReflectionDeterminismFactory.SeedPropertyKey]!;

        seedA.Should().Be(seedB, "default reproducibility mode: seed depends on asset only");
    }

    [Fact]
    public void Build_IncludeSessionInSeedTrue_SeedVariesAcrossSessionsOnPurpose()
    {
        var options = new ReflectionDeterminismOptions
        {
            Enabled = true,
            SeedStrategy = SeedStrategy.AssetIdHash,
            IncludeSessionInSeed = true
        };

        var seedA = (long)ReflectionDeterminismFactory.Build(options, "ASSET-TX-001", "session-A")
            .AdditionalProperties![ReflectionDeterminismFactory.SeedPropertyKey]!;
        var seedB = (long)ReflectionDeterminismFactory.Build(options, "ASSET-TX-001", "session-B")
            .AdditionalProperties![ReflectionDeterminismFactory.SeedPropertyKey]!;

        seedA.Should().NotBe(seedB);
    }

    // ──────────────────────────────────────────────────────────────────
    // VerdictParser.SnapConfidenceToBucket — discrete bucket calibration
    // ──────────────────────────────────────────────────────────────────

    public static IEnumerable<object[]> SnapData() => new[]
    {
        // Each: rawConfidence, expectedSnappedValue
        new object[] { 0.83, 0.80 },   // mid-band → Medium
        new object[] { 0.87, 0.90 },   // closer to High than Medium
        new object[] { 0.92, 0.90 },   // High band
        new object[] { 0.50, 0.55 },   // very low → VeryLow
        new object[] { 0.99, 0.95 },   // very high → VeryHigh (95 is max bucket)
        new object[] { 0.75, 0.70 },   // tie-break: equidistant 0.70/0.80 → lower bucket (more conservative)
        new object[] { 0.55, 0.55 },   // exact match
        new object[] { 0.95, 0.95 },   // exact match
        new object[] { 0.00, 0.55 }    // 0 snaps up to lowest bucket
    };

    [Theory]
    [MemberData(nameof(SnapData))]
    public void SnapConfidenceToBucket_DiscreteBucketsEnabled_SnapsToNearestBucket(
        double rawConfidence, double expected)
    {
        var options = new ReflectionDeterminismOptions
        {
            UseDiscreteConfidenceBuckets = true,
            ConfidenceBuckets = [0.55, 0.70, 0.80, 0.90, 0.95]
        };

        var snapped = VerdictParser.SnapConfidenceToBucket(rawConfidence, options, NullLogger.Instance);

        snapped.Should().Be(expected);
    }

    [Fact]
    public void SnapConfidenceToBucket_DiscreteBucketsDisabled_PreservesRawValue()
    {
        var options = new ReflectionDeterminismOptions { UseDiscreteConfidenceBuckets = false };

        var snapped = VerdictParser.SnapConfidenceToBucket(0.83, options, NullLogger.Instance);

        snapped.Should().Be(0.83);
    }

    [Fact]
    public void SnapConfidenceToBucket_NullOptions_PreservesRawValue()
    {
        var snapped = VerdictParser.SnapConfidenceToBucket(0.83, null, NullLogger.Instance);

        snapped.Should().Be(0.83);
    }

    [Fact]
    public void SnapConfidenceToBucket_EmptyBucketArray_PreservesRawValue()
    {
        var options = new ReflectionDeterminismOptions
        {
            UseDiscreteConfidenceBuckets = true,
            ConfidenceBuckets = []
        };

        var snapped = VerdictParser.SnapConfidenceToBucket(0.83, options, NullLogger.Instance);

        snapped.Should().Be(0.83);
    }

    // ──────────────────────────────────────────────────────────────────
    // VerdictParser.ParseVerdict — raw value preserved on DTO for audit
    // ──────────────────────────────────────────────────────────────────

    [Fact]
    public void ParseVerdict_DiscreteBucketsEnabled_SnapsConfidence_AndRecordsRawForAudit()
    {
        const string verdictJson = """
            {
                "verdict": "ClearWithConditions",
                "confidenceScore": 0.83,
                "conditions": ["Refresh BPO"],
                "evidenceTrail": ["Title clear"],
                "reflectionLog": "All facts verified with one stale BPO."
            }
            """;

        var options = new ReflectionDeterminismOptions
        {
            UseDiscreteConfidenceBuckets = true,
            ConfidenceBuckets = [0.55, 0.70, 0.80, 0.90, 0.95]
        };

        var dto = VerdictParser.ParseVerdict(
            verdictJson, "ASSET-TX-001", "session-abc",
            NullLogger.Instance, humanReviewThreshold: 0.75, determinismOptions: options);

        dto.ConfidenceScore.Should().Be(0.80, "0.83 snaps to Medium bucket");
        dto.LlmRawConfidence.Should().Be(0.83, "raw LLM confidence preserved for drift audit");
        dto.LlmRawVerdict.Should().Be("ClearWithConditions");
    }

    [Fact]
    public void ParseVerdict_DeterminismOptionsNull_PreservesPriorBehaviour()
    {
        // Back-compat: callers that don't pass determinism options must see no change.
        const string verdictJson = """
            {
                "verdict": "Clear",
                "confidenceScore": 0.87,
                "conditions": [],
                "evidenceTrail": ["All clean"],
                "reflectionLog": "OK"
            }
            """;

        var dto = VerdictParser.ParseVerdict(
            verdictJson, "ASSET-X", "SESSION-Y", NullLogger.Instance);

        dto.ConfidenceScore.Should().Be(0.87, "no snapping when determinism is not configured");
        dto.LlmRawConfidence.Should().Be(0.87, "raw value still recorded as a copy");
        dto.LlmRawVerdict.Should().Be("Clear");
    }

    [Fact]
    public void ParseVerdict_SnappedConfidenceCanTriggerHumanReviewRemap()
    {
        // Raw 0.74 (below 0.75 threshold) but snaps to 0.70 — still below.
        // Verifies the remap rules use the SNAPPED value (post-determinism), which is the
        // value persisted on the DTO and shown to operators.
        const string verdictJson = """
            {
                "verdict": "Clear",
                "confidenceScore": 0.74,
                "conditions": [],
                "evidenceTrail": ["Borderline"],
                "reflectionLog": "Borderline confidence."
            }
            """;

        var options = new ReflectionDeterminismOptions
        {
            UseDiscreteConfidenceBuckets = true,
            ConfidenceBuckets = [0.55, 0.70, 0.80, 0.90, 0.95]
        };

        var dto = VerdictParser.ParseVerdict(
            verdictJson, "ASSET-X", "SESSION-Y",
            NullLogger.Instance, humanReviewThreshold: 0.75, determinismOptions: options);

        dto.ConfidenceScore.Should().Be(0.70);
        dto.Verdict.Should().Be(Cascade.CTL.Agent.Domain.Enums.CTLVerdict.NeedsHumanReview);
        dto.LlmRawConfidence.Should().Be(0.74);
    }

    // ──────────────────────────────────────────────────────────────────
    // Robust JSON extraction (verdict-determinism v2 fix B)
    // Prevents the parser from latching onto embedded citation arrays
    // and silently mis-parsing markdown narratives as verdicts.
    // ──────────────────────────────────────────────────────────────────

    [Fact]
    public void TryExtractVerdictJson_PlainJsonObject_ReturnsAsIs()
    {
        const string text = """
            {"verdict":"Clear","confidenceScore":0.95,"conditions":[],"evidenceTrail":[],"reflectionLog":"ok"}
            """;

        var extracted = VerdictParser.TryExtractVerdictJson(text);

        extracted.Should().NotBeNull();
        extracted!.Should().Contain("\"verdict\"");
    }

    [Fact]
    public void TryExtractVerdictJson_FencedJsonBlock_ExtractsBody()
    {
        const string text = """
            Here is my verdict:

            ```json
            {"verdict":"ClearWithConditions","confidenceScore":0.80,"conditions":["Refresh BPO"],"evidenceTrail":[],"reflectionLog":"ok"}
            ```

            That concludes the analysis.
            """;

        var extracted = VerdictParser.TryExtractVerdictJson(text);

        extracted.Should().NotBeNull();
        extracted!.Should().Contain("\"ClearWithConditions\"");
    }

    [Fact]
    public void TryExtractVerdictJson_MarkdownNarrativeWithCitationsArrayOnly_ReturnsNull()
    {
        // This is the exact pathology that produced Run #2 in the field measurement:
        // narrative bullets describe the verdict, but the only JSON in the text is a
        // citations array. The naive IndexOf('{')…LastIndexOf('}') extractor would
        // grab the citations and silently mis-parse them as a verdict. The robust
        // extractor must refuse: no object with a "verdict" key → null.
        const string text = """
            ### Final Verdict
            - **Verdict:** Clear
            - **Confidence:** 0.95

            ### Citations
            ```json
            [
                {"source":"Texas Foreclosure CTL Requirements","reference":"CTL-POLICY-TX-001#c0","excerpt":"..."},
                {"source":"Title Clearance","reference":"CTL-POLICY-TITLE-001#c0","excerpt":"..."}
            ]
            ```
            """;

        var extracted = VerdictParser.TryExtractVerdictJson(text);

        extracted.Should().BeNull("no JSON object containing a \"verdict\" field exists in the text");
    }

    [Fact]
    public void TryExtractVerdictJson_MultipleObjects_PicksFirstWithVerdictKey()
    {
        // The LLM emits a citation object before its verdict block. The extractor must
        // skip the citation object (no "verdict" key) and pick the verdict object.
        const string text = """
            Some narrative.

            ```json
            {"source":"Policy A","reference":"X","excerpt":"foo"}
            ```

            Then the verdict:

            ```json
            {"verdict":"NotClear","confidenceScore":0.55,"conditions":[],"evidenceTrail":[],"reflectionLog":"reason"}
            ```
            """;

        var extracted = VerdictParser.TryExtractVerdictJson(text);

        extracted.Should().NotBeNull();
        extracted!.Should().Contain("\"NotClear\"");
        extracted.Should().NotContain("Policy A");
    }

    [Fact]
    public void TryExtractVerdictJson_BraceBalancedScan_SkipsStringsContainingBraces()
    {
        // A string containing '{' or '}' must not fool the depth counter.
        const string text = """
            Some preamble {with a literal brace} that is not JSON.

            {"verdict":"Clear","confidenceScore":0.95,"conditions":[],"evidenceTrail":[],"reflectionLog":"a string with {curly} inside"}
            """;

        var extracted = VerdictParser.TryExtractVerdictJson(text);

        extracted.Should().NotBeNull();
        extracted!.Should().Contain("\"Clear\"");
    }

    [Fact]
    public void TryExtractVerdictJson_EmptyInput_ReturnsNull()
    {
        VerdictParser.TryExtractVerdictJson("").Should().BeNull();
        VerdictParser.TryExtractVerdictJson("   ").Should().BeNull();
    }

    [Fact]
    public void ParseVerdict_NarrativeWithCitationsOnly_RoutesToHumanReview()
    {
        // End-to-end: the run-#2 pathology must now produce NeedsHumanReview/0.0 with
        // an explicit "parsing failed" condition (not a fake-but-confident verdict).
        const string text = """
            ### Final Verdict
            - **Verdict:** Clear
            - **Confidence:** 0.95

            ### Citations
            ```json
            [{"source":"X","reference":"Y","excerpt":"Z"}]
            ```
            """;

        var dto = VerdictParser.ParseVerdict(
            text, "ASSET-TX-001", "session-X", NullLogger.Instance, humanReviewThreshold: 0.75);

        dto.Verdict.Should().Be(Cascade.CTL.Agent.Domain.Enums.CTLVerdict.NeedsHumanReview);
        dto.ConfidenceScore.Should().Be(0.0);
        dto.Conditions.Should().Contain(c => c.Contains("parsing failed", StringComparison.OrdinalIgnoreCase));
    }

    // ──────────────────────────────────────────────────────────────────
    // Fix C — strict structured outputs (response_format = json_schema)
    // ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Build_UseStructuredOutputsTrue_SetsJsonSchemaResponseFormat()
    {
        var options = new ReflectionDeterminismOptions { UseStructuredOutputs = true };

        var chatOptions = ReflectionDeterminismFactory.Build(options, "ASSET-X", "SESSION-Y");

        chatOptions.ResponseFormat.Should().BeOfType<ChatResponseFormatJson>();
        var jsonFormat = (ChatResponseFormatJson)chatOptions.ResponseFormat!;
        jsonFormat.Schema.Should().NotBeNull("Fix C requires a strict JSON schema, not free-form JSON mode");
        jsonFormat.SchemaName.Should().Be(ReflectionDeterminismFactory.VerdictSchemaName);
    }

    [Fact]
    public void Build_UseStructuredOutputsFalse_LeavesResponseFormatUnset()
    {
        var options = new ReflectionDeterminismOptions { UseStructuredOutputs = false };

        var chatOptions = ReflectionDeterminismFactory.Build(options, "ASSET-X", "SESSION-Y");

        chatOptions.ResponseFormat.Should().BeNull(
            "feature flag must be respected — connectors that do not support structured outputs " +
            "or operators who explicitly opt out get unchanged behaviour");
    }

    [Fact]
    public void Build_DisabledOverride_DoesNotSetResponseFormat()
    {
        // Whole determinism feature disabled → no structured outputs either.
        var options = new ReflectionDeterminismOptions { Enabled = false, UseStructuredOutputs = true };

        var chatOptions = ReflectionDeterminismFactory.Build(options, "ASSET-X", "SESSION-Y");

        chatOptions.ResponseFormat.Should().BeNull();
    }
}
