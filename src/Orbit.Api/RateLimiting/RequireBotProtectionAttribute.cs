using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Options;
using Orbit.Api.Extensions;
using Orbit.Application.Common;
using Orbit.Domain.Interfaces;

namespace Orbit.Api.RateLimiting;

[AttributeUsage(AttributeTargets.Method)]
public sealed class RequireBotProtectionAttribute : Attribute, IFilterFactory, IOrderedFilter
{
    public int Order => 1;
    public bool IsReusable => false;

    public IFilterMetadata CreateInstance(IServiceProvider serviceProvider)
        => new RequireBotProtectionFilter(
            serviceProvider.GetRequiredService<IOptions<BotProtectionSettings>>(),
            serviceProvider.GetRequiredService<ITurnstileVerificationService>(),
            serviceProvider.GetRequiredService<ILogger<RequireBotProtectionFilter>>());
}

public sealed partial class RequireBotProtectionFilter(
    IOptions<BotProtectionSettings> options,
    ITurnstileVerificationService verifier,
    ILogger<RequireBotProtectionFilter> logger) : IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        if (!options.Value.Enabled)
        {
            await next();
            return;
        }

        if (HasSmokeBypass(context))
        {
            await next();
            return;
        }

        var token = GetStringProperty(context.ActionArguments.Values, "TurnstileToken");
        if (string.IsNullOrWhiteSpace(token) || token.Length > 2048)
        {
            Reject(context);
            return;
        }

        try
        {
            var result = await verifier.VerifyAsync(token, context.HttpContext.RequestAborted);
            if (!result.Success || !options.Value.AllowsHostname(result.Hostname))
            {
                Reject(context);
                return;
            }
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException
            or OperationCanceledException && !context.HttpContext.RequestAborted.IsCancellationRequested)
        {
            LogVerifierUnavailable(logger, context.HttpContext.GetRequestId());
            context.Result = ErrorResult(context, "Verification unavailable", StatusCodes.Status503ServiceUnavailable);
            return;
        }

        await next();
    }

    private bool HasSmokeBypass(ActionExecutingContext context)
    {
        var secret = options.Value.SmokeBypassSecret;
        var header = context.HttpContext.Request.Headers["X-Orbit-Smoke-Bypass"];
        var smokeEmail = Environment.GetEnvironmentVariable("SMOKE_TEST_EMAIL");
        var email = GetStringProperty(context.ActionArguments.Values, "Email");
        return !string.IsNullOrWhiteSpace(secret) && header.Count == 1
            && !string.IsNullOrWhiteSpace(smokeEmail)
            && string.Equals(email?.Trim(), smokeEmail.Trim(), StringComparison.OrdinalIgnoreCase)
            && CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(header[0] ?? ""));
    }

    private void Reject(ActionExecutingContext context)
    {
        LogTokenRejected(logger, context.HttpContext.GetRequestId());
        context.Result = ErrorResult(context, "Invalid verification token", StatusCodes.Status400BadRequest);
    }

    private static ObjectResult ErrorResult(ActionExecutingContext context, string error, int statusCode)
        => new(new { error, requestId = context.HttpContext.GetRequestId() }) { StatusCode = statusCode };

    private static string? GetStringProperty(IEnumerable<object?> arguments, string name)
    {
        foreach (var argument in arguments)
        {
            var property = argument?.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            if (property?.PropertyType == typeof(string) && property.GetValue(argument) is string value)
                return value;
        }

        return null;
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "Turnstile token rejected. RequestId={RequestId}")]
    private static partial void LogTokenRejected(ILogger logger, string requestId);

    [LoggerMessage(EventId = 2, Level = LogLevel.Error, Message = "Turnstile verification unavailable. RequestId={RequestId}")]
    private static partial void LogVerifierUnavailable(ILogger logger, string requestId);
}
