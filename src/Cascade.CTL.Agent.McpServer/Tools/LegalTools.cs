using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cascade.CTL.Agent.Domain.Contracts;
using ModelContextProtocol.Server;

namespace Cascade.CTL.Agent.McpServer.Tools;

[McpServerToolType]
public sealed class LegalTools
{
    private readonly ITitleSearchProvider _titleProvider;
    private readonly IHOAProvider _hoaProvider;
    private readonly ICodeViolationProvider _codeViolationProvider;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public LegalTools(
        ITitleSearchProvider titleProvider,
        IHOAProvider hoaProvider,
        ICodeViolationProvider codeViolationProvider)
    {
        _titleProvider = titleProvider;
        _hoaProvider = hoaProvider;
        _codeViolationProvider = codeViolationProvider;
    }

    [McpServerTool, Description("Search for title defects, open liens, and encumbrances for a property by parcel ID and state code. Returns title clearance status, list of open liens, encumbrances, and title defects.")]
    public async Task<string> SearchTitle(
        [Description("County recorder parcel identifier (e.g., TX-DAL-123456)")] string parcelId,
        [Description("Two-letter US state code (e.g., TX, CA, FL)")] string stateCode)
    {
        if (string.IsNullOrWhiteSpace(parcelId))
            return """{"error": "parcelId is required"}""";
        if (parcelId.Length > 50)
            return """{"error": "parcelId exceeds maximum length of 50 characters"}""";
        if (string.IsNullOrWhiteSpace(stateCode) || stateCode.Length != 2)
            return """{"error": "stateCode must be a valid 2-letter US state code"}""";

        try
        {
            var result = await _titleProvider.SearchAsync(parcelId, stateCode.ToUpperInvariant());
            return JsonSerializer.Serialize(result, JsonOptions);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = "Title search failed", transient = IsTransient(ex), detail = ex.GetType().Name }, JsonOptions);
        }
    }

    [McpServerTool, Description("Check HOA (Homeowners Association) delinquency status for a property address. Returns whether property has an HOA, delinquency status, amount owed, and last payment date.")]
    public async Task<string> CheckHOADelinquency(
        [Description("Full property address including city, state, and zip")] string propertyAddress)
    {
        if (string.IsNullOrWhiteSpace(propertyAddress))
            return """{"error": "propertyAddress is required"}""";
        if (propertyAddress.Length > 500)
            return """{"error": "propertyAddress exceeds maximum length of 500 characters"}""";

        try
        {
            var result = await _hoaProvider.CheckDelinquencyAsync(propertyAddress);
            return JsonSerializer.Serialize(result, JsonOptions);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = "HOA delinquency check failed", transient = IsTransient(ex), detail = ex.GetType().Name }, JsonOptions);
        }
    }

    [McpServerTool, Description("Look up open code violations for a property address in a specific county. Returns list of violations with type, severity, and case numbers.")]
    public async Task<string> LookupCodeViolations(
        [Description("Full property address")] string propertyAddress,
        [Description("County name (e.g., Dallas, Los Angeles, Miami-Dade)")] string county)
    {
        if (string.IsNullOrWhiteSpace(propertyAddress))
            return """{"error": "propertyAddress is required"}""";
        if (propertyAddress.Length > 500)
            return """{"error": "propertyAddress exceeds maximum length of 500 characters"}""";
        if (string.IsNullOrWhiteSpace(county))
            return """{"error": "county is required"}""";
        if (county.Length > 100)
            return """{"error": "county exceeds maximum length of 100 characters"}""";

        try
        {
            var result = await _codeViolationProvider.LookupAsync(propertyAddress, county);
            return JsonSerializer.Serialize(result, JsonOptions);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = "Code violation lookup failed", transient = IsTransient(ex), detail = ex.GetType().Name }, JsonOptions);
        }
    }

    private static bool IsTransient(Exception ex) => ex is
        HttpRequestException or TimeoutException or IOException or System.Net.Sockets.SocketException
        or TaskCanceledException
        || (ex.InnerException != null && IsTransient(ex.InnerException));
}
