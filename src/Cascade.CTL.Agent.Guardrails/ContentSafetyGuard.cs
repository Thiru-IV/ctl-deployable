using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Azure.AI.ContentSafety;
using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cascade.CTL.Agent.Guardrails;

public sealed class ContentSafetyOptions
{
    public string? Endpoint { get; set; }
    public bool Enabled { get; set; }
    public bool PromptShieldsEnabled { get; set; } = true;
    public string? TenantId { get; set; }
    public int TimeoutSeconds { get; set; } = 10;
    public int CircuitBreakerThreshold { get; set; } = 5;
    public int CircuitBreakerDurationSeconds { get; set; } = 60;
}

public sealed class ContentSafetyGuard
{
    private readonly ILogger<ContentSafetyGuard> _logger;
    private readonly LocalPromptInjectionDetector _localDetector;
    private readonly IContentSafetyClientWrapper? _client;
    private readonly HttpClient? _promptShieldHttpClient;
    private readonly TokenCredential? _credential;
    private readonly string? _promptShieldEndpoint;
    private readonly bool _isAzureEnabled;
    private readonly bool _promptShieldsEnabled;
    private readonly ContentSafetyOptions _options;

    // Circuit breaker state (thread-safe)
    private int _consecutiveFailures;
    private long _circuitOpenedAtTicks;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public ContentSafetyGuard(
        ILogger<ContentSafetyGuard> logger,
        LocalPromptInjectionDetector localDetector,
        IOptions<ContentSafetyOptions> options)
        : this(logger, localDetector, options, httpClient: null, credential: null, contentSafetyClient: null) { }

    /// <summary>
    /// Constructor with injectable dependencies for unit testing.
    /// Pass contentSafetyClient to mock the sealed ContentSafetyClient via interface.
    /// </summary>
    internal ContentSafetyGuard(
        ILogger<ContentSafetyGuard> logger,
        LocalPromptInjectionDetector localDetector,
        IOptions<ContentSafetyOptions> options,
        HttpClient? httpClient,
        TokenCredential? credential,
        IContentSafetyClientWrapper? contentSafetyClient)
    {
        _logger = logger;
        _localDetector = localDetector;
        _options = options.Value;

        if (_options.Enabled && !string.IsNullOrEmpty(_options.Endpoint))
        {
            var defaultCredential = credential ?? new DefaultAzureCredential(new DefaultAzureCredentialOptions
            {
                TenantId = _options.TenantId,
                ExcludeVisualStudioCredential = true
            });

            _client = contentSafetyClient ?? new ContentSafetyClientWrapper(
                new ContentSafetyClient(new Uri(_options.Endpoint), defaultCredential));
            _isAzureEnabled = true;

            // Prompt Shields uses REST since SDK v1.0.0 doesn't include ShieldPromptAsync
            if (_options.PromptShieldsEnabled)
            {
                _promptShieldHttpClient = httpClient ?? new HttpClient();
                _credential = defaultCredential;
                _promptShieldEndpoint = $"{_options.Endpoint.TrimEnd('/')}/contentsafety/text:shieldPrompt?api-version=2024-09-01";
                _promptShieldsEnabled = true;
                _logger.LogInformation("Azure Prompt Shields enabled at {Endpoint}", _promptShieldEndpoint);
            }

            _logger.LogInformation("Azure AI Content Safety enabled at {Endpoint}", _options.Endpoint);
        }
        else
        {
            _isAzureEnabled = false;
            _logger.LogInformation("Azure AI Content Safety not configured — using local prompt injection detector");
        }
    }

    public async Task<GuardResult> ScreenInputAsync(string text, CancellationToken cancellationToken = default)
    {
        var localResult = _localDetector.Detect(text);
        if (!localResult.IsAllowed)
            return localResult;

        return await RunAzureScreeningAsync(
            userPrompt: text, documents: null,
            includeContentModeration: true, cancellationToken);
    }

    /// <summary>
    /// Screens tool output for indirect prompt injection.
    /// Passes tool text as a "document" to Prompt Shields, which detects
    /// injection payloads embedded in external data (e.g., title search results).
    /// </summary>
    public async Task<GuardResult> ScreenToolResultAsync(string toolResult, CancellationToken cancellationToken = default)
    {
        var localResult = _localDetector.Detect(toolResult);
        if (!localResult.IsAllowed)
            return localResult;

        return await RunAzureScreeningAsync(
            userPrompt: null, documents: [toolResult],
            includeContentModeration: false, cancellationToken);
    }

