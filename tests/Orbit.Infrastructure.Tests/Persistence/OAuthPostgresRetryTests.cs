using System.Data.Common;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;
using Npgsql.EntityFrameworkCore.PostgreSQL;
using NSubstitute;
using Orbit.Api.Extensions;
using Orbit.Api.OAuth;
using Orbit.Application.Auth.Commands;
using Orbit.Application.Auth.Models;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Persistence;
using Orbit.Infrastructure.Services;

namespace Orbit.Infrastructure.Tests.Persistence;

public partial class OAuthPostgresRetryTests
{
    private const string ConnectionVariable = "ORBIT_POSTGRES_TEST_CONNECTION";

    [PostgresFact]
    public Task DesktopHandoff_ConcurrentRedeems_CreateExactlyOneSession() =>
        WithDatabase(async db =>
        {
            const string redirect = "org.useorbit.desktop://auth/callback";
            const string verifier = "dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk";
            const string challenge = "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM";
            var user = User.Create("Desktop", "desktop@example.com").Value;
            db.Users.Add(user);
            await db.SaveChangesAsync();
            var create = new CreateDesktopHandoffCommandHandler(new DesktopHandoffStore(db), new GenericRepository<User>(db),
                new UnitOfWork(db, new DatabaseConnectionSettings()), Options.Create(new DesktopAuthOptions()), TimeProvider.System);
            var handoff = await create.Handle(new(user.Id, "state", challenge, "S256", redirect), default);
            handoff.IsSuccess.Should().BeTrue();
            var barrier = new DesktopConsumeBarrier();
            var options = new DbContextOptionsBuilder<OrbitDbContext>()
                .UseNpgsql(db.Database.GetConnectionString(), postgres => postgres.EnableRetryOnFailure(3, TimeSpan.FromSeconds(5), null))
                .AddInterceptors(barrier).Options;
            var tokens = Substitute.For<ITokenService>();
            tokens.GenerateToken(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<Guid>()).Returns("desktop-access");

            async Task<bool> Redeem()
            {
                await using var context = new OrbitDbContext(options);
                var work = new UnitOfWork(context, new DatabaseConnectionSettings());
                var users = new GenericRepository<User>(context);
                var sessions = new AuthSessionService(new GenericRepository<UserSession>(context),
                    new GenericRepository<UserSessionRefreshToken>(context), users, tokens, work, Options.Create(new JwtSettings { SecretKey = "desktop-test-secret", Issuer = "test", Audience = "test" }));
                var exchange = new ExchangeDesktopHandoffCommandHandler(new DesktopHandoffStore(context), users, sessions,
                    work, Options.Create(new DesktopAuthOptions()), TimeProvider.System);
                return (await exchange.Handle(new(handoff.Value.Code, "state", verifier, redirect), default)).IsSuccess;
            }

            var results = await Task.WhenAll(Redeem(), Redeem());

            barrier.Arrivals.Should().Be(2);
            results.Count(success => success).Should().Be(1);
            (await db.UserSessions.AsNoTracking().ToListAsync()).Should().ContainSingle().Which.UserId.Should().Be(user.Id);
            (await db.DesktopHandoffs.CountAsync()).Should().Be(0);
            tokens.Received(1).GenerateToken(user.Id, user.Email, Arg.Any<Guid>());
        });

    private sealed class DesktopConsumeBarrier : DbCommandInterceptor
    {
        private readonly TaskCompletionSource _bothRead = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _arrivals;
        public int Arrivals => _arrivals;

