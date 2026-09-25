using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Orbit.Application.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Infrastructure.Persistence;
using Orbit.Infrastructure.Services;
using Orbit.Infrastructure.Tests.Persistence;

namespace Orbit.Infrastructure.Tests.Services;

/// <summary>
/// Tests the soft-delete purge window. The cleanup cutoff must carry a margin beyond the
/// 30-day incremental-sync contract so a tombstone is never GC'd on the exact boundary a
/// slow client may still request via /sync/changes.
/// </summary>
public class SyncCleanupServiceTests
{
    [Fact]
    public async Task PurgeSoftDeletedEntities_DeletedJustPastSyncWindow_IsRetainedByMargin()
    {
        using var sqliteFactory = new SqliteOrbitDbContextFactory();
        var dbContext = sqliteFactory.Context;
        var user = User.Create("Margin", "margin@example.com").Value;
        var habit = CreateDeletedHabit(
            deletedDaysAgo: AppConstants.MaxSyncWindowDays + 0.5, userId: user.Id);
        dbContext.Users.Add(user);
        dbContext.Habits.Add(habit);
        await dbContext.SaveChangesAsync();

        var service = CreateService(dbContext);
        await service.PurgeSoftDeletedEntities(CancellationToken.None);

        (await dbContext.Habits.IgnoreQueryFilters().CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task PurgeSoftDeletedEntities_DeletedBeyondWindowPlusMargin_IsPurged()
    {
        using var sqliteFactory = new SqliteOrbitDbContextFactory();
        var dbContext = sqliteFactory.Context;
        var user = User.Create("User", "purged@example.com").Value;
        var completionDate = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-40);
        var parent = CreateDeletedHabit(
            deletedDaysAgo: AppConstants.MaxSyncWindowDays + AppConstants.SyncCleanupMarginDays + 1,
            userId: user.Id);
        var child = Habit.Create(new HabitCreateParams(
            user.Id, "Child", FrequencyUnit.Day, 1, DueDate: completionDate,
            ParentHabitId: parent.Id)).Value;
        child.Log(completionDate, advanceDueDate: false);
        dbContext.Users.Add(user);
        dbContext.Habits.AddRange(parent, child);
        await dbContext.SaveChangesAsync();

        var reader = new HabitLogReader(dbContext);
        (await reader.GetLastCompletionDateAsync(user.Id)).Should().Be(completionDate);

        var service = CreateService(dbContext);
        await service.PurgeSoftDeletedEntities(CancellationToken.None);

        (await dbContext.Habits.IgnoreQueryFilters().CountAsync()).Should().Be(0);
        (await dbContext.HabitLogs.IgnoreQueryFilters().CountAsync()).Should().Be(0);
        var persistedUser = await dbContext.Users.AsNoTracking().SingleAsync(u => u.Id == user.Id);
        persistedUser.GetLastCompletionDate(await reader.GetLastCompletionDateAsync(user.Id))
            .Should().Be(completionDate);
    }

    [Fact]
    public async Task PurgeSoftDeletedEntities_LiveGrandchildCompletionSurvivesParentPurge()
    {
        using var sqliteFactory = new SqliteOrbitDbContextFactory();
        var dbContext = sqliteFactory.Context;
        var user = User.Create("Grandchild User", "grandchild@example.com").Value;
        var completionDate = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-40);
        var parent = CreateDeletedHabit(
            AppConstants.MaxSyncWindowDays + AppConstants.SyncCleanupMarginDays + 1, user.Id);
        var child = Habit.Create(new HabitCreateParams(
            user.Id, "Child", FrequencyUnit.Day, 1, DueDate: completionDate,
            ParentHabitId: parent.Id)).Value;
        var grandchild = Habit.Create(new HabitCreateParams(
            user.Id, "Grandchild", FrequencyUnit.Day, 1, DueDate: completionDate,
            ParentHabitId: child.Id)).Value;
        grandchild.Log(completionDate, advanceDueDate: false);
        dbContext.Users.Add(user);
        dbContext.Habits.AddRange(parent, child, grandchild);
        await dbContext.SaveChangesAsync();

        await CreateService(dbContext).PurgeSoftDeletedEntities(CancellationToken.None);

        (await dbContext.Habits.IgnoreQueryFilters().CountAsync()).Should().Be(0);
        (await dbContext.HabitLogs.IgnoreQueryFilters().CountAsync()).Should().Be(0);
        var persistedUser = await dbContext.Users.AsNoTracking().SingleAsync(u => u.Id == user.Id);
        persistedUser.LastPurgedCompletionDate.Should().Be(completionDate);
    }

