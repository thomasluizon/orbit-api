using System.Text.RegularExpressions;
using Orbit.Application.Common;
using Orbit.Application.Chat.Grounding;
using Orbit.Application.Habits.Queries;
using Orbit.Domain.Enums;
using Orbit.Domain.Models;

namespace Orbit.Application.Chat.Commands;

public partial class ProcessUserChatCommandHandler
{
    private static bool UsesComposition(ProcessUserChatCommand request, ChatContext context) =>
        request.ClientContext?.SupportsParts == true
        && context.EnabledFeatureFlags.Contains(FeatureFlagKeys.AstraBlockFirst);

    private static bool CanCompleteComposition(ProcessUserChatCommand request, ChatContext context, ToolExecutionAccumulator results) =>
        UsesComposition(request, context) && request.BlockAnswer is null && results.OperationResults.Count > 0
        && results.OperationResults.All(operation => operation.Status == AgentOperationStatus.Succeeded
            && (operation.ReadBlock is not null || operation.Payload is HabitRankingResponse));

    private ChatResponse CompleteCompositionWithoutNarrative(ProcessUserChatCommand request, ChatContext context, ToolExecutionAccumulator results)
    {
        var cards = ComposeAnswer(FollowUpDirective.Parse(null), false, GetUserLanguage(context.User), request, context, results);
        CaptureResponseCardEvents(request, context, cards);
        return new ChatResponse(cards.AiMessage, [], results.OperationResults, CorrelationId: request.CorrelationId,
            RelatedSurfaces: results.RelatedSurfaces, Parts: cards.Parts)
        { GroundingFacts = TurnFactSet.FromToolResults(results.ToolResults) };
    }

    private static ChatResponse ComposeCachedFaq(string answer, ProcessUserChatCommand request, bool composition)
    {
        var text = composition ? ShortText(answer, 3) : answer;
        return new ChatResponse(text, [], CorrelationId: request.CorrelationId,
            Parts: composition ? [new ChatTextPart(text)] : null);
    }

    private ResponseCards ComposeAnswer(ParsedResponseDirectives parsed, bool truncated, string? language,
        ProcessUserChatCommand request, ChatContext context, ToolExecutionAccumulator results)
    {
        try
        {
            return ComposeAnswerCore(parsed, truncated, language, request, context, results);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogResponseCardBuildThrew(logger, ex);
            AnalyticsCapture.SafeCaptureUserEvent(execution.ProductAnalytics, logger, request.UserId,
                context.User?.Plan.ToString(), "astra_answer_composed", new Dictionary<string, object>
                {
                    ["family"] = "state",
                    ["leading_kind"] = "none",
                    ["external_text_count"] = 1,
                    ["fallback_reason"] = "build_failure",
                    ["build_failure"] = true,
                    ["duplicate_suppression"] = 0
                });
            var text = ToolFailureMessage(language);
            return new ResponseCards(text, Parts: [new ChatTextPart(text)]) { ServerComposedText = true };
        }
    }

    private ResponseCards ComposeAnswerCore(ParsedResponseDirectives parsed, bool truncated, string? language,
        ProcessUserChatCommand request, ChatContext context, ToolExecutionAccumulator results)
    {
        var candidates = results.OperationResults
            .Where(operation => operation.Status == AgentOperationStatus.Succeeded && operation.ReadBlock is not null)
            .Select(operation => operation.ReadBlock!)
            .DistinctBy(read => read.Read.QueryId).ToList();
        var hasOutcome = results.PendingOperations.Count > 0 || results.ActionResults.Count > 0;
        var built = BuildCompositionParts(candidates, request, context, results, hasOutcome, language);
        foreach (var read in candidates.Where(read => read.Read.TotalCount == 0))
            results.AddRelatedSurface(SurfaceForRead(read));
        var parts = built.Parts;
        var missingProjection = !hasOutcome && results.OperationResults.Any(operation =>
            operation.Status == AgentOperationStatus.Succeeded && ReadBlockFactory.Supports(operation.OperationId)
            && operation.ReadBlock is null);
        var status = CompositionStatus(results, hasOutcome, missingProjection, truncated, language);
        if (status is not null)
            parts.Add(new ChatTextPart(ShortText(status, 1)));
        else if (parts.Count == 0 && !hasOutcome)
            parts.Add(new ChatTextPart(ShortText(parsed.VisibleText, 3,
                Mentions(request.Message, "steps|passos|step by step|passo a passo"))));

        CaptureComposition(request, context, results, candidates, built, missingProjection, hasOutcome);
        return new ResponseCards(JoinPartText(parts), Parts: parts)
        {
            ServerComposedText = candidates.Count > 0 || status is not null || hasOutcome || results.AskBlock is not null
            || results.LastSuccessfulOperation("get_habit_ranking") is not null
        };
    }

