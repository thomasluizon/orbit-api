using FluentValidation.TestHelper;
using Orbit.Application.Habits.Queries;
using Orbit.Application.Habits.Validators;

namespace Orbit.Application.Tests.Validators;

public class GetHabitsQueryValidatorTests
{
    private readonly GetHabitsQueryValidator _validator = new();

    private static GetHabitsQuery ValidQuery() => new(
        UserId: Guid.NewGuid());

    [Fact]
    public void Validate_Valid_NoErrors()
    {
        // Arrange
        var query = ValidQuery();

        // Act
        var result = _validator.TestValidate(query);

        // Assert
        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Validate_SearchOver200Chars_HasError()
    {
        // Arrange
        var query = ValidQuery() with { Search = new string('s', 201) };

        // Act
        var result = _validator.TestValidate(query);

        // Assert
        result.ShouldHaveValidationErrorFor(x => x.Search);
    }

    [Fact]
    public void Validate_InvalidFrequencyUnit_HasError()
    {
        // Arrange
        var query = ValidQuery() with { FrequencyUnitFilter = "Invalid" };

        // Act
        var result = _validator.TestValidate(query);

        // Assert
        result.ShouldHaveValidationErrorFor(x => x.FrequencyUnitFilter);
    }

    [Theory]
    [InlineData("Day")]
    [InlineData("Week")]
    [InlineData("Month")]
    [InlineData("Year")]
    [InlineData("none")]
    public void Validate_ValidFrequencyUnits_NoErrors(string unit)
    {
        // Arrange
        var query = ValidQuery() with { FrequencyUnitFilter = unit };

        // Act
        var result = _validator.TestValidate(query);

        // Assert
        result.ShouldNotHaveValidationErrorFor(x => x.FrequencyUnitFilter);
    }

    [Fact]
    public void Validate_DateToBeforeDateFrom_HasError()
    {
        // Arrange
        var query = ValidQuery() with
        {
            DueDateFrom = new DateOnly(2025, 3, 1),
            DueDateTo = new DateOnly(2025, 2, 1)
        };

        // Act
        var result = _validator.TestValidate(query);

        // Assert
        result.ShouldHaveValidationErrorFor(x => x.DueDateTo);
    }
}
