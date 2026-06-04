using Cascade.CTL.Agent.Infrastructure.Teams;
using FluentAssertions;
using Microsoft.Bot.Schema;
using Xunit;

namespace Cascade.CTL.Agent.Tests.Teams;

public class InMemoryConversationReferenceStoreTests
{
    private static ConversationReference Ref(string convId) => new()
    {
        Conversation = new ConversationAccount { Id = convId },
        ChannelId = "msteams"
    };

    [Fact]
    public void Save_ThenGet_ReturnsSameReference()
    {
        var sut = new InMemoryConversationReferenceStore();
        var r = Ref("c1");
        sut.Save("alice@contoso.com", r);

        sut.Get("alice@contoso.com").Should().BeSameAs(r);
    }

    [Fact]
    public void Get_IsCaseInsensitive()
    {
        var sut = new InMemoryConversationReferenceStore();
        var r = Ref("c1");
        sut.Save("Alice@Contoso.com", r);

        sut.Get("alice@contoso.com").Should().BeSameAs(r);
    }

    [Fact]
    public void Get_ReturnsNull_WhenKeyUnknown()
    {
        var sut = new InMemoryConversationReferenceStore();
        sut.Get("nobody").Should().BeNull();
    }

    [Fact]
    public void Save_OverwritesExistingKey()
    {
        var sut = new InMemoryConversationReferenceStore();
        sut.Save("alice", Ref("c1"));
        var newer = Ref("c2");
        sut.Save("alice", newer);

        sut.Get("alice").Should().BeSameAs(newer);
    }

    [Fact]
    public void All_EnumeratesEveryStoredReference()
    {
        var sut = new InMemoryConversationReferenceStore();
        sut.Save("alice", Ref("c1"));
        sut.Save("bob", Ref("c2"));

        sut.All().Should().HaveCount(2);
    }
}
