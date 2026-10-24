using Orbit.Api.Hosting;

namespace Orbit.Api.Extensions;

public static class ShutdownDrainExtensions
{
    public static IServiceCollection AddOrbitShutdownDrain(this IServiceCollection services)
    {
        services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromSeconds(270));
        services.AddOptions<ShutdownDrainOptions>()
            .Validate(options => options.Timeout > TimeSpan.Zero && options.Timeout < TimeSpan.FromSeconds(270))
            .ValidateOnStart();
        services.AddSingleton<ShutdownDrain>();
        services.AddHostedService(provider => provider.GetRequiredService<ShutdownDrain>());
        return services;
    }
}
