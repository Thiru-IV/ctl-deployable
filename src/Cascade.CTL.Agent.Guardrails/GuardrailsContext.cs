namespace Cascade.CTL.Agent.Guardrails;

/// <summary>
/// Ambient context that flows the current workflow phase name into the
/// GuardrailsMiddleware so every audit entry can be tagged with the phase
/// that triggered it (e.g., "Planning", "Legal &amp; Title", "Reflection").
/// Uses <see cref="AsyncLocal{T}"/> so parallel investigation agents each
/// get their own value without cross-contamination.
/// </summary>
public static class GuardrailsContext
{
    private static readonly AsyncLocal<string?> _currentPhase = new();

    public static string? CurrentPhase
    {
        get => _currentPhase.Value;
        set => _currentPhase.Value = value;
    }
}
