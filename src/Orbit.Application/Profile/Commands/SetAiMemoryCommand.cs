using MediatR;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Profile.Commands;

public record SetAiMemoryCommand(Guid UserId, bool Enabled) : IRequest<Result>;

public class SetAiMemoryCommandHandler(
    IGenericRepository<User> userRepository,
    IUnitOfWork unitOfWork) : IRequestHandler<SetAiMemoryCommand, Result>
{
    public async Task<Result> Handle(SetAiMemoryCommand request, CancellationToken cancellationToken)
    {
        var user = await userRepository.FindOneTrackedAsync(
            u => u.Id == request.UserId,
            cancellationToken: cancellationToken);

        if (user is null)
            return Result.Failure(ErrorMessages.UserNotFound, ErrorCodes.UserNotFound);

        user.SetAiMemory(request.Enabled);

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Success();
    }
}
