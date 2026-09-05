using Cascade.CTL.Agent.Application.Configuration;
using Cascade.CTL.Agent.Application.Orchestration;
using Cascade.CTL.Agent.Application.Prompts;
using Cascade.CTL.Agent.Application.Resilience;
using Cascade.CTL.Agent.Domain.Contracts;
using Cascade.CTL.Agent.Domain.Enums;
using Cascade.CTL.Agent.Domain.Models;
using Cascade.CTL.Agent.Guardrails;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace Cascade.CTL.Agent.Tests.Evaluation;

// ─────────────────────────────────────────────────────────────────────────────
// 1. VerdictGroundednessEvaluator — unit tests for the LLM-as-judge
// ─────────────────────────────────────────────────────────────────────────────

public class VerdictGroundednessEvaluatorTests
{
    private static readonly CTLVerdictDto SampleVerdict = new()
    {
        Verdict = CTLVerdict.Clear,
        ConfidenceScore = 0.95,
        Conditions = [],
        EvidenceTrail = ["Title clear", "BPO current"],
        ReflectionLog = "All domains clear.",
        AssetId = "ASSET-TX-001",
        Timestamp = DateTime.UtcNow,
        SessionId = "test-session"
    };

    private const string SampleFindings = "Legal: Title is clear. Valuation: BPO current. Occupancy: Vacant.";

    // ── ParseJudgeResponse ──────────────────────────────────────────

    [Fact]
    public void ParseJudgeResponse_ValidJson_ReturnsCorrectScore()
    {
        var json = """{"groundednessScore": 4, "reasoning": "Well grounded with minor gap."}""";

        var result = VerdictGroundednessEvaluator.ParseJudgeResponse(json);

        result.Score.Should().Be(4);
        result.Reasoning.Should().Contain("Well grounded");
        result.EvaluationSucceeded.Should().BeTrue();
    }

    [Fact]
    public void ParseJudgeResponse_JsonWithMarkdownWrapper_ExtractsCorrectly()
    {
        var json = """
            Here is my evaluation:
            ```json
            {"groundednessScore": 5, "reasoning": "Fully grounded."}
            ```
            """;

        var result = VerdictGroundednessEvaluator.ParseJudgeResponse(json);

        result.Score.Should().Be(5);
        result.EvaluationSucceeded.Should().BeTrue();
    }

    [Fact]
    public void ParseJudgeResponse_ScoreOutOfRange_ReturnsFailed()
    {
        var json = """{"groundednessScore": 7, "reasoning": "Invalid."}""";

        var result = VerdictGroundednessEvaluator.ParseJudgeResponse(json);

        result.EvaluationSucceeded.Should().BeFalse();
        result.Score.Should().Be(0);
    }

    [Fact]
    public void ParseJudgeResponse_ZeroScore_ReturnsFailed()
    {
        var json = """{"groundednessScore": 0, "reasoning": "Zero."}""";

        var result = VerdictGroundednessEvaluator.ParseJudgeResponse(json);

        result.EvaluationSucceeded.Should().BeFalse();
    }

    [Fact]
    public void ParseJudgeResponse_InvalidJson_ReturnsFailed()
    {
        var result = VerdictGroundednessEvaluator.ParseJudgeResponse("not json at all");

        result.EvaluationSucceeded.Should().BeFalse();
        result.Score.Should().Be(0);
    }

    [Fact]
    public void ParseJudgeResponse_EmptyString_ReturnsFailed()
    {
        var result = VerdictGroundednessEvaluator.ParseJudgeResponse("");

        result.EvaluationSucceeded.Should().BeFalse();
    }

    [Fact]
    public void ParseJudgeResponse_MissingReasoning_DefaultsToMessage()
    {
        var json = """{"groundednessScore": 3}""";

        var result = VerdictGroundednessEvaluator.ParseJudgeResponse(json);

        result.Score.Should().Be(3);
        result.Reasoning.Should().Be("No reasoning provided.");
        result.EvaluationSucceeded.Should().BeTrue();
    }

    // ── BuildVerdictUserPrompt ──────────────────────────────────────

    [Fact]
    public void BuildUserPrompt_IncludesAllVerdictFields()
    {
        var prompt = GroundednessJudgePrompts.BuildVerdictUserPrompt(SampleFindings, SampleVerdict);

        prompt.Should().Contain("Investigation Findings");
        prompt.Should().Contain(SampleFindings);
        prompt.Should().Contain("Verdict: Clear");
        prompt.Should().Contain("Confidence Score: 0.95");
        prompt.Should().Contain("Title clear");
        prompt.Should().Contain("All domains clear.");
    }

    [Fact]
    public void BuildUserPrompt_WithConditions_IncludesConditions()
    {
        var verdict = SampleVerdict with
        {
            Verdict = CTLVerdict.ClearWithConditions,
            Conditions = ["HOA payment required", "BPO refresh needed"]
        };

        var prompt = GroundednessJudgePrompts.BuildVerdictUserPrompt(SampleFindings, verdict);

        prompt.Should().Contain("HOA payment required; BPO refresh needed");
    }

