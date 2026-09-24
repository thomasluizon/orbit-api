using MediatR;
using Orbit.Application.ApiKeys.Services;
using Orbit.Application.Auth.Services;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.ApiKeys.Commands;

public record ConfirmApiKeyCreationChallengeCommand(Guid UserId, string Code) : IRequest<Result>;

public sealed class ConfirmApiKeyCreationChallengeCommandHandler(
    EmailChallengeService challengeService,
    ApiKeyManagementAuthorization authorization,
    IGenericRepository<User> userRepository) : IRequestHandler<ConfirmApiKeyCreationChallengeCommand, Result>
{
    public async Task<Result> Handle(
        ConfirmApiKeyCreationChallengeCommand request,
        CancellationToken cancellationToken)
    {
        var user = await userRepository.GetByIdAsync(request.UserId, cancellationToken);
        if (user is null)
            return Result.Failure(ErrorMessages.UserNotFound);

        var confirmation = challengeService.Confirm(
            EmailChallengeOperation.ApiKeyManagement,
            user.Email,
            request.Code);
        if (confirmation.IsFailure)
            return confirmation.PropagateError();

        authorization.Grant(request.UserId, confirmation.Value.RemainingLifetime);

        return Result.Success();
    }
}
