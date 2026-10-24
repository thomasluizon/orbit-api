using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Orbit.Application.Habits.Commands;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Chat.Tools.Implementations;

public sealed partial class BulkUpdateHabitEmojisTool(
    IGenericRepository<Habit> habitRepository,
    IHabitEmojiInferenceService inferenceService,
    IUnitOfWork unitOfWork,
    ILogger<BulkUpdateHabitEmojisTool> logger) : IAiTool, IArgumentCheckTool
{
    internal const int InferenceChunkSize = 25;

    public string Name => "bulk_update_habit_emojis";

    public string Description =>
        "Infer habit emojis, set a requested emoji, or clear icons for all matches. For without an icon / sem ícone, use filter.has_emoji=false. Inference preserves existing icons unless the user asks to replace them; then set replace_existing=true.";

    public object GetParameterSchema() => new
    {
        type = JsonSchemaTypes.Object,
        properties = new
        {
            emoji_mode = new { type = new[] { JsonSchemaTypes.String, "null" }, @enum = new[] { "infer", "set", "clear" }, description = "Defaults to infer. Use set with emoji, or clear to remove icons." },
            replace_existing = new
            {
                type = new[] { JsonSchemaTypes.Boolean, "null" },
                description = "Defaults to false. Set true only when the user asks to replace existing icons, such as change every icon / troca todos os ícones. Applies to inference."
            },
            filter = BulkHabitToolArguments.FilterSchema(),
            habit_ids = new
            {
                type = JsonSchemaTypes.Array,
                items = new { type = JsonSchemaTypes.String },
                description = "Legacy selection. Omit to update all active habits, or use filter for server-side predicates."
            },
            emoji = new
            {
                type = new[] { JsonSchemaTypes.String, "null" },
                description = "Requested icon. Null uses the default mode; clear via emoji_mode."
            },
            infer_from_title = new
            {
                type = new[] { JsonSchemaTypes.Boolean, "null" },
                description = "Infer one sensible emoji from each title and description. Defaults to true when emoji is omitted."
            },
            include_completed = new
            {
                type = new[] { JsonSchemaTypes.Boolean, "null" },
                description = "Legacy selection option. Include completed habits when filter is omitted. Defaults to false."
            }
        },
        required = Array.Empty<string>()
    };

    public async Task<Result> CheckArgumentsAsync(JsonElement args, Guid userId, CancellationToken ct)
    {
        if (!args.TryGetProperty("revised_items", out _))
            args = OptionalToolArguments.OmitOptionalNulls(args, GetParameterSchema());
        var overrides = new Dictionary<Guid, string?>();
        var (filter, error) = args.TryGetProperty("revised_items", out var items)
            ? ParseRevisedItems(items, overrides) : ParseInferenceFilter(args);
        if (error is not null)
            return Result.Failure(error);
        var (hasEmoji, infer, modeError) = ResolveEmojiOperation(args);
        if (modeError is not null)
            return Result.Failure(modeError);
        var emoji = JsonArgumentParser.GetNullableString(args, "emoji");
        if (!infer && !hasEmoji && overrides.Count != filter!.HabitIds.Count)
            return Result.Failure("Provide emoji or set infer_from_title to true.");
        if (emoji is not null && !IsSingleEmojiGrapheme(emoji))
            return Result.Failure("Invalid emoji.");
        var habits = await BulkHabitSelection.LoadAsync(habitRepository, userId, filter!, ct);
        var ownership = Orbit.Application.Common.OwnershipValidation.AllResolved(filter!.HabitIds, habits, h => h.Id,
            Orbit.Application.Common.ErrorMessages.HabitNotFound);
        if (ownership.IsFailure)
            return ownership;
        foreach (var habit in habits)
        {
            if (infer && !overrides.ContainsKey(habit.Id))
                continue;
            var validation = habit.ValidateUpdate(EmojiUpdate(habit, overrides.GetValueOrDefault(habit.Id, emoji)));
            if (validation.IsFailure)
                return validation;
        }
        return habits.Count == 0 ? Result.Failure("No matching habits found.") : Result.Success();
    }

    internal async Task<Result<JsonElement>> PreparePreviewArgumentsAsync(
        JsonElement args, Guid userId, CancellationToken ct)
    {
        var emojis = new Dictionary<Guid, string?>();
        var (filter, error) = args.TryGetProperty("revised_items", out var items)
            ? ParseRevisedItems(items, emojis) : ParseInferenceFilter(args);
        if (error is not null)
            return Result.Failure<JsonElement>(error);
        var habits = await BulkHabitSelection.LoadAsync(habitRepository, userId, filter!, ct);
        if (habits.Count == 0)
            return Result.Failure<JsonElement>("No matching habits found.");
        var ownership = Orbit.Application.Common.OwnershipValidation.AllResolved(filter!.HabitIds, habits,
            habit => habit.Id, Orbit.Application.Common.ErrorMessages.HabitNotFound);
        if (ownership.IsFailure)
            return ownership.PropagateError<JsonElement>();
        var inputs = habits.Where(habit => !emojis.ContainsKey(habit.Id))
            .Select(habit => new HabitEmojiInferenceInput(habit.Id, habit.Title, habit.Description)).ToArray();
        var chunkIndex = 0;
        var chunkCount = (inputs.Length + InferenceChunkSize - 1) / InferenceChunkSize;
        foreach (var chunk in inputs.Chunk(InferenceChunkSize))
        {
            chunkIndex++;
            var inferred = await InferPreviewChunkAsync(userId, chunk, chunkIndex, chunkCount, habits.Count, ct);
            if (inferred.IsFailure)
                return inferred.PropagateError<JsonElement>();
            foreach (var input in chunk)
                emojis[input.HabitId] = inferred.Value[input.HabitId];
        }
        var changed = habits.Where(habit => !string.Equals(habit.Emoji, emojis[habit.Id], StringComparison.Ordinal)).ToArray();
        if (changed.Length == 0)
            return Result.Failure<JsonElement>("No matching emoji changes.");
        return Result.Success(JsonSerializer.SerializeToElement(new
        {
            infer_from_title = false,
            revised_items = changed.Select(habit => new { habit_id = habit.Id, emoji = emojis[habit.Id] }).ToArray()
        }));
    }

    private async Task<Result<IReadOnlyDictionary<Guid, string>>> InferPreviewChunkAsync(
        Guid userId, HabitEmojiInferenceInput[] chunk, int chunkIndex, int chunkCount, int targetCount,
        CancellationToken ct)
    {
        Result<IReadOnlyDictionary<Guid, string>> inferred;
        try
        {
            inferred = await inferenceService.InferAsync(userId, chunk, ct);
        }
        catch (OperationCanceledException)
        {
            LogPreparationFailed(logger, "inference", ct.IsCancellationRequested ? "cancelled" : "deadline",
                chunkIndex, chunkCount, chunk.Length, targetCount);
            throw;
        }
        catch (Exception)
        {
            LogPreparationFailed(logger, "inference", "unknown", chunkIndex, chunkCount, chunk.Length, targetCount);
            return Result.Failure<IReadOnlyDictionary<Guid, string>>("preview_unavailable");
        }
        if (inferred.IsFailure)
        {
            LogPreparationFailed(logger, "inference",
                inferred.ErrorCode == "AI_DAILY_BUDGET_EXCEEDED" ? "budget_exceeded" : "unknown",
                chunkIndex, chunkCount, chunk.Length, targetCount);
            return inferred;
        }
        var ids = chunk.Select(input => input.HabitId).ToHashSet();
        var reason = inferred.Value.Keys.Any(id => !ids.Contains(id)) ? "unexpected_mapping"
            : chunk.Any(input => !inferred.Value.ContainsKey(input.HabitId)) ? "missing_mapping"
            : inferred.Value.Values.Any(emoji => !IsSingleEmojiGrapheme(emoji)) ? "invalid_emoji" : null;
        if (reason is null)
            return inferred;
        LogPreparationFailed(logger, "mapping", reason, chunkIndex, chunkCount, chunk.Length, targetCount);
        return Result.Failure<IReadOnlyDictionary<Guid, string>>("preview_unavailable");
    }

    [LoggerMessage(EventId = 3, Level = LogLevel.Warning,
        Message = "Habit emoji preparation failed at {Stage} with {Reason}; chunk {ChunkIndex} of {ChunkCount}, {ChunkTargetCount} of {TargetCount} targets")]
    private static partial void LogPreparationFailed(ILogger logger, string stage, string reason,
        int chunkIndex, int chunkCount, int chunkTargetCount, int targetCount);

    public async Task<ToolResult> ExecuteAsync(JsonElement args, Guid userId, CancellationToken ct)
    {
        if (!args.TryGetProperty("revised_items", out _))
            args = OptionalToolArguments.OmitOptionalNulls(args, GetParameterSchema());
        var overrides = new Dictionary<Guid, string?>();
        var (filter, filterError) = args.TryGetProperty("revised_items", out var revisedItems)
            ? ParseRevisedItems(revisedItems, overrides)
            : ParseInferenceFilter(args);
        if (filterError is not null)
            return new ToolResult(false, Error: filterError);

        var (hasEmojiArgument, inferFromTitle, modeError) = ResolveEmojiOperation(args);
        if (modeError is not null)
            return new ToolResult(false, Error: modeError);
        var requestedEmoji = JsonArgumentParser.GetNullableString(args, "emoji");
        if (!inferFromTitle && !hasEmojiArgument
            && (filter is null || overrides.Count != filter.HabitIds.Count))
            return new ToolResult(false, Error: "Provide emoji or set infer_from_title to true.");

        var habits = await BulkHabitSelection.LoadAsync(habitRepository, userId, filter!, ct);
        if (habits.Count == 0)
            return new ToolResult(false, Error: "No matching habits found to update.");

        var inputs = habits
            .Select(habit => new HabitEmojiInferenceInput(habit.Id, habit.Title, habit.Description))
            .ToList();
        var appliedCount = 0;
        var stopped = false;
        foreach (var chunk in inputs.Chunk(InferenceChunkSize))
        {
            IReadOnlyDictionary<Guid, string>? inferred = null;
            var inferenceInputs = chunk.Where(input => !overrides.ContainsKey(input.HabitId)).ToArray();
            if (inferFromTitle && inferenceInputs.Length > 0)
            {
                var inferenceResult = await inferenceService.InferAsync(
                    userId,
                    inferenceInputs,
                    ct);
                if (inferenceResult.IsFailure)
                {
                    LogInferenceStopped(logger, appliedCount, habits.Count, inferenceResult.Error);
                    stopped = true;
                    break;
                }
                inferred = inferenceResult.Value;
            }

            try
            {
                var chunkIds = chunk.Select(input => input.HabitId).ToHashSet();
                var chunkApplied = await unitOfWork.ExecuteInTransactionAsync(async transactionToken =>
                {
                    var trackedHabits = await habitRepository.FindTrackedAsync(
                        habit => habit.UserId == userId && chunkIds.Contains(habit.Id),
                        query => query,
                        transactionToken);
                    var attemptApplied = 0;
                    foreach (var habit in trackedHabits)
                    {
                        string? emoji;
                        if (overrides.TryGetValue(habit.Id, out var overrideEmoji))
                        {
                            emoji = overrideEmoji;
                        }
                        else if (inferFromTitle)
                        {
                            if (!inferred!.TryGetValue(habit.Id, out emoji) || !IsSingleEmojiGrapheme(emoji))
                                continue;
                        }
                        else
                        {
                            emoji = requestedEmoji;
                        }

                        if (string.Equals(habit.Emoji, emoji, StringComparison.Ordinal))
                            continue;
                        if (ApplyEmoji(habit, emoji).IsSuccess)
                            attemptApplied++;
                    }

                    if (attemptApplied > 0)
                        await unitOfWork.SaveChangesAsync(transactionToken);

                    return attemptApplied;
                }, ct);
                appliedCount += chunkApplied;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                unitOfWork.DiscardChanges();
                LogChunkFailed(logger, appliedCount, habits.Count, exception);
                stopped = true;
                break;
            }
        }

        var totalMatched = habits.Count;
        var skippedCount = totalMatched - appliedCount;
        var partial = stopped || skippedCount > 0;
        return BulkUpdateHabitsTool.BuildResult(
            new BulkHabitMutationResult(appliedCount, totalMatched, skippedCount, partial),
            "Updated emojis for",
            includeUpdatedCount: true);
    }

    private static (BulkHabitFilter? Filter, string? Error) ParseInferenceFilter(JsonElement args)
    {
        if (args.TryGetProperty("replace_existing", out var replace)
            && replace.ValueKind is not (JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null))
            return (null, "replace_existing must be a boolean.");
        var (filter, error) = BulkHabitToolArguments.ParseEmojiFilter(args);
        if (error is not null || filter is null || !ResolveEmojiOperation(args).Infer
            || JsonArgumentParser.GetOptionalBool(args, "replace_existing") == true)
            return (filter, error);
        if (filter.HasEmoji == true)
            return (null, "Set replace_existing to true to infer replacements for existing icons.");
        return (filter with { HasEmoji = false }, null);
    }

    internal static (bool HasEmoji, bool Infer, string? Error) ResolveEmojiOperation(JsonElement args)
    {
        var hasEmoji = JsonArgumentParser.PropertyExists(args, "emoji");
        return JsonArgumentParser.GetOptionalString(args, "emoji_mode") switch
        {
            "infer" => (false, true, null),
            "clear" when !hasEmoji => (true, false, null),
            "set" when hasEmoji => (true, false, null),
            null => (hasEmoji, JsonArgumentParser.GetOptionalBool(args, "infer_from_title") ?? !hasEmoji, null),
            _ => (false, false, "Invalid emoji_mode or conflicting emoji value.")
        };
    }

    private static (BulkHabitFilter? Filter, string? Error) ParseRevisedItems(
        JsonElement items, Dictionary<Guid, string?> overrides)
    {
        if (items.ValueKind != JsonValueKind.Array || items.GetArrayLength() == 0)
            return (null, "revised_items must be a non-empty array.");
        var ids = new List<Guid>();
        foreach (var item in items.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object
                || !item.TryGetProperty("habit_id", out var idValue)
                || idValue.ValueKind != JsonValueKind.String
                || !Guid.TryParse(idValue.GetString(), out var id)
                || ids.Contains(id))
                return (null, "revised_items contains an invalid habit ID.");
            ids.Add(id);
            if (!item.TryGetProperty("emoji", out var emoji))
                continue;
            if (emoji.ValueKind is not JsonValueKind.Null and not JsonValueKind.String)
                return (null, "revised_items contains an invalid emoji.");
            var value = emoji.ValueKind == JsonValueKind.Null ? null : emoji.GetString();
            if (value is not null && !IsSingleEmojiGrapheme(value))
                return (null, "revised_items contains an invalid emoji.");
            overrides[id] = value;
        }
        return (new BulkHabitFilter(false, ids, IncludeCompleted: true), null);
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "Bulk emoji inference stopped after {AppliedCount} of {TotalMatched} matches: {Reason}")]
    private static partial void LogInferenceStopped(ILogger logger, int appliedCount, int totalMatched, string reason);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "Bulk emoji update chunk failed after {AppliedCount} of {TotalMatched} matches")]
    private static partial void LogChunkFailed(ILogger logger, int appliedCount, int totalMatched, Exception ex);

    internal static bool IsSingleEmojiGrapheme(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || !string.Equals(value, value.Trim(), StringComparison.Ordinal))
            return false;
        if (StringInfo.ParseCombiningCharacters(value).Length != 1)
            return false;

        return value.EnumerateRunes().Any(IsEmojiRune);
    }

    private static bool IsEmojiRune(Rune rune)
    {
        var value = rune.Value;
        return value is 0x00A9 or 0x00AE or 0x203C or 0x2049 or 0x2122 or 0x2139
            || value is >= 0x2194 and <= 0x21FF
            || value is >= 0x2300 and <= 0x23FF
            || value is >= 0x2600 and <= 0x27BF
            || value is >= 0x1F000 and <= 0x1FAFF;
    }

    private static Result ApplyEmoji(Habit habit, string? emoji)
    {
        return habit.Update(EmojiUpdate(habit, emoji));
    }

    private static HabitUpdateParams EmojiUpdate(Habit habit, string? emoji) => new(
            habit.Title,
            habit.Description,
            habit.FrequencyUnit,
            habit.FrequencyQuantity,
            habit.Days.ToList(),
            habit.IsBadHabit,
            habit.NextDueDate,
            DueTime: habit.DueTime,
            DueEndTime: habit.DueEndTime,
            ReminderEnabled: habit.ReminderEnabled,
            ReminderTimes: habit.ReminderTimes,
            SlipAlertEnabled: habit.SlipAlertEnabled,
            ChecklistItems: habit.ChecklistItems,
            IsGeneral: habit.Schedule == HabitSchedule.General,
            IsFlexible: habit.Schedule == HabitSchedule.Flexible,
            EndDate: habit.EndDate,
            ScheduledReminders: habit.ScheduledReminders,
            Emoji: emoji,
            IntervalWeeks: habit.IntervalWeeks);
}
