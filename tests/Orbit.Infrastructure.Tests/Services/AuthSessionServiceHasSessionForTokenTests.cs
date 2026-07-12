using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Persistence;
using Orbit.Infrastructure.Services;

namespace Orbit.Infrastructure.Tests.Services;

public class AuthSessionServiceHasSessionForTokenTests
{
    [Fact]
    public async Task HasSessionForTokenAsync_ReturnsTrue_WhenAStoredSessionMatchesTheToken()
    {
        var dbName = NewDbName();
        const string refreshToken = "a-real-server-issued-token";

        await using (var seed = CreateContext(dbName))
        {
            seed.UserSessions.Add(UserSession.Create(Guid.NewGuid(), Hash(refreshToken), DateTime.UtcNow.AddDays(90)).Value);
            await seed.SaveChangesAsync();
        }

        await using var context = CreateContext(dbName);

        var exists = await CreateService(context).HasSessionForTokenAsync(refreshToken, CancellationToken.None);

        exists.Should().BeTrue();
    }

    [Fact]
    public async Task HasSessionForTokenAsync_ReturnsFalse_WhenNoStoredSessionMatchesTheToken()
    {
        var dbName = NewDbName();

        await using (var seed = CreateContext(dbName))
        {
            seed.UserSessions.Add(UserSession.Create(Guid.NewGuid(), Hash("a-different-token"), DateTime.UtcNow.AddDays(90)).Value);
            await seed.SaveChangesAsync();
        }

        await using var context = CreateContext(dbName);

        var exists = await CreateService(context).HasSessionForTokenAsync("a-forged-token-no-session-has", CancellationToken.None);

        exists.Should().BeFalse();
    }

    [Fact]
    public async Task HasSessionForTokenAsync_ReturnsFalse_WhenTheMatchingSessionIsRevoked()
    {
        var dbName = NewDbName();
        const string refreshToken = "a-revoked-token";
        var session = UserSession.Create(Guid.NewGuid(), Hash(refreshToken), DateTime.UtcNow.AddDays(90)).Value;
        session.Revoke(DateTime.UtcNow);

        await using (var seed = CreateContext(dbName))
        {
            seed.UserSessions.Add(session);
            await seed.SaveChangesAsync();
        }

        await using var context = CreateContext(dbName);

        var exists = await CreateService(context).HasSessionForTokenAsync(refreshToken, CancellationToken.None);

        exists.Should().BeFalse();
    }

    [Fact]
    public async Task HasSessionForTokenAsync_ReturnsFalse_WhenTheMatchingSessionIsExpired()
    {
        var dbName = NewDbName();
        const string refreshToken = "an-expired-token";
        var session = UserSession.Create(Guid.NewGuid(), Hash(refreshToken), DateTime.UtcNow.AddDays(-1)).Value;

        await using (var seed = CreateContext(dbName))
        {
            seed.UserSessions.Add(session);
            await seed.SaveChangesAsync();
        }

        await using var context = CreateContext(dbName);

        var exists = await CreateService(context).HasSessionForTokenAsync(refreshToken, CancellationToken.None);

        exists.Should().BeFalse();
    }

    private static AuthSessionService CreateService(OrbitDbContext context)
    {
        var tokenService = Substitute.For<ITokenService>();
        tokenService.GenerateToken(Arg.Any<Guid>(), Arg.Any<string>()).Returns("access-token");

        return new AuthSessionService(
            new GenericRepository<UserSession>(context),
            new GenericRepository<User>(context),
            tokenService,
            new UnitOfWork(context),
            Options.Create(new JwtSettings
            {
                SecretKey = "test-secret-key-that-is-at-least-32-bytes-long-for-hmac",
                Issuer = "test-issuer",
                Audience = "test-audience",
                ExpiryMinutes = 0,
                RefreshExpiryDays = 90
            }));
    }

    private static OrbitDbContext CreateContext(string dbName)
    {
        var builder = new DbContextOptionsBuilder<OrbitDbContext>().UseInMemoryDatabase(dbName);
        return new OrbitDbContext(builder.Options);
    }

    private static string NewDbName() => $"AuthSessionHasToken_{Guid.NewGuid()}";

    private static string Hash(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