    private async Task<GuardResult> RunAzureScreeningAsync(
        string? userPrompt, string[]? documents,
        bool includeContentModeration, CancellationToken cancellationToken)
    {
        if (!_isAzureEnabled || _client == null)
            return GuardResult.Pass();

        if (IsCircuitOpen())
        {
            _logger.LogWarning("Content Safety circuit breaker OPEN — falling back to local detection only");
            return GuardResult.PassDegraded("Azure Content Safety circuit breaker open — local regex detection only");
        }

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(_options.TimeoutSeconds));

            if (_promptShieldsEnabled)//azure ML service for injection detection on both user prompt and tool output (documents)
            {
                var shieldResult = await CallPromptShieldsAsync(userPrompt, documents, timeoutCts.Token);
                if (!shieldResult.IsAllowed)
                {
                    Interlocked.Exchange(ref _consecutiveFailures, 0);
                    return shieldResult;
                }
            }

            if (includeContentModeration && userPrompt != null) //hate/violence/self-harm screening on user input
            {
                // Azure Content Safety AnalyzeText API has a 10,000 character limit.
                // Chunk the input and screen EVERY chunk — if harmful content is in ANY chunk,
                // it must be caught. Truncation would miss content after the limit.
                var moderationResult = await AnalyzeTextInChunksAsync(userPrompt, timeoutCts.Token);

                Interlocked.Exchange(ref _consecutiveFailures, 0);

                if (moderationResult != null)
                    return moderationResult;
            }
            else
            {
                Interlocked.Exchange(ref _consecutiveFailures, 0);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var failures = Interlocked.Increment(ref _consecutiveFailures);
            if (failures >= _options.CircuitBreakerThreshold)
            {
                Interlocked.Exchange(ref _circuitOpenedAtTicks, DateTime.UtcNow.Ticks);
                _logger.LogWarning(ex,
                    "Azure Content Safety call failed ({Failures} consecutive) — circuit breaker OPENED for {Duration}s",
                    failures, _options.CircuitBreakerDurationSeconds);
            }
            else
            {
                _logger.LogWarning(ex,
                    "Azure Content Safety call failed ({Failures}/{Threshold}) — falling back to local detection",
                    failures, _options.CircuitBreakerThreshold);
            }

            return GuardResult.PassDegraded($"Azure Content Safety unavailable ({failures} consecutive failures) — local regex detection only");
        }

