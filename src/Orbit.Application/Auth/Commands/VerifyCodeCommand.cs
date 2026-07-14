using MediatR;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Orbit.Application.Auth.Queries;
using Orbit.Application.Common;
using Orbit.Application.Referrals.Commands;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using System.Security.Cryptography;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Auth.Commands;

public record VerifyCodeCommand(string Email, string Code, string Language = "en", string? ReferralCode = null)
    : IRequest<Result<LoginResponse>>;

public partial class VerifyCodeCommandHandler(
    IMemoryCache cache,
    IGenericRepository<User> userRepository,
    IUnitOfWork unitOfWork,
    IAuthSessionService authSessionService,
    IEmailService emailService,
    IMediator mediator,
    ILogger<VerifyCodeCommandHandler> logger) : IRequestHandler<VerifyCodeCommand, Result<LoginResponse>>
{
    public async Task<Result<LoginResponse>> Handle(VerifyCodeCommand request, CancellationToken cancellationToken)
    {
        var email = request.Email.Trim().ToLowerInvariant();

        var codeValidation = ValidateCode(email, request.Code);
        if (codeValidation.IsFailure)
            return codeValidation.PropagateError<LoginResponse>();

        var findResult = await FindOrCreateUserAsync(email, request.Language, cancellationToken);
        if (findResult.IsFailure)
            return findResult.PropagateError<LoginResponse>();

        var (user, isNewUser) = findResult.Value;

        var wasReactivated = await HandlePostLoginAsync(user, isNewUser, request, cancellationToken);

        var sessionResult = await authSessionService.CreateSessionAsync(user.Id, user.Email, cancellationToken);
        if (sessionResult.IsFailure)
            return sessionResult.PropagateError<LoginResponse>();

        return Result.Success(new LoginResponse(
            user.Id,
            sessionResult.Value.AccessToken,
            user.Name,
            user.Email,
            wasReactivated,
            sessionResult.Value.RefreshToken));
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
            userRepository, unitOfWork, email, email.Split('@')[0], language, cancellationToken);

    private async Task<bool> HandlePostLoginAsync(
        User user, bool isNewUser, VerifyCodeCommand request, CancellationToken cancellationToken)
    {
        if (isNewUser && !string.IsNullOrWhiteSpace(request.ReferralCode))
            await ProcessReferralSafeAsync(user.Id, request.ReferralCode, cancellationToken);

        var wasReactivated = false;
        if (user.IsDeactivated)
        {
            user.CancelDeactivation();
            await unitOfWork.SaveChangesAsync(cancellationToken);
            wasReactivated = true;
        }

        if (isNewUser)
            SendWelcomeEmailInBackground(user.Id, user.Email, user.Name, request.Language);

        return wasReactivated;
    }

    private async Task ProcessReferralSafeAsync(Guid userId, string referralCode, CancellationToken cancellationToken)
    {
        try
        {
            await mediator.Send(new ProcessReferralCodeCommand(userId, referralCode), cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogReferralProcessingFailed(logger, ex, userId);
        }
    }

    private void SendWelcomeEmailInBackground(Guid userId, string email, string name, string language)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await emailService.SendWelcomeEmailAsync(email, name, language, CancellationToken.None);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogWelcomeEmailFailed(logger, ex, userId);
            }
        }, CancellationToken.None);
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "Referral processing failed for user {UserId}")]
    private static partial void LogReferralProcessingFailed(ILogger logger, Exception ex, Guid userId);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "Welcome email failed for user {UserId}")]
    private static partial void LogWelcomeEmailFailed(ILogger logger, Exception ex, Guid userId);
}
