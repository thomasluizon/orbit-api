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
    ILogger<CompleteSignupJob> logger)
{
    public Task ExecuteAsync(Guid userId, bool isNewUser, string language, string? referralCode)
    {
        if (isNewUser)
        {
            backgroundJobClient.Enqueue<CompleteSignupJob>(job => job.ProcessReferralAsync(userId, referralCode));
            backgroundJobClient.Enqueue<CompleteSignupJob>(job => job.CaptureSignupAsync(userId));
            backgroundJobClient.Enqueue<SendWelcomeEmailJob>(job => job.ExecuteAsync(userId, language));
        }

        return Task.CompletedTask;
    }

    public async Task ProcessReferralAsync(Guid userId, string? referralCode)
    {
        if (string.IsNullOrWhiteSpace(referralCode))
            return;

        var user = await userRepository.GetByIdAsync(userId, CancellationToken.None);
        if (user is null)
            return;

        try
        {
            await mediator.Send(new ProcessReferralCodeCommand(user.Id, referralCode), CancellationToken.None);
        }
        catch (Exception exception)
        {
            LogReferralProcessingFailed(logger, exception, user.Id);
        }
    }

    public async Task CaptureSignupAsync(Guid userId)
    {
        var user = await userRepository.GetByIdAsync(userId, CancellationToken.None);
        if (user is not null)
            AnalyticsCapture.SafeCaptureUserEvent(productAnalytics, logger, user, "signup_completed");
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "Referral processing failed for user {UserId}")]
    private static partial void LogReferralProcessingFailed(ILogger logger, Exception exception, Guid userId);
}
