using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Orbit.Application.Common;
using Orbit.Infrastructure.Configuration;

namespace Orbit.Infrastructure.AI;

internal static class AiEvalCredentials
{
    internal static string Resolve(AiSettings settings, IConfiguration? configuration, IHttpContextAccessor? http)
    {
        var userId = AiCallContext.Current?.UserId;
        if (userId is null && Guid.TryParse(http?.HttpContext?.User.FindFirstValue(ClaimTypes.NameIdentifier), out var authenticatedUser))
            userId = authenticatedUser;
        if (userId is not { } id || !EvalAccountAccess.Contains(configuration?[EvalAccountAccess.AccountIdsKey], id))
            return settings.ApiKey;
        return !string.IsNullOrWhiteSpace(settings.EvalApiKey) ? settings.EvalApiKey
            : throw new InvalidOperationException("Evaluation AI credentials are required.");
    }
}
