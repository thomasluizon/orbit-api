using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Orbit.Application.Notifications;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.Persistence;
using Orbit.Infrastructure.Services;

namespace Orbit.Infrastructure.Tests.Services;

public class PeriodCloseNotificationServiceTests
{
    private static readonly DateOnly ClosedMonthLogDate = new(2026, 1, 15);
    private static readonly DateOnly FirstDayAfterClosedMonth = new(2026, 2, 1);

    [Fact]
    public async Task CheckAndSendNotifications_ActiveUserAtBoundary_RecordsLocalizedPeriodAndPushes()
    {
        await using var dbContext = CreateDbContext();
        var user = await SeedUserAsync(dbContext, withActivity: true, withSubscription: true);
        var pushService = Substitute.For<IPushNotificationService>();
        var userDateService = Substitute.For<IUserDateService>();
        ConfigureToday(userDateService, FirstDayAfterClosedMonth);
        var service = CreateService(dbContext, pushService, userDateService);

        await service.CheckAndSendNotificationsAsync(CancellationToken.None);

        var expectedUrl = NotificationUrls.WrappedClosedMonth(2026, 1);
        var notification = await dbContext.Notifications.SingleAsync();
        notification.UserId.Should().Be(user.Id);
        notification.Title.Should().Be("Your Wrapped is ready");
        notification.Body.Should().Be("January is closed - see how your month went.");
        notification.Url.Should().Be(expectedUrl);
        notification.DedupeKey.Should().Be(PeriodCloseNotificationService.BuildDedupeKey(user.Id, 2026, 1));
        await pushService.Received(1).SendToUserAsync(
            user.Id,
            notification.Title,
            notification.Body,
            expectedUrl,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CheckAndSendNotifications_RunTwice_RecordsAndPushesOnce()
    {
        await using var dbContext = CreateDbContext();
        var user = await SeedUserAsync(dbContext, withActivity: true, withSubscription: true);
        var pushService = Substitute.For<IPushNotificationService>();
        var userDateService = Substitute.For<IUserDateService>();
        ConfigureToday(userDateService, FirstDayAfterClosedMonth);
        var service = CreateService(dbContext, pushService, userDateService);

        await service.CheckAndSendNotificationsAsync(CancellationToken.None);
        await service.CheckAndSendNotificationsAsync(CancellationToken.None);

        (await dbContext.Notifications.CountAsync()).Should().Be(1);
        await pushService.Received(1).SendToUserAsync(
            user.Id,
            Arg.Any<string>(),
            Arg.Any<string>(),
            NotificationUrls.WrappedClosedMonth(2026, 1),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CheckAndSendNotifications_PushThrows_ReleasesClaimForNextRun()
    {
        await using var dbContext = CreateDbContext();
        var user = await SeedUserAsync(dbContext, withActivity: true, withSubscription: true);
        var pushService = Substitute.For<IPushNotificationService>();
        pushService.SendToUserAsync(
                user.Id,
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .Returns(
                Task.FromException(new InvalidOperationException("push transport unavailable")),
                Task.CompletedTask);
        var userDateService = Substitute.For<IUserDateService>();
        ConfigureToday(userDateService, FirstDayAfterClosedMonth);
        var service = CreateService(dbContext, pushService, userDateService);

        await service.CheckAndSendNotificationsAsync(CancellationToken.None);

        (await dbContext.Notifications.CountAsync()).Should().Be(0);

        await service.CheckAndSendNotificationsAsync(CancellationToken.None);

        (await dbContext.Notifications.CountAsync()).Should().Be(1);
        await pushService.Received(2).SendToUserAsync(
            user.Id,
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CheckAndSendNotifications_PushCanceled_ReleasesClaimForNextRun()
    {
        await using var dbContext = CreateDbContext();
        var user = await SeedUserAsync(dbContext, withActivity: true, withSubscription: true);
        var pushService = Substitute.For<IPushNotificationService>();
        pushService.SendToUserAsync(
                user.Id,
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .Returns(
                Task.FromCanceled(new CancellationToken(canceled: true)),
                Task.CompletedTask);
        var userDateService = Substitute.For<IUserDateService>();
        ConfigureToday(userDateService, FirstDayAfterClosedMonth);
        var service = CreateService(dbContext, pushService, userDateService);

        var firstRun = () => service.CheckAndSendNotificationsAsync(CancellationToken.None);
        await firstRun.Should().ThrowAsync<OperationCanceledException>();
        (await dbContext.Notifications.CountAsync()).Should().Be(0);

        await service.CheckAndSendNotificationsAsync(CancellationToken.None);

        (await dbContext.Notifications.CountAsync()).Should().Be(1);
        await pushService.Received(2).SendToUserAsync(
            user.Id,
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CheckAndSendNotifications_EmptyClosedMonth_DoesNothing()
    {
        await using var dbContext = CreateDbContext();
        await SeedUserAsync(dbContext, withActivity: false, withSubscription: true);
        var pushService = Substitute.For<IPushNotificationService>();
        var userDateService = Substitute.For<IUserDateService>();
        ConfigureToday(userDateService, FirstDayAfterClosedMonth);
        var service = CreateService(dbContext, pushService, userDateService);

        await service.CheckAndSendNotificationsAsync(CancellationToken.None);

        (await dbContext.Notifications.CountAsync()).Should().Be(0);
        await pushService.DidNotReceive().SendToUserAsync(
            Arg.Any<Guid>(),
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CheckAndSendNotifications_DifferentLocalBoundaries_NotifiesUsersOnSeparateRuns()
    {
        await using var dbContext = CreateDbContext();
        var firstUser = await SeedUserAsync(
            dbContext,
            withActivity: true,
            withSubscription: true,
            name: "First",
            timeZone: "Pacific/Auckland");
        var secondUser = await SeedUserAsync(
            dbContext,
            withActivity: true,
            withSubscription: true,
            name: "Second",
            timeZone: "America/Sao_Paulo");
        var localDates = new Dictionary<Guid, DateOnly>
        {
            [firstUser.Id] = FirstDayAfterClosedMonth,
            [secondUser.Id] = FirstDayAfterClosedMonth.AddDays(-1)
        };
        var pushService = Substitute.For<IPushNotificationService>();
        var userDateService = Substitute.For<IUserDateService>();
        userDateService.GetUserTodayAsync(
                Arg.Any<string?>(),
                Arg.Any<Guid>(),
                Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult(localDates[call.ArgAt<Guid>(1)]));
        var service = CreateService(dbContext, pushService, userDateService);

        await service.CheckAndSendNotificationsAsync(CancellationToken.None);

        await pushService.Received(1).SendToUserAsync(
            firstUser.Id,
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
        await pushService.DidNotReceive().SendToUserAsync(
            secondUser.Id,
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());

        localDates[firstUser.Id] = FirstDayAfterClosedMonth.AddDays(1);
        localDates[secondUser.Id] = FirstDayAfterClosedMonth;
        await service.CheckAndSendNotificationsAsync(CancellationToken.None);

        await pushService.Received(1).SendToUserAsync(
            secondUser.Id,
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
        (await dbContext.Notifications.CountAsync()).Should().Be(2);
    }

    [Fact]
    public async Task CheckAndSendNotifications_UserWithoutPushSubscription_DoesNothing()
    {
        await using var dbContext = CreateDbContext();
        await SeedUserAsync(dbContext, withActivity: true, withSubscription: false);
        var pushService = Substitute.For<IPushNotificationService>();
        var userDateService = Substitute.For<IUserDateService>();
        ConfigureToday(userDateService, FirstDayAfterClosedMonth);
        var service = CreateService(dbContext, pushService, userDateService);

        await service.CheckAndSendNotificationsAsync(CancellationToken.None);

        (await dbContext.Notifications.CountAsync()).Should().Be(0);
        await userDateService.DidNotReceive().GetUserTodayAsync(
            Arg.Any<string?>(),
            Arg.Any<Guid>(),
            Arg.Any<CancellationToken>());
        await pushService.DidNotReceive().SendToUserAsync(
            Arg.Any<Guid>(),
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public void BuildNotification_Portuguese_UsesLocalizedMonthNameAndCopy()
    {
        var notification = PeriodCloseNotificationService.BuildNotification(3, "pt-BR");

        notification.Title.Should().Be("Seu Wrapped está pronto");
        notification.Body.Should().Be("Março fechou - veja como foi o seu mês.");
    }

    [Fact]
    public void BuildDedupeKey_IncludesUserAndZeroPaddedMonth()
    {
        var userId = Guid.Parse("d88f532c-49bb-4452-b46a-350e3460a03f");

        var key = PeriodCloseNotificationService.BuildDedupeKey(userId, 2026, 2);

        key.Should().Be("wrapped-d88f532c-49bb-4452-b46a-350e3460a03f-2026-02");
    }

    private static void ConfigureToday(IUserDateService userDateService, DateOnly today) =>
        userDateService.GetUserTodayAsync(
                Arg.Any<string?>(),
                Arg.Any<Guid>(),
                Arg.Any<CancellationToken>())
            .Returns(today);

    private static async Task<User> SeedUserAsync(
        OrbitDbContext dbContext,
        bool withActivity,
        bool withSubscription,
        string name = "User",
        string? timeZone = null)
    {
        var user = User.Create(name, $"{name.ToLowerInvariant()}-{Guid.NewGuid():N}@example.com").Value;
        if (timeZone is not null)
            user.SetTimeZone(timeZone).IsSuccess.Should().BeTrue();

        var habit = Habit.Create(new HabitCreateParams(
            user.Id,
            $"{name} habit",
            FrequencyUnit.Day,
            1,
            DueDate: new DateOnly(2026, 1, 1))).Value;
        if (withActivity)
            habit.Log(ClosedMonthLogDate, advanceDueDate: false).IsSuccess.Should().BeTrue();

        dbContext.Users.Add(user);
        dbContext.Habits.Add(habit);
        if (withSubscription)
        {
            dbContext.PushSubscriptions.Add(PushSubscription.Create(
                user.Id,
                $"token-{user.Id}",
                PushSubscription.FcmSentinel,
                "auth").Value);
        }

        await dbContext.SaveChangesAsync();
        return user;
    }

    private static OrbitDbContext CreateDbContext() =>
        new(new DbContextOptionsBuilder<OrbitDbContext>()
            .UseInMemoryDatabase($"PeriodCloseNotificationServiceTests_{Guid.NewGuid()}")
            .Options);

    private static PeriodCloseNotificationService CreateService(
        OrbitDbContext dbContext,
        IPushNotificationService pushService,
        IUserDateService userDateService)
    {
        var serviceProvider = new ServiceCollection()
            .AddSingleton(dbContext)
            .AddSingleton(pushService)
            .AddSingleton(userDateService)
            .BuildServiceProvider();
        return new PeriodCloseNotificationService(
            serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<PeriodCloseNotificationService>.Instance,
            new ConfigurationBuilder().Build());
    }
}
