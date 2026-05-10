using System.Text.Json;
using System.Text.Json.Serialization;
using Cascade.CTL.Agent.Evals;
using Cascade.CTL.Agent.Application.Configuration;
using Cascade.CTL.Agent.Application.Orchestration;
using Cascade.CTL.Agent.Domain.Models;
using Cascade.CTL.Agent.Guardrails;
using Cascade.CTL.Agent.Infrastructure;
using Cascade.CTL.Agent.Host;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

Console.WriteLine("╔══════════════════════════════════════════════════════════════╗");
Console.WriteLine("║  Cascade 2.0 — CTL Agent Evaluation Suite                  ║");
Console.WriteLine("╚══════════════════════════════════════════════════════════════╝");
Console.WriteLine();

var builder = Microsoft.Extensions.Hosting.Host.CreateDefaultBuilder(args);
builder.ConfigureCTLAgent();
using var host = builder.Build();

var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("CTLAgent.Evals");
var results = new List<EvalResult>();
var qualityResults = new List<(string CaseName, ReflectionQualityResult? Quality)>();
var jsonOptions = new JsonSerializerOptions
{
    WriteIndented = true,
    Converters = { new JsonStringEnumConverter() }
};

try
{
    // Initialize MCP
    var toolProvider = host.Services.GetRequiredService<IMcpToolProvider>();
    logger.LogInformation("Initializing MCP Tool Provider for evals...");
    await Task.Delay(2000);
    await toolProvider.InitializeAsync();

    var orchestrator = host.Services.GetRequiredService<ICTLEvaluationOrchestrator>();

    // Set up Microsoft AI Evaluators for reflection quality scoring.
    // Uses the same IChatClient as evaluator judge — in production, use a separate model.
    var evaluatorChatClient = host.Services.GetRequiredService<IChatClient>();
    var reflectionQualityEvaluator = new ReflectionQualityEvaluator(evaluatorChatClient);

    foreach (var evalCase in EvalRunner.EvalCases)
    {
        Console.WriteLine($"\n{new string('=', 60)}");
        Console.WriteLine($"Running eval: {evalCase.Name}");
        Console.WriteLine($"Description: {evalCase.Description}");
        Console.WriteLine(new string('=', 60));

        try
        {
            var request = new CTLEvaluationRequest
            {
                AssetId = evalCase.AssetId,
                WorkflowInstanceId = $"EVAL-{Guid.NewGuid():N}"[..16],
                RequestedBy = "EvalRunner"
            };

            var evaluationResult = await orchestrator.EvaluateAsync(request);
            var evalResult = EvalRunner.Evaluate(evalCase, evaluationResult);
            results.Add(evalResult);
            EvalRunner.PrintResult(evalResult);

            // Run Microsoft AI Evaluators (Groundedness + Relevance) on the reflection output
            try
            {
                var investigationFindings = $"""
                    Legal: {evaluationResult.Verdict.EvidenceTrail?.Length ?? 0} evidence items.
                    Reflection: {evaluationResult.Verdict.ReflectionLog}
                    Evidence: {string.Join("; ", evaluationResult.Verdict.EvidenceTrail ?? [])}
                    """;

                var qualityResult = await reflectionQualityEvaluator.EvaluateAsync(
                    investigationFindings, evaluationResult);
                qualityResults.Add((evalCase.Name, qualityResult));

                Console.ForegroundColor = qualityResult.Passed ? ConsoleColor.Green : ConsoleColor.Yellow;
                Console.WriteLine($"  📊 AI Evaluators — Groundedness: {qualityResult.GroundednessScore:F1}/5 ({qualityResult.GroundednessRating}), Relevance: {qualityResult.RelevanceScore:F1}/5 ({qualityResult.RelevanceRating})");
                Console.ResetColor();
            }
            catch (Exception qualityEx)
            {
                logger.LogWarning(qualityEx, "AI Quality Evaluators failed for {CaseName} (non-fatal)", evalCase.Name);
                qualityResults.Add((evalCase.Name, null));
                Console.ForegroundColor = ConsoleColor.DarkYellow;
                Console.WriteLine($"  📊 AI Evaluators — Skipped (requires Azure OpenAI): {qualityEx.Message}");
                Console.ResetColor();
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Eval case {CaseName} threw exception", evalCase.Name);
            var failResult = new EvalResult
            {
                CaseName = evalCase.Name,
                Passed = false,
                EvaluationResult = null,
                Issues = [$"Exception: {ex.Message}"],
                Duration = TimeSpan.Zero
            };
            results.Add(failResult);
            EvalRunner.PrintResult(failResult);
        }
    }
}
catch (Exception ex)
{
    logger.LogError(ex, "Eval suite failed to initialize");
    Console.ForegroundColor = ConsoleColor.Red;
    Console.WriteLine($"\nFATAL: {ex.Message}");
    Console.ResetColor();
    return 1;
}

// Summary
Console.WriteLine($"\n{new string('=', 60)}");
Console.WriteLine("EVALUATION SUMMARY");
Console.WriteLine(new string('=', 60));

var passed = results.Count(r => r.Passed);
var total = results.Count;
Console.ForegroundColor = passed == total ? ConsoleColor.Green : ConsoleColor.Yellow;
Console.WriteLine($"  {passed}/{total} eval cases passed");
Console.ResetColor();

Console.WriteLine($"\n  Full results:\n{JsonSerializer.Serialize(results.Select(r => new {
    r.CaseName,
    r.Passed,
    Verdict = r.EvaluationResult?.Verdict.Verdict.ToString(),
    Confidence = r.EvaluationResult?.Verdict.ConfidenceScore,
    r.Issues,
    DurationSeconds = r.Duration.TotalSeconds
}), jsonOptions)}");

// AI Evaluator Quality Summary
if (qualityResults.Any(q => q.Quality != null))
{
    Console.WriteLine($"\n{new string('─', 60)}");
    Console.WriteLine("AI EVALUATOR QUALITY SCORES (Microsoft.Extensions.AI.Evaluation)");
    Console.WriteLine(new string('─', 60));
    foreach (var (caseName, quality) in qualityResults)
    {
        if (quality != null)
        {
            Console.ForegroundColor = quality.Passed ? ConsoleColor.Green : ConsoleColor.Yellow;
            Console.WriteLine($"  {caseName}:");
            Console.WriteLine($"    Groundedness: {quality.GroundednessScore:F1}/5 ({quality.GroundednessRating})");
            Console.WriteLine($"    Relevance:    {quality.RelevanceScore:F1}/5 ({quality.RelevanceRating})");
            Console.ResetColor();
        }
        else
        {
            Console.ForegroundColor = ConsoleColor.DarkYellow;
            Console.WriteLine($"  {caseName}: AI Evaluators skipped");
            Console.ResetColor();
        }
    }
}

return passed == total ? 0 : 1;
