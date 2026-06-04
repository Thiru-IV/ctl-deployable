using Cascade.CTL.Agent.Infrastructure.RAG.Indexing;
using FluentAssertions;
using Xunit;

namespace Cascade.CTL.Agent.Tests.RAG;

public class SearchIndexSchemaTests
{
    [Fact]
    public void BuildIndex_DeclaresSemanticConfigurationForReranking()
    {
        var index = SearchIndexSchema.BuildIndex("ctl-policy-knowledge", vectorDimensions: 1536);

        index.SemanticSearch.Should().NotBeNull("the L2 semantic reranker requires a semantic configuration on the index");
        index.SemanticSearch!.Configurations.Should().ContainSingle()
            .Which.Name.Should().Be(SearchIndexSchema.SemanticConfigurationName);

        var config = index.SemanticSearch.Configurations.Single();
        config.PrioritizedFields.TitleField!.FieldName.Should().Be("title");
        config.PrioritizedFields.ContentFields.Should().ContainSingle()
            .Which.FieldName.Should().Be("content");
    }

    [Fact]
    public void BuildIndex_RetainsHybridRetrievalArtifacts()
    {
        var index = SearchIndexSchema.BuildIndex("ctl-policy-knowledge", vectorDimensions: 1536);

        // Vector ANN profile must still be present alongside the new semantic configuration.
        index.VectorSearch.Should().NotBeNull();
        index.VectorSearch!.Profiles.Should().ContainSingle()
            .Which.Name.Should().Be(SearchIndexSchema.VectorSearchProfileName);
        index.Fields.Should().Contain(f => f.Name == "contentVector");
    }
}
