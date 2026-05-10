using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cascade.CTL.Agent.Application.Orchestration;
using Cascade.CTL.Agent.Domain.Contracts;
using Cascade.CTL.Agent.Domain.Enums;
using Cascade.CTL.Agent.Domain.Models;
using Cascade.CTL.Agent.Host;
using Cascade.CTL.Agent.Infrastructure.Observability;
using Microsoft.ApplicationInsights;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = Microsoft.Extensions.Hosting.Host.CreateDefaultBuilder(args);
builder.ConfigureCTLAgent();

using var host = builder.Build();

var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("CTLAgent.Host");
var jsonOptions = new JsonSerializerOptions
{
    WriteIndented = true,
    Converters = { new JsonStringEnumConverter() }
};

// ── CLI: --audit-history — list past evaluation sessions ──
if (args.Contains("--audit-history"))
{
    var fileStore = host.Services.GetRequiredService<AuditFileStore>();
    var sessions = fileStore.GetPersistedSessionIds(50);

    if (sessions.Count == 0)
    {
        Console.WriteLine("No audit logs found. Run an evaluation first.");
        return 0;
    }

    Console.ForegroundColor = ConsoleColor.Cyan;
    Console.WriteLine($"  AUDIT HISTORY — {sessions.Count} session(s) found");
    Console.WriteLine("  ─────────────────────────────────────────────────────");
    Console.ResetColor();

    foreach (var sid in sessions)
    {
        var entries = fileStore.ReadSession(sid);
        var firstEntry = entries.FirstOrDefault();
        var lastEntry = entries.LastOrDefault();
        var entryAssetId = firstEntry?.AssetId ?? "?";
        var startTime = firstEntry?.Timestamp.ToString("yyyy-MM-dd HH:mm:ss") ?? "?";
        var verdict = entries.LastOrDefault(e => e.StepType == "EvaluationCompleted")?.Description ?? "—";

        Console.WriteLine($"  {sid}  |  {startTime} UTC  |  Asset: {entryAssetId}  |  {entries.Count} steps");
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine($"    └─ {verdict}");
        Console.ResetColor();
    }

    Console.WriteLine();
    Console.ForegroundColor = ConsoleColor.DarkGray;
    Console.WriteLine("  Use --audit-view <session-id> to see full audit trail");
    Console.ResetColor();
    return 0;
}

// ── CLI: --audit-view <sessionId> — display a specific session's audit trail ──
var auditViewIndex = Array.IndexOf(args, "--audit-view");
if (auditViewIndex >= 0)
{
    if (auditViewIndex + 1 >= args.Length)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine("  ERROR: --audit-view requires a session ID");
        Console.ResetColor();
        Console.WriteLine("  Usage: --audit-view <session-id>");
        Console.WriteLine("  Run --audit-history to see available sessions");
        return 1;
    }

    var targetSessionId = args[auditViewIndex + 1];
    var fileStore = host.Services.GetRequiredService<AuditFileStore>();
    var auditTrail = fileStore.ReadSession(targetSessionId);

    if (auditTrail.Count == 0)
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"  No audit trail found for session: {targetSessionId}");
        Console.ResetColor();
        Console.WriteLine("  Run --audit-history to see available sessions");
        return 1;
    }

    PrintFullSessionReplay(auditTrail, targetSessionId, jsonOptions);
    return 0;
}

