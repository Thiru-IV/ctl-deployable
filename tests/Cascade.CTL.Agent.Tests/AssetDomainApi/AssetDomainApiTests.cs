using System.Net;
using System.Net.Http.Json;
using Cascade.CTL.Agent.Domain.Enums;
using Cascade.CTL.Agent.Domain.Models;
using Cascade.CTL.AssetService;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Cascade.CTL.Agent.Tests.AssetDomainApi;

public sealed class AssetDomainApiTests : IClassFixture<AssetDomainApiTests.Factory>
{
    public const string TestApiKey = "test-api-key-42";

    public sealed class Factory : WebApplicationFactory<Cascade.CTL.AssetService.Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Test");
            builder.ConfigureAppConfiguration((_, cfg) =>
            {
                cfg.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ApiKey:ApiKey"] = TestApiKey
                });
            });
        }
    }

    private readonly Factory _factory;

    public AssetDomainApiTests(Factory factory) => _factory = factory;

    [Fact]
    public async Task Health_IsAlwaysAllowedWithoutKey()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/health");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task GetAsset_WithoutApiKey_Returns401()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/assets/ASSET-TX-001");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetAsset_WithWrongApiKey_Returns401()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(ApiKeyAuthenticationMiddleware.HeaderName, "wrong-key");

        var response = await client.GetAsync("/api/assets/ASSET-TX-001");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetAsset_WithValidApiKey_ReturnsAssetProfile()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(ApiKeyAuthenticationMiddleware.HeaderName, TestApiKey);

        var jsonOptions = new System.Text.Json.JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
        };
        var asset = await client.GetFromJsonAsync<Asset>("/api/assets/ASSET-TX-001", jsonOptions);

        asset.Should().NotBeNull();
        asset!.AssetId.Should().Be("ASSET-TX-001");
        asset.AssetType.Should().Be(AssetType.Foreclosure);
        asset.StateCode.Should().Be("TX");
    }

    [Fact]
    public async Task GetAsset_WithUnknownId_Returns404()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(ApiKeyAuthenticationMiddleware.HeaderName, TestApiKey);

        var response = await client.GetAsync("/api/assets/DOES-NOT-EXIST");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetAsset_WithOverlongId_Returns400()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(ApiKeyAuthenticationMiddleware.HeaderName, TestApiKey);
        var longId = new string('x', 51);

        var response = await client.GetAsync($"/api/assets/{longId}");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ListAssets_WithValidApiKey_ReturnsKnownIds()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(ApiKeyAuthenticationMiddleware.HeaderName, TestApiKey);

        var response = await client.GetAsync("/api/assets");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("ASSET-TX-001");
    }
}
