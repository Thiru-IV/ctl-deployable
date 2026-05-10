using System.Text.RegularExpressions;
using Azure;
using Azure.AI.TextAnalytics;
using Azure.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cascade.CTL.Agent.Guardrails;

public sealed class PiiFilterOptions
{
    public bool AzurePiiEnabled { get; set; }
    public string? Endpoint { get; set; }
    public string? TenantId { get; set; }
    public double MinConfidence { get; set; } = 0.8;
    public int TimeoutSeconds { get; set; } = 5;
}

public sealed class PiiFilter
{
    private readonly ILogger<PiiFilter> _logger;
    private readonly TextAnalyticsClient? _textAnalyticsClient;
    private readonly PiiFilterOptions _options;
    private readonly bool _isAzureEnabled;

    /// <summary>
    /// PII detection patterns (Tier 1 — fast, offline, zero-cost).
    /// <list type="table">
    ///   <listheader><term>Index</term><description>PII Type — Example</description></listheader>
    ///   <item><term>[0]</term><description>SSN (hyphenated) — 123-45-6789</description></item>
    ///   <item><term>[1]</term><description>SSN (9 contiguous digits) — 123456789</description></item>
    ///   <item><term>[2]</term><description>Credit/debit card number (16 digits, optional spaces/hyphens) — 4111 1111 1111 1111</description></item>
    ///   <item><term>[3]</term><description>Email address — user@example.com</description></item>
    ///   <item><term>[4]</term><description>US phone number (10 digits, optional +1 prefix, parens, hyphens) — (555) 123-4567</description></item>
    /// </list>
    /// All patterns use <see cref="RegexOptions.Compiled"/> with a 1-second timeout to prevent ReDoS.
    /// </summary>
    private static readonly Regex[] PiiPatterns =
    [
        new(@"\b\d{3}-\d{2}-\d{4}\b", RegexOptions.Compiled, TimeSpan.FromSeconds(1)),             // [0] SSN (hyphenated)
        new(@"\b\d{9}\b", RegexOptions.Compiled, TimeSpan.FromSeconds(1)),                          // [1] SSN (contiguous)
        new(@"\b\d{4}[\s-]?\d{4}[\s-]?\d{4}[\s-]?\d{4}\b", RegexOptions.Compiled, TimeSpan.FromSeconds(1)), // [2] Credit card
        new(@"\b[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}\b", RegexOptions.Compiled, TimeSpan.FromSeconds(1)), // [3] Email
        new(@"\b(?:\+?1[-.\s]?)?\(?\d{3}\)?[-.\s]?\d{3}[-.\s]?\d{4}\b", RegexOptions.Compiled, TimeSpan.FromSeconds(1)), // [4] US phone
    ];

    private static readonly string[] MaskReplacements =
    [
        "***-**-****",
        "*********",
        "****-****-****-****",
        "***@***.***",
        "(***) ***-****",
    ];

    public PiiFilter(ILogger<PiiFilter> logger, IOptions<PiiFilterOptions> options)
        : this(logger, options, client: null) { }

    /// <summary>
    /// Constructor with injectable TextAnalyticsClient for unit testing.
    /// </summary>
    internal PiiFilter(ILogger<PiiFilter> logger, IOptions<PiiFilterOptions> options, TextAnalyticsClient? client)
    {
        _logger = logger;
        _options = options.Value;

        if (_options.AzurePiiEnabled && !string.IsNullOrEmpty(_options.Endpoint))
        {
            _textAnalyticsClient = client ?? new TextAnalyticsClient(
                new Uri(_options.Endpoint),
                new DefaultAzureCredential(new DefaultAzureCredentialOptions
                {
                    TenantId = _options.TenantId,
                    ExcludeVisualStudioCredential = true
                }));
            _isAzureEnabled = true;
            _logger.LogInformation("Azure AI Language PII detection enabled at {Endpoint}", _options.Endpoint);
        }
        else
        {
            _isAzureEnabled = false;
            _logger.LogInformation("Azure AI Language PII not configured — using local regex PII masking only");
        }
    }

