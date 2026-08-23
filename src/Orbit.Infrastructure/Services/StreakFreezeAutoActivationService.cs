using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orbit.Application.Notifications;
using Orbit.Application.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.BackgroundJobs;
using Orbit.Infrastructure.Persistence;
using Orbit.Infrastructure.Services.Hosting;

namespace Orbit.Infrastructure.Services;

/// <summary>
/// Permanently provides automatic streak coverage. Its configuration flag is an operational kill switch.
/// </summary>
public partial class StreakFreezeAutoActivationService(
    IServiceScopeFactory scopeFactory,
    ILogger<StreakFreezeAutoActivationService> logger,
    IConfiguration configuration) : ScheduledServiceBase, IScheduledJob
{
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

        var candidates = await dbContext.Users
            .Where(user => user.CurrentStreak > 0
                && user.StreakFreezesAccumulated > 0)
            .ToListAsync(cancellationToken);

        if (candidates.Count == 0)
            return;

        var gamificationFreeTierEnabled = await dbContext.AppFeatureFlags
            .AsNoTracking()
            .AnyAsync(
                flag => flag.Key == FeatureFlagKeys.GamificationFreeTier && flag.Enabled,
                cancellationToken);

        var repairBatch = await LoadRepairBatchAsync(
            candidates,
            dbContext,
            userDateService,
            cancellationToken);
        var staged = new List<StagedFreeze>(candidates.Count);
        foreach (var user in candidates)
        {
            var stagedFreeze = StageFreeze(
                user,
                gamificationFreeTierEnabled,
                dbContext,
                repairBatch);
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
            staged.Select(freeze => freeze.User.Id).ToList(),
            gamificationFreeTierEnabled,
            pushService,
            dbContext,
            userDateService,
            cancellationToken);
    }

    private sealed record StagedFreeze(User User, DateOnly MissedDate, string Title, string Body);

    private sealed record RepairBatch(
        IReadOnlyDictionary<Guid, DateOnly> UserTodayById,
        IReadOnlyDictionary<Guid, List<Habit>> EligibleHabitsByUser,
        IReadOnlyDictionary<Guid, HashSet<DateOnly>> CompletionDatesByUser,
        IReadOnlyDictionary<Guid, HashSet<DateOnly>> FreezeDatesByUser);

    private static StagedFreeze? StageFreeze(
        User user,
        bool gamificationFreeTierEnabled,
        OrbitDbContext dbContext,
        RepairBatch repairBatch)
    {
        if (!user.HasProAccess && !gamificationFreeTierEnabled)
            return null;

        var userToday = repairBatch.UserTodayById[user.Id];
        var missedDate = DateOnly.FromDayNumber(userToday.DayNumber - 1);
        var repair = UserStreakService.EvaluateRepair(
            user,
            userToday,
            missedDate,
            repairBatch.EligibleHabitsByUser.GetValueOrDefault(user.Id) ?? [],
            repairBatch.CompletionDatesByUser.GetValueOrDefault(user.Id) ?? [],
            repairBatch.FreezeDatesByUser.GetValueOrDefault(user.Id) ?? []);
        if (!repair.IsAvailable)
            return null;

        var consume = user.ConsumeStreakFreeze();
        if (consume.IsFailure)
            return null;

        dbContext.StreakFreezes.Add(StreakFreeze.Create(user.Id, missedDate));

        var (title, body) = BuildNotification(user.CurrentStreak, user.Language ?? "en");
        dbContext.Notifications.Add(Notification.Create(user.Id, title, body, NotificationUrls.Progress));

        return new StagedFreeze(user, missedDate, title, body);
    }

    private static async Task<RepairBatch> LoadRepairBatchAsync(
        List<User> candidates,
        OrbitDbContext dbContext,
        IUserDateService userDateService,
        CancellationToken cancellationToken)
    {
        var userTodayById = new Dictionary<Guid, DateOnly>(candidates.Count);
        foreach (var user in candidates)
        {
            userTodayById[user.Id] = await userDateService.GetUserTodayAsync(
                user.TimeZone,
                user.Id,
                cancellationToken);
        }

        var candidateIds = candidates.Select(user => user.Id).ToList();
        var lookbackStart = userTodayById.Values
            .Min()
            .AddDays(-AppConstants.MaxStreakLookbackDays);
        var eligibleHabits = await dbContext.Habits
            .AsNoTracking()
            .Where(habit => candidateIds.Contains(habit.UserId)
                && !habit.IsDeleted
                && !habit.IsBadHabit)
            .ToListAsync(cancellationToken);
        var eligibleHabitsByUser = eligibleHabits
            .GroupBy(habit => habit.UserId)
            .ToDictionary(group => group.Key, group => group.ToList());

        var ownerByHabitId = eligibleHabits.ToDictionary(habit => habit.Id, habit => habit.UserId);
        var eligibleHabitIds = ownerByHabitId.Keys.ToList();
        var logs = eligibleHabitIds.Count == 0
            ? []
            : await dbContext.HabitLogs
                .AsNoTracking()
                .Where(log => eligibleHabitIds.Contains(log.HabitId)
                    && log.Value > 0
                    && log.Date >= lookbackStart)
                .ToListAsync(cancellationToken);
        var completionDatesByUser = new Dictionary<Guid, HashSet<DateOnly>>();
        foreach (var log in logs)
        {
            var userId = ownerByHabitId[log.HabitId];
            if (!completionDatesByUser.TryGetValue(userId, out var dates))
            {
                dates = [];
                completionDatesByUser[userId] = dates;
            }
            dates.Add(log.Date);
        }

        var freezes = await dbContext.StreakFreezes
            .AsNoTracking()
            .Where(freeze => candidateIds.Contains(freeze.UserId)
                && freeze.UsedOnDate >= lookbackStart)
            .ToListAsync(cancellationToken);
        var freezeDatesByUser = freezes
            .GroupBy(freeze => freeze.UserId)
            .ToDictionary(
                group => group.Key,
                group => group.Select(freeze => freeze.UsedOnDate).ToHashSet());

        return new RepairBatch(
            userTodayById,
            eligibleHabitsByUser,
            completionDatesByUser,
            freezeDatesByUser);
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
                NotificationUrls.Progress,
                cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogFreezePushFailed(logger, freeze.User.Id, exception);
        }

        if (logger.IsEnabled(LogLevel.Information))
            LogFreezeActivated(logger, freeze.User.Id, freeze.MissedDate);
    }

    private async Task ActivatePerUserFallbackAsync(
        List<Guid> candidateIds,
        bool gamificationFreeTierEnabled,
        IPushNotificationService pushService,
        OrbitDbContext dbContext,
        IUserDateService userDateService,
        CancellationToken cancellationToken)
    {
        var users = await dbContext.Users
            .Where(user => candidateIds.Contains(user.Id))
            .ToListAsync(cancellationToken);
        var repairBatch = await LoadRepairBatchAsync(
            users,
            dbContext,
            userDateService,
            cancellationToken);

        foreach (var user in users)
        {
            var staged = StageFreeze(
                user,
                gamificationFreeTierEnabled,
                dbContext,
                repairBatch);
            if (staged is null)
                continue;

            if (!await TrySaveUserFreezeAsync(user.Id, dbContext, cancellationToken))
                continue;

            await NotifyFreezeActivatedAsync(staged, pushService, cancellationToken);
        }
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
