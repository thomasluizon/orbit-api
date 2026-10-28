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
        var pendingIds = zones.Keys.ToList();
        var excludedIds = new List<Guid>();
        var anchors = new Dictionary<Guid, DateTime>();
        while (pendingIds.Count > 0)
        {
            var candidates = await LatestRepeatDoseCandidates(dbContext, pendingIds, excludedIds).ToListAsync(ct);
            pendingIds = [];
            foreach (var dose in candidates)
            {
                var loggedDate = dose.LoggedOnLocalDate ?? DateOnly.FromDateTime(
                    TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(dose.CreatedAtUtc, DateTimeKind.Utc), zones[dose.HabitId]));
                if (dose.Date == loggedDate)
                    anchors[dose.HabitId] = dose.TakenAtUtc ?? dose.CreatedAtUtc;
                else
                {
                    excludedIds.Add(dose.Id);
                    pendingIds.Add(dose.HabitId);
                }
            }
        }
        var recurringIds = repeatHabits.Where(h => h.Schedule == HabitSchedule.Recurring).Select(h => h.Id).ToList();
        var maxLocalDate = minLocalDate.AddDays(2);
        var loggedDates = (await dbContext.HabitLogs.AsNoTracking()
            .Where(l => recurringIds.Contains(l.HabitId) && l.Value > 0 && l.Date >= minLocalDate && l.Date <= maxLocalDate)
            .Select(l => new { l.HabitId, l.Date }).Distinct()
            .ToListAsync(ct)).Select(l => (l.HabitId, l.Date)).ToHashSet();
        var repeatSent = await dbContext.SentReminders.AsNoTracking()
            .Where(r => repeatIds.Contains(r.HabitId) && r.ReminderAtUtc >= nowUtc.AddDays(-3))
            .ToListAsync(ct);

        return (anchors, loggedDates, repeatSent);
    }

    internal static IQueryable<HabitLog> LatestRepeatDoseCandidates(OrbitDbContext dbContext, List<Guid> habitIds,
        List<Guid> excludedIds) => dbContext.HabitLogs.AsNoTracking()
        .Where(l => habitIds.Contains(l.HabitId) && l.Value > 0 && !excludedIds.Contains(l.Id)
            && (l.LoggedOnLocalDate == null || l.Date == l.LoggedOnLocalDate))
        .GroupBy(l => l.HabitId)
        .Select(g => g.OrderByDescending(l => l.TakenAtUtc ?? l.CreatedAtUtc).ThenByDescending(l => l.Id).First());

    private async Task ProcessRepeatReminders(ReminderSnapshot snapshot, OrbitDbContext db,
        List<PendingReminderPush> pending, DateTime nowAtUtc, CancellationToken ct)
    {
        foreach (var habit in snapshot.RepeatHabits)
        {
            if (!snapshot.Users.TryGetValue(habit.UserId, out var user) || habit.RepeatReminder is not { } options)
                continue;
            var zone = TimeZoneHelper.FindTimeZone(user.TimeZone, logger, user.Id);
            var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(nowAtUtc, zone));
            if (!CanSendRepeat(habit, today, user.WeekStartDay)
                || RepeatReminderClock.MoveIntoWindow(nowAtUtc, options, zone) != nowAtUtc)
                continue;
            if (habit.Schedule == HabitSchedule.Recurring && snapshot.RepeatLoggedDates.Contains((habit.Id, today)))
                continue;
            var anchorAtUtc = snapshot.RepeatAnchors.GetValueOrDefault(habit.Id, options.StartAtUtc!.Value);
            anchorAtUtc = DateTime.SpecifyKind(anchorAtUtc, DateTimeKind.Utc);
            if (anchorAtUtc > nowAtUtc)
                continue;
            var firstAtUtc = RepeatReminderClock.Next(anchorAtUtc, options, zone);
            if (firstAtUtc > nowAtUtc)
                continue;
            var dueAtUtc = RepeatReminderClock.LatestDue(firstAtUtc, nowAtUtc, options, zone);
            var sent = snapshot.RepeatSent.FirstOrDefault(r => r.HabitId == habit.Id && r.ReminderAtUtc == dueAtUtc);
            if (sent is null)
            {
                var nextAtUtc = RepeatReminderClock.Next(dueAtUtc, options, zone);
                var reminder = SentReminder.CreateRepeat(habit.Id, today, dueAtUtc, nextAtUtc, anchorAtUtc);
                if (await RecordRepeat(habit, user, reminder, reminder, zone, db, pending, ct))
                    snapshot.RepeatSent.Add(reminder);
            }
            else if (options.IntervalMinutes >= 60 && sent.ParentReminderId is null && sent.ConsumedAtUtc is null
                && sent.DoseAnchorAtUtc == anchorAtUtc && nowAtUtc >= dueAtUtc.AddMinutes(15)
                && RepeatReminderClock.MoveIntoWindow(dueAtUtc.AddMinutes(15), options, zone) == dueAtUtc.AddMinutes(15)
                && !snapshot.RepeatSent.Any(r => r.ParentReminderId == sent.Id))
            {
                var followUp = SentReminder.CreateRepeat(habit.Id, today, dueAtUtc.AddMinutes(15),
                    DateTime.SpecifyKind(sent.ExpiresAtUtc!.Value, DateTimeKind.Utc), anchorAtUtc, sent.Id);
                if (await RecordRepeat(habit, user, followUp, sent, zone, db, pending, ct))
                    snapshot.RepeatSent.Add(followUp);
            }
        }
    }

    private static bool CanSendRepeat(Habit habit, DateOnly date, int weekStartDay) =>
        !habit.IsCompleted && !habit.IsDeleted && (!habit.EndDate.HasValue || date <= habit.EndDate.Value)
        && (habit.Schedule == HabitSchedule.General
            ? date >= habit.StartDate : HabitScheduleService.IsHabitDueOnDate(habit, date, weekStartDay));

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
