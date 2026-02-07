using MediatR;
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
            cancellationToken);
    }
}
