using Cascade.CTL.Agent.Application.Prompts;
using FluentAssertions;
using Xunit;

namespace Cascade.CTL.Agent.Tests.Orchestration;

/// <summary>
/// Guards the orchestrator planning prompt against regressions that could reintroduce
/// the now-removed <c>GetAssetProfile</c> tool instruction.
/// </summary>
public sealed class OrchestratorPromptsTests
{
    [Fact]
    public void PlanningSystemPrompt_DoesNotInstructAgentToCallGetAssetProfile()
    {
        OrchestratorPrompts.PlanningSystemPrompt
            .Should().NotContain("GetAssetProfile",
                "the orchestrator pre-fetches the asset profile and injects it into the prompt; " +
                "the agent must not be told to call a tool that has been intentionally removed from its tool list");
    }

    [Fact]
    public void PlanningSystemPrompt_TellsAgentProfileIsAlreadyProvided()
    {
        OrchestratorPrompts.PlanningSystemPrompt
            .Should().Contain("already been retrieved");
    }

    [Fact]
    public void PlanningSystemPrompt_StillReferencesKnowledgeBaseTool()
    {
        OrchestratorPrompts.PlanningSystemPrompt
            .Should().Contain("query_policy_knowledge_base_via_rag");
    }
}
