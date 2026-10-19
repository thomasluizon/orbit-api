using System.IdentityModel.Tokens.Jwt;
using System.Linq.Expressions;
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
using Orbit.Application.Auth.Commands;
using Orbit.Domain.Entities;
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
    public async Task Logout_SameAccessToken_IsRejectedWithUnauthorizedChallenge()
    {
        var rows = new List<UserSession>();
        var repository = Substitute.For<IGenericRepository<UserSession>>();
        repository.AddAsync(Arg.Any<UserSession>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                rows.Add(call.Arg<UserSession>());
                return Task.CompletedTask;
            });
        repository.FindOneTrackedAsync(
                Arg.Any<Expression<Func<UserSession, bool>>>(),
                Arg.Any<Func<IQueryable<UserSession>, IQueryable<UserSession>>?>(),
                Arg.Any<CancellationToken>())
            .Returns(call => rows.SingleOrDefault(call.Arg<Expression<Func<UserSession, bool>>>().Compile()));
        repository.AnyAsync(Arg.Any<Expression<Func<UserSession, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(call => rows.Any(call.Arg<Expression<Func<UserSession, bool>>>().Compile()));
        var sessions = new AuthSessionService(
            repository,
            Substitute.For<IGenericRepository<UserSessionRefreshToken>>(),
            Substitute.For<IGenericRepository<User>>(),
            new JwtTokenService(Options.Create(Settings())),
            Substitute.For<IUnitOfWork>(),
            Options.Create(Settings()));
        var issued = await sessions.CreateSessionAsync(Guid.NewGuid(), "user@example.com");
        issued.IsSuccess.Should().BeTrue();
        using var services = CreateServices(sessions);
        using (var scope = services.CreateScope())
        {
            var request = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
            request.Request.Headers.Authorization = $"Bearer {issued.Value.AccessToken}";
            (await request.AuthenticateAsync()).Succeeded.Should().BeTrue();
        }

        var logout = await new LogoutSessionCommandHandler(sessions)
            .Handle(new LogoutSessionCommand(issued.Value.RefreshToken), CancellationToken.None);

        logout.IsSuccess.Should().BeTrue();
        rows.Should().ContainSingle().Which.RevokedAtUtc.Should().NotBeNull();
        (await sessions.RefreshSessionAsync(issued.Value.RefreshToken)).IsFailure.Should().BeTrue();
        using (var scope = services.CreateScope())
        {
            var request = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
            request.Request.Headers.Authorization = $"Bearer {issued.Value.AccessToken}";

            var authentication = await request.AuthenticateAsync();

            authentication.Succeeded.Should().BeFalse();
            authentication.Failure.Should().NotBeNull();
            await request.ChallengeAsync();
            request.Response.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
        }
    }

    [Fact]
    public async Task ValidatedToken_WithoutSessionClaim_IsRejected()
    {
        var context = CreateContext(Substitute.For<IAuthSessionService>());
        context.Principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString())], "JwtBearer"));

        await context.Options.Events.OnTokenValidated(context);

        context.Result.Should().BeOfType<AuthenticateResult>()
            .Which.Failure.Should().NotBeNull();
    }

    private static TokenValidatedContext CreateContext(IAuthSessionService sessions)
    {
        var services = CreateServices(sessions);
        var options = services.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>().Get("JwtBearer");
        var httpContext = new DefaultHttpContext { RequestServices = services };
        var scheme = new AuthenticationScheme("JwtBearer", "JWT", typeof(JwtBearerHandler));
        return new TokenValidatedContext(httpContext, scheme, options);
    }

    private static ServiceProvider CreateServices(IAuthSessionService sessions)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Jwt:SecretKey"] = Settings().SecretKey,
            ["Jwt:Issuer"] = Settings().Issuer,
            ["Jwt:Audience"] = Settings().Audience,
            ["Jwt:ExpiryMinutes"] = "15",
            ["Jwt:ExpiryHours"] = "0"
        });
        builder.Services.AddSingleton(sessions);
        builder.AddOrbitAuthentication();
        return builder.Services.BuildServiceProvider();
    }

    private static JwtSettings Settings() => new()
    {
        SecretKey = "test-secret-key-that-is-at-least-32-bytes-long-for-hmac",
        Issuer = "test-issuer",
        Audience = "test-audience",
        ExpiryMinutes = 15
    };
}
