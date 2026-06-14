using System.Reflection;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Orbit.Application.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.Persistence;
using Orbit.Infrastructure.Services;

namespace Orbit.Infrastructure.Tests.Services;

/// <summary>
/// Tests the helper methods of SlipAlertSchedulerService (CalculateAlertTime,
/// IsWithinSendWindow, WeekStartDay-aware dedup) plus DB-backed regressions for the
/// skip-exclusion and per-week dedup paths.
/// </summary>
public class SlipAlertSchedulerServiceTests
{
    private static readonly BindingFlags PrivateStatic =
        BindingFlags.NonPublic | BindingFlags.Static;

    private static readonly BindingFlags PrivateInstance =
        BindingFlags.NonPublic | BindingFlags.Instance;

    private static readonly DateOnly UtcToday = DateOnly.FromDateTime(DateTime.UtcNow);

    [Theory]
    [InlineData(10, 8)]    [InlineData(12, 10)]    [InlineData(20, 18)]    [InlineData(23, 21)]    [InlineData(9, 8)]    [InlineData(8, 8)]    [InlineData(5, 8)]    public void CalculateAlertTime_WithPeakHour_ReturnsClamped(int peakHour, int expectedHour)
    {
        var result = InvokeCalculateAlertTime(peakHour);

        result.Hour.Should().Be(expectedHour);
        result.Minute.Should().Be(0);
    }

    [Fact]
    public void CalculateAlertTime_WithoutPeakHour_ReturnsMorningDefault()
    {
        var result = InvokeCalculateAlertTime(null);

        result.Hour.Should().Be(8);
        result.Minute.Should().Be(0);
    }

    [Fact]
    public void CalculateAlertTime_HighPeakHour_ClampsTo22()
    {
        var result = InvokeCalculateAlertTime(24);

        result.Hour.Should().Be(22);
    }

    [Theory]
    [InlineData(0, 8)]    [InlineData(1, 8)]    [InlineData(2, 8)]    [InlineData(3, 8)]    public void CalculateAlertTime_VeryEarlyPeakHour_ClampsToMorning(int peakHour, int expectedHour)
    {
        var result = InvokeCalculateAlertTime(peakHour);
        result.Hour.Should().Be(expectedHour);
    }

    [Fact]
    public void CalculateAlertTime_PeakHourAt10_AlertAt8()
    {
        var result = InvokeCalculateAlertTime(10);
        result.Hour.Should().Be(8);
    }

    [Fact]
    public void CalculateAlertTime_PeakHourAt24_AlertAt22()
    {
        var result = InvokeCalculateAlertTime(24);
        result.Hour.Should().Be(22);
    }

    [Fact]
    public void CalculateAlertTime_AlwaysReturnsZeroMinutes()
    {
        for (var h = 0; h <= 24; h++)
        {
            var result = InvokeCalculateAlertTime(h);
            result.Minute.Should().Be(0, $"peak hour {h} should have 0 minutes");
        }
    }

    [Fact]
    public void IsWithinSendWindow_ExactlyAtAlertTime_ReturnsTrue()
    {
        var result = InvokeIsWithinSendWindow(new TimeOnly(10, 0), new TimeOnly(10, 0));
        result.Should().BeTrue();
    }

    [Fact]
    public void IsWithinSendWindow_WithinFiveMinutes_ReturnsTrue()
    {
        var result = InvokeIsWithinSendWindow(new TimeOnly(10, 3), new TimeOnly(10, 0));
        result.Should().BeTrue();
    }

    [Fact]
    public void IsWithinSendWindow_ExactlyFiveMinutesAfter_ReturnsFalse()
    {
        var result = InvokeIsWithinSendWindow(new TimeOnly(10, 5), new TimeOnly(10, 0));
        result.Should().BeFalse();
    }

    [Fact]
    public void IsWithinSendWindow_BeforeAlertTime_ReturnsFalse()
    {
        var result = InvokeIsWithinSendWindow(new TimeOnly(9, 58), new TimeOnly(10, 0));
        result.Should().BeFalse();
    }

    [Fact]
    public void IsWithinSendWindow_LongAfterAlertTime_ReturnsFalse()
    {
        var result = InvokeIsWithinSendWindow(new TimeOnly(14, 0), new TimeOnly(10, 0));
        result.Should().BeFalse();
    }

    [Fact]
    public void IsWithinSendWindow_OneMinuteAfter_ReturnsTrue()
    {
        var result = InvokeIsWithinSendWindow(new TimeOnly(10, 1), new TimeOnly(10, 0));
        result.Should().BeTrue();
    }

