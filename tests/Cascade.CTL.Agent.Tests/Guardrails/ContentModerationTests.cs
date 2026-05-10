using Azure.AI.ContentSafety;
using Cascade.CTL.Agent.Guardrails;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace Cascade.CTL.Agent.Tests.Guardrails;

public class ContentModerationTests
{
    private readonly ILogger<ContentSafetyGuard> _logger = Substitute.For<ILogger<ContentSafetyGuard>>();
    private readonly LocalPromptInjectionDetector _localDetector;

    public ContentModerationTests()
    {
        _localDetector = new LocalPromptInjectionDetector(Substitute.For<ILogger<LocalPromptInjectionDetector>>());
    }

    // ──────────────────────────────────────────────────────────────────
    // AnalyzeText chunking: text under 10K = single call
    // ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ScreenInput_ShouldMakeSingleCall_WhenTextUnder10K()
    {
        var mockClient = Substitute.For<IContentSafetyClientWrapper>();
        mockClient.AnalyzeTextAsync(Arg.Any<AnalyzeTextOptions>(), Arg.Any<CancellationToken>())
            .Returns(CreateSafeResult());

        var guard = CreateGuard(mockClient);

        await guard.ScreenInputAsync("Short safe text", CancellationToken.None);

        await mockClient.Received(1).AnalyzeTextAsync(Arg.Any<AnalyzeTextOptions>(), Arg.Any<CancellationToken>());
    }

    // ──────────────────────────────────────────────────────────────────
    // AnalyzeText chunking: text over 10K = multiple calls
    // ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ScreenInput_ShouldChunkLargeText_WhenOver10K()
    {
        var mockClient = Substitute.For<IContentSafetyClientWrapper>();
        mockClient.AnalyzeTextAsync(Arg.Any<AnalyzeTextOptions>(), Arg.Any<CancellationToken>())
            .Returns(CreateSafeResult());

        var guard = CreateGuard(mockClient);

        // 15,000 chars → should be 2 chunks (10,000 + 5,000)
        var largeText = new string('A', 15_000);
        await guard.ScreenInputAsync(largeText, CancellationToken.None);

        await mockClient.Received(2).AnalyzeTextAsync(Arg.Any<AnalyzeTextOptions>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ScreenInput_ShouldChunk25KInto3Calls()
    {
        var mockClient = Substitute.For<IContentSafetyClientWrapper>();
        mockClient.AnalyzeTextAsync(Arg.Any<AnalyzeTextOptions>(), Arg.Any<CancellationToken>())
            .Returns(CreateSafeResult());

        var guard = CreateGuard(mockClient);

        // 25,000 chars → 3 chunks (10K + 10K + 5K)
        var largeText = new string('B', 25_000);
        await guard.ScreenInputAsync(largeText, CancellationToken.None);

        await mockClient.Received(3).AnalyzeTextAsync(Arg.Any<AnalyzeTextOptions>(), Arg.Any<CancellationToken>());
    }

    // ──────────────────────────────────────────────────────────────────
    // Key safety test: harmful content in LATER chunk must be caught
    // ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ScreenInput_ShouldBlockOnSecondChunk_WhenHarmfulContentAfter10K()
    {
        var callCount = 0;
        var mockClient = Substitute.For<IContentSafetyClientWrapper>();
        mockClient.AnalyzeTextAsync(Arg.Any<AnalyzeTextOptions>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                callCount++;
                // First chunk: safe. Second chunk: harmful (severity 4 = Block)
                return callCount == 1 ? CreateSafeResult() : CreateHarmfulResult(severity: 4);
            });

        var guard = CreateGuard(mockClient);

        // 15K text → 2 chunks; harmful content detected in chunk 2
        var largeText = new string('C', 15_000);
        var result = await guard.ScreenInputAsync(largeText, CancellationToken.None);

        result.IsAllowed.Should().BeFalse("harmful content in the second chunk must be caught");
        result.Action.Should().Be("Block");
    }

    [Fact]
    public async Task ScreenInput_ShouldFlagOnSecondChunk_WhenModerateContentAfter10K()
    {
        var callCount = 0;
        var mockClient = Substitute.For<IContentSafetyClientWrapper>();
        mockClient.AnalyzeTextAsync(Arg.Any<AnalyzeTextOptions>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                callCount++;
                // First chunk: safe. Second chunk: flagged (severity 2)
                return callCount == 1 ? CreateSafeResult() : CreateHarmfulResult(severity: 2);
            });

        var guard = CreateGuard(mockClient);

        var largeText = new string('D', 15_000);
        var result = await guard.ScreenInputAsync(largeText, CancellationToken.None);

        result.IsAllowed.Should().BeTrue("severity 2 flags but doesn't block");
        result.Action.Should().Be("Flag");
    }

