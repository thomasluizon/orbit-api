using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Orbit.Api.OAuth;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using Orbit.Infrastructure.Persistence;
using Orbit.Infrastructure.Tests.Persistence;

namespace Orbit.Infrastructure.Tests.Controllers;

public partial class OAuthRefreshRaceTests
{
    [Fact]
    public async Task Refresh_FirstPassConcurrencyConflict_ReplaysToOwnSuccessor()
    {
        using var database = new SqliteOrbitDbContextFactory();
        var db = database.Context;
        var user = User.Create("Refresh", "replay@example.com").Value;
        var first = ApiKey.Create(user.Id, "Claude.ai", [AgentScopes.ReadHabits],
            oauthFamilyId: Guid.NewGuid(), oauthClientId: OAuthTestClient.ClientId).Value.Entity;
        db.AddRange(user, first);
        await db.SaveChangesAsync();
        var store = new OAuthAuthorizationStore(new OAuthStateStore(db, TimeProvider.System), TimeProvider.System);
        var refresh = store.CreateRefreshToken(new(user.Id, first.Id, OAuthTestClient.ClientId, first.OAuthFamilyId!.Value));
        var work = Substitute.For<IUnitOfWork>();
        work.SaveChangesAsync(Arg.Any<CancellationToken>()).Returns(call => db.SaveChangesAsync(call.Arg<CancellationToken>()));
        var passes = 0;
        work.ExecuteReplayableInTransactionAsync(Arg.Any<Func<CancellationToken, Task<IActionResult>>>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                var operation = call.Arg<Func<CancellationToken, Task<IActionResult>>>();
                try
                {
                    passes++;
                    await operation(default);
                    throw new DbUpdateConcurrencyException("Replay after the first persisted rotation");
                }
                catch (DbUpdateConcurrencyException)
                {
                    passes++;
                    return await operation(default);
                }
            });
        var controller = Controller(store, new GenericRepository<ApiKey>(db), work);

        var result = (await controller.Token("refresh_token", null, null, null, default, refresh, OAuthTestClient.ClientId))
            .Should().BeOfType<OkObjectResult>().Subject;

        passes.Should().Be(2);
        var payload = JsonSerializer.SerializeToElement(result.Value);
        var active = (await db.ApiKeys.AsNoTracking().Where(key => key.OAuthFamilyId == first.OAuthFamilyId && !key.IsRevoked).ToListAsync())
            .Should().ContainSingle().Subject;
        active.Verify(payload.GetProperty("access_token").GetString()!).Should().BeTrue();
        store.ReadRefreshToken(payload.GetProperty("refresh_token").GetString()!)!.ApiKeyId.Should().Be(active.Id);
        (await db.ApiKeys.AsNoTracking().CountAsync(key => key.IsRevoked)).Should().Be(2);
    }
}
