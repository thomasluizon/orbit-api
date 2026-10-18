using Hangfire;
using Orbit.Application.Auth.Jobs;
using Microsoft.Extensions.Logging;
using Orbit.Application.Auth.Queries;
using Orbit.Application.Common;
using Orbit.Application.Auth.Services;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Auth.Commands;

public class GoogleSignInFlow(
    IGenericRepository<User> userRepository,
    IUnitOfWork unitOfWork,
    IAuthSessionService authSessionService,
    GoogleSignInNotifications notifications,
    IProductAnalytics productAnalytics,
    ILogger<GoogleSignInFlow> logger,
    IBackgroundJobClient backgroundJobClient)
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
            notifications.SendWelcomeEmailInBackground(user.Id, user.Email, user.Name, language);
            if (!string.IsNullOrWhiteSpace(referralCode))
                notifications.ProcessReferralInBackground(user.Id, referralCode);
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

        if (wasReactivated)
            CancelSubscriptionOnDeletionJob.EnqueueRestoration(backgroundJobClient, user);

        if (isNewUser)
            AnalyticsCapture.SafeCaptureUserEvent(productAnalytics, logger, user, "signup_completed");

        var sessionResult = await authSessionService.CreateSessionAsync(user.Id, user.Email, cancellationToken);
        if (sessionResult.IsFailure)
            return sessionResult.PropagateError<LoginResponse>();

        return Result.Success(new LoginResponse(
            user.Id, sessionResult.Value.AccessToken, user.Name, user.Email,
            wasReactivated, sessionResult.Value.RefreshToken));
    }
}
