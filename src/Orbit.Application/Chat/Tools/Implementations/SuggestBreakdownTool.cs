using System.Text.Json;
using Orbit.Application.Chat.Validators;
using Orbit.Domain.Models;

namespace Orbit.Application.Chat.Tools.Implementations;

public class SuggestBreakdownTool : IAiTool, IArgumentCheckTool
{
    public string Name => "suggest_breakdown";

    public string Description =>
        "Suggest sub-habits without creating them. When the person is stuck starting an activity, set one_small_step=true, "
        + "language to en or pt-BR matching their message, title to the activity and suggested_sub_habits to exactly one concrete starting action. Never return an empty array. "
        + "For a request to make a step smaller, return an easier action requiring less effort than the previous step, keeping the same activity. "
        + "The server returns oneSmallStep with smaller and save actions. Emit no prose or lists alongside it. "
        + "Suggestions never save anything. Only an explicit save request uses create_habit for a Task or update_checklist, "
        + "preserving existing checklist items and resolving the owned habit first. Both require the existing approval preview.";

    public object GetParameterSchema() => new
    {
        type = JsonSchemaTypes.Object,
        properties = new
        {
            one_small_step = new { type = JsonSchemaTypes.Boolean, description = "Return exactly one starting step, or a smaller refinement, as a oneSmallStep block. Default false." },
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
