using Cascade.CTL.Agent.Domain.Models;
using Cascade.CTL.Agent.Infrastructure.RAG.Indexing;
using FluentAssertions;
using Xunit;

namespace Cascade.CTL.Agent.Tests.RAG;

public class PolicyDocumentChunkerTests
{
    private static RAGDocument MakeDoc(string id, string content, string? state = "ALL") => new()
    {
        Id = id,
        Title = $"Title for {id}",
        Content = content,
        RelevanceScore = 0,
        State = state,
        County = "ALL",
        AssetType = "ALL",
        PolicyType = "Test",
    };

    [Fact]
    public void Chunk_ShortDocument_ReturnsSingleChunk()
    {
        var doc = MakeDoc("P-1", "Short policy content.");

        var chunks = PolicyDocumentChunker.Chunk(doc);

        chunks.Should().HaveCount(1);
        chunks[0].ParentId.Should().Be("P-1");
        chunks[0].ChunkIndex.Should().Be(0);
        chunks[0].ChunkId.Should().Be("P-1__c000");
        chunks[0].Content.Should().Be("Short policy content.");
        chunks[0].Title.Should().Be("Title for P-1");
        chunks[0].State.Should().Be("ALL");
    }

    [Fact]
    public void Chunk_RespectsMaxCharsPerChunk()
    {
        // Build 10 paragraphs of ~300 chars each = ~3000 chars content
        var para = new string('x', 290);
        var content = string.Join("\n\n", Enumerable.Repeat(para, 10));
        var doc = MakeDoc("P-long", content);

        var opts = new ChunkingOptions { MaxCharsPerChunk = 800, OverlapChars = 50, MinCharsToChunk = 100 };
        var chunks = PolicyDocumentChunker.Chunk(doc, opts);

        chunks.Should().HaveCountGreaterThan(1);
        foreach (var c in chunks)
            c.Content.Length.Should().BeLessThanOrEqualTo(opts.MaxCharsPerChunk + opts.OverlapChars + 10,
                because: "chunks may carry overlap prefix but shouldn't significantly exceed the cap");
    }

    [Fact]
    public void Chunk_PreservesDocumentMetadata()
    {
        var para = new string('y', 1000);
        var content = string.Join("\n\n", para, para, para);
        var doc = new RAGDocument
        {
            Id = "TX-001",
            Title = "Texas policy",
            Content = content,
            RelevanceScore = 0,
            State = "TX",
            County = "Dallas",
            AssetType = "Foreclosure",
            PolicyType = "CTL-State",
        };

        var chunks = PolicyDocumentChunker.Chunk(doc, new ChunkingOptions { MaxCharsPerChunk = 1200, OverlapChars = 0, MinCharsToChunk = 500 });

        chunks.Should().AllSatisfy(c =>
        {
            c.ParentId.Should().Be("TX-001");
            c.Title.Should().Be("Texas policy");
            c.State.Should().Be("TX");
            c.County.Should().Be("Dallas");
            c.AssetType.Should().Be("Foreclosure");
            c.PolicyType.Should().Be("CTL-State");
        });
        chunks.Select(c => c.ChunkIndex).Should().BeInAscendingOrder();
        chunks.Select(c => c.ChunkIndex).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void Chunk_GeneratesStableZeroPaddedChunkIds()
    {
        var para = new string('z', 800);
        var content = string.Join("\n\n", Enumerable.Repeat(para, 15));
        var doc = MakeDoc("MANY", content);

        var chunks = PolicyDocumentChunker.Chunk(doc, new ChunkingOptions
        {
            MaxCharsPerChunk = 900, OverlapChars = 0, MinCharsToChunk = 100
        });

        chunks.First().ChunkId.Should().StartWith("MANY__c000");
        chunks.All(c => c.ChunkId.StartsWith("MANY__c")).Should().BeTrue();
        chunks.All(c => c.ChunkId.Length == "MANY__c000".Length).Should().BeTrue(
            because: "chunk index is zero-padded to 3 digits");
    }

    [Fact]
    public void Chunk_InvalidOptions_Throws()
    {
        var doc = MakeDoc("X", "content");
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            PolicyDocumentChunker.Chunk(doc, new ChunkingOptions { MaxCharsPerChunk = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            PolicyDocumentChunker.Chunk(doc, new ChunkingOptions { MaxCharsPerChunk = 100, OverlapChars = 100 }));
    }

    [Fact]
    public void Chunk_OverlapCarriesContextBetweenChunks()
    {
        // Three distinct paragraphs joined by blank lines, each large enough to fill a chunk.
        var p1 = "PARA_ONE " + new string('a', 500);
        var p2 = "PARA_TWO " + new string('b', 500);
        var p3 = "PARA_THREE " + new string('c', 500);
        var content = $"{p1}\n\n{p2}\n\n{p3}";
        var doc = MakeDoc("OVER", content);

        var chunks = PolicyDocumentChunker.Chunk(doc, new ChunkingOptions
        {
            MaxCharsPerChunk = 600, OverlapChars = 80, MinCharsToChunk = 100
        });

        chunks.Should().HaveCountGreaterThan(1);
        // Each chunk after the first should begin with overlap from the previous chunk's tail.
        for (var i = 1; i < chunks.Count; i++)
        {
            var prevTail = chunks[i - 1].Content[^80..];
            chunks[i].Content.Should().StartWith(prevTail);
        }
    }
}
