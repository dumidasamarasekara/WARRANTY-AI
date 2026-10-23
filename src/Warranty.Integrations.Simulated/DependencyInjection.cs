using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Warranty.Application.Abstractions.Integrations;

namespace Warranty.Integrations.Simulated;

public static class DependencyInjection
{
    /// <summary>
    /// Registers the simulated CRM, ERP serial registry, service network, repair requests, notifications
    /// and refunds. They persist through the Application repository ports, so the host must also register
    /// the Infrastructure persistence (and logging, which the refund adapter records to).
    /// </summary>
    public static IServiceCollection AddSimulatedIntegrations(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<ICrmClient, SimulatedCrmClient>();
        services.AddScoped<IErpSerialRegistry, SimulatedErpSerialRegistry>();
        services.AddScoped<IServiceNetwork, SimulatedServiceNetwork>();
        services.AddScoped<IRepairRequestService, SimulatedRepairRequestService>();
        services.AddScoped<INotificationService, SimulatedNotificationService>();
        services.AddScoped<IRefundService, SimulatedRefundService>();
        return services;
    }
}
