using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Orbit.Application.Common;
using Orbit.Application.Goals.Services;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Habits.Commands;

public record LinkGoalsToHabitCommand(
    Guid UserId,
    Guid HabitId,
    IReadOnlyList<Guid> GoalIds) : IRequest<Result>;

public partial class LinkGoalsToHabitCommandHandler(
    IGenericRepository<Habit> habitRepository,
    IGenericRepository<Goal> goalRepository,
    IPayGateService payGate,
    IGamificationService gamificationService,
    IUserDateService userDateService,
    IUnitOfWork unitOfWork,
    ILogger<LinkGoalsToHabitCommandHandler> logger) : IRequestHandler<LinkGoalsToHabitCommand, Result>
{
    public async Task<Result> Handle(LinkGoalsToHabitCommand request, CancellationToken cancellationToken)
    {
        var gateCheck = await payGate.CanLinkGoalsToHabits(request.UserId, cancellationToken);
        if (gateCheck.IsFailure)
            return gateCheck;

        var habit = await habitRepository.FindOneTrackedAsync(
            h => h.Id == request.HabitId && h.UserId == request.UserId,
            q => q.Include(h => h.Goals),
            cancellationToken);

        if (habit is null)
            return Result.Failure(ErrorMessages.HabitNotFound);

        var affectedGoalIds = habit.Goals.Select(g => g.Id).Concat(request.GoalIds).ToHashSet();
        var goals = await goalRepository.FindTrackedAsync(
            g => affectedGoalIds.Contains(g.Id) && g.UserId == request.UserId,
            q => q.Include(g => g.Habits).ThenInclude(h => h.Logs),
            cancellationToken);

        var requestedGoals = goals.Where(g => request.GoalIds.Contains(g.Id)).ToList();
        var goalsResolved = OwnershipValidation.AllResolved(request.GoalIds, requestedGoals, g => g.Id, ErrorMessages.GoalNotFound);
        if (goalsResolved.IsFailure)
            return goalsResolved;

        foreach (var existing in habit.Goals.ToList())
            habit.RemoveGoal(existing);

        foreach (var goal in requestedGoals)
            habit.AddGoal(goal);

        var today = await userDateService.GetUserTodayAsync(request.UserId, cancellationToken);
        var anyJustCompleted = false;
        foreach (var goal in goals)
            anyJustCompleted |= GoalProgressSyncService.SyncCurrentProgress(goal, today).JustCompleted;
        await unitOfWork.SaveChangesAsync(cancellationToken);

        if (anyJustCompleted)
            await ProcessGoalCompletionSafeAsync(request.UserId, cancellationToken);

        return Result.Success();
    }

    private async Task ProcessGoalCompletionSafeAsync(Guid userId, CancellationToken cancellationToken)
    {
        try
        {
            await gamificationService.ProcessGoalCompleted(userId, cancellationToken);
        }
        catch (Exception ex)
        {
            LogGamificationGoalCompletionFailed(logger, ex, userId);
        }
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "Gamification processing failed for linked goal completion by user {UserId}")]
    private static partial void LogGamificationGoalCompletionFailed(ILogger logger, Exception ex, Guid userId);
}
