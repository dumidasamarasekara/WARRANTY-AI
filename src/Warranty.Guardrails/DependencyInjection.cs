using Microsoft.Extensions.DependencyInjection;

namespace Warranty.Guardrails;

public static class DependencyInjection
{
    /// <summary>Registers the deterministic guardrail engine and its checks.</summary>
    public static IServiceCollection AddWarrantyGuardrails(this IServiceCollection services)
    {
        // Stateless and pure, so one instance serves every run.
        services.AddSingleton<IGuardrailEngine, GuardrailEngine>();
        return services;
    }
}
