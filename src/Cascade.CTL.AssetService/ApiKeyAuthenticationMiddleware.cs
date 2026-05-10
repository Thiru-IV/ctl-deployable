using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace Cascade.CTL.AssetService;

/// <summary>
/// Validates the <c>X-Api-Key</c> header on inbound requests against the configured key.
/// Uses a fixed-time comparison to prevent timing attacks.
/// Bypasses authentication for allow-listed paths (e.g. <c>/health</c>).
/// </summary>
public sealed class ApiKeyAuthenticationMiddleware
{
    public const string HeaderName = "X-Api-Key";

    private readonly RequestDelegate _next;
    private readonly ApiKeyOptions _options;
    private readonly ILogger<ApiKeyAuthenticationMiddleware> _logger;

    public ApiKeyAuthenticationMiddleware(
        RequestDelegate next,
        IOptions<ApiKeyOptions> options,
        ILogger<ApiKeyAuthenticationMiddleware> logger)
    {
        _next = next;
        _options = options.Value;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path.Value ?? string.Empty;
        if (_options.AllowListedPaths.Any(p => path.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
        {
            await _next(context);
            return;
        }

        if (string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            _logger.LogError("AssetDomain API is misconfigured: no API key is set");
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            await context.Response.WriteAsync("API key not configured on server");
            return;
        }

        if (!context.Request.Headers.TryGetValue(HeaderName, out var providedValues))
        {
            _logger.LogWarning("Request rejected: missing {Header} header (path={Path})", HeaderName, path);
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsync($"Missing {HeaderName} header");
            return;
        }

        var provided = providedValues.ToString();
        if (!FixedTimeEquals(provided, _options.ApiKey))
        {
            _logger.LogWarning("Request rejected: invalid API key (path={Path})", path);
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsync("Invalid API key");
            return;
        }

        await _next(context);
    }

    internal static bool FixedTimeEquals(string a, string b)
    {
        var bytesA = Encoding.UTF8.GetBytes(a);
        var bytesB = Encoding.UTF8.GetBytes(b);
        if (bytesA.Length != bytesB.Length)
        {
            // Still run a dummy compare to avoid a length-based timing signal
            CryptographicOperations.FixedTimeEquals(bytesA, bytesA);
            return false;
        }
        return CryptographicOperations.FixedTimeEquals(bytesA, bytesB);
    }
}

public sealed class ApiKeyOptions
{
    public const string SectionName = "ApiKey";

    public string ApiKey { get; set; } = string.Empty;

    public List<string> AllowListedPaths { get; set; } = new() { "/health" };
}
