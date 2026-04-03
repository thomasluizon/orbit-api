using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orbit.Application.Common;
using Orbit.Application.Habits.Services;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.Persistence;

namespace Orbit.Infrastructure.Services;

public partial class SlipAlertSchedulerService(
    IServiceScopeFactory scopeFactory,
    ILogger<SlipAlertSchedulerService> logger,
    IConfiguration configuration) : BackgroundService
{
    private const int DefaultMorningHour = 8;

    private readonly TimeSpan _interval = TimeSpan.FromMinutes(
        configuration.GetValue("BackgroundServices:SlipAlertIntervalMinutes", 5));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LogServiceStarted(logger);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await CheckAndSendAlerts(stoppingToken);
                    BackgroundServiceHealthCheck.RecordTick("SlipAlertScheduler");
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    LogServiceError(logger, ex);
                }

                await Task.Delay(_interval, stoppingToken);
            }
        }
        finally
        {
            LogServiceStopped(logger);
        }
    }

    private async Task CheckAndSendAlerts(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<OrbitDbContext>();
        var pushService = scope.ServiceProvider.GetRequiredService<IPushNotificationService>();
        var messageService = scope.ServiceProvider.GetRequiredService<ISlipAlertMessageService>();

        // Load active bad habits with slip alerts enabled.
        // Subtract 1 day from UTC date to include users west of UTC whose local "today" lags behind UTC.
        // The per-user timezone check (userToday) below is the authoritative active/ended guard.
        var utcDate = DateOnly.FromDateTime(DateTime.UtcNow);
        var habits = await dbContext.Habits
            .Where(h => !h.IsCompleted && h.IsBadHabit && h.SlipAlertEnabled
                && (!h.EndDate.HasValue || h.EndDate.Value >= utcDate.AddDays(-1)))
            .ToListAsync(ct);

        if (habits.Count == 0) return;

        // Group by user to handle timezones
        var userIds = habits.Select(h => h.UserId).Distinct().ToList();
        var users = await dbContext.Users
            .Where(u => userIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, ct);

        // Batch-load all relevant habit logs upfront (avoid per-habit query in loop)
        var habitIds = habits.Select(h => h.Id).ToList();
        var cutoff = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-60));
        var allLogs = await dbContext.HabitLogs
            .Where(l => habitIds.Contains(l.HabitId) && l.Date >= cutoff)
            .ToListAsync(ct);
        var logsByHabit = allLogs.GroupBy(l => l.HabitId)
            .ToDictionary(g => g.Key, g => g.ToList());

        // Batch-load sent slip alerts for this week
        var daysToMondayUtc = ((int)DateTime.UtcNow.DayOfWeek - 1 + 7) % 7;
        var currentWeekStart = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-daysToMondayUtc));
        var sentAlertHabitIds = (await dbContext.SentSlipAlerts
            .Where(a => habitIds.Contains(a.HabitId) && a.WeekStart == currentWeekStart)
            .Select(a => a.HabitId)
            .ToListAsync(ct)).ToHashSet();

        var anyChanges = false;

        var context = new SlipAlertContext(users, logsByHabit, sentAlertHabitIds,
            pushService, messageService, dbContext);

        foreach (var habit in habits)
        {
            var alertSent = await ProcessHabitSlipAlertAsync(habit, context, ct);

            if (alertSent) anyChanges = true;
        }

        if (anyChanges)
            await dbContext.SaveChangesAsync(ct);
    }

    private sealed record SlipAlertContext(
        Dictionary<Guid, User> Users,
        Dictionary<Guid, List<HabitLog>> LogsByHabit,
        HashSet<Guid> SentAlertHabitIds,
        IPushNotificationService PushService,
        ISlipAlertMessageService MessageService,
        OrbitDbContext DbContext);

    private async Task<bool> ProcessHabitSlipAlertAsync(
        Habit habit, SlipAlertContext ctx, CancellationToken ct)
    {
        if (!ctx.Users.TryGetValue(habit.UserId, out var user)) return false;

        var tz = TimeZoneHelper.FindTimeZone(user.TimeZone, logger, user.Id);
        var userNow = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, tz);
        var userTimeNow = TimeOnly.FromDateTime(userNow);

        var logs = ctx.LogsByHabit.GetValueOrDefault(habit.Id) ?? [];
        var pattern = SlipPatternDetectionService.DetectPattern(logs, habit.Id, tz);
        if (pattern is null) return false;

        if (userNow.DayOfWeek != pattern.DayOfWeek) return false;

        var alertTime = CalculateAlertTime(pattern.PeakHour);
        if (!IsWithinSendWindow(userTimeNow, alertTime)) return false;

        if (ctx.SentAlertHabitIds.Contains(habit.Id)) return false;

        var lang = user.Language ?? "en";
        var messageResult = await ctx.MessageService.GenerateMessageAsync(
            habit.Title, pattern.DayOfWeek, pattern.PeakHour, lang, ct);

        if (messageResult.IsFailure)
        {
            if (logger.IsEnabled(LogLevel.Warning))
                LogSlipAlertMessageFailed(logger, habit.Id);
            return false;
        }

        var (title, body) = messageResult.Value;

        await ctx.PushService.SendToUserAsync(habit.UserId, title, body, "/", ct);

        await RecordSentAlertAsync(habit, userNow, title, body, ctx.DbContext, ct);

        ctx.SentAlertHabitIds.Add(habit.Id);
        if (logger.IsEnabled(LogLevel.Information))
            LogSentSlipAlert(logger, habit.Id, habit.UserId);
        return true;
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

    private static bool IsWithinSendWindow(TimeOnly userTimeNow, TimeOnly alertTime)
    {
        var diffMinutes = (userTimeNow - alertTime).TotalMinutes;
        return diffMinutes >= 0 && diffMinutes < 5;
    }

    private static async Task RecordSentAlertAsync(
        Habit habit, DateTime userNow, string title, string body,
        OrbitDbContext dbContext, CancellationToken ct)
    {
        var userToday = DateOnly.FromDateTime(userNow);
        var daysToMonday = ((int)userToday.DayOfWeek - 1 + 7) % 7;
        var weekStart = userToday.AddDays(-daysToMonday);
        var sentAlert = SentSlipAlert.Create(habit.Id, weekStart);
        await dbContext.SentSlipAlerts.AddAsync(sentAlert, ct);

        var notification = Notification.Create(habit.UserId, title, body, "/", habit.Id);
        await dbContext.Notifications.AddAsync(notification, ct);
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "SlipAlertSchedulerService started")]
    private static partial void LogServiceStarted(ILogger logger);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "SlipAlertSchedulerService stopped")]
    private static partial void LogServiceStopped(ILogger logger);

    [LoggerMessage(EventId = 3, Level = LogLevel.Error, Message = "Error in slip alert scheduler")]
    private static partial void LogServiceError(ILogger logger, Exception ex);

    [LoggerMessage(EventId = 4, Level = LogLevel.Warning, Message = "Failed to generate slip alert message for habit {HabitId}")]
    private static partial void LogSlipAlertMessageFailed(ILogger logger, Guid habitId);

    [LoggerMessage(EventId = 5, Level = LogLevel.Information, Message = "Sent slip alert for habit {HabitId} to user {UserId}")]
    private static partial void LogSentSlipAlert(ILogger logger, Guid habitId, Guid userId);

}
