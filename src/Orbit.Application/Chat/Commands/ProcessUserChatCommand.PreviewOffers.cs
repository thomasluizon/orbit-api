using Orbit.Application.Common;
using Orbit.Domain.Models;

namespace Orbit.Application.Chat.Commands;

public partial class ProcessUserChatCommandHandler
{
    private async Task<(ResponseCards Cards, bool Offered)> ComposePreviewOfferAsync(ProcessUserChatCommand request,
        ChatContext context, ResponseCards cards, ToolExecutionAccumulator results, CancellationToken ct)
    {
        if (!ProactiveProposal.Enabled(request.ClientContext) || offerStore is null || results.AskBlock is not null
            || results.PolicyDenials.Count > 0 || results.OperationResults.Any(operation =>
                operation.Status is not (AgentOperationStatus.Succeeded or AgentOperationStatus.PendingConfirmation)
                || operation.Status == AgentOperationStatus.PendingConfirmation && !ProactiveProposal.Supports(operation.OperationId)))
            return (cards, false);
        var preview = results.PendingOperations.FirstOrDefault(pending => pending.Items is { Count: > 0 }
            && pending.PreviewFingerprint is not null);
        if (preview is null) return (cards, false);
        var conversation = request.ClientContext!.ConversationId!.Value;
        var dismissed = await offerStore.DismissedAsync(request.UserId, conversation, ct);
        var candidates = PreviewOffers(preview, context).Where(fact => !dismissed.Contains(fact.Action.OfferKey)).ToArray();
        if (candidates.Length == 0) return (cards, false);
        var issued = await offerStore.IssuedAsync(request.UserId, conversation, ct);
        var selected = await SelectOfferAsync(request, candidates, issued, context, ct, results.PendingOperations);
        var rejected = selected?.DismissedOfferKeys?.Where(key => issued.Any(offer => offer.Action.OfferKey == key))
            .Distinct(StringComparer.Ordinal).ToArray() ?? [];
        if (rejected.Length > 0) await offerStore.DismissAsync(request.UserId, conversation, rejected, ct);
        var candidate = candidates.SingleOrDefault(fact => fact.Id == selected?.CandidateId);
        if (candidate is null || selected?.Interpretation != (candidate.Family == "calendar" ? "dated_event" : "aim")
            || await execution.UserDateService.GetUserTodayAsync(request.UserId, ct) != candidate.LocalDate
            || (await offerStore.DismissedAsync(request.UserId, conversation, ct)).Contains(candidate.Action.OfferKey))
            return (cards, false);
        await offerStore.IssueAsync(request.UserId, conversation, candidate, ct);
        var index = results.PendingOperations.FindIndex(pending => pending.Id == preview.Id);
        results.PendingOperations[index] = preview with { NextAction = candidate.Action };
        return (cards, true);
    }

    private static List<AstraOfferFact> PreviewOffers(PendingAgentOperation preview, ChatContext context)
    {
        var pt = LocaleHelper.IsPortuguese(context.Language);
        AstraOfferFact Offer(string key, string family, string evidence, AstraNextAction action) =>
            new(key, family, evidence, action, preview.Id, preview.PreviewFingerprint!, context.UserToday)
            { PendingOperationId = preview.Id, SourceKind = "pendingOperation" };
        var offers = new List<AstraOfferFact>();
        var hasDate = preview.Items!.SelectMany(item => item.Fields).Any(field => field.Field is "due_date" or "deadline" && field.ProposedValue is not null);
        if (context.HasProAccess && context.EnabledFeatureFlags.Contains(FeatureFlagKeys.CalendarIntegration)
            && context.User?.GoogleAccessToken is null && hasDate)
            offers.Add(Offer("calendar:calendar-sync", "calendar", "An editable dated draft and an available disconnected Google Calendar connection; requires dated_event interpretation.",
                new("calendar:calendar-sync", "openSurface", pt ? "Conectar Google Agenda" : "Connect Google Calendar", Surface: "calendar-sync")));
        if (preview.CapabilityId != AgentCapabilityIds.GoalsWrite
            && !preview.Items!.SelectMany(item => item.Fields).Any(field => field.Field is "target_value" or "goal_type"))
            offers.Add(Offer("aim:account", "aim", "The editable draft and complete active-goal snapshot support a goal only for an expressed aim with no equivalent active goal.",
            new("aim:account", "continueChat", pt ? "Planejar esta meta" : "Plan this goal", Prompt: pt
                ? "Sugira uma meta para o que está nesta prévia."
                : "Suggest a goal for what is in this preview.")));
        return offers;
    }
}
