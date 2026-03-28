using MediatR;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Referrals.Commands;

public record ProcessReferralCodeCommand(Guid NewUserId, string ReferralCode) : IRequest<Result>;

public class ProcessReferralCodeCommandHandler(
    IGenericRepository<User> userRepository,
    IGenericRepository<Referral> referralRepository,
    IAppConfigService appConfigService,
    IReferralRewardService referralRewardService,
    IUnitOfWork unitOfWork) : IRequestHandler<ProcessReferralCodeCommand, Result>
{
    public async Task<Result> Handle(ProcessReferralCodeCommand request, CancellationToken cancellationToken)
    {
        var referrer = await userRepository.FindOneTrackedAsync(
            u => u.ReferralCode == request.ReferralCode,
            cancellationToken: cancellationToken);

        if (referrer is null)
            return Result.Failure(ErrorMessages.InvalidReferralCode);

        if (referrer.Id == request.NewUserId)
            return Result.Failure(ErrorMessages.SelfReferral);

        var newUser = await userRepository.FindOneTrackedAsync(
            u => u.Id == request.NewUserId,
            cancellationToken: cancellationToken);

        if (newUser is null)
            return Result.Failure(ErrorMessages.UserNotFound);

        if (newUser.ReferredByUserId is not null)
            return Result.Failure(ErrorMessages.AlreadyReferred);

        // Count referrer's successful referrals (Completed or Rewarded)
        var successfulReferrals = await referralRepository.FindAsync(
            r => r.ReferrerId == referrer.Id && r.Status != ReferralStatus.Pending,
            cancellationToken: cancellationToken);

        var maxReferrals = await appConfigService.GetAsync(AppConfigKeys.MaxReferrals, AppConstants.DefaultMaxReferrals, cancellationToken);
        if (successfulReferrals.Count >= maxReferrals)
            return Result.Failure(ErrorMessages.ReferralCapReached);

        newUser.SetReferredBy(referrer.Id);

        var referral = Referral.Create(referrer.Id, newUser.Id);
        await referralRepository.AddAsync(referral, cancellationToken);

        // Create a 10% discount coupon for the referred user
        var promoCodeId = await referralRewardService.CreateReferralCouponAsync(
            newUser.Id, cancellationToken);
        newUser.SetReferralCoupon(promoCodeId);

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Success();
    }
}
