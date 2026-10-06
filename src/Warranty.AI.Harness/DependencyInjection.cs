using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Warranty.AI.Harness.Agents;
using Warranty.AI.Harness.Agents.Risk;
using Warranty.AI.Harness.Context;
using Warranty.AI.Harness.Execution;
using Warranty.AI.Harness.Safety;
using Warranty.AI.Harness.Schemas;
using Warranty.AI.Harness.Tools;
using Warranty.AI.Harness.Tools.Implementations;
using Warranty.Application.Abstractions.Adjudication;
using Warranty.Application.Abstractions.Knowledge;
using Warranty.Domain.Adjudication;

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

        // Consequential tools: descriptor-only, never offered to a model and never executed through a
        // tool call, so an agent requesting one is a TOOL_SCOPE_VIOLATION (the ActionExecutor calls the
        // integration ports itself, only for a guardrail-approved action).
        services.AddSingleton<ITool>(_ => ConsequentialTool.CreateRepairRequest());
        services.AddSingleton<ITool>(_ => ConsequentialTool.NotifyCustomer());

        // The risk capability (scoped: it reads the tenant's claim history and settings) and its
        // injection detector over the global phrase list (research R14).
        services.TryAddSingleton(_ => new InjectionDetector(InjectionDetector.GlobalPhrases));
        services.AddScoped<IRiskAssessor, RiskAssessor>();

        // Agents are scoped (they persist through the scoped adjudication repository); each resolves as
        // itself and as its IAgent<TInput, TOutput>.
        services.AddScoped<IntakeAgent>();
        services.AddScoped<IAgent<CaseContext, IntakeResult>>(sp => sp.GetRequiredService<IntakeAgent>());
        services.AddScoped<EvidenceAgent>();
        services.AddScoped<IAgent<EvidenceInput, EvidenceResult>>(sp => sp.GetRequiredService<EvidenceAgent>());
        services.AddScoped<PolicyAgent>();
        services.AddScoped<IAgent<PolicyInput, PolicyResult>>(sp => sp.GetRequiredService<PolicyAgent>());
        services.AddScoped<DecisionAgent>();
        services.AddScoped<IAgent<DecisionInput, RecommendationResult>>(sp => sp.GetRequiredService<DecisionAgent>());

        // The run lifecycle (scoped: one run per job scope, sharing that scope's unit of work).
        services.AddScoped(sp => new AdjudicationAgents(
            sp.GetRequiredService<IAgent<CaseContext, IntakeResult>>(),
            sp.GetRequiredService<IAgent<EvidenceInput, EvidenceResult>>(),
            sp.GetRequiredService<IAgent<PolicyInput, PolicyResult>>(),
            sp.GetRequiredService<IAgent<DecisionInput, RecommendationResult>>()));
        services.AddScoped<IAdjudicationRunner, AdjudicationRunner>();

        return services;
    }
}
