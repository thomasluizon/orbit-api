using MediatR;
using Microsoft.Extensions.Caching.Memory;
using Orbit.Application.Common;
using Orbit.Application.Goals.Services;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;

namespace Orbit.Application.Habits.Commands;

public record UpdateHabitCommand(
    Guid UserId,
    Guid HabitId,
    string Title,
    string? Description,
    FrequencyUnit? FrequencyUnit,
    int? FrequencyQuantity,
    bool IsBadHabit = false,
    DateOnly? DueDate = null,
    bool? IsGeneral = null,
    bool? ClearEndDate = null,
    UpdateHabitCommandOptions? Options = null,
    IReadOnlyList<Guid>? GoalIds = null,
    string? Emoji = null,
    int? IntervalWeeks = null) : IRequest<Result>;

public sealed record UpdateHabitFieldsCommand(
    Guid UserId,
    Guid HabitId,
    BulkHabitChanges Changes) : IRequest<Result<string>>;

public class UpdateHabitCommandHandler(
    IGenericRepository<Habit> habitRepository,
    IGenericRepository<SentReminder> sentReminderRepository,
    IGenericRepository<Goal> goalRepository,
    IPayGateService payGate,
    IGoalCompletionService goalCompletionService,
    IUserDateService userDateService,
    IUnitOfWork unitOfWork,
    IMemoryCache cache) : IRequestHandler<UpdateHabitCommand, Result>,
    IRequestHandler<UpdateHabitFieldsCommand, Result<string>>
{
    public async Task<Result> Handle(UpdateHabitCommand request, CancellationToken cancellationToken)
    {
        return await HabitCeilingLock.ExecuteEntryAsync(
            unitOfWork,
            request.UserId,
            payGate,
            ct => PrepareUpdateAsync(request.UserId, request.HabitId, request.GoalIds,
                (habit, today) => ResolveUpdate(request, habit, today), false, ct),
            state => HabitLiveRootEntry.FromUpdate(state.Habit, state.Update),
            (state, ct) => ApplyUpdateAsync(request, state, ct),
            cancellationToken);
    }

    public Task<Result<string>> Handle(UpdateHabitFieldsCommand request, CancellationToken cancellationToken) =>
        HabitCeilingLock.ExecuteEntryAsync<UpdateState, string>(
            unitOfWork,
            request.UserId,
            payGate,
            ct => PrepareUpdateAsync(request.UserId, request.HabitId, null,
                (habit, today) => HabitUpdateValidation.ResolveSingleUpdate(habit, request.Changes, today), true, ct),
            state => HabitLiveRootEntry.FromUpdate(state.Habit, state.Update),
            async (state, ct) =>
            {
                var command = HabitUpdateValidation.ToCommand(request.UserId, request.HabitId, state.Update);
                var result = await ApplyUpdateAsync(command, state, ct);
                return result.IsFailure ? result.PropagateError<string>() : Result.Success(state.Habit.Title);
            },
            cancellationToken);

    private async Task<Result<UpdateState>> PrepareUpdateAsync(
        Guid userId,
        Guid habitId,
        IReadOnlyList<Guid>? goalIds,
        Func<Habit, DateOnly, HabitUpdateParams> resolveUpdate,
        bool validateFields,
        CancellationToken cancellationToken)
    {
        var habit = goalIds is not null
            ? await habitRepository.FindOneTrackedAsync(
                h => h.Id == habitId && h.UserId == userId,
                q => q.Include(h => h.Goals),
                cancellationToken)
            : await habitRepository.FindOneTrackedAsync(
                h => h.Id == habitId && h.UserId == userId,
                cancellationToken: cancellationToken);

        if (habit is null)
            return Result.Failure<UpdateState>(ErrorMessages.HabitNotFound);

        await habitRepository.ReloadAsync(habit, cancellationToken);
        var today = await userDateService.GetUserTodayAsync(userId, cancellationToken);
        var update = resolveUpdate(habit, today);
        if (validateFields)
        {
            var validation = await HabitUpdateValidation.ValidateAsync(userId, habit, update, habitRepository, cancellationToken);
            if (validation.IsFailure)
                return validation.PropagateError<UpdateState>();
        }

        if (update.SlipAlertEnabled == true && !habit.SlipAlertEnabled)
        {
            var slipAlertGate = await payGate.CanUseSlipAlerts(userId, cancellationToken);
            if (slipAlertGate.IsFailure)
                return slipAlertGate.PropagateError<UpdateState>();
        }

        if (update.IsGeneral.HasValue && !validateFields)
        {
            var related = await HabitUpdateValidation.LoadRelatedAsync([habit], userId, habitRepository, cancellationToken);
            var generalMismatch = HabitUpdateValidation.ValidateGeneral(habit, update.IsGeneral.Value, related);
            if (generalMismatch.IsFailure)
                return generalMismatch.PropagateError<UpdateState>();
        }

        return Result.Success(new UpdateState(habit, update, today));
    }

    private static HabitUpdateParams ResolveUpdate(UpdateHabitCommand request, Habit habit, DateOnly today)
    {
        var opts = request.Options ?? new UpdateHabitCommandOptions();
        return new HabitUpdateParams(
            request.Title,
            request.Description,
            request.FrequencyUnit,
            request.FrequencyQuantity,
            opts.Days,
            request.IsBadHabit,
            request.DueDate,
            DueTime: opts.DueTime,
            DueEndTime: opts.DueEndTime,
            ReminderEnabled: opts.ReminderEnabled,
            ReminderTimes: opts.ReminderTimes,
            SlipAlertEnabled: opts.SlipAlertEnabled,
            ChecklistItems: opts.ChecklistItems,
            IsGeneral: request.IsGeneral,
            IsFlexible: opts.IsFlexible,
            EndDate: opts.EndDate,
            ClearEndDate: request.ClearEndDate,
            ScheduledReminders: opts.ScheduledReminders,
            RelativeReminders: opts.RelativeReminders,
            Emoji: request.Emoji,
            UserToday: today,
            IntervalWeeks: request.IntervalWeeks ?? habit.IntervalWeeks);

    }

    private async Task<Result> ApplyUpdateAsync(
        UpdateHabitCommand request,
        UpdateState state,
        CancellationToken cancellationToken)
    {
        var habit = state.Habit;
        var previousDueTime = habit.DueTime;
        var previousReminderEnabled = habit.ReminderEnabled;
        var previousReminderTimes = GetReminderTimes(habit).ToHashSet();
        var result = habit.Update(state.Update);

        if (result.IsFailure)
            return result;

        if (previousDueTime != habit.DueTime
            || previousReminderEnabled != habit.ReminderEnabled
            || !previousReminderTimes.SetEquals(GetReminderTimes(habit)))
            await ClearTodaySentRemindersAsync(request.UserId, request.HabitId, cancellationToken);

        IReadOnlyList<Guid> affectedGoalIds = [];
        if (request.GoalIds is not null)
        {
            var goalLinkResult = await SyncGoalLinksAsync(habit, request.UserId, request.GoalIds, cancellationToken);
            if (goalLinkResult.IsFailure)
                return goalLinkResult.PropagateError();
            affectedGoalIds = goalLinkResult.Value;
        }

        if (request.GoalIds is not null)
        {
            await goalCompletionService.SyncDerivedGoalsAsync(
                request.UserId,
                affectedGoalIds,
                state.Today,
                cancellationToken: cancellationToken);
        }
        else
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

        CacheInvalidationHelper.InvalidateUserAiCaches(cache, request.UserId, state.Today);

        return Result.Success();
    }

    private static IEnumerable<RelativeReminderTime> GetReminderTimes(Habit habit) =>
        habit.ScheduledReminders.Select(RelativeReminderTime.FromScheduled)
            .Concat(habit.DueTime.HasValue
                ? habit.RelativeReminders.Concat(habit.ReminderTimes.Select(minutes => new RelativeReminderTime(minutes)))
                : []);

    private sealed record UpdateState(
        Habit Habit,
        HabitUpdateParams Update,
        DateOnly Today);

    private async Task ClearTodaySentRemindersAsync(Guid userId, Guid habitId, CancellationToken cancellationToken)
    {
        var userToday = await userDateService.GetUserTodayAsync(userId, cancellationToken);
        var existing = await sentReminderRepository.FindAsync(
            r => r.HabitId == habitId && r.Date == userToday,
            cancellationToken);
        foreach (var r in existing)
            sentReminderRepository.Remove(r);
    }

    private async Task<Result<IReadOnlyList<Guid>>> SyncGoalLinksAsync(
        Habit habit, Guid userId, IReadOnlyList<Guid> goalIds, CancellationToken cancellationToken)
    {
        var affectedGoalIds = habit.Goals.Select(g => g.Id).Concat(goalIds).ToHashSet();
        var goals = await goalRepository.FindTrackedAsync(
            g => affectedGoalIds.Contains(g.Id) && g.UserId == userId,
            q => q.Include(g => g.Habits).ThenInclude(h => h.Logs),
            cancellationToken);

        var requestedGoals = goals.Where(g => goalIds.Contains(g.Id)).ToList();
        var goalsResolved = OwnershipValidation.AllResolved(goalIds, requestedGoals, g => g.Id, ErrorMessages.GoalNotFound);
        if (goalsResolved.IsFailure)
            return goalsResolved.PropagateError<IReadOnlyList<Guid>>();

        foreach (var existingGoal in habit.Goals.ToList())
            habit.RemoveGoal(existingGoal);
        foreach (var goal in requestedGoals)
            habit.AddGoal(goal);

        return Result.Success<IReadOnlyList<Guid>>(affectedGoalIds.ToList());
    }
}
