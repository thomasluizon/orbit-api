using System.Reflection;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Options;
using Orbit.Api.Extensions;
using Orbit.Application.Auth.Validators;
using Orbit.Application.Common;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;

namespace Orbit.Api.RateLimiting;

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class DistributedRateLimitAttribute(string policyName, bool blockAnswersOnly = false) : Attribute, IFilterFactory
{
    public bool IsReusable => false;

    public IFilterMetadata CreateInstance(IServiceProvider serviceProvider)
        => new DistributedRateLimitFilter(
            policyName,
            serviceProvider.GetRequiredService<IDistributedRateLimitService>(),
            serviceProvider.GetRequiredService<IAuthSessionService>(),
            serviceProvider.GetRequiredService<IOptions<BffForwardingSettings>>(),
            serviceProvider.GetRequiredService<ILogger<DistributedRateLimitFilter>>(), blockAnswersOnly);
}

public sealed partial class DistributedRateLimitFilter(
    string policyName,
    IDistributedRateLimitService distributedRateLimitService,
    IAuthSessionService authSessionService,
    IOptions<BffForwardingSettings> bffForwardingOptions,
    ILogger<DistributedRateLimitFilter> logger,
    bool blockAnswersOnly = false) : IAsyncActionFilter
{
    private const string FailOpenPolicy = "support";

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        if (blockAnswersOnly && (!context.ActionArguments.TryGetValue("blockAnswer", out var answer) || answer is null))
        {
            await next();
            return;
        }
        var partitions = await ResolvePartitionsAsync(
            policyName,
            context.HttpContext,
            context.ActionArguments.Values,
            context.HttpContext.RequestAborted);
        (string PolicyName, string PartitionKey, DistributedRateLimitDecision Decision)? rejection = null;

        foreach (var partition in partitions)
        {
            DistributedRateLimitDecision decision;
            try
            {
                decision = await distributedRateLimitService.TryAcquireAsync(
                    partition.PolicyName,
                    partition.PartitionKey,
                    context.HttpContext.RequestAborted);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                LogRateLimitEvaluationFailed(
                    logger,
                    partition.PolicyName,
                    partition.PartitionKey,
                    context.HttpContext.Request.Method,
                    context.HttpContext.Request.Path,
                    context.HttpContext.GetRequestId(),
                    exception);

                if (string.Equals(policyName, FailOpenPolicy, StringComparison.OrdinalIgnoreCase))
                {
                    await next();
                    return;
                }

                throw;
            }

            if (!decision.Allowed)
                rejection ??= (partition.PolicyName, partition.PartitionKey, decision);
        }

        if (rejection is { } rejected)
        {
            var decision = rejected.Decision;
            var retryAfterSeconds = Math.Max(
                1,
                (int)Math.Ceiling((decision.WindowEndsAtUtc - DateTime.UtcNow).TotalSeconds));
            context.HttpContext.Response.Headers.RetryAfter = Math.Max(
                1,
                retryAfterSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture);
            context.HttpContext.Response.Headers[Orbit.Api.Extensions.HttpContextExtensions.RequestIdHeaderName] = context.HttpContext.GetRequestId();
            LogRateLimitRejected(
                logger,
                rejected.PolicyName,
                rejected.PartitionKey,
                decision.CurrentCount,
                decision.PermitLimit,
                retryAfterSeconds,
                context.HttpContext.Request.Method,
                context.HttpContext.Request.Path,
                context.HttpContext.GetRequestId());
            context.Result = new ObjectResult(ErrorMessages.TooManyRequests.ToErrorBody())
            {
                StatusCode = StatusCodes.Status429TooManyRequests
            };
            return;
        }

        await next();
    }

    private async Task<(string PolicyName, string PartitionKey)[]> ResolvePartitionsAsync(
        string policyName,
        HttpContext context,
        IEnumerable<object?> actionArguments,
        CancellationToken cancellationToken)
    {
        if (context.User.Identity?.IsAuthenticated == true)
            return [(policyName, $"user:{context.GetUserId()}")];

        if (TryResolveEmailPartitionKey(policyName, actionArguments, out var emailPartitionKey))
            return [(policyName, emailPartitionKey), ($"{policyName.ToLowerInvariant()}-ip", $"ip:{GetEmailRequestSourceIp(context) ?? "unknown"}")];

        if (TryExtractRefreshToken(policyName, actionArguments, out var refreshToken)
            && await authSessionService.HasSessionForTokenAsync(refreshToken, cancellationToken))
        {
            return [(policyName, BuildRefreshTokenPartitionKey(policyName, refreshToken))];
        }

        return [(policyName, $"ip:{context.GetClientIpAddress() ?? "unknown"}")];
    }

    /// <summary>
    /// Only a BFF authenticated by its separate forwarding secret may supply an end-client IP.
    /// Direct requests retain the connection IP established by ForwardedHeaders middleware.
    /// Authenticated BFF requests with no single valid client IP share the unknown bucket.
    /// </summary>
    private string? GetEmailRequestSourceIp(HttpContext context)
    {
        var configuredSecret = bffForwardingOptions.Value.BffForwardingSecret;
        var suppliedSecret = context.Request.Headers["X-Orbit-Bff-Secret"];
        if (string.IsNullOrWhiteSpace(configuredSecret)
            || suppliedSecret.Count != 1
            || string.IsNullOrWhiteSpace(suppliedSecret[0])
            || !CryptographicOperations.FixedTimeEquals(
                SHA256.HashData(Encoding.UTF8.GetBytes(configuredSecret)),
                SHA256.HashData(Encoding.UTF8.GetBytes(suppliedSecret.ToString()))))
        {
            return context.GetClientIpAddress();
        }

        var suppliedIp = context.Request.Headers["X-Orbit-Client-Ip"];
        if (suppliedIp.Count != 1 || !IPAddress.TryParse(suppliedIp[0], out var clientIp))
            return null;

        return (clientIp.IsIPv4MappedToIPv6 ? clientIp.MapToIPv4() : clientIp).ToString();
    }

    private static readonly HashSet<string> EmailPartitionedPolicies =
        new(StringComparer.OrdinalIgnoreCase) { "auth", "waitlist" };

    /// <summary>
    /// Anonymous email requests consume both the per-account bucket and the policy's IP bucket.
    /// The email ceiling limits attacks across sources; the IP ceiling limits email rotation
    /// while allowing separate account buckets for callers behind a shared NAT. Either ceiling rejects.
    /// </summary>
    public static bool TryResolveEmailPartitionKey(
        string policyName,
        IEnumerable<object?> actionArguments,
        out string partitionKey)
    {
        partitionKey = string.Empty;

        if (!EmailPartitionedPolicies.Contains(policyName))
            return false;

        foreach (var argument in actionArguments)
        {
            if (argument is null)
                continue;

            var emailProperty = argument.GetType().GetProperty(
                "Email",
                BindingFlags.Public | BindingFlags.Instance);

            if (emailProperty?.PropertyType != typeof(string))
                continue;

            if (emailProperty.GetValue(argument) is not string rawEmail || string.IsNullOrWhiteSpace(rawEmail))
                continue;

            partitionKey = $"{policyName.ToLowerInvariant()}:email:{rawEmail.Trim().ToLowerInvariant()}";
            return true;
        }

        return false;
    }

    private static readonly HashSet<string> RefreshTokenPartitionedPolicies =
        new(StringComparer.OrdinalIgnoreCase) { "refresh" };

    public static bool TryExtractRefreshToken(
        string policyName,
        IEnumerable<object?> actionArguments,
        out string refreshToken)
    {
        refreshToken = string.Empty;

        if (!RefreshTokenPartitionedPolicies.Contains(policyName))
            return false;

        foreach (var argument in actionArguments)
        {
            if (argument is null)
                continue;

            var tokenProperty = argument.GetType().GetProperty(
                "RefreshToken",
                BindingFlags.Public | BindingFlags.Instance);

            if (tokenProperty?.PropertyType != typeof(string))
                continue;

            if (tokenProperty.GetValue(argument) is not string rawToken || !RefreshTokenRules.IsWellFormed(rawToken))
                continue;

            refreshToken = rawToken;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Builds the per-session rate-limit partition key for a refresh token already confirmed to map to a
    /// real stored session. The token is SHA-256 hashed so the partition key (which is logged) never carries
    /// the raw secret; the same token always maps to the same bucket, so a stolen or targeted token cannot
    /// escape throttling by rotating source IPs.
    /// </summary>
    public static string BuildRefreshTokenPartitionKey(string policyName, string refreshToken) =>
        $"{policyName.ToLowerInvariant()}:token:{HashRefreshToken(refreshToken)}";

    private static string HashRefreshToken(string refreshToken) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(refreshToken)));

    // S107 false positive: [LoggerMessage] source-gen binds each parameter to a named {placeholder} in the template, so folding these into a parameter object would drop the structured-log fields. https://learn.microsoft.com/dotnet/core/extensions/logger-message-generator
