using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cascade.CTL.Agent.Domain.Contracts;
using Cascade.CTL.Agent.Domain.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cascade.CTL.Agent.Infrastructure.Providers.Http;

/// <summary>
/// Implements <see cref="IAssetProfileProvider"/> by calling a real Asset Domain microservice over HTTP.
/// Uses <see cref="IHttpClientFactory"/> typed-client pattern with a resilience pipeline
/// (retry + circuit breaker + timeout) configured at registration via Microsoft.Extensions.Http.Resilience.
/// </summary>
/// <remarks>
/// Applies a short-TTL in-process cache so repeated lookups within a single CTL evaluation
/// (orchestrator pre-fetch + on-demand MCP tool re-queries by investigation agents) collapse to
/// a single network round trip.
/// </remarks>
public sealed class HttpAssetProfileProvider : IAssetProfileProvider
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<HttpAssetProfileProvider> _logger;
    private readonly TimeSpan _cacheTtl;
    private readonly int _cacheMaxEntries;

    private static readonly ConcurrentDictionary<string, CacheEntry> _cache = new();
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public HttpAssetProfileProvider(
        HttpClient httpClient,
        ILogger<HttpAssetProfileProvider> logger,
        IOptions<AssetDomainServiceOptions>? options = null)
    {
        _httpClient = httpClient;
        _logger = logger;
        // Caching is only enabled when options are explicitly supplied (i.e. via DI). Direct
        // constructor callers (unit tests with hand-rolled HttpClient) get the pre-cache behavior
        // so they can assert per-call network interactions.
        if (options is null)
        {
            _cacheTtl = TimeSpan.Zero;
            _cacheMaxEntries = 1;
        }
        else
        {
            var opts = options.Value;
            _cacheTtl = TimeSpan.FromSeconds(Math.Max(0, opts.CacheTtlSeconds));
            _cacheMaxEntries = Math.Max(1, opts.CacheMaxEntries);
        }
    }

    public async Task<Asset> GetAssetProfileAsync(string assetId, CancellationToken cancellationToken = default)
    {
        if (_cacheTtl > TimeSpan.Zero
            && _cache.TryGetValue(assetId, out var cached)
            && cached.ExpiresAtUtc > DateTime.UtcNow)
        {
            _logger.LogDebug("Asset profile cache hit for {AssetId}", assetId);
            return cached.Asset;
        }

        _logger.LogInformation("Fetching asset profile from domain service for {AssetId}", assetId);

        var response = await _httpClient.GetAsync(
            $"api/assets/{Uri.EscapeDataString(assetId)}",
            cancellationToken);

        response.EnsureSuccessStatusCode();

        var asset = await response.Content.ReadFromJsonAsync<Asset>(_jsonOptions, cancellationToken: cancellationToken)
            ?? throw new InvalidOperationException($"Asset domain service returned null for '{assetId}'");

        _logger.LogInformation(
            "Asset profile retrieved for {AssetId}: Type={AssetType}, State={StateCode}",
            assetId, asset.AssetType, asset.StateCode);

        if (_cacheTtl > TimeSpan.Zero)
        {
            TrimCacheIfNeeded();
            _cache[assetId] = new CacheEntry(asset, DateTime.UtcNow.Add(_cacheTtl));
        }

        return asset;
    }

    private void TrimCacheIfNeeded()
    {
        if (_cache.Count < _cacheMaxEntries) return;

        var now = DateTime.UtcNow;
        foreach (var kvp in _cache)
        {
            if (kvp.Value.ExpiresAtUtc <= now)
                _cache.TryRemove(kvp.Key, out _);
        }

        if (_cache.Count >= _cacheMaxEntries)
        {
            foreach (var kvp in _cache.OrderBy(k => k.Value.ExpiresAtUtc).Take(_cache.Count - _cacheMaxEntries + 1))
                _cache.TryRemove(kvp.Key, out _);
        }
    }

    /// <summary>Clears the in-memory cache. Intended for test isolation.</summary>
    internal static void ClearCacheForTests() => _cache.Clear();

    private readonly record struct CacheEntry(Asset Asset, DateTime ExpiresAtUtc);
}
