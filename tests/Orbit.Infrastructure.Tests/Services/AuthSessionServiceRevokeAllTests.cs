using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Persistence;
using Orbit.Infrastructure.Services;

namespace Orbit.Infrastructure.Tests.Services;

public class AuthSessionServiceRevokeAllTests
{
    [Fact]
    public async Task RevokeAllSessionsAsync_RevokesOnlyActiveSessionsOfTargetUser()
    {
        var dbName = NewDbName();
        var target = Guid.NewGuid();
        var other = Guid.NewGuid();
        var alreadyRevokedAt = DateTime.UtcNow.AddDays(-3);

        var targetActiveA = UserSession.Create(target, Hash("target-a"), DateTime.UtcNow.AddDays(90)).Value;
        var targetActiveB = UserSession.Create(target, Hash("target-b"), null).Value;
        var targetRevoked = UserSession.Create(target, Hash("target-revoked"), DateTime.UtcNow.AddDays(90)).Value;
        targetRevoked.Revoke(alreadyRevokedAt);
        var otherActive = UserSession.Create(other, Hash("other-a"), DateTime.UtcNow.AddDays(90)).Value;

        await using (var seed = CreateContext(dbName))
        {
            seed.UserSessions.AddRange(targetActiveA, targetActiveB, targetRevoked, otherActive);
            await seed.SaveChangesAsync();
        }

        await using (var context = CreateContext(dbName))
        {
            var result = await CreateService(context).RevokeAllSessionsAsync(target, CancellationToken.None);
            result.IsSuccess.Should().BeTrue();
        }

        await using var verify = CreateContext(dbName);
        var sessions = await verify.UserSessions.ToListAsync();

        sessions.Single(s => s.Id == targetActiveA.Id).RevokedAtUtc.Should().NotBeNull();
        sessions.Single(s => s.Id == targetActiveB.Id).RevokedAtUtc.Should().NotBeNull();
        sessions.Single(s => s.Id == targetRevoked.Id).RevokedAtUtc.Should().BeCloseTo(alreadyRevokedAt, TimeSpan.FromSeconds(1));
        sessions.Single(s => s.Id == otherActive.Id).RevokedAtUtc.Should().BeNull();
    }

    [Fact]
    public async Task RevokeAllSessionsAsync_NoActiveSessions_Succeeds()
    {
        var dbName = NewDbName();
        await using var context = CreateContext(dbName);

        var result = await CreateService(context).RevokeAllSessionsAsync(Guid.NewGuid(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task RevokeAllSessionsAsync_TransientConcurrencyConflict_RetriesAndRevokesEveryActiveSession()
    {
        var dbName = NewDbName();
        var userId = Guid.NewGuid();
        var sessionA = UserSession.Create(userId, Hash("active-a"), DateTime.UtcNow.AddDays(90)).Value;
        var sessionB = UserSession.Create(userId, Hash("active-b"), DateTime.UtcNow.AddDays(90)).Value;

        await using (var seed = CreateContext(dbName))
        {
            seed.UserSessions.AddRange(sessionA, sessionB);
            await seed.SaveChangesAsync();
        }

        await using (var context = CreateContext(dbName, new ConflictOnceInterceptor()))
        {
            var result = await CreateService(context).RevokeAllSessionsAsync(userId, CancellationToken.None);
            result.IsSuccess.Should().BeTrue();
        }

        await using var verify = CreateContext(dbName);
        var sessions = await verify.UserSessions.Where(s => s.UserId == userId).ToListAsync();

        sessions.Should().HaveCount(2);
        sessions.Should().OnlyContain(s => s.RevokedAtUtc != null);
    }

    [Fact]
    public async Task RevokeAllSessionsAsync_ConcurrencyConflict_ReturnsInvalidSessionWithoutThrowing()
    {
        var dbName = NewDbName();
        var userId = Guid.NewGuid();

        await using (var seed = CreateContext(dbName))
        {
            seed.UserSessions.Add(UserSession.Create(userId, Hash("active"), DateTime.UtcNow.AddDays(90)).Value);
            await seed.SaveChangesAsync();
        }

        await using var context = CreateContext(dbName, new ConflictAlwaysInterceptor());

        var act = async () => await CreateService(context).RevokeAllSessionsAsync(userId, CancellationToken.None);

        var result = await act.Should().NotThrowAsync();
        result.Which.IsFailure.Should().BeTrue();
        result.Which.ErrorCode.Should().Be("INVALID_SESSION");
    }

    [Fact]
    public async Task RevokeAllSessionsAsync_EmptyUserId_Fails()
    {
        var dbName = NewDbName();
        await using var context = CreateContext(dbName);

        var result = await CreateService(context).RevokeAllSessionsAsync(Guid.Empty, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be("USER_ID_REQUIRED");
    }

    private static AuthSessionService CreateService(OrbitDbContext context)
    {
        var tokenService = Substitute.For<ITokenService>();
        tokenService.GenerateToken(Arg.Any<Guid>(), Arg.Any<string>()).Returns("access-token");

        return new AuthSessionService(
            new GenericRepository<UserSession>(context),
            new GenericRepository<User>(context),
            tokenService,
            new UnitOfWork(context, new DatabaseConnectionSettings()),
            Options.Create(new JwtSettings
            {
                SecretKey = "test-secret-key-that-is-at-least-32-bytes-long-for-hmac",
                Issuer = "test-issuer",
                Audience = "test-audience",
                ExpiryMinutes = 0,
                RefreshExpiryDays = 90
            }));
    }

    private static OrbitDbContext CreateContext(string dbName, ISaveChangesInterceptor? interceptor = null)
    {
        var builder = new DbContextOptionsBuilder<OrbitDbContext>().UseInMemoryDatabase(dbName);
        if (interceptor is not null)
            builder.AddInterceptors(interceptor);
        return new OrbitDbContext(builder.Options);
    }

    private static string NewDbName() => $"AuthSessionRevokeAll_{Guid.NewGuid()}";

    private static string Hash(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private sealed class ConflictAlwaysInterceptor : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            throw new DbUpdateConcurrencyException("simulated stale token");
        }
    }

    private sealed class ConflictOnceInterceptor : SaveChangesInterceptor
    {
        private bool _hasThrown;

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (_hasThrown)
                return base.SavingChangesAsync(eventData, result, cancellationToken);

            _hasThrown = true;
            throw new DbUpdateConcurrencyException("simulated transient stale token");
        }
    }
}
