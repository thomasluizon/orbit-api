using Orbit.Api.Mcp;
using Orbit.Application.FeatureFlags;
using System.Net;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using Orbit.Api.Extensions;
using Orbit.Domain.Common;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Persistence;
using Orbit.Infrastructure.Services;
using Scalar.AspNetCore;

namespace Orbit.Api.Extensions;

public static partial class WebApplicationExtensions
{
    public static async Task ConfigureOrbitPipeline(this WebApplication app)
    {
        await MigrateDatabaseIfEnabledAsync(app.Configuration, async (migrationConnectionString, databaseSettings) =>
        {
            var migrationOptions = new DbContextOptionsBuilder<OrbitDbContext>()
                .UseNpgsql(migrationConnectionString, npgsql =>
                {
                    npgsql.EnableRetryOnFailure(maxRetryCount: 3, maxRetryDelay: TimeSpan.FromSeconds(5), errorCodesToAdd: null);
                    npgsql.CommandTimeout(databaseSettings.MigrationCommandTimeoutSeconds);
                })
                .Options;
            await using var migrationDb = new OrbitDbContext(migrationOptions);
            await migrationDb.Database.MigrateAsync();
        });

        app.UseMiddleware<Orbit.Api.Middleware.ShutdownDrainMiddleware>();
        app.UseMiddleware<Orbit.Api.Middleware.SecurityHeadersMiddleware>();
        app.UseMiddleware<Orbit.Api.Middleware.BrandAssetsMiddleware>();
        app.UseForwardedHeaders(BuildForwardedHeadersOptions(app));

        // After UseForwardedHeaders so Request.IsHttps reflects X-Forwarded-Proto, which EnableForHttps gates on: https://learn.microsoft.com/aspnet/core/performance/response-compression
        app.UseResponseCompression();

        app.UseMiddleware<Orbit.Api.Middleware.RequestCorrelationMiddleware>();

        if (app.Environment.IsDevelopment())
        {
            app.MapOpenApi();
            app.MapScalarApiReference(options => options
                .AddDocument("v1", "Orbit API v1", isDefault: true)
                .AddDocument("v2", "Orbit API v2"));
        }

        app.UseExceptionHandler();
        app.UseRouting();
        app.UseMiddleware<Orbit.Api.Middleware.AnonymousOnboardingMiddleware>();
        app.UseCors();
        app.UseCookiePolicy();
        app.UseMiddleware<Orbit.Api.Middleware.UploadObjectKeyValidationMiddleware>();

        app.UseMiddleware<Orbit.Api.Middleware.MinimumVersionMiddleware>();

        app.UseMcpSelectiveAuth();

        app.UseAuthentication();
        app.UseAuthorization();
        app.MapControllers();
        app.MapMcp("/mcp").RequireCors("ThirdParty");

        app.MapHealthChecks("/health", new HealthCheckOptions
        {
            ResponseWriter = WriteHealthCheckResponseAsync
        }).AllowAnonymous();
    }

    internal static void UseMcpSelectiveAuth(this IApplicationBuilder app) => McpHttpGate.Use(app);

    internal static async Task MigrateDatabaseIfEnabledAsync(
        IConfiguration configuration,
        Func<string, DatabaseConnectionSettings, Task> migrate)
    {
        if (BuildTimeDocumentGeneration.IsActive)
            return;

        var settings = DatabaseConnectionSettings.From(configuration);
        if (!settings.MigrateOnStartup)
            return;

        await migrate(OrbitConnectionStringFactory.ForSession(configuration), settings);
    }

    internal static async Task WriteHealthCheckResponseAsync(HttpContext context, HealthReport report)
    {
        context.Response.StatusCode = report.Status == HealthStatus.Healthy
            ? StatusCodes.Status200OK
            : StatusCodes.Status503ServiceUnavailable;
        context.Response.ContentType = "application/json";

        var result = new
        {
            status = report.Status.ToString(),
            commit = Environment.GetEnvironmentVariable("RENDER_GIT_COMMIT"),
            checks = report.Entries.Select(e => new
            {
                name = e.Key,
                status = e.Value.Status.ToString(),
                description = e.Value.Description
            })
        };

        await context.Response.WriteAsJsonAsync(result);
    }

    private static ForwardedHeadersOptions BuildForwardedHeadersOptions(WebApplication app)
    {
        var options = new ForwardedHeadersOptions
        {
            ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
            ForwardLimit = 1
        };

        var knownProxies = app.Configuration.GetSection("ForwardedHeaders:KnownProxies").Get<string[]>() ?? [];
        foreach (var proxy in knownProxies)
        {
            if (IPAddress.TryParse(proxy, out var parsedProxy))
                options.KnownProxies.Add(parsedProxy);
        }

        // https://github.com/thomasluizon/orbit-tickets/issues/114: Render staging logged peer 10.24.103.103 as the refresh IP bucket, so trust the configured private proxy network for one hop.
        var knownNetworks = app.Configuration.GetSection("ForwardedHeaders:KnownNetworks").Get<string[]>() ?? [];
        foreach (var network in knownNetworks)
        {
            var parts = network.Split('/', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2 || !IPAddress.TryParse(parts[0], out var prefix) || !int.TryParse(parts[1], out var prefixLength))
                continue;

            if (System.Net.IPNetwork.TryParse($"{prefix}/{prefixLength}", out var parsedNetwork))
                options.KnownIPNetworks.Add(parsedNetwork);
        }

        return options;
    }

}
