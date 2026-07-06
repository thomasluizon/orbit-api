using FluentValidation.TestHelper;
using Orbit.Application.Profile.Commands;
using Orbit.Application.Profile.Validators;

namespace Orbit.Application.Tests.Validators;

public class UpdateMarketingConsentCommandValidatorTests
{
    private readonly UpdateMarketingConsentCommandValidator _validator = new();

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Validate_KnownUser_NoErrors(bool enabled)
    {
        var command = new UpdateMarketingConsentCommand(Guid.NewGuid(), enabled);

        var result = _validator.TestValidate(command);

        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Validate_EmptyUserId_HasError()
    {
        var command = new UpdateMarketingConsentCommand(Guid.Empty, true);

        var result = _validator.TestValidate(command);

        result.ShouldHaveValidationErrorFor(x => x.UserId);
    }
}
