using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cascade.CTL.Agent.Domain.Contracts;
using ModelContextProtocol.Server;

namespace Cascade.CTL.Agent.McpServer.Tools;

[McpServerToolType]
public sealed class ValuationTools
{
    private readonly IBPOProvider _bpoProvider;
    private readonly IAVMProvider _avmProvider;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public ValuationTools(IBPOProvider bpoProvider, IAVMProvider avmProvider)
    {
        _bpoProvider = bpoProvider;
        _avmProvider = avmProvider;
    }

    [McpServerTool, Description("Retrieve the Broker Price Opinion (BPO) for an asset. Returns estimated value, BPO date, quality rating, staleness status, vendor name, and property condition rating. Missing BPO is a CTL blocker.")]
    public async Task<string> RetrieveBPO(
        [Description("The unique asset identifier")] string assetId)
    {
        if (string.IsNullOrWhiteSpace(assetId))
            return """{"error": "assetId is required"}""";
        if (assetId.Length > 50)
            return """{"error": "assetId exceeds maximum length of 50 characters"}""";

        try
        {
            var result = await _bpoProvider.RetrieveAsync(assetId);
            return JsonSerializer.Serialize(result, JsonOptions);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = "BPO retrieval failed", transient = IsTransient(ex), detail = ex.GetType().Name }, JsonOptions);
        }
    }

    [McpServerTool, Description("Get Automated Valuation Model (AVM) estimate for a property. Returns estimated value, confidence score, variance from BPO, and valuation provider. Used as secondary valuation cross-reference.")]
    public async Task<string> GetAVM(
        [Description("Full property address")] string propertyAddress,
        [Description("Two-letter US state code")] string stateCode)
    {
        if (string.IsNullOrWhiteSpace(propertyAddress))
            return """{"error": "propertyAddress is required"}""";
        if (propertyAddress.Length > 500)
            return """{"error": "propertyAddress exceeds maximum length of 500 characters"}""";
        if (string.IsNullOrWhiteSpace(stateCode) || stateCode.Length != 2)
            return """{"error": "stateCode must be a valid 2-letter US state code"}""";

        try
        {
            var result = await _avmProvider.GetValuationAsync(propertyAddress, stateCode.ToUpperInvariant());
            return JsonSerializer.Serialize(result, JsonOptions);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = "AVM valuation failed", transient = IsTransient(ex), detail = ex.GetType().Name }, JsonOptions);
        }
    }

    private static bool IsTransient(Exception ex) => ex is
        HttpRequestException or TimeoutException or IOException or System.Net.Sockets.SocketException
        or TaskCanceledException
        || (ex.InnerException != null && IsTransient(ex.InnerException));
}
