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
    string? Icon = null) : IRequest<Result>;

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
            return Result.Failure(ErrorMessages.HabitNotFound, ErrorCodes.HabitNotFound);

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
            Icon: request.Icon));

        if (result.IsFailure)
            return result;

        if (opts.DueTime.HasValue)
            await ClearTodaySentRemindersAsync(request.UserId, request.HabitId, cancellationToken);

        if (request.GoalIds is not null)
            await SyncGoalLinksAsync(habit, request.UserId, request.GoalIds, cancellationToken);

        await unitOfWork.SaveChangesAsync(cancellationToken);

        CacheInvalidationHelper.InvalidateSummaryCache(cache, request.UserId);

        return Result.Success();
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

    private async Task SyncGoalLinksAsync(
        Habit habit, Guid userId, IReadOnlyList<Guid> goalIds, CancellationToken cancellationToken)
    {
        foreach (var existingGoal in habit.Goals.ToList())
            habit.RemoveGoal(existingGoal);

        if (goalIds.Count == 0)
            return;

        var goals = await goalRepository.FindTrackedAsync(
            g => goalIds.Contains(g.Id) && g.UserId == userId,
            cancellationToken);
        foreach (var goal in goals)
            habit.AddGoal(goal);
    }
}
