using Cascade.CTL.Agent.Domain.Enums;
using Cascade.CTL.Agent.Domain.Models;
using Cascade.CTL.Agent.Evals;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.Evaluation;
using Microsoft.Extensions.AI.Evaluation.Quality;
using NSubstitute;
using Xunit;

namespace Cascade.CTL.Agent.Tests.Evaluation;

public class ReflectionQualityEvaluatorTests
{
    // ──────────────────────────────────────────────────────────────────
    // Evaluator instantiation
    // ──────────────────────────────────────────────────────────────────

    [Fact]
    public void ReflectionQualityEvaluator_ShouldInstantiateWithChatClient()
    {
        var mockClient = Substitute.For<IChatClient>();

        var evaluator = new ReflectionQualityEvaluator(mockClient);

        evaluator.Should().NotBeNull();
    }

    // ──────────────────────────────────────────────────────────────────
    // Result model tests
    // ──────────────────────────────────────────────────────────────────

    [Fact]
    public void ReflectionQualityResult_ShouldPassWhenBothEvaluatorsSucceed()
    {
        var result = new ReflectionQualityResult
        {
            GroundednessScore = 4.5,
            GroundednessRating = EvaluationRating.Good,
            GroundednessFailed = false,
            RelevanceScore = 5.0,
            RelevanceRating = EvaluationRating.Exceptional,
            RelevanceFailed = false,
            HasDiagnostics = false
        };

        result.Passed.Should().BeTrue();
    }

    [Fact]
    public void ReflectionQualityResult_ShouldFailWhenGroundednessFails()
    {
        var result = new ReflectionQualityResult
        {
            GroundednessScore = 1.0,
            GroundednessRating = EvaluationRating.Unacceptable,
            GroundednessFailed = true,
            RelevanceScore = 5.0,
            RelevanceRating = EvaluationRating.Exceptional,
            RelevanceFailed = false,
            HasDiagnostics = false
        };

        result.Passed.Should().BeFalse();
    }

    [Fact]
    public void ReflectionQualityResult_ShouldFailWhenRelevanceFails()
    {
        var result = new ReflectionQualityResult
        {
            GroundednessScore = 5.0,
            GroundednessRating = EvaluationRating.Exceptional,
            GroundednessFailed = false,
            RelevanceScore = 1.0,
            RelevanceRating = EvaluationRating.Unacceptable,
            RelevanceFailed = true,
            HasDiagnostics = false
        };

        result.Passed.Should().BeFalse();
    }

    [Fact]
    public void ReflectionQualityResult_ShouldFailWhenDiagnosticsPresent()
    {
        var result = new ReflectionQualityResult
        {
            GroundednessScore = 4.0,
            GroundednessRating = EvaluationRating.Good,
            GroundednessFailed = false,
            RelevanceScore = 4.0,
            RelevanceRating = EvaluationRating.Good,
            RelevanceFailed = false,
            HasDiagnostics = true
        };

        result.Passed.Should().BeFalse();
    }

    // ──────────────────────────────────────────────────────────────────
    // Microsoft AI Evaluator type verification
    // ──────────────────────────────────────────────────────────────────

    [Fact]
    public void GroundednessEvaluator_ShouldImplementIEvaluator()
    {
        var evaluator = new GroundednessEvaluator();
        evaluator.Should().BeAssignableTo<IEvaluator>();
    }

    [Fact]
    public void RelevanceEvaluator_ShouldImplementIEvaluator()
    {
        var evaluator = new RelevanceEvaluator();
        evaluator.Should().BeAssignableTo<IEvaluator>();
    }

    [Fact]
    public void GroundednessEvaluator_MetricName_ShouldBeAccessible()
    {
        GroundednessEvaluator.GroundednessMetricName.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void RelevanceEvaluator_MetricName_ShouldBeAccessible()
    {
        RelevanceEvaluator.RelevanceMetricName.Should().NotBeNullOrWhiteSpace();
    }

    // ──────────────────────────────────────────────────────────────────
    // ChatConfiguration wiring
    // ──────────────────────────────────────────────────────────────────

    [Fact]
    public void ChatConfiguration_ShouldAcceptIChatClient()
    {
        var mockClient = Substitute.For<IChatClient>();
        var config = new ChatConfiguration(mockClient);

        config.ChatClient.Should().BeSameAs(mockClient);
    }

    // ──────────────────────────────────────────────────────────────────
    // EvaluationResult model
    // ──────────────────────────────────────────────────────────────────

    [Fact]
    public void NumericMetric_ShouldHoldScoreAndInterpretation()
    {
        var metric = new NumericMetric("TestMetric", 4.5);
        metric.Value.Should().Be(4.5);
        metric.Name.Should().Be("TestMetric");
    }

    [Fact]
    public void EvaluationResult_ShouldStoreMetrics()
    {
        var result = new EvaluationResult();
        var metric = new NumericMetric("TestScore", 3.0);
        result.Metrics.Add("TestScore", metric);

        result.Get<NumericMetric>("TestScore").Value.Should().Be(3.0);
    }

    // ──────────────────────────────────────────────────────────────────
    // CTLEvaluationResult format for evaluators
    // ──────────────────────────────────────────────────────────────────

    [Fact]
    public void ReflectionQualityResult_AllFields_ShouldBePopulated()
    {
        var result = new ReflectionQualityResult
        {
            GroundednessScore = 4.0,
            GroundednessRating = EvaluationRating.Good,
            GroundednessFailed = false,
            RelevanceScore = 3.5,
            RelevanceRating = EvaluationRating.Average,
            RelevanceFailed = false,
            HasDiagnostics = false
        };

        result.GroundednessScore.Should().Be(4.0);
        result.RelevanceScore.Should().Be(3.5);
        result.GroundednessRating.Should().Be(EvaluationRating.Good);
        result.RelevanceRating.Should().Be(EvaluationRating.Average);
        result.Passed.Should().BeTrue();
    }
}