    [Fact]
    public async Task PurgeSoftDeletedEntities_BadChildIsExcludedAndOtherUsersStayIsolated()
    {
        using var sqliteFactory = new SqliteOrbitDbContextFactory();
        var dbContext = sqliteFactory.Context;
        var firstUser = User.Create("First", "firstchild@example.com").Value;
        var secondUser = User.Create("Second", "secondchild@example.com").Value;
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var agedDays = AppConstants.MaxSyncWindowDays + AppConstants.SyncCleanupMarginDays + 1;
        var firstParent = CreateDeletedHabit(agedDays, firstUser.Id);
        var badChild = Habit.Create(new HabitCreateParams(
            firstUser.Id, "Slip", FrequencyUnit.Day, 1, DueDate: today,
            IsBadHabit: true, ParentHabitId: firstParent.Id)).Value;
        badChild.Log(today.AddDays(-40), advanceDueDate: false);
        var secondParent = CreateDeletedHabit(agedDays, secondUser.Id);
        var goodChild = Habit.Create(new HabitCreateParams(
            secondUser.Id, "Completion", FrequencyUnit.Day, 1, DueDate: today,
            ParentHabitId: secondParent.Id)).Value;
        var completionDate = today.AddDays(-38);
        goodChild.Log(completionDate, advanceDueDate: false);
        dbContext.Users.AddRange(firstUser, secondUser);
        dbContext.Habits.AddRange(firstParent, badChild, secondParent, goodChild);
        await dbContext.SaveChangesAsync();

        await CreateService(dbContext).PurgeSoftDeletedEntities(CancellationToken.None);

        var users = await dbContext.Users.AsNoTracking().ToDictionaryAsync(u => u.Id);
        users[firstUser.Id].LastPurgedCompletionDate.Should().BeNull();
        users[secondUser.Id].LastPurgedCompletionDate.Should().Be(completionDate);
    }

    [Fact]
    public async Task PurgeSoftDeletedEntities_LaterLiveCompletionWinsOverPurgedChild()
    {
        using var sqliteFactory = new SqliteOrbitDbContextFactory();
        var dbContext = sqliteFactory.Context;
        var user = User.Create("Later", "laterchild@example.com").Value;
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var parent = CreateDeletedHabit(
            AppConstants.MaxSyncWindowDays + AppConstants.SyncCleanupMarginDays + 1, user.Id);
        var child = Habit.Create(new HabitCreateParams(
            user.Id, "Old", FrequencyUnit.Day, 1, DueDate: today,
            ParentHabitId: parent.Id)).Value;
        child.Log(today.AddDays(-40), advanceDueDate: false);
        var liveHabit = Habit.Create(new HabitCreateParams(
            user.Id, "Live", FrequencyUnit.Day, 1, DueDate: today)).Value;
        liveHabit.Log(today.AddDays(-1), advanceDueDate: false);
        dbContext.Users.Add(user);
        dbContext.Habits.AddRange(parent, child, liveHabit);
        await dbContext.SaveChangesAsync();

        await CreateService(dbContext).PurgeSoftDeletedEntities(CancellationToken.None);

        var reader = new HabitLogReader(dbContext);
        var persistedUser = await dbContext.Users.AsNoTracking().SingleAsync(u => u.Id == user.Id);
        persistedUser.LastPurgedCompletionDate.Should().Be(today.AddDays(-40));
        persistedUser.GetLastCompletionDate(await reader.GetLastCompletionDateAsync(user.Id))
            .Should().Be(today.AddDays(-1));
    }

    [Fact]
    public async Task PurgeSoftDeletedEntities_ExcludesBadHabitsAndSkipsWithoutLeakingOtherUsersCompletion()
    {
        using var sqliteFactory = new SqliteOrbitDbContextFactory();
        var dbContext = sqliteFactory.Context;
        var firstUser = User.Create("First", "first@example.com").Value;
        var secondUser = User.Create("Second", "second@example.com").Value;
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var agedDays = AppConstants.MaxSyncWindowDays + AppConstants.SyncCleanupMarginDays + 1;

        var badHabit = Habit.Create(new HabitCreateParams(
            firstUser.Id, "Slip", FrequencyUnit.Day, 1, DueDate: today, IsBadHabit: true)).Value;
        badHabit.Log(today.AddDays(-40), advanceDueDate: false);
        badHabit.SoftDelete(DateTime.UtcNow.AddDays(-agedDays));

        var skippedHabit = Habit.Create(new HabitCreateParams(
            firstUser.Id, "Skip", FrequencyUnit.Week, 3, DueDate: today, IsFlexible: true)).Value;
        skippedHabit.SkipFlexible(today.AddDays(-39));
        skippedHabit.SoftDelete(DateTime.UtcNow.AddDays(-agedDays));

        var otherHabit = Habit.Create(new HabitCreateParams(
            secondUser.Id, "Complete", FrequencyUnit.Day, 1, DueDate: today)).Value;
        var otherCompletionDate = today.AddDays(-38);
        otherHabit.Log(otherCompletionDate, advanceDueDate: false);
        otherHabit.SoftDelete(DateTime.UtcNow.AddDays(-agedDays));

        dbContext.Users.AddRange(firstUser, secondUser);
        dbContext.Habits.AddRange(badHabit, skippedHabit, otherHabit);
        await dbContext.SaveChangesAsync();

        await CreateService(dbContext).PurgeSoftDeletedEntities(CancellationToken.None);

        var users = await dbContext.Users.AsNoTracking().ToDictionaryAsync(u => u.Id);
        users[firstUser.Id].LastPurgedCompletionDate.Should().BeNull();
        users[secondUser.Id].LastPurgedCompletionDate.Should().Be(otherCompletionDate);
    }

