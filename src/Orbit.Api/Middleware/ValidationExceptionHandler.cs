using System.Text.Json;
using FluentValidation;
using Microsoft.AspNetCore.Diagnostics;

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

        var errors = validationException.Errors
            .GroupBy(e => e.PropertyName)
            .ToDictionary(
                g => g.Key,
                g => g.Select(e => e.ErrorMessage).ToArray());

        LogValidationFailed(logger, httpContext.Request.Path, string.Join(", ", errors.Keys));

        var response = new
        {
            type = "ValidationFailure",
            status = 400,
            errors
        };

        await httpContext.Response.WriteAsJsonAsync(response, cancellationToken);

        return true;
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "Validation failed on {Path}: {Fields}")]
    private static partial void LogValidationFailed(ILogger logger, string path, string fields);
}
