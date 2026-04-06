using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Orbit.Infrastructure.Persistence;
using Scalar.AspNetCore;

namespace Orbit.Api.Extensions;

public static class WebApplicationExtensions
{
    public static async Task ConfigureOrbitPipeline(this WebApplication app)
    {
        // Apply Migrations
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OrbitDbContext>();
            await db.Database.MigrateAsync();
        }

        // Security & Forwarded Headers
        app.UseMiddleware<Orbit.Api.Middleware.SecurityHeadersMiddleware>();
        app.UseForwardedHeaders(new ForwardedHeadersOptions
        {
            ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
            ForwardLimit = 1,
            KnownIPNetworks = { },
            KnownProxies = { }
        });

        if (app.Environment.IsProduction())
            app.UseRateLimiter();

        if (app.Environment.IsDevelopment())
        {
            app.MapOpenApi();
            app.MapScalarApiReference();
        }

        app.UseExceptionHandler();
        app.UseCors();
        app.UseCookiePolicy();

        if (app.Environment.IsProduction())
        {
            app.UseHttpsRedirection();
        }

        // MCP selective auth
        app.UseMcpSelectiveAuth();

        app.UseAuthentication();
        app.UseAuthorization();
        app.MapControllers();
        app.MapMcp("/mcp");

        app.MapHealthChecks("/health", new HealthCheckOptions
        {
            ResponseWriter = async (context, report) =>
            {
                context.Response.ContentType = "application/json";
                var result = new
                {
                    status = report.Status.ToString(),
                    checks = report.Entries.Select(e => new
                    {
                        name = e.Key,
                        status = e.Value.Status.ToString(),
                        description = e.Value.Description,
                        data = e.Value.Data
                    })
                };
                await context.Response.WriteAsJsonAsync(result);
            }
        }).AllowAnonymous();
    }

    private static void UseMcpSelectiveAuth(this WebApplication app)
    {
        app.Use(async (context, next) =>
        {
            if (context.Request.Path.StartsWithSegments("/mcp") && context.Request.Method == "POST")
            {
                context.Request.EnableBuffering();
                using var reader = new StreamReader(context.Request.Body, leaveOpen: true);
                var body = await reader.ReadToEndAsync();
                context.Request.Body.Position = 0;

                if (IsMcpUnauthenticatedMethod(body))
                {
                    await next();
                    return;
                }

                // For tool calls, require auth
                var authResult = await context.AuthenticateAsync();
                if (!authResult.Succeeded)
                {
                    var scheme = context.Request.Headers["X-Forwarded-Proto"].FirstOrDefault() ?? context.Request.Scheme;
                    var resourceUrl = $"{scheme}://{context.Request.Host}/.well-known/oauth-protected-resource";
                    context.Response.StatusCode = 401;
                    context.Response.Headers.WWWAuthenticate = $"Bearer resource_metadata=\"{resourceUrl}\"";
                    return;
                }
                context.User = authResult.Principal!;
            }
            await next();
        });
    }

    private static bool IsMcpUnauthenticatedMethod(string body)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("method", out var methodProp))
            {
                var method = methodProp.GetString();
                return method is "initialize" or "ping"
                    || (method?.StartsWith("notifications/") == true);
            }
        }
        catch (System.Text.Json.JsonException)
        {
            // Invalid JSON -- require auth
        }

        return false;
    }
}
