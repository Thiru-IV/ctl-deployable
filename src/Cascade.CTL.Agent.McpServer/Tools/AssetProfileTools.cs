using System.ComponentModel;
using Cascade.CTL.Agent.Domain.Contracts;
using ModelContextProtocol.Server;

namespace Cascade.CTL.Agent.McpServer.Tools;

[McpServerToolType]
public sealed class AssetProfileTools
{
    private readonly IAssetProfileProvider _provider;

    public AssetProfileTools(IAssetProfileProvider provider)
    {
        _provider = provider;
    }

    [McpServerTool, Description("Retrieve the full asset profile for a given asset ID. Returns asset type, state, county, seller tier, occupancy status, parcel ID, and property address. This is the first tool to call in any CTL evaluation.")]
    public async Task<string> GetAssetProfile(
        [Description("The unique asset identifier (e.g., ASSET-TX-001)")] string assetId)
    {
        if (string.IsNullOrWhiteSpace(assetId))
            return """{"error": "assetId is required"}""";
        if (assetId.Length > 50)
            return """{"error": "assetId exceeds maximum length of 50 characters"}""";

        try
        {
            var asset = await _provider.GetAssetProfileAsync(assetId);
            return System.Text.Json.JsonSerializer.Serialize(asset, new System.Text.Json.JsonSerializerOptions
            {
                WriteIndented = true,
                Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
            });
        }
        catch (Exception ex)
        {
            return System.Text.Json.JsonSerializer.Serialize(new { error = "Asset profile retrieval failed", transient = IsTransient(ex), detail = ex.GetType().Name });
        }
    }

    private static bool IsTransient(Exception ex) => ex is
        HttpRequestException or TimeoutException or IOException or System.Net.Sockets.SocketException
        or TaskCanceledException
        || (ex.InnerException != null && IsTransient(ex.InnerException));
}
