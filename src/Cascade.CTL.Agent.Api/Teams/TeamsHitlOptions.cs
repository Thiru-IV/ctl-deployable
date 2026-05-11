namespace Cascade.CTL.Agent.Api.Teams;

/// <summary>
/// Configuration for the Teams HITL notification channel.
/// Bound from <c>CTLAgent:Teams</c> in appsettings.json.
/// </summary>
public sealed class TeamsHitlOptions
{
    public const string SectionName = "CTLAgent:Teams";

    /// <summary>Master switch. When false, AutoApproveHumanReviewService is used directly.</summary>
    public bool Enabled { get; set; }

    /// <summary>Bot Framework Microsoft App ID (Entra app registration client ID).</summary>
    public string MicrosoftAppId { get; set; } = string.Empty;

    /// <summary>Bot Framework Microsoft App password (Entra app registration secret).</summary>
    public string MicrosoftAppPassword { get; set; } = string.Empty;

    /// <summary>"MultiTenant" (default), "SingleTenant", or "UserAssignedMSI".</summary>
    public string MicrosoftAppType { get; set; } = "MultiTenant";

    /// <summary>Tenant ID — required only for SingleTenant / UserAssignedMSI.</summary>
    public string MicrosoftAppTenantId { get; set; } = string.Empty;

    /// <summary>
    /// Deep-link template for the "Open in Cascade" button. {0} = AssetId, {1} = SessionId.
    /// e.g. "https://cascade.xome.com/reviews/{0}?session={1}"
    /// </summary>
    public string CascadeReviewUrlTemplate { get; set; } = "https://cascade.example.com/reviews/{0}?session={1}";

    /// <summary>
    /// Optional UPN of the default reviewer. The bot logs a warning if no conversation reference
    /// has been captured yet for this user (i.e. they haven't messaged the bot once).
    /// </summary>
    public string DefaultReviewerUpn { get; set; } = string.Empty;

    /// <summary>
    /// How long the workflow waits for the reviewer to click a button in Teams
    /// before falling back to the inner auto-approve service.
    /// </summary>
    public int ResponseTimeoutSeconds { get; set; } = 300;
}
