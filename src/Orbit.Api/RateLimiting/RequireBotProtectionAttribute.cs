using System.Reflection;
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
        if (!options.Value.Enabled || IsProductionSmokeAccount(context.ActionArguments.Values))
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
            if (!result.Success)
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

    private void Reject(ActionExecutingContext context)
    {
        LogTokenRejected(logger, context.HttpContext.GetRequestId());
        context.Result = ErrorResult(context, "Invalid verification token", StatusCodes.Status400BadRequest);
    }

    private static ObjectResult ErrorResult(ActionExecutingContext context, string error, int statusCode)
        => new(new { error, requestId = context.HttpContext.GetRequestId() }) { StatusCode = statusCode };

    private static bool IsProductionSmokeAccount(IEnumerable<object?> arguments)
    {
        var smokeEmail = Environment.GetEnvironmentVariable("SMOKE_TEST_EMAIL");
        var smokeCode = Environment.GetEnvironmentVariable("SMOKE_TEST_CODE");
        if (string.IsNullOrWhiteSpace(smokeEmail) || string.IsNullOrWhiteSpace(smokeCode)
            || smokeCode.Length != 6 || !smokeCode.All(char.IsDigit)
            || !string.Equals(Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT"), "Production", StringComparison.OrdinalIgnoreCase))
            return false;

        var email = GetStringProperty(arguments, "Email");
        return string.Equals(email?.Trim(), smokeEmail.Trim(), StringComparison.OrdinalIgnoreCase);
    }

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
