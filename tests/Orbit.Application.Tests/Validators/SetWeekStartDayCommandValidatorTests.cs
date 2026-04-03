using FluentValidation.TestHelper;
using Orbit.Application.Profile.Commands;
using Orbit.Application.Profile.Validators;

namespace Orbit.Application.Tests.Validators;

public class SetWeekStartDayCommandValidatorTests
{
    private readonly SetWeekStartDayCommandValidator _validator = new();

    private static SetWeekStartDayCommand ValidCommand() => new(
        UserId: Guid.NewGuid(),
        WeekStartDay: 0);

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(6)]
    public void Validate_ValidDay_NoErrors(int day)
    {
        // Arrange
        var command = ValidCommand() with { WeekStartDay = day };

        // Act
        var result = _validator.TestValidate(command);

        // Assert
        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Validate_NegativeDay_HasError()
    {
        // Arrange
        var command = ValidCommand() with { WeekStartDay = -1 };

        // Act
        var result = _validator.TestValidate(command);

        // Assert
        result.ShouldHaveValidationErrorFor(x => x.WeekStartDay);
    }

    [Fact]
    public void Validate_DayAboveSix_HasError()
    {
        // Arrange
        var command = ValidCommand() with { WeekStartDay = 7 };

        // Act
        var result = _validator.TestValidate(command);

        // Assert
        result.ShouldHaveValidationErrorFor(x => x.WeekStartDay);
    }
}
