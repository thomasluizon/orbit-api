using System.Globalization;
using System.Text.Json;
using Orbit.Application.Chat.Models;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Models;

namespace Orbit.Application.Chat.Commands;

public partial class ProcessUserChatCommandHandler
{
    /// <summary>
    /// One round of the <c>ai_change_plan</c> path. The compiler runs the change calls of the response as one typed list
    /// beside the reads. A list with an error, or a change call with no change, issues no <c>ask</c> and returns every
    /// error in one typed list. It holds nothing of that list; a preview from before the turn or of an earlier round
    /// stays. The model gets one repair round; a second failed list ends the turn with no preview, and the answer says so.
    /// </summary>
    private async Task<ToolRoundResult> ProcessChangeListRoundAsync(AiResponse response, ProcessUserChatCommand request,
        ChatContext context, ToolExecutionAccumulator results, int iteration, string? language,
        Func<AiStreamEvent, Task>? streamSink, CancellationToken ct)
    {
        var shape = ai.ChangeShape;
        var calls = response.ToolCalls!;
        LogToolCallingIteration(logger, iteration, calls.Count);
        var changeCalls = calls.Where(IsChangeListCall).ToArray();
        var lists = changeCalls.Select(call => (Call: call, Changes: ChangeDeclarations.Read(call, shape))).ToArray();
        var changes = lists.SelectMany(list => list.Changes).ToArray();
        var emptyLists = lists.Where(list => list.Changes.Count == 0).Select(list => list.Call).ToArray();
        var ask = calls.FirstOrDefault(call => call.Name == "ask");
        var askRecords = ask is null ? new HashSet<Guid>()
            : ChangeListCompiler.RecordIds(AskTool.ExecutionArguments(ask.Args)).ToHashSet();
        var others = calls.Where(call => !changeCalls.Contains(call) && call.Name != "ask")
            .OrderBy(call => ai.ToolRegistry.GetTool(call.Name)?.Order ?? int.MaxValue);
        AiToolCall[] writes = [];
        List<AiToolCall> ordered = [];
        var outcomes = new Dictionary<string, ToolCallOutcome>(StringComparer.Ordinal);
        var mark = results.Previews.MarkList();
        var compiled = await new ChangeListCompiler(ai.ToolRegistry).CompileAsync(request.UserId, changes, askRecords,
            async (runnable, token) =>
            {
                writes = [.. runnable.Select(ChangeKinds.Dispatch).Select(call => ProactiveProposal.KeepRequestedSchedule(
                    ProactiveProposal.CompleteCreateReminder(call, request.ClientContext), request.Message))];
                ordered = [.. others.Concat(writes).Select(NormalizeExecutionArguments)];
                foreach (var (id, outcome) in await ExecuteToolCallsAsync(ordered, request, results, language, token))
                    outcomes[id] = outcome;
                return [.. writes.Select(call => ChangeResultOf(call, outcomes[call.Id], results))];
            },
            execution.PendingOperationStore, ct, [.. emptyLists.Select(call => ChangeListCompiler.EmptyList(call.Id))],
            prepared => results.Previews.WithdrawList(request.UserId, execution.PendingOperationStore, mark,
                [.. prepared.Select(preview => preview.Id)]));
        var errors = compiled.Errors;
        if (!compiled.IsSuccess)
            HoldNothing(writes, outcomes, errors, results);
        else if (changes.Length > 0)
        {
            results.ChangesNotPrepared = false;
            await BuildChangePlanAsync(request, results, changeCalls, changes, compiled.Results);
        }
        if (ask is not null)
        {
            var asked = NormalizeExecutionArguments(ask with { Args = AskTool.ExecutionArguments(ask.Args) });
            ordered.Add(asked);
            outcomes[asked.Id] = errors.Count > 0
                ? TypedChangeOutcome(new ProposedChange(asked.Id, asked.Name, asked.Args), ChangeListCompiler.NotPrepared,
                    new { error = ChangeListCompiler.NotPrepared, errors })
                : await ExecuteAskCallAsync(asked, request, results, ct);
        }
        foreach (var waiting in compiled.Waiting)
            outcomes[waiting.Change] = TypedChangeOutcome(changes.First(change => change.Id == waiting.Change), waiting.Code,
                new { error = waiting.Code, errors = new[] { waiting } });
        var unprepared = changes.Where(change => !outcomes.ContainsKey(change.Id))
            .Concat(emptyLists.Select(call => new ProposedChange(call.Id, call.Name, call.Args))).ToArray();
        foreach (var change in unprepared)
            outcomes[change.Id] = TypedChangeOutcome(change, ChangeListCompiler.NotPrepared,
                new { error = ChangeListCompiler.NotPrepared, errors });

        var traced = ordered.Where(call => writes.All(write => write.Id != call.Id)).Concat(emptyLists)
            .Concat(changes.Select(change => new AiToolCall(change.Kind, change.Id, change.Arguments))).ToArray();
        RecordToolRound(response with { ToolCalls = traced }, ordered, outcomes, results, isCorrection: false);
        RecordReadOfferIntents(response, request, results);
        results.CapturePreviewDiscussionText(response.TextMessage);

        if (errors.Count > 0 && results.FailedChangeLists > 1)
            return new ToolRoundResult(new AiResponse());
        if (BulkNoChangesMessage(results, language) is { } noChanges)
            return new ToolRoundResult(new AiResponse { TextMessage = noChanges });
        if (await EndsWithAskAsync(response, ordered, outcomes, request, context, results, ct))
            return new ToolRoundResult(response with { ToolCalls = null, ReportedTokenCount = 0, RequestBudgetTokenCount = 0 });
        var toolResults = ChangeListToolResults(calls, shape, changes, outcomes, errors);
        if (errors.Count == 0 && (CanCompleteChangeList(response, changeCalls, changes, compiled)
            || changeCalls.Length == 0 && CanCompleteToolRound(response, request, context, results, iteration, ordered, outcomes, toolResults)))
        {
            ct.ThrowIfCancellationRequested();
            return new ToolRoundResult(response with { ToolCalls = null, ReportedTokenCount = 0, RequestBudgetTokenCount = 0 });
        }

        ct.ThrowIfCancellationRequested();
        var continued = await ai.IntentService.ContinueWithToolResultsAsync(response.ConversationContext!, toolResults, streamSink, ct);
        if (continued.IsSuccess)
            return new ToolRoundResult(continued.Value);
        LogContinueWithToolResultsFailed(logger, continued.Error);
        if (RecoverBudgetDeniedContinuation(continued.ErrorCode, request, results, language, ct) is { } recovery)
            return new ToolRoundResult(recovery);
        return new ToolRoundResult(response, ProviderError: new AppError(
            continued.ErrorCode ?? ErrorMessages.AiUnavailable.Code, continued.Error));
    }

