using System.Globalization;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Orbit.Application.Behaviors;
using Orbit.Application.Common;
using Orbit.Application.Gamification.Queries;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Gamification.Commands;

public record RepairStreakCommand(Guid UserId)
    : IRequest<Result<StreakInfoResponse>>, IConcurrencyRetryable;

public class RepairStreakCommandHandler(
    IGenericRepository<User> userRepository,
    IGenericRepository<StreakFreeze> streakFreezeRepository,
    IUserDateService userDateService,
    IUserStreakService userStreakService,
    IFeatureFlagService featureFlagService,
    IUnitOfWork unitOfWork,
    ISender sender,
    IProductAnalytics productAnalytics,
    ILogger<RepairStreakCommandHandler> logger)
    : IRequestHandler<RepairStreakCommand, Result<StreakInfoResponse>>
{
    public async Task<Result<StreakInfoResponse>> Handle(
        RepairStreakCommand request,
        CancellationToken cancellationToken)
    {
        var user = await userRepository.FindOneTrackedAsync(
            candidate => candidate.Id == request.UserId,
            cancellationToken: cancellationToken);
        if (user is null)
            return Result.Failure<StreakInfoResponse>(ErrorMessages.UserNotFound);

        var enabledFlags = await featureFlagService.GetEnabledKeysForUserAsync(
            request.UserId,
            cancellationToken);
        var unlocked = user.HasProAccess || enabledFlags.Contains(FeatureFlagKeys.GamificationFreeTier);
        if (!unlocked)
            return Result.PayGateFailure<StreakInfoResponse>("Streak insights are a Pro feature. Upgrade to unlock!");

        var today = await userDateService.GetUserTodayAsync(request.UserId, cancellationToken);
        var missedDate = today.AddDays(-1);
        var alreadyRepaired = await streakFreezeRepository.AnyAsync(
            freeze => freeze.UserId == request.UserId && freeze.UsedOnDate == missedDate,
            cancellationToken);
        if (alreadyRepaired)
            return await sender.Send(new GetStreakInfoQuery(request.UserId), cancellationToken);

        var repair = await userStreakService.EvaluateRepairAsync(
            request.UserId,
            today,
            missedDate,
            cancellationToken);
        if (repair is not { IsAvailable: true, RepairedState: not null }
            || repair.MissedDate != missedDate)
        {
            return Result.Failure<StreakInfoResponse>(ErrorMessages.StreakRepairUnavailable);
        }

        var consumeResult = user.ConsumeStreakFreeze();
        if (consumeResult.IsFailure)
            return Result.Failure<StreakInfoResponse>(ErrorMessages.StreakRepairUnavailable);

        user.SetStreakState(
            repair.RepairedState.CurrentStreak,
            repair.RepairedState.LongestStreak,
            repair.RepairedState.LastActiveDate);
        await streakFreezeRepository.AddAsync(
            StreakFreeze.Create(request.UserId, missedDate),
            cancellationToken);

        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (DbUniqueViolation.IsUniqueViolation(exception))
        {
            unitOfWork.ResetTracking();
            return await sender.Send(new GetStreakInfoQuery(request.UserId), cancellationToken);
        }

        AnalyticsCapture.SafeCaptureUserEvent(
            productAnalytics,
            logger,
            user,
            "streak_repair_spent",
            new Dictionary<string, object>
            {
                ["missed_date"] = missedDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                ["remaining_bank"] = user.StreakFreezesAccumulated
            });

        return await sender.Send(new GetStreakInfoQuery(request.UserId), cancellationToken);
    }
}