        return GuardResult.Pass();
    }

    /// <summary>
    /// Analyzes text for content moderation (hate/violence/self-harm) in chunks.
    /// The Azure AnalyzeText API has a 10,000 character limit per request.
    /// Every chunk is screened — returns Block/Flag on first violation found, or null if all pass.
    /// </summary>
    internal async Task<GuardResult?> AnalyzeTextInChunksAsync(string text, CancellationToken cancellationToken)
    {
        const int maxChunkLength = 10000;

        if (text.Length <= maxChunkLength)
        {
            // Single call — fits within the limit
            return await AnalyzeSingleChunkAsync(text, cancellationToken);
        }

        // Split into chunks and screen each one
        var chunkCount = (int)Math.Ceiling((double)text.Length / maxChunkLength);
        _logger.LogDebug(
            "Content moderation: input is {Length} chars — splitting into {ChunkCount} chunks of up to {Limit} chars",
            text.Length, chunkCount, maxChunkLength);

        GuardResult? worstFlag = null;

        for (int i = 0; i < text.Length; i += maxChunkLength)
        {
            var chunk = text.Substring(i, Math.Min(maxChunkLength, text.Length - i));
            var result = await AnalyzeSingleChunkAsync(chunk, cancellationToken);

            if (result != null)
            {
                // Block immediately — no need to check remaining chunks
                if (result.Action == "Block")
                    return result;

                // Track the worst Flag (only return it if no Block found in any chunk)
                worstFlag ??= result;
            }
        }

        return worstFlag;
    }

    private async Task<GuardResult?> AnalyzeSingleChunkAsync(string text, CancellationToken cancellationToken)
    {
        var request = new AnalyzeTextOptions(text);
        var response = await _client!.AnalyzeTextAsync(request, cancellationToken);

        foreach (var category in response.Categories)
        {
            if (category.Severity >= 4)
            {
                return GuardResult.Block(
                    $"Content safety violation: {category.Category} severity {category.Severity}",
                    [category.Category]);
            }
            else if (category.Severity >= 2)
            {
                return GuardResult.Flag(
                    $"Content safety flag: {category.Category} severity {category.Severity}",
                    [category.Category]);
            }
        }

        return null;
    }

    /// <summary>
    /// Calls Azure AI Content Safety Prompt Shields REST API.
    /// Detects both direct injection (in userPrompt) and indirect injection (in documents from tool outputs).
    /// Prompt Shields has a 10,000 character limit per field — large inputs are chunked into
    /// multiple document entries to stay within the limit.
    /// </summary>
    internal async Task<GuardResult> CallPromptShieldsAsync(
        string? userPrompt, string[]? documents, CancellationToken cancellationToken)
    {
        if (_promptShieldHttpClient == null || _credential == null || _promptShieldEndpoint == null)
            return GuardResult.Pass();

        var token = await _credential.GetTokenAsync(
            new TokenRequestContext(["https://cognitiveservices.azure.com/.default"]),
            cancellationToken);

        // Prompt Shields API limit: 10,000 characters per field
        const int maxFieldLength = 10000;

        var safeUserPrompt = userPrompt ?? "";
        if (safeUserPrompt.Length > maxFieldLength)
        {
            _logger.LogDebug("Prompt Shields: userPrompt is {Length} chars — truncating to {Limit}",
                safeUserPrompt.Length, maxFieldLength);
            safeUserPrompt = safeUserPrompt[..maxFieldLength];
        }

        // Chunk documents that exceed the limit into multiple entries
        string[]? safeDocuments = null;
        if (documents != null)
        {
            var chunked = new List<string>();
            foreach (var doc in documents)
            {
                if (doc.Length <= maxFieldLength)
                {
                    chunked.Add(doc);
                }
                else
                {
                    _logger.LogDebug("Prompt Shields: document is {Length} chars — splitting into chunks of {Limit}",
                        doc.Length, maxFieldLength);
                    for (int offset = 0; offset < doc.Length; offset += maxFieldLength)
                    {
                        var chunkLen = Math.Min(maxFieldLength, doc.Length - offset);
                        chunked.Add(doc.Substring(offset, chunkLen));
                    }
                }
            }
            safeDocuments = chunked.ToArray();
        }

        var payload = new PromptShieldRequest { UserPrompt = safeUserPrompt, Documents = safeDocuments };
        var json = JsonSerializer.Serialize(payload, JsonOptions);

        using var request = new HttpRequestMessage(HttpMethod.Post, _promptShieldEndpoint);
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);

        var response = await _promptShieldHttpClient.SendAsync(request, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogWarning(
                "Prompt Shields returned {StatusCode}: {Body} | Request payload: {Payload}",
                (int)response.StatusCode, errorBody, json);
            throw new HttpRequestException(
                $"Response status code does not indicate success: {(int)response.StatusCode} ({response.ReasonPhrase}). Body: {errorBody}");
        }

        var responseJson = await response.Content.ReadAsStringAsync(cancellationToken);
        var result = JsonSerializer.Deserialize<PromptShieldResponse>(responseJson, JsonOptions);

        if (result?.UserPromptAnalysis?.AttackDetected == true)
        {
            _logger.LogWarning("Prompt Shields detected direct injection attack");
            return GuardResult.Block(
                "Prompt injection detected by Azure Prompt Shields (direct attack)",
                ["PromptShields:UserPrompt"]);
        }

        if (result?.DocumentsAnalysis != null)
        {
            for (int i = 0; i < result.DocumentsAnalysis.Length; i++)
            {
                if (result.DocumentsAnalysis[i].AttackDetected == true)
                {
                    _logger.LogWarning("Prompt Shields detected indirect injection in document {Index}", i);
                    return GuardResult.Block(
                        $"Indirect prompt injection detected by Azure Prompt Shields in tool output (document {i})",
                        [$"PromptShields:Document:{i}"]);
                }
            }
        }

        return GuardResult.Pass();
    }

    private bool IsCircuitOpen()
    {
        var failures = Volatile.Read(ref _consecutiveFailures);
        if (failures < _options.CircuitBreakerThreshold)
            return false;

        var openedAt = new DateTime(Volatile.Read(ref _circuitOpenedAtTicks), DateTimeKind.Utc);
        var elapsed = DateTime.UtcNow - openedAt;
        if (elapsed.TotalSeconds >= _options.CircuitBreakerDurationSeconds)
        {
            // Half-open: reset and allow one probe
            Interlocked.Exchange(ref _consecutiveFailures, 0);
            _logger.LogInformation("Content Safety circuit breaker reset (half-open probe)");
            return false;
        }

        return true;
    }

    // ──────────────────────────────────────────────────────────────────
    // Prompt Shields REST API DTOs
    // ──────────────────────────────────────────────────────────────────

    internal sealed record PromptShieldRequest
    {
        public string? UserPrompt { get; init; }
        public string[]? Documents { get; init; }
    }

    internal sealed record PromptShieldAnalysis
    {
        public bool AttackDetected { get; init; }
    }

    internal sealed record PromptShieldResponse
    {
        public PromptShieldAnalysis? UserPromptAnalysis { get; init; }
        public PromptShieldAnalysis[]? DocumentsAnalysis { get; init; }
    }
}
