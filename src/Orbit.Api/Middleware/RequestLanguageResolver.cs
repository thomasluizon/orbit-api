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
