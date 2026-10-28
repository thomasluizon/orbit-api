using System.Text.Json;
using Orbit.Domain.Models;

namespace Orbit.Application.Chat;

public sealed record OneSmallStepAction(string Id, string Label, string Message);

public sealed record OneSmallStepCard(StartingStep Suggestion, IReadOnlyList<OneSmallStepAction> Actions)
{
    internal static OneSmallStepCard Build(StartingStep suggestion)
    {
        var portuguese = suggestion.Language == "pt-BR";
        var goal = JsonSerializer.Serialize(suggestion.Goal);
        var step = JsonSerializer.Serialize(suggestion.Step);
        return new(suggestion,
        [
            new("smaller", portuguese ? "Menor ainda" : "Smaller",
                portuguese ? $"Quero um passo ainda menor para {goal}. O passo anterior foi {step}."
                    : $"Give me an even smaller starting step for {goal}. The previous step was {step}."),
            new("saveTask", portuguese ? "Salvar como tarefa" : "Save as task",
                portuguese ? $"Prepare uma prévia para salvar {step} como tarefa."
                    : $"Prepare a preview to save {step} as a task."),
            new("saveChecklistItem", portuguese ? "Salvar na checklist" : "Save as checklist item",
                portuguese ? $"Quero adicionar {step} à checklist de um hábito. Pergunte em qual hábito se não estiver claro e preserve os itens existentes. Prepare uma prévia."
                    : $"Add {step} to a habit checklist. Ask which habit if unclear and preserve existing items. Prepare a preview.")
        ]);
    }

    internal static string Readable(OneSmallStepCard card) => card.Suggestion.Step;
}
