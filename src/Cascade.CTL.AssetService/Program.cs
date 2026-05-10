using System.Text.Json.Serialization;
using Cascade.CTL.AssetService;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<ApiKeyOptions>(
    builder.Configuration.GetSection(ApiKeyOptions.SectionName));

// Allow override via environment variable (e.g. from Docker / compose)
var envKey = Environment.GetEnvironmentVariable("ASSETDOMAIN_API_KEY");
if (!string.IsNullOrWhiteSpace(envKey))
{
    builder.Services.Configure<ApiKeyOptions>(o => o.ApiKey = envKey);
}

builder.Services.AddSingleton<IAssetRepository, InMemoryAssetRepository>();

builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
    o.SerializerOptions.WriteIndented = false;
});

var app = builder.Build();

app.UseMiddleware<ApiKeyAuthenticationMiddleware>();

app.MapGet("/health", () => Results.Ok(new { status = "healthy", service = "asset-service" }));

app.MapGet("/api/assets/{assetId}", (string assetId, IAssetRepository repo) =>
{
    if (string.IsNullOrWhiteSpace(assetId))
        return Results.BadRequest(new { error = "assetId is required" });
    if (assetId.Length > 50)
        return Results.BadRequest(new { error = "assetId exceeds maximum length of 50 characters" });

    var asset = repo.Find(assetId);
    return asset is null
        ? Results.NotFound(new { error = $"Asset '{assetId}' not found" })
        : Results.Ok(asset);
});

app.MapGet("/api/assets", (IAssetRepository repo) =>
    Results.Ok(new { assetIds = repo.KnownAssetIds }));

app.Run();

// Expose Program for WebApplicationFactory-based integration tests
namespace Cascade.CTL.AssetService
{
    public partial class Program { }
}
