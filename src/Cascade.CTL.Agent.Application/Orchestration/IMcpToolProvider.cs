using Microsoft.Extensions.AI;

namespace Cascade.CTL.Agent.Application.Orchestration;

public interface IMcpToolProvider
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
    IReadOnlyList<AITool> GetToolsForOrchestrator();
    IReadOnlyList<AITool> GetToolsForLegalAgent();
    IReadOnlyList<AITool> GetToolsForValuationAgent();
    IReadOnlyList<AITool> GetToolsForOccupancyAgent();
    IReadOnlyList<AITool> GetAllTools();
}
