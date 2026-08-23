using Hangfire;
using MediatR;
using Orbit.Application.ApiKeys.Jobs;
using Orbit.Application.Auth.Services;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.ApiKeys.Commands;

public record RequestApiKeyCreationChallengeCommand(Guid UserId) : IRequest<Result>;

public sealed class RequestApiKeyCreationChallengeCommandHandler(
    EmailChallengeService challengeService,
    IGenericRepository<User> userRepository,
    IBackgroundJobClient backgroundJobClient) : IRequestHandler<RequestApiKeyCreationChallengeCommand, Result>
{
    public async Task<Result> Handle(
        RequestApiKeyCreationChallengeCommand request,
        CancellationToken cancellationToken)
    {
        var user = await userRepository.GetByIdAsync(request.UserId, cancellationToken);
        if (user is null)
            return Result.Failure(ErrorMessages.UserNotFound);

        var challenge = challengeService.Issue(EmailChallengeOperation.ApiKeyCreation, user.Email);
        if (challenge.IsFailure)
            return challenge.PropagateError();

        backgroundJobClient.Enqueue<SendApiKeyCreationCodeEmailJob>(
            job => job.ExecuteAsync(user.Email, challenge.Value, user.Language ?? "en"));

        return Result.Success();
    }
}
