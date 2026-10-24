using System.Globalization;
using System.Text;

namespace Orbit.Application.Chat;

public static class ChatToolGroups
{
    private sealed record ExtendedGroup(
        IReadOnlyList<string> ToolNames,
        IReadOnlyList<string> Keywords);

    private static readonly IReadOnlyList<ExtendedGroup> Groups =
    [
        new(["reorder_habits", "move_habit_parent", "duplicate_habit", "create_sub_habit", "suggest_breakdown", "move_habit"],
            ["reorder", "reorganiz", "reorden", "parent", "sub-habit", "subhabit", "sub hab", "sub-hab", "duplicate", "duplic", "break down", "breakdown", "dividir", "decompor", "move", "mover"]),
        new(["link_habits_to_goal", "link_goals_to_habit", "reorder_goals", "update_goal_status", "delete_goal", "review_goals"],
            ["goal", "meta", "link", "vincul", "objetivo"]),
        new(["get_retrospective", "get_daily_summary", "get_habit_metrics", "get_habit_history", "get_habit_ranking", "get_period_analytics"],
            ["retrospect", "recap", "wrapped", "summary", "resumo", "review", "insight", "metric", "metrica", "progress", "progresso", "stats", "estatistica", "history", "historico", "ranking", "rank", "most", "mais", "period", "compare", "comparar"]),
        new(["update_checklist"], ["checklist", "check list", "check-list", "lista de tarefas"]),
        new(["assign_tags", "list_tags", "create_tag", "update_tag", "delete_tag"],
            ["tag", "label", "etiqueta", "marcador", "categoria"]),
        new(["bulk_create_habits", "bulk_log_habits", "bulk_skip_habits", "bulk_update_habits", "bulk_reschedule_habits", "bulk_delete_habits", "bulk_update_habit_emojis"],
            ["all", "every", "bulk", "multiple", "several", "todos", "todas", "varios", "varias", "lote", "habits", "habitos"]),
        new(["update_profile_preferences", "set_ai_summary"],
            ["preference", "preferencia", "setting", "configura", "theme", "tema", "language", "idioma", "timezone", "fuso", "clock", "relogio", "summary", "resumo"]),
        new(["undo_skip_habit"], ["undo", "unskip", "desfazer", "despular"]),
        new(["set_proactive_astra"],
            ["proactive", "proativa", "proativo"]),
        new(["get_calendar_overview", "manage_calendar_sync"],
            ["calendar", "agenda", "google cal", "calendario", "event", "evento"]),
        new(["get_notion_tasks"], ["notion"]),
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
        new(["get_checklist_templates", "create_checklist_template", "update_checklist_template", "delete_checklist_template"],
            ["template", "checklist template", "reusable checklist", "modelo"]),
        new(["get_notifications", "update_notifications", "delete_notifications"],
            ["notification", "notificacao", "push alert", "reminder settings"]),
    ];

    private static readonly HashSet<string> ExtendedToolNames =
        Groups.SelectMany(group => group.ToolNames).ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// Returns the tool names to declare this turn: every core tool, plus the tools of each rarely-used
    /// domain whose keywords appear anywhere in the conversation text.
    /// Pass the user message joined with the recent history so a keyword unlock persists across turns.
    /// </summary>
    public static IReadOnlyCollection<string> ResolveActiveToolNames(
        IEnumerable<string> allToolNames,
        string conversationText)
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
