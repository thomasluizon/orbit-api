using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Npgsql.EntityFrameworkCore.PostgreSQL;
using Orbit.Api.Extensions;
using Orbit.Api.OAuth;
using Orbit.Infrastructure.Persistence;

namespace Orbit.Infrastructure.Tests.Persistence;

public class OAuthPostgresRetryTests
{
    private const string ConnectionVariable = "ORBIT_POSTGRES_TEST_CONNECTION";

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
