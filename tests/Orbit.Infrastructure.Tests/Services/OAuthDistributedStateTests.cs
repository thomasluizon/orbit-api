using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Orbit.Api.OAuth;
using Orbit.Infrastructure.Persistence;
using Orbit.Infrastructure.Tests.Persistence;

namespace Orbit.Infrastructure.Tests.Services;

public class OAuthDistributedStateTests
{
    [Fact]
    public void ConsumedRefreshLineage_RemainsHashedAndReadableAcrossInstances_UntilFamilyExpiry()
    {
        using var database = new SqliteOrbitDbContextFactory();
        using var firstDb = database.CreateContext();
        using var secondDb = database.CreateContext();
        var clock = new RefreshClock();
        var first = new OAuthAuthorizationStore(new OAuthStateStore(firstDb, clock), clock);
        var second = new OAuthAuthorizationStore(new OAuthStateStore(secondDb, clock), clock);
        var token = first.CreateRefreshToken(new(Guid.NewGuid(), Guid.NewGuid(), "client"));

        var original = second.ConsumeRefreshToken(token)!;
        first.ConsumeRefreshToken(token).Should().BeNull();
        first.ReadRefreshToken(token).Should().Be(original);
        firstDb.OAuthStates.Single().SecretHash.Should().NotBe(token).And.HaveLength(64);
        firstDb.OAuthStates.Single().ConsumedAtUtc.Should().NotBeNull();
        var successor = second.CreateRefreshToken(original with { ApiKeyId = Guid.NewGuid() });
        second.ReadRefreshToken(successor)!.FamilyId.Should().Be(original.FamilyId);
        second.ReadRefreshToken(successor)!.FamilyExpiresAtUtc.Should().Be(original.FamilyExpiresAtUtc);
        clock.Advance(TimeSpan.FromDays(31));
        first.ReadRefreshToken(token).Should().BeNull();
        second.ConsumeRefreshToken(successor).Should().BeNull();
    }

    private sealed class RefreshClock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now += duration;
    }

    [Fact]
    public void CodeCreatedByOneInstance_IsConsumedOnceByAnother()
    {
        using var database = new SqliteOrbitDbContextFactory();
        using var firstDb = database.CreateContext();
        using var secondDb = database.CreateContext();
        var first = new OAuthAuthorizationStore(new OAuthStateStore(firstDb, TimeProvider.System), TimeProvider.System);
        var second = new OAuthAuthorizationStore(new OAuthStateStore(secondDb, TimeProvider.System), TimeProvider.System);
        const string verifier = "a-client-generated-pkce-verifier";
        var challenge = Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))).Replace("+", "-").Replace("/", "_").TrimEnd('=');
        var code = first.CreateCode(Guid.NewGuid(), challenge, "https://claude.ai/callback", "client", scopes: ["read_habits"], isReadOnly: true);
        second.ExchangeCode(code, verifier, "https://claude.ai/callback")!.Scopes.Should().Equal("read_habits");
        first.ExchangeCode(code, verifier, "https://claude.ai/callback").Should().BeNull();
        firstDb.OAuthStates.Should().BeEmpty();
    }
}
