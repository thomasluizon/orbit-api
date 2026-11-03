using Orbit.Application.Connections;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Orbit.Application.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Persistence;
using Orbit.Infrastructure.Services;
using Orbit.Infrastructure.Services.Notion;
using Orbit.Infrastructure.Tests.Persistence;

namespace Orbit.Infrastructure.Tests.Services.Notion;

public class NotionBoundedPollingTests
{
    [Fact]
    public async Task TwoHundredConnections_ArePolledInFourBoundedTicks()
    {
        using var factory = new SqliteOrbitDbContextFactory();
        for (var index = 0; index < 200; index++)
        {
            var user = User.Create("Notion", $"notion-{index}@example.test").Value;
            user.GrantLifetimePro();
            factory.Context.Users.Add(user);
            factory.Context.NotionConnections.Add(NotionConnection.Create(user.Id, $"token-{index}", null, Guid.NewGuid(), "Workspace", Guid.NewGuid()));
        }
        await factory.Context.SaveChangesAsync();
        factory.Context.ChangeTracker.Clear();
        var api = Substitute.For<INotionApi>();
        var polled = new List<string>();
        api.SearchAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            polled.Add(call.Arg<string>());
            return Array.Empty<JsonElement>();
        });
        var dates = Substitute.For<IUserDateService>();
        dates.GetUserTodayAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(new DateOnly(2026, 11, 1));
        var flags = Substitute.For<IFeatureFlagService>();
        flags.GetEnabledKeysForUserAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>(), true).Returns([FeatureFlagKeys.NotionIntegration]);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["NotionSync:Parallelism"] = "1" }).Build();
        using var provider = new ServiceCollection().AddScoped(_ => factory.CreateContext()).AddScoped(sp =>
        {
            var db = sp.GetRequiredService<OrbitDbContext>();
            var payGate = new PayGateService(new GenericRepository<Habit>(db), new GenericRepository<User>(db), Substitute.For<IAppConfigService>(), dates, flags);
            return new NotionPollService(db, new NotionAccessGate(payGate, flags), api, dates,
                new UnitOfWork(db, new DatabaseConnectionSettings()), TimeProvider.System, Substitute.For<IProductAnalytics>(), NullLogger<NotionPollService>.Instance);
        }).BuildServiceProvider();
        var service = new NotionAutoSyncService(provider.GetRequiredService<IServiceScopeFactory>(), TimeProvider.System,
            NullLogger<NotionAutoSyncService>.Instance, configuration);
        for (var tick = 0; tick < 4; tick++)
        {
            await service.RunAsync(default);
            polled.Should().HaveCount((tick + 1) * 50);
        }
        polled.Distinct().Should().HaveCount(200);
        (await factory.Context.NotionConnections.CountAsync(c => c.LastPollAtUtc != null)).Should().Be(200);
    }
}
