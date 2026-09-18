using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Orbit.Application.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Api.Middleware;

/// <summary>
/// The language a response should speak, for the one request in front of it.
/// </summary>
internal interface IRequestLanguageResolver
{
    ValueTask<bool> IsPortugueseAsync(HttpContext context, CancellationToken cancellationToken);
}

/// <summary>
/// Resolves the reply language from the signed-in account's stored <see cref="User.Language"/>,
/// and falls back to the <c>Accept-Language</c> header only for an anonymous request.
/// <para>
/// The header alone was not enough. The Android app sends no <c>Accept-Language</c> at all, and
/// the web app reaches this API through Server Actions that send none either, so a header-only
/// rule answered every signed-in pt-BR reader in English. The stored column is also what the rest
/// of the product already keys off: <c>ResendEmailService</c> picks an email's language from it,
/// and <c>ProactiveCheckinSchedulerService</c> picks a push notification's language from it.
/// </para>
/// <para>
/// The lookup runs only on a response that already carries an error body, so it costs one indexed
/// read on a path that is rare by construction. It is deliberately not cached: a cached language
/// would answer in the old one for as long as the entry lived after somebody changed it.
/// </para>
/// </summary>
internal sealed class RequestLanguageResolver(IGenericRepository<User> userRepository) : IRequestLanguageResolver
{
    public async ValueTask<bool> IsPortugueseAsync(HttpContext context, CancellationToken cancellationToken)
    {
        var storedLanguage = await GetStoredLanguageAsync(context, cancellationToken);

        return storedLanguage is not null
            ? LocaleHelper.IsPortuguese(storedLanguage)
            : LocaleHelper.IsPortugueseAcceptLanguage(context.Request.Headers.AcceptLanguage.ToString());
    }

    private async ValueTask<string?> GetStoredLanguageAsync(HttpContext context, CancellationToken cancellationToken)
    {
        if (context.User.Identity?.IsAuthenticated != true)
            return null;

        if (!Guid.TryParse(context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var userId))
            return null;

        var user = await userRepository.GetByIdAsync(userId, cancellationToken);
        return string.IsNullOrWhiteSpace(user?.Language) ? null : user.Language;
    }
}
