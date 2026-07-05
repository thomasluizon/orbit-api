namespace Orbit.Application.Common;

/// <summary>
/// Names the single administrative authorization boundary. A user's IsAdmin flag mints the
/// admin claim into the JWT; the "Admin" policy (RequireClaim on that claim) gates admin-only
/// endpoints. The bootstrap seeder is only how the flag is first granted, never the gate itself.
/// </summary>
public static class AdminPolicy
{
    public const string Name = "Admin";
    public const string ClaimType = "admin";
    public const string ClaimValue = "true";
}
