using Orbit.Domain.Entities;
using System.Reflection;

namespace Orbit.Application.Tests.Common;

/// <summary>
/// Builds a <see cref="User"/> in a state only a pre-existing database row can reach. No migration
/// rewrites <see cref="User.ColorScheme"/>, so rows written before the colour collapse still hold a
/// retired key. No runtime write remains. Reflection reaches the private setter the same way
/// EF Core materialises the row.
/// </summary>
public static class LegacyUserRows
{
    private static readonly MethodInfo ColorSchemeSetter =
        typeof(User).GetProperty(nameof(User.ColorScheme))!.GetSetMethod(nonPublic: true)!;

    public static User WithStoredColorScheme(this User user, string? storedValue)
    {
        ColorSchemeSetter.Invoke(user, [storedValue]);
        return user;
    }
}
