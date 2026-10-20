using MediatR;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Domain.ValueObjects;

namespace Orbit.Application.Habits.Commands;

public sealed record BulkHabitChanges(
    bool HasTitle = false,
    string? Title = null,
    bool HasDescription = false,
    string? Description = null,
    bool HasEmoji = false,
    string? Emoji = null,
    bool HasFrequencyUnit = false,
    FrequencyUnit? FrequencyUnit = null,
    bool HasFrequencyQuantity = false,
    int? FrequencyQuantity = null,
    bool HasIntervalWeeks = false,
    int? IntervalWeeks = null,
    bool HasDays = false,
    IReadOnlyList<DayOfWeek>? Days = null,
    bool HasDueDate = false,
    DateOnly? DueDate = null,
    bool HasEndDate = false,
    DateOnly? EndDate = null,
    bool HasDueTime = false,
    TimeOnly? DueTime = null,
    bool HasIsBadHabit = false,
    bool IsBadHabit = false,
    bool HasIsFlexible = false,
    bool IsFlexible = false,
    bool HasReminderEnabled = false,
    bool ReminderEnabled = false,
    bool HasReminderTimes = false,
    IReadOnlyList<int>? ReminderTimes = null,
    bool HasChecklistItems = false,
    IReadOnlyList<ChecklistItem>? ChecklistItems = null,
    bool HasScheduledReminders = false,
    IReadOnlyList<ScheduledReminderTime>? ScheduledReminders = null,
    bool HasDueEndTime = false,
    TimeOnly? DueEndTime = null,
    bool HasIsGeneral = false,
    bool IsGeneral = false,
    bool HasSlipAlertEnabled = false,
    bool SlipAlertEnabled = false,
    bool HasRelativeReminders = false,
    IReadOnlyList<RelativeReminderTime>? RelativeReminders = null)
{
    public bool HasAnyChange =>
        HasTitle || HasDescription || HasEmoji || HasFrequencyUnit || HasFrequencyQuantity
        || HasIntervalWeeks || HasDays || HasDueDate || HasEndDate || HasDueTime
        || HasIsBadHabit || HasIsFlexible || HasReminderEnabled || HasReminderTimes
        || HasChecklistItems || HasScheduledReminders || HasDueEndTime || HasIsGeneral
        || HasSlipAlertEnabled || HasRelativeReminders;
}

public sealed record BulkUpdateHabitsCommand(
    Guid UserId,
    BulkHabitFilter Filter,
    BulkHabitChanges Changes) : IRequest<Result<BulkHabitMutationResult>>;

public sealed record BulkHabitMutationResult(
    int AppliedCount,
    int TotalMatched,
    int SkippedCount,
    bool Partial);

