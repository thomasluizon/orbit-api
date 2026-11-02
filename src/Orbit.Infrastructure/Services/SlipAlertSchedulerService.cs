using Orbit.Domain.Models;
using Orbit.Application.Chat;
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
    IConfiguration configuration, TimeProvider? timeProvider = null) : ScheduledServiceBase, IScheduledJob
{
    private const int DefaultMorningHour = 8;
    private readonly SemaphoreSlim _tickGate = new(1, 1);
    private SlipSnapshot? _snapshot;
    private TimeProvider Clock => timeProvider ?? TimeProvider.System;

    private const int MaxTimeZoneSkewDays = 1;

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
        var dbContext = scope.ServiceProvider.GetRequiredService<OrbitDbContext>();
        var pushService = scope.ServiceProvider.GetRequiredService<IPushNotificationService>();
        var messageService = scope.ServiceProvider.GetRequiredService<ISlipAlertMessageService>();

        var nowAtUtc = Clock.GetUtcNow().UtcDateTime;
#pragma warning disable ORBIT0004
        var utcDate = DateOnly.FromDateTime(nowAtUtc);
#pragma warning restore ORBIT0004
        var probe = await ReadProbeAsync(dbContext, utcDate, ct);
        if (_snapshot is null || _snapshot.UtcDate != utcDate || _snapshot.Probe != probe)
            _snapshot = await LoadSnapshotAsync(dbContext, utcDate, probe, ct);
        var snapshot = _snapshot;
        var habits = snapshot.Habits;
        var context = new SlipAlertContext(snapshot.Users, snapshot.LogsByHabit, snapshot.SentWeeksByHabit,
            pushService, messageService, dbContext);

        foreach (var habit in habits)
        {
            try
            {
                await ProcessHabitSlipAlertAsync(habit, context, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogSlipAlertProcessingFailed(logger, habit.Id, habit.UserId, ex);
            }
        }
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
        var rows = await db.Habits.AsNoTracking().Where(h => h.IsBadHabit && h.SlipAlertEnabled && !h.IsCompleted
                && (!h.EndDate.HasValue || h.EndDate >= floor))
            .Select(h => new
            {
                h.UserId,
                h.Title,
                Snapshot = new HabitScheduleSnapshot(h.Id, h.ParentHabitId,
                h.FrequencyUnit, h.FrequencyQuantity, h.IntervalWeeks, h.NextDueDate, h.ScheduledStartDate,
                h.OriginalDayOfMonth, h.EndDate, h.CreatedAtUtc, h.DeletedAtUtc, h.IsDeleted, h.IsBadHabit,
                h.IsCompleted, h.Days.ToList(), h.Schedule, h.StartDate)
            }).ToListAsync(ct);
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
        return new(utcDate, probe, habits, users, logs.GroupBy(l => l.HabitId).ToDictionary(g => g.Key, g => g.ToList()),
            sent.GroupBy(a => a.HabitId).ToDictionary(g => g.Key, g => g.Select(a => a.WeekStart).ToHashSet()));
    }

    private sealed record SlipAlertContext(
        Dictionary<Guid, SlipUser> Users,
        Dictionary<Guid, List<HabitLog>> LogsByHabit,
        Dictionary<Guid, HashSet<DateOnly>> SentWeeksByHabit,
        IPushNotificationService PushService,
        ISlipAlertMessageService MessageService,
        OrbitDbContext DbContext);

    private async Task ProcessHabitSlipAlertAsync(
        Habit habit, SlipAlertContext ctx, CancellationToken ct)
    {
        if (!ctx.Users.TryGetValue(habit.UserId, out var user)) return;

        var tz = TimeZoneHelper.FindTimeZone(user.TimeZone, logger, user.Id);
        var userNow = TimeZoneInfo.ConvertTimeFromUtc(Clock.GetUtcNow().UtcDateTime, tz);
        var userToday = DateOnly.FromDateTime(userNow);
        var userTimeNow = TimeOnly.FromDateTime(userNow);

        if (habit.EndDate.HasValue && habit.EndDate.Value < userToday) return;
        if (!HabitScheduleService.IsActiveIntervalWeek(habit, userToday, user.WeekStartDay)) return;

        var weekStart = WeekMath.WeekStart(userToday, user.WeekStartDay);
        var sentWeeks = ctx.SentWeeksByHabit.GetValueOrDefault(habit.Id) ?? [];
        if (sentWeeks.Contains(weekStart)) return;

        var logs = ctx.LogsByHabit.GetValueOrDefault(habit.Id) ?? [];
        var pattern = SlipPatternDetectionService.DetectPattern(logs, habit.Id, tz, Clock.GetUtcNow().UtcDateTime);
        if (pattern is null) return;

        if (userNow.DayOfWeek != pattern.DayOfWeek) return;

        var alertTime = CalculateAlertTime(pattern.PeakHour);
        if (!IsWithinSendWindow(userTimeNow, alertTime)) return;

        var lang = user.Language ?? "en";
        using var aiUser = Orbit.Infrastructure.AI.AiCallContext.Begin(user.Id);
        var messageResult = await ctx.MessageService.GenerateMessageAsync(
            habit.Title, pattern.DayOfWeek, pattern.PeakHour, lang, ct);

        if (messageResult.IsFailure)
        {
            if (logger.IsEnabled(LogLevel.Warning))
                LogSlipAlertMessageFailed(logger, habit.Id);
            return;
        }

        var (title, body) = messageResult.Value;

        if (!await TryRecordSentAlertAsync(habit, weekStart, title, body, ctx.DbContext, ct))
            return;

        sentWeeks.Add(weekStart);
        ctx.SentWeeksByHabit[habit.Id] = sentWeeks;

        await ctx.PushService.SendToUserAsync(habit.UserId, title, body, NotificationUrls.Home, ct);

        if (logger.IsEnabled(LogLevel.Debug))
            LogSentSlipAlert(logger, habit.Id, habit.UserId);
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
        return diff >= TimeSpan.Zero && diff < _interval;
    }

    private async Task<bool> TryRecordSentAlertAsync(
        Habit habit, DateOnly weekStart, string title, string body,
        OrbitDbContext dbContext, CancellationToken ct)
    {
        await dbContext.SentSlipAlerts.AddAsync(SentSlipAlert.Create(habit.Id, weekStart), ct);
        await dbContext.Notifications.AddAsync(
            Notification.Create(habit.UserId, title, body, NotificationUrls.Home, habit.Id), ct);

        try
        {
            await dbContext.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateException ex) when (DbUniqueViolation.IsUniqueViolation(ex))
        {
            DetachPendingEntries(dbContext);
            if (logger.IsEnabled(LogLevel.Debug))
                LogSlipAlertAlreadySent(logger, habit.Id);
            return false;
        }
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

    [LoggerMessage(EventId = 5, Level = LogLevel.Debug, Message = "Sent slip alert for habit {HabitId} to user {UserId}")]
    private static partial void LogSentSlipAlert(ILogger logger, Guid habitId, Guid userId);

    [LoggerMessage(EventId = 6, Level = LogLevel.Debug, Message = "Slip alert already recorded for habit {HabitId}; skipping push")]
    private static partial void LogSlipAlertAlreadySent(ILogger logger, Guid habitId);

    [LoggerMessage(EventId = 7, Level = LogLevel.Error, Message = "Failed to process slip alert for habit {HabitId} (user {UserId}); continuing with remaining habits")]
    private static partial void LogSlipAlertProcessingFailed(ILogger logger, Guid habitId, Guid userId, Exception ex);
}
