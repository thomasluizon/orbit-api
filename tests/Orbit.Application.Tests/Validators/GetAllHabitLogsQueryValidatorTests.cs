using FluentAssertions;
using FluentValidation.TestHelper;
using Orbit.Application.Habits.Queries;
using Orbit.Application.Habits.Validators;

namespace Orbit.Application.Tests.Validators;

public class GetAllHabitLogsQueryValidatorTests
{
    private readonly GetAllHabitLogsQueryValidator _validator = new();

    private static GetAllHabitLogsQuery ValidQuery() => new(
        UserId: Guid.NewGuid(),
        DateFrom: new DateOnly(2026, 4, 1),
        DateTo: new DateOnly(2026, 4, 30));

    [Fact]
    public void Validate_ValidRange_NoErrors()
    {
        var query = ValidQuery();

        var result = _validator.TestValidate(query);

        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Validate_DateToBeforeDateFrom_HasError()
    {
        var query = ValidQuery() with
        {
            DateFrom = new DateOnly(2026, 5, 1),
            DateTo = new DateOnly(2026, 4, 1)
        };

        var result = _validator.TestValidate(query);

        result.ShouldHaveValidationErrorFor(x => x.DateTo);
    }

    [Fact]
    public void Validate_RangeOver366Days_HasError()
    {
        var query = ValidQuery() with
        {
            DateFrom = new DateOnly(2026, 1, 1),
            DateTo = new DateOnly(2027, 1, 3)
        };

        var result = _validator.TestValidate(query);

        result.Errors.Should().Contain(e => e.ErrorMessage.Contains("366"));
    }

    [Fact]
    public void Validate_SingleDayRange_NoErrors()
    {
        var query = ValidQuery() with
        {
            DateFrom = new DateOnly(2026, 4, 15),
            DateTo = new DateOnly(2026, 4, 15)
        };

        var result = _validator.TestValidate(query);

        result.ShouldNotHaveAnyValidationErrors();
    }
}
