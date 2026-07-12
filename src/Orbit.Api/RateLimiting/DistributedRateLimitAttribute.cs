using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Orbit.Api.Extensions;
using Orbit.Application.Auth.Validators;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;

namespace Orbit.Api.RateLimiting;

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class DistributedRateLimitAttribute(string policyName) : Attribute, IFilterFactory
{
    public bool IsReusable => false;

    public IFilterMetadata CreateInstance(IServiceProvider serviceProvider)
        => new DistributedRateLimitFilter(
            policyName,
            serviceProvider.GetRequiredService<IDistributedRateLimitService>(),
            serviceProvider.GetRequiredService<IAuthSessionService>(),
            serviceProvider.GetRequiredService<ILogger<DistributedRateLimitFilter>>());
}

public sealed partial class DistributedRateLimitFilter(
    string policyName,
    IDistributedRateLimitService distributedRateLimitService,
    IAuthSessionService authSessionService,
    ILogger<DistributedRateLimitFilter> logger) : IAsyncActionFilter
{
    private const string FailOpenPolicy = "support";

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var partitionKey = await ResolvePartitionKeyAsync(
            policyName,
            context.HttpContext,
            context.ActionArguments.Values,
            context.HttpContext.RequestAborted);
        DistributedRateLimitDecision decision;

        try
        {
            decision = await distributedRateLimitService.TryAcquireAsync(
                policyName,
                partitionKey,
                context.HttpContext.RequestAborted);
        }
        catch (Exception exception)
        {
            LogRateLimitEvaluationFailed(
                logger,
                policyName,
                partitionKey,
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
        {
            var retryAfterSeconds = Math.Max(
                1,
                (int)Math.Ceiling((decision.WindowEndsAtUtc - DateTime.UtcNow).TotalSeconds));
            context.HttpContext.Response.Headers.RetryAfter = Math.Max(
                1,
                retryAfterSeconds).ToString();
            context.HttpContext.Response.Headers[HttpContextExtensions.RequestIdHeaderName] = context.HttpContext.GetRequestId();
            LogRateLimitRejected(
                logger,
                policyName,
                partitionKey,
                decision.CurrentCount,
                decision.PermitLimit,
                retryAfterSeconds,
                context.HttpContext.Request.Method,
                context.HttpContext.Request.Path,
                context.HttpContext.GetRequestId());
            context.Result = new ObjectResult(new
            {
                error = "Too many requests",
                requestId = context.HttpContext.GetRequestId(),
                limit = decision.PermitLimit,
                count = decision.CurrentCount,
                retryAfterUtc = decision.WindowEndsAtUtc
            })
            {
                StatusCode = StatusCodes.Status429TooManyRequests
            };
            return;
        }

        await next();
    }

    private async Task<string> ResolvePartitionKeyAsync(
        string policyName,
        HttpContext context,
        IEnumerable<object?> actionArguments,
        CancellationToken cancellationToken)
    {
        if (context.User.Identity?.IsAuthenticated == true)
            return $"user:{context.GetUserId()}";

        if (TryResolveEmailPartitionKey(policyName, actionArguments, out var emailPartitionKey))
            return emailPartitionKey;

        if (TryExtractRefreshToken(policyName, actionArguments, out var refreshToken)
            && await authSessionService.HasSessionForTokenAsync(refreshToken, cancellationToken))
        {
            return BuildRefreshTokenPartitionKey(policyName, refreshToken);
        }

        return $"ip:{context.GetClientIpAddress() ?? "unknown"}";
    }

    private static readonly HashSet<string> EmailPartitionedPolicies =
        new(StringComparer.OrdinalIgnoreCase) { "auth", "waitlist" };

    /// <summary>
    /// For unauthenticated requests under an email-partitioned policy (<c>auth</c>, <c>waitlist</c>),
    /// partitions by the request's normalized email so OTP and waitlist flows can't be throttled by
    /// a shared proxy IP or bypassed by rotating forwarded-IP headers. The key is prefixed with the
    /// policy name so each policy keeps its own bucket. Returns false when the policy isn't email
    /// partitioned or no email-bearing argument is present, so the caller falls back to IP partitioning.
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

    /// <summary>
    /// For unauthenticated requests under the <c>refresh</c> policy, extracts the request's refresh token
    /// when it matches the exact server-issued shape (<see cref="RefreshTokenRules.IsWellFormed"/>). Format
    /// alone is not enough to earn a per-session bucket: the caller additionally confirms the token maps to
    /// a real stored session before partitioning by it, so a malformed OR well-formed-but-forged token —
    /// the "vary the body to mint a fresh, never-throttled bucket" bypass, which is as cheap for an attacker
    /// as minting a real token — never yields a private bucket and instead falls back to per-IP throttling.
    /// Returns false when the policy isn't refresh partitioned or no well-formed refresh token is present.
    /// </summary>
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
