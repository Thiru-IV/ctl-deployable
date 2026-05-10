using System.Text.Json;
using Cascade.CTL.Agent.Domain.Enums;
using Microsoft.Extensions.AI;

namespace Cascade.CTL.Agent.Application.Orchestration;

/// <summary>
/// Shared static utilities for parsing LLM planning output and counting tool calls.
/// Used by workflow executors and unit tests.
/// </summary>
public static class PlanParser
{
    private static JsonSerializerOptions JsonOptions => VerdictParser.JsonOptions;

    /// <summary>
    /// Extracts required verification domains from the LLM's planning phase JSON output.
    /// Falls back to all 3 domains if parsing fails — never silently skips checks.
    /// The LLM may emit multiple JSON blocks (e.g. echoed asset profile + the actual plan),
    /// so we scan all balanced {...} regions and pick the one carrying `requiredDomains`.
    /// </summary>
    public static HashSet<VerificationDomain> ParseRequiredDomains(string planJson)
    {
        foreach (var candidate in EnumerateJsonObjects(planJson))
        {
            try
            {
                var plan = JsonSerializer.Deserialize<PlanJsonResponse>(candidate, JsonOptions);
                if (plan?.RequiredDomains is { Length: > 0 })
                {
                    var parsed = new HashSet<VerificationDomain>();
                    foreach (var domain in plan.RequiredDomains)
                    {
                        if (Enum.TryParse<VerificationDomain>(domain, ignoreCase: true, out var d))
                            parsed.Add(d);
                    }
                    if (parsed.Count > 0)
                        return parsed;
                }
            }
            catch (JsonException)
            {
                // Try the next candidate block
            }
        }

        // Safe default: run ALL domains if no parseable plan found — never silently skip checks
        return [VerificationDomain.Legal, VerificationDomain.Valuation, VerificationDomain.Occupancy];
    }

    /// <summary>
    /// Yields every balanced top-level JSON object substring found in <paramref name="text"/>,
    /// honouring string literals and escapes so braces inside strings are not mistaken for
    /// structural braces. Robust to any number of objects, fenced code blocks, and prose
    /// surrounding the JSON.
    /// </summary>
    private static IEnumerable<string> EnumerateJsonObjects(string text)
    {
        if (string.IsNullOrEmpty(text)) yield break;

        var depth = 0;
        var start = -1;
        var inString = false;
        var escape = false;

        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];

            if (inString)
            {
                if (escape) { escape = false; }
                else if (ch == '\\') { escape = true; }
                else if (ch == '"') { inString = false; }
                continue;
            }

            if (ch == '"') { inString = true; continue; }

            if (ch == '{')
            {
                if (depth == 0) start = i;
                depth++;
            }
            else if (ch == '}')
            {
                if (depth > 0)
                {
                    depth--;
                    if (depth == 0 && start >= 0)
                    {
                        yield return text[start..(i + 1)];
                        start = -1;
                    }
                }
            }
        }
    }

    /// <summary>
    /// Counts actual tool invocations (FunctionCallContent items) from a ChatResponse.
    /// Counts structured content, not string heuristics.
    /// </summary>
    public static int CountActualToolCalls(ChatResponse response)
    {
        if (response.Messages == null) return 0;

        return response.Messages
            .SelectMany(m => m.Contents)
            .OfType<FunctionCallContent>()
            .Count();
    }

    internal sealed record PlanJsonResponse
    {
        public string? AssetId { get; init; }
        public string[]? RequiredDomains { get; init; }
        public string[]? RelevantPolicies { get; init; }
        public string? AssetProfileSummary { get; init; }
        public string? PlanRationale { get; init; }
    }
}