        public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
            DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("FROM \"DesktopHandoffs\"", StringComparison.Ordinal))
            {
                if (Interlocked.Increment(ref _arrivals) == 2) _bothRead.TrySetResult();
                await _bothRead.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            }
            return result;
        }
    }

    [PostgresFact]
    public Task Put_WithProductionRetryConfiguration_PreservesCapacityAndUncappedWrites() =>
        WithDatabase(db =>
        {
            var store = new OAuthStateStore(db, TimeProvider.System);
            var nowAtUtc = TimeProvider.System.GetUtcNow().UtcDateTime;
            var expiresAtUtc = nowAtUtc.AddMinutes(5);

            store.Put("expired", "google", "expired", nowAtUtc.AddMinutes(-1), 1).Should().BeTrue();
            store.Put("google", "google", "request", expiresAtUtc, 1).Should().BeTrue();
            store.Put("overflow", "google", "overflow", expiresAtUtc, 1).Should().BeFalse();
            store.Put("consent", "consent", "consent", expiresAtUtc).Should().BeTrue();
            store.Read("google", "google", nowAtUtc).Should().Be("request");
            store.Take("consent", "consent", nowAtUtc).Should().Be("consent");
            store.Take("consent", "consent", nowAtUtc).Should().BeNull();
            db.ChangeTracker.Entries().Should().BeEmpty();
            db.OAuthStates.AsNoTracking().Should().HaveCount(3);
            db.OAuthStates.AsNoTracking().Single(state => state.Kind == "consent").ConsumedAtUtc.Should().Be(nowAtUtc);
            return Task.CompletedTask;
        });

    [PostgresFact]
    public Task AuthorizationStore_WithProductionRetryConfiguration_CreatesGoogleAndEmailConsentState() =>
        WithDatabase(db =>
        {
            var store = new OAuthAuthorizationStore(new OAuthStateStore(db, TimeProvider.System), TimeProvider.System);
            var google = store.TryCreateGoogleRequest("client", "https://claude.ai/callback", "client-state",
                "challenge", null, "https://api-staging.useorbit.org/oauth/google/callback", "en");

            google.Should().NotBeNull();
            store.ConsumeGoogleRequest(google!.GoogleState).Should().BeEquivalentTo(google);
            store.ConsumeGoogleRequest(google.GoogleState).Should().BeNull();
            var emailConsent = new ConsentEntry(Guid.NewGuid(), "challenge", "https://claude.ai/callback",
                "client", "client-state", null, "en", ["read_habits"]);
            var ticket = store.CreatePendingConsent(emailConsent);
            store.ReadPendingConsent(ticket).Should().BeEquivalentTo(new ConsentTicket(emailConsent, false));
            store.ConsumePendingConsent(ticket).Should().BeEquivalentTo(new ConsentTicket(emailConsent, false));
            store.ConsumePendingConsent(ticket).Should().BeEquivalentTo(new ConsentTicket(emailConsent, true));
            db.OAuthStates.AsNoTracking().Should().ContainSingle().Which.ConsumedAtUtc.Should().NotBeNull();
            return Task.CompletedTask;
        });

    private static async Task WithDatabase(Func<OrbitDbContext, Task> operation)
    {
        var connection = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable(ConnectionVariable))
        {
            Database = $"orbit_oauth_test_{Guid.NewGuid():N}",
            Pooling = false
        };
        var builder = WebApplication.CreateBuilder();
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:DefaultConnection"] = connection.ConnectionString
        });
        builder.Services.AddHttpContextAccessor();
        builder.AddOrbitDatabase();
        await using var provider = builder.Services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrbitDbContext>();
        var strategy = db.Database.CreateExecutionStrategy().Should().BeOfType<NpgsqlRetryingExecutionStrategy>().Subject;
        strategy.MaxRetryCount.Should().Be(3);
        strategy.MaxRetryDelay.Should().Be(TimeSpan.FromSeconds(5));

        try
        {
            await db.Database.MigrateAsync();
            await operation(db);
        }
        finally
        {
            await db.Database.EnsureDeletedAsync();
        }
    }

    private sealed class PostgresFactAttribute : FactAttribute
    {
        public PostgresFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ConnectionVariable)))
                Skip = $"Set {ConnectionVariable} to a disposable PostgreSQL server with database creation permission.";
        }
    }
}
