using System.Text.Json;
using System.Text.RegularExpressions;
using Orbit.Application.Common;
using Orbit.Domain.Models;

namespace Orbit.Application.Chat;

internal static class HabitReadMessages
{
    internal static bool IsSingleStreakRead(string message) => Regex.IsMatch(message,
        @"\b(streak|sequ[eê]ncia|dias seguidos|days in a row)\b", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1))
        && !Regex.IsMatch(message,
            @"\b(and|then|also|e|depois|tamb[eé]m|create|add|change|edit|update|delete|remove|skip|log|crie|criar|adicione|mude|altere|edite|exclua|apague|remova|pule|registre)\b",
            RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));

    internal static bool IsMissingMetricsLookup(QueryReadBlock read) => read.Block is HabitListCard { Items.Count: 0 }
        && read.Read.TotalCount == 0 && read.Read.Scope.TryGetProperty("search", out var search)
        && search.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(search.GetString())
        && read.Read.Scope.TryGetProperty("include_metrics", out var metrics) && metrics.ValueKind == JsonValueKind.True;

    internal static string? Missing(HabitListCard card, string? language) => card.Read is { } read
        && IsMissingMetricsLookup(new("habitList", card, read))
            ? ErrorCopy.Resolve("chat.read.missingHabitStreak", LocaleHelper.IsPortuguese(language)) : null;

    internal static string? Skipped(HabitListCard card, string? language) => card.Items.Any(item => card.SkippedIds.Contains(item.Id))
        ? string.Join("\n", card.Items.Where(item => card.SkippedIds.Contains(item.Id))
            .Select(item => item.Title + ": " + ErrorCopy.Resolve(card.Scope == HabitListCardBuilder.ScopeToday
                ? "chat.read.skippedToday" : "chat.read.skippedOnDate", LocaleHelper.IsPortuguese(language)) + ".")) : null;
}
