using Cascade.CTL.Agent.Application.Configuration;
using Cascade.CTL.Agent.Application.Orchestration;
using Cascade.CTL.Agent.Domain.Enums;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Xunit;

namespace Cascade.CTL.Agent.Tests.Orchestration;

// ─────────────────────────────────────────────────────────────────────────────
// 1. ParseRequiredDomains — plan drives which agents run
// ─────────────────────────────────────────────────────────────────────────────

public class ParseRequiredDomainsTests
{
    [Fact]
    public void ShouldParseAllThreeDomains()
    {
        var planJson = """
            {
                "assetId": "ASSET-TX-001",
                "requiredDomains": ["Legal", "Valuation", "Occupancy"],
                "relevantPolicies": ["TX Foreclosure Policy"],
                "assetProfileSummary": "Texas foreclosure",
                "planRationale": "Standard evaluation"
            }
            """;

        var domains = PlanParser.ParseRequiredDomains(planJson);

        domains.Should().HaveCount(3);
        domains.Should().Contain(VerificationDomain.Legal);
        domains.Should().Contain(VerificationDomain.Valuation);
        domains.Should().Contain(VerificationDomain.Occupancy);
    }

    [Fact]
    public void ShouldParseLegalOnly()
    {
        var planJson = """
            {
                "assetId": "ASSET-CA-002",
                "requiredDomains": ["Legal"],
                "relevantPolicies": ["CA Title Policy"],
                "assetProfileSummary": "CA REO with clean valuation",
                "planRationale": "Only legal check needed"
            }
            """;

        var domains = PlanParser.ParseRequiredDomains(planJson);

        domains.Should().HaveCount(1);
        domains.Should().Contain(VerificationDomain.Legal);
        domains.Should().NotContain(VerificationDomain.Valuation);
        domains.Should().NotContain(VerificationDomain.Occupancy);
    }

    [Fact]
    public void ShouldParseLegalAndValuation()
    {
        var planJson = """
            {
                "assetId": "ASSET-FL-003",
                "requiredDomains": ["Legal", "Valuation"],
                "relevantPolicies": [],
                "assetProfileSummary": "Florida short sale",
                "planRationale": "Occupancy already confirmed vacant"
            }
            """;

        var domains = PlanParser.ParseRequiredDomains(planJson);

        domains.Should().HaveCount(2);
        domains.Should().Contain(VerificationDomain.Legal);
        domains.Should().Contain(VerificationDomain.Valuation);
        domains.Should().NotContain(VerificationDomain.Occupancy);
    }

    [Fact]
    public void ShouldBeCaseInsensitive()
    {
        var planJson = """
            {
                "requiredDomains": ["legal", "VALUATION", "occupancy"]
            }
            """;

        var domains = PlanParser.ParseRequiredDomains(planJson);

        domains.Should().HaveCount(3);
    }

    [Fact]
    public void ShouldIgnoreUnknownDomains()
    {
        var planJson = """
            {
                "requiredDomains": ["Legal", "Insurance", "Valuation", "Environmental"]
            }
            """;

        var domains = PlanParser.ParseRequiredDomains(planJson);

        domains.Should().HaveCount(2);
        domains.Should().Contain(VerificationDomain.Legal);
        domains.Should().Contain(VerificationDomain.Valuation);
    }

    [Fact]
    public void ShouldFallBackToAllDomainsOnEmptyArray()
    {
        var planJson = """{"requiredDomains": []}""";

        var domains = PlanParser.ParseRequiredDomains(planJson);

        domains.Should().HaveCount(3, "empty plan should fail safe to ALL domains");
    }

    [Fact]
    public void ShouldFallBackToAllDomainsOnMissingField()
    {
        var planJson = """{"assetId": "ASSET-TX-001", "planRationale": "some plan"}""";

        var domains = PlanParser.ParseRequiredDomains(planJson);

        domains.Should().HaveCount(3, "missing requiredDomains should fail safe to ALL domains");
    }

