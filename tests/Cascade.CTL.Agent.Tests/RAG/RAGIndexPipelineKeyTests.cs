using Cascade.CTL.Agent.Infrastructure.RAG.Indexing;
using FluentAssertions;
using Xunit;

namespace Cascade.CTL.Agent.Tests.RAG;

public class RAGIndexPipelineKeyTests
{
    [Theory]
    [InlineData("POLICY-001__c000", "POLICY-001__c000")]
    [InlineData("POLICY-001#frag", "POLICY-001_frag")]
    [InlineData("ID with spaces!", "ID_with_spaces_")]
    [InlineData("abc.def/ghi", "abc_def_ghi")]
    [InlineData("OK=1", "OK=1")]
    public void SanitizeKey_ReplacesDisallowedCharacters(string raw, string expected)
    {
        RAGIndexPipeline.SanitizeKey(raw).Should().Be(expected);
    }
}
