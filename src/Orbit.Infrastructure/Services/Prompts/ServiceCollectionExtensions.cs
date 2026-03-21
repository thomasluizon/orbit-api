using Microsoft.Extensions.DependencyInjection;

namespace Orbit.Infrastructure.Services.Prompts;

public static class PromptServiceExtensions
{
    public static IServiceCollection AddPromptBuilder(this IServiceCollection services)
    {
        services.AddSingleton<ActionDiscoveryService>();
        return services;
    }
}
