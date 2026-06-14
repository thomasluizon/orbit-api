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

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    public void Validate_DayOutsideSundayMonday_HasError(int day)
    {
        var command = ValidCommand() with { WeekStartDay = day };

        var result = _validator.TestValidate(command);

        result.ShouldHaveValidationErrorFor(x => x.WeekStartDay);
    }
}
