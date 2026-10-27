using System.Text.Json;
using Orbit.Application.Chat.Tools;
using Orbit.Application.Common;
using Orbit.Domain.Models;

namespace Orbit.Application.Chat.Commands;

public partial class ProcessUserChatCommandHandler
{
    private IReadOnlyList<IAiTool> CompositionTools(ProcessUserChatCommand request, ChatContext context) =>
        UsesComposition(request, context) && IsCoachingRequest(request.Message) ? [] : ai.ToolRegistry.GetAll();

    private static bool HasTerminalCompositionSurface(ToolExecutionAccumulator results, bool completedMetrics) =>
        results.AskBlock is not null || completedMetrics;

    private static bool IsCoachingRequest(string message) =>
        Mentions(message, "help me|me ajude|me ajuda|how can I|como posso")
        && Mentions(message, "easier|easily|facilidade|motivation|motiva[cç][aã]o|consistent|consist[eê]ncia")
        && !RequestsWrite(message);

    private static bool RequestsWrite(string message) => Mentions(message,
        "create|add|edit|update|delete|remove|log|skip|reschedule|schedule|set|change|move|complete|mark|save|agende|marque|mude|defina|salve|conclua|crie|criar|adicione|adicionar|edite|editar|altere|alterar|exclua|excluir|remova|remover|registre|registrar|pule|pular|reagende|reagendar");

    private static bool IsPlanHelp(string message) =>
        Mentions(message, "where|onde|how|como") && Mentions(message, "features?|recursos?|funcionalidades?")
        && Mentions(message, "plan|plano|subscription|assinatura") && !RequestsWrite(message);

    private AiResponse NormalizeCompositionReadPlan(AiResponse response, ProcessUserChatCommand request, ChatContext context)
    {
        if (!UsesComposition(request, context) || request.BlockAnswer is not null) return response;
        if (IsCoachingRequest(request.Message))
            return response with
            {
                ToolCalls = null,
                TextMessage = response.HasToolCalls || string.IsNullOrWhiteSpace(response.TextMessage)
                    ? ErrorCopy.Resolve("chat.read.coaching", LocaleHelper.IsPortuguese(context.Language)) : response.TextMessage
            };
        if (response.ToolCalls is not { Count: > 0 }) return response;
        var explicitTools = response.ToolCalls.Select(call => call.Name).ToHashSet(StringComparer.Ordinal);
        var calls = response.ToolCalls.Select(call => (Original: call, Routed: RouteCompositionRead(call, request.Message)))
            .Where(pair => pair.Original.Name == pair.Routed.Name || !explicitTools.Contains(pair.Routed.Name))
            .Select(pair => pair.Routed).ToArray();
        return response with { ToolCalls = calls };
    }

    private AiToolCall RouteCompositionRead(AiToolCall call, string message)
    {
        if (call.Args.ValueKind != JsonValueKind.Object) return call;
        if (call.Name == "get_habit_month" && Mentions(message, "calendar|calend[aá]rio|events?|eventos?")
            && !Mentions(message, "habit calendar|calend[aá]rio de h[aá]bitos|my month|meu m[eê]s")
            && Mentions(message, "provider|provedor|connection|conex[aã]o|sync|sincroniza[cç][aã]o|Google|events?|eventos?")
            && ai.ToolRegistry.GetTool("get_calendar_overview") is { IsReadOnly: true })
            return call with
            {
                Name = "get_calendar_overview",
                Args = JsonSerializer.SerializeToElement(call.Args.EnumerateObject()
                    .Where(property => property.Name != "language").ToDictionary(property => property.Name, property => property.Value))
            };
        if (call.Name == "get_habit_month" && Mentions(message, "habits?|h[aá]bitos?")
            && !Mentions(message, "month|m[eê]s|calendar|calend[aá]rio") && !RequestsWrite(message)
            && ai.ToolRegistry.GetTool("query_habits") is { IsReadOnly: true })
            return call with { Name = "query_habits", Args = JsonSerializer.SerializeToElement(new { orbit_read_complete = true }) };
        if (call.Name == "get_subscription_overview" && Mentions(message, "profile|perfil|settings|configura[cç][oõ]es")
            && !Mentions(message, "plan|plano|subscription|assinatura")
            && ai.ToolRegistry.GetTool("get_profile") is { IsReadOnly: true })
            return call with
            {
                Name = "get_profile",
                Args = JsonSerializer.SerializeToElement(call.Args.EnumerateObject()
                    .Where(property => property.Name == TerminalReadDirective.CompleteArgument)
                    .ToDictionary(property => property.Name, property => property.Value))
            };
        if (call.Name == "get_subscription_overview" && IsPlanHelp(message)
            && ai.ToolRegistry.GetTool("describe_feature") is { IsReadOnly: true })
            return call with
            {
                Name = "describe_feature",
                Args = JsonSerializer.SerializeToElement(new { feature_key = "paygate", orbit_read_complete = true })
            };
        return call;
    }

