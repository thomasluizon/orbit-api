using MediatR;
using Microsoft.EntityFrameworkCore;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Habits.Queries;

public record GetHabitsQuery(Guid UserId) : IRequest<IReadOnlyList<Habit>>;

public class GetHabitsQueryHandler(
    IGenericRepository<Habit> habitRepository) : IRequestHandler<GetHabitsQuery, IReadOnlyList<Habit>>
{
    public async Task<IReadOnlyList<Habit>> Handle(GetHabitsQuery request, CancellationToken cancellationToken)
    {
        return await habitRepository.FindAsync(
            h => h.UserId == request.UserId && h.IsActive,
            q => q.Include(h => h.SubHabits.Where(sh => sh.IsActive).OrderBy(sh => sh.SortOrder))
                  .Include(h => h.Tags),
            cancellationToken);
    }
}
