using Cascade.CTL.Agent.Domain.Contracts;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace Cascade.CTL.Agent.Infrastructure.Observability;

/// <summary>
/// In-memory audit service that persists all audit entries to disk (JSONL)
/// and keeps them in memory for fast retrieval. Supports reviewing old runs
/// from previous process invocations via the file store.
/// Used in development and as the default when Application Insights is not configured.
/// </summary>
public sealed class InMemoryAuditService : IAuditService
{
    private readonly ILogger<InMemoryAuditService> _logger;
    private readonly AuditFileStore _fileStore;
    private readonly ConcurrentDictionary<string, ConcurrentBag<AuditEntry>> _sessions = new();

    public InMemoryAuditService(ILogger<InMemoryAuditService> logger, AuditFileStore fileStore)
    {
        _logger = logger;
        _fileStore = fileStore;
    }

    public Task RecordStepAsync(AuditEntry entry, CancellationToken cancellationToken = default)
    {
        var bag = _sessions.GetOrAdd(entry.SessionId, _ => []);
        bag.Add(entry);

        // Write-through to disk for durability
        _fileStore.AppendEntry(entry);

        _logger.LogInformation(
            "[AUDIT] Session={SessionId} Asset={AssetId} Agent={AgentName} Step={StepType} | {Description} | Tokens={Tokens} Duration={Duration}ms",
            entry.SessionId,
            entry.AssetId,
            entry.AgentName,
            entry.StepType,
            entry.Description,
            entry.TokensUsed,
            entry.Duration?.TotalMilliseconds);

        if (entry.OutputPayload is not null)
        {
            _logger.LogDebug(
                "[AUDIT:PAYLOAD] Session={SessionId} Step={StepType} Payload={Payload}",
                entry.SessionId, entry.StepType, entry.OutputPayload);
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<AuditEntry>> GetSessionAuditTrailAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        // Check in-memory first (current process sessions)
        if (_sessions.TryGetValue(sessionId, out var bag))
        {
            IReadOnlyList<AuditEntry> result = bag.OrderBy(e => e.Timestamp).ToList();
            return Task.FromResult(result);
        }

        // Fall back to disk (old sessions from previous runs)
        IReadOnlyList<AuditEntry> diskResult = _fileStore.ReadSession(sessionId);
        return Task.FromResult(diskResult);
    }

    public Task<IReadOnlyList<string>> GetRecentSessionIdsAsync(int count = 20, CancellationToken cancellationToken = default)
    {
        // Merge in-memory session IDs with persisted ones from disk
        var diskIds = _fileStore.GetPersistedSessionIds(count);
        var memoryIds = _sessions.Keys.ToList();

        IReadOnlyList<string> result = diskIds
            .Concat(memoryIds)
            .Distinct()
            .TakeLast(count)
            .ToList();
        return Task.FromResult(result);
    }

    public static string ComputeHash(string input)
    {
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(hashBytes)[..16];
    }
}
