using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orbit.Application.Auth.Queries;
using Orbit.Application.Common;
using Orbit.Application.Referrals.Commands;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Auth.Commands;

public partial class GoogleSignInFlow(
    IGenericRepository<User> userRepository,
    IUnitOfWork unitOfWork,
    IAuthSessionService authSessionService,
    IEmailService emailService,
    IServiceScopeFactory scopeFactory,
    IProductAnalytics productAnalytics,
    ILogger<GoogleSignInFlow> logger)
{
    public async Task<Result<LoginResponse>> CompleteAsync(
        string rawEmail, string name, string language, string? referralCode,
        string? googleAccessToken, string? googleRefreshToken, CancellationToken cancellationToken)
    {
        var email = rawEmail.Trim().ToLowerInvariant();
        var findResult = await AuthUserProvisioning.FindOrCreateUserAsync(
            userRepository, unitOfWork, email, name, language, cancellationToken);
        if (findResult.IsFailure)
            return findResult.PropagateError<LoginResponse>();

        var (user, isNewUser) = findResult.Value;
        if (isNewUser)
        {
            SendWelcomeEmailInBackground(user.Id, user.Email, user.Name, language);
            if (!string.IsNullOrWhiteSpace(referralCode))
                ProcessReferralInBackground(user.Id, referralCode);
        }

        var wasReactivated = false;
        if (user.IsDeactivated)
        {
            user.CancelDeactivation();
            wasReactivated = true;
        }

        if (googleAccessToken is not null)
            user.SetGoogleTokens(googleAccessToken, googleRefreshToken);

        if (wasReactivated || googleAccessToken is not null)
            await unitOfWork.SaveChangesAsync(cancellationToken);

        if (isNewUser)
            AnalyticsCapture.SafeCaptureUserEvent(productAnalytics, logger, user, "signup_completed");

        var sessionResult = await authSessionService.CreateSessionAsync(user.Id, user.Email, cancellationToken);
        if (sessionResult.IsFailure)
            return sessionResult.PropagateError<LoginResponse>();

        return Result.Success(new LoginResponse(
            user.Id, sessionResult.Value.AccessToken, user.Name, user.Email,
            wasReactivated, sessionResult.Value.RefreshToken));
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

    private void ProcessReferralInBackground(Guid userId, string referralCode)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var scopedMediator = scope.ServiceProvider.GetRequiredService<IMediator>();
                await scopedMediator.Send(new ProcessReferralCodeCommand(userId, referralCode), CancellationToken.None);
            }
            catch (Exception ex)
            {
                LogReferralProcessingFailed(logger, ex, userId);
            }
        }, CancellationToken.None);
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "Welcome email failed for user {UserId}")]
    private static partial void LogWelcomeEmailFailed(ILogger logger, Exception ex, Guid userId);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "Referral processing failed for user {UserId}")]
    private static partial void LogReferralProcessingFailed(ILogger logger, Exception ex, Guid userId);
}
