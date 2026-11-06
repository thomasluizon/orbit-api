using System.Text.Json;
using Microsoft.Extensions.Logging;
using Orbit.Application.Chat.Models;
using Orbit.Application.Common;
using Orbit.Application.Chat.Validators;
using Orbit.Domain.Common;
using Orbit.Domain.Models;

namespace Orbit.Application.Chat.Commands;

public partial class ProcessUserChatCommandHandler
{
    private async Task<ToolRoundResult> ProcessToolCallsAsync(AiResponse response, ProcessUserChatCommand request,
        ChatContext context, ToolExecutionAccumulator results, int iteration, long totalBudgetTokens,
        RejectedCallCorrection? correction, string? language, Func<AiStreamEvent, Task>? streamSink, CancellationToken ct)
    {
        var plans = await AcceptPlanAsync(response, request, results);
        TracePlanCalls(response, plans, results);
        response = response with { ToolCalls = response.ToolCalls!.Where(call => call.Name != PlanToolDeclaration.Name).ToArray() };
        return response.HasToolCalls
            ? await ProcessActionCallsAsync(response, request, context, results, iteration, totalBudgetTokens, correction, language, streamSink, ct, plans)
            : await ContinueAfterPlanOnlyRoundAsync(response, plans, language, streamSink, ct);
    }

    /// <summary>
    /// A round that holds only <c>present_plan</c> executes no action. The plan acknowledgement goes back
    /// to the model so that the next round can call the action tools, inside the turn's iteration limit.
    /// </summary>
    private async Task<ToolRoundResult> ContinueAfterPlanOnlyRoundAsync(AiResponse response, IReadOnlyList<AiToolCallResult> plans,
        string? language, Func<AiStreamEvent, Task>? streamSink, CancellationToken ct)
    {
        if (response.ConversationContext is null)
            return new ToolRoundResult(MessageResponse(language));
        ct.ThrowIfCancellationRequested();
        var continued = await ai.IntentService.ContinueWithToolResultsAsync(response.ConversationContext, plans, streamSink, ct);
        if (continued.IsSuccess)
            return new ToolRoundResult(continued.Value);
        LogContinueWithToolResultsFailed(logger, continued.Error);
        return new ToolRoundResult(response, ProviderError: new AppError(
            continued.ErrorCode ?? ErrorMessages.AiUnavailable.Code, continued.Error));
    }

    private static void TracePlanCalls(AiResponse response, IReadOnlyList<AiToolCallResult> plans, ToolExecutionAccumulator results)
    {
        if (results.EvalTrace is not { } trace) return;
        foreach (var call in response.ToolCalls!.Where(call => call.Name == PlanToolDeclaration.Name))
            trace.Record(call, plans.FirstOrDefault(plan => plan.Id == call.Id), null, omitData: false);
    }

    private async Task<List<AiToolCallResult>> AcceptPlanAsync(AiResponse response, ProcessUserChatCommand request,
        ToolExecutionAccumulator results)
    {
        var acknowledgements = new List<AiToolCallResult>();
        foreach (var call in response.ToolCalls!.Where(call => call.Name == PlanToolDeclaration.Name))
        {
            var accepted = false;
            if (results.Recording is { Plan: null } recording && results.ToolResults.Count == 0
                && response.ToolCalls!.Any(action => action.Name != PlanToolDeclaration.Name))
            {
                try
                {
                    var draft = call.Args.Deserialize<AstraPlanDraft>(AstraPlan.JsonOptions);
                    if (draft is not null && new AstraPlanDraftValidator().Validate(draft).IsValid
                        && draft.Steps.All(step => ai.ToolRegistry.GetTool(step.Tool) is not null)
                        && draft.Steps.Count(step => step.Phase == "execution") >= 1
                        && (draft.Steps.Count(step => step.Phase == "execution") >= 2 || draft.Steps.Any(step => step.Phase == "approval"))
                        && draft.Steps.Where(step => step.Phase == "approval").All(step => ai.ToolRegistry.GetTool(step.Tool)?.IsReadOnly == false))
                    {
                        var created = AstraPlan.Create(draft.Title, draft.Steps.Select((step, index) =>
                            new AstraPlanStep((index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), step.Text, "pending", step.Tool, step.Phase)).ToArray());
                        if (created.IsSuccess)
                        {
                            recording.Plan = created.Value;
                            accepted = true;
                            if (request.StreamSink is { } sink) await sink(new("plan", Plan: recording.Plan));
                        }
                    }
                }
                catch (JsonException)
                {
                    logger.LogWarning("Invalid model plan arguments for call {CallId}", call.Id);
                }
            }
            acknowledgements.Add(new(call.Name, call.Id, accepted, null, null, accepted ? null : "Plan was not accepted. Continue only with the requested action tools."));
        }
        return acknowledgements;
    }

