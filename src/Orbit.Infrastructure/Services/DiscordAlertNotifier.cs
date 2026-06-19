using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.Configuration;

namespace Orbit.Infrastructure.Services;

/// <summary>
/// Posts critical alerts to a Discord channel webhook. No-ops when the webhook URL is unconfigured,
/// so a missing Render env var disables alerting instead of crashing. Scrubs email addresses and
/// bearer/JWT tokens out of every field before serialization so an exception message cannot leak
/// PII into the channel, and never lets a webhook failure mask the original error it was reporting.
/// </summary>
public sealed partial class DiscordAlertNotifier(
    IHttpClientFactory httpClientFactory,
    IOptions<DiscordAlertSettings> options,
    ILogger<DiscordAlertNotifier> logger) : IAlertNotifier
{
    private const int MaxFieldLength = 1000;
    private static readonly string[] AllowedContextKeys =
        ["Method", "Path", "RequestId", "ClientIp", "UserId"];

    private readonly DiscordAlertSettings _settings = options.Value;

    public async Task SendCriticalAsync(
        string title,
        string detail,
        IReadOnlyDictionary<string, string?> context,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_settings.WebhookUrl))
            return;

        var payload = BuildPayload(title, detail, context);
        var content = new StringContent(
            JsonSerializer.Serialize(payload),
            Encoding.UTF8,
            "application/json");

        try
        {
            var client = httpClientFactory.CreateClient("Discord");
            var response = await client.PostAsync(_settings.WebhookUrl, content, cancellationToken);
            if (!response.IsSuccessStatusCode)
                LogAlertRejected(logger, response.StatusCode);
        }
        catch (Exception exception)
        {
            LogAlertFailed(logger, exception);
        }
    }

    private static object BuildPayload(
        string title,
        string detail,
        IReadOnlyDictionary<string, string?> context)
    {
        var fields = AllowedContextKeys
            .Where(key => context.TryGetValue(key, out var value) && !string.IsNullOrEmpty(value))
            .Select(key => new
            {
                name = key,
                value = Scrub(Truncate(context[key]!)),
                inline = true,
            })
            .ToArray();

        return new
        {
            content = Scrub(Truncate(title)),
            embeds = new[]
            {
                new
                {
                    title = "Critical error",
                    description = Scrub(Truncate(detail)),
                    color = 15548997,
                    fields,
                },
            },
        };
    }

    private static string Truncate(string value) =>
        value.Length <= MaxFieldLength ? value : value[..MaxFieldLength];

    private static string Scrub(string value)
    {
        var withoutEmails = EmailPattern().Replace(value, "[redacted-email]");
        return BearerTokenPattern().Replace(withoutEmails, "[redacted-token]");
    }

    [GeneratedRegex(@"[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}", RegexOptions.Compiled)]
    private static partial Regex EmailPattern();

    [GeneratedRegex(@"(?i)bearer\s+[A-Za-z0-9._\-]+|eyJ[A-Za-z0-9._\-]+", RegexOptions.Compiled)]
    private static partial Regex BearerTokenPattern();

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "Discord alert rejected with status {Status}")]
    private static partial void LogAlertRejected(ILogger logger, System.Net.HttpStatusCode status);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "Discord alert failed to send")]
    private static partial void LogAlertFailed(ILogger logger, Exception exception);
}
