using MediatR;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Profile.Commands;

public record MarkTourCompletedCommand(Guid UserId, string PageName) : IRequest<Result>;

public class MarkTourCompletedCommandHandler(
    IGenericRepository<User> userRepository,
    IUnitOfWork unitOfWork) : IRequestHandler<MarkTourCompletedCommand, Result>
{
    public async Task<Result> Handle(MarkTourCompletedCommand request, CancellationToken cancellationToken)
    {
        var user = await userRepository.FindOneTrackedAsync(
            u => u.Id == request.UserId,
            cancellationToken: cancellationToken);

        if (user is null)
            return Result.Failure("User not found.");

        user.MarkTourCompleted(request.PageName);

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Success();
    }
}
