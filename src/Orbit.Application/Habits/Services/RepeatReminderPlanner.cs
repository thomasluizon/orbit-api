using Orbit.Application.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.ValueObjects;

namespace Orbit.Application.Habits.Services;

public sealed record RepeatReminderPlan(DateTime DispatchAtUtc, DateTime ReminderAtUtc, DateTime ExpiresAtUtc,
    DateTime DoseAnchorAtUtc, SentReminder? Parent);

public static class RepeatReminderPlanner
{
    /// <summary>
    /// Projects the first eligible scheduler opportunity within the shared schedule range limit.
    /// An unsent overdue occurrence fires at the next opportunity, retaining its original reminder instant.
    /// A dispatch cutoff keeps scheduler ticks from searching future opportunities.
    /// </summary>
    public static RepeatReminderPlan? Next(Habit habit, DateTime nowAtUtc, TimeZoneInfo zone, int weekStartDay,
        DateTime? doseAtUtc, RepeatReminderEligibility eligibility, IReadOnlyCollection<SentReminder> sentReminders,
        DateTime? dispatchThroughAtUtc = null)
    {
        if (EnabledOptions(habit) is not { } options
            || options.StartAtUtc is not { } startAtUtc)
            return null;
        var anchorAtUtc = DateTime.SpecifyKind(doseAtUtc ?? startAtUtc, DateTimeKind.Utc);
        if (RepeatReminderClock.Next(anchorAtUtc, options, zone) is not { } firstAtUtc)
            return null;
        var candidateAtUtc = nowAtUtc > firstAtUtc ? nowAtUtc : firstAtUtc;
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(nowAtUtc, zone));
        var lastDayNumber = Math.Min(DateOnly.MaxValue.DayNumber, today.DayNumber + AppConstants.MaxRangeDays);
        while (true)
        {
            if (RepeatReminderClock.MoveIntoWindow(candidateAtUtc, options, zone) is not { } windowAtUtc)
                return null;
            candidateAtUtc = windowAtUtc;
            if (dispatchThroughAtUtc is { } cutoffAtUtc && candidateAtUtc > cutoffAtUtc)
                return null;
            var date = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(candidateAtUtc, zone));
            if (date.DayNumber > lastDayNumber || habit.EndDate is { } end && date > end)
                return null;
            var start = habit.NextDueDate;
            if (date < start)
            {
                if (RepeatReminderClock.LocalInstant(start.ToDateTime(TimeOnly.MinValue), zone) is not { } startAt)
                    return null;
                candidateAtUtc = startAt;
                continue;
            }
            if (!eligibility.CanSend(habit, date, weekStartDay))
            {
                if (date == DateOnly.MaxValue || habit.Schedule == HabitSchedule.OneTime)
                    return null;
                if (RepeatReminderClock.LocalInstant(date.AddDays(1).ToDateTime(TimeOnly.MinValue), zone) is not { } tomorrowAtUtc)
                    return null;
                candidateAtUtc = tomorrowAtUtc;
                continue;
            }
            var dueAtUtc = RepeatReminderClock.LatestDue(firstAtUtc, candidateAtUtc, options, zone);
            var sent = sentReminders.FirstOrDefault(r => r.HabitId == habit.Id && r.ReminderAtUtc == dueAtUtc);
            if (RepeatReminderClock.Next(dueAtUtc, options, zone) is not { } nextAtUtc)
                return null;
            if (sent is null)
                return new(candidateAtUtc, dueAtUtc, nextAtUtc, anchorAtUtc, null);
            if (dueAtUtc.Ticks > DateTime.MaxValue.Ticks - 15 * TimeSpan.TicksPerMinute)
                return null;
            var followUpAtUtc = dueAtUtc.AddMinutes(15);
            if (CanFollowUp(sent, anchorAtUtc, followUpAtUtc, options, zone, sentReminders))
            {
                if (candidateAtUtc >= followUpAtUtc)
                    return new(candidateAtUtc, followUpAtUtc,
                        DateTime.SpecifyKind(sent.ExpiresAtUtc!.Value, DateTimeKind.Utc), anchorAtUtc, sent);
                candidateAtUtc = followUpAtUtc;
            }
            else
                candidateAtUtc = nextAtUtc;
        }
    }

    private static RepeatReminderOptions? EnabledOptions(Habit habit) =>
        habit.IsCompleted || habit.IsDeleted || !habit.ReminderEnabled ? null : habit.RepeatReminder;

    private static bool CanFollowUp(SentReminder sent, DateTime anchorAtUtc, DateTime followUpAtUtc,
        RepeatReminderOptions options, TimeZoneInfo zone, IReadOnlyCollection<SentReminder> sentReminders) =>
        options.IntervalMinutes >= 60 && sent.ParentReminderId is null && sent.ConsumedAtUtc is null
        && sent.DoseAnchorAtUtc == anchorAtUtc
        && RepeatReminderClock.MoveIntoWindow(followUpAtUtc, options, zone) == followUpAtUtc
        && !sentReminders.Any(r => r.ParentReminderId == sent.Id);
}
