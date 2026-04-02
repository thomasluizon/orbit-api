using FluentAssertions;
using FluentValidation.TestHelper;
using Orbit.Application.Habits.Queries;
using Orbit.Application.Habits.Validators;

namespace Orbit.Application.Tests.Validators;

public class GetHabitScheduleQueryValidatorTests
{
    private readonly GetHabitScheduleQueryValidator _validator = new();

    private static GetHabitScheduleQuery ValidQuery() => new(
        UserId: Guid.NewGuid(),
        DateFrom: new DateOnly(2025, 1, 1),
        DateTo: new DateOnly(2025, 1, 31));

    [Fact]
    public void Validate_ValidRange_NoErrors()
    {
        // Arrange
        var query = ValidQuery();

        // Act
        var result = _validator.TestValidate(query);

        // Assert
        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Validate_DateToBeforeDateFrom_HasError()
    {
        // Arrange
        var query = ValidQuery() with
        {
            DateFrom = new DateOnly(2025, 2, 1),
            DateTo = new DateOnly(2025, 1, 1)
        };

        // Act
        var result = _validator.TestValidate(query);

        // Assert
        result.ShouldHaveValidationErrorFor(x => x.DateTo);
    }

    [Fact]
    public void Validate_RangeOver366Days_HasError()
    {
        // Arrange
        var query = ValidQuery() with
        {
            DateFrom = new DateOnly(2025, 1, 1),
            DateTo = new DateOnly(2026, 1, 3) // 367 days
        };

        // Act
        var result = _validator.TestValidate(query);

        // Assert
        result.Errors.Should().Contain(e => e.ErrorMessage.Contains("366"));
    }

    [Fact]
    public void Validate_PageLessThan1_HasError()
    {
        // Arrange
        var query = ValidQuery() with { Page = 0 };

        // Act
        var result = _validator.TestValidate(query);

        // Assert
        result.ShouldHaveValidationErrorFor(x => x.Page);
    }

    [Fact]
    public void Validate_PageSizeOver200_HasError()
    {
        // Arrange
        var query = ValidQuery() with { PageSize = 201 };

        // Act
        var result = _validator.TestValidate(query);

        // Assert
        result.ShouldHaveValidationErrorFor(x => x.PageSize);
    }

    [Fact]
    public void Validate_PageSize0_HasError()
    {
        // Arrange
        var query = ValidQuery() with { PageSize = 0 };

        // Act
        var result = _validator.TestValidate(query);

        // Assert
        result.ShouldHaveValidationErrorFor(x => x.PageSize);
    }
}
