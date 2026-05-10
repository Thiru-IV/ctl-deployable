using Cascade.CTL.AssetService;
using FluentAssertions;
using Xunit;

namespace Cascade.CTL.Agent.Tests.AssetDomainApi;

public sealed class ApiKeyMiddlewareFixedTimeEqualsTests
{
    [Fact]
    public void FixedTimeEquals_IdenticalStrings_ReturnsTrue()
    {
        ApiKeyAuthenticationMiddleware
            .FixedTimeEquals("abc-123", "abc-123")
            .Should().BeTrue();
    }

    [Fact]
    public void FixedTimeEquals_DifferentStrings_ReturnsFalse()
    {
        ApiKeyAuthenticationMiddleware
            .FixedTimeEquals("abc-123", "abc-124")
            .Should().BeFalse();
    }

    [Fact]
    public void FixedTimeEquals_DifferentLengths_ReturnsFalse()
    {
        ApiKeyAuthenticationMiddleware
            .FixedTimeEquals("abc", "abc-extra")
            .Should().BeFalse();
    }

    [Fact]
    public void FixedTimeEquals_EmptyStrings_ReturnsTrue()
    {
        ApiKeyAuthenticationMiddleware
            .FixedTimeEquals(string.Empty, string.Empty)
            .Should().BeTrue();
    }
}
