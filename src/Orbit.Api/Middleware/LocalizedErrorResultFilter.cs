using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Orbit.Application.Common;
using Orbit.Application.Habits.Commands;

namespace Orbit.Api.Middleware;

internal sealed class LocalizedErrorResultFilter(IRequestLanguageResolver languageResolver) : IAsyncResultFilter
{
    public async Task OnResultExecutionAsync(ResultExecutingContext context, ResultExecutionDelegate next)
    {
        if (context.Result is ObjectResult { Value: ErrorResponse body } objectResult)
        {
            var isPtBr = !RequiresEnglishCalendarDetection(body.ErrorCode)
                && await languageResolver.IsPortugueseAsync(
                    context.HttpContext, context.HttpContext.RequestAborted);

            if (ErrorCopy.TryResolve(body.ErrorCode, isPtBr, body.Args, out var message))
                objectResult.Value = body with { Error = message };
        }

        if (context.Result is ObjectResult { Value: BulkMoveHabitParentResult moves } moveResult)
        {
            var isPtBr = await languageResolver.IsPortugueseAsync(
                context.HttpContext, context.HttpContext.RequestAborted);
            moveResult.Value = moves with
            {
                Results = moves.Results.Select(item =>
                    item.Error is not null && ErrorCopy.TryResolve(item.ErrorCode, isPtBr, item.ErrorArgs, out var message)
                        ? item with { Error = message }
                        : item).ToList()
            };
        }

        await next();
    }

    /// <summary>
    /// https://github.com/thomasluizon/orbit-ui-mobile/blob/b84bdc9e/packages/shared/src/utils/calendar-sync.ts
    /// classifies connection failures from English message substrings in both shipped clients.
    /// </summary>
    private static bool RequiresEnglishCalendarDetection(string? code) =>
        code is ErrorCodes.CalendarNotConnected or ErrorCodes.CalendarReconnectRequired;
}
