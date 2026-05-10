using Cascade.CTL.Agent.Domain.Contracts;
using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.DataContracts;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;

namespace Cascade.CTL.Agent.Infrastructure.Observability;

public sealed class AppInsightsAuditService : IAuditService
{
    private readonly TelemetryClient _telemetryClient;
    private readonly ILogger<AppInsightsAuditService> _logger;
    private readonly AuditFileStore _fileStore;
    private readonly ConcurrentDictionary<string, ConcurrentBag<AuditEntry>> _sessions = new();

    public AppInsightsAuditService(TelemetryClient telemetryClient, ILogger<AppInsightsAuditService> logger, AuditFileStore fileStore)
    {
        _telemetryClient = telemetryClient;
        _logger = logger;
        _fileStore = fileStore;
    }

    public Task RecordStepAsync(AuditEntry entry, CancellationToken cancellationToken = default)
    {
        var bag = _sessions.GetOrAdd(entry.SessionId, _ => []);
        bag.Add(entry);

        // Write-through to disk for durability
        _fileStore.AppendEntry(entry);

        var telemetry = new EventTelemetry("CTL.AuditStep")
        {
            Timestamp = entry.Timestamp
        };

        telemetry.Properties["SessionId"] = entry.SessionId;
        telemetry.Properties["AssetId"] = entry.AssetId;
        telemetry.Properties["AgentName"] = entry.AgentName;
        telemetry.Properties["StepType"] = entry.StepType;
        telemetry.Properties["Description"] = entry.Description;

        if (entry.CorrelationId is not null)
            telemetry.Properties["CorrelationId"] = entry.CorrelationId;

        if (entry.InputHash is not null)
            telemetry.Properties["InputHash"] = entry.InputHash;

        if (entry.OutputHash is not null)
            telemetry.Properties["OutputHash"] = entry.OutputHash;

        if (entry.TokensUsed.HasValue)
            telemetry.Metrics["TokensUsed"] = entry.TokensUsed.Value;

        if (entry.Duration.HasValue)
            telemetry.Metrics["DurationMs"] = entry.Duration.Value.TotalMilliseconds;

        if (entry.OutputPayload is not null)
            telemetry.Properties["OutputPayload"] = entry.OutputPayload;

        _telemetryClient.TrackEvent(telemetry);

        _logger.LogDebug(
            "[AUDIT] Session={SessionId} Asset={AssetId} Agent={AgentName} Step={StepType}",
            entry.SessionId, entry.AssetId, entry.AgentName, entry.StepType);

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<AuditEntry>> GetSessionAuditTrailAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        if (_sessions.TryGetValue(sessionId, out var bag))
        {
            IReadOnlyList<AuditEntry> result = bag.OrderBy(e => e.Timestamp).ToList();
            return Task.FromResult(result);
        }

        IReadOnlyList<AuditEntry> diskResult = _fileStore.ReadSession(sessionId);
        return Task.FromResult(diskResult);
    }

    public Task<IReadOnlyList<string>> GetRecentSessionIdsAsync(int count = 20, CancellationToken cancellationToken = default)
    {
        var diskIds = _fileStore.GetPersistedSessionIds(count);
        var memoryIds = _sessions.Keys.ToList();

        IReadOnlyList<string> result = diskIds
            .Concat(memoryIds)
            .Distinct()
            .TakeLast(count)
            .ToList();
        return Task.FromResult(result);
    }
}
