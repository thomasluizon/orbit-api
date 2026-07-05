using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Marketing.Commands;

public record SendMarketingBroadcastCommand(
    string SubjectEn,
    string SubjectPt,
    string BodyHtmlEn,
    string BodyHtmlPt,
    string? TestEmail) : IRequest<Result<MarketingBroadcastResult>>;

public record MarketingBroadcastResult(int RecipientCount, bool WasTest);

public partial class SendMarketingBroadcastCommandHandler(
    IGenericRepository<User> userRepository,
    IEmailService emailService,
    IMarketingUnsubscribeTokenService unsubscribeTokenService,
    IServiceScopeFactory scopeFactory,
    IOptions<MarketingSettings> marketingSettings,
    ILogger<SendMarketingBroadcastCommandHandler> logger)
    : IRequestHandler<SendMarketingBroadcastCommand, Result<MarketingBroadcastResult>>
{
    private readonly MarketingSettings _settings = marketingSettings.Value;

    public async Task<Result<MarketingBroadcastResult>> Handle(
        SendMarketingBroadcastCommand request, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(request.TestEmail))
        {
            var preview = RenderFor(request, "en");
            var previewUrl = BuildUnsubscribeUrl(unsubscribeTokenService.CreateToken(Guid.Empty), "en");
            await emailService.SendMarketingEmailAsync(
                request.TestEmail, preview.Subject, preview.BodyHtml, "en", previewUrl, cancellationToken);

            LogPreviewSent(logger, request.TestEmail);
            return Result.Success(new MarketingBroadcastResult(RecipientCount: 1, WasTest: true));
        }

        var audience = await userRepository.FindAsync(
            user => user.MarketingEmailConsent == true, cancellationToken);

        var recipients = audience
            .Select(user => new MarketingRecipient(user.Id, user.Email, user.Language ?? "en"))
            .ToList();

        LogBroadcastQueued(logger, recipients.Count, request.SubjectEn, DateTime.UtcNow);
        FanOutInBackground(request, recipients);

        return Result.Success(new MarketingBroadcastResult(recipients.Count, WasTest: false));
    }

    private void FanOutInBackground(SendMarketingBroadcastCommand request, IReadOnlyList<MarketingRecipient> recipients)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var bgEmailService = scope.ServiceProvider.GetRequiredService<IEmailService>();
                var bgTokenService = scope.ServiceProvider.GetRequiredService<IMarketingUnsubscribeTokenService>();
                var bgLogger = scope.ServiceProvider.GetRequiredService<ILogger<SendMarketingBroadcastCommandHandler>>();

                foreach (var recipient in recipients)
                {
                    var content = RenderFor(request, recipient.Language);
                    var unsubscribeUrl = BuildUnsubscribeUrl(bgTokenService.CreateToken(recipient.UserId), recipient.Language);
                    await bgEmailService.SendMarketingEmailAsync(
                        recipient.Email, content.Subject, content.BodyHtml, recipient.Language, unsubscribeUrl, CancellationToken.None);

                    if (_settings.SendDelayMilliseconds > 0)
                        await Task.Delay(_settings.SendDelayMilliseconds, CancellationToken.None);
                }

                LogBroadcastCompleted(bgLogger, recipients.Count);
            }
            catch (Exception ex)
            {
                LogBroadcastFailed(logger, ex, recipients.Count);
            }
        }, CancellationToken.None);
    }

    private static RenderedContent RenderFor(SendMarketingBroadcastCommand request, string language) =>
        LocaleHelper.IsPortuguese(language)
            ? new RenderedContent(request.SubjectPt, request.BodyHtmlPt)
            : new RenderedContent(request.SubjectEn, request.BodyHtmlEn);

    private string BuildUnsubscribeUrl(string token, string language) =>
        $"{_settings.ApiBaseUrl.TrimEnd('/')}/api/marketing/unsubscribe" +
        $"?token={Uri.EscapeDataString(token)}&lang={(LocaleHelper.IsPortuguese(language) ? "pt" : "en")}";

    private sealed record MarketingRecipient(Guid UserId, string Email, string Language);

    private sealed record RenderedContent(string Subject, string BodyHtml);

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Marketing broadcast preview sent to {TestEmail}")]
    private static partial void LogPreviewSent(ILogger logger, string testEmail);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "Marketing broadcast queued: {RecipientCount} recipients, subject={Subject}, at {QueuedAtUtc:o}")]
    private static partial void LogBroadcastQueued(ILogger logger, int recipientCount, string subject, DateTime queuedAtUtc);

    [LoggerMessage(EventId = 3, Level = LogLevel.Information, Message = "Marketing broadcast fan-out completed for {RecipientCount} recipients")]
    private static partial void LogBroadcastCompleted(ILogger logger, int recipientCount);

    [LoggerMessage(EventId = 4, Level = LogLevel.Error, Message = "Marketing broadcast fan-out failed after queuing {RecipientCount} recipients")]
    private static partial void LogBroadcastFailed(ILogger logger, Exception ex, int recipientCount);
}
