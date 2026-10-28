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
using Orbit.Infrastructure.Persistence;

namespace Orbit.Infrastructure.Services;

public partial class ReminderSchedulerService
{
    private static async Task<(List<Habit> Habits, List<RepeatDose> Doses, List<SentReminder> Sent)> LoadRepeatSnapshot(
        OrbitDbContext dbContext, DateTime nowUtc, DateOnly minLocalDate, CancellationToken ct)
    {
        var repeatHabits = await dbContext.Habits.AsNoTracking()
            .Where(h => !h.IsCompleted && h.ReminderEnabled && h.RepeatIntervalMinutes != null
                && (!h.EndDate.HasValue || h.EndDate.Value >= minLocalDate))
            .ToListAsync(ct);
        var repeatIds = repeatHabits.Select(h => h.Id).ToList();
        var repeatDoses = await dbContext.HabitLogs.AsNoTracking()
            .Where(l => repeatIds.Contains(l.HabitId) && l.Value > 0)
            .Select(l => new RepeatDose(l.HabitId, l.Date, l.TakenAtUtc ?? l.CreatedAtUtc, l.CreatedAtUtc))
            .ToListAsync(ct);
        var repeatSent = await dbContext.SentReminders.AsNoTracking()
            .Where(r => repeatIds.Contains(r.HabitId) && r.ReminderAtUtc >= nowUtc.AddDays(-3))
            .ToListAsync(ct);

        return (repeatHabits, repeatDoses, repeatSent);
    }

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
            var doses = snapshot.RepeatDoses.Where(d => d.HabitId == habit.Id).ToList();
            if (habit.Schedule == HabitSchedule.Recurring && doses.Any(d => d.Date == today))
                continue;
            var anchorAtUtc = doses.Where(d => d.Date == DateOnly.FromDateTime(
                    TimeZoneInfo.ConvertTimeFromUtc(d.CreatedAtUtc, zone)))
                .Select(d => (DateTime?)d.TakenAtUtc).Max() ?? options.StartAtUtc!.Value;
            anchorAtUtc = DateTime.SpecifyKind(anchorAtUtc, DateTimeKind.Utc);
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
        pending.Add(new PendingReminderPush(user.Id, habit.Title, body, habit.Id));
        return true;
    }
}
