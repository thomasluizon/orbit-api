using System.Text.Json;
using Orbit.Application.Common;
using Orbit.Domain.Common;

namespace Orbit.Application.Habits.Commands;

internal static class LegacyBulkHabitResult
{
    private static readonly JsonSerializerOptions SerializerOptions =
        new(JsonSerializerDefaults.Web) { Converters = { new ResultJsonConverterFactory() } };

    public static IReadOnlyList<Guid>? ReadHabitIds(string responseBody, string requestType)
    {
        if (requestType == typeof(BulkLogHabitsCommand).FullName)
            return JsonSerializer.Deserialize<Result<BulkLogResult>>(responseBody, SerializerOptions) is { IsSuccess: true } log
                ? log.Value.Results.Select(item => item.HabitId).ToArray()
                : null;

        if (requestType == typeof(BulkSkipHabitsCommand).FullName)
            return JsonSerializer.Deserialize<Result<BulkSkipResult>>(responseBody, SerializerOptions) is { IsSuccess: true } skip
                ? skip.Value.Results.Select(item => item.HabitId).ToArray()
                : null;

        return null;
    }
}
