using Cascade.CTL.Agent.Application.Prompts;
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
        var userPrompt = GroundednessJudgePrompts.BuildVerdictUserPrompt(investigationFindings, verdict);

        try
        {
            var messages = new List<ChatMessage>
            {
                new(ChatRole.System, GroundednessJudgePrompts.VerdictJudgeSystemPrompt),
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
