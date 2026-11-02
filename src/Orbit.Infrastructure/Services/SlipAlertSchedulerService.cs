using System.Collections.Concurrent;
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
    }

    protected override void LogStarted() => LogServiceStarted(logger);

    protected override void LogStopped() => LogServiceStopped(logger);

    protected override void LogTickError(Exception ex) => LogServiceError(logger, ex);

    internal async Task CheckAndSendAlerts(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrbitDbContext>();
        var nowAtUtc = (timeProvider ?? TimeProvider.System).GetUtcNow().UtcDateTime;
        var utcDate = DateOnly.FromDateTime(nowAtUtc);
        var endDateFloor = utcDate.AddDays(-MaxTimeZoneSkewDays);
        var zones = await db.Users.AsNoTracking().Select(u => u.TimeZone).Distinct().ToListAsync(ct);
        var openZones = zones.Where(id =>
        {
            var local = TimeOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(nowAtUtc, TimeZoneHelper.FindTimeZone(id, logger, Guid.Empty)));
            return Enumerable.Range(8, 15).Any(hour => IsWithinSendWindow(local, new TimeOnly(hour, 0)));
        }).ToArray();
        if (openZones.Length == 0) return;
        var pending = new List<PendingSlip>();
        Guid? after = null;
        while (true)
        {
            var habits = await db.Habits.AsNoTracking()
                .Where(h => !h.IsCompleted && h.IsBadHabit && h.SlipAlertEnabled
                    && (!h.EndDate.HasValue || h.EndDate.Value >= endDateFloor)
                    && (after == null || h.Id.CompareTo(after.Value) > 0)
                    && db.Users.Any(u => u.Id == h.UserId && openZones.Contains(u.TimeZone)))
                .OrderBy(h => h.Id).Take(256).ToListAsync(ct);
            if (habits.Count == 0) break;
            var userIds = habits.Select(h => h.UserId).Distinct().ToArray();
            var users = await db.Users.AsNoTracking().Where(u => userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, ct);
            var habitIds = habits.Select(h => h.Id).ToArray();
            var sentFloor = utcDate.AddDays(-(7 + MaxTimeZoneSkewDays));
            var sent = (await db.SentSlipAlerts.AsNoTracking()
                .Where(a => habitIds.Contains(a.HabitId) && a.WeekStart >= sentFloor).ToListAsync(ct)).ToLookup(a => a.HabitId);
            var cutoff = utcDate.AddDays(-60);
            var logs = (await db.HabitLogs.AsNoTracking()
                .Where(l => habitIds.Contains(l.HabitId) && l.Date >= cutoff).ToListAsync(ct)).ToLookup(l => l.HabitId);
            var due = new List<SlipCandidate>();
            foreach (var habit in habits)
            {
                if (!users.TryGetValue(habit.UserId, out var user)) continue;
                var candidate = FindDue(habit, user, logs[habit.Id].ToList(), sent[habit.Id], nowAtUtc);
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
            after = habits[^1].Id;
            if (habits.Count < 256) break;
        }
        var accepted = await SaveClaimsAsync(db, pending, ct);
        if (accepted.Count > 0)
            await scope.ServiceProvider.GetRequiredService<IPushNotificationService>().SendManyAsync(accepted.Select(p =>
                new PushDelivery(p.Habit.UserId, p.Title, p.Body, NotificationUrls.Home)).ToArray(), ct);
    }

    private sealed record SlipCandidate(Habit Habit, User User, SlipPattern Pattern, DateOnly WeekStart);
    private sealed record PendingSlip(Habit Habit, DateOnly WeekStart, string Title, string Body);

    private SlipCandidate? FindDue(Habit habit, User user, List<HabitLog> logs, IEnumerable<SentSlipAlert> sent, DateTime nowAtUtc)
    {
        var zone = TimeZoneHelper.FindTimeZone(user.TimeZone, logger, user.Id);
        var local = TimeZoneInfo.ConvertTimeFromUtc(nowAtUtc, zone);
        var today = DateOnly.FromDateTime(local);
        if (habit.EndDate.HasValue && habit.EndDate.Value < today) return null;
        if (!HabitScheduleService.IsActiveIntervalWeek(habit, today, user.WeekStartDay)) return null;
        var weekStart = WeekMath.WeekStart(today, user.WeekStartDay);
        if (sent.Any(a => a.WeekStart == weekStart)) return null;
        var pattern = SlipPatternDetectionService.DetectPattern(logs, habit.Id, zone);
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
