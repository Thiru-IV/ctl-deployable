using Cascade.CTL.Agent.Infrastructure;
using Microsoft.Extensions.Configuration;

namespace Cascade.CTL.Agent.McpServer;

public static class McpServerRegistration
{
    /// <summary>
    /// Registers the CTL MCP tool server: infrastructure providers, MCP runtime,
    /// HTTP transport, and auto-discovered tool classes from this assembly.
    /// </summary>
    public static IServiceCollection AddCTLMcpServer(this IServiceCollection services, IConfiguration? configuration = null)
    {
        services.AddCTLInfrastructure(useMockProviders: true, configuration: configuration);

        services.AddMcpServer()
            .WithHttpTransport()
            .WithToolsFromAssembly();

        return services;
    }

    /// <summary>
    /// Maps MCP endpoints with required API key authentication.
    /// Clients must send <c>X-Api-Key</c> header matching <c>McpServer:ApiKey</c> config.
    /// Throws at startup if no API key is configured.
    /// </summary>
    public static WebApplication UseCTLMcpServer(this WebApplication app)
    {
        var expectedApiKey = app.Configuration["McpServer:ApiKey"]
            ?? throw new InvalidOperationException(
                "McpServer:ApiKey must be configured. Set it in appsettings.json or via environment variable McpServer__ApiKey.");

        app.Use(async (context, next) =>
        {
            var apiKey = context.Request.Headers["X-Api-Key"].ToString();
            if (!string.Equals(apiKey, expectedApiKey, StringComparison.Ordinal))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await context.Response.WriteAsync("Invalid or missing API key");
                return;
            }
            await next();
        });

        app.MapMcp();

        return app;
    }
}