    private static string? PlanHelpText(ProcessUserChatCommand request, ToolExecutionAccumulator results, string? language) =>
        IsPlanHelp(request.Message) && results.LastSuccessfulOperation("describe_feature") is not null
        && results.RelatedSurfaces.Contains("subscriptions")
            ? ErrorCopy.Resolve("chat.read.planHelp", LocaleHelper.IsPortuguese(language)) : null;

    private async Task<bool> CompleteFocusedMetricsReadAsync(bool allowed, ProcessUserChatCommand request, ChatContext context,
        ToolExecutionAccumulator results, IReadOnlyList<AiToolCall> calls, CancellationToken ct)
    {
        if (!allowed || !UsesComposition(request, context) || request.BlockAnswer is not null || RequestsWrite(request.Message)
            || !Mentions(request.Message, "metrics|m[eé]tricas")
            || calls.Count != 1 || calls[0].Name != "query_habits"
            || results.LastSuccessfulOperation("get_habit_metrics") is not null
            || ai.ToolRegistry.GetTool("get_habit_metrics") is not { IsReadOnly: true }
            || !results.QueryReads.TryGetValue(calls[0].Id, out var lookup)
            || lookup.Read.TotalCount != 1 || lookup.Block is not HabitListCard { Items.Count: 1 } habits
            || !Guid.TryParse(habits.Items[0].Id, out var id))
            return false;
        var scope = lookup.Read.Scope.EnumerateObject()
            .Where(property => property.Name is "relative_period" or "period_count" or "date_from" or "date_to")
            .ToDictionary(property => property.Name, property => property.Value);
        scope["habit_id"] = JsonSerializer.SerializeToElement(id);
        var call = new AiToolCall("get_habit_metrics", calls[0].Id + "_metrics", JsonSerializer.SerializeToElement(scope));
        var outcomes = await ExecuteToolCallsAsync([call], request, results, context.Language, ct);
        RecordToolRound(new AiResponse { ToolCalls = [call] }, [call], outcomes, results, false);
        return true;
    }

    private static bool HasUnansweredReadSubject(string message, ToolExecutionAccumulator results)
    {
        (string Pattern, string[] Tools)[] subjects =
        [
            ("goals?|metas?|objetivos?", ["query_goals", "review_goals"]),
            ("profile|perfil|settings|configura[cç][oõ]es", ["get_profile"]),
            ("plan|plano|subscription|assinatura", ["get_subscription_overview", "describe_feature"]),
            ("calendar|calend[aá]rio|events?|eventos?|sync|sincroniza[cç][aã]o", ["get_calendar_overview", "get_habit_month"]),
            ("history|hist[oó]rico|logs?|registros?", ["get_habit_history"]),
            ("notifications?|notifica[cç][oõ]es", ["get_notifications"]),
            ("tags?|etiquetas?", ["list_tags"]),
            ("templates?|modelos?", ["get_checklist_templates"]),
            ("keys?|chaves?", ["get_api_keys"]),
            ("ranking|rank|classifique", ["get_habit_ranking"])
        ];
        return subjects.Any(subject => Mentions(message, subject.Pattern)
            && !results.OperationResults.Any(operation => subject.Tools.Contains(operation.OperationId)));
    }

    private static bool SatisfiesIndependentReadRequest(ProcessUserChatCommand request, ToolExecutionAccumulator results)
    {
        if (RequestsWrite(request.Message) || HasUnansweredReadSubject(request.Message, results)) return false;
        var reads = SelectCompositionReads(results.QueryReads.Values.ToList(), results).ToArray();
        if (reads.Length > 0 && reads.All(read => EmptyCompositionText(read, null) is not null)) return true;
        if (!Mentions(request.Message, "show|list|mostre|liste")) return false;
        return reads.Length > 0 && reads.All(read => MentionsSubject(request.Message, read))
            && reads.Select(read => read.Read.QueryFamily).Distinct().Count() > 1
            && !Mentions(request.Message, "then|depois|based on|com base|compare|comparar");
    }
}
