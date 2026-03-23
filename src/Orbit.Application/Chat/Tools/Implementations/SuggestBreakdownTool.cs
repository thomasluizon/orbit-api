using System.Text.Json;

namespace Orbit.Application.Chat.Tools.Implementations;

public class SuggestBreakdownTool : IAiTool
{
    public string Name => "suggest_breakdown";

    public string Description =>
        "Suggest breaking down a broad/vague habit into smaller sub-habits. Returns suggestions for the user to review and accept - does NOT create anything. Use when the user's request is too vague for a direct CreateHabit.";

    public object GetParameterSchema() => new
    {
        type = "OBJECT",
        properties = new
        {
            title = new { type = "STRING", description = "The broad habit title to break down" },
            suggested_sub_habits = new
            {
                type = "ARRAY",
                description = "Suggested sub-habit breakdowns",
                items = new
                {
                    type = "OBJECT",
                    properties = new
                    {
                        title = new { type = "STRING", description = "Sub-habit title" },
                        description = new { type = "STRING", description = "Optional description" },
                        frequency_unit = new
                        {
                            type = "STRING",
                            @enum = new[] { "Day", "Week", "Month", "Year" }
                        },
                        frequency_quantity = new { type = "INTEGER" },
                        days = new { type = "ARRAY", items = new { type = "STRING" } }
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