    /// <summary>
    /// The declarations of the <c>ai_change_plan</c> path: each read tool, <c>ask</c> and <c>revise_pending_changes</c> as
    /// before on this path, and the change kinds in the shape of the setting. <c>present_plan</c> is not declared. The
    /// wrapper holds the completion and offer controls once, at its top level.
    /// </summary>
    private List<object> ChangeListDeclarations(IReadOnlyList<Orbit.Application.Chat.Tools.IAiTool> tools,
        AgentClientContext? client, ChatContext context, bool composition)
    {
        var declarations = tools.Where(tool => tool.IsReadOnly).Select(tool => (object)new
        {
            name = tool.Name,
            description = context.Policy.ToolDescription(tool),
            parameters = context.Policy.ToolSchema(tool, client, composition)
        }).ToList();
        var kinds = tools.Where(ChangeKinds.IsKind).OrderBy(tool => tool.Name, StringComparer.Ordinal).ToArray();
        var offers = ProactiveProposal.Enabled(client);
        if (ai.ChangeShape == ChangeDeclarationShape.Kinds)
            declarations.AddRange(kinds.Select(tool => (object)new
            {
                name = tool.Name,
                description = context.Policy.ToolDescription(tool),
                parameters = ChatToolSchema(tool, client, composition, source: ChangeDeclarations.WithPlanTitle(tool.GetParameterSchema()))
            }));
        else if (kinds.Length > 0)
            declarations.Add(ChangeDeclarations.Wrapper(kinds.Select(tool => (tool.Name, context.Policy.ToolDescription(tool),
                    offers && ProactiveProposal.Supports(tool.Name) ? ProactiveProposal.Schema(tool.GetParameterSchema()) : tool.GetParameterSchema())),
                schema => TerminalWriteDirective.WithCompletionArgument(offers ? ProactiveOfferDismissal.Schema(schema) : schema)));
        return declarations;
    }

