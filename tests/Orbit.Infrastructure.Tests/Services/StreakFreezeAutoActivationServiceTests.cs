using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Orbit.Application.Social.Services;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using Orbit.Infrastructure.Persistence;
using Orbit.Infrastructure.Services;

namespace Orbit.Infrastructure.Tests.Services;

public class StreakFreezeAutoActivationServiceTests
{
    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly DateOnly Today = new(2026, 6, 4);
    private static readonly DateTimeOffset Instant = new(2026, 6, 4, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void BuildNotification_English_MentionsStreakLengthAndFreeze()
    {
        var (title, body) = StreakFreezeAutoActivationService.BuildNotification(14, "en");

        title.Should().Be("Streak protected");
        body.Should().Contain("14-day");
        body.Should().Contain("freeze");
    }

    [Fact]
    public void BuildNotification_Portuguese_UsesPortugueseCopy()
    {
        var (title, body) = StreakFreezeAutoActivationService.BuildNotification(14, "pt-BR");

        title.Should().Be("Sequência protegida");
        body.Should().Contain("14 dias");
        body.Should().Contain("congelamento");
    }

    [Fact]
    public async Task ActivateMissedDayFreezes_EligibleUser_PersistsOneFreezeAndConsumesBank()
    {
        await using var dbContext = CreateDbContext();
        var pushService = Substitute.For<IPushNotificationService>();
        var userDateService = Substitute.For<IUserDateService>();
        userDateService.GetUserTodayAsync(UserId, Arg.Any<CancellationToken>()).Returns(Today);
        var user = CreateEligibleProUser();
        dbContext.Users.Add(user);
        await dbContext.SaveChangesAsync();
        var service = CreateService(dbContext, pushService, userDateService);

        await service.ActivateMissedDayFreezes(CancellationToken.None);

        var freeze = await dbContext.StreakFreezes.AsNoTracking().SingleAsync();
        freeze.UserId.Should().Be(UserId);
        freeze.UsedOnDate.Should().Be(new DateOnly(2026, 6, 3));
        user.StreakFreezesAccumulated.Should().Be(0);
        (await dbContext.Notifications.AsNoTracking().CountAsync()).Should().Be(1);
        await pushService.Received(1).SendToUserAsync(
            UserId,
            Arg.Any<string>(),
            Arg.Any<string>(),
            "/streak",
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ActivateMissedDayFreezes_SecondPass_DoesNotSpendOrNotifyAgain()
    {
        await using var dbContext = CreateDbContext();
        var pushService = Substitute.For<IPushNotificationService>();
        var userDateService = Substitute.For<IUserDateService>();
        userDateService.GetUserTodayAsync(UserId, Arg.Any<CancellationToken>()).Returns(Today);
        var user = CreateEligibleProUser();
        dbContext.Users.Add(user);
        await dbContext.SaveChangesAsync();
        var service = CreateService(dbContext, pushService, userDateService);

        await service.ActivateMissedDayFreezes(CancellationToken.None);
        await service.ActivateMissedDayFreezes(CancellationToken.None);

        (await dbContext.StreakFreezes.AsNoTracking().CountAsync()).Should().Be(1);
        (await dbContext.Notifications.AsNoTracking().CountAsync()).Should().Be(1);
        await pushService.Received(1).SendToUserAsync(
            UserId,
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ActivateMissedDayFreezes_FreeUserWithoutFlag_DoesNotActivate()
    {
        await using var dbContext = CreateDbContext();
        var pushService = Substitute.For<IPushNotificationService>();
        var userDateService = Substitute.For<IUserDateService>();
        userDateService.GetUserTodayAsync(UserId, Arg.Any<CancellationToken>()).Returns(Today);
        var user = CreateEligibleProUser();
        user.StartTrial(Instant.UtcDateTime.AddDays(-1));
        dbContext.Users.Add(user);
        await dbContext.SaveChangesAsync();
        var service = CreateService(dbContext, pushService, userDateService);

        await service.ActivateMissedDayFreezes(CancellationToken.None);

        (await dbContext.StreakFreezes.AsNoTracking().CountAsync()).Should().Be(0);
        user.StreakFreezesAccumulated.Should().Be(1);
        await pushService.DidNotReceive().SendToUserAsync(
            Arg.Any<Guid>(),
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ActivateMissedDayFreezes_UsesUserDateServiceForMissedDate()
    {
        await using var dbContext = CreateDbContext();
        var pushService = Substitute.For<IPushNotificationService>();
        var userDateService = Substitute.For<IUserDateService>();
        userDateService.GetUserTodayAsync(UserId, Arg.Any<CancellationToken>())
            .Returns(new DateOnly(2026, 6, 5));
        var user = CreateEligibleProUser();
        dbContext.Users.Add(user);
        await dbContext.SaveChangesAsync();
        var service = CreateService(dbContext, pushService, userDateService);

        await service.ActivateMissedDayFreezes(CancellationToken.None);

        (await dbContext.StreakFreezes.AsNoTracking().SingleAsync()).UsedOnDate
            .Should().Be(new DateOnly(2026, 6, 4));
        await userDateService.Received(1).GetUserTodayAsync(
            UserId,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ActivateMissedDayFreezes_CompletionTodayAfterMissedYesterday_StillRepairs()
    {
        await using var dbContext = CreateDbContext();
        var pushService = Substitute.For<IPushNotificationService>();
        var userDateService = Substitute.For<IUserDateService>();
        userDateService.GetUserTodayAsync(UserId, Arg.Any<CancellationToken>()).Returns(Today);
        var user = CreateEligibleProUser();
        user.SetStreakState(1, 10, Today);
        var habit = Habit.Create(new HabitCreateParams(
            UserId,
            "Run",
            FrequencyUnit.Day,
            1,
            DueDate: new DateOnly(2026, 5, 30))).Value;
        typeof(Habit).GetProperty(nameof(Habit.CreatedAtUtc))!.SetValue(
            habit,
            new DateTime(2026, 5, 30, 0, 0, 0, DateTimeKind.Utc));
        foreach (var date in new[]
        {
            new DateOnly(2026, 5, 30),
            new DateOnly(2026, 5, 31),
            new DateOnly(2026, 6, 1),
            new DateOnly(2026, 6, 2),
            Today
        })
        {
            habit.Log(date, advanceDueDate: false);
        }
        dbContext.Users.Add(user);
        dbContext.Habits.Add(habit);
        await dbContext.SaveChangesAsync();
        var streakService = new UserStreakService(
            new UserStreakRepositories(
                new GenericRepository<User>(dbContext),
                new GenericRepository<Habit>(dbContext),
                new GenericRepository<HabitLog>(dbContext),
                new GenericRepository<StreakFreeze>(dbContext)),
            userDateService,
            Substitute.For<IFriendFeedEventEmitter>());
        var service = CreateService(dbContext, pushService, userDateService, streakService);

        await service.ActivateMissedDayFreezes(CancellationToken.None);

        (await dbContext.StreakFreezes.AsNoTracking().SingleAsync()).UsedOnDate
            .Should().Be(new DateOnly(2026, 6, 3));
        user.StreakFreezesAccumulated.Should().Be(0);
    }

    [Fact]
    public async Task ActivateMissedDayFreezes_WhenRepairDoesNotRaiseStreak_DoesNotSpend()
    {
        await using var dbContext = CreateDbContext();
        var pushService = Substitute.For<IPushNotificationService>();
        var userDateService = Substitute.For<IUserDateService>();
        userDateService.GetUserTodayAsync(UserId, Arg.Any<CancellationToken>()).Returns(Today);
        var streakService = Substitute.For<IUserStreakService>();
        streakService.EvaluateRepairAsync(
                UserId,
                Today,
                new DateOnly(2026, 6, 3),
                Arg.Any<CancellationToken>())
            .Returns(StreakRepairEvaluation.Unavailable(new DateOnly(2026, 6, 3)));
        var user = CreateEligibleProUser();
        dbContext.Users.Add(user);
        await dbContext.SaveChangesAsync();
        var service = CreateService(dbContext, pushService, userDateService, streakService);

        await service.ActivateMissedDayFreezes(CancellationToken.None);

        (await dbContext.StreakFreezes.AsNoTracking().CountAsync()).Should().Be(0);
        user.StreakFreezesAccumulated.Should().Be(1);
    }

    private static User CreateEligibleProUser()
    {
        var user = User.Create("Test User", "test@example.com").Value;
        typeof(User).GetProperty(nameof(User.Id))!.SetValue(user, UserId);
        user.SetStreakState(10, 10, new DateOnly(2026, 6, 2));
        user.AwardStreakFreezeIfEligible();
        return user;
    }

    private static OrbitDbContext CreateDbContext() =>
        new(new DbContextOptionsBuilder<OrbitDbContext>()
            .UseInMemoryDatabase($"StreakFreezeAutoActivationServiceTests_{Guid.NewGuid()}")
            .Options);

    private static StreakFreezeAutoActivationService CreateService(
        OrbitDbContext dbContext,
        IPushNotificationService pushService,
        IUserDateService userDateService,
        IUserStreakService? userStreakService = null)
    {
        userStreakService ??= CreateAvailableRepairService();
        var serviceProvider = new ServiceCollection()
            .AddSingleton(dbContext)
            .AddSingleton(pushService)
            .AddSingleton(userDateService)
            .AddSingleton(userStreakService)
            .BuildServiceProvider();
        return new StreakFreezeAutoActivationService(
            serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<StreakFreezeAutoActivationService>.Instance,
            new ConfigurationBuilder().Build());
    }

    private static IUserStreakService CreateAvailableRepairService()
    {
        var service = Substitute.For<IUserStreakService>();
        service.EvaluateRepairAsync(
                Arg.Any<Guid>(),
                Arg.Any<DateOnly>(),
                Arg.Any<DateOnly>(),
                Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var missedDate = call.ArgAt<DateOnly>(2);
                return StreakRepairEvaluation.Available(
                    missedDate,
                    new UserStreakState(10, 10, missedDate));
            });
        return service;
    }
}
