using MediatR;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Domain.ValueObjects;

namespace Orbit.Application.Habits.Commands;

public record BulkCreateHabitsCommand(
    Guid UserId,
    IReadOnlyList<BulkHabitItem> Habits) : IRequest<Result<BulkCreateResult>>;

public record BulkHabitItem(
    string Title,
    string? Description,
    FrequencyUnit? FrequencyUnit,
    int? FrequencyQuantity,
    IReadOnlyList<DayOfWeek>? Days = null,
    bool IsBadHabit = false,
    DateOnly? DueDate = null,
    TimeOnly? DueTime = null,
    TimeOnly? DueEndTime = null,
    bool ReminderEnabled = false,
    IReadOnlyList<int>? ReminderTimes = null,
    IReadOnlyList<BulkHabitItem>? SubHabits = null,
    bool IsGeneral = false,
    DateOnly? EndDate = null,
    bool IsFlexible = false,
    IReadOnlyList<ScheduledReminderTime>? ScheduledReminders = null,
    IReadOnlyList<ChecklistItem>? ChecklistItems = null);

public record BulkCreateResult(IReadOnlyList<BulkCreateItemResult> Results);

public record BulkCreateItemResult(
    int Index,
    BulkItemStatus Status,
    Guid? HabitId = null,
    string? Title = null,
    string? Error = null,
    string? Field = null);

public enum BulkItemStatus { Success, Failed }

public partial class BulkCreateHabitsCommandHandler(
    IGenericRepository<Habit> habitRepository,
    IPayGateService payGate,
    IUserDateService userDateService,
    IUnitOfWork unitOfWork,
    IMemoryCache cache,
    ILogger<BulkCreateHabitsCommandHandler> logger) : IRequestHandler<BulkCreateHabitsCommand, Result<BulkCreateResult>>
{
    public async Task<Result<BulkCreateResult>> Handle(BulkCreateHabitsCommand request, CancellationToken cancellationToken)
    {
        // Count total habits being created (parents only, sub-habits don't count toward limit)
        var parentCount = request.Habits.Count;
        var habitGate = await payGate.CanCreateHabits(request.UserId, parentCount, cancellationToken);
        if (habitGate.IsFailure)
            return habitGate.PropagateError<BulkCreateResult>();

        // Check sub-habit access if any items have sub-habits
        var hasSubHabits = request.Habits.Any(h => h.SubHabits is { Count: > 0 });
        if (hasSubHabits)
        {
            var subGate = await payGate.CanCreateSubHabits(request.UserId, cancellationToken);
            if (subGate.IsFailure)
                return subGate.PropagateError<BulkCreateResult>();
        }

        var userToday = await userDateService.GetUserTodayAsync(request.UserId, cancellationToken);
        var results = new List<BulkCreateItemResult>();

        // Use transaction so partial failures don't leave orphaned habits
        await unitOfWork.BeginTransactionAsync(cancellationToken);

        try
        {
            for (int i = 0; i < request.Habits.Count; i++)
            {
                var itemResult = await CreateSingleHabit(
                    request.UserId, request.Habits[i], i, userToday, cancellationToken);
                results.Add(itemResult);
            }

            // Save all successful entities and commit
            await unitOfWork.SaveChangesAsync(cancellationToken);
            await unitOfWork.CommitTransactionAsync(cancellationToken);
        }
        catch
        {
            await unitOfWork.RollbackTransactionAsync(cancellationToken);
            throw;
        }

        CacheInvalidationHelper.InvalidateSummaryCache(cache, request.UserId);

        return Result.Success(new BulkCreateResult(results));
    }

    private async Task<BulkCreateItemResult> CreateSingleHabit(
        Guid userId, BulkHabitItem item, int index, DateOnly userToday,
        CancellationToken cancellationToken)
    {
        try
        {
            var habitResult = Habit.Create(new HabitCreateParams(
                userId,
                item.Title,
                item.FrequencyUnit,
                item.FrequencyQuantity,
                item.Description,
                Days: item.Days,
                IsBadHabit: item.IsBadHabit,
                DueDate: item.DueDate ?? userToday,
                DueTime: item.DueTime,
                DueEndTime: item.DueEndTime,
                ReminderEnabled: item.ReminderEnabled,
                ReminderTimes: item.ReminderTimes,
                IsGeneral: item.IsGeneral,
                IsFlexible: item.IsFlexible,
                ScheduledReminders: item.ScheduledReminders,
                ChecklistItems: item.ChecklistItems));

            if (habitResult.IsFailure)
            {
                return new BulkCreateItemResult(
                    Index: index,
                    Status: BulkItemStatus.Failed,
                    Title: item.Title,
                    Error: habitResult.Error,
                    Field: DetermineFieldFromError(habitResult.Error));
            }

            var parentHabit = habitResult.Value;
            await habitRepository.AddAsync(parentHabit, cancellationToken);

            // Create child habits if any
            if (item.SubHabits is { Count: > 0 })
            {
                foreach (var subItem in item.SubHabits)
                {
                    var childResult = Habit.Create(new HabitCreateParams(
                        userId,
                        subItem.Title,
                        subItem.FrequencyUnit ?? item.FrequencyUnit,
                        subItem.FrequencyQuantity ?? item.FrequencyQuantity,
                        subItem.Description,
                        Days: subItem.Days ?? item.Days,
                        IsBadHabit: subItem.IsBadHabit,
                        DueDate: subItem.DueDate ?? item.DueDate ?? userToday,
                        ParentHabitId: parentHabit.Id,
                        IsGeneral: item.IsGeneral,
                        IsFlexible: subItem.IsFlexible));

                    if (childResult.IsFailure)
                    {
                        habitRepository.Remove(parentHabit);
                        return new BulkCreateItemResult(
                            Index: index,
                            Status: BulkItemStatus.Failed,
                            Title: item.Title,
                            Error: $"Sub-habit '{subItem.Title}' failed: {childResult.Error}",
                            Field: "SubHabits");
                    }

                    await habitRepository.AddAsync(childResult.Value, cancellationToken);
                }
            }

            return new BulkCreateItemResult(
                Index: index,
                Status: BulkItemStatus.Success,
                HabitId: parentHabit.Id,
                Title: parentHabit.Title);
        }
        catch (Exception ex)
        {
            LogBulkCreateItemFailed(logger, ex, index);
            return new BulkCreateItemResult(
                Index: index,
                Status: BulkItemStatus.Failed,
                Title: item.Title,
                Error: "An error occurred processing this item");
        }
    }

    private static string? DetermineFieldFromError(string error)
    {
        if (error.Contains("title", StringComparison.OrdinalIgnoreCase))
            return "Title";
        if (error.Contains("frequency", StringComparison.OrdinalIgnoreCase))
            return "FrequencyUnit";
        if (error.Contains("days", StringComparison.OrdinalIgnoreCase))
            return "Days";
        return null;
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "BulkCreate item {Index} failed")]
    private static partial void LogBulkCreateItemFailed(ILogger logger, Exception ex, int index);
}
