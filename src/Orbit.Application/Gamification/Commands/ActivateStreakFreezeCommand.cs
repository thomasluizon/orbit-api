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
    int CurrentStreak,
    int StreakFreezesAccumulated);

public record ActivateStreakFreezeCommand(Guid UserId) : IRequest<Result<StreakFreezeResponse>>;

public class ActivateStreakFreezeCommandHandler(
    IGenericRepository<User> userRepository,
    IGenericRepository<StreakFreeze> streakFreezeRepository,
    IGenericRepository<HabitLog> habitLogRepository,
    IGenericRepository<Habit> habitRepository,
    IUserStreakService userStreakService,
    IUserDateService userDateService,
    IUnitOfWork unitOfWork) : IRequestHandler<ActivateStreakFreezeCommand, Result<StreakFreezeResponse>>
{
    public async Task<Result<StreakFreezeResponse>> Handle(ActivateStreakFreezeCommand request, CancellationToken cancellationToken)
    {
        var user = await userRepository.FindOneTrackedAsync(
            u => u.Id == request.UserId,
            cancellationToken: cancellationToken);

        if (user is null)
            return Result.Failure<StreakFreezeResponse>(ErrorMessages.UserNotFound, ErrorCodes.UserNotFound);

        var existingStreak = await userStreakService.RecalculateAsync(request.UserId, cancellationToken);
        if (existingStreak is null)
            return Result.Failure<StreakFreezeResponse>(ErrorMessages.UserNotFound, ErrorCodes.UserNotFound);

        var today = await userDateService.GetUserTodayAsync(request.UserId, cancellationToken);

        if (existingStreak.CurrentStreak <= 0)
            return Result.Failure<StreakFreezeResponse>(ErrorMessages.NoActiveStreak, ErrorCodes.NoActiveStreak);

        if (user.StreakFreezesAccumulated <= 0)
            return Result.Failure<StreakFreezeResponse>(ErrorMessages.StreakFreezeNotAvailable, ErrorCodes.StreakFreezeNotAvailable);

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

        var existingFreeze = await streakFreezeRepository.FindAsync(
            sf => sf.UserId == request.UserId && sf.UsedOnDate == today,
            cancellationToken);

        if (existingFreeze.Count > 0)
            return Result.Failure<StreakFreezeResponse>(ErrorMessages.AlreadyUsedStreakFreezeToday, ErrorCodes.AlreadyUsedStreakFreezeToday);

        var monthStart = new DateOnly(today.Year, today.Month, 1);
        var monthEnd = monthStart.AddMonths(1);
        var freezesThisMonth = await streakFreezeRepository.FindAsync(
            sf => sf.UserId == request.UserId && sf.UsedOnDate >= monthStart && sf.UsedOnDate < monthEnd,
            cancellationToken);

        if (freezesThisMonth.Count >= AppConstants.MaxStreakFreezesPerMonth)
            return Result.Failure<StreakFreezeResponse>(ErrorMessages.StreakFreezeMonthlyLimit, ErrorCodes.StreakFreezeMonthlyLimit);

        var consume = user.ConsumeStreakFreeze();
        if (consume.IsFailure)
            return Result.Failure<StreakFreezeResponse>(consume.Error!, ErrorCodes.StreakFreezeNotAvailable);

        var freeze = StreakFreeze.Create(request.UserId, today);
        await streakFreezeRepository.AddAsync(freeze, cancellationToken);

        await unitOfWork.SaveChangesAsync(cancellationToken);
        var updatedStreak = await userStreakService.RecalculateAsync(request.UserId, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        var freezesRemaining = Math.Max(0, AppConstants.MaxStreakFreezesPerMonth - (freezesThisMonth.Count + 1));

        return Result.Success(new StreakFreezeResponse(
            freezesRemaining,
            today,
            updatedStreak?.CurrentStreak ?? 0,
            user.StreakFreezesAccumulated));
    }
}
