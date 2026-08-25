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
        BackgroundServiceHealthCheck.RecordTick("PeriodCloseNotification");
    }

    protected override void LogStarted() => LogServiceStarted(logger);

    protected override void LogStopped() => LogServiceStopped(logger);

    protected override void LogTickError(Exception ex) => LogServiceError(logger, ex);

    internal async Task CheckAndSendNotificationsAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<OrbitDbContext>();
        var pushService = scope.ServiceProvider.GetRequiredService<IPushNotificationService>();
        var userDateService = scope.ServiceProvider.GetRequiredService<IUserDateService>();

        var subscribedUsers = await dbContext.Users
            .AsNoTracking()
            .Where(user => !user.IsDeactivated
                && dbContext.PushSubscriptions.Any(subscription => subscription.UserId == user.Id))
            .Select(user => new SubscribedUser(user.Id, user.TimeZone, user.Language))
            .ToListAsync(cancellationToken);

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
                pushService,
                cancellationToken);
        }
    }

    private async Task ProcessClosedMonthAsync(
        List<BoundaryUser> users,
        int year,
        int month,
        OrbitDbContext dbContext,
        IPushNotificationService pushService,
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

        foreach (var user in activeUsers)
        {
            var dedupeKey = BuildDedupeKey(user.Id, year, month);
            if (!sentKeys.Add(dedupeKey))
                continue;

            try
            {
                await TryRecordAndSendAsync(
                    user,
                    year,
                    month,
                    dedupeKey,
                    dbContext,
                    pushService,
                    cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogUserProcessingFailed(logger, user.Id, year, month, ex);
            }
        }
    }

    private async Task TryRecordAndSendAsync(
        BoundaryUser user,
        int year,
        int month,
        string dedupeKey,
        OrbitDbContext dbContext,
        IPushNotificationService pushService,
        CancellationToken cancellationToken)
    {
        var (title, body) = BuildNotification(month, user.Language);
        var url = NotificationUrls.WrappedClosedMonth(year, month);
        var notification = Notification.Create(
            user.Id,
            title,
            body,
            url,
            dedupeKey: dedupeKey);
        await dbContext.Notifications.AddAsync(notification, cancellationToken);

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (DbUniqueViolation.IsUniqueViolation(ex))
        {
            dbContext.Entry(notification).State = EntityState.Detached;
            if (logger.IsEnabled(LogLevel.Debug))
                LogNotificationAlreadyRecorded(logger, user.Id, year, month);
            return;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            dbContext.Entry(notification).State = EntityState.Detached;
            LogNotificationRecordFailed(logger, user.Id, year, month, ex);
            return;
        }

        try
        {
            var delivered = await pushService.TrySendToUserAsync(user.Id, title, body, url, cancellationToken);
            if (!delivered)
            {
                await RemoveNotificationAfterPushFailureAsync(notification, dbContext);
                LogNotificationNotDelivered(logger, user.Id, year, month);
                return;
            }
        }
        catch
        {
            await RemoveNotificationAfterPushFailureAsync(notification, dbContext);
            throw;
        }

        if (logger.IsEnabled(LogLevel.Debug))
            LogNotificationSent(logger, user.Id, year, month);
    }

    private async Task RemoveNotificationAfterPushFailureAsync(
        Notification notification,
        OrbitDbContext dbContext)
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
            ? ("Seu Wrapped está pronto", $"{monthName} fechou - veja como foi o seu mês.")
            : ("Your Wrapped is ready", $"{monthName} is closed - see how your month went.");
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
