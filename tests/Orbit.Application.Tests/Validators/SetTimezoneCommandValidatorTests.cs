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
        var command = ValidCommand();

        var result = _validator.TestValidate(command);

        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Validate_EmptyTimezone_HasError()
    {
        var command = ValidCommand() with { TimeZone = "" };

        var result = _validator.TestValidate(command);

        result.ShouldHaveValidationErrorFor(x => x.TimeZone);
    }

    [Fact]
    public void Validate_Over100Chars_HasError()
    {
        var command = ValidCommand() with { TimeZone = new string('z', 101) };

        var result = _validator.TestValidate(command);

        result.ShouldHaveValidationErrorFor(x => x.TimeZone);
    }
}
