using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.Persistence;
using Orbit.Infrastructure.Services;
using Orbit.Infrastructure.Tests.Persistence;

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
        ConfigureToday(userDateService, Today);
        var user = await SeedEligibleUserAsync(dbContext, Today);
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
        ConfigureToday(userDateService, Today);
        await SeedEligibleUserAsync(dbContext, Today);
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
        ConfigureToday(userDateService, Today);
        var user = CreateEligibleProUser();
        user.StartTrial(Instant.UtcDateTime.AddDays(-1));
        await SeedEligibleUserAsync(dbContext, Today, user);
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
        var localToday = new DateOnly(2026, 6, 5);
        ConfigureToday(userDateService, localToday);
        await SeedEligibleUserAsync(dbContext, localToday);
        var service = CreateService(dbContext, pushService, userDateService);

        await service.ActivateMissedDayFreezes(CancellationToken.None);

        (await dbContext.StreakFreezes.AsNoTracking().SingleAsync()).UsedOnDate
            .Should().Be(new DateOnly(2026, 6, 4));
        await userDateService.Received(1).GetUserTodayAsync(
            Arg.Any<string?>(),
            UserId,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ActivateMissedDayFreezes_CompletionTodayAfterMissedYesterday_StillRepairs()
    {
        await using var dbContext = CreateDbContext();
        var pushService = Substitute.For<IPushNotificationService>();
        var userDateService = Substitute.For<IUserDateService>();
        ConfigureToday(userDateService, Today);
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
        var service = CreateService(dbContext, pushService, userDateService);

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
        ConfigureToday(userDateService, Today);
        var user = CreateEligibleProUser();
        var habit = CreateDailyHabit(UserId, Today.AddDays(-6));
        foreach (var date in new[] { Today.AddDays(-5), Today.AddDays(-4), Today })
            habit.Log(date, advanceDueDate: false);
        dbContext.Users.Add(user);
        dbContext.Habits.Add(habit);
        await dbContext.SaveChangesAsync();
        var service = CreateService(dbContext, pushService, userDateService);

        await service.ActivateMissedDayFreezes(CancellationToken.None);

        (await dbContext.StreakFreezes.AsNoTracking().CountAsync()).Should().Be(0);
        user.StreakFreezesAccumulated.Should().Be(1);
    }

    [Fact]
    public async Task ActivateMissedDayFreezes_QueryCount_IsInvariantToCandidateVolume()
    {
        var small = await CountActivationQueriesAsync(candidateCount: 2);
        var large = await CountActivationQueriesAsync(candidateCount: 20);

        large.Should().Be(small);
        large.Should().BeLessThanOrEqualTo(5);
    }

    private static User CreateEligibleProUser()
    {
        var user = User.Create("Test User", "test@example.com").Value;
        typeof(User).GetProperty(nameof(User.Id))!.SetValue(user, UserId);
        user.SetStreakState(10, 10, new DateOnly(2026, 6, 2));
        user.AwardStreakFreezeIfEligible();
        return user;
    }

    private static async Task<User> SeedEligibleUserAsync(
        OrbitDbContext dbContext,
        DateOnly today,
        User? user = null)
    {
        user ??= CreateEligibleProUser();
        var habit = CreateDailyHabit(user.Id, today.AddDays(-6));
        foreach (var offset in new[] { -5, -4, -3, -2 })
            habit.Log(today.AddDays(offset), advanceDueDate: false);

        dbContext.Users.Add(user);
        dbContext.Habits.Add(habit);
        await dbContext.SaveChangesAsync();
        return user;
    }

    private static Habit CreateDailyHabit(Guid userId, DateOnly dueDate)
    {
        var habit = Habit.Create(new HabitCreateParams(
            userId,
            "Run",
            FrequencyUnit.Day,
            1,
            DueDate: dueDate)).Value;
        typeof(Habit).GetProperty(nameof(Habit.CreatedAtUtc))!.SetValue(
            habit,
            dueDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));
        return habit;
    }

    private static void ConfigureToday(IUserDateService userDateService, DateOnly today) =>
        userDateService.GetUserTodayAsync(
                Arg.Any<string?>(),
                Arg.Any<Guid>(),
                Arg.Any<CancellationToken>())
            .Returns(today);

    private static async Task<int> CountActivationQueriesAsync(int candidateCount)
    {
        var counter = new CountingDbCommandInterceptor();
        using var factory = new SqliteOrbitDbContextFactory(counter);
        var dbContext = factory.Context;
        for (var index = 0; index < candidateCount; index++)
        {
            var userId = Guid.NewGuid();
            var user = User.Create("Query User", $"query-{userId:N}@example.com").Value;
            typeof(User).GetProperty(nameof(User.Id))!.SetValue(user, userId);
            user.SetStreakState(10, 10, Today.AddDays(-3));
            user.AwardStreakFreezeIfEligible();

            var habit = CreateDailyHabit(userId, Today.AddDays(-6));
            foreach (var date in new[] { Today.AddDays(-5), Today.AddDays(-4), Today })
                habit.Log(date, advanceDueDate: false);

            dbContext.Users.Add(user);
            dbContext.Habits.Add(habit);
        }
        await dbContext.SaveChangesAsync();
        dbContext.ChangeTracker.Clear();

        var userDateService = Substitute.For<IUserDateService>();
        ConfigureToday(userDateService, Today);
        var service = CreateService(
            dbContext,
            Substitute.For<IPushNotificationService>(),
            userDateService);

        counter.Reset();
        await service.ActivateMissedDayFreezes(CancellationToken.None);
        return counter.CommandCount;
    }

    private static OrbitDbContext CreateDbContext() =>
        new(new DbContextOptionsBuilder<OrbitDbContext>()
            .UseInMemoryDatabase($"StreakFreezeAutoActivationServiceTests_{Guid.NewGuid()}")
            .Options);

    private static StreakFreezeAutoActivationService CreateService(
        OrbitDbContext dbContext,
        IPushNotificationService pushService,
        IUserDateService userDateService)
    {
        var serviceProvider = new ServiceCollection()
            .AddSingleton(dbContext)
            .AddSingleton(pushService)
            .AddSingleton(userDateService)
            .BuildServiceProvider();
        return new StreakFreezeAutoActivationService(
            serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<StreakFreezeAutoActivationService>.Instance,
            new ConfigurationBuilder().Build());
    }
}
