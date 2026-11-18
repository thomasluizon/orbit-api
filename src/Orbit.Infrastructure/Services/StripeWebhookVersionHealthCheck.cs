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
    /// Reported for an endpoint with no pinned version. Stripe then sends events at the account default version. The
    /// check cannot read that default through the API, and a Dashboard upgrade can change it outside a release, so the
    /// check never accepts an endpoint without a pinned version.
    /// </summary>
    public const string AccountDefaultVersion = "account-default";

    /// <summary>
    /// How long one completed Stripe read is reused. All callers share one read, and a caller's cancellation does not
    /// cancel it, so the anonymous route makes at most one Stripe read per lifetime.
    /// </summary>
    internal static readonly TimeSpan ResultLifetime = TimeSpan.FromMinutes(1);

    private readonly Lock _readLock = new();
    private readonly CancellationTokenSource _disposal = new();
    private Task<HealthCheckResult>? _read;
    private long _readCompletedTimestamp;

    /// <summary>
    /// Waits on the shared read. <paramref name="cancellationToken"/> stops only this wait; the read continues and its
    /// result serves the next caller. The Stripe client timeout limits the read, and <see cref="Dispose"/> cancels it.
    /// </summary>
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        Task<HealthCheckResult> read;
        lock (_readLock)
        {
            if (_read is null || (_read.IsCompleted && timeProvider.GetElapsedTime(_readCompletedTimestamp) >= ResultLifetime))
                _read = ReadAndStampAsync(_disposal.Token);
            read = _read;
        }

        return read.WaitAsync(cancellationToken);
    }

    public void Dispose()
    {
        _disposal.Cancel();
        _disposal.Dispose();
    }

    private async Task<HealthCheckResult> ReadAndStampAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await ReadEndpointsAsync(cancellationToken);
        }
        finally
        {
            _readCompletedTimestamp = timeProvider.GetTimestamp();
        }
    }

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
