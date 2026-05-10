using Cascade.CTL.Agent.Domain.Models;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.Evaluation;
using Microsoft.Extensions.AI.Evaluation.Quality;

namespace Cascade.CTL.Agent.Evals;

/// <summary>
/// Evaluates the quality of the reflection phase output using Microsoft AI Evaluators.
/// Scores the verdict response for Groundedness (is verdict grounded in investigation findings?)
/// and Relevance (is the verdict relevant to the asset listing readiness question?).
/// </summary>
public sealed class ReflectionQualityEvaluator
{
    private readonly ChatConfiguration _chatConfiguration;

    public ReflectionQualityEvaluator(IChatClient evaluatorChatClient)
    {
        _chatConfiguration = new ChatConfiguration(evaluatorChatClient);
    }

    /// <summary>
    /// Runs GroundednessEvaluator and RelevanceEvaluator against the reflection output.
    /// </summary>
    /// <param name="investigationFindings">The concatenated investigation findings (context the verdict should be grounded in).</param>
    /// <param name="evaluationResult">The CTL evaluation result containing the verdict.</param>
    /// <returns>Scored results with Groundedness and Relevance metrics.</returns>
    public async Task<ReflectionQualityResult> EvaluateAsync(
        string investigationFindings,
        CTLEvaluationResult evaluationResult,
        CancellationToken cancellationToken = default)
    {
        // Build the conversation that produced the reflection verdict
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System,
                "You are a CTL (Clear-To-List) evaluation agent. Your task is to review investigation findings " +
                "from Legal, Valuation, and Occupancy agents and produce a structured verdict with confidence score, " +
                "conditions, and evidence trail."),
            new(ChatRole.User,
                $"Review the following investigation findings and produce a CTL verdict:\n\n{investigationFindings}")
        };

        // The reflection output is what the LLM produced
        var verdictText = FormatVerdictForEvaluation(evaluationResult);
        var response = new ChatResponse([new ChatMessage(ChatRole.Assistant, verdictText)]);

        // Provide investigation findings as grounding context
        var groundingContext = new GroundednessEvaluatorContext(investigationFindings);

        // Run evaluators
        var groundednessEvaluator = new GroundednessEvaluator();
        var relevanceEvaluator = new RelevanceEvaluator();

        var groundednessResult = await groundednessEvaluator.EvaluateAsync(
            messages, response, _chatConfiguration, [groundingContext], cancellationToken);

        var relevanceResult = await relevanceEvaluator.EvaluateAsync(
            messages, response, _chatConfiguration, cancellationToken: cancellationToken);

        // Extract numeric scores
        var groundedness = groundednessResult.Get<NumericMetric>(GroundednessEvaluator.GroundednessMetricName);
        var relevance = relevanceResult.Get<NumericMetric>(RelevanceEvaluator.RelevanceMetricName);

        return new ReflectionQualityResult
        {
            GroundednessScore = groundedness.Value ?? 0.0,
            GroundednessRating = groundedness.Interpretation?.Rating ?? EvaluationRating.Inconclusive,
            GroundednessFailed = groundedness.Interpretation?.Failed ?? true,
            RelevanceScore = relevance.Value ?? 0.0,
            RelevanceRating = relevance.Interpretation?.Rating ?? EvaluationRating.Inconclusive,
            RelevanceFailed = relevance.Interpretation?.Failed ?? true,
            HasDiagnostics = groundedness.ContainsDiagnostics() || relevance.ContainsDiagnostics()
        };
    }

    private static string FormatVerdictForEvaluation(CTLEvaluationResult result)
    {
        var verdict = result.Verdict;
        return $"""
            Verdict: {verdict.Verdict}
            Confidence Score: {verdict.ConfidenceScore:F2}
            Conditions: {(verdict.Conditions.Length > 0 ? string.Join("; ", verdict.Conditions) : "None")}
            Evidence Trail: {string.Join("; ", verdict.EvidenceTrail)}
            Reflection Log: {verdict.ReflectionLog}
            """;
    }
}

/// <summary>
/// Results from Microsoft AI Evaluators scoring the reflection output.
/// </summary>
public sealed record ReflectionQualityResult
{
    /// <summary>Groundedness score (1-5). Higher = more grounded in investigation findings.</summary>
    public required double GroundednessScore { get; init; }
    public required EvaluationRating GroundednessRating { get; init; }
    public required bool GroundednessFailed { get; init; }

    /// <summary>Relevance score (1-5). Higher = more relevant to the CTL evaluation question.</summary>
    public required double RelevanceScore { get; init; }
    public required EvaluationRating RelevanceRating { get; init; }
    public required bool RelevanceFailed { get; init; }

    /// <summary>Whether any evaluator reported diagnostics (errors/warnings).</summary>
    public required bool HasDiagnostics { get; init; }

    /// <summary>Overall pass: both evaluators scored Good or Exceptional with no diagnostics.</summary>
    public bool Passed =>
        !GroundednessFailed && !RelevanceFailed && !HasDiagnostics;
}
