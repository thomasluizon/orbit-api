using System.Globalization;
using System.Text;

namespace Orbit.Application.Chat;

/// <summary>
/// Cache-safe coarse tool grouping: a handful of rarely-used tool domains are only declared to the
/// model when the conversation actually references them, trimming the per-turn tool payload for the
/// common habit/goal flows. Core habit, goal, logging, query, profile, tag, gamification, and meta
/// tools are ALWAYS declared, so a turn is never starved of a core capability. Gating is driven by the
/// cumulative conversation text (message + history), so an unlocked domain stays unlocked for the rest
/// of the conversation — the active set only grows, keeping prompt caching stable between unlock points.
/// </summary>
public static class ChatToolGroups
{
    private sealed record ExtendedGroup(IReadOnlyList<string> ToolNames, IReadOnlyList<string> Keywords);

    private static readonly IReadOnlyList<ExtendedGroup> Groups =
    [
        new(["get_calendar_overview", "manage_calendar_sync"],
            ["calendar", "agenda", "google cal", "calendario", "event", "evento"]),
        new(["get_api_keys", "manage_api_keys"],
            ["api key", "apikey", "api-key", "developer key", "mcp", "personal token"]),
        new(["get_referral_overview", "get_referral_code"],
            ["referral", "refer a friend", "invite", "indicacao", "indicar", "convidar"]),
        new(["get_subscription_overview", "manage_subscription"],
            ["subscription", "subscribe", "billing", "upgrade", "downgrade", "cancel plan", "assinatura", "pagamento", "cobranca"]),
        new(["send_support_request"],
            ["support", "contact the team", "report a bug", "suporte", "fale conosco"]),
        new(["manage_account"],
            ["my account", "delete account", "export data", "change password", "minha conta", "excluir conta", "senha"]),
        new(["get_checklist_templates", "create_checklist_template", "delete_checklist_template"],
            ["template", "checklist template", "reusable checklist", "modelo"]),
        new(["get_notifications", "update_notifications", "delete_notifications"],
            ["notification", "notificacao", "push alert", "reminder settings"]),
    ];

    private static readonly HashSet<string> ExtendedToolNames =
        Groups.SelectMany(group => group.ToolNames).ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// Returns the tool names to declare this turn: every core tool, plus the tools of each rarely-used
    /// domain whose keywords appear anywhere in the conversation text. Pass the user message joined with
    /// the recent history so an unlocked domain stays unlocked across the conversation.
    /// </summary>
    public static IReadOnlyCollection<string> ResolveActiveToolNames(
        IEnumerable<string> allToolNames, string conversationText)
    {
        var normalized = Normalize(conversationText);
        var active = new HashSet<string>(StringComparer.Ordinal);

        foreach (var name in allToolNames.Where(name => !ExtendedToolNames.Contains(name)))
            active.Add(name);

        foreach (var group in Groups.Where(group =>
                     group.Keywords.Any(keyword => normalized.Contains(keyword, StringComparison.Ordinal))))
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
