using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Warranty.AI.Harness.Context;
using Warranty.AI.Harness.Execution;
using Warranty.AI.Harness.Schemas;
using Warranty.AI.Harness.Tools;

namespace Warranty.AI.Harness;

public static class DependencyInjection
{
    /// <summary>
    /// Registers the harness: agents, tools, context building and the adjudication runner. Requires the
    /// AI-ops repository, security event writer, PII redactor and a <see cref="TimeProvider"/> from the host.
    /// </summary>
    public static IServiceCollection AddWarrantyAiHarness(this IServiceCollection services)
    {
        services.TryAddSingleton<ITraceWriter, ActivityTraceWriter>();
        services.AddSingleton<AgentTurnLoop>();
        services.AddSingleton<ContextBuilder>();
        services.AddSingleton<SchemaValidator>();

        // Tools are scoped (they read tenant data through scoped repositories); implementations
        // register as ITool and are picked up by the registry.
        services.AddScoped<ToolRegistry>();
        services.AddScoped<ToolInvoker>();

        // Tool implementations (T060), agents and the AdjudicationRunner (T068) register here.
        return services;
    }
}
