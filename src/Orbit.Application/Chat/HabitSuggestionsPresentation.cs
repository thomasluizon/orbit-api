using System.Globalization;
using Orbit.Application.Common;
using Orbit.Domain.Models;

namespace Orbit.Application.Chat;

internal static class HabitSuggestionsPresentation
{
    internal static string Introduction(string language) => LocaleHelper.IsPortuguese(language)
        ? "Aqui estão sugestões para sua rotina."
        : "Here are suggestions for your routine.";

    internal static string Readable(HabitSuggestionsCard card)
    {
        var portuguese = LocaleHelper.IsPortuguese(card.Language);
        var culture = CultureInfo.GetCultureInfo(portuguese ? "pt-BR" : "en-US");
        return string.Join("\n\n", card.Drafts.Select(draft =>
        {
            var type = draft.Type == "Task" ? portuguese ? "Tarefa" : "Task" : portuguese ? "Hábito" : "Habit";
            var cadence = draft.FrequencyUnit is { } unit ? $"{draft.FrequencyQuantity} {Unit(unit.ToString(), portuguese)}" : type;
            var days = string.Join(", ", draft.Days.Select(day => culture.DateTimeFormat.GetDayName(day)));
            var reminder = draft.ReminderMinutesBefore is { } minutes
                ? portuguese ? $"Lembrete: {minutes} min antes." : $"Reminder: {minutes} min before." : string.Empty;
            var action = draft.ExistingHabitId.HasValue ? portuguese ? "Ajustar existente" : "Adjust existing" : type;
            return $"{draft.Emoji} {draft.Title} ({action})\n{cadence} {days} {draft.DueDate:yyyy-MM-dd} {draft.DueTime:HH:mm} - {draft.DueEndTime:HH:mm}\n"
                + string.Join("\n", draft.ChecklistItems) + $"\n{reminder}\n{draft.Reason}";
        }));
    }

    private static string Unit(string unit, bool portuguese) => (unit, portuguese) switch
    {
        ("Day", true) => "dia(s)",
        ("Week", true) => "semana(s)",
        ("Month", true) => "mês(es)",
        ("Year", true) => "ano(s)",
        _ => unit
    };
}
