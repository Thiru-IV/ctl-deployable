using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Cascade.CTL.Agent.Application.Configuration;
using Cascade.CTL.Agent.Domain.Enums;
using Cascade.CTL.Agent.Domain.Models;
using Microsoft.Extensions.Logging;

namespace Cascade.CTL.Agent.Application.Orchestration;

/// <summary>
/// Shared verdict parsing logic used by both imperative and workflow orchestrators.
/// </summary>
internal static class VerdictParser
{
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
        WriteIndented = true
    };

    // Pre-compiled regex matching ```json ... ``` (case-insensitive, multiline). Greedy match
    // is intentional — we then iterate over all matches and pick the first containing a verdict.
    private static readonly Regex FencedJsonRegex = new(
        @"```\s*json\s*(?<body>.*?)```",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);

    internal static CTLVerdictDto ParseVerdict(
        string verdictJson,
        string assetId,
        string sessionId,
        ILogger logger,
        double humanReviewThreshold = 0.75,
        ReflectionDeterminismOptions? determinismOptions = null)
    {
        try
        {
            // ── Robust extraction (verdict-determinism v2 fix B) ──
            // The previous IndexOf('{')…LastIndexOf('}') heuristic could latch onto an embedded
            // citations array and silently mis-parse markdown narratives as a verdict. The
            // extractor below picks ONLY a JSON object that actually contains a "verdict" field.
            var cleanJson = TryExtractVerdictJson(verdictJson);
            if (cleanJson is not null)
            {
                var parsed = JsonSerializer.Deserialize<VerdictJsonResponse>(cleanJson, JsonOptions);
                if (parsed is not null && !string.IsNullOrWhiteSpace(parsed.Verdict))
                {
                    var parsedVerdict = ParseVerdictEnum(parsed.Verdict);

                    // ── Phase 1 v2: snap continuous LLM confidence to nearest discrete bucket ──
                    var rawConfidence = parsed.ConfidenceScore;
                    var confidence = SnapConfidenceToBucket(rawConfidence, determinismOptions, logger);

                    var conditions = parsed.Conditions ?? [];

                    // Enforce confidence→verdict consistency (configurable threshold):
                    // 1. LLM says NeedsHumanReview but confidence >= threshold → remap to ClearWithConditions
                    //    (add a condition explaining the remap so the user sees why)
                    // 2. LLM says Clear/ClearWithConditions but confidence < threshold → force NeedsHumanReview
                    // 3. LLM says NotClear but confidence < threshold → force NeedsHumanReview
                    //    (a low-confidence denial is as unreliable as a low-confidence approval)
                    if (parsedVerdict == CTLVerdict.NeedsHumanReview && confidence >= humanReviewThreshold)
                    {
                        logger.LogWarning(
                            "Verdict correction: LLM returned NeedsHumanReview with confidence {Confidence:F2} (>= {Threshold:F2}). Remapping to ClearWithConditions.",
                            confidence, humanReviewThreshold);
                        parsedVerdict = CTLVerdict.ClearWithConditions;
                        conditions = [.. conditions,
                            $"Verdict remapped from NeedsHumanReview to ClearWithConditions (confidence {confidence:F2} >= threshold {humanReviewThreshold:F2}). Review evidence trail for unresolved domain findings."];
                    }
                    else if (parsedVerdict is CTLVerdict.Clear or CTLVerdict.ClearWithConditions or CTLVerdict.NotClear && confidence < humanReviewThreshold)
                    {
                        logger.LogWarning(
                            "Verdict correction: LLM returned {Verdict} with confidence {Confidence:F2} (< {Threshold:F2}). Forcing NeedsHumanReview.",
                            parsedVerdict, confidence, humanReviewThreshold);
                        conditions = [.. conditions,
                            $"Verdict remapped from {parsedVerdict} to NeedsHumanReview (confidence {confidence:F2} < threshold {humanReviewThreshold:F2}). Low-confidence verdicts require human oversight."];
                        parsedVerdict = CTLVerdict.NeedsHumanReview;
                    }

                    return new CTLVerdictDto
                    {
                        Verdict = parsedVerdict,
                        ConfidenceScore = confidence,
                        Conditions = conditions,
                        EvidenceTrail = parsed.EvidenceTrail ?? [],
                        ReflectionLog = parsed.ReflectionLog ?? "No reflection log provided",
                        AssetId = assetId,
                        Timestamp = DateTime.UtcNow,
                        SessionId = sessionId,
                        Citations = parsed.Citations?.Select(c => new CitationEntry
                        {
                            Source = c.Source ?? "Unknown",
                            Reference = c.Reference,
                            Excerpt = c.Excerpt
                        }).ToArray(),
                        // Phase 1 v2: preserve raw LLM output for audit/drift analysis
                        LlmRawVerdict = parsed.Verdict,
                        LlmRawConfidence = rawConfidence
                    };
                }
            }

            // No verdict-bearing JSON object found. Log loudly so the operator can see what the LLM emitted.
            logger.LogWarning(
                "Verdict parsing: no JSON object containing a 'verdict' field was found in the LLM output. " +
                "Routing to NeedsHumanReview. Raw LLM output (truncated): {RawPreview}",
                verdictJson.Length > 800 ? verdictJson[..800] + "…" : verdictJson);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Failed to parse verdict JSON � returning NeedsHumanReview");
        }

        return new CTLVerdictDto
        {
            Verdict = CTLVerdict.NeedsHumanReview,
            ConfidenceScore = 0.0,
            Conditions = ["Verdict parsing failed � manual review required"],
            EvidenceTrail = ["Verdict parsing failed � raw response omitted for security"],
            ReflectionLog = "Failed to parse structured verdict from agent response",
            AssetId = assetId,
            Timestamp = DateTime.UtcNow,
            SessionId = sessionId
        };
    }

    /// <summary>
    /// Snaps a continuous LLM-reported confidence to the nearest configured discrete bucket
    /// (verdict-determinism v2, Phase 1). When discrete buckets are disabled or no buckets are
    /// configured, returns the input unchanged. Pass-through for negative/NaN inputs.
    /// </summary>
    internal static double SnapConfidenceToBucket(
        double rawConfidence,
        ReflectionDeterminismOptions? options,
        ILogger logger)
    {
        if (options is null || !options.UseDiscreteConfidenceBuckets) return rawConfidence;
        if (options.ConfidenceBuckets is null || options.ConfidenceBuckets.Length == 0) return rawConfidence;
        if (double.IsNaN(rawConfidence) || rawConfidence < 0.0) return rawConfidence;

        // Pick the bucket with smallest absolute distance; ties resolve to the lower bucket
        // (more conservative — prefers caution on borderline confidence).
        var bestBucket = options.ConfidenceBuckets[0];
        var bestDistance = Math.Abs(rawConfidence - bestBucket);
        for (var i = 1; i < options.ConfidenceBuckets.Length; i++)
        {
            var bucket = options.ConfidenceBuckets[i];
            var distance = Math.Abs(rawConfidence - bucket);
            if (distance < bestDistance)
            {
                bestBucket = bucket;
                bestDistance = distance;
            }
        }

        if (Math.Abs(bestBucket - rawConfidence) > 1e-9)
        {
            logger.LogDebug(
                "Confidence snapped to discrete bucket: raw={Raw:F4} → bucket={Bucket:F2} (distance={Dist:F4})",
                rawConfidence, bestBucket, bestDistance);
        }

        return bestBucket;
    }

    internal static CTLVerdict ParseVerdictEnum(string? verdict) => verdict?.ToLowerInvariant() switch
    {
        "clear" => CTLVerdict.Clear,
        "clearwithconditions" => CTLVerdict.ClearWithConditions,
        "notclear" => CTLVerdict.NotClear,
        "needshumanreview" => CTLVerdict.NeedsHumanReview,
        _ => CTLVerdict.NeedsHumanReview
    };

    /// <summary>
    /// Extracts a verdict-bearing JSON object from the LLM output, robust to markdown wrappers,
    /// fenced code blocks, and embedded auxiliary JSON (e.g. citation arrays).
    /// Returns the JSON substring of the FIRST top-level object that contains a <c>"verdict"</c>
    /// field, or null if none is found. Strategy:
    ///   1. If the raw text starts with '{', try the whole document first (canonical case).
    ///   2. Scan ```json fenced code blocks (in order); pick the first that parses to an object
    ///      containing a "verdict" key.
    ///   3. Fall back to a brace-balanced scan over the raw text.
    /// </summary>
    internal static string? TryExtractVerdictJson(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        // 1. Canonical case: whole payload is a single JSON object.
        var trimmed = text.Trim();
        if (trimmed.StartsWith('{') && trimmed.EndsWith('}') && ContainsVerdictKey(trimmed))
        {
            return trimmed;
        }

        // 2. Fenced ```json blocks.
        foreach (Match fence in FencedJsonRegex.Matches(text))
        {
            var body = fence.Groups["body"].Value.Trim();
            if (body.StartsWith('{') && body.EndsWith('}') && ContainsVerdictKey(body))
            {
                return body;
            }
        }

        // 3. Brace-balanced scan: find every top-level object and pick the first with "verdict".
        foreach (var candidate in EnumerateTopLevelJsonObjects(text))
        {
            if (ContainsVerdictKey(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>Cheap check: does this candidate contain a "verdict" property? Avoids quoted-string false positives by requiring the colon.</summary>
    private static bool ContainsVerdictKey(string json) =>
        Regex.IsMatch(json, "\"verdict\"\\s*:", RegexOptions.IgnoreCase);

    /// <summary>
    /// Yields every top-level (i.e. brace-depth-0 to brace-depth-1-and-back-to-0) JSON object
    /// substring in <paramref name="text"/>. Skips strings (so quoted braces don't fool the scanner).
    /// </summary>
    private static IEnumerable<string> EnumerateTopLevelJsonObjects(string text)
    {
        int depth = 0;
        int objectStart = -1;
        bool inString = false;
        bool escape = false;

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];

            if (escape) { escape = false; continue; }
            if (inString)
            {
                if (c == '\\') escape = true;
                else if (c == '"') inString = false;
                continue;
            }

            switch (c)
            {
                case '"':
                    inString = true;
                    break;
                case '{':
                    if (depth == 0) objectStart = i;
                    depth++;
                    break;
                case '}':
                    depth--;
                    if (depth == 0 && objectStart >= 0)
                    {
                        yield return text[objectStart..(i + 1)];
                        objectStart = -1;
                    }
                    break;
            }
        }
    }

    internal sealed record VerdictJsonResponse
    {
        public string? Verdict { get; init; }
        public double ConfidenceScore { get; init; }
        public string[]? Conditions { get; init; }
        public string[]? EvidenceTrail { get; init; }
        public string? ReflectionLog { get; init; }
        public CitationJsonEntry[]? Citations { get; init; }
    }

    internal sealed record CitationJsonEntry
    {
        public string? Source { get; init; }
        public string? Reference { get; init; }
        public string? Excerpt { get; init; }
    }
}
