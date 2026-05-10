using System.Net.Http.Headers;
using Azure.Core;
using Azure.Identity;

namespace Cascade.CTL.Agent.Infrastructure.Providers.Http;

/// <summary>
/// DelegatingHandler that acquires OAuth 2.0 tokens via Azure Identity (DefaultAzureCredential)
/// and attaches them as Bearer tokens on outgoing HTTP requests.
/// Tokens are cached by the underlying Azure.Identity library and refreshed automatically.
/// </summary>
public sealed class AzureIdentityAuthHandler : DelegatingHandler
{
    private readonly TokenCredential _credential;
    private readonly string[] _scopes;

    public AzureIdentityAuthHandler(TokenCredential credential, string scope)
    {
        _credential = credential;
        _scopes = [scope];
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var tokenResult = await _credential.GetTokenAsync(
            new TokenRequestContext(_scopes), cancellationToken);

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokenResult.Token);

        return await base.SendAsync(request, cancellationToken);
    }
}
