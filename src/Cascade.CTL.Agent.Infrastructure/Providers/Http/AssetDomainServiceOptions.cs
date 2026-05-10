namespace Cascade.CTL.Agent.Infrastructure.Providers.Http;

public sealed class AssetDomainServiceOptions
{
    public const string SectionName = "AssetDomainService";

    public string BaseUrl { get; set; } = string.Empty;
    public bool UseAzureIdentity { get; set; } = false;
    public string Scope { get; set; } = "api://asset-domain-service/.default";
    public string? ApiKey { get; set; }
    public int TimeoutSeconds { get; set; } = 30;
    public int RetryCount { get; set; } = 3;
    public int CircuitBreakerThreshold { get; set; } = 5;
    public int CircuitBreakerDurationSeconds { get; set; } = 30;

    /// <summary>
    /// Time-to-live for the in-process asset profile cache. Set to 0 to disable caching.
    /// Defaults to 10 minutes — long enough to collapse all fetches within a single CTL evaluation
    /// (orchestrator pre-fetch + agent tool re-queries), short enough that stale data never reaches production runs.
    /// </summary>
    public int CacheTtlSeconds { get; set; } = 600;

    /// <summary>Maximum number of asset profile entries retained in the in-process cache.</summary>
    public int CacheMaxEntries { get; set; } = 256;
}
