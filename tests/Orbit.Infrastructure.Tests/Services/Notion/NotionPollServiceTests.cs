using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Orbit.Domain.Entities;
using Orbit.Infrastructure.Persistence;
using Orbit.Infrastructure.Services;
using Orbit.Infrastructure.Services.Notion;

namespace Orbit.Infrastructure.Tests.Services.Notion;

public class NotionPollServiceTests
{
    [Fact]
    public async Task ForcedFeatureFlagReadObservesKillSwitchWithAWarmCache()
    {
        using var test = new NotionTestContext();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var flags = new FeatureFlagService(test.Db, cache);
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
}
