using Sentry;
using Sentry.Protocol;
using Orbit.Infrastructure.AI;

namespace Orbit.Api.Observability;

internal static class SentryEventScrubber
{
    private const string RedactedMarker = "[Filtered]";

    private static readonly string[] SensitiveKeyFragments =
    [
        "password", "passwd", "secret", "token", "apikey", "api_key",
        "authorization", "auth", "cookie", "session", "credential",
        "email", "phone", "ssn", "card", "cvv", "otp", "signature",
        "bearer", "jwt", "refresh",
    ];

    public static SentryEvent? Scrub(SentryEvent sentryEvent, SentryHint hint)
    {
        if (sentryEvent.SentryExceptions?.Any(exception => exception.Mechanism?.Type == "SentryHttpFailedRequestHandler") == true
            && hint.Items.TryGetValue(HintTypes.HttpResponseMessage, out var responseHint)
            && responseHint is HttpResponseMessage { RequestMessage: { } request }
            && request.Options.TryGetValue(ContentModerationService.HandlesFailedResponse, out var handled) && handled)
            return null;

        ScrubUser(sentryEvent);
        ScrubRequest(sentryEvent);
        ScrubResponse(sentryEvent);
        ScrubExtra(sentryEvent);
        return sentryEvent;
    }

    private static void ScrubUser(SentryEvent sentryEvent)
    {
        if (sentryEvent.User is not { } user)
            return;

        user.Email = null;
        user.Username = null;
        user.IpAddress = null;
        user.Other.Clear();
    }

    private static void ScrubRequest(SentryEvent sentryEvent)
    {
        if (sentryEvent.Request is not { } request)
            return;

        request.Headers.Clear();
        request.Cookies = null;
        request.Data = null;
        request.QueryString = null;
    }

    private static void ScrubResponse(SentryEvent sentryEvent)
    {
        foreach (var context in sentryEvent.Contexts.Values)
        {
            if (context is not Response response)
                continue;

            response.Data = null;
            response.Cookies = null;
            response.Headers.Clear();
        }
    }

    private static void ScrubExtra(SentryEvent sentryEvent)
    {
        var sensitiveKeys = sentryEvent.Extra
            .Where(entry => entry.Value is not null && IsSensitiveKey(entry.Key))
            .Select(entry => entry.Key)
            .ToList();

        foreach (var key in sensitiveKeys)
            sentryEvent.SetExtra(key, RedactedMarker);
    }

    private static bool IsSensitiveKey(string key) =>
        SensitiveKeyFragments.Any(fragment =>
            key.Contains(fragment, StringComparison.OrdinalIgnoreCase));
}
