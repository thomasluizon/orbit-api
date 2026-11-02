using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Hosting;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Orbit.Application.Common;
using Orbit.Domain.Entities;
using Orbit.Infrastructure.Persistence;
using Orbit.Infrastructure.Services;
using Orbit.Infrastructure.Services.Notion;

namespace Orbit.Infrastructure.Tests.Services.Notion;

public class NotionPollServiceTests
{
    [Fact]
    public async Task TruncatedPoll_PreservesUnseenTasksAndWarns()
    {
        using var test = new NotionTestContext();
        await test.SeedConnectionAsync();
        var pages = new List<JsonElement>();
        await foreach (var page in NotionTestContext.Pages(600)) pages.Add(page);
        var mapper = new NotionTaskMapper(NotionExamples.Source());
        foreach (var page in pages)
            test.Db.NotionTasks.Add(mapper.Map(page, test.User.Id, new DateOnly(2026, 9, 15),
                TimeZoneInfo.Utc, test.Clock.Now.UtcDateTime)!);
        await test.Db.SaveChangesAsync();
        test.Db.ChangeTracker.Clear();
        test.Api.QueryAsync(Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(_ => ReadPages(pages));
        var logger = Substitute.For<ILogger<NotionPollService>>();
        await new NotionPollService(test.Db, test.Gate, test.Api, test.Dates, test.UnitOfWork,
            test.Clock, test.Analytics, logger).PollAsync(test.User.Id, default);
        (await test.Db.NotionTasks.Select(task => task.PageId).ToArrayAsync())
            .Should().BeEquivalentTo(pages.Select(page => page.GetProperty("id").GetGuid()));
        logger.ReceivedCalls().Should().Contain(call => call.GetMethodInfo().Name == "Log"
            && (LogLevel)call.GetArguments()[0]! == LogLevel.Warning
            && call.GetArguments()[2]!.ToString()!.Contains("truncated", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task FiveHundredUnmappedInputRows_PreserveTheUnreachedHundredTasks()
    {
        using var test = new NotionTestContext();
        await test.SeedConnectionAsync();
        var ids = new List<Guid>();
        var mapper = new NotionTaskMapper(NotionExamples.Source());
        await foreach (var page in NotionTestContext.Pages(600))
        {
            ids.Add(page.GetProperty("id").GetGuid());
            test.Db.NotionTasks.Add(mapper.Map(page, test.User.Id, new DateOnly(2026, 9, 15),
                TimeZoneInfo.Utc, test.Clock.Now.UtcDateTime)!);
        }
        await test.Db.SaveChangesAsync();
        test.Db.ChangeTracker.Clear();
        using var http = new NotionHttpStub();
        http.Responses.Enqueue(NotionHttpStub.List([NotionExamples.Source()], false));
        http.Responses.Enqueue(NotionHttpStub.Json(NotionExamples.Source()));
        foreach (var chunk in ids.Chunk(100))
        {
            var pages = chunk.Select(id =>
            {
                var page = NotionExamples.Page(due: null);
                page["id"] = id;
                page["properties"]!["Status"]!["status"] = null;
                return NotionExamples.Element(page);
            }).ToArray();
            http.Responses.Enqueue(NotionHttpStub.List(pages, true, Guid.NewGuid().ToString()));
        }
        var logger = Substitute.For<ILogger<NotionPollService>>();
        await new NotionPollService(test.Db, test.Gate, http.Api, test.Dates, test.UnitOfWork,
            test.Clock, test.Analytics, logger).PollAsync(test.User.Id, default);
        (await test.Db.NotionTasks.Select(task => task.PageId).ToArrayAsync()).Should().BeEquivalentTo(ids.Skip(500));
        http.Requests.Should().HaveCount(7);
        http.Responses.Should().ContainSingle();
        logger.ReceivedCalls().Should().ContainSingle(call => call.GetMethodInfo().Name == "Log"
            && (LogLevel)call.GetArguments()[0]! == LogLevel.Warning
            && call.GetArguments()[2]!.ToString()!.Contains("500 input rows and 0 mapped tasks", StringComparison.Ordinal));
    }

    private static async IAsyncEnumerable<JsonElement> ReadPages(IEnumerable<JsonElement> pages)
    {
        await Task.CompletedTask;
        foreach (var page in pages) yield return page;
    }

    [Fact]
    public async Task PollPersistsWhenProviderMaterializesTimestampsAtMicrosecondPrecision()
    {
        using var test = new NotionTestContext(new MicrosecondTimestampMaterializer());
        test.Clock.Now = new DateTimeOffset(2026, 9, 15, 12, 0, 0, TimeSpan.Zero).AddTicks(7);
        await test.SeedConnectionAsync();
        await test.Poller().PollAsync(test.User.Id, CancellationToken.None);
        (await test.Db.NotionTasks.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task ForcedFeatureFlagReadObservesKillSwitchWithAWarmCache()
    {
        using var test = new NotionTestContext();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var flags = new FeatureFlagService(test.Db, cache, Substitute.For<IHostEnvironment>(), new ConfigurationBuilder().Build());
        var flag = await test.Db.AppFeatureFlags.SingleAsync(f => f.Key == "notion_integration");
        flag.SetEnabled(true);
        await test.Db.SaveChangesAsync();
        (await flags.GetEnabledKeysForUserAsync(test.User.Id)).Should().Contain("notion_integration");
        flag.SetEnabled(false);
        await test.Db.SaveChangesAsync();
        (await flags.GetEnabledKeysForUserAsync(test.User.Id)).Should().Contain("notion_integration");
        (await flags.GetEnabledKeysForUserAsync(test.User.Id, forceRefresh: true)).Should().NotContain("notion_integration");
    }

    [Fact]
    public async Task CapAppliesAcrossDataSources()
    {
        using var test = new NotionTestContext();
        await test.SeedConnectionAsync();
        var second = System.Text.Json.Nodes.JsonNode.Parse(NotionExamples.Source().GetRawText())!;
        second["id"] = Guid.NewGuid();
        test.Api.SearchAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new[] { NotionExamples.Source(), NotionExamples.Element(second) });
        test.Api.QueryAsync(Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(_ => NotionTestContext.Pages(70));
        await test.Poller().PollAsync(test.User.Id, CancellationToken.None);
        (await test.Db.NotionTasks.CountAsync()).Should().Be(100);
        test.Api.Received(2).QueryAsync(Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        await test.Api.Received(1).GetDatabaseAsync(Arg.Any<string>(), NotionExamples.DatabaseId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PollMapsTasksAndCapsAccountAtOneHundred()
    {
        using var test = new NotionTestContext();
        await test.SeedConnectionAsync();
        test.Api.QueryAsync(Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(_ => NotionTestContext.Pages(150));
        await test.Poller().PollAsync(test.User.Id, CancellationToken.None);
        test.Db.ChangeTracker.Clear();
        var tasks = await test.Db.NotionTasks.ToListAsync();
        tasks.Should().HaveCount(100);
        tasks.Should().OnlyContain(t => t.Title == "Launch checklist" && t.DueDate == new DateOnly(2026, 9, 15) && t.Status == "In progress");
        var connection = await test.Db.NotionConnections.SingleAsync();
        connection.Status.Should().Be(NotionConnectionStatus.Idle);
        connection.LastPollAtUtc.Should().Be(test.Clock.Now.UtcDateTime);
        connection.SharedDatabasesJson.Should().Contain(NotionExamples.DatabaseId.ToString());
        await test.Dates.Received(1).GetUserTodayAsync(test.User.Id, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PollWithoutDateOrStatusStoresTitlesOnlyAndReplacesOldSnapshot()
    {
        using var test = new NotionTestContext();
        await test.SeedConnectionAsync();
        test.Api.SearchAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new[] { NotionExamples.Source(false, false) });
        await test.Poller().PollAsync(test.User.Id, CancellationToken.None);
        test.Db.ChangeTracker.Clear();
        var task = await test.Db.NotionTasks.SingleAsync();
        task.Title.Should().Be("Launch checklist");
        task.DueDate.Should().BeNull();
        task.Status.Should().BeNull();
        test.Clock.Now = test.Clock.Now.AddMinutes(16);
        test.Api.SearchAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Array.Empty<JsonElement>());
        await test.Poller().PollAsync(test.User.Id, CancellationToken.None);
        (await test.Db.NotionTasks.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task UnauthorizedMarksReconnectAndFuturePollsStop()
    {
        using var test = new NotionTestContext();
        await test.SeedConnectionAsync();
        test.Api.SearchAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<JsonElement>>(_ => throw new NotionApiException(401));
        await test.Poller().PollAsync(test.User.Id, CancellationToken.None);
        test.Db.ChangeTracker.Clear();
        (await test.Db.NotionConnections.SingleAsync()).Status.Should().Be(NotionConnectionStatus.ReconnectRequired);
        test.Clock.Now = test.Clock.Now.AddMinutes(16);
        await test.Poller().PollAsync(test.User.Id, CancellationToken.None);
        await test.Api.Received(1).SearchAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        test.Analytics.Received(1).CaptureUserEvent(test.User.Id, "notion_poll_failed", "pro",
            Arg.Is<IReadOnlyDictionary<string, object>>(p => (int)p["status_code"] == 401));
    }

    [Fact]
    public async Task PollUsesClientRetryAfterHandling()
    {
        using var test = new NotionTestContext();
        await test.SeedConnectionAsync();
        using var http = new NotionHttpStub();
        http.Responses.Enqueue(NotionHttpStub.Throttled(429, 73));
        http.Responses.Enqueue(NotionHttpStub.List([], false));
        var poller = new NotionPollService(test.Db, test.Gate, http.Api, test.Dates, test.UnitOfWork,
            test.Clock, test.Analytics, NullLogger<NotionPollService>.Instance);
        await poller.PollAsync(test.User.Id, CancellationToken.None);
        http.Delays.Should().Contain(TimeSpan.FromSeconds(73));
        http.Requests.Should().HaveCount(2);
        test.Db.ChangeTracker.Clear();
        (await test.Db.NotionConnections.SingleAsync()).Status.Should().Be(NotionConnectionStatus.Idle);
    }

    [Fact]
    public async Task ProEndingStopsPollAndResumingProRestartsItWithoutReconnecting()
    {
        using var test = new NotionTestContext();
        await test.SeedConnectionAsync();
        var user = await test.Db.Users.SingleAsync();
        user.StartTrial(DateTime.UtcNow.AddDays(-1));
        user.SetStripeSubscription("subscription", DateTime.UtcNow.AddDays(-1));
        await test.Db.SaveChangesAsync();
        test.Db.ChangeTracker.Clear();
        await test.Poller().PollAsync(test.User.Id, CancellationToken.None);
        await test.Api.DidNotReceive().SearchAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        (await test.Db.NotionConnections.CountAsync()).Should().Be(1);
        user = await test.Db.Users.SingleAsync();
        user.SetStripeSubscription("subscription", DateTime.UtcNow.AddDays(1));
        await test.Db.SaveChangesAsync();
        test.Db.ChangeTracker.Clear();
        await test.Poller().PollAsync(test.User.Id, CancellationToken.None);
        await test.Api.Received(1).SearchAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        (await test.Db.NotionTasks.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task FlagOffAndRecentPollSkipNetwork()
    {
        using var test = new NotionTestContext();
        await test.SeedConnectionAsync();
        test.Flags.GetEnabledKeysForUserAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>(), true).Returns(Array.Empty<string>());
        await test.Poller().PollAsync(test.User.Id, CancellationToken.None);
        await test.Api.DidNotReceive().SearchAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        test.Flags.GetEnabledKeysForUserAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>(), true).Returns(new[] { "notion_integration" });
        await test.Poller().PollAsync(test.User.Id, CancellationToken.None);
        await test.Poller().PollAsync(test.User.Id, CancellationToken.None);
        await test.Api.Received(1).SearchAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task InFlightPollCannotRestoreDataAfterDisconnect()
    {
        using var test = new NotionTestContext();
        await test.SeedConnectionAsync();
        var pending = new TaskCompletionSource<IReadOnlyList<JsonElement>>();
        test.Api.SearchAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(pending.Task);
        var poll = test.Poller().PollAsync(test.User.Id, CancellationToken.None);
        await test.Connections.DisconnectAsync(test.User.Id, CancellationToken.None);
        pending.SetResult([NotionExamples.Source()]);
        await poll;
        (await test.Db.NotionConnections.AnyAsync()).Should().BeFalse();
        (await test.Db.NotionTasks.AnyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task AnotherPollCannotClaimTheSameWindowWhileNetworkIsPending()
    {
        using var test = new NotionTestContext();
        await test.SeedConnectionAsync();
        var pending = new TaskCompletionSource<IReadOnlyList<JsonElement>>();
        test.Api.SearchAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(pending.Task);
        var first = test.Poller().PollAsync(test.User.Id, CancellationToken.None);
        await test.Poller().PollAsync(test.User.Id, CancellationToken.None);
        await test.Api.Received(1).SearchAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        pending.SetResult([]);
        await first;
    }

    [Theory]
    [InlineData(-1, false)]
    [InlineData(0, false)]
    [InlineData(1, true)]
    public async Task ScheduledTick_BonusProEligibilityUsesIndependentExpiry(int daysUntilExpiry, bool eligible)
    {
        using var test = new NotionTestContext();
        var now = test.Clock.Now;
        test.Clock.Now = now.AddDays(daysUntilExpiry - AppConstants.ReferralBonusProDays);
        var user = User.Create("Bonus", "bonus@example.com", test.Clock).Value;
        user.StartTrial(now.UtcDateTime.AddDays(-1));
        user.GrantBonusProDays(AppConstants.ReferralBonusProDays);
        test.Clock.Now = now;
        test.Db.Users.Add(user);
        test.Db.NotionConnections.Add(NotionConnection.Create(user.Id, "access-token", "refresh-token",
            Guid.NewGuid(), "Workspace", Guid.NewGuid()));
        await test.Db.SaveChangesAsync();
        test.Db.ChangeTracker.Clear();
        var services = new ServiceCollection();
        services.AddSingleton<OrbitDbContext>(_ => test.Db);
        services.AddScoped(_ => test.Poller());
        using var provider = services.BuildServiceProvider();
        var scheduled = new NotionAutoSyncService(provider.GetRequiredService<IServiceScopeFactory>(), test.Clock,
            NullLogger<NotionAutoSyncService>.Instance);

        await scheduled.RunAsync(CancellationToken.None);

        await test.Api.Received(eligible ? 1 : 0).SearchAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ScheduledTickIncludesPeopleWithTrialProAccess()
    {
        using var test = new NotionTestContext();
        await test.SeedConnectionAsync();
        var services = new ServiceCollection();
        services.AddSingleton<OrbitDbContext>(_ => test.Db);
        services.AddScoped(_ => test.Poller());
        using var provider = services.BuildServiceProvider();
        var scheduled = new NotionAutoSyncService(provider.GetRequiredService<IServiceScopeFactory>(), test.Clock,
            NullLogger<NotionAutoSyncService>.Instance);
        await scheduled.RunAsync(CancellationToken.None);
        await test.Api.Received(1).SearchAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    private sealed class MicrosecondTimestampMaterializer : IMaterializationInterceptor
    {
        public object InitializedInstance(MaterializationInterceptionData materializationData, object entity)
        {
            if (entity is NotionConnection { LastPollAtUtc: { } timestamp })
            {
                typeof(NotionConnection).GetProperty(nameof(NotionConnection.LastPollAtUtc))!
                    .SetValue(entity, new DateTime(timestamp.Ticks / 10 * 10, DateTimeKind.Utc));
            }
            return entity;
        }
    }
}