#pragma warning disable S107 // LogRateLimitRejected maps each policy, partition, count, retry, method, path and request id to its own generated structured-log placeholder. https://github.com/thomasluizon/orbit-tickets/issues/230
    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Warning,
        Message = "Rate limit rejected. Policy={PolicyName} PartitionKey={PartitionKey} Count={CurrentCount}/{PermitLimit} RetryAfterSeconds={RetryAfterSeconds} {Method} {Path} RequestId={RequestId}")]
    private static partial void LogRateLimitRejected(
        ILogger logger,
        string policyName,
        string partitionKey,
        int currentCount,
        int permitLimit,
        int retryAfterSeconds,
        string method,
        string path,
        string requestId);
#pragma warning restore S107

    [LoggerMessage(
        EventId = 2,
        Level = LogLevel.Error,
        Message = "Rate limit evaluation failed. Policy={PolicyName} PartitionKey={PartitionKey} {Method} {Path} RequestId={RequestId}")]
    private static partial void LogRateLimitEvaluationFailed(
        ILogger logger,
        string policyName,
        string partitionKey,
        string method,
        string path,
        string requestId,
        Exception exception);
}

[AttributeUsage(AttributeTargets.Method)]
public sealed class ConcurrentChatLimitAttribute : Attribute, IFilterFactory
{
    public bool IsReusable => false;

