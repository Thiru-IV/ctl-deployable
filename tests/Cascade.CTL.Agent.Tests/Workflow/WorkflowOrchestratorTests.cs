using Cascade.CTL.Agent.Application.Configuration;
using Cascade.CTL.Agent.Application.Orchestration;
using Cascade.CTL.Agent.Application.Orchestration.Workflow;
using Cascade.CTL.Agent.Application.Resilience;
using Cascade.CTL.Agent.Domain.Contracts;
using Cascade.CTL.Agent.Domain.Enums;
using Cascade.CTL.Agent.Domain.Models;
using Cascade.CTL.Agent.Guardrails;
using FluentAssertions;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace Cascade.CTL.Agent.Tests.Workflow;

public class WorkflowOrchestratorTests
{
    private readonly IChatClient _mockChatClient = Substitute.For<IChatClient>();
    private readonly IMcpToolProvider _mockToolProvider = Substitute.For<IMcpToolProvider>();
    private readonly IAuditService _mockAuditService = Substitute.For<IAuditService>();
    private readonly IAssetProfileProvider _mockAssetProfileProvider = Substitute.For<IAssetProfileProvider>();
    private readonly IHumanReviewService _mockHumanReviewService = Substitute.For<IHumanReviewService>();
    private readonly ContentSafetyGuard _contentSafetyGuard;
    private readonly TokenBudgetGuard _tokenBudgetGuard;
    private readonly CTLRequestValidator _requestValidator;
    private readonly IOptions<ResilienceOptions> _resilienceOptions = Options.Create(new ResilienceOptions());
    private readonly IOptions<CTLAgentOptions> _agentOptions = Options.Create(new CTLAgentOptions());
    private readonly VerdictGroundednessEvaluator _groundednessEvaluator;

    public WorkflowOrchestratorTests()
    {
        var tbLogger = Substitute.For<ILogger<TokenBudgetGuard>>();
        _tokenBudgetGuard = new TokenBudgetGuard(tbLogger, Options.Create(new TokenBudgetOptions()));
        var rvLogger = Substitute.For<ILogger<CTLRequestValidator>>();
        _requestValidator = new CTLRequestValidator(rvLogger);
        _contentSafetyGuard = new ContentSafetyGuard(
            Substitute.For<ILogger<ContentSafetyGuard>>(),
            new LocalPromptInjectionDetector(Substitute.For<ILogger<LocalPromptInjectionDetector>>()),
            Options.Create(new ContentSafetyOptions { Enabled = false }));
        _groundednessEvaluator = new VerdictGroundednessEvaluator(
            _mockChatClient, Substitute.For<ILogger<VerdictGroundednessEvaluator>>());
    }

    // ──────────────────────────────────────────────────────────────────
    // ICTLEvaluationOrchestrator interface tests
    // ──────────────────────────────────────────────────────────────────

    [Fact]
    public void WorkflowOrchestrator_ShouldImplementInterface()
    {
        var logger = Substitute.For<ILogger<CTLWorkflowOrchestrator>>();
        var orchestrator = new CTLWorkflowOrchestrator(
            _mockChatClient, _mockToolProvider, _mockAuditService,
            _mockAssetProfileProvider, _mockHumanReviewService, _contentSafetyGuard, _tokenBudgetGuard, _requestValidator,
            _groundednessEvaluator, _agentOptions, Options.Create(new VerdictPolicyOptions()), _resilienceOptions,
            Options.Create(new ReflectionDeterminismOptions()), logger);

        orchestrator.Should().BeAssignableTo<ICTLEvaluationOrchestrator>();
    }

    // ──────────────────────────────────────────────────────────────────
    // Workflow orchestrator validation tests
    // ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task WorkflowOrchestrator_ShouldRejectInvalidRequest()
    {
        var logger = Substitute.For<ILogger<CTLWorkflowOrchestrator>>();
        var orchestrator = new CTLWorkflowOrchestrator(
            _mockChatClient, _mockToolProvider, _mockAuditService,
            _mockAssetProfileProvider, _mockHumanReviewService, _contentSafetyGuard, _tokenBudgetGuard, _requestValidator,
            _groundednessEvaluator, _agentOptions, Options.Create(new VerdictPolicyOptions()), _resilienceOptions,
            Options.Create(new ReflectionDeterminismOptions()), logger);

        var request = new CTLEvaluationRequest
        {
            AssetId = "", // invalid — empty
            WorkflowInstanceId = "test"
        };

        Func<Task> act = () => orchestrator.EvaluateAsync(request);
        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*Invalid evaluation request*");
    }

