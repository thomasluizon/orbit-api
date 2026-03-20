using MediatR;
using Microsoft.EntityFrameworkCore;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Tags.Commands;

public record AssignTagsCommand(
    Guid UserId,
    Guid HabitId,
    IReadOnlyList<Guid> TagIds) : IRequest<Result>;

public class AssignTagsCommandHandler(
    IGenericRepository<Habit> habitRepository,
    IGenericRepository<Tag> tagRepository,
    IAppConfigService appConfigService,
    IUnitOfWork unitOfWork) : IRequestHandler<AssignTagsCommand, Result>
{
    public async Task<Result> Handle(AssignTagsCommand request, CancellationToken cancellationToken)
    {
        var maxTags = await appConfigService.GetAsync("MaxTagsPerHabit", 5, cancellationToken);

        if (request.TagIds.Count > maxTags)
            return Result.Failure($"A habit can have at most {maxTags} tags.");

        var habit = await habitRepository.FindOneTrackedAsync(
            h => h.Id == request.HabitId && h.UserId == request.UserId,
            q => q.Include(h => h.Tags),
            cancellationToken);

        if (habit is null)
            return Result.Failure(ErrorMessages.HabitNotFound);

        // Load requested tags
        var tags = await tagRepository.FindAsync(
            t => request.TagIds.Contains(t.Id) && t.UserId == request.UserId,
            cancellationToken);

        // Clear existing and set new
        foreach (var existing in habit.Tags.ToList())
            habit.RemoveTag(existing);

        foreach (var tag in tags)
            habit.AddTag(tag);

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }
}
