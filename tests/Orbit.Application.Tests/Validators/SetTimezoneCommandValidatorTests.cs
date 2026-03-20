using FluentValidation.TestHelper;
using Orbit.Application.Profile.Commands;
using Orbit.Application.Profile.Validators;

namespace Orbit.Application.Tests.Validators;

public class SetTimezoneCommandValidatorTests
{
    private readonly SetTimezoneCommandValidator _validator = new();

    private static SetTimezoneCommand ValidCommand() => new(
        UserId: Guid.NewGuid(),
        TimeZone: "America/New_York");

    [Fact]
    public void Validate_Valid_NoErrors()
    {
        // Arrange
        var command = ValidCommand();

        // Act
        var result = _validator.TestValidate(command);

        // Assert
        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Validate_EmptyTimezone_HasError()
    {
        // Arrange
        var command = ValidCommand() with { TimeZone = "" };

        // Act
        var result = _validator.TestValidate(command);

        // Assert
        result.ShouldHaveValidationErrorFor(x => x.TimeZone);
    }

    [Fact]
    public void Validate_Over100Chars_HasError()
    {
        // Arrange
        var command = ValidCommand() with { TimeZone = new string('z', 101) };

        // Act
        var result = _validator.TestValidate(command);

        // Assert
        result.ShouldHaveValidationErrorFor(x => x.TimeZone);
    }
}
