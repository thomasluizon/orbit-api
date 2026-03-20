using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orbit.Application.Habits.Services;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.Persistence;

namespace Orbit.Infrastructure.Services;

public class SlipAlertSchedulerService(
    IServiceScopeFactory scopeFactory,
    ILogger<SlipAlertSchedulerService> logger) : BackgroundService
{
    private const int DefaultMorningHour = 8;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("SlipAlertSchedulerService started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CheckAndSendAlerts(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Error in slip alert scheduler");
            }

            await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
        }
    }

    private async Task CheckAndSendAlerts(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<OrbitDbContext>();
        var pushService = scope.ServiceProvider.GetRequiredService<IPushNotificationService>();
        var messageService = scope.ServiceProvider.GetRequiredService<ISlipAlertMessageService>();

        // Load active bad habits with slip alerts enabled
        var habits = await dbContext.Habits
            .Where(h => h.IsActive && !h.IsCompleted && h.IsBadHabit && h.SlipAlertEnabled)
            .ToListAsync(ct);

        if (habits.Count == 0) return;

        // Group by user to handle timezones
        var userIds = habits.Select(h => h.UserId).Distinct().ToList();
        var users = await dbContext.Users
            .Where(u => userIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, ct);

        foreach (var habit in habits)
        {
            if (!users.TryGetValue(habit.UserId, out var user)) continue;

            var tz = user.TimeZone is not null
                ? TimeZoneInfo.FindSystemTimeZoneById(user.TimeZone)
                : TimeZoneInfo.Utc;
            var userNow = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, tz);
            var userToday = DateOnly.FromDateTime(userNow);
            var userTimeNow = TimeOnly.FromDateTime(userNow);

            // Load habit logs for pattern detection
            var logs = await dbContext.HabitLogs
                .Where(l => l.HabitId == habit.Id)
                .ToListAsync(ct);

            var pattern = SlipPatternDetectionService.DetectPattern(logs, habit.Id, tz);
            if (pattern is null) continue;

            // Check if today matches the pattern's day of week
            if (userNow.DayOfWeek != pattern.DayOfWeek) continue;

            // Calculate alert time:
            // - If time pattern exists: 2 hours before peak, clamped to 8:00-22:00
            // - If day-only pattern: send at 8:00 AM (early morning heads-up)
            var alertHour = pattern.PeakHour.HasValue
                ? Math.Clamp(pattern.PeakHour.Value - 2, 8, 22)
                : DefaultMorningHour;
            var alertTime = new TimeOnly(alertHour, 0);

            // Check if we're within the 5-minute send window
            var diffMinutes = (userTimeNow - alertTime).TotalMinutes;
            if (diffMinutes < 0 || diffMinutes >= 5) continue;

            // Check weekly idempotency (Monday of current week)
            var daysToMonday = ((int)userToday.DayOfWeek - 1 + 7) % 7;
            var weekStart = userToday.AddDays(-daysToMonday);

            var alreadySent = await dbContext.SentSlipAlerts
                .AnyAsync(a => a.HabitId == habit.Id && a.WeekStart == weekStart, ct);
            if (alreadySent) continue;

            // Generate AI message
            var lang = user.Language ?? "en";
            var messageResult = await messageService.GenerateMessageAsync(
                habit.Title, pattern.DayOfWeek, pattern.PeakHour, lang, ct);

            if (messageResult.IsFailure)
            {
                logger.LogWarning("Failed to generate slip alert message for habit {HabitId}", habit.Id);
                continue;
            }

            var (title, body) = messageResult.Value;

            await pushService.SendToUserAsync(habit.UserId, title, body, "/", ct);

            // Record sent alert + create in-app notification
            var sentAlert = SentSlipAlert.Create(habit.Id, weekStart);
            await dbContext.SentSlipAlerts.AddAsync(sentAlert, ct);

            var notification = Notification.Create(habit.UserId, title, body, "/", habit.Id);
            await dbContext.Notifications.AddAsync(notification, ct);

            await dbContext.SaveChangesAsync(ct);

            logger.LogInformation("Sent slip alert for habit {HabitId} to user {UserId}", habit.Id, habit.UserId);
        }
    }
}