    [Fact]
    public async Task WorkflowOrchestrator_ShouldRejectNullAssetId()
    {
        var logger = Substitute.For<ILogger<CTLWorkflowOrchestrator>>();
        var orchestrator = new CTLWorkflowOrchestrator(
            _mockChatClient, _mockToolProvider, _mockAuditService,
            _mockAssetProfileProvider, _mockHumanReviewService, _contentSafetyGuard, _tokenBudgetGuard, _requestValidator,
            _groundednessEvaluator, _agentOptions, Options.Create(new VerdictPolicyOptions()), _resilienceOptions,
            Options.Create(new ReflectionDeterminismOptions()), logger);

        var request = new CTLEvaluationRequest
        {
            AssetId = null!,
            WorkflowInstanceId = "test"
        };

        Func<Task> act = () => orchestrator.EvaluateAsync(request);
        await act.Should().ThrowAsync<ArgumentException>();
    }

    // ──────────────────────────────────────────────────────────────────
    // Executor instantiation tests
    // ──────────────────────────────────────────────────────────────────

    [Fact]
    public void PlanningExecutor_ShouldHaveCorrectId()
    {
        var logger = Substitute.For<ILogger>();
        var executor = new PlanningExecutor(
            _mockChatClient, _mockToolProvider,
            _mockAuditService, new ResilienceOptions(), logger);

        executor.Id.Should().Be("PlanningExecutor");
    }

    [Fact]
    public void InvestigationPhaseExecutor_ShouldHaveCorrectId()
    {
        var logger = Substitute.For<ILogger>();
        var executor = new InvestigationPhaseExecutor(
            _mockChatClient, _mockToolProvider,
            _mockAuditService, new ResilienceOptions(), logger);

        executor.Id.Should().Be("InvestigationPhaseExecutor");
    }

    [Fact]
    public void ReflectionExecutor_ShouldHaveCorrectId()
    {
        var logger = Substitute.For<ILogger>();
        var executor = new ReflectionExecutor(
            _mockChatClient, _mockToolProvider,
            _mockAuditService, new ResilienceOptions(), logger);

        executor.Id.Should().Be("ReflectionExecutor");
    }

    // ──────────────────────────────────────────────────────────────────
    // Executor protocol configuration tests
    // ──────────────────────────────────────────────────────────────────

    [Fact]
    public void PlanningExecutor_ShouldAcceptPlanRequest()
    {
        var logger = Substitute.For<ILogger>();
        var executor = new PlanningExecutor(
            _mockChatClient, _mockToolProvider,
            _mockAuditService, new ResilienceOptions(), logger);

        executor.CanHandle(typeof(PlanRequest)).Should().BeTrue();
    }

    [Fact]
    public void PlanningExecutor_ShouldOutputPlanResult()
    {
        var logger = Substitute.For<ILogger>();
        var executor = new PlanningExecutor(
            _mockChatClient, _mockToolProvider,
            _mockAuditService, new ResilienceOptions(), logger);

        executor.OutputTypes.Should().Contain(typeof(PlanResult));
    }

    [Fact]
    public void InvestigationPhaseExecutor_ShouldAcceptPlanResult()
    {
        var logger = Substitute.For<ILogger>();
        var executor = new InvestigationPhaseExecutor(
            _mockChatClient, _mockToolProvider,
            _mockAuditService, new ResilienceOptions(), logger);

        executor.CanHandle(typeof(PlanResult)).Should().BeTrue();
    }

    [Fact]
    public void InvestigationPhaseExecutor_ShouldOutputInvestigationPhaseResult()
    {
        var logger = Substitute.For<ILogger>();
        var executor = new InvestigationPhaseExecutor(
            _mockChatClient, _mockToolProvider,
            _mockAuditService, new ResilienceOptions(), logger);

        executor.OutputTypes.Should().Contain(typeof(InvestigationPhaseResult));
    }

