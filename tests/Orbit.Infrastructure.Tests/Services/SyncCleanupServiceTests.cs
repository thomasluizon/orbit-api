using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Orbit.Application.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Infrastructure.Persistence;
using Orbit.Infrastructure.Services;

namespace Orbit.Infrastructure.Tests.Services;

/// <summary>
/// Tests the soft-delete purge window. The cleanup cutoff must carry a margin beyond the
/// 30-day incremental-sync contract so a tombstone is never GC'd on the exact boundary a
/// slow client may still request via /sync/changes.
/// </summary>
public class SyncCleanupServiceTests
{
    private static readonly Guid UserId = Guid.NewGuid();

    [Fact]
    public async Task PurgeSoftDeletedEntities_DeletedJustPastSyncWindow_IsRetainedByMargin()
    {
        await using var dbContext = CreateInMemoryDbContext();
        var habit = CreateDeletedHabit(deletedDaysAgo: AppConstants.MaxSyncWindowDays + 0.5);
        dbContext.Habits.Add(habit);
        await dbContext.SaveChangesAsync();

        var service = CreateService(dbContext);
        await service.PurgeSoftDeletedEntities(CancellationToken.None);

        (await dbContext.Habits.IgnoreQueryFilters().CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task PurgeSoftDeletedEntities_DeletedBeyondWindowPlusMargin_IsPurged()
    {
        await using var dbContext = CreateInMemoryDbContext();
        var habit = CreateDeletedHabit(deletedDaysAgo: AppConstants.MaxSyncWindowDays + AppConstants.SyncCleanupMarginDays + 1);
        dbContext.Habits.Add(habit);
        await dbContext.SaveChangesAsync();

        var service = CreateService(dbContext);
        await service.PurgeSoftDeletedEntities(CancellationToken.None);

        (await dbContext.Habits.IgnoreQueryFilters().CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task PurgeSoftDeletedEntities_RecentlyDeleted_IsRetained()
    {
        await using var dbContext = CreateInMemoryDbContext();
        var habit = CreateDeletedHabit(deletedDaysAgo: 1);
        dbContext.Habits.Add(habit);
        await dbContext.SaveChangesAsync();

        var service = CreateService(dbContext);
        await service.PurgeSoftDeletedEntities(CancellationToken.None);

        (await dbContext.Habits.IgnoreQueryFilters().CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task PurgeSoftDeletedEntities_AgedSoftDeletedLogsAndNotifications_ArePurged()
    {
        await using var dbContext = CreateInMemoryDbContext();
        var agedDays = AppConstants.MaxSyncWindowDays + AppConstants.SyncCleanupMarginDays + 1;

        var habitLog = HabitLogTestFactory.CreateDeleted(agedDays);
        var notification = Notification.Create(UserId, "Title", "Body");
        notification.SoftDelete();
        SetDeletedAt(notification, agedDays);
        var template = ChecklistTemplate.Create(UserId, "Morning", ["Item"]).Value;
        template.SoftDelete();
        SetDeletedAt(template, agedDays);

        dbContext.HabitLogs.Add(habitLog);
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

    private static class HabitLogTestFactory
    {
        public static HabitLog CreateDeleted(double deletedDaysAgo)
        {
            var today = DateOnly.FromDateTime(DateTime.UtcNow);
            var habit = Habit.Create(new HabitCreateParams(UserId, "Exercise", FrequencyUnit.Day, 1, DueDate: today)).Value;
            var log = habit.Log(today).Value;
            habit.Unlog(today);
            SetDeletedAt(log, deletedDaysAgo);
            return log;
        }
    }

    private static Habit CreateDeletedHabit(double deletedDaysAgo)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var habit = Habit.Create(new HabitCreateParams(UserId, "Exercise", FrequencyUnit.Day, 1, DueDate: today)).Value;
        habit.SoftDelete();
        typeof(Habit)
            .GetProperty(nameof(Habit.DeletedAtUtc))!
            .SetValue(habit, DateTime.UtcNow.AddDays(-deletedDaysAgo));
        return habit;
    }

    private static OrbitDbContext CreateInMemoryDbContext()
    {
        var options = new DbContextOptionsBuilder<OrbitDbContext>()
            .UseInMemoryDatabase($"SyncCleanupServiceTests_{Guid.NewGuid()}")
            .Options;
        return new OrbitDbContext(options);
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