    [Fact]
    public void IsWithinSendWindow_FourMinutes59Seconds_ReturnsTrue()
    {
        var result = InvokeIsWithinSendWindow(new TimeOnly(10, 4, 59), new TimeOnly(10, 0));
        result.Should().BeTrue();
    }

    [Fact]
    public void IsWithinSendWindow_OneSecondBefore_ReturnsFalse()
    {
        var result = InvokeIsWithinSendWindow(new TimeOnly(9, 59, 59), new TimeOnly(10, 0));
        result.Should().BeFalse();
    }

    [Fact]
    public void IsWithinSendWindow_MidnightAlertTime_WorksCorrectly()
    {
        var result = InvokeIsWithinSendWindow(new TimeOnly(0, 2), new TimeOnly(0, 0));
        result.Should().BeTrue();
    }

    [Fact]
    public void IsWithinSendWindow_EndOfDay_WorksCorrectly()
    {
        var result = InvokeIsWithinSendWindow(new TimeOnly(23, 58), new TimeOnly(23, 55));
        result.Should().BeTrue();
    }

    [Theory]
    [InlineData(2025, 4, 9, 1, 2025, 4, 7)]
    [InlineData(2025, 4, 9, 0, 2025, 4, 6)]
    [InlineData(2025, 4, 13, 1, 2025, 4, 7)]
    [InlineData(2025, 4, 13, 0, 2025, 4, 13)]
    public void WeekStart_HonorsUserWeekStartDay(
        int year, int month, int day, int weekStartDay,
        int expectedYear, int expectedMonth, int expectedDay)
    {
        var weekStart = WeekMath.WeekStart(new DateOnly(year, month, day), weekStartDay);

        weekStart.Should().Be(new DateOnly(expectedYear, expectedMonth, expectedDay));
    }

    [Fact]
    public void IsWithinSendWindow_DerivesWidthFromInterval_TenMinuteInterval()
    {
        InvokeIsWithinSendWindow(new TimeOnly(10, 7), new TimeOnly(10, 0), intervalMinutes: 10)
            .Should().BeTrue();
    }

    [Fact]
    public void IsWithinSendWindow_DerivesWidthFromInterval_AtIntervalEdge_Excluded()
    {
        InvokeIsWithinSendWindow(new TimeOnly(10, 10), new TimeOnly(10, 0), intervalMinutes: 10)
            .Should().BeFalse();
    }

    [Fact]
    public void IsWithinSendWindow_DerivesWidthFromInterval_OneMinuteInterval_NarrowsWindow()
    {
        InvokeIsWithinSendWindow(new TimeOnly(10, 3), new TimeOnly(10, 0), intervalMinutes: 1)
            .Should().BeFalse();
    }

