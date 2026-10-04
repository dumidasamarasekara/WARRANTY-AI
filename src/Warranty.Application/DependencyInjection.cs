using Microsoft.Extensions.DependencyInjection;
using Warranty.Application.Abstractions.Knowledge;
using Warranty.Application.Adjudication;
using Warranty.Application.Claims;

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
        return services;
    }
}
