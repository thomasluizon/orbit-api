using MediatR;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Orbit.Application.Common;
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
    IPayGateService payGate,
    IUserDateService userDateService,
    IGamificationService gamificationService,
    IUnitOfWork unitOfWork,
    IMemoryCache cache,
    ILogger<CreateGoalCommandHandler> logger) : IRequestHandler<CreateGoalCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(CreateGoalCommand request, CancellationToken cancellationToken)
    {
        var gateCheck = await payGate.CanAccessGoals(request.UserId, cancellationToken);
        if (gateCheck.IsFailure)
            return gateCheck.PropagateError<Guid>();

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

        if (request.HabitIds is { Count: > 0 } habitIds)
        {
            if (habitIds.Count > AppConstants.MaxHabitsPerGoal)
                return Result.Failure<Guid>(ErrorMessages.MaxHabitsPerGoal.Format(AppConstants.MaxHabitsPerGoal));

            var habits = await habitRepository.FindTrackedAsync(
                h => habitIds.Contains(h.Id) && h.UserId == request.UserId,
                cancellationToken);

            var habitsResolved = OwnershipValidation.AllResolved(habitIds, habits, h => h.Id, ErrorMessages.HabitNotFound);
            if (habitsResolved.IsFailure)
                return habitsResolved.PropagateError<Guid>();

            foreach (var habit in habits)
                goal.AddHabit(habit);
        }

        await goalRepository.AddAsync(goal, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        try
        {
            await gamificationService.ProcessGoalCreated(request.UserId, cancellationToken);
        }
        catch (Exception ex)
        {
            LogGamificationGoalCreationFailed(logger, ex, request.UserId);
        }

        CacheInvalidationHelper.InvalidateUserAiCaches(cache, request.UserId, today);

        return Result.Success(goal.Id);
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "Gamification processing failed for goal creation by user {UserId}")]
    private static partial void LogGamificationGoalCreationFailed(ILogger logger, Exception ex, Guid userId);
}
