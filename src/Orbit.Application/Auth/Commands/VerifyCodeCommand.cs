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
    ITokenService tokenService,
    IEmailService emailService,
    IMediator mediator,
    ILogger<VerifyCodeCommandHandler> logger) : IRequestHandler<VerifyCodeCommand, Result<LoginResponse>>
{
    public async Task<Result<LoginResponse>> Handle(VerifyCodeCommand request, CancellationToken cancellationToken)
    {
        var email = request.Email.Trim().ToLowerInvariant();

        var codeValidation = ValidateCode(email, request.Code);
        if (codeValidation.IsFailure)
            return Result.Failure<LoginResponse>(codeValidation.Error, codeValidation.ErrorCode!);

        var findResult = await FindOrCreateUserAsync(email, request.Language, cancellationToken);
        if (findResult.IsFailure)
            return Result.Failure<LoginResponse>(findResult.Error);

        var (user, isNewUser) = findResult.Value;

        var wasReactivated = await HandlePostLoginAsync(user, isNewUser, request, cancellationToken);

        var token = tokenService.GenerateToken(user.Id, user.Email);

        return Result.Success(new LoginResponse(user.Id, token, user.Name, user.Email, wasReactivated));
    }

    private Result ValidateCode(string email, string code)
    {
        var cacheKey = $"verify:{email}";

        if (!cache.TryGetValue(cacheKey, out VerificationEntry? entry) || entry is null)
            return Result.Failure("Verification code expired or not found", ErrorCodes.CodeExpired);

        if (entry.Attempts >= AppConstants.MaxVerificationAttempts)
        {
            cache.Remove(cacheKey);
            return Result.Failure("Too many attempts. Please request a new code", ErrorCodes.TooManyAttempts);
        }

        if (!CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(entry.Code),
            System.Text.Encoding.UTF8.GetBytes(code)))
        {
            RecordFailedAttempt(cacheKey, entry);
            return Result.Failure("Invalid verification code", ErrorCodes.InvalidVerificationCode);
        }

        cache.Remove(cacheKey);
        return Result.Success();
    }

    private void RecordFailedAttempt(string cacheKey, VerificationEntry entry)
    {
        var updated = new VerificationEntry(entry.Code, entry.Attempts + 1, entry.CreatedAt);
        var remaining = TimeSpan.FromMinutes(5) - (DateTime.UtcNow - entry.CreatedAt);
        if (remaining > TimeSpan.Zero)
        {
            cache.Set(cacheKey, updated, new MemoryCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = remaining
            });
        }
    }

    private async Task<Result<(User User, bool IsNew)>> FindOrCreateUserAsync(
        string email, string language, CancellationToken cancellationToken)
    {
        var user = await userRepository.FindOneTrackedAsync(
            u => u.Email == email,
            cancellationToken: cancellationToken);

        if (user is not null)
            return Result.Success((user, false));

        var namePart = email.Split('@')[0];
        var createResult = User.Create(namePart, email);
        if (createResult.IsFailure)
            return Result.Failure<(User, bool)>(createResult.Error);

        user = createResult.Value;
        user.SetLanguage(language);
        await userRepository.AddAsync(user, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Success((user, true));
    }

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
            SendWelcomeEmailInBackground(user.Email, user.Name, request.Language);

        return wasReactivated;
    }

    private async Task ProcessReferralSafeAsync(Guid userId, string referralCode, CancellationToken cancellationToken)
    {
        try
        {
            await mediator.Send(new ProcessReferralCodeCommand(userId, referralCode), cancellationToken);
        }
        catch (Exception ex)
        {
            LogReferralProcessingFailed(logger, ex, userId);
        }
    }

    private void SendWelcomeEmailInBackground(string email, string name, string language)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await emailService.SendWelcomeEmailAsync(email, name, language, CancellationToken.None);
            }
            catch (Exception ex)
            {
                LogWelcomeEmailFailed(logger, ex, email);
            }
        }, CancellationToken.None);
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "Referral processing failed for user {UserId}")]
    private static partial void LogReferralProcessingFailed(ILogger logger, Exception ex, Guid userId);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "Welcome email failed for user {Email}")]
    private static partial void LogWelcomeEmailFailed(ILogger logger, Exception ex, string email);
}
