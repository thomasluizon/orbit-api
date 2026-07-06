namespace Orbit.Application.Common;

/// <summary>
/// Names the single administrative authorization boundary. The "Admin" policy reads
/// <c>User.IsAdmin</c> live from the database on every request, so revoking admin takes effect
/// immediately; the JWT carries identity only and never asserts admin. The first admin is granted
/// by a direct DB update (Users.IsAdmin = true).
/// </summary>
public static class AdminPolicy
{
    public const string Name = "Admin";
}
