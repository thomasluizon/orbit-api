using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Api.Authorization;

/// <summary>
/// Marker requirement for the admin authorization boundary; satisfied by <see cref="AdminAuthorizationHandler"/>.
/// </summary>
public sealed class AdminRequirement : IAuthorizationRequirement;

/// <summary>
/// Authorizes admin-only endpoints by reading <c>User.IsAdmin</c> live from the database on every request,
/// so revoking admin in the DB takes effect immediately rather than lingering until the caller's token expires.
/// The JWT carries identity only; it never asserts admin.
/// </summary>
public sealed class AdminAuthorizationHandler(IGenericRepository<User> userRepository)
    : AuthorizationHandler<AdminRequirement>
{
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context, AdminRequirement requirement)
    {
        if (!Guid.TryParse(context.User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
            return;

        if (await userRepository.AnyAsync(user => user.Id == userId && user.IsAdmin))
            context.Succeed(requirement);
    }
}
