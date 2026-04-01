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

public record CreateHabitCommand(
    Guid UserId,
    string Title,
    string? Description,
    FrequencyUnit? FrequencyUnit,
    int? FrequencyQuantity,
    IReadOnlyList<System.DayOfWeek>? Days = null,
    bool IsBadHabit = false,
    IReadOnlyList<string>? SubHabits = null,
    DateOnly? DueDate = null,
    TimeOnly? DueTime = null,
    TimeOnly? DueEndTime = null,
    bool ReminderEnabled = false,
    IReadOnlyList<int>? ReminderTimes = null,
    bool SlipAlertEnabled = false,
    IReadOnlyList<Guid>? TagIds = null,
    IReadOnlyList<ChecklistItem>? ChecklistItems = null,
    bool IsGeneral = false,
    DateOnly? EndDate = null,
    bool IsFlexible = false,
    IReadOnlyList<Guid>? GoalIds = null,
    IReadOnlyList<ScheduledReminderTime>? ScheduledReminders = null) : IRequest<Result<Guid>>;

public class CreateHabitCommandHandler(
    IGenericRepository<Habit> habitRepository,
    IGenericRepository<Tag> tagRepository,
    IGenericRepository<Goal> goalRepository,
    IUserDateService userDateService,
    IPayGateService payGate,
    IGamificationService gamificationService,
    IUnitOfWork unitOfWork,
    IMemoryCache cache,
    ILogger<CreateHabitCommandHandler> logger) : IRequestHandler<CreateHabitCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(CreateHabitCommand request, CancellationToken cancellationToken)
    {
        // Check habit limit
        var gateCheck = await payGate.CanCreateHabits(request.UserId, 1, cancellationToken);
        if (gateCheck.IsFailure)
            return gateCheck.PropagateError<Guid>();

        // Check sub-habit access if creating with sub-habits
        if (request.SubHabits is { Count: > 0 })
        {
            var subGateCheck = await payGate.CanCreateSubHabits(request.UserId, cancellationToken);
            if (subGateCheck.IsFailure)
                return subGateCheck.PropagateError<Guid>();
        }

        var dueDate = request.DueDate ?? await userDateService.GetUserTodayAsync(request.UserId, cancellationToken);

        var habitResult = Habit.Create(
            request.UserId,
            request.Title,
            request.FrequencyUnit,
            request.FrequencyQuantity,
            request.Description,
            request.Days,
            request.IsBadHabit,
            dueDate,
            dueTime: request.DueTime,
            dueEndTime: request.DueEndTime,
            reminderEnabled: request.ReminderEnabled,
            reminderTimes: request.ReminderTimes,
            slipAlertEnabled: request.SlipAlertEnabled,
            checklistItems: request.ChecklistItems,
            isGeneral: request.IsGeneral,
            isFlexible: request.IsFlexible,
            scheduledReminders: request.ScheduledReminders);

        if (habitResult.IsFailure)
            return Result.Failure<Guid>(habitResult.Error);

        var habit = habitResult.Value;

        if (request.SubHabits is { Count: > 0 })
        {
            foreach (var subTitle in request.SubHabits)
            {
                var childResult = Habit.Create(
                    request.UserId,
                    subTitle,
                    request.FrequencyUnit,
                    request.FrequencyQuantity,
                    dueDate: request.DueDate ?? dueDate,
                    parentHabitId: habit.Id,
                    isGeneral: request.IsGeneral,
                    endDate: request.EndDate);

                if (childResult.IsFailure)
                    return Result.Failure<Guid>(childResult.Error);

                await habitRepository.AddAsync(childResult.Value, cancellationToken);
            }
        }

        if (request.TagIds is { Count: > 0 })
        {
            var tags = await tagRepository.FindTrackedAsync(
                t => request.TagIds.Contains(t.Id) && t.UserId == request.UserId,
                cancellationToken);
            foreach (var tag in tags)
                habit.AddTag(tag);
        }

        if (request.GoalIds is { Count: > 0 })
        {
            var goals = await goalRepository.FindTrackedAsync(
                g => request.GoalIds.Contains(g.Id) && g.UserId == request.UserId,
                cancellationToken);
            foreach (var goal in goals)
                habit.AddGoal(goal);
        }

        await habitRepository.AddAsync(habit, cancellationToken);

        await unitOfWork.SaveChangesAsync(cancellationToken);

        // Gamification: process habit creation
        try
        {
            await gamificationService.ProcessHabitCreated(request.UserId, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Gamification processing failed for habit creation by user {UserId}", request.UserId);
        }

        CacheInvalidationHelper.InvalidateSummaryCache(cache, request.UserId);

        return Result.Success(habit.Id);
    }
}
