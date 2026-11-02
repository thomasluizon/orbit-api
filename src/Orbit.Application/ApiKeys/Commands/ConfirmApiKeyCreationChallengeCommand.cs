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

        var confirmation = await challengeService.ConfirmAsync(
            EmailChallengeOperation.ApiKeyManagement,
            user.Email,
            request.Code,
            cancellationToken);
        if (confirmation.IsFailure)
            return confirmation.PropagateError();

        await authorization.GrantAsync(request.UserId, confirmation.Value.RemainingLifetime);

        return Result.Success();
    }
}
