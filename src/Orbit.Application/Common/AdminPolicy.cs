namespace Orbit.Application.Common;

/// <summary>
/// Names the single administrative authorization boundary. A user's IsAdmin flag mints the
/// admin claim into the JWT; the "Admin" policy (RequireClaim on that claim) gates admin-only
/// endpoints. The first admin is granted by a direct DB update (Users.IsAdmin = true); there is
/// no email-based gate.
/// </summary>
public static class AdminPolicy
{
    public const string Name = "Admin";
    public const string ClaimType = "admin";
    public const string ClaimValue = "true";
}
