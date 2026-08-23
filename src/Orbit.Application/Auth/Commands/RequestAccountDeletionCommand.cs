using Hangfire;
using MediatR;
using Orbit.Application.Auth.Jobs;
using Orbit.Application.Auth.Services;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Auth.Commands;

public record RequestAccountDeletionCommand(Guid UserId) : IRequest<Result>;

public class RequestAccountDeletionCommandHandler(
    EmailChallengeService challengeService,
    IGenericRepository<User> userRepository,
    IBackgroundJobClient backgroundJobClient) : IRequestHandler<RequestAccountDeletionCommand, Result>
{
    public async Task<Result> Handle(RequestAccountDeletionCommand request, CancellationToken cancellationToken)
    {
        var user = await userRepository.GetByIdAsync(request.UserId, cancellationToken);
        if (user is null)
            return Result.Failure(ErrorMessages.UserNotFound);

        var challenge = challengeService.Issue(EmailChallengeOperation.AccountDeletion, user.Email);
        if (challenge.IsFailure)
            return challenge.PropagateError();

        backgroundJobClient.Enqueue<SendAccountDeletionCodeEmailJob>(
            job => job.ExecuteAsync(user.Email, challenge.Value, user.Language ?? "en"));

        return Result.Success();
    }
}
