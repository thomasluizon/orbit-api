using System.Data.Common;
using System.Reflection;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
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
/// Tests the pure pieces of StreakFreezeAutoActivationService: the localized notification
/// copy, the interval default, the SentStreakFreezeAlert guard entity, and the eligibility
/// predicate (replicated here and asserted across include/exclude cases). The background
/// loop and DB interactions are integration concerns.
/// </summary>
public class StreakFreezeAutoActivationServiceTests
{
    private static readonly BindingFlags PrivateStatic =
        BindingFlags.NonPublic | BindingFlags.Static;

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

    [Fact]
    public void IntervalDefault_Is60Minutes()
    {
        var defaultMinutes = GetConfiguredIntervalMinutesDefault();
        defaultMinutes.Should().Be(60);
    }

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

    [Fact]
    public void MissedDate_IsLocalToday_MinusOneDay()
    {
        var userToday = new DateOnly(2026, 6, 4);
        var missedDate = userToday.AddDays(-1);

        missedDate.Should().Be(new DateOnly(2026, 6, 3));
    }

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

    [Fact]
    public async Task LoadRecentCompletions_SoftDeletedHabitLog_DoesNotMarkDateActive()
    {
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

    [Fact]
    public async Task ActivateMissedDayFreezes_OneUserConflict_DoesNotDiscardAnotherUsersFreeze()
    {
        var conflictUser = CreateEligibleProUser();
        var healthyUser = CreateEligibleProUser();

        var interceptor = new ThrowUniqueViolationForUserInterceptor(conflictUser.Id);
        await using var dbContext = CreateInterceptingContext(interceptor);
        var pushService = Substitute.For<IPushNotificationService>();

        dbContext.Users.AddRange(conflictUser, healthyUser);
        await dbContext.SaveChangesAsync();

        var service = CreateService(dbContext, pushService);
        await service.ActivateMissedDayFreezes(CancellationToken.None);

        var conflictFreezes = await dbContext.StreakFreezes.AsNoTracking()
            .CountAsync(f => f.UserId == conflictUser.Id);
        var healthyFreezes = await dbContext.StreakFreezes.AsNoTracking()
            .CountAsync(f => f.UserId == healthyUser.Id);

        conflictFreezes.Should().Be(0);
        healthyFreezes.Should().Be(1);

        await pushService.DidNotReceive().SendToUserAsync(
            conflictUser.Id, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
        await pushService.Received(1).SendToUserAsync(
            healthyUser.Id, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ActivateMissedDayFreezes_OneUserConcurrencyConflict_SkipsUserAndContinuesBatch()
    {
        var conflictUser = CreateEligibleProUser();
        var healthyUser = CreateEligibleProUser();

        var interceptor = new ThrowConcurrencyForUserInterceptor(conflictUser.Id);
        await using var dbContext = CreateInterceptingContext(interceptor);
        var pushService = Substitute.For<IPushNotificationService>();

        dbContext.Users.AddRange(conflictUser, healthyUser);
        await dbContext.SaveChangesAsync();

        var service = CreateService(dbContext, pushService);
        await service.ActivateMissedDayFreezes(CancellationToken.None);

        var conflictFreezes = await dbContext.StreakFreezes.AsNoTracking()
            .CountAsync(f => f.UserId == conflictUser.Id);
        var healthyFreezes = await dbContext.StreakFreezes.AsNoTracking()
            .CountAsync(f => f.UserId == healthyUser.Id);

        conflictFreezes.Should().Be(0);
        healthyFreezes.Should().Be(1);

        await pushService.DidNotReceive().SendToUserAsync(
            conflictUser.Id, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
        await pushService.Received(1).SendToUserAsync(
            healthyUser.Id, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ActivateMissedDayFreezes_PushFiresOnlyAfterFreezePersisted()
    {
        var user = CreateEligibleProUser();

        await using var dbContext = CreateInMemoryDbContext();
        var pushService = Substitute.For<IPushNotificationService>();

        var freezeExistedAtPushTime = false;
        pushService
            .SendToUserAsync(user.Id, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                freezeExistedAtPushTime = dbContext.StreakFreezes
                    .AsNoTracking().Any(f => f.UserId == user.Id);
                return Task.CompletedTask;
            });

        dbContext.Users.Add(user);
        await dbContext.SaveChangesAsync();

        var service = CreateService(dbContext, pushService);
        await service.ActivateMissedDayFreezes(CancellationToken.None);

        freezeExistedAtPushTime.Should().BeTrue();
        (await dbContext.StreakFreezes.AsNoTracking().CountAsync(f => f.UserId == user.Id))
            .Should().Be(1);
        user.StreakFreezesAccumulated.Should().Be(0);
    }

    [Fact]
    public async Task ActivateMissedDayFreezes_FreeUser_FlagOff_DoesNotActivate()
    {
        var user = CreateEligibleFreeUser();

        await using var dbContext = CreateInMemoryDbContext();
        var pushService = Substitute.For<IPushNotificationService>();

        dbContext.Users.Add(user);
        await dbContext.SaveChangesAsync();

        var service = CreateService(dbContext, pushService);
        await service.ActivateMissedDayFreezes(CancellationToken.None);

        (await dbContext.StreakFreezes.AsNoTracking().CountAsync(f => f.UserId == user.Id))
            .Should().Be(0);
        user.StreakFreezesAccumulated.Should().Be(1);
    }

    [Fact]
    public async Task ActivateMissedDayFreezes_FreeUser_FlagOn_ActivatesFreeze()
    {
        var user = CreateEligibleFreeUser();

        await using var dbContext = CreateInMemoryDbContext();
        var pushService = Substitute.For<IPushNotificationService>();

        dbContext.Users.Add(user);
        dbContext.AppFeatureFlags.Add(AppFeatureFlag.Create(FeatureFlagKeys.GamificationFreeTier, enabled: true));
        await dbContext.SaveChangesAsync();

        var service = CreateService(dbContext, pushService);
        await service.ActivateMissedDayFreezes(CancellationToken.None);

        (await dbContext.StreakFreezes.AsNoTracking().CountAsync(f => f.UserId == user.Id))
            .Should().Be(1);
        user.StreakFreezesAccumulated.Should().Be(0);
    }

    private static User CreateEligibleProUser()
    {
        var user = User.Create($"User-{Guid.NewGuid():N}", $"{Guid.NewGuid():N}@test.com").Value;
        var twoDaysAgo = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-2);
        user.SetStreakState(currentStreak: 10, longestStreak: 10, lastActiveDate: twoDaysAgo);
        user.AwardStreakFreezeIfEligible();
        return user;
    }

    private static User CreateEligibleFreeUser()
    {
        var user = User.Create($"User-{Guid.NewGuid():N}", $"{Guid.NewGuid():N}@test.com").Value;
        user.StartTrial(DateTime.UtcNow.AddDays(-1));
        var twoDaysAgo = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-2);
        user.SetStreakState(currentStreak: 10, longestStreak: 10, lastActiveDate: twoDaysAgo);
        user.AwardStreakFreezeIfEligible();
        return user;
    }

    private static OrbitDbContext CreateInMemoryDbContext() =>
        new(new DbContextOptionsBuilder<OrbitDbContext>()
            .UseInMemoryDatabase($"StreakFreezeAutoActivationServiceTests_{Guid.NewGuid()}")
            .Options);

    private static OrbitDbContext CreateInterceptingContext(ISaveChangesInterceptor interceptor) =>
        new(new DbContextOptionsBuilder<OrbitDbContext>()
            .UseInMemoryDatabase($"StreakFreezeAutoActivationServiceTests_{Guid.NewGuid()}")
            .AddInterceptors(interceptor)
            .Options);

    private static StreakFreezeAutoActivationService CreateService(
        OrbitDbContext dbContext, IPushNotificationService pushService)
    {
        var serviceProvider = new ServiceCollection()
            .AddSingleton(dbContext)
            .AddSingleton(pushService)
            .BuildServiceProvider();
        var scopeFactory = serviceProvider.GetRequiredService<IServiceScopeFactory>();
        return new StreakFreezeAutoActivationService(
            scopeFactory, NullLogger<StreakFreezeAutoActivationService>.Instance,
            new ConfigurationBuilder().Build());
    }

    private sealed class ThrowUniqueViolationForUserInterceptor(Guid conflictUserId) : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            var hasConflictingFreeze = eventData.Context?.ChangeTracker
                .Entries<StreakFreeze>()
                .Any(e => e.State == EntityState.Added && e.Entity.UserId == conflictUserId) == true;

            if (hasConflictingFreeze)
                throw new DbUpdateException("duplicate", new FakeUniqueViolationException());

            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }

    private sealed class FakeUniqueViolationException : DbException
    {
        public override string SqlState => "23505";
    }

    private sealed class ThrowConcurrencyForUserInterceptor(Guid conflictUserId) : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            var hasConflictingFreeze = eventData.Context?.ChangeTracker
                .Entries<StreakFreeze>()
                .Any(e => e.State == EntityState.Added && e.Entity.UserId == conflictUserId) == true;

            if (hasConflictingFreeze)
                throw new DbUpdateConcurrencyException("simulated stale token");

            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }

    private static int GetConfiguredIntervalMinutesDefault()
    {
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
