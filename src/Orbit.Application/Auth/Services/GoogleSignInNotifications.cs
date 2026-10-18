using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orbit.Application.Referrals.Commands;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Auth.Services;

public partial class GoogleSignInNotifications(
    IEmailService emailService,
    IServiceScopeFactory scopeFactory,
    ILogger<GoogleSignInNotifications> logger)
{
    public void SendWelcomeEmailInBackground(Guid userId, string email, string name, string language)
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

    public void ProcessReferralInBackground(Guid userId, string referralCode)
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
