using System.Data.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.Persistence;

namespace Orbit.Infrastructure.Tests.Persistence;

public class OAuthConsentStateTests
{
    [Fact]
    public void Take_Consent_RetainsUsedRowUntilExpiry()
    {
        using var database = new SqliteOrbitDbContextFactory();
        var store = new OAuthStateStore(database.Context, TimeProvider.System);
        var nowAtUtc = DateTime.UtcNow;
        var expiresAtUtc = nowAtUtc.AddMinutes(5);
        store.Put("ticket", "consent", "payload", expiresAtUtc).Should().BeTrue();
        store.ReadState("ticket", "consent", nowAtUtc).Should().Be(new OAuthStateSnapshot("payload", false));

        store.Take("ticket", "consent", nowAtUtc).Should().Be("payload");
        store.Take("ticket", "consent", nowAtUtc).Should().BeNull();
        store.Read("ticket", "consent", nowAtUtc).Should().Be("payload");
        store.ReadState("ticket", "consent", nowAtUtc).Should().Be(new OAuthStateSnapshot("payload", true));
        database.Context.OAuthStates.AsNoTracking().Should().ContainSingle().Which.ConsumedAtUtc.Should().Be(nowAtUtc);
        store.RemoveExpired(expiresAtUtc.AddTicks(-1));
        database.Context.OAuthStates.AsNoTracking().Should().ContainSingle();
        store.Read("ticket", "consent", expiresAtUtc).Should().BeNull();
        store.ReadState("ticket", "consent", expiresAtUtc).Should().BeNull();
        store.Take("ticket", "consent", expiresAtUtc).Should().BeNull();
        store.RemoveExpired(expiresAtUtc);
        database.Context.OAuthStates.AsNoTracking().Should().BeEmpty();
    }

    [Fact]
    public async Task Take_Consent_ConcurrentConsumersHaveOneWinner_AndRetainUsedRow()
    {
        using var overlap = new ConcurrentReadProbe();
        using var database = new SqliteOrbitDbContextFactory(
            Path.Combine(Path.GetTempPath(), $"orbit-consent-{Guid.NewGuid():N}.db"), overlap);
        var nowAtUtc = DateTime.UtcNow;
        new OAuthStateStore(database.Context, TimeProvider.System)
            .Put("ticket", "consent", "payload", nowAtUtc.AddMinutes(5)).Should().BeTrue();
        overlap.Enabled = true;

        string? Consume()
        {
            using var context = database.CreateContext();
            return new OAuthStateStore(context, TimeProvider.System).Take("ticket", "consent", nowAtUtc);
        }

        var results = await Task.WhenAll(Task.Run(Consume), Task.Run(Consume));
        overlap.Enabled = false;
        results.Should().ContainSingle(value => value == "payload");
        results.Should().ContainSingle(value => value == null);
        database.Context.OAuthStates.AsNoTracking().Should().ContainSingle().Which.ConsumedAtUtc.Should().Be(nowAtUtc);
    }

    private sealed class ConcurrentReadProbe : DbCommandInterceptor, IDisposable
    {
        private readonly Barrier _readers = new(2);
        public bool Enabled { get; set; }

        public override DbDataReader ReaderExecuted(DbCommand command, CommandExecutedEventData eventData, DbDataReader result)
        {
            if (Enabled && command.CommandText.StartsWith("SELECT", StringComparison.Ordinal)
                && command.CommandText.Contains("\"SecretHash\"", StringComparison.Ordinal))
                _readers.SignalAndWait(TimeSpan.FromSeconds(10)).Should().BeTrue();
            return result;
        }

        public void Dispose() => _readers.Dispose();
    }
}