// ── CLI: --push-audit-logs [sessionId] — backfill local audit JSONL files into App Insights ──
// Replays every entry in the local audit-logs/ directory (or one session) as CTL.AuditStep events.
// Useful when the in-process telemetry buffer dropped tail events on prior runs, or when the
// App Insights connection was added later and earlier sessions never made it to Azure.
if (args.Contains("--push-audit-logs"))
{
    var pushIdx = Array.IndexOf(args, "--push-audit-logs");
    var onlySessionId = (pushIdx + 1 < args.Length && !args[pushIdx + 1].StartsWith("--")) ? args[pushIdx + 1] : null;

    var telemetry = host.Services.GetService<TelemetryClient>();
    if (telemetry is null)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine("  ERROR: Application Insights is not configured. Set ApplicationInsights:ConnectionString.");
        Console.ResetColor();
        return 1;
    }

    var fileStore = host.Services.GetRequiredService<AuditFileStore>();
    var sessionIds = onlySessionId is not null
        ? new List<string> { onlySessionId }
        : fileStore.GetPersistedSessionIds(int.MaxValue).ToList();

    if (sessionIds.Count == 0)
    {
        Console.WriteLine("  No local audit logs found.");
        return 0;
    }

    Console.ForegroundColor = ConsoleColor.Cyan;
    Console.WriteLine($"  Pushing {sessionIds.Count} session(s) to Application Insights...");
    Console.ResetColor();

    var totalEntries = 0;
    foreach (var sid in sessionIds)
    {
        var entries = fileStore.ReadSession(sid);
        foreach (var entry in entries)
        {
            var evt = new Microsoft.ApplicationInsights.DataContracts.EventTelemetry("CTL.AuditStep")
            {
                Timestamp = entry.Timestamp
            };
            evt.Properties["SessionId"] = entry.SessionId;
            evt.Properties["AssetId"] = entry.AssetId;
            evt.Properties["AgentName"] = entry.AgentName;
            evt.Properties["StepType"] = entry.StepType;
            evt.Properties["Description"] = entry.Description;
            evt.Properties["BackfilledFromFile"] = "true";
            if (entry.CorrelationId is not null) evt.Properties["CorrelationId"] = entry.CorrelationId;
            if (entry.InputHash is not null) evt.Properties["InputHash"] = entry.InputHash;
            if (entry.OutputHash is not null) evt.Properties["OutputHash"] = entry.OutputHash;
            if (entry.OutputPayload is not null) evt.Properties["OutputPayload"] = entry.OutputPayload;
            if (entry.TokensUsed.HasValue) evt.Metrics["TokensUsed"] = entry.TokensUsed.Value;
            if (entry.Duration.HasValue) evt.Metrics["DurationMs"] = entry.Duration.Value.TotalMilliseconds;

            telemetry.TrackEvent(evt);
            totalEntries++;
        }
        Console.WriteLine($"    {sid}: {entries.Count} entries queued");
    }

    Console.WriteLine();
    Console.WriteLine($"  Flushing {totalEntries} entries to App Insights...");
    telemetry.Flush();
    // App Insights flush is fire-and-forget; give the channel time to drain over the network.
    await Task.Delay(TimeSpan.FromSeconds(10));

    Console.ForegroundColor = ConsoleColor.Green;
    Console.WriteLine($"  Done. {totalEntries} audit entries pushed across {sessionIds.Count} session(s).");
    Console.WriteLine($"  Note: ingestion latency in App Insights is typically 1–5 minutes.");
    Console.ResetColor();
    return 0;
}

logger.LogInformation("╔══════════════════════════════════════════════════════════════════╗");
logger.LogInformation("║  Cascade 2.0 — CTL Agent Host                                    ║");
logger.LogInformation("║  Microsoft Agent Framework SDK · MCP · RAG . Evals . Azure OpenAI║");
logger.LogInformation("╚══════════════════════════════════════════════════════════════════╝");

// Parse command line
var assetId = "ASSET-TX-001"; // default
for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--asset-id" && i + 1 < args.Length)
    {
        assetId = args[i + 1];
        break;
    }
}

