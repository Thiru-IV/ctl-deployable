using System.Text.Json;
using Cascade.CTL.Agent.Domain.Enums;
using Cascade.CTL.Agent.Domain.Models;
using Microsoft.Bot.Builder;
using Microsoft.Bot.Schema;
using Microsoft.Extensions.Logging;

namespace Cascade.CTL.Agent.Infrastructure.Teams;

/// <summary>
/// Bot Framework activity handler. Two responsibilities:
///  1. Capture the reviewer's <see cref="ConversationReference"/> on first contact
///     so the agent can later push proactive Adaptive Cards.
///  2. Handle Adaptive Card submit activities (button clicks) and signal the
///     waiting workflow via <see cref="IPendingReviewRegistry"/>.
/// </summary>
public sealed class HitlNotifierBot : ActivityHandler
{
    private readonly IConversationReferenceStore _store;
    private readonly IPendingReviewRegistry _registry;
    private readonly ILogger<HitlNotifierBot> _logger;

    public HitlNotifierBot(
        IConversationReferenceStore store,
        IPendingReviewRegistry registry,
        ILogger<HitlNotifierBot> logger)
    {
        _store = store;
        _registry = registry;
        _logger = logger;
    }

    protected override Task OnConversationUpdateActivityAsync(
        ITurnContext<IConversationUpdateActivity> turnContext, CancellationToken cancellationToken)
    {
        CaptureReference(turnContext);
        return base.OnConversationUpdateActivityAsync(turnContext, cancellationToken);
    }

    protected override async Task OnMembersAddedAsync(
        IList<ChannelAccount> membersAdded,
        ITurnContext<IConversationUpdateActivity> turnContext,
        CancellationToken cancellationToken)
    {
        CaptureReference(turnContext);
        foreach (var member in membersAdded)
        {
            if (member.Id != turnContext.Activity.Recipient.Id)
            {
                await turnContext.SendActivityAsync(
                    MessageFactory.Text(
                        "👋 Cascade CTL HITL bot connected. " +
                        "I'll DM you Adaptive Cards when an asset evaluation needs human review."),
                    cancellationToken);
            }
        }
    }

    protected override async Task OnMessageActivityAsync(
        ITurnContext<IMessageActivity> turnContext, CancellationToken cancellationToken)
    {
        CaptureReference(turnContext);

        // Adaptive Card submit clicks arrive as a Message activity with Value populated.
        if (turnContext.Activity.Value is not null)
        {
            await HandleSubmitAsync(turnContext, cancellationToken);
            return;
        }

        await turnContext.SendActivityAsync(
            MessageFactory.Text(
                $"Registered. You'll receive HITL notifications here. " +
                $"(Reviewer: {turnContext.Activity.From?.Name ?? turnContext.Activity.From?.Id ?? "unknown"})"),
            cancellationToken);
    }

    private async Task HandleSubmitAsync(ITurnContext<IMessageActivity> turnContext, CancellationToken ct)
    {
        // Bot Builder deserializes Activity.Value with Newtonsoft, so it normally arrives as
        // a Newtonsoft.Json.Linq.JObject. System.Text.Json.JsonSerializer.Serialize(JObject)
        // would serialize the JObject's CLR properties (HasValues, Type, ...) instead of the
        // underlying JSON, so call ToString() on JToken to get the real payload JSON.
        var raw = turnContext.Activity.Value;
        string json = raw switch
        {
            null => "{}",
            string s => s,
            Newtonsoft.Json.Linq.JToken jt => jt.ToString(Newtonsoft.Json.Formatting.None),
            _ => JsonSerializer.Serialize(raw)
        };

        SubmitPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<SubmitPayload>(json,
                new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString
                });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[HITL-Bot] Failed to parse submit payload: {Json}", json);
            await turnContext.SendActivityAsync(MessageFactory.Text("⚠️ Could not parse your response."), ct);
            return;
        }

        if (payload is null || string.IsNullOrWhiteSpace(payload.SessionId) || string.IsNullOrWhiteSpace(payload.Action))
        {
            _logger.LogWarning("[HITL-Bot] Submit payload missing sessionId/action: {Json}", json);
            await turnContext.SendActivityAsync(MessageFactory.Text("⚠️ Missing sessionId or action."), ct);
            return;
        }

        if (!Enum.TryParse<HumanReviewAction>(payload.Action, ignoreCase: true, out var action))
        {
            await turnContext.SendActivityAsync(
                MessageFactory.Text($"⚠️ Unknown action '{payload.Action}'."), ct);
            return;
        }

        var reviewerName = turnContext.Activity.From?.Name
                           ?? turnContext.Activity.From?.Properties?["userPrincipalName"]?.ToString()
                           ?? turnContext.Activity.From?.Id
                           ?? "teams.reviewer";

        CTLVerdict? overriddenVerdict = null;
        if (action == HumanReviewAction.OverrideVerdict
            && !string.IsNullOrWhiteSpace(payload.OverrideVerdict)
            && Enum.TryParse<CTLVerdict>(payload.OverrideVerdict, ignoreCase: true, out var parsed))
        {
            overriddenVerdict = parsed;
        }

        var notes = string.IsNullOrWhiteSpace(payload.Notes)
            ? $"Reviewed via Teams. Action: {action}."
            : payload.Notes!.Trim();

        var decision = new HumanReviewDecision
        {
            Action = action,
            OverriddenVerdict = overriddenVerdict,
            // Always capture the reviewer-supplied confidence regardless of action so an
            // adjusted slider takes effect on both Accept and Override.
            OverriddenConfidence = payload.OverrideConfidence,
            ReviewerNotes = notes,
            ReviewedBy = reviewerName
        };

        var completed = _registry.Complete(payload.SessionId!, decision);
        if (completed)
        {
            _logger.LogInformation(
                "[HITL-Bot] Decision recorded for Session={SessionId} Action={Action} By={By}",
                payload.SessionId, action, reviewerName);
            await turnContext.SendActivityAsync(
                MessageFactory.Text(
                    $"✅ Recorded **{action}** for asset `{payload.AssetId}` (session `{payload.SessionId}`)."),
                ct);
        }
        else
        {
            _logger.LogWarning(
                "[HITL-Bot] No pending review for Session={SessionId} (timed out or already answered).",
                payload.SessionId);
            await turnContext.SendActivityAsync(
                MessageFactory.Text(
                    $"⚠️ No pending review for session `{payload.SessionId}`. It may have timed out."),
                ct);
        }
    }

    private void CaptureReference(ITurnContext turnContext)
    {
        var activity = turnContext.Activity;
        var reference = activity.GetConversationReference();

        var aad = activity.From?.AadObjectId ?? activity.From?.Id ?? "unknown";
        _store.Save(aad, reference);

        var upn = activity.From?.Properties?["userPrincipalName"]?.ToString();
        if (!string.IsNullOrWhiteSpace(upn)) _store.Save(upn, reference);

        var name = activity.From?.Name;
        if (!string.IsNullOrWhiteSpace(name)) _store.Save(name, reference);

        _logger.LogInformation(
            "[HITL-Bot] Captured conversation reference. AadObjectId={Aad} Upn={Upn} Name={Name}",
            aad, upn, name);
    }

    private sealed class SubmitPayload
    {
        public string? Action { get; set; }
        public string? SessionId { get; set; }
        public string? AssetId { get; set; }
        public string? OverrideVerdict { get; set; }
        public double? OverrideConfidence { get; set; }
        public string? Notes { get; set; }
    }
}
