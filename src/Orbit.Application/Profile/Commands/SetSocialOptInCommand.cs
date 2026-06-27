using MediatR;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Profile.Commands;

public record SetSocialOptInCommand(Guid UserId, bool Enabled) : IRequest<Result>;

public class SetSocialOptInCommandHandler(
    IGenericRepository<User> userRepository,
    IUnitOfWork unitOfWork) : IRequestHandler<SetSocialOptInCommand, Result>
{
    public async Task<Result> Handle(SetSocialOptInCommand request, CancellationToken cancellationToken)
    {
        var user = await userRepository.FindOneTrackedAsync(
            u => u.Id == request.UserId,
            cancellationToken: cancellationToken);

        if (user is null)
            return Result.Failure(ErrorMessages.UserNotFound);

        user.SetSocialOptIn(request.Enabled);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Success();
    }
}
