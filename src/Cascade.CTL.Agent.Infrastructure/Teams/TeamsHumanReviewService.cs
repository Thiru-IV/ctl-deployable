using Cascade.CTL.Agent.Domain.Contracts;
using Cascade.CTL.Agent.Domain.Enums;
using Cascade.CTL.Agent.Domain.Models;
using Microsoft.Bot.Builder;
using Microsoft.Bot.Builder.Integration.AspNet.Core;
using Microsoft.Bot.Schema;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cascade.CTL.Agent.Infrastructure.Teams;

/// <summary>
/// Teams-bound HITL implementation. Sends an interactive Adaptive Card to the
/// configured reviewer's DM and BLOCKS the workflow until either:
///   1) the reviewer clicks a button (Confirm / Override / Re-evaluate), or
///   2) the configured timeout elapses, in which case the inner fallback
///      service (typically AutoApprove) is used.
///
/// Standalone POC use only — for production the system of record is Cascade 2.0
/// and Teams should be notification-only.
/// </summary>
public sealed class TeamsHumanReviewService : IHumanReviewService
{
    private readonly IHumanReviewService _fallback;
    private readonly IBotFrameworkHttpAdapter _adapter;
    private readonly IConversationReferenceStore _store;
    private readonly IPendingReviewRegistry _registry;
    private readonly TeamsHitlOptions _options;
    private readonly ILogger<TeamsHumanReviewService> _logger;

    public TeamsHumanReviewService(
        IHumanReviewService fallback,
        IBotFrameworkHttpAdapter adapter,
        IConversationReferenceStore store,
        IPendingReviewRegistry registry,
        IOptions<TeamsHitlOptions> options,
        ILogger<TeamsHumanReviewService> logger)
    {
        _fallback = fallback;
        _adapter = adapter;
        _store = store;
        _registry = registry;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<HumanReviewDecision> RequestReviewAsync(
        HumanReviewRequest request, CancellationToken cancellationToken = default)
    {
        var reference = ResolveReference();
        if (reference is null)
        {
            _logger.LogWarning(
                "[Teams-HITL] No conversation reference for reviewer '{Upn}'. " +
                "Falling back to auto-approve. Reviewer must DM the bot once to register.",
                _options.DefaultReviewerUpn);
            return await _fallback.RequestReviewAsync(request, cancellationToken);
        }

        // Register BEFORE sending so a fast click can't race past us.
        var timeout = TimeSpan.FromSeconds(Math.Max(10, _options.ResponseTimeoutSeconds));
        var pendingTask = _registry.RegisterAsync(request.SessionId, timeout, cancellationToken);

        try
        {
            await SendCardAsync(reference, request, cancellationToken);
            _logger.LogInformation(
                "[Teams-HITL] Awaiting reviewer click. Asset={AssetId} Session={SessionId} Timeout={TimeoutSec}s",
                request.AssetId, request.SessionId, timeout.TotalSeconds);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "[Teams-HITL] Failed to send Adaptive Card. Falling back to auto-approve. " +
                "Asset={AssetId} Session={SessionId}", request.AssetId, request.SessionId);
            // Cancel the pending registration so it doesn't sit until timeout.
            _registry.Complete(request.SessionId, FallbackDecision(request, "Card send failed"));
            return await _fallback.RequestReviewAsync(request, cancellationToken);
        }

        var decision = await pendingTask;
        if (decision is not null)
        {
            _logger.LogInformation(
                "[Teams-HITL] Reviewer responded. Asset={AssetId} Session={SessionId} Action={Action} By={By}",
                request.AssetId, request.SessionId, decision.Action, decision.ReviewedBy);
            return decision;
        }

        _logger.LogWarning(
            "[Teams-HITL] Timed out waiting for reviewer ({TimeoutSec}s). Falling back to auto-approve. " +
            "Asset={AssetId} Session={SessionId}",
            timeout.TotalSeconds, request.AssetId, request.SessionId);
        return await _fallback.RequestReviewAsync(request, cancellationToken);
    }

    private Task SendCardAsync(ConversationReference reference, HumanReviewRequest request, CancellationToken ct)
    {
        var attachment = HitlInteractiveCardBuilder.Build(request, _options.CascadeReviewUrlTemplate);

        return ((BotAdapter)_adapter).ContinueConversationAsync(
            _options.MicrosoftAppId,
            reference,
            async (turn, innerCt) =>
            {
                var activity = MessageFactory.Attachment(attachment);
                await turn.SendActivityAsync(activity, innerCt);
            },
            ct);
    }

    private ConversationReference? ResolveReference()
    {
        if (!string.IsNullOrWhiteSpace(_options.DefaultReviewerUpn))
        {
            var byUpn = _store.Get(_options.DefaultReviewerUpn);
            if (byUpn is not null) return byUpn;
        }
        return _store.All().FirstOrDefault();
    }

    private static HumanReviewDecision FallbackDecision(HumanReviewRequest request, string reason) => new()
    {
        Action = HumanReviewAction.Confirm,
        ReviewerNotes = $"Auto-confirmed (Teams unavailable: {reason}). Confidence {request.ProposedVerdict.ConfidenceScore:F2}.",
        ReviewedBy = "teams.hitl.fallback"
    };
}
