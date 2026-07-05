using System.Text.Json;
using MediatR;
using Microsoft.Extensions.Logging;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Resend.Commands;

public record HandleResendWebhookCommand(string Payload, string SvixId, string SvixTimestamp, string SvixSignature)
    : IRequest<Result>;

public partial class HandleResendWebhookCommandHandler(
    IResendWebhookVerifier verifier,
    IGenericRepository<User> userRepository,
    IUnitOfWork unitOfWork,
    ILogger<HandleResendWebhookCommandHandler> logger) : IRequestHandler<HandleResendWebhookCommand, Result>
{
    public async Task<Result> Handle(HandleResendWebhookCommand request, CancellationToken cancellationToken)
    {
        switch (verifier.Verify(request.Payload, request.SvixId, request.SvixTimestamp, request.SvixSignature))
        {
            case ResendWebhookVerification.SecretNotConfigured:
                LogSecretNotConfigured(logger);
                return Result.Failure(ErrorMessages.ResendWebhookSecretNotConfigured);
            case ResendWebhookVerification.InvalidSignature:
                LogInvalidSignature(logger);
                return Result.Failure(ErrorMessages.InvalidResendWebhookSignature);
        }

        if (!TryParseUnsubscribedContact(request.Payload, out var email))
            return Result.Success();

        var normalizedEmail = email.ToLowerInvariant();
        var user = await userRepository.FindOneTrackedAsync(
            u => u.Email.ToLower() == normalizedEmail,
            cancellationToken: cancellationToken);

        if (user is null)
        {
            LogUnknownContact(logger);
            return Result.Success();
        }

        user.SetMarketingConsent(false);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        LogConsentRevoked(logger, user.Id);
        return Result.Success();
    }

    private static bool TryParseUnsubscribedContact(string payload, out string email)
    {
        email = "";
        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;

            if (!root.TryGetProperty("type", out var type) || type.GetString() != "contact.updated")
                return false;
            if (!root.TryGetProperty("data", out var data))
                return false;
            if (!data.TryGetProperty("unsubscribed", out var unsubscribed) || unsubscribed.ValueKind != JsonValueKind.True)
                return false;
            if (!data.TryGetProperty("email", out var emailElement) || emailElement.ValueKind != JsonValueKind.String)
                return false;

            var value = emailElement.GetString();
            if (string.IsNullOrWhiteSpace(value))
                return false;

            email = value;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Critical, Message = "Resend WebhookSecret is not configured -- rejecting webhook")]
    private static partial void LogSecretNotConfigured(ILogger logger);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "Resend webhook signature verification failed")]
    private static partial void LogInvalidSignature(ILogger logger);

    [LoggerMessage(EventId = 3, Level = LogLevel.Information, Message = "Resend contact.updated for an email with no matching user -- ignoring")]
    private static partial void LogUnknownContact(ILogger logger);

    [LoggerMessage(EventId = 4, Level = LogLevel.Information, Message = "Marketing consent revoked for user {UserId} via Resend unsubscribe webhook")]
    private static partial void LogConsentRevoked(ILogger logger, Guid userId);
}