try
{
    // Tool Discovery: Initialize MCP Tool Provider (with built-in retry + timeout)
    var toolProvider = host.Services.GetRequiredService<IMcpToolProvider>();
    logger.LogInformation("Initializing MCP Tool Provider...");

    using var initCts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
    await toolProvider.InitializeAsync(initCts.Token);

    logger.LogInformation("MCP Tool Provider initialized successfully.");

    // Run CTL evaluation
    // When true, uses the Microsoft Agent Framework Workflows-based CTLWorkflowOrchestrator otherwise uses the imperative CTLEvaluationOrchestrator. Can be toggled at runtime via config or environment variable.
    var orchestrator = host.Services.GetRequiredService<ICTLEvaluationOrchestrator>();

    var request = new CTLEvaluationRequest
    {
        AssetId = assetId,
        WorkflowInstanceId = $"WF-{Guid.NewGuid():N}"[..16],
        RequestTimestamp = DateTime.UtcNow,
        RequestedBy = "CTLAgent.Host.CLI"
    };

    logger.LogInformation("Starting CTL evaluation for asset: {AssetId}", assetId);

    var result = await orchestrator.EvaluateAsync(request);

    // ── Session ID banner — immediately after evaluation completes ──
    var sessionId = result.Verdict.SessionId;
    Console.ForegroundColor = ConsoleColor.Cyan;
    Console.WriteLine();
    Console.WriteLine($"  SESSION ID: {sessionId}");
    Console.ResetColor();

    // Output results
    Console.WriteLine();
    Console.WriteLine(" ********CTL EVALUATION RESULT******** ");
    Console.WriteLine(JsonSerializer.Serialize(result, jsonOptions));
    Console.WriteLine();

    var verdictColor = result.Verdict.Verdict switch
    {
        CTLVerdict.Clear => ConsoleColor.Green,
        CTLVerdict.ClearWithConditions => ConsoleColor.Yellow,
        CTLVerdict.NotClear => ConsoleColor.Red,
        CTLVerdict.NeedsHumanReview => ConsoleColor.Magenta,
        _ => ConsoleColor.White
    };

    Console.ForegroundColor = verdictColor;
    Console.WriteLine($"  VERDICT: {result.Verdict.Verdict}");
    Console.WriteLine($"  CONFIDENCE: {result.Verdict.ConfidenceScore:F2}");
    Console.WriteLine($"  DURATION: {result.EvaluationDuration.TotalSeconds:F1}s");
    Console.WriteLine($"  TOKENS USED: {result.TotalTokensUsed}");
    if (result.IsDegradedSafety)
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("  ⚠ SAFETY MODE: DEGRADED (Azure ML screening unavailable — local regex only)");
    }
    Console.ResetColor();
    Console.WriteLine();

    if (result.Verdict.Conditions.Length > 0)
    {
        Console.WriteLine("  CONDITIONS:");
        foreach (var condition in result.Verdict.Conditions)
            Console.WriteLine($"    • {condition}");
        Console.WriteLine();
    }

    if (result.Verdict.EvidenceTrail.Length > 0)
    {
        Console.WriteLine("  EVIDENCE TRAIL:");
        foreach (var evidence in result.Verdict.EvidenceTrail)
            Console.WriteLine($"    ✓ {evidence}");
        Console.WriteLine();
    }

    Console.WriteLine("  REFLECTION LOG:");
    Console.WriteLine($"    {result.Verdict.ReflectionLog}");
    Console.WriteLine();

    // ── Audit Trail: Full transparency dump ──
    var auditService = host.Services.GetRequiredService<IAuditService>();
    var recentSessions = await auditService.GetRecentSessionIdsAsync(10);
    if (recentSessions.Count > 0)
    {
        var latestSession = recentSessions[^1];
        var auditTrail = await auditService.GetSessionAuditTrailAsync(latestSession);
        PrintAuditTrail(auditTrail, latestSession);

        // Point user to the persisted file + replay command
        var fileStore = host.Services.GetRequiredService<AuditFileStore>();
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine($"  Audit log persisted: {Path.Combine(fileStore.AuditLogDirectory, latestSession + ".jsonl")}");
        Console.ResetColor();
        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine($"  ┌─────────────────────────────────────────────────────────┐");
        Console.WriteLine($"  │  SESSION ID: {sessionId,-43}│");
        Console.WriteLine($"  │  Replay:  dotnet run -- --audit-view {sessionId}  │");
        Console.WriteLine($"  └─────────────────────────────────────────────────────────┘");
        Console.ResetColor();
        Console.WriteLine();
    }
}
catch (Exception ex)
{
    logger.LogError(ex, "CTL Agent Host encountered a fatal error");
    Console.ForegroundColor = ConsoleColor.Red;
    Console.WriteLine($"ERROR: {ex.Message}");
    Console.ResetColor();

    // ── Recover session ID from audit trail even on failure ──
    try
    {
        var auditService = host.Services.GetRequiredService<IAuditService>();
        var recentSessions = await auditService.GetRecentSessionIdsAsync(1);
        if (recentSessions.Count > 0)
        {
            var failedSession = recentSessions[^1];
            var partialTrail = await auditService.GetSessionAuditTrailAsync(failedSession);

            if (partialTrail.Count > 0)
            {
                Console.WriteLine();
                PrintAuditTrail(partialTrail, failedSession);

                var fileStore = host.Services.GetRequiredService<AuditFileStore>();
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine($"  Audit log persisted: {Path.Combine(fileStore.AuditLogDirectory, failedSession + ".jsonl")}");
                Console.ResetColor();
                Console.WriteLine();
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.WriteLine($"  ┌─────────────────────────────────────────────────────────┐");
                Console.WriteLine($"  │  SESSION ID: {failedSession,-43}│");
                Console.WriteLine($"  │  Replay:  dotnet run -- --audit-view {failedSession}  │");
                Console.WriteLine($"  └─────────────────────────────────────────────────────────┘");
                Console.ResetColor();
                Console.WriteLine();
            }
        }
    }
    catch
    {
        // Audit recovery is best-effort — don't mask the original error
    }

    if (ex.Message.Contains("Azure OpenAI") || ex.Message.Contains("Endpoint"))
    {
        Console.WriteLine();
        Console.WriteLine("SETUP REQUIRED:");
        Console.WriteLine("  1. Set Azure OpenAI endpoint in config/appsettings.Development.json");
        Console.WriteLine("     \"Endpoint\": \"https://YOUR-RESOURCE.openai.azure.com/\"");
        Console.WriteLine("  2. Set deployment name (default: gpt-4o)");
        Console.WriteLine("  3. Run: az login (for DefaultAzureCredential)");
        Console.WriteLine("  4. Or set ApiKey for key-based auth");
    }

    FlushTelemetry(host.Services);
    return 1;
}

