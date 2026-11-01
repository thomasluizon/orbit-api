using System.Text.Json;
using Orbit.Application.Chat.Validators;
using Orbit.Domain.Models;

namespace Orbit.Application.Chat.Tools.Implementations;

public class SuggestBreakdownTool : IAiTool, IArgumentCheckTool
{
    public const string ReadAimInstruction = "When a read request also states an aim, answer only the requested read. An aim alone does not request a write or suggest_breakdown. The server offers a goal action on the read block when appropriate; wait for the person to select it before planning the aim.";
    public const string TouchRefinementInstruction = "If previousStartingStep.step already consists of a single touch or contact, another touch, tap or finger placement is equal effort and is not smaller. Reduce to looking at the same object without touching or moving it. Compare with the actual typed previous step. Do not repeat contact as a refinement of contact.";
    public const string SmallerStepInstruction = "For a smaller refinement, use the typed previousStartingStep.goal and previousStartingStep.step to compare effort before choosing the action. Keep the same activity and return only the first preparatory physical motion, stopping before the previous action is completed. When the previous step is already one short action, reduce it to contact or positioning and stop there. Picking up material can take as much effort as opening it. Tapping material to open it still completes the previous action. For opening study material, use a motion such as resting one fingertip on its closed cover: 'Rest one finger on the closed cover' / 'Encostar um dedo na capa fechada'. Do not append opening, reading or another action to that motion. Apply this comparison in both en and pt-BR.";

    public string Name => "suggest_breakdown";
    public bool IsReadOnly => true;

    public string Description =>
        "Suggest sub-habits without creating them. Call once per request; never repeat identical arguments. When the person cannot get themselves to do an activity, is stuck starting it, or asks for one concrete first step, call suggest_breakdown with one_small_step=true even if they do not say start. "
        + ReadAimInstruction + " "
        + "Set language to en or pt-BR matching their message, title to the activity and suggested_sub_habits to exactly one concrete starting action. Never return an empty array. "
        + "For a smaller refinement, use structured previousStartingStep as the baseline, keep its goal and return a proper part of its step or its first physical action, taking strictly less time and effort. A paraphrase or a different action of equal or greater effort is not smaller. "
        + "The server returns oneSmallStep with smaller and save actions. Emit no prose or lists alongside it. "
        + "Suggestions never save anything. Only an explicit save request uses create_habit for a Task or update_checklist, "
        + "with item_to_add containing only the step, resolving the owned habit first. The server preserves all stored checklist items. Both require the existing approval preview.";

    public object GetParameterSchema() => new
    {
        type = JsonSchemaTypes.Object,
        properties = new
        {
            one_small_step = new { type = JsonSchemaTypes.Boolean, description = "Return exactly one starting step as a oneSmallStep block. For a refinement, use structured previousStartingStep and return a proper part of its step or its first physical action, taking strictly less time and effort. Default false." },
            language = new { type = JsonSchemaTypes.String, @enum = new[] { "en", "pt-BR" }, description = "Required with one_small_step=true; the person's message language." },
            title = new { type = JsonSchemaTypes.String, description = "The broad habit title to break down" },
            suggested_sub_habits = new
            {
                type = JsonSchemaTypes.Array,
                minItems = 1,
                description = "Suggested sub-habit breakdowns",
                items = new
                {
                    type = JsonSchemaTypes.Object,
                    properties = new
                    {
                        title = new { type = JsonSchemaTypes.String, pattern = @"^\S(?:[^\r\n]{0,198}\S)?$" },
                        description = new { type = JsonSchemaTypes.String },
                        frequency_unit = new
                        {
                            type = JsonSchemaTypes.String,
                            @enum = JsonSchemaTypes.FrequencyUnitEnum
                        },
                        frequency_quantity = new { type = JsonSchemaTypes.Integer },
                        days = new { type = JsonSchemaTypes.Array, items = new { type = JsonSchemaTypes.String, @enum = JsonSchemaTypes.WeekdayEnum } }
                    },
                    required = new[] { "title" }
                }
            }
        },
        required = new[] { "title", "suggested_sub_habits" }
    };

    public Task<Orbit.Domain.Common.Result> CheckArgumentsAsync(JsonElement args, Guid userId, CancellationToken ct) =>
        ChatToolArgumentCheck.CheckAsync(this, args, () => ExecuteAsync(args, userId, ct));

    public Task<ToolResult> ExecuteAsync(JsonElement args, Guid userId, CancellationToken ct)
    {
        string? title = null;
        if (args.TryGetProperty("title", out var titleEl) && titleEl.ValueKind == JsonValueKind.String)
            title = titleEl.GetString();

        if (!args.TryGetProperty("one_small_step", out var mode) || mode.ValueKind is JsonValueKind.False or JsonValueKind.Null)
            return Task.FromResult(new ToolResult(true, EntityName: title));
        if (mode.ValueKind != JsonValueKind.True)
            return Task.FromResult(new ToolResult(false, Error: "one_small_step must be a boolean."));

        var items = args.TryGetProperty("suggested_sub_habits", out var suggestions) && suggestions.ValueKind == JsonValueKind.Array
            ? suggestions.EnumerateArray().ToArray() : [];
        var step = items.Length == 1 && items[0].ValueKind == JsonValueKind.Object
            ? JsonArgumentParser.GetOptionalString(items[0], "title") : null;
        var language = JsonArgumentParser.GetOptionalString(args, "language");
        var input = new StartingStepArguments(title, step, language, items.Length);
        if (!new StartingStepArgumentsValidator().Validate(input).IsValid)
            return Task.FromResult(new ToolResult(false,
                Error: "Return exactly one nonempty starting action and goal, each at most 200 characters on one line, with language en or pt-BR."));

        return Task.FromResult(new ToolResult(true, EntityName: title,
            Payload: new StartingStep(title!, step!, language!)));
    }
}