    /// <summary>
    /// Masks PII using a two-tier approach:
    /// Tier 1 (regex): fast, offline masking of SSN, credit card, email, phone.
    /// Tier 2 (Azure AI Language): ML-based detection of names, addresses, dates of birth,
    /// bank accounts, and other entity types that regex cannot reliably catch.
    /// </summary>
    public string MaskPii(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return input;

        var masked = input;
        int totalMasked = 0;

        // Tier 1: Local regex (fast, zero-cost)
        for (int i = 0; i < PiiPatterns.Length; i++)
        {
            try
            {
                var matches = PiiPatterns[i].Matches(masked);
                if (matches.Count > 0)
                {
                    totalMasked += matches.Count;
                    masked = PiiPatterns[i].Replace(masked, MaskReplacements[i]);
                }
            }
            catch (RegexMatchTimeoutException) //Regular Expression Denial of Service (ReDoS) defense.
            {
                _logger.LogWarning("Regex timeout during PII masking");
            }
        }

        if (totalMasked > 0)
        {
            _logger.LogDebug("PII Filter (Tier 1 regex) masked {Count} occurrences", totalMasked);
        }

        return masked;
    }

    /// <summary>
    /// Async PII masking with Azure AI Language (Tier 2).
    /// Called from GuardrailsMiddleware where async is available.
    /// Falls back to Tier 1 regex if Azure is unavailable.
    /// </summary>
    public async Task<string> MaskPiiAsync(string input, CancellationToken cancellationToken = default)
    {
        // Tier 1: always run regex first
        var masked = MaskPii(input);

        // Tier 2: Azure AI Language PII detection
        if (_isAzureEnabled && _textAnalyticsClient != null)
        {
            try
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(_options.TimeoutSeconds));

                masked = await ProcessPiiInChunksAsync(masked, timeoutCts.Token);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw; // Caller cancelled — propagate
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Azure AI Language PII detection failed — Tier 1 regex result retained");
            }
        }

        return masked;
    }

    /// <summary>
    /// Azure AI Language PII API has a 5,120 character limit per document.
    /// This method splits large inputs into chunks, processes each independently,
    /// and reassembles the masked result.
    /// </summary>
    private async Task<string> ProcessPiiInChunksAsync(string text, CancellationToken cancellationToken)
    {
        const int maxChunkSize = 5000; // Leave margin below 5,120 limit

        if (text.Length <= maxChunkSize)
        {
            return await MaskSingleChunkAsync(text, cancellationToken);
        }

        _logger.LogDebug("PII input is {Length} chars — splitting into chunks of {ChunkSize}", text.Length, maxChunkSize);

        var result = new System.Text.StringBuilder(text.Length);
        var offset = 0;

        while (offset < text.Length)
        {
            var remaining = text.Length - offset;
            var chunkLength = Math.Min(maxChunkSize, remaining);

            // Try to break at a sentence or line boundary to avoid splitting entities
            if (chunkLength < remaining)
            {
                var breakPoint = text.LastIndexOfAny(['\n', '.', '!', '?'], offset + chunkLength - 1, Math.Min(chunkLength, 500));
                if (breakPoint > offset)
                    chunkLength = breakPoint - offset + 1;
            }

            var chunk = text.Substring(offset, chunkLength);
            var maskedChunk = await MaskSingleChunkAsync(chunk, cancellationToken);
            result.Append(maskedChunk);

            offset += chunkLength;
        }

        return result.ToString();
    }

    /// <summary>
    /// Processes a single chunk (≤ 5,000 chars) through Azure AI Language PII detection.
    /// </summary>
    private async Task<string> MaskSingleChunkAsync(string chunk, CancellationToken cancellationToken)
    {
        var response = await _textAnalyticsClient!.RecognizePiiEntitiesAsync(
            chunk,
            language: "en",
            new RecognizePiiEntitiesOptions { CategoriesFilter = { PiiEntityCategory.All } },
            cancellationToken);

        var entities = response.Value;
        if (entities.Count > 0)
        {
            var result = new System.Text.StringBuilder(chunk);
            var sorted = entities
                .Where(e => e.ConfidenceScore >= _options.MinConfidence)
                .OrderByDescending(e => e.Offset)
                .ToList();

            foreach (var entity in sorted)
            {
                var mask = $"[{entity.Category}]";
                result.Remove(entity.Offset, entity.Length);
                result.Insert(entity.Offset, mask);
            }

            if (sorted.Count > 0)
            {
                _logger.LogDebug("PII Filter (Tier 2 Azure) masked {Count} entities in chunk: {Categories}",
                    sorted.Count,
                    string.Join(", ", sorted.Select(e => $"{e.Category}({e.ConfidenceScore:F2})")));
                return result.ToString();
            }
        }

        return chunk;
    }
}
