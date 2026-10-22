using System.Text.Json;
using Microsoft.Extensions.Logging;
using Orbit.Application.Chat.Tools;
using Orbit.Application.Chat.Validators;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Application.Habits.Commands;
using Orbit.Domain.Models;

namespace Orbit.Application.Chat.Commands;

public partial class ProcessUserChatCommandHandler
{
    private void PrepareHabitProposals(Dictionary<string, AiToolCall?> calls,
        ProcessUserChatCommand request, ToolExecutionAccumulator results, string? language)
    {
        foreach (var (id, call) in calls.ToArray())
        {
            if (call is { Name: "update_habit" or "bulk_update_habits" })
            {
                if (CorrectionError(call, request.Message, results) is { } correctionError)
                {
                    results.InvalidCalls[id] = correctionError;
                    calls[id] = null;
                    logger.LogWarning("Agent operation {OperationId} rejected with {ReasonCode}", call.Name, "CORRECTION_PRESERVES_SCHEDULE");
                }
                else
                    calls[id] = call with { Args = HabitProposalValidator.LocalizeTextDates(call.Args, language) };
                continue;
            }
            if (call is null || call.Name is not ("create_habit" or "bulk_create_habits" or "create_sub_habit"))
                continue;
            var arguments = ai.ToolRegistry.GetTool(call.Name) is { } tool
                ? OptionalToolArguments.OmitOptionalNulls(call.Args, tool.GetParameterSchema()) : call.Args;
            var validation = new HabitProposalValidator().Validate(new HabitProposal(request.Message, arguments));
            if (!validation.IsValid)
            {
                results.InvalidCalls[id] = string.Join(" ", validation.Errors.Select(error => error.ErrorMessage).Distinct());
                calls[id] = null;
                logger.LogWarning("Agent operation {OperationId} rejected with {ReasonCode}", call.Name, validation.Errors[0].ErrorCode);
                continue;
            }
            calls[id] = call with { Args = HabitProposalValidator.LocalizeTextDates(arguments, language) };
        }
        CoalesceCreateCalls(calls, results);
    }

    private static string? CorrectionError(AiToolCall call, string message, ToolExecutionAccumulator results)
    {
        var targets = new List<(Orbit.Domain.Entities.Habit Habit, JsonElement Changes)>();
        if (call.Name == "update_habit" && call.Args.TryGetProperty("habit_id", out var id)
            && Guid.TryParse(id.GetString(), out var habitId)
            && results.AllHabits.FirstOrDefault(habit => habit.Id == habitId) is { } current)
            targets.Add((current, call.Args));
        if (call.Name == "bulk_update_habits")
        {
            var (filter, error) = BulkHabitToolArguments.ParseRequiredFilter(call.Args);
            if (error is null && filter is not null && call.Args.TryGetProperty("updates", out var updates))
                targets.AddRange(BulkHabitSelection.Select(results.AllHabits, filter).Select(habit => (habit, updates)));
        }
        foreach (var (habit, changes) in targets)
        {
            var validation = new HabitCorrectionProposalValidator().Validate(new HabitCorrectionProposal(message, changes, habit));
            if (!validation.IsValid)
                return validation.Errors[0].ErrorMessage;
        }
        return null;
    }

    private void CoalesceCreateCalls(Dictionary<string, AiToolCall?> calls, ToolExecutionAccumulator results)
    {
        if (ai.ToolRegistry.GetTool("bulk_create_habits") is not { } bulk)
            return;
        var schema = JsonSerializer.SerializeToElement(bulk.GetParameterSchema())
            .GetProperty("properties").GetProperty("habits").GetProperty("items");
        var groups = calls.Values.OfType<AiToolCall>()
            .Where(call => call.Name is "create_habit" or "bulk_create_habits")
            .GroupBy(call => call.Args.TryGetProperty("parent_habit_id", out var parent) ? parent.ToString() : string.Empty)
            .Select(group => group.ToArray()).ToArray();
        foreach (var group in groups)
        {
            if (group.Length < 2)
                continue;
            var items = group.SelectMany(call => call.Name == "bulk_create_habits"
                ? call.Args.GetProperty("habits").EnumerateArray().ToArray() : [call.Args]).ToArray();
            if (items.Any(item => item.EnumerateObject().Any(field =>
                !AgentArgumentSchema.Accepts(schema, field.Name, field.Value))))
                continue;
            var first = group[0];
            var arguments = new Dictionary<string, object?> { ["habits"] = items };
            if (first.Args.TryGetProperty("parent_habit_id", out var parent))
                arguments["parent_habit_id"] = parent.Clone();
            calls[first.Id] = first with { Name = "bulk_create_habits", Args = JsonSerializer.SerializeToElement(arguments) };
            foreach (var call in group.Skip(1))
            {
                calls[call.Id] = null;
                results.MergedCallIds.Add(call.Id);
            }
        }
    }
}
