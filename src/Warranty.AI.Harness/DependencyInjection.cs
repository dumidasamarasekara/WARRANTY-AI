using Microsoft.Extensions.DependencyInjection;

namespace Warranty.AI.Harness;

public static class DependencyInjection
{
    /// <summary>Registers the harness: agents, tools, context building and the adjudication runner.</summary>
    public static IServiceCollection AddWarrantyAiHarness(this IServiceCollection services)
    {
        // The harness framework (T034–T036) and the AdjudicationRunner (T068) register here.
        return services;
    }
}
