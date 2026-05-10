using Cascade.CTL.Agent.Domain.Contracts;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cascade.CTL.Agent.Infrastructure.Observability;

/// <summary>
/// Handles reading and writing audit entries to disk as JSONL files.
/// Each session gets its own file: {AuditLogDirectory}/{sessionId}.jsonl
/// Entries are appended line-by-line for crash safety.
/// </summary>
public sealed class AuditFileStore
{
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(), new TimeSpanConverter() }
    };

    public string AuditLogDirectory { get; }

    public AuditFileStore(string? auditLogDirectory = null)
    {
        AuditLogDirectory = auditLogDirectory ?? Path.Combine(Directory.GetCurrentDirectory(), "audit-logs");
    }

    /// <summary>
    /// Appends a single audit entry to the session's JSONL file.
    /// Creates the directory and file if they don't exist.
    /// </summary>
    public void AppendEntry(AuditEntry entry)
    {
        Directory.CreateDirectory(AuditLogDirectory);
        var filePath = GetSessionFilePath(entry.SessionId);
        var json = JsonSerializer.Serialize(entry, s_jsonOptions);
        File.AppendAllText(filePath, json + Environment.NewLine);
    }

    /// <summary>
    /// Reads all audit entries for a session from disk.
    /// Returns empty list if no file exists.
    /// </summary>
    public IReadOnlyList<AuditEntry> ReadSession(string sessionId)
    {
        var filePath = GetSessionFilePath(sessionId);
        if (!File.Exists(filePath))
            return Array.Empty<AuditEntry>();

        var entries = new List<AuditEntry>();
        foreach (var line in File.ReadAllLines(filePath))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var entry = JsonSerializer.Deserialize<AuditEntry>(line, s_jsonOptions);
            if (entry is not null)
                entries.Add(entry);
        }
        return entries.OrderBy(e => e.Timestamp).ToList();
    }

    /// <summary>
    /// Returns session IDs from disk, ordered by file creation time (most recent last).
    /// </summary>
    public IReadOnlyList<string> GetPersistedSessionIds(int count = 50)
    {
        if (!Directory.Exists(AuditLogDirectory))
            return Array.Empty<string>();

        return Directory.GetFiles(AuditLogDirectory, "*.jsonl")
            .Select(f => new FileInfo(f))
            .OrderBy(f => f.CreationTimeUtc)
            .TakeLast(count)
            .Select(f => Path.GetFileNameWithoutExtension(f.Name))
            .ToList();
    }

    private string GetSessionFilePath(string sessionId)
    {
        // Sanitize session ID for safe file name
        var safeName = string.Concat(sessionId.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        return Path.Combine(AuditLogDirectory, $"{safeName}.jsonl");
    }

    /// <summary>
    /// Custom converter for TimeSpan since System.Text.Json doesn't handle it natively in all cases.
    /// </summary>
    private sealed class TimeSpanConverter : JsonConverter<TimeSpan?>
    {
        public override TimeSpan? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Null) return null;
            if (reader.TokenType == JsonTokenType.String)
            {
                var s = reader.GetString();
                return s is not null ? TimeSpan.Parse(s) : null;
            }
            if (reader.TokenType == JsonTokenType.Number)
                return TimeSpan.FromMilliseconds(reader.GetDouble());
            return null;
        }

        public override void Write(Utf8JsonWriter writer, TimeSpan? value, JsonSerializerOptions options)
        {
            if (value is null)
                writer.WriteNullValue();
            else
                writer.WriteStringValue(value.Value.ToString());
        }
    }
}
