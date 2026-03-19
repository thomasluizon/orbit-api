using MediatR;
using Microsoft.Extensions.Caching.Memory;
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
    TimeOnly? DueTime = null) : IRequest<Result<Guid>>;

public class CreateHabitCommandHandler(
    IGenericRepository<Habit> habitRepository,
    IGenericRepository<User> userRepository,
    IUserDateService userDateService,
    IUnitOfWork unitOfWork,
    IMemoryCache cache) : IRequestHandler<CreateHabitCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(CreateHabitCommand request, CancellationToken cancellationToken)
    {
        // Check habit limit for free users
        var user = await userRepository.FindOneTrackedAsync(u => u.Id == request.UserId, cancellationToken: cancellationToken);
        if (user is not null && !user.HasProAccess)
        {
            var activeHabits = await habitRepository.FindAsync(h => h.UserId == request.UserId && h.IsActive, cancellationToken);
            if (activeHabits.Count >= 10)
            {
                return Result.Failure<Guid>("You've reached the 10 habit limit on the free plan. Upgrade to Pro for unlimited habits.");
            }
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
            dueTime: request.DueTime);

        if (habitResult.IsFailure)
            return Result.Failure<Guid>(habitResult.Error);

        var habit = habitResult.Value;

        if (request.SubHabits is { Count: > 0 })
        {
            if (user is not null && !user.HasProAccess)
                return Result.Failure<Guid>("Sub-habits are a Pro feature. Upgrade to unlock!");

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
        await unitOfWork.SaveChangesAsync(cancellationToken);

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        for (int i = -1; i <= 1; i++)
        {
            cache.Remove($"summary:{request.UserId}:{today.AddDays(i):yyyy-MM-dd}:en");
            cache.Remove($"summary:{request.UserId}:{today.AddDays(i):yyyy-MM-dd}:pt-BR");
        }

        return Result.Success(habit.Id);
    }
}
