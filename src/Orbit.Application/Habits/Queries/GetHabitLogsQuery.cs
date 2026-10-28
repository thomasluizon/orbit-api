using MediatR;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Habits.Queries;

public record HabitLogResponse(
    Guid Id,
    DateOnly Date,
    decimal Value,
    DateTime CreatedAtUtc,
    DateTime? TakenAtUtc = null,
    DateTime? ReminderAtUtc = null);

public record GetHabitLogsQuery(
    Guid UserId,
    Guid HabitId,
    DateOnly? From = null,
    DateOnly? To = null) : IRequest<Result<IReadOnlyList<HabitLogResponse>>>;

public class GetHabitLogsQueryHandler(
    IGenericRepository<Habit> habitRepository,
    IHabitLogReader habitLogReader,
    IUserDateService userDateService) : IRequestHandler<GetHabitLogsQuery, Result<IReadOnlyList<HabitLogResponse>>>
{
    public async Task<Result<IReadOnlyList<HabitLogResponse>>> Handle(GetHabitLogsQuery request, CancellationToken cancellationToken)
    {
        var habit = await habitRepository.FindOneTrackedAsync(
            h => h.Id == request.HabitId && h.UserId == request.UserId,
            cancellationToken: cancellationToken);

        if (habit is null)
            return Result.Failure<IReadOnlyList<HabitLogResponse>>(ErrorMessages.HabitNotFound);

        IReadOnlyList<HabitLog> logs;
        if (request.From is { } from && request.To is { } to)
        {
            logs = await habitLogReader.ReadRangeLogsAsync(request.HabitId, from, to, cancellationToken);
        }
        else
        {
            var userToday = await userDateService.GetUserTodayAsync(request.UserId, cancellationToken);
            var cutoff = userToday.AddDays(-AppConstants.HabitLogsLookbackDays);
            logs = await habitLogReader.ReadRecentLogsAsync(
                request.HabitId,
                cutoff,
                AppConstants.MaxHabitLogsReturned,
                cancellationToken);
        }

        var result = logs
            .Select(l => new HabitLogResponse(l.Id, l.Date, l.Value, l.CreatedAtUtc, l.TakenAtUtc, l.ReminderAtUtc))
            .ToList();

        return Result.Success<IReadOnlyList<HabitLogResponse>>(result);
    }
}
