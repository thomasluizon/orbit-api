using MediatR;
using Orbit.Application.Behaviors;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Profile.Commands;

public record SetMascotMotionCommand(Guid UserId, bool Enabled) : IRequest<Result>, IConcurrencyRetryable;

public class SetMascotMotionCommandHandler(
    IGenericRepository<User> userRepository,
    IUnitOfWork unitOfWork) : IRequestHandler<SetMascotMotionCommand, Result>
{
    public async Task<Result> Handle(SetMascotMotionCommand request, CancellationToken cancellationToken)
    {
        var user = await userRepository.FindOneTrackedAsync(
            u => u.Id == request.UserId,
            cancellationToken: cancellationToken);

        if (user is null)
            return Result.Failure(ErrorMessages.UserNotFound);

        user.SetMascotMotion(request.Enabled);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Success();
    }
}
