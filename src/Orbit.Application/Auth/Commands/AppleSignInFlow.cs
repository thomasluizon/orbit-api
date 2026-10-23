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
            var resolved = await ResolveUserAsync(identity, name, language, referralCode, ct);
            if (resolved.IsFailure)
                return resolved.PropagateError<LoginResponse>();
            var user = resolved.Value;
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
            DispatchJobs(signedInUser, result.Value.WasReactivated, restoredPlayToken, signupPending, language, referralCode);
        return result;
    }

    private async Task<Result<User>> ResolveUserAsync(AppleIdentity identity, string? name, string language, string? referralCode, CancellationToken cancellationToken)
    {
        var user = await users.FindOneTrackedIgnoringFiltersAsync(u => u.AppleSubject == identity.Subject, cancellationToken);
        if (user is not null)
            return Result.Success(user);
        if (!identity.EmailVerified || string.IsNullOrWhiteSpace(identity.Email))
            return Result.Failure<User>(ErrorMessages.InvalidSession);
        var email = identity.Email.Trim().ToLowerInvariant();
        var provision = await AuthUserProvisioning.FindOrCreateUserAsync(userStore, email,
            string.IsNullOrWhiteSpace(name) ? email.Split('@')[0] : name.Trim(), language, cancellationToken, referralCode);
        if (provision.IsFailure)
            return provision;
        user = provision.Value;
        return user.AppleSubject is not null && user.AppleSubject != identity.Subject
            ? Result.Failure<User>(ErrorMessages.InvalidSession) : provision;
    }

    private void DispatchJobs(User user, bool wasReactivated, string? restoredPlayToken, bool signupPending, string language, string? referralCode)
    {
        if (wasReactivated)
        {
            CancelSubscriptionOnDeletionJob.EnqueueRestoration(backgroundJobs, user);
            NotifyPlayRenewalOnReactivationJob.Enqueue(backgroundJobs, user.Id, restoredPlayToken);
        }
        signupJobs.Dispatch(user.Id, signupPending, user.SignupLanguage ?? language,
            user.SignupLanguage is not null ? user.SignupReferralCode : referralCode);
    }
}
