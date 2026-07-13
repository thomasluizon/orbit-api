using MediatR;
using Microsoft.EntityFrameworkCore;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Habits.Commands;

public record MoveHabitParentCommand(
    Guid UserId,
    Guid HabitId,
    Guid? ParentId) : IRequest<Result>;

public class MoveHabitParentCommandHandler(
    IGenericRepository<Habit> habitRepository,
    IUnitOfWork unitOfWork,
    IAppConfigService appConfigService) : IRequestHandler<MoveHabitParentCommand, Result>
{
    public async Task<Result> Handle(MoveHabitParentCommand request, CancellationToken cancellationToken)
    {
        var habit = await habitRepository.FindOneTrackedAsync(
            h => h.Id == request.HabitId && h.UserId == request.UserId,
            q => q.Include(h => h.Children),
            cancellationToken);

        if (habit is null)
            return Result.Failure(ErrorMessages.HabitNotFound);

        if (request.ParentId is null)
        {
            habit.SetParentHabitId(null);
            await unitOfWork.SaveChangesAsync(cancellationToken);
            return Result.Success();
        }

        if (request.ParentId == request.HabitId)
            return Result.Failure(ErrorMessages.SelfParent);

        var parent = await habitRepository.FindOneTrackedAsync(
            h => h.Id == request.ParentId && h.UserId == request.UserId,
            cancellationToken: cancellationToken);

        if (parent is null)
            return Result.Failure(ErrorMessages.TargetParentNotFound);

        var allHabits = await habitRepository.FindAsync(h => h.UserId == request.UserId, cancellationToken);
        var habitsById = allHabits.ToDictionary(h => h.Id);

        if (WouldCreateCycle(request.HabitId, request.ParentId.Value, habitsById))
            return Result.Failure(ErrorMessages.CircularReference);

        var maxDepth = await appConfigService.GetAsync(AppConfigKeys.MaxHabitDepth, AppConstants.MaxHabitDepth, cancellationToken);
        var parentDepth = GetDepth(parent.Id, habitsById);
        var subtreeHeight = GetSubtreeHeight(request.HabitId, habitsById);
        if (parentDepth + 1 + subtreeHeight > maxDepth - 1)
            return Result.Failure(ErrorMessages.MaxDepthReached.Format(maxDepth));

        habit.SetParentHabitId(request.ParentId);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }

    private static bool WouldCreateCycle(Guid habitId, Guid targetParentId, Dictionary<Guid, Habit> habitsById)
    {
        var currentId = targetParentId;
        while (habitsById.TryGetValue(currentId, out var current))
        {
            if (current.ParentHabitId is null) return false;
            if (current.ParentHabitId == habitId) return true;
            currentId = current.ParentHabitId.Value;
        }
        return false;
    }

    private static int GetDepth(Guid habitId, Dictionary<Guid, Habit> habitsById)
    {
        var depth = 0;
        var currentId = habitsById.TryGetValue(habitId, out var habit) ? habit.ParentHabitId : null;
        while (currentId is not null && habitsById.TryGetValue(currentId.Value, out var parent))
        {
            depth++;
            currentId = parent.ParentHabitId;
        }
        return depth;
    }

    private static int GetSubtreeHeight(Guid habitId, IReadOnlyDictionary<Guid, Habit> habitsById)
    {
        var childrenByParent = habitsById.Values
            .Where(h => h.ParentHabitId is not null)
            .GroupBy(h => h.ParentHabitId!.Value)
            .ToDictionary(g => g.Key, g => g.Select(h => h.Id).ToList());

        return MeasureHeight(habitId, childrenByParent);
    }

    private static int MeasureHeight(Guid habitId, IReadOnlyDictionary<Guid, List<Guid>> childrenByParent)
    {
        if (!childrenByParent.TryGetValue(habitId, out var children) || children.Count == 0)
            return 0;

        return 1 + children.Max(childId => MeasureHeight(childId, childrenByParent));
    }
}
