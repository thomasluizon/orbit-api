using MediatR;
using Microsoft.Extensions.Caching.Memory;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Habits.Commands;

public record SkipHabitCommand(Guid UserId, Guid HabitId) : IRequest<Result>;

public class SkipHabitCommandHandler(
    IGenericRepository<Habit> habitRepository,
    IUserDateService userDateService,
    IUnitOfWork unitOfWork,
    IMemoryCache cache) : IRequestHandler<SkipHabitCommand, Result>
{
    public async Task<Result> Handle(SkipHabitCommand request, CancellationToken cancellationToken)
    {
        var habit = await habitRepository.FindOneTrackedAsync(
            h => h.Id == request.HabitId,
            cancellationToken: cancellationToken);

        if (habit is null)
            return Result.Failure(ErrorMessages.HabitNotFound);

        if (habit.UserId != request.UserId)
            return Result.Failure(ErrorMessages.HabitNotOwned);

        if (habit.IsCompleted)
            return Result.Failure("Cannot skip a completed habit.");

        if (habit.FrequencyUnit is null)
            return Result.Failure("Cannot skip a one-time task.");

        var today = await userDateService.GetUserTodayAsync(request.UserId, cancellationToken);

        if (habit.DueDate > today)
            return Result.Failure("Cannot skip a habit that is not yet due.");

        habit.AdvanceDueDate(today);

        await unitOfWork.SaveChangesAsync(cancellationToken);

        CacheInvalidationHelper.InvalidateSummaryCache(cache, habit.UserId);

        return Result.Success();
    }
}
