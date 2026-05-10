using Cascade.CTL.Agent.Domain.Models;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.Evaluation;
using Microsoft.Extensions.AI.Evaluation.Quality;

namespace Cascade.CTL.Agent.Application.Orchestration;

/// <summary>
/// CTL-tuned groundedness evaluator that implements <see cref="IEvaluator"/>.
/// Uses the same domain-specific prompt as the runtime <see cref="VerdictGroundednessEvaluator"/>
/// but conforms to the Microsoft.Extensions.AI.Evaluation framework, making it usable in both
/// offline eval pipelines and (future) as a drop-in replacement for the runtime quality gate.
///
/// Key design choices:
/// - Prompt is tuned for CTL verdict structures (not generic).
/// - Scoring rubric matches the runtime quality gate (1-5, CTL-specific definitions).
/// - Fail-closed: returns score 0 with an error diagnostic on any failure.
/// - Accepts investigation findings via <see cref="GroundednessEvaluatorContext"/>.
/// </summary>
public sealed class CTLGroundednessEvaluator : IEvaluator
{
    public static string CTLGroundednessMetricName => "CTL.Groundedness";

    public IReadOnlyCollection<string> EvaluationMetricNames { get; } = [CTLGroundednessMetricName];

    private static readonly ChatOptions _chatOptions = new()
    {
        Temperature = 0.0f,
        MaxOutputTokens = 800,
        TopP = 1.0f,
        PresencePenalty = 0.0f,
        FrequencyPenalty = 0.0f,
        ResponseFormat = ChatResponseFormat.Text
    };

    // Same CTL-tuned prompt used by the runtime VerdictGroundednessEvaluator.
    internal const string SystemPrompt = """
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

        Provide your assessment in the following format:
        <S0>Let's think step by step: your chain of thoughts</S0>
        <S1>your explanation</S1>
        <S2>your Score (integer 1-5)</S2>

        Do NOT evaluate the correctness of the verdict itself — only whether it is grounded in the provided findings.
        """;

    public async ValueTask<EvaluationResult> EvaluateAsync(
        IEnumerable<ChatMessage> messages,
        ChatResponse modelResponse,
        ChatConfiguration? chatConfiguration = null,
        IEnumerable<EvaluationContext>? additionalContext = null,
        CancellationToken cancellationToken = default)
    {
        var metric = new NumericMetric(CTLGroundednessMetricName);
        var result = new EvaluationResult(metric);

        if (chatConfiguration is null)
        {
            metric.AddDiagnostics(
                EvaluationDiagnostic.Error("ChatConfiguration is required but was not provided."));
            return result;
        }

        if (string.IsNullOrWhiteSpace(modelResponse?.Text))
        {
            metric.AddDiagnostics(
                EvaluationDiagnostic.Error("The model response supplied for evaluation was null or empty."));
            return result;
        }

        var groundingContext = additionalContext?
            .OfType<GroundednessEvaluatorContext>()
            .FirstOrDefault();

        if (groundingContext is null)
        {
            metric.AddDiagnostics(
                EvaluationDiagnostic.Error(
                    $"A {nameof(GroundednessEvaluatorContext)} with investigation findings was not found."));
            return result;
        }

        try
        {
            var evaluationMessages = new List<ChatMessage>
            {
                new(ChatRole.System, SystemPrompt),
                new(ChatRole.User, $"""
                    ## Investigation Findings (CONTEXT)
                    {groundingContext.GroundingContext}

                    ## Verdict to Evaluate (RESPONSE)
                    {modelResponse.Text}
                    """)
            };

            var response = await chatConfiguration.ChatClient.GetResponseAsync(
                evaluationMessages, _chatOptions, cancellationToken);

            var score = ParseScore(response.Text ?? string.Empty);
            if (score.HasValue)
            {
                metric.Value = score.Value;
                metric.Interpretation = score.Value switch
                {
                    >= 4 => new EvaluationMetricInterpretation
                    {
                        Rating = EvaluationRating.Good,
                        Failed = false
                    },
                    3 => new EvaluationMetricInterpretation
                    {
                        Rating = EvaluationRating.Average,
                        Failed = false
                    },
                    _ => new EvaluationMetricInterpretation
                    {
                        Rating = EvaluationRating.Poor,
                        Failed = true
                    }
                };
            }
            else
            {
                // Fail-closed: unparseable response = score 0 = failed.
                metric.Value = 0;
                metric.Interpretation = new EvaluationMetricInterpretation
                {
                    Rating = EvaluationRating.Inconclusive,
                    Failed = true
                };
                metric.AddDiagnostics(
                    EvaluationDiagnostic.Error("Could not parse groundedness score from judge response."));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Fail-closed: exception = score 0 = failed.
            metric.Value = 0;
            metric.Interpretation = new EvaluationMetricInterpretation
            {
                Rating = EvaluationRating.Inconclusive,
                Failed = true
            };
            metric.AddDiagnostics(
                EvaluationDiagnostic.Error(
                    $"Groundedness evaluation failed (fail-closed): {ex.Message}"));
        }

        return result;
    }

    /// <summary>
    /// Parses the integer score from the <![CDATA[<S2>]]> tag in the judge response.
    /// </summary>
    internal static int? ParseScore(string responseText)
    {
        const string startTag = "<S2>";
        const string endTag = "</S2>";
        var start = responseText.IndexOf(startTag, StringComparison.OrdinalIgnoreCase);
        var end = responseText.IndexOf(endTag, StringComparison.OrdinalIgnoreCase);

        if (start >= 0 && end > start)
        {
            var scoreText = responseText[(start + startTag.Length)..end].Trim();
            if (int.TryParse(scoreText, out var score) && score is >= 1 and <= 5)
            {
                return score;
            }
        }

        return null;
    }
}
