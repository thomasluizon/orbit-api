using Orbit.Application.Chat.Models;
using Orbit.Application.Common;

namespace Orbit.Api.Middleware;

internal static class ChatErrorLocalization
{
    private static readonly object LocaleKey = new();

    public static void SetLocale(HttpContext context, string? locale)
    {
        if (!string.IsNullOrWhiteSpace(locale)) context.Items[LocaleKey] = locale;
    }

    public static string? PreferredLocale(HttpContext context) => context.Items[LocaleKey] as string;

    public static async Task<string> MessageAsync(HttpContext context, string? code, string fallback,
        IReadOnlyList<object?>? args = null)
    {
        var portuguese = PreferredLocale(context) is { } locale ? LocaleHelper.IsPortuguese(locale)
            : context.RequestServices.GetService<IRequestLanguageResolver>() is { } resolver
                ? await resolver.IsPortugueseAsync(context, context.RequestAborted)
                : LocaleHelper.IsPortugueseAcceptLanguage(context.Request.Headers.AcceptLanguage);
        return ErrorCopy.TryResolve(code, portuguese, args ?? [], out var message) ? message : fallback;
    }

    public static async Task<ChatStreamEvent> FailureAsync(HttpContext context, int status, string? code,
        string fallback, IReadOnlyList<object?>? args = null) =>
        ChatStreamEvent.Failure(status, await MessageAsync(context, code, fallback, args), code);
}
