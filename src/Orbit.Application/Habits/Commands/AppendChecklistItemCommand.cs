using MediatR;
using Microsoft.Extensions.Caching.Memory;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Habits.Commands;

public sealed record AppendChecklistItemCommand(Guid UserId, Guid HabitId, string Text) : IRequest<Result<Guid>>;

public sealed class AppendChecklistItemCommandHandler(IGenericRepository<Habit> habits, IUnitOfWork work,
    IMemoryCache cache, IUserDateService dates) : IRequestHandler<AppendChecklistItemCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(AppendChecklistItemCommand request, CancellationToken ct)
    {
        var habit = await habits.FindOneTrackedAsync(habit => habit.Id == request.HabitId
            && habit.UserId == request.UserId && !habit.IsDeleted, cancellationToken: ct);
        if (habit is null) return Result.Failure<Guid>(ErrorMessages.HabitNotFound);
        var result = habit.AppendChecklistItem(request.Text);
        if (result.IsFailure) return result.PropagateError<Guid>();
        await work.SaveChangesAsync(ct);
        CacheInvalidationHelper.InvalidateUserAiCaches(cache, request.UserId, await dates.GetUserTodayAsync(request.UserId, ct));
        return Result.Success(habit.Id);
    }

    internal static async Task<Result> CheckAsync(AppendChecklistItemCommand request,
        IGenericRepository<Habit> habits, CancellationToken ct)
    {
        var found = await habits.FindAsync(habit => habit.Id == request.HabitId
            && habit.UserId == request.UserId && !habit.IsDeleted, ct);
        return found.FirstOrDefault() is { } habit ? habit.CanAppendChecklistItem(request.Text)
            : Result.Failure(ErrorMessages.HabitNotFound);
    }
}
