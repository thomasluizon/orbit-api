using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Orbit.Application.Common;

namespace Orbit.Api.Middleware;

/// <summary>
/// Replaces the message on every <see cref="ErrorResponse"/> with the user-facing copy
/// <see cref="ErrorCopy"/> holds for its code, in the language the reader reads.
/// <para>
/// This is the single seam where that substitution happens. Threading a language through the
/// 190 call sites of <c>ToErrorResult</c> and <c>ToPayGateAwareResult</c> would have put the
/// same three lines in 190 places, and a failure raised by a domain guard would still have
/// depended on each of them remembering. Here a guard's developer-facing sentence cannot reach
/// a client at all, because the code always resolves first.
/// </para>
/// <para>
/// The language comes from <see cref="IRequestLanguageResolver"/>, which reads the signed-in
/// account's stored language and treats <c>Accept-Language</c> as the anonymous fallback. Both
/// shipped clients send no such header, so reading it alone answered every signed-in pt-BR
/// reader in English.
/// </para>
/// <para>
/// The status code, the error code and the body's shape are untouched. Only the sentence
/// changes, and only when the code carries copy.
/// </para>
/// </summary>
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
