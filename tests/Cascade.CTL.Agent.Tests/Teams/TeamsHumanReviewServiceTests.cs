using Cascade.CTL.Agent.Domain.Contracts;
using Cascade.CTL.Agent.Domain.Enums;
using Cascade.CTL.Agent.Domain.Models;
using Cascade.CTL.Agent.Infrastructure.Teams;
using FluentAssertions;
using Microsoft.Bot.Builder.Integration.AspNet.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace Cascade.CTL.Agent.Tests.Teams;

/// <summary>
/// Unit tests for <see cref="TeamsHumanReviewService"/>. Focused on the deterministic
/// codepaths that do NOT require a real Bot Framework adapter — primarily the
/// "no conversation reference captured yet → defer to fallback" path. The proactive
/// SendCardAsync path is exercised by integration tests against the Bot Framework
/// Emulator (out of scope for unit tests).
/// </summary>
public class TeamsHumanReviewServiceTests
{
    private static HumanReviewRequest Request() => new()
    {
        SessionId = "sess-1",
        AssetId = "asset-1",
        ProposedVerdict = new CTLVerdictDto
        {
            AssetId = "asset-1",
            SessionId = "sess-1",
            Verdict = CTLVerdict.NeedsHumanReview,
            ConfidenceScore = 0.55,
            Conditions = Array.Empty<string>(),
            EvidenceTrail = Array.Empty<string>(),
            ReflectionLog = "r",
            Timestamp = DateTime.UtcNow
        },
        ReflectionOutput = "reflection"
    };

    private static TeamsHumanReviewService Build(
        IHumanReviewService fallback,
        IConversationReferenceStore store,
        TeamsHitlOptions? options = null)
    {
        return new TeamsHumanReviewService(
            fallback: fallback,
            adapter: Substitute.For<IBotFrameworkHttpAdapter>(),
            store: store,
            registry: new InMemoryPendingReviewRegistry(),
            options: Options.Create(options ?? new TeamsHitlOptions
            {
                Enabled = true,
                MicrosoftAppId = "00000000-0000-0000-0000-000000000000",
                DefaultReviewerUpn = "reviewer@contoso.com",
                ResponseTimeoutSeconds = 30
            }),
            logger: Substitute.For<ILogger<TeamsHumanReviewService>>());
    }

    [Fact]
    public async Task RequestReviewAsync_DelegatesToFallback_WhenNoConversationReferenceExists()
    {
        var fallback = Substitute.For<IHumanReviewService>();
        var fallbackDecision = new HumanReviewDecision
        {
            Action = HumanReviewAction.Confirm,
            ReviewerNotes = "auto",
            ReviewedBy = "auto"
        };
        fallback
            .RequestReviewAsync(Arg.Any<HumanReviewRequest>(), Arg.Any<CancellationToken>())
            .Returns(fallbackDecision);

        var sut = Build(fallback, new InMemoryConversationReferenceStore());

        var result = await sut.RequestReviewAsync(Request(), CancellationToken.None);

        result.Should().BeSameAs(fallbackDecision);
        await fallback.Received(1).RequestReviewAsync(Arg.Any<HumanReviewRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RequestReviewAsync_DoesNotLeavePendingRegistration_WhenFallingBackEarly()
    {
        var fallback = Substitute.For<IHumanReviewService>();
        fallback
            .RequestReviewAsync(Arg.Any<HumanReviewRequest>(), Arg.Any<CancellationToken>())
            .Returns(new HumanReviewDecision
            {
                Action = HumanReviewAction.Confirm,
                ReviewerNotes = "auto",
                ReviewedBy = "auto"
            });

        var registry = new InMemoryPendingReviewRegistry();
        var sut = new TeamsHumanReviewService(
            fallback,
            Substitute.For<IBotFrameworkHttpAdapter>(),
            new InMemoryConversationReferenceStore(),
            registry,
            Options.Create(new TeamsHitlOptions { Enabled = true, ResponseTimeoutSeconds = 30 }),
            Substitute.For<ILogger<TeamsHumanReviewService>>());

        var req = Request();
        await sut.RequestReviewAsync(req, CancellationToken.None);

        // Early-return path must not have registered anything pending for this session.
        registry.IsPending(req.SessionId).Should().BeFalse();
    }
}
