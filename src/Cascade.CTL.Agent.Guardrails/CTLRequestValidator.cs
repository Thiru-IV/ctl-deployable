using Cascade.CTL.Agent.Domain.Models;
using Microsoft.Extensions.Logging;

namespace Cascade.CTL.Agent.Guardrails;

public sealed class CTLRequestValidator
{
    private static readonly HashSet<string> ValidStateCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        "AL","AK","AZ","AR","CA","CO","CT","DE","FL","GA",
        "HI","ID","IL","IN","IA","KS","KY","LA","ME","MD",
        "MA","MI","MN","MS","MO","MT","NE","NV","NH","NJ",
        "NM","NY","NC","ND","OH","OK","OR","PA","RI","SC",
        "SD","TN","TX","UT","VT","VA","WA","WV","WI","WY","DC"
    };

    private readonly ILogger<CTLRequestValidator> _logger;

    public CTLRequestValidator(ILogger<CTLRequestValidator> logger)
    {
        _logger = logger;
    }

    public ValidationResult ValidateEvaluationRequest(CTLEvaluationRequest request)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(request.AssetId))
            errors.Add("AssetId is required");
        else if (request.AssetId.Length > 50)
            errors.Add("AssetId exceeds maximum length of 50 characters");

        if (request.RequestTimestamp > DateTime.UtcNow.AddMinutes(5))
            errors.Add("RequestTimestamp cannot be in the future");

        return errors.Count > 0
            ? ValidationResult.Failure(errors.ToArray())
            : ValidationResult.Success();
    }

    public static bool IsValidStateCode(string stateCode) =>
        !string.IsNullOrWhiteSpace(stateCode) && ValidStateCodes.Contains(stateCode);

    public static bool IsValidParcelId(string parcelId) =>
        !string.IsNullOrWhiteSpace(parcelId) && parcelId.Length <= 50;
}

public sealed record ValidationResult
{
    public required bool IsValid { get; init; }
    public required string[] Errors { get; init; }

    public static ValidationResult Success() => new() { IsValid = true, Errors = [] };
    public static ValidationResult Failure(string[] errors) => new() { IsValid = false, Errors = errors };
}
