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
            .Where(h => !h.IsCompleted && h.ReminderEnabled && h.DueTime != null)
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

            // Check if habit is scheduled for today
            if (!HabitScheduleService.IsHabitDueOnDate(habit, userToday))
                continue;

            // Check if already logged today
            var alreadyLogged = await dbContext.HabitLogs
                .AnyAsync(l => l.HabitId == habit.Id && l.Date == userToday, ct);
            if (alreadyLogged) continue;

            foreach (var minutesBefore in habit.ReminderTimes)
            {
                var reminderTime = habit.DueTime!.Value.AddMinutes(-minutesBefore);

                var diffMinutes = (userTimeNow - reminderTime).TotalMinutes;
                if (diffMinutes < 0 || diffMinutes >= 1) continue;

                var alreadySent = await dbContext.SentReminders
                    .AnyAsync(r => r.HabitId == habit.Id && r.Date == userToday && r.MinutesBefore == minutesBefore, ct);
                if (alreadySent) continue;

                var lang = user.Language ?? "en";
                var minutesText = FormatReminderText(minutesBefore, lang);

                await pushService.SendToUserAsync(habit.UserId, habit.Title, minutesText, "/", ct);

                var sentReminder = SentReminder.Create(habit.Id, userToday, minutesBefore);
                await dbContext.SentReminders.AddAsync(sentReminder, ct);

                var notification = Notification.Create(habit.UserId, habit.Title, minutesText, "/", habit.Id);
                await dbContext.Notifications.AddAsync(notification, ct);

                await dbContext.SaveChangesAsync(ct);

                logger.LogInformation("Sent reminder ({Minutes}min) for habit {HabitId} to user {UserId}", minutesBefore, habit.Id, habit.UserId);
            }
        }
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
