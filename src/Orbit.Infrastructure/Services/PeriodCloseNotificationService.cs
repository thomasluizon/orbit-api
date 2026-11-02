using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orbit.Application.Common;
using Orbit.Application.Notifications;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.BackgroundJobs;
using Orbit.Infrastructure.Persistence;
using Orbit.Infrastructure.Services.Hosting;

namespace Orbit.Infrastructure.Services;

public partial class PeriodCloseNotificationService(
    IServiceScopeFactory scopeFactory,
    ILogger<PeriodCloseNotificationService> logger,
    IConfiguration configuration) : ScheduledServiceBase, IScheduledJob
{
    private readonly TimeSpan _interval = TimeSpan.FromMinutes(
        configuration.GetValue("BackgroundServices:PeriodCloseNotificationIntervalMinutes", 30));

    public string Name => "period-close-notification";

    public string CronExpression => "*/30 * * * *";

    public Task RunAsync(CancellationToken cancellationToken) => ExecuteTickAsync(cancellationToken);

    protected override TimeSpan Interval => _interval;

    protected override async Task ExecuteTickAsync(CancellationToken stoppingToken)
    {
        await CheckAndSendNotificationsAsync(stoppingToken);
    }

    protected override void LogStarted() => LogServiceStarted(logger);

    protected override void LogStopped() => LogServiceStopped(logger);

    protected override void LogTickError(Exception ex) => LogServiceError(logger, ex);

    internal async Task CheckAndSendNotificationsAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<OrbitDbContext>();
        var userDateService = scope.ServiceProvider.GetRequiredService<IUserDateService>();

        Guid? cursor = null;
        while (true)
        {
            var subscribedUsers = await dbContext.Users
                .AsNoTracking()
                .Where(user => !user.IsDeactivated
                    && dbContext.PushSubscriptions.Any(subscription => subscription.UserId == user.Id))
                .Page(cursor, configuration.GetValue("BackgroundServices:ScanPageSize", JobPaging.DefaultPageSize))
                .Select(user => new SubscribedUser(user.Id, user.TimeZone, user.Language))
                .ToListAsync(cancellationToken);

            if (subscribedUsers.Count == 0) return;
            cursor = subscribedUsers[^1].Id;
            var boundaryUsers = new List<BoundaryUser>();
            foreach (var user in subscribedUsers)
            {
                var userToday = await userDateService.GetUserTodayAsync(
                    user.TimeZone,
                    user.Id,
                    cancellationToken);
                if (userToday.Day != 1)
                    continue;

                var closedMonth = userToday.AddMonths(-1);
                boundaryUsers.Add(new BoundaryUser(user.Id, user.Language, closedMonth.Year, closedMonth.Month));
            }

            foreach (var monthGroup in boundaryUsers.GroupBy(user => new { user.Year, user.Month }))
            {
                await ProcessClosedMonthAsync(
                    monthGroup.ToList(),
                    monthGroup.Key.Year,
                    monthGroup.Key.Month,
                    dbContext,
                    cancellationToken);
            }
            dbContext.ChangeTracker.Clear();
        }
    }

    private async Task ProcessClosedMonthAsync(
        List<BoundaryUser> users,
        int year,
        int month,
        OrbitDbContext dbContext,
        CancellationToken cancellationToken)
    {
        var dateFrom = new DateOnly(year, month, 1);
        var dateTo = new DateOnly(year, month, DateTime.DaysInMonth(year, month));
        var userIds = users.Select(user => user.Id).ToList();

        var activeUserIds = (await dbContext.Habits
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(habit => userIds.Contains(habit.UserId)
                && habit.Logs.Any(log => !log.IsDeleted
                    && log.Value > 0
                    && log.Date >= dateFrom
                    && log.Date <= dateTo))
            .Select(habit => habit.UserId)
            .Distinct()
            .ToListAsync(cancellationToken))
            .ToHashSet();

        var activeUsers = users.Where(user => activeUserIds.Contains(user.Id)).ToList();
        if (activeUsers.Count == 0)
            return;

        var dedupeKeys = activeUsers
            .Select(user => BuildDedupeKey(user.Id, year, month))
            .ToList();
        var sentKeys = (await dbContext.Notifications
            .Where(notification => dedupeKeys.Contains(notification.DedupeKey!))
            .Select(notification => notification.DedupeKey!)
            .ToListAsync(cancellationToken))
            .ToHashSet();

        var claims = activeUsers.Where(user => sentKeys.Add(BuildDedupeKey(user.Id, year, month))).Select(user =>
        {
            var (title, body) = BuildNotification(month, user.Language);
            return Notification.Create(user.Id, title, body, NotificationUrls.WrappedClosedMonth(year, month),
                dedupeKey: BuildDedupeKey(user.Id, year, month));
        }).ToList();
        if (claims.Count == 0) return;
        dbContext.Notifications.AddRange(claims);
        try { await dbContext.SaveChangesAsync(cancellationToken); }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            foreach (var claim in claims) dbContext.Entry(claim).State = EntityState.Detached;
            var retained = new List<Notification>();
            foreach (var claim in claims)
                if (await TryRecordAsync(claim, year, month, dbContext, cancellationToken)) retained.Add(claim);
            claims = retained;
        }
        foreach (var claim in claims) dbContext.Entry(claim).State = EntityState.Detached;
        var failed = new System.Collections.Concurrent.ConcurrentBag<Notification>();
        try
        {
            await Parallel.ForEachAsync(claims, new ParallelOptions
            { MaxDegreeOfParallelism = Math.Clamp(configuration.GetValue("BackgroundServices:PushParallelism", 4), 1, 8), CancellationToken = cancellationToken }, async (claim, token) =>
            {
                try
                {
                    using var deliveryScope = scopeFactory.CreateScope();
                    var pushService = deliveryScope.ServiceProvider.GetRequiredService<IPushNotificationService>();
                    if (!await pushService.TrySendToUserAsync(claim.UserId, claim.Title, claim.Body, claim.Url, token))
                    {
                        failed.Add(claim);
                        LogNotificationNotDelivered(logger, claim.UserId, year, month);
                    }
                    else if (logger.IsEnabled(LogLevel.Debug)) LogNotificationSent(logger, claim.UserId, year, month);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    failed.Add(claim);
                    LogUserProcessingFailed(logger, claim.UserId, year, month, exception);
                }
                catch (OperationCanceledException) { failed.Add(claim); throw; }
            });
        }
        finally
        {
            foreach (var claim in failed) await RemoveNotificationAfterPushFailureAsync(claim, dbContext);
        }
    }

    private async Task<bool> TryRecordAsync(Notification notification, int year, int month,
        OrbitDbContext dbContext, CancellationToken ct)
    {
        dbContext.Notifications.Add(notification);
        try
        {
            await dbContext.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateException exception) when (DbUniqueViolation.IsUniqueViolation(exception))
        {
            dbContext.Entry(notification).State = EntityState.Detached;
            if (logger.IsEnabled(LogLevel.Debug)) LogNotificationAlreadyRecorded(logger, notification.UserId, year, month);
            return false;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            dbContext.Entry(notification).State = EntityState.Detached;
            LogNotificationRecordFailed(logger, notification.UserId, year, month, exception);
            return false;
        }
    }

    private async Task RemoveNotificationAfterPushFailureAsync(Notification notification, OrbitDbContext dbContext)
    {
        try
        {
            dbContext.Notifications.Remove(notification);
            await dbContext.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            dbContext.Entry(notification).State = EntityState.Detached;
            LogNotificationReleaseFailed(logger, notification.UserId, notification.DedupeKey!, ex);
        }
    }

    internal static string BuildDedupeKey(Guid userId, int year, int month) =>
        $"wrapped-{userId}-{year}-{month:D2}";

    internal static (string Title, string Body) BuildNotification(int month, string? language)
    {
        var isPortuguese = LocaleHelper.IsPortuguese(language);
        var culture = CultureInfo.GetCultureInfo(isPortuguese ? "pt-BR" : "en-US");
        var monthName = culture.TextInfo.ToTitleCase(culture.DateTimeFormat.GetMonthName(month));

        return isPortuguese
            ? ("Seu Wrapped está pronto", $"{monthName} terminou. Veja como foi o seu mês.")
            : ("Your Wrapped is ready", $"{monthName} is over. See how your month went.");
    }

    private sealed record SubscribedUser(Guid Id, string? TimeZone, string? Language);

    private sealed record BoundaryUser(Guid Id, string? Language, int Year, int Month);

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "PeriodCloseNotificationService started")]
    private static partial void LogServiceStarted(ILogger logger);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "PeriodCloseNotificationService stopped")]
    private static partial void LogServiceStopped(ILogger logger);

    [LoggerMessage(EventId = 3, Level = LogLevel.Error, Message = "Error in period close notification service")]
    private static partial void LogServiceError(ILogger logger, Exception ex);

    [LoggerMessage(EventId = 4, Level = LogLevel.Debug, Message = "Sent closed month notification for user {UserId} and period {Year}-{Month}")]
    private static partial void LogNotificationSent(ILogger logger, Guid userId, int year, int month);

    [LoggerMessage(EventId = 5, Level = LogLevel.Debug, Message = "Closed month notification already recorded for user {UserId} and period {Year}-{Month}")]
    private static partial void LogNotificationAlreadyRecorded(ILogger logger, Guid userId, int year, int month);

    [LoggerMessage(EventId = 6, Level = LogLevel.Error, Message = "Failed to record closed month notification for user {UserId} and period {Year}-{Month}")]
    private static partial void LogNotificationRecordFailed(ILogger logger, Guid userId, int year, int month, Exception ex);

    [LoggerMessage(EventId = 7, Level = LogLevel.Error, Message = "Failed to process closed month notification for user {UserId} and period {Year}-{Month}")]
    private static partial void LogUserProcessingFailed(ILogger logger, Guid userId, int year, int month, Exception ex);

    [LoggerMessage(EventId = 8, Level = LogLevel.Error, Message = "Failed to release closed month notification claim {DedupeKey} for user {UserId} after push failure")]
    private static partial void LogNotificationReleaseFailed(ILogger logger, Guid userId, string dedupeKey, Exception ex);

    [LoggerMessage(EventId = 9, Level = LogLevel.Warning, Message = "No push delivery succeeded for user {UserId} and closed period {Year}-{Month}")]
    private static partial void LogNotificationNotDelivered(ILogger logger, Guid userId, int year, int month);
}
