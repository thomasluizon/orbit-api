using MediatR;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Habits.Queries;

public record HabitCountResponse(int Count);

public record GetHabitCountQuery(Guid UserId) : IRequest<HabitCountResponse>;

public class GetHabitCountQueryHandler(
    IGenericRepository<Habit> habitRepository) : IRequestHandler<GetHabitCountQuery, HabitCountResponse>
{
    public async Task<HabitCountResponse> Handle(GetHabitCountQuery request, CancellationToken cancellationToken)
    {
        var count = await habitRepository.CountAsync(
            h => h.UserId == request.UserId && !h.IsGeneral,
            cancellationToken);

        return new HabitCountResponse(count);
    }
}
