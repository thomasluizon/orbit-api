using System.Net;
using Microsoft.Extensions.Options;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.Configuration;
using Svix;
using Svix.Exceptions;

namespace Orbit.Infrastructure.Services;

public sealed class ResendWebhookVerifier(IOptions<ResendSettings> settings) : IResendWebhookVerifier
{
    private readonly ResendSettings _settings = settings.Value;

    public ResendWebhookVerification Verify(string payload, string svixId, string svixTimestamp, string svixSignature)
    {
        if (string.IsNullOrEmpty(_settings.WebhookSecret))
            return ResendWebhookVerification.SecretNotConfigured;

        var headers = new WebHeaderCollection
        {
            { "svix-id", svixId },
            { "svix-timestamp", svixTimestamp },
            { "svix-signature", svixSignature },
        };

        try
        {
            new Webhook(_settings.WebhookSecret).Verify(payload, headers);
            return ResendWebhookVerification.Verified;
        }
        catch (WebhookVerificationException)
        {
            return ResendWebhookVerification.InvalidSignature;
        }
    }
}
