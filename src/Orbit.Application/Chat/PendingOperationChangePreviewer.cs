using static Orbit.Application.Chat.Tools.ToolArgumentNames;
using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
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

public sealed partial class PendingOperationChangePreviewer(
    IGenericRepository<Habit> habitRepository,
    IGenericRepository<Goal> goalRepository,
    IGenericRepository<Tag> tagRepository,
    IUserDateService userDateService,
    AiToolRegistry toolRegistry,
    IDestructiveOperationPreviewer? destructivePreviewer = null,
    IGenericRepository<ChecklistTemplate>? templateRepository = null) : IPendingOperationChangePreviewer
{
    private const string IsGeneralField = "is_general";
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
        IntervalWeeks, "days", DueDate, StartDate, EndDate, DueTime, GoalIds, TagNames, TagIds, HabitId,
        IsBadHabit, IsGeneralField, IsFlexible, ChecklistItemsField, SubHabits,
        DueEndTimeField, SlipAlertEnabledField, ReminderEnabledField, ReminderTimesField,
        ScheduledRemindersField, RelativeRemindersField
    };

    internal static bool CanReviseHabitField(string operationId, string field) =>
        operationId is "create_habit" or "create_sub_habit" or BulkCreateHabits or "update_habit" or "bulk_update_habits"
        && (field is ParentHabitId or "is_completed" || field != HabitId && CreateFields.Contains(field));

    public async Task<Result<PendingOperationChangePreview?>> PreviewAsync(
        Guid userId,
        string operationId,
        JsonElement arguments,
        CancellationToken cancellationToken = default,
        AgentClientContext? clientContext = null)
    {
        if (operationId == AccountChangeSet.OperationName)
            return Result.Success(await AccountChangeSet.PreviewAsync(userId, arguments, this, cancellationToken, clientContext));
        if (!arguments.TryGetProperty("revised_items", out _) && toolRegistry.GetTool(operationId) is { } tool)
            arguments = OptionalToolArguments.OmitOptionalNulls(arguments, tool.GetParameterSchema());
        if (await HasMissingSearchAsync(userId, operationId, arguments, cancellationToken))
            return Result.Success<PendingOperationChangePreview?>(null);
        if (RequiresEmojiInference(operationId, arguments))
            return await PreviewInferredEmojisAsync(userId, arguments, cancellationToken);
        if (operationId is "create_habit" or "create_sub_habit" or BulkCreateHabits)
            return Result.Success(await PreviewPlacementAsync(userId, operationId, arguments, cancellationToken, clientContext));
        return Result.Success(LocalizeActions(await PreviewChangesAsync(userId, operationId, arguments, cancellationToken), clientContext));
    }

    private static PendingOperationChangePreview? LocalizeActions(PendingOperationChangePreview? preview, AgentClientContext? client)
    {
        if (preview is null) return null;
        var skip = Orbit.Application.Common.LocaleHelper.IsPortuguese(client?.Locale) ? "Pular" : "Skip";
        PendingOperationChange Localize(PendingOperationChange field) =>
            field is { Field: "action", NewValue: "skip" } ? field with { NewValue = skip } : field;
        return preview with
        {
            Changes = preview.Changes.Select(Localize).ToArray(),
            Items = preview.Items?.Select(item => item with { Fields = item.Fields.Select(Localize).ToArray() }).ToArray()
        };
    }

    private async Task<bool> HasMissingSearchAsync(Guid userId, string operationId, JsonElement arguments,
        CancellationToken ct)
    {
        if (!BulkNoChangesOutcome.Supports(operationId) || arguments.TryGetProperty("revised_items", out _))
            return false;
        var (filter, error) = BulkHabitToolArguments.ParseRequiredFilter(arguments);
        if (error is not null || string.IsNullOrWhiteSpace(filter?.Search))
            return false;
        var matches = await BulkHabitSelection.LoadAsync(habitRepository, userId,
            new BulkHabitFilter(true, [], IncludeCompleted: true, Search: filter.Search), ct);
        return matches.Count == 0;
    }

    private async Task<PendingOperationChangePreview?> PreviewChangesAsync(
        Guid userId, string operationId, JsonElement arguments, CancellationToken cancellationToken)
    {
        arguments = await ResolveOccurrenceDateAsync(userId, operationId, arguments, cancellationToken);
        if (HasLogDates(operationId, arguments))
            return await PreviewLogDatesAsync(userId, operationId, arguments, cancellationToken);
        if (operationId == BulkCreateHabits)
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
        if (HasMissingRevisedHabits(operationId, isRevised, revisedChanges.Count, habits.Count))
            return null;
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
                if (update.IsFailure)
                    return null;
                var preview = habit.PreviewUpdate(update.Value);
                if (preview.IsFailure)
                    return null;
                AddChanges(fields, habit, preview.Value, itemChanges);
                actualChangeCount = fields.Count;
                AddProposedFields(fields, habit, preview.Value, itemChanges,
                    operationId, arguments, isRevised, revisedItems);
            }
            else
            {
                fields.AddRange(BuildActionChanges(habit, operationId, arguments, revisedItems,
                    isRevised, today));
                actualChangeCount = fields.Count;
            }

            if (operationId == "bulk_update_habit_emojis"
                && fields.All(field => string.Equals(field.OldValue, field.NewValue, StringComparison.Ordinal)))
                continue;

            fields = fields.Select(field => field with { EntityEmoji = habit.Emoji }).ToList();
            items.Add(new PendingOperationItem(habit.Id.ToString(), habit.Id, habit.Title,
                fields, FingerprintHabit(habit), RemovesData: operationId == "bulk_delete_habits",
                EntityEmoji: habit.Emoji));
            if (items.Count <= 10)
                rows.AddRange(fields.Take(actualChangeCount));
        }

        var result = BuildPreview(operationId, rows, items);
        return result is not null && operationId == "bulk_update_habit_emojis"
            ? result with
            {
                ExecutionArguments = JsonSerializer.SerializeToElement(new
                {
                    infer_from_title = false,
                    revised_items = items.Select(item => new { habit_id = item.EntityId, emoji = item.Fields.Single().NewValue }).ToArray()
                })
            }
            : result;
    }

    private static bool HasMissingRevisedHabits(string operationId, bool isRevised, int expectedCount, int actualCount) =>
        isRevised && operationId is ("bulk_update_habits" or "bulk_reschedule_habits") && expectedCount != actualCount;

    private async Task<PendingOperationChangePreview?> PreviewLogDatesAsync(Guid userId, string operationId,
        JsonElement arguments, CancellationToken ct)
    {
        var expanded = await BulkHabitLogArguments.ExpandAsync(arguments, habitRepository,
            userDateService, userId, ct);
        if (expanded.IsFailure)
            return null;
        return expanded.Value is { } expandedArguments
            ? await PreviewChangesAsync(userId, operationId, expandedArguments, ct)
            : BuildPreview(operationId, [], []);
    }

    private async Task<JsonElement> ResolveOccurrenceDateAsync(Guid userId, string operationId,
        JsonElement arguments, CancellationToken cancellationToken)
    {
        if (operationId is "unlog_habit" or "skip_habit" && arguments.ValueKind == JsonValueKind.Object
            && (!arguments.TryGetProperty("date", out var date) || date.ValueKind == JsonValueKind.Null))
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
        if (prepared.Value is not { } preparedArguments)
            return Result.Success(BuildPreview(emojiTool.Name, [], []));
        var preview = await PreviewChangesAsync(userId, emojiTool.Name, preparedArguments, ct);
        return preview is null
            ? Result.Failure<PendingOperationChangePreview?>("preview_unavailable")
            : Result.Success<PendingOperationChangePreview?>(preview);
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
            var ids = singles.Select(single => Guid.Parse(single.GetProperty(HabitId).GetString()!)).ToArray();
            var habits = await habitRepository.FindAsync(habit => habit.UserId == userId && ids.Contains(habit.Id),
                AssignTagsTool.IncludeTags, ct);
            if (habits.Count != ids.Length)
                return null;
            var items = habits.OrderBy(habit => habit.Id).Select(habit =>
            {
                var single = singles.Single(value => value.GetProperty(HabitId).GetString() == habit.Id.ToString());
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
        if (target is null && new[] { HabitId, "goal_id", "tag_id" }.Any(field => arguments.TryGetProperty(field, out _)))
            return null;
        var built = AgentArgumentPreview.Build(operationId, arguments, target, EditableSchema(operationId));
        return await BindDependenciesAsync(userId, operationId, arguments, built, ct);
    }

    private async Task<PendingOperationChangePreview?> BindDependenciesAsync(Guid userId, string operationId,
        JsonElement arguments, PendingOperationChangePreview? preview, CancellationToken ct, bool bindHabits = true)
    {
        if (preview?.Items is null)
            return preview;
        var habitIds = bindHabits ? DependencyIds(arguments, "habit_ids", ParentHabitId) : [];
        var tagIds = DependencyIds(arguments, TagIds);
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
                ParentHabitId => field with { NewValue = habits.Single().Title },
                TagIds => field with { NewValue = string.Join(", ", tags.OrderBy(tag => tag.Id).Select(tag => tag.Name)) },
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
            .ToDictionary(item => (Guid.Parse(item.GetProperty(HabitId).GetString()!),
                DateOnly.ParseExact(item.GetProperty("date").GetString()!, DateFormat, CultureInfo.InvariantCulture)),
                item => item.GetProperty("preview_item_id").GetString()!);
        var multipleDates = datedItems.GroupBy(item => item.HabitId).Any(group => group.Count() > 1);
        var datedPreview = datedItems.OrderBy(item => item.HabitId).ThenBy(item => item.Date).Select(item =>
        {
            var habit = byId[item.HabitId];
            var date = item.Date.ToString(DateFormat, CultureInfo.InvariantCulture);
            var change = new PendingOperationChange(habit.Id, habit.Title, "date", null, date,
                "date", JsonSerializer.SerializeToElement(date), true, EntityEmoji: habit.Emoji);
            var itemId = stableIds.TryGetValue((item.HabitId, item.Date), out var stableId)
                ? stableId : multipleDates ? $"{habit.Id}:{date}" : habit.Id.ToString();
            return new PendingOperationItem(itemId,
                habit.Id, habit.Title, operationId == "bulk_skip_habits"
                    ? [change, AgentArgumentPreview.SkipAction(habit.Id, habit.Title, habit.Emoji)] : [change],
                FingerprintHabit(habit), RemovesData: false, EntityEmoji: habit.Emoji);
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

        if (TryReadId(arguments, HabitId, out var habitId))
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
            values[TagNames] = values[TagIds] = FormatTags(habit);
        var fingerprint = readsTags ? AgentOperationFingerprint.Compute(FingerprintHabit(habit),
            JsonSerializer.Serialize(habit.Tags.OrderBy(tag => tag.Id).Select(FingerprintTag))) : FingerprintHabit(habit);
        return new AgentPreviewTarget(habit.Id, habit.Title, values, fingerprint, habit.Emoji);
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
        [IntervalWeeks] = AgentArgumentPreview.Format(habit.IntervalWeeks),
        ["days"] = string.Join(", ", habit.Days),
        [DueDate] = AgentArgumentPreview.Format(habit.NextDueDate),
        [EndDate] = AgentArgumentPreview.Format(habit.EndDate),
        [DueTime] = AgentArgumentPreview.Format(habit.DueTime),
        [DueEndTimeField] = AgentArgumentPreview.Format(habit.DueEndTime),
        [IsGeneralField] = AgentArgumentPreview.Format(habit.Schedule == HabitSchedule.General),
        [SlipAlertEnabledField] = AgentArgumentPreview.Format(habit.SlipAlertEnabled),
        [RelativeRemindersField] = string.Join(", ", habit.RelativeReminders.Select(HabitFieldSummary.FormatRelative)),
        [ScheduledRemindersField] = string.Join(", ", habit.GetScheduledRemindersForLegacyClients().Select(FormatScheduledReminder)),
        [IsBadHabit] = AgentArgumentPreview.Format(habit.IsBadHabit),
        [IsFlexible] = AgentArgumentPreview.Format(habit.Schedule == HabitSchedule.Flexible),
        [ReminderEnabledField] = AgentArgumentPreview.Format(habit.ReminderEnabled),
        [ReminderTimesField] = string.Join(", ", habit.ReminderTimes),
        [ChecklistItemsField] = string.Join(", ", habit.ChecklistItems.Select(FormatChecklistItem))
    };

    private static bool HasTagArguments(JsonElement arguments) =>
        arguments.TryGetProperty(TagNames, out _) || arguments.TryGetProperty(TagIds, out _);

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

    private static IEnumerable<PendingOperationChange> BuildActionChanges(Habit habit, string operationId,
        JsonElement arguments, JsonElement revisedItems, bool isRevised, DateOnly today)
    {
        yield return BuildActionChange(habit, operationId, arguments, revisedItems, isRevised, today);
        if (operationId == "bulk_skip_habits")
            yield return AgentArgumentPreview.SkipAction(habit.Id, habit.Title, habit.Emoji);
    }

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
                item.GetProperty(HabitId).GetString() == habit.Id.ToString())
            : default;
        var value = isRevised && revisedItem.TryGetProperty(field, out var revisedValue)
            ? revisedValue.ValueKind == JsonValueKind.Null ? null : revisedValue.ToString()
            : arguments.TryGetProperty(field == "emoji" ? "emoji" : "date", out var proposed)
                ? proposed.ValueKind == JsonValueKind.Null ? null : proposed.ToString()
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
                item.GetProperty(HabitId).GetString() == habit.Id.ToString())
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
                property.Name, value, value, property.Name == DueDate ? "date" : "text", property.Value.Clone(),
                IsEditableUpdateField(property.Name, habit, effective, changes)));
        }
    }

    private static BulkHabitChanges? ResolveChanges(string operationId, JsonElement arguments)
    {
        if (operationId == "bulk_reschedule_habits")
        {
            if (!arguments.TryGetProperty(DueDate, out var dateElement)
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
            if (revisedItems.ValueKind != JsonValueKind.Array)
                return (null, "invalid_revised_items");
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
                || !item.TryGetProperty(HabitId, out var idValue)
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
                || !item.TryGetProperty(HabitId, out var idElement)
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
        if (!arguments.TryGetProperty(HabitId, out var value)
            || value.ValueKind != JsonValueKind.String
            || !Guid.TryParse(value.GetString(), out var id))
            return null;
        var filter = new BulkHabitFilter(false, [id], IncludeCompleted: true);
        var habits = await BulkHabitSelection.LoadAsync(habitRepository, userId, filter, cancellationToken);
        return BuildPreview("delete_habit", [], habits.Select(habit =>
            new PendingOperationItem(habit.Id.ToString(), habit.Id, habit.Title,
                [new PendingOperationChange(habit.Id, habit.Title, "delete", null, null, "action", EntityEmoji: habit.Emoji)],
                FingerprintHabit(habit), RemovesData: true, EntityEmoji: habit.Emoji)).ToList());
    }

    private async Task<PendingOperationChangePreview?> PreviewCreateAsync(
        Guid userId, JsonElement arguments, CancellationToken cancellationToken)
    {
        if (!arguments.TryGetProperty(ToolArgumentNames.Habits, out var habits) || habits.ValueKind != JsonValueKind.Array)
            return null;
        var command = new BulkCreateHabitsCommand(userId, habits.EnumerateArray()
            .Select(habit => habit.ValueKind == JsonValueKind.Object
                ? BulkCreateHabitsTool.ParseBulkHabitItem(habit, allowEmptyTitle: true)
                : null)
            .Select(item => item ?? new BulkHabitItem(string.Empty, null, null, null)).ToList());
        var validation = await new BulkCreateHabitsCommandValidator().ValidateAsync(command, cancellationToken);
        var parents = await LoadCreationParentsAsync(userId, arguments, habits, cancellationToken);
        var today = await userDateService.GetUserTodayAsync(userId, cancellationToken);
        var items = new List<PendingOperationItem>();
        for (var index = 0; index < habits.GetArrayLength(); index++)
        {
            var habit = habits[index];
            var errors = CreationValidationErrors(userId, command.Habits[index], index, today, validation, cancellationToken);
            var name = habit.ValueKind == JsonValueKind.Object
                && habit.TryGetProperty("title", out var title) ? title.ToString() : string.Empty;
            var fields = habit.ValueKind == JsonValueKind.Object ? CreationFields(habit, name) : [];
            var (parentFingerprint, parentOptions) = AddCreationParent(arguments, habit, parents, name, fields, errors);
            var (existing, reuseOptions) = AddCreationReuse(habit, parents, name, fields, errors);
            var reuseFingerprint = await BindCreationReuseAsync(userId, habit, existing, parents, fields, cancellationToken);
            if (reuseFingerprint is null) return null;
            parentFingerprint += reuseFingerprint;
            foreach (var error in errors.Where(error => CreateFields.Contains(error.Field)))
                if (fields.All(field => field.Field != error.Field))
                    fields.Add(new PendingOperationChange(Guid.Empty, name, error.Field,
                        null, null, "text", JsonSerializer.SerializeToElement<string?>(null), true));
            var itemId = habit.ValueKind == JsonValueKind.Object
                && habit.TryGetProperty("preview_item_id", out var storedId)
                ? storedId.ToString() : index.ToString(CultureInfo.InvariantCulture);
            var entityEmoji = existing is not null ? existing.Emoji : AgentArgumentPreview.DraftEmoji(habit);
            fields = fields.Select(field => field with { EntityEmoji = entityEmoji }).ToList();
            items.Add(new PendingOperationItem(itemId, existing?.Id,
                existing?.Title ?? name, fields, AgentOperationFingerprint.Compute(BulkCreateHabits + parentFingerprint, habit.GetRawText()),
                RemovesData: false, ValidationErrors: errors.Count > 0 ? errors : null,
                FieldOptions: parentOptions is null && reuseOptions is null ? null
                    : new[] { parentOptions, reuseOptions }.OfType<PendingOperationFieldOptions>().ToArray(),
                EntityEmoji: entityEmoji, HabitType: CreationHabitType(arguments, habit, command.Habits[index], existing, parents)));
        }
        return BuildPreview(BulkCreateHabits, items.SelectMany(item => item.Fields)
            .Where(field => field.Field == ParentHabitId && field.EntityId != Guid.Empty)
            .DistinctBy(field => field.EntityId).ToList(), items);
    }

    private static string? CreationHabitType(JsonElement arguments, JsonElement source, BulkHabitItem item,
        Habit? existing, IReadOnlyList<Habit> known)
    {
        var placement = JsonArgumentParser.GetOptionalString(source, HabitPlacementRecommendation.Argument);
        if (placement == HabitPlacement.ChecklistItem) return null;
        Habit? parent = null;
        var parentValue = CreationParentValue(arguments, source);
        if (existing is null && placement != HabitPlacement.NewParent && parentValue.ValueKind == JsonValueKind.String)
        {
            if (!Guid.TryParse(parentValue.GetString(), out var id)
                || (parent = known.FirstOrDefault(habit => habit.Id == id)) is null) return null;
        }
        var general = parent is not null ? parent.Schedule == HabitSchedule.General
            : JsonArgumentParser.GetOptionalBool(source, IsGeneralField)
                ?? (existing?.Schedule == HabitSchedule.General || item.IsGeneral);
        var frequency = source.TryGetProperty("frequency_unit", out _)
            ? item.FrequencyUnit : existing?.FrequencyUnit ?? item.FrequencyUnit
                ?? (item.InheritParentFrequency ? parent?.FrequencyUnit : null);
        return general ? "General" : frequency.HasValue ? "Habit" : "Task";
    }

    private static List<PendingOperationValidationError> CreationValidationErrors(Guid userId, BulkHabitItem item,
        int index, DateOnly today, FluentValidation.Results.ValidationResult validation, CancellationToken ct)
    {
        var prefix = $"Habits[{index}].";
        var errors = validation.Errors
            .Where(error => error.PropertyName.StartsWith(prefix, StringComparison.Ordinal)
                || !error.PropertyName.StartsWith("Habits[", StringComparison.Ordinal))
            .Select(error => new PendingOperationValidationError(
                JsonNamingPolicy.SnakeCaseLower.ConvertName(error.PropertyName.StartsWith(prefix, StringComparison.Ordinal)
                    ? error.PropertyName[prefix.Length..] : error.PropertyName),
                error.ErrorCode, error.ErrorMessage)).ToList();
        var intrinsic = BulkHabitSubtree.Build(userId, item, today, index, int.MaxValue, ct, out var invalidField);
        var field = JsonNamingPolicy.SnakeCaseLower.ConvertName(invalidField ?? "title");
        if (intrinsic.IsFailure && errors.All(error => error.Field != field))
            errors.Add(new(field, intrinsic.ErrorCode ?? "item_rejected", intrinsic.Error));
        return errors;
    }

    private async Task<IReadOnlyList<Habit>> LoadCreationParentsAsync(Guid userId, JsonElement arguments,
        JsonElement habits, CancellationToken cancellationToken)
    {
        var ids = new HashSet<Guid>();
        var names = new HashSet<string>();
        foreach (var habit in CreationNodes(habits))
        {
            ids.UnionWith(DependencyIds(habit, HabitId, HabitHeadingParents.ExistingCandidatesField));
            var value = CreationParentValue(arguments, habit);
            if (value.ValueKind != JsonValueKind.String)
                continue;
            if (Guid.TryParse(value.GetString(), out var id))
                ids.Add(id);
            else if (habit.TryGetProperty(HabitHeadingParents.CandidateIdsField, out _))
                ids.UnionWith(DependencyIds(habit, HabitHeadingParents.CandidateIdsField));
            else
                names.Add(value.GetString()!.ToLowerInvariant());
        }
        if (ids.Count == 0 && names.Count == 0)
            return [];
        var known = await habitRepository.FindAsync(habit => habit.UserId == userId && !habit.IsDeleted
            && (ids.Contains(habit.Id) || names.Contains(habit.Title.ToLower())),
            query => query.Include(habit => habit.Tags).Include(habit => habit.Goals).AsSplitQuery(), cancellationToken);
        var oldParentIds = known.Where(habit => habit.ParentHabitId.HasValue)
            .Select(habit => habit.ParentHabitId!.Value).Except(known.Select(habit => habit.Id)).ToArray();
        if (oldParentIds.Length == 0) return known;
        var oldParents = await habitRepository.FindAsync(habit => habit.UserId == userId && !habit.IsDeleted
            && oldParentIds.Contains(habit.Id), cancellationToken);
        return known.Concat(oldParents).DistinctBy(habit => habit.Id).ToArray();
    }

    private static JsonElement CreationParentValue(JsonElement arguments, JsonElement habit) =>
        habit.ValueKind == JsonValueKind.Object && habit.TryGetProperty(ParentHabitId, out var parent)
            || arguments.TryGetProperty(ParentHabitId, out parent) ? parent : default;

    private static (string Fingerprint, PendingOperationFieldOptions? Options) AddCreationParent(JsonElement arguments, JsonElement habit, IReadOnlyList<Habit> parents,
        string name, List<PendingOperationChange> fields, List<PendingOperationValidationError> errors)
    {
        var parentValue = CreationParentValue(arguments, habit);
        var isParentId = parentValue.ValueKind == JsonValueKind.String && Guid.TryParse(parentValue.GetString(), out _);
        var parent = isParentId && Guid.TryParse(parentValue.GetString(), out var id)
            ? parents.FirstOrDefault(candidate => candidate.Id == id) : null;
        var parentFingerprint = parent is null ? string.Empty : FingerprintHabit(parent);
        var unresolved = parentValue.ValueKind == JsonValueKind.String && !isParentId;
        var candidateIds = habit.ValueKind == JsonValueKind.Object
            ? DependencyIds(habit, HabitHeadingParents.CandidateIdsField) : [];
        var candidates = unresolved ? HabitHeadingParents.Candidates(parentValue.GetString()!,
            candidateIds.Count > 0 ? parents.Where(candidate => candidateIds.Contains(candidate.Id)).ToArray() : parents) : [];
        var requiresChoice = unresolved && (candidateIds.Count > 0 || candidates.Count > 1);
        fields.Add(new PendingOperationChange(parent?.Id ?? Guid.Empty, name, ParentHabitId, null,
            parent?.Title ?? (requiresChoice ? parentValue.GetString() : "No parent"), "text",
            parent is not null || requiresChoice ? parentValue.Clone() : JsonSerializer.SerializeToElement<string?>(null), true));
        if (requiresChoice)
            errors.Add(new PendingOperationValidationError(ParentHabitId, "parent_choice_required",
                "Choose a parent habit or No parent."));
        else if (unresolved || isParentId && parent is null)
            errors.Add(new PendingOperationValidationError(ParentHabitId, "parent_not_found", "Parent habit not found."));
        if (!requiresChoice)
            return (parentFingerprint, SuggestedParentOptions(habit, parent));
        var options = candidates.OrderBy(candidate => candidate.Id)
            .Select(candidate => new PendingOperationFieldOption(JsonSerializer.SerializeToElement(candidate.Id), candidate.Title))
            .Append(new PendingOperationFieldOption(JsonSerializer.SerializeToElement<string?>(null), "No parent")).ToArray();
        parentFingerprint = string.Join('|', candidates.OrderBy(candidate => candidate.Id).Select(FingerprintHabit));
        return (parentFingerprint, new PendingOperationFieldOptions(ParentHabitId, true, options));
    }

    private static PendingOperationFieldOptions? SuggestedParentOptions(JsonElement source, Habit? parent) =>
        parent is not null && source.TryGetProperty(HabitHeadingParents.SuggestedParentField, out var suggested) && suggested.ValueKind == JsonValueKind.True
            ? new(ParentHabitId, false, [new(JsonSerializer.SerializeToElement(parent.Id), parent.Title), new(JsonSerializer.SerializeToElement<string?>(null), "No parent")])
            : null;

    private static (Habit? Existing, PendingOperationFieldOptions? Options) AddCreationReuse(JsonElement source,
        IReadOnlyList<Habit> known, string name, List<PendingOperationChange> fields, List<PendingOperationValidationError> errors)
    {
        if (!source.TryGetProperty(HabitId, out var value)) return (null, null);
        var field = fields.FindIndex(field => field.Field == HabitId);
        if (Guid.TryParse(value.ToString(), out var id))
        {
            var existing = known.FirstOrDefault(habit => habit.Id == id);
            if (existing is null) errors.Add(new(HabitId, "habit_not_found", "Habit not found."));
            if (field >= 0) fields[field] = fields[field] with { NewValue = existing?.Title ?? name, IsEditable = false };
            return (existing, null);
        }
        var ids = DependencyIds(source, HabitHeadingParents.ExistingCandidatesField);
        var candidates = known.Where(habit => ids.Contains(habit.Id)).OrderBy(habit => habit.Id).ToArray();
        errors.Add(new(HabitId, "habit_choice_required", "Choose an existing habit in the preview."));
        return (null, new(HabitId, true, candidates.Select(habit => new PendingOperationFieldOption(JsonSerializer.SerializeToElement(habit.Id), habit.Title)).ToArray()));
    }

    private static PendingOperationChangePreview? BuildPreview(
        string operationId, IReadOnlyList<PendingOperationChange> changes, IReadOnlyList<PendingOperationItem> items)
    {
        if (items.Count == 0)
            return BulkNoChangesOutcome.Supports(operationId)
                ? new PendingOperationChangePreview([], 0, [])
                : null;
        var fingerprint = AgentOperationFingerprint.Compute(operationId, JsonSerializer.Serialize(items));
        return new PendingOperationChangePreview(changes, items.Count, items, fingerprint);
    }

    private static string FingerprintHabit(Habit habit) => AgentOperationFingerprint.Compute(
        habit.Id.ToString(), JsonSerializer.Serialize(new
        {
            habit.UpdatedAtUtc,
            habit.ParentHabitId,
            Tags = habit.Tags.OrderBy(tag => tag.Id).Select(tag => new { tag.Id, tag.Name }),
            Goals = habit.Goals.OrderBy(goal => goal.Id).Select(goal => new { goal.Id, goal.Title }),
            habit.Title,
            habit.Description,
            habit.Emoji,
            habit.IsCompleted,
            DueDate = habit.NextDueDate,
            habit.DueTime,
            habit.DueEndTime,
            habit.Schedule,
            habit.SlipAlertEnabled,
            habit.EndDate,
            habit.FrequencyUnit,
            habit.FrequencyQuantity,
            habit.IntervalWeeks,
            habit.IsBadHabit,
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
        Add(IntervalWeeks, habit.IntervalWeeks, effective.IntervalWeeks, "number");
        Add("days", string.Join(", ", habit.Days), string.Join(", ", effective.Days), "text",
            typedValue: JsonSerializer.SerializeToElement(effective.Days.Select(day => day.ToString())));
        Add(DueDate, habit.NextDueDate, effective.NextDueDate, "date");
        if (changes.HasStartDate) Add(StartDate, habit.StartDate, effective.StartDate, "date");
        Add(EndDate, habit.EndDate, effective.EndDate, "date");
        Add(DueTime, habit.DueTime, effective.DueTime, "time");
        Add(DueEndTimeField, habit.DueEndTime, effective.DueEndTime, "time");
        Add(IsGeneralField, habit.Schedule == HabitSchedule.General, effective.Schedule == HabitSchedule.General, JsonSchemaTypes.Boolean);
        Add(SlipAlertEnabledField, habit.SlipAlertEnabled, effective.SlipAlertEnabled, JsonSchemaTypes.Boolean);
        AddList(RelativeRemindersField, habit.RelativeReminders, effective.RelativeReminders,
            HabitFieldSummary.FormatRelative,
            JsonSerializer.SerializeToElement(effective.RelativeReminders.Select(RelativeReminderValue)));
        Add(IsBadHabit, habit.IsBadHabit, effective.IsBadHabit, JsonSchemaTypes.Boolean);
        Add(IsFlexible, habit.Schedule == HabitSchedule.Flexible, effective.Schedule == HabitSchedule.Flexible, JsonSchemaTypes.Boolean);
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
                or IntervalWeeks or DueDate or DueTime or IsBadHabit
                or IsFlexible or ReminderEnabledField or ChecklistItemsField
                or DueEndTimeField or IsGeneralField or SlipAlertEnabledField or RelativeRemindersField => true,
            "days" => effective.Schedule != HabitSchedule.Flexible,
            EndDate => habit.Schedule != HabitSchedule.General,
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
