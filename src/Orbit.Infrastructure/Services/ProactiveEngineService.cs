using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Orbit.Application.Calendar.Queries;
using Orbit.Application.Calendar.Services;
using Orbit.Application.Common;
using Orbit.Application.Goals.Services;
using Orbit.Application.Habits.Services;
using Orbit.Application.Notifications;
using Orbit.Application.Proactive.Services;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using Orbit.Infrastructure.Persistence;

namespace Orbit.Infrastructure.Services;

public sealed partial class ProactiveEngineService(
    OrbitDbContext db, TimeProvider clock, IProactiveCheckinMessageService messages,
    IPushNotificationService push, ICalendarEventFetcher calendar, IGoogleTokenService tokens,
    IProductAnalytics analytics, IUserDateService dates, IFeatureFlagService featureFlags,
    ILogger<ProactiveEngineService> logger) : IProactiveEngine
{
    internal sealed record Batch(List<User> Users, IReadOnlyDictionary<Guid, IReadOnlyList<string>> EnabledFlags, List<Habit> Habits,
        List<Goal> Goals, List<ProactiveItem> Items, List<ProactiveCalendarDay> CalendarDays)
    {
        public Dictionary<Guid, DateOnly> LocalDates { get; } = [];
    }

    public async Task EvaluateAsync(CancellationToken cancellationToken)
    {
        var nowUtc = clock.GetUtcNow().UtcDateTime;
        var batch = await LoadAsync(null, nowUtc, cancellationToken);
        foreach (var user in batch.Users)
        {
            try
            {
                await EvaluateUserAsync(batch, user, nowUtc, true, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                foreach (var entry in db.ChangeTracker.Entries().Where(e => e.State is EntityState.Added or EntityState.Modified).ToList())
                    entry.State = EntityState.Detached;
                UserFailed(logger, ex, user.Id);
            }
        }
    }

    internal async Task<Batch> LoadAsync(Guid? userId, DateTime nowUtc, CancellationToken ct)
    {
        var users = await db.Users.AsNoTracking().Where(u => (!userId.HasValue || u.Id == userId.Value)
            && (u.IsLifetimePro || u.BonusProExpiresAtUtc > nowUtc
                || (u.SubscriptionPlan == UserPlan.Pro && u.SubscriptionExpiresAtUtc > nowUtc) || u.TrialEndsAt > nowUtc))
            .ToListAsync(ct);
        if (users.Count == 0) return new(users, new Dictionary<Guid, IReadOnlyList<string>>(), [], [], [], []);
        var flags = await featureFlags.GetEnabledKeysForUsersAsync(users, ct);
        users = users.Where(user => !flags[user.Id].Contains(FeatureFlagKeys.AstraProactiveEngineDisabled)).ToList();
        if (users.Count == 0) return new(users, flags, [], [], [], []);
        var ids = users.Select(u => u.Id).ToList();
        var habits = await db.Habits.IgnoreQueryFilters().AsNoTracking()
            .Where(h => ids.Contains(h.UserId)).ToListAsync(ct);
        var habitIds = habits.Select(h => h.Id).ToList();
        var logs = await db.HabitLogs.AsNoTracking().Where(l => habitIds.Contains(l.HabitId)).ToListAsync(ct);
        var logsByHabit = logs.ToLookup(l => l.HabitId);
        foreach (var habit in habits) habit.LoadScheduleLogsForRead(logsByHabit[habit.Id]);
        var goals = await db.Goals.IgnoreQueryFilters().AsNoTracking().Include(g => g.Habits)
            .Where(g => ids.Contains(g.UserId)).ToListAsync(ct);
        var byId = habits.ToDictionary(h => h.Id);
        foreach (var goal in goals)
        {
            foreach (var linked in goal.Habits.ToList())
            {
                goal.RemoveHabit(linked);
                if (byId.TryGetValue(linked.Id, out var loaded)) goal.AddHabit(loaded);
            }
        }
        var localDates = new Dictionary<Guid, DateOnly>();
        foreach (var user in users)
            localDates[user.Id] = await dates.GetUserTodayAsync(user.TimeZone, user.Id, ct);
        var earliestLocalDay = localDates.Values.Min();
        var items = await db.ProactiveItems.Where(i => ids.Contains(i.UserId)).ToListAsync(ct);
        var calendarDays = await db.ProactiveCalendarDays.Where(d => ids.Contains(d.UserId)
            && d.LocalDate >= earliestLocalDay).ToListAsync(ct);
        var batch = new Batch(users, flags, habits, goals, items, calendarDays);
        foreach (var entry in localDates) batch.LocalDates.Add(entry.Key, entry.Value);
        return batch;
    }

    private DateTime LocalNow(User user, DateTime nowUtc) =>
        TimeZoneInfo.ConvertTimeFromUtc(nowUtc, TimeZoneHelper.FindTimeZone(user.TimeZone, logger, user.Id));

    private async Task<List<ProactiveFacts>> EvaluateUserAsync(Batch batch, User user, DateTime nowUtc, bool create, CancellationToken ct)
    {
        var localNow = LocalNow(user, nowUtc);
        var today = batch.LocalDates[user.Id];
        var items = batch.Items.Where(i => i.UserId == user.Id).ToList();
        var calendarDay = batch.CalendarDays.FirstOrDefault(d => d.UserId == user.Id && d.LocalDate == today);
        if (create && calendarDay is null && ProactiveRules.InWindow(ProactiveTrigger.BusyCalendarDay, localNow.Hour)
            && CalendarAllowed(batch, user))
            calendarDay = await ReadCalendarAsync(user, today, batch, ct);
        var facts = BuildFacts(batch, user, today, calendarDay);

        foreach (var item in items.Where(i => !i.EndedAtUtc.HasValue))
        {
            var reason = item.EvaluateEndReason(today, localNow.Hour, ConditionStillTrue(item, facts));
            if (reason.HasValue && item.End(reason.Value, nowUtc))
            {
                await SaveStateAsync(item, ct);
                CaptureEnded(user, item);
            }
        }
        if (create) await CreateItemsAsync(batch, user, nowUtc, today, localNow.Hour, facts, items, ct);
        return facts;
    }

    private async Task CreateItemsAsync(Batch batch, User user, DateTime nowUtc, DateOnly today,
        int localHour, List<ProactiveFacts> facts, List<ProactiveItem> items, CancellationToken ct)
    {
        var pushedToday = items.Any(i => i.LocalDate == today && i.PushedAtUtc.HasValue);
        var accountTitles = batch.Habits.Where(h => h.UserId == user.Id).Select(h => h.Title)
            .Concat(batch.Goals.Where(g => g.UserId == user.Id).Select(g => g.Title))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        foreach (var fact in facts.OrderBy(f => f.Trigger))
        {
            if (!ProactiveRules.InWindow(fact.Trigger, localHour)
                || items.Any(i => i.LocalDate == today && i.Trigger == fact.Trigger)
                || IsResting(items, fact.Trigger, nowUtc)) continue;
            if (fact.Trigger == ProactiveTrigger.GoalBehind && items.Any(i => i.Trigger == fact.Trigger
                && i.GoalId == fact.GoalId && i.LocalDate > today.AddDays(-ProactiveRules.GoalCooldownDays))) continue;

            using var aiUser = Orbit.Infrastructure.AI.AiCallContext.Begin(user.Id);
            var message = await messages.GenerateAsync(fact, user.Language ?? "en", accountTitles, ct);
            var shouldPush = !pushedToday && ProactiveRules.MayPush(fact.Trigger)
                && user.ProactiveAstraEnabled && !ProactiveRules.IsQuiet(localHour);
            var item = NewItem(user, today, nowUtc, fact, message);
            if (shouldPush) item.Push(nowUtc);
            var notification = shouldPush ? Notification.Create(user.Id, "Astra", item.Text, NotificationUrls.Chat) : null;
            db.ProactiveItems.Add(item);
            if (notification is not null) db.Notifications.Add(notification);
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException ex) when (DbUniqueViolation.IsUniqueViolation(ex))
            {
                db.Entry(item).State = EntityState.Detached;
                if (notification is not null) db.Entry(notification).State = EntityState.Detached;
                if (!shouldPush) continue;
                pushedToday = true;
                item = NewItem(user, today, nowUtc, fact, message);
                db.ProactiveItems.Add(item);
                try { await db.SaveChangesAsync(ct); }
                catch (DbUpdateException retry) when (DbUniqueViolation.IsUniqueViolation(retry))
                {
                    db.Entry(item).State = EntityState.Detached;
                    continue;
                }
                shouldPush = false;
            }
            batch.Items.Add(item);
            items.Add(item);
            Capture(user, "astra_proactive_item_created", new Dictionary<string, object>
            {
                ["trigger"] = item.Trigger.ToString(),
                ["pushed"] = shouldPush
            });
            if (shouldPush)
            {
                pushedToday = true;
                await push.SendToUserAsync(user.Id, "Astra", item.Text, NotificationUrls.Chat, ct);
            }
        }
    }

    private static ProactiveItem NewItem(User user, DateOnly today, DateTime nowUtc, ProactiveFacts fact, ProactiveMessage message) =>
        ProactiveItem.Create(user.Id, fact.Trigger, today, JsonSerializer.Serialize(fact), message.Text,
            message.ActionLabel, message.ActionPrompt, nowUtc, fact.GoalId);

    internal static bool IsResting(IEnumerable<ProactiveItem> items, ProactiveTrigger trigger, DateTime nowUtc)
    {
        var last = items.Where(i => i.Trigger == trigger && i.EndedAtUtc.HasValue)
            .OrderByDescending(i => i.EndedAtUtc).ThenByDescending(i => i.CreatedAtUtc)
            .Take(ProactiveRules.IgnoredItemsBeforeRest).ToList();
        return last.Count == ProactiveRules.IgnoredItemsBeforeRest && last.All(i => i.Ignored)
            && nowUtc < last[0].EndedAtUtc!.Value.AddDays(ProactiveRules.RestDays);
    }

    private static bool ConditionStillTrue(ProactiveItem item, List<ProactiveFacts> facts) =>
        facts.Any(f => f.Trigger == item.Trigger && (item.Trigger != ProactiveTrigger.GoalBehind || f.GoalId == item.GoalId));

    internal List<ProactiveFacts> BuildFacts(Batch batch, User user, DateOnly today, ProactiveCalendarDay? calendarDay)
    {
        var result = new List<ProactiveFacts>();
        var allHabits = batch.Habits.Where(h => h.UserId == user.Id && h.Schedule != HabitSchedule.General).ToList();
        var habits = allHabits.Where(h => !h.IsBadHabit && !h.IsDeleted && !h.IsCompleted).ToList();
        var scheduled = ScheduledForAction(habits, today, user.WeekStartDay);
        var due = Leaves(scheduled);
        var scheduledIds = scheduled.Select(h => h.Id).ToHashSet();
        var actionable = habits.Where(h => scheduledIds.Contains(h.Id)
            || HabitScheduleService.IsOverdueOnDate(h, today, user.WeekStartDay)).ToList();
        var open = Leaves(actionable).Where(h => !h.Logs.Any(l => l.Date == today && l.Value > 0)).ToList();
        var yesterday = today.AddDays(-1);
        var timeZone = TimeZoneHelper.FindTimeZone(user.TimeZone, logger, user.Id);
        var hadScheduled = allHabits.Where(h => !h.IsBadHabit).Any(h => HabitScheduleService.GetHistoricalScheduledDates(
            h, yesterday, yesterday, timeZone, user.WeekStartDay).Count > 0);
        var allLogs = batch.Habits.Where(h => h.UserId == user.Id && !h.IsBadHabit)
            .SelectMany(h => h.Logs).Where(l => l.IsSlip != true).ToList();
        var hadPositiveLogsYesterday = allLogs.Any(l => l.Date == yesterday && l.Value > 0);
        var hadRecentActivity = allLogs.Any(l => l.Value > 0 && l.Date < yesterday
            && l.Date >= yesterday.AddDays(-ProactiveRules.RestartLookbackDays));
        if (hadScheduled && !hadPositiveLogsYesterday && hadRecentActivity)
            result.Add(new(ProactiveTrigger.RestartAfterMiss, [], HadScheduledHabitsYesterday: hadScheduled,
                HadPositiveLogsYesterday: hadPositiveLogsYesterday, HadRecentActivity: hadRecentActivity));
        if (open.Count > 0)
            result.Add(new(ProactiveTrigger.EveningCheckin, open.Select(h => h.Title).ToList(), open.Count,
                user.CurrentStreak, user.CurrentStreak >= ProactiveRules.StreakMentionMinimum));
        if (CalendarAllowed(batch, user) && calendarDay?.TimedEventCount is { } count
            && (count >= ProactiveRules.BusyEventMinimum || calendarDay.TimedMinutes >= ProactiveRules.BusyMinutesMinimum))
            result.Add(new(ProactiveTrigger.BusyCalendarDay, [], TimedEventCount: count, TimedMinutes: calendarDay.TimedMinutes));
        result.AddRange(GoalFacts(batch, user, today));
        var review = WeeklyFacts(allHabits, user, today, timeZone);
        if (review is not null) result.Add(review);
        if (due.Count > 0)
            result.Add(new(ProactiveTrigger.MorningPlan, due.Select(h => h.Title).ToList(), due.Count));
        return result;
    }

    private static IEnumerable<ProactiveFacts> GoalFacts(Batch batch, User user, DateOnly today)
    {
        foreach (var goal in batch.Goals.Where(g => g.UserId == user.Id && !g.IsDeleted && g.Status == GoalStatus.Active)
            .OrderBy(g => g.Deadline).ThenBy(g => g.Id))
        {
            GoalProgressSyncService.ApplyReadValue(goal, today, user.WeekStartDay);
            if (goal.CurrentValue >= goal.TargetValue) continue;
            var metrics = GoalMetricsCalculator.Calculate(goal, today, user.WeekStartDay);
            if (metrics.TrackingStatus is "behind" or "at_risk")
                yield return new(ProactiveTrigger.GoalBehind, [], GoalId: goal.Id, GoalTitle: goal.Title,
                    TrackingStatus: metrics.TrackingStatus);
        }
    }

    private static List<Habit> ScheduledForAction(List<Habit> habits, DateOnly today, int weekStartDay) =>
        habits.Where(h => IsDueForAction(h, today, weekStartDay)
            || h.Logs.Any(l => l.Date == today && l.Value > 0)).ToList();

    private static List<Habit> Leaves(List<Habit> habits)
    {
        var parents = habits.Where(h => h.ParentHabitId.HasValue).Select(h => h.ParentHabitId!.Value).ToHashSet();
        return habits.Where(h => !parents.Contains(h.Id)).ToList();
    }

    private static bool IsDueForAction(Habit habit, DateOnly date, int weekStartDay) =>
        HabitScheduleService.IsHabitDueOnDate(habit, date, weekStartDay)
        && (habit.Schedule != HabitSchedule.Flexible || HabitScheduleService.IsFlexibleHabitDueOnDate(habit, date, habit.Logs, weekStartDay));

    private static ProactiveFacts? WeeklyFacts(List<Habit> allHabits, User user, DateOnly today, TimeZoneInfo timeZone)
    {
        var yesterday = today.AddDays(-1);
        if ((int)today.DayOfWeek != user.WeekStartDay) return null;
        var from = today.AddDays(-ProactiveRules.WeekDays);
        var scheduledByDate = allHabits.Where(h => !h.IsBadHabit)
            .SelectMany(h => HabitScheduleService.GetHistoricalScheduledDates(h, from, yesterday, timeZone, user.WeekStartDay)
                .Select(date => (Date: date, Habit: h)))
            .ToLookup(x => x.Date, x => x.Habit);
        var scheduled = 0;
        var completions = 0;
        for (var date = from; date <= yesterday; date = date.AddDays(1))
        {
            var leaves = Leaves(scheduledByDate[date].ToList());
            scheduled += leaves.Count;
            completions += leaves.Sum(h => h.Logs.Count(l => l.Date == date && l.Value > 0));
        }
        var slips = allHabits.Where(h => h.IsBadHabit).SelectMany(h => h.Logs)
            .Count(l => l.Value > 0 && l.Date >= from && l.Date <= yesterday);
        if (scheduled > 0)
            return new(ProactiveTrigger.WeeklyReview, [], PeriodFrom: from, PeriodTo: yesterday,
                TotalCompletions: completions, TotalScheduled: scheduled, BadHabitSlips: slips);
        return null;
    }

    private static bool CalendarAllowed(Batch batch, User user) =>
        (user.GoogleRefreshToken is not null || user.GoogleAccessToken is not null)
        && batch.EnabledFlags[user.Id].Contains(FeatureFlagKeys.CalendarIntegration);

    private async Task<ProactiveCalendarDay?> ReadCalendarAsync(User user, DateOnly today, Batch batch, CancellationToken ct)
    {
        var day = ProactiveCalendarDay.Claim(user.Id, today);
        db.ProactiveCalendarDays.Add(day);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (DbUniqueViolation.IsUniqueViolation(ex))
        {
            db.Entry(day).State = EntityState.Detached;
            return null;
        }
        batch.CalendarDays.Add(day);
        try
        {
            var token = await ReadCalendarTokenAsync(user.Id, ct);
            if (token is null) return day;
            var events = await calendar.FetchAsync(token, user.GetSelectedCalendarIds(), null, ct);
            var zone = TimeZoneHelper.FindTimeZone(user.TimeZone, logger, user.Id);
            var ranges = TimedRanges(events, today, zone);
            day.Complete(ranges.Count, (int)ranges.Sum(r => (r.End - r.Start).TotalMinutes));
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            CalendarSkipped(logger, user.Id);
        }
        return day;
    }

    private Task<string?> ReadCalendarTokenAsync(Guid userId, CancellationToken ct) =>
        db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var transaction = db.Database.IsRelational() && db.Database.CurrentTransaction is null
                ? await db.Database.BeginTransactionAsync(ct) : null;
            await UserRowLock.AcquireAsync(db, userId, ct);
            var tokenUser = await db.Users.SingleOrDefaultAsync(user => user.Id == userId, ct);
            if (tokenUser is null) return null;
            try
            {
                await db.Entry(tokenUser).ReloadAsync(ct);
                var token = tokenUser.GoogleRefreshToken is null
                    ? await tokens.GetValidAccessTokenAsync(tokenUser, ct)
                    : (await tokens.TryRefreshAsync(tokenUser, ct)).AccessToken;
                await db.SaveChangesAsync(ct);
                if (transaction is not null) await transaction.CommitAsync(ct);
                return token;
            }
            catch
            {
                var entry = db.Entry(tokenUser);
                entry.CurrentValues.SetValues(entry.OriginalValues);
                entry.State = EntityState.Unchanged;
                throw;
            }
        });

    internal static List<(DateTime Start, DateTime End)> TimedRanges(List<CalendarEventItem> events, DateOnly today, TimeZoneInfo zone)
    {
        var dayStart = StartOfDayUtc(today, zone);
        var dayEnd = StartOfDayUtc(today.AddDays(1), zone);
        var ranges = new List<(DateTime, DateTime)>();
        foreach (var item in events.Where(e => e.StartTime is not null && e.StartUtc.HasValue))
        {
            var starts = item.IsRecurring && item.ExpandedOccurrencesUtc is { Count: > 0 }
                ? item.ExpandedOccurrencesUtc : new[] { item.StartUtc!.Value };
            var duration = item.EndUtc - item.StartUtc;
            foreach (var startUtc in starts)
            {
                var start = DateTime.SpecifyKind(startUtc, DateTimeKind.Utc);
                var end = duration is { } length && length > TimeSpan.Zero ? start + length : start;
                if (start >= dayEnd || end < dayStart || (end == dayStart && start < dayStart)) continue;
                ranges.Add((start < dayStart ? dayStart : start, end > dayEnd ? dayEnd : end));
            }
        }
        return ranges;
    }

    private static DateTime StartOfDayUtc(DateOnly date, TimeZoneInfo zone)
    {
        var local = date.ToDateTime(TimeOnly.MinValue);
        while (zone.IsInvalidTime(local)) local = local.AddMinutes(1);
        if (zone.IsAmbiguousTime(local))
            return DateTime.SpecifyKind(local - zone.GetAmbiguousTimeOffsets(local).Max(), DateTimeKind.Utc);
        return TimeZoneInfo.ConvertTimeToUtc(local, zone);
    }

    public async Task<ProactiveItem?> GetCurrentAsync(Guid userId, CancellationToken cancellationToken)
    {
        var nowUtc = clock.GetUtcNow().UtcDateTime;
        var batch = await LoadAsync(userId, nowUtc, cancellationToken);
        var user = batch.Users.SingleOrDefault();
        if (user is null) return null;
        var facts = await EvaluateUserAsync(batch, user, nowUtc, false, cancellationToken);
        var today = batch.LocalDates[user.Id];
        var current = batch.Items.Where(i => i.UserId == userId && i.LocalDate == today && !i.EndedAtUtc.HasValue)
            .OrderBy(i => i.Trigger).ThenBy(i => i.CreatedAtUtc).FirstOrDefault();
        if (current is not null)
        {
            current.Show(nowUtc);
            await SaveStateAsync(current, cancellationToken);
            if (current.EndedAtUtc.HasValue) return null;
            var fact = facts.Single(f => f.Trigger == current.Trigger && f.GoalId == current.GoalId);
            if (current.FactsJson != JsonSerializer.Serialize(fact))
                return current.WithCurrentContent(fact, AiProactiveCheckinMessageService.Template(fact, user.Language ?? "en"));
        }
        return current;
    }

    public async Task<bool?> IsItemActiveAsync(Guid userId, Guid itemId, CancellationToken cancellationToken)
    {
        var item = await db.ProactiveItems.AsNoTracking()
            .SingleOrDefaultAsync(i => i.Id == itemId && i.UserId == userId, cancellationToken);
        if (item is null) return null;
        if (item.EndedAtUtc.HasValue || item.ActedAtUtc.HasValue) return false;
        var nowUtc = clock.GetUtcNow().UtcDateTime;
        var batch = await LoadAsync(userId, nowUtc, cancellationToken);
        var user = batch.Users.SingleOrDefault();
        if (user is null) return false;
        var today = batch.LocalDates[user.Id];
        var calendarDay = batch.CalendarDays.FirstOrDefault(d => d.UserId == user.Id && d.LocalDate == today);
        var facts = BuildFacts(batch, user, today, calendarDay);
        return item.EvaluateEndReason(today, LocalNow(user, nowUtc).Hour, ConditionStillTrue(item, facts)) is null;
    }

    public async Task<Result> RecordOutcomeAsync(Guid userId, Guid itemId, bool acted, CancellationToken cancellationToken)
    {
        var item = await db.ProactiveItems.SingleOrDefaultAsync(i => i.Id == itemId && i.UserId == userId, cancellationToken);
        if (item is null) return Result.Failure(ErrorMessages.ProactiveItemNotFound);
        var nowUtc = clock.GetUtcNow().UtcDateTime;
        var ended = false;
        if (acted) ended = item.Act(nowUtc);
        else item.Open(nowUtc);
        await SaveStateAsync(item, cancellationToken);
        if (ended)
        {
            var user = await db.Users.SingleOrDefaultAsync(u => u.Id == userId, cancellationToken);
            if (user is not null) CaptureEnded(user, item);
        }
        return Result.Success();
    }

    private async Task SaveStateAsync(ProactiveItem item, CancellationToken ct)
    {
        var shown = item.ShownAtUtc;
        var opened = item.OpenedAtUtc;
        var acted = item.ActedAtUtc;
        var ended = item.EndedAtUtc;
        var reason = item.EndReason;
        while (true)
        {
            try { await db.SaveChangesAsync(ct); return; }
            catch (DbUpdateConcurrencyException)
            {
                await db.Entry(item).ReloadAsync(ct);
                if (db.Entry(item).State == EntityState.Detached) return;
                if (shown.HasValue) item.Show(shown.Value);
                if (opened.HasValue) item.Open(opened.Value);
                if (acted.HasValue) item.Act(acted.Value);
                else if (ended.HasValue && reason.HasValue) item.End(reason.Value, ended.Value);
            }
        }
    }

    private void CaptureEnded(User user, ProactiveItem item)
    {
        var properties = new Dictionary<string, object>
        {
            ["trigger"] = item.Trigger.ToString(),
            ["outcome"] = item.ActedAtUtc.HasValue ? "acted" : item.OpenedAtUtc.HasValue ? "opened" : "ignored"
        };
        if (item.ShownAtUtc.HasValue && item.OpenedAtUtc.HasValue)
            properties["minutes_from_shown_to_opened"] = Math.Max(0, (item.OpenedAtUtc.Value - item.ShownAtUtc.Value).TotalMinutes);
        Capture(user, "astra_proactive_item_ended", properties);
    }

    private void Capture(User user, string name, Dictionary<string, object> properties)
    {
        try { analytics.CaptureUserEvent(user.Id, name, user.Plan.ToString(), properties); }
        catch (Exception ex) { AnalyticsFailed(logger, ex); }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Proactive evaluation failed for user {UserId}")]
    private static partial void UserFailed(ILogger logger, Exception exception, Guid userId);
    [LoggerMessage(Level = LogLevel.Debug, Message = "Calendar read skipped for proactive user {UserId}")]
    private static partial void CalendarSkipped(ILogger logger, Guid userId);
    [LoggerMessage(Level = LogLevel.Warning, Message = "Proactive analytics capture failed")]
    private static partial void AnalyticsFailed(ILogger logger, Exception exception);
}
