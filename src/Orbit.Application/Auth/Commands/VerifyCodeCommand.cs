using Hangfire;
using MediatR;
using Orbit.Application.Auth.Jobs;
using Orbit.Application.Auth.Services;
using Orbit.Application.Auth.Queries;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Auth.Commands;

public record VerifyCodeCommand(string Email, string Code, string Language = "en", string? ReferralCode = null)
    : IRequest<Result<LoginResponse>>;

public class VerifyCodeCommandHandler(
    IEmailChallengeStore challenges,
    IAuthUserStore userStore,
    IUnitOfWork unitOfWork,
    IAuthSessionService authSessionService,
    ISignupJobDispatcher signupJobDispatcher,
    IBackgroundJobClient backgroundJobClient,
    AccountReactivation accountReactivation) : IRequestHandler<VerifyCodeCommand, Result<LoginResponse>>
{
    public async Task<Result<LoginResponse>> Handle(VerifyCodeCommand request, CancellationToken cancellationToken)
    {
        var email = request.Email.Trim().ToLowerInvariant();

        var codeValidation = await challenges.ConfirmAsync(EmailChallengeOperation.SignIn, email, request.Code, cancellationToken: cancellationToken);
        if (codeValidation.IsFailure)
            return codeValidation.PropagateError<LoginResponse>();

        User? reactivatedUser = null;
        string? reactivatedPlayToken = null;
        var signupPending = false;
        var signupLanguage = request.Language;
        var signupReferralCode = request.ReferralCode;
        var result = await unitOfWork.ExecuteInTransactionAsync(async transactionToken =>
        {
            var findResult = await FindOrCreateUserAsync(email, request.Language, request.ReferralCode, transactionToken);
            if (findResult.IsFailure)
                return findResult.PropagateError<LoginResponse>();

            var user = findResult.Value;
            signupPending = user.SignupLanguage is not null && user.SignupCompletedAtUtc is null;
            signupLanguage = user.SignupLanguage ?? request.Language;
            signupReferralCode = user.SignupLanguage is not null ? user.SignupReferralCode : request.ReferralCode;

            var wasReactivated = user.IsDeactivated;
            if (wasReactivated)
            {
                reactivatedPlayToken = await accountReactivation.ReactivateAsync(user, transactionToken);
                reactivatedUser = user;
            }
            await unitOfWork.SaveChangesAsync(transactionToken);

            var sessionResult = await authSessionService.CreateSessionAsync(user.Id, user.Email, transactionToken);
            if (sessionResult.IsFailure)
                return sessionResult.PropagateError<LoginResponse>();

            return Result.Success(new LoginResponse(
                user.Id,
                sessionResult.Value.AccessToken,
                user.Name,
                user.Email,
                wasReactivated,
                sessionResult.Value.RefreshToken));
        }, cancellationToken);

        if (result.IsFailure)
            return result;

        if (result.Value.WasReactivated && reactivatedUser is not null)
        {
            CancelSubscriptionOnDeletionJob.EnqueueRestoration(backgroundJobClient, reactivatedUser);
            NotifyPlayRenewalOnReactivationJob.Enqueue(backgroundJobClient, reactivatedUser.Id, reactivatedPlayToken);
        }

        signupJobDispatcher.Dispatch(result.Value.UserId, signupPending, signupLanguage, signupReferralCode);

        return result;
    }

    private Task<Result<User>> FindOrCreateUserAsync(
        string email, string language, string? referralCode, CancellationToken cancellationToken) =>
        AuthUserProvisioning.FindOrCreateUserAsync(
            userStore, email, email.Split('@')[0], language, cancellationToken, referralCode);
}
