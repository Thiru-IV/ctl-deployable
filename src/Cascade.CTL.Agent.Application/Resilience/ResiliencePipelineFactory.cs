using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Resilience;
using Polly;
using Polly.Retry;

namespace Cascade.CTL.Agent.Application.Resilience;

/// <summary>
/// Creates Polly resilience pipelines for MCP initialization and agent LLM calls.
/// Centralizes retry + timeout configuration so hand-rolled loops are eliminated.
/// </summary>
public static class ResiliencePipelineFactory
{
    /// <summary>
    /// Pipeline for MCP server connection attempts.
    /// Retries on transient HTTP/IO/socket errors with exponential backoff.
    /// </summary>
    public static ResiliencePipeline CreateMcpInitPipeline(ResilienceOptions options, ILogger logger)
    {
        return new ResiliencePipelineBuilder()
            .AddRetry(new RetryStrategyOptions
            {
                MaxRetryAttempts = options.McpInitMaxRetryAttempts,
                BackoffType = DelayBackoffType.Exponential,
                Delay = TimeSpan.FromSeconds(2),
                ShouldHandle = new PredicateBuilder().Handle<Exception>(ex => IsMcpTransient(ex)),
                OnRetry = args =>
                {
                    logger.LogWarning(args.Outcome.Exception,
                        "MCP init retry {Attempt}/{Max} after {Delay}s",
                        args.AttemptNumber + 1, options.McpInitMaxRetryAttempts, args.RetryDelay.TotalSeconds);
                    return ValueTask.CompletedTask;
                }
            })
            .AddTimeout(TimeSpan.FromSeconds(options.McpInitTimeoutSeconds))
            .Build();
    }

    /// <summary>
    /// Pipeline for investigation agent LLM calls.
    /// Retries on transient HTTP errors (429, 5xx) with exponential backoff.
    /// </summary>
    public static ResiliencePipeline CreateAgentRetryPipeline(ResilienceOptions options, ILogger logger)
    {
        return new ResiliencePipelineBuilder()
            .AddRetry(new RetryStrategyOptions
            {
                MaxRetryAttempts = options.AgentMaxRetryAttempts,
                BackoffType = DelayBackoffType.Exponential,
                Delay = TimeSpan.FromMilliseconds(200),
                ShouldHandle = new PredicateBuilder().Handle<Exception>(ex => IsAgentTransient(ex)),
                OnRetry = args =>
                {
                    logger.LogWarning(args.Outcome.Exception,
                        "Agent retry {Attempt}/{Max} after {Delay}ms",
                        args.AttemptNumber + 1, options.AgentMaxRetryAttempts, args.RetryDelay.TotalMilliseconds);
                    return ValueTask.CompletedTask;
                }
            })
            .Build();
    }

    /// <summary>
    /// Transient errors for MCP connection: HTTP, IO, socket failures.
    /// </summary>
    internal static bool IsMcpTransient(Exception ex) => ex is
        HttpRequestException or IOException or System.Net.Sockets.SocketException
        or TimeoutException
        || (ex is TaskCanceledException tce && tce.InnerException is TimeoutException)
        || (ex.InnerException != null && IsMcpTransient(ex.InnerException));

    /// <summary>
    /// Transient errors for agent LLM calls: HTTP 429/5xx, timeouts, socket errors.
    /// Caller-initiated cancellation is NOT transient.
    /// </summary>
    internal static bool IsAgentTransient(Exception ex) => ex switch
    {
        TaskCanceledException tce when tce.InnerException is TimeoutException => true,
        TimeoutException => true,
        HttpRequestException httpEx => (int)(httpEx.StatusCode ?? 0) >= 500
            || httpEx.StatusCode == System.Net.HttpStatusCode.TooManyRequests,
        OperationCanceledException => false,
        IOException => true,
        System.Net.Sockets.SocketException => true,
        _ when ex.InnerException != null => IsAgentTransient(ex.InnerException),
        _ => false
    };
}
