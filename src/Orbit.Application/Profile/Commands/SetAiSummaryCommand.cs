using MediatR;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Profile.Commands;

public record SetAiSummaryCommand(Guid UserId, bool Enabled) : IRequest<Result>;

public class SetAiSummaryCommandHandler(
    IGenericRepository<User> userRepository,
    IPayGateService payGate,
    IUnitOfWork unitOfWork) : IRequestHandler<SetAiSummaryCommand, Result>
{
    public async Task<Result> Handle(SetAiSummaryCommand request, CancellationToken cancellationToken)
    {
        var gateCheck = await payGate.CanManageAiSummary(request.UserId, cancellationToken);
        if (gateCheck.IsFailure)
            return gateCheck;

        var user = await userRepository.FindOneTrackedAsync(
            u => u.Id == request.UserId,
            cancellationToken: cancellationToken);

        if (user is null)
            return Result.Failure(ErrorMessages.UserNotFound, ErrorCodes.UserNotFound);

        user.SetAiSummary(request.Enabled);

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Success();
    }
}
