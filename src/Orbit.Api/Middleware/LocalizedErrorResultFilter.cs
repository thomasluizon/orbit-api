using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Orbit.Application.Common;

namespace Orbit.Api.Middleware;

/// <summary>
/// Replaces the message on every <see cref="ErrorResponse"/> with the user-facing copy
/// <see cref="ErrorCopy"/> holds for its code, in the language the request asked for.
/// <para>
/// This is the single seam where that substitution happens. Threading a language through the
/// 190 call sites of <c>ToErrorResult</c> and <c>ToPayGateAwareResult</c> would have put the
/// same three lines in 190 places, and a failure raised by a domain guard would still have
/// depended on each of them remembering. Here a guard's developer-facing sentence cannot reach
/// a client at all, because the code always resolves first.
/// </para>
/// <para>
/// The status code, the error code and the body's shape are untouched. Only the sentence
/// changes, and only when the code carries copy.
/// </para>
/// </summary>
internal sealed class LocalizedErrorResultFilter : IResultFilter
{
    public void OnResultExecuting(ResultExecutingContext context)
    {
        if (context.Result is not ObjectResult { Value: ErrorResponse body } objectResult)
            return;

        var isPtBr = LocaleHelper.IsPortuguese(
            context.HttpContext.Request.Headers.AcceptLanguage.ToString());

        if (ErrorCopy.TryResolve(body.ErrorCode, isPtBr, body.Args, out var message))
            objectResult.Value = body with { Error = message };
    }

    public void OnResultExecuted(ResultExecutedContext context)
    {
    }
}
