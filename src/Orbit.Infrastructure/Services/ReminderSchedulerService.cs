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

            // Calculate reminder time
            var reminderTime = habit.DueTime!.Value.AddMinutes(-habit.ReminderMinutesBefore);

            // Check if we're within the 1-minute window for sending
            var diffMinutes = (userTimeNow - reminderTime).TotalMinutes;
            if (diffMinutes < 0 || diffMinutes >= 1) continue;

            // Check if already sent
            var alreadySent = await dbContext.SentReminders
                .AnyAsync(r => r.HabitId == habit.Id && r.Date == userToday, ct);
            if (alreadySent) continue;

            // Send push notification (translated)
            var lang = user.Language ?? "en";
            var minutesText = (lang.StartsWith("pt"), habit.ReminderMinutesBefore > 0) switch
            {
                (true, true) => $"Em {habit.ReminderMinutesBefore} minutos",
                (true, false) => "Agora",
                (false, true) => $"Due in {habit.ReminderMinutesBefore} minutes",
                (false, false) => "Due now"
            };

            await pushService.SendToUserAsync(
                habit.UserId,
                habit.Title,
                minutesText,
                "/",
                ct);

            // Record sent reminder + create in-app notification
            var sentReminder = SentReminder.Create(habit.Id, userToday);
            await dbContext.SentReminders.AddAsync(sentReminder, ct);

            var notification = Notification.Create(
                habit.UserId, habit.Title, minutesText, "/", habit.Id);
            await dbContext.Notifications.AddAsync(notification, ct);

            await dbContext.SaveChangesAsync(ct);

            logger.LogInformation("Sent reminder for habit {HabitId} to user {UserId}", habit.Id, habit.UserId);
        }
    }
}
