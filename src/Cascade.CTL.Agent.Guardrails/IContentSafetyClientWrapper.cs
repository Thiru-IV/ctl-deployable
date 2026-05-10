using Azure.AI.ContentSafety;

namespace Cascade.CTL.Agent.Guardrails;

/// <summary>
/// Abstraction over Azure ContentSafetyClient to enable unit testing.
/// The Azure SDK's ContentSafetyClient is sealed and cannot be mocked directly.
/// Returns a simplified result type to decouple tests from the Azure SDK.
/// </summary>
public interface IContentSafetyClientWrapper
{
    Task<ContentModerationResult> AnalyzeTextAsync(AnalyzeTextOptions options, CancellationToken cancellationToken = default);
}

/// <summary>
/// Simplified content moderation result that decouples from the Azure SDK's sealed AnalyzeTextResult.
/// </summary>
public sealed record ContentModerationResult
{
    public required ContentModerationCategory[] Categories { get; init; }
}

public sealed record ContentModerationCategory
{
    public required string Category { get; init; }
    public required int Severity { get; init; }
}

/// <summary>
/// Production wrapper that delegates to the real Azure ContentSafetyClient.
/// </summary>
internal sealed class ContentSafetyClientWrapper : IContentSafetyClientWrapper
{
    private readonly ContentSafetyClient _client;

    public ContentSafetyClientWrapper(ContentSafetyClient client)
    {
        _client = client;
    }

    public async Task<ContentModerationResult> AnalyzeTextAsync(AnalyzeTextOptions options, CancellationToken cancellationToken = default)
    {
        var response = await _client.AnalyzeTextAsync(options, cancellationToken);
        return new ContentModerationResult
        {
            Categories = response.Value.CategoriesAnalysis
                .Select(c => new ContentModerationCategory
                {
                    Category = c.Category.ToString(),
                    Severity = c.Severity ?? 0
                })
                .ToArray()
        };
    }
}
