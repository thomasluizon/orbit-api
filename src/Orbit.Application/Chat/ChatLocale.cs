using Orbit.Domain.Models;

namespace Orbit.Application.Chat;

internal static class ChatLocale
{
    public static string? Resolve(AgentClientContext? client, string? profileLanguage) =>
        string.IsNullOrWhiteSpace(client?.Locale) ? profileLanguage : client.Locale;
}
