using System.Text.Json;
using Orbit.Application.Common;
using Orbit.Domain.Models;

namespace Orbit.Application.Chat;

internal static class HabitReadMessages
{
    internal static string? FilteredEmpty(HabitListCard card, string? language)
    {
        if (card.Items.Count != 0 || card.Read is not
            { QueryFamily: "query_habits", Completeness: "complete", TotalCount: 0, NextCursor: null, Recovery: null } read
            || !read.Scope.TryGetProperty("search", out var search) || search.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(search.GetString()))
            return null;
        var term = search.GetString()!.Trim().ReplaceLineEndings(" ");
        if (term.Length > 120) term = term[..117] + "...";
        return ErrorCopy.TryResolve("chat.read.noMatchingHabit", LocaleHelper.IsPortuguese(language), [term], out var text)
            ? text : null;
    }

    internal static bool IsMissingMetricsLookup(QueryReadBlock read) => read.Block is HabitListCard { Items.Count: 0 }
        && read.Read is { QueryFamily: "query_habits", Completeness: "complete", TotalCount: 0, NextCursor: null, Recovery: null }
        && read.Read.Scope.TryGetProperty("search", out var search)
        && search.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(search.GetString())
        && read.Read.Scope.TryGetProperty("include_metrics", out var metrics) && metrics.ValueKind == JsonValueKind.True;

    internal static string? Missing(HabitListCard card, string? language) => card.Read is { } read
        && IsMissingMetricsLookup(new("habitList", card, read))
            ? ErrorCopy.Resolve("chat.read.missingHabitStreak", LocaleHelper.IsPortuguese(language)) : null;

    internal static bool IsSkipped(HabitListCard card, HabitListCardItem item) => item.IsSkippedOnDate == true || card.SkippedIds.Contains(item.Id);

    internal static string? Skipped(HabitListCard card, string? language) => card.Items.Any(item => IsSkipped(card, item))
        ? string.Join("\n", card.Items.Where(item => IsSkipped(card, item))
            .Select(item => item.Title + ": " + ErrorCopy.Resolve(card.Scope == HabitListCardBuilder.ScopeToday
                ? "chat.read.skippedToday" : "chat.read.skippedOnDate", LocaleHelper.IsPortuguese(language)) + ".")) : null;
}
