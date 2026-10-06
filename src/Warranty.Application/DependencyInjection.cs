using Microsoft.Extensions.DependencyInjection;
using Warranty.Application.Abstractions.Knowledge;
using Warranty.Application.Actions;
using Warranty.Application.Adjudication;
using Warranty.Application.Claims;
using Warranty.Application.Policies;
using Warranty.Application.Trace;

namespace Warranty.Application;

public static class DependencyInjection
{
    /// <summary>
    /// Registers the use cases, queries and the <c>ActionExecutor</c>. The ports they depend on are
    /// registered by Infrastructure, the integrations, the gateway, knowledge and the harness.
    /// </summary>
    public static IServiceCollection AddWarrantyApplication(this IServiceCollection services)
    {
        // Use cases are registered here as they are implemented (user story phases).
        services.AddScoped<ICaseKnowledgeProvider, CaseKnowledgeProvider>();
        services.AddScoped<SubmitClaim>();
        services.AddScoped<ClaimantAccess>();
        services.AddScoped<IActionExecutor, ActionExecutor>();
        services.AddScoped<DecisionTraceQuery>();
        services.AddScoped<PolicyVersionsQuery>();
        return services;
    }
}
