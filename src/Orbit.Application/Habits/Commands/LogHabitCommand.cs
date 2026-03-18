using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Habits.Commands;

public record LogHabitCommand(
    Guid UserId,
    Guid HabitId,
    string? Note = null) : IRequest<Result<Guid>>;

public class LogHabitCommandHandler(
    IGenericRepository<Habit> habitRepository,
    IGenericRepository<HabitLog> habitLogRepository,
    IUserDateService userDateService,
    IUnitOfWork unitOfWork,
    IMemoryCache cache) : IRequestHandler<LogHabitCommand, Result<Guid>>
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

        var today = await userDateService.GetUserTodayAsync(request.UserId, cancellationToken);

        // Toggle: if already logged for today, unlog it
        var existingLog = habit.Logs.FirstOrDefault(l => l.Date == today);
        if (existingLog is not null)
        {
            var unlogResult = habit.Unlog(today);
            if (unlogResult.IsFailure)
                return Result.Failure<Guid>(unlogResult.Error);

            habitLogRepository.Remove(unlogResult.Value);

            // If a child was unlogged, also unlog the auto-completed parent
            await TryUnlogParent(habit, today, cancellationToken);

            await unitOfWork.SaveChangesAsync(cancellationToken);

            var utcToday = DateOnly.FromDateTime(DateTime.UtcNow);
            for (int i = -1; i <= 1; i++)
            {
                cache.Remove($"summary:{habit.UserId}:{utcToday.AddDays(i):yyyy-MM-dd}:en");
                cache.Remove($"summary:{habit.UserId}:{utcToday.AddDays(i):yyyy-MM-dd}:pt-BR");
            }

            return Result.Success(unlogResult.Value.Id);
        }

        var logResult = habit.Log(today, request.Note);

        if (logResult.IsFailure)
            return Result.Failure<Guid>(logResult.Error);

        await habitLogRepository.AddAsync(logResult.Value, cancellationToken);

        // Auto-complete parent when all children are done (recursive up the tree)
        await TryAutoCompleteParent(habit, today, cancellationToken);

        await unitOfWork.SaveChangesAsync(cancellationToken);

        var utcDate = DateOnly.FromDateTime(DateTime.UtcNow);
        for (int i = -1; i <= 1; i++)
        {
            cache.Remove($"summary:{habit.UserId}:{utcDate.AddDays(i):yyyy-MM-dd}:en");
            cache.Remove($"summary:{habit.UserId}:{utcDate.AddDays(i):yyyy-MM-dd}:pt-BR");
        }

        return Result.Success(logResult.Value.Id);
    }

    private async Task TryAutoCompleteParent(Habit child, DateOnly today, CancellationToken ct)
    {
        if (child.ParentHabitId is null) return;

        var parent = await habitRepository.FindOneTrackedAsync(
            h => h.Id == child.ParentHabitId.Value,
            q => q.Include(h => h.Logs)
                  .Include(h => h.Children).ThenInclude(c => c.Logs),
            ct);

        if (parent is null || parent.IsCompleted) return;

        // Only auto-log if the parent is actually due today (or overdue)
        if (parent.DueDate > today) return;

        // Check if ALL children are done for today (logged today or permanently completed)
        var allChildrenDone = parent.Children.All(c =>
            c.IsCompleted || c.Logs.Any(l => l.Date == today));
        if (!allChildrenDone) return;

        // Auto-log the parent
        var alreadyLogged = parent.Logs.Any(l => l.Date == today);
        if (!alreadyLogged)
        {
            var logResult = parent.Log(today);
            if (logResult.IsSuccess)
                await habitLogRepository.AddAsync(logResult.Value, ct);
        }

        // Recurse up the tree
        await TryAutoCompleteParent(parent, today, ct);
    }

    private async Task TryUnlogParent(Habit child, DateOnly today, CancellationToken ct)
    {
        if (child.ParentHabitId is null) return;

        var parent = await habitRepository.FindOneTrackedAsync(
            h => h.Id == child.ParentHabitId.Value,
            q => q.Include(h => h.Logs),
            ct);

        if (parent is null) return;

        // If parent was logged today, unlog it since a child is no longer done
        var parentLog = parent.Logs.FirstOrDefault(l => l.Date == today);
        if (parentLog is null) return;

        var unlogResult = parent.Unlog(today);
        if (unlogResult.IsSuccess)
            habitLogRepository.Remove(unlogResult.Value);

        // Recurse up the tree
        await TryUnlogParent(parent, today, ct);
    }

}
