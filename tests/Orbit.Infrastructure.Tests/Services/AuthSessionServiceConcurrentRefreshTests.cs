using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Options;
using NSubstitute;
using Npgsql;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Persistence;
using Orbit.Infrastructure.Services;

namespace Orbit.Infrastructure.Tests.Services;

public class AuthSessionServiceConcurrentRefreshTests
{
    [Fact]
    public async Task RefreshSessionAsync_LosesHistoryInsertRace_ReturnsInvalidSessionAndLeavesContextClean()
    {
        var dbName = NewDbName();
        var (userId, token) = await SeedSessionAsync(dbName);

        var interceptor = new HistoryConflictAfterConcurrentRefreshInterceptor(async () =>
        {
            await using var racer = CreateContext(dbName);
            var winner = await CreateService(racer).RefreshSessionAsync(token, CancellationToken.None);
            winner.IsSuccess.Should().BeTrue();
        });

        await using var context = CreateContext(dbName, interceptor);
        var result = await CreateService(context).RefreshSessionAsync(token, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be("INVALID_SESSION");
        interceptor.SaveAttempts.Should().Be(1);
        context.ChangeTracker.Entries().Should().NotContain(entry =>
            entry.State == EntityState.Added || entry.State == EntityState.Modified || entry.State == EntityState.Deleted);

        await using var verify = CreateContext(dbName);
        verify.UserSessionRefreshTokens.Should().ContainSingle(history =>
            history.TokenHash == Hash(token));
        verify.UserSessions.Single(session => session.UserId == userId).TokenHash
            .Should().NotBe(Hash(token));
    }

    [Fact]
    public async Task RefreshSessionAsync_LosesRotationRaceToConcurrentRefresh_ReturnsInvalidSessionAndRotatesRowOnce()
    {
        var dbName = NewDbName();
        var (userId, token) = await SeedSessionAsync(dbName);
        const string winnerToken = "winning-concurrent-refresh-token";

        var interceptor = new ConflictOnceInterceptor(onFirstSave: () =>
        {
            using var racer = CreateContext(dbName);
            var raced = racer.UserSessions.Single(s => s.UserId == userId);
            raced.Rotate(Hash(winnerToken), DateTime.UtcNow.AddDays(90), DateTime.UtcNow);
            racer.SaveChanges();
        });

        await using var context = CreateContext(dbName, interceptor);
        var result = await CreateService(context).RefreshSessionAsync(token, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be("INVALID_SESSION");
        interceptor.SaveAttempts.Should().Be(1);

        await using var verify = CreateContext(dbName);
        var persisted = verify.UserSessions.Single(s => s.UserId == userId);
        persisted.TokenHash.Should().Be(Hash(winnerToken));
        persisted.TokenHash.Should().NotBe(Hash(token));
    }

    [Fact]
    public async Task RefreshSessionAsync_PersistentConcurrencyConflict_ReturnsInvalidSessionWithoutRetrying()
    {
        var dbName = NewDbName();
        var (_, token) = await SeedSessionAsync(dbName);

        var interceptor = new ConflictAlwaysInterceptor();
        await using var context = CreateContext(dbName, interceptor);

        var result = await CreateService(context).RefreshSessionAsync(token, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be("INVALID_SESSION");
        interceptor.SaveAttempts.Should().Be(1);
    }

    [Fact]
    public async Task RefreshSessionAsync_AfterConcurrencyConflict_LeavesContextCleanForSubsequentAuditWrite()
    {
        var dbName = NewDbName();
        var (userId, token) = await SeedSessionAsync(dbName);

        var interceptor = new ConflictOnModifiedSessionInterceptor();
        await using var context = CreateContext(dbName, interceptor);

        var result = await CreateService(context).RefreshSessionAsync(token, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be("INVALID_SESSION");
        context.ChangeTracker.Entries<UserSession>()
            .Should().NotContain(entry => entry.State == EntityState.Modified);

        var auditService = new AgentAuditService(context);
        var writeAuditLog = async () =>
            await auditService.RecordAsync(BuildRefreshAuditEntry(userId), CancellationToken.None);

        await writeAuditLog.Should().NotThrowAsync<DbUpdateConcurrencyException>();

        await using var verify = CreateContext(dbName);
        verify.AgentAuditLogs.Should().ContainSingle(log => log.UserId == userId);
    }

    private static async Task<(Guid UserId, string Token)> SeedSessionAsync(string dbName)
    {
        const string token = "shared-refresh-token";
        var user = User.Create("Alex", $"{Guid.NewGuid():N}@example.com").Value;
        var session = UserSession.Create(user.Id, Hash(token), DateTime.UtcNow.AddDays(90)).Value;

        await using var seed = CreateContext(dbName);
        seed.Users.Add(user);
        seed.UserSessions.Add(session);
        await seed.SaveChangesAsync();

        return (user.Id, token);
    }

    private static AuthSessionService CreateService(OrbitDbContext context)
    {
        var tokenService = Substitute.For<ITokenService>();
        tokenService.GenerateToken(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<Guid>()).Returns("access-token");

        return new AuthSessionService(
            new GenericRepository<UserSession>(context),
            new GenericRepository<UserSessionRefreshToken>(context),
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

    private static string NewDbName() => $"AuthSessionConcurrentRefresh_{Guid.NewGuid()}";

    private static AgentAuditEntry BuildRefreshAuditEntry(Guid userId) => new(
        userId,
        AgentCapabilityIds.AuthManage,
        "refresh_auth_session",
        AgentExecutionSurface.Metadata,
        AgentAuthMethod.Unknown,
        AgentRiskClass.Low,
        AgentPolicyDecisionStatus.Denied,
        AgentOperationStatus.Failed);

    private static string Hash(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private sealed class ConflictOnceInterceptor(Action onFirstSave) : SaveChangesInterceptor
    {
        public int SaveAttempts { get; private set; }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            SaveAttempts++;
            if (SaveAttempts == 1)
            {
                onFirstSave();
                throw new DbUpdateConcurrencyException("simulated stale token");
            }
            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }

    private sealed class HistoryConflictAfterConcurrentRefreshInterceptor(Func<Task> onFirstSave) : SaveChangesInterceptor
    {
        public int SaveAttempts { get; private set; }

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            SaveAttempts++;
            if (SaveAttempts == 1)
            {
                await onFirstSave();
                throw new DbUpdateException(
                    "duplicate history hash",
                    new PostgresException("duplicate history hash", "ERROR", "ERROR", PostgresErrorCodes.UniqueViolation));
            }

            return await base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }

    private sealed class ConflictAlwaysInterceptor : SaveChangesInterceptor
    {
        public int SaveAttempts { get; private set; }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            SaveAttempts++;
            throw new DbUpdateConcurrencyException("simulated stale token");
        }
    }

    private sealed class ConflictOnModifiedSessionInterceptor : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            var staleSessionTracked = eventData.Context!.ChangeTracker
                .Entries<UserSession>()
                .Any(entry => entry.State == EntityState.Modified);

            if (staleSessionTracked)
                throw new DbUpdateConcurrencyException("simulated stale token");

            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }
}
