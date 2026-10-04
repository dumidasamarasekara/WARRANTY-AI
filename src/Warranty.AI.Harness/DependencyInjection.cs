using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Warranty.AI.Harness.Agents.Risk;
using Warranty.AI.Harness.Context;
using Warranty.AI.Harness.Execution;
using Warranty.AI.Harness.Schemas;
using Warranty.AI.Harness.Tools;
using Warranty.AI.Harness.Tools.Implementations;

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

        // Read-only tools (contracts/agents-and-tools.md tool catalog). claim_history_lookup is also
        // resolvable as itself: the risk capability calls it directly.
        services.AddScoped<ITool, CustomerLookupTool>();
        services.AddScoped<ITool, ProductLookupTool>();
        services.AddScoped<ITool, WarrantyLookupTool>();
        services.AddScoped<ITool, InvoiceValidationTool>();
        services.AddScoped<ClaimHistoryLookupTool>();
        services.AddScoped<ITool>(sp => sp.GetRequiredService<ClaimHistoryLookupTool>());
        services.AddScoped<ITool, SearchPolicyKnowledgeTool>();
        services.AddScoped<ITool, SearchGlobalKnowledgeTool>();

        // The risk capability (scoped: it reads the tenant's claim history and settings).
        services.AddScoped<IRiskAssessor, RiskAssessor>();

        // Agents and the AdjudicationRunner (T068) register here.
        return services;
    }
}
