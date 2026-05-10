namespace Cascade.CTL.Agent.Application.Configuration;

public sealed class CTLAgentOptions
{
    public AzureAIFoundryOptions AzureAIFoundry { get; set; } = new();
    public AzureAIFoundryOptions JudgeModel { get; set; } = new();
    public McpServerOptions McpServer { get; set; } = new();
    public DataProviderOptions Providers { get; set; } = new();
    public QualityGateOptions QualityGate { get; set; } = new();
}

public sealed class AzureAIFoundryOptions
{
    public string Endpoint { get; set; } = "";
    public string ModelId { get; set; } = "gpt-4o";
    public bool UseAzureIdentity { get; set; } = true;
    public string? ApiKey { get; set; }
}

public sealed class McpServerOptions
{
    public string Endpoint { get; set; } = "http://localhost:5100";
    public string? ApiKey { get; set; }
}

public sealed class DataProviderOptions
{
    public bool UseMockProviders { get; set; } = true;
}

public sealed class QualityGateOptions
{
    public bool Enabled { get; set; } = true;
    public int MinGroundednessScore { get; set; } = 3;
}

public sealed class VerdictPolicyOptions
{
    public const string SectionName = "VerdictPolicy";

    /// <summary>
    /// Minimum confidence threshold for NeedsHumanReview enforcement.
    /// If the LLM returns NeedsHumanReview with confidence >= this value,
    /// the verdict is remapped to ClearWithConditions.
    /// If the LLM returns any other verdict with confidence below this value,
    /// the verdict is forced to NeedsHumanReview.
    /// Default: 0.75 (matching the prompt rule).
    /// </summary>
    public double HumanReviewConfidenceThreshold { get; set; } = 0.75;
}

/// <summary>
/// Phase-1 determinism guardrails for the Reflection LLM call (verdict-determinism v2).
/// Implements industry-standard sampling lockdown + discrete confidence buckets:
///   - OpenAI reproducibility (temp=0, seed, system_fingerprint).
///   - LLM-as-judge calibration literature (G-Eval EMNLP 2023, MT-Bench NeurIPS 2023):
///     discrete Likert-style buckets are reliable; continuous 0–1 scores are noise.
/// Provider-agnostic: only ChatOptions are used; no provider-specific SDK calls.
/// </summary>
public sealed class ReflectionDeterminismOptions
{
    public const string SectionName = "ReflectionDeterminism";

    /// <summary>Master switch. When false, Reflection runs with prior (non-locked) sampling.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Sampling temperature for the Reflection call. Locked to 0.0 by default.</summary>
    public float Temperature { get; set; } = 0.0f;

    /// <summary>Top-p nucleus sampling cap. 1.0 (off) by default; combined with temp=0 this is deterministic.</summary>
    public float TopP { get; set; } = 1.0f;

    /// <summary>
    /// Strategy for the per-call <c>seed</c> parameter (best-effort on Azure OpenAI; ignored by providers
    /// that do not expose a seed knob — code logs and continues).
    ///   - <c>AssetIdHash</c> (default): seed = stable hash of AssetId (and optionally SessionId, see
    ///     <see cref="IncludeSessionInSeed"/>). Same asset → same seed across runs by default — required
    ///     for "rerun same asset → same verdict" reproducibility.
    ///   - <c>Fixed</c>: use <see cref="FixedSeed"/> for every call.
    ///   - <c>None</c>: do not set a seed.
    /// </summary>
    public SeedStrategy SeedStrategy { get; set; } = SeedStrategy.AssetIdHash;

    /// <summary>
    /// Fixed seed value used when <see cref="SeedStrategy"/> is <c>Fixed</c>.
    /// </summary>
    public long FixedSeed { get; set; } = 42L;

    /// <summary>
    /// When <see cref="SeedStrategy"/> is <c>AssetIdHash</c>, controls whether the per-run SessionId
    /// is mixed into the seed. Default is <c>false</c> — same asset always produces the same seed
    /// regardless of session, which is the intended behaviour for cross-run reproducibility.
    /// Set to <c>true</c> only if you specifically want a different seed per session (e.g. to
    /// intentionally diversify Reflection sampling across sessions for ensemble-style use).
    /// </summary>
    public bool IncludeSessionInSeed { get; set; } = false;

    /// <summary>
    /// When true, the parsed Reflection confidence is snapped to the nearest allowed bucket
    /// (<see cref="ConfidenceBuckets"/>). Pre-snap value is preserved on the verdict DTO for audit.
    /// </summary>
    public bool UseDiscreteConfidenceBuckets { get; set; } = true;

    /// <summary>
    /// Allowed discrete confidence values. Defaults to a 5-point Likert-aligned set:
    /// VeryLow=0.55, Low=0.70, Medium=0.80, High=0.90, VeryHigh=0.95.
    /// </summary>
    public double[] ConfidenceBuckets { get; set; } = [0.55, 0.70, 0.80, 0.90, 0.95];

    /// <summary>
    /// Phase 1 v2 — Fix C: when true, the Reflection call is made with a strict JSON schema
    /// (<c>response_format = json_schema</c>, <c>strict: true</c> on Azure OpenAI / OpenAI). This
    /// forces the model to emit a JSON object matching the verdict schema at the protocol layer,
    /// eliminating the markdown-narrative failure mode where the parser would route to
    /// NeedsHumanReview/0.0 because no <c>"verdict"</c> field was emitted. Connectors that do not
    /// support structured outputs ignore the <see cref="Microsoft.Extensions.AI.ChatOptions.ResponseFormat"/>
    /// and continue to work. Default: <c>true</c>.
    /// </summary>
    public bool UseStructuredOutputs { get; set; } = true;
}

/// <summary>How the Reflection determinism layer derives the per-call seed.</summary>
public enum SeedStrategy
{
    /// <summary>Stable hash of AssetId and SessionId. Best for "rerun same asset" reproducibility.</summary>
    AssetIdHash = 0,
    /// <summary>Use ReflectionDeterminismOptions.FixedSeed for every call.</summary>
    Fixed = 1,
    /// <summary>Do not set a seed (relies on temp=0 only).</summary>
    None = 2
}
