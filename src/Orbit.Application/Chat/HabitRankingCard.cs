using System.Globalization;
using Orbit.Application.Habits.Queries;
using Orbit.Domain.Models;

namespace Orbit.Application.Chat;

public sealed record HabitRankingCard(HabitRankingResponse Ranking, string SurfaceId = "progress");

public static class HabitRankingCardBuilder
{
    public const string Kind = "habitRanking";
    public const string Directive = "[[orbit:ranking]]";
    public const string PromptInstruction = "After get_habit_ranking succeeds, place [[orbit:ranking]] on its own line. "
        + "It contains completion log counts and absolute dates, not rates. Never substitute get_retrospective. "
        + "Do not invent missing rows. Use the query token for later pages or refresh.";

    public static bool IsSupported(AgentClientContext? client) => client?.SupportsParts == true
        && client.PartKinds?.Contains(Kind, StringComparer.Ordinal) == true;

    public static HabitRankingCard? Build(HabitRankingResponse? response)
    {
        if (response is null || response.Query.DateFrom >= response.Query.DateToExclusive
            || response.Query.Metric != HabitRankingDefinition.CompletionLogRecords
            || response.Rows.Any(row => row.HabitId == Guid.Empty || row.CompletedCount < 1)
            || response.Offset < 0 || response.Offset + response.Rows.Count > response.TotalCount
            || string.IsNullOrWhiteSpace(response.QueryToken) || string.IsNullOrWhiteSpace(response.Revision))
            return null;
        return new HabitRankingCard(response);
    }

    public static string Readable(HabitRankingResponse response, string? language)
    {
        var portuguese = language?.StartsWith("pt", StringComparison.OrdinalIgnoreCase) == true;
        var from = response.Query.DateFrom.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var through = response.Query.DateToExclusive.AddDays(-1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var title = portuguese
            ? $"Conclusões de hábitos de {from} a {through} ({response.Query.TimeZone}). Contagem de registros positivos, excluindo registros desfeitos ou excluídos."
            : $"Habit completions from {from} through {through} ({response.Query.TimeZone}). Counts positive completion records, excluding undone or deleted records.";
        var rows = response.Rows.Select(row => $"{row.Rank}. {row.Name}: {row.CompletedCount}");
        var status = response.TotalCount == 0
            ? portuguese ? "Nenhuma conclusão elegível neste período." : "No eligible completions in this window."
            : portuguese
                ? $"{response.Offset + 1}-{response.Offset + response.Rows.Count} de {response.TotalCount}."
                : $"{response.Offset + 1}-{response.Offset + response.Rows.Count} of {response.TotalCount}.";
        return string.Join('\n', new[] { title }.Concat(rows).Append(status));
    }
}
