using Cascade.CTL.Agent.Domain.Models;

namespace Cascade.CTL.Agent.Application.Orchestration;

/// <summary>
/// Abstraction for CTL evaluation orchestration.
/// Implemented by <see cref="Workflow.CTLWorkflowOrchestrator"/> using Microsoft Agent Framework workflows.
/// </summary>
public interface ICTLEvaluationOrchestrator
{
    Task<CTLEvaluationResult> EvaluateAsync(CTLEvaluationRequest request, CancellationToken cancellationToken = default);
}