    [Fact]
    public void ShouldFallBackToAllDomainsOnInvalidJson()
    {
        var planJson = "This is not JSON at all, just LLM rambling text.";

        var domains = PlanParser.ParseRequiredDomains(planJson);

        domains.Should().HaveCount(3, "unparseable plan should fail safe to ALL domains");
    }

    [Fact]
    public void ShouldFallBackToAllDomainsOnNullDomains()
    {
        var planJson = """{"requiredDomains": null}""";

        var domains = PlanParser.ParseRequiredDomains(planJson);

        domains.Should().HaveCount(3, "null requiredDomains should fail safe to ALL domains");
    }

    [Fact]
    public void ShouldFallBackToAllDomainsOnAllUnrecognizedDomains()
    {
        var planJson = """{"requiredDomains": ["Insurance", "Environmental", "Zoning"]}""";

        var domains = PlanParser.ParseRequiredDomains(planJson);

        domains.Should().HaveCount(3, "all-unrecognized domains should fail safe to ALL domains");
    }

    [Fact]
    public void ShouldHandleJsonEmbeddedInLlmNarrative()
    {
        var planJson = """
            Here is the verification plan for ASSET-TX-001:

            ```json
            {
                "assetId": "ASSET-TX-001",
                "requiredDomains": ["Legal", "Occupancy"],
                "relevantPolicies": [],
                "assetProfileSummary": "Texas foreclosure",
                "planRationale": "Valuation already done"
            }
            ```

            This plan focuses on legal and occupancy verification.
            """;

        var domains = PlanParser.ParseRequiredDomains(planJson);

        domains.Should().HaveCount(2);
        domains.Should().Contain(VerificationDomain.Legal);
        domains.Should().Contain(VerificationDomain.Occupancy);
        domains.Should().NotContain(VerificationDomain.Valuation);
    }

    [Fact]
    public void ShouldHandleEmptyString()
    {
        var domains = PlanParser.ParseRequiredDomains("");

        domains.Should().HaveCount(3, "empty string should fail safe to ALL domains");
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// 2. CountActualToolCalls — counts FunctionCallContent, not string heuristic
// ─────────────────────────────────────────────────────────────────────────────

public class CountActualToolCallsTests
{
    [Fact]
    public void ShouldCountZeroForPlainTextResponse()
    {
        var response = new ChatResponse([new ChatMessage(ChatRole.Assistant, "Just a text answer")]);

        PlanParser.CountActualToolCalls(response).Should().Be(0);
    }

    [Fact]
    public void ShouldCountFunctionCallContentItems()
    {
        var msg = new ChatMessage(ChatRole.Assistant,
        [
            new FunctionCallContent("call1", "SearchTitle", new Dictionary<string, object?> { ["parcelId"] = "TX-123" }),
            new FunctionCallContent("call2", "CheckHOADelinquency", new Dictionary<string, object?> { ["address"] = "123 Main" }),
        ]);

        var response = new ChatResponse([msg]);

        PlanParser.CountActualToolCalls(response).Should().Be(2);
    }

    [Fact]
    public void ShouldCountAcrossMultipleMessages()
    {
        var msg1 = new ChatMessage(ChatRole.Assistant,
            [new FunctionCallContent("call1", "SearchTitle", new Dictionary<string, object?> { ["id"] = "1" })]);
        var msg2 = new ChatMessage(ChatRole.Assistant,
            [new FunctionCallContent("call2", "GetAVM", new Dictionary<string, object?> { ["id"] = "2" })]);
        var msg3 = new ChatMessage(ChatRole.Assistant, "Final text answer with SearchTitle mentioned");

        var response = new ChatResponse([msg1, msg2, msg3]);

        PlanParser.CountActualToolCalls(response).Should().Be(2,
            "should count actual FunctionCallContent, not string mentions");
    }

    [Fact]
    public void ShouldHandleNullMessages()
    {
        var response = new ChatResponse([]);

        PlanParser.CountActualToolCalls(response).Should().Be(0);
    }
}