FlushTelemetry(host.Services);
return 0;

// ── Drain the App Insights TelemetryClient buffer before the CLI exits.
// Without this, the last batch of CTL.AuditStep events queued during the run
// can be lost because the in-memory channel hasn't shipped them to Azure yet.
static void FlushTelemetry(IServiceProvider services)
{
    var telemetry = services.GetService<TelemetryClient>();
    if (telemetry is null) return;
    telemetry.Flush();
    // Flush is non-blocking; give the channel time to ship over the network.
    Thread.Sleep(TimeSpan.FromSeconds(5));
}

// ── Shared: Print audit trail to console with color coding ──
static void PrintAuditTrail(IReadOnlyList<AuditEntry> auditTrail, string sessionId)
{
    Console.ForegroundColor = ConsoleColor.Cyan;
    Console.WriteLine("═══════════════════════════════════════════════════════════════");
    Console.WriteLine($"  AUDIT TRAIL — Session: {sessionId}");
    Console.WriteLine("═══════════════════════════════════════════════════════════════");
    Console.ResetColor();

    PrintNarrativeEntries(auditTrail, truncatePayload: true);
    PrintNarrativeFooter(auditTrail);

    Console.ForegroundColor = ConsoleColor.Cyan;
    Console.WriteLine("═══════════════════════════════════════════════════════════════");
    Console.ResetColor();
}

