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
