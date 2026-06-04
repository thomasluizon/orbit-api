using System.Reflection;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Orbit.Application.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Infrastructure.Persistence;
using Orbit.Infrastructure.Services;

namespace Orbit.Infrastructure.Tests.Services;

/// <summary>
/// Tests the pure pieces of StreakFreezeAutoActivationService: the localized notification
/// copy, the interval default, the SentStreakFreezeAlert guard entity, and the eligibility
/// predicate (replicated here and asserted across include/exclude cases). The background
/// loop and DB interactions are integration concerns.
/// </summary>
public class StreakFreezeAutoActivationServiceTests
{
    private static readonly BindingFlags PrivateStatic =
        BindingFlags.NonPublic | BindingFlags.Static;

    // --- Notification copy ---

    [Fact]
    public void BuildNotification_English_MentionsStreakLengthAndFreeze()
    {
        var (title, body) = InvokeBuildNotification(14, "en");

        title.Should().Be("Streak protected");
        body.Should().Contain("14-day");
        body.Should().Contain("freeze");
    }

    [Fact]
    public void BuildNotification_Portuguese_UsesPortugueseCopy()
    {
        var (title, body) = InvokeBuildNotification(14, "pt-BR");

        title.Should().Be("Sequência protegida");
        body.Should().Contain("14 dias");
        body.Should().Contain("congelamento");
    }

    [Fact]
    public void BuildNotification_UnknownLanguage_FallsBackToEnglish()
    {
        var (title, _) = InvokeBuildNotification(3, "fr");

        title.Should().Be("Streak protected");
    }

    // --- Interval default ---

    [Fact]
    public void IntervalDefault_Is60Minutes()
    {
        // The service reads BackgroundServices:StreakFreezeIntervalMinutes with a default of 60.
        // GetValue is invoked at field init; assert the documented default constant directly
        // by constructing with empty configuration.
        var defaultMinutes = GetConfiguredIntervalMinutesDefault();
        defaultMinutes.Should().Be(60);
    }

    // --- SentStreakFreezeAlert entity ---

    [Fact]
    public void SentStreakFreezeAlert_Create_SetsFieldsCorrectly()
    {
        var userId = Guid.NewGuid();
        var frozenDate = new DateOnly(2026, 6, 3);

        var alert = SentStreakFreezeAlert.Create(userId, frozenDate);

        alert.UserId.Should().Be(userId);
        alert.FrozenDate.Should().Be(frozenDate);
        alert.SentAtUtc.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
    }

    // --- Local-yesterday computation ---

    [Fact]
    public void MissedDate_IsLocalToday_MinusOneDay()
    {
        var userToday = new DateOnly(2026, 6, 4);
        var missedDate = userToday.AddDays(-1);

        missedDate.Should().Be(new DateOnly(2026, 6, 3));
    }

    // --- Eligibility predicate (replicates ProcessUserAsync guards) ---

    private static bool IsEligible(EligibilityCase c)
    {
        if (!c.HasProAccess) return false;
        if (c.LastActiveDate is null || c.LastActiveDate >= c.MissedDate) return false;
        if (c.HasFreezeOnMissedDate) return false;
        if (c.HasGuardOnMissedDate) return false;
        if (c.HasCompletionOnMissedDate) return false;
        if (c.FreezesThisMonth >= AppConstants.MaxStreakFreezesPerMonth) return false;
        if (c.StreakFreezesAccumulated <= 0) return false;
        return true;
    }

    private sealed record EligibilityCase
    {
        public bool HasProAccess { get; init; } = true;
        public DateOnly MissedDate { get; init; } = new(2026, 6, 3);
        public DateOnly? LastActiveDate { get; init; } = new(2026, 6, 2);
        public bool HasFreezeOnMissedDate { get; init; }
        public bool HasGuardOnMissedDate { get; init; }
        public bool HasCompletionOnMissedDate { get; init; }
        public int FreezesThisMonth { get; init; }
        public int StreakFreezesAccumulated { get; init; } = 2;
    }

    [Fact]
    public void Eligibility_AllConditionsMet_IsEligible()
    {
        IsEligible(new EligibilityCase()).Should().BeTrue();
    }

    [Fact]
    public void Eligibility_NotPro_Excluded()
    {
        IsEligible(new EligibilityCase { HasProAccess = false }).Should().BeFalse();
    }

    [Fact]
    public void Eligibility_LastActiveOnMissedDate_Excluded()
    {
        // User was credited active on the "missed" day -> not actually missed.
        IsEligible(new EligibilityCase { LastActiveDate = new DateOnly(2026, 6, 3) })
            .Should().BeFalse();
    }

    [Fact]
    public void Eligibility_LastActiveAfterMissedDate_Excluded()
    {
        IsEligible(new EligibilityCase { LastActiveDate = new DateOnly(2026, 6, 4) })
            .Should().BeFalse();
    }

    [Fact]
    public void Eligibility_NoLastActiveDate_Excluded()
    {
        IsEligible(new EligibilityCase { LastActiveDate = null }).Should().BeFalse();
    }

