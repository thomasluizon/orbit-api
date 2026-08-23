using FluentAssertions;
using Orbit.Application.Gamification.Queries;

namespace Orbit.Application.Tests.Queries.Gamification;

public class GetRecapQueryValidatorTests
{
    private readonly GetRecapQueryValidator _validator = new();

    private static readonly DateOnly DateTo = new(2026, 6, 20);
    private static readonly DateOnly DateFrom = DateTo.AddDays(-6);

    [Theory]
    [InlineData("week")]
    [InlineData("month")]
    [InlineData("quarter")]
    [InlineData("semester")]
    [InlineData("year")]
    public void Validate_AllowedPeriod_Passes(string period)
    {
        var result = _validator.Validate(new GetRecapQuery(Guid.NewGuid(), DateFrom, DateTo, period));

        result.IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData("day")]
    [InlineData("decade")]
    [InlineData("")]
    public void Validate_DisallowedPeriod_Fails(string period)
    {
        var result = _validator.Validate(new GetRecapQuery(Guid.NewGuid(), DateFrom, DateTo, period));

        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public void Validate_DateFromAfterDateTo_Fails()
    {
        var result = _validator.Validate(new GetRecapQuery(Guid.NewGuid(), DateTo.AddDays(1), DateTo, "week"));

        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public void Validate_ClosedMonthWithCalendarParameters_Passes()
    {
        var query = new GetRecapQuery(
            Guid.NewGuid(),
            new DateOnly(2026, 6, 1),
            new DateOnly(2026, 6, 30),
            "month",
            2026,
            6);

        var result = _validator.Validate(query);

        result.IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData("month", 2026, null)]
    [InlineData("month", null, 6)]
    [InlineData("week", 2026, 6)]
    [InlineData("month", 2026, 13)]
    public void Validate_InvalidClosedMonthParameters_Fails(string period, int? year, int? month)
    {
        var query = new GetRecapQuery(Guid.NewGuid(), DateFrom, DateTo, period, year, month);

        var result = _validator.Validate(query);

        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public void Validate_ClosedMonthWithRollingDates_Fails()
    {
        var query = new GetRecapQuery(Guid.NewGuid(), DateFrom, DateTo, "month", 2026, 6);

        var result = _validator.Validate(query);

        result.IsValid.Should().BeFalse();
    }
}
