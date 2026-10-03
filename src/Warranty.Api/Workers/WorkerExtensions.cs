using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Warranty.Api.Workers;

public static class WorkerExtensions
{
    /// <summary>Registers the claim job worker unless <c>ClaimJobWorker:Enabled</c> is false.</summary>
    public static IServiceCollection AddClaimJobWorker(this IServiceCollection services, IConfiguration configuration)
    {
        var section = configuration.GetSection(ClaimJobWorkerOptions.SectionName);
        services.AddOptions<ClaimJobWorkerOptions>().Bind(section);
        if (section.GetValue(nameof(ClaimJobWorkerOptions.Enabled), defaultValue: true))
        {
            services.TryAddSingleton(TimeProvider.System);
            services.AddHostedService<ClaimJobWorker>();
        }

        return services;
    }
}