    private async Task<(ResponseCards Cards, IReadOnlyList<string>? FollowUps, TurnFactSet Facts)> ComposeFinalReplyAsync(
        ProcessUserChatCommand request, ChatContext context, ToolExecutionAccumulator executionResults,
        ToolLoopResult toolLoopResult, AiResponse aiResponse, GroundingTurn groundingTurn, CancellationToken cancellationToken)
    {
        var composition = UsesComposition(request, context);
        var language = GetUserLanguage(context.User);
        var parsedResponse = FollowUpDirective.Parse(StripJsonWrapper(aiResponse.TextMessage));
        var followUps = CanEmitFollowUps(request, toolLoopResult, executionResults, aiResponse)
            ? parsedResponse.FollowUps
            : null;
        var responseText = parsedResponse.CardText;
        if (aiResponse.IsTruncated)
            responseText = AppendTruncationNotice(responseText, language);
        var cards = composition
            ? ComposeAnswer(parsedResponse, aiResponse.IsTruncated, language, request, context, executionResults)
            : request.ClientContext?.SupportsParts == true
            ? await BuildResponsePartsAsync(parsedResponse, aiResponse.IsTruncated, language,
                request, context, executionResults, cancellationToken)
            : await TryBuildResponseCardsAsync(
                responseText, request, context, executionResults, cancellationToken);
        if (!composition)
            cards = ApplyResolutionToCards(cards, executionResults, language);
        var groundedReply = await ApplyGroundingAsync(cards.ServerComposedText ? null : cards.AiMessage, followUps, executionResults,
            composition ? request with { StreamSink = null } : request,
            composition ? groundingTurn with { Enforce = true } : groundingTurn);
        var aiMessage = cards.ServerComposedText ? cards.AiMessage : groundedReply.Message;
        followUps = groundedReply.FollowUps;
        cards = ApplyGroundingToCards(cards, aiMessage);
        if (!composition)
        {
            cards = EnsureHabitRankingResult(cards, request, context, executionResults);
            cards = EnsureFocusedReadResults(cards, request, context, executionResults);
        }
        aiMessage = cards.AiMessage;
        if (!composition && executionResults.AskBlock is { } askBlock)
            cards = cards with { Parts = (cards.Parts ?? []).Concat(new[] { askBlock }).ToArray() };

        followUps = composition ? ContextualFollowUps(followUps, executionResults) : followUps;
        if (composition)
            AnalyticsCapture.SafeCaptureUserEvent(execution.ProductAnalytics, logger, request.UserId,
                context.User?.Plan.ToString(), "astra_composition_follow_ups", new Dictionary<string, object>
                { ["count"] = followUps?.Count ?? 0 });
        return (cards, followUps, groundedReply.Facts);
    }

    private sealed class CompositionParts
    {
        public List<ChatResponsePart> Parts { get; } = [];
        public string Fallback { get; set; } = "none";
        public int Duplicates { get; set; }
        public bool BuildFailure { get; set; }
    }

