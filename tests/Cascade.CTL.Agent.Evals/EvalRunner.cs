using System.Text.Json;
using System.Text.Json.Serialization;
using Cascade.CTL.Agent.Application.Configuration;
using Cascade.CTL.Agent.Application.Orchestration;
using Cascade.CTL.Agent.Domain.Enums;
using Cascade.CTL.Agent.Domain.Models;
using Cascade.CTL.Agent.Guardrails;
using Cascade.CTL.Agent.Infrastructure;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cascade.CTL.Agent.Evals;

public sealed record EvalCase
{
    public required string Name { get; init; }
    public required string AssetId { get; init; }
    public required CTLVerdict[] AcceptableVerdicts { get; init; }
    public double? MinConfidence { get; init; }
    public double? MaxConfidence { get; init; }
    public required string Description { get; init; }
}

public sealed record EvalResult
{
    public required string CaseName { get; init; }
    public required bool Passed { get; init; }
    public required CTLEvaluationResult? EvaluationResult { get; init; }
    public required string[] Issues { get; init; }
    public required TimeSpan Duration { get; init; }
}

public static class EvalRunner
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static readonly EvalCase[] EvalCases =
    [
        new EvalCase
        {
            Name = "TX Foreclosure — Clean Path",
            AssetId = "ASSET-TX-001",
            AcceptableVerdicts = [CTLVerdict.Clear, CTLVerdict.ClearWithConditions],
            MinConfidence = 0.75,
            Description = "Texas foreclosure, Tier 1 seller, vacant. Clean title, current BPO, no violations. Expected: Clear or ClearWithConditions with high confidence."
        },
        new EvalCase
        {
            Name = "CA REO — Contradictions",
            AssetId = "ASSET-CA-002",
            AcceptableVerdicts = [CTLVerdict.ClearWithConditions, CTLVerdict.NeedsHumanReview],
            MaxConfidence = 0.95,
            Description = "California REO, Tier 2, occupied, stale BPO, open liens, HOA delinquent, eviction in progress. Expected: ClearWithConditions or NeedsHumanReview due to contradictions and unresolved issues."
        }
    ];

    public static EvalResult Evaluate(EvalCase evalCase, CTLEvaluationResult? result)
    {
        var issues = new List<string>();

        if (result == null)
        {
            return new EvalResult
            {
                CaseName = evalCase.Name,
                Passed = false,
                EvaluationResult = null,
                Issues = ["Evaluation returned null result"],
                Duration = TimeSpan.Zero
            };
        }

        // Check verdict is in acceptable range
        if (!evalCase.AcceptableVerdicts.Contains(result.Verdict.Verdict))
        {
            issues.Add($"Verdict '{result.Verdict.Verdict}' not in acceptable range: [{string.Join(", ", evalCase.AcceptableVerdicts)}]");
        }

        // Check confidence thresholds
        if (evalCase.MinConfidence.HasValue && result.Verdict.ConfidenceScore < evalCase.MinConfidence.Value)
        {
            issues.Add($"Confidence {result.Verdict.ConfidenceScore:F2} below minimum {evalCase.MinConfidence:F2}");
        }

        if (evalCase.MaxConfidence.HasValue && result.Verdict.ConfidenceScore > evalCase.MaxConfidence.Value)
        {
            issues.Add($"Confidence {result.Verdict.ConfidenceScore:F2} above maximum {evalCase.MaxConfidence:F2}");
        }

        // Check evidence trail exists
        if (result.Verdict.EvidenceTrail.Length == 0)
        {
            issues.Add("Evidence trail is empty — agent should provide supporting evidence");
        }

        // Check reflection log exists
        if (string.IsNullOrWhiteSpace(result.Verdict.ReflectionLog))
        {
            issues.Add("Reflection log is empty — orchestrator should document reasoning");
        }

        return new EvalResult
        {
            CaseName = evalCase.Name,
            Passed = issues.Count == 0,
            EvaluationResult = result,
            Issues = issues.ToArray(),
            Duration = result.EvaluationDuration
        };
    }

    public static void PrintResult(EvalResult result)
    {
        var icon = result.Passed ? "✅" : "❌";
        Console.ForegroundColor = result.Passed ? ConsoleColor.Green : ConsoleColor.Red;
        Console.WriteLine($"\n{icon} EVAL: {result.CaseName}");
        Console.ResetColor();

        if (result.EvaluationResult != null)
        {
            Console.WriteLine($"  Verdict: {result.EvaluationResult.Verdict.Verdict}");
            Console.WriteLine($"  Confidence: {result.EvaluationResult.Verdict.ConfidenceScore:F2}");
            Console.WriteLine($"  Duration: {result.Duration.TotalSeconds:F1}s");
            Console.WriteLine($"  Tokens: {result.EvaluationResult.TotalTokensUsed}");
        }

        if (result.Issues.Length > 0)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("  Issues:");
            foreach (var issue in result.Issues)
                Console.WriteLine($"    ⚠ {issue}");
            Console.ResetColor();
        }
    }
}
