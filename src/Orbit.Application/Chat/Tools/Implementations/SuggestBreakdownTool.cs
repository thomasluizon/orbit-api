using System.Text.Json;

namespace Orbit.Application.Chat.Tools.Implementations;

public class SuggestBreakdownTool : IAiTool, IArgumentCheckTool
{
    public string Name => "suggest_breakdown";

    public string Description =>
        "Suggest sub-habits for a vague request, without creating them.";

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
                        title = new { type = JsonSchemaTypes.String },
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

        return Task.FromResult(new ToolResult(true, EntityName: title));
    }
}
