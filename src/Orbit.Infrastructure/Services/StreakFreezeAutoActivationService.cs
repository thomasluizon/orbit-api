using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orbit.Application.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.BackgroundJobs;
using Orbit.Infrastructure.Persistence;
using Orbit.Infrastructure.Services.Hosting;

namespace Orbit.Infrastructure.Services;

/// <summary>
/// Auto-activates a streak freeze for an eligible user who held an active streak but logged
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
    IConfiguration configuration) : ScheduledServiceBase, IScheduledJob
{
    private const int MaxTimeZoneSkewDays = 1;

    private readonly TimeSpan _interval = TimeSpan.FromMinutes(
        configuration.GetValue("BackgroundServices:StreakFreezeIntervalMinutes", 60));

    public string Name => "streak-freeze-auto-activation";

    public string CronExpression => "0 * * * *";

    public Task RunAsync(CancellationToken cancellationToken) => ExecuteTickAsync(cancellationToken);

    protected override TimeSpan Interval => _interval;

    protected override async Task ExecuteTickAsync(CancellationToken stoppingToken)
    {
        await ActivateMissedDayFreezes(stoppingToken);
        BackgroundServiceHealthCheck.RecordTick("StreakFreezeAutoActivation");
    }

    protected override void LogStarted() => LogServiceStarted(logger);

    protected override void LogStopped() => LogServiceStopped(logger);

    protected override void LogTickError(Exception ex) => LogServiceError(logger, ex);

    internal async Task ActivateMissedDayFreezes(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<OrbitDbContext>();
        var pushService = scope.ServiceProvider.GetRequiredService<IPushNotificationService>();

        var utcYesterday = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-1));
        var candidates = await dbContext.Users
            .Where(u => u.CurrentStreak > 0
                && u.StreakFreezesAccumulated > 0
                && u.LastActiveDate != null
                && u.LastActiveDate < utcYesterday)
            .ToListAsync(ct);

        if (candidates.Count == 0) return;

        var gamificationFreeTierEnabled = await dbContext.AppFeatureFlags
            .AsNoTracking()
            .AnyAsync(f => f.Key == FeatureFlagKeys.GamificationFreeTier && f.Enabled, ct);

        var candidateIds = candidates.Select(u => u.Id).ToList();

        var earliestMissed = utcYesterday.AddDays(-MaxTimeZoneSkewDays);
        var monthFloor = new DateOnly(earliestMissed.Year, earliestMissed.Month, 1);
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

        var staged = new List<StagedFreeze>(candidates.Count);
        foreach (var user in candidates)
        {
            var stagedFreeze = StageFreeze(user, gamificationFreeTierEnabled, freezesByUser, guardedByUser, completionsByUser, dbContext);
            if (stagedFreeze is not null)
                staged.Add(stagedFreeze);
        }

        if (staged.Count == 0) return;

        if (await TrySaveBatchAsync(dbContext, ct))
        {
            await NotifyActivatedAsync(staged, pushService, ct);
            return;
        }

        dbContext.ChangeTracker.Clear();
        await ActivatePerUserFallbackAsync(
            candidateIds, gamificationFreeTierEnabled, freezesByUser, guardedByUser, completionsByUser, pushService, dbContext, ct);
    }

    private sealed record StagedFreeze(User User, DateOnly MissedDate, string Title, string Body);

    private StagedFreeze? StageFreeze(
        User user,
        bool gamificationFreeTierEnabled,
        Dictionary<Guid, List<StreakFreeze>> freezesByUser,
        Dictionary<Guid, HashSet<DateOnly>> guardedByUser,
        Dictionary<Guid, HashSet<DateOnly>> completionsByUser,
        OrbitDbContext dbContext)
    {
        if (!user.HasProAccess && !gamificationFreeTierEnabled) return null;

        var tz = TimeZoneHelper.FindTimeZone(user.TimeZone, logger, user.Id);
        var userToday = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, tz));
        var missedDate = userToday.AddDays(-1);

        if (user.LastActiveDate is null || user.LastActiveDate >= missedDate) return null;

        var existingFreezes = freezesByUser.GetValueOrDefault(user.Id) ?? [];
        if (existingFreezes.Any(f => f.UsedOnDate == missedDate)) return null;

        var guardedDates = guardedByUser.GetValueOrDefault(user.Id) ?? [];
        if (guardedDates.Contains(missedDate)) return null;

        var completions = completionsByUser.GetValueOrDefault(user.Id) ?? [];
        if (completions.Contains(missedDate)) return null;

        var monthStart = new DateOnly(missedDate.Year, missedDate.Month, 1);
        var monthEnd = monthStart.AddMonths(1);
        var freezesThisMonth = existingFreezes.Count(f => f.UsedOnDate >= monthStart && f.UsedOnDate < monthEnd);
        if (freezesThisMonth >= AppConstants.MaxStreakFreezesPerMonth) return null;

        var consume = user.ConsumeStreakFreeze();
        if (consume.IsFailure) return null;

        dbContext.StreakFreezes.Add(StreakFreeze.Create(user.Id, missedDate));
        dbContext.SentStreakFreezeAlerts.Add(SentStreakFreezeAlert.Create(user.Id, missedDate));

        var (title, body) = BuildNotification(user.CurrentStreak, user.Language ?? "en");
        dbContext.Notifications.Add(Notification.Create(user.Id, title, body, StreakUrl));

        return new StagedFreeze(user, missedDate, title, body);
    }

    private async Task<bool> TrySaveBatchAsync(OrbitDbContext dbContext, CancellationToken ct)
    {
        try
        {
            await dbContext.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            return false;
        }
        catch (DbUpdateException ex) when (DbUniqueViolation.IsUniqueViolation(ex))
        {
            return false;
        }
    }

    private async Task NotifyActivatedAsync(
        List<StagedFreeze> staged, IPushNotificationService pushService, CancellationToken ct)
    {
        foreach (var freeze in staged)
            await NotifyFreezeActivatedAsync(freeze, pushService, ct);
    }

    private async Task NotifyFreezeActivatedAsync(
        StagedFreeze freeze, IPushNotificationService pushService, CancellationToken ct)
    {
        try
        {
            await pushService.SendToUserAsync(freeze.User.Id, freeze.Title, freeze.Body, StreakUrl, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogFreezePushFailed(logger, freeze.User.Id, ex);
        }

        if (logger.IsEnabled(LogLevel.Information))
            LogFreezeActivated(logger, freeze.User.Id, freeze.MissedDate);
    }

    private async Task ActivatePerUserFallbackAsync(
        List<Guid> candidateIds,
        bool gamificationFreeTierEnabled,
        Dictionary<Guid, List<StreakFreeze>> freezesByUser,
        Dictionary<Guid, HashSet<DateOnly>> guardedByUser,
        Dictionary<Guid, HashSet<DateOnly>> completionsByUser,
        IPushNotificationService pushService,
        OrbitDbContext dbContext,
        CancellationToken ct)
    {
        var users = await dbContext.Users
            .Where(u => candidateIds.Contains(u.Id))
            .ToListAsync(ct);

        foreach (var user in users)
        {
            var staged = StageFreeze(user, gamificationFreeTierEnabled, freezesByUser, guardedByUser, completionsByUser, dbContext);
            if (staged is null) continue;

            if (!await TrySaveUserFreezeAsync(user.Id, dbContext, ct))
                continue;

            await NotifyFreezeActivatedAsync(staged, pushService, ct);
        }
    }

    private static async Task<Dictionary<Guid, HashSet<DateOnly>>> LoadRecentCompletionsAsync(
        OrbitDbContext dbContext, List<Guid> userIds, DateOnly since, CancellationToken ct)
    {
        var habitOwners = await dbContext.Habits
            .Where(h => userIds.Contains(h.UserId) && !h.IsDeleted && !h.IsBadHabit)
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

    private async Task<bool> TrySaveUserFreezeAsync(Guid userId, OrbitDbContext dbContext, CancellationToken ct)
    {
        try
        {
            await dbContext.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            DiscardPendingChanges(dbContext);
            if (logger.IsEnabled(LogLevel.Debug))
                LogFreezeConflictSkipped(logger, userId);
            return false;
        }
        catch (DbUpdateException ex) when (DbUniqueViolation.IsUniqueViolation(ex))
        {
            DiscardPendingChanges(dbContext);
            if (logger.IsEnabled(LogLevel.Debug))
                LogFreezeAlreadyActivated(logger, userId);
            return false;
        }
    }

    private static void DiscardPendingChanges(OrbitDbContext dbContext)
    {
        foreach (var entry in dbContext.ChangeTracker.Entries().ToList())
        {
            entry.State = entry.State switch
            {
                EntityState.Added => EntityState.Detached,
                EntityState.Modified or EntityState.Deleted => EntityState.Unchanged,
                _ => entry.State
            };
        }
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

    [LoggerMessage(EventId = 5, Level = LogLevel.Debug, Message = "Streak freeze already activated for user {UserId}; skipping")]
    private static partial void LogFreezeAlreadyActivated(ILogger logger, Guid userId);

    [LoggerMessage(EventId = 6, Level = LogLevel.Debug, Message = "Streak freeze skipped for user {UserId} due to a concurrent update; will re-evaluate next run")]
    private static partial void LogFreezeConflictSkipped(ILogger logger, Guid userId);

    [LoggerMessage(EventId = 7, Level = LogLevel.Warning, Message = "Failed to deliver streak-freeze push for user {UserId}; freeze already persisted")]
    private static partial void LogFreezePushFailed(ILogger logger, Guid userId, Exception ex);
}
