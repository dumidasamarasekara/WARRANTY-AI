using Microsoft.Extensions.DependencyInjection.Extensions;
using Warranty.Application.Abstractions;
using Warranty.Infrastructure.Audit;

namespace Warranty.Api.Tenancy;

public static class TenancyExtensions
{
    /// <summary>
    /// Registers the scoped <see cref="ITenantContext"/> of the API (request tenant, or the worker's
    /// <see cref="TenantContextScope"/>), the cached tenant directory and the request source for
    /// security events. Call it in any order relative to the Infrastructure registration.
    /// </summary>
    public static IServiceCollection AddTenantResolution(this IServiceCollection services)
    {
        services.AddHttpContextAccessor();
        services.AddMemoryCache();
        services.AddScoped<HttpTenantContext>();
        services.AddScoped<ITenantContext>(sp => sp.GetRequiredService<HttpTenantContext>());
        services.AddScoped<TenantDirectory>();
        services.RemoveAll<IRequestSourceAccessor>();
        services.AddSingleton<IRequestSourceAccessor, HttpRequestSourceAccessor>();
        return services;
    }

    /// <summary>Resolves the request tenant; place it after authentication and before authorization.</summary>
    public static IApplicationBuilder UseTenantResolution(this IApplicationBuilder app)
        => app.UseMiddleware<TenantResolutionMiddleware>();
}
