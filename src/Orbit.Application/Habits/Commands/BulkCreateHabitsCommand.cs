using MediatR;
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
    IReadOnlyList<BulkHabitItem>? SubHabits = null);

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
    IUnitOfWork unitOfWork) : IRequestHandler<BulkCreateHabitsCommand, Result<BulkCreateResult>>
{
    public async Task<Result<BulkCreateResult>> Handle(BulkCreateHabitsCommand request, CancellationToken cancellationToken)
    {
        var results = new List<BulkCreateItemResult>();

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
                    item.DueDate);

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
                if (item.SubHabits is { Count: > 0 })
                {
                    foreach (var subItem in item.SubHabits)
                    {
                        var childResult = Habit.Create(
                            request.UserId,
                            subItem.Title,
                            subItem.FrequencyUnit,
                            subItem.FrequencyQuantity,
                            subItem.Description,
                            subItem.Days,
                            subItem.IsBadHabit,
                            subItem.DueDate,
                            parentHabitId: parentHabit.Id);

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
                            goto NextItem; // Skip to next top-level item
                        }

                        // Explicitly add child habit
                        await habitRepository.AddAsync(childResult.Value, cancellationToken);
                    }
                }

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

            NextItem: ; // Label for goto
        }

        // Save all successful entities once
        await unitOfWork.SaveChangesAsync(cancellationToken);

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
