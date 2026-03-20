using MediatR;
using Microsoft.Extensions.Caching.Memory;
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
    IReadOnlyList<System.DayOfWeek>? Days = null,
    bool IsBadHabit = false,
    IReadOnlyList<string>? SubHabits = null,
    DateOnly? DueDate = null,
    TimeOnly? DueTime = null,
    bool ReminderEnabled = false,
    int ReminderMinutesBefore = 15,
    IReadOnlyList<Guid>? TagIds = null) : IRequest<Result<Guid>>;

public class CreateHabitCommandHandler(
    IGenericRepository<Habit> habitRepository,
    IGenericRepository<Tag> tagRepository,
    IUserDateService userDateService,
    IPayGateService payGate,
    IUnitOfWork unitOfWork,
    IMemoryCache cache) : IRequestHandler<CreateHabitCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(CreateHabitCommand request, CancellationToken cancellationToken)
    {
        // Check habit limit
        var gateCheck = await payGate.CanCreateHabits(request.UserId, 1, cancellationToken);
        if (gateCheck.IsFailure)
            return gateCheck.PropagateError<Guid>();

        // Check sub-habit access if creating with sub-habits
        if (request.SubHabits is { Count: > 0 })
        {
            var subGateCheck = await payGate.CanCreateSubHabits(request.UserId, cancellationToken);
            if (subGateCheck.IsFailure)
                return subGateCheck.PropagateError<Guid>();
        }

        var dueDate = request.DueDate ?? await userDateService.GetUserTodayAsync(request.UserId, cancellationToken);

        var habitResult = Habit.Create(
            request.UserId,
            request.Title,
            request.FrequencyUnit,
            request.FrequencyQuantity,
            request.Description,
            request.Days,
            request.IsBadHabit,
            dueDate,
            dueTime: request.DueTime,
            reminderEnabled: request.ReminderEnabled,
            reminderMinutesBefore: request.ReminderMinutesBefore);

        if (habitResult.IsFailure)
            return Result.Failure<Guid>(habitResult.Error);

        var habit = habitResult.Value;

        if (request.SubHabits is { Count: > 0 })
        {
            foreach (var subTitle in request.SubHabits)
            {
                var childResult = Habit.Create(
                    request.UserId,
                    subTitle,
                    request.FrequencyUnit,
                    request.FrequencyQuantity,
                    dueDate: request.DueDate ?? dueDate,
                    parentHabitId: habit.Id);

                if (childResult.IsFailure)
                    return Result.Failure<Guid>(childResult.Error);

                await habitRepository.AddAsync(childResult.Value, cancellationToken);
            }
        }

        await habitRepository.AddAsync(habit, cancellationToken);

        if (request.TagIds is { Count: > 0 })
        {
            var tags = await tagRepository.FindAsync(
                t => request.TagIds.Contains(t.Id) && t.UserId == request.UserId,
                cancellationToken);
            foreach (var tag in tags)
                habit.AddTag(tag);
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);

        CacheInvalidationHelper.InvalidateSummaryCache(cache, request.UserId);

        return Result.Success(habit.Id);
    }
}
