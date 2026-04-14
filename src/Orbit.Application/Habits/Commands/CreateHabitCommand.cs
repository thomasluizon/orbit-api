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

/// <summary>
/// Groups repository dependencies for habit creation to reduce constructor parameter count (S107).
/// </summary>
public record CreateHabitRepositories(
    IGenericRepository<Habit> HabitRepository,
    IGenericRepository<Tag> TagRepository,
    IGenericRepository<Goal> GoalRepository);

public partial class CreateHabitCommandHandler(
    CreateHabitRepositories repos,
    IUserDateService userDateService,
    IPayGateService payGate,
    IGamificationService gamificationService,
    IUnitOfWork unitOfWork,
    IMemoryCache cache,
    ILogger<CreateHabitCommandHandler> logger) : IRequestHandler<CreateHabitCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(CreateHabitCommand request, CancellationToken cancellationToken)
    {
        var opts = request.Options ?? new HabitCommandOptions();

        var gateCheck = await payGate.CanCreateHabits(request.UserId, 1, cancellationToken);
        if (gateCheck.IsFailure)
            return gateCheck.PropagateError<Guid>();

        if (request.SubHabits is { Count: > 0 })
        {
            var subGateCheck = await payGate.CanCreateSubHabits(request.UserId, cancellationToken);
            if (subGateCheck.IsFailure)
                return subGateCheck.PropagateError<Guid>();
        }

        if (request.GoalIds is { Count: > 0 })
        {
            var goalLinkGate = await payGate.CanLinkGoalsToHabits(request.UserId, cancellationToken);
            if (goalLinkGate.IsFailure)
                return goalLinkGate.PropagateError<Guid>();
        }

        if (opts.SlipAlertEnabled)
        {
            var slipAlertGate = await payGate.CanUseSlipAlerts(request.UserId, cancellationToken);
            if (slipAlertGate.IsFailure)
                return slipAlertGate.PropagateError<Guid>();
        }

        var dueDate = request.DueDate ?? await userDateService.GetUserTodayAsync(request.UserId, cancellationToken);

        // If specific days are set, advance DueDate to the next matching day
        if (opts.Days is { Count: > 0 } && !opts.Days.Contains(dueDate.DayOfWeek))
        {
            while (!opts.Days.Contains(dueDate.DayOfWeek))
                dueDate = dueDate.AddDays(1);
        }

        // Assign explicit Position = (max sibling root position) + 1 so new habits never enter as NULL.
        var siblings = await repos.HabitRepository.FindAsync(
            h => h.UserId == request.UserId && h.ParentHabitId == null && !h.IsDeleted,
            cancellationToken);
        var nextPosition = siblings.Count == 0
            ? 0
            : siblings.Max(h => h.Position ?? -1) + 1;

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
            ScheduledReminders: opts.ScheduledReminders,
            Position: nextPosition));

        if (habitResult.IsFailure)
            return Result.Failure<Guid>(habitResult.Error);

        var habit = habitResult.Value;

        var subResult = await CreateSubHabitsAsync(request, habit.Id, dueDate, opts, cancellationToken);
        if (subResult.IsFailure)
            return Result.Failure<Guid>(subResult.Error);

        await LinkTagsAndGoalsAsync(habit, request.UserId, request.TagIds, request.GoalIds, cancellationToken);

        await repos.HabitRepository.AddAsync(habit, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        await ProcessGamificationSafeAsync(request.UserId, cancellationToken);

        CacheInvalidationHelper.InvalidateSummaryCache(cache, request.UserId);

        return Result.Success(habit.Id);
    }

    private async Task<Result> CreateSubHabitsAsync(
        CreateHabitCommand request, Guid parentId, DateOnly dueDate,
        HabitCommandOptions opts, CancellationToken cancellationToken)
    {
        if (request.SubHabits is not { Count: > 0 })
            return Result.Success();

        var subPosition = 0;
        foreach (var subTitle in request.SubHabits)
        {
            var childResult = Habit.Create(new HabitCreateParams(
                request.UserId,
                subTitle,
                request.FrequencyUnit,
                request.FrequencyQuantity,
                DueDate: request.DueDate ?? dueDate,
                ParentHabitId: parentId,
                IsGeneral: request.IsGeneral,
                EndDate: opts.EndDate,
                Position: subPosition++));

            if (childResult.IsFailure)
                return Result.Failure(childResult.Error);

            await repos.HabitRepository.AddAsync(childResult.Value, cancellationToken);
        }

        return Result.Success();
    }

    private async Task LinkTagsAndGoalsAsync(
        Habit habit, Guid userId, IReadOnlyList<Guid>? tagIds,
        IReadOnlyList<Guid>? goalIds, CancellationToken cancellationToken)
    {
        if (tagIds is { Count: > 0 })
        {
            var tags = await repos.TagRepository.FindTrackedAsync(
                t => tagIds.Contains(t.Id) && t.UserId == userId,
                cancellationToken);
            foreach (var tag in tags)
                habit.AddTag(tag);
        }

        if (goalIds is { Count: > 0 })
        {
            var goals = await repos.GoalRepository.FindTrackedAsync(
                g => goalIds.Contains(g.Id) && g.UserId == userId,
                cancellationToken);
            foreach (var goal in goals)
                habit.AddGoal(goal);
        }
    }

    private async Task ProcessGamificationSafeAsync(Guid userId, CancellationToken cancellationToken)
    {
        try
        {
            await gamificationService.ProcessHabitCreated(userId, cancellationToken);
        }
        catch (Exception ex)
        {
            LogGamificationHabitCreationFailed(logger, ex, userId);
        }
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "Gamification processing failed for habit creation by user {UserId}")]
    private static partial void LogGamificationHabitCreationFailed(ILogger logger, Exception ex, Guid userId);
}
