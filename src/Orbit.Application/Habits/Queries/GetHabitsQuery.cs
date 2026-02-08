using MediatR;
using Microsoft.EntityFrameworkCore;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Habits.Queries;

public record GetHabitsQuery(Guid UserId, IReadOnlyList<Guid>? TagIds = null) : IRequest<IReadOnlyList<Habit>>;

public class GetHabitsQueryHandler(
    IGenericRepository<Habit> habitRepository) : IRequestHandler<GetHabitsQuery, IReadOnlyList<Habit>>
{
    public async Task<IReadOnlyList<Habit>> Handle(GetHabitsQuery request, CancellationToken cancellationToken)
    {
        if (request.TagIds is { Count: > 0 })
        {
            return await habitRepository.FindAsync(
                h => h.UserId == request.UserId && h.IsActive
                     && h.Tags.Any(t => request.TagIds.Contains(t.Id)),
                q => q.Include(h => h.SubHabits.Where(sh => sh.IsActive).OrderBy(sh => sh.SortOrder))
                      .Include(h => h.Tags),
                cancellationToken);
        }

        return await habitRepository.FindAsync(
            h => h.UserId == request.UserId && h.IsActive,
            q => q.Include(h => h.SubHabits.Where(sh => sh.IsActive).OrderBy(sh => sh.SortOrder))
                  .Include(h => h.Tags),
            cancellationToken);
    }
}
