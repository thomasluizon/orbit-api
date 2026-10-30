using System.Text.Json;
using Microsoft.Extensions.Logging;
using Orbit.Application.Chat.Queries;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;

namespace Orbit.Application.Chat.Commands;

public partial class ProcessUserChatCommandHandler
{
    private async Task<ProcessUserChatCommand> LoadOfferDismissalsAsync(ProcessUserChatCommand request, CancellationToken ct)
    {
        if (!ProactiveProposal.Enabled(request.ClientContext) || offerStore is null) return request;
        var client = request.ClientContext!;
        var conversation = client.ConversationId!.Value;
        if (client.DismissedOfferKeys is { Count: > 0 } keys)
            await offerStore!.DismissAsync(request.UserId, conversation, keys, ct);
        var dismissed = await offerStore!.DismissedAsync(request.UserId, conversation, ct);
        return request with { ClientContext = client with { DismissedOfferKeys = dismissed.ToArray() } };
    }

    private async Task<Result<ProcessUserChatCommand>> PrepareSelectedOfferAsync(ProcessUserChatCommand request, CancellationToken ct)
    {
        if (request.ClientContext?.SelectedOfferKey is not { } key) return Result.Success(request);
        if (!ProactiveProposal.Enabled(request.ClientContext) || offerStore is null)
            return Result.Failure<ProcessUserChatCommand>("offers_not_supported");
        var conversation = request.ClientContext.ConversationId!.Value;
        var issued = await offerStore.IssuedAsync(request.UserId, conversation, ct);
        var selected = issued.SingleOrDefault(offer => offer.Action.OfferKey == key);
        var today = await execution.UserDateService.GetUserTodayAsync(request.UserId, ct);
        if (selected is null || selected.LocalDate != today
            || selected.Action.Kind is not ("preparePreview" or "continueChat") || selected.Action.Prompt is null
            || !await OfferSourceCurrentAsync(request, selected, ct)
            || (await offerStore.DismissedAsync(request.UserId, conversation, ct)).Contains(key))
            return Result.Failure<ProcessUserChatCommand>("stale_offer");
        await offerStore.DismissAsync(request.UserId, conversation, [key], ct);
        return Result.Success(request with { Message = selected.Action.Prompt, SelectedOffer = selected });
    }

    private async Task<bool> OfferSourceCurrentAsync(ProcessUserChatCommand request, AstraOfferFact selected, CancellationToken ct)
    {
        if (selected.PendingOperationId is { } pendingId)
        {
            var pending = execution.PendingOperationStore.GetExecution(request.UserId, pendingId);
            if (pending is null || pending.RequiresReproposal || !execution.PendingOperationStore.IsAwaitingConfirmation(request.UserId, pendingId)) return false;
            var previewer = execution.PendingOperationPreviewer ?? new PendingOperationChangePreviewer(data.HabitRepository,
                data.GoalRepository, data.TagRepository, execution.UserDateService, ai.ToolRegistry);
            var current = await previewer.PreviewAsync(request.UserId, pending.OperationId, pending.Arguments, ct, request.ClientContext);
            return current.IsSuccess && current.Value?.PreviewFingerprint == selected.Revision;
        }
        if (selected.Source is not { } source) return false;
        var fresh = await execution.Mediator.Send(new RefreshReadBlockQuery(request.UserId, source.RefreshToken,
            request.ClientContext, AuthMethod: request.AuthMethod, GrantedScopes: request.GrantedScopes,
            ReadOnlyCredential: request.IsReadOnlyCredential, GamificationOnly: selected.SourceKind == "gamification",
            HabitMonthOnly: selected.SourceKind == "habitMonth"), ct);
        return fresh.IsSuccess && fresh.Value.Read.Revision == selected.Revision;
    }

    private async Task<(ResponseCards Cards, bool Offered)> ComposeOfferAsync(ProcessUserChatCommand request,
        ChatContext context, ResponseCards cards, ToolExecutionAccumulator results, CancellationToken ct)
    {
        try
        {
            return results.PendingOperations.Count > 0
                ? await ComposePreviewOfferAsync(request, context, cards, results, ct)
                : await ComposeOfferCoreAsync(request, context, cards, results, ct);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning(exception, "Optional Astra offer selection failed");
            return (cards, false);
        }
    }