    private static CompositionParts BuildCompositionParts(List<QueryReadBlock> candidates,
        ProcessUserChatCommand request, ChatContext context, ToolExecutionAccumulator results, bool hasOutcome, string? language)
    {
        var built = new CompositionParts
        { Duplicates = results.OperationResults.Count(operation => operation.ReadBlock is not null) - candidates.Count };
        var parts = built.Parts;
        if (results.AskBlock is { } ask)
            parts.Add(ask);
        else if (!hasOutcome)
        {
            var ranking = results.LastSuccessfulPayload<HabitRankingResponse>("get_habit_ranking");
            if (ranking is not null)
                AddRankingPart(built, ranking, request, context, language);
            var selected = SelectCompositionReads(candidates, request.Message);
            if (ranking is not null && !RequestsMultipleSubjects(request.Message)) selected = [];
            var seenSubjects = new HashSet<string>(StringComparer.Ordinal);
            var seenFamilies = new HashSet<string>(StringComparer.Ordinal);
            foreach (var read in selected)
            {
                if (!seenSubjects.Add(ReadSubject(read)) || !AllowAdditionalScope(seenFamilies, read, request.Message))
                {
                    built.Duplicates++;
                    continue;
                }
                if (ReadEnabled(read, request.ClientContext, context.EnabledFeatureFlags))
                    parts.Add(new ChatBlockPart(read.Kind, ForClient(read, request.ClientContext, context.EnabledFeatureFlags).Block));
                else
                {
                    built.Fallback = "missing_capability";
                    var text = ReadFallback(read.Block);
                    parts.Add(new ChatTextPart(string.IsNullOrWhiteSpace(text) ? ToolFailureMessage(language) : text));
                }
            }


        }

        return built;
    }

    private static void AddRankingPart(CompositionParts built, HabitRankingResponse ranking,
        ProcessUserChatCommand request, ChatContext context, string? language)
    {
        var card = HabitRankingCardBuilder.Build(ranking);
        built.BuildFailure = card is null;
        if (card is not null && HabitRankingCardBuilder.IsSupported(request.ClientContext)
            && !context.EnabledFeatureFlags.Contains(FeatureFlagKeys.AstraPeriodBlocksDisabled))
            built.Parts.Add(new ChatBlockPart(PartKinds.HabitRanking, card));
        else
        {
            built.Fallback = built.BuildFailure ? "build_failure" : "missing_capability";
            built.Parts.Add(new ChatTextPart(built.BuildFailure ? ToolFailureMessage(language)
                : HabitRankingCardBuilder.Readable(ranking, language)));
        }
    }

    private static List<QueryReadBlock> SelectCompositionReads(List<QueryReadBlock> candidates, string message)
    {
        var explicitReads = candidates.Where(read => MentionsSubject(message, read))
            .OrderByDescending(read => read.Kind is "metrics" or "periodTotal" or "periodComparison").ToList();
        if (explicitReads.Count == 0) return candidates.Take(1).ToList();
        return RequestsMultipleSubjects(message) ? explicitReads : explicitReads.Take(1).ToList();
    }

    private static bool RequestsMultipleSubjects(string message) => message.Contains(',')
        || Mentions(message, "and|also|e|tamb[eé]m|both|two|three|ambos|ambas|dois|duas|tr[eê]s");

    private static string? CompositionStatus(ToolExecutionAccumulator results, bool hasOutcome,
        bool missingProjection, bool truncated, string? language)
    {
        var failed = results.OperationResults.Any(operation => operation.Status is AgentOperationStatus.Failed
            or AgentOperationStatus.Denied or AgentOperationStatus.UnsupportedByPolicy);
        string? status = null;
        if (HasResolutionQuestion(results) && results.AskBlock is null)
            status = AddResolutionQuestion(null, results, language);
        else if (truncated)
            status = AppendTruncationNotice(null, language);
        else if ((failed || missingProjection) && !hasOutcome)
            status = ToolFailureMessage(language);
        return status;
    }

    private void CaptureComposition(ProcessUserChatCommand request, ChatContext context, ToolExecutionAccumulator results,
        List<QueryReadBlock> candidates, CompositionParts built, bool missingProjection, bool hasOutcome)
    {
        var blocks = built.Parts.OfType<ChatBlockPart>().ToArray();
        var family = results.AskBlock is not null ? "clarification" : hasOutcome ? "write"
            : candidates.FirstOrDefault()?.Kind ?? (results.LastSuccessfulOperation("get_habit_ranking") is not null ? "ranking" : "text");
        AnalyticsCapture.SafeCaptureUserEvent(execution.ProductAnalytics, logger, request.UserId,
            context.User?.Plan.ToString(), "astra_answer_composed", new Dictionary<string, object>
            {
                ["family"] = family,
                ["leading_kind"] = blocks.FirstOrDefault()?.Kind ?? "none",
                ["external_text_count"] = built.Parts.OfType<ChatTextPart>().Count(),
                ["fallback_reason"] = missingProjection ? "build_failure" : built.Fallback,
                ["build_failure"] = built.BuildFailure || missingProjection,
                ["duplicate_suppression"] = built.Duplicates
            });
    }

