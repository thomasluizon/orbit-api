using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Orbit.Api.Controllers;
using Orbit.Api.Extensions;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Infrastructure.Tests.Extensions;

public class AuthorizationFallbackPolicyTests
{
    [Fact]
    public async Task AnonymousRequest_ToEndpointWithoutAuthorizationMetadata_Returns401()
    {
        await using var app = await StartAsync(endpoints => endpoints.MapGet("/unmarked", () => "reachable"));

        var response = await app.GetTestClient().GetAsync("/unmarked");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task EveryEndpoint_HasAuthorizationOrAnonymousMetadata()
    {
        await using var app = await StartAsync();

        var endpoints = app.Services.GetRequiredService<EndpointDataSource>().Endpoints;
        endpoints.Should().NotBeEmpty();
        var unmarked = endpoints
            .Where(endpoint => endpoint.Metadata.GetMetadata<IAuthorizeData>() is null
                && endpoint.Metadata.GetMetadata<IAllowAnonymous>() is null)
            .Select(endpoint => endpoint.DisplayName)
            .ToArray();

        unmarked.Should().BeEmpty("each endpoint needs [Authorize] or [AllowAnonymous]; these have neither: {0}", string.Join(", ", unmarked));
    }

    [Theory]
    [InlineData("/health")]
    [InlineData("/health/jobs")]
    [InlineData("/health/stripe")]
    public async Task AnonymousRequest_ToHealthEndpoint_IsNotRejected(string path)
    {
        await using var app = await StartAsync();

        var response = await app.GetTestClient().GetAsync(path);

        response.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.ServiceUnavailable);
    }

    [Fact]
    public async Task AnonymousRequest_ToMcpLifecycle_GetsTheGateChallenge()
    {
        await using var app = await StartAsync();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent(
                """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"anonymous-probe","version":"1"}}}""",
                System.Text.Encoding.UTF8,
                "application/json")
        };
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");

        var response = await app.GetTestClient().SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        response.Headers.WwwAuthenticate.ToString().Should().Be(
            "Bearer resource_metadata=\"http://localhost/.well-known/oauth-protected-resource/mcp\"");
    }

    private static async Task<WebApplication> StartAsync(Action<IEndpointRouteBuilder>? extraEndpoints = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Configuration["Jwt:SecretKey"] = new string('s', 64);
        builder.Configuration["Jwt:Issuer"] = "orbit-test";
        builder.Configuration["Jwt:Audience"] = "orbit-test";
        builder.AddOrbitAuthentication();
        builder.Services.AddSingleton(Substitute.For<IAuthSessionService>());
        builder.Services.AddSingleton(Substitute.For<IGenericRepository<User>>());
        builder.Services.AddCors(options => options.AddPolicy("ThirdParty", policy => policy.AllowAnyOrigin()));
        builder.Services.AddControllers().AddApplicationPart(typeof(HabitsController).Assembly);
        builder.Services.AddHealthChecks()
            .AddCheck("database", () => HealthCheckResult.Healthy("Database reachable"))
            .AddCheck("background-services", () => HealthCheckResult.Healthy("All background services running"), tags: ["jobs"])
            .AddCheck(WebApplicationExtensions.StripeHealthCheckName, () => HealthCheckResult.Healthy("Versions match"), tags: [WebApplicationExtensions.StripeHealthTag]);
        Orbit.Api.Extensions.ServiceCollectionExtensions.AddMcpToolServer(builder);

        var app = builder.Build();
        app.UseRouting();
        app.UseMcpSelectiveAuth();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapOrbitEndpoints();
        extraEndpoints?.Invoke(app);
        await app.StartAsync();
        return app;
    }
}
