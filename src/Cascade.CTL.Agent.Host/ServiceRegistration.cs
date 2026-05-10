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
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenAI;
using OpenTelemetry.Trace;

namespace Cascade.CTL.Agent.Host;

public static class ServiceRegistration
{
    public static IHostBuilder ConfigureCTLAgent(this IHostBuilder builder)
    {
        builder.ConfigureAppConfiguration((context, config) =>
        {
            // Resolve solution root: walk up from current dir until we find the config/ folder
            var baseDir = Directory.GetCurrentDirectory();
            var solutionRoot = FindSolutionRoot(baseDir) ?? baseDir;

            config.SetBasePath(solutionRoot);
            config.AddJsonFile("appsettings.json", optional: true);
            config.AddJsonFile(Path.Combine("config", "appsettings.json"), optional: true);
            config.AddJsonFile("appsettings.Development.json", optional: true);
            config.AddJsonFile(Path.Combine("config", "appsettings.Development.json"), optional: true);
            config.AddEnvironmentVariables(prefix: "CTL_");
        });

        builder.ConfigureServices((context, services) =>
        {
            var config = context.Configuration;

            // Bind configuration sections
            services.Configure<CTLAgentOptions>(config.GetSection("CTLAgent"));
            services.Configure<ContentSafetyOptions>(config.GetSection("ContentSafety"));
            services.Configure<PiiFilterOptions>(config.GetSection("PiiFilter"));
            services.Configure<TokenBudgetOptions>(config.GetSection("TokenBudget"));
            services.Configure<ResilienceOptions>(config.GetSection(ResilienceOptions.SectionName));
            services.Configure<VerdictPolicyOptions>(config.GetSection(VerdictPolicyOptions.SectionName));
            services.Configure<ReflectionDeterminismOptions>(config.GetSection(ReflectionDeterminismOptions.SectionName));

            // Infrastructure (mock providers + RAG + audit + telemetry)
            var useMock = config.GetValue("CTLAgent:Providers:UseMockProviders", true);
            var ragPath = Path.Combine(AppContext.BaseDirectory, "rag-knowledge");
            services.AddCTLInfrastructure(useMockProviders: useMock, configuration: config, ragKnowledgePath: ragPath);

            // Override mock HITL with interactive CLI service for live demos
            services.AddSingleton<IHumanReviewService, InteractiveHumanReviewService>();

            // Guardrails
            services.AddCTLGuardrails();

            // Azure AI Foundry IChatClient with guardrails middleware
            services.AddSingleton<IChatClient>(sp =>
            {
                var options = sp.GetRequiredService<IOptions<CTLAgentOptions>>().Value;
                var logger = sp.GetRequiredService<ILogger<GuardrailsMiddleware>>();
                var contentSafety = sp.GetRequiredService<ContentSafetyGuard>();
                var tokenBudget = sp.GetRequiredService<TokenBudgetGuard>();
                var piiFilter = sp.GetRequiredService<PiiFilter>();
                var auditService = sp.GetRequiredService<IAuditService>();

                IChatClient innerClient;

                if (!string.IsNullOrEmpty(options.AzureAIFoundry.Endpoint))
                {
                    var endpoint = new Uri(options.AzureAIFoundry.Endpoint);
                    var isAzureOpenAI = options.AzureAIFoundry.Endpoint.Contains(".openai.azure.com", StringComparison.OrdinalIgnoreCase)
                                     || options.AzureAIFoundry.Endpoint.Contains(".cognitiveservices.azure.com", StringComparison.OrdinalIgnoreCase);

                    if (isAzureOpenAI)
                    {
                        // Azure OpenAI (Cognitive Services) endpoint — use AzureOpenAIClient
                        AzureOpenAIClient azureClient;
                        if (options.AzureAIFoundry.UseAzureIdentity)
                        {
                            azureClient = new AzureOpenAIClient(endpoint, new DefaultAzureCredential());
                        }
                        else if (!string.IsNullOrEmpty(options.AzureAIFoundry.ApiKey))
                        {
                            azureClient = new AzureOpenAIClient(endpoint, new ApiKeyCredential(options.AzureAIFoundry.ApiKey));
                        }
                        else
                        {
                            throw new InvalidOperationException(
                                "Azure OpenAI requires either UseAzureIdentity=true or an ApiKey");
                        }

                        innerClient = azureClient
                            .GetChatClient(options.AzureAIFoundry.ModelId)
                            .AsIChatClient();
                    }
                    else
                    {
                        // Azure AI Model Inference (serverless) — use OpenAIClient with custom endpoint
                        var clientOptions = new OpenAIClientOptions
                        {
                            Endpoint = endpoint
                        };

                        ApiKeyCredential credential;

                        if (options.AzureAIFoundry.UseAzureIdentity)
                        {
                            var azureCredential = new DefaultAzureCredential();
                            var tokenResult = azureCredential.GetToken(
                                new Azure.Core.TokenRequestContext(["https://cognitiveservices.azure.com/.default"]));
                            credential = new ApiKeyCredential(tokenResult.Token);
                        }
                        else if (!string.IsNullOrEmpty(options.AzureAIFoundry.ApiKey))
                        {
                            credential = new ApiKeyCredential(options.AzureAIFoundry.ApiKey);
                        }
                        else
                        {
                            throw new InvalidOperationException(
                                "Azure AI Foundry requires either UseAzureIdentity=true or an ApiKey");
                        }

                        var openAIClient = new OpenAIClient(credential, clientOptions);
                        innerClient = openAIClient
                            .GetChatClient(options.AzureAIFoundry.ModelId)
                            .AsIChatClient();
                    }
                }
                else
                {
                    throw new InvalidOperationException(
                        "Azure AI Foundry Endpoint is required. Set CTLAgent:AzureAIFoundry:Endpoint in appsettings.json");
                }

                // Build middleware pipeline: Guardrails → FunctionInvocation → OpenTelemetry
                var chatPipeline = new ChatClientBuilder(innerClient)
                    .UseOpenTelemetry(
                        sourceName: Infrastructure.Observability.TelemetryConfiguration.ServiceName,
                        configure: c => c.EnableSensitiveData = true)
                    .UseFunctionInvocation()
                    .Build();

                // Wrap with guardrails
                return new GuardrailsMiddleware(chatPipeline, contentSafety, tokenBudget, piiFilter, auditService, logger);
            });

            // MCP Tool Provider — connects to the single MCP server endpoint.
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

            // CTL Evaluation Orchestrator
            services.AddSingleton<VerdictGroundednessEvaluator>(sp =>
            {
                var options = sp.GetRequiredService<IOptions<CTLAgentOptions>>().Value;
                var logger = sp.GetRequiredService<ILogger<VerdictGroundednessEvaluator>>();

                // Build a separate IChatClient for the judge model to avoid self-bias.
                // The judge uses an independent deployment (gpt-4o-judge) that evaluates
                // verdicts produced by the agent's primary model (gpt-4o).
                var judgeConfig = options.JudgeModel;
                IChatClient judgeClient;

                if (!string.IsNullOrEmpty(judgeConfig.Endpoint))
                {
                    var endpoint = new Uri(judgeConfig.Endpoint);
                    AzureOpenAIClient azureClient;

                    if (judgeConfig.UseAzureIdentity)
                    {
                        azureClient = new AzureOpenAIClient(endpoint, new DefaultAzureCredential());
                    }
                    else if (!string.IsNullOrEmpty(judgeConfig.ApiKey))
                    {
                        azureClient = new AzureOpenAIClient(endpoint, new ApiKeyCredential(judgeConfig.ApiKey));
                    }
                    else
                    {
                        throw new InvalidOperationException(
                            "JudgeModel requires either UseAzureIdentity=true or an ApiKey");
                    }

                    judgeClient = azureClient
                        .GetChatClient(judgeConfig.ModelId)
                        .AsIChatClient();
                }
                else
                {
                    // Fallback: if JudgeModel not configured, use the primary model (same as before)
                    logger.LogWarning("JudgeModel not configured — falling back to primary model. " +
                        "Configure CTLAgent:JudgeModel in appsettings.json for self-bias mitigation.");
                    judgeClient = sp.GetRequiredService<IChatClient>();
                }

                return new VerdictGroundednessEvaluator(judgeClient, logger);
            });
            services.AddSingleton<CTLWorkflowOrchestrator>();
            services.AddSingleton<ICTLEvaluationOrchestrator>(sp =>
                sp.GetRequiredService<CTLWorkflowOrchestrator>());
        });

        return builder;
    }

    private static string? FindSolutionRoot(string startDir)
    {
        var dir = new DirectoryInfo(startDir);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "config")))
                return dir.FullName;
            dir = dir.Parent;
        }
        return null;
    }
}
