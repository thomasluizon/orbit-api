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
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using NSubstitute;
using Orbit.Api.Extensions;
using Orbit.Application.Auth.Commands;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Services;
using Orbit.Infrastructure.Events;
using Orbit.Infrastructure.Persistence;
using Orbit.Infrastructure.Tests.Persistence;

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
        using var memory = new MemoryCache(new MemoryCacheOptions());
        var cache = new AuthValidationCache(memory, Options.Create(new AuthCacheSettings()), TimeProvider.System);
        using var bus = new InMemoryAccountEventBus();
        var collector = new AccountEventCollector(bus, new HttpContextAccessor(), cache);
        using var database = new SqliteOrbitDbContextFactory(collector, new AccountEventTransactionInterceptor(collector));
        var db = database.Context;
        var user = User.Create("User", "user@example.com").Value;
        db.Users.Add(user);
        await db.SaveChangesAsync();
        var sessions = new AuthSessionService(
            new GenericRepository<UserSession>(db),
            new GenericRepository<UserSessionRefreshToken>(db),
            new GenericRepository<User>(db),
            new JwtTokenService(Options.Create(Settings())),
            new UnitOfWork(db, new DatabaseConnectionSettings()),
            Options.Create(Settings()), cache);
        var issued = await sessions.CreateSessionAsync(user.Id, user.Email);
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
        (await db.UserSessions.AsNoTracking().SingleAsync()).RevokedAtUtc.Should().NotBeNull();
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