    /// <summary>
    /// A call of the change list: the <c>propose_changes</c> call of the wrapper shape, and each call of a write tool or
    /// of an unknown tool. A write tool that is no change kind gets <c>unknown_kind</c> from the compiler.
    /// </summary>
    private bool IsChangeListCall(AiToolCall call) => call.Name != "ask"
        && (ChangeDeclarations.IsChangeCall(call, ai.ChangeShape, ai.ToolRegistry)
            || ai.ToolRegistry.GetTool(call.Name) is not { IsReadOnly: true });

    /// <summary>The preview or the typed error of one change, from the outcome of its write tool call.</summary>
    private static ChangeResult ChangeResultOf(AiToolCall call, ToolCallOutcome outcome, ToolExecutionAccumulator results)
    {
        if (outcome.ToolResult.Payload is HabitSetupMissingFieldResult missing)
            return new(call.Id, null, new ChangeError(call.Id, missing.MissingField, ChangeListCompiler.MissingField,
                $"The new habit {missing.Title} needs a value for {missing.MissingField}."));
        if (outcome.PendingOperation is { } preview)
            return new(call.Id, preview, null);
        if (outcome.ToolResult.Success && (results.MergedCallIds.Contains(call.Id)
            || outcome.OperationResult is { Status: AgentOperationStatus.Succeeded }))
            return new(call.Id, null, null);
        return new(call.Id, null, ChangeListCompiler.ErrorOf(call.Id, outcome.OperationResult, outcome.ToolResult, outcome.FailureKey));
    }

    /// <summary>
    /// The turn holds nothing of a change list with an error, and the compiler withdrew what the list prepared. Each
    /// change returns the error list with no card. The clarification of a create with a missing field stays, so an
    /// <c>ask</c> can bind to it.
    /// </summary>
    private static void HoldNothing(IReadOnlyList<AiToolCall> writes, Dictionary<string, ToolCallOutcome> outcomes,
        IReadOnlyList<ChangeError> errors, ToolExecutionAccumulator results)
    {
        results.FailedChangeLists++;
        results.ChangesNotPrepared = true;
        foreach (var call in writes)
            outcomes[call.Id] = new ToolCallOutcome(new AiToolCallResult(call.Name, call.Id, false, null, null,
                ChangeListCompiler.NotPrepared, new { error = ChangeListCompiler.NotPrepared, errors }),
                null, null, null, null, FailureKey: ChangeListCompiler.NotPrepared);
    }

    private static ToolCallOutcome TypedChangeOutcome(ProposedChange change, string code, object payload) => new(
        new AiToolCallResult(change.Kind, change.Id, false, null, null, code, payload), null, null, null, null, FailureKey: code);

    /// <summary>
    /// A change list ends the turn with its previews alone when it covers the whole request: the response holds only
    /// change calls that declare completion, no change waits for an answer, and the list prepared a preview.
    /// </summary>
    private static bool CanCompleteChangeList(AiResponse response, IReadOnlyList<AiToolCall> changeCalls,
        IReadOnlyList<ProposedChange> changes, ChangeListOutcome compiled) =>
        changeCalls.Count == response.ToolCalls!.Count && changes.Count > 0 && compiled.Waiting.Count == 0
        && compiled.Previews.Count > 0 && TerminalWriteDirective.IsComplete(response);

