using Microsoft.AspNetCore.Mvc;
using Orbit.Domain.Common;

namespace Orbit.Api.Extensions;

public static class ResultActionResultExtensions
{
    public static IActionResult ToPayGateAwareResult(this Result result)
    {
        if (result.IsSuccess)
            return new OkResult();

        if (result.ErrorCode == "PAY_GATE")
            return new ObjectResult(new { error = result.Error, errorCode = "PAY_GATE" }) { StatusCode = 403 };

        return result.ErrorCode is not null
            ? new BadRequestObjectResult(new { error = result.Error, errorCode = result.ErrorCode })
            : new BadRequestObjectResult(new { error = result.Error });
    }

    public static IActionResult ToPayGateAwareResult<T>(this Result<T> result, Func<T, IActionResult> onSuccess)
    {
        if (result.IsSuccess)
            return onSuccess(result.Value);

        if (result.ErrorCode == "PAY_GATE")
            return new ObjectResult(new { error = result.Error, errorCode = "PAY_GATE" }) { StatusCode = 403 };

        return result.ErrorCode is not null
            ? new BadRequestObjectResult(new { error = result.Error, errorCode = result.ErrorCode })
            : new BadRequestObjectResult(new { error = result.Error });
    }
}
