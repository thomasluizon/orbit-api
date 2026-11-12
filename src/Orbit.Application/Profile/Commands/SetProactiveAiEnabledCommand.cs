using MediatR;
using Orbit.Application.Behaviors;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Profile.Commands;

public record SetProactiveAiEnabledCommand(Guid UserId, bool Enabled) : IRequest<Result>, IConcurrencyRetryable;

public class SetProactiveAiEnabledCommandHandler(
    IGenericRepository<User> userRepository,
    IPayGateService payGate,
    IUnitOfWork unitOfWork) : IRequestHandler<SetProactiveAiEnabledCommand, Result>
{
    public async Task<Result> Handle(SetProactiveAiEnabledCommand request, CancellationToken cancellationToken)
    {
        var gateCheck = await payGate.CanManageProactiveAi(request.UserId, cancellationToken);
        if (gateCheck.IsFailure)
            return gateCheck;

        var user = await userRepository.FindOneTrackedAsync(
            u => u.Id == request.UserId,
            cancellationToken: cancellationToken);

        if (user is null)
            return Result.Failure(ErrorMessages.UserNotFound);

        user.SetProactiveAiEnabled(request.Enabled);

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Success();
    }
}
