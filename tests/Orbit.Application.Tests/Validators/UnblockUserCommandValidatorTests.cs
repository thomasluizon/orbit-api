using FluentValidation.TestHelper;
using Orbit.Application.Social.Commands;
using Orbit.Application.Social.Validators;

namespace Orbit.Application.Tests.Validators;

public class UnblockUserCommandValidatorTests
{
    private readonly UnblockUserCommandValidator _validator = new();

    private static UnblockUserCommand ValidCommand() => new(
        UserId: Guid.NewGuid(),
        BlockedUserId: Guid.NewGuid());

    [Fact]
    public void Validate_ValidCommand_NoErrors()
    {
        var result = _validator.TestValidate(ValidCommand());
        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Validate_EmptyUserId_HasError()
    {
        var result = _validator.TestValidate(ValidCommand() with { UserId = Guid.Empty });
        result.ShouldHaveValidationErrorFor(x => x.UserId);
    }

    [Fact]
    public void Validate_EmptyBlockedUserId_HasError()
    {
        var result = _validator.TestValidate(ValidCommand() with { BlockedUserId = Guid.Empty });
        result.ShouldHaveValidationErrorFor(x => x.BlockedUserId);
    }

    [Fact]
    public void Validate_SelfUnblock_HasError()
    {
        var id = Guid.NewGuid();
        var result = _validator.TestValidate(new UnblockUserCommand(id, id));
        result.ShouldHaveValidationErrorFor(x => x.BlockedUserId);
    }
}
