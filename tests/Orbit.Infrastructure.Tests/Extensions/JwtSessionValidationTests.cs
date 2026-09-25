using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orbit.Api.Extensions;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Services;

namespace Orbit.Infrastructure.Tests.Extensions;

public class JwtSessionValidationTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ValidatedSessionToken_UsesConfiguredSessionGate(bool active)
    {
        var userId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var sessions = Substitute.For<IAuthSessionService>();
        sessions.IsSessionActiveAsync(sessionId, userId, Arg.Any<CancellationToken>()).Returns(active);

        var context = CreateContext(sessions);
        var token = new JwtTokenService(Options.Create(Settings())).GenerateToken(userId, "user@example.com", sessionId);
        context.Principal = new ClaimsPrincipal(new ClaimsIdentity(
            new JwtSecurityTokenHandler().ReadJwtToken(token).Claims, "JwtBearer"));

        await context.Options.Events.OnTokenValidated(context);

        if (active)
            context.Result.Should().BeNull();
        else
            context.Result?.Failure.Should().NotBeNull();
    }

    [Fact]
    public async Task ValidatedLegacyToken_WithoutSessionClaim_RemainsAccepted()
    {
        var context = CreateContext(Substitute.For<IAuthSessionService>());
        context.Principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString())], "JwtBearer"));

        await context.Options.Events.OnTokenValidated(context);

        context.Result.Should().BeNull();
    }

    private static TokenValidatedContext CreateContext(IAuthSessionService sessions)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Jwt:SecretKey"] = Settings().SecretKey,
            ["Jwt:Issuer"] = Settings().Issuer,
            ["Jwt:Audience"] = Settings().Audience
        });
        builder.Services.AddSingleton(sessions);
        builder.AddOrbitAuthentication();
        var services = builder.Services.BuildServiceProvider();
        var options = services.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>().Get("JwtBearer");
        var httpContext = new DefaultHttpContext { RequestServices = services };
        var scheme = new AuthenticationScheme("JwtBearer", "JWT", typeof(JwtBearerHandler));
        return new TokenValidatedContext(httpContext, scheme, options);
    }

    private static JwtSettings Settings() => new()
    {
        SecretKey = "test-secret-key-that-is-at-least-32-bytes-long-for-hmac",
        Issuer = "test-issuer",
        Audience = "test-audience",
        ExpiryHours = 1
    };
}
