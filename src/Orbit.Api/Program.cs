using Orbit.Api.Extensions;
using Orbit.Application.Common;

ValidationLanguageConfiguration.ConfigureEnglishDefaults();
var builder = WebApplication.CreateBuilder(args);
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
