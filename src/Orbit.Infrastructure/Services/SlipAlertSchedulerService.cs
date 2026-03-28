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

        // Load active bad habits with slip alerts enabled.
        // Subtract 1 day from UTC date to include users west of UTC whose local "today" lags behind UTC.
        // The per-user timezone check (userToday) below is the authoritative active/ended guard.
        var utcDate = DateOnly.FromDateTime(DateTime.UtcNow);
        var habits = await dbContext.Habits
            .Where(h => !h.IsCompleted && h.IsBadHabit && h.SlipAlertEnabled
                && (!h.EndDate.HasValue || h.EndDate.Value >= utcDate.AddDays(-1)))
            .ToListAsync(ct);

        if (habits.Count == 0) return;

        // Group by user to handle timezones
        var userIds = habits.Select(h => h.UserId).Distinct().ToList();
        var users = await dbContext.Users
            .Where(u => userIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, ct);

        // Batch-load all relevant habit logs upfront (avoid per-habit query in loop)
        var habitIds = habits.Select(h => h.Id).ToList();
        var cutoff = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-60));
        var allLogs = await dbContext.HabitLogs
            .Where(l => habitIds.Contains(l.HabitId) && l.Date >= cutoff)
            .ToListAsync(ct);
        var logsByHabit = allLogs.GroupBy(l => l.HabitId)
            .ToDictionary(g => g.Key, g => g.ToList());

        // Batch-load sent slip alerts for this week
        var daysToMondayUtc = ((int)DateTime.UtcNow.DayOfWeek - 1 + 7) % 7;
        var currentWeekStart = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-daysToMondayUtc));
        var sentAlertHabitIds = (await dbContext.SentSlipAlerts
            .Where(a => habitIds.Contains(a.HabitId) && a.WeekStart == currentWeekStart)
            .Select(a => a.HabitId)
            .ToListAsync(ct)).ToHashSet();

        foreach (var habit in habits)
        {
            if (!users.TryGetValue(habit.UserId, out var user)) continue;

            var tz = user.TimeZone is not null
                ? TimeZoneInfo.FindSystemTimeZoneById(user.TimeZone)
                : TimeZoneInfo.Utc;
            var userNow = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, tz);
            var userToday = DateOnly.FromDateTime(userNow);
            var userTimeNow = TimeOnly.FromDateTime(userNow);

            // Use pre-loaded logs for pattern detection
            var logs = logsByHabit.GetValueOrDefault(habit.Id) ?? [];
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

            // Check weekly idempotency using pre-loaded set
            if (sentAlertHabitIds.Contains(habit.Id)) continue;

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
            var daysToMonday = ((int)userToday.DayOfWeek - 1 + 7) % 7;
            var weekStart = userToday.AddDays(-daysToMonday);
            var sentAlert = SentSlipAlert.Create(habit.Id, weekStart);
            await dbContext.SentSlipAlerts.AddAsync(sentAlert, ct);

            var notification = Notification.Create(habit.UserId, title, body, "/", habit.Id);
            await dbContext.Notifications.AddAsync(notification, ct);

            // Track in memory to prevent duplicate sends within the same scheduler tick
            sentAlertHabitIds.Add(habit.Id);

            await dbContext.SaveChangesAsync(ct);

            logger.LogInformation("Sent slip alert for habit {HabitId} to user {UserId}", habit.Id, habit.UserId);
        }
    }
}
