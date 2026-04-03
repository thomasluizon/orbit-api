using System.Text.Json;

namespace Orbit.Application.Chat.Tools.Implementations;

public class SuggestBreakdownTool : IAiTool
{
    public string Name => "suggest_breakdown";

    public string Description =>
        "Suggest breaking down a broad/vague habit into smaller sub-habits. Returns suggestions for the user to review and accept - does NOT create anything. Use when the user's request is too vague for a direct CreateHabit.";

    public object GetParameterSchema() => new
    {
        type = JsonSchemaTypes.Object,
        properties = new
        {
            title = new { type = JsonSchemaTypes.String, description = "The broad habit title to break down" },
            suggested_sub_habits = new
            {
                type = JsonSchemaTypes.Array,
                description = "Suggested sub-habit breakdowns",
                items = new
                {
                    type = JsonSchemaTypes.Object,
                    properties = new
                    {
                        title = new { type = JsonSchemaTypes.String, description = "Sub-habit title" },
                        description = new { type = JsonSchemaTypes.String, description = "Optional description" },
                        frequency_unit = new
                        {
                            type = JsonSchemaTypes.String,
                            @enum = JsonSchemaTypes.FrequencyUnitEnum
                        },
                        frequency_quantity = new { type = JsonSchemaTypes.Integer },
                        days = new { type = JsonSchemaTypes.Array, items = new { type = JsonSchemaTypes.String } }
                    },
                    required = new[] { "title" }
                }
            }
        },
        required = new[] { "title", "suggested_sub_habits" }
    };

    public Task<ToolResult> ExecuteAsync(JsonElement args, Guid userId, CancellationToken ct)
    {
        // SuggestBreakdown creates nothing -- it passes through the AI's suggestions.
        // The caller interprets this as a "Suggestion" status, not a creation.
        string? title = null;
        if (args.TryGetProperty("title", out var titleEl) && titleEl.ValueKind == JsonValueKind.String)
            title = titleEl.GetString();

        return Task.FromResult(new ToolResult(true, EntityName: title));
    }
}
