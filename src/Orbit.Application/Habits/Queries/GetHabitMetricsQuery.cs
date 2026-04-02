using MediatR;
using Microsoft.EntityFrameworkCore;
using Orbit.Application.Common;
using Orbit.Application.Habits.Services;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;

namespace Orbit.Application.Habits.Queries;

public record GetHabitMetricsQuery(Guid UserId, Guid HabitId) : IRequest<Result<HabitMetrics>>;

public class GetHabitMetricsQueryHandler(
    IGenericRepository<Habit> habitRepository,
    IGenericRepository<User> userRepository) : IRequestHandler<GetHabitMetricsQuery, Result<HabitMetrics>>
{
    public async Task<Result<HabitMetrics>> Handle(GetHabitMetricsQuery request, CancellationToken cancellationToken)
    {
        var habit = await habitRepository.FindOneTrackedAsync(
            h => h.Id == request.HabitId && h.UserId == request.UserId,
            q => q.Include(h => h.Logs),
            cancellationToken);
        if (habit is null)
            return Result.Failure<HabitMetrics>(ErrorMessages.HabitNotFound, ErrorCodes.HabitNotFound);

        var user = await userRepository.GetByIdAsync(request.UserId, cancellationToken);
        if (user is null)
            return Result.Failure<HabitMetrics>(ErrorMessages.UserNotFound, ErrorCodes.UserNotFound);

        var userTimeZone = user.TimeZone is not null
            ? TimeZoneInfo.FindSystemTimeZoneById(user.TimeZone)
            : TimeZoneInfo.Utc;
        var today = HabitMetricsCalculator.GetUserToday(user);
        return Result.Success(HabitMetricsCalculator.Calculate(habit, today, userTimeZone));

    }
}
