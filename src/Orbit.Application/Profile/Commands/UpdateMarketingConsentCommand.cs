using MediatR;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Profile.Commands;

public record UpdateMarketingConsentCommand(Guid UserId, bool Enabled) : IRequest<Result>;

public class UpdateMarketingConsentCommandHandler(
    IGenericRepository<User> userRepository,
    IUnitOfWork unitOfWork) : IRequestHandler<UpdateMarketingConsentCommand, Result>
{
    public async Task<Result> Handle(UpdateMarketingConsentCommand request, CancellationToken cancellationToken)
    {
        var user = await userRepository.FindOneTrackedAsync(
            u => u.Id == request.UserId,
            cancellationToken: cancellationToken);

        if (user is null)
            return Result.Failure(ErrorMessages.UserNotFound);

        user.SetMarketingConsent(request.Enabled);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Success();
    }
}
