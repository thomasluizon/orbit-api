using MediatR;
using Orbit.Application.Common;
using Orbit.Application.Marketing.Services;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Profile.Commands;

public record UpdateMarketingConsentCommand(Guid UserId, bool Enabled) : IRequest<Result>;

public class UpdateMarketingConsentCommandHandler(
    IGenericRepository<User> userRepository,
    IGenericRepository<MarketingContact> contactRepository,
    IUnitOfWork unitOfWork) : IRequestHandler<UpdateMarketingConsentCommand, Result>
{
    public async Task<Result> Handle(UpdateMarketingConsentCommand request, CancellationToken cancellationToken)
    {
        var user = await userRepository.FindOneTrackedAsync(
            u => u.Id == request.UserId,
            cancellationToken: cancellationToken);

        if (user is null)
            return Result.Failure(ErrorMessages.UserNotFound);

        if (request.Enabled)
        {
            await unitOfWork.ExecuteInTransactionAsync(async ct =>
            {
                var normalizedEmail = user.Email.Trim().ToLowerInvariant();
                await unitOfWork.AcquireAdvisoryLockAsync($"marketing-contact:{normalizedEmail}", ct);
                var contact = await contactRepository.FindOneTrackedAsync(
                    candidate => candidate.Email == normalizedEmail && candidate.Source == "user",
                    cancellationToken: ct);
                contact?.RestoreUserConsent();
                user.SetMarketingConsent(true);
                await unitOfWork.SaveChangesAsync(ct);
            }, cancellationToken);
        }
        else
        {
            await unitOfWork.ExecuteInTransactionAsync(async ct =>
            {
                await MarketingContactOptOut.RecordAsync(user.Email, contactRepository, unitOfWork, ct);
                user.SetMarketingConsent(false);
                await unitOfWork.SaveChangesAsync(ct);
            }, cancellationToken);
        }

        return Result.Success();
    }
}
