using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Orbit.Application.Common;
using Orbit.Application.Habits.Services;
using Orbit.Application.Notifications;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using Orbit.Infrastructure.Persistence;

namespace Orbit.Infrastructure.Services;

public partial class ReminderSchedulerService
{
    private static Task<List<Habit>> LoadRepeatHabits(
        OrbitDbContext dbContext, DateOnly minLocalDate, CancellationToken ct) =>
        dbContext.Habits.AsNoTracking()
            .Where(h => !h.IsCompleted && h.ReminderEnabled && h.RepeatIntervalMinutes != null
                && (!h.EndDate.HasValue || h.EndDate.Value >= minLocalDate))
            .ToListAsync(ct);

    private async Task<(Dictionary<Guid, DateTime> Anchors, HashSet<(Guid HabitId, DateOnly Date)> LoggedDates,
        List<SentReminder> Sent)> LoadRepeatSnapshot(OrbitDbContext dbContext, List<Habit> repeatHabits,
        Dictionary<Guid, User> users, DateTime nowUtc, DateOnly minLocalDate, CancellationToken ct)
    {
        var repeatIds = repeatHabits.Select(h => h.Id).ToList();
        var zones = repeatHabits.Where(h => users.ContainsKey(h.UserId)).ToDictionary(h => h.Id,
            h => TimeZoneHelper.FindTimeZone(users[h.UserId].TimeZone, logger, h.UserId));
        var anchors = await RepeatReminderDoseLoader.LoadLatestAsync(new GenericRepository<HabitLog>(dbContext), zones, ct);
        var recurringIds = repeatHabits.Where(h => h.Schedule == HabitSchedule.Recurring).Select(h => h.Id).ToList();
        var maxLocalDate = minLocalDate.AddDays(2);
        var loggedDates = (await dbContext.HabitLogs.AsNoTracking()
            .Where(l => recurringIds.Contains(l.HabitId) && l.Value > 0 && l.Date >= minLocalDate && l.Date <= maxLocalDate)
            .Select(l => new { l.HabitId, l.Date }).Distinct()
            .ToListAsync(ct)).Select(l => (l.HabitId, l.Date)).ToHashSet();
        var repeatSent = await RepeatReminderSnapshotQueries.LoadSentAsync(dbContext, repeatIds, nowUtc, ct);

        return (anchors, loggedDates, repeatSent);
    }

    private async Task ProcessRepeatReminders(ReminderSnapshot snapshot, OrbitDbContext db,
        List<PendingReminderPush> pending, DateTime nowAtUtc, CancellationToken ct)
    {
        foreach (var habit in snapshot.RepeatHabits)
        {
            if (!snapshot.Users.TryGetValue(habit.UserId, out var user) || habit.RepeatReminder is null)
                continue;
            var zone = TimeZoneHelper.FindTimeZone(user.TimeZone, logger, user.Id);
            var plan = RepeatReminderPlanner.Next(habit, nowAtUtc, zone, user.WeekStartDay,
                snapshot.RepeatAnchors.TryGetValue(habit.Id, out var anchorAtUtc) ? anchorAtUtc : null,
                snapshot.RepeatLoggedDates.Where(l => l.HabitId == habit.Id).Select(l => l.Date).ToHashSet(),
                snapshot.RepeatSent.Where(r => r.HabitId == habit.Id).ToList());
            if (plan is null || plan.DispatchAtUtc > nowAtUtc)
                continue;
            var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(nowAtUtc, zone));
            var reminder = SentReminder.CreateRepeat(habit.Id, today, plan.ReminderAtUtc, plan.ExpiresAtUtc,
                plan.DoseAnchorAtUtc, plan.Parent?.Id);
            if (await RecordRepeat(habit, user, reminder, plan.Parent ?? reminder, zone, db, pending, ct))
                snapshot.RepeatSent.Add(reminder);
        }
    }

    private async Task<bool> RecordRepeat(Habit habit, User user, SentReminder reminder, SentReminder actionReminder,
        TimeZoneInfo zone, OrbitDbContext db, List<PendingReminderPush> pending, CancellationToken ct)
    {
        var localNext = TimeZoneInfo.ConvertTimeFromUtc(actionReminder.ExpiresAtUtc!.Value, zone);
        var format = TimeFormatResolver.Uses24HourClock(user.Uses24HourClockPreference, user.TimeZone) ? "HH:mm" : "h:mm tt";
        var time = localNext.ToString(format, CultureInfo.GetCultureInfo(LocaleHelper.IsPortuguese(user.Language) ? "pt-BR" : "en-US"));
        var body = LocaleHelper.IsPortuguese(user.Language) ? $"Próximo às {time}" : $"Next at {time}";
        var notification = Notification.Create(user.Id, habit.Title, body, NotificationUrls.Home, habit.Id);
        if (!await TryRecordReminderAsync(habit, reminder, notification, db, ct))
            return false;
        using var scope = scopeFactory.CreateScope();
        var tokens = scope.ServiceProvider.GetRequiredService<IReminderActionTokenService>();
        var reminderAtUtc = DateTime.SpecifyKind(actionReminder.ReminderAtUtc!.Value, DateTimeKind.Utc);
        var expiresAtUtc = DateTime.SpecifyKind(actionReminder.ExpiresAtUtc.Value, DateTimeKind.Utc);
        var token = tokens.Sign(new ReminderActionClaims(user.Id, habit.Id, actionReminder.Id, reminderAtUtc, expiresAtUtc));
        var data = new Dictionary<string, string>
        {
            ["habitId"] = habit.Id.ToString(),
            ["tag"] = "orbit-repeat-" + habit.Id.ToString("N"),
            ["reminderAtUtc"] = reminderAtUtc.ToString("O", CultureInfo.InvariantCulture),
            ["doneExpiresAtUtc"] = expiresAtUtc.ToString("O", CultureInfo.InvariantCulture),
            ["doneToken"] = token,
            ["doneLabel"] = LocaleHelper.IsPortuguese(user.Language) ? "Concluir" : "Done"
        };
        pending.Add(new PendingReminderPush(user.Id, habit.Title, body, habit.Id, data));
        return true;
    }
}
