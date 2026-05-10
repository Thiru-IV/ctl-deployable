using System.Net;
using System.Text.Json;
using Azure.Core;
using Cascade.CTL.Agent.Domain.Enums;
using Cascade.CTL.Agent.Domain.Models;
using Cascade.CTL.Agent.Infrastructure.Providers.Http;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;

namespace Cascade.CTL.Agent.Tests.Providers;

public class HttpAssetProfileProviderTests
{
    private readonly ILogger<HttpAssetProfileProvider> _logger = Substitute.For<ILogger<HttpAssetProfileProvider>>();

    private static readonly Asset SampleAsset = new()
    {
        AssetId = "ASSET-TX-001",
        AssetType = AssetType.Foreclosure,
        StateCode = "TX",
        County = "Dallas",
        SellerTier = SellerTier.Tier1,
        OccupancyStatus = OccupancyStatus.Vacant,
        ParcelId = "TX-DAL-123456",
        PropertyAddress = "1234 Oak Street, Dallas, TX 75201",
        SellerName = "First National Bank",
        IngestionDate = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
    };

    private static HttpClient CreateMockHttpClient(HttpStatusCode statusCode, Asset? asset = null)
    {
        var handler = new FakeHttpMessageHandler(statusCode, asset);
        return new HttpClient(handler) { BaseAddress = new Uri("https://asset-domain.example.com/") };
    }

    [Fact]
    public async Task GetAssetProfileAsync_SuccessfulResponse_ReturnsAsset()
    {
        using var httpClient = CreateMockHttpClient(HttpStatusCode.OK, SampleAsset);
        var provider = new HttpAssetProfileProvider(httpClient, _logger);

        var result = await provider.GetAssetProfileAsync("ASSET-TX-001");

        result.AssetId.Should().Be("ASSET-TX-001");
        result.AssetType.Should().Be(AssetType.Foreclosure);
        result.StateCode.Should().Be("TX");
        result.County.Should().Be("Dallas");
        result.SellerTier.Should().Be(SellerTier.Tier1);
        result.OccupancyStatus.Should().Be(OccupancyStatus.Vacant);
        result.ParcelId.Should().Be("TX-DAL-123456");
        result.PropertyAddress.Should().Be("1234 Oak Street, Dallas, TX 75201");
    }

    [Fact]
    public async Task GetAssetProfileAsync_NotFound_ThrowsHttpRequestException()
    {
        using var httpClient = CreateMockHttpClient(HttpStatusCode.NotFound);
        var provider = new HttpAssetProfileProvider(httpClient, _logger);

        var act = () => provider.GetAssetProfileAsync("UNKNOWN-ASSET");

        await act.Should().ThrowAsync<HttpRequestException>();
    }

    [Fact]
    public async Task GetAssetProfileAsync_ServerError_ThrowsHttpRequestException()
    {
        using var httpClient = CreateMockHttpClient(HttpStatusCode.InternalServerError);
        var provider = new HttpAssetProfileProvider(httpClient, _logger);

        var act = () => provider.GetAssetProfileAsync("ASSET-TX-001");

        await act.Should().ThrowAsync<HttpRequestException>();
    }

    [Fact]
    public async Task GetAssetProfileAsync_Unauthorized_ThrowsHttpRequestException()
    {
        using var httpClient = CreateMockHttpClient(HttpStatusCode.Unauthorized);
        var provider = new HttpAssetProfileProvider(httpClient, _logger);

        var act = () => provider.GetAssetProfileAsync("ASSET-TX-001");

        await act.Should().ThrowAsync<HttpRequestException>();
    }

    [Fact]
    public async Task GetAssetProfileAsync_RequestUrl_ContainsEncodedAssetId()
    {
        var handler = new FakeHttpMessageHandler(HttpStatusCode.OK, SampleAsset);
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://asset-domain.example.com/") };
        var provider = new HttpAssetProfileProvider(httpClient, _logger);

        await provider.GetAssetProfileAsync("ASSET/WITH SPACES");

        handler.LastRequestUri.Should().NotBeNull();
        handler.LastRequestUri!.PathAndQuery.Should().Contain("ASSET%2FWITH%20SPACES");
    }

    [Fact]
    public async Task GetAssetProfileAsync_CancellationRequested_ThrowsOperationCanceled()
    {
        using var httpClient = CreateMockHttpClient(HttpStatusCode.OK, SampleAsset);
        var provider = new HttpAssetProfileProvider(httpClient, _logger);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = () => provider.GetAssetProfileAsync("ASSET-TX-001", cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public void AssetDomainServiceOptions_Defaults_AreReasonable()
    {
        var options = new AssetDomainServiceOptions();

        options.BaseUrl.Should().BeEmpty();
        options.UseAzureIdentity.Should().BeFalse();
        options.Scope.Should().Be("api://asset-domain-service/.default");
        options.ApiKey.Should().BeNull();
        options.TimeoutSeconds.Should().Be(30);
        options.RetryCount.Should().Be(3);
        options.CircuitBreakerThreshold.Should().Be(5);
        options.CircuitBreakerDurationSeconds.Should().Be(30);
        AssetDomainServiceOptions.SectionName.Should().Be("AssetDomainService");
    }

    [Fact]
    public async Task AzureIdentityAuthHandler_AttachesBearerToken()
    {
        var fakeCredential = new FakeTokenCredential("test-access-token-12345");
        var handler = new AzureIdentityAuthHandler(fakeCredential, "api://test/.default")
        {
            InnerHandler = new FakeHttpMessageHandler(HttpStatusCode.OK, SampleAsset)
        };

        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://test.example.com/") };

        var request = new HttpRequestMessage(HttpMethod.Get, "api/assets/ASSET-TX-001");
        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        request.Headers.Authorization.Should().NotBeNull();
        request.Headers.Authorization!.Scheme.Should().Be("Bearer");
        request.Headers.Authorization!.Parameter.Should().Be("test-access-token-12345");
    }

    [Fact]
    public async Task AzureIdentityAuthHandler_PropagatesCancellation()
    {
        var fakeCredential = new FakeTokenCredential("token");
        var handler = new AzureIdentityAuthHandler(fakeCredential, "api://test/.default")
        {
            InnerHandler = new FakeHttpMessageHandler(HttpStatusCode.OK, SampleAsset)
        };

        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://test.example.com/") };
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = () => client.GetAsync("api/assets/X", cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    /// <summary>
    /// Fake handler for unit-testing HttpAssetProfileProvider without real HTTP calls.
    /// </summary>
    private sealed class FakeHttpMessageHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _statusCode;
        private readonly Asset? _asset;

        public Uri? LastRequestUri { get; private set; }

        public FakeHttpMessageHandler(HttpStatusCode statusCode, Asset? asset = null)
        {
            _statusCode = statusCode;
            _asset = asset;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastRequestUri = request.RequestUri;

            var response = new HttpResponseMessage(_statusCode);
            if (_asset is not null && _statusCode == HttpStatusCode.OK)
            {
                var json = JsonSerializer.Serialize(_asset);
                response.Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
            }
            return Task.FromResult(response);
        }
    }

    /// <summary>
    /// Fake TokenCredential for testing AzureIdentityAuthHandler without real Azure AD.
    /// </summary>
    private sealed class FakeTokenCredential : TokenCredential
    {
        private readonly string _token;

        public FakeTokenCredential(string token) => _token = token;

        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new AccessToken(_token, DateTimeOffset.UtcNow.AddHours(1));
        }

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new ValueTask<AccessToken>(new AccessToken(_token, DateTimeOffset.UtcNow.AddHours(1)));
        }
    }
}
