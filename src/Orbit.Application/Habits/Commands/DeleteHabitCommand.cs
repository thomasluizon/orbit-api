using MediatR;
using Microsoft.Extensions.Caching.Memory;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Habits.Commands;

public record DeleteHabitCommand(
    Guid UserId,
    Guid HabitId) : IRequest<Result>;

public class DeleteHabitCommandHandler(
    IGenericRepository<Habit> habitRepository,
    IUnitOfWork unitOfWork,
    IMemoryCache cache) : IRequestHandler<DeleteHabitCommand, Result>
{
    public async Task<Result> Handle(DeleteHabitCommand request, CancellationToken cancellationToken)
    {
        var habit = await habitRepository.GetByIdAsync(request.HabitId, cancellationToken);

        if (habit is null)
            return Result.Failure(ErrorMessages.HabitNotFound, ErrorCodes.HabitNotFound);

        if (habit.UserId != request.UserId)
            return Result.Failure(ErrorMessages.NoPermission, ErrorCodes.NoPermission);

        habitRepository.Remove(habit);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        CacheInvalidationHelper.InvalidateSummaryCache(cache, request.UserId);

        return Result.Success();
    }
}
