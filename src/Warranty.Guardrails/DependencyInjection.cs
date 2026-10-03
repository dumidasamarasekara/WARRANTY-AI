using Microsoft.Extensions.DependencyInjection;

namespace Warranty.Guardrails;

public static class DependencyInjection
{
    /// <summary>Registers the deterministic guardrail engine and its checks.</summary>
    public static IServiceCollection AddWarrantyGuardrails(this IServiceCollection services)
    {
        // The guardrail engine is registered here when it is implemented (User Story 1 and 4).
        return services;
    }
}