    /// <summary>
    /// The results that the model gets for one response, one for each call. A wrapper call gets the result of its whole
    /// list; each other call gets its own.
    /// </summary>
    private static List<AiToolCallResult> ChangeListToolResults(IReadOnlyList<AiToolCall> calls, ChangeDeclarationShape shape,
        IReadOnlyList<ProposedChange> changes, IReadOnlyDictionary<string, ToolCallOutcome> outcomes, IReadOnlyList<ChangeError> errors)
    {
        var results = new List<AiToolCallResult>(calls.Count);
        foreach (var call in calls)
        {
            if (shape == ChangeDeclarationShape.Wrapper && call.Name == ChangeDeclarations.WrapperName)
            {
                results.Add(WrapperResult(call, ChangeDeclarations.Read(call, shape), outcomes, errors));
                continue;
            }
            results.Add(outcomes.TryGetValue(call.Id, out var outcome)
                ? outcome.ToolResult with { Name = call.Name, Id = call.Id }
                : new AiToolCallResult(call.Name, call.Id, false, null, null, ChangeListCompiler.NotPrepared,
                    new { error = ChangeListCompiler.NotPrepared, errors }));
        }
        return results;
    }

    private static AiToolCallResult WrapperResult(AiToolCall call, IReadOnlyList<ProposedChange> own,
        IReadOnlyDictionary<string, ToolCallOutcome> outcomes, IReadOnlyList<ChangeError> errors)
    {
        if (errors.Count > 0)
            return new AiToolCallResult(call.Name, call.Id, false, null, null, ChangeListCompiler.NotPrepared,
                new { error = ChangeListCompiler.NotPrepared, errors });
        var waiting = own.Where(change => outcomes.TryGetValue(change.Id, out var outcome)
            && outcome.FailureKey == ChangeListCompiler.WaitsForAnswer).ToArray();
        return new AiToolCallResult(call.Name, call.Id, true, null, null, null, new
        {
            status = "awaiting_confirmation",
            changes = own.Where(change => outcomes.ContainsKey(change.Id) && !waiting.Contains(change))
                .Select(change => new { change = change.Id, result = outcomes[change.Id].ToolResult.Payload }).ToArray(),
            waiting = waiting.SelectMany(change => WaitingErrors(outcomes[change.Id])).ToArray()
        });
    }

    private static IEnumerable<ChangeError> WaitingErrors(ToolCallOutcome outcome) =>
        outcome.ToolResult.Payload is { } payload
        && JsonSerializer.SerializeToElement(payload).TryGetProperty("errors", out var list)
            ? list.Deserialize<ChangeError[]>() ?? [] : [];

    /// <summary>
    /// The plan block of a change list with two or more previews. The model gives the title; the server builds each step
    /// from its change: the titles of the records in its preview, and a link to that preview.
    /// </summary>
    private static async Task BuildChangePlanAsync(ProcessUserChatCommand request, ToolExecutionAccumulator results,
        IReadOnlyList<AiToolCall> changeCalls, IReadOnlyList<ProposedChange> changes, IReadOnlyList<ChangeResult> prepared)
    {
        var previews = prepared.Where(result => result.Preview is not null).ToArray();
        if (results.Recording is not { Plan: null } recording || previews.Length < 2
            || ChangeDeclarations.PlanTitle(changeCalls) is not { } title || !AiPlan.IsShortText(title))
            return;
        var steps = previews.Select((result, index) => new AiPlanStep((index + 1).ToString(CultureInfo.InvariantCulture),
            StepText(result.Preview!), "pending", changes.First(change => change.Id == result.ChangeId).Kind, "approval",
            ToolCallId: result.ChangeId, PendingOperationId: result.Preview!.Id)).ToArray();
        var created = AiPlan.Create(title, steps);
        if (created.IsFailure)
            return;
        recording.Plan = created.Value;
        if (request.StreamSink is { } sink)
            await sink(new("plan", Plan: recording.Plan));
    }

    /// <summary>
    /// The text of one plan step: the titles of the records in its preview, in preview order, as many whole titles as
    /// fit in 40 characters. A longer first title is cut to 40 characters.
    /// </summary>
    internal static string StepText(PendingAgentOperation preview)
    {
        var names = (preview.Items ?? []).Select(item => new string([.. item.EntityName.Where(character => !char.IsControl(character))]).Trim())
            .Where(name => name.Length > 0).Distinct(StringComparer.Ordinal).ToArray();
        if (names.Length == 0)
            return string.Empty;
        var text = names[0].Length <= 40 ? names[0] : names[0][..40].TrimEnd();
        foreach (var name in names.Skip(1))
        {
            if (text.Length + 2 + name.Length > 40) break;
            text += ", " + name;
        }
        return text;
    }
}