// ── Full session replay for --audit-view: shows everything ──
static void PrintFullSessionReplay(IReadOnlyList<AuditEntry> auditTrail, string sessionId, JsonSerializerOptions jsonOptions)
{
    var assetId = auditTrail.FirstOrDefault()?.AssetId ?? "?";
    var started = auditTrail.FirstOrDefault()?.Timestamp;
    var completed = auditTrail.LastOrDefault()?.Timestamp;

    // ── Header ──
    Console.ForegroundColor = ConsoleColor.Cyan;
    Console.WriteLine("╔══════════════════════════════════════════════════════════════╗");
    Console.WriteLine($"║  SESSION REPLAY — {sessionId,-40} ║");
    Console.WriteLine($"║  Asset: {assetId,-50} ║");
    if (started.HasValue)
        Console.WriteLine($"║  Started: {started.Value:yyyy-MM-dd HH:mm:ss} UTC                              ║");
    Console.WriteLine("╚══════════════════════════════════════════════════════════════╝");
    Console.ResetColor();
    Console.WriteLine();

    // ── Walk each audit entry with FULL output, grouped into narrative phases ──
    PrintNarrativeEntries(auditTrail, truncatePayload: false);

    // ── Narrative Summary Section ──
    PrintNarrativeFooter(auditTrail);

    Console.WriteLine();
    Console.ForegroundColor = ConsoleColor.Cyan;
    Console.WriteLine("═══════════════════════════════════════════════════════════════");
    Console.ResetColor();
}

// ── Narrative phase groupings for story-telling display ──

static bool IsPhaseHeader(string stepType) => stepType is
    "PhaseStarted" or "EvaluationStarted" or "PlanGenerated" or "InvestigationFindings" or
    "ReflectionCompleted" or "VerdictParsed" or "QualityGateEvaluated" or
    "VerdictEscalated" or "HumanReviewCompleted" or "EvaluationCompleted" or
    "PhaseFailed" or "EvaluationFailed";

static bool IsGuardrailStep(string stepType) => stepType is
    "ContentSafetyBlocked" or "ContentSafetyFlagged" or "ContentSafetyPassed" or "SafetyDegraded" or
    "PiiMasked" or "PiiScreened" or "LlmCallCompleted" or "TokenBudgetExceeded" or "TokenBudgetChecked";

static string GetPhaseNarrative(string stepType) => stepType switch
{
    "EvaluationStarted"    => "THE EXECUTION BEGINS — Orchestrator (deterministic) initiated a new CTL evaluation",
    "PhaseStarted"         => "PHASE STARTING — A new processing phase is being initiated",
    "PlanGenerated"        => "PLANNING COMPLETE — LLM (non-deterministic) generated a verification plan based on the asset profile",
    "InvestigationFindings" => "INVESTIGATION — LLM sub-agent (non-deterministic) gathered evidence via MCP tool calls",
    "ReflectionCompleted"  => "REFLECTION — LLM (non-deterministic) reviewed all evidence and reasoned toward a verdict",
    "VerdictParsed"        => "VERDICT PARSING — Orchestrator (deterministic) applied rule-based validation to normalize the LLM verdict",
    "QualityGateEvaluated" => "QUALITY GATE — LLM-as-Judge (non-deterministic) scored the reflection for groundedness",
    "VerdictEscalated"     => "VERDICT ESCALATED — Orchestrator (deterministic) changed the verdict because Quality Gate failed",
    "HumanReviewCompleted" => "HUMAN REVIEW — Human reviewer (manual input) weighed in on the verdict",
    "EvaluationCompleted"  => "EXECUTION COMPLETE — Orchestrator (deterministic) finalized the Clear-To-List determination",
    "PhaseFailed"          => "PHASE FAILED — An error occurred during workflow execution",
    "EvaluationFailed"     => "EVALUATION FAILED — Workflow execution could not complete",
    _                      => stepType
};

