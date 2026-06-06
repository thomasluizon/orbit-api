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
        var command = ValidCommand() with { WeekStartDay = day };

        var result = _validator.TestValidate(command);

        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Validate_NegativeDay_HasError()
    {
        var command = ValidCommand() with { WeekStartDay = -1 };

        var result = _validator.TestValidate(command);

        result.ShouldHaveValidationErrorFor(x => x.WeekStartDay);
    }

    [Fact]
    public void Validate_DayAboveSix_HasError()
    {
        var command = ValidCommand() with { WeekStartDay = 7 };

        var result = _validator.TestValidate(command);

        result.ShouldHaveValidationErrorFor(x => x.WeekStartDay);
    }
}
