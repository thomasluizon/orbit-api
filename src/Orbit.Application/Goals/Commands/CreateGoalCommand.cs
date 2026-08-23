using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Orbit.Application.Common;
using Orbit.Application.Goals.Services;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Goals.Commands;

public record CreateGoalCommand(
    Guid UserId,
    string Title,
    string? Description,
    decimal TargetValue,
    string Unit,
    DateOnly? Deadline,
    int Position = 0,
    GoalType Type = GoalType.Standard,
    IReadOnlyList<Guid>? HabitIds = null) : IRequest<Result<Guid>>, IIdempotentCommand;

public partial class CreateGoalCommandHandler(
    IGenericRepository<Goal> goalRepository,
    IGenericRepository<Habit> habitRepository,
    IUserDateService userDateService,
    IGamificationService gamificationService,
    IGoalCompletionService goalCompletionService,
    IUnitOfWork unitOfWork,
    IMemoryCache cache,
    ILogger<CreateGoalCommandHandler> logger) : IRequestHandler<CreateGoalCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(CreateGoalCommand request, CancellationToken cancellationToken)
    {
        var today = await userDateService.GetUserTodayAsync(request.UserId, cancellationToken);
        if (request.Deadline is { } deadline && deadline < today)
            return Result.Failure<Guid>(ErrorMessages.DeadlineInPast);

        var goalResult = Goal.Create(new Goal.CreateGoalParams(
            request.UserId,
            request.Title,
            request.TargetValue,
            request.Unit,
            request.Description,
            request.Deadline,
            request.Position,
            request.Type));

        if (goalResult.IsFailure)
            return goalResult.PropagateError<Guid>();

        var goal = goalResult.Value;
        var hasLinkedHabits = false;

        if (request.HabitIds is { Count: > 0 } habitIds)
        {
            if (habitIds.Count > AppConstants.MaxHabitsPerGoal)
                return Result.Failure<Guid>(ErrorMessages.MaxHabitsPerGoal.Format(AppConstants.MaxHabitsPerGoal));

            var habits = await habitRepository.FindTrackedAsync(
                h => habitIds.Contains(h.Id) && h.UserId == request.UserId,
                q => q.Include(h => h.Logs),
                cancellationToken);

            var habitsResolved = OwnershipValidation.AllResolved(habitIds, habits, h => h.Id, ErrorMessages.HabitNotFound);
            if (habitsResolved.IsFailure)
                return habitsResolved.PropagateError<Guid>();

            foreach (var habit in habits)
                goal.AddHabit(habit);

            hasLinkedHabits = true;
        }

        await goalRepository.AddAsync(goal, cancellationToken);
        var goalId = goal.Id;
        if (hasLinkedHabits)
        {
            await goalCompletionService.SyncDerivedGoalsAsync(
                request.UserId,
                [goalId],
                today,
                cancellationToken: cancellationToken);
        }
        else
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

        try
        {
            await gamificationService.ProcessGoalCreated(request.UserId, cancellationToken);
        }
        catch (Exception ex)
        {
            LogGamificationGoalCreationFailed(logger, ex, request.UserId);
        }

        CacheInvalidationHelper.InvalidateUserAiCaches(cache, request.UserId, today);

        return Result.Success(goalId);
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "Gamification processing failed for goal creation by user {UserId}")]
    private static partial void LogGamificationGoalCreationFailed(ILogger logger, Exception ex, Guid userId);

}
