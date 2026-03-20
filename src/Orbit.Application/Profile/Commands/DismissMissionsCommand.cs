using MediatR;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Profile.Commands;

public record DismissMissionsCommand(Guid UserId) : IRequest<Result>;

public class DismissMissionsCommandHandler(
    IGenericRepository<User> userRepository,
    IUnitOfWork unitOfWork) : IRequestHandler<DismissMissionsCommand, Result>
{
    public async Task<Result> Handle(DismissMissionsCommand request, CancellationToken cancellationToken)
    {
        var user = await userRepository.FindOneTrackedAsync(
            u => u.Id == request.UserId,
            cancellationToken: cancellationToken);

        if (user is null)
            return Result.Failure(ErrorMessages.UserNotFound);

        user.DismissMissions();

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Success();
    }
}