    [Fact]
    public void ReflectionExecutor_ShouldAcceptInvestigationPhaseResult()
    {
        var logger = Substitute.For<ILogger>();
        var executor = new ReflectionExecutor(
            _mockChatClient, _mockToolProvider,
            _mockAuditService, new ResilienceOptions(), logger);

        executor.CanHandle(typeof(InvestigationPhaseResult)).Should().BeTrue();
    }

    [Fact]
    public void ReflectionExecutor_ShouldOutputReflectionResult()
    {
        var logger = Substitute.For<ILogger>();
        var executor = new ReflectionExecutor(
            _mockChatClient, _mockToolProvider,
            _mockAuditService, new ResilienceOptions(), logger);

        executor.OutputTypes.Should().Contain(typeof(ReflectionResult));
    }

    // ──────────────────────────────────────────────────────────────────
    // Edge type compatibility tests — output of source matches input of target
    // ──────────────────────────────────────────────────────────────────

    [Fact]
    public void PlanningToInvestigation_EdgeTypes_ShouldBeCompatible()
    {
        var logger = Substitute.For<ILogger>();
        var planning = new PlanningExecutor(
            _mockChatClient, _mockToolProvider,
            _mockAuditService, new ResilienceOptions(), logger);
        var investigation = new InvestigationPhaseExecutor(
            _mockChatClient, _mockToolProvider,
            _mockAuditService, new ResilienceOptions(), logger);

        // PlanningExecutor outputs PlanResult, InvestigationPhaseExecutor accepts PlanResult
        planning.OutputTypes.Should().Contain(typeof(PlanResult));
        investigation.CanHandle(typeof(PlanResult)).Should().BeTrue();
    }

    [Fact]
    public void InvestigationToReflection_EdgeTypes_ShouldBeCompatible()
    {
        var logger = Substitute.For<ILogger>();
        var investigation = new InvestigationPhaseExecutor(
            _mockChatClient, _mockToolProvider,
            _mockAuditService, new ResilienceOptions(), logger);
        var reflection = new ReflectionExecutor(
            _mockChatClient, _mockToolProvider,
            _mockAuditService, new ResilienceOptions(), logger);

        // InvestigationPhaseExecutor outputs InvestigationPhaseResult, ReflectionExecutor accepts it
        investigation.OutputTypes.Should().Contain(typeof(InvestigationPhaseResult));
        reflection.CanHandle(typeof(InvestigationPhaseResult)).Should().BeTrue();
    }

    // ──────────────────────────────────────────────────────────────────
    // Executor type incompatibility tests (negative)
    // ──────────────────────────────────────────────────────────────────

    [Fact]
    public void PlanningExecutor_ShouldNotAcceptWrongType()
    {
        var logger = Substitute.For<ILogger>();
        var executor = new PlanningExecutor(
            _mockChatClient, _mockToolProvider,
            _mockAuditService, new ResilienceOptions(), logger);

        executor.CanHandle(typeof(InvestigationPhaseResult)).Should().BeFalse();
        executor.CanHandle(typeof(string)).Should().BeFalse();
    }

    [Fact]
    public void InvestigationPhaseExecutor_ShouldNotAcceptWrongType()
    {
        var logger = Substitute.For<ILogger>();
        var executor = new InvestigationPhaseExecutor(
            _mockChatClient, _mockToolProvider,
            _mockAuditService, new ResilienceOptions(), logger);

        executor.CanHandle(typeof(PlanRequest)).Should().BeFalse();
        executor.CanHandle(typeof(ReflectionResult)).Should().BeFalse();
    }

    [Fact]
    public void ReflectionExecutor_ShouldNotAcceptWrongType()
    {
        var logger = Substitute.For<ILogger>();
        var executor = new ReflectionExecutor(
            _mockChatClient, _mockToolProvider,
            _mockAuditService, new ResilienceOptions(), logger);

        executor.CanHandle(typeof(PlanRequest)).Should().BeFalse();
        executor.CanHandle(typeof(PlanResult)).Should().BeFalse();
    }

    // ──────────────────────────────────────────────────────────────────
    // Workflow message model tests
    // ──────────────────────────────────────────────────────────────────

