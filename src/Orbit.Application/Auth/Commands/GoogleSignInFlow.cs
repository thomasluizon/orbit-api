using Orbit.Domain.Entities;
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
    ISignupJobDispatcher signupJobDispatcher,
    IBackgroundJobClient backgroundJobClient,
    AccountReactivation accountReactivation)
{
    public async Task<Result<LoginResponse>> CompleteAsync(
        string rawEmail, string name, string language, string? referralCode,
        string? googleAccessToken, string? googleRefreshToken, CancellationToken cancellationToken)
    {
        var email = rawEmail.Trim().ToLowerInvariant();
        User? reactivatedUser = null;
        var signupPending = false;
        var signupLanguage = language;
        var signupReferralCode = referralCode;
        var result = await unitOfWork.ExecuteInTransactionAsync(async transactionToken =>
        {
            var findResult = await AuthUserProvisioning.FindOrCreateUserAsync(
                userStore, email, name, language, transactionToken, referralCode);
            if (findResult.IsFailure)
                return findResult.PropagateError<LoginResponse>();

            var user = findResult.Value;
            signupPending = user.SignupLanguage is not null && user.SignupCompletedAtUtc is null;
            signupLanguage = user.SignupLanguage ?? language;
            signupReferralCode = user.SignupLanguage is not null ? user.SignupReferralCode : referralCode;
            var wasReactivated = user.IsDeactivated;
            if (wasReactivated)
            {
                await accountReactivation.ReactivateAsync(user, transactionToken);
                reactivatedUser = user;
            }

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

        if (result.Value.WasReactivated && reactivatedUser is not null)
            CancelSubscriptionOnDeletionJob.EnqueueRestoration(backgroundJobClient, reactivatedUser);

        signupJobDispatcher.Dispatch(result.Value.UserId, signupPending, signupLanguage, signupReferralCode);

        return result;
    }
}
