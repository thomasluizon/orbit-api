using System.Globalization;
using Orbit.Application.Common;
using Orbit.Domain.Enums;
using Orbit.Domain.Models;

namespace Orbit.Application.Chat;

internal static class HabitSuggestionsPresentation
{
    internal static string Readable(HabitSuggestionsCard card)
    {
        var portuguese = LocaleHelper.IsPortuguese(card.Language);
        var culture = CultureInfo.GetCultureInfo(portuguese ? "pt-BR" : "en-US");
        return string.Join("\n\n", card.Drafts.Select(draft =>
        {
            var type = draft.Type == "Task" ? portuguese ? "Tarefa" : "Task" : portuguese ? "Hábito" : "Habit";
            var cadence = draft.FrequencyUnit is { } unit ? Cadence(unit, draft.FrequencyQuantity ?? 1, portuguese) : type;
            var days = string.Join(", ", draft.Days.Select(day => culture.DateTimeFormat.GetDayName(day)));
            var reminder = draft.ReminderMinutesBefore is { } minutes
                ? portuguese ? $"Lembrete: {minutes} min antes." : $"Reminder: {minutes} min before." : string.Empty;
            var action = draft.ExistingHabitId.HasValue ? portuguese ? "Ajustar hábito" : "Adjust habit" : type;
            return $"{draft.Emoji} {draft.Title} ({action})\n{cadence} {days} {draft.DueDate.ToString("d", culture)} {draft.DueTime:HH:mm} - {draft.DueEndTime:HH:mm}\n"
                + string.Join("\n", draft.ChecklistItems) + $"\n{reminder}\n{draft.Reason}";
        }));
    }

    private static string Cadence(FrequencyUnit unit, int quantity, bool portuguese) => (unit, portuguese, quantity == 1) switch
    {
        (FrequencyUnit.Day, true, true) => "Todo dia",
        (FrequencyUnit.Week, true, true) => "Toda semana",
        (FrequencyUnit.Month, true, true) => "Todo mês",
        (FrequencyUnit.Year, true, true) => "Todo ano",
        (FrequencyUnit.Day, true, false) => $"A cada {quantity} dias",
        (FrequencyUnit.Week, true, false) => $"A cada {quantity} semanas",
        (FrequencyUnit.Month, true, false) => $"A cada {quantity} meses",
        (FrequencyUnit.Year, true, false) => $"A cada {quantity} anos",
        (FrequencyUnit.Day, false, true) => "Every day",
        (FrequencyUnit.Week, false, true) => "Every week",
        (FrequencyUnit.Month, false, true) => "Every month",
        (FrequencyUnit.Year, false, true) => "Every year",
        (FrequencyUnit.Day, false, false) => $"Every {quantity} days",
        (FrequencyUnit.Week, false, false) => $"Every {quantity} weeks",
        (FrequencyUnit.Month, false, false) => $"Every {quantity} months",
        _ => $"Every {quantity} years"
    };
}
