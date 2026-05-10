using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Cascade.CTL.Agent.Guardrails;

public sealed class LocalPromptInjectionDetector
{
    private readonly ILogger<LocalPromptInjectionDetector> _logger;

    /// <list type="table">
    /// <listheader><term>Attack Type</term><description>Caught?</description></listheader>
    /// <item><term>1.Direct instruction override ("ignore all previous instructions")</term><description>Yes</description></item>
    /// <item><term>2.Role manipulation ("you are now a hacker")</term><description>Yes</description></item>
    /// <item><term>3.Obfuscation / encoding ("ign0re previ0us instruct!ons")</term><description>No</description></item>
    /// <item><term>4.Unicode substitution (Cyrillic і instead of Latin i)</term><description>No</description></item>
    /// <item><term>5.Indirect injection (malicious text in tool responses)</term><description>No — only screens direct input</description></item>
    /// <item><term>6.Payload splitting (attack spread across multiple messages)</term><description>No</description></item>
    /// <item><term>7.Multi-language ("ignorez toutes les instructions précédentes")</term><description>No</description></item>
    /// <item><term>8.Prompt leaking ("repeat your system prompt")</term><description>No</description></item>
    /// <item><term>9.Few-shot manipulation (crafted example Q&amp;A to steer behavior)</term><description>No</description></item>
    /// <item><term>10.Markdown/code block injection</term><description>Partially</description></item>
    /// </list>
    private static readonly Regex[] InjectionPatterns =
    [
        new(@"ignore\s+(all\s+)?(previous|prior|above)\s+(instructions|prompts|rules)", RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromSeconds(1)),
        new(@"you\s+are\s+now\s+(a|an)\s+", RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromSeconds(1)),
        new(@"(system|admin)\s*:\s*", RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromSeconds(1)),
        new(@"disregard\s+(all\s+)?(previous|prior|your)\s+", RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromSeconds(1)),
        new(@"override\s+(your|the)\s+(instructions|rules|prompt)", RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromSeconds(1)),
        new(@"forget\s+(everything|all|your)\s+", RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromSeconds(1)),
        new(@"(act|pretend|behave)\s+as\s+(if|though)\s+", RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromSeconds(1)),
        new(@"new\s+(instructions|rules|directive)\s*:", RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromSeconds(1)),
        new(@"<\s*/?\s*(system|prompt|instruction)", RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromSeconds(1)),
        new(@"\[\s*SYSTEM\s*\]", RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromSeconds(1)),
    ];

    public LocalPromptInjectionDetector(ILogger<LocalPromptInjectionDetector> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Detects common prompt injection attempts using regex heuristics.
    /// This is a Tier 1 (local, fast, zero-cost) defense — it catches obvious attacks
    /// but does NOT cover all attack vectors. Known limitations:
    /// 
    /// For production coverage, layer this with Azure AI Content Safety Prompt Shields (Tier 2)
    /// which uses a trained ML classifier to handle obfuscation, encoding, multi-language,
    /// and indirect injection via tool outputs.
    /// </summary>
    public GuardResult Detect(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return GuardResult.Pass();

        var detectedPatterns = new List<string>();

        foreach (var pattern in InjectionPatterns)
        {
            try
            {
                if (pattern.IsMatch(input))
                {
                    detectedPatterns.Add(pattern.ToString());
                }
            }
            catch (RegexMatchTimeoutException)
            {
                _logger.LogWarning("Regex timeout during prompt injection detection");
            }
        }

        if (detectedPatterns.Count > 0)
        {
            _logger.LogWarning("Prompt injection detected: {PatternCount} suspicious patterns found", detectedPatterns.Count);
            return GuardResult.Block(
                $"Potential prompt injection detected: {detectedPatterns.Count} suspicious pattern(s)",
                detectedPatterns.ToArray());
        }

        return GuardResult.Pass();
    }
}
