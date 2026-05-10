using Cascade.CTL.Agent.Domain.Enums;
using Cascade.CTL.Agent.Domain.Models;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Cascade.CTL.Agent.Application.Orchestration;

/// <summary>
/// Production quality gate that uses an LLM-as-judge to verify whether the orchestrator's
/// CTL verdict is actually grounded in the investigation findings.
/// Runs as a post-Reflection check: if the verdict is not grounded (score below threshold),
/// the verdict is escalated to <see cref="CTLVerdict.NeedsHumanReview"/>.
/// </summary>
public sealed class VerdictGroundednessEvaluator
{
    private readonly IChatClient _chatClient;
    private readonly ILogger<VerdictGroundednessEvaluator> _logger;

    internal const string JudgeSystemPrompt = """
        You are a quality assurance judge for a Clear-To-List (CTL) property evaluation system.
        Your ONLY task is to determine whether a verdict is grounded in the investigation findings.

        You will be given:
        1. Investigation findings from Legal, Valuation, and Occupancy agents.
        2. A verdict (Clear, ClearWithConditions, NotClear, or NeedsHumanReview) with a confidence score and evidence trail.

        Score the verdict's groundedness on a scale of 1 to 5:
        - 5: Every claim in the verdict is directly supported by the findings. Evidence trail accurately cites findings.
        - 4: The verdict is well-supported with minor omissions. No contradictions.
        - 3: The verdict is partially supported but makes claims not found in findings, or ignores relevant findings.
        - 2: The verdict contradicts the findings in significant ways, or the confidence score is inconsistent with the evidence.
        - 1: The verdict is fabricated or completely unsupported by the findings.

        Respond with ONLY a JSON object:
        {
            "groundednessScore": <1-5>,
            "reasoning": "<one paragraph explaining your score>"
        }

        Do NOT evaluate the correctness of the verdict itself — only whether it is grounded in the provided findings.
        """;

    public VerdictGroundednessEvaluator(
        IChatClient chatClient,
        ILogger<VerdictGroundednessEvaluator> logger)
    {
        _chatClient = chatClient ?? throw new ArgumentNullException(nameof(chatClient));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// It's a judge, not an agent. No tool-calling loop, no multi-turn.
    /// Evaluates whether the verdict is grounded in the investigation findings.
    /// Returns the groundedness score (1-5) and reasoning.
    /// </summary>
    public async Task<GroundednessResult> EvaluateAsync(
        string investigationFindings,
        CTLVerdictDto verdict,
        CancellationToken cancellationToken = default)
    {
        var userPrompt = BuildUserPrompt(investigationFindings, verdict);

        try
        {
            var messages = new List<ChatMessage>
            {
                new(ChatRole.System, JudgeSystemPrompt),
                new(ChatRole.User, userPrompt)
            };

            var options = new ChatOptions { Temperature = 0.0f };

            var response = await _chatClient.GetResponseAsync(messages, options, cancellationToken);
            var responseText = response.Text ?? string.Empty;

            return ParseJudgeResponse(responseText);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Verdict groundedness evaluation failed — escalating to human review (fail-closed)");
            return new GroundednessResult
            {
                Score = 0,
                Reasoning = "Groundedness evaluation unavailable — defaulted to fail (fail-closed). Verdict should be escalated to human review.",
                EvaluationSucceeded = false
            };
        }
    }

    internal static string BuildUserPrompt(string investigationFindings, CTLVerdictDto verdict) =>
        $"""
        ## Investigation Findings
        {investigationFindings}

        ## Verdict to Evaluate
        Verdict: {verdict.Verdict}
        Confidence Score: {verdict.ConfidenceScore:F2}
        Conditions: {(verdict.Conditions.Length > 0 ? string.Join("; ", verdict.Conditions) : "None")}
        Evidence Trail: {string.Join("; ", verdict.EvidenceTrail)}
        Reflection Log: {verdict.ReflectionLog}
        """;

    internal static GroundednessResult ParseJudgeResponse(string responseText)
    {
        try
        {
            var jsonStart = responseText.IndexOf('{');
            var jsonEnd = responseText.LastIndexOf('}');
            if (jsonStart >= 0 && jsonEnd > jsonStart)
            {
                var cleanJson = responseText[jsonStart..(jsonEnd + 1)];
                var parsed = System.Text.Json.JsonSerializer.Deserialize<JudgeResponse>(
                    cleanJson,
                    new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                if (parsed != null && parsed.GroundednessScore is >= 1 and <= 5)
                {
                    return new GroundednessResult
                    {
                        Score = parsed.GroundednessScore,
                        Reasoning = parsed.Reasoning ?? "No reasoning provided.",
                        EvaluationSucceeded = true
                    };
                }
            }
        }
        catch (System.Text.Json.JsonException)
        {
            // Fall through to default
        }

        return new GroundednessResult
        {
            Score = 0,
            Reasoning = "Failed to parse judge response.",
            EvaluationSucceeded = false
        };
    }

    private sealed record JudgeResponse
    {
        public int GroundednessScore { get; init; }
        public string? Reasoning { get; init; }
    }
}

/// <summary>
/// Result of the post-Reflection groundedness quality gate.
/// </summary>
public sealed record GroundednessResult
{
    /// <summary>Groundedness score from 1 (fabricated) to 5 (fully grounded).</summary>
    public required int Score { get; init; }

    /// <summary>The judge's reasoning for the score.</summary>
    public required string Reasoning { get; init; }

    /// <summary>Whether the LLM-as-judge call succeeded. False means fail-open default was used.</summary>
    public required bool EvaluationSucceeded { get; init; }

    /// <summary>Whether the verdict passes the quality gate at the given minimum score threshold.</summary>
    public bool Passes(int minimumScore) => Score >= minimumScore;
}
