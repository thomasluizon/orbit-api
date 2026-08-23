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
/// Temporarily preserves automatic streak freezes while clients adopt the person initiated repair flow.
/// </summary>
public partial class StreakFreezeAutoActivationService(
    IServiceScopeFactory scopeFactory,
    ILogger<StreakFreezeAutoActivationService> logger,
    IConfiguration configuration,
    TimeProvider timeProvider) : ScheduledServiceBase, IScheduledJob
{
    private const int MaxTimeZoneSkewDays = 1;
    private const string StreakUrl = "/streak";

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

    internal async Task ActivateMissedDayFreezes(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<OrbitDbContext>();
        var pushService = scope.ServiceProvider.GetRequiredService<IPushNotificationService>();
        var userDateService = scope.ServiceProvider.GetRequiredService<IUserDateService>();

        var utcToday = DateOnly.FromDateTime(timeProvider.GetUtcNow().UtcDateTime);
        var utcYesterday = DateOnly.FromDayNumber(utcToday.DayNumber - 1);
        var candidates = await dbContext.Users
            .Where(user => user.CurrentStreak > 0
                && user.StreakFreezesAccumulated > 0
                && user.LastActiveDate != null
                && user.LastActiveDate < utcYesterday)
            .ToListAsync(cancellationToken);

        if (candidates.Count == 0)
            return;

        var gamificationFreeTierEnabled = await dbContext.AppFeatureFlags
            .AsNoTracking()
            .AnyAsync(
                flag => flag.Key == FeatureFlagKeys.GamificationFreeTier && flag.Enabled,
                cancellationToken);

        var candidateIds = candidates.Select(user => user.Id).ToList();
        var earliestMissed = utcYesterday.AddDays(-MaxTimeZoneSkewDays);
        var monthFloor = new DateOnly(earliestMissed.Year, earliestMissed.Month, 1);
        var freezesByUser = (await dbContext.StreakFreezes
            .Where(freeze => candidateIds.Contains(freeze.UserId) && freeze.UsedOnDate >= monthFloor)
            .ToListAsync(cancellationToken))
            .GroupBy(freeze => freeze.UserId)
            .ToDictionary(group => group.Key, group => group.ToList());
        var completionsByUser = await LoadRecentCompletionsAsync(
            dbContext,
            candidateIds,
            monthFloor,
            cancellationToken);

        var staged = new List<StagedFreeze>(candidates.Count);
        foreach (var user in candidates)
        {
            var stagedFreeze = await StageFreezeAsync(
                user,
                gamificationFreeTierEnabled,
                freezesByUser,
                completionsByUser,
                dbContext,
                userDateService,
                cancellationToken);
            if (stagedFreeze is not null)
                staged.Add(stagedFreeze);
        }

        if (staged.Count == 0)
            return;

        if (await TrySaveBatchAsync(dbContext, cancellationToken))
        {
            await NotifyActivatedAsync(staged, pushService, cancellationToken);
            return;
        }

        dbContext.ChangeTracker.Clear();
        await ActivatePerUserFallbackAsync(
            candidateIds,
            gamificationFreeTierEnabled,
            new PerUserStreakLookups(freezesByUser, completionsByUser),
            pushService,
            userDateService,
            dbContext,
            cancellationToken);
    }

    private sealed record StagedFreeze(User User, DateOnly MissedDate, string Title, string Body);

    private async Task<StagedFreeze?> StageFreezeAsync(
        User user,
        bool gamificationFreeTierEnabled,
        Dictionary<Guid, List<StreakFreeze>> freezesByUser,
        Dictionary<Guid, HashSet<DateOnly>> completionsByUser,
        OrbitDbContext dbContext,
        IUserDateService userDateService,
        CancellationToken cancellationToken)
    {
        if (!user.HasProAccess && !gamificationFreeTierEnabled)
            return null;

        var userToday = await userDateService.GetUserTodayAsync(user.Id, cancellationToken);
        var missedDate = DateOnly.FromDayNumber(userToday.DayNumber - 1);
        if (user.LastActiveDate is null || user.LastActiveDate >= missedDate)
            return null;

        var existingFreezes = freezesByUser.GetValueOrDefault(user.Id) ?? [];
        if (existingFreezes.Any(freeze => freeze.UsedOnDate == missedDate))
            return null;

        var completions = completionsByUser.GetValueOrDefault(user.Id) ?? [];
        if (completions.Contains(missedDate))
            return null;

        var monthStart = new DateOnly(missedDate.Year, missedDate.Month, 1);
        var monthEnd = monthStart.AddMonths(1);
        var freezesThisMonth = existingFreezes.Count(
            freeze => freeze.UsedOnDate >= monthStart && freeze.UsedOnDate < monthEnd);
        if (freezesThisMonth >= AppConstants.MaxStreakFreezesPerMonth)
            return null;

        var consume = user.ConsumeStreakFreeze();
        if (consume.IsFailure)
            return null;

        dbContext.StreakFreezes.Add(StreakFreeze.Create(user.Id, missedDate));

        var (title, body) = BuildNotification(user.CurrentStreak, user.Language ?? "en");
        dbContext.Notifications.Add(Notification.Create(user.Id, title, body, StreakUrl));

        return new StagedFreeze(user, missedDate, title, body);
    }

    private static async Task<bool> TrySaveBatchAsync(
        OrbitDbContext dbContext,
        CancellationToken cancellationToken)
    {
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            return false;
        }
        catch (DbUpdateException exception) when (DbUniqueViolation.IsUniqueViolation(exception))
        {
            return false;
        }
    }

    private async Task NotifyActivatedAsync(
        List<StagedFreeze> staged,
        IPushNotificationService pushService,
        CancellationToken cancellationToken)
    {
        foreach (var freeze in staged)
            await NotifyFreezeActivatedAsync(freeze, pushService, cancellationToken);
    }

    private async Task NotifyFreezeActivatedAsync(
        StagedFreeze freeze,
        IPushNotificationService pushService,
        CancellationToken cancellationToken)
    {
        try
        {
            await pushService.SendToUserAsync(
                freeze.User.Id,
                freeze.Title,
                freeze.Body,
                StreakUrl,
                cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogFreezePushFailed(logger, freeze.User.Id, exception);
        }

        if (logger.IsEnabled(LogLevel.Information))
            LogFreezeActivated(logger, freeze.User.Id, freeze.MissedDate);
    }

    private sealed record PerUserStreakLookups(
        Dictionary<Guid, List<StreakFreeze>> Freezes,
        Dictionary<Guid, HashSet<DateOnly>> Completions);

    private async Task ActivatePerUserFallbackAsync(
        List<Guid> candidateIds,
        bool gamificationFreeTierEnabled,
        PerUserStreakLookups lookups,
        IPushNotificationService pushService,
        IUserDateService userDateService,
        OrbitDbContext dbContext,
        CancellationToken cancellationToken)
    {
        var users = await dbContext.Users
            .Where(user => candidateIds.Contains(user.Id))
            .ToListAsync(cancellationToken);

        foreach (var user in users)
        {
            var staged = await StageFreezeAsync(
                user,
                gamificationFreeTierEnabled,
                lookups.Freezes,
                lookups.Completions,
                dbContext,
                userDateService,
                cancellationToken);
            if (staged is null)
                continue;

            if (!await TrySaveUserFreezeAsync(user.Id, dbContext, cancellationToken))
                continue;

            await NotifyFreezeActivatedAsync(staged, pushService, cancellationToken);
        }
    }

    private static async Task<Dictionary<Guid, HashSet<DateOnly>>> LoadRecentCompletionsAsync(
        OrbitDbContext dbContext,
        List<Guid> userIds,
        DateOnly since,
        CancellationToken cancellationToken)
    {
        var habitOwners = await dbContext.Habits
            .Where(habit => userIds.Contains(habit.UserId) && !habit.IsDeleted && !habit.IsBadHabit)
            .Select(habit => new { habit.Id, habit.UserId })
            .ToListAsync(cancellationToken);

        var ownerByHabit = habitOwners.ToDictionary(habit => habit.Id, habit => habit.UserId);
        var habitIds = habitOwners.Select(habit => habit.Id).ToList();
        if (habitIds.Count == 0)
            return new Dictionary<Guid, HashSet<DateOnly>>();

        var logs = await dbContext.HabitLogs
            .Where(log => habitIds.Contains(log.HabitId) && log.Value > 0 && log.Date >= since)
            .Select(log => new { log.HabitId, log.Date })
            .ToListAsync(cancellationToken);

        var completions = new Dictionary<Guid, HashSet<DateOnly>>();
        foreach (var log in logs)
        {
            if (!ownerByHabit.TryGetValue(log.HabitId, out var ownerId))
                continue;
            if (!completions.TryGetValue(ownerId, out var dates))
            {
                dates = [];
                completions[ownerId] = dates;
            }
            dates.Add(log.Date);
        }
        return completions;
    }

    private async Task<bool> TrySaveUserFreezeAsync(
        Guid userId,
        OrbitDbContext dbContext,
        CancellationToken cancellationToken)
    {
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            DiscardPendingChanges(dbContext);
            if (logger.IsEnabled(LogLevel.Debug))
                LogFreezeConflictSkipped(logger, userId);
            return false;
        }
        catch (DbUpdateException exception) when (DbUniqueViolation.IsUniqueViolation(exception))
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

    internal static (string Title, string Body) BuildNotification(int currentStreak, string language)
    {
        var isPortuguese = LocaleHelper.IsPortuguese(language);
        return isPortuguese
            ? ("Sequência protegida", $"Usamos um congelamento para manter sua sequência de {currentStreak} dias depois de um dia sem registro.")
            : ("Streak protected", $"We used a freeze to keep your {currentStreak}-day streak alive after a day with no check-ins.");
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "StreakFreezeAutoActivationService started")]
    private static partial void LogServiceStarted(ILogger logger);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "StreakFreezeAutoActivationService stopped")]
    private static partial void LogServiceStopped(ILogger logger);

    [LoggerMessage(EventId = 3, Level = LogLevel.Error, Message = "Error in streak freeze auto-activation")]
    private static partial void LogServiceError(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 4, Level = LogLevel.Information, Message = "Auto-activated streak freeze for user {UserId} on {FrozenDate}")]
    private static partial void LogFreezeActivated(ILogger logger, Guid userId, DateOnly frozenDate);

    [LoggerMessage(EventId = 5, Level = LogLevel.Debug, Message = "Streak freeze already activated for user {UserId}; skipping")]
    private static partial void LogFreezeAlreadyActivated(ILogger logger, Guid userId);

    [LoggerMessage(EventId = 6, Level = LogLevel.Debug, Message = "Streak freeze skipped for user {UserId} due to a concurrent update; will re-evaluate next run")]
    private static partial void LogFreezeConflictSkipped(ILogger logger, Guid userId);

    [LoggerMessage(EventId = 7, Level = LogLevel.Warning, Message = "Failed to deliver streak-freeze push for user {UserId}; freeze already persisted")]
    private static partial void LogFreezePushFailed(ILogger logger, Guid userId, Exception exception);
}
