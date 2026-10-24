using Microsoft.Extensions.DependencyInjection;
using Warranty.Guardrails.Remedies;

namespace Warranty.Guardrails;

public static class DependencyInjection
{
    /// <summary>Registers the deterministic guardrail engine and its checks.</summary>
    public static IServiceCollection AddWarrantyGuardrails(this IServiceCollection services)
    {
        // Stateless and pure, so one instance serves every run.
        services.AddSingleton<IGuardrailEngine, GuardrailEngine>();
        services.AddSingleton<RemedyPolicyEvaluator>();
        services.AddSingleton<ReplacementPolicy>();
        return services;
    }
}
