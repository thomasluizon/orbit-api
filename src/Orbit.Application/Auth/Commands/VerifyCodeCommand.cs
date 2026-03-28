using MediatR;
using Microsoft.Extensions.Caching.Memory;
using Orbit.Application.Auth.Queries;
using Orbit.Application.Referrals.Commands;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Auth.Commands;

public record VerifyCodeCommand(string Email, string Code, string Language = "en", string? ReferralCode = null)
    : IRequest<Result<LoginResponse>>;

public class VerifyCodeCommandHandler(
    IMemoryCache cache,
    IGenericRepository<User> userRepository,
    IUnitOfWork unitOfWork,
    ITokenService tokenService,
    IEmailService emailService,
    IMediator mediator) : IRequestHandler<VerifyCodeCommand, Result<LoginResponse>>
{
    public async Task<Result<LoginResponse>> Handle(VerifyCodeCommand request, CancellationToken cancellationToken)
    {
        var email = request.Email.Trim().ToLowerInvariant();
        var cacheKey = $"verify:{email}";

        if (!cache.TryGetValue(cacheKey, out VerificationEntry? entry) || entry is null)
            return Result.Failure<LoginResponse>("Verification code expired or not found");

        // Check attempts
        if (entry.Attempts >= 3)
        {
            cache.Remove(cacheKey);
            return Result.Failure<LoginResponse>("Too many attempts. Please request a new code");
        }

        // Validate code
        if (entry.Code != request.Code)
        {
            // Increment attempts
            var updated = new VerificationEntry(entry.Code, entry.Attempts + 1, entry.CreatedAt);
            var remaining = TimeSpan.FromMinutes(5) - (DateTime.UtcNow - entry.CreatedAt);
            if (remaining > TimeSpan.Zero)
            {
                cache.Set(cacheKey, updated, new MemoryCacheEntryOptions
                {
                    AbsoluteExpirationRelativeToNow = remaining
                });
            }
            return Result.Failure<LoginResponse>("Invalid verification code");
        }

        // Code valid - remove from cache
        cache.Remove(cacheKey);

        // Find or create user (tracked so deactivation cancellation persists)
        var user = await userRepository.FindOneTrackedAsync(
            u => u.Email == email,
            cancellationToken: cancellationToken);

        var isNewUser = user is null;

        if (user is null)
        {
            // Create new user with email prefix as name
            var namePart = email.Split('@')[0];
            var createResult = User.Create(namePart, email);
            if (createResult.IsFailure)
                return Result.Failure<LoginResponse>(createResult.Error);

            user = createResult.Value;
            user.SetLanguage(request.Language);
            await userRepository.AddAsync(user, cancellationToken);
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

        // Process referral code for new users (fire and forget -- don't fail login)
        if (isNewUser && !string.IsNullOrWhiteSpace(request.ReferralCode))
        {
            try
            {
                await mediator.Send(new ProcessReferralCodeCommand(user.Id, request.ReferralCode), cancellationToken);
            }
            catch
            {
                // Silently ignore referral processing failures
            }
        }

        // Cancel deactivation if user was deactivated
        var wasReactivated = false;
        if (user.IsDeactivated)
        {
            user.CancelDeactivation();
            await unitOfWork.SaveChangesAsync(cancellationToken);
            wasReactivated = true;
        }

        // Generate JWT
        var token = tokenService.GenerateToken(user.Id, user.Email);

        // Send welcome email for new users (fire and forget)
        if (isNewUser)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await emailService.SendWelcomeEmailAsync(user.Email, user.Name, request.Language, CancellationToken.None);
                }
                catch
                {
                    // Silently ignore email failures
                }
            }, CancellationToken.None);
        }

        return Result.Success(new LoginResponse(user.Id, token, user.Name, user.Email, wasReactivated));
    }
}