    [Fact]
    public async Task CheckAndSendAlerts_SkipsOnly_SendsNothing()
    {
        await using var dbContext = CreateInMemoryDbContext();
        var pushService = Substitute.For<IPushNotificationService>();
        var messageService = Substitute.For<ISlipAlertMessageService>();

        var user = User.Create("Thomas", "thomas@test.com").Value;
        var habit = Habit.Create(new HabitCreateParams(
            user.Id, "Doom scrolling", FrequencyUnit.Day, 1,
            IsBadHabit: true, IsFlexible: true, SlipAlertEnabled: true,
            DueDate: UtcToday)).Value;

        var todayWeekday = DateTime.UtcNow;
        for (var i = 0; i < 5; i++)
        {
            var skip = habit.SkipFlexible(UtcToday.AddDays(-7 * i)).Value;
            SetLogCreatedAtUtc(skip, todayWeekday.AddDays(-7 * i));
        }

        dbContext.Users.Add(user);
        dbContext.Habits.Add(habit);
        dbContext.HabitLogs.AddRange(habit.Logs);
        await dbContext.SaveChangesAsync();

        var service = CreateService(dbContext, pushService, messageService);
        await service.CheckAndSendAlerts(CancellationToken.None);

        await pushService.DidNotReceive().SendToUserAsync(
            Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
        (await dbContext.SentSlipAlerts.CountAsync()).Should().Be(0);
        await messageService.DidNotReceive().GenerateMessageAsync(
            Arg.Any<string>(), Arg.Any<DayOfWeek>(), Arg.Any<int?>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CheckAndSendAlerts_AlreadySentThisWeek_SundayStartUser_DoesNotResendOrDuplicate()
    {
        await using var dbContext = CreateInMemoryDbContext();
        var pushService = Substitute.For<IPushNotificationService>();
        var messageService = Substitute.For<ISlipAlertMessageService>();

        var user = User.Create("Thomas", "thomas@test.com").Value;
        user.SetWeekStartDay(0);
        var habit = CreateSlipAlertBadHabit(user.Id);

        var todayWeekday = DateTime.UtcNow;
        for (var i = 0; i < 4; i++)
        {
            var log = habit.Log(UtcToday.AddDays(-7 * i)).Value;
            SetLogCreatedAtUtc(log, todayWeekday.AddDays(-7 * i));
        }

        var sundayWeekStart = WeekMath.WeekStart(UtcToday, weekStartDay: 0);

        dbContext.Users.Add(user);
        dbContext.Habits.Add(habit);
        dbContext.HabitLogs.AddRange(habit.Logs);
        dbContext.SentSlipAlerts.Add(SentSlipAlert.Create(habit.Id, sundayWeekStart));
        await dbContext.SaveChangesAsync();

        var service = CreateService(dbContext, pushService, messageService);
        await service.CheckAndSendAlerts(CancellationToken.None);

        await pushService.DidNotReceive().SendToUserAsync(
            Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
        (await dbContext.SentSlipAlerts.CountAsync(a => a.HabitId == habit.Id)).Should().Be(1);
    }

    [Fact]
    public void SentSlipAlert_Create_SetsFieldsCorrectly()
    {
        var habitId = Guid.NewGuid();
        var weekStart = new DateOnly(2025, 4, 7);

        var alert = SentSlipAlert.Create(habitId, weekStart);

        alert.HabitId.Should().Be(habitId);
        alert.WeekStart.Should().Be(weekStart);
        alert.SentAtUtc.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void DefaultMorningHour_Is8()
    {
        var field = typeof(SlipAlertSchedulerService)
            .GetField("DefaultMorningHour", PrivateStatic)!;
        var value = (int)field.GetValue(null)!;

        value.Should().Be(8);
    }

    private static TimeOnly InvokeCalculateAlertTime(int? peakHour)
    {
        var method = typeof(SlipAlertSchedulerService)
            .GetMethod("CalculateAlertTime", PrivateStatic)!;
        return (TimeOnly)method.Invoke(null, [peakHour])!;
    }

    private static bool InvokeIsWithinSendWindow(
        TimeOnly userTimeNow, TimeOnly alertTime, int intervalMinutes = 5)
    {
        var service = CreateBareService(intervalMinutes);
        var method = typeof(SlipAlertSchedulerService)
            .GetMethod("IsWithinSendWindow", PrivateInstance)!;
        return (bool)method.Invoke(service, [userTimeNow, alertTime])!;
    }

    private static SlipAlertSchedulerService CreateBareService(int intervalMinutes = 5)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["BackgroundServices:SlipAlertIntervalMinutes"] = intervalMinutes.ToString()
            })
            .Build();
        return new SlipAlertSchedulerService(
            Substitute.For<IServiceScopeFactory>(),
            NullLogger<SlipAlertSchedulerService>.Instance,
            configuration);
    }

    private static OrbitDbContext CreateInMemoryDbContext() =>
        new(new DbContextOptionsBuilder<OrbitDbContext>()
            .UseInMemoryDatabase($"SlipAlertSchedulerServiceTests_{Guid.NewGuid()}")
            .Options);

    private static SlipAlertSchedulerService CreateService(
        OrbitDbContext dbContext,
        IPushNotificationService pushService,
        ISlipAlertMessageService messageService)
    {
        var serviceProvider = new ServiceCollection()
            .AddSingleton(dbContext)
            .AddSingleton(pushService)
            .AddSingleton(messageService)
            .BuildServiceProvider();
        var scopeFactory = serviceProvider.GetRequiredService<IServiceScopeFactory>();
        return new SlipAlertSchedulerService(
            scopeFactory, NullLogger<SlipAlertSchedulerService>.Instance,
            new ConfigurationBuilder().Build());
    }

    private static Habit CreateSlipAlertBadHabit(Guid userId) =>
        Habit.Create(new HabitCreateParams(
            userId, "Doom scrolling", FrequencyUnit.Day, 1, IsBadHabit: true,
            SlipAlertEnabled: true, DueDate: UtcToday)).Value;

    private static void SetLogCreatedAtUtc(HabitLog log, DateTime createdAtUtc) =>
        typeof(HabitLog).GetProperty(nameof(HabitLog.CreatedAtUtc))!
            .SetValue(log, createdAtUtc);
}
