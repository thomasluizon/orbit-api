using System.Diagnostics;
using Hangfire;
using Orbit.Application.Auth.Commands;
using Orbit.Application.Auth.Services;
using System.Text.Encodings.Web;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orbit.Api.Authentication;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Events;
using Orbit.Infrastructure.Persistence;
using Orbit.Infrastructure.Services;
using Orbit.Infrastructure.Tests.Persistence;
using Xunit.Abstractions;

namespace Orbit.Infrastructure.Tests.Authentication;

public class AuthenticationCacheTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("revoke")]
    [InlineData("downgrade")]
    [InlineData("deactivate")]
    public async Task RepeatedKeyRequest_ReusesVerificationUntilACommittedCredentialOrPlanChange(string change)
    {
        using var memory = new MemoryCache(new MemoryCacheOptions());
        var cache = new AuthValidationCache(memory, Options.Create(new AuthCacheSettings()), TimeProvider.System);
        using var bus = new InMemoryAccountEventBus();
        var collector = new AccountEventCollector(bus, new HttpContextAccessor(), cache);
        using var factory = new SqliteOrbitDbContextFactory(collector, new AccountEventTransactionInterceptor(collector));
        var user = User.Create("User", "auth-cache@example.com").Value;
        user.StartTrial(DateTime.UtcNow.AddDays(-1));
        user.SetStripeSubscription("subscription", DateTime.UtcNow.AddDays(30));
        var (key, raw) = ApiKey.Create(user.Id, "Agent").Value;
        factory.Context.AddRange(user, key);
        await factory.Context.SaveChangesAsync();
        var store = new CountingStore(factory);
        using var provider = new ServiceCollection().AddSingleton<IApiKeyAuthenticationStore>(store)
            .AddSingleton(cache).AddSingleton(Substitute.For<IApiKeyUsageTracker>()).BuildServiceProvider();
        var baseline = Stopwatch.StartNew();
        key.Verify(raw).Should().BeTrue();
        key.Verify(raw).Should().BeTrue();
        baseline.Stop();
        var first = Stopwatch.StartNew();
        (await Authenticate(provider, raw)).Succeeded.Should().BeTrue();
        first.Stop();
        var repeated = Stopwatch.StartNew();
        (await Authenticate(provider, raw)).Succeeded.Should().BeTrue();
        repeated.Stop();
        store.Reads.Should().Be(1, "the second request must skip the key query and BCrypt verification");
        output.WriteLine("BCrypt prefix {0}; two uncached verifies {1:F2} ms; first request {2:F2} ms; cached request {3:F2} ms",
            key.KeyHash[..7], baseline.Elapsed.TotalMilliseconds, first.Elapsed.TotalMilliseconds, repeated.Elapsed.TotalMilliseconds);
        using (var mutation = factory.CreateContext())
        {
            if (change == "revoke") (await mutation.ApiKeys.SingleAsync()).Revoke();
            else
            {
                var owner = await mutation.Users.SingleAsync();
                if (change == "deactivate") owner.Deactivate(DateTime.UtcNow.AddDays(7));
                else owner.CancelStripeSubscription(Orbit.Domain.Enums.SubscriptionLapseReason.Canceled);
            }
            await mutation.SaveChangesAsync();
        }
        (await Authenticate(provider, raw)).Succeeded.Should().BeFalse();
        store.Reads.Should().Be(2);
    }

    [Fact]
    public async Task ActiveSession_IsCachedAndARevokeInvalidatesTheNextValidation()
    {
        using var memory = new MemoryCache(new MemoryCacheOptions());
        var cache = new AuthValidationCache(memory, Options.Create(new AuthCacheSettings()), TimeProvider.System);
        using var bus = new InMemoryAccountEventBus();
        var collector = new AccountEventCollector(bus, new HttpContextAccessor(), cache);
        var commands = new CountingDbCommandInterceptor();
        using var factory = new SqliteOrbitDbContextFactory(collector, new AccountEventTransactionInterceptor(collector), commands);
        var user = User.Create("User", "session-cache@example.com").Value;
        factory.Context.Users.Add(user);
        await factory.Context.SaveChangesAsync();
        using var context = factory.CreateContext();
        var service = new AuthSessionService(new GenericRepository<UserSession>(context), new GenericRepository<UserSessionRefreshToken>(context),
            new GenericRepository<User>(context), Substitute.For<ITokenService>(), new UnitOfWork(context, new DatabaseConnectionSettings()),
            Options.Create(new JwtSettings { SecretKey = new string('s', 48), Issuer = "test", Audience = "test" }), cache);
        var tokens = await service.CreateSessionAsync(user.Id, user.Email);
        var session = await context.UserSessions.SingleAsync();
        commands.Reset();
        (await service.IsSessionActiveAsync(session.Id, user.Id)).Should().BeTrue();
        (await service.IsSessionActiveAsync(session.Id, user.Id)).Should().BeTrue();
        commands.SelectCommandCount.Should().Be(1);
        (await service.RevokeSessionAsync(tokens.Value.RefreshToken)).IsSuccess.Should().BeTrue();
        (await service.IsSessionActiveAsync(session.Id, user.Id)).Should().BeFalse();
    }

    [Fact]
    public async Task DisabledCacheAndExpiryAndInvalidationDuringAReadCannotAdmitAnEntry()
    {
        using var memory = new MemoryCache(new MemoryCacheOptions());
        var settings = new AuthCacheSettings { ApiKeyTtlSeconds = 0, SessionTtlSeconds = 0 };
        var disabled = new AuthValidationCache(memory, Options.Create(settings), TimeProvider.System);
        var userId = Guid.NewGuid();
        disabled.Set("disabled", userId, new object(), disabled.Epoch, session: false);
        disabled.TryGet<object>("disabled", out _).Should().BeFalse();
        var enabled = new AuthValidationCache(memory, Options.Create(new AuthCacheSettings()), TimeProvider.System);
        var epoch = enabled.Epoch;
        enabled.InvalidateUser(userId);
        enabled.Set("racing", userId, new object(), epoch, session: false);
        enabled.TryGet<object>("racing", out _).Should().BeFalse();
        enabled.Set("expiring", userId, new object(), enabled.Epoch, session: false, DateTime.UtcNow.AddMilliseconds(50));
        enabled.TryGet<object>("expiring", out _).Should().BeTrue();
        await Task.Delay(100);
        enabled.TryGet<object>("expiring", out _).Should().BeFalse();
    }

    [Fact]
    public async Task LogoutAll_RevokesOAuthKeysAndSessionsAndPreservesPersonalKeys()
    {
        using var memory = new MemoryCache(new MemoryCacheOptions());
        var cache = new AuthValidationCache(memory, Options.Create(new AuthCacheSettings()), TimeProvider.System);
        using var bus = new InMemoryAccountEventBus();
        var collector = new AccountEventCollector(bus, new HttpContextAccessor(), cache);
        using var factory = new SqliteOrbitDbContextFactory(collector, new AccountEventTransactionInterceptor(collector));
        var user = User.Create("User", "logout-all@example.com").Value;
        var personal = ApiKey.Create(user.Id, "Personal").Value;
        var oauth = ApiKey.Create(user.Id, "OAuth", oauthFamilyId: Guid.NewGuid()).Value;
        factory.Context.AddRange(user, personal.Entity, oauth.Entity);
        await factory.Context.SaveChangesAsync();
        using var db = factory.CreateContext();
        var sessions = Sessions(db, cache);
        var tokens = await sessions.CreateSessionAsync(user.Id, user.Email);
        var session = await db.UserSessions.SingleAsync();
        (await sessions.IsSessionActiveAsync(session.Id, user.Id)).Should().BeTrue();
        using var provider = new ServiceCollection().AddSingleton<IApiKeyAuthenticationStore>(new CountingStore(factory))
            .AddSingleton(cache).AddSingleton(Substitute.For<IApiKeyUsageTracker>()).BuildServiceProvider();
        (await Authenticate(provider, personal.RawKey)).Succeeded.Should().BeTrue();
        (await Authenticate(provider, oauth.RawKey)).Succeeded.Should().BeTrue();
        var handler = new LogoutAllSessionsCommandHandler(sessions, new GenericRepository<ApiKey>(db), new UnitOfWork(db, new DatabaseConnectionSettings()));
        (await handler.Handle(new(user.Id), default)).IsSuccess.Should().BeTrue();
        (await sessions.IsSessionActiveAsync(session.Id, user.Id)).Should().BeFalse();
        (await Authenticate(provider, personal.RawKey)).Succeeded.Should().BeTrue();
        (await Authenticate(provider, oauth.RawKey)).Succeeded.Should().BeFalse();
    }

    [Fact]
    public async Task ConfirmedDeletion_RevokesEverySessionAndKeyAndRejectsTheCachedSession()
    {
        using var memory = new MemoryCache(new MemoryCacheOptions());
        var cache = new AuthValidationCache(memory, Options.Create(new AuthCacheSettings()), TimeProvider.System);
        using var bus = new InMemoryAccountEventBus();
        var collector = new AccountEventCollector(bus, new HttpContextAccessor(), cache);
        using var factory = new SqliteOrbitDbContextFactory(collector, new AccountEventTransactionInterceptor(collector));
        var user = User.Create("User", "deletion@example.com").Value;
        factory.Context.AddRange(user, ApiKey.Create(user.Id, "Personal").Value.Entity,
            ApiKey.Create(user.Id, "OAuth", oauthFamilyId: Guid.NewGuid()).Value.Entity);
        await factory.Context.SaveChangesAsync();
        using var db = factory.CreateContext();
        using var challengeDb = factory.CreateContext();
        var sessions = Sessions(db, cache);
        await sessions.CreateSessionAsync(user.Id, user.Email);
        await sessions.CreateSessionAsync(user.Id, user.Email);
        var sessionIds = await db.UserSessions.Select(row => row.Id).ToListAsync();
        foreach (var id in sessionIds) (await sessions.IsSessionActiveAsync(id, user.Id)).Should().BeTrue();
        var challenge = new PostgresEmailChallengeStore(challengeDb, new UnitOfWork(challengeDb, new DatabaseConnectionSettings()),
            Options.Create(new JwtSettings { SecretKey = new string('s', 48), Issuer = "test", Audience = "test" }), TimeProvider.System);
        (await challenge.IssueAsync(EmailChallengeOperation.AccountDeletion, user.Email, "123456")).IsSuccess.Should().BeTrue();
        var handler = new ConfirmAccountDeletionCommandHandler(new EmailChallengeService(challenge), new GenericRepository<User>(db),
            new UnitOfWork(db, new DatabaseConnectionSettings()), Substitute.For<IBackgroundJobClient>(), sessions, new GenericRepository<ApiKey>(db));
        (await handler.Handle(new(user.Id, "123456"), default)).IsSuccess.Should().BeTrue();
        (await db.Users.IgnoreQueryFilters().SingleAsync()).IsDeactivated.Should().BeTrue();
        (await db.ApiKeys.AsNoTracking().ToListAsync()).Should().OnlyContain(key => key.IsRevoked);
        (await db.UserSessions.AsNoTracking().ToListAsync()).Should().OnlyContain(row => row.RevokedAtUtc != null);
        foreach (var id in sessionIds) (await sessions.IsSessionActiveAsync(id, user.Id)).Should().BeFalse();
    }

    private static AuthSessionService Sessions(OrbitDbContext db, AuthValidationCache cache) => new(
        new GenericRepository<UserSession>(db), new GenericRepository<UserSessionRefreshToken>(db), new GenericRepository<User>(db),
        Substitute.For<ITokenService>(), new UnitOfWork(db, new DatabaseConnectionSettings()),
        Options.Create(new JwtSettings { SecretKey = new string('s', 48), Issuer = "test", Audience = "test" }), cache);

    private static async Task<AuthenticateResult> Authenticate(IServiceProvider provider, string key)
    {
        var options = Substitute.For<IOptionsMonitor<AuthenticationSchemeOptions>>();
        options.Get(Arg.Any<string>()).Returns(new AuthenticationSchemeOptions());
        var handler = new ApiKeyAuthenticationHandler(options, NullLoggerFactory.Instance, UrlEncoder.Default, provider);
        var context = new DefaultHttpContext();
        context.Request.Path = "/mcp";
        context.Request.Headers.Authorization = "Bearer " + key;
        await handler.InitializeAsync(new AuthenticationScheme("ApiKey", "ApiKey", typeof(ApiKeyAuthenticationHandler)), context);
        return await handler.AuthenticateAsync();
    }

    private sealed class CountingStore(SqliteOrbitDbContextFactory factory) : IApiKeyAuthenticationStore
    {
        public int Reads { get; private set; }
        public async Task<ApiKeyAuthenticationCandidate?> FindAsync(string prefix, CancellationToken cancellationToken)
        {
            Reads++;
            using var db = factory.CreateContext();
            return await new ApiKeyAuthenticationStore(db, TimeProvider.System).FindAsync(prefix, cancellationToken);
        }
    }
}
