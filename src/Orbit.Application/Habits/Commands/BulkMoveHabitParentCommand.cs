using MediatR;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Caching.Memory;
using Orbit.Application.Common;
using Orbit.Application.Habits.Services;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Habits.Commands;

public record BulkMoveHabitParentItem(Guid HabitId) : IBulkHabitItem;

public record BulkMoveHabitParentCommand(
    Guid UserId,
    IReadOnlyList<Guid> HabitIds,
    Guid? ParentId) : IRequest<Result<BulkMoveHabitParentResult>>, IBulkHabitCommand<BulkMoveHabitParentItem>
{
    IReadOnlyList<BulkMoveHabitParentItem> IBulkHabitCommand<BulkMoveHabitParentItem>.Items =>
        HabitIds?.Select(id => new BulkMoveHabitParentItem(id)).ToList() ?? [];
}

public record BulkMoveHabitParentResult(IReadOnlyList<BulkMoveHabitParentItemResult> Results);

public record BulkMoveHabitParentItemResult(
    int Index,
    BulkItemStatus Status,
    Guid HabitId,
    string? Error = null,
    string? ErrorCode = null)
{
    [JsonIgnore]
    public IReadOnlyList<object?> ErrorArgs { get; init; } = Array.Empty<object?>();
}

public class BulkMoveHabitParentCommandHandler(
    IGenericRepository<Habit> habitRepository,
    IAppConfigService appConfigService,
    IUserStreakService userStreakService,
    IUnitOfWork unitOfWork,
    IUserDateService userDateService,
    IMemoryCache cache) : IRequestHandler<BulkMoveHabitParentCommand, Result<BulkMoveHabitParentResult>>
{
    public async Task<Result<BulkMoveHabitParentResult>> Handle(
        BulkMoveHabitParentCommand request, CancellationToken cancellationToken)
    {
        var results = new List<BulkMoveHabitParentItemResult>();
        await HabitCeilingLock.ExecuteAsync(unitOfWork, request.UserId, async ct =>
        {
            results.Clear();
            var habits = await habitRepository.FindTrackedAsync(h => h.UserId == request.UserId, ct);
            var habitsById = habits.ToDictionary(h => h.Id);
            var parentsById = habits.ToDictionary(h => h.Id, h => h.ParentHabitId);
            var errors = new Dictionary<Guid, AppError>();
            var candidates = new HashSet<Guid>();
            var parent = request.ParentId is { } parentId ? habitsById.GetValueOrDefault(parentId) : null;

            foreach (var id in request.HabitIds.Distinct())
            {
                if (!habitsById.TryGetValue(id, out var habit))
                    errors[id] = ErrorMessages.HabitNotFound;
                else if (id == request.ParentId)
                    errors[id] = ErrorMessages.SelfParent;
                else if (request.ParentId is not null && parent is null)
                    errors[id] = ErrorMessages.TargetParentNotFound;
                else if (parent is not null && (parent.Schedule == HabitSchedule.General) != (habit.Schedule == HabitSchedule.General))
                    errors[id] = ErrorMessages.GeneralMismatchWithParent;
                else
                {
                    candidates.Add(id);
                    parentsById[id] = request.ParentId;
                }
            }

            if (request.ParentId is { } targetId)
            {
                bool cycleRejected;
                do
                {
                    cycleRejected = false;
                    foreach (var id in candidates.ToList())
                    {
                        if (!HabitHierarchy.WouldCreateCycle(id, targetId, parentsById))
                            continue;
                        Reject(id, ErrorMessages.CircularReference);
                        cycleRejected = true;
                    }
                } while (cycleRejected);

                var maxDepth = await appConfigService.GetAsync(
                    AppConfigKeys.MaxHabitDepth, AppConstants.MaxHabitDepth, ct);
                bool rejected;
                do
                {
                    rejected = false;
                    foreach (var id in candidates.ToList())
                    {
                        if (HabitHierarchy.GetDepth(id, parentsById)
                            + HabitHierarchy.GetSubtreeHeight(id, parentsById) <= maxDepth - 1)
                            continue;
                        Reject(id, ErrorMessages.MaxDepthReached.Format(maxDepth));
                        rejected = true;
                    }
                } while (rejected);
            }
            else
            {
                var maxHabits = await appConfigService.GetAsync(
                    AppConfigKeys.FreeMaxHabits, AppConstants.DefaultFreeMaxHabits, ct);
                var rootCount = habits.Count(h => h.ParentHabitId is null && !h.IsCompleted);
                foreach (var id in candidates.ToList())
                {
                    if (!HabitLiveRootEntry.FromPromotion(habitsById[id], null))
                        continue;
                    if (rootCount >= maxHabits)
                        Reject(id, ErrorMessages.HabitLimitReached.Format(maxHabits));
                    else
                        rootCount++;
                }
            }

            for (var index = 0; index < request.HabitIds.Count; index++)
            {
                var id = request.HabitIds[index];
                if (errors.TryGetValue(id, out var error))
                    results.Add(new(index, BulkItemStatus.Failed, id, error.Message, error.Code) { ErrorArgs = error.Args });
                else
                {
                    habitsById[id].SetParentHabitId(request.ParentId);
                    results.Add(new(index, BulkItemStatus.Success, id));
                }
            }

            await unitOfWork.SaveChangesAsync(ct);
            if (results.Any(r => r.Status == BulkItemStatus.Success))
                await ConcurrencyRetry.SaveWithRetryAsync(unitOfWork,
                    c => userStreakService.RecalculateAsync(request.UserId, cancellationToken: c), ct);

            void Reject(Guid id, AppError error)
            {
                candidates.Remove(id);
                errors[id] = error;
                parentsById[id] = habitsById[id].ParentHabitId;
            }
        }, cancellationToken);

        var today = await userDateService.GetUserTodayAsync(request.UserId, cancellationToken);
        CacheInvalidationHelper.InvalidateUserAiCaches(cache, request.UserId, today);
        return Result.Success(new BulkMoveHabitParentResult(results));
    }
}