/// <summary>
/// Walks audit entries and inserts narrative section headers when the
/// phase changes, so the output reads like a story for business users.
/// </summary>
static void PrintNarrativeEntries(IReadOnlyList<AuditEntry> auditTrail, bool truncatePayload)
{
    string? currentPhase = null;

    foreach (var entry in auditTrail)
    {
        // Insert a narrative section header when we enter a new major phase.
        // PhaseStarted entries always get a new header (since multiple phases share this StepType).
        // Other phase headers group by StepType to avoid duplicate headers.
        var phaseKey = entry.StepType == "PhaseStarted"
            ? $"PhaseStarted-{entry.AgentName}"
            : entry.StepType;

        if (IsPhaseHeader(entry.StepType) && phaseKey != currentPhase)
        {
            currentPhase = phaseKey;
            Console.WriteLine();
            Console.ForegroundColor = ConsoleColor.Cyan;
            if (entry.StepType == "PhaseStarted")
            {
                // For PhaseStarted, extract the phase name from Description for the header
                var phaseName = entry.AgentName.Replace("CTLOrchestrator-", "").Replace("CTLWorkflowOrchestrator-", "");
                Console.WriteLine($"  ── ▶ {phaseName.ToUpperInvariant()} PHASE STARTING ──");
            }
            else
            {
                Console.WriteLine($"  ── {GetPhaseNarrative(entry.StepType)} ──");
            }
            Console.ResetColor();
        }

        PrintAuditEntry(entry, truncatePayload);
    }
}

/// <summary>
/// Prints a narrative summary footer with key statistics — tool call count,
/// guardrail events, investigation agents, timing, and token usage.
/// </summary>
static void PrintNarrativeFooter(IReadOnlyList<AuditEntry> auditTrail)
{
    var toolCalls = auditTrail.Count(e => e.StepType == "ToolCallExecuted");
    var guardrailEvents = auditTrail.Count(e => IsGuardrailStep(e.StepType));
    var piiMasks = auditTrail.Count(e => e.StepType == "PiiMasked");
    var piiScreened = auditTrail.Count(e => e.StepType == "PiiScreened");
    var safetyBlocks = auditTrail.Count(e => e.StepType == "ContentSafetyBlocked");
    var safetyPassed = auditTrail.Count(e => e.StepType == "ContentSafetyPassed");
    var investigations = auditTrail.Where(e => e.StepType == "InvestigationFindings").ToList();
    var evalCompleted = auditTrail.LastOrDefault(e => e.StepType == "EvaluationCompleted");
    var humanReview = auditTrail.LastOrDefault(e => e.StepType == "HumanReviewCompleted");
    var degraded = auditTrail.Any(e => e.StepType == "SafetyDegraded");

    Console.WriteLine();
    Console.ForegroundColor = ConsoleColor.Cyan;
    Console.WriteLine("  ── NARRATIVE SUMMARY ─────────────────────────────────────");
    Console.ResetColor();

    Console.WriteLine($"    Total audit entries : {auditTrail.Count}");
    Console.WriteLine($"    Tool calls recorded : {toolCalls}");
    Console.WriteLine($"    Guardrail events    : {guardrailEvents}");
    Console.WriteLine($"      Content Safety    : {safetyPassed} passed, {safetyBlocks} blocked");
    Console.WriteLine($"      PII screening     : {piiScreened} screened (no PII), {piiMasks} masked (PII found)");
    Console.WriteLine($"    Investigation agents: {investigations.Count}");

    foreach (var inv in investigations)
    {
        Console.ForegroundColor = ConsoleColor.DarkYellow;
        Console.Write($"      • {inv.AgentName}");
        Console.ResetColor();
        Console.WriteLine($" — {inv.Description}");
    }

    if (evalCompleted != null)
    {
        if (evalCompleted.TokensUsed.HasValue)
            Console.WriteLine($"    Total tokens        : {evalCompleted.TokensUsed:N0}");
        if (evalCompleted.Duration.HasValue)
            Console.WriteLine($"    Total duration      : {evalCompleted.Duration.Value.TotalSeconds:F1}s");
        Console.WriteLine($"    Outcome             : {evalCompleted.Description}");
    }

    if (humanReview != null)
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"    Human review        : {humanReview.Description}");
        Console.ResetColor();
    }

    if (degraded)
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("    ⚠ SAFETY MODE       : DEGRADED (Azure Content Safety was unavailable during this session)");
        Console.ResetColor();
    }
}

