using MediatR;
using Microsoft.EntityFrameworkCore;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Habits.Queries;

public record HabitLogResponse(
    Guid Id,
    DateOnly Date,
    decimal Value,
    string? Note,
    DateTime CreatedAtUtc);

public record GetHabitLogsQuery(Guid UserId, Guid HabitId) : IRequest<Result<IReadOnlyList<HabitLogResponse>>>;

public class GetHabitLogsQueryHandler(
    IGenericRepository<Habit> habitRepository,
    IGenericRepository<HabitLog> habitLogRepository) : IRequestHandler<GetHabitLogsQuery, Result<IReadOnlyList<HabitLogResponse>>>
{
    private const int DefaultLookbackDays = 365;

    public async Task<Result<IReadOnlyList<HabitLogResponse>>> Handle(GetHabitLogsQuery request, CancellationToken cancellationToken)
    {
        // Verify ownership without loading logs
        var habit = await habitRepository.FindOneTrackedAsync(
            h => h.Id == request.HabitId && h.UserId == request.UserId,
            cancellationToken: cancellationToken);

        if (habit is null)
            return Result.Failure<IReadOnlyList<HabitLogResponse>>(ErrorMessages.HabitNotFound);

        // Cap log history to last 365 days to prevent unbounded queries
        var cutoff = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-DefaultLookbackDays);

        var logs = await habitLogRepository.FindAsync(
            l => l.HabitId == request.HabitId && l.Date >= cutoff,
            cancellationToken);

        var result = logs
            .OrderByDescending(l => l.Date)
            .Select(l => new HabitLogResponse(l.Id, l.Date, l.Value, l.Note, l.CreatedAtUtc))
            .ToList();

        return Result.Success<IReadOnlyList<HabitLogResponse>>(result);
    }
}
