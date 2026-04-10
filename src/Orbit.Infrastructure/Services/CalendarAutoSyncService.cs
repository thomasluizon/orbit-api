using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orbit.Application.Calendar.Commands;
using Orbit.Domain.Enums;
using Orbit.Infrastructure.Persistence;

namespace Orbit.Infrastructure.Services;

/// <summary>
/// Background service that periodically runs Google Calendar auto-sync for eligible users.
/// Pro-gated server-side, batched (50 users/tick), and per-user try/catch so one bad user
/// does not crash the tick. All business logic lives in <see cref="RunCalendarAutoSyncCommand"/>.
/// </summary>
public partial class CalendarAutoSyncService(
    IServiceScopeFactory scopeFactory,
    ILogger<CalendarAutoSyncService> logger,
    IConfiguration configuration,
    TimeProvider timeProvider) : BackgroundService
{
    private readonly TimeSpan _interval = TimeSpan.FromMinutes(
        configuration.GetValue("BackgroundServices:CalendarAutoSyncIntervalMinutes", 15));
    private static readonly TimeSpan DedupeWindow = TimeSpan.FromHours(4);
    private const int BatchSize = 50;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LogServiceStarted(logger);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await ProcessTick(stoppingToken);
                    BackgroundServiceHealthCheck.RecordTick("CalendarAutoSync");
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

    private async Task ProcessTick(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<OrbitDbContext>();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

        var nowUtc = timeProvider.GetUtcNow().UtcDateTime;
        var cutoff = nowUtc - DedupeWindow;

        var userIds = await dbContext.Users
            .Where(u =>
                u.GoogleCalendarAutoSyncEnabled
                && u.GoogleAccessToken != null
                && (u.GoogleCalendarLastSyncedAt == null || u.GoogleCalendarLastSyncedAt < cutoff)
                && (u.IsLifetimePro
                    || (u.Plan == UserPlan.Pro && u.PlanExpiresAt != null && u.PlanExpiresAt > nowUtc)))
            .OrderBy(u => u.GoogleCalendarLastSyncedAt ?? DateTime.MinValue)
            .Take(BatchSize)
            .Select(u => u.Id)
            .ToListAsync(ct);

        if (userIds.Count == 0) return;

        LogTickStarted(logger, userIds.Count);

        int succeeded = 0;
        foreach (var userId in userIds)
        {
            if (ct.IsCancellationRequested) break;

            try
            {
                var result = await mediator.Send(new RunCalendarAutoSyncCommand(userId), ct);
                if (result.IsSuccess)
                    succeeded++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogUserSyncFailed(logger, ex, userId);
            }
        }

        LogTickCompleted(logger, succeeded, userIds.Count);
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "CalendarAutoSyncService started")]
    private static partial void LogServiceStarted(ILogger logger);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "CalendarAutoSyncService stopped")]
    private static partial void LogServiceStopped(ILogger logger);

    [LoggerMessage(EventId = 3, Level = LogLevel.Error, Message = "Error in calendar auto-sync tick")]
    private static partial void LogServiceError(ILogger logger, Exception ex);

    [LoggerMessage(EventId = 4, Level = LogLevel.Information, Message = "CalendarAutoSync tick processing {Count} users")]
    private static partial void LogTickStarted(ILogger logger, int count);

    [LoggerMessage(EventId = 5, Level = LogLevel.Information, Message = "CalendarAutoSync tick finished: {Succeeded}/{Total} succeeded")]
    private static partial void LogTickCompleted(ILogger logger, int succeeded, int total);

    [LoggerMessage(EventId = 6, Level = LogLevel.Warning, Message = "CalendarAutoSync failed for user {UserId}")]
    private static partial void LogUserSyncFailed(ILogger logger, Exception ex, Guid userId);
}
