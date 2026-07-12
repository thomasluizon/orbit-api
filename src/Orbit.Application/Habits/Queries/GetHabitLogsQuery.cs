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
    DateTime CreatedAtUtc);

public record GetHabitLogsQuery(Guid UserId, Guid HabitId) : IRequest<Result<IReadOnlyList<HabitLogResponse>>>;

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

        var userToday = await userDateService.GetUserTodayAsync(request.UserId, cancellationToken);
        var cutoff = userToday.AddDays(-AppConstants.HabitLogsLookbackDays);

        var logs = await habitLogReader.ReadRecentLogsAsync(
            request.HabitId,
            cutoff,
            AppConstants.MaxHabitLogsReturned,
            cancellationToken);

        var result = logs
            .Select(l => new HabitLogResponse(l.Id, l.Date, l.Value, l.CreatedAtUtc))
            .ToList();

        return Result.Success<IReadOnlyList<HabitLogResponse>>(result);
    }
}
