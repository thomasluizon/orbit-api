using System.Text.Json;
using System.Text.Json.Nodes;
using MediatR;
using Orbit.Application.Habits.Commands;
using Orbit.Application.Tags.Commands;
using Orbit.Application.Tags.Queries;

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
            {
                habit.Remove("preview_item_id");
                habit.Remove(HabitHeadingParents.CandidateIdsField);
            }
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

        if (HasExistingParent(args, habitsEl))
            return await ExecuteWithParentsAsync(args, habitsEl, items, userId, checkOnly, ct);

        if (habitsEl.EnumerateArray().Any(HasGoalLinks))
        {
            var created = new List<BulkCreateItemResult>();
            for (var index = 0; index < items.Count; index++)
            {
                var createdItem = await ExecuteTreeAsync(habitsEl[index], items[index], userId, null, checkOnly, ct);
                if (!createdItem.Success) return createdItem;
                created.Add(new(index, BulkItemStatus.Success,
                    Guid.TryParse(createdItem.EntityId, out var id) ? id : null, items[index].Title));
            }
            return new ToolResult(true, Payload: new BulkCreateResult(created));
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

    private static bool HasExistingParent(JsonElement args, JsonElement habits) =>
        args.TryGetProperty("parent_habit_id", out _)
        || habits.EnumerateArray().Any(habit => habit.TryGetProperty("parent_habit_id", out _));

    private async Task<ToolResult> ExecuteWithParentsAsync(JsonElement args, JsonElement habitsEl,
        IReadOnlyList<BulkHabitItem> items, Guid userId, bool checkOnly, CancellationToken ct)
    {
        var created = new List<BulkCreateItemResult>();
        for (var index = 0; index < items.Count; index++)
        {
            var item = items[index];
            var source = habitsEl[index];
            var hasParent = source.TryGetProperty("parent_habit_id", out var parentValue)
                || args.TryGetProperty("parent_habit_id", out parentValue);
            if (!hasParent)
            {
                var rootCommand = new BulkCreateHabitsCommand(userId, [item]);
                if (checkOnly)
                {
                    var checkedRoot = await ChatToolArgumentCheck.CheckCommandAsync(mediator, rootCommand, ct);
                    if (!checkedRoot.Success) return checkedRoot;
                }
                else
                {
                    var rootResult = await mediator.Send(rootCommand, ct);
                    if (rootResult.IsFailure) return ToolResult.FromFailure(rootResult);
                    created.AddRange(rootResult.Value.Results.Select(result => result with { Index = index }));
                }
                continue;
            }
            if (parentValue.ValueKind != JsonValueKind.String || !Guid.TryParse(parentValue.GetString(), out var parentId))
                return new ToolResult(false, Error: "parent_habit_id must be a valid habit ID.");
            var childResult = await ExecuteTreeAsync(source, item, userId, parentId, checkOnly, ct);
            if (!childResult.Success)
                return childResult;
            created.Add(new BulkCreateItemResult(index, BulkItemStatus.Success,
                Guid.TryParse(childResult.EntityId, out var childId) ? childId : null, item.Title));
        }
        return new ToolResult(true, EntityName: $"{items.Count} sub-habits", Payload: new BulkCreateResult(created));
    }

    private static bool HasGoalLinks(JsonElement item) => item.TryGetProperty("goal_ids", out _)
        || item.TryGetProperty("sub_habits", out var children) && children.EnumerateArray().Any(HasGoalLinks);

    private async Task<ToolResult> ExecuteTreeAsync(JsonElement source, BulkHabitItem item, Guid userId,
        Guid? parentId, bool checkOnly, CancellationToken ct)
    {
        var options = new HabitCommandOptions(item.Days, item.DueTime, item.DueEndTime,
            item.ReminderEnabled, item.ReminderTimes, item.SlipAlertEnabled, item.ChecklistItems,
            item.ScheduledReminders, item.EndDate, item.IsFlexible, item.RelativeReminders);
        IRequest<Orbit.Domain.Common.Result<Guid>> command = parentId is { } parent
            ? new CreateSubHabitCommand(userId, parent, item.Title, item.Description,
                item.FrequencyUnit, item.FrequencyQuantity, item.IsBadHabit, item.DueDate, options,
                Emoji: item.Emoji, InheritParentFrequency: item.InheritParentFrequency,
                IntervalWeeks: item.IntervalWeeks, StartDate: item.StartDate)
            : new CreateHabitCommand(userId, item.Title, item.Description, item.FrequencyUnit,
                item.FrequencyQuantity, item.IsBadHabit, DueDate: item.DueDate, IsGeneral: item.IsGeneral,
                Options: options, GoalIds: JsonArgumentParser.ParseGuidArray(source, "goal_ids"),
                Emoji: item.Emoji, IntervalWeeks: item.IntervalWeeks, StartDate: item.StartDate);
        if (checkOnly)
        {
            var check = await ChatToolArgumentCheck.CheckCommandAsync(mediator, command, ct);
            if (!check.Success) return check;
            return new ToolResult(true);
        }
        var result = await mediator.Send(command, ct);
        if (result.IsFailure) return ToolResult.FromFailure(result);
        var id = result.Value;
        if (item.Tags is { Count: > 0 })
        {
            var available = await mediator.Send(new GetTagsQuery(userId), ct);
            if (available.IsFailure) return ToolResult.FromFailure(available);
            var tagIds = new List<Guid>();
            foreach (var name in item.Tags)
            {
                var matches = available.Value.Where(tag => string.Equals(tag.Name, name, StringComparison.OrdinalIgnoreCase)).ToArray();
                if (matches.Length > 1) return new ToolResult(false, Error: "tag_choice_required");
                if (matches.Length == 1) tagIds.Add(matches[0].Id);
                else
                {
                    var added = await mediator.Send(new CreateTagCommand(userId, name, "#7c3aed"), ct);
                    if (added.IsFailure) return ToolResult.FromFailure(added);
                    tagIds.Add(added.Value);
                }
            }
            var tags = await mediator.Send(new AssignTagsCommand(userId, id, tagIds), ct);
            if (tags.IsFailure) return ToolResult.FromFailure(tags);
        }
        if (parentId is not null && JsonArgumentParser.ParseGuidArray(source, "goal_ids") is { Count: > 0 } goals)
        {
            var linked = await mediator.Send(new LinkGoalsToHabitCommand(userId, id, goals), ct);
            if (linked.IsFailure) return ToolResult.FromFailure(linked);
        }
        if (source.TryGetProperty("sub_habits", out var children))
            foreach (var child in children.EnumerateArray())
            {
                var parsed = ParseBulkHabitItem(child);
                if (parsed is null) return new ToolResult(false, Error: "Each child needs a title.");
                var added = await ExecuteTreeAsync(child, parsed, userId, id, false, ct);
                if (!added.Success) return added;
            }
        return new ToolResult(true, EntityId: id.ToString(), EntityName: item.Title);
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
            IntervalWeeks: oneTime ? null : JsonArgumentParser.GetOptionalInt(el, "interval_weeks"),
            ReminderEnabled: JsonArgumentParser.GetOptionalBool(el, "reminder_enabled") ?? false,
            ReminderTimes: JsonArgumentParser.ParseIntArray(el, "reminder_times"),
            ScheduledReminders: JsonArgumentParser.ParseScheduledReminders(el),
            RelativeReminders: JsonArgumentParser.ParseRelativeReminders(el),
            Tags: JsonArgumentParser.ParseStringArray(el, "tag_names"),
            SlipAlertEnabled: JsonArgumentParser.GetOptionalBool(el, "slip_alert_enabled") ?? false,
            StartDate: JsonArgumentParser.ParseDateOnly(el, "start_date"))
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

        properties["start_date"] = new { type = JsonSchemaTypes.String, description = "YYYY-MM-DD recurrence anchor." };
        properties["reminder_enabled"] = new { type = JsonSchemaTypes.Boolean };
        properties["reminder_times"] = new { type = JsonSchemaTypes.Array, items = new { type = JsonSchemaTypes.Integer } };
        properties["scheduled_reminders"] = BulkHabitToolArguments.ScheduledRemindersSchema();
        properties["relative_reminders"] = BulkHabitToolArguments.RelativeRemindersSchema();
        properties["slip_alert_enabled"] = new { type = JsonSchemaTypes.Boolean };
        properties["tag_names"] = new { type = JsonSchemaTypes.Array, items = new { type = JsonSchemaTypes.String } };
        properties["goal_ids"] = new { type = JsonSchemaTypes.Array, items = new { type = JsonSchemaTypes.String } };
        if (includeSubHabits)
        {
            properties["parent_habit_id"] = new { type = JsonSchemaTypes.String, description = "Existing parent for this item. Overrides the shared parent. Resolve headings against account habits; never turn them into description text." };
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
