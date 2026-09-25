using System.IdentityModel.Tokens.Jwt;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Options;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Persistence;
using Orbit.Infrastructure.Services;

namespace Orbit.Infrastructure.Tests.Services;

public class AuthSessionServiceLogoutRaceTests
{
    [Fact]
    public async Task RefreshFinishesAfterLogout_ReturnedRefreshAndAccessTokensAreInactive()
    {
        var databaseName = Guid.NewGuid().ToString();
        var (user, session, originalToken) = await SeedAsync(databaseName);
        var blockingTokens = new BlockingTokenService(TokenService());

        await using var refreshContext = CreateContext(databaseName);
        var refreshTask = Task.Run(() => CreateService(refreshContext, blockingTokens)
            .RefreshSessionAsync(originalToken));

        await blockingTokens.Entered.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            await using var logoutContext = CreateContext(databaseName);
            var logout = await CreateService(logoutContext, TokenService()).RevokeSessionAsync(originalToken);
            logout.IsSuccess.Should().BeTrue();
        }
        finally
        {
            blockingTokens.Release();
        }

        var refresh = await refreshTask;
        refresh.IsSuccess.Should().BeTrue();
        new JwtSecurityTokenHandler().ReadJwtToken(refresh.Value.AccessToken).Claims
            .Should().Contain(claim => claim.Type == "orbit_session_id" && claim.Value == session.Id.ToString());

        await using var verifyContext = CreateContext(databaseName);
        var verifyService = CreateService(verifyContext, TokenService());
        (await verifyService.IsSessionActiveAsync(session.Id, user.Id)).Should().BeFalse();
        (await verifyService.RefreshSessionAsync(refresh.Value.RefreshToken)).IsFailure.Should().BeTrue();
        verifyContext.UserSessionRefreshTokens.Should().ContainSingle(token =>
            token.UserSessionId == session.Id && token.TokenHash == Hash(originalToken));
    }

    [Fact]
    public async Task LogoutOneDevice_PreservesOtherDeviceAndNormalRefresh()
    {
        var databaseName = Guid.NewGuid().ToString();
        var (user, firstSession, firstToken) = await SeedAsync(databaseName);
        const string secondToken = "second-device-token";
        var secondSession = UserSession.Create(user.Id, Hash(secondToken), null).Value;
        await using (var seed = CreateContext(databaseName))
        {
            seed.UserSessions.Add(secondSession);
            await seed.SaveChangesAsync();
        }

        await using var context = CreateContext(databaseName);
        var service = CreateService(context, TokenService());
        var normalRefresh = await service.RefreshSessionAsync(secondToken);
        normalRefresh.IsSuccess.Should().BeTrue();

        var logout = await service.RevokeSessionAsync(firstToken);
        logout.IsSuccess.Should().BeTrue();
        (await service.IsSessionActiveAsync(firstSession.Id, user.Id)).Should().BeFalse();
        (await service.IsSessionActiveAsync(secondSession.Id, user.Id)).Should().BeTrue();
        (await service.RefreshSessionAsync(normalRefresh.Value.RefreshToken)).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task LogoutWithOldestToken_RevokesFamilyAfterMultipleRotations()
    {
        var databaseName = Guid.NewGuid().ToString();
        var (user, session, originalToken) = await SeedAsync(databaseName);
        await using var context = CreateContext(databaseName);
        var service = CreateService(context, TokenService());

        var firstRefresh = await service.RefreshSessionAsync(originalToken);
        var secondRefresh = await service.RefreshSessionAsync(firstRefresh.Value.RefreshToken);
        var logout = await service.RevokeSessionAsync(originalToken);

        logout.IsSuccess.Should().BeTrue();
        (await service.IsSessionActiveAsync(session.Id, user.Id)).Should().BeFalse();
        (await service.RefreshSessionAsync(secondRefresh.Value.RefreshToken)).IsFailure.Should().BeTrue();
        context.UserSessionRefreshTokens.Count().Should().Be(2);
    }

    [Fact]
    public async Task LogoutLosesInitialWriteRaceToRefresh_RetriesWithHistoricalToken()
    {
        var databaseName = Guid.NewGuid().ToString();
        var (user, session, originalToken) = await SeedAsync(databaseName);
        var interceptor = new RefreshBeforeLogoutSaveInterceptor(async () =>
        {
            await using var racer = CreateContext(databaseName);
            var refresh = await CreateService(racer, TokenService()).RefreshSessionAsync(originalToken);
            refresh.IsSuccess.Should().BeTrue();
        });

        await using var context = CreateContext(databaseName, interceptor);
        var logout = await CreateService(context, TokenService()).RevokeSessionAsync(originalToken);

        logout.IsSuccess.Should().BeTrue();
        interceptor.SaveAttempts.Should().Be(2);
        await using var verify = CreateContext(databaseName);
        (await CreateService(verify, TokenService()).IsSessionActiveAsync(session.Id, user.Id)).Should().BeFalse();
    }

    private static async Task<(User User, UserSession Session, string Token)> SeedAsync(string databaseName)
    {
        const string token = "original-device-token";
        var user = User.Create("Thomas", $"{Guid.NewGuid():N}@example.com").Value;
        var session = UserSession.Create(user.Id, Hash(token), null).Value;
        await using var context = CreateContext(databaseName);
        context.Users.Add(user);
        context.UserSessions.Add(session);
        await context.SaveChangesAsync();
        return (user, session, token);
    }

    private static OrbitDbContext CreateContext(string databaseName, ISaveChangesInterceptor? interceptor = null)
    {
        var options = new DbContextOptionsBuilder<OrbitDbContext>().UseInMemoryDatabase(databaseName);
        if (interceptor is not null)
            options.AddInterceptors(interceptor);
        return new OrbitDbContext(options.Options);
    }

    private static AuthSessionService CreateService(OrbitDbContext context, ITokenService tokenService) =>
        new(
            new GenericRepository<UserSession>(context),
            new GenericRepository<UserSessionRefreshToken>(context),
            new GenericRepository<User>(context),
            tokenService,
            new UnitOfWork(context, new DatabaseConnectionSettings()),
            Options.Create(Settings()));

    private static JwtTokenService TokenService() => new(Options.Create(Settings()));

    private static JwtSettings Settings() => new()
    {
        SecretKey = "test-secret-key-that-is-at-least-32-bytes-long-for-hmac",
        Issuer = "test-issuer",
        Audience = "test-audience",
        ExpiryHours = 1,
        RefreshExpiryDays = 90
    };

    private static string Hash(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private sealed class BlockingTokenService(ITokenService inner) : ITokenService
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Entered => _entered.Task;

        public void Release() => _release.TrySetResult();

        public string GenerateToken(Guid userId, string email, Guid sessionId)
        {
            _entered.TrySetResult();
            if (!_release.Task.Wait(TimeSpan.FromSeconds(10)))
                throw new TimeoutException("Refresh token issuance did not resume.");
            return inner.GenerateToken(userId, email, sessionId);
        }
    }

    private sealed class RefreshBeforeLogoutSaveInterceptor(Func<Task> refresh) : SaveChangesInterceptor
    {
        public int SaveAttempts { get; private set; }

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            SaveAttempts++;
            if (SaveAttempts == 1)
            {
                await refresh();
                throw new DbUpdateConcurrencyException("simulated stale session row");
            }

            return await base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }
}
