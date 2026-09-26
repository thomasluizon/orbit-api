using System.Text.Json;
using MediatR;
using Orbit.Application.Common;
using Orbit.Application.Habits.Commands;
using Orbit.Domain.Common;

namespace Orbit.Application.Chat.Tools.Implementations;

public sealed class BulkUpdateHabitsTool(IMediator mediator) : IAiTool
{
    public string Name => "bulk_update_habits";

    public string Description =>
        "Update fields or schedules for the complete server-side set of habits matching a filter. Use one call for every multi-habit update instead of repeating update_habit.";

    public object GetParameterSchema() => new
    {
        type = JsonSchemaTypes.Object,
        properties = new
        {
            filter = BulkHabitToolArguments.FilterSchema(),
            updates = new
            {
                type = JsonSchemaTypes.Object,
                description = "Fields to apply to every matching habit. Omitted fields stay unchanged.",
                properties = new
                {
                    title = new { type = JsonSchemaTypes.String },
                    description = new { type = JsonSchemaTypes.String, nullable = true },
                    emoji = new { type = JsonSchemaTypes.String, nullable = true },
                    frequency_unit = new { type = JsonSchemaTypes.String, nullable = true, @enum = JsonSchemaTypes.FrequencyUnitEnum },
                    frequency_quantity = new { type = JsonSchemaTypes.Integer, nullable = true },
                    interval_weeks = new { type = JsonSchemaTypes.Integer, nullable = true },
                    days = new { type = JsonSchemaTypes.Array, items = new { type = JsonSchemaTypes.String } },
                    due_date = new { type = JsonSchemaTypes.String },
                    end_date = new { type = JsonSchemaTypes.String, nullable = true },
                    due_time = new { type = JsonSchemaTypes.String, nullable = true },
                    is_bad_habit = new { type = JsonSchemaTypes.Boolean },
                    is_flexible = new { type = JsonSchemaTypes.Boolean },
                    reminder_enabled = new { type = JsonSchemaTypes.Boolean },
                    reminder_times = new { type = JsonSchemaTypes.Array, items = new { type = JsonSchemaTypes.Integer } },
                    checklist_items = new { type = JsonSchemaTypes.Array, items = new { type = JsonSchemaTypes.Object } },
                    scheduled_reminders = new { type = JsonSchemaTypes.Array, items = new { type = JsonSchemaTypes.Object } }
                }
            }
        },
        required = new[] { "filter", "updates" }
    };

    public async Task<ToolResult> ExecuteAsync(JsonElement args, Guid userId, CancellationToken ct)
    {
        if (args.TryGetProperty("revised_items", out var revisedItems))
            return await ExecuteRevisedAsync(mediator, revisedItems, userId, "Updated", ct);

        var (filter, filterError) = BulkHabitToolArguments.ParseRequiredFilter(args);
        if (filterError is not null)
            return new ToolResult(false, Error: filterError);
        var (changes, changesError) = BulkHabitToolArguments.ParseChanges(args);
        if (changesError is not null)
            return new ToolResult(false, Error: changesError);

        var result = await mediator.Send(new BulkUpdateHabitsCommand(userId, filter!, changes!), ct);
        if (result.IsFailure)
            return ToolResult.FromFailure(result);
        return BuildResult(result.Value, "Updated");
    }

    internal static async Task<ToolResult> ExecuteRevisedAsync(IMediator mediator,
        JsonElement items, Guid userId, string verb, CancellationToken ct)
    {
        if (items.ValueKind != JsonValueKind.Array || items.GetArrayLength() == 0)
            return new ToolResult(false, Error: "revised_items must be a non-empty array.");

        var parsed = new List<(Guid Id, BulkHabitChanges Changes)>();
        var seen = new HashSet<Guid>();
        foreach (var item in items.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object
                || !item.TryGetProperty("habit_id", out var idValue)
                || idValue.ValueKind != JsonValueKind.String
                || !Guid.TryParse(idValue.GetString(), out var id)
                || !seen.Add(id))
                return new ToolResult(false, Error: "revised_items contains an invalid habit ID.");
            var (changes, error) = BulkHabitToolArguments.ParseChanges(item);
            if (error is not null || changes is null || !changes.HasAnyChange)
                return new ToolResult(false, Error: error ?? "revised_items requires changes.");
            parsed.Add((id, changes));
        }

        var applied = 0;
        foreach (var item in parsed)
        {
            var result = await mediator.Send(new BulkUpdateHabitsCommand(userId,
                new BulkHabitFilter(false, [item.Id], IncludeCompleted: true), item.Changes), ct);
            if (result.IsFailure)
                return applied == 0 ? ToolResult.FromFailure(result)
                    : BuildResult(new BulkHabitMutationResult(applied, parsed.Count,
                        parsed.Count - applied, true), verb);
            applied += result.Value.AppliedCount;
        }
        return BuildResult(new BulkHabitMutationResult(applied, parsed.Count,
            parsed.Count - applied, applied != parsed.Count), verb);
    }

    internal static ToolResult BuildResult(
        BulkHabitMutationResult result,
        string verb,
        bool includeUpdatedCount = false)
    {
        if (result.TotalMatched == 0)
            return new ToolResult(false, Error: "No matching habits found.");

        var completion = result.Partial ? "Partial result." : "Complete result.";
        object payload = includeUpdatedCount
            ? new
            {
                applied_count = result.AppliedCount,
                total_matched = result.TotalMatched,
                skipped_count = result.SkippedCount,
                partial = result.Partial,
                updated_count = result.AppliedCount
            }
            : new
            {
                applied_count = result.AppliedCount,
                total_matched = result.TotalMatched,
                skipped_count = result.SkippedCount,
                partial = result.Partial
            };
        return new ToolResult(
            true,
            EntityName: $"{verb} {result.AppliedCount} of {result.TotalMatched} matching habit(s). Skipped {result.SkippedCount}. {completion}",
            Payload: payload);
    }

    internal static async Task<ToolResult> ExecuteInChunksAsync<TItem, TResult>(
        IReadOnlyList<TItem> items,
        Func<IReadOnlyList<TItem>, CancellationToken, Task<Result<TResult>>> executeChunk,
        Func<TResult, int> countApplied,
        string verb,
        CancellationToken cancellationToken)
    {
        var appliedCount = 0;
        foreach (var chunk in items.Chunk(AppConstants.MaxBulkOperationSize))
        {
            Result<TResult> result;
            try
            {
                result = await executeChunk(chunk, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                return appliedCount == 0
                    ? new ToolResult(false, Error: "Bulk habit operation failed before any changes were applied.")
                    : BuildResult(
                        new BulkHabitMutationResult(appliedCount, items.Count, items.Count - appliedCount, true),
                        verb);
            }

            if (result.IsFailure)
            {
                return appliedCount == 0
                    ? ToolResult.FromFailure(result)
                    : BuildResult(
                        new BulkHabitMutationResult(appliedCount, items.Count, items.Count - appliedCount, true),
                        verb);
            }

            appliedCount += countApplied(result.Value);
        }

        return BuildResult(
            new BulkHabitMutationResult(appliedCount, items.Count, items.Count - appliedCount, appliedCount != items.Count),
            verb);
    }
}
