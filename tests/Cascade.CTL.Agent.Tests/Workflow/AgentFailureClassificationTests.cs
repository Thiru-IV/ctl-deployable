using System.Net;
using Cascade.CTL.Agent.Application.Orchestration.Workflow;
using FluentAssertions;
using Xunit;

namespace Cascade.CTL.Agent.Tests.Workflow;

/// <summary>
/// Tests for <see cref="InvestigationPhaseExecutor.ClassifyAgentFailure"/> — the helper that
/// turns a sub-agent retry-pipeline exception into a human-readable audit description so the
/// audit JSONL records "HTTP 429 (Azure OpenAI rate limit ...)" instead of an opaque
/// "ClientResultException". Demo-day defensibility property.
/// </summary>
public class AgentFailureClassificationTests
{
    [Fact]
    public void ClassifyAgentFailure_HttpRequestException429_LabelsAsAzureOpenAIRateLimit()
    {
        var ex = new HttpRequestException("Too Many Requests", null, HttpStatusCode.TooManyRequests);

        var (label, status) = InvestigationPhaseExecutor.ClassifyAgentFailure(ex);

        status.Should().Be(429);
        label.Should().Contain("HTTP 429");
        label.Should().Contain("rate limit");
    }

    [Fact]
    public void ClassifyAgentFailure_ClientResultExceptionLikeStatus429_LabelsAsRateLimit()
    {
        // Simulate the System.ClientModel.ClientResultException shape (an int Status property)
        // without taking a hard reference on that type from the test project.
        var ex = new FakeClientResultException("HTTP 429 (too_many_requests: too_many_requests)", 429);

        var (label, status) = InvestigationPhaseExecutor.ClassifyAgentFailure(ex);

        status.Should().Be(429);
        label.Should().Contain("HTTP 429");
    }

    [Fact]
    public void ClassifyAgentFailure_StatusInInnerException_StillExtracts()
    {
        var inner = new HttpRequestException("rate limited", null, HttpStatusCode.TooManyRequests);
        var outer = new InvalidOperationException("retry pipeline gave up", inner);

        var (label, status) = InvestigationPhaseExecutor.ClassifyAgentFailure(outer);

        status.Should().Be(429);
        label.Should().Contain("HTTP 429");
    }

    [Fact]
    public void ClassifyAgentFailure_Http503_LabelsAsUpstreamServiceError()
    {
        var ex = new HttpRequestException("Service Unavailable", null, HttpStatusCode.ServiceUnavailable);

        var (label, status) = InvestigationPhaseExecutor.ClassifyAgentFailure(ex);

        status.Should().Be(503);
        label.Should().Contain("HTTP 503");
        label.Should().Contain("upstream service error");
    }

    [Fact]
    public void ClassifyAgentFailure_Http400_LabelsAsBareHttpStatus()
    {
        var ex = new HttpRequestException("Bad Request", null, HttpStatusCode.BadRequest);

        var (label, status) = InvestigationPhaseExecutor.ClassifyAgentFailure(ex);

        status.Should().Be(400);
        label.Should().Be("HTTP 400");
    }

    [Fact]
    public void ClassifyAgentFailure_Cancellation_LabelsAsCancelledOrTimedOut()
    {
        var ex = new OperationCanceledException("timed out");

        var (label, status) = InvestigationPhaseExecutor.ClassifyAgentFailure(ex);

        status.Should().BeNull();
        label.Should().Be("Cancelled or timed out");
    }

    [Fact]
    public void ClassifyAgentFailure_UnknownException_FallsBackToTypeName()
    {
        var ex = new InvalidOperationException("something else");

        var (label, status) = InvestigationPhaseExecutor.ClassifyAgentFailure(ex);

        status.Should().BeNull();
        label.Should().Be(nameof(InvalidOperationException));
    }

    [Fact]
    public void ClassifyAgentFailure_MessageContainsTooManyRequests_DetectsAs429()
    {
        // Last-resort message scan covers SDK exceptions whose Status property we cannot reach.
        var ex = new InvalidOperationException("Inner error: too_many_requests");

        var (label, status) = InvestigationPhaseExecutor.ClassifyAgentFailure(ex);

        status.Should().Be(429);
        label.Should().Contain("HTTP 429");
    }

    /// <summary>
    /// Test double matching the public surface of <c>System.ClientModel.ClientResultException</c>
    /// — specifically the public <c>Status</c> int property. <see cref="InvestigationPhaseExecutor.ClassifyAgentFailure"/>
    /// uses reflection to read this without taking a hard SDK dependency, so any exception type
    /// exposing the same property must be recognised.
    /// </summary>
    private sealed class FakeClientResultException : Exception
    {
        public FakeClientResultException(string message, int status) : base(message)
        {
            Status = status;
        }

        public int Status { get; }
    }
}
