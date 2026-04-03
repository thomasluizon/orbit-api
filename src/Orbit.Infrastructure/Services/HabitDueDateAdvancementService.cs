using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orbit.Application.Common;
using Orbit.Infrastructure.Persistence;

namespace Orbit.Infrastructure.Services;

public partial class HabitDueDateAdvancementService(
    IServiceScopeFactory scopeFactory,
    ILogger<HabitDueDateAdvancementService> logger,
    IConfiguration configuration) : BackgroundService
{
    private readonly TimeSpan _interval = TimeSpan.FromMinutes(
        configuration.GetValue("BackgroundServices:DueDateAdvancementIntervalMinutes", 30));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LogServiceStarted(logger);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await AdvanceStaleDueDates(stoppingToken);
                    BackgroundServiceHealthCheck.RecordTick("HabitDueDateAdvancement");
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

    private async Task AdvanceStaleDueDates(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<OrbitDbContext>();

        // Conservative cutoff: UTC today - 1 day to avoid timezone false positives
        var cutoff = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-1));
        var habits = await dbContext.Habits
            .Where(h => !h.IsCompleted
                && h.FrequencyUnit != null
                && h.FrequencyQuantity != null
                && !h.IsFlexible
                && h.DueDate < cutoff)
            .ToListAsync(ct);

        if (habits.Count == 0) return;

        var userIds = habits.Select(h => h.UserId).Distinct().ToList();
        var users = await dbContext.Users
            .Where(u => userIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, ct);

        var advanced = 0;
        foreach (var habit in habits)
        {
            if (!users.TryGetValue(habit.UserId, out var user)) continue;

            var tz = TimeZoneHelper.FindTimeZone(user.TimeZone, logger, user.Id);
            var userNow = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, tz);
            var userToday = DateOnly.FromDateTime(userNow);

            if (habit.DueDate >= userToday) continue;

            habit.CatchUpDueDate(userToday);
            advanced++;
        }

        if (advanced > 0)
        {
            await dbContext.SaveChangesAsync(ct);
            if (logger.IsEnabled(LogLevel.Information))
                LogDueDatesAdvanced(logger, advanced);
        }
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "HabitDueDateAdvancementService started")]
    private static partial void LogServiceStarted(ILogger logger);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "HabitDueDateAdvancementService stopped")]
    private static partial void LogServiceStopped(ILogger logger);

    [LoggerMessage(EventId = 3, Level = LogLevel.Error, Message = "Error in habit due date advancement")]
    private static partial void LogServiceError(ILogger logger, Exception ex);

    [LoggerMessage(EventId = 4, Level = LogLevel.Information, Message = "Advanced DueDate for {Count} recurring habits")]
    private static partial void LogDueDatesAdvanced(ILogger logger, int count);

}
