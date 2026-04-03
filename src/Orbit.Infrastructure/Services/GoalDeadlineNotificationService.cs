using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orbit.Application.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.Persistence;

namespace Orbit.Infrastructure.Services;

public partial class GoalDeadlineNotificationService(
    IServiceScopeFactory scopeFactory,
    ILogger<GoalDeadlineNotificationService> logger,
    IConfiguration configuration) : BackgroundService
{
    private static readonly int[] NotifyDaysBefore = [7, 3, 1];

    private readonly TimeSpan _interval = TimeSpan.FromMinutes(
        configuration.GetValue("BackgroundServices:GoalDeadlineIntervalMinutes", 30));

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

    private async Task CheckAndSendDeadlineNotifications(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<OrbitDbContext>();
        var pushService = scope.ServiceProvider.GetRequiredService<IPushNotificationService>();

        // Load active goals with deadlines
        var goals = await dbContext.Goals
            .Where(g => g.Status == GoalStatus.Active && g.Deadline != null)
            .ToListAsync(ct);

        if (goals.Count == 0) return;

        // Group by user to handle timezones
        var userIds = goals.Select(g => g.UserId).Distinct().ToList();
        var users = await dbContext.Users
            .Where(u => userIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, ct);

        // Batch-load all possible dedup keys upfront to avoid N+1 AnyAsync queries
        var allKeys = goals
            .Where(g => g.Deadline.HasValue)
            .SelectMany(g => NotifyDaysBefore.Select(d => $"goal-deadline-{g.Id}-{d}d"))
            .ToList();
        var sentKeys = (await dbContext.Notifications
            .Where(n => allKeys.Contains(n.Url!))
            .Select(n => n.Url)
            .ToListAsync(ct))
            .ToHashSet();

        var anyChanges = false;

        foreach (var goal in goals)
        {
            anyChanges |= await ProcessGoalDeadlineAsync(goal, users, sentKeys, pushService, dbContext, ct);
        }

        if (anyChanges)
            await dbContext.SaveChangesAsync(ct);
    }

    private async Task<bool> ProcessGoalDeadlineAsync(
        Goal goal, Dictionary<Guid, User> users, HashSet<string?> sentKeys,
        IPushNotificationService pushService, OrbitDbContext dbContext, CancellationToken ct)
    {
        if (!users.TryGetValue(goal.UserId, out var user)) return false;
        if (!goal.Deadline.HasValue) return false;

        var tz = TimeZoneHelper.FindTimeZone(user.TimeZone, logger, user.Id);
        var userNow = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, tz);
        var userToday = DateOnly.FromDateTime(userNow);
        var daysUntilDeadline = goal.Deadline.Value.DayNumber - userToday.DayNumber;

        var anyChanges = false;

        foreach (var daysBefore in NotifyDaysBefore)
        {
            if (daysUntilDeadline != daysBefore) continue;

            // FUTURE: The Url field is being repurposed as a deduplication key here (notificationKey).
            // This should be replaced with a proper SentGoalDeadlineNotification entity.
            var notificationKey = $"goal-deadline-{goal.Id}-{daysBefore}d";
            if (sentKeys.Contains(notificationKey)) continue;

            var body = FormatDeadlineBody(goal, daysBefore, user.Language ?? "en");
            await pushService.SendToUserAsync(goal.UserId, goal.Title, body, "/", ct);

            var notification = Notification.Create(goal.UserId, goal.Title, body, notificationKey);
            await dbContext.Notifications.AddAsync(notification, ct);

            sentKeys.Add(notificationKey);
            anyChanges = true;

            if (logger.IsEnabled(LogLevel.Information))
                LogSentDeadlineNotification(logger, daysBefore, goal.Id, goal.UserId);
        }

        return anyChanges;
    }

    private static string FormatDeadlineBody(Goal goal, int daysBefore, string lang)
    {
        var isPt = lang.StartsWith("pt");
        var progressText = $"{goal.CurrentValue}/{goal.TargetValue} {goal.Unit}";
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

}
