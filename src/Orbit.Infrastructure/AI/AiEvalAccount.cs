using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Orbit.Application.Common;
using Orbit.Domain.Interfaces;

namespace Orbit.Infrastructure.AI;

internal readonly record struct AiEvalAccount
{
    internal Guid? UserId { get; }
    internal bool IsUnmetered { get; }
    internal bool IsEvaluation { get; }

    private AiEvalAccount(Guid userId, bool unmetered, bool listed)
    {
        UserId = userId;
        IsUnmetered = unmetered;
        IsEvaluation = unmetered || listed;
    }

    internal static Guid? CurrentUserId(IHttpContextAccessor? http) => AiCallContext.Current?.UserId
        ?? (Guid.TryParse(http?.HttpContext?.User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId) ? userId : null);

    internal static async Task<AiEvalAccount> ResolveAsync(Guid? userId, IFeatureFlagService flags,
        IConfiguration? configuration, CancellationToken cancellationToken)
    {
        if (userId is not { } id)
            return default;

        return Resolve(id, await flags.GetEnabledKeysForUserAsync(id, cancellationToken), configuration);
    }
    internal static AiEvalAccount Resolve(Guid? userId, IReadOnlyList<string> enabledFlags, IConfiguration? configuration) =>
        userId is { } id ? new AiEvalAccount(id, enabledFlags.Contains(FeatureFlagKeys.AiEvalUnmetered, StringComparer.Ordinal),
            EvalAccountAccess.Contains(configuration?[EvalAccountAccess.AccountIdsKey], id)) : default;
}
