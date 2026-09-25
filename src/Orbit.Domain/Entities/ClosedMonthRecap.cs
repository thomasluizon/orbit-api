using System.Text.Json;
using Orbit.Domain.Common;

namespace Orbit.Domain.Entities;

/// <summary>
/// Immutable recap response captured when a closed period is first requested. The legacy table
/// stores months, weeks, and years because later edits cannot reconstruct their original metrics.
/// </summary>
public sealed class ClosedMonthRecap : Entity
{
    public Guid UserId { get; private set; }
    public DateOnly DateFrom { get; private set; }
    public DateOnly DateTo { get; private set; }
    public string ResponseJson { get; private set; } = null!;
    public DateTime CreatedAtUtc { get; private set; }

    private ClosedMonthRecap() { }

    public static Result<ClosedMonthRecap> Create(
        Guid userId,
        DateOnly dateFrom,
        DateOnly dateTo,
        string responseJson)
    {
        if (userId == Guid.Empty)
            return Result.Failure<ClosedMonthRecap>(DomainErrors.UserIdRequired);

        var expectedDateTo = dateFrom.Day == 1 ? dateFrom.AddMonths(1).AddDays(-1) : default;
        if (dateFrom.Day != 1 || dateTo != expectedDateTo)
            return Result.Failure<ClosedMonthRecap>(DomainErrors.ClosedMonthRangeInvalid);

        return CreateValid(userId, dateFrom, dateTo, responseJson);
    }

    public static Result<ClosedMonthRecap> CreateClosedWeek(
        Guid userId, DateOnly dateFrom, DateOnly dateTo, string responseJson)
    {
        if (dateFrom.DayNumber > DateOnly.MaxValue.DayNumber - 6
            || dateTo != dateFrom.AddDays(6))
            return Result.Failure<ClosedMonthRecap>(DomainErrors.ClosedWeekRangeInvalid);

        return CreateValid(userId, dateFrom, dateTo, responseJson);
    }

    public static Result<ClosedMonthRecap> CreateClosedYear(
        Guid userId, DateOnly dateFrom, DateOnly dateTo, string responseJson)
    {
        if (dateFrom.Month != 1 || dateFrom.Day != 1
            || dateTo != new DateOnly(dateFrom.Year, 12, 31))
            return Result.Failure<ClosedMonthRecap>(DomainErrors.ClosedYearRangeInvalid);

        return CreateValid(userId, dateFrom, dateTo, responseJson);
    }

    private static Result<ClosedMonthRecap> CreateValid(
        Guid userId, DateOnly dateFrom, DateOnly dateTo, string responseJson)
    {
        if (userId == Guid.Empty)
            return Result.Failure<ClosedMonthRecap>(DomainErrors.UserIdRequired);

        if (!IsValidJson(responseJson))
            return Result.Failure<ClosedMonthRecap>(DomainErrors.ClosedMonthRecapResponseInvalid);

        return Result.Success(new ClosedMonthRecap
        {
            UserId = userId,
            DateFrom = dateFrom,
            DateTo = dateTo,
            ResponseJson = responseJson,
            CreatedAtUtc = DateTime.UtcNow
        });
    }

    private static bool IsValidJson(string responseJson)
    {
        if (string.IsNullOrWhiteSpace(responseJson))
            return false;

        try
        {
            using var document = JsonDocument.Parse(responseJson);
            return document.RootElement.ValueKind == JsonValueKind.Object;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
