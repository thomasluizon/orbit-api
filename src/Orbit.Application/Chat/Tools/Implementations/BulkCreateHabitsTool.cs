using static Orbit.Application.Chat.Tools.ToolArgumentNames;
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

    public bool SupportsAtomicExecution(JsonElement arguments)
    {
        if (!arguments.TryGetProperty(ToolArgumentNames.Habits, out var habits) || habits.ValueKind != JsonValueKind.Array
            || habits.EnumerateArray().Any(HasGoalLinks)) return false;
        if (!HasExistingParent(arguments, habits) && !habits.EnumerateArray().Any(RequiresTreeCommands)) return true;
        return habits.EnumerateArray().All(item => item.TryGetProperty(HabitId, out _)
            || item.TryGetProperty(ParentHabitId, out _) || arguments.TryGetProperty(ParentHabitId, out _));
    }

    internal static bool HasGoalLinks(JsonElement item) => item.TryGetProperty(GoalIds, out _)
        || item.TryGetProperty(SubHabits, out var children) && children.EnumerateArray().Any(HasGoalLinks);

    public string Name => ToolArgumentNames.BulkCreateHabits;

    public string Description =>
        "Create multiple habits with inline sub-habits.";

    public object GetParameterSchema() => new
    {
        type = JsonSchemaTypes.Object,
        properties = new
        {
            parent_habit_id = new { type = JsonSchemaTypes.String, description = "Existing parent habit ID. When supplied, create the items as children of this habit, never create the parent again." },
            habits = new
            {
                type = JsonSchemaTypes.Array,
                description = "The complete set of habits to create.",
                items = HabitItemSchema(Orbit.Application.Common.AppConstants.MaxHabitDepth)
            }
        },
        required = new[] { "habits" }
    };

    public Task<ToolResult> ExecuteAsync(JsonElement args, Guid userId, CancellationToken ct) =>
        ExecuteCoreAsync(args, userId, ct, checkOnly: false);

    public Task<Orbit.Domain.Common.Result> CheckArgumentsAsync(JsonElement args, Guid userId, CancellationToken ct)
    {
        var schemaArguments = JsonNode.Parse(args.GetRawText());
        if (schemaArguments?[ToolArgumentNames.Habits] is JsonArray habits)
            foreach (var habit in habits.OfType<JsonObject>())
            {
                habit.Remove("preview_item_id");
                habit.Remove(HabitHeadingParents.CandidateIdsField);
                habit.Remove(HabitHeadingParents.SuggestedParentField);
                habit.Remove(HabitHeadingParents.ExistingCandidatesField);
            }
        return ChatToolArgumentCheck.CheckAsync(this, JsonSerializer.SerializeToElement(schemaArguments),
            () => ExecuteCoreAsync(args, userId, ct, checkOnly: true));
    }

    private async Task<ToolResult> ExecuteCoreAsync(JsonElement args, Guid userId, CancellationToken ct, bool checkOnly)
    {
        args = OptionalToolArguments.OmitOptionalNulls(args, GetParameterSchema());
        if (!args.TryGetProperty(ToolArgumentNames.Habits, out var habitsEl) || habitsEl.ValueKind != JsonValueKind.Array)
            return new ToolResult(false, Error: "habits is required and must be an array.");

        var semantics = new Validators.HabitProposalValidator().Validate(new Validators.HabitProposal(string.Empty, args));
        if (!semantics.IsValid)
            return new ToolResult(false, Error: semantics.Errors[0].ErrorMessage, ErrorCode: semantics.Errors[0].ErrorCode);
        var items = new List<BulkHabitItem>();
        foreach (var habitEl in habitsEl.EnumerateArray())
        {
            if (habitEl.ValueKind != JsonValueKind.Object)
                return new ToolResult(false, Error: "Each habit must be an object.");
            var fieldError = ValidateTree(habitEl);
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

        if (habitsEl.EnumerateArray().Any(RequiresTreeCommands))
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
        args.TryGetProperty(ParentHabitId, out _)
        || habits.EnumerateArray().Any(habit => habit.TryGetProperty(ParentHabitId, out _));

    private async Task<ToolResult> ExecuteWithParentsAsync(JsonElement args, JsonElement habitsEl,
        List<BulkHabitItem> items, Guid userId, bool checkOnly, CancellationToken ct)
    {
        var created = new List<BulkCreateItemResult>();
        for (var index = 0; index < items.Count; index++)
        {
            var source = habitsEl[index];
            var hasParent = source.TryGetProperty(ParentHabitId, out var value) || args.TryGetProperty(ParentHabitId, out value);
            if (hasParent && (value.ValueKind != JsonValueKind.String || !Guid.TryParse(value.GetString(), out _)))
                return new ToolResult(false, Error: "parent_habit_id must be a valid habit ID.");
            var parent = hasParent ? Guid.Parse(value.GetString()!) : (Guid?)null;
            var result = await ExecuteTreeAsync(source, items[index], userId, parent, checkOnly, ct);
            if (!result.Success) return result;
            created.Add(new(index, BulkItemStatus.Success,
                Guid.TryParse(result.EntityId, out var id) ? id : null, items[index].Title));
        }
        return new ToolResult(true, EntityName: $"{items.Count} sub-habits", Payload: new BulkCreateResult(created));
    }

    private static bool RequiresTreeCommands(JsonElement item) =>
        new[] { GoalIds, TagIds, HabitId }.Any(field => item.TryGetProperty(field, out _))
        || item.TryGetProperty(SubHabits, out var children) && children.EnumerateArray().Any(RequiresTreeCommands);

    internal async Task<ToolResult> ExecuteSingleAsync(JsonElement source, Guid userId, CancellationToken ct)
    {
        if (source.ValueKind != JsonValueKind.Object || string.IsNullOrWhiteSpace(JsonArgumentParser.GetOptionalString(source, "title")))
            return new(false, Error: "title is required.");
        var error = ValidateTree(source);
        if (error is not null) return new(false, Error: error);
        var frequency = JsonArgumentParser.ParseFrequencyUnit(source);
        if (frequency.IsFailure) return ToolResult.FromFailure(frequency);
        var parent = Guid.TryParse(JsonArgumentParser.GetOptionalString(source, ParentHabitId), out var id) ? id : (Guid?)null;
        var item = ParseBulkHabitItem(source, isRoot: parent is null);
        if (item is null) return new ToolResult(false, Error: "A title and valid schedule are required.");
        if (parent is null) item = item with { SlipAlertEnabled = JsonArgumentParser.GetOptionalBool(source, "slip_alert_enabled") ?? item.IsBadHabit };
        return await ExecuteTreeAsync(source, item, userId, parent, false, ct);
    }

    private static string? ValidateTree(JsonElement source)
    {
        var error = ValidateLinkIds(source) ?? BulkHabitToolArguments.CheckCreateHabitFields(source);
        if (error is not null) return error;
        if (!source.TryGetProperty(SubHabits, out var children)) return null;
        if (children.ValueKind != JsonValueKind.Array) return "sub_habits must be an array.";
        foreach (var child in children.EnumerateArray())
        {
            if (child.ValueKind != JsonValueKind.Object || string.IsNullOrWhiteSpace(JsonArgumentParser.GetOptionalString(child, TitleProperty)))
                return "Each child needs a title.";
            if (ValidateTree(child) is { } childError) return childError;
        }
        return null;
    }

    private static string? ValidateLinkIds(JsonElement source)
    {
        foreach (var field in new[] { TagIds, GoalIds })
            if (source.TryGetProperty(field, out var ids) && (ids.ValueKind != JsonValueKind.Array
                || ids.EnumerateArray().Any(id => id.ValueKind != JsonValueKind.String || !Guid.TryParse(id.GetString(), out _))))
                return $"{field} must contain valid GUIDs.";
        return null;
    }

    private async Task<ToolResult> ExecuteTreeAsync(JsonElement source, BulkHabitItem item, Guid userId,
        Guid? parentId, bool checkOnly, CancellationToken ct)
    {
        if (source.TryGetProperty(HabitId, out var existingId))
            return existingId.ValueKind == JsonValueKind.String && Guid.TryParse(existingId.GetString(), out var reusedId)
                ? await UpdateExistingTreeAsync(source, item, reusedId, parentId, userId, checkOnly, ct)
                : new ToolResult(false, Error: "habit_choice_required");
        var command = CreateTreeCommand(source, item, userId, parentId);
        if (checkOnly)
        {
            var check = await ChatToolArgumentCheck.CheckCommandAsync(mediator, command, ct);
            if (!check.Success) return check;
            return new ToolResult(true);
        }
        var result = await mediator.Send(command, ct);
        if (result.IsFailure) return ToolResult.FromFailure(result);
        var id = result.Value;
        var tags = await AssignTreeTagsAsync(source, item, userId, id, ct);
        if (!tags.Success) return tags;
        if (parentId is not null && JsonArgumentParser.ParseGuidArray(source, GoalIds) is { Count: > 0 } goals)
        {
            var linked = await mediator.Send(new LinkGoalsToHabitCommand(userId, id, goals), ct);
            if (linked.IsFailure) return ToolResult.FromFailure(linked);
        }
        return await ExecuteChildrenAsync(source, item, userId, id, ct);
    }

    private static IRequest<Orbit.Domain.Common.Result<Guid>> CreateTreeCommand(JsonElement source, BulkHabitItem item,
        Guid userId, Guid? parentId)
    {
        var options = new HabitCommandOptions(item.Days ?? (item.DueDate.HasValue ? [] : null), item.DueTime, item.DueEndTime,
            item.ReminderEnabled, item.ReminderTimes, item.SlipAlertEnabled, item.ChecklistItems,
            item.ScheduledReminders, item.EndDate, item.IsFlexible, item.RelativeReminders);
        return parentId is { } parent
            ? new CreateSubHabitCommand(userId, parent, item.Title, item.Description,
                item.FrequencyUnit, item.FrequencyQuantity, item.IsBadHabit, item.DueDate, options,
                TagIds: JsonArgumentParser.ParseGuidArray(source, TagIds),
                Emoji: item.Emoji, InheritParentFrequency: item.InheritParentFrequency,
                IntervalWeeks: item.IntervalWeeks, StartDate: item.StartDate)
            : new CreateHabitCommand(userId, item.Title, item.Description, item.FrequencyUnit,
                item.FrequencyQuantity, item.IsBadHabit, DueDate: item.DueDate, IsGeneral: item.IsGeneral,
                Options: options, TagIds: JsonArgumentParser.ParseGuidArray(source, TagIds),
                GoalIds: JsonArgumentParser.ParseGuidArray(source, GoalIds),
                Emoji: item.Emoji, IntervalWeeks: item.IntervalWeeks, StartDate: item.StartDate);
    }

    private async Task<ToolResult> AssignTreeTagsAsync(JsonElement source, BulkHabitItem item, Guid userId, Guid id, CancellationToken ct)
    {
        if (item.Tags is not { Count: > 0 } && !source.TryGetProperty(TagNames, out _)) return new(true);
        var available = await mediator.Send(new GetTagsQuery(userId, BypassCache: true), ct);
        if (available.IsFailure) return ToolResult.FromFailure(available);
        var tagIds = new List<Guid>(JsonArgumentParser.ParseGuidArray(source, TagIds) ?? []);
        foreach (var name in (item.Tags ?? []).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var matches = available.Value.Where(tag => string.Equals(tag.Name, name, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length > 1) return new ToolResult(false, Error: "tag_choice_required");
            if (matches.Length == 1)
            {
                tagIds.Add(matches[0].Id);
                continue;
            }
            var added = await mediator.Send(new CreateTagCommand(userId, name, "#7c3aed"), ct);
            if (added.IsFailure) return ToolResult.FromFailure(added);
            tagIds.Add(added.Value);
        }
        var tags = await mediator.Send(new AssignTagsCommand(userId, id, tagIds), ct);
        return tags.IsFailure ? ToolResult.FromFailure(tags) : new(true);
    }

    private async Task<ToolResult> ExecuteChildrenAsync(JsonElement source, BulkHabitItem item, Guid userId, Guid id, CancellationToken ct)
    {
        var children = item.SubHabits ?? [];
        for (var index = 0; index < children.Count; index++)
        {
            var result = await ExecuteTreeAsync(source.GetProperty(SubHabits)[index], children[index], userId, id, false, ct);
            if (!result.Success) return result;
        }
        return new ToolResult(true, EntityId: id.ToString(), EntityName: item.Title);
    }

    private async Task<ToolResult> UpdateExistingTreeAsync(JsonElement source, BulkHabitItem item, Guid habitId,
        Guid? parentId, Guid userId, bool checkOnly, CancellationToken ct)
    {
        var patch = JsonNode.Parse(source.GetRawText())!.AsObject();
        foreach (var field in new[] { HabitId, ParentHabitId, SubHabits, TagNames, TagIds, GoalIds, "preview_item_id", HabitHeadingParents.SuggestedParentField, HabitHeadingParents.CandidateIdsField, HabitHeadingParents.ExistingCandidatesField }) patch.Remove(field);
        var (changes, error) = BulkHabitToolArguments.ParseUpdateFields(JsonSerializer.SerializeToElement(patch));
        if (error is not null) return new(false, Error: error);
        var command = new UpdateHabitFieldsCommand(userId, habitId, changes!,
            ReuseAssociationIds(source, GoalIds), ReuseAssociationIds(source, TagIds), parentId);
        if (checkOnly) return new(true);
        var updated = await mediator.Send(command, ct);
        if (updated.IsFailure) return ToolResult.FromFailure(updated);
        var tags = await AssignTreeTagsAsync(source, item, userId, habitId, ct);
        if (!tags.Success) return tags;
        return await ExecuteChildrenAsync(source, item, userId, habitId, ct);
    }

    private static Guid[]? ReuseAssociationIds(JsonElement source, string field) =>
        source.TryGetProperty(field, out var ids) && ids.ValueKind == JsonValueKind.Array
            ? ids.EnumerateArray().Select(id => Guid.Parse(id.GetString()!)).ToArray() : null;

    internal static BulkHabitItem? ParseBulkHabitItem(JsonElement el, bool allowEmptyTitle = false, bool isRoot = true)
    {
        var title = JsonArgumentParser.GetOptionalString(el, TitleProperty);
        if (!allowEmptyTitle && string.IsNullOrWhiteSpace(title))
            return null;

        var (subHabits, validChildren) = ParseChildren(el);
        if (!validChildren) return null;

        var parsedFrequency = JsonArgumentParser.ParseFrequencyUnit(el);
        if (parsedFrequency.IsFailure)
            return null;
        var frequencyUnit = parsedFrequency.Value;
        var oneTime = IsOneTime(el, frequencyUnit, isRoot);
        int? frequencyQuantity = null;
        if (!oneTime)
            frequencyQuantity = JsonArgumentParser.GetOptionalInt(el, "frequency_quantity") ?? (frequencyUnit is not null ? 1 : (int?)null);

        var days = oneTime ? null : JsonArgumentParser.ParseDays(el);
        if (frequencyUnit == Orbit.Domain.Enums.FrequencyUnit.Week && frequencyQuantity == 1 && days is { Count: > 0 }
            && JsonArgumentParser.GetOptionalBool(el, IsFlexible) != true)
            frequencyUnit = Orbit.Domain.Enums.FrequencyUnit.Day;
        return new BulkHabitItem(
            title ?? string.Empty,
            JsonArgumentParser.GetOptionalString(el, "description"),
            frequencyUnit,
            frequencyQuantity,
            Days: days,
            IsBadHabit: JsonArgumentParser.GetOptionalBool(el, IsBadHabit) ?? false,
            DueDate: JsonArgumentParser.ParseDateOnly(el, DueDate),
            DueTime: JsonArgumentParser.ParseTimeOnly(el, DueTime),
            DueEndTime: JsonArgumentParser.ParseTimeOnly(el, "due_end_time"),
            IsGeneral: JsonArgumentParser.GetOptionalBool(el, "is_general") ?? false,
            EndDate: JsonArgumentParser.ParseDateOnly(el, EndDate),
            IsFlexible: JsonArgumentParser.GetOptionalBool(el, IsFlexible) ?? false,
            ChecklistItems: JsonArgumentParser.ParseChecklistItems(el),
            SubHabits: subHabits,
            Emoji: JsonArgumentParser.GetOptionalString(el, "emoji"),
            IntervalWeeks: oneTime ? null : JsonArgumentParser.GetOptionalInt(el, IntervalWeeks),
            ReminderEnabled: JsonArgumentParser.GetOptionalBool(el, "reminder_enabled") ?? false,
            ReminderTimes: JsonArgumentParser.ParseIntArray(el, "reminder_times"),
            ScheduledReminders: JsonArgumentParser.ParseScheduledReminders(el),
            RelativeReminders: JsonArgumentParser.ParseRelativeReminders(el),
            Tags: JsonArgumentParser.ParseStringArray(el, TagNames),
            SlipAlertEnabled: JsonArgumentParser.GetOptionalBool(el, "slip_alert_enabled") ?? false,
            StartDate: JsonArgumentParser.ParseDateOnly(el, StartDate))
        { InheritParentFrequency = !oneTime };
    }

    private static (List<BulkHabitItem>? Items, bool Valid) ParseChildren(JsonElement el)
    {
        List<BulkHabitItem>? subHabits = null;
        if (el.TryGetProperty(SubHabits, out var subEl) && subEl.ValueKind == JsonValueKind.Array)
        {
            subHabits = new List<BulkHabitItem>();
            foreach (var child in subEl.EnumerateArray())
            {
                var parsedChild = ParseBulkHabitItem(child, isRoot: false);
                if (parsedChild is null) return (null, false);
                subHabits.Add(parsedChild);
            }
        }

        return (subHabits, true);
    }

    private static bool IsOneTime(JsonElement item, Orbit.Domain.Enums.FrequencyUnit? unit, bool isRoot) =>
        JsonArgumentParser.GetOptionalString(item, "frequency_unit") == "OneTime"
        || isRoot && unit is null && JsonArgumentParser.ParseDateOnly(item, DueDate) is not null;

    internal static object HabitItemSchema(int remainingDepth)
    {
        var properties = new Dictionary<string, object>
        {
            [TitleProperty] = new { type = JsonSchemaTypes.String },
            ["description"] = new { type = JsonSchemaTypes.String },
            ["emoji"] = new { type = new[] { JsonSchemaTypes.String, "null" }, description = "Emoji icon. Pick a relevant one when clear." },
            ["frequency_unit"] = new { type = new[] { JsonSchemaTypes.String, "null" }, description = "OneTime for a dated task; Day, Week, Month or Year for recurrence.", @enum = JsonSchemaTypes.FrequencyOrOneTimeEnum },
            ["frequency_quantity"] = new { type = JsonSchemaTypes.Integer, description = "How often. Defaults to 1." },
            ["interval_weeks"] = new { type = JsonSchemaTypes.Integer, description = "Repeat interval in weeks. Omit for every week. Minimum 1." },
            ["days"] = new { type = JsonSchemaTypes.Array, description = "Specific weekdays. Only when frequency_quantity is 1.", items = new { type = JsonSchemaTypes.String, @enum = JsonSchemaTypes.WeekdayEnum } },
            ["due_date"] = new { type = JsonSchemaTypes.String, description = "YYYY-MM-DD. Defaults to today." },
            ["end_date"] = new { type = new[] { JsonSchemaTypes.String, "null" }, description = "YYYY-MM-DD. Optional end date." },
            ["due_time"] = new { type = JsonSchemaTypes.String, description = "HH:mm" },
            ["due_end_time"] = new { type = new[] { JsonSchemaTypes.String, "null" }, description = "HH:mm end time or deadline." },
            ["is_bad_habit"] = new { type = JsonSchemaTypes.Boolean, description = "True for ongoing reduction or slip tracking." },
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
        properties["habit_id"] = new { type = JsonSchemaTypes.String, description = "Existing habit ID to reuse and edit instead of creating a duplicate." };
        properties["tag_ids"] = new { type = JsonSchemaTypes.Array, items = new { type = JsonSchemaTypes.String } };
        properties["goal_ids"] = new { type = JsonSchemaTypes.Array, items = new { type = JsonSchemaTypes.String } };
        if (remainingDepth > 1)
        {
            properties["parent_habit_id"] = new { type = JsonSchemaTypes.String, description = "Existing parent for this item. Overrides the shared parent. Resolve headings against account habits; never turn them into description text." };
            properties["sub_habits"] = new
            {
                type = JsonSchemaTypes.Array,
                description = "Independently trackable child habits under this parent. Routine activities belong here under one parent; preserve each requested time. Choose this form without asking checklist versus children.",
                items = HabitItemSchema(remainingDepth - 1)
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
