using MediatR;
using Microsoft.Extensions.Caching.Memory;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;
using System.Linq.Expressions;

namespace Orbit.Application.Habits.Commands;

public record UpdateHabitCommand(
    Guid UserId,
    Guid HabitId,
    string Title,
    string? Description,
    FrequencyUnit? FrequencyUnit,
    int? FrequencyQuantity,
    IReadOnlyList<System.DayOfWeek>? Days = null,
    bool IsBadHabit = false,
    DateOnly? DueDate = null,
    TimeOnly? DueTime = null,
    TimeOnly? DueEndTime = null,
    bool? ReminderEnabled = null,
    IReadOnlyList<int>? ReminderTimes = null,
    bool? SlipAlertEnabled = null,
    IReadOnlyList<ChecklistItem>? ChecklistItems = null,
    bool? IsGeneral = null,
    DateOnly? EndDate = null,
    bool? ClearEndDate = null,
    bool? IsFlexible = null,
    IReadOnlyList<Guid>? GoalIds = null,
    IReadOnlyList<ScheduledReminderTime>? ScheduledReminders = null) : IRequest<Result>;

public class UpdateHabitCommandHandler(
    IGenericRepository<Habit> habitRepository,
    IGenericRepository<SentReminder> sentReminderRepository,
    IGenericRepository<Goal> goalRepository,
    IUserDateService userDateService,
    IUnitOfWork unitOfWork,
    IMemoryCache cache) : IRequestHandler<UpdateHabitCommand, Result>
{
    public async Task<Result> Handle(UpdateHabitCommand request, CancellationToken cancellationToken)
    {
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

        var result = habit.Update(
            request.Title,
            request.Description,
            request.FrequencyUnit,
            request.FrequencyQuantity,
            request.Days,
            request.IsBadHabit,
            request.DueDate,
            dueTime: request.DueTime,
            dueEndTime: request.DueEndTime,
            reminderEnabled: request.ReminderEnabled,
            reminderTimes: request.ReminderTimes,
            slipAlertEnabled: request.SlipAlertEnabled,
            checklistItems: request.ChecklistItems,
            isGeneral: request.IsGeneral,
            isFlexible: request.IsFlexible,
            endDate: request.EndDate,
            clearEndDate: request.ClearEndDate,
            scheduledReminders: request.ScheduledReminders);

        if (result.IsFailure)
            return result;

        // If dueTime changed, clear today's sent reminders so they re-trigger
        if (request.DueTime.HasValue)
        {
            var userToday = await userDateService.GetUserTodayAsync(request.UserId, cancellationToken);
            var existing = await sentReminderRepository.FindAsync(
                r => r.HabitId == request.HabitId && r.Date == userToday,
                cancellationToken);
            foreach (var r in existing)
                sentReminderRepository.Remove(r);
        }

        // Sync goal links if GoalIds was provided
        if (request.GoalIds is not null)
        {
            foreach (var existingGoal in habit.Goals.ToList())
                habit.RemoveGoal(existingGoal);

            if (request.GoalIds.Count > 0)
            {
                var goals = await goalRepository.FindTrackedAsync(
                    g => request.GoalIds.Contains(g.Id) && g.UserId == request.UserId,
                    cancellationToken);
                foreach (var goal in goals)
                    habit.AddGoal(goal);
            }
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);

        CacheInvalidationHelper.InvalidateSummaryCache(cache, request.UserId);

        return Result.Success();
    }
}
