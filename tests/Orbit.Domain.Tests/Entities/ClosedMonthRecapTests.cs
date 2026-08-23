using FluentAssertions;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;

namespace Orbit.Domain.Tests.Entities;

public class ClosedMonthRecapTests
{
    [Fact]
    public void Create_CompleteCalendarMonthAndJsonObject_Succeeds()
    {
        var userId = Guid.NewGuid();

        var result = ClosedMonthRecap.Create(
            userId,
            new DateOnly(2026, 2, 1),
            new DateOnly(2026, 2, 28),
            "{\"period\":\"month\"}");

        result.IsSuccess.Should().BeTrue();
        result.Value.UserId.Should().Be(userId);
        result.Value.DateFrom.Should().Be(new DateOnly(2026, 2, 1));
        result.Value.DateTo.Should().Be(new DateOnly(2026, 2, 28));
        result.Value.ResponseJson.Should().Be("{\"period\":\"month\"}");
        result.Value.CreatedAtUtc.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void Create_EmptyUserId_ReturnsNamedFailure()
    {
        var result = ClosedMonthRecap.Create(
            Guid.Empty,
            new DateOnly(2026, 2, 1),
            new DateOnly(2026, 2, 28),
            "{}");

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(DomainErrors.UserIdRequired.Code);
    }

    [Theory]
    [InlineData(2026, 2, 2, 2026, 2, 28)]
    [InlineData(2026, 2, 1, 2026, 2, 27)]
    [InlineData(2026, 2, 1, 2026, 3, 1)]
    public void Create_NonCalendarMonthRange_ReturnsNamedFailure(
        int fromYear,
        int fromMonth,
        int fromDay,
        int toYear,
        int toMonth,
        int toDay)
    {
        var result = ClosedMonthRecap.Create(
            Guid.NewGuid(),
            new DateOnly(fromYear, fromMonth, fromDay),
            new DateOnly(toYear, toMonth, toDay),
            "{}");

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(DomainErrors.ClosedMonthRangeInvalid.Code);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    public void Create_InvalidResponseJson_ReturnsNamedFailure(string responseJson)
    {
        var result = ClosedMonthRecap.Create(
            Guid.NewGuid(),
            new DateOnly(2026, 2, 1),
            new DateOnly(2026, 2, 28),
            responseJson);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(DomainErrors.ClosedMonthRecapResponseInvalid.Code);
    }
}
