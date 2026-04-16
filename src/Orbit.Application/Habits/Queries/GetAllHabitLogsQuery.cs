using MediatR;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Habits.Queries;

public record GetAllHabitLogsQuery(
    Guid UserId,
    DateOnly DateFrom,
    DateOnly DateTo) : IRequest<Result<Dictionary<Guid, List<HabitLogResponse>>>>;

public class GetAllHabitLogsQueryHandler(
    IGenericRepository<Habit> habitRepository,
    IGenericRepository<HabitLog> logRepository) : IRequestHandler<GetAllHabitLogsQuery, Result<Dictionary<Guid, List<HabitLogResponse>>>>
{
    public async Task<Result<Dictionary<Guid, List<HabitLogResponse>>>> Handle(
        GetAllHabitLogsQuery request, CancellationToken cancellationToken)
    {
        var habits = await habitRepository.FindAsync(
            h => h.UserId == request.UserId && h.ParentHabitId == null,
            cancellationToken);

        var habitIds = habits.Select(h => h.Id).ToHashSet();

        var logs = await logRepository.FindAsync(
            l => habitIds.Contains(l.HabitId) && l.Date >= request.DateFrom && l.Date <= request.DateTo,
            cancellationToken);

        var grouped = logs
            .OrderByDescending(l => l.Date)
            .GroupBy(l => l.HabitId)
            .ToDictionary(
                g => g.Key,
                g => g.Select(l => new HabitLogResponse(l.Id, l.Date, l.Value, l.CreatedAtUtc)).ToList());

        return Result.Success(grouped);
    }
}