    private static async Task StartPlanCallsAsync(IReadOnlyList<AiToolCall> calls, ProcessUserChatCommand request, ToolExecutionAccumulator results)
    {
        foreach (var call in calls)
        {
            if (results.Recording?.Plan is not { } plan) return;
            var step = plan.Steps.FirstOrDefault(step => step.Tool == call.Name && step.Phase == "execution" && step.ToolCallId is null);
            if (step is null) continue;
            await ChangePlanStepAsync(request, results, step.Id, "running", call.Id);
            var approval = results.Recording.Plan!.Steps.FirstOrDefault(step => step.Tool == call.Name && step.Phase == "approval" && step.ToolCallId is null);
            if (approval is not null) results.Recording.Plan = results.Recording.Plan.Change(approval.Id, "pending", call.Id);
        }
    }

    private static async Task CompletePlanCallsAsync(IReadOnlyDictionary<string, ToolCallOutcome> outcomes, ProcessUserChatCommand request,
        ToolExecutionAccumulator results)
    {
        if (results.Recording?.Plan is not { } plan) return;
        foreach (var step in plan.Steps.Where(step => step.Phase == "execution" && step.ToolCallId is not null))
        {
            if (!outcomes.TryGetValue(step.ToolCallId!, out var outcome)) continue;
            await ChangePlanStepAsync(request, results, step.Id, outcome.ToolResult.Success ? "done" : "failed");
            foreach (var approval in results.Recording.Plan!.Steps.Where(item => item.Phase == "approval" && item.ToolCallId == step.ToolCallId))
                await ChangePlanStepAsync(request, results, approval.Id, outcome.PendingOperation is not null ? "pending" : outcome.ToolResult.Success ? "done" : "failed",
                    pendingId: outcome.PendingOperation?.Id);
        }
    }

    private static async Task CompletePlanCallAsync(AiToolCall call, ToolCallOutcome outcome, ProcessUserChatCommand request, ToolExecutionAccumulator results)
    {
        if (results.Recording?.Plan is null) return;
        await results.PlanGate.WaitAsync();
        try
        {
            await CompletePlanCallsAsync(new Dictionary<string, ToolCallOutcome> { [call.Id] = outcome }, request, results);
        }
        finally
        {
            results.PlanGate.Release();
        }
    }

    private static async Task ChangePlanStepAsync(ProcessUserChatCommand request, ToolExecutionAccumulator results, string id, string status,
        string? callId = null, Guid? pendingId = null)
    {
        var plan = results.Recording!.Plan!;
        var before = plan.Steps.Single(step => step.Id == id);
        plan = plan.Change(id, status, callId, pendingId);
        results.Recording.Plan = plan;
        var after = plan.Steps.Single(step => step.Id == id);
        if (before != after && request.StreamSink is { } sink)
            await sink(new("plan_step", PlanId: plan.Id, PlanStep: after));
    }

    private static async Task FinishPlanAsync(ProcessUserChatCommand request, ToolExecutionAccumulator results)
    {
        if (results.Recording?.Plan is not { } plan) return;
        foreach (var step in plan.Steps.Where(step => step.Status == "running" || step.Status == "pending" && step.PendingOperationId is null))
            await ChangePlanStepAsync(request, results, step.Id, "skipped");
    }
}
