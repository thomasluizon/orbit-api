using Hangfire;
using Orbit.Application.Auth.Jobs;
using Orbit.Application.Auth.Queries;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Auth.Commands;

public sealed class AppleSignInFlow(IGenericRepository<User> users, IAuthUserStore userStore, IUnitOfWork unitOfWork,
    IAuthSessionService sessions, ISignupJobDispatcher signupJobs, IBackgroundJobClient backgroundJobs, AccountReactivation reactivation)
{
    public async Task<Result<LoginResponse>> CompleteAsync(AppleIdentity identity, string? name, string language, string? referralCode, CancellationToken cancellationToken)
    {
        User? signedInUser = null;
        string? restoredPlayToken = null;
        var signupPending = false;
        var result = await unitOfWork.ExecuteInTransactionAsync(async ct =>
        {
            await unitOfWork.AcquireAdvisoryLockAsync($"apple-subject:{identity.Subject}", ct);
            var user = await users.FindOneTrackedIgnoringFiltersAsync(u => u.AppleSubject == identity.Subject, ct);
            if (user is null)
            {
                if (!identity.EmailVerified || string.IsNullOrWhiteSpace(identity.Email))
                    return Result.Failure<LoginResponse>(ErrorMessages.InvalidSession);
                var email = identity.Email.Trim().ToLowerInvariant();
                var provision = await AuthUserProvisioning.FindOrCreateUserAsync(userStore, email,
                    string.IsNullOrWhiteSpace(name) ? email.Split('@')[0] : name.Trim(), language, ct, referralCode);
                if (provision.IsFailure)
                    return provision.PropagateError<LoginResponse>();
                user = provision.Value;
                if (user.AppleSubject is not null && user.AppleSubject != identity.Subject)
                    return Result.Failure<LoginResponse>(ErrorMessages.InvalidSession);
            }
            await unitOfWork.AcquireUserRowLockAsync(user.Id, ct);
            var wasReactivated = user.IsDeactivated;
            if (wasReactivated)
                restoredPlayToken = await reactivation.ReactivateAsync(user, ct);
            user.SetAppleIdentity(identity.Subject, identity.RefreshToken, identity.ClientId);
            signupPending = user.SignupLanguage is not null && user.SignupCompletedAtUtc is null;
            await unitOfWork.SaveChangesAsync(ct);
            var session = await sessions.CreateSessionAsync(user.Id, user.Email, ct);
            if (session.IsFailure)
                return session.PropagateError<LoginResponse>();
            signedInUser = user;
            return Result.Success(new LoginResponse(user.Id, session.Value.AccessToken, user.Name, user.Email, wasReactivated, session.Value.RefreshToken));
        }, cancellationToken);
        if (result.IsSuccess && signedInUser is not null)
        {
            if (result.Value.WasReactivated)
            {
                CancelSubscriptionOnDeletionJob.EnqueueRestoration(backgroundJobs, signedInUser);
                NotifyPlayRenewalOnReactivationJob.Enqueue(backgroundJobs, signedInUser.Id, restoredPlayToken);
            }
            signupJobs.Dispatch(signedInUser.Id, signupPending, signedInUser.SignupLanguage ?? language,
                signedInUser.SignupLanguage is not null ? signedInUser.SignupReferralCode : referralCode);
        }
        return result;
    }
}
