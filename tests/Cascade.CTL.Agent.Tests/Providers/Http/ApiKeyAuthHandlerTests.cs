using System.Net;
using System.Net.Http.Json;
using Cascade.CTL.Agent.Domain.Enums;
using Cascade.CTL.Agent.Domain.Models;
using Cascade.CTL.Agent.Infrastructure.Providers.Http;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Cascade.CTL.Agent.Tests.Providers.Http;

public sealed class ApiKeyAuthHandlerTests
{
    [Fact]
    public async Task SendAsync_AttachesApiKeyHeader()
    {
        var capture = new HeaderCapturingHandler();
        var handler = new ApiKeyAuthHandler("top-secret-key") { InnerHandler = capture };
        var client = new HttpClient(handler) { BaseAddress = new Uri("http://example.com") };

        await client.GetAsync("/ping");

        capture.LastRequest!.Headers.TryGetValues(ApiKeyAuthHandler.HeaderName, out var values).Should().BeTrue();
        values!.Single().Should().Be("top-secret-key");
    }

    [Fact]
    public void Ctor_RejectsEmptyKey()
    {
        Action act = () => _ = new ApiKeyAuthHandler("");
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public async Task SendAsync_DoesNotOverrideExistingHeader()
    {
        var capture = new HeaderCapturingHandler();
        var handler = new ApiKeyAuthHandler("from-handler") { InnerHandler = capture };
        var client = new HttpClient(handler) { BaseAddress = new Uri("http://example.com") };

        var request = new HttpRequestMessage(HttpMethod.Get, "/ping");
        request.Headers.Add(ApiKeyAuthHandler.HeaderName, "from-caller");
        await client.SendAsync(request);

        capture.LastRequest!.Headers.GetValues(ApiKeyAuthHandler.HeaderName).Single().Should().Be("from-caller");
    }

    private sealed class HeaderCapturingHandler : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastRequest = request;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }
}

public sealed class HttpAssetProfileProviderCachingTests
{
    public HttpAssetProfileProviderCachingTests()
    {
        HttpAssetProfileProvider.ClearCacheForTests();
    }

    [Fact]
    public async Task SecondCallWithinTtl_DoesNotHitNetwork()
    {
        var stub = new AssetStubHandler();
        var client = new HttpClient(stub) { BaseAddress = new Uri("http://example.com") };
        var options = Options.Create(new AssetDomainServiceOptions { CacheTtlSeconds = 600 });
        var provider = new HttpAssetProfileProvider(client, NullLogger<HttpAssetProfileProvider>.Instance, options);

        await provider.GetAssetProfileAsync("ASSET-TX-001");
        await provider.GetAssetProfileAsync("ASSET-TX-001");

        stub.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task DifferentAssetIds_EachHitNetwork()
    {
        var stub = new AssetStubHandler();
        var client = new HttpClient(stub) { BaseAddress = new Uri("http://example.com") };
        var options = Options.Create(new AssetDomainServiceOptions { CacheTtlSeconds = 600 });
        var provider = new HttpAssetProfileProvider(client, NullLogger<HttpAssetProfileProvider>.Instance, options);

        await provider.GetAssetProfileAsync("ASSET-TX-001");
        await provider.GetAssetProfileAsync("ASSET-CA-002");

        stub.CallCount.Should().Be(2);
    }

    [Fact]
    public async Task CacheDisabled_AlwaysHitsNetwork()
    {
        var stub = new AssetStubHandler();
        var client = new HttpClient(stub) { BaseAddress = new Uri("http://example.com") };
        var options = Options.Create(new AssetDomainServiceOptions { CacheTtlSeconds = 0 });
        var provider = new HttpAssetProfileProvider(client, NullLogger<HttpAssetProfileProvider>.Instance, options);

        await provider.GetAssetProfileAsync("ASSET-TX-001");
        await provider.GetAssetProfileAsync("ASSET-TX-001");

        stub.CallCount.Should().Be(2);
    }

    private sealed class AssetStubHandler : HttpMessageHandler
    {
        public int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            CallCount++;
            var id = request.RequestUri!.AbsolutePath.Split('/').Last();
            var asset = new Asset
            {
                AssetId = id,
                AssetType = AssetType.Foreclosure,
                StateCode = "TX",
                County = "Dallas",
                SellerTier = SellerTier.Tier1,
                OccupancyStatus = OccupancyStatus.Vacant,
                ParcelId = "TX-DAL-000001",
                PropertyAddress = "1 Main St"
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(asset)
            });
        }
    }
}
