using FluentAssertions;
using FluentValidation.TestHelper;
using Orbit.Application.Common;
using Orbit.Application.Habits.Queries;
using Orbit.Application.Habits.Validators;

namespace Orbit.Application.Tests.Validators;

public class GetHabitLogsQueryValidatorTests
{
    private readonly GetHabitLogsQueryValidator _validator = new();

    [Theory]
    [InlineData(null, null)]
    [InlineData("2025-01-01", "2025-01-01")]
    [InlineData("2024-01-01", "2024-12-31")]
    [InlineData("0001-01-01", "0001-01-01")]
    [InlineData("9999-12-31", "9999-12-31")]
    public void ValidRange_HasNoErrors(string? from, string? to)
    {
        _validator.TestValidate(Query(from, to)).ShouldNotHaveAnyValidationErrors();
    }

    [Theory]
    [InlineData("2025-01-31", "2025-01-01", nameof(GetHabitLogsQuery.To), ValidationErrorCodes.DateRangeOrder)]
    [InlineData("2024-01-01", "2025-01-01", nameof(GetHabitLogsQuery.To), ValidationErrorCodes.DateRangeLimit)]
    [InlineData("2025-01-01", null, nameof(GetHabitLogsQuery.To), ValidationErrorCodes.DateToRequired)]
    [InlineData(null, "2025-01-01", nameof(GetHabitLogsQuery.From), ValidationErrorCodes.DateFromRequired)]
    public void InvalidRange_HasFieldError(string? from, string? to, string field, string code)
    {
        var result = _validator.TestValidate(Query(from, to));

        result.Errors.Should().ContainSingle().Which.Should().Match<FluentValidation.Results.ValidationFailure>(
            error => error.PropertyName == field && error.ErrorCode == code);
    }

    [Fact]
    public void EmptyIds_HaveErrors()
    {
        var result = _validator.TestValidate(new GetHabitLogsQuery(Guid.Empty, Guid.Empty));

        result.ShouldHaveValidationErrorFor(query => query.UserId);
        result.ShouldHaveValidationErrorFor(query => query.HabitId);
    }

    private static GetHabitLogsQuery Query(string? from, string? to) => new(
        Guid.NewGuid(), Guid.NewGuid(),
        from is null ? null : DateOnly.ParseExact(from, "yyyy-MM-dd"),
        to is null ? null : DateOnly.ParseExact(to, "yyyy-MM-dd"));
}
