using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.Persistence;

namespace Orbit.Infrastructure.Services;

public class GoalDeadlineNotificationService(
    IServiceScopeFactory scopeFactory,
    ILogger<GoalDeadlineNotificationService> logger) : BackgroundService
{
    private static readonly int[] NotifyDaysBefore = [7, 3, 1];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("GoalDeadlineNotificationService started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CheckAndSendDeadlineNotifications(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Error in goal deadline notification service");
            }

            // Check every 30 minutes
            await Task.Delay(TimeSpan.FromMinutes(30), stoppingToken);
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

        foreach (var goal in goals)
        {
            if (!users.TryGetValue(goal.UserId, out var user)) continue;

            var tz = user.TimeZone is not null
                ? TimeZoneInfo.FindSystemTimeZoneById(user.TimeZone)
                : TimeZoneInfo.Utc;
            var userNow = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, tz);
            var userToday = DateOnly.FromDateTime(userNow);

            if (!goal.Deadline.HasValue) continue;

            var daysUntilDeadline = goal.Deadline.Value.DayNumber - userToday.DayNumber;

            foreach (var daysBefore in NotifyDaysBefore)
            {
                if (daysUntilDeadline != daysBefore) continue;

                // Check if we already sent a notification for this goal + daysBefore.
                // TODO: The Url field is being repurposed as a deduplication key here (notificationKey).
                // This is a hack -- the Notification entity's Url field was designed to hold a navigation URL,
                // not an opaque string identifier. This should be replaced with a proper SentGoalDeadlineNotification
                // entity (similar to SentReminder/SentSlipAlert) that tracks (GoalId, DaysBefore) with a unique
                // constraint, eliminating the need to abuse the Url column for dedup.
                var notificationKey = $"goal-deadline-{goal.Id}-{daysBefore}d";
                var alreadySent = await dbContext.Notifications
                    .AnyAsync(n => n.UserId == goal.UserId && n.Url == notificationKey, ct);

                if (alreadySent) continue;

                var lang = user.Language ?? "en";
                var isPt = lang.StartsWith("pt");

                var progressText = $"{goal.CurrentValue}/{goal.TargetValue} {goal.Unit}";
                var body = daysBefore switch
                {
                    1 => isPt
                        ? $"Sua meta termina amanha - voce esta em {progressText}"
                        : $"Your goal is due tomorrow - you're at {progressText}",
                    _ => isPt
                        ? $"Sua meta termina em {daysBefore} dias - voce esta em {progressText}"
                        : $"Your goal is due in {daysBefore} days - you're at {progressText}"
                };

                await pushService.SendToUserAsync(goal.UserId, goal.Title, body, "/", ct);

                var notification = Notification.Create(goal.UserId, goal.Title, body, notificationKey);
                await dbContext.Notifications.AddAsync(notification, ct);
                await dbContext.SaveChangesAsync(ct);

                logger.LogInformation(
                    "Sent deadline notification ({Days}d before) for goal {GoalId} to user {UserId}",
                    daysBefore, goal.Id, goal.UserId);
            }
        }
    }
}
