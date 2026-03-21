using MediatR;
using Microsoft.Extensions.Caching.Memory;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Domain.ValueObjects;
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
    bool? ReminderEnabled = null,
    int? ReminderMinutesBefore = null,
    bool? SlipAlertEnabled = null,
    IReadOnlyList<ChecklistItem>? ChecklistItems = null) : IRequest<Result>;

public class UpdateHabitCommandHandler(
    IGenericRepository<Habit> habitRepository,
    IGenericRepository<SentReminder> sentReminderRepository,
    IUserDateService userDateService,
    IUnitOfWork unitOfWork,
    IMemoryCache cache) : IRequestHandler<UpdateHabitCommand, Result>
{
    public async Task<Result> Handle(UpdateHabitCommand request, CancellationToken cancellationToken)
    {
        var habit = await habitRepository.FindOneTrackedAsync(
            h => h.Id == request.HabitId && h.UserId == request.UserId,
            cancellationToken: cancellationToken);

        if (habit is null)
            return Result.Failure(ErrorMessages.HabitNotFound);

        var result = habit.Update(
            request.Title,
            request.Description,
            request.FrequencyUnit,
            request.FrequencyQuantity,
            request.Days,
            request.IsBadHabit,
            request.DueDate,
            dueTime: request.DueTime,
            reminderEnabled: request.ReminderEnabled,
            reminderMinutesBefore: request.ReminderMinutesBefore,
            slipAlertEnabled: request.SlipAlertEnabled,
            checklistItems: request.ChecklistItems);

        if (result.IsFailure)
            return result;

        // If dueTime changed, clear today's sent reminder so it re-triggers
        if (request.DueTime.HasValue)
        {
            var userToday = await userDateService.GetUserTodayAsync(request.UserId, cancellationToken);
            var existing = await sentReminderRepository.FindOneTrackedAsync(
                r => r.HabitId == request.HabitId && r.Date == userToday,
                cancellationToken: cancellationToken);
            if (existing is not null)
                sentReminderRepository.Remove(existing);
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);

        CacheInvalidationHelper.InvalidateSummaryCache(cache, request.UserId);

        return Result.Success();
    }
}