    private async Task<(ResponseCards Cards, bool Offered)> ComposeOfferCoreAsync(ProcessUserChatCommand request,
        ChatContext context, ResponseCards cards, ToolExecutionAccumulator results, CancellationToken ct)
    {
        if (!CanComposeOffer(request, results)) return (cards, false);
        var blocks = cards.Parts?.OfType<ChatBlockPart>().ToArray() ?? [];
        var reads = results.OperationResults.Where(operation => operation.ReadBlock is not null)
            .Select(operation => operation.ReadBlock!).Where(read => blocks.Any(block =>
                ReadBlockTelemetry.Describe(block.Block).Read?.QueryId == read.Read.QueryId)).ToArray();
        var conversation = request.ClientContext!.ConversationId!.Value;
        var issued = await offerStore!.IssuedAsync(request.UserId, conversation, ct);
        if (reads.Length == 0 && issued.Count == 0) return (cards, false);
        var facts = await OfferFactsAsync(request.UserId, reads, context, ct);
        var dismissed = await offerStore!.DismissedAsync(request.UserId, conversation, ct);
        var candidates = ProactiveOfferPlanner.Build(reads, facts, context.Language)
            .Where(candidate => !dismissed.Contains(candidate.Action.OfferKey)).Take(100).ToArray();
        var selected = await SelectOfferAsync(request, candidates, issued, context, ct);
        if (selected is null) return (cards, false);
        var rejected = selected.DismissedOfferKeys?.Where(key => issued.Any(offer => offer.Action.OfferKey == key))
            .Distinct(StringComparer.Ordinal).ToArray() ?? [];
        if (rejected.Length > 0) await offerStore!.DismissAsync(request.UserId, conversation, rejected, ct);
        var candidate = candidates.SingleOrDefault(candidate => candidate.Id == selected.CandidateId);
        if (candidate is null || rejected.Contains(candidate.Action.OfferKey, StringComparer.Ordinal)
            || candidate.Family == "aim" && selected.Interpretation != "aim"
            || candidate.Family == "calendar" && selected.Interpretation != "dated_event") return (cards, false);
        if ((await offerStore!.DismissedAsync(request.UserId, conversation, ct)).Contains(candidate.Action.OfferKey))
            return (cards, false);
        if (await execution.UserDateService.GetUserTodayAsync(request.UserId, ct) != candidate.LocalDate) return (cards, false);
        await offerStore!.IssueAsync(request.UserId, conversation, candidate, ct);
        var updated = blocks.Select(block =>
        {
            var read = ReadBlockTelemetry.Describe(block.Block).Read;
            return read?.QueryId == candidate.QueryId && read.Revision == candidate.Revision
                ? block with { Block = ReadBlockFactory.Attach(block.Block, read with { NextAction = candidate.Action }) } : block;
        }).ToArray();
        return (cards with { Parts = updated, AiMessage = null, ServerComposedText = true }, true);
    }

    private bool CanComposeOffer(ProcessUserChatCommand request, ToolExecutionAccumulator results) =>
        ProactiveProposal.Enabled(request.ClientContext) && offerStore is not null && results.AskBlock is null
        && results.PendingOperations.Count == 0 && results.PolicyDenials.Count == 0 && !results.HasBudgetRecovery
        && results.OperationResults.All(operation => operation.Status == AgentOperationStatus.Succeeded);

    private async Task<ProactiveAccountFacts> OfferFactsAsync(Guid userId, QueryReadBlock[] reads, ChatContext context, CancellationToken ct)
    {
        var ids = reads.SelectMany(read => read.Block switch
        {
            HabitListCard list => list.Items.Select(item => item.Id),
            DaySummaryCard day => (day.Rows ?? []).Select(item => item.Id),
            MetricsCard { HabitId: { } id } => [id.ToString()],
            PeriodInsightCard insight => insight.NeedsAttention.Where(item => item.HabitId.HasValue).Select(item => item.HabitId!.Value.ToString()),
            _ => Array.Empty<string>()
        }).Select(id => Guid.TryParse(id, out var parsed) ? parsed : Guid.Empty).Where(id => id != Guid.Empty).ToArray();
        var habits = ids.Length == 0 ? Array.Empty<Habit>() : await data.HabitRepository.FindAsync(
            habit => habit.UserId == userId && !habit.IsDeleted && ids.Contains(habit.Id), ct);
        var days = ids.Length == 0 ? Array.Empty<HabitScheduleLogDay>() : await execution.ScheduleLogReader.ReadDaysAsync(ids,
            context.UserToday.AddDays(-14), context.UserToday, ct);
        return new ProactiveAccountFacts(context.UserToday,
            await execution.UserDateService.GetUserWeekStartDayAsync(userId, ct), habits,
            context.ActiveGoals, days, context.HasProAccess, context.User?.GoogleAccessToken is not null);
    }

    private sealed record OfferSelection(string? CandidateId, string? Interpretation, IReadOnlyList<string>? DismissedOfferKeys);

    private async Task<OfferSelection?> SelectOfferAsync(ProcessUserChatCommand request, AstraOfferFact[] candidates,
        IReadOnlyList<AstraOfferFact> issued, ChatContext context, CancellationToken ct, IReadOnlyList<PendingAgentOperation>? previews = null)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            var response = await ai.IntentService.SendWithToolsAsync(new AiToolRequest(request.Message,
                ProactiveOfferPlanner.SelectionInstruction, [], request.UserId, History: request.History)
            {
                DynamicContext = JsonSerializer.Serialize(new
                {
                    Candidates = candidates.Select(candidate => new { candidate.Id, candidate.Family, candidate.Evidence, candidate.Action }),
                    PreviouslyIssued = issued.Select(offer => new { offer.Action.OfferKey, offer.Action.Label }),
                    ActiveGoals = context.ActiveGoals.Select(goal => new { goal.Id, goal.Title, goal.Description }),
                    Language = context.Language,
                    PendingPreviews = previews
                })
            }, cancellationToken: deadline.Token);
            if (response.IsFailure || response.Value.HasToolCalls || response.Value.IsTruncated
                || response.Value.TextMessage is not { Length: > 0 and <= 4000 } json) return null;
            return JsonSerializer.Deserialize<OfferSelection>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return null; }
        catch (JsonException) { return null; }
    }
}