public sealed partial class BulkUpdateHabitsCommandHandler(
    IGenericRepository<Habit> habitRepository,
    IUserDateService userDateService,
    IPayGateService payGate,
    IUnitOfWork unitOfWork,
    IMemoryCache cache,
    ILogger<BulkUpdateHabitsCommandHandler> logger) : IRequestHandler<BulkUpdateHabitsCommand, Result<BulkHabitMutationResult>>
{
    internal const int ChunkSize = 100;

    public async Task<Result<BulkHabitMutationResult>> Handle(
        BulkUpdateHabitsCommand request,
        CancellationToken cancellationToken)
    {
        var selectedHabits = await BulkHabitSelection.LoadAsync(
            habitRepository,
            request.UserId,
            request.Filter,
            cancellationToken);
        var selectedIds = selectedHabits.Select(habit => habit.Id).ToList();
        var totalMatched = selectedIds.Count;
        var appliedCount = 0;
        var stopped = false;
        var today = await userDateService.GetUserTodayAsync(request.UserId, cancellationToken);

        foreach (var chunk in selectedIds.Chunk(ChunkSize))
        {
            Result<int> chunkResult;
            try
            {
                chunkResult = await HabitCeilingLock.ExecuteAsync(
                    unitOfWork,
                    request.UserId,
                    transactionToken => ApplyChunkAsync(
                        request.UserId,
                        chunk,
                        request.Changes,
                        today,
                        transactionToken),
                    cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                unitOfWork.DiscardChanges();
                LogChunkFailed(logger, appliedCount, totalMatched, ex);
                stopped = true;
                break;
            }

            if (chunkResult.IsFailure)
            {
                if (appliedCount == 0)
                    return chunkResult.PropagateError<BulkHabitMutationResult>();
                stopped = true;
                break;
            }

            appliedCount += chunkResult.Value;
        }

        if (appliedCount > 0)
            CacheInvalidationHelper.InvalidateUserAiCaches(cache, request.UserId, today);

        var skippedCount = totalMatched - appliedCount;
        return Result.Success(new BulkHabitMutationResult(
            appliedCount,
            totalMatched,
            skippedCount,
            stopped || skippedCount > 0));
    }

    internal static async Task<Result> CheckArgumentsAsync(BulkUpdateHabitsCommand request,
        IGenericRepository<Habit> repository, IUserDateService dates, CancellationToken ct)
    {
        var habits = await BulkHabitSelection.LoadAsync(repository, request.UserId, request.Filter, ct);
        var ownership = OwnershipValidation.AllResolved(request.Filter.HabitIds, habits, h => h.Id, ErrorMessages.HabitNotFound);
        if (ownership.IsFailure)
            return ownership;
        if (habits.Count == 0)
            return Result.Failure("No matching habits found.");
        var today = await dates.GetUserTodayAsync(request.UserId, ct);
        var updates = habits.Select(habit => (Habit: habit, Update: HabitUpdateValidation.ResolveUpdate(habit, request.Changes, today))).ToList();
        return await ValidateUpdatesAsync(request.UserId, updates, repository, ct);
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "Bulk habit update chunk failed after {AppliedCount} of {TotalMatched} matches")]
    private static partial void LogChunkFailed(ILogger logger, int appliedCount, int totalMatched, Exception ex);

    private async Task<Result<int>> ApplyChunkAsync(
        Guid userId,
        IReadOnlyCollection<Guid> habitIds,
        BulkHabitChanges changes,
        DateOnly today,
        CancellationToken cancellationToken)
    {
        var habits = await habitRepository.FindTrackedAsync(
            habit => habit.UserId == userId && habitIds.Contains(habit.Id),
            query => query,
            cancellationToken);
        foreach (var habit in habits)
            await habitRepository.ReloadAsync(habit, cancellationToken);
        var updates = habits
            .Select(habit => (Habit: habit, Update: HabitUpdateValidation.ResolveUpdate(habit, changes, today)))
            .ToList();

        var validation = await ValidateUpdatesAsync(userId, updates, habitRepository, cancellationToken);
        if (validation.IsFailure)
            return validation.PropagateError<int>();

        if (updates.Any(item => item.Update.SlipAlertEnabled == true && !item.Habit.SlipAlertEnabled))
        {
            var slipGate = await payGate.CanUseSlipAlerts(userId, cancellationToken);
            if (slipGate.IsFailure)
                return slipGate.PropagateError<int>();
        }

        var liveRootEntries = updates.Count(item => HabitLiveRootEntry.FromUpdate(item.Habit, item.Update));
        if (liveRootEntries > 0)
        {
            var allowance = await payGate.CanCreateHabits(userId, liveRootEntries, cancellationToken);
            if (allowance.IsFailure)
                return allowance.PropagateError<int>();
        }

        foreach (var item in updates)
            item.Habit.Update(item.Update);

        if (updates.Count > 0)
            await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Success(updates.Count);
    }

    private static async Task<Result> ValidateUpdatesAsync(Guid userId,
        IReadOnlyList<(Habit Habit, HabitUpdateParams Update)> updates, IGenericRepository<Habit> repository,
        CancellationToken ct)
    {
        var related = updates.Any(item => item.Update.IsGeneral.HasValue)
            ? await HabitUpdateValidation.LoadRelatedAsync(updates.Select(item => item.Habit).ToList(), userId, repository, ct)
            : [];
        foreach (var (habit, update) in updates)
        {
            var validation = await HabitUpdateValidation.ValidateAsync(userId, habit, update, repository, ct, related);
            if (validation.IsFailure)
                return validation;
            validation = habit.ValidateUpdate(update);
            if (validation.IsFailure)
                return validation;
        }
        return Result.Success();
    }


}
