using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orbit.Application.Common;
using Orbit.Application.Habits.Services;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.Persistence;

namespace Orbit.Infrastructure.Services;

public class ReminderSchedulerService(
    IServiceScopeFactory scopeFactory,
    ILogger<ReminderSchedulerService> logger,
    IConfiguration configuration) : BackgroundService
{
    private readonly TimeSpan _interval = TimeSpan.FromMinutes(
        configuration.GetValue("BackgroundServices:ReminderIntervalMinutes", 1));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("ReminderSchedulerService started");

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await CheckAndSendReminders(stoppingToken);
                    BackgroundServiceHealthCheck.RecordTick("ReminderScheduler");
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "Error in reminder scheduler");
                }

                await Task.Delay(_interval, stoppingToken);
            }
        }
        finally
        {
            logger.LogInformation("ReminderSchedulerService stopped");
        }
    }

    private async Task CheckAndSendReminders(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<OrbitDbContext>();
        var pushService = scope.ServiceProvider.GetRequiredService<IPushNotificationService>();

        var anyChanges = false;

        // === Pass 1: Relative reminders (habits with DueTime) ===
        anyChanges |= await ProcessRelativeReminders(dbContext, pushService, ct);

        // === Pass 2: Scheduled reminders (habits without DueTime) ===
        anyChanges |= await ProcessScheduledReminders(dbContext, pushService, ct);

        if (anyChanges)
            await dbContext.SaveChangesAsync(ct);
    }

    private async Task<bool> ProcessRelativeReminders(OrbitDbContext dbContext, IPushNotificationService pushService, CancellationToken ct)
    {
        var habits = await dbContext.Habits
            .Where(h => !h.IsCompleted && !h.IsGeneral && h.ReminderEnabled && h.DueTime != null)
            .ToListAsync(ct);

        if (habits.Count == 0) return false;

        var userIds = habits.Select(h => h.UserId).Distinct().ToList();
        var users = await dbContext.Users
            .Where(u => userIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, ct);

        var habitIds = habits.Select(h => h.Id).ToList();
        var utcToday = DateOnly.FromDateTime(DateTime.UtcNow);
        var loggedHabitIds = (await dbContext.HabitLogs
            .Where(l => habitIds.Contains(l.HabitId) && l.Date == utcToday)
            .Select(l => l.HabitId)
            .ToListAsync(ct)).ToHashSet();

        var sentReminderKeys = await dbContext.SentReminders
            .Where(r => habitIds.Contains(r.HabitId) && r.Date == utcToday && r.ReminderTimeUtc == null)
            .Select(r => new { r.HabitId, r.MinutesBefore })
            .ToListAsync(ct);
        var sentReminderSet = sentReminderKeys
            .Select(r => (r.HabitId, r.MinutesBefore))
            .ToHashSet();

        var anyChanges = false;

        foreach (var habit in habits)
        {
            if (!users.TryGetValue(habit.UserId, out var user)) continue;

            var tz = TimeZoneHelper.FindTimeZone(user.TimeZone, logger, user.Id);
            var userNow = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, tz);
            var userToday = DateOnly.FromDateTime(userNow);
            var userTimeNow = TimeOnly.FromDateTime(userNow);

            if (!HabitScheduleService.IsHabitDueOnDate(habit, userToday))
                continue;

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

                sentReminderSet.Add((habit.Id, minutesBefore));
                anyChanges = true;

                logger.LogInformation("Sent reminder ({Minutes}min) for habit {HabitId} to user {UserId}", minutesBefore, habit.Id, habit.UserId);
            }
        }

        return anyChanges;
    }

    private async Task<bool> ProcessScheduledReminders(OrbitDbContext dbContext, IPushNotificationService pushService, CancellationToken ct)
    {
        var habits = await dbContext.Habits
            .Where(h => !h.IsCompleted && !h.IsGeneral && h.ReminderEnabled && h.DueTime == null)
            .ToListAsync(ct);

        // Filter to habits that actually have scheduled reminders (can't do jsonb length check in EF)
        habits = habits.Where(h => h.ScheduledReminders.Count > 0).ToList();

        if (habits.Count == 0) return false;

        var userIds = habits.Select(h => h.UserId).Distinct().ToList();
        var users = await dbContext.Users
            .Where(u => userIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, ct);

        var habitIds = habits.Select(h => h.Id).ToList();
        var utcToday = DateOnly.FromDateTime(DateTime.UtcNow);

        // Pre-load sent scheduled reminders (those with ReminderTimeUtc set)
        var sentScheduledKeys = await dbContext.SentReminders
            .Where(r => habitIds.Contains(r.HabitId) && r.ReminderTimeUtc != null
                && (r.Date == utcToday || r.Date == utcToday.AddDays(-1) || r.Date == utcToday.AddDays(1)))
            .Select(r => new { r.HabitId, r.Date, r.ReminderTimeUtc })
            .ToListAsync(ct);
        var sentScheduledSet = sentScheduledKeys
            .Select(r => (r.HabitId, r.Date, ReminderTimeUtc: r.ReminderTimeUtc!.Value))
            .ToHashSet();

        var anyChanges = false;

        foreach (var habit in habits)
        {
            if (!users.TryGetValue(habit.UserId, out var user)) continue;

            var tz = TimeZoneHelper.FindTimeZone(user.TimeZone, logger, user.Id);
            var userNow = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, tz);
            var userToday = DateOnly.FromDateTime(userNow);
            var userTomorrow = userToday.AddDays(1);
            var userTimeNow = TimeOnly.FromDateTime(userNow);

            var isDueToday = HabitScheduleService.IsHabitDueOnDate(habit, userToday);
            var isDueTomorrow = HabitScheduleService.IsHabitDueOnDate(habit, userTomorrow);

            foreach (var sr in habit.ScheduledReminders)
            {
                DateOnly reminderForDate;
                if (sr.When == ScheduledReminderWhen.SameDay && isDueToday)
                    reminderForDate = userToday;
                else if (sr.When == ScheduledReminderWhen.DayBefore && isDueTomorrow)
                    reminderForDate = userToday;
                else
                    continue;

                // Check if current time matches (within 1-minute window)
                var diffMinutes = (userTimeNow - sr.Time).TotalMinutes;
                if (diffMinutes < 0 || diffMinutes >= 1) continue;

                // Dedup: use the date the habit is due for, plus the reminder time
                var dueDate = sr.When == ScheduledReminderWhen.SameDay ? userToday : userTomorrow;
                if (sentScheduledSet.Contains((habit.Id, dueDate, sr.Time))) continue;

                var lang = user.Language ?? "en";
                var text = FormatScheduledReminderText(sr.When, lang);

                await pushService.SendToUserAsync(habit.UserId, habit.Title, text, "/", ct);

                var sentReminder = SentReminder.Create(habit.Id, dueDate, 0, sr.Time);
                await dbContext.SentReminders.AddAsync(sentReminder, ct);

                var notification = Notification.Create(habit.UserId, habit.Title, text, "/", habit.Id);
                await dbContext.Notifications.AddAsync(notification, ct);

                sentScheduledSet.Add((habit.Id, dueDate, sr.Time));
                anyChanges = true;

                logger.LogInformation("Sent scheduled reminder ({When} at {Time}) for habit {HabitId} to user {UserId}",
                    sr.When, sr.Time, habit.Id, habit.UserId);
            }
        }

        return anyChanges;
    }

    private static string FormatReminderText(int minutesBefore, string lang)
    {
        var isPt = lang.StartsWith("pt");
        return minutesBefore switch
        {
            0 => isPt ? "Agora" : "Due now",
            < 60 => isPt ? $"Em {minutesBefore} {(minutesBefore == 1 ? "minuto" : "minutos")}" : $"Due in {minutesBefore} minutes",
            < 1440 => isPt ? $"Em {minutesBefore / 60} hora{(minutesBefore / 60 > 1 ? "s" : "")}" : $"Due in {minutesBefore / 60} hour{(minutesBefore / 60 > 1 ? "s" : "")}",
            _ => isPt ? $"Em {minutesBefore / 1440} dia{(minutesBefore / 1440 > 1 ? "s" : "")}" : $"Due in {minutesBefore / 1440} day{(minutesBefore / 1440 > 1 ? "s" : "")}"
        };
    }

    private static string FormatScheduledReminderText(ScheduledReminderWhen when, string lang)
    {
        var isPt = lang.StartsWith("pt");
        return when switch
        {
            ScheduledReminderWhen.SameDay => isPt ? "Para hoje" : "Due today",
            ScheduledReminderWhen.DayBefore => isPt ? "Para amanhã" : "Due tomorrow",
            _ => isPt ? "Lembrete" : "Reminder"
        };
    }
}
