using MediatR;
using Microsoft.EntityFrameworkCore;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Gamification.Commands;

public record StreakFreezeResponse(
    int FreezesRemainingThisMonth,
    DateOnly FrozenDate,
    int CurrentStreak);

public record ActivateStreakFreezeCommand(Guid UserId) : IRequest<Result<StreakFreezeResponse>>;

public class ActivateStreakFreezeCommandHandler(
    IGenericRepository<User> userRepository,
    IGenericRepository<StreakFreeze> streakFreezeRepository,
    IGenericRepository<HabitLog> habitLogRepository,
    IGenericRepository<Habit> habitRepository,
    IUserDateService userDateService,
    IUnitOfWork unitOfWork) : IRequestHandler<ActivateStreakFreezeCommand, Result<StreakFreezeResponse>>
{
    private const int MaxFreezesPerMonth = 3;

    public async Task<Result<StreakFreezeResponse>> Handle(ActivateStreakFreezeCommand request, CancellationToken cancellationToken)
    {
        var user = await userRepository.FindOneTrackedAsync(
            u => u.Id == request.UserId,
            cancellationToken: cancellationToken);

        if (user is null)
            return Result.Failure<StreakFreezeResponse>(ErrorMessages.UserNotFound, ErrorCodes.UserNotFound);

        var today = await userDateService.GetUserTodayAsync(request.UserId, cancellationToken);

        // Validate streak > 0
        if (user.CurrentStreak <= 0)
            return Result.Failure<StreakFreezeResponse>(ErrorMessages.NoActiveStreak, ErrorCodes.NoActiveStreak);

        // Check if user already logged a habit today
        var userHabits = await habitRepository.FindAsync(h => h.UserId == request.UserId, cancellationToken);
        var habitIds = userHabits.Select(h => h.Id).ToList();

        if (habitIds.Count > 0)
        {
            var todayLogs = await habitLogRepository.FindAsync(
                l => habitIds.Contains(l.HabitId) && l.Date == today && l.Value > 0,
                cancellationToken);

            if (todayLogs.Count > 0)
                return Result.Failure<StreakFreezeResponse>("You already completed a habit today. No freeze needed!");
        }

        // Check if already frozen today
        var existingFreeze = await streakFreezeRepository.FindAsync(
            sf => sf.UserId == request.UserId && sf.UsedOnDate == today,
            cancellationToken);

        if (existingFreeze.Count > 0)
            return Result.Failure<StreakFreezeResponse>(ErrorMessages.AlreadyUsedStreakFreezeToday, ErrorCodes.AlreadyUsedStreakFreezeToday);

        // Count freezes in rolling 30-day window
        var windowStart = today.AddDays(-29);
        var recentFreezes = await streakFreezeRepository.FindAsync(
            sf => sf.UserId == request.UserId && sf.UsedOnDate >= windowStart,
            cancellationToken);

        if (recentFreezes.Count >= MaxFreezesPerMonth)
            return Result.Failure<StreakFreezeResponse>(ErrorMessages.StreakFreezeNotAvailable, ErrorCodes.StreakFreezeNotAvailable);

        // Create freeze
        var freeze = StreakFreeze.Create(request.UserId, today);
        await streakFreezeRepository.AddAsync(freeze, cancellationToken);

        // Preserve streak by bridging the gap
        user.ApplyStreakFreeze(today);

        await unitOfWork.SaveChangesAsync(cancellationToken);

        var freezesRemaining = MaxFreezesPerMonth - (recentFreezes.Count + 1);

        return Result.Success(new StreakFreezeResponse(
            freezesRemaining,
            today,
            user.CurrentStreak));
    }
}
