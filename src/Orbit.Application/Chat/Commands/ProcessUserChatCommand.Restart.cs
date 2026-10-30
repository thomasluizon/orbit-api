using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Application.Chat.Validators;
using Orbit.Domain.Models;

namespace Orbit.Application.Chat.Commands;

public partial class ProcessUserChatCommandHandler
{
    private async Task RejectInvalidRestartPlansAsync(IReadOnlyDictionary<string, AiToolCall?> calls, Guid userId,
        ToolExecutionAccumulator results, IEnumerable<AiToolCallResult> currentReads,
        Dictionary<string, ToolCallOutcome> outcomes, CancellationToken ct)
    {
        foreach (var (id, call) in calls)
        {
            if (call is null || !TerminalWriteDirective.IsRestartPlan(call) || outcomes.ContainsKey(id))
                continue;
            if (await ValidateRestartPlanAsync(call, userId, results, currentReads, ct) is { } error)
                outcomes[id] = new ToolCallOutcome(new AiToolCallResult(call.Name, call.Id, false, null, null, error),
                    null, null, null, null);
        }
    }

    private async Task<string?> ValidateRestartPlanAsync(AiToolCall call, Guid userId,
        ToolExecutionAccumulator results, IEnumerable<AiToolCallResult> currentReads, CancellationToken ct)
    {
        if (ai.ToolRegistry.GetTool("query_habits") is not QueryHabitsTool query)
            return RestartPlanValidator.FreshReadRequired;
        var current = await query.ReadRestartSetAsync(userId, ct);
        if (current.IsFailure)
            return current.Error;
        var reads = results.ToolResults.Concat(currentReads)
            .Where(read => read is { Name: "query_habits", Success: true, Payload: HabitListCard })
            .Select(read => (HabitListCard)read.Payload!).ToArray();
        var proposal = new RestartPlanProposal(call.Args, current.Value.Today, current.Value.Habits,
            results.AllHabits, reads);
        var validation = await new RestartPlanValidator().ValidateAsync(proposal, ct);
        return validation.IsValid ? null : string.Join(" ", validation.Errors.Select(error => error.ErrorMessage));
    }
}