    // ──────────────────────────────────────────────────────────────────
    // Block in chunk 1 should short-circuit (skip remaining chunks)
    // ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ScreenInput_ShouldShortCircuitOnBlock_SkippingRemainingChunks()
    {
        var mockClient = Substitute.For<IContentSafetyClientWrapper>();
        mockClient.AnalyzeTextAsync(Arg.Any<AnalyzeTextOptions>(), Arg.Any<CancellationToken>())
            .Returns(CreateHarmfulResult(severity: 4)); // Block on first chunk

        var guard = CreateGuard(mockClient);

        var largeText = new string('E', 25_000); // Would be 3 chunks
        var result = await guard.ScreenInputAsync(largeText, CancellationToken.None);

        result.IsAllowed.Should().BeFalse();
        result.Action.Should().Be("Block");
        // Should only have called AnalyzeText once (short-circuited after Block)
        await mockClient.Received(1).AnalyzeTextAsync(Arg.Any<AnalyzeTextOptions>(), Arg.Any<CancellationToken>());
    }

    // ──────────────────────────────────────────────────────────────────
    // Degraded safety: Azure failure surfaces IsDegradedSafety flag
    // ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ScreenInput_ShouldReturnDegradedFlag_WhenAzureThrows()
    {
        var mockClient = Substitute.For<IContentSafetyClientWrapper>();
        mockClient.AnalyzeTextAsync(Arg.Any<AnalyzeTextOptions>(), Arg.Any<CancellationToken>())
            .Throws(new HttpRequestException("Azure is down"));

        var guard = CreateGuard(mockClient);

        var result = await guard.ScreenInputAsync("Safe text", CancellationToken.None);

        result.IsAllowed.Should().BeTrue("should pass via local detection fallback");
        result.IsDegradedSafety.Should().BeTrue("Azure ML screening failed — must flag as degraded");
    }

    [Fact]
    public async Task ScreenInput_ShouldNotBeDegraded_WhenAzureSucceeds()
    {
        var mockClient = Substitute.For<IContentSafetyClientWrapper>();
        mockClient.AnalyzeTextAsync(Arg.Any<AnalyzeTextOptions>(), Arg.Any<CancellationToken>())
            .Returns(CreateSafeResult());

        var guard = CreateGuard(mockClient);

        var result = await guard.ScreenInputAsync("Safe text", CancellationToken.None);

        result.IsAllowed.Should().BeTrue();
        result.IsDegradedSafety.Should().BeFalse();
    }

    // ──────────────────────────────────────────────────────────────────
    // Helpers
    // ──────────────────────────────────────────────────────────────────

    private ContentSafetyGuard CreateGuard(IContentSafetyClientWrapper mockClient)
    {
        var options = Options.Create(new ContentSafetyOptions
        {
            Enabled = true,
            Endpoint = "https://fake-endpoint.cognitiveservices.azure.com/",
            PromptShieldsEnabled = false, // isolate AnalyzeText testing
            TimeoutSeconds = 10,
            CircuitBreakerThreshold = 5,
            CircuitBreakerDurationSeconds = 60
        });

        return new ContentSafetyGuard(
            _logger, _localDetector, options,
            httpClient: null, credential: null,
            contentSafetyClient: mockClient);
    }

    private static ContentModerationResult CreateSafeResult() => new()
    {
        Categories =
        [
            new() { Category = "Hate", Severity = 0 },
            new() { Category = "Violence", Severity = 0 },
            new() { Category = "SelfHarm", Severity = 0 },
            new() { Category = "Sexual", Severity = 0 }
        ]
    };

    private static ContentModerationResult CreateHarmfulResult(int severity) => new()
    {
        Categories =
        [
            new() { Category = "Hate", Severity = severity },
            new() { Category = "Violence", Severity = 0 },
            new() { Category = "SelfHarm", Severity = 0 },
            new() { Category = "Sexual", Severity = 0 }
        ]
    };
}
