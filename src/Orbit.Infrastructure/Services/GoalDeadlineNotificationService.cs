using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orbit.Application.Common;
using Orbit.Application.Goals.Services;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.BackgroundJobs;
using Orbit.Infrastructure.Persistence;

namespace Orbit.Infrastructure.Services;

public partial class GoalDeadlineNotificationService(
    IServiceScopeFactory scopeFactory,
    ILogger<GoalDeadlineNotificationService> logger,
    IConfiguration configuration) : BackgroundService, IScheduledJob
{
    private static readonly int[] NotifyDaysBefore = [7, 3, 1];

    private readonly TimeSpan _interval = TimeSpan.FromMinutes(
        configuration.GetValue("BackgroundServices:GoalDeadlineIntervalMinutes", 30));

    public string Name => "goal-deadline-notification";

    public string CronExpression => "*/30 * * * *";

    public Task RunAsync(CancellationToken cancellationToken) => CheckAndSendDeadlineNotifications(cancellationToken);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LogServiceStarted(logger);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await CheckAndSendDeadlineNotifications(stoppingToken);
                    BackgroundServiceHealthCheck.RecordTick("GoalDeadlineNotification");
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    LogServiceError(logger, ex);
                }

                await Task.Delay(_interval, stoppingToken);
            }
        }
        finally
        {
            LogServiceStopped(logger);
        }
    }

    internal async Task CheckAndSendDeadlineNotifications(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<OrbitDbContext>();
        var pushService = scope.ServiceProvider.GetRequiredService<IPushNotificationService>();

        var freshStreakValues = await ComputeFreshStreakValuesAsync(dbContext, ct);

        var candidateGoals = await dbContext.Goals
            .AsNoTracking()
            .Where(g => g.Status == GoalStatus.Active && g.Deadline != null)
            .ToListAsync(ct);

        var goals = candidateGoals
            .Where(g => EffectiveCurrentValue(g, freshStreakValues) < g.TargetValue)
            .ToList();

        if (goals.Count == 0) return;

        var userIds = goals.Select(g => g.UserId).Distinct().ToList();
        var users = await dbContext.Users
            .AsNoTracking()
            .Where(u => userIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, ct);

        var allKeys = goals
            .Where(g => g.Deadline.HasValue)
            .SelectMany(g => NotifyDaysBefore.Select(d => $"goal-deadline-{g.Id}-{d}d"))
            .ToList();
        var sentKeys = (await dbContext.Notifications
            .Where(n => allKeys.Contains(n.Url!))
            .Select(n => n.Url)
            .ToListAsync(ct))
            .ToHashSet();

        foreach (var goal in goals)
        {
            await ProcessGoalDeadlineAsync(
                goal, EffectiveCurrentValue(goal, freshStreakValues), users, sentKeys, pushService, dbContext, ct);
        }
    }

    private async Task<Dictionary<Guid, int>> ComputeFreshStreakValuesAsync(OrbitDbContext dbContext, CancellationToken ct)
    {
        var streakWindowStart = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-AppConstants.MaxStreakLookbackDays - 1);
        var streakGoals = await dbContext.Goals
            .AsNoTracking()
            .Where(g => g.Type == GoalType.Streak && g.Status == GoalStatus.Active && g.Deadline != null && !g.IsDeleted)
            .Include(g => g.Habits).ThenInclude(h => h.Logs.Where(l => l.Date >= streakWindowStart))
            .ToListAsync(ct);

        var freshValues = new Dictionary<Guid, int>();
        if (streakGoals.Count == 0) return freshValues;

        var streakUserIds = streakGoals.Select(g => g.UserId).Distinct().ToList();
        var streakUsers = await dbContext.Users
            .AsNoTracking()
            .Where(u => streakUserIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, ct);

        foreach (var goal in streakGoals)
        {
            if (!streakUsers.TryGetValue(goal.UserId, out var user)) continue;

            var tz = TimeZoneHelper.FindTimeZone(user.TimeZone, logger, user.Id);
            var userToday = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, tz));

            var readValue = GoalStreakSyncService.ComputeReadValue(goal, userToday);
            if (readValue.HasValue)
                freshValues[goal.Id] = readValue.Value;
        }

        return freshValues;
    }

    private static decimal EffectiveCurrentValue(Goal goal, IReadOnlyDictionary<Guid, int> freshStreakValues) =>
        freshStreakValues.TryGetValue(goal.Id, out var fresh) ? fresh : goal.CurrentValue;

    private async Task ProcessGoalDeadlineAsync(
        Goal goal, decimal currentValue, Dictionary<Guid, User> users, HashSet<string?> sentKeys,
        IPushNotificationService pushService, OrbitDbContext dbContext, CancellationToken ct)
    {
        if (!users.TryGetValue(goal.UserId, out var user)) return;
        if (!goal.Deadline.HasValue) return;

        var tz = TimeZoneHelper.FindTimeZone(user.TimeZone, logger, user.Id);
        var userNow = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, tz);
        var userToday = DateOnly.FromDateTime(userNow);
        var daysUntilDeadline = goal.Deadline.Value.DayNumber - userToday.DayNumber;

        if (daysUntilDeadline < 1) return;

        foreach (var daysBefore in NotifyDaysBefore)
        {
            if (daysUntilDeadline > daysBefore) continue;

            var notificationKey = $"goal-deadline-{goal.Id}-{daysBefore}d";
            if (sentKeys.Contains(notificationKey)) continue;

            var body = FormatDeadlineBody(goal, currentValue, daysBefore, user.Language ?? "en");

            sentKeys.Add(notificationKey);

            if (!await TryRecordAndSendAsync(goal, body, notificationKey, pushService, dbContext, ct))
                return;

            if (logger.IsEnabled(LogLevel.Information))
                LogSentDeadlineNotification(logger, daysBefore, goal.Id, goal.UserId);

            return;
        }
    }

    private async Task<bool> TryRecordAndSendAsync(
        Goal goal, string body, string notificationKey,
        IPushNotificationService pushService, OrbitDbContext dbContext, CancellationToken ct)
    {
        await dbContext.Notifications.AddAsync(
            Notification.Create(goal.UserId, goal.Title, body, notificationKey), ct);

        try
        {
            await dbContext.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (DbUniqueViolation.IsUniqueViolation(ex))
        {
            DetachPendingEntries(dbContext);
            if (logger.IsEnabled(LogLevel.Information))
                LogDeadlineAlreadySent(logger, goal.Id);
            return false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            DetachPendingEntries(dbContext);
            LogDeadlineRecordFailed(logger, goal.Id, goal.UserId, ex);
            return false;
        }

        await pushService.SendToUserAsync(goal.UserId, goal.Title, body, "/", ct);
        return true;
    }

    private static void DetachPendingEntries(OrbitDbContext dbContext)
    {
        foreach (var entry in dbContext.ChangeTracker.Entries().ToList())
            entry.State = EntityState.Detached;
    }

    internal static string FormatDeadlineBody(Goal goal, decimal currentValue, int daysBefore, string lang)
    {
        var isPt = LocaleHelper.IsPortuguese(lang);
        var progressText = $"{currentValue}/{goal.TargetValue} {goal.Unit}";
        return daysBefore switch
        {
            1 => isPt
                ? $"Sua meta termina amanhã - você está em {progressText}"
                : $"Your goal is due tomorrow - you're at {progressText}",
            _ => isPt
                ? $"Sua meta termina em {daysBefore} dias - você está em {progressText}"
                : $"Your goal is due in {daysBefore} days - you're at {progressText}"
        };
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "GoalDeadlineNotificationService started")]
    private static partial void LogServiceStarted(ILogger logger);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "GoalDeadlineNotificationService stopped")]
    private static partial void LogServiceStopped(ILogger logger);

    [LoggerMessage(EventId = 3, Level = LogLevel.Error, Message = "Error in goal deadline notification service")]
    private static partial void LogServiceError(ILogger logger, Exception ex);

    [LoggerMessage(EventId = 4, Level = LogLevel.Information, Message = "Sent deadline notification ({Days}d before) for goal {GoalId} to user {UserId}")]
    private static partial void LogSentDeadlineNotification(ILogger logger, int days, Guid goalId, Guid userId);

    [LoggerMessage(EventId = 5, Level = LogLevel.Information, Message = "Deadline notification already recorded for goal {GoalId}; skipping push")]
    private static partial void LogDeadlineAlreadySent(ILogger logger, Guid goalId);

    [LoggerMessage(EventId = 6, Level = LogLevel.Error, Message = "Failed to record deadline notification for goal {GoalId} (user {UserId}); skipping push")]
    private static partial void LogDeadlineRecordFailed(ILogger logger, Guid goalId, Guid userId, Exception ex);

}
