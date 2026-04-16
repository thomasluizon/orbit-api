using Orbit.Api.Extensions;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.AddFilter("LuckyPennySoftware.MediatR.License", LogLevel.None);
builder.Logging.AddFilter("Microsoft.AspNetCore.DataProtection", LogLevel.Error);

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