// ── Shared: Print a single audit entry ──
static void PrintAuditEntry(AuditEntry entry, bool truncatePayload)
{
    Console.ForegroundColor = entry.StepType switch
    {
        "EvaluationStarted" => ConsoleColor.DarkCyan,
        "PhaseStarted" => ConsoleColor.Cyan,
        "PlanGenerated" => ConsoleColor.Blue,
        "ToolCallExecuted" => ConsoleColor.Gray,
        "InvestigationFindings" => ConsoleColor.DarkYellow,
        "ReflectionCompleted" => ConsoleColor.Magenta,
        "VerdictParsed" => ConsoleColor.DarkCyan,
        "DomainVerdictConflict" => ConsoleColor.Yellow,
        "QualityGateEvaluated" => ConsoleColor.DarkGreen,
        "VerdictEscalated" => ConsoleColor.Yellow,
        "HumanReviewCompleted" => ConsoleColor.Yellow,
        "EvaluationCompleted" => ConsoleColor.Green,
        "AgentExhaustedRetries" => ConsoleColor.Red,
        "PhaseFailed" => ConsoleColor.Red,
        "EvaluationFailed" => ConsoleColor.Red,
        "ContentSafetyBlocked" => ConsoleColor.Red,
        "ContentSafetyFlagged" => ConsoleColor.DarkYellow,
        "ContentSafetyPassed" => ConsoleColor.DarkGreen,
        "SafetyDegraded" => ConsoleColor.Yellow,
        "PiiMasked" => ConsoleColor.DarkMagenta,
        "PiiScreened" => ConsoleColor.DarkGreen,
        "LlmCallCompleted" => ConsoleColor.DarkYellow,
        "TokenBudgetExceeded" => ConsoleColor.Red,
        "TokenBudgetChecked" => ConsoleColor.DarkGreen,
        _ => ConsoleColor.Gray
    };

    // Storytelling: use narrative icons to help non-technical users follow the flow
    var icon = entry.StepType switch
    {
        "EvaluationStarted" => "📋",
        "PhaseStarted" => "▶️",
        "PlanGenerated" => "🗺️",
        "ToolCallExecuted" => "  🔧",
        "InvestigationFindings" => "🔍",
        "ReflectionCompleted" => "🤔",
        "VerdictParsed" => "🎯",
        "DomainVerdictConflict" => "⚠️",
        "QualityGateEvaluated" => "✅",
        "VerdictEscalated" => "⚠️",
        "HumanReviewCompleted" => "👤",
        "EvaluationCompleted" => "🏁",
        "AgentExhaustedRetries" => "❌",
        "PhaseFailed" => "❌",
        "EvaluationFailed" => "❌",
        "ContentSafetyBlocked" => "🛡️",
        "ContentSafetyFlagged" => "⚠️",
        "ContentSafetyPassed" => "  ✅",
        "SafetyDegraded" => "⚠️",
        "PiiMasked" => "🔒",
        "PiiScreened" => "  ✅",
        "LlmCallCompleted" => "🧠",
        "TokenBudgetExceeded" => "🚫",
        "TokenBudgetChecked" => "  ✅",
        _ => "  "
    };

    Console.WriteLine($"  [{entry.Timestamp:HH:mm:ss.fff}] {icon} {entry.StepType,-28} | {entry.AgentName}");
    Console.ResetColor();
    Console.WriteLine($"    {entry.Description}");

    if (entry.TokensUsed.HasValue)
        Console.WriteLine($"    Tokens: {entry.TokensUsed}");
    if (entry.Duration.HasValue)
        Console.WriteLine($"    Duration: {entry.Duration.Value.TotalMilliseconds:F0}ms");
    if (entry.OutputPayload is not null)
    {
        if (truncatePayload)
        {
            var payloadPreview = entry.OutputPayload.Length > 200
                ? entry.OutputPayload[..200] + "..."
                : entry.OutputPayload;
            Console.WriteLine($"    Payload: {payloadPreview}");
        }
        else
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine("    ┌─── Full Payload ───────────────────────────────────────");
            foreach (var line in entry.OutputPayload.Split('\n'))
                Console.WriteLine($"    │ {line}");
            Console.WriteLine("    └───────────────────────────────────────────────────────");
            Console.ResetColor();
        }
    }
    Console.WriteLine();
}