    private static string? JoinPartText(IEnumerable<ChatResponsePart> parts)
    {
        var text = string.Join("\n\n", parts.OfType<ChatTextPart>().Select(part => part.Text).Where(text => !string.IsNullOrWhiteSpace(text)));
        return text.Length == 0 ? null : text;
    }

    private static string ShortText(string? text, int maxLines, bool steps = false) => string.Join("\n",
        (text ?? string.Empty).Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0)
            .Where(line => !line.StartsWith('#'))
            .Select(line => steps ? line : Regex.Replace(line, @"^(?:[-*•]|\d+[.)])\s*", string.Empty,
                RegexOptions.None, TimeSpan.FromSeconds(1)))
            .Select(line => line.Length <= 180 ? line : line[..180].TrimEnd())
            .Take(maxLines));

    private static bool Mentions(string message, string pattern) => Regex.IsMatch(message,
        @"\b(?:" + pattern + @")\b", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));

    private static bool MentionsSubject(string message, QueryReadBlock read) => Mentions(message, read.Kind switch
    {
        "habitList" => "habits?|h[aá]bitos?|schedule|agenda|days?|dias?",
        "goalList" => "goals?|metas?|objetivos?",
        "metrics" => "metrics|m[eé]tricas|progress|progresso|statistics|estat[ií]sticas",
        "streak" => "streak|sequ[eê]ncia|level|n[ií]vel|achievements?|conquistas?",
        "daySummary" => "summary|overview|resumo|day summary|resumo do dia",
        "calendar" => "calendar|calend[aá]rio|events?|eventos?|sync|sincroniza[cç][aã]o",
        "periodInsight" => "recap|retrospectiv[ao]|resumo",
        "periodTotal" => "total|count|quantos|quantas|contagem",
        "periodComparison" => "compar[a-z]*|versus|versos",
        "recordList" => read.Block is RecordListCard records ? records.Kind switch
        {
            "habitHistory" => "history|hist[oó]rico|logs?|registros?",
            "notifications" => "notifications?|notifica[cç][oõ]es",
            "tags" => "tags?|etiquetas?",
            "templates" => "templates?|modelos?",
            "keys" => "keys?|chaves?",
            _ => "(?!)"
        } : "(?!)",
        "accountRows" => read.Block is AccountRowsCard account ? account.Kind switch
        {
            "profile" => "profile|perfil|settings|configura[cç][oõ]es",
            "plan" => "plan|plano|subscription|assinatura",
            "referral" => "referrals?|indica[cç][oõ]es|convites?",
            _ => "(?!)"
        } : "(?!)",
        _ => "(?!)"
    });

    private static string ReadSubject(QueryReadBlock read) => read.Kind + ":" + (read.Block switch
    {
        RecordListCard records => records.Kind,
        AccountRowsCard account => account.Kind,
        _ => read.Read.Scope.GetRawText()
    });

    private static bool AllowAdditionalScope(HashSet<string> families, QueryReadBlock read, string message)
    {
        var family = read.Kind + (read.Block switch
        {
            RecordListCard records => records.Kind,
            AccountRowsCard account => account.Kind,
            _ => string.Empty
        });
        return families.Add(family) || Mentions(message, "both|two|three|ambos|ambas|dois|duas|tr[eê]s")
            || read.Read.Scope.EnumerateObject().Any(property => property.Value.ValueKind == System.Text.Json.JsonValueKind.String
                && DateOnly.TryParse(property.Value.GetString(), System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out _)
                && message.Contains(property.Value.GetString()!, StringComparison.Ordinal));
    }
}
