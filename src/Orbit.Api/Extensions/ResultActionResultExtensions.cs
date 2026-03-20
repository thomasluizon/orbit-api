using Microsoft.AspNetCore.Mvc;
using Orbit.Domain.Common;

namespace Orbit.Api.Extensions;

public static class ResultActionResultExtensions
{
    public static IActionResult ToPayGateAwareResult(this Result result)
    {
        if (result.IsSuccess)
            return new OkResult();

        return result.ErrorCode == "PAY_GATE"
            ? new ObjectResult(new { error = result.Error, code = "PAY_GATE" }) { StatusCode = 403 }
            : new BadRequestObjectResult(new { error = result.Error });
    }

    public static IActionResult ToPayGateAwareResult<T>(this Result<T> result, Func<T, IActionResult> onSuccess)
    {
        if (result.IsSuccess)
            return onSuccess(result.Value);

        return result.ErrorCode == "PAY_GATE"
            ? new ObjectResult(new { error = result.Error, code = "PAY_GATE" }) { StatusCode = 403 }
            : new BadRequestObjectResult(new { error = result.Error });
    }
}
