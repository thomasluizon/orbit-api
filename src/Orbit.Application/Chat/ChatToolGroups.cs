using System.Globalization;
using System.Text;

namespace Orbit.Application.Chat;

/// <summary>
/// Cache-safe coarse tool grouping: a handful of rarely-used tool domains are only declared to the
/// model when the conversation actually references them, trimming the per-turn tool payload for the
/// common habit/goal flows. Core habit, goal, logging, query, profile, tag, gamification, and meta
/// tools are ALWAYS declared, so a turn is never starved of a core capability. Keywords are matched
/// against the cumulative conversation text (message + history), so a keyword unlock persists across
/// turns. A client entry-point intent also activates its matching group when supplied on each turn.
/// </summary>
public static class ChatToolGroups
{
    private sealed record ExtendedGroup(
        string EntryPointIntent,
        IReadOnlyList<string> ToolNames,
        IReadOnlyList<string> Keywords);

    private static readonly IReadOnlyList<ExtendedGroup> Groups =
    [
        new("calendar", ["get_calendar_overview", "manage_calendar_sync"],
            ["calendar", "agenda", "google cal", "calendario", "event", "evento"]),
        new("api_keys", ["get_api_keys", "manage_api_keys"],
            ["api key", "apikey", "api-key", "developer key", "mcp", "personal token"]),
        new("referrals", ["get_referral_overview", "get_referral_code"],
            ["referral", "refer a friend", "invite", "indicacao", "indicar", "convidar"]),
        new("subscription", ["get_subscription_overview", "manage_subscription"],
            ["subscription", "subscribe", "billing", "upgrade", "downgrade", "cancel plan", "assinatura", "pagamento", "cobranca"]),
        new("support", ["send_support_request"],
            ["support", "contact the team", "report a bug", "suporte", "fale conosco"]),
        new("account", ["manage_account"],
            ["my account", "delete account", "export data", "change password", "minha conta", "excluir conta", "senha"]),
        new("checklist_templates", ["get_checklist_templates", "create_checklist_template", "delete_checklist_template"],
            ["template", "checklist template", "reusable checklist", "modelo"]),
        new("notifications", ["get_notifications", "update_notifications", "delete_notifications"],
            ["notification", "notificacao", "push alert", "reminder settings"]),
    ];

    private static readonly HashSet<string> ExtendedToolNames =
        Groups.SelectMany(group => group.ToolNames).ToHashSet(StringComparer.Ordinal);

    public static bool IsKnownEntryPointIntent(string? entryPointIntent) =>
        Groups.Any(group => string.Equals(
            group.EntryPointIntent,
            entryPointIntent,
            StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Returns the tool names to declare this turn: every core tool, plus the tools of each rarely-used
    /// domain whose keywords appear anywhere in the conversation text or whose entry-point intent matches.
    /// Pass the user message joined with the recent history so a keyword unlock persists across turns.
    /// </summary>
    public static IReadOnlyCollection<string> ResolveActiveToolNames(
        IEnumerable<string> allToolNames,
        string conversationText,
        string? entryPointIntent = null)
    {
        var normalized = Normalize(conversationText);
        var active = new HashSet<string>(StringComparer.Ordinal);

        foreach (var name in allToolNames.Where(name => !ExtendedToolNames.Contains(name)))
            active.Add(name);

        foreach (var group in Groups.Where(group =>
                     group.Keywords.Any(keyword => normalized.Contains(keyword, StringComparison.Ordinal)) ||
                     (entryPointIntent is not null &&
                      string.Equals(group.EntryPointIntent, entryPointIntent, StringComparison.OrdinalIgnoreCase))))
        {
            foreach (var name in group.ToolNames)
                active.Add(name);
        }

        return active;
    }

    private static string Normalize(string text)
    {
        var decomposed = text.ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var ch in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
                builder.Append(ch);
        }
        return builder.ToString();
    }
}
