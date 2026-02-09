using MediatR;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Orbit.Application.Habits.Queries;

public record HabitLogResponse(
    Guid Id,
    DateOnly Date,
    decimal Value,
    string? Note,
    DateTime CreatedAtUtc);

public record GetHabitLogsQuery(Guid UserId, Guid HabitId) : IRequest<Result<IReadOnlyList<HabitLogResponse>>>;

public class GetHabitLogsQueryHandler(
    IGenericRepository<Habit> habitRepository) : IRequestHandler<GetHabitLogsQuery, Result<IReadOnlyList<HabitLogResponse>>>
{
    public async Task<Result<IReadOnlyList<HabitLogResponse>>> Handle(GetHabitLogsQuery request, CancellationToken cancellationToken)
    {
        var habit = await habitRepository.FindAsync(
            h => h.Id == request.HabitId && h.UserId == request.UserId,
            q => q.Include(h => h.Logs),
            cancellationToken);

        var found = habit.FirstOrDefault();
        if (found is null)
            return Result.Failure<IReadOnlyList<HabitLogResponse>>("Habit not found.");

        var logs = found.Logs
            .OrderByDescending(l => l.Date)
            .Select(l => new HabitLogResponse(l.Id, l.Date, l.Value, l.Note, l.CreatedAtUtc))
            .ToList();

        return Result.Success<IReadOnlyList<HabitLogResponse>>(logs);
    }
}
