using System.Collections.Concurrent;
using Cascade.CTL.Agent.Domain.Models;

namespace Cascade.CTL.Agent.Infrastructure.Teams;

/// <summary>
/// Tracks in-flight HITL reviews so that the workflow can asynchronously wait
/// for a human reviewer's button click in Teams. Keyed by SessionId.
/// </summary>
public interface IPendingReviewRegistry
{
    /// <summary>
    /// Registers a pending review and returns a task that completes when either
    /// (a) <see cref="Complete"/> is called for the same sessionId, or
    /// (b) the supplied timeout elapses (returns null).
    /// </summary>
    Task<HumanReviewDecision?> RegisterAsync(string sessionId, TimeSpan timeout, CancellationToken ct);

    /// <summary>Returns true if a pending review existed and was completed.</summary>
    bool Complete(string sessionId, HumanReviewDecision decision);

    /// <summary>True if a review is currently awaiting a Teams response.</summary>
    bool IsPending(string sessionId);
}

public sealed class InMemoryPendingReviewRegistry : IPendingReviewRegistry
{
    private readonly ConcurrentDictionary<string, TaskCompletionSource<HumanReviewDecision>> _pending = new();

    public async Task<HumanReviewDecision?> RegisterAsync(string sessionId, TimeSpan timeout, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<HumanReviewDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[sessionId] = tcs;

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        var delayTask = Task.Delay(Timeout.InfiniteTimeSpan, timeoutCts.Token);

        try
        {
            var winner = await Task.WhenAny(tcs.Task, delayTask);
            if (winner == tcs.Task)
            {
                return await tcs.Task;
            }
            return null; // timeout or cancellation
        }
        finally
        {
            _pending.TryRemove(sessionId, out _);
        }
    }

    public bool Complete(string sessionId, HumanReviewDecision decision)
    {
        if (_pending.TryRemove(sessionId, out var tcs))
        {
            return tcs.TrySetResult(decision);
        }
        return false;
    }

    public bool IsPending(string sessionId) => _pending.ContainsKey(sessionId);
}
