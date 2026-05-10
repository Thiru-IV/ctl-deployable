using Cascade.CTL.Agent.McpServer;

var builder = WebApplication.CreateBuilder(args);

// Load shared config from solution root (same config the Host uses)
var solutionRoot = FindSolutionRoot(Directory.GetCurrentDirectory());
if (solutionRoot is not null)
{
    builder.Configuration.AddJsonFile(Path.Combine(solutionRoot, "config", "appsettings.json"), optional: true, reloadOnChange: false);
}

builder.Services.AddCTLMcpServer(builder.Configuration);

var app = builder.Build();

app.UseCTLMcpServer();

app.Lifetime.ApplicationStarted.Register(() =>
{
    var urls = app.Urls;
    var address = urls.FirstOrDefault() ?? "http://localhost:5100";
    Console.ForegroundColor = ConsoleColor.Green;
    Console.WriteLine($"  MCP Server started successfully. Listening on {address}");
    Console.ResetColor();
    Console.WriteLine("  Press Ctrl+C to shut down.");
});

try
{
    app.Run();
}
catch (Exception ex)
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.WriteLine($"  MCP Server failed to start: {ex.Message}");
    Console.ResetColor();
    return 1;
}

return 0;

static string? FindSolutionRoot(string startDir)
{
    var dir = startDir;
    while (dir is not null)
    {
        if (Directory.Exists(Path.Combine(dir, "config")))
            return dir;
        dir = Directory.GetParent(dir)?.FullName;
    }
    return null;
}

public partial class Program { }
