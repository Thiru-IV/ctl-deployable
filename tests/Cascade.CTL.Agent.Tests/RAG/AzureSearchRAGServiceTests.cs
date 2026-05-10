using Cascade.CTL.Agent.Infrastructure.RAG;
using Cascade.CTL.Agent.Infrastructure.RAG.Query;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace Cascade.CTL.Agent.Tests.RAG;

public class AzureSearchRAGServiceTests
{
    private static AzureSearchRAGService BuildService(
        IAzureSearchExecutor executor,
        IRAGEmbeddingGenerator embeddings,
        int topK = 5)
    {
        var options = Options.Create(new AzureSearchRAGOptions { TopK = topK, IndexName = "idx" });
        return new AzureSearchRAGService(executor, embeddings, options, NullLogger<AzureSearchRAGService>.Instance);
    }

    [Fact]
    public void BuildODataFilter_WithNoFilters_ReturnsNull()
    {
        AzureSearchRAGService.BuildODataFilter(null, null, null).Should().BeNull();
        AzureSearchRAGService.BuildODataFilter("", "", "").Should().BeNull();
    }

    [Fact]
    public void BuildODataFilter_IncludesALLTolerance()
    {
        var filter = AzureSearchRAGService.BuildODataFilter("TX", null, null);
        filter.Should().Contain("state eq 'TX'").And.Contain("state eq 'ALL'");
    }

    [Fact]
    public void BuildODataFilter_CombinesAllThree()
    {
        var filter = AzureSearchRAGService.BuildODataFilter("CA", "Los Angeles", "REO");
        filter.Should().Contain("state eq 'CA'");
        filter.Should().Contain("county eq 'Los Angeles'");
        filter.Should().Contain("assetType eq 'REO'");
        filter.Should().Contain(" and ");
    }

    [Fact]
    public void BuildODataFilter_EscapesSingleQuotes()
    {
        var filter = AzureSearchRAGService.BuildODataFilter("O'Brien", null, null);
        filter.Should().Contain("state eq 'O''Brien'");
    }

    [Fact]
    public async Task QueryAsync_EmbedsQueryAndPassesToExecutor()
    {
        var embeddings = Substitute.For<IRAGEmbeddingGenerator>();
        var vector = new float[] { 0.1f, 0.2f, 0.3f };
        embeddings.EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ReadOnlyMemory<float>(vector));

        var executor = Substitute.For<IAzureSearchExecutor>();
        executor.HybridSearchAsync(
                Arg.Any<string>(), Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<string?>(),
                Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<PolicySearchHit>());

        var service = BuildService(executor, embeddings, topK: 7);

        await service.QueryAsync("What is CTL baseline?", "TX", null, "REO");

        await embeddings.Received(1).EmbedAsync("What is CTL baseline?", Arg.Any<CancellationToken>());
        await executor.Received(1).HybridSearchAsync(
            "What is CTL baseline?",
            Arg.Is<ReadOnlyMemory<float>>(v => v.Length == 3),
            Arg.Is<string>(f => f != null && f.Contains("state eq 'TX'") && f.Contains("assetType eq 'REO'")),
            7,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task QueryAsync_MapsHitsToRAGDocuments()
    {
        var embeddings = Substitute.For<IRAGEmbeddingGenerator>();
        embeddings.EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ReadOnlyMemory<float>(new[] { 1f, 0f }));

        var executor = Substitute.For<IAzureSearchExecutor>();
        executor.HybridSearchAsync(
                Arg.Any<string>(), Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<string?>(),
                Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new PolicySearchHit[]
            {
                new(0.85, "DOC-1", 2, "Title One", "Body One", "TX", "Dallas", "Foreclosure", "CTL-State"),
                new(0.72, "DOC-2", 0, "Title Two", "Body Two", "ALL", "ALL", "ALL", "CTL-Baseline"),
            });

        var service = BuildService(executor, embeddings);

        var result = await service.QueryAsync("eviction timeline", "TX", null, null);

        result.Query.Should().Be("eviction timeline");
        result.Documents.Should().HaveCount(2);
        result.TotalMatches.Should().Be(2);

        result.Documents[0].Id.Should().Be("DOC-1#c2");
        result.Documents[0].Title.Should().Be("Title One");
        result.Documents[0].Content.Should().Be("Body One");
        result.Documents[0].RelevanceScore.Should().BeApproximately(0.85, 0.001);
        result.Documents[0].State.Should().Be("TX");
        result.Documents[0].County.Should().Be("Dallas");
        result.Documents[0].AssetType.Should().Be("Foreclosure");
        result.Documents[0].PolicyType.Should().Be("CTL-State");

        result.Documents[1].RelevanceScore.Should().BeApproximately(0.72, 0.001);
    }

    [Fact]
    public async Task QueryAsync_ClampsRelevanceScoreTo0To1()
    {
        var embeddings = Substitute.For<IRAGEmbeddingGenerator>();
        embeddings.EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ReadOnlyMemory<float>(new[] { 1f }));

        var executor = Substitute.For<IAzureSearchExecutor>();
        executor.HybridSearchAsync(
                Arg.Any<string>(), Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<string?>(),
                Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new PolicySearchHit[]
            {
                new(5.4, "A", 0, "t", "c", null, null, null, null),  // BM25 can exceed 1
                new(-0.1, "B", 0, "t", "c", null, null, null, null), // defensive lower bound
            });

        var service = BuildService(executor, embeddings);
        var result = await service.QueryAsync("q");

        result.Documents[0].RelevanceScore.Should().Be(1.0);
        result.Documents[1].RelevanceScore.Should().Be(0.0);
    }
}
