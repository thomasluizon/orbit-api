using MediatR;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Habits.Queries;

public record GetAllHabitLogsQuery(
    Guid UserId,
    DateOnly DateFrom,
    DateOnly DateTo) : IRequest<Result<Dictionary<Guid, List<HabitLogResponse>>>>;

public record HabitLogReadRow(Guid HabitId, HabitLogResponse Log);

public class GetAllHabitLogsQueryHandler(
    IGenericRepository<Habit> habitRepository,
    IGenericRepository<HabitLog> logRepository) : IRequestHandler<GetAllHabitLogsQuery, Result<Dictionary<Guid, List<HabitLogResponse>>>>
{
    public async Task<Result<Dictionary<Guid, List<HabitLogResponse>>>> Handle(
        GetAllHabitLogsQuery request, CancellationToken cancellationToken)
    {
        var habitIds = await habitRepository.ProjectAsync(
            h => h.UserId == request.UserId && h.ParentHabitId == null,
            query => query.Select(h => h.Id), cancellationToken);
        var logs = await logRepository.ProjectAsync(
            l => habitIds.Contains(l.HabitId) && l.Date >= request.DateFrom && l.Date <= request.DateTo,
            query => query.OrderByDescending(l => l.Date).Select(l => new HabitLogReadRow(l.HabitId,
                new HabitLogResponse(l.Id, l.Date, l.Value, l.CreatedAtUtc, l.TakenAtUtc, l.ReminderAtUtc))),
            cancellationToken);
        var grouped = logs.GroupBy(row => row.HabitId)
            .ToDictionary(group => group.Key, group => group.Select(row => row.Log).ToList());

        return Result.Success(grouped);
    }
}
