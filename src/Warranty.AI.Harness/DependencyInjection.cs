using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Warranty.AI.Harness.Context;
using Warranty.AI.Harness.Execution;

namespace Warranty.AI.Harness;

public static class DependencyInjection
{
    /// <summary>Registers the harness: agents, tools, context building and the adjudication runner.</summary>
    public static IServiceCollection AddWarrantyAiHarness(this IServiceCollection services)
    {
        services.TryAddSingleton<ITraceWriter, ActivityTraceWriter>();
        services.AddSingleton<AgentTurnLoop>();
        services.AddSingleton<ContextBuilder>();

        // The tool framework (T035), agents and the AdjudicationRunner (T068) register here.
        return services;
    }
}
