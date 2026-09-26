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

public record RepairStreakGapCommand(Guid UserId, IReadOnlyCollection<DateOnly> Dates)
    : IRequest<Result<StreakInfoResponse>>, IConcurrencyRetryable;

public class RepairStreakGapCommandHandler(
    IGenericRepository<User> userRepository,
    IGenericRepository<StreakFreeze> streakFreezeRepository,
    IUserDateService userDateService,
    IUserStreakService userStreakService,
    IFeatureFlagService featureFlagService,
    IUnitOfWork unitOfWork,
    ISender sender,
    ILogger<RepairStreakGapCommandHandler> logger) : IRequestHandler<RepairStreakGapCommand, Result<StreakInfoResponse>>
{
    public async Task<Result<StreakInfoResponse>> Handle(
        RepairStreakGapCommand request, CancellationToken cancellationToken)
    {
        var repaired = await HabitCeilingLock.ExecuteAsync(
            unitOfWork,
            request.UserId,
            async transactionToken =>
            {
                var user = await userRepository.FindOneTrackedAsync(
                    candidate => candidate.Id == request.UserId, cancellationToken: transactionToken);
                if (user is null)
                    return Result.Failure<int>(ErrorMessages.UserNotFound);

                var flags = await featureFlagService.GetEnabledKeysForUserAsync(request.UserId, transactionToken);
                if (!user.HasProAccess && !flags.Contains(FeatureFlagKeys.GamificationFreeTier))
                    return Result.PayGateFailure<int>("Streak insights are a Pro feature. Upgrade to unlock!");

                var today = await userDateService.GetUserTodayAsync(request.UserId, transactionToken);
                var gap = StreakFreeze.CreateGap(request.UserId, request.Dates, today);
                if (gap.IsFailure)
                    return gap.PropagateError<int>();
                if (user.StreakFreezesAccumulated < gap.Value.Count)
                    return Result.Failure<int>(DomainErrors.InsufficientStreakFreezes);

                var state = await userStreakService.EvaluateGapRepairAsync(
                    request.UserId, today, request.Dates, transactionToken);
                if (state is null)
                    return Result.Failure<int>(ErrorMessages.StreakGapRepairUnavailable);

                var spent = user.ConsumeStreakFreezes(gap.Value.Count);
                if (spent.IsFailure)
                    return spent.PropagateError<int>();

                /**
                 * The SCHEDULED predecessor the service validated the gap against, never the previous
                 * calendar day. On a sparse cadence those differ, the cursor match then failed, and the
                 * derived-cursor fallback marked a newly crossed milestone as awarded without granting
                 * its freeze.
                 */
                user.RestoreStreakAfterGapRepair(state.CurrentStreak, state.LongestStreak, state.LastActiveDate,
                    state.PrecedingScheduledDate ?? gap.Value[0].UsedOnDate.AddDays(-1),
                    state.PreGapStreak);
                user.AwardStreakFreezeIfEligible(
                    AppConstants.MaxStreakFreezesAccumulated,
                    AppConstants.StreakDaysPerFreeze);
                foreach (var freeze in gap.Value)
                    await streakFreezeRepository.AddAsync(freeze, transactionToken);

                try
                {
                    await unitOfWork.SaveChangesAsync(transactionToken);
                }
                catch (DbUpdateException exception)
                {
                    unitOfWork.ResetTracking();
                    if (DbUniqueViolation.IsUniqueViolation(exception))
                        return Result.Failure<int>(ErrorMessages.StreakGapRepairUnavailable);
                    throw;
                }

                return Result.Success(gap.Value.Count);
            },
            cancellationToken);

        if (repaired.IsFailure)
            return repaired.PropagateError<StreakInfoResponse>();

        logger.LogInformation("Streak gap repaired for {UserId} using {FreezeCount} freezes", request.UserId, repaired.Value);
        return await sender.Send(new GetStreakInfoQuery(request.UserId), cancellationToken);
    }
}
