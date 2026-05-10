using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Logging;
using Azure.Identity;
using Cascade.CTL.Agent.Domain.Contracts;
using Cascade.CTL.Agent.Infrastructure.Providers.Http;
using Cascade.CTL.Agent.Infrastructure.Providers.Mock;
using Cascade.CTL.Agent.Infrastructure.RAG;
using Cascade.CTL.Agent.Infrastructure.RAG.Query;
using Cascade.CTL.Agent.Infrastructure.Observability;
using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.Extensibility;

namespace Cascade.CTL.Agent.Infrastructure;

public static class InfrastructureRegistration
{
    public static IServiceCollection AddCTLInfrastructure(
        this IServiceCollection services,
        bool useMockProviders = true,
        IConfiguration? configuration = null,
        string? ragKnowledgePath = null)
    {
        // Wire IAssetProfileProvider: HTTP provider takes precedence when configured
        RegisterAssetProfileProvider(services, configuration);

        if (useMockProviders)
        {
            services.AddSingleton<ITitleSearchProvider, MockTitleSearchProvider>();
            services.AddSingleton<IHOAProvider, MockHOAProvider>();
            services.AddSingleton<ICodeViolationProvider, MockCodeViolationProvider>();
            services.AddSingleton<IBPOProvider, MockBPOProvider>();
            services.AddSingleton<IAVMProvider, MockAVMProvider>();
            services.AddSingleton<IOccupancyProvider, MockOccupancyProvider>();
        }

        services.AddSingleton<IRAGQueryService>(sp => CreateRAGService(sp, configuration, ragKnowledgePath));

        // Audit file store — shared by all audit service implementations for disk persistence
        services.AddSingleton<AuditFileStore>();

        var appInsightsConnectionString = configuration?["ApplicationInsights:ConnectionString"];
        if (!string.IsNullOrWhiteSpace(appInsightsConnectionString))
        {
            var telemetryConfig = new Microsoft.ApplicationInsights.Extensibility.TelemetryConfiguration
            {
                ConnectionString = appInsightsConnectionString
            };
            services.AddSingleton(telemetryConfig);
            services.AddSingleton(new TelemetryClient(telemetryConfig));
            services.AddSingleton<IAuditService, AppInsightsAuditService>();
        }
        else
        {
            services.AddSingleton<IAuditService, InMemoryAuditService>();
        }

        services.AddSingleton<IHumanReviewService, MockHumanReviewService>();
        services.AddCTLTelemetry(appInsightsConnectionString);

        return services;
    }

    /// <summary>
    /// Registers IAssetProfileProvider. When <c>AssetDomainService:BaseUrl</c> is configured,
    /// wires the HTTP-based provider with resilience pipeline; otherwise falls back to mock.
    /// </summary>
    private static void RegisterAssetProfileProvider(
        IServiceCollection services,
        IConfiguration? configuration)
    {
        var section = configuration?.GetSection(AssetDomainServiceOptions.SectionName);
        var baseUrl = section?["BaseUrl"];

        if (!string.IsNullOrWhiteSpace(baseUrl))
        {
            services.Configure<AssetDomainServiceOptions>(section!);
            var options = new AssetDomainServiceOptions();
            section!.Bind(options);

            var httpClientBuilder = services
                .AddHttpClient<IAssetProfileProvider, HttpAssetProfileProvider>(client =>
                {
                    client.BaseAddress = new Uri(options.BaseUrl);
                    client.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);
                });

            if (options.UseAzureIdentity)
            {
                // OAuth 2.0 token acquisition via Azure Identity — acquires tokens per-request,
                // caching handled by DefaultAzureCredential internally
                httpClientBuilder.AddHttpMessageHandler(() =>
                    new AzureIdentityAuthHandler(new DefaultAzureCredential(), options.Scope));
            }
            else if (!string.IsNullOrEmpty(options.ApiKey))
            {
                // Static X-Api-Key header — for development/local Docker and test scenarios.
                // The Asset Domain API validates this header with a fixed-time comparison.
                httpClientBuilder.AddHttpMessageHandler(() => new ApiKeyAuthHandler(options.ApiKey!));
            }

            httpClientBuilder.AddStandardResilienceHandler(resilience =>
            {
                resilience.Retry.MaxRetryAttempts = options.RetryCount;
                resilience.CircuitBreaker.FailureRatio = 0.5;
                resilience.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(options.CircuitBreakerDurationSeconds);
                resilience.AttemptTimeout.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);
            });
        }
        else
        {
            services.AddSingleton<IAssetProfileProvider, MockAssetProfileProvider>();
        }
    }

    /// <summary>
    /// Factory for <see cref="IRAGQueryService"/>. When <c>CTLAgent:RAG:AzureSearch:Enabled = true</c> and
    /// an endpoint is configured, wires the production <see cref="AzureSearchRAGService"/>; otherwise falls back
    /// to <see cref="InMemoryRAGService"/> for local dev and CI.
    /// </summary>
    private static IRAGQueryService CreateRAGService(
        IServiceProvider sp,
        IConfiguration? configuration,
        string? ragKnowledgePath)
    {
        var factoryLogger = sp.GetRequiredService<ILoggerFactory>()
            .CreateLogger("Cascade.CTL.Agent.Infrastructure.InfrastructureRegistration");

        var section = configuration?.GetSection(AzureSearchRAGOptions.SectionName);
        if (section is not null && section.Exists())
        {
            var options = new AzureSearchRAGOptions();
            section.Bind(options);

            if (options.Enabled && !string.IsNullOrWhiteSpace(options.Endpoint))
            {
                try
                {
                    var azureLogger = sp.GetRequiredService<ILogger<AzureSearchRAGService>>();
                    var searchClient = AzureSearchClientFactory.CreateSearchClient(options);
                    var embeddingClient = AzureSearchClientFactory.CreateEmbeddingClient(options);

                    var executor = new AzureSearchExecutor(searchClient);
                    var embeddings = new AzureOpenAIEmbeddingGenerator(embeddingClient);
                    var wrappedOptions = Microsoft.Extensions.Options.Options.Create(options);

                    azureLogger.LogInformation(
                        "RAG: Using AzureSearchRAGService (index='{Index}', embedding='{Model}', dim={Dim})",
                        options.IndexName, options.EmbeddingDeployment, options.EmbeddingDimensions);

                    return new AzureSearchRAGService(executor, embeddings, wrappedOptions, azureLogger);
                }
                catch (Exception ex)
                {
                    factoryLogger.LogError(ex,
                        "RAG: Failed to initialise AzureSearchRAGService — falling back to InMemoryRAGService.");
                }
            }
        }

        var inMemoryLogger = sp.GetRequiredService<ILogger<InMemoryRAGService>>();
        factoryLogger.LogInformation("RAG: Using InMemoryRAGService (knowledgePath={Path})", ragKnowledgePath ?? "<builtin>");
        return new InMemoryRAGService(inMemoryLogger, ragKnowledgePath);
    }
}