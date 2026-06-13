using Microsoft.AspNetCore.Mvc;
using Orbit.Domain.Common;

namespace Orbit.Api.Extensions;

public static class ResultActionResultExtensions
{
    public static IActionResult ToPayGateAwareResult(
        this Result result,
        Func<IActionResult> onSuccess,
        int failureStatusCode = StatusCodes.Status400BadRequest)
    {
        return result.IsSuccess ? onSuccess() : result.ToErrorResult(failureStatusCode);
    }

    public static IActionResult ToPayGateAwareResult(
        this Result result,
        int failureStatusCode = StatusCodes.Status400BadRequest)
    {
        return result.IsSuccess ? new OkResult() : result.ToErrorResult(failureStatusCode);
    }

    public static IActionResult ToPayGateAwareResult<T>(
        this Result<T> result,
        Func<T, IActionResult> onSuccess,
        int failureStatusCode = StatusCodes.Status400BadRequest)
    {
        return result.IsSuccess ? onSuccess(result.Value) : result.ToErrorResult(failureStatusCode);
    }

    /// <summary>
    /// Serializes a failed result as the uniform error payload, honoring pay-gate 403s.
    /// Every failure carries both the English fallback message and its stable errorCode.
    /// </summary>
    public static IActionResult ToErrorResult(
        this Result result,
        int failureStatusCode = StatusCodes.Status400BadRequest)
    {
        return new ObjectResult(new { error = result.Error, errorCode = result.ErrorCode })
        {
            StatusCode = result.ErrorCode == Result.PayGateErrorCode
                ? StatusCodes.Status403Forbidden
                : failureStatusCode
        };
    }

    /// <summary>Uniform error body for controller-authored failures that bypass Result.</summary>
    public static object ToErrorBody(this AppError error) =>
        new { error = error.Message, errorCode = error.Code };
}
