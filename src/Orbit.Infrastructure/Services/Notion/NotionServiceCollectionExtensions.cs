using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Orbit.Application.Connections;

namespace Orbit.Infrastructure.Services.Notion;

public static class NotionServiceCollectionExtensions
{
    public static IServiceCollection AddNotionServices(this IServiceCollection services, IConfiguration configuration, TimeSpan httpTimeout)
    {
        services.Configure<NotionSettings>(configuration.GetSection(NotionSettings.SectionName));
        services.AddSingleton<INotionDelay, NotionDelay>();
        services.AddSingleton<NotionRequestScheduler>();
        services.AddHttpClient<INotionApi, NotionApi>(http => http.Timeout = httpTimeout);
        services.AddScoped<NotionAccessGate>();
        services.AddScoped<INotionConnectionService, NotionConnectionService>();
        services.AddScoped<NotionPollService>();
        return services;
    }
}
