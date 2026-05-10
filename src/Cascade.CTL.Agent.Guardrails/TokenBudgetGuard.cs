using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cascade.CTL.Agent.Guardrails;

public sealed class TokenBudgetOptions
{
    public int MaxTokenBudget { get; set; } = 50000;
}

/// <summary>
/// Tracks per-evaluation token budgets using session-scoped counters.
/// The guard itself is registered as a singleton; isolation between concurrent
/// evaluations is achieved via <see cref="CurrentSessionId"/> which flows
/// through the async call stack using <see cref="AsyncLocal{T}"/>.
/// </summary>
public sealed class TokenBudgetGuard
{
    private static readonly AsyncLocal<string?> _currentSessionId = new();
    private readonly ConcurrentDictionary<string, int> _sessionTokens = new();
    private readonly ILogger<TokenBudgetGuard> _logger;
    private readonly int _maxTokenBudget;

    private const string GlobalSessionKey = "__global__";

    public TokenBudgetGuard(ILogger<TokenBudgetGuard> logger, IOptions<TokenBudgetOptions> options)
    {
        _logger = logger;
        _maxTokenBudget = options.Value.MaxTokenBudget;
    }

    /// <summary>
    /// Gets or sets the active session ID for the current async flow.
    /// The orchestrator sets this at the start of each evaluation; the value
    /// automatically propagates through <c>Task.WhenAll</c> and other async
    /// continuations to the <see cref="GuardrailsMiddleware"/>.
    /// </summary>
    public static string? CurrentSessionId
    {
        get => _currentSessionId.Value;
        set => _currentSessionId.Value = value;
    }

    private string ActiveSessionKey => _currentSessionId.Value ?? GlobalSessionKey;

    public int CurrentUsage => _sessionTokens.GetValueOrDefault(ActiveSessionKey, 0);
    public int Budget => _maxTokenBudget;
    public bool IsWithinBudget => CurrentUsage < _maxTokenBudget;

    public bool TryConsumeTokens(int tokens)
    {
        var key = ActiveSessionKey;
        var newTotal = _sessionTokens.AddOrUpdate(key, tokens, (_, current) => current + tokens);
        if (newTotal > _maxTokenBudget)
        {
            _logger.LogWarning(
                "Token budget exceeded for session {SessionId}: {Current}/{Max} tokens used",
                key, newTotal, _maxTokenBudget);
            return false;
        }

        _logger.LogDebug("Token usage for session {SessionId}: {Current}/{Max}", key, newTotal, _maxTokenBudget);
        return true;
    }

    public void Reset()
    {
        var key = ActiveSessionKey;
        _sessionTokens.TryRemove(key, out _);
        _logger.LogDebug("Token budget reset for session {SessionId}", key);
    }
}
