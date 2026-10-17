using FluentValidation;
using Microsoft.AspNetCore.Diagnostics;
using Orbit.Api.Extensions;
using Orbit.Application.Common;

namespace Orbit.Api.Middleware;

internal sealed partial class ValidationExceptionHandler(ILogger<ValidationExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        if (exception is not ValidationException validationException)
            return false;

        httpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
        httpContext.Response.ContentType = "application/json";
        httpContext.Response.Headers[HttpContextExtensions.RequestIdHeaderName] = httpContext.GetRequestId();

        var languageResolver = httpContext.RequestServices.GetRequiredService<IRequestLanguageResolver>();
        var isPtBr = await languageResolver.IsPortugueseAsync(httpContext, cancellationToken);
        var errorDetails = validationException.Errors
            .GroupBy(e => e.PropertyName)
            .ToDictionary(
                g => g.Key,
                g => g.Select(e => new
                {
                    code = e.ErrorCode,
                    message = e.LocalizedMessage(isPtBr)
                }).ToArray());
        var errors = errorDetails.ToDictionary(
            g => g.Key,
            g => g.Value.Select(e => e.message).ToArray());

        LogValidationFailed(
            logger,
            httpContext.Request.Path,
            string.Join(", ", errors.Keys),
            httpContext.GetRequestId());

        var response = new
        {
            type = "ValidationFailure",
            status = 400,
            requestId = httpContext.GetRequestId(),
            errors,
            errorDetails
        };

        await httpContext.Response.WriteAsJsonAsync(response, cancellationToken);

        return true;
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "Validation failed on {Path}: {Fields}. RequestId={RequestId}")]
    private static partial void LogValidationFailed(ILogger logger, string path, string fields, string requestId);
}
