using MediatR;
using Microsoft.Extensions.Caching.Memory;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
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
    string? Emoji = null) : IRequest<Result>;

public class UpdateHabitCommandHandler(
    IGenericRepository<Habit> habitRepository,
    IGenericRepository<SentReminder> sentReminderRepository,
    IGenericRepository<Goal> goalRepository,
    IPayGateService payGate,
    IUserDateService userDateService,
    IUnitOfWork unitOfWork,
    IMemoryCache cache) : IRequestHandler<UpdateHabitCommand, Result>
{
    public async Task<Result> Handle(UpdateHabitCommand request, CancellationToken cancellationToken)
    {
        if (request.GoalIds is not null)
        {
            var goalLinkGate = await payGate.CanLinkGoalsToHabits(request.UserId, cancellationToken);
            if (goalLinkGate.IsFailure)
                return goalLinkGate;
        }

        if (request.Options?.SlipAlertEnabled is not null)
        {
            var slipAlertGate = await payGate.CanUseSlipAlerts(request.UserId, cancellationToken);
            if (slipAlertGate.IsFailure)
                return slipAlertGate;
        }

        var habit = request.GoalIds is not null
            ? await habitRepository.FindOneTrackedAsync(
                h => h.Id == request.HabitId && h.UserId == request.UserId,
                q => q.Include(h => h.Goals),
                cancellationToken)
            : await habitRepository.FindOneTrackedAsync(
                h => h.Id == request.HabitId && h.UserId == request.UserId,
                cancellationToken: cancellationToken);

        if (habit is null)
            return Result.Failure(ErrorMessages.HabitNotFound);

        if (request.IsGeneral.HasValue)
        {
            var generalMismatch = await ValidateGeneralAgainstRelativesAsync(
                habit, request.IsGeneral.Value, request.UserId, cancellationToken);
            if (generalMismatch.IsFailure)
                return generalMismatch;
        }

        var opts = request.Options ?? new UpdateHabitCommandOptions();

        var result = habit.Update(new HabitUpdateParams(
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
            Emoji: request.Emoji));

        if (result.IsFailure)
            return result;

        if (opts.DueTime.HasValue)
            await ClearTodaySentRemindersAsync(request.UserId, request.HabitId, cancellationToken);

        if (request.GoalIds is not null)
        {
            var goalLinkResult = await SyncGoalLinksAsync(habit, request.UserId, request.GoalIds, cancellationToken);
            if (goalLinkResult.IsFailure)
                return goalLinkResult;
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);

        var today = await userDateService.GetUserTodayAsync(request.UserId, cancellationToken);
        CacheInvalidationHelper.InvalidateUserAiCaches(cache, request.UserId, today);

        return Result.Success();
    }

    /// <summary>
    /// Rejects an <c>IsGeneral</c> change that would break the invariant that a habit's
    /// <c>IsGeneral</c> must match its parent's: if the habit has a parent, the new value must
    /// match the parent's; otherwise, it must match every child that isn't a completed
    /// one-time task. A completed one-time-task child is functionally inert and excluded so an
    /// ancient, already-finished child can't permanently block the parent's General flag.
    /// </summary>
    private async Task<Result> ValidateGeneralAgainstRelativesAsync(
        Habit habit, bool newIsGeneral, Guid userId, CancellationToken cancellationToken)
    {
        if (habit.ParentHabitId is not null)
        {
            var parent = await habitRepository.FindOneTrackedAsync(
                h => h.Id == habit.ParentHabitId && h.UserId == userId,
                cancellationToken: cancellationToken);

            return parent is not null && parent.IsGeneral != newIsGeneral
                ? Result.Failure(ErrorMessages.GeneralMismatchWithParent)
                : Result.Success();
        }

        var children = await habitRepository.FindAsync(
            h => h.ParentHabitId == habit.Id && h.UserId == userId,
            cancellationToken);

        var activeMismatchedChild = children.Any(c =>
            c.IsGeneral != newIsGeneral && !(c.IsCompleted && c.FrequencyUnit is null));

        return activeMismatchedChild
            ? Result.Failure(ErrorMessages.GeneralMismatchWithChildren)
            : Result.Success();
    }

    private async Task ClearTodaySentRemindersAsync(Guid userId, Guid habitId, CancellationToken cancellationToken)
    {
        var userToday = await userDateService.GetUserTodayAsync(userId, cancellationToken);
        var existing = await sentReminderRepository.FindAsync(
            r => r.HabitId == habitId && r.Date == userToday,
            cancellationToken);
        foreach (var r in existing)
            sentReminderRepository.Remove(r);
    }

    private async Task<Result> SyncGoalLinksAsync(
        Habit habit, Guid userId, IReadOnlyList<Guid> goalIds, CancellationToken cancellationToken)
    {
        if (goalIds.Count == 0)
        {
            foreach (var existingGoal in habit.Goals.ToList())
                habit.RemoveGoal(existingGoal);
            return Result.Success();
        }

        var goals = await goalRepository.FindTrackedAsync(
            g => goalIds.Contains(g.Id) && g.UserId == userId,
            cancellationToken);

        var goalsResolved = OwnershipValidation.AllResolved(goalIds, goals, g => g.Id, ErrorMessages.GoalNotFound);
        if (goalsResolved.IsFailure)
            return goalsResolved;

        foreach (var existingGoal in habit.Goals.ToList())
            habit.RemoveGoal(existingGoal);
        foreach (var goal in goals)
            habit.AddGoal(goal);

        return Result.Success();
    }
}
