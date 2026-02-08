using MediatR;
using Microsoft.EntityFrameworkCore;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Tags.Commands;

public record AssignTagCommand(Guid UserId, Guid HabitId, Guid TagId) : IRequest<Result>;

public class AssignTagCommandHandler(
    IGenericRepository<Habit> habitRepository,
    IGenericRepository<Tag> tagRepository,
    IUnitOfWork unitOfWork) : IRequestHandler<AssignTagCommand, Result>
{
    public async Task<Result> Handle(AssignTagCommand request, CancellationToken cancellationToken)
    {
        var habit = await habitRepository.FindOneTrackedAsync(
            h => h.Id == request.HabitId && h.UserId == request.UserId,
            q => q.Include(h => h.Tags),
            cancellationToken);

        if (habit is null)
            return Result.Failure("Habit not found or you don't have permission.");

        var tag = await tagRepository.GetByIdAsync(request.TagId, cancellationToken);

        if (tag is null)
            return Result.Failure("Tag not found.");

        if (tag.UserId != request.UserId)
            return Result.Failure("You don't have permission to use this tag.");

        if (habit.Tags.Any(t => t.Id == request.TagId))
            return Result.Success();

        habit.Tags.Add(tag);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Success();
    }
}
