using System.Text.Json;
using System.Text.Json.Serialization;
using Orbit.Domain.Models;

namespace Orbit.Application.Chat;

public sealed record OneSmallStepAction(string Id, string Label, string Message,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] StartingStep? PreviousStep = null);

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
                    : $"Give me an even smaller starting step for {goal}. The previous step was {step}.", suggestion),
            new("saveTask", portuguese ? "Salvar como tarefa" : "Save as task",
                portuguese ? $"Quero salvar {step} como tarefa."
                    : $"Save {step} as a task."),
            new("saveChecklistItem", portuguese ? "Salvar no checklist" : "Save as checklist item",
                portuguese ? $"Quero adicionar {step} ao checklist de um hábito."
                    : $"Add {step} to a habit's checklist.")
        ]);
    }

    internal static string Readable(OneSmallStepCard card) => card.Suggestion.Step;

    internal OneSmallStepCard ForClient(bool supportsContext) => this with
    {
        Actions = Actions.Select(action => action with
        {
            PreviousStep = supportsContext && action.Id == "smaller" ? Suggestion : null
        }).ToArray()
    };
}
