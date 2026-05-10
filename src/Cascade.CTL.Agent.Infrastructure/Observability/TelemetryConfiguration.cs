using Azure.Monitor.OpenTelemetry.Exporter;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Cascade.CTL.Agent.Infrastructure.Observability;

public static class TelemetryConfiguration
{
    public const string ServiceName = "Cascade.CTL.Agent";
    public const string ServiceVersion = "1.0.0";

    public static IServiceCollection AddCTLTelemetry(
        this IServiceCollection services,
        string? appInsightsConnectionString = null)
    {
        services.AddOpenTelemetry()
            .ConfigureResource(resource => resource
                .AddService(serviceName: ServiceName, serviceVersion: ServiceVersion))
            .WithTracing(tracing =>
            {
                tracing
                    .AddSource(ServiceName)
                    .AddSource("Microsoft.Extensions.AI")
                    .AddHttpClientInstrumentation();

                if (!string.IsNullOrWhiteSpace(appInsightsConnectionString))
                    tracing.AddAzureMonitorTraceExporter(o => o.ConnectionString = appInsightsConnectionString);
            })
            .WithMetrics(metrics =>
            {
                metrics
                    .AddMeter(ServiceName)
                    .AddHttpClientInstrumentation()
                    .AddRuntimeInstrumentation();

                if (!string.IsNullOrWhiteSpace(appInsightsConnectionString))
                    metrics.AddAzureMonitorMetricExporter(o => o.ConnectionString = appInsightsConnectionString);
            });

        return services;
    }
}
