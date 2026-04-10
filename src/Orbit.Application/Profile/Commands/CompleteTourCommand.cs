using MediatR;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Profile.Commands;

public record CompleteTourCommand(Guid UserId) : IRequest<Result>;

public class CompleteTourCommandHandler(
    IGenericRepository<User> userRepository,
    IUnitOfWork unitOfWork) : IRequestHandler<CompleteTourCommand, Result>
{
    public async Task<Result> Handle(CompleteTourCommand request, CancellationToken cancellationToken)
    {
        var user = await userRepository.FindOneTrackedAsync(
            u => u.Id == request.UserId,
            cancellationToken: cancellationToken);

        if (user is null)
            return Result.Failure(ErrorMessages.UserNotFound, ErrorCodes.UserNotFound);

        user.CompleteTour();

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Success();
    }
}
