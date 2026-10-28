using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Habits.Commands;

public sealed record CorrectHabitDoseCommand(Guid UserId, Guid HabitId, Guid LogId, DateTime TakenAtUtc)
    : IRequest<Result>;

public sealed class CorrectHabitDoseCommandHandler(
    IGenericRepository<Habit> habits, IUnitOfWork unitOfWork, IUserDateService dates, IMemoryCache cache)
    : IRequestHandler<CorrectHabitDoseCommand, Result>
{
    public Task<Result> Handle(CorrectHabitDoseCommand request, CancellationToken cancellationToken) =>
        HabitCeilingLock.ExecuteAsync(unitOfWork, request.UserId, async ct =>
        {
            var habit = await habits.FindOneTrackedAsync(
                h => h.Id == request.HabitId && h.UserId == request.UserId,
                q => q.Include(h => h.Logs.Where(l => l.Id == request.LogId)), ct);
            if (habit is null)
                return Result.Failure(ErrorMessages.HabitNotFound);
            var result = habit.CorrectDose(request.LogId, request.TakenAtUtc, DateTime.UtcNow);
            if (result.IsFailure)
                return result;
            await unitOfWork.SaveChangesAsync(ct);
            var today = await dates.GetUserTodayAsync(request.UserId, ct);
            CacheInvalidationHelper.InvalidateUserAiCaches(cache, request.UserId, today);
            return Result.Success();
        }, cancellationToken);
}
