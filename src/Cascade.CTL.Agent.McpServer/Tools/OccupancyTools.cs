using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cascade.CTL.Agent.Domain.Contracts;
using ModelContextProtocol.Server;

namespace Cascade.CTL.Agent.McpServer.Tools;

[McpServerToolType]
public sealed class OccupancyTools
{
    private readonly IOccupancyProvider _provider;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public OccupancyTools(IOccupancyProvider provider)
    {
        _provider = provider;
    }

    [McpServerTool, Description("Get occupancy status for a property. Returns whether property is vacant/occupied, last inspection date, eviction status, property condition, and inspector notes.")]
    public async Task<string> GetOccupancyStatus(
        [Description("Full property address")] string propertyAddress)
    {
        if (string.IsNullOrWhiteSpace(propertyAddress))
            return """{"error": "propertyAddress is required"}""";
        if (propertyAddress.Length > 500)
            return """{"error": "propertyAddress exceeds maximum length of 500 characters"}""";

        try
        {
            var result = await _provider.GetStatusAsync(propertyAddress);
            return JsonSerializer.Serialize(result, JsonOptions);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = "Occupancy status check failed", transient = IsTransient(ex), detail = ex.GetType().Name }, JsonOptions);
        }
    }

    private static bool IsTransient(Exception ex) => ex is
        HttpRequestException or TimeoutException or IOException or System.Net.Sockets.SocketException
        or TaskCanceledException
        || (ex.InnerException != null && IsTransient(ex.InnerException));
}
