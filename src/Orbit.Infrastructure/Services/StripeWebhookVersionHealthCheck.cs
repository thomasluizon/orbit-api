using System.Text.Json;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Stripe;

namespace Orbit.Infrastructure.Services;

/// <summary>
/// Serves <c>/health/stripe</c>: Unhealthy when no enabled webhook endpoint for <see cref="WebhookPath"/> sends events
/// that this build's webhook handler accepts. The release workflow reads it before a production deploy. One compatible
/// endpoint is sufficient, so an old and a new endpoint can overlap. Render probes <c>/health</c>, not this route.
/// </summary>
public sealed partial class StripeWebhookVersionHealthCheck(
    WebhookEndpointService webhookEndpoints,
    TimeProvider timeProvider,
    ILogger<StripeWebhookVersionHealthCheck> logger) : IHealthCheck, IDisposable
{
    public const string WebhookPath = "/api/subscriptions/webhook";

    /// <summary>
    /// Reported for an endpoint with no pinned version. Stripe then sends events at the account default version,
    /// which the API cannot read, so the check never accepts it.
    /// </summary>
    public const string AccountDefaultVersion = "account-default";

    /// <summary>How long one Stripe read is reused, so the anonymous route cannot multiply Stripe API calls.</summary>
    internal static readonly TimeSpan ResultLifetime = TimeSpan.FromMinutes(1);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private HealthCheckResult? _result;
    private long _resultTimestamp;

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_result is { } cached && timeProvider.GetElapsedTime(_resultTimestamp) < ResultLifetime)
                return cached;

            var result = await ReadEndpointsAsync(cancellationToken);
            _result = result;
            _resultTimestamp = timeProvider.GetTimestamp();
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();

    private async Task<HealthCheckResult> ReadEndpointsAsync(CancellationToken cancellationToken)
    {
        var libraryVersion = StripeConfiguration.ApiVersion;
        var endpointVersions = new List<string>();
        try
        {
            var options = new WebhookEndpointListOptions { Limit = 100 };
            await foreach (var endpoint in webhookEndpoints.ListAutoPagingAsync(options, cancellationToken: cancellationToken))
            {
                if (endpoint.Status == "enabled" && DeliversToWebhookPath(endpoint.Url))
                    endpointVersions.Add(endpoint.ApiVersion ?? AccountDefaultVersion);
            }
        }
        catch (Exception ex) when (ex is StripeException or HttpRequestException
            || ex is TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            LogEndpointReadFailed(logger, ex);
            return HealthCheckResult.Degraded(
                "Could not read the Stripe webhook endpoints",
                data: new Dictionary<string, object> { ["libraryApiVersion"] = libraryVersion });
        }

        var data = new Dictionary<string, object>
        {
            ["libraryApiVersion"] = libraryVersion,
            ["endpointApiVersions"] = endpointVersions,
        };
        return endpointVersions.Exists(IsAcceptedByWebhookHandler)
            ? HealthCheckResult.Healthy($"A webhook endpoint sends events that Stripe.net {libraryVersion} accepts", data)
            : HealthCheckResult.Unhealthy(
                $"No enabled webhook endpoint for {WebhookPath} sends events that Stripe.net {libraryVersion} accepts; "
                + $"enabled endpoint versions: [{string.Join(", ", endpointVersions)}]",
                data: data);
    }

    private static bool DeliversToWebhookPath(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && string.Equals(uri.AbsolutePath.TrimEnd('/'), WebhookPath, StringComparison.Ordinal);

    /// <summary>
    /// Asks <see cref="EventUtility.ParseEvent(string, bool)"/> with the <c>throwOnApiVersionMismatch: true</c> that
    /// <c>HandleWebhookCommandHandler</c> passes, so the check accepts exactly the versions the handler accepts.
    /// </summary>
    private static bool IsAcceptedByWebhookHandler(string apiVersion)
    {
        var probe = """{"id":"evt_version_probe","object":"event","api_version":""" + JsonSerializer.Serialize(apiVersion)
            + ""","type":"ping","data":{"object":{}}}""";
        try
        {
            EventUtility.ParseEvent(probe, throwOnApiVersionMismatch: true);
            return true;
        }
        catch (StripeException)
        {
            return false;
        }
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "Could not read the Stripe webhook endpoints for the version check")]
    private static partial void LogEndpointReadFailed(ILogger logger, Exception ex);
}
