using System.Text.Json;
using System.Text.Json.Serialization;
using Orbit.Application.Common;
using Orbit.Application.Habits.Queries;

namespace Orbit.Application.Chat;

internal sealed record HabitRankingModelPayload(string QueryId, string QueryToken, string Revision,
    HabitRankingModelQuery Query, IReadOnlyList<object[]> Rows, IReadOnlyDictionary<string, string> Names,
    int TotalCount, int Offset, bool IsComplete, string? NextCursor, int OmittedNames)
{
    private static readonly JsonSerializerOptions ModelSerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public IReadOnlyList<string> RowColumns => ["habitId", "completedCount"];
    public string Instruction => "Rows contain every identity and completion count in this page, in rank order. "
        + "Habit IDs use GUIDs without hyphens; rank is offset + row index + 1. Names are optional shortened hints keyed by habit ID. "
        + "Do not infer missing names or retry for them. The client ranking and localized text fallback retain complete rows. "
        + "Emit [[orbit:ranking]]. Reuse queryToken with nextCursor for later pages, or with refresh=true, without query overrides.";

    public static HabitRankingModelPayload From(HabitRankingResponse response)
    {
        var rows = response.Rows.Select(row => new object[] { row.HabitId.ToString("N"), row.CompletedCount }).ToArray();
        var names = response.Rows.ToDictionary(row => row.HabitId.ToString("N"), row => ShortName(row.Name));
        var query = new HabitRankingModelQuery(response.Query.DateFrom, response.Query.DateToExclusive,
            response.Query.TimeZone, response.Query.Metric, response.Query.Scope, response.Query.PageSize,
            response.Query.HabitIds.Count);
        var payload = new HabitRankingModelPayload(response.QueryId, response.QueryToken, response.Revision,
            query, rows, names, response.TotalCount, response.Offset, response.IsComplete, response.NextCursor, 0);
        foreach (var row in response.Rows.Reverse())
        {
            if (JsonSerializer.Serialize(payload, ModelSerializerOptions).Length < AppConstants.MaxAiToolPayloadJsonLength)
                break;
            names.Remove(row.HabitId.ToString("N"));
            payload = payload with { OmittedNames = response.Rows.Count - names.Count };
        }
        return payload;
    }

    private static string ShortName(string name)
    {
        if (name.Length <= 64) return name;
        var length = char.IsHighSurrogate(name[63]) ? 63 : 64;
        return name[..length];
    }
}

internal sealed record HabitRankingModelQuery(DateOnly DateFrom, DateOnly DateToExclusive,
    string TimeZone, string Metric, HabitRankingScope Scope, int PageSize, int FilteredHabitCount);
