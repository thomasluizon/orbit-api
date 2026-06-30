using MediatR;
using Orbit.Application.Behaviors;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Profile.Commands;

public record SetProactiveAstraEnabledCommand(Guid UserId, bool Enabled) : IRequest<Result>, IConcurrencyRetryable;

public class SetProactiveAstraEnabledCommandHandler(
    IGenericRepository<User> userRepository,
    IPayGateService payGate,
    IUnitOfWork unitOfWork) : IRequestHandler<SetProactiveAstraEnabledCommand, Result>
{
    public async Task<Result> Handle(SetProactiveAstraEnabledCommand request, CancellationToken cancellationToken)
    {
        var gateCheck = await payGate.CanManageProactiveAstra(request.UserId, cancellationToken);
        if (gateCheck.IsFailure)
            return gateCheck;

        var user = await userRepository.FindOneTrackedAsync(
            u => u.Id == request.UserId,
            cancellationToken: cancellationToken);

        if (user is null)
            return Result.Failure(ErrorMessages.UserNotFound);

        user.SetProactiveAstraEnabled(request.Enabled);

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Success();
    }
}
