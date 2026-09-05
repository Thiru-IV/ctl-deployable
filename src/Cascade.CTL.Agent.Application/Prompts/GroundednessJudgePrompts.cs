namespace Cascade.CTL.Agent.Application.Prompts;

using Cascade.CTL.Agent.Domain.Models;

/// <summary>
/// Prompts for the groundedness LLM-as-judge.
/// Two output contracts share one scoring rubric: JSON for the runtime quality gate
/// (VerdictGroundednessEvaluator) and S-tags for the Microsoft.Extensions.AI.Evaluation
/// pipeline (CTLGroundednessEvaluator). Keeping both here prevents the rubrics drifting apart.
/// </summary>
public static class GroundednessJudgePrompts
{
    private const string ScoringRubric = """
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
        """;

    private const string ScopeConstraint =
        "Do NOT evaluate the correctness of the verdict itself — only whether it is grounded in the provided findings.";

    /// <summary>Runtime quality gate variant — the judge must reply with a single JSON object.</summary>
    public const string VerdictJudgeSystemPrompt = ScoringRubric + "\n\n" + """
        Respond with ONLY a JSON object:
        {
            "groundednessScore": <1-5>,
            "reasoning": "<one paragraph explaining your score>"
        }
        """ + "\n\n" + ScopeConstraint;

    /// <summary>Offline eval variant — S-tag format parsed by CTLGroundednessEvaluator.ParseScore.</summary>
    public const string EvaluationJudgeSystemPrompt = ScoringRubric + "\n\n" + """
        Provide your assessment in the following format:
        <S0>Let's think step by step: your chain of thoughts</S0>
        <S1>your explanation</S1>
        <S2>your Score (integer 1-5)</S2>
        """ + "\n\n" + ScopeConstraint;

    /// <summary>Builds the judge user prompt from a parsed verdict (runtime quality gate).</summary>
    public static string BuildVerdictUserPrompt(string investigationFindings, CTLVerdictDto verdict) =>
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

    /// <summary>Builds the judge user prompt from raw model response text (offline eval pipeline).</summary>
    public static string BuildEvaluationUserPrompt(string groundingContext, string modelResponseText) =>
        $"""
        ## Investigation Findings (CONTEXT)
        {groundingContext}

        ## Verdict to Evaluate (RESPONSE)
        {modelResponseText}
        """;
}
