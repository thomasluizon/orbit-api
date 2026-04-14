using Orbit.Api.Extensions;

var builder = WebApplication.CreateBuilder(args);

builder
    .ValidateOrbitSecuritySettings()
    .AddOrbitDatabase()
    .AddOrbitAuthentication()
    .AddOrbitAiServices()
    .AddOrbitInfrastructure()
    .AddOrbitRateLimiting();

var app = builder.Build();

await app.ConfigureOrbitPipeline();

await app.RunAsync();
