using Hangfire;
using MediatR;
using Microsoft.Extensions.Logging;
using Orbit.Application.Common;
using Orbit.Application.Referrals.Commands;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Auth.Jobs;

/// <summary>
/// Dispatches signup side effects as independently retried jobs outside the sign-in response path.
/// A welcome-email retry cannot repeat referral processing or signup analytics.
/// </summary>
public sealed partial class CompleteSignupJob(
    IGenericRepository<User> userRepository,
    IBackgroundJobClient backgroundJobClient,
    IMediator mediator,
    IProductAnalytics productAnalytics,
    ILogger<CompleteSignupJob> logger,
    IUnitOfWork unitOfWork)
{
    public Task ExecuteAsync(Guid userId, bool isNewUser, string language, string? referralCode)
    {
        if (!isNewUser)
            return Task.CompletedTask;

        return SignupWork.RunAsync(userId, userRepository, unitOfWork, user =>
        {
            user.BeginSignup(language, referralCode);
            if (user.SignupCompletedAtUtc is not null)
                return Task.CompletedTask;

            if (user.SignupReferralCompletedAtUtc is null)
                backgroundJobClient.Enqueue<CompleteSignupJob>(job => job.ProcessReferralAsync(userId, user.SignupReferralCode));
            if (user.SignupAnalyticsCompletedAtUtc is null)
                backgroundJobClient.Enqueue<CompleteSignupJob>(job => job.CaptureSignupAsync(userId));
            var signupLanguage = user.SignupLanguage ?? language;
            if (user.SignupWelcomeCompletedAtUtc is null)
                backgroundJobClient.Enqueue<SendWelcomeEmailJob>(job => job.ExecuteAsync(userId, signupLanguage));

            return Task.CompletedTask;
        });
    }

    public Task ProcessReferralAsync(Guid userId, string? referralCode) =>
        SignupWork.RunAsync(userId, userRepository, unitOfWork, async user =>
        {
            if (user.SignupReferralCompletedAtUtc is not null)
                return;

            var originalReferral = user.SignupLanguage is not null ? user.SignupReferralCode : referralCode;
            if (!string.IsNullOrWhiteSpace(originalReferral))
            {
                try
                {
                    await mediator.Send(new ProcessReferralCodeCommand(user.Id, originalReferral), CancellationToken.None);
                }
                catch (Exception exception)
                {
                    LogReferralProcessingFailed(logger, exception, user.Id);
                }
            }

            if (user.SignupLanguage is not null)
                user.CompleteSignupReferral();
        });

    public Task CaptureSignupAsync(Guid userId) =>
        SignupWork.RunAsync(userId, userRepository, unitOfWork, user =>
        {
            if (user.SignupAnalyticsCompletedAtUtc is not null)
                return Task.CompletedTask;

            AnalyticsCapture.SafeCaptureUserEvent(productAnalytics, logger, user, "signup_completed");
            if (user.SignupLanguage is not null)
                user.CompleteSignupAnalytics();
            return Task.CompletedTask;
        });

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "Referral processing failed for user {UserId}")]
    private static partial void LogReferralProcessingFailed(ILogger logger, Exception exception, Guid userId);
}
