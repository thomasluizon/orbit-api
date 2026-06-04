using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orbit.Application.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.Persistence;

namespace Orbit.Infrastructure.Services;

/// <summary>
/// Auto-activates a streak freeze for a Pro user who held an active streak but logged
/// nothing on their fully-elapsed local "yesterday". Inserting a <see cref="StreakFreeze"/>
/// row for the missed date is sufficient to preserve the streak: the presence-based
/// resolver in <see cref="UserStreakService"/> treats any date carrying a freeze as covered,
/// so the next on-read RecalculateAsync keeps CurrentStreak intact without mutating it here.
/// Idempotency is enforced by two unique guards — the StreakFreeze (UserId, UsedOnDate) index
/// and the SentStreakFreezeAlert (UserId, FrozenDate) index — both re-checked before spending.
/// </summary>
public partial class StreakFreezeAutoActivationService(
    IServiceScopeFactory scopeFactory,
    ILogger<StreakFreezeAutoActivationService> logger,
    IConfiguration configuration) : BackgroundService
{
    private readonly TimeSpan _interval = TimeSpan.FromMinutes(
        configuration.GetValue("BackgroundServices:StreakFreezeIntervalMinutes", 60));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LogServiceStarted(logger);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await ActivateMissedDayFreezes(stoppingToken);
                    BackgroundServiceHealthCheck.RecordTick("StreakFreezeAutoActivation");
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

    private async Task ActivateMissedDayFreezes(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<OrbitDbContext>();
        var pushService = scope.ServiceProvider.GetRequiredService<IPushNotificationService>();

        // Conservative UTC pre-filter: a user can only have a fully-elapsed missed day if their
        // last active date is already before UTC yesterday. The per-user local-yesterday guard
        // below is the authoritative check. HasProAccess is computed (not mapped) so it is gated
        // in memory per user rather than in SQL.
        var utcYesterday = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-1));
        var candidates = await dbContext.Users
            .Where(u => u.CurrentStreak > 0
                && u.StreakFreezesAccumulated > 0
                && u.LastActiveDate != null
                && u.LastActiveDate < utcYesterday)
            .ToListAsync(ct);

        if (candidates.Count == 0) return;

        var candidateIds = candidates.Select(u => u.Id).ToList();

        var monthFloor = utcYesterday.AddDays(-1).AddMonths(-1);
        var freezesByUser = (await dbContext.StreakFreezes
            .Where(f => candidateIds.Contains(f.UserId) && f.UsedOnDate >= monthFloor)
            .ToListAsync(ct))
            .GroupBy(f => f.UserId)
            .ToDictionary(g => g.Key, g => g.ToList());

        var guardedByUser = (await dbContext.SentStreakFreezeAlerts
            .Where(a => candidateIds.Contains(a.UserId) && a.FrozenDate >= monthFloor)
            .ToListAsync(ct))
            .GroupBy(a => a.UserId)
            .ToDictionary(g => g.Key, g => g.Select(a => a.FrozenDate).ToHashSet());

        var completionsByUser = await LoadRecentCompletionsAsync(dbContext, candidateIds, monthFloor, ct);

        var anyChanges = false;
        foreach (var user in candidates)
        {
            anyChanges |= await ProcessUserAsync(user, freezesByUser, guardedByUser, completionsByUser, pushService, dbContext, ct);
        }

        if (anyChanges)
            await dbContext.SaveChangesAsync(ct);
    }

    private static async Task<Dictionary<Guid, HashSet<DateOnly>>> LoadRecentCompletionsAsync(
        OrbitDbContext dbContext, List<Guid> userIds, DateOnly since, CancellationToken ct)
    {
        var habitOwners = await dbContext.Habits
            .Where(h => userIds.Contains(h.UserId) && !h.IsBadHabit)
            .Select(h => new { h.Id, h.UserId })
            .ToListAsync(ct);

        var ownerByHabit = habitOwners.ToDictionary(h => h.Id, h => h.UserId);
        var habitIds = habitOwners.Select(h => h.Id).ToList();
        if (habitIds.Count == 0) return new Dictionary<Guid, HashSet<DateOnly>>();

        var logs = await dbContext.HabitLogs
            .Where(l => habitIds.Contains(l.HabitId) && l.Value > 0 && l.Date >= since)
            .Select(l => new { l.HabitId, l.Date })
            .ToListAsync(ct);

        var completions = new Dictionary<Guid, HashSet<DateOnly>>();
        foreach (var log in logs)
        {
            if (!ownerByHabit.TryGetValue(log.HabitId, out var ownerId)) continue;
            if (!completions.TryGetValue(ownerId, out var dates))
            {
                dates = [];
                completions[ownerId] = dates;
            }
            dates.Add(log.Date);
        }
        return completions;
    }

    private async Task<bool> ProcessUserAsync(
        User user,
        Dictionary<Guid, List<StreakFreeze>> freezesByUser,
        Dictionary<Guid, HashSet<DateOnly>> guardedByUser,
        Dictionary<Guid, HashSet<DateOnly>> completionsByUser,
        IPushNotificationService pushService,
        OrbitDbContext dbContext,
        CancellationToken ct)
    {
        if (!user.HasProAccess) return false;

        var tz = TimeZoneHelper.FindTimeZone(user.TimeZone, logger, user.Id);
        var userToday = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, tz));
        var missedDate = userToday.AddDays(-1);

        // Authoritative local guard: the missed day must be fully elapsed (strictly before today)
        // and the user must not already be credited as active on or after it.
        if (user.LastActiveDate is null || user.LastActiveDate >= missedDate) return false;

        var existingFreezes = freezesByUser.GetValueOrDefault(user.Id) ?? [];
        if (existingFreezes.Any(f => f.UsedOnDate == missedDate)) return false;

        var guardedDates = guardedByUser.GetValueOrDefault(user.Id) ?? [];
        if (guardedDates.Contains(missedDate)) return false;

        var completions = completionsByUser.GetValueOrDefault(user.Id) ?? [];
        if (completions.Contains(missedDate)) return false;

        var monthStart = new DateOnly(missedDate.Year, missedDate.Month, 1);
        var monthEnd = monthStart.AddMonths(1);
        var freezesThisMonth = existingFreezes.Count(f => f.UsedOnDate >= monthStart && f.UsedOnDate < monthEnd);
        if (freezesThisMonth >= AppConstants.MaxStreakFreezesPerMonth) return false;

        var consume = user.ConsumeStreakFreeze();
        if (consume.IsFailure) return false;

        var freeze = StreakFreeze.Create(user.Id, missedDate);
        dbContext.StreakFreezes.Add(freeze);
        existingFreezes.Add(freeze);
        freezesByUser[user.Id] = existingFreezes;

        dbContext.SentStreakFreezeAlerts.Add(SentStreakFreezeAlert.Create(user.Id, missedDate));

        var (title, body) = BuildNotification(user.CurrentStreak, user.Language ?? "en");
        dbContext.Notifications.Add(Notification.Create(user.Id, title, body, StreakUrl));
        await pushService.SendToUserAsync(user.Id, title, body, StreakUrl, ct);

        if (logger.IsEnabled(LogLevel.Information))
            LogFreezeActivated(logger, user.Id, missedDate);
        return true;
    }

    private const string StreakUrl = "/streak";

    internal static (string Title, string Body) BuildNotification(int currentStreak, string lang)
    {
        var isPt = LocaleHelper.IsPortuguese(lang);
        return isPt
            ? ("Sequência protegida", $"Usamos um congelamento para manter sua sequência de {currentStreak} dias depois de um dia sem registro.")
            : ("Streak protected", $"We used a freeze to keep your {currentStreak}-day streak alive after a day with no check-ins.");
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "StreakFreezeAutoActivationService started")]
    private static partial void LogServiceStarted(ILogger logger);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "StreakFreezeAutoActivationService stopped")]
    private static partial void LogServiceStopped(ILogger logger);

    [LoggerMessage(EventId = 3, Level = LogLevel.Error, Message = "Error in streak freeze auto-activation")]
    private static partial void LogServiceError(ILogger logger, Exception ex);

    [LoggerMessage(EventId = 4, Level = LogLevel.Information, Message = "Auto-activated streak freeze for user {UserId} on {FrozenDate}")]
    private static partial void LogFreezeActivated(ILogger logger, Guid userId, DateOnly frozenDate);
}
