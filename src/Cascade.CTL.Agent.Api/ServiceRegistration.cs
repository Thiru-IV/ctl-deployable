using System.ClientModel;
using Azure.AI.OpenAI;
using Azure.Identity;
using Cascade.CTL.Agent.Application.Configuration;
using Cascade.CTL.Agent.Application.Orchestration;
using Cascade.CTL.Agent.Application.Orchestration.Workflow;
using Cascade.CTL.Agent.Application.Resilience;
using Cascade.CTL.Agent.Domain.Contracts;
using Cascade.CTL.Agent.Domain.Models;
using Cascade.CTL.Agent.Guardrails;
using Cascade.CTL.Agent.Infrastructure;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenAI;

namespace Cascade.CTL.Agent.Api;

/// <summary>
/// DI wiring for the Agent.Api HTTP host. Mirrors Host/ServiceRegistration.cs
/// but: (a) uses <see cref="AutoApproveHumanReviewService"/> instead of the
/// console-based reviewer, (b) loads config from env vars + appsettings.json
/// in the application content root rather than walking up to find a solution
/// folder, since the container has no parent solution.
/// NOTE: keep this in sync with Host/ServiceRegistration.cs until both are
/// merged into a shared composition module (tracked as deferred work).
/// </summary>
public static class ServiceRegistration
{
    public static WebApplicationBuilder ConfigureCTLAgentApi(this WebApplicationBuilder builder)
    {
        // ── Configuration ─────────────────────────────────────────────────────
        // Container layout: appsettings.json sits next to the binaries (content root).
        // Env vars use double-underscore for nesting per .NET conventions, e.g.
        //   CTLAgent__AzureAIFoundry__ApiKey
        //   ApplicationInsights__ConnectionString
        builder.Configuration
            .SetBasePath(builder.Environment.ContentRootPath)
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
            .AddJsonFile($"appsettings.{builder.Environment.EnvironmentName}.json", optional: true, reloadOnChange: false)
            .AddEnvironmentVariables();

        var services = builder.Services;
        var config = builder.Configuration;

        // ── Options binding ──────────────────────────────────────────────────
        services.Configure<CTLAgentOptions>(config.GetSection("CTLAgent"));
        services.Configure<ContentSafetyOptions>(config.GetSection("ContentSafety"));
        services.Configure<PiiFilterOptions>(config.GetSection("PiiFilter"));
        services.Configure<TokenBudgetOptions>(config.GetSection("TokenBudget"));
        services.Configure<ResilienceOptions>(config.GetSection(ResilienceOptions.SectionName));
        services.Configure<VerdictPolicyOptions>(config.GetSection(VerdictPolicyOptions.SectionName));
        services.Configure<ReflectionDeterminismOptions>(config.GetSection(ReflectionDeterminismOptions.SectionName));

        // ── Infrastructure (mock providers + RAG + audit + telemetry) ────────
        var useMock = config.GetValue("CTLAgent:Providers:UseMockProviders", true);
        var ragPath = Path.Combine(AppContext.BaseDirectory, "rag-knowledge");
        services.AddCTLInfrastructure(useMockProviders: useMock, configuration: config, ragKnowledgePath: ragPath);

        // ── Service-mode HITL: no console; returns NeedsHumanReview to caller ─
        services.AddSingleton<IHumanReviewService, AutoApproveHumanReviewService>();

        // ── Guardrails ───────────────────────────────────────────────────────
        services.AddCTLGuardrails();

        // ── Primary IChatClient with guardrails middleware ───────────────────
        services.AddSingleton<IChatClient>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<CTLAgentOptions>>().Value;
            var logger = sp.GetRequiredService<ILogger<GuardrailsMiddleware>>();
            var contentSafety = sp.GetRequiredService<ContentSafetyGuard>();
            var tokenBudget = sp.GetRequiredService<TokenBudgetGuard>();
            var piiFilter = sp.GetRequiredService<PiiFilter>();
            var auditService = sp.GetRequiredService<IAuditService>();

            if (string.IsNullOrEmpty(options.AzureAIFoundry.Endpoint))
            {
                throw new InvalidOperationException(
                    "CTLAgent:AzureAIFoundry:Endpoint is required.");
            }

            var endpoint = new Uri(options.AzureAIFoundry.Endpoint);
            var isAzureOpenAI = options.AzureAIFoundry.Endpoint.Contains(".openai.azure.com", StringComparison.OrdinalIgnoreCase)
                             || options.AzureAIFoundry.Endpoint.Contains(".cognitiveservices.azure.com", StringComparison.OrdinalIgnoreCase);

            IChatClient innerClient;
            if (isAzureOpenAI)
            {
                AzureOpenAIClient azureClient = options.AzureAIFoundry.UseAzureIdentity
                    ? new AzureOpenAIClient(endpoint, new DefaultAzureCredential())
                    : !string.IsNullOrEmpty(options.AzureAIFoundry.ApiKey)
                        ? new AzureOpenAIClient(endpoint, new ApiKeyCredential(options.AzureAIFoundry.ApiKey))
                        : throw new InvalidOperationException("Azure OpenAI requires UseAzureIdentity=true or ApiKey.");

                innerClient = azureClient.GetChatClient(options.AzureAIFoundry.ModelId).AsIChatClient();
            }
            else
            {
                ApiKeyCredential credential;
                if (options.AzureAIFoundry.UseAzureIdentity)
                {
                    var azureCredential = new DefaultAzureCredential();
                    var token = azureCredential.GetToken(
                        new Azure.Core.TokenRequestContext(["https://cognitiveservices.azure.com/.default"]));
                    credential = new ApiKeyCredential(token.Token);
                }
                else if (!string.IsNullOrEmpty(options.AzureAIFoundry.ApiKey))
                {
                    credential = new ApiKeyCredential(options.AzureAIFoundry.ApiKey);
                }
                else
                {
                    throw new InvalidOperationException("Azure AI Foundry requires UseAzureIdentity=true or ApiKey.");
                }

                var openAIClient = new OpenAIClient(credential, new OpenAIClientOptions { Endpoint = endpoint });
                innerClient = openAIClient.GetChatClient(options.AzureAIFoundry.ModelId).AsIChatClient();
            }

            var chatPipeline = new ChatClientBuilder(innerClient)
                .UseOpenTelemetry(
                    sourceName: Infrastructure.Observability.TelemetryConfiguration.ServiceName,
                    configure: c => c.EnableSensitiveData = true)
                .UseFunctionInvocation()
                .Build();

            return new GuardrailsMiddleware(chatPipeline, contentSafety, tokenBudget, piiFilter, auditService, logger);
        });

        // ── MCP Tool Provider ────────────────────────────────────────────────
        services.AddSingleton<IMcpToolProvider>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<CTLAgentOptions>>().Value;
            var resilienceOpts = sp.GetRequiredService<IOptions<ResilienceOptions>>().Value;
            var logger = sp.GetRequiredService<ILogger<McpToolProvider>>();

            var serverEndpoints = new Dictionary<string, string>
            {
                ["Default"] = options.McpServer.Endpoint
            };
            return new McpToolProvider(logger, serverEndpoints, resilienceOpts, options.McpServer.ApiKey);
        });

        // ── Quality Gate / Judge ─────────────────────────────────────────────
        services.AddSingleton<VerdictGroundednessEvaluator>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<CTLAgentOptions>>().Value;
            var logger = sp.GetRequiredService<ILogger<VerdictGroundednessEvaluator>>();

            IChatClient judgeClient;
            var judgeConfig = options.JudgeModel;
            if (!string.IsNullOrEmpty(judgeConfig.Endpoint))
            {
                var endpoint = new Uri(judgeConfig.Endpoint);
                AzureOpenAIClient azureClient = judgeConfig.UseAzureIdentity
                    ? new AzureOpenAIClient(endpoint, new DefaultAzureCredential())
                    : !string.IsNullOrEmpty(judgeConfig.ApiKey)
                        ? new AzureOpenAIClient(endpoint, new ApiKeyCredential(judgeConfig.ApiKey))
                        : throw new InvalidOperationException("JudgeModel requires UseAzureIdentity=true or ApiKey.");
                judgeClient = azureClient.GetChatClient(judgeConfig.ModelId).AsIChatClient();
            }
            else
            {
                logger.LogWarning("JudgeModel not configured — falling back to primary model.");
                judgeClient = sp.GetRequiredService<IChatClient>();
            }

            return new VerdictGroundednessEvaluator(judgeClient, logger);
        });

        // ── Orchestrator ─────────────────────────────────────────────────────
        services.AddSingleton<CTLWorkflowOrchestrator>();
        services.AddSingleton<ICTLEvaluationOrchestrator>(sp => sp.GetRequiredService<CTLWorkflowOrchestrator>());

        return builder;
    }
}
