using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Warranty.AI.Gateway;

public static class DependencyInjection
{
    /// <summary>Registers <c>IAiGateway</c> with its routes, prompt templates and providers (contracts/ai-gateway.md).</summary>
    public static IServiceCollection AddWarrantyAiGateway(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        // The gateway core (T026) and provider selection from the AiGateway section (T031) register here.
        return services;
    }
}