    [Fact]
    public void Eligibility_FreezeAlreadyOnMissedDate_Excluded()
    {
        IsEligible(new EligibilityCase { HasFreezeOnMissedDate = true }).Should().BeFalse();
    }

    [Fact]
    public void Eligibility_GuardAlreadyOnMissedDate_Excluded()
    {
        IsEligible(new EligibilityCase { HasGuardOnMissedDate = true }).Should().BeFalse();
    }

    [Fact]
    public void Eligibility_CompletionOnMissedDate_Excluded()
    {
        IsEligible(new EligibilityCase { HasCompletionOnMissedDate = true }).Should().BeFalse();
    }

    [Fact]
    public void Eligibility_MonthlyCapReached_Excluded()
    {
        IsEligible(new EligibilityCase { FreezesThisMonth = AppConstants.MaxStreakFreezesPerMonth })
            .Should().BeFalse();
    }

    [Fact]
    public void Eligibility_NoInventory_Excluded()
    {
        IsEligible(new EligibilityCase { StreakFreezesAccumulated = 0 }).Should().BeFalse();
    }

    // --- Completion loading (LoadRecentCompletionsAsync excludes soft-deleted habits) ---

    [Fact]
    public async Task LoadRecentCompletions_SoftDeletedHabitLog_DoesNotMarkDateActive()
    {
        // A soft-deleted habit's completion log must not count as activity: otherwise it would
        // falsely cover the missed date and suppress the auto-freeze even though no live habit
        // was completed. Mirrors UserStreakService, which also excludes IsDeleted habits.
        var userId = Guid.NewGuid();
        var missedDate = new DateOnly(2026, 6, 3);
        var since = missedDate.AddDays(-1);

        await using var context = CreateInMemoryContext();

        var activeHabit = CreateHabit(userId);
        var deletedHabit = CreateHabit(userId);
        deletedHabit.SoftDelete();
        context.Habits.AddRange(activeHabit, deletedHabit);

        deletedHabit.Log(missedDate);
        context.HabitLogs.AddRange(deletedHabit.Logs);
        await context.SaveChangesAsync();

        var completionsByUser = await InvokeLoadRecentCompletions(context, [userId], since);

        var completionDates = completionsByUser.GetValueOrDefault(userId) ?? [];
        completionDates.Should().NotContain(missedDate);
    }

    [Fact]
    public async Task LoadRecentCompletions_LiveHabitLog_MarksDateActive()
    {
        var userId = Guid.NewGuid();
        var completedDate = new DateOnly(2026, 6, 3);
        var since = completedDate.AddDays(-1);

        await using var context = CreateInMemoryContext();

        var activeHabit = CreateHabit(userId);
        context.Habits.Add(activeHabit);

        activeHabit.Log(completedDate);
        context.HabitLogs.AddRange(activeHabit.Logs);
        await context.SaveChangesAsync();

        var completionsByUser = await InvokeLoadRecentCompletions(context, [userId], since);

        completionsByUser.GetValueOrDefault(userId).Should().Contain(completedDate);
    }

    // --- Helpers ---

    private static (string Title, string Body) InvokeBuildNotification(int currentStreak, string lang)
    {
        var method = typeof(StreakFreezeAutoActivationService)
            .GetMethod("BuildNotification", PrivateStatic)!;
        return ((string, string))method.Invoke(null, [currentStreak, lang])!;
    }

    private static OrbitDbContext CreateInMemoryContext()
    {
        var options = new DbContextOptionsBuilder<OrbitDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new OrbitDbContext(options);
    }

    private static Habit CreateHabit(Guid userId) =>
        Habit.Create(new HabitCreateParams(
            userId, "Habit", FrequencyUnit.Day, 1, DueDate: new DateOnly(2026, 6, 3))).Value;

    private static async Task<Dictionary<Guid, HashSet<DateOnly>>> InvokeLoadRecentCompletions(
        OrbitDbContext context, List<Guid> userIds, DateOnly since)
    {
        var method = typeof(StreakFreezeAutoActivationService)
            .GetMethod("LoadRecentCompletionsAsync", PrivateStatic)!;
        var task = (Task<Dictionary<Guid, HashSet<DateOnly>>>)method.Invoke(
            null, [context, userIds, since, CancellationToken.None])!;
        return await task;
    }

    private static int GetConfiguredIntervalMinutesDefault()
    {
        // Construct the service with an empty configuration so the field initializer resolves
        // to the hard-coded default, then read back the private _interval field.
        var configuration = new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build();
        var service = (StreakFreezeAutoActivationService)Activator.CreateInstance(
            typeof(StreakFreezeAutoActivationService),
            Substitute.For<Microsoft.Extensions.DependencyInjection.IServiceScopeFactory>(),
            Substitute.For<Microsoft.Extensions.Logging.ILogger<StreakFreezeAutoActivationService>>(),
            configuration)!;
        var interval = (TimeSpan)typeof(StreakFreezeAutoActivationService)
            .GetField("_interval", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(service)!;
        return (int)interval.TotalMinutes;
    }
}
