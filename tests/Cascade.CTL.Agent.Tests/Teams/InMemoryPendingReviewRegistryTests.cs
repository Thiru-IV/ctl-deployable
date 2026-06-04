using Cascade.CTL.Agent.Domain.Enums;
using Cascade.CTL.Agent.Domain.Models;
using Cascade.CTL.Agent.Infrastructure.Teams;
using FluentAssertions;
using Xunit;

namespace Cascade.CTL.Agent.Tests.Teams;

public class InMemoryPendingReviewRegistryTests
{
    private static HumanReviewDecision Decision(string by = "reviewer") => new()
    {
        Action = HumanReviewAction.Confirm,
        ReviewerNotes = "ok",
        ReviewedBy = by
    };

    [Fact]
    public async Task RegisterAsync_CompletesWithDecision_WhenCompleteCalledBeforeTimeout()
    {
        var sut = new InMemoryPendingReviewRegistry();
        var sessionId = Guid.NewGuid().ToString();

        var pending = sut.RegisterAsync(sessionId, TimeSpan.FromSeconds(5), CancellationToken.None);

        // Allow Register to actually insert before Complete races.
        await Task.Yield();
        sut.IsPending(sessionId).Should().BeTrue();

        var expected = Decision("alice");
        var completed = sut.Complete(sessionId, expected);

        completed.Should().BeTrue();
        var actual = await pending;
        actual.Should().BeSameAs(expected);
        sut.IsPending(sessionId).Should().BeFalse();
    }

    [Fact]
    public async Task RegisterAsync_ReturnsNull_WhenTimeoutElapses()
    {
        var sut = new InMemoryPendingReviewRegistry();
        var sessionId = Guid.NewGuid().ToString();

        var result = await sut.RegisterAsync(sessionId, TimeSpan.FromMilliseconds(50), CancellationToken.None);

        result.Should().BeNull();
        sut.IsPending(sessionId).Should().BeFalse("registry must clean up after timeout");
    }

    [Fact]
    public async Task RegisterAsync_ReturnsNull_WhenExternalCancellationFires()
    {
        var sut = new InMemoryPendingReviewRegistry();
        var sessionId = Guid.NewGuid().ToString();
        using var cts = new CancellationTokenSource();

        var pending = sut.RegisterAsync(sessionId, TimeSpan.FromSeconds(30), cts.Token);
        await Task.Yield();
        cts.Cancel();

        var result = await pending;
        result.Should().BeNull();
        sut.IsPending(sessionId).Should().BeFalse();
    }

    [Fact]
    public void Complete_ReturnsFalse_WhenNoPendingReviewForSession()
    {
        var sut = new InMemoryPendingReviewRegistry();
        sut.Complete("unknown-session", Decision()).Should().BeFalse();
    }

    [Fact]
    public async Task Complete_IsIdempotent_SecondCallReturnsFalse()
    {
        var sut = new InMemoryPendingReviewRegistry();
        var sessionId = Guid.NewGuid().ToString();
        var pending = sut.RegisterAsync(sessionId, TimeSpan.FromSeconds(5), CancellationToken.None);
        await Task.Yield();

        sut.Complete(sessionId, Decision()).Should().BeTrue();
        await pending; // drain

        sut.Complete(sessionId, Decision()).Should().BeFalse();
    }
}
