using Orbit.Api.Extensions;
using Orbit.Api.Seed;
using Orbit.Application.Common;

ValidationLanguageConfiguration.ConfigureEnglishDefaults();
var builder = WebApplication.CreateBuilder(args);
if (args is ["seed-staging"])
{
    await StagingSeedCommand.RunAsync(builder.Configuration, builder.Environment.EnvironmentName);
    return;
}
if (args is ["migrate-staging"])
{
    await StagingSeedCommand.MigrateAsync(builder.Configuration, builder.Environment.EnvironmentName);
    return;
}
builder.Logging.AddFilter("LuckyPennySoftware.MediatR.License", LogLevel.None);
builder.Logging.AddFilter("Microsoft.AspNetCore.DataProtection", LogLevel.Error);

builder
    .ValidateOrbitSecuritySettings()
    .AddOrbitDatabase()
    .AddOrbitAuthentication()
    .AddOrbitAiServices()
    .AddOrbitInfrastructure()
    .AddOrbitRateLimiting()
    .AddOrbitProductAnalytics()
    .AddOrbitObservability();

var app = builder.Build();

await app.ConfigureOrbitPipeline();

await app.RunAsync();
