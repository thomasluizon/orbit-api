using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Orbit.Api.Extensions;
using Orbit.Application.Common;
using Orbit.Domain.Models;

namespace Orbit.Api.Authorization;

public sealed class EvalAccountRequirement : IAuthorizationRequirement
{
    public const string PolicyName = "EvalAccount";
}

public sealed class EvalAccountAuthorizationHandler(IHostEnvironment environment, IConfiguration configuration)
    : AuthorizationHandler<EvalAccountRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, EvalAccountRequirement requirement)
    {
        if ((environment.IsStaging() || environment.IsProduction())
            && context.User.Identity?.IsAuthenticated == true
            && context.User.GetAgentAuthMethod() == AgentAuthMethod.Jwt
            && Guid.TryParse(context.User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId)
            && EvalAccountAccess.Contains(configuration[EvalAccountAccess.AccountIdsKey], userId))
            context.Succeed(requirement);

        return Task.CompletedTask;
    }
}
