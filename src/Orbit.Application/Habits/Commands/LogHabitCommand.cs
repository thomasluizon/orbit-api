using MediatR;
using Microsoft.EntityFrameworkCore;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Habits.Commands;

public record LogHabitCommand(
    Guid UserId,
    Guid HabitId,
    decimal? Value,
    string? Note = null) : IRequest<Result<Guid>>;

public class LogHabitCommandHandler(
    IGenericRepository<Habit> habitRepository,
    IGenericRepository<HabitLog> habitLogRepository,
    IGenericRepository<User> userRepository,
    IUnitOfWork unitOfWork) : IRequestHandler<LogHabitCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(LogHabitCommand request, CancellationToken cancellationToken)
    {
        var habit = await habitRepository.FindOneTrackedAsync(
            h => h.Id == request.HabitId,
            q => q.Include(h => h.Logs),
            cancellationToken);

        if (habit is null)
            return Result.Failure<Guid>("Habit not found.");

        if (habit.UserId != request.UserId)
            return Result.Failure<Guid>("Habit does not belong to this user.");

        var user = await userRepository.GetByIdAsync(request.UserId, cancellationToken);
        var today = GetUserToday(user);

        var logResult = habit.Log(today, request.Value, request.Note);

        if (logResult.IsFailure)
            return Result.Failure<Guid>(logResult.Error);

        await habitLogRepository.AddAsync(logResult.Value, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Success(logResult.Value.Id);
    }

    private static DateOnly GetUserToday(User? user)
    {
        var timeZone = user?.TimeZone is not null
            ? TimeZoneInfo.FindSystemTimeZoneById(user.TimeZone)
            : TimeZoneInfo.Utc;

        return DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, timeZone));
    }
}
