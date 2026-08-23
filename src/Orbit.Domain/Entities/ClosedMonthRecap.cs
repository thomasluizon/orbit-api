using System.Text.Json;
using Orbit.Domain.Common;

namespace Orbit.Domain.Entities;

/// <summary>
/// Immutable recap response captured when a closed calendar month is first requested. The response
/// is stored because later cadence edits cannot reconstruct the schedule that existed in the month.
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
