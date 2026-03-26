using MediatR;
using Microsoft.Extensions.Caching.Memory;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;

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
    bool IsFlexible = false);

public record BulkCreateResult(IReadOnlyList<BulkCreateItemResult> Results);

public record BulkCreateItemResult(
    int Index,
    BulkItemStatus Status,
    Guid? HabitId = null,
    string? Title = null,
    string? Error = null,
    string? Field = null);

public enum BulkItemStatus { Success, Failed }

public class BulkCreateHabitsCommandHandler(
    IGenericRepository<Habit> habitRepository,
    IPayGateService payGate,
    IUserDateService userDateService,
    IUnitOfWork unitOfWork,
    IMemoryCache cache) : IRequestHandler<BulkCreateHabitsCommand, Result<BulkCreateResult>>
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
                var item = request.Habits[i];

                try
                {
                    // Create parent habit
                    var habitResult = Habit.Create(
                        request.UserId,
                        item.Title,
                        item.FrequencyUnit,
                        item.FrequencyQuantity,
                        item.Description,
                        item.Days,
                        item.IsBadHabit,
                        item.DueDate ?? userToday,
                        dueTime: item.DueTime,
                        dueEndTime: item.DueEndTime,
                        reminderEnabled: item.ReminderEnabled,
                        reminderTimes: item.ReminderTimes,
                        isGeneral: item.IsGeneral,
                        isFlexible: item.IsFlexible);

                    if (habitResult.IsFailure)
                    {
                        results.Add(new BulkCreateItemResult(
                            Index: i,
                            Status: BulkItemStatus.Failed,
                            Title: item.Title,
                            Error: habitResult.Error,
                            Field: DetermineFieldFromError(habitResult.Error)));
                        continue;
                    }

                    var parentHabit = habitResult.Value;

                    // Explicitly add parent habit
                    await habitRepository.AddAsync(parentHabit, cancellationToken);

                    // Create child habits if any
                    var subHabitFailed = false;
                    if (item.SubHabits is { Count: > 0 })
                    {
                        foreach (var subItem in item.SubHabits)
                        {
                            // Sub-habits inherit parent frequency/dueDate when not specified
                            var childResult = Habit.Create(
                                request.UserId,
                                subItem.Title,
                                subItem.FrequencyUnit ?? item.FrequencyUnit,
                                subItem.FrequencyQuantity ?? item.FrequencyQuantity,
                                subItem.Description,
                                subItem.Days ?? item.Days,
                                subItem.IsBadHabit,
                                subItem.DueDate ?? item.DueDate ?? userToday,
                                parentHabitId: parentHabit.Id,
                                isGeneral: item.IsGeneral,
                                isFlexible: item.IsFlexible);

                            if (childResult.IsFailure)
                            {
                                // Sub-habit creation failed - remove parent from tracking
                                habitRepository.Remove(parentHabit);
                                results.Add(new BulkCreateItemResult(
                                    Index: i,
                                    Status: BulkItemStatus.Failed,
                                    Title: item.Title,
                                    Error: $"Sub-habit '{subItem.Title}' failed: {childResult.Error}",
                                    Field: "SubHabits"));
                                subHabitFailed = true;
                                break;
                            }

                            // Explicitly add child habit
                            await habitRepository.AddAsync(childResult.Value, cancellationToken);
                        }
                    }

                    if (subHabitFailed) continue;

                    // Success
                    results.Add(new BulkCreateItemResult(
                        Index: i,
                        Status: BulkItemStatus.Success,
                        HabitId: parentHabit.Id,
                        Title: parentHabit.Title));
                }
                catch (Exception ex)
                {
                    results.Add(new BulkCreateItemResult(
                        Index: i,
                        Status: BulkItemStatus.Failed,
                        Title: item.Title,
                        Error: ex.Message));
                }
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
}
