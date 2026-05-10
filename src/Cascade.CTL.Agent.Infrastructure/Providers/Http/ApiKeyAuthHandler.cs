namespace Cascade.CTL.Agent.Infrastructure.Providers.Http;

/// <summary>
/// DelegatingHandler that attaches a static <c>X-Api-Key</c> header to every outgoing request.
/// Used when calling the Asset Domain service with a shared development/test API key.
/// </summary>
public sealed class ApiKeyAuthHandler : DelegatingHandler
{
    public const string HeaderName = "X-Api-Key";

    private readonly string _apiKey;

    public ApiKeyAuthHandler(string apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new ArgumentException("API key must be non-empty", nameof(apiKey));
        _apiKey = apiKey;
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (!request.Headers.Contains(HeaderName))
        {
            request.Headers.Add(HeaderName, _apiKey);
        }
        return base.SendAsync(request, cancellationToken);
    }
}
