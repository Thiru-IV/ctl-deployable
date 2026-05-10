namespace Cascade.CTL.Agent.Domain.Contracts;

public interface IAuditService
{
    Task RecordStepAsync(AuditEntry entry, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AuditEntry>> GetSessionAuditTrailAsync(string sessionId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<string>> GetRecentSessionIdsAsync(int count = 20, CancellationToken cancellationToken = default);
}

public sealed record AuditEntry
{
    public required string SessionId { get; init; }
    public required string AssetId { get; init; }
    public required string AgentName { get; init; }
    public required string StepType { get; init; }
    public required string Description { get; init; }
    public string? InputHash { get; init; }
    public string? OutputHash { get; init; }
    public int? TokensUsed { get; init; }
    public TimeSpan? Duration { get; init; }
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
    public string? CorrelationId { get; init; }
    public string? OutputPayload { get; init; }
}
