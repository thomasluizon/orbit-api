using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Orbit.Application.Common;

namespace Orbit.Api.Middleware;

internal sealed class LocalizedErrorResultFilter(IRequestLanguageResolver languageResolver) : IAsyncResultFilter
{
    public async Task OnResultExecutionAsync(ResultExecutingContext context, ResultExecutionDelegate next)
    {
        if (context.Result is ObjectResult { Value: ErrorResponse body } objectResult)
        {
            var isPtBr = await languageResolver.IsPortugueseAsync(
                context.HttpContext, context.HttpContext.RequestAborted);

            if (ErrorCopy.TryResolve(body.ErrorCode, isPtBr, body.Args, out var message))
                objectResult.Value = body with { Error = message };
        }

        await next();
    }
}
