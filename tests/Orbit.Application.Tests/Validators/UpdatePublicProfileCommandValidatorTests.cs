using FluentValidation.TestHelper;
using Orbit.Application.Profile.Commands;
using Orbit.Application.Profile.Validators;

namespace Orbit.Application.Tests.Validators;

public class UpdatePublicProfileCommandValidatorTests
{
    private readonly UpdatePublicProfileCommandValidator _validator = new();

    private static UpdatePublicProfileCommand ValidCommand() => new(
        UserId: Guid.NewGuid(),
        Enabled: true,
        ShowStreak: true,
        ShowLevel: true,
        ShowAchievements: true,
        ShowTopHabits: false,
        Regenerate: false);

    [Fact]
    public void Validate_ValidCommand_NoErrors()
    {
        var result = _validator.TestValidate(ValidCommand());

        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Validate_EmptyUserId_HasError()
    {
        var command = ValidCommand() with { UserId = Guid.Empty };

        var result = _validator.TestValidate(command);

        result.ShouldHaveValidationErrorFor(x => x.UserId);
    }
}
