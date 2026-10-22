using System.Text.Json;
using System.Text.Json.Nodes;
using MediatR;
using Orbit.Application.Habits.Commands;

namespace Orbit.Application.Chat.Tools.Implementations;

public class BulkCreateHabitsTool(
    IMediator mediator) : IAiTool, IArgumentCheckTool, ITransactionalAiTool
{
    private const string TitleProperty = "title";

    public string Name => "bulk_create_habits";

    public string Description =>
        "Create multiple habits with inline sub-habits." + "\n" + HabitModelInstructions.All;

    public object GetParameterSchema() => new
    {
        type = JsonSchemaTypes.Object,
        properties = new
        {
            parent_habit_id = new { type = JsonSchemaTypes.String, description = "Existing parent habit ID. When supplied, create the items as children of this habit, never create the parent again." },
            habits = new
            {
                type = JsonSchemaTypes.Array,
                description = HabitModelInstructions.Batch,
                items = HabitItemSchema(includeSubHabits: true)
            }
        },
        required = new[] { "habits" }
    };

    public Task<ToolResult> ExecuteAsync(JsonElement args, Guid userId, CancellationToken ct) =>
        ExecuteCoreAsync(args, userId, ct, checkOnly: false);

    public Task<Orbit.Domain.Common.Result> CheckArgumentsAsync(JsonElement args, Guid userId, CancellationToken ct)
    {
        var schemaArguments = JsonNode.Parse(args.GetRawText());
        if (schemaArguments?["habits"] is JsonArray habits)
            foreach (var habit in habits.OfType<JsonObject>())
                habit.Remove("preview_item_id");
        return ChatToolArgumentCheck.CheckAsync(this, JsonSerializer.SerializeToElement(schemaArguments),
            () => ExecuteCoreAsync(args, userId, ct, checkOnly: true));
    }

    private async Task<ToolResult> ExecuteCoreAsync(JsonElement args, Guid userId, CancellationToken ct, bool checkOnly)
    {
        args = OptionalToolArguments.OmitOptionalNulls(args, GetParameterSchema());
        if (!args.TryGetProperty("habits", out var habitsEl) || habitsEl.ValueKind != JsonValueKind.Array)
            return new ToolResult(false, Error: "habits is required and must be an array.");

        var semantics = new Validators.HabitProposalValidator().Validate(new Validators.HabitProposal(string.Empty, args));
        if (!semantics.IsValid)
            return new ToolResult(false, Error: semantics.Errors[0].ErrorMessage, ErrorCode: semantics.Errors[0].ErrorCode);
        var items = new List<BulkHabitItem>();
        foreach (var habitEl in habitsEl.EnumerateArray())
        {
            if (habitEl.ValueKind != JsonValueKind.Object)
                return new ToolResult(false, Error: "Each habit must be an object.");
            var fieldError = BulkHabitToolArguments.CheckCreateHabitFields(habitEl);
            if (fieldError is not null)
                return new ToolResult(false, Error: fieldError);
            var item = ParseBulkHabitItem(habitEl);
            if (item is null)
                return new ToolResult(false, Error: "Each habit requires a non-empty title.");
            items.Add(item);
        }

        if (items.Count == 0)
            return new ToolResult(false, Error: "No habits provided.");

        if (args.TryGetProperty("parent_habit_id", out var parentValue))
        {
            if (parentValue.ValueKind != JsonValueKind.String || !Guid.TryParse(parentValue.GetString(), out var parentId))
                return new ToolResult(false, Error: "parent_habit_id must be a valid habit ID.");
            if (items.Any(item => item.SubHabits is { Count: > 0 }))
                return new ToolResult(false, Error: "Create children directly under the existing parent; nested sub_habits are not supported in this call.");
            var created = new List<BulkCreateItemResult>();
            foreach (var item in items)
            {
                var childCommand = new CreateSubHabitCommand(userId, parentId, item.Title, item.Description,
                    item.FrequencyUnit, item.FrequencyQuantity, item.IsBadHabit, item.DueDate,
                    new HabitCommandOptions(Days: item.Days, DueTime: item.DueTime, DueEndTime: item.DueEndTime,
                        IsFlexible: item.IsFlexible, EndDate: item.EndDate, ChecklistItems: item.ChecklistItems), Emoji: item.Emoji, InheritParentFrequency: item.InheritParentFrequency,
                    IntervalWeeks: item.IntervalWeeks);
                if (checkOnly)
                {
                    var checkedItem = await ChatToolArgumentCheck.CheckCommandAsync(mediator, childCommand, ct);
                    if (!checkedItem.Success)
                        return checkedItem;
                }
                else
                {
                    var childResult = await mediator.Send(childCommand, ct);
                    if (childResult.IsFailure)
                        return ToolResult.FromFailure(childResult);
                    created.Add(new BulkCreateItemResult(created.Count, BulkItemStatus.Success, childResult.Value, item.Title));
                }
            }
            return new ToolResult(true, EntityName: $"{items.Count} sub-habits", Payload: new BulkCreateResult(created));
        }

        var command = new BulkCreateHabitsCommand(userId, items);
        if (checkOnly)
            return await ChatToolArgumentCheck.CheckCommandAsync(mediator, command, ct);

        var result = await mediator.Send(command, ct);

        if (result.IsFailure)
            return ToolResult.FromFailure(result);

        var successCount = result.Value.Results.Count(item => item.Status == BulkItemStatus.Success);
        return new ToolResult(true, EntityName: $"{successCount}/{items.Count} habits created", Payload: result.Value);
    }

    internal static BulkHabitItem? ParseBulkHabitItem(JsonElement el, bool allowEmptyTitle = false)
    {
        var title = JsonArgumentParser.GetOptionalString(el, TitleProperty);
        if (!allowEmptyTitle && string.IsNullOrWhiteSpace(title))
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

        var parsedFrequency = JsonArgumentParser.ParseFrequencyUnit(el);
        if (parsedFrequency.IsFailure)
            return null;
        var frequencyUnit = parsedFrequency.Value;
        var oneTime = JsonArgumentParser.GetOptionalString(el, "frequency_unit") == "OneTime";
        var frequencyQuantity = oneTime ? null : JsonArgumentParser.GetOptionalInt(el, "frequency_quantity")
            ?? (frequencyUnit is not null ? 1 : (int?)null);

        return new BulkHabitItem(
            title ?? string.Empty,
            JsonArgumentParser.GetOptionalString(el, "description"),
            frequencyUnit,
            frequencyQuantity,
            Days: oneTime ? null : JsonArgumentParser.ParseDays(el),
            IsBadHabit: JsonArgumentParser.GetOptionalBool(el, "is_bad_habit") ?? false,
            DueDate: JsonArgumentParser.ParseDateOnly(el, "due_date"),
            DueTime: JsonArgumentParser.ParseTimeOnly(el, "due_time"),
            DueEndTime: JsonArgumentParser.ParseTimeOnly(el, "due_end_time"),
            IsGeneral: JsonArgumentParser.GetOptionalBool(el, "is_general") ?? false,
            EndDate: JsonArgumentParser.ParseDateOnly(el, "end_date"),
            IsFlexible: JsonArgumentParser.GetOptionalBool(el, "is_flexible") ?? false,
            ChecklistItems: JsonArgumentParser.ParseChecklistItems(el),
            SubHabits: subHabits,
            Emoji: JsonArgumentParser.GetOptionalString(el, "emoji"),
            IntervalWeeks: oneTime ? null : JsonArgumentParser.GetOptionalInt(el, "interval_weeks"))
        { InheritParentFrequency = !oneTime };
    }

    private static object HabitItemSchema(bool includeSubHabits)
    {
        var properties = new Dictionary<string, object>
        {
            [TitleProperty] = new { type = JsonSchemaTypes.String },
            ["description"] = new { type = JsonSchemaTypes.String },
            ["emoji"] = new { type = new[] { JsonSchemaTypes.String, "null" }, description = "Emoji icon. Pick a relevant one when clear." },
            ["frequency_unit"] = new { type = new[] { JsonSchemaTypes.String, "null" }, description = HabitModelInstructions.Schedule, @enum = JsonSchemaTypes.FrequencyOrOneTimeEnum },
            ["frequency_quantity"] = new { type = JsonSchemaTypes.Integer, description = "How often. Defaults to 1." },
            ["interval_weeks"] = new { type = JsonSchemaTypes.Integer, description = "Repeat interval in weeks. Omit for every week. Minimum 1." },
            ["days"] = new { type = JsonSchemaTypes.Array, description = "Specific weekdays. Only when frequency_quantity is 1.", items = new { type = JsonSchemaTypes.String, @enum = JsonSchemaTypes.WeekdayEnum } },
            ["due_date"] = new { type = JsonSchemaTypes.String, description = "YYYY-MM-DD. Defaults to today." },
            ["end_date"] = new { type = new[] { JsonSchemaTypes.String, "null" }, description = "YYYY-MM-DD. Optional end date." },
            ["due_time"] = new { type = JsonSchemaTypes.String, description = "HH:mm" },
            ["due_end_time"] = new { type = new[] { JsonSchemaTypes.String, "null" }, description = "HH:mm end time or deadline." },
            ["is_bad_habit"] = new { type = JsonSchemaTypes.Boolean, description = HabitModelInstructions.Polarity },
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
                        text = new { type = JsonSchemaTypes.String },
                        is_checked = new { type = JsonSchemaTypes.Boolean, description = "Default: false." }
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
