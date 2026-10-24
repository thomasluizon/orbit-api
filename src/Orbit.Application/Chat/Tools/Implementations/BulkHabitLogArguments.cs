using System.Globalization;
using System.Text.Json;
using Orbit.Application.Habits.Commands;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Chat.Tools.Implementations;

internal static class BulkHabitLogArguments
{
    public static async Task<Result<JsonElement?>> ExpandAsync(JsonElement arguments,
        IGenericRepository<Habit> habits, IUserDateService dates, Guid userId, CancellationToken ct,
        IReadOnlyList<Habit>? loadedHabits = null)
    {
        if (arguments.TryGetProperty("revised_items", out _))
        {
            var (_, error) = BulkHabitToolArguments.ParseRevisedDatedItems(arguments);
            return error is null ? Result.Success<JsonElement?>(arguments) : Result.Failure<JsonElement?>(error);
        }
        var (filter, filterError) = BulkHabitToolArguments.ParseActionFilter(arguments);
        if (filterError is not null)
            return Result.Failure<JsonElement?>(filterError);
        var selectedDates = new HashSet<DateOnly>();
        if (arguments.TryGetProperty("dates", out var dateArray) && dateArray.ValueKind != JsonValueKind.Null)
        {
            if ((arguments.TryGetProperty("date", out var singleDate) && singleDate.ValueKind != JsonValueKind.Null) || dateArray.ValueKind != JsonValueKind.Array
                || dateArray.GetArrayLength() == 0)
                return Result.Failure<JsonElement?>("Provide dates as a non-empty array, without date.");
            if (dateArray.GetArrayLength() > AppConstants.MaxBulkOperationSize)
                return Result.Failure<JsonElement?>($"Provide at most {AppConstants.MaxBulkOperationSize} dates per preview.");
            foreach (var date in dateArray.EnumerateArray())
            {
                if (!TryDate(date, out var parsed))
                    return Result.Failure<JsonElement?>("Every date must use YYYY-MM-DD format.");
                selectedDates.Add(parsed);
            }
        }
        else if (arguments.TryGetProperty("date", out var date) && date.ValueKind != JsonValueKind.Null)
        {
            if (!TryDate(date, out var parsed))
                return Result.Failure<JsonElement?>("date must use YYYY-MM-DD format.");
            selectedDates.Add(parsed);
        }
        else
            selectedDates.Add(await dates.GetUserTodayAsync(userId, ct));

        var selectedHabits = loadedHabits is null
            ? await BulkHabitSelection.LoadAsync(habits, userId, filter!, ct)
            : BulkHabitSelection.Select(loadedHabits, filter!);
        if (selectedHabits.Count == 0)
            return Result.Success<JsonElement?>(null);
        return Result.Success<JsonElement?>(JsonSerializer.SerializeToElement(new
        {
            revised_items = selectedHabits.SelectMany(habit => selectedDates.Order().Select(date => new
            {
                habit_id = habit.Id,
                date = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            })).ToArray()
        }));
    }

    private static bool TryDate(JsonElement value, out DateOnly date)
    {
        date = default;
        return value.ValueKind == JsonValueKind.String && DateOnly.TryParseExact(value.GetString(),
            "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
    }
}
