using FluentValidation.TestHelper;
using Orbit.Application.Profile.Commands;
using Orbit.Application.Profile.Validators;

namespace Orbit.Application.Tests.Validators;

public class SetClockFormatCommandValidatorTests
{
    private readonly SetClockFormatCommandValidator _validator = new();

    [Fact]
    public void Validate_EmptyUserIdFails()
    {
        var result = _validator.TestValidate(new SetClockFormatCommand(Guid.Empty, true));

        result.ShouldHaveValidationErrorFor(x => x.UserId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Validate_ValidUserIdAcceptsBothFormats(bool uses24HourClock)
    {
        var result = _validator.TestValidate(new SetClockFormatCommand(Guid.NewGuid(), uses24HourClock));

        result.ShouldNotHaveAnyValidationErrors();
    }
}
