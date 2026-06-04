using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cascade.CTL.Agent.Application.Configuration;
using Microsoft.Extensions.AI;

namespace Cascade.CTL.Agent.Application.Orchestration.Workflow;

/// <summary>
/// Builds a deterministic <see cref="ChatOptions"/> for the Reflection LLM call (verdict-determinism v2,
/// Phase 1: sampling lockdown). Provider-agnostic — sets only standard <see cref="ChatOptions"/>
/// properties plus a <c>seed</c> entry in <see cref="ChatOptions.AdditionalProperties"/>.
/// Connectors that do not expose a seed knob (e.g., Anthropic today) ignore it without error;
/// the temperature lockdown still applies.
/// </summary>
internal static class ReflectionDeterminismFactory
{
    /// <summary>
    /// Stable, well-documented additional-property key for the per-call seed. Microsoft.Extensions.AI
    /// connectors for Azure OpenAI / OpenAI propagate this into the underlying request when supported.
    /// </summary>
    internal const string SeedPropertyKey = "seed";

    /// <summary>
    /// Schema name presented to the OpenAI / Azure OpenAI <c>response_format = json_schema</c> mode.
    /// Must match <c>^[a-zA-Z0-9_-]+$</c> per the OpenAI API contract.
    /// </summary>
    internal const string VerdictSchemaName = "ctl_verdict";

    /// <summary>
    /// Strict JSON schema for the Reflection verdict payload (Fix C — structured outputs).
    /// Mirrors <c>VerdictParser.VerdictJsonResponse</c> exactly. OpenAI strict mode requires:
    ///   - <c>"additionalProperties": false</c> on every object,
    ///   - every property listed in <c>"required"</c> (nullable values use <c>"type": ["string","null"]</c>).
    /// </summary>
    private const string VerdictJsonSchema = """
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["verdict","confidenceScore","conditions","evidenceTrail","reflectionLog","citations"],
          "properties": {
            "verdict": {
              "type": "string",
              "enum": ["Clear","ClearWithConditions","NotClear","NeedsHumanReview"],
              "description": "Final CTL verdict."
            },
            "confidenceScore": {
              "type": "number",
              "description": "Confidence in the verdict between 0.0 and 1.0. Will be snapped to the nearest configured discrete bucket post-parse."
            },
            "conditions": {
              "type": "array",
              "items": { "type": "string" },
              "description": "Conditions or remediation items that must be addressed."
            },
            "evidenceTrail": {
              "type": "array",
              "items": { "type": "string" },
              "description": "Ordered list of factual findings supporting the verdict."
            },
            "reflectionLog": {
              "type": "string",
              "description": "Free-form summary of the reasoning, contradictions weighed, and final justification."
            },
            "citations": {
              "type": "array",
              "items": {
                "type": "object",
                "additionalProperties": false,
                "required": ["source","reference","excerpt"],
                "properties": {
                  "source": { "type": "string" },
                  "reference": { "type": ["string","null"] },
                  "excerpt": { "type": ["string","null"] }
                }
              },
              "description": "Citations from the policy knowledge base or tool outputs that ground the verdict."
            }
          }
        }
        """;

    private static readonly JsonElement VerdictSchemaElement =
        JsonDocument.Parse(VerdictJsonSchema).RootElement;

    /// <summary>
    /// Builds <see cref="ChatOptions"/> for the Reflection call. Returns a vanilla
    /// <c>{ Temperature = 0.0f }</c> ChatOptions when <paramref name="options"/> is null or disabled
    /// — preserving prior behaviour exactly so this is safe to ship behind a config flag.
    /// </summary>
    public static ChatOptions Build(
        ReflectionDeterminismOptions? options,
        string assetId,
        string sessionId)
    {
        if (options is null || !options.Enabled)
        {
            return new ChatOptions { Temperature = 0.0f };
        }

        var chatOptions = new ChatOptions
        {
            Temperature = options.Temperature,
            TopP = options.TopP
        };

        var seed = ResolveSeed(options, assetId, sessionId);
        if (seed.HasValue)
        {
            // Microsoft.Extensions.AI 10.x OpenAI connector reads ChatOptions.Seed → request `seed`.
            // AdditionalProperties[SeedPropertyKey] kept for any connector still using the legacy path.
            chatOptions.Seed = seed.Value;
            chatOptions.AdditionalProperties ??= new AdditionalPropertiesDictionary();
            chatOptions.AdditionalProperties[SeedPropertyKey] = seed.Value;
        }

        // ── Fix C: strict structured outputs ──
        // Force the model to emit a JSON object matching the verdict schema. Eliminates the
        // markdown-narrative failure mode observed in the v2 6-run measurement (2/6 fallbacks).
        // The Microsoft.Extensions.AI Azure OpenAI connector translates this to
        // response_format = json_schema, strict: true on supported deployments; connectors that
        // do not support structured outputs ignore the ResponseFormat and behave as before.
        if (options.UseStructuredOutputs)
        {
            chatOptions.ResponseFormat = ChatResponseFormat.ForJsonSchema(
                schema: VerdictSchemaElement,
                schemaName: VerdictSchemaName,
                schemaDescription: "CTL verdict produced by the Reflection phase.");
        }

        return chatOptions;
    }

    /// <summary>
    /// Resolves the seed value per <see cref="ReflectionDeterminismOptions.SeedStrategy"/>.
    /// Returns null when strategy is <c>None</c> or inputs are insufficient.
    /// </summary>
    public static long? ResolveSeed(ReflectionDeterminismOptions options, string assetId, string sessionId)
    {
        return options.SeedStrategy switch
        {
            SeedStrategy.None => null,
            SeedStrategy.Fixed => options.FixedSeed,
            SeedStrategy.AssetIdHash => HashToSeed(assetId, options.IncludeSessionInSeed ? sessionId : null),
            _ => null
        };
    }

    /// <summary>
    /// Stable, deterministic 63-bit positive seed derived from AssetId (and optionally SessionId).
    /// Uses SHA-256 (not String.GetHashCode, which is process-randomised). Same inputs always
    /// produce the same output across processes, machines, and .NET runtimes.
    /// When <paramref name="sessionId"/> is null, only AssetId contributes — required for
    /// "rerun same asset → same seed" reproducibility (the default behaviour).
    /// </summary>
    internal static long HashToSeed(string assetId, string? sessionId)
    {
        var input = sessionId is null
            ? Encoding.UTF8.GetBytes(assetId)
            : Encoding.UTF8.GetBytes($"{assetId}|{sessionId}");
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(input, hash);
        // Take first 8 bytes as little-endian Int64, mask sign bit → non-negative.
        var raw = BitConverter.ToInt64(hash[..8]);
        return raw & 0x7FFFFFFFFFFFFFFFL;
    }
}
