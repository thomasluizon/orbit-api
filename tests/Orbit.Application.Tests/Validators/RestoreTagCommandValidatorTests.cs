using FluentValidation.TestHelper;
using Orbit.Application.Tags.Commands;
using Orbit.Application.Tags.Validators;

namespace Orbit.Application.Tests.Validators;

public class RestoreTagCommandValidatorTests
{
    private readonly RestoreTagCommandValidator _validator = new();

    private static RestoreTagCommand ValidCommand() => new(
        UserId: Guid.NewGuid(),
        TagId: Guid.NewGuid());

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
    public void Validate_EmptyTagId_HasError()
    {
        var result = _validator.TestValidate(ValidCommand() with { TagId = Guid.Empty });
        result.ShouldHaveValidationErrorFor(x => x.TagId);
    }
}
