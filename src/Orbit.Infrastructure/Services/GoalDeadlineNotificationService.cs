using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orbit.Application.Common;
using Orbit.Application.Goals.Services;
using Orbit.Application.Notifications;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.BackgroundJobs;
using Orbit.Infrastructure.Persistence;
using Orbit.Infrastructure.Services.Hosting;

namespace Orbit.Infrastructure.Services;

public partial class GoalDeadlineNotificationService(
    IServiceScopeFactory scopeFactory,
    ILogger<GoalDeadlineNotificationService> logger,
    IConfiguration configuration) : ScheduledServiceBase, IScheduledJob
{
    private static readonly int[] NotifyDaysBefore = [7, 3, 1];

    private readonly TimeSpan _interval = TimeSpan.FromMinutes(
        configuration.GetValue("BackgroundServices:GoalDeadlineIntervalMinutes", 30));

    public string Name => "goal-deadline-notification";

    public string CronExpression => "*/30 * * * *";

    public Task RunAsync(CancellationToken cancellationToken) => ExecuteTickAsync(cancellationToken);

    protected override TimeSpan Interval => _interval;

    protected override async Task ExecuteTickAsync(CancellationToken stoppingToken)
    {
        await CheckAndSendDeadlineNotifications(stoppingToken);
    }

    protected override void LogStarted() => LogServiceStarted(logger);

    protected override void LogStopped() => LogServiceStopped(logger);

    protected override void LogTickError(Exception ex) => LogServiceError(logger, ex);

    internal async Task CheckAndSendDeadlineNotifications(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<OrbitDbContext>();
        var pushService = scope.ServiceProvider.GetRequiredService<IPushNotificationService>();

        Guid? cursor = null;
        while (true)
        {
            var streakWindowStart = DateOnly.FromDateTime(TimeProvider.System.GetUtcNow().UtcDateTime)
                .AddDays(-AppConstants.MaxStreakLookbackDays - 1);
            var page = await dbContext.Goals.AsNoTracking()
                .Where(goal => goal.Status == GoalStatus.Active && goal.Deadline != null)
                .Page(cursor, configuration.GetValue("BackgroundServices:ScanPageSize", JobPaging.DefaultPageSize))
                .Include(goal => goal.Habits).ThenInclude(habit => habit.Logs.Where(log => log.Date >= streakWindowStart))
                .AsSplitQuery()
                .Select(goal => new
                {
                    Goal = goal,
                    StandardCount = goal.Habits.Where(habit => !habit.IsDeleted).SelectMany(habit => habit.Logs)
                        .Count(log => !log.IsDeleted && log.Value > 0 && log.CreatedAtUtc >= goal.CreatedAtUtc)
                }).ToListAsync(ct);
            if (page.Count == 0) return;
            cursor = page[^1].Goal.Id;
            var goals = page.Select(row => row.Goal).ToList();
            var userIds = goals.Select(goal => goal.UserId).Distinct().ToList();
            var users = await dbContext.Users.AsNoTracking().Where(user => userIds.Contains(user.Id))
                .ToDictionaryAsync(user => user.Id, ct);
            var freshDerivedValues = new Dictionary<Guid, int>();
            foreach (var row in page)
            {
                var goal = row.Goal;
                if (!users.TryGetValue(goal.UserId, out var user)) continue;
                var userToday = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(TimeProvider.System.GetUtcNow().UtcDateTime,
                    TimeZoneHelper.FindTimeZone(user.TimeZone, logger, user.Id)));
                var fresh = goal.Type == GoalType.Standard && goal.Habits.Count > 0
                    ? row.StandardCount : GoalProgressSyncService.ComputeReadValue(goal, userToday, user.WeekStartDay);
                if (fresh.HasValue) freshDerivedValues[goal.Id] = fresh.Value;
            }
            var allKeys = goals.SelectMany(goal => NotifyDaysBefore.Select(days => BuildDedupeKey(goal.Id, days))).ToList();
            var sentKeys = (await dbContext.Notifications.Where(notification => allKeys.Contains(notification.DedupeKey!))
                .Select(notification => notification.DedupeKey!).ToListAsync(ct)).ToHashSet();
            foreach (var goal in goals.Where(goal => EffectiveCurrentValue(goal, freshDerivedValues) < goal.TargetValue))
            {
                try
                {
                    await ProcessGoalDeadlineAsync(goal, EffectiveCurrentValue(goal, freshDerivedValues), users,
                        sentKeys, pushService, dbContext, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    LogGoalDeadlineProcessingFailed(logger, goal.Id, goal.UserId, ex);
                }
            }
            dbContext.ChangeTracker.Clear();
        }
    }

    private static decimal EffectiveCurrentValue(Goal goal, Dictionary<Guid, int> freshStreakValues) =>
        freshStreakValues.TryGetValue(goal.Id, out var fresh) ? fresh : goal.CurrentValue;

    private async Task ProcessGoalDeadlineAsync(
        Goal goal, decimal currentValue, Dictionary<Guid, User> users, HashSet<string> sentKeys,
        IPushNotificationService pushService, OrbitDbContext dbContext, CancellationToken ct)
    {
        if (!users.TryGetValue(goal.UserId, out var user)) return;
        if (!goal.Deadline.HasValue) return;

        var tz = TimeZoneHelper.FindTimeZone(user.TimeZone, logger, user.Id);
        var userNow = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, tz);
        var userToday = DateOnly.FromDateTime(userNow);
        var daysUntilDeadline = goal.Deadline.Value.DayNumber - userToday.DayNumber;

        if (daysUntilDeadline < 1) return;

        foreach (var milestoneDaysBefore in NotifyDaysBefore)
        {
            if (daysUntilDeadline > milestoneDaysBefore) continue;

            var notificationKey = BuildDedupeKey(goal.Id, milestoneDaysBefore);
            if (sentKeys.Contains(notificationKey)) continue;

            var body = FormatDeadlineBody(goal, currentValue, daysUntilDeadline, user.Language ?? "en");

            sentKeys.Add(notificationKey);

            if (!await TryRecordAndSendAsync(goal, body, notificationKey, pushService, dbContext, ct))
                return;

            if (logger.IsEnabled(LogLevel.Debug))
                LogSentDeadlineNotification(logger, milestoneDaysBefore, goal.Id, goal.UserId);

            return;
        }
    }

    private async Task<bool> TryRecordAndSendAsync(
        Goal goal, string body, string notificationKey,
        IPushNotificationService pushService, OrbitDbContext dbContext, CancellationToken ct)
    {
        await dbContext.Notifications.AddAsync(
            Notification.Create(
                goal.UserId,
                goal.Title,
                body,
                NotificationUrls.Progress,
                dedupeKey: notificationKey), ct);

        try
        {
            await dbContext.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (DbUniqueViolation.IsUniqueViolation(ex))
        {
            DetachPendingEntries(dbContext);
            if (logger.IsEnabled(LogLevel.Debug))
                LogDeadlineAlreadySent(logger, goal.Id);
            return false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            DetachPendingEntries(dbContext);
            LogDeadlineRecordFailed(logger, goal.Id, goal.UserId, ex);
            return false;
        }

        DetachPendingEntries(dbContext);
        await pushService.SendToUserAsync(
            goal.UserId, goal.Title, body, NotificationUrls.Progress, ct);
        return true;
    }

    internal static string BuildDedupeKey(Guid goalId, int daysBefore) =>
        $"goal-deadline-{goalId}-{daysBefore}d";

    private static void DetachPendingEntries(OrbitDbContext dbContext)
    {
        foreach (var entry in dbContext.ChangeTracker.Entries().ToList())
            entry.State = EntityState.Detached;
    }

    internal static string FormatDeadlineBody(Goal goal, decimal currentValue, int daysUntilDeadline, string lang)
    {
        var isPt = LocaleHelper.IsPortuguese(lang);
        var culture = CultureInfo.GetCultureInfo(isPt ? "pt-BR" : "en-US");
        var progressText = string.Create(culture, $"{currentValue}/{goal.TargetValue} {goal.Unit}");
        return daysUntilDeadline switch
        {
            1 => isPt
                ? $"Sua meta termina amanhã. Progresso: {progressText}"
                : $"Your goal is due tomorrow - you're at {progressText}",
            _ => isPt
                ? $"Sua meta termina em {daysUntilDeadline} dias. Progresso: {progressText}"
                : $"Your goal is due in {daysUntilDeadline} days - you're at {progressText}"
        };
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "GoalDeadlineNotificationService started")]
    private static partial void LogServiceStarted(ILogger logger);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "GoalDeadlineNotificationService stopped")]
    private static partial void LogServiceStopped(ILogger logger);

    [LoggerMessage(EventId = 3, Level = LogLevel.Error, Message = "Error in goal deadline notification service")]
    private static partial void LogServiceError(ILogger logger, Exception ex);

    [LoggerMessage(EventId = 4, Level = LogLevel.Debug, Message = "Sent deadline notification ({Days}d before) for goal {GoalId} to user {UserId}")]
    private static partial void LogSentDeadlineNotification(ILogger logger, int days, Guid goalId, Guid userId);

    [LoggerMessage(EventId = 5, Level = LogLevel.Debug, Message = "Deadline notification already recorded for goal {GoalId}; skipping push")]
    private static partial void LogDeadlineAlreadySent(ILogger logger, Guid goalId);

    [LoggerMessage(EventId = 6, Level = LogLevel.Error, Message = "Failed to record deadline notification for goal {GoalId} (user {UserId}); skipping push")]
    private static partial void LogDeadlineRecordFailed(ILogger logger, Guid goalId, Guid userId, Exception ex);

    [LoggerMessage(EventId = 7, Level = LogLevel.Error, Message = "Failed to process deadline notification for goal {GoalId} (user {UserId}); continuing with remaining goals")]
    private static partial void LogGoalDeadlineProcessingFailed(ILogger logger, Guid goalId, Guid userId, Exception ex);

}
