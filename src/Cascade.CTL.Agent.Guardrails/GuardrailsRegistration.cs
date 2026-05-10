using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Cascade.CTL.Agent.Guardrails;

public static class GuardrailsRegistration
{
    public static IServiceCollection AddCTLGuardrails(this IServiceCollection services)
    {
        services.AddSingleton<LocalPromptInjectionDetector>();
          //uses model-based content safety screening for both direct input and indirect prompt injection(via tool outputs)
        services.AddSingleton<ContentSafetyGuard>();
        services.AddSingleton<PiiFilter>();
        services.AddSingleton<CTLRequestValidator>();
        services.AddSingleton<TokenBudgetGuard>();

        return services;
    }
}
