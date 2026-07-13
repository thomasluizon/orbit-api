using System.Data.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.Persistence;
using Orbit.Infrastructure.Services;

namespace Orbit.Infrastructure.Tests.Services;

/// <summary>
/// DB-backed behavior tests for ProactiveCheckinSchedulerService: an off-track, opted-in Pro user
/// inside the send window receives exactly one push and a dedup row; free / opted-out / on-track /
/// already-sent / out-of-window users receive nothing; and a unique-violation on the dedup write
/// skips the push. The send window is made deterministic via config (hour 0 + a full-day interval
/// always matches; a 12-hour-away hour never matches) so the tests do not depend on wall-clock time.
/// </summary>
public class ProactiveCheckinSchedulerServiceTests
{
    private const int AlwaysInWindowHour = 0;
    private const int AlwaysInWindowInterval = 1440;
    private const int OutsideWindowInterval = 60;

    private static int OutsideWindowHour() => (DateTime.UtcNow.Hour + 12) % 24;

    [Fact]
    public async Task CheckAndSendCheckins_OffTrackProUserInWindow_SendsPushAndRecordsRow()
    {
        await using var dbContext = CreateInMemoryDbContext();
        var pushService = Substitute.For<IPushNotificationService>();
        var messageService = CreateMessageService();

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var user = CreateOptedInProUser();
        dbContext.Users.Add(user);
        dbContext.Habits.Add(CreateOffTrackHabit(user.Id, today));
        await dbContext.SaveChangesAsync();

        var service = CreateService(dbContext, pushService, messageService, AlwaysInWindowHour, AlwaysInWindowInterval);
        await service.CheckAndSendCheckins(CancellationToken.None);

        await pushService.Received(1).SendToUserAsync(user.Id, "Title", "Body", "/chat", Arg.Any<CancellationToken>());
        (await dbContext.SentProactiveCheckins.CountAsync(a => a.UserId == user.Id)).Should().Be(1);
        (await dbContext.Notifications.CountAsync(n => n.UserId == user.Id)).Should().Be(1);
    }