    [Fact]
    public void PlanRequest_ShouldHoldAssetIdAndSessionId()
    {
        var request = new PlanRequest { AssetId = "ASSET-TX-001", SessionId = "abc123", AssetProfileJson = "{}" };
        request.AssetId.Should().Be("ASSET-TX-001");
        request.SessionId.Should().Be("abc123");
    }

    [Fact]
    public void PlanResult_ShouldHoldAllFields()
    {
        var result = new PlanResult
        {
            PlanJson = "{\"requiredDomains\":[\"Legal\"]}",
            AssetProfileJson = "{}",
            RequiredDomains = [VerificationDomain.Legal],
            ToolCalls = 3,
            AssetId = "ASSET-TX-001",
            SessionId = "abc123"
        };

        result.PlanJson.Should().Contain("Legal");
        result.RequiredDomains.Should().Contain(VerificationDomain.Legal);
        result.ToolCalls.Should().Be(3);
    }

    [Fact]
    public void InvestigationPhaseResult_ShouldAggregateAllDomainFindings()
    {
        var result = new InvestigationPhaseResult
        {
            PlanJson = "{}",
            AssetProfileJson = "{}",
            RequiredDomains = [VerificationDomain.Legal, VerificationDomain.Valuation],
            LegalFindings = "Title clear",
            ValuationFindings = "BPO fresh",
            OccupancyFindings = "Domain not evaluated — not required by verification plan.",
            TotalToolCalls = 7,
            AssetId = "ASSET-FL-003",
            SessionId = "ghi789"
        };

        result.RequiredDomains.Should().HaveCount(2);
        result.TotalToolCalls.Should().Be(7);
        result.LegalFindings.Should().Contain("clear");
        result.OccupancyFindings.Should().Contain("not required");
    }

    [Fact]
    public void ReflectionResult_ShouldCarryVerdictAndToolCounts()
    {
        var result = new ReflectionResult
        {
            VerdictJson = "{\"verdict\":\"Clear\"}",
            InvestigationFindings = "Legal: title clear. Valuation: BPO $285K.",
            ToolCalls = 2,
            TotalToolCalls = 9,
            AssetId = "ASSET-TX-001",
            SessionId = "abc123"
        };

        result.VerdictJson.Should().Contain("Clear");
        result.TotalToolCalls.Should().Be(9);
    }

    // ──────────────────────────────────────────────────────────────────
    // Workflow graph construction tests — single connected graph
    // ──────────────────────────────────────────────────────────────────

    [Fact]
    public void WorkflowBuilder_ShouldBuildConnectedThreeNodeGraph()
    {
        var logger = Substitute.For<ILogger>();
        var planning = new PlanningExecutor(
            _mockChatClient, _mockToolProvider,
            _mockAuditService, new ResilienceOptions(), logger);
        var investigation = new InvestigationPhaseExecutor(
            _mockChatClient, _mockToolProvider,
            _mockAuditService, new ResilienceOptions(), logger);
        var reflection = new ReflectionExecutor(
            _mockChatClient, _mockToolProvider,
            _mockAuditService, new ResilienceOptions(), logger);

        // Build the exact same graph the orchestrator builds
        var workflow = new WorkflowBuilder(planning)
            .AddEdge(planning, investigation)
            .AddEdge(investigation, reflection)
            .WithOutputFrom(reflection)
            .Build();

        workflow.Should().NotBeNull();
        workflow.StartExecutorId.Should().Be("PlanningExecutor");
    }

    [Fact]
    public void WorkflowGraph_StartNode_ShouldBePlanningExecutor()
    {
        var logger = Substitute.For<ILogger>();
        var planning = new PlanningExecutor(
            _mockChatClient, _mockToolProvider,
            _mockAuditService, new ResilienceOptions(), logger);

        var workflow = new WorkflowBuilder(planning)
            .WithOutputFrom(planning)
            .Build();

        workflow.StartExecutorId.Should().Be("PlanningExecutor");
    }

    // ──────────────────────────────────────────────────────────────────
    // Integration: both orchestrators produce same type
    // ──────────────────────────────────────────────────────────────────

    [Fact]
    public void BothOrchestrators_ShouldReturnCTLEvaluationResult()
    {
        typeof(ICTLEvaluationOrchestrator).GetMethod("EvaluateAsync")!
            .ReturnType.Should().Be(typeof(Task<CTLEvaluationResult>));
    }
}
