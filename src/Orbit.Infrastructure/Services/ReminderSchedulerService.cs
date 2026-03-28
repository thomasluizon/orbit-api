using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orbit.Application.Habits.Services;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.Persistence;

namespace Orbit.Infrastructure.Services;

public class ReminderSchedulerService(
    IServiceScopeFactory scopeFactory,
    ILogger<ReminderSchedulerService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("ReminderSchedulerService started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CheckAndSendReminders(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Error in reminder scheduler");
            }

            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
        }
    }

    private async Task CheckAndSendReminders(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<OrbitDbContext>();
        var pushService = scope.ServiceProvider.GetRequiredService<IPushNotificationService>();

        // Load habits with reminders enabled and a due time set
        var habits = await dbContext.Habits
            .Where(h => !h.IsCompleted && !h.IsGeneral && h.ReminderEnabled && h.DueTime != null)
            .ToListAsync(ct);

        if (habits.Count == 0) return;

        // Group by user to handle timezones
        var userIds = habits.Select(h => h.UserId).Distinct().ToList();
        var users = await dbContext.Users
            .Where(u => userIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, ct);

        // Batch-load today's log habit IDs and sent reminders to avoid per-habit AnyAsync calls
        var habitIds = habits.Select(h => h.Id).ToList();

        // Pre-load all habit IDs that have a log today (UTC date as approximation; per-user check below)
        var utcToday = DateOnly.FromDateTime(DateTime.UtcNow);
        var loggedHabitIds = (await dbContext.HabitLogs
            .Where(l => habitIds.Contains(l.HabitId) && l.Date == utcToday)
            .Select(l => l.HabitId)
            .ToListAsync(ct)).ToHashSet();

        // Pre-load sent reminders for today
        var sentReminderKeys = await dbContext.SentReminders
            .Where(r => habitIds.Contains(r.HabitId) && r.Date == utcToday)
            .Select(r => new { r.HabitId, r.MinutesBefore })
            .ToListAsync(ct);
        var sentReminderSet = sentReminderKeys
            .Select(r => (r.HabitId, r.MinutesBefore))
            .ToHashSet();

        var anyChanges = false;

        foreach (var habit in habits)
        {
            if (!users.TryGetValue(habit.UserId, out var user)) continue;

            var tz = user.TimeZone is not null
                ? TimeZoneInfo.FindSystemTimeZoneById(user.TimeZone)
                : TimeZoneInfo.Utc;
            var userNow = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, tz);
            var userToday = DateOnly.FromDateTime(userNow);
            var userTimeNow = TimeOnly.FromDateTime(userNow);

            // Check if habit is scheduled for today
            if (!HabitScheduleService.IsHabitDueOnDate(habit, userToday))
                continue;

            // Check if already logged today (use pre-loaded set; UTC date approximation is acceptable here)
            if (loggedHabitIds.Contains(habit.Id)) continue;

            foreach (var minutesBefore in habit.ReminderTimes)
            {
                var reminderTime = habit.DueTime!.Value.AddMinutes(-minutesBefore);

                var diffMinutes = (userTimeNow - reminderTime).TotalMinutes;
                if (diffMinutes < 0 || diffMinutes >= 1) continue;

                if (sentReminderSet.Contains((habit.Id, minutesBefore))) continue;

                var lang = user.Language ?? "en";
                var minutesText = FormatReminderText(minutesBefore, lang);

                await pushService.SendToUserAsync(habit.UserId, habit.Title, minutesText, "/", ct);

                var sentReminder = SentReminder.Create(habit.Id, userToday, minutesBefore);
                await dbContext.SentReminders.AddAsync(sentReminder, ct);

                var notification = Notification.Create(habit.UserId, habit.Title, minutesText, "/", habit.Id);
                await dbContext.Notifications.AddAsync(notification, ct);

                // Track in memory to prevent duplicate sends within the same scheduler tick
                sentReminderSet.Add((habit.Id, minutesBefore));
                anyChanges = true;

                logger.LogInformation("Sent reminder ({Minutes}min) for habit {HabitId} to user {UserId}", minutesBefore, habit.Id, habit.UserId);
            }
        }

        if (anyChanges)
            await dbContext.SaveChangesAsync(ct);
    }

    private static string FormatReminderText(int minutesBefore, string lang)
    {
        var isPt = lang.StartsWith("pt");
        return minutesBefore switch
        {
            0 => isPt ? "Agora" : "Due now",
            < 60 => isPt ? $"Em {minutesBefore} minutos" : $"Due in {minutesBefore} minutes",
            < 1440 => isPt ? $"Em {minutesBefore / 60} hora{(minutesBefore / 60 > 1 ? "s" : "")}" : $"Due in {minutesBefore / 60} hour{(minutesBefore / 60 > 1 ? "s" : "")}",
            _ => isPt ? $"Em {minutesBefore / 1440} dia{(minutesBefore / 1440 > 1 ? "s" : "")}" : $"Due in {minutesBefore / 1440} day{(minutesBefore / 1440 > 1 ? "s" : "")}"
        };
    }
}
