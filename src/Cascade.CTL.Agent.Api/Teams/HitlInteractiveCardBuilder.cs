using AdaptiveCards;
using Cascade.CTL.Agent.Domain.Enums;
using Cascade.CTL.Agent.Domain.Models;
using Microsoft.Bot.Schema;

namespace Cascade.CTL.Agent.Api.Teams;

/// <summary>
/// Builds an INTERACTIVE Adaptive Card for Teams HITL with Action.Submit buttons.
/// The reviewer's click round-trips back to the bot's /api/messages endpoint
/// as an activity whose <c>activity.Value</c> contains the submit payload
/// (action, sessionId, optional override verdict + notes).
/// </summary>
internal static class HitlInteractiveCardBuilder
{
    public static Attachment Build(HumanReviewRequest request, string cascadeUrlTemplate)
    {
        var verdict = request.ProposedVerdict;
        var sessionId = request.SessionId;
        var assetId = verdict.AssetId;

        var card = new AdaptiveCard(new AdaptiveSchemaVersion(1, 4))
        {
            Body =
            {
                new AdaptiveTextBlock("🚨 CTL — Human Review Required")
                {
                    Size = AdaptiveTextSize.Large,
                    Weight = AdaptiveTextWeight.Bolder,
                    Color = AdaptiveTextColor.Attention
                },
                new AdaptiveFactSet
                {
                    Facts =
                    {
                        new AdaptiveFact("Asset ID", assetId),
                        new AdaptiveFact("Session", sessionId),
                        new AdaptiveFact("Proposed Verdict", verdict.Verdict.ToString()),
                        new AdaptiveFact("Confidence", verdict.ConfidenceScore.ToString("P0")),
                        new AdaptiveFact("Requested", request.RequestedAt.ToString("u"))
                    }
                },
                new AdaptiveTextBlock("Top findings")
                {
                    Weight = AdaptiveTextWeight.Bolder,
                    Spacing = AdaptiveSpacing.Medium
                },
                new AdaptiveTextBlock(BuildFindings(verdict)) { Wrap = true, IsSubtle = true },

                // Override controls (used only when the reviewer clicks "Override").
                new AdaptiveTextBlock("Override (optional)")
                {
                    Weight = AdaptiveTextWeight.Bolder,
                    Spacing = AdaptiveSpacing.Medium
                },
                new AdaptiveChoiceSetInput
                {
                    Id = "overrideVerdict",
                    Style = AdaptiveChoiceInputStyle.Compact,
                    Value = verdict.Verdict.ToString(),
                    Choices =
                    {
                        new AdaptiveChoice { Title = "Clear", Value = nameof(CTLVerdict.Clear) },
                        new AdaptiveChoice { Title = "ClearWithConditions", Value = nameof(CTLVerdict.ClearWithConditions) },
                        new AdaptiveChoice { Title = "NotClear", Value = nameof(CTLVerdict.NotClear) },
                        new AdaptiveChoice { Title = "NeedsHumanReview", Value = nameof(CTLVerdict.NeedsHumanReview) }
                    }
                },
                new AdaptiveNumberInput
                {
                    Id = "overrideConfidence",
                    Placeholder = "Confidence (0.0 - 1.0)",
                    Min = 0,
                    Max = 1,
                    Value = verdict.ConfidenceScore
                },
                new AdaptiveTextInput
                {
                    Id = "notes",
                    Placeholder = "Reviewer notes (required for audit)",
                    IsMultiline = true,
                    MaxLength = 1000
                }
            },
            Actions =
            {
                new AdaptiveSubmitAction
                {
                    Title = "✅ Confirm",
                    Data = new { action = "Confirm", sessionId, assetId }
                },
                new AdaptiveSubmitAction
                {
                    Title = "✏️ Override",
                    Data = new { action = "OverrideVerdict", sessionId, assetId }
                },
                new AdaptiveSubmitAction
                {
                    Title = "🔁 Re-evaluate",
                    Data = new { action = "RequestReEvaluation", sessionId, assetId }
                },
                new AdaptiveOpenUrlAction
                {
                    Title = "Open in Cascade",
                    Url = new Uri(string.Format(cascadeUrlTemplate, assetId, sessionId))
                }
            }
        };

        return new Attachment
        {
            ContentType = AdaptiveCard.ContentType,
            Content = card
        };
    }

    private static string BuildFindings(CTLVerdictDto verdict)
    {
        var lines = new List<string>();
        if (verdict.EvidenceTrail is { Length: > 0 })
        {
            foreach (var item in verdict.EvidenceTrail.Take(3))
            {
                lines.Add($"• {Trim(item, 220)}");
            }
        }
        if (verdict.Conditions is { Length: > 0 })
        {
            lines.Add($"Conditions: {string.Join("; ", verdict.Conditions.Take(3).Select(c => Trim(c, 100)))}");
        }
        return lines.Count == 0 ? "(no evidence captured)" : string.Join("\n", lines);
    }

    private static string Trim(string s, int max) =>
        string.IsNullOrEmpty(s) || s.Length <= max ? s : s[..max] + "…";
}
