using Hangfire;
using Orbit.Application.Auth.Jobs;
using Orbit.Application.Auth.Queries;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Auth.Commands;

public class GoogleSignInFlow(
    IAuthUserStore userStore,
    IUnitOfWork unitOfWork,
    IAuthSessionService authSessionService,
    IBackgroundJobClient backgroundJobClient)
{
    public async Task<Result<LoginResponse>> CompleteAsync(
        string rawEmail, string name, string language, string? referralCode,
        string? googleAccessToken, string? googleRefreshToken, CancellationToken cancellationToken)
    {
        var email = rawEmail.Trim().ToLowerInvariant();
        var isNewUser = false;
        var result = await unitOfWork.ExecuteInTransactionAsync(async transactionToken =>
        {
            var findResult = await AuthUserProvisioning.FindOrCreateUserAsync(
                userStore, email, name, language, transactionToken);
            if (findResult.IsFailure)
                return findResult.PropagateError<LoginResponse>();

            var (user, created) = findResult.Value;
            isNewUser = created;
            var wasReactivated = user.IsDeactivated;
            if (wasReactivated)
                user.CancelDeactivation();

            if (googleAccessToken is not null)
                user.SetGoogleTokens(googleAccessToken, googleRefreshToken);

            await unitOfWork.SaveChangesAsync(transactionToken);

            var sessionResult = await authSessionService.CreateSessionAsync(user.Id, user.Email, transactionToken);
            if (sessionResult.IsFailure)
                return sessionResult.PropagateError<LoginResponse>();

            return Result.Success(new LoginResponse(
                user.Id, sessionResult.Value.AccessToken, user.Name, user.Email,
                wasReactivated, sessionResult.Value.RefreshToken));
        }, cancellationToken);

        if (result.IsFailure)
            return result;

        backgroundJobClient.Enqueue<CompleteSignupJob>(job => job.ExecuteAsync(
            result.Value.UserId, isNewUser, language, referralCode));

        return result;
    }
}
