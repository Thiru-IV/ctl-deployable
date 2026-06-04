using AdaptiveCards;
using Cascade.CTL.Agent.Domain.Enums;
using Cascade.CTL.Agent.Domain.Models;
using Cascade.CTL.Agent.Infrastructure.Teams;
using FluentAssertions;
using Xunit;

namespace Cascade.CTL.Agent.Tests.Teams;

public class HitlInteractiveCardBuilderTests
{
    private static HumanReviewRequest Request() => new()
    {
        SessionId = "sess-001",
        AssetId = "ASSET-42",
        ProposedVerdict = new CTLVerdictDto
        {
            AssetId = "ASSET-42",
            SessionId = "sess-001",
            Verdict = CTLVerdict.NeedsHumanReview,
            ConfidenceScore = 0.42,
            Conditions = new[] { "title defect", "lien outstanding" },
            EvidenceTrail = new[] { "evidence A", "evidence B", "evidence C", "evidence D" },
            ReflectionLog = "log",
            Timestamp = DateTime.UtcNow
        },
        ReflectionOutput = "reflection text"
    };

    [Fact]
    public void Build_ReturnsAdaptiveCardAttachment()
    {
        var att = HitlInteractiveCardBuilder.Build(Request(), "https://cascade/{0}?s={1}");

        att.Should().NotBeNull();
        att.ContentType.Should().Be(AdaptiveCard.ContentType);
        att.Content.Should().BeOfType<AdaptiveCard>();
    }

    [Fact]
    public void Build_Card_ContainsAssetAndSessionFacts()
    {
        var att = HitlInteractiveCardBuilder.Build(Request(), "https://cascade/{0}?s={1}");
        var card = (AdaptiveCard)att.Content;

        var facts = card.Body.OfType<AdaptiveFactSet>().Single().Facts;
        facts.Should().Contain(f => f.Title == "Asset ID" && f.Value == "ASSET-42");
        facts.Should().Contain(f => f.Title == "Session" && f.Value == "sess-001");
        facts.Should().Contain(f => f.Title == "Proposed Verdict" && f.Value == nameof(CTLVerdict.NeedsHumanReview));
    }

    [Fact]
    public void Build_Card_Exposes_Confirm_Override_ReEvaluate_AndDeepLinkActions()
    {
        var att = HitlInteractiveCardBuilder.Build(Request(), "https://cascade.example/{0}?s={1}");
        var card = (AdaptiveCard)att.Content;

        var submitTitles = card.Actions.OfType<AdaptiveSubmitAction>().Select(a => a.Title).ToArray();
        submitTitles.Should().Contain(t => t.Contains("Confirm"));
        submitTitles.Should().Contain(t => t.Contains("Override"));
        submitTitles.Should().Contain(t => t.Contains("Re-evaluate"));

        var openUrl = card.Actions.OfType<AdaptiveOpenUrlAction>().Single();
        openUrl.Url.ToString().Should().Be("https://cascade.example/ASSET-42?s=sess-001");
    }

    [Fact]
    public void Build_Card_TruncatesEvidenceTrailToTopThreeBullets()
    {
        var att = HitlInteractiveCardBuilder.Build(Request(), "https://cascade/{0}?s={1}");
        var card = (AdaptiveCard)att.Content;

        // The 2nd text block after the heading + findings header is the bulleted findings list.
        var findingsBlock = card.Body.OfType<AdaptiveTextBlock>()
            .First(t => t.Text != null && t.Text.StartsWith("•"));

        findingsBlock.Text.Split('\n').Count(l => l.StartsWith("•")).Should().Be(3);
    }
}
