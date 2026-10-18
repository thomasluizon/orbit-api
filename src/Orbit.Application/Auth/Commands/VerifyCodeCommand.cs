using MediatR;
using Microsoft.Extensions.Caching.Memory;
using Hangfire;
using Orbit.Application.Auth.Jobs;
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
    IBackgroundJobClient backgroundJobClient) : IRequestHandler<VerifyCodeCommand, Result<LoginResponse>>
{
    public async Task<Result<LoginResponse>> Handle(VerifyCodeCommand request, CancellationToken cancellationToken)
    {
        var email = request.Email.Trim().ToLowerInvariant();

        var codeValidation = ValidateCode(email, request.Code);
        if (codeValidation.IsFailure)
            return codeValidation.PropagateError<LoginResponse>();

        var isNewUser = false;
        var result = await unitOfWork.ExecuteInTransactionAsync(async transactionToken =>
        {
            var findResult = await FindOrCreateUserAsync(email, request.Language, transactionToken);
            if (findResult.IsFailure)
                return findResult.PropagateError<LoginResponse>();

            var (user, created) = findResult.Value;
            isNewUser = created;

            var wasReactivated = user.IsDeactivated;
            if (wasReactivated)
                user.CancelDeactivation();
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

        backgroundJobClient.Enqueue<CompleteSignupJob>(job => job.ExecuteAsync(
            result.Value.UserId, isNewUser, request.Language, request.ReferralCode));

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

    private Task<Result<(User User, bool IsNew)>> FindOrCreateUserAsync(
        string email, string language, CancellationToken cancellationToken) =>
        AuthUserProvisioning.FindOrCreateUserAsync(
            userStore, email, email.Split('@')[0], language, cancellationToken);
}
