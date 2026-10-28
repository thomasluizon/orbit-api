using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Orbit.Api.Extensions;
using Orbit.Domain.Models;

namespace Orbit.Api.Authorization;

public sealed class StagingEvalRequirement : IAuthorizationRequirement
{
    public const string PolicyName = "StagingEval";
    public const string AccountIdsKey = "StagingEval:AccountIds";
}

public sealed class StagingEvalAuthorizationHandler(IHostEnvironment environment, IConfiguration configuration)
    : AuthorizationHandler<StagingEvalRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, StagingEvalRequirement requirement)
    {
        if (environment.IsStaging()
            && context.User.Identity?.IsAuthenticated == true
            && context.User.GetAgentAuthMethod() == AgentAuthMethod.Jwt
            && Guid.TryParse(context.User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId)
            && userId != Guid.Empty
            && (configuration[StagingEvalRequirement.AccountIdsKey] ?? "")
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Any(value => Guid.TryParse(value, out var allowedId) && allowedId == userId))
            context.Succeed(requirement);

        return Task.CompletedTask;
    }
}