    public IFilterMetadata CreateInstance(IServiceProvider serviceProvider)
        => new ConcurrentChatLimitFilter(
            serviceProvider.GetRequiredService<IDistributedRateLimitService>(),
            serviceProvider.GetRequiredService<ILogger<ConcurrentChatLimitFilter>>());
}

public sealed partial class ConcurrentChatLimitFilter(
    IDistributedRateLimitService distributedRateLimitService,
    ILogger<ConcurrentChatLimitFilter> logger) : IAsyncActionFilter
{
    internal const string PolicyName = "chat-in-flight";
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(5);

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var partitionKey = $"user:{context.HttpContext.GetUserId()}";
        var decision = await distributedRateLimitService.TryAcquireLeaseAsync(
            PolicyName,
            partitionKey,
            AppConstants.MaxConcurrentChatRequestsPerUser,
            LeaseDuration,
            context.HttpContext.RequestAborted);

        if (!decision.Allowed)
        {
            var retryAfterSeconds = Math.Max(
                1,
                (int)Math.Ceiling((decision.WindowEndsAtUtc - TimeProvider.System.GetUtcNow().UtcDateTime).TotalSeconds));
            context.HttpContext.Response.Headers.RetryAfter = retryAfterSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
            context.HttpContext.Response.Headers[Orbit.Api.Extensions.HttpContextExtensions.RequestIdHeaderName] = context.HttpContext.GetRequestId();
            LogConcurrentChatRejected(
                logger,
                partitionKey,
                decision.CurrentCount,
                decision.PermitLimit,
                retryAfterSeconds,
                context.HttpContext.Request.Method,
                context.HttpContext.Request.Path,
                context.HttpContext.GetRequestId());
            context.Result = new ObjectResult(ErrorMessages.TooManyRequests.ToErrorBody())
            {
                StatusCode = StatusCodes.Status429TooManyRequests
            };
            return;
        }

        try
        {
            await next();
        }
        finally
        {
            await distributedRateLimitService.ReleaseLeaseAsync(
                PolicyName,
                partitionKey,
                decision.WindowEndsAtUtc,
                CancellationToken.None);
        }
    }

    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Warning,
        Message = "Concurrent chat request rejected. PartitionKey={PartitionKey} Count={CurrentCount}/{PermitLimit} RetryAfterSeconds={RetryAfterSeconds} {Method} {Path} RequestId={RequestId}")]
    private static partial void LogConcurrentChatRejected(
        ILogger logger,
        string partitionKey,
        int currentCount,
        int permitLimit,
        int retryAfterSeconds,
        string method,
        string path,
        string requestId);
}
