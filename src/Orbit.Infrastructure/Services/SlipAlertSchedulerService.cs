using System.Collections.Concurrent;
using Orbit.Application.Chat;
using Orbit.Domain.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orbit.Application.Notifications;
using Orbit.Application.Common;
using Orbit.Application.Habits.Services;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.BackgroundJobs;
using Orbit.Infrastructure.Persistence;
using Orbit.Infrastructure.Services.Hosting;

namespace Orbit.Infrastructure.Services;

public partial class SlipAlertSchedulerService(
    IServiceScopeFactory scopeFactory,
    ILogger<SlipAlertSchedulerService> logger,
    IConfiguration configuration,
    TimeProvider? timeProvider = null) : ScheduledServiceBase, IScheduledJob
{
    private const int DefaultMorningHour = 8;
    private readonly SemaphoreSlim _tickGate = new(1, 1);
    private SlipSnapshot? _snapshot;
    private TimeProvider Clock => timeProvider ?? TimeProvider.System;
    private const int MaxTimeZoneSkewDays = 1;

    private readonly int _concurrency = Math.Clamp(configuration.GetValue("BackgroundServices:SlipAlertConcurrency", 4), 1, 8);
    private readonly TimeSpan _windowMargin = TimeSpan.FromMinutes(Math.Max(0,
        configuration.GetValue("BackgroundServices:SlipAlertWindowMarginMinutes", 2)));

    private readonly TimeSpan _interval = TimeSpan.FromMinutes(
        configuration.GetValue("BackgroundServices:SlipAlertIntervalMinutes", 5));

    public string Name => "slip-alert-scheduler";

    public string CronExpression => "*/5 * * * *";

    public Task RunAsync(CancellationToken cancellationToken) => ExecuteTickAsync(cancellationToken);

    protected override TimeSpan Interval => _interval;

    protected override async Task ExecuteTickAsync(CancellationToken stoppingToken)
    {
        await CheckAndSendAlerts(stoppingToken);
        BackgroundServiceHealthCheck.RecordTick("SlipAlertScheduler");
    }

    protected override void LogStarted() => LogServiceStarted(logger);

    protected override void LogStopped() => LogServiceStopped(logger);

    protected override void LogTickError(Exception ex) => LogServiceError(logger, ex);

    internal async Task CheckAndSendAlerts(CancellationToken ct)
    {
        await _tickGate.WaitAsync(ct);
        try { await CheckAndSendCore(ct); }
        finally { _tickGate.Release(); }
    }

    private async Task CheckAndSendCore(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrbitDbContext>();
        var nowAtUtc = Clock.GetUtcNow().UtcDateTime;
#pragma warning disable ORBIT0004
        var utcDate = DateOnly.FromDateTime(nowAtUtc);
#pragma warning restore ORBIT0004
        var probe = await ReadProbeAsync(db, utcDate, ct);
        if (_snapshot is null || _snapshot.UtcDate != utcDate || _snapshot.Probe != probe)
            _snapshot = await LoadSnapshotAsync(db, utcDate, probe, ct);
        var snapshot = _snapshot;
        var pending = new List<PendingSlip>();
        foreach (var habits in snapshot.Habits.Chunk(256))
        {
            var due = new List<SlipCandidate>();
            foreach (var habit in habits)
            {
                if (!snapshot.Users.TryGetValue(habit.UserId, out var user)) continue;
                var candidate = FindDue(habit, user, snapshot.LogsByHabit.GetValueOrDefault(habit.Id) ?? [],
                    snapshot.SentWeeksByHabit.GetValueOrDefault(habit.Id) ?? [], nowAtUtc);
                if (candidate is not null) due.Add(candidate);
            }
            var generated = new ConcurrentBag<PendingSlip>();
            await Parallel.ForEachAsync(due,
                new ParallelOptions { MaxDegreeOfParallelism = _concurrency, CancellationToken = ct }, async (candidate, token) =>
            {
                try
                {
                    using var aiScope = scopeFactory.CreateScope();
                    using var aiUser = Orbit.Infrastructure.AI.AiCallContext.Begin(candidate.User.Id);
                    var message = await aiScope.ServiceProvider.GetRequiredService<ISlipAlertMessageService>()
                        .GenerateMessageAsync(candidate.Habit.Title, candidate.Pattern.DayOfWeek, candidate.Pattern.PeakHour,
                            candidate.User.Language ?? "en", token);
                    if (message.IsFailure)
                    {
                        if (logger.IsEnabled(LogLevel.Warning)) LogSlipAlertMessageFailed(logger, candidate.Habit.Id);
                        return;
                    }
                    generated.Add(new(candidate.Habit, candidate.WeekStart, message.Value.Title, message.Value.Body));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    LogSlipAlertProcessingFailed(logger, candidate.Habit.Id, candidate.User.Id, ex);
                }
            });
            pending.AddRange(generated);
        }
        var accepted = await SaveClaimsAsync(db, pending, ct);
        foreach (var item in accepted)
        {
            if (!snapshot.SentWeeksByHabit.TryGetValue(item.Habit.Id, out var weeks))
                snapshot.SentWeeksByHabit[item.Habit.Id] = weeks = [];
            weeks.Add(item.WeekStart);
        }
        if (accepted.Count > 0)
            await scope.ServiceProvider.GetRequiredService<IPushNotificationService>().SendManyAsync(accepted.Select(p =>
                new PushDelivery(p.Habit.UserId, p.Title, p.Body, NotificationUrls.Home)).ToArray(), ct);
    }

    private sealed record SlipSnapshot(DateOnly UtcDate, string Probe, IReadOnlyList<Habit> Habits,
        Dictionary<Guid, SlipUser> Users, Dictionary<Guid, List<HabitLog>> LogsByHabit,
        Dictionary<Guid, HashSet<DateOnly>> SentWeeksByHabit);

    private sealed record SlipUser(Guid Id, string? TimeZone, int WeekStartDay, string? Language);

    private static async Task<string> ReadProbeAsync(OrbitDbContext db, DateOnly utcDate, CancellationToken ct)
    {
        var floor = utcDate.AddDays(-MaxTimeZoneSkewDays);
        var cutoff = utcDate.AddDays(-60);
        if (!db.Database.IsRelational())
        {
            var habitVersions = await db.Habits.Where(h => h.IsBadHabit && h.SlipAlertEnabled && !h.IsCompleted)
                .OrderBy(h => h.Id).Select(h => new { h.Id, h.ReminderProbeVersion }).ToListAsync(ct);
            var userVersions = await db.Users.OrderBy(u => u.Id).Select(u => new { u.Id, u.ReminderPreferencesVersion }).ToListAsync(ct);
            var logVersions = await db.HabitLogs.Where(l => l.Date >= cutoff).OrderBy(l => l.Id)
                .Select(l => new { l.Id, l.UpdatedAtUtc }).ToListAsync(ct);
            return ReadRevision.Hash(new { habitVersions, userVersions, logVersions });
        }
        var rows = await db.Database.SqlQuery<SlipProbe>($"""
            WITH eligible AS MATERIALIZED (
                SELECT "Id", "UserId", "ReminderProbeVersion" FROM "Habits"
                WHERE NOT "IsDeleted" AND NOT "IsCompleted" AND "IsBadHabit" AND "SlipAlertEnabled"
                    AND ("EndDate" IS NULL OR "EndDate" >= {floor})
            )
            SELECT md5(COALESCE((SELECT string_agg(CAST("Id" AS text) || ':' || CAST("ReminderProbeVersion" AS text), ',' ORDER BY "Id") FROM eligible), '')
                || COALESCE((SELECT string_agg(CAST(u."Id" AS text) || ':' || CAST(u."ReminderPreferencesVersion" AS text), ',' ORDER BY u."Id")
                    FROM "Users" u WHERE NOT u."IsDeactivated" AND u."Id" IN (SELECT "UserId" FROM eligible)), '')
                || COALESCE((SELECT string_agg(CAST(l."Id" AS text) || ':' || CAST(l."UpdatedAtUtc" AS text), ',' ORDER BY l."Id")
                    FROM "HabitLogs" l WHERE NOT l."IsDeleted" AND l."Date" >= {cutoff}
                        AND l."HabitId" IN (SELECT "Id" FROM eligible)), '')) AS "Fingerprint"
            """).ToListAsync(ct);
        return rows.Single().Fingerprint;
    }

    private sealed class SlipProbe
    {
        public string Fingerprint { get; set; } = string.Empty;
    }

    private static async Task<SlipSnapshot> LoadSnapshotAsync(OrbitDbContext db, DateOnly utcDate, string probe, CancellationToken ct)
    {
        var floor = utcDate.AddDays(-MaxTimeZoneSkewDays);
        var allHabits = new List<Habit>();
        var allUsers = new Dictionary<Guid, SlipUser>();
        var allLogs = new Dictionary<Guid, List<HabitLog>>();
        var allSent = new Dictionary<Guid, HashSet<DateOnly>>();
        Guid? after = null;
        while (true)
        {
            var rows = await db.Habits.AsNoTracking().Where(h => h.IsBadHabit && h.SlipAlertEnabled && !h.IsCompleted
                    && (!h.EndDate.HasValue || h.EndDate >= floor)
                    && (after == null || h.Id.CompareTo(after.Value) > 0))
                .OrderBy(h => h.Id).Take(256)
                .Select(h => new
                {
                    h.UserId,
                    h.Title,
                    Snapshot = new HabitScheduleSnapshot(h.Id, h.ParentHabitId,
                    h.FrequencyUnit, h.FrequencyQuantity, h.IntervalWeeks, h.NextDueDate, h.ScheduledStartDate,
                    h.OriginalDayOfMonth, h.EndDate, h.CreatedAtUtc, h.DeletedAtUtc, h.IsDeleted, h.IsBadHabit,
                    h.IsCompleted, h.Days.ToList(), h.Schedule, h.StartDate)
                }).ToListAsync(ct);
            if (rows.Count == 0) break;
            var habits = rows.Select(row =>
            {
                var habit = Habit.FromScheduleSnapshot(row.Snapshot, row.UserId);
                habit.LoadScheduleCandidateFieldsForRead(row.Title, null, null, []);
                return habit;
            }).ToArray();
            var userIds = habits.Select(h => h.UserId).Distinct().ToArray();
            var users = await db.Users.AsNoTracking().Where(u => userIds.Contains(u.Id))
                .Select(u => new SlipUser(u.Id, u.TimeZone, u.WeekStartDay, u.Language)).ToDictionaryAsync(u => u.Id, ct);
            var habitIds = habits.Select(h => h.Id).ToArray();
            var sentFloor = utcDate.AddDays(-(7 + MaxTimeZoneSkewDays));
            var sent = await db.SentSlipAlerts.AsNoTracking().Where(a => habitIds.Contains(a.HabitId) && a.WeekStart >= sentFloor)
                .Select(a => new { a.HabitId, a.WeekStart }).ToListAsync(ct);
            var cutoff = utcDate.AddDays(-60);
            var logs = await db.HabitLogs.AsNoTracking().Where(l => habitIds.Contains(l.HabitId) && l.Date >= cutoff)
                .Select(l => HabitLog.FromScheduleRead(l.Id, l.HabitId, l.Date, l.Value, 0, l.CreatedAtUtc, null, null, null)).ToListAsync(ct);
            allHabits.AddRange(habits);
            foreach (var pair in users) allUsers[pair.Key] = pair.Value;
            foreach (var group in logs.GroupBy(l => l.HabitId)) allLogs[group.Key] = group.ToList();
            foreach (var group in sent.GroupBy(a => a.HabitId)) allSent[group.Key] = group.Select(a => a.WeekStart).ToHashSet();
            after = habits[^1].Id;
            if (habits.Length < 256) break;
        }
        return new(utcDate, probe, allHabits, allUsers, allLogs, allSent);
    }

    private sealed record SlipCandidate(Habit Habit, SlipUser User, SlipPattern Pattern, DateOnly WeekStart);
    private sealed record PendingSlip(Habit Habit, DateOnly WeekStart, string Title, string Body);

    private SlipCandidate? FindDue(Habit habit, SlipUser user, List<HabitLog> logs, HashSet<DateOnly> sent, DateTime nowAtUtc)
    {
        var zone = TimeZoneHelper.FindTimeZone(user.TimeZone, logger, user.Id);
        var local = TimeZoneInfo.ConvertTimeFromUtc(nowAtUtc, zone);
        var today = DateOnly.FromDateTime(local);
        if (habit.EndDate.HasValue && habit.EndDate.Value < today) return null;
        if (!HabitScheduleService.IsActiveIntervalWeek(habit, today, user.WeekStartDay)) return null;
        var weekStart = WeekMath.WeekStart(today, user.WeekStartDay);
        if (sent.Contains(weekStart)) return null;
        var pattern = SlipPatternDetectionService.DetectPattern(logs, habit.Id, zone, nowAtUtc);
        if (pattern is null || local.DayOfWeek != pattern.DayOfWeek
            || !IsWithinSendWindow(TimeOnly.FromDateTime(local), CalculateAlertTime(pattern.PeakHour))) return null;
        return new(habit, user, pattern, weekStart);
    }

    private async Task<List<PendingSlip>> SaveClaimsAsync(OrbitDbContext db, List<PendingSlip> pending, CancellationToken ct)
    {
        if (pending.Count == 0) return [];
        foreach (var item in pending) AddClaim(db, item);
        try
        {
            await db.SaveChangesAsync(ct);
            return pending;
        }
        catch (DbUpdateException ex) when (DbUniqueViolation.IsUniqueViolation(ex))
        {
            DetachPendingEntries(db);
        }
        var accepted = new List<PendingSlip>();
        foreach (var item in pending)
        {
            AddClaim(db, item);
            try
            {
                await db.SaveChangesAsync(ct);
                accepted.Add(item);
            }
            catch (DbUpdateException ex) when (DbUniqueViolation.IsUniqueViolation(ex))
            {
                DetachPendingEntries(db);
                if (logger.IsEnabled(LogLevel.Debug)) LogSlipAlertAlreadySent(logger, item.Habit.Id);
            }
        }
        return accepted;
    }

    private static void AddClaim(OrbitDbContext db, PendingSlip item)
    {
        db.SentSlipAlerts.Add(SentSlipAlert.Create(item.Habit.Id, item.WeekStart));
        db.Notifications.Add(Notification.Create(item.Habit.UserId, item.Title, item.Body, NotificationUrls.Home, item.Habit.Id));
    }

    /// <summary>
    /// If a time pattern exists: 2 hours before peak, clamped to 8:00-22:00.
    /// If day-only pattern: send at 8:00 AM (early morning heads-up).
    /// </summary>
    private static TimeOnly CalculateAlertTime(int? peakHour)
    {
        var alertHour = peakHour.HasValue
            ? Math.Clamp(peakHour.Value - 2, 8, 22)
            : DefaultMorningHour;

        return new TimeOnly(alertHour, 0);
    }

    private bool IsWithinSendWindow(TimeOnly userTimeNow, TimeOnly alertTime)
    {
        var diff = userTimeNow.ToTimeSpan() - alertTime.ToTimeSpan();
        return diff >= TimeSpan.Zero && diff < _interval + _windowMargin;
    }

    private static void DetachPendingEntries(OrbitDbContext dbContext)
    {
        foreach (var entry in dbContext.ChangeTracker.Entries().ToList())
            entry.State = EntityState.Detached;
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "SlipAlertSchedulerService started")]
    private static partial void LogServiceStarted(ILogger logger);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "SlipAlertSchedulerService stopped")]
    private static partial void LogServiceStopped(ILogger logger);

    [LoggerMessage(EventId = 3, Level = LogLevel.Error, Message = "Error in slip alert scheduler")]
    private static partial void LogServiceError(ILogger logger, Exception ex);

    [LoggerMessage(EventId = 4, Level = LogLevel.Warning, Message = "Failed to generate slip alert message for habit {HabitId}")]
    private static partial void LogSlipAlertMessageFailed(ILogger logger, Guid habitId);

    [LoggerMessage(EventId = 6, Level = LogLevel.Debug, Message = "Slip alert already recorded for habit {HabitId}; skipping push")]
    private static partial void LogSlipAlertAlreadySent(ILogger logger, Guid habitId);

    [LoggerMessage(EventId = 7, Level = LogLevel.Error, Message = "Failed to process slip alert for habit {HabitId} (user {UserId}); continuing with remaining habits")]
    private static partial void LogSlipAlertProcessingFailed(ILogger logger, Guid habitId, Guid userId, Exception ex);
}
