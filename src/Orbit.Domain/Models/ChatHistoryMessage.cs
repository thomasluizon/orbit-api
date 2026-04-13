namespace Orbit.Domain.Models;

public record ChatHistoryMessage(string Role, string Content)
{
    public const string UserRole = "user";
    public const string AssistantRole = "assistant";
    public const string LegacyAssistantRole = "ai";

    public static bool IsSupportedRole(string? role) =>
        NormalizeRole(role) is not null;

    public static string? NormalizeRole(string? role)
    {
        if (string.Equals(role, UserRole, StringComparison.OrdinalIgnoreCase))
            return UserRole;

        if (string.Equals(role, AssistantRole, StringComparison.OrdinalIgnoreCase))
            return AssistantRole;

        if (string.Equals(role, LegacyAssistantRole, StringComparison.OrdinalIgnoreCase))
            return AssistantRole;

        return null;
    }
}
