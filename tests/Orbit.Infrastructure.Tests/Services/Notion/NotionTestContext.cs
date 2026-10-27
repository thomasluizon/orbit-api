using MediatR;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NSubstitute;
using Orbit.Api.Controllers;
using Orbit.Application.Common;
using Orbit.Application.Connections;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Persistence;
using Orbit.Infrastructure.Services;
using Orbit.Infrastructure.Services.Notion;
using Orbit.Infrastructure.Tests.Persistence;

namespace Orbit.Infrastructure.Tests.Services.Notion;

internal sealed class NotionTestContext : IDisposable
{
    internal const string RedirectUri = "https://useorbit.org/notion/callback";
    internal readonly INotionApi Api = Substitute.For<INotionApi>();
    internal readonly IFeatureFlagService Flags = Substitute.For<IFeatureFlagService>();
    internal readonly IUserDateService Dates = Substitute.For<IUserDateService>();
    internal readonly IProductAnalytics Analytics = Substitute.For<IProductAnalytics>();
    internal readonly MutableClock Clock = new();
    internal readonly User User = User.Create("Notion person", "notion@example.com").Value;
    internal readonly SqliteOrbitDbContextFactory Factory;
    internal OrbitDbContext Db => Factory.Context;
    internal readonly NotionConnectionService Connections;
    internal readonly NotionAccessGate Gate;
    internal readonly IUnitOfWork UnitOfWork;

    internal NotionTestContext(params IInterceptor[] interceptors)
    {
        var encryption = new EncryptionService(Options.Create(new EncryptionSettings
        {
            Key = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32))
        }), NullLogger<EncryptionService>.Instance);
        Factory = new SqliteOrbitDbContextFactory(encryption, interceptors);
        User.SetTimeZone("America/Sao_Paulo");
        Db.Users.Add(User);
        Db.SaveChanges();
        Db.ChangeTracker.Clear();
        Flags.GetEnabledKeysForUserAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>(), true)
            .Returns(new[] { FeatureFlagKeys.NotionIntegration });
        Dates.GetUserTodayAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(new DateOnly(2026, 9, 15));
        var payGate = new PayGateService(new GenericRepository<Habit>(Db), new GenericRepository<User>(Db),
            Substitute.For<IAppConfigService>(), Dates, Substitute.For<IFeatureFlagService>());
        Gate = new NotionAccessGate(payGate, Flags);
        UnitOfWork = new UnitOfWork(Db, new DatabaseConnectionSettings());
        Connections = new NotionConnectionService(Db, Api, Options.Create(new NotionSettings
        {
            ClientId = "client-id",
            ClientSecret = "client-secret",
            AllowedRedirectUris = [RedirectUri]
        }), encryption, UnitOfWork, Clock, Analytics, NullLogger<NotionConnectionService>.Instance);
        Api.ExchangeAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new NotionCredentials("access-token", "refresh-token", Guid.NewGuid(), "Workspace", Guid.NewGuid()));
        Api.SearchAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new[] { NotionExamples.Source() });
        Api.GetDatabaseAsync(Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(NotionExamples.Source());
        Api.QueryAsync(Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(_ => Pages(1));
    }

    internal ConnectionsController Controller(Guid? userId = null)
    {
        var sender = Substitute.For<ISender>();
        sender.Send(Arg.Any<AuthorizeNotionCommand>(), Arg.Any<CancellationToken>())
            .Returns(c => new AuthorizeNotionCommandHandler(Gate, Connections).Handle(c.Arg<AuthorizeNotionCommand>(), c.Arg<CancellationToken>()));
        sender.Send(Arg.Any<CompleteNotionCommand>(), Arg.Any<CancellationToken>())
            .Returns(c => new CompleteNotionCommandHandler(Gate, Connections).Handle(c.Arg<CompleteNotionCommand>(), c.Arg<CancellationToken>()));
        sender.Send(Arg.Any<GetNotionConnectionQuery>(), Arg.Any<CancellationToken>())
            .Returns(c => new GetNotionConnectionQueryHandler(Gate, Connections).Handle(c.Arg<GetNotionConnectionQuery>(), c.Arg<CancellationToken>()));
        sender.Send(Arg.Any<DisconnectNotionCommand>(), Arg.Any<CancellationToken>())
            .Returns(c => new DisconnectNotionCommandHandler(Gate, Connections).Handle(c.Arg<DisconnectNotionCommand>(), c.Arg<CancellationToken>()));
        return new ConnectionsController(sender, NullLogger<ConnectionsController>.Instance)
        {
            ControllerContext = new Microsoft.AspNetCore.Mvc.ControllerContext
            {
                HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext
                {
                    User = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(
                        [new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.NameIdentifier, (userId ?? User.Id).ToString())], "test"))
                }
            }
        };
    }

    internal NotionPollService Poller() => new(Db, Gate, Api, Dates, UnitOfWork, Clock, Analytics,
        NullLogger<NotionPollService>.Instance);

    internal async Task SeedConnectionAsync()
    {
        Db.NotionConnections.Add(NotionConnection.Create(User.Id, "access-token", "refresh-token", Guid.NewGuid(), "Workspace", Guid.NewGuid()));
        await Db.SaveChangesAsync();
        Db.ChangeTracker.Clear();
    }

    internal static async IAsyncEnumerable<System.Text.Json.JsonElement> Pages(int count)
    {
        await Task.CompletedTask;
        for (var i = 0; i < count; i++)
        {
            var page = NotionExamples.Page();
            page["id"] = Guid.NewGuid();
            yield return NotionExamples.Element(page);
        }
    }

    public void Dispose() => Factory.Dispose();

    internal sealed class MutableClock : TimeProvider
    {
        internal DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