    [Fact]
    public async Task PurgeSoftDeletedEntities_RecentlyDeleted_IsRetained()
    {
        using var sqliteFactory = new SqliteOrbitDbContextFactory();
        var dbContext = sqliteFactory.Context;
        var user = User.Create("Recent", "recent@example.com").Value;
        var habit = CreateDeletedHabit(deletedDaysAgo: 1, userId: user.Id);
        dbContext.Users.Add(user);
        dbContext.Habits.Add(habit);
        await dbContext.SaveChangesAsync();

        var service = CreateService(dbContext);
        await service.PurgeSoftDeletedEntities(CancellationToken.None);

        (await dbContext.Habits.IgnoreQueryFilters().CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task PurgeSoftDeletedEntities_AgedSoftDeletedLogsAndNotifications_ArePurged()
    {
        using var sqliteFactory = new SqliteOrbitDbContextFactory();
        var dbContext = sqliteFactory.Context;
        var user = User.Create("Other Purge", "otherpurge@example.com").Value;
        var agedDays = AppConstants.MaxSyncWindowDays + AppConstants.SyncCleanupMarginDays + 1;

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var habit = Habit.Create(new HabitCreateParams(
            user.Id, "Exercise", FrequencyUnit.Day, 1, DueDate: today)).Value;
        var habitLog = habit.Log(today).Value;
        habit.Unlog(today);
        SetDeletedAt(habitLog, agedDays);
        var notification = Notification.Create(user.Id, "Title", "Body");
        notification.SoftDelete();
        SetDeletedAt(notification, agedDays);
        var template = ChecklistTemplate.Create(user.Id, "Morning", ["Item"]).Value;
        template.SoftDelete();
        SetDeletedAt(template, agedDays);

        dbContext.Users.Add(user);
        dbContext.Habits.Add(habit);
        dbContext.Notifications.Add(notification);
        dbContext.ChecklistTemplates.Add(template);
        await dbContext.SaveChangesAsync();

        var service = CreateService(dbContext);
        await service.PurgeSoftDeletedEntities(CancellationToken.None);

        (await dbContext.HabitLogs.IgnoreQueryFilters().CountAsync()).Should().Be(0);
        (await dbContext.Notifications.IgnoreQueryFilters().CountAsync()).Should().Be(0);
        (await dbContext.ChecklistTemplates.IgnoreQueryFilters().CountAsync()).Should().Be(0);
    }

    private static void SetDeletedAt(object entity, double daysAgo)
    {
        entity.GetType()
            .GetProperty("DeletedAtUtc")!
            .SetValue(entity, DateTime.UtcNow.AddDays(-daysAgo));
    }

    private static Habit CreateDeletedHabit(double deletedDaysAgo, Guid userId)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var habit = Habit.Create(new HabitCreateParams(userId, "Exercise", FrequencyUnit.Day, 1, DueDate: today)).Value;
        habit.SoftDelete();
        typeof(Habit)
            .GetProperty(nameof(Habit.DeletedAtUtc))!
            .SetValue(habit, DateTime.UtcNow.AddDays(-deletedDaysAgo));
        return habit;
    }

    private static SyncCleanupService CreateService(OrbitDbContext dbContext)
    {
        var serviceProvider = new ServiceCollection()
            .AddSingleton(dbContext)
            .BuildServiceProvider();
        var scopeFactory = serviceProvider.GetRequiredService<IServiceScopeFactory>();
        return new SyncCleanupService(scopeFactory, NullLogger<SyncCleanupService>.Instance);
    }
}
