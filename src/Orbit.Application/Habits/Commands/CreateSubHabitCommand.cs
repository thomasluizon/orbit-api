using MediatR;
using Microsoft.Extensions.Caching.Memory;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Habits.Commands;

public record CreateSubHabitCommand(
    Guid UserId,
    Guid ParentHabitId,
    string Title,
    string? Description,
    FrequencyUnit? FrequencyUnit = null,
    int? FrequencyQuantity = null,
    bool IsBadHabit = false,
    DateOnly? DueDate = null,
    HabitCommandOptions? Options = null,
    IReadOnlyList<Guid>? TagIds = null,
    string? Icon = null) : IRequest<Result<Guid>>;

public class CreateSubHabitCommandHandler(
    IGenericRepository<Habit> habitRepository,
    IGenericRepository<Tag> tagRepository,
    IPayGateService payGate,
    IUserDateService userDateService,
    IUnitOfWork unitOfWork,
    IAppConfigService appConfigService,
    IMemoryCache cache) : IRequestHandler<CreateSubHabitCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(CreateSubHabitCommand request, CancellationToken cancellationToken)
    {
        // Sub-habits are a Pro feature
        var gateCheck = await payGate.CanCreateSubHabits(request.UserId, cancellationToken);
        if (gateCheck.IsFailure)
            return gateCheck.PropagateError<Guid>();

        var parent = await habitRepository.FindOneTrackedAsync(
            h => h.Id == request.ParentHabitId && h.UserId == request.UserId,
            cancellationToken: cancellationToken);

        if (parent is null)
            return Result.Failure<Guid>(ErrorMessages.ParentHabitNotFound, ErrorCodes.ParentHabitNotFound);

        // Enforce max nesting depth from config
        var maxDepth = await appConfigService.GetAsync(AppConfigKeys.MaxHabitDepth, AppConstants.MaxHabitDepth, cancellationToken);
        var depth = await GetDepthAsync(parent, habitRepository, cancellationToken);
        if (depth >= maxDepth - 1)
            return Result.Failure<Guid>($"Maximum nesting depth reached ({maxDepth} levels).");

        // Use explicit DueDate if provided, otherwise derive from parent
        var userToday = await userDateService.GetUserTodayAsync(request.UserId, cancellationToken);
        var childDueDate = request.DueDate
            ?? (parent.DueDate > userToday ? parent.DueDate : userToday);

        var opts = request.Options ?? new HabitCommandOptions();

        // Compute next position within this parent's children.
        var siblings = await habitRepository.FindAsync(
            h => h.UserId == request.UserId && h.ParentHabitId == request.ParentHabitId && !h.IsDeleted,
            cancellationToken);
        var nextPosition = siblings.Count == 0
            ? 0
            : siblings.Max(h => h.Position ?? -1) + 1;

        var childResult = Habit.Create(new HabitCreateParams(
            request.UserId,
            request.Title,
            request.FrequencyUnit ?? parent.FrequencyUnit,
            request.FrequencyQuantity ?? parent.FrequencyQuantity,
            request.Description,
            Days: opts.Days,
            IsBadHabit: request.IsBadHabit,
            DueDate: childDueDate,
            DueTime: opts.DueTime,
            DueEndTime: opts.DueEndTime,
            ParentHabitId: parent.Id,
            ReminderEnabled: opts.ReminderEnabled,
            ReminderTimes: opts.ReminderTimes,
            SlipAlertEnabled: opts.SlipAlertEnabled,
            ChecklistItems: opts.ChecklistItems,
            IsGeneral: parent.IsGeneral,
            IsFlexible: opts.IsFlexible,
            EndDate: opts.EndDate,
            ScheduledReminders: opts.ScheduledReminders,
            Position: nextPosition,
            Icon: request.Icon));

        if (childResult.IsFailure)
            return Result.Failure<Guid>(childResult.Error);

        var child = childResult.Value;

        if (request.TagIds is { Count: > 0 })
        {
            var tags = await tagRepository.FindTrackedAsync(
                t => request.TagIds.Contains(t.Id) && t.UserId == request.UserId,
                cancellationToken);
            foreach (var tag in tags)
                child.AddTag(tag);
        }

        await habitRepository.AddAsync(child, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        CacheInvalidationHelper.InvalidateSummaryCache(cache, request.UserId);

        return Result.Success(childResult.Value.Id);
    }

    private static async Task<int> GetDepthAsync(Habit habit, IGenericRepository<Habit> repo, CancellationToken ct)
    {
        if (habit.ParentHabitId is null) return 0;

        // Load all user habits once and walk in memory instead of N+1 queries
        var allHabits = await repo.FindAsync(h => h.UserId == habit.UserId, ct);
        var habitDict = allHabits.ToDictionary(h => h.Id);

        var depth = 0;
        var currentId = habit.ParentHabitId;
        while (currentId is not null && habitDict.TryGetValue(currentId.Value, out var parent))
        {
            depth++;
            currentId = parent.ParentHabitId;
        }
        return depth;
    }

}
