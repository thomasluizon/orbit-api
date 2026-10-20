using System.Linq.Expressions;
using System.Text.Json;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orbit.Api.Controllers;
using Orbit.Api.OAuth;
using Orbit.Application.ApiKeys.Commands;
using Orbit.Application.ApiKeys.Services;
using Orbit.Application.Auth.Services;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Persistence;
using Orbit.Infrastructure.Tests.Persistence;

namespace Orbit.Infrastructure.Tests.Controllers;

public class OAuthRefreshRaceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Refresh_RevocationOrRotationCommitsAfterTrackedRead_DoesNotIssueUsableSuccessor(bool rotate)
    {
        using var database = new SqliteOrbitDbContextFactory();
        var db = database.Context;
        var user = User.Create("Refresh", "refresh@example.com").Value;
        var key = ApiKey.Create(user.Id, "Claude.ai", [AgentScopes.ReadHabits]).Value.Entity;
        db.AddRange(user, key);
        await db.SaveChangesAsync();
        var store = new OAuthAuthorizationStore(new OAuthStateStore(db, TimeProvider.System), TimeProvider.System);
        var refresh = store.CreateRefreshToken(new(user.Id, key.Id, "client"));
        var repository = new GenericRepository<ApiKey>(db);
        var interleaved = Substitute.For<IGenericRepository<ApiKey>>();
        interleaved.GetByIdAsync(key.Id, Arg.Any<CancellationToken>()).Returns(call => repository.GetByIdAsync(key.Id, call.Arg<CancellationToken>()));
        interleaved.FindOneTrackedAsync(Arg.Any<Expression<Func<ApiKey, bool>>>(), Arg.Any<Func<IQueryable<ApiKey>, IQueryable<ApiKey>>?>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                var tracked = await repository.FindOneTrackedAsync(call.Arg<Expression<Func<ApiKey, bool>>>());
                using var revokeDb = database.CreateContext();
                if (rotate)
                {
                    var winnerStore = new OAuthAuthorizationStore(new OAuthStateStore(revokeDb, TimeProvider.System), TimeProvider.System);
                    var winner = Controller(winnerStore, new GenericRepository<ApiKey>(revokeDb), new UnitOfWork(revokeDb, new DatabaseConnectionSettings()));
                    (await winner.Token("refresh_token", null, null, null, default, refresh)).Should().BeOfType<OkObjectResult>();
                    return tracked;
                }
                var payGate = Substitute.For<IPayGateService>();
                payGate.CanManageApiKeys(user.Id, Arg.Any<CancellationToken>()).Returns(Result.Success());
                var appConfig = Substitute.For<IAppConfigService>();
                using var cache = new MemoryCache(new MemoryCacheOptions());
                var authorization = new ApiKeyManagementAuthorization(appConfig, new EmailChallengeService(cache, TimeProvider.System));
                var revoker = new RevokeApiKeyCommandHandler(new GenericRepository<ApiKey>(revokeDb), payGate,
                    new UnitOfWork(revokeDb, new DatabaseConnectionSettings()), cache, authorization);
                (await revoker.Handle(new(user.Id, key.Id), default)).IsSuccess.Should().BeTrue();
                return tracked;
            });
        interleaved.FindTrackedAsync(Arg.Any<Expression<Func<ApiKey, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(call => repository.FindTrackedAsync(call.Arg<Expression<Func<ApiKey, bool>>>(), call.Arg<CancellationToken>()));
        interleaved.AddAsync(Arg.Any<ApiKey>(), Arg.Any<CancellationToken>()).Returns(call => repository.AddAsync(call.Arg<ApiKey>(), call.Arg<CancellationToken>()));
        var controller = Controller(store, interleaved, new UnitOfWork(db, new DatabaseConnectionSettings()));

        var result = await controller.Token("refresh_token", null, null, null, default, refresh);

        result.Should().BeOfType<BadRequestObjectResult>();
        using var verify = database.CreateContext();
        (await verify.ApiKeys.ToListAsync()).Should().OnlyContain(candidate => candidate.IsRevoked);
    }

    [Fact]
    public async Task Refresh_ConsumedTokenReuse_RevokesOnlyItsFamily_UnknownTokenRevokesNothing()
    {
        using var database = new SqliteOrbitDbContextFactory();
        var db = database.Context;
        var user = User.Create("Refresh", "refresh@example.com").Value;
        var first = ApiKey.Create(user.Id, "Claude.ai", [AgentScopes.ReadHabits]).Value.Entity;
        var unrelated = ApiKey.Create(user.Id, "Other client", [AgentScopes.ReadHabits]).Value.Entity;
        db.AddRange(user, first, unrelated);
        await db.SaveChangesAsync();
        var store = new OAuthAuthorizationStore(new OAuthStateStore(db, TimeProvider.System), TimeProvider.System);
        var original = store.CreateRefreshToken(new(user.Id, first.Id, "client"));
        var other = store.CreateRefreshToken(new(user.Id, unrelated.Id, "other-client"));
        var controller = Controller(store, new GenericRepository<ApiKey>(db), new UnitOfWork(db, new DatabaseConnectionSettings()));
        var rotated = (OkObjectResult)await controller.Token("refresh_token", null, null, null, default, original);
        var successor = JsonSerializer.SerializeToElement(rotated.Value).GetProperty("refresh_token").GetString()!;

        (await controller.Token("refresh_token", null, null, null, default, "unknown")).Should().BeOfType<BadRequestObjectResult>();
        db.ApiKeys.AsNoTracking().Count(key => !key.IsRevoked).Should().Be(2);
        (await controller.Token("refresh_token", null, null, null, default, original)).Should().BeOfType<BadRequestObjectResult>();
        db.ApiKeys.AsNoTracking().Where(key => !key.IsRevoked).Select(key => key.Id).Should().Equal(unrelated.Id);
        (await controller.Token("refresh_token", null, null, null, default, successor)).Should().BeOfType<BadRequestObjectResult>();
        (await controller.Token("refresh_token", null, null, null, default, other)).Should().BeOfType<OkObjectResult>();
    }

    private static OAuthController Controller(OAuthAuthorizationStore store, IGenericRepository<ApiKey> keys, IUnitOfWork work) =>
        new(Substitute.For<IMediator>(), store, keys, work, Options.Create(new GoogleSettings()),
            new ConfigurationBuilder().Build(), NullLogger<OAuthController>.Instance)
        { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() } };
}
