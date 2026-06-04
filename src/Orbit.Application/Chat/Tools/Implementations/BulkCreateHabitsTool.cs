using System.Text.Json;
using MediatR;
using Orbit.Application.Habits.Commands;

namespace Orbit.Application.Chat.Tools.Implementations;

public class BulkCreateHabitsTool(
    IMediator mediator) : IAiTool
{
    private const string TitleProperty = "title";

    public string Name => "bulk_create_habits";

    public string Description =>
        "Create multiple habits in a single operation. Each habit may contain its own sub-habits.";

    public object GetParameterSchema() => new
    {
        type = JsonSchemaTypes.Object,
        properties = new
        {
            habits = new
            {
                type = JsonSchemaTypes.Array,
                description = "Habits to create.",
                items = HabitItemSchema(includeSubHabits: true)
            }
        },
        required = new[] { "habits" }
    };

    public async Task<ToolResult> ExecuteAsync(JsonElement args, Guid userId, CancellationToken ct)
    {
        if (!args.TryGetProperty("habits", out var habitsEl) || habitsEl.ValueKind != JsonValueKind.Array)
            return new ToolResult(false, Error: "habits is required and must be an array.");

        var items = new List<BulkHabitItem>();
        foreach (var habitEl in habitsEl.EnumerateArray())
        {
            var item = ParseBulkHabitItem(habitEl);
            if (item is null)
                return new ToolResult(false, Error: "Each habit requires a non-empty title.");
            items.Add(item);
        }

        if (items.Count == 0)
            return new ToolResult(false, Error: "No habits provided.");

        var result = await mediator.Send(new BulkCreateHabitsCommand(userId, items), ct);

        if (result.IsFailure)
            return new ToolResult(false, Error: result.Error);

        var successCount = result.Value.Results.Count(item => item.Status == BulkItemStatus.Success);
        return new ToolResult(true, EntityName: $"{successCount}/{items.Count} habits created", Payload: result.Value);
    }

    private static BulkHabitItem? ParseBulkHabitItem(JsonElement el)
    {
        var title = JsonArgumentParser.GetOptionalString(el, TitleProperty);
        if (string.IsNullOrWhiteSpace(title))
            return null;

        List<BulkHabitItem>? subHabits = null;
        if (el.TryGetProperty("sub_habits", out var subEl) && subEl.ValueKind == JsonValueKind.Array)
        {
            subHabits = new List<BulkHabitItem>();
            foreach (var child in subEl.EnumerateArray())
            {
                var parsedChild = ParseBulkHabitItem(child);
                if (parsedChild is not null)
                    subHabits.Add(parsedChild);
            }
        }

        return new BulkHabitItem(
            title,
            JsonArgumentParser.GetOptionalString(el, "description"),
            JsonArgumentParser.ParseFrequencyUnit(el),
            JsonArgumentParser.GetOptionalInt(el, "frequency_quantity"),
            Days: JsonArgumentParser.ParseDays(el),
            IsBadHabit: JsonArgumentParser.GetOptionalBool(el, "is_bad_habit") ?? false,
            DueDate: JsonArgumentParser.ParseDateOnly(el, "due_date"),
            DueTime: JsonArgumentParser.ParseTimeOnly(el, "due_time"),
            IsGeneral: JsonArgumentParser.GetOptionalBool(el, "is_general") ?? false,
            EndDate: JsonArgumentParser.ParseDateOnly(el, "end_date"),
            IsFlexible: JsonArgumentParser.GetOptionalBool(el, "is_flexible") ?? false,
            ChecklistItems: JsonArgumentParser.ParseChecklistItems(el),
            SubHabits: subHabits,
            Emoji: JsonArgumentParser.GetOptionalString(el, "emoji"));
    }

    private static object HabitItemSchema(bool includeSubHabits)
    {
        var properties = new Dictionary<string, object>
        {
            [TitleProperty] = new { type = JsonSchemaTypes.String, description = "Name of the habit" },
            ["description"] = new { type = JsonSchemaTypes.String, description = "Optional description" },
            ["emoji"] = new { type = JsonSchemaTypes.String, description = "Emoji icon. Pick a relevant one when clear.", nullable = true },
            ["frequency_unit"] = new { type = JsonSchemaTypes.String, description = "Recurrence unit. Omit for one-time tasks.", nullable = true, @enum = JsonSchemaTypes.FrequencyUnitEnum },
            ["frequency_quantity"] = new { type = JsonSchemaTypes.Integer, description = "How often. Defaults to 1." },
            ["days"] = new { type = JsonSchemaTypes.Array, description = "Specific weekdays. Only when frequency_quantity is 1.", items = new { type = JsonSchemaTypes.String } },
            ["due_date"] = new { type = JsonSchemaTypes.String, description = "YYYY-MM-DD. Defaults to today." },
            ["end_date"] = new { type = JsonSchemaTypes.String, description = "YYYY-MM-DD. Optional end date.", nullable = true },
            ["due_time"] = new { type = JsonSchemaTypes.String, description = "HH:mm 24h format" },
            ["is_bad_habit"] = new { type = JsonSchemaTypes.Boolean, description = "Whether this is a bad habit to reduce. Defaults to false." },
            ["is_general"] = new { type = JsonSchemaTypes.Boolean, description = "Whether this is a general habit with no schedule. Defaults to false." },
            ["is_flexible"] = new { type = JsonSchemaTypes.Boolean, description = "Window-based tracking. Requires frequency_unit." },
            ["checklist_items"] = new
            {
                type = JsonSchemaTypes.Array,
                description = "Atomic sub-steps done together in one execution.",
                items = new
                {
                    type = JsonSchemaTypes.Object,
                    properties = new
                    {
                        text = new { type = JsonSchemaTypes.String, description = "Checklist item text" },
                        is_checked = new { type = JsonSchemaTypes.Boolean, description = "Whether checked. Defaults to false." }
                    },
                    required = new[] { "text" }
                }
            }
        };

        if (includeSubHabits)
        {
            properties["sub_habits"] = new
            {
                type = JsonSchemaTypes.Array,
                description = "Independently trackable child habits under this parent.",
                items = HabitItemSchema(includeSubHabits: false)
            };
        }

        return new
        {
            type = JsonSchemaTypes.Object,
            properties,
            required = new[] { TitleProperty }
        };
    }
}
