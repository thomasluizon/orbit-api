using MediatR;
using Microsoft.EntityFrameworkCore;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Habits.Commands;

public record SubHabitCompletion(Guid SubHabitId, bool IsCompleted);

public record LogSubHabitCommand(
    Guid UserId,
    Guid HabitId,
    DateOnly Date,
    IReadOnlyList<SubHabitCompletion> Completions) : IRequest<Result>;

public class LogSubHabitCommandHandler(
    IGenericRepository<Habit> habitRepository,
    IGenericRepository<SubHabitLog> subHabitLogRepository,
    IUnitOfWork unitOfWork) : IRequestHandler<LogSubHabitCommand, Result>
{
    public async Task<Result> Handle(LogSubHabitCommand request, CancellationToken cancellationToken)
    {
        var habit = await habitRepository.FindOneTrackedAsync(
            h => h.Id == request.HabitId,
            q => q.Include(h => h.SubHabits),
            cancellationToken);

        if (habit is null)
            return Result.Failure("Habit not found.");

        if (habit.UserId != request.UserId)
            return Result.Failure("Habit does not belong to this user.");

        var completions = request.Completions
            .Select(c => (c.SubHabitId, c.IsCompleted))
            .ToList();

        var logResult = habit.LogSubHabitCompletions(request.Date, completions);

        if (logResult.IsFailure)
            return Result.Failure(logResult.Error);

        foreach (var log in logResult.Value)
        {
            await subHabitLogRepository.AddAsync(log, cancellationToken);
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Success();
    }
}