    [Fact]
    public void BuildUserPrompt_NoConditions_ShowsNone()
    {
        var prompt = GroundednessJudgePrompts.BuildVerdictUserPrompt(SampleFindings, SampleVerdict);

        prompt.Should().Contain("Conditions: None");
    }

    // ── GroundednessResult.Passes ────────────────────────────────────

    [Theory]
    [InlineData(5, 3, true)]
    [InlineData(4, 3, true)]
    [InlineData(3, 3, true)]
    [InlineData(2, 3, false)]
    [InlineData(1, 3, false)]
    [InlineData(5, 5, true)]
    [InlineData(4, 5, false)]
    public void GroundednessResult_Passes_RespectsThreshold(int score, int minScore, bool expected)
    {
        var result = new GroundednessResult
        {
            Score = score,
            Reasoning = "Test",
            EvaluationSucceeded = true
        };

        result.Passes(minScore).Should().Be(expected);
    }

    // ── EvaluateAsync — integration with mock IChatClient ───────────

    [Fact]
    public async Task EvaluateAsync_HighScore_ReturnsPassing()
    {
        var chatClient = Substitute.For<IChatClient>();
        chatClient.GetResponseAsync(
            Arg.Any<IEnumerable<ChatMessage>>(),
            Arg.Any<ChatOptions>(),
            Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ChatResponse(
                [new ChatMessage(ChatRole.Assistant, """{"groundednessScore": 5, "reasoning": "Fully grounded."}""")])));

        var evaluator = new VerdictGroundednessEvaluator(
            chatClient, Substitute.For<ILogger<VerdictGroundednessEvaluator>>());

        var result = await evaluator.EvaluateAsync(SampleFindings, SampleVerdict);

        result.Score.Should().Be(5);
        result.EvaluationSucceeded.Should().BeTrue();
        result.Passes(3).Should().BeTrue();
    }

    [Fact]
    public async Task EvaluateAsync_LowScore_ReturnsNonPassing()
    {
        var chatClient = Substitute.For<IChatClient>();
        chatClient.GetResponseAsync(
            Arg.Any<IEnumerable<ChatMessage>>(),
            Arg.Any<ChatOptions>(),
            Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ChatResponse(
                [new ChatMessage(ChatRole.Assistant, """{"groundednessScore": 1, "reasoning": "Verdict is fabricated."}""")])));

        var evaluator = new VerdictGroundednessEvaluator(
            chatClient, Substitute.For<ILogger<VerdictGroundednessEvaluator>>());

        var result = await evaluator.EvaluateAsync(SampleFindings, SampleVerdict);

        result.Score.Should().Be(1);
        result.EvaluationSucceeded.Should().BeTrue();
        result.Passes(3).Should().BeFalse();
    }

    [Fact]
    public async Task EvaluateAsync_LlmFailure_FailsClosed()
    {
        var chatClient = Substitute.For<IChatClient>();
        chatClient.GetResponseAsync(
            Arg.Any<IEnumerable<ChatMessage>>(),
            Arg.Any<ChatOptions>(),
            Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("Service unavailable"));

        var evaluator = new VerdictGroundednessEvaluator(
            chatClient, Substitute.For<ILogger<VerdictGroundednessEvaluator>>());

        var result = await evaluator.EvaluateAsync(SampleFindings, SampleVerdict);

        result.Score.Should().Be(0, "fail-closed defaults to zero score");
        result.EvaluationSucceeded.Should().BeFalse();
        result.Passes(3).Should().BeFalse("verdict should escalate to human review when judge is unavailable");
    }

    [Fact]
    public async Task EvaluateAsync_SendsCorrectSystemPrompt()
    {
        var chatClient = Substitute.For<IChatClient>();
        IEnumerable<ChatMessage>? capturedMessages = null;

        chatClient.GetResponseAsync(
            Arg.Any<IEnumerable<ChatMessage>>(),
            Arg.Any<ChatOptions>(),
            Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                capturedMessages = callInfo.Arg<IEnumerable<ChatMessage>>();
                return Task.FromResult(new ChatResponse(
                    [new ChatMessage(ChatRole.Assistant, """{"groundednessScore": 4, "reasoning": "OK"}""")]));
            });

        var evaluator = new VerdictGroundednessEvaluator(
            chatClient, Substitute.For<ILogger<VerdictGroundednessEvaluator>>());

        await evaluator.EvaluateAsync(SampleFindings, SampleVerdict);

        capturedMessages.Should().NotBeNull();
        var messages = capturedMessages!.ToList();
        messages.Should().HaveCount(2);
        messages[0].Role.Should().Be(ChatRole.System);
        messages[0].Text.Should().Contain("quality assurance judge");
        messages[1].Role.Should().Be(ChatRole.User);
        messages[1].Text.Should().Contain(SampleFindings);
    }

    [Fact]
    public void JudgeSystemPrompt_ContainsScoreCriteria()
    {
        GroundednessJudgePrompts.VerdictJudgeSystemPrompt.Should().Contain("1 to 5");
        GroundednessJudgePrompts.VerdictJudgeSystemPrompt.Should().Contain("groundednessScore");
        GroundednessJudgePrompts.VerdictJudgeSystemPrompt.Should().Contain("grounded");
    }
}
