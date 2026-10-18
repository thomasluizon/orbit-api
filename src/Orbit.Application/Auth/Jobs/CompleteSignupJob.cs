using MediatR;
using Microsoft.Extensions.Logging;
using Orbit.Application.Common;
using Orbit.Application.Referrals.Commands;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Auth.Jobs;

/// <summary>
/// Enqueued once per successful sign-in so signup-only work runs outside the response path.
/// Hangfire resolves fresh scoped collaborators rather than retaining request-scoped services.
/// </summary>
public sealed partial class CompleteSignupJob(
    IGenericRepository<User> userRepository,
    IEmailService emailService,
    IMediator mediator,
    IProductAnalytics productAnalytics,
    ILogger<CompleteSignupJob> logger)
{
    public async Task ExecuteAsync(Guid userId, bool isNewUser, string language, string? referralCode)
    {
        if (!isNewUser)
            return;

        var user = await userRepository.GetByIdAsync(userId, CancellationToken.None);
        if (user is null)
            return;

        if (!string.IsNullOrWhiteSpace(referralCode))
        {
            try
            {
                await mediator.Send(new ProcessReferralCodeCommand(user.Id, referralCode), CancellationToken.None);
            }
            catch (Exception exception)
            {
                LogReferralProcessingFailed(logger, exception, user.Id);
            }
        }

        AnalyticsCapture.SafeCaptureUserEvent(productAnalytics, logger, user, "signup_completed");
        await emailService.SendWelcomeEmailAsync(user.Email, user.Name, language, CancellationToken.None);
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "Referral processing failed for user {UserId}")]
    private static partial void LogReferralProcessingFailed(ILogger logger, Exception exception, Guid userId);
}
