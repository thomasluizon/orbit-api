using Hangfire;
using MediatR;
using Orbit.Application.Auth.Jobs;
using Microsoft.Extensions.Caching.Memory;
using Orbit.Application.Auth.Queries;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using System.Security.Cryptography;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Auth.Commands;

public record VerifyCodeCommand(string Email, string Code, string Language = "en", string? ReferralCode = null)
    : IRequest<Result<LoginResponse>>;

public class VerifyCodeCommandHandler(
    IMemoryCache cache,
    IAuthUserStore userStore,
    IUnitOfWork unitOfWork,
    IAuthSessionService authSessionService,
    ISignupJobDispatcher signupJobDispatcher,
    IBackgroundJobClient backgroundJobClient) : IRequestHandler<VerifyCodeCommand, Result<LoginResponse>>
{
    public async Task<Result<LoginResponse>> Handle(VerifyCodeCommand request, CancellationToken cancellationToken)
    {
        var email = request.Email.Trim().ToLowerInvariant();

        var codeValidation = ValidateCode(email, request.Code);
        if (codeValidation.IsFailure)
            return codeValidation.PropagateError<LoginResponse>();

        User? reactivatedUser = null;
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
                user.CancelDeactivation();
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
            CancelSubscriptionOnDeletionJob.EnqueueRestoration(backgroundJobClient, reactivatedUser);

        signupJobDispatcher.Dispatch(result.Value.UserId, signupPending, signupLanguage, signupReferralCode);

        return result;
    }

    private Result ValidateCode(string email, string code)
    {
        var cacheKey = $"verify:{email}";

        if (CountFailedAttempts(email) >= AppConstants.MaxVerificationAttempts)
            return Result.Failure(ErrorMessages.TooManyCodeAttempts);

        if (!cache.TryGetValue(cacheKey, out VerificationEntry? entry) || entry is null)
            return Result.Failure(ErrorMessages.VerificationCodeExpired);

        if (!CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(entry.Code),
            System.Text.Encoding.UTF8.GetBytes(code)))
        {
            RecordFailedAttempt(email);
            return Result.Failure(ErrorMessages.InvalidVerificationCode);
        }

        cache.Remove(cacheKey);
        return Result.Success();
    }

    private int CountFailedAttempts(string email) =>
        cache.TryGetValue(FailedAttemptCacheKey(email), out int attempts) ? attempts : 0;

    private void RecordFailedAttempt(string email)
    {
        var cacheKey = FailedAttemptCacheKey(email);
        var attempts = CountFailedAttempts(email) + 1;
        cache.Set(cacheKey, attempts, new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(AppConstants.VerificationAttemptWindowMinutes)
        });
    }

    private static string FailedAttemptCacheKey(string email) => $"verify-attempts:{email}";

    private Task<Result<User>> FindOrCreateUserAsync(
        string email, string language, string? referralCode, CancellationToken cancellationToken) =>
        AuthUserProvisioning.FindOrCreateUserAsync(
            userStore, email, email.Split('@')[0], language, cancellationToken, referralCode);
}
