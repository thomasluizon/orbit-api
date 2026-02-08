using MediatR;
using Microsoft.EntityFrameworkCore;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Tags.Commands;

public record UnassignTagCommand(Guid UserId, Guid HabitId, Guid TagId) : IRequest<Result>;

public class UnassignTagCommandHandler(
    IGenericRepository<Habit> habitRepository,
    IUnitOfWork unitOfWork) : IRequestHandler<UnassignTagCommand, Result>
{
    public async Task<Result> Handle(UnassignTagCommand request, CancellationToken cancellationToken)
    {
        var habit = await habitRepository.FindOneTrackedAsync(
            h => h.Id == request.HabitId && h.UserId == request.UserId,
            q => q.Include(h => h.Tags),
            cancellationToken);

        if (habit is null)
            return Result.Failure("Habit not found or you don't have permission.");

        var tag = habit.Tags.FirstOrDefault(t => t.Id == request.TagId);

        if (tag is null)
            return Result.Success(); // Idempotent: already not assigned

        habit.Tags.Remove(tag);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Success();
    }
}
