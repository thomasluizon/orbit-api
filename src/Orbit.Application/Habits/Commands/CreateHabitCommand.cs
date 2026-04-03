using MediatR;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Habits.Commands;

public record CreateHabitCommand(
    Guid UserId,
    string Title,
    string? Description,
    FrequencyUnit? FrequencyUnit,
    int? FrequencyQuantity,
    bool IsBadHabit = false,
    IReadOnlyList<string>? SubHabits = null,
    DateOnly? DueDate = null,
    bool IsGeneral = false,
    HabitCommandOptions? Options = null,
    IReadOnlyList<Guid>? TagIds = null,
    IReadOnlyList<Guid>? GoalIds = null) : IRequest<Result<Guid>>;

public partial class CreateHabitCommandHandler(
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
        var opts = request.Options ?? new HabitCommandOptions();

        var habitResult = Habit.Create(new HabitCreateParams(
            request.UserId,
            request.Title,
            request.FrequencyUnit,
            request.FrequencyQuantity,
            request.Description,
            Days: opts.Days,
            IsBadHabit: request.IsBadHabit,
            DueDate: dueDate,
            DueTime: opts.DueTime,
            DueEndTime: opts.DueEndTime,
            ReminderEnabled: opts.ReminderEnabled,
            ReminderTimes: opts.ReminderTimes,
            SlipAlertEnabled: opts.SlipAlertEnabled,
            ChecklistItems: opts.ChecklistItems,
            IsGeneral: request.IsGeneral,
            IsFlexible: opts.IsFlexible,
            ScheduledReminders: opts.ScheduledReminders));

        if (habitResult.IsFailure)
            return Result.Failure<Guid>(habitResult.Error);

        var habit = habitResult.Value;

        if (request.SubHabits is { Count: > 0 })
        {
            foreach (var subTitle in request.SubHabits)
            {
                var childResult = Habit.Create(new HabitCreateParams(
                    request.UserId,
                    subTitle,
                    request.FrequencyUnit,
                    request.FrequencyQuantity,
                    DueDate: request.DueDate ?? dueDate,
                    ParentHabitId: habit.Id,
                    IsGeneral: request.IsGeneral,
                    EndDate: opts.EndDate));

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
            LogGamificationHabitCreationFailed(logger, ex, request.UserId);
        }

        CacheInvalidationHelper.InvalidateSummaryCache(cache, request.UserId);

        return Result.Success(habit.Id);
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "Gamification processing failed for habit creation by user {UserId}")]
    private static partial void LogGamificationHabitCreationFailed(ILogger logger, Exception ex, Guid userId);
}
