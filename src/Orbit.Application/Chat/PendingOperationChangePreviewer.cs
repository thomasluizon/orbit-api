using System.Globalization;
using System.Text.Json;
using Orbit.Domain.Common;
using Orbit.Application.Common;
using Orbit.Application.Chat.Tools;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Application.Habits.Commands;
using Orbit.Application.Habits.Validators;
using Orbit.Application.Habits.Queries;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using Orbit.Domain.ValueObjects;

namespace Orbit.Application.Chat;

public sealed class PendingOperationChangePreviewer(
    IGenericRepository<Habit> habitRepository,
    IGenericRepository<Goal> goalRepository,
    IGenericRepository<Tag> tagRepository,
    IUserDateService userDateService,
    AiToolRegistry toolRegistry,
    IDestructiveOperationPreviewer? destructivePreviewer = null,
    IGenericRepository<ChecklistTemplate>? templateRepository = null) : IPendingOperationChangePreviewer
{
    private const string IsBadHabitField = "is_bad_habit";
    private const string IsGeneralField = "is_general";
    private const string IsFlexibleField = "is_flexible";
    private const string ChecklistItemsField = "checklist_items";
    private const string SlipAlertEnabledField = "slip_alert_enabled";
    private const string DueEndTimeField = "due_end_time";
    private const string ReminderEnabledField = "reminder_enabled";
    private const string ReminderTimesField = "reminder_times";
    private const string ScheduledRemindersField = "scheduled_reminders";
    private const string RelativeRemindersField = "relative_reminders";
    private const string TimeFormat = "HH:mm";
    private const int MaxDisplayedEntries = 3;
    private const int MaxEntryLength = 60;
    private const string DateFormat = "yyyy-MM-dd";
    private static readonly HashSet<string> CreateFields = new(StringComparer.Ordinal)
    {
        "title", "description", "emoji", "frequency_unit", "frequency_quantity",
        "interval_weeks", "days", "due_date", "end_date", "due_time",
        IsBadHabitField, IsGeneralField, IsFlexibleField, ChecklistItemsField, "sub_habits",
        DueEndTimeField, SlipAlertEnabledField, ReminderEnabledField, ReminderTimesField,
        ScheduledRemindersField, RelativeRemindersField
    };

    public async Task<Result<PendingOperationChangePreview?>> PreviewAsync(
        Guid userId,
        string operationId,
        JsonElement arguments,
        CancellationToken cancellationToken = default)
    {
        if (!arguments.TryGetProperty("revised_items", out _) && toolRegistry.GetTool(operationId) is { } tool)
            arguments = OptionalToolArguments.OmitOptionalNulls(arguments, tool.GetParameterSchema());
        if (RequiresEmojiInference(operationId, arguments))
            return await PreviewInferredEmojisAsync(userId, arguments, cancellationToken);
        return Result.Success(await PreviewChangesAsync(userId, operationId, arguments, cancellationToken));
    }

    private async Task<PendingOperationChangePreview?> PreviewChangesAsync(
        Guid userId, string operationId, JsonElement arguments, CancellationToken cancellationToken)
    {
        arguments = await ResolveUnlogDateAsync(userId, operationId, arguments, cancellationToken);
        if (HasLogDates(operationId, arguments))
        {
            var expanded = await BulkHabitLogArguments.ExpandAsync(arguments, habitRepository,
                userDateService, userId, cancellationToken);
            if (expanded.IsFailure)
                return null;
            arguments = expanded.Value;
        }
        if (operationId == "bulk_create_habits")
            return await PreviewCreateAsync(userId, arguments, cancellationToken);

        if (operationId is not ("bulk_update_habits" or "bulk_reschedule_habits"
            or "bulk_delete_habits" or "bulk_log_habits" or "bulk_skip_habits"
            or "bulk_update_habit_emojis" or "delete_habit"))
        {
            return await PreviewOtherAsync(userId, operationId, arguments, cancellationToken);
        }

        if (operationId == "delete_habit")
            return await PreviewSingleHabitAsync(userId, arguments, cancellationToken);

        var isRevised = arguments.TryGetProperty("revised_items", out var revisedItems);
        if (isRevised && revisedItems.ValueKind != JsonValueKind.Array)
            return null;
        var revisedChanges = new Dictionary<Guid, BulkHabitChanges>();
        var (filter, filterError) = ResolveFilter(operationId, arguments,
            isRevised, revisedItems, revisedChanges);
        if (filterError is not null || filter is null)
            return null;

        var changes = isRevised ? null : ResolveChanges(operationId, arguments);

        if (!isRevised && operationId is ("bulk_update_habits" or "bulk_reschedule_habits")
            && (changes is null || !changes.HasAnyChange))
            return null;

        var habits = await BulkHabitSelection.LoadAsync(habitRepository, userId, filter, cancellationToken);
        var today = await userDateService.GetUserTodayAsync(userId, cancellationToken);
        if (IsDatedRevision(operationId, isRevised))
            return PreviewDatedItems(operationId, arguments, habits);
        var rows = new List<PendingOperationChange>();
        var items = new List<PendingOperationItem>();
        foreach (var habit in habits)
        {
            var fields = new List<PendingOperationChange>();
            var actualChangeCount = 0;
            var itemChanges = isRevised ? revisedChanges.GetValueOrDefault(habit.Id) : changes;
            if (itemChanges is not null)
            {
                var update = HabitUpdateValidation.ResolveUpdate(habit, itemChanges, today);
                var preview = habit.PreviewUpdate(update);
                if (preview.IsFailure)
                    return null;
                AddChanges(fields, habit, preview.Value, itemChanges);
                actualChangeCount = fields.Count;
                AddProposedFields(fields, habit, preview.Value, itemChanges,
                    operationId, arguments, isRevised, revisedItems);
            }
            else
            {
                fields.Add(BuildActionChange(habit, operationId, arguments, revisedItems,
                    isRevised, today));
                actualChangeCount = fields.Count;
            }

            items.Add(new PendingOperationItem(habit.Id.ToString(), habit.Id, habit.Title,
                fields, FingerprintHabit(habit), RemovesData: operationId == "bulk_delete_habits"));
            if (items.Count <= 10)
                rows.AddRange(fields.Take(actualChangeCount));
        }

        return BuildPreview(operationId, rows, items);
    }

    private async Task<JsonElement> ResolveUnlogDateAsync(Guid userId, string operationId,
        JsonElement arguments, CancellationToken cancellationToken)
    {
        if (operationId == "unlog_habit" && arguments.ValueKind == JsonValueKind.Object
            && !arguments.TryGetProperty("date", out _))
        {
            var today = await userDateService.GetUserTodayAsync(userId, cancellationToken);
            var resolved = arguments.EnumerateObject().ToDictionary(property => property.Name, property => property.Value);
            resolved["date"] = JsonSerializer.SerializeToElement(today.ToString(DateFormat, CultureInfo.InvariantCulture));
            arguments = JsonSerializer.SerializeToElement(resolved);
        }
        return arguments;
    }

    private static bool RequiresEmojiInference(string operationId, JsonElement arguments) =>
        operationId == "bulk_update_habit_emojis"
        && BulkUpdateHabitEmojisTool.ResolveEmojiOperation(arguments).Infer;

    private async Task<Result<PendingOperationChangePreview?>> PreviewInferredEmojisAsync(
        Guid userId, JsonElement arguments, CancellationToken ct)
    {
        if (toolRegistry.GetTool("bulk_update_habit_emojis") is not BulkUpdateHabitEmojisTool emojiTool)
            return Result.Failure<PendingOperationChangePreview?>("preview_unavailable");
        var prepared = await emojiTool.PreparePreviewArgumentsAsync(arguments, userId, ct);
        if (prepared.IsFailure)
            return prepared.PropagateError<PendingOperationChangePreview?>();
        var preview = await PreviewChangesAsync(userId, emojiTool.Name, prepared.Value, ct);
        return preview is null
            ? Result.Failure<PendingOperationChangePreview?>("preview_unavailable")
            : Result.Success<PendingOperationChangePreview?>(preview with { ExecutionArguments = prepared.Value });
    }

    private async Task<PendingOperationChangePreview?> PreviewOtherAsync(Guid userId, string operationId,
        JsonElement arguments, CancellationToken ct)
    {
        if (destructivePreviewer is not null && destructivePreviewer.Handles(operationId))
            return await destructivePreviewer.PreviewAsync(userId, operationId, arguments, ct);
        if (operationId == "assign_tags" && arguments.TryGetProperty("habit_ids", out _))
        {
            var singles = AssignTagsTool.SingleHabitArguments(arguments);
            if (singles is null)
                return null;
            var ids = singles.Select(single => Guid.Parse(single.GetProperty("habit_id").GetString()!)).ToArray();
            var habits = await habitRepository.FindAsync(habit => habit.UserId == userId && ids.Contains(habit.Id),
                AssignTagsTool.IncludeTags, ct);
            if (habits.Count != ids.Length)
                return null;
            var items = habits.OrderBy(habit => habit.Id).Select(habit =>
            {
                var single = singles.Single(value => value.GetProperty("habit_id").GetString() == habit.Id.ToString());
                var preview = AgentArgumentPreview.Build(operationId, single, TargetHabit(habit, true), EditableSchema(operationId))!;
                return preview.Items![0] with
                {
                    Fields = preview.Items[0].Fields.Select(field => field with { IsEditable = false }).ToList()
                };
            }).ToList();
            return await BindDependenciesAsync(userId, operationId, arguments,
                BuildPreview(operationId, items.Take(10).SelectMany(item => item.Fields).ToList(), items), ct,
                bindHabits: false);
        }
        var target = await ResolveTargetAsync(userId, arguments, ct);
        if (target is null && new[] { "habit_id", "goal_id", "tag_id" }.Any(field => arguments.TryGetProperty(field, out _)))
            return null;
        var built = AgentArgumentPreview.Build(operationId, arguments, target, EditableSchema(operationId));
        return await BindDependenciesAsync(userId, operationId, arguments, built, ct);
    }

    private async Task<PendingOperationChangePreview?> BindDependenciesAsync(Guid userId, string operationId,
        JsonElement arguments, PendingOperationChangePreview? preview, CancellationToken ct, bool bindHabits = true)
    {
        if (preview?.Items is null)
            return preview;
        var habitIds = bindHabits ? DependencyIds(arguments, "habit_ids", "parent_habit_id") : [];
        var tagIds = DependencyIds(arguments, "tag_ids");
        if (habitIds.Count == 0 && tagIds.Count == 0)
            return preview;
        var habits = habitIds.Count == 0 ? [] : await habitRepository.FindAsync(
            habit => habit.UserId == userId && habitIds.Contains(habit.Id), ct);
        var tags = tagIds.Count == 0 ? [] : await tagRepository.FindAsync(
            tag => tag.UserId == userId && tagIds.Contains(tag.Id), ct);
        if (habits.Count != habitIds.Count || tags.Count != tagIds.Count)
            return null;
        var states = habits.OrderBy(habit => habit.Id).Select(FingerprintHabit)
            .Concat(tags.OrderBy(tag => tag.Id).Select(FingerprintTag)).ToArray();
        var items = preview.Items.Select(item => item with
        {
            Fields = item.Fields.Select(field => field.Field switch
            {
                "habit_ids" => field with { NewValue = string.Join(", ", habits.OrderBy(habit => habit.Id).Select(habit => habit.Title)) },
                "parent_habit_id" => field with { NewValue = habits.Single().Title },
                "tag_ids" => field with { NewValue = string.Join(", ", tags.OrderBy(tag => tag.Id).Select(tag => tag.Name)) },
                _ => field
            }).ToList(),
            StateFingerprint = AgentOperationFingerprint.Compute(item.StateFingerprint, JsonSerializer.Serialize(states))
        }).ToList();
        return BuildPreview(operationId, items.Take(10).SelectMany(item => item.Fields).ToList(), items);
    }

    private static HashSet<Guid> DependencyIds(JsonElement arguments, params string[] fields)
    {
        var result = new HashSet<Guid>();
        foreach (var field in fields)
        {
            if (!arguments.TryGetProperty(field, out var value))
                continue;
            var values = value.ValueKind == JsonValueKind.Array ? value.EnumerateArray().ToArray() : [value];
            foreach (var candidate in values)
                if (candidate.ValueKind == JsonValueKind.String && Guid.TryParse(candidate.GetString(), out var id))
                    result.Add(id);
        }
        return result;
    }

    private static bool HasLogDates(string operationId, JsonElement arguments) =>
        operationId == "bulk_log_habits" && arguments.TryGetProperty("dates", out _)
        && !arguments.TryGetProperty("revised_items", out _);

    private static bool IsDatedRevision(string operationId, bool revised) =>
        revised && operationId is "bulk_log_habits" or "bulk_skip_habits";

    private static PendingOperationChangePreview? PreviewDatedItems(string operationId,
        JsonElement arguments, IReadOnlyList<Habit> habits)
    {
        var (datedItems, _) = BulkHabitToolArguments.ParseRevisedDatedItems(arguments);
        var byId = habits.ToDictionary(habit => habit.Id);
        if (datedItems is null || datedItems.Any(item => !byId.ContainsKey(item.HabitId)))
            return null;
        var stableIds = arguments.GetProperty("revised_items").EnumerateArray()
            .Where(item => item.TryGetProperty("preview_item_id", out var id) && id.ValueKind == JsonValueKind.String)
            .ToDictionary(item => (Guid.Parse(item.GetProperty("habit_id").GetString()!),
                DateOnly.ParseExact(item.GetProperty("date").GetString()!, DateFormat, CultureInfo.InvariantCulture)),
                item => item.GetProperty("preview_item_id").GetString()!);
        var multipleDates = datedItems.GroupBy(item => item.HabitId).Any(group => group.Count() > 1);
        var datedPreview = datedItems.OrderBy(item => item.HabitId).ThenBy(item => item.Date).Select(item =>
        {
            var habit = byId[item.HabitId];
            var date = item.Date.ToString(DateFormat, CultureInfo.InvariantCulture);
            var change = new PendingOperationChange(habit.Id, habit.Title, "date", null, date,
                "date", JsonSerializer.SerializeToElement(date), true);
            var itemId = stableIds.TryGetValue((item.HabitId, item.Date), out var stableId)
                ? stableId : multipleDates ? $"{habit.Id}:{date}" : habit.Id.ToString();
            return new PendingOperationItem(itemId,
                habit.Id, habit.Title, [change], FingerprintHabit(habit), RemovesData: false);
        }).ToList();
        return BuildPreview(operationId, datedPreview.Take(10).SelectMany(item => item.Fields).ToList(), datedPreview);
    }

    /// <summary>
    /// The parameter schema of a tool that checks its own arguments. A tool with no check offers
    /// no editable field, because the revise route could not hold an edit to the tool's rules.
    /// </summary>
    private JsonElement EditableSchema(string operationId) =>
        toolRegistry.GetTool(operationId) is { } tool and IArgumentCheckTool
            ? JsonSerializer.SerializeToElement(tool.GetParameterSchema())
            : default;

    private async Task<AgentPreviewTarget?> ResolveTargetAsync(
        Guid userId, JsonElement arguments, CancellationToken cancellationToken)
    {
        if (arguments.ValueKind != JsonValueKind.Object)
            return null;

        if (TryReadId(arguments, "habit_id", out var habitId))
        {
            var readsTags = HasTagArguments(arguments);
            Func<IQueryable<Habit>, IQueryable<Habit>>? includes = readsTags ? AssignTagsTool.IncludeTags : null;
            var habits = await habitRepository.FindAsync(
                item => item.Id == habitId && item.UserId == userId, includes, cancellationToken);
            var habit = habits.FirstOrDefault();
            if (habit is null)
                return null;
            return TargetHabit(habit, readsTags);
        }

        if (TryReadId(arguments, "goal_id", out var goalId))
        {
            var goals = await goalRepository.FindAsync(
                item => item.Id == goalId && item.UserId == userId, cancellationToken);
            var goal = goals.FirstOrDefault();
            return goal is null ? null : new AgentPreviewTarget(
                goal.Id, goal.Title, GoalValues(goal), FingerprintGoal(goal));
        }

        if (TryReadId(arguments, "tag_id", out var tagId))
        {
            var tags = await tagRepository.FindAsync(
                item => item.Id == tagId && item.UserId == userId, cancellationToken);
            var tag = tags.FirstOrDefault();
            return tag is null ? null : new AgentPreviewTarget(
                tag.Id, tag.Name, TagValues(tag), FingerprintTag(tag));
        }

        if (templateRepository is not null && TryReadId(arguments, "template_id", out var templateId))
        {
            var templates = await templateRepository.FindAsync(
                item => item.Id == templateId && item.UserId == userId, cancellationToken);
            var template = templates.FirstOrDefault();
            return template is null ? null : new AgentPreviewTarget(template.Id, template.Name,
                new Dictionary<string, string?>
                {
                    ["name"] = template.Name,
                    ["items"] = JsonSerializer.Serialize(template.Items)
                }, AgentOperationFingerprint.Compute(template.Id.ToString(),
                    JsonSerializer.Serialize(new { template.Name, template.Items, template.UpdatedAtUtc })));
        }

        return null;
    }

    private static AgentPreviewTarget TargetHabit(Habit habit, bool readsTags)
    {
        var values = HabitValues(habit);
        if (readsTags)
            values["tag_names"] = values["tag_ids"] = FormatTags(habit);
        var fingerprint = readsTags ? AgentOperationFingerprint.Compute(FingerprintHabit(habit),
            JsonSerializer.Serialize(habit.Tags.OrderBy(tag => tag.Id).Select(FingerprintTag))) : FingerprintHabit(habit);
        return new AgentPreviewTarget(habit.Id, habit.Title, values, fingerprint);
    }

    private static bool TryReadId(JsonElement arguments, string field, out Guid id)
    {
        id = Guid.Empty;
        return arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty(field, out var value)
            && value.ValueKind == JsonValueKind.String
            && Guid.TryParse(value.GetString(), out id);
    }

    private static Dictionary<string, string?> HabitValues(Habit habit) => new(StringComparer.Ordinal)
    {
        ["title"] = habit.Title,
        ["description"] = habit.Description,
        ["emoji"] = habit.Emoji,
        ["frequency_unit"] = AgentArgumentPreview.Format(habit.FrequencyUnit),
        ["frequency_quantity"] = AgentArgumentPreview.Format(habit.FrequencyQuantity),
        ["interval_weeks"] = AgentArgumentPreview.Format(habit.IntervalWeeks),
        ["days"] = string.Join(", ", habit.Days),
        ["due_date"] = AgentArgumentPreview.Format(habit.NextDueDate),
        ["end_date"] = AgentArgumentPreview.Format(habit.EndDate),
        ["due_time"] = AgentArgumentPreview.Format(habit.DueTime),
        [DueEndTimeField] = AgentArgumentPreview.Format(habit.DueEndTime),
        [IsGeneralField] = AgentArgumentPreview.Format(habit.Schedule == HabitSchedule.General),
        [SlipAlertEnabledField] = AgentArgumentPreview.Format(habit.SlipAlertEnabled),
        [RelativeRemindersField] = string.Join(", ", habit.RelativeReminders.Select(HabitFieldSummary.FormatRelative)),
        [ScheduledRemindersField] = string.Join(", ", habit.GetScheduledRemindersForLegacyClients().Select(FormatScheduledReminder)),
        [IsBadHabitField] = AgentArgumentPreview.Format(habit.IsBadHabit),
        [IsFlexibleField] = AgentArgumentPreview.Format(habit.Schedule == HabitSchedule.Flexible),
        [ReminderEnabledField] = AgentArgumentPreview.Format(habit.ReminderEnabled),
        [ReminderTimesField] = string.Join(", ", habit.ReminderTimes),
        [ChecklistItemsField] = string.Join(", ", habit.ChecklistItems.Select(FormatChecklistItem))
    };

    private static bool HasTagArguments(JsonElement arguments) =>
        arguments.TryGetProperty("tag_names", out _) || arguments.TryGetProperty("tag_ids", out _);

    private static string? FormatTags(Habit habit) => habit.Tags.Count == 0
        ? null
        : string.Join(", ", habit.Tags.Select(tag => tag.Name).Order(StringComparer.Ordinal));

    private static Dictionary<string, string?> GoalValues(Goal goal) => new(StringComparer.Ordinal)
    {
        ["title"] = goal.Title,
        ["description"] = goal.Description,
        ["target_value"] = AgentArgumentPreview.Format(goal.TargetValue),
        ["current_value"] = AgentArgumentPreview.Format(goal.CurrentValue),
        ["unit"] = goal.Unit,
        ["deadline"] = AgentArgumentPreview.Format(goal.Deadline),
        ["status"] = AgentArgumentPreview.Format(goal.Status)
    };

    private static Dictionary<string, string?> TagValues(Tag tag) => new(StringComparer.Ordinal)
    {
        ["name"] = tag.Name,
        ["color"] = tag.Color
    };

    private static string FingerprintGoal(Goal goal) => AgentOperationFingerprint.Compute(
        goal.Id.ToString(), JsonSerializer.Serialize(new
        {
            goal.UpdatedAtUtc,
            goal.Title,
            goal.Description,
            goal.TargetValue,
            goal.CurrentValue,
            goal.Unit,
            goal.Status,
            goal.Deadline,
            goal.IsDeleted
        }));

    private static string FingerprintTag(Tag tag) => AgentOperationFingerprint.Compute(
        tag.Id.ToString(), JsonSerializer.Serialize(new
        {
            tag.UpdatedAtUtc,
            tag.Name,
            tag.Color,
            tag.IsDeleted
        }));

    private static PendingOperationChange BuildActionChange(Habit habit, string operationId,
        JsonElement arguments, JsonElement revisedItems, bool isRevised, DateOnly today)
    {
        var field = operationId switch
        {
            "bulk_delete_habits" => "delete",
            "bulk_log_habits" or "bulk_skip_habits" => "date",
            _ => "emoji"
        };
        var revisedItem = isRevised
            ? revisedItems.EnumerateArray().First(item =>
                item.GetProperty("habit_id").GetString() == habit.Id.ToString())
            : default;
        var value = isRevised && revisedItem.TryGetProperty(field, out var revisedValue)
            ? revisedValue.ToString()
            : arguments.TryGetProperty(field == "emoji" ? "emoji" : "date", out var proposed)
                ? proposed.ToString()
                : field == "date" ? today.ToString(DateFormat, CultureInfo.InvariantCulture) : null;
        return new PendingOperationChange(habit.Id, habit.Title, field,
            field == "emoji" ? habit.Emoji : null, value,
            field == "emoji" ? "emoji" : field == "date" ? "date" : "action",
            field == "delete" ? null : JsonSerializer.SerializeToElement(value),
            field != "delete" && !(operationId == "bulk_skip_habits" && habit.Schedule is HabitSchedule.OneTime or HabitSchedule.General));
    }

    private static void AddProposedFields(List<PendingOperationChange> fields, Habit habit,
        Habit effective, BulkHabitChanges changes, string operationId, JsonElement arguments,
        bool isRevised, JsonElement revisedItems)
    {
        var proposed = isRevised
            ? revisedItems.EnumerateArray().First(item =>
                item.GetProperty("habit_id").GetString() == habit.Id.ToString())
                .GetProperty("updates")
            : operationId == "bulk_update_habits"
                ? arguments.GetProperty("updates") : default;
        if (proposed.ValueKind != JsonValueKind.Object)
            return;
        foreach (var property in proposed.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal))
        {
            if (fields.Any(field => field.Field == property.Name))
                continue;
            var value = AgentArgumentPreview.FormatValue(property.Value);
            fields.Add(new PendingOperationChange(habit.Id, habit.Title,
                property.Name, value, value, "text", property.Value.Clone(),
                IsEditableUpdateField(property.Name, habit, effective, changes)));
        }
    }

    private static BulkHabitChanges? ResolveChanges(string operationId, JsonElement arguments)
    {
        if (operationId == "bulk_reschedule_habits")
        {
            if (!arguments.TryGetProperty("due_date", out var dateElement)
                || dateElement.ValueKind != JsonValueKind.String
                || !DateOnly.TryParseExact(dateElement.GetString(), DateFormat, CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var date))
                return null;
            return new BulkHabitChanges(HasDueDate: true, DueDate: date);
        }
        if (operationId == "bulk_update_habits")
            return BulkHabitToolArguments.ParseChanges(arguments).Changes;
        return null;
    }

    private static (BulkHabitFilter? Filter, string? Error) ResolveFilter(
        string operationId, JsonElement arguments, bool isRevised, JsonElement revisedItems,
        Dictionary<Guid, BulkHabitChanges> revisedChanges)
    {
        if (isRevised)
        {
            if (operationId is "bulk_log_habits" or "bulk_skip_habits")
            {
                var (items, error) = BulkHabitToolArguments.ParseRevisedDatedItems(arguments);
                return error is null
                    ? (new BulkHabitFilter(false, items!.Select(item => item.HabitId).ToList(),
                        IncludeCompleted: true), null) : (null, error);
            }
            if (operationId == "bulk_update_habit_emojis")
                return ParseRevisedEmojiFilter(revisedItems);
            return ParseRevisedFilter(revisedItems, revisedChanges);
        }
        return operationId switch
        {
            "bulk_update_habits" or "bulk_reschedule_habits" => BulkHabitToolArguments.ParseRequiredFilter(arguments),
            "bulk_update_habit_emojis" => BulkHabitToolArguments.ParseEmojiFilter(arguments),
            _ => BulkHabitToolArguments.ParseActionFilter(arguments)
        };
    }

    private static (BulkHabitFilter? Filter, string? Error) ParseRevisedEmojiFilter(JsonElement items)
    {
        var ids = new List<Guid>();
        foreach (var item in items.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object
                || !item.TryGetProperty("habit_id", out var idValue)
                || idValue.ValueKind != JsonValueKind.String
                || !Guid.TryParse(idValue.GetString(), out var id)
                || ids.Contains(id))
                return (null, "invalid_revised_items");
            ids.Add(id);
        }
        return (new BulkHabitFilter(false, ids, IncludeCompleted: true), null);
    }

    private static (BulkHabitFilter? Filter, string? Error) ParseRevisedFilter(
        JsonElement items, Dictionary<Guid, BulkHabitChanges> changes)
    {
        var ids = new List<Guid>();
        foreach (var item in items.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object
                || !item.TryGetProperty("habit_id", out var idElement)
                || !Guid.TryParse(idElement.GetString(), out var id)
                || !item.TryGetProperty("updates", out var updates))
                return (null, "invalid_revised_items");
            var parsed = BulkHabitToolArguments.ParseChanges(
                JsonDocument.Parse($"{{\"updates\":{updates.GetRawText()}}}").RootElement);
            if (parsed.Error is not null || parsed.Changes is null || !changes.TryAdd(id, parsed.Changes))
                return (null, "invalid_revised_items");
            ids.Add(id);
        }
        return (new BulkHabitFilter(false, ids, IncludeCompleted: true), null);
    }

    private async Task<PendingOperationChangePreview?> PreviewSingleHabitAsync(
        Guid userId, JsonElement arguments, CancellationToken cancellationToken)
    {
        if (!arguments.TryGetProperty("habit_id", out var value)
            || value.ValueKind != JsonValueKind.String
            || !Guid.TryParse(value.GetString(), out var id))
            return null;
        var filter = new BulkHabitFilter(false, [id], IncludeCompleted: true);
        var habits = await BulkHabitSelection.LoadAsync(habitRepository, userId, filter, cancellationToken);
        return BuildPreview("delete_habit", [], habits.Select(habit =>
            new PendingOperationItem(habit.Id.ToString(), habit.Id, habit.Title,
                [new PendingOperationChange(habit.Id, habit.Title, "delete", null, null, "action")],
                FingerprintHabit(habit), RemovesData: true)).ToList());
    }

    private async Task<PendingOperationChangePreview?> PreviewCreateAsync(
        Guid userId, JsonElement arguments, CancellationToken cancellationToken)
    {
        if (!arguments.TryGetProperty("habits", out var habits) || habits.ValueKind != JsonValueKind.Array)
            return null;
        var command = new BulkCreateHabitsCommand(userId, habits.EnumerateArray()
            .Select(habit => habit.ValueKind == JsonValueKind.Object
                ? BulkCreateHabitsTool.ParseBulkHabitItem(habit, allowEmptyTitle: true)
                : null)
            .Select(item => item ?? new BulkHabitItem(string.Empty, null, null, null)).ToList());
        var validation = await new BulkCreateHabitsCommandValidator().ValidateAsync(command, cancellationToken);
        var parents = await habitRepository.FindAsync(
            habit => habit.UserId == userId && !habit.IsDeleted, cancellationToken);
        var today = await userDateService.GetUserTodayAsync(userId, cancellationToken);
        var items = habits.EnumerateArray().Select((habit, index) =>
        {
            var prefix = $"Habits[{index}].";
            var errors = validation.Errors
                .Where(error => error.PropertyName.StartsWith(prefix, StringComparison.Ordinal)
                    || !error.PropertyName.StartsWith("Habits[", StringComparison.Ordinal))
                .Select(error => new PendingOperationValidationError(
                    JsonNamingPolicy.SnakeCaseLower.ConvertName(error.PropertyName.StartsWith(prefix, StringComparison.Ordinal)
                        ? error.PropertyName[prefix.Length..] : error.PropertyName),
                    error.ErrorCode, error.ErrorMessage)).ToList();
            var intrinsic = BulkHabitSubtree.Build(userId, command.Habits[index], today, index,
                int.MaxValue, cancellationToken, out var invalidField);
            var intrinsicField = JsonNamingPolicy.SnakeCaseLower.ConvertName(invalidField ?? "title");
            if (intrinsic.IsFailure && errors.All(error => error.Field != intrinsicField))
                errors.Add(new PendingOperationValidationError(
                    intrinsicField,
                    intrinsic.ErrorCode ?? "item_rejected", intrinsic.Error));
            var name = habit.ValueKind == JsonValueKind.Object
                && habit.TryGetProperty("title", out var title) ? title.ToString() : string.Empty;
            var fields = habit.ValueKind == JsonValueKind.Object
                ? habit.EnumerateObject().Where(property => CreateFields.Contains(property.Name))
                    .OrderBy(property => property.Name != "title")
                    .ThenBy(property => property.Name, StringComparer.Ordinal)
                    .Select(property => new PendingOperationChange(
                    Guid.Empty, name, property.Name, null, AgentArgumentPreview.FormatValue(property.Value), AgentArgumentPreview.ResolveValueType(property.Name, property.Value),
                    property.Value.Clone(), true)).ToList()
                : [];
            var (parentFingerprint, parentOptions) = AddCreationParent(arguments, habit, parents, name, fields, errors);
            foreach (var error in errors.Where(error => CreateFields.Contains(error.Field)))
                if (fields.All(field => field.Field != error.Field))
                    fields.Add(new PendingOperationChange(Guid.Empty, name, error.Field,
                        null, null, "text", JsonSerializer.SerializeToElement<string?>(null), true));
            var itemId = habit.ValueKind == JsonValueKind.Object
                && habit.TryGetProperty("preview_item_id", out var storedId)
                ? storedId.ToString() : index.ToString(CultureInfo.InvariantCulture);
            return new PendingOperationItem(itemId, null,
                name, fields, AgentOperationFingerprint.Compute("bulk_create_habits" + parentFingerprint, habit.GetRawText()),
                RemovesData: false, ValidationErrors: errors.Count > 0 ? errors : null, FieldOptions: [parentOptions]);
        }).ToList();
        return BuildPreview("bulk_create_habits", items.SelectMany(item => item.Fields)
            .Where(field => field.Field == "parent_habit_id" && field.EntityId != Guid.Empty)
            .DistinctBy(field => field.EntityId).ToList(), items);
    }

    private static (string Fingerprint, PendingOperationFieldOptions Options) AddCreationParent(JsonElement arguments, JsonElement habit, IReadOnlyList<Habit> parents,
        string name, List<PendingOperationChange> fields, List<PendingOperationValidationError> errors)
    {
        JsonElement parentValue = default;
        var hasParent = habit.ValueKind == JsonValueKind.Object && habit.TryGetProperty("parent_habit_id", out parentValue)
            || arguments.TryGetProperty("parent_habit_id", out parentValue);
        var parent = hasParent && parentValue.ValueKind == JsonValueKind.String && Guid.TryParse(parentValue.GetString(), out var id)
            ? parents.FirstOrDefault(candidate => candidate.Id == id) : null;
        var parentFingerprint = parent is null ? string.Empty : FingerprintHabit(parent);
        var unresolved = hasParent && parentValue.ValueKind == JsonValueKind.String && parent is null;
        var candidates = unresolved ? HabitHeadingParents.Candidates(parentValue.GetString()!, parents) : parents;
        var requiresChoice = unresolved && candidates.Count > 1;
        fields.Add(new PendingOperationChange(parent?.Id ?? Guid.Empty, name, "parent_habit_id", null,
            parent?.Title ?? (requiresChoice ? parentValue.GetString() : "No parent"), "text",
            parent is not null || requiresChoice ? parentValue.Clone() : JsonSerializer.SerializeToElement<string?>(null), true));
        if (requiresChoice)
            errors.Add(new PendingOperationValidationError("parent_habit_id", "parent_choice_required",
                "Choose a parent habit or No parent."));
        else if (unresolved)
            errors.Add(new PendingOperationValidationError("parent_habit_id", "parent_not_found", "Parent habit not found."));
        var options = candidates.OrderBy(candidate => candidate.Id)
            .Select(candidate => new PendingOperationFieldOption(JsonSerializer.SerializeToElement(candidate.Id), candidate.Title))
            .Append(new PendingOperationFieldOption(JsonSerializer.SerializeToElement<string?>(null), "No parent")).ToArray();
        return (parentFingerprint, new PendingOperationFieldOptions("parent_habit_id", requiresChoice, options));
    }

    private static PendingOperationChangePreview? BuildPreview(
        string operationId, IReadOnlyList<PendingOperationChange> changes, IReadOnlyList<PendingOperationItem> items)
    {
        if (items.Count == 0)
            return null;
        var fingerprint = AgentOperationFingerprint.Compute(operationId, JsonSerializer.Serialize(items));
        return new PendingOperationChangePreview(changes, items.Count, items, fingerprint);
    }

    private static string FingerprintHabit(Habit habit) => AgentOperationFingerprint.Compute(
        habit.Id.ToString(), JsonSerializer.Serialize(new
        {
            habit.UpdatedAtUtc,
            habit.Title,
            habit.Description,
            habit.Emoji,
            habit.IsCompleted,
            DueDate = habit.NextDueDate,
            habit.DueTime,
            habit.DueEndTime,
            IsGeneral = habit.Schedule == HabitSchedule.General,
            habit.SlipAlertEnabled,
            habit.EndDate,
            habit.FrequencyUnit,
            habit.FrequencyQuantity,
            habit.IntervalWeeks,
            habit.IsBadHabit,
            IsFlexible = habit.Schedule == HabitSchedule.Flexible,
            habit.ReminderEnabled,
            habit.ReminderTimes,
            habit.Days,
            habit.ChecklistItems,
            habit.ScheduledReminders,
            habit.RelativeReminders,
            habit.IsDeleted
        }));

    private static void AddChanges(List<PendingOperationChange> rows, Habit habit, Habit effective,
        BulkHabitChanges changes)
    {
        void Add(string field, object? oldValue, object? newValue, string valueType,
            bool? changed = null, JsonElement? typedValue = null)
        {
            if (changed ?? !Equals(oldValue, newValue))
                rows.Add(new PendingOperationChange(
                    habit.Id, habit.Title, field, Format(oldValue), Format(newValue), valueType,
                    typedValue ?? JsonSerializer.SerializeToElement(newValue switch
                    {
                        DateOnly or TimeOnly or FrequencyUnit => Format(newValue),
                        _ => newValue
                    }), IsEditableUpdateField(field, habit, effective, changes)));
        }

        Add("title", habit.Title, effective.Title, "text");
        Add("description", habit.Description, effective.Description, "text");
        Add("emoji", habit.Emoji, effective.Emoji, "emoji");
        Add("frequency_unit", habit.FrequencyUnit, effective.FrequencyUnit, "text");
        Add("frequency_quantity", habit.FrequencyQuantity, effective.FrequencyQuantity, "number");
        Add("interval_weeks", habit.IntervalWeeks, effective.IntervalWeeks, "number");
        Add("days", string.Join(", ", habit.Days), string.Join(", ", effective.Days), "text",
            typedValue: JsonSerializer.SerializeToElement(effective.Days.Select(day => day.ToString())));
        Add("due_date", habit.NextDueDate, effective.NextDueDate, "date");
        Add("end_date", habit.EndDate, effective.EndDate, "date");
        Add("due_time", habit.DueTime, effective.DueTime, "time");
        Add(DueEndTimeField, habit.DueEndTime, effective.DueEndTime, "time");
        Add(IsGeneralField, habit.Schedule == HabitSchedule.General, effective.Schedule == HabitSchedule.General, JsonSchemaTypes.Boolean);
        Add(SlipAlertEnabledField, habit.SlipAlertEnabled, effective.SlipAlertEnabled, JsonSchemaTypes.Boolean);
        AddList(RelativeRemindersField, habit.RelativeReminders, effective.RelativeReminders,
            HabitFieldSummary.FormatRelative,
            JsonSerializer.SerializeToElement(effective.RelativeReminders.Select(RelativeReminderValue)));
        Add(IsBadHabitField, habit.IsBadHabit, effective.IsBadHabit, JsonSchemaTypes.Boolean);
        Add(IsFlexibleField, habit.Schedule == HabitSchedule.Flexible, effective.Schedule == HabitSchedule.Flexible, JsonSchemaTypes.Boolean);
        Add("is_completed", habit.IsCompleted, effective.IsCompleted, JsonSchemaTypes.Boolean);
        Add(ReminderEnabledField, habit.ReminderEnabled, effective.ReminderEnabled, JsonSchemaTypes.Boolean);
        AddList(ReminderTimesField, habit.ReminderTimes, effective.ReminderTimes,
            FormatReminderTime, JsonSerializer.SerializeToElement(effective.ReminderTimes));
        AddList(ChecklistItemsField, habit.ChecklistItems, effective.ChecklistItems,
            FormatChecklistItem, JsonSerializer.SerializeToElement(effective.ChecklistItems.Select(item =>
                new { text = item.Text, is_checked = item.IsChecked })));
        var oldScheduledReminders = habit.GetScheduledRemindersForLegacyClients();
        var newScheduledReminders = effective.GetScheduledRemindersForLegacyClients();
        AddList(ScheduledRemindersField, oldScheduledReminders, newScheduledReminders,
            FormatScheduledReminder, JsonSerializer.SerializeToElement(newScheduledReminders.Select(item =>
                new
                {
                    when = item.When == ScheduledReminderWhen.DayBefore ? "day_before" : "same_day",
                    time = item.Time.ToString(TimeFormat, CultureInfo.InvariantCulture)
                })));

        void AddList<T>(string field, IReadOnlyList<T> oldValues, IReadOnlyList<T> newValues,
            Func<T, string> format, JsonElement typedValue)
        {
            if (oldValues.SequenceEqual(newValues))
                return;

            var (oldText, newText) = FormatChangedList(oldValues, newValues, format);
            Add(field, oldText, newText, "text", changed: true, typedValue: typedValue);
        }
    }

    private static object RelativeReminderValue(RelativeReminderTime item)
    {
        if (item.MinutesBefore is { } minutes)
            return new { minutes_before = minutes };
        return new
        {
            when = item.When == ScheduledReminderWhen.DayBefore ? "day_before" : "same_day",
            time = item.Time?.ToString(TimeFormat, CultureInfo.InvariantCulture)
        };
    }

    private static bool IsEditableUpdateField(string field, Habit habit, Habit effective,
        BulkHabitChanges changes) => field switch
        {
            "title" or "description" or "emoji" or "frequency_unit" or "frequency_quantity"
                or "interval_weeks" or "due_date" or "due_time" or IsBadHabitField
                or IsFlexibleField or ReminderEnabledField or ChecklistItemsField
                or DueEndTimeField or IsGeneralField or SlipAlertEnabledField or RelativeRemindersField => true,
            "days" => effective.Schedule != HabitSchedule.Flexible,
            "end_date" => habit.Schedule != HabitSchedule.General,
            ReminderTimesField => effective.DueTime is null
                || !changes.HasScheduledReminders || changes.ScheduledReminders is not { Count: > 0 },
            ScheduledRemindersField => effective.DueTime is null,
            _ => false
        };

    private static (string OldText, string NewText) FormatChangedList<T>(
        IReadOnlyList<T> oldValues, IReadOnlyList<T> newValues, Func<T, string> format)
    {
        var firstDifference = 0;
        while (firstDifference < Math.Min(oldValues.Count, newValues.Count)
            && EqualityComparer<T>.Default.Equals(oldValues[firstDifference], newValues[firstDifference]))
            firstDifference++;

        var oldText = FormatListWindow(oldValues, firstDifference, format);
        var newText = FormatListWindow(newValues, firstDifference, format);
        if (oldText == newText)
            newText += " (changed)";
        return (oldText, newText);
    }

    private static string FormatListWindow<T>(IReadOnlyList<T> values, int start, Func<T, string> format)
    {
        var entries = new List<string>();
        if (start > 0)
            entries.Add($"+{start} earlier");

        var displayed = values.Skip(start).Take(MaxDisplayedEntries)
            .Select(value => TruncateEntry(format(value)))
            .ToList();
        if (displayed.Count == 0)
            entries.Add("(none)");
        else
            entries.AddRange(displayed);

        var remaining = values.Count - start - displayed.Count;
        if (remaining > 0)
            entries.Add($"+{remaining} more");
        return string.Join(", ", entries);
    }

    private static string TruncateEntry(string value)
    {
        var singleLine = value.Replace('\r', ' ').Replace('\n', ' ');
        return singleLine.Length <= MaxEntryLength
            ? singleLine
            : string.Concat(singleLine.AsSpan(0, MaxEntryLength - 3), "...");
    }

    private static string FormatReminderTime(int offset) => $"{offset} min before due";

    private static string FormatScheduledReminder(ScheduledReminderTime reminder) =>
        $"{(reminder.When == ScheduledReminderWhen.DayBefore ? "day_before" : "same_day")} {reminder.Time.ToString(TimeFormat, CultureInfo.InvariantCulture)}";

    private static string FormatChecklistItem(ChecklistItem item) =>
        item.IsChecked ? $"{item.Text} (checked)" : item.Text;

    private static string? Format(object? value) => value switch
    {
        null => null,
        DateOnly date => date.ToString(DateFormat, CultureInfo.InvariantCulture),
        TimeOnly time => time.ToString(TimeFormat, CultureInfo.InvariantCulture),
        bool boolean => boolean ? "true" : "false",
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString()
    };
}
