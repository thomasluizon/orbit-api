using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orbit.Application.Notifications;
using Orbit.Application.Common;
using Orbit.Application.Habits.Services;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.BackgroundJobs;
using Orbit.Infrastructure.Persistence;
using Orbit.Infrastructure.Services.Hosting;

namespace Orbit.Infrastructure.Services;

public partial class ReminderSchedulerService(
    IServiceScopeFactory scopeFactory,
    ILogger<ReminderSchedulerService> logger,
    IConfiguration configuration,
    TimeProvider? timeProvider = null) : ScheduledServiceBase, IScheduledJob
{
    private const int MinRelativeDayOffset = -1;
    private const int MaxRelativeLookaheadDays = DomainConstants.MaxReminderMinutesBefore / 1440 + 1;
    private const string ReminderProbeSql = """
        SELECT p."UserId", p."Version" AS "ScheduleVersion",
               u."ReminderPreferencesVersion" AS "UserPreferencesVersion"
        FROM "ReminderScheduleProbes" AS p JOIN "Users" AS u ON u."Id" = p."UserId"
        WHERE u."IsDeactivated" = FALSE
        """;

    private readonly TimeSpan _interval = TimeSpan.FromMinutes(
        configuration.GetValue("BackgroundServices:ReminderIntervalMinutes", 1));

    private readonly SemaphoreSlim _tickGate = new(1, 1);
    private readonly Dictionary<Guid, ReminderSnapshot> _snapshots = [];
    private readonly List<ReminderWrite> _writes = [];

    public string Name => "reminder-scheduler";

    public string CronExpression => "* * * * *";

    public Task RunAsync(CancellationToken cancellationToken) => ExecuteTickAsync(cancellationToken);

    protected override TimeSpan Interval => _interval;

    protected override async Task ExecuteTickAsync(CancellationToken stoppingToken)
    {
        await CheckAndSendReminders(stoppingToken);
    }

    protected override void LogStarted() => LogServiceStarted(logger);

    protected override void LogStopped() => LogServiceStopped(logger);

    protected override void LogTickError(Exception ex) => LogServiceError(logger, ex);

    internal async Task CheckAndSendReminders(CancellationToken ct)
    {
        if (!await _tickGate.WaitAsync(0, ct)) return;
        var pending = new List<PendingReminderPush>();
        try
        {
            using var scope = scopeFactory.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<OrbitDbContext>();
            var nowUtc = (timeProvider ?? TimeProvider.System).GetUtcNow().UtcDateTime;
            var probes = await dbContext.Database.SqlQueryRaw<ReminderProbe>(ReminderProbeSql).ToListAsync(ct);
            var currentUsers = probes.Select(p => p.UserId).ToHashSet();
            foreach (var removed in _snapshots.Keys.Where(id => !currentUsers.Contains(id)).ToArray())
                _snapshots.Remove(removed);
            foreach (var probe in probes)
            {
                if (!_snapshots.TryGetValue(probe.UserId, out var snapshot) || nowUtc < snapshot.LoadedAtUtc
                    || nowUtc - snapshot.LoadedAtUtc >= TimeSpan.FromHours(1) || probe != snapshot.Probe)
                {
                    snapshot = await LoadSnapshotAsync(dbContext, probe, nowUtc, ct);
                    _snapshots[probe.UserId] = snapshot;
                }
                ProcessRelativeReminders(snapshot, pending, nowUtc, ct);
                ProcessScheduledReminders(snapshot, pending, nowUtc, ct);
                ProcessRepeatReminders(snapshot, pending, nowUtc, ct);
            }
            var accepted = await FlushRemindersAsync(dbContext, ct);
            pending.RemoveAll(p => !accepted.Contains(p.ReminderId));
            var cutoffAtUtc = nowUtc.AddMinutes(-15);
            var missing = await (from reminder in dbContext.SentReminders.AsNoTracking()
                                 join habit in dbContext.Habits on reminder.HabitId equals habit.Id
                                 join user in dbContext.Users on habit.UserId equals user.Id
                                 where reminder.PushSentAtUtc == null && reminder.PushUserId != null
                                     && reminder.SentAtUtc >= cutoffAtUtc
                                     && (reminder.ExpiresAtUtc == null || reminder.ExpiresAtUtc > nowUtc)
                                 select reminder).ToListAsync(ct);
            pending = missing.Select(row => new PendingReminderPush(row.PushUserId!.Value,
                row.PushTitle!, row.PushBody!, row.HabitId, row.Id,
                row.PushDataJson is null ? null : System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(row.PushDataJson))).ToList();
        }
        catch
        {
            _snapshots.Clear();
            throw;
        }
        finally
        {
            _writes.Clear();
            _tickGate.Release();
        }
        using var pushDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await SendPushesAsync(pending, pushDeadline.Token);
    }

    private async Task<ReminderSnapshot> LoadSnapshotAsync(
        OrbitDbContext dbContext, ReminderProbe probe, DateTime nowUtc, CancellationToken ct)
    {
        var minLocalDate = DateOnly.FromDateTime(nowUtc.AddDays(-1));
        var maxLocalDate = DateOnly.FromDateTime(nowUtc.AddDays(MaxRelativeLookaheadDays + 1));
        var (relativeHabits, scheduledHabits) = await LoadLegacyHabits(dbContext, probe.UserId, nowUtc, minLocalDate, maxLocalDate, ct);

        var repeatHabits = await LoadRepeatHabits(dbContext, probe.UserId, minLocalDate, ct);

        var users = await dbContext.Users
            .AsNoTracking()
            .Where(u => u.Id == probe.UserId)
            .ToDictionaryAsync(u => u.Id, ct);

        var (repeatAnchors, repeatEligibility, repeatSent) = await LoadRepeatSnapshot(
            dbContext, repeatHabits, users, nowUtc, minLocalDate, ct);

        var habitIds = relativeHabits.Select(h => h.Id).ToList();
        var minWindowDate = minLocalDate.AddDays(MinRelativeDayOffset);
        var maxWindowDate = maxLocalDate;

        var loggedHabitDates = (await dbContext.HabitLogs
            .AsNoTracking()
            .Where(l => habitIds.Contains(l.HabitId)
                && l.Date >= minWindowDate && l.Date <= maxWindowDate)
            .Select(l => new { l.HabitId, l.Date })
            .ToListAsync(ct))
            .Select(l => (l.HabitId, l.Date))
            .ToHashSet();

        var sentReminderKeys = await dbContext.SentReminders
            .AsNoTracking()
            .Where(r => habitIds.Contains(r.HabitId) && r.ReminderTimeUtc == null
                && r.Date >= minWindowDate && r.Date <= maxWindowDate)
            .Select(r => new { r.HabitId, r.Date, r.MinutesBefore })
            .ToListAsync(ct);
        var sentReminderSet = sentReminderKeys
            .Select(r => (r.HabitId, r.Date, r.MinutesBefore))
            .ToHashSet();

        var sentClockKeys = await dbContext.SentReminders
            .AsNoTracking()
            .Where(r => habitIds.Contains(r.HabitId) && r.ReminderTimeUtc != null
                && r.Date >= minWindowDate && r.Date <= maxWindowDate)
            .Select(r => new { r.HabitId, r.Date, r.ReminderTimeUtc, r.When })
            .ToListAsync(ct);
        var sentClockSet = sentClockKeys
            .Select(r => (r.HabitId, r.Date, Time: r.ReminderTimeUtc!.Value, r.When))
            .ToHashSet();

        var scheduledHabitIds = scheduledHabits.Select(h => h.Id).ToList();
        var utcToday = DateOnly.FromDateTime(nowUtc);
        var sentScheduledKeys = await dbContext.SentReminders
            .AsNoTracking()
            .Where(r => scheduledHabitIds.Contains(r.HabitId) && r.ReminderTimeUtc != null
                && (r.Date == utcToday || r.Date == utcToday.AddDays(-1) || r.Date == utcToday.AddDays(1)))
            .Select(r => new { r.HabitId, r.Date, r.ReminderTimeUtc, r.When })
            .ToListAsync(ct);
        var sentScheduledSet = sentScheduledKeys
            .Select(r => (r.HabitId, r.Date, ReminderTimeUtc: r.ReminderTimeUtc!.Value, r.When))
            .ToHashSet();

        return new ReminderSnapshot(nowUtc, probe, relativeHabits, scheduledHabits,
            users, loggedHabitDates, sentReminderSet, sentClockSet, sentScheduledSet, repeatHabits, repeatAnchors, repeatEligibility, repeatSent);
    }

    private void ProcessRelativeReminders(ReminderSnapshot snapshot,
        List<PendingReminderPush> pending, DateTime nowUtc, CancellationToken ct)
    {
        var sentByHabit = snapshot.SentReminderSet.ToLookup(r => r.HabitId);
        var clocksByHabit = snapshot.SentClockSet.ToLookup(r => r.HabitId);
        foreach (var habit in snapshot.RelativeHabits)
        {
            ProcessSingleRelativeReminder(
                habit, snapshot.Users, snapshot.LoggedHabitDates, snapshot.SentReminderSet,
                snapshot.SentClockSet, sentByHabit[habit.Id], clocksByHabit[habit.Id], pending, nowUtc, ct);
        }
    }

    private static async Task<(List<Habit> Relative, List<Habit> Scheduled)> LoadLegacyHabits(
        OrbitDbContext dbContext, Guid userId, DateTime nowUtc, DateOnly minLocalDate, DateOnly maxLocalDate, CancellationToken ct)
    {
        var relativeHabits = await dbContext.Habits
            .AsNoTracking()
            .Where(h => h.UserId == userId && !h.IsCompleted && h.Schedule != HabitSchedule.General && h.ReminderEnabled && h.RepeatIntervalMinutes == null && h.DueTime != null
                && h.NextDueDate <= maxLocalDate
                && (!h.EndDate.HasValue || h.EndDate.Value >= minLocalDate))
            .LoadReminderPageAsync(ct);
        var scheduledHabits = await dbContext.Habits
            .AsNoTracking()
            .Where(h => h.UserId == userId && !h.IsCompleted && h.Schedule != HabitSchedule.General && h.ReminderEnabled && h.RepeatIntervalMinutes == null && h.DueTime == null
                && h.NextDueDate <= DateOnly.FromDateTime(nowUtc.AddDays(2))
                && (!h.EndDate.HasValue || h.EndDate.Value >= minLocalDate))
            .LoadReminderPageAsync(ct);
        scheduledHabits = scheduledHabits.Where(h => h.ScheduledReminders.Count > 0).ToList();

        return (relativeHabits, scheduledHabits);
    }

    /// <summary>
    /// The UTC instant a local due time first happens. A repeated fall-back hour resolves to its
    /// first occurrence, and a time skipped by a spring-forward gap resolves to the moment the clock
    /// jumps past it, so a reminder is never an hour late and never silently dropped.
    /// </summary>
    private static DateTime LocalDueInstantUtc(DateTime dueLocal, TimeZoneInfo tz)
    {
        if (tz.IsAmbiguousTime(dueLocal))
            return DateTime.SpecifyKind(dueLocal - tz.GetAmbiguousTimeOffsets(dueLocal).Max(), DateTimeKind.Utc);
        if (tz.IsInvalidTime(dueLocal))
            return DateTime.SpecifyKind(dueLocal - tz.GetUtcOffset(dueLocal.AddDays(-1)), DateTimeKind.Utc);
        return TimeZoneInfo.ConvertTimeToUtc(dueLocal, tz);
    }

    private static DateTime LocalReminderInstantUtc(DateTime reminderLocal, TimeZoneInfo tz)
    {
        while (tz.IsInvalidTime(reminderLocal))
            reminderLocal = reminderLocal.AddMinutes(1);

        return LocalDueInstantUtc(reminderLocal, tz);
    }

    private void ProcessSingleRelativeReminder(
        Habit habit, Dictionary<Guid, User> users,
        HashSet<(Guid HabitId, DateOnly Date)> loggedHabitDates,
        HashSet<(Guid HabitId, DateOnly Date, int MinutesBefore)> sentReminderSet,
        HashSet<(Guid HabitId, DateOnly Date, TimeOnly Time, ScheduledReminderWhen? When)> sentClockSet,
        IEnumerable<(Guid HabitId, DateOnly Date, int MinutesBefore)> sentForHabit,
        IEnumerable<(Guid HabitId, DateOnly Date, TimeOnly Time, ScheduledReminderWhen? When)> clocksForHabit,
        List<PendingReminderPush> pending, DateTime nowUtc, CancellationToken ct)
    {
        if (!users.TryGetValue(habit.UserId, out var user)) return;

        var tz = TimeZoneHelper.FindTimeZone(user.TimeZone, logger, user.Id);
        var userNow = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, tz);
        var userToday = DateOnly.FromDateTime(userNow);
        if (HasEnded(habit, userToday))
            return;
        var sentInstants = sentForHabit
            .Select(r => LocalDueInstantUtc(r.Date.ToDateTime(habit.DueTime!.Value), tz).AddMinutes(-r.MinutesBefore))
            .Concat(clocksForHabit
                .Select(r => LocalReminderInstantUtc(r.Date.ToDateTime(r.Time), tz)))
            .ToHashSet();

        for (var dayOffset = MinRelativeDayOffset; dayOffset <= MaxRelativeLookaheadDays; dayOffset++)
        {
            var occurrenceDate = userToday.AddDays(dayOffset);
            if (!HabitScheduleService.IsHabitDueOnDate(habit, occurrenceDate, user.WeekStartDay)) continue;
            if (loggedHabitDates.Contains((habit.Id, occurrenceDate))) continue;

            var dueUtc = LocalDueInstantUtc(occurrenceDate.ToDateTime(habit.DueTime!.Value), tz);
            foreach (var minutesBefore in habit.ReminderTimes.Concat(
                habit.RelativeReminders.Where(r => r.MinutesBefore.HasValue).Select(r => r.MinutesBefore!.Value)).Distinct())
            {
                var reminderUtc = dueUtc.AddMinutes(-minutesBefore);
                var reminderLocalDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(reminderUtc, tz));
                if (reminderLocalDate != userToday || nowUtc < reminderUtc) continue;
                if (sentReminderSet.Contains((habit.Id, occurrenceDate, minutesBefore))
                    || sentInstants.Contains(reminderUtc)) continue;

                var lang = user.Language ?? "en";
                var minutesText = minutesBefore < 0
                    ? FormatScheduledReminderText(ScheduledReminderWhen.SameDay, lang)
                    : FormatReminderText(minutesBefore, lang);

                var sentReminder = SentReminder.Create(habit.Id, occurrenceDate, minutesBefore);
                var notification = Notification.Create(
                    habit.UserId, habit.Title, minutesText, NotificationUrls.Home, habit.Id);

                StageReminder(habit, sentReminder, notification, tz, nowUtc, ct);

                sentReminderSet.Add((habit.Id, occurrenceDate, minutesBefore));
                sentInstants.Add(reminderUtc);
                pending.Add(new PendingReminderPush(habit.UserId, habit.Title, minutesText, habit.Id, sentReminder.Id));

                if (logger.IsEnabled(LogLevel.Debug))
                    LogSentReminder(logger, minutesBefore, habit.Id, habit.UserId);
            }

            foreach (var reminder in habit.RelativeReminders.Where(r => r.When.HasValue)
                .Concat(habit.ScheduledReminders.Select(Domain.ValueObjects.RelativeReminderTime.FromScheduled)).Distinct())
            {
                var sendDate = reminder.When == ScheduledReminderWhen.DayBefore
                    ? occurrenceDate.AddDays(-1) : occurrenceDate;
                if (sendDate != userToday) continue;

                var sendUtc = LocalReminderInstantUtc(sendDate.ToDateTime(reminder.Time!.Value), tz);
                if (nowUtc < sendUtc) continue;

                var clockKey = (habit.Id, sendDate, reminder.Time.Value, reminder.When);
                if (sentInstants.Contains(sendUtc) || sentClockSet.Contains(clockKey)
                    || sentClockSet.Contains((habit.Id, sendDate, reminder.Time.Value, null))) continue;

                var lang = user.Language ?? "en";
                var body = FormatScheduledReminderText(reminder.When!.Value, lang);
                var sentReminder = SentReminder.Create(habit.Id, sendDate, 0, reminder.Time, reminder.When);
                var notification = Notification.Create(habit.UserId, habit.Title, body, NotificationUrls.Home, habit.Id);
                StageReminder(habit, sentReminder, notification, tz, nowUtc, ct);

                sentClockSet.Add(clockKey);
                sentInstants.Add(sendUtc);
                pending.Add(new PendingReminderPush(habit.UserId, habit.Title, body, habit.Id, sentReminder.Id));
            }
        }
    }

    private void ProcessScheduledReminders(ReminderSnapshot snapshot,
        List<PendingReminderPush> pending, DateTime nowUtc, CancellationToken ct)
    {
        foreach (var habit in snapshot.ScheduledHabits)
        {
            ProcessSingleScheduledReminder(
                habit, snapshot.Users, snapshot.SentScheduledSet, pending, nowUtc, ct);
        }
    }

    private void ProcessSingleScheduledReminder(
        Habit habit, Dictionary<Guid, User> users,
        HashSet<(Guid HabitId, DateOnly Date, TimeOnly ReminderTimeUtc, ScheduledReminderWhen? When)> sentScheduledSet,
        List<PendingReminderPush> pending, DateTime nowUtc, CancellationToken ct)
    {
        if (!users.TryGetValue(habit.UserId, out var user)) return;

        var tz = TimeZoneHelper.FindTimeZone(user.TimeZone, logger, user.Id);
        var userNow = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, tz);
        var userToday = DateOnly.FromDateTime(userNow);
        if (HasEnded(habit, userToday))
            return;
        var userTomorrow = userToday.AddDays(1);
        var userTimeNow = TimeOnly.FromDateTime(userNow);

        var isDueToday = HabitScheduleService.IsHabitDueOnDate(habit, userToday, user.WeekStartDay);
        var isDueTomorrow = HabitScheduleService.IsHabitDueOnDate(habit, userTomorrow, user.WeekStartDay);

        foreach (var sr in habit.ScheduledReminders)
        {
            if (!ShouldSendScheduledReminder(sr, isDueToday, isDueTomorrow, userTimeNow))
                continue;

            if (sentScheduledSet.Contains((habit.Id, userToday, sr.Time, sr.When))
                || sentScheduledSet.Contains((habit.Id, userToday, sr.Time, null))) continue;

            var lang = user.Language ?? "en";
            var text = FormatScheduledReminderText(sr.When, lang);

            var sentReminder = SentReminder.Create(habit.Id, userToday, 0, sr.Time, sr.When);
            var notification = Notification.Create(
                habit.UserId, habit.Title, text, NotificationUrls.Home, habit.Id);

            StageReminder(habit, sentReminder, notification, tz, nowUtc, ct);

            sentScheduledSet.Add((habit.Id, userToday, sr.Time, sr.When));
            pending.Add(new PendingReminderPush(habit.UserId, habit.Title, text, habit.Id, sentReminder.Id));

            if (logger.IsEnabled(LogLevel.Debug))
                LogSentScheduledReminder(logger, sr.When, sr.Time, habit.Id, habit.UserId);
        }
    }

    private static bool ShouldSendScheduledReminder(
        Domain.ValueObjects.ScheduledReminderTime sr, bool isDueToday, bool isDueTomorrow, TimeOnly userTimeNow)
    {
        if (sr.When == ScheduledReminderWhen.SameDay && !isDueToday) return false;
        if (sr.When == ScheduledReminderWhen.DayBefore && !isDueTomorrow) return false;
        if (sr.When != ScheduledReminderWhen.SameDay && sr.When != ScheduledReminderWhen.DayBefore) return false;

        return userTimeNow >= sr.Time;
    }

    private void StageReminder(Habit habit, SentReminder sentReminder, Notification notification,
        TimeZoneInfo zone, DateTime sentAtUtc, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (sentReminder.PushUserId is null)
            sentReminder.PreparePush(habit.UserId, notification.Title, notification.Body, recordedAtUtc: sentAtUtc);
        _writes.Add(new(habit, sentReminder, notification, zone, sentAtUtc));
    }

    private sealed record ReminderWrite(Habit Habit, SentReminder Reminder, Notification Notification, TimeZoneInfo Zone, DateTime SentAtUtc);

    private async Task<HashSet<Guid>> FlushRemindersAsync(OrbitDbContext db, CancellationToken ct)
    {
        if (_writes.Count == 0) return [];
        db.SentReminders.AddRange(_writes.Select(w => w.Reminder));
        foreach (var write in _writes.GroupBy(w => (w.Habit.Id,
            DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(w.SentAtUtc, w.Zone)))).Select(g => g.Last()))
            await RefreshInboxAsync(db, write, ct);
        try
        {
            await db.SaveChangesAsync(ct);
            return _writes.Select(w => w.Reminder.Id).ToHashSet();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            DetachPendingEntries(db);
            _snapshots.Clear();
        }
        var accepted = new HashSet<Guid>();
        foreach (var write in _writes)
        {
            db.SentReminders.Add(write.Reminder);
            try
            {
                await RefreshInboxAsync(db, write, ct);
                await db.SaveChangesAsync(ct);
                accepted.Add(write.Reminder.Id);
            }
            catch (DbUpdateException ex) when (DbUniqueViolation.IsUniqueViolation(ex))
            {
                DetachPendingEntries(db);
                if (logger.IsEnabled(LogLevel.Debug)) LogReminderAlreadySent(logger, write.Habit.Id);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                DetachPendingEntries(db);
                LogReminderRecordFailed(logger, write.Habit.Id, write.Habit.UserId, ex);
            }
        }
        return accepted;
    }

    private static Task RefreshInboxAsync(OrbitDbContext db, ReminderWrite write, CancellationToken ct) =>
        HabitReminderInbox.RefreshAsync(new GenericRepository<Notification>(db), write.Notification, write.Zone, write.SentAtUtc,
            write.Reminder.When == ScheduledReminderWhen.DayBefore ? write.Reminder.Date.AddDays(1) : write.Reminder.Date, ct);

    private async Task SendPushesAsync(IReadOnlyList<PendingReminderPush> pending, CancellationToken ct)
    {
        if (pending.Count == 0) return;
        using var scope = scopeFactory.CreateScope();
        using var progressGate = new SemaphoreSlim(1, 1);
        var db = scope.ServiceProvider.GetRequiredService<OrbitDbContext>();
        try
        {
            await scope.ServiceProvider.GetRequiredService<IPushNotificationService>().TrySendManyAsync(pending.Select(p =>
                new Orbit.Domain.Models.PushDelivery(p.UserId, p.Title, p.Body, NotificationUrls.Home, p.Data)).ToArray(), async index =>
                {
                    using var completionDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    await progressGate.WaitAsync(completionDeadline.Token);
                    try
                    {
                        var id = pending[index].ReminderId;
                        var sentAtUtc = (timeProvider ?? TimeProvider.System).GetUtcNow().UtcDateTime;
                        await db.SentReminders.Where(row => row.Id == id && row.PushSentAtUtc == null)
                            .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.PushSentAtUtc, sentAtUtc), completionDeadline.Token);
                    }
                    finally { progressGate.Release(); }
                }, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            LogReminderPushDeadline(logger, pending.Count);
        }
        catch (Exception ex)
        {
            LogReminderPushFailed(logger, pending[0].HabitId, pending[0].UserId, ex);
        }
    }

    private static void DetachPendingEntries(OrbitDbContext dbContext)
    {
        foreach (var entry in dbContext.ChangeTracker.Entries().ToList())
            entry.State = EntityState.Detached;
    }

    private readonly record struct PendingReminderPush(Guid UserId, string Title, string Body, Guid HabitId, Guid ReminderId,
        IReadOnlyDictionary<string, string>? Data = null);

    private sealed record ReminderProbe
    {
        public Guid UserId { get; init; }
        public long ScheduleVersion { get; init; }
        public int UserPreferencesVersion { get; init; }
    }

    private sealed record ReminderSnapshot(
        DateTime LoadedAtUtc,
        ReminderProbe Probe,
        List<Habit> RelativeHabits,
        List<Habit> ScheduledHabits,
        Dictionary<Guid, User> Users,
        HashSet<(Guid HabitId, DateOnly Date)> LoggedHabitDates,
        HashSet<(Guid HabitId, DateOnly Date, int MinutesBefore)> SentReminderSet,
        HashSet<(Guid HabitId, DateOnly Date, TimeOnly Time, ScheduledReminderWhen? When)> SentClockSet,
        HashSet<(Guid HabitId, DateOnly Date, TimeOnly ReminderTimeUtc, ScheduledReminderWhen? When)> SentScheduledSet,
        List<Habit> RepeatHabits,
        Dictionary<Guid, DateTime> RepeatAnchors,
        RepeatReminderEligibility RepeatEligibility,
        List<SentReminder> RepeatSent);

    private static bool HasEnded(Habit habit, DateOnly today) =>
        habit.IsDeleted || habit.IsCompleted || habit.EndDate is { } end && today > end;

    private static string Pluralize(string singular, int count) => count > 1 ? singular + "s" : singular;

    private static string FormatReminderText(int minutesBefore, string lang)
    {
        var isPt = LocaleHelper.IsPortuguese(lang);
        return minutesBefore switch
        {
            0 => isPt ? "Agora" : "Due now",
            < 60 => isPt
                ? $"Em {minutesBefore} {Pluralize("minuto", minutesBefore)}"
                : $"Due in {minutesBefore} {Pluralize("minute", minutesBefore)}",
            < 1440 => isPt
                ? $"Em {minutesBefore / 60} {Pluralize("hora", minutesBefore / 60)}"
                : $"Due in {minutesBefore / 60} {Pluralize("hour", minutesBefore / 60)}",
            _ => isPt
                ? $"Em {minutesBefore / 1440} {Pluralize("dia", minutesBefore / 1440)}"
                : $"Due in {minutesBefore / 1440} {Pluralize("day", minutesBefore / 1440)}"
        };
    }

    private static string FormatScheduledReminderText(ScheduledReminderWhen when, string lang)
    {
        var isPt = LocaleHelper.IsPortuguese(lang);
        return when switch
        {
            ScheduledReminderWhen.SameDay => isPt ? "Para hoje" : "Due today",
            ScheduledReminderWhen.DayBefore => isPt ? "Para amanhã" : "Due tomorrow",
            _ => isPt ? "Lembrete" : "Reminder"
        };
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "ReminderSchedulerService started")]
    private static partial void LogServiceStarted(ILogger logger);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "ReminderSchedulerService stopped")]
    private static partial void LogServiceStopped(ILogger logger);

    [LoggerMessage(EventId = 3, Level = LogLevel.Error, Message = "Error in reminder scheduler")]
    private static partial void LogServiceError(ILogger logger, Exception ex);

    [LoggerMessage(EventId = 4, Level = LogLevel.Debug, Message = "Sent reminder ({Minutes}min) for habit {HabitId} to user {UserId}")]
    private static partial void LogSentReminder(ILogger logger, int minutes, Guid habitId, Guid userId);

    [LoggerMessage(EventId = 5, Level = LogLevel.Debug, Message = "Sent scheduled reminder ({When} at {Time}) for habit {HabitId} to user {UserId}")]
    private static partial void LogSentScheduledReminder(ILogger logger, Orbit.Domain.Enums.ScheduledReminderWhen when, TimeOnly time, Guid habitId, Guid userId);

    [LoggerMessage(EventId = 6, Level = LogLevel.Debug, Message = "Reminder already recorded for habit {HabitId}; skipping push")]
    private static partial void LogReminderAlreadySent(ILogger logger, Guid habitId);

    [LoggerMessage(EventId = 7, Level = LogLevel.Error, Message = "Failed to record reminder for habit {HabitId} (user {UserId}); skipping push")]
    private static partial void LogReminderRecordFailed(ILogger logger, Guid habitId, Guid userId, Exception ex);

    [LoggerMessage(EventId = 8, Level = LogLevel.Warning, Message = "Failed to deliver reminder push for habit {HabitId} (user {UserId})")]
    private static partial void LogReminderPushFailed(ILogger logger, Guid habitId, Guid userId, Exception ex);

    [LoggerMessage(EventId = 9, Level = LogLevel.Warning, Message = "Reminder push batch reached its deadline with {ReminderCount} reminders; pending deliveries will retry")]
    private static partial void LogReminderPushDeadline(ILogger logger, int reminderCount);

}