    [Fact]
    public async Task CheckAndSendCheckins_FreeUser_DoesNotSend()
    {
        await using var dbContext = CreateInMemoryDbContext();
        var pushService = Substitute.For<IPushNotificationService>();
        var messageService = CreateMessageService();

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var user = CreateOptedInProUser();
        user.StartTrial(DateTime.UtcNow.AddDays(-1));
        dbContext.Users.Add(user);
        dbContext.Habits.Add(CreateOffTrackHabit(user.Id, today));
        await dbContext.SaveChangesAsync();

        var service = CreateService(dbContext, pushService, messageService, AlwaysInWindowHour, AlwaysInWindowInterval);
        await service.CheckAndSendCheckins(CancellationToken.None);

        await AssertNoPush(pushService);
        (await dbContext.SentProactiveCheckins.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task CheckAndSendCheckins_OptedOutUser_DoesNotSend()
    {
        await using var dbContext = CreateInMemoryDbContext();
        var pushService = Substitute.For<IPushNotificationService>();
        var messageService = CreateMessageService();

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var user = User.Create("Thomas", "thomas@test.com").Value;
        dbContext.Users.Add(user);
        dbContext.Habits.Add(CreateOffTrackHabit(user.Id, today));
        await dbContext.SaveChangesAsync();

        var service = CreateService(dbContext, pushService, messageService, AlwaysInWindowHour, AlwaysInWindowInterval);
        await service.CheckAndSendCheckins(CancellationToken.None);

        await AssertNoPush(pushService);
        (await dbContext.SentProactiveCheckins.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task CheckAndSendCheckins_AlreadySentToday_DoesNotResend()
    {
        await using var dbContext = CreateInMemoryDbContext();
        var pushService = Substitute.For<IPushNotificationService>();
        var messageService = CreateMessageService();

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var user = CreateOptedInProUser();
        dbContext.Users.Add(user);
        dbContext.Habits.Add(CreateOffTrackHabit(user.Id, today));
        dbContext.SentProactiveCheckins.Add(SentProactiveCheckin.Create(user.Id, today));
        await dbContext.SaveChangesAsync();

        var service = CreateService(dbContext, pushService, messageService, AlwaysInWindowHour, AlwaysInWindowInterval);
        await service.CheckAndSendCheckins(CancellationToken.None);

        await AssertNoPush(pushService);
        await messageService.DidNotReceive().GenerateMessageAsync(
            Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<int>(),
            Arg.Any<string>(), Arg.Any<CancellationToken>());
        (await dbContext.SentProactiveCheckins.CountAsync(a => a.UserId == user.Id)).Should().Be(1);
    }

    [Fact]
    public async Task CheckAndSendCheckins_OnTrackUser_DoesNotSend()
    {
        await using var dbContext = CreateInMemoryDbContext();
        var pushService = Substitute.For<IPushNotificationService>();
        var messageService = CreateMessageService();

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var user = CreateOptedInProUser();
        var habit = CreateOffTrackHabit(user.Id, today);
        habit.Log(today, advanceDueDate: false);
        dbContext.Users.Add(user);
        dbContext.Habits.Add(habit);
        dbContext.HabitLogs.AddRange(habit.Logs);
        await dbContext.SaveChangesAsync();

        var service = CreateService(dbContext, pushService, messageService, AlwaysInWindowHour, AlwaysInWindowInterval);
        await service.CheckAndSendCheckins(CancellationToken.None);

        await AssertNoPush(pushService);
        await messageService.DidNotReceive().GenerateMessageAsync(
            Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<int>(),
            Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CheckAndSendCheckins_OutsideSendWindow_DoesNotSend()
    {
        await using var dbContext = CreateInMemoryDbContext();
        var pushService = Substitute.For<IPushNotificationService>();
        var messageService = CreateMessageService();

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var user = CreateOptedInProUser();
        dbContext.Users.Add(user);
        dbContext.Habits.Add(CreateOffTrackHabit(user.Id, today));
        await dbContext.SaveChangesAsync();

        var service = CreateService(dbContext, pushService, messageService, OutsideWindowHour(), OutsideWindowInterval);
        await service.CheckAndSendCheckins(CancellationToken.None);

        await AssertNoPush(pushService);
        (await dbContext.SentProactiveCheckins.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task CheckAndSendCheckins_DedupUniqueViolation_SkipsPush()
    {
        var options = new DbContextOptionsBuilder<OrbitDbContext>()
            .UseInMemoryDatabase($"ProactiveCheckinSchedulerServiceTests_{Guid.NewGuid()}")
            .Options;
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var user = CreateOptedInProUser();

        await using (var seedContext = new OrbitDbContext(options))
        {
            seedContext.Users.Add(user);
            seedContext.Habits.Add(CreateOffTrackHabit(user.Id, today));
            await seedContext.SaveChangesAsync();
        }

        await using var throwingContext = new UniqueViolationDbContext(options);
        var pushService = Substitute.For<IPushNotificationService>();
        var messageService = CreateMessageService();

        var service = CreateService(throwingContext, pushService, messageService, AlwaysInWindowHour, AlwaysInWindowInterval);
        await service.CheckAndSendCheckins(CancellationToken.None);

        await AssertNoPush(pushService);
    }

    [Fact]
    public async Task ExecuteAsync_HostedLifecycle_RunsOneCheckinPassThenStopsGracefully()
    {
        await using var dbContext = CreateInMemoryDbContext();
        var pushService = Substitute.For<IPushNotificationService>();
        var messageService = CreateMessageService();

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var user = CreateOptedInProUser();
        dbContext.Users.Add(user);
        dbContext.Habits.Add(CreateOffTrackHabit(user.Id, today));
        await dbContext.SaveChangesAsync();

        var firstPush = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        pushService
            .SendToUserAsync(user.Id, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(_ => { firstPush.TrySetResult(); return Task.CompletedTask; });

        var service = CreateService(dbContext, pushService, messageService, AlwaysInWindowHour, AlwaysInWindowInterval);

        await service.StartAsync(CancellationToken.None);
        await firstPush.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await service.StopAsync(CancellationToken.None);

        await pushService.Received(1).SendToUserAsync(user.Id, "Title", "Body", "/chat", Arg.Any<CancellationToken>());
        (await dbContext.SentProactiveCheckins.CountAsync(a => a.UserId == user.Id)).Should().Be(1);
        service.ExecuteTask.Should().NotBeNull();
        service.ExecuteTask!.IsCompleted.Should().BeTrue();
        service.ExecuteTask!.IsFaulted.Should().BeFalse();
    }

    [Fact]
    public async Task CheckAndSendCheckins_OneUserMessageThrows_StillSendsToRemainingUsers()
    {
        await using var dbContext = CreateInMemoryDbContext();
        var pushService = Substitute.For<IPushNotificationService>();

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var throwingUser = CreateOptedInProUser("Alice", "alice@test.com");
        var healthyUser = CreateOptedInProUser("Bob", "bob@test.com");
        dbContext.Users.AddRange(throwingUser, healthyUser);
        dbContext.Habits.Add(CreateOffTrackHabit(throwingUser.Id, today));
        dbContext.Habits.Add(CreateOffTrackHabit(healthyUser.Id, today));
        await dbContext.SaveChangesAsync();

        var messageService = Substitute.For<IProactiveCheckinMessageService>();
        messageService.GenerateMessageAsync(
                "Alice", Arg.Any<IReadOnlyList<string>>(), Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<Result<(string Title, string Body)>>(
                new InvalidOperationException("message service down")));
        messageService.GenerateMessageAsync(
                "Bob", Arg.Any<IReadOnlyList<string>>(), Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Success<(string Title, string Body)>(("Title", "Body"))));

        var service = CreateService(dbContext, pushService, messageService, AlwaysInWindowHour, AlwaysInWindowInterval);

        var checkin = async () => await service.CheckAndSendCheckins(CancellationToken.None);
        await checkin.Should().NotThrowAsync();

        await pushService.Received(1).SendToUserAsync(
            healthyUser.Id, "Title", "Body", "/chat", Arg.Any<CancellationToken>());
        await pushService.DidNotReceive().SendToUserAsync(
            throwingUser.Id, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
        (await dbContext.SentProactiveCheckins.CountAsync(a => a.UserId == healthyUser.Id)).Should().Be(1);
        (await dbContext.SentProactiveCheckins.CountAsync(a => a.UserId == throwingUser.Id)).Should().Be(0);
    }

    private static async Task AssertNoPush(IPushNotificationService pushService) =>
        await pushService.DidNotReceive().SendToUserAsync(
            Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());

    private static User CreateOptedInProUser(string name = "Thomas", string email = "thomas@test.com")
    {
        var user = User.Create(name, email).Value;
        user.SetProactiveAstraEnabled(true);
        return user;
    }

    private static Habit CreateOffTrackHabit(Guid userId, DateOnly dueDate) =>
        Habit.Create(new HabitCreateParams(
            userId, "Meditate", FrequencyUnit.Day, 1, DueDate: dueDate)).Value;

    private static IProactiveCheckinMessageService CreateMessageService()
    {
        var messageService = Substitute.For<IProactiveCheckinMessageService>();
        messageService.GenerateMessageAsync(
            Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<int>(),
            Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Success<(string Title, string Body)>(("Title", "Body"))));
        return messageService;
    }

    private static OrbitDbContext CreateInMemoryDbContext() =>
        new(new DbContextOptionsBuilder<OrbitDbContext>()
            .UseInMemoryDatabase($"ProactiveCheckinSchedulerServiceTests_{Guid.NewGuid()}")
            .Options);

    private static ProactiveCheckinSchedulerService CreateService(
        OrbitDbContext dbContext,
        IPushNotificationService pushService,
        IProactiveCheckinMessageService messageService,
        int checkinHour,
        int intervalMinutes)
    {
        var serviceProvider = new ServiceCollection()
            .AddSingleton(dbContext)
            .AddSingleton(pushService)
            .AddSingleton(messageService)
            .BuildServiceProvider();
        var scopeFactory = serviceProvider.GetRequiredService<IServiceScopeFactory>();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["BackgroundServices:ProactiveCheckinHour"] = checkinHour.ToString(),
                ["BackgroundServices:ProactiveCheckinIntervalMinutes"] = intervalMinutes.ToString()
            })
            .Build();
        return new ProactiveCheckinSchedulerService(
            scopeFactory, NullLogger<ProactiveCheckinSchedulerService>.Instance, configuration);
    }

    private sealed class UniqueViolationDbContext(DbContextOptions<OrbitDbContext> options) : OrbitDbContext(options)
    {
        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) =>
            throw new DbUpdateException("Unique violation", new FakeUniqueViolationException());
    }

    private sealed class FakeUniqueViolationException : DbException
    {
        public override string? SqlState => "23505";
    }
}
