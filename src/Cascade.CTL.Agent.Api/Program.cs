using System.Text.Json;
using System.Text.Json.Serialization;
using Cascade.CTL.Agent.Api;
using Cascade.CTL.Agent.Application.Orchestration;
using Cascade.CTL.Agent.Domain.Contracts;
using Cascade.CTL.Agent.Domain.Models;
using Microsoft.ApplicationInsights;
using Microsoft.AspNetCore.Mvc;

var builder = WebApplication.CreateBuilder(args);
builder.ConfigureCTLAgentApi();

builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.WriteIndented = false;
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});

var app = builder.Build();
var logger = app.Logger;

// ── API key auth (matches McpServer pattern) ───────────────────────────────
// CTLAgent:Api:ApiKey must be set via env var CTLAgent__Api__ApiKey in ACA.
var expectedApiKey = app.Configuration["CTLAgent:Api:ApiKey"]
    ?? throw new InvalidOperationException(
        "CTLAgent:Api:ApiKey must be configured (env var CTLAgent__Api__ApiKey).");

app.Use(async (ctx, next) =>
{
    var path = ctx.Request.Path.Value ?? string.Empty;
    // Health + OpenAPI spec are unauthenticated so probes / Foundry ingestion work.
    // /api/messages uses Bot Framework JWT (channel-issued) — its own auth.
    if (path.StartsWith("/health", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith("/openapi", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith("/api/messages", StringComparison.OrdinalIgnoreCase) ||
        path == "/")
    {
        await next();
        return;
    }

    var apiKey = ctx.Request.Headers["X-Api-Key"].ToString();
    if (!string.Equals(apiKey, expectedApiKey, StringComparison.Ordinal))
    {
        ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await ctx.Response.WriteAsync("Invalid or missing X-Api-Key header.");
        return;
    }
    await next();
});

// ── Lazy MCP initialization (first /evaluate call wires the tool provider) ─
var mcpInitLock = new SemaphoreSlim(1, 1);
var mcpInitialized = false;

async Task EnsureMcpInitializedAsync(IServiceProvider sp, CancellationToken ct)
{
    if (mcpInitialized) return;
    await mcpInitLock.WaitAsync(ct);
    try
    {
        if (mcpInitialized) return;
        var provider = sp.GetRequiredService<IMcpToolProvider>();
        using var initCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        initCts.CancelAfter(TimeSpan.FromMinutes(2));
        await provider.InitializeAsync(initCts.Token);
        mcpInitialized = true;
        logger.LogInformation("MCP Tool Provider initialized.");
    }
    finally
    {
        mcpInitLock.Release();
    }
}

// ── Routes ─────────────────────────────────────────────────────────────────

app.MapGet("/", () => Results.Ok(new
{
    service = "Cascade.CTL.Agent.Api",
    version = "1.0",
    endpoints = new[] { "/health", "/openapi.json", "/evaluate (POST, X-Api-Key)" }
}));

app.MapGet("/health", () => Results.Ok(new
{
    status = "healthy",
    mcpInitialized,
    timestamp = DateTimeOffset.UtcNow
}));

app.MapGet("/openapi.json", async (IWebHostEnvironment env) =>
{
    var path = Path.Combine(env.ContentRootPath, "openapi.json");
    if (!File.Exists(path))
    {
        path = Path.Combine(AppContext.BaseDirectory, "openapi.json");
    }
    if (!File.Exists(path))
    {
        return Results.NotFound("openapi.json missing from deployment.");
    }
    var json = await File.ReadAllTextAsync(path);
    return Results.Content(json, "application/json");
});

app.MapPost("/evaluate", async (
    [FromBody] EvaluateRequest req,
    HttpContext ctx,
    ICTLEvaluationOrchestrator orchestrator,
    CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(req.AssetId))
    {
        return Results.BadRequest(new { error = "assetId is required" });
    }

    await EnsureMcpInitializedAsync(ctx.RequestServices, ct);

    var evalRequest = new CTLEvaluationRequest
    {
        AssetId = req.AssetId.Trim(),
        WorkflowInstanceId = $"WF-{Guid.NewGuid():N}"[..16],
        RequestTimestamp = DateTime.UtcNow,
        RequestedBy = string.IsNullOrWhiteSpace(req.RequestedBy)
            ? "Cascade.CTL.Agent.Api"
            : req.RequestedBy!.Trim()
    };

    logger.LogInformation("HTTP /evaluate — Asset={AssetId} RequestedBy={RequestedBy}",
        evalRequest.AssetId, evalRequest.RequestedBy);

    try
    {
        var result = await orchestrator.EvaluateAsync(evalRequest);
        return Results.Ok(new
        {
            sessionId = result.Verdict.SessionId,
            assetId = result.Verdict.AssetId,
            verdict = result.Verdict.Verdict.ToString(),
            confidence = result.Verdict.ConfidenceScore,
            conditions = result.Verdict.Conditions,
            evidenceTrail = result.Verdict.EvidenceTrail,
            reflectionLog = result.Verdict.ReflectionLog,
            durationSeconds = result.EvaluationDuration.TotalSeconds,
            tokensUsed = result.TotalTokensUsed,
            isDegradedSafety = result.IsDegradedSafety
        });
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Evaluation failed for asset {AssetId}", evalRequest.AssetId);
        FlushTelemetry(ctx.RequestServices);
        return Results.Problem(
            title: "Evaluation failed",
            detail: ex.Message,
            statusCode: StatusCodes.Status500InternalServerError);
    }
});

// Drain App Insights buffer on graceful shutdown.
app.Lifetime.ApplicationStopping.Register(() => FlushTelemetry(app.Services));

// Map Bot Framework messaging endpoint (/api/messages) when Teams HITL is enabled.
if (app.Configuration.GetValue("CTLAgent:Teams:Enabled", false))
{
    app.MapControllers();
    logger.LogInformation("Teams HITL notifications enabled. Bot endpoint: POST /api/messages");
}

app.Run();

static void FlushTelemetry(IServiceProvider services)
{
    var telemetry = services.GetService<TelemetryClient>();
    if (telemetry is null) return;
    telemetry.Flush();
    Thread.Sleep(TimeSpan.FromSeconds(3));
}

internal sealed record EvaluateRequest(string AssetId, string? RequestedBy);
