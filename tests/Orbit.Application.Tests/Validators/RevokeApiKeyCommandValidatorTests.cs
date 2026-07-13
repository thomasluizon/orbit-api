using FluentValidation.TestHelper;
using Orbit.Application.ApiKeys.Commands;
using Orbit.Application.ApiKeys.Validators;

namespace Orbit.Application.Tests.Validators;

public class RevokeApiKeyCommandValidatorTests
{
    private readonly RevokeApiKeyCommandValidator _validator = new();

    private static RevokeApiKeyCommand ValidCommand() => new(
        UserId: Guid.NewGuid(),
        KeyId: Guid.NewGuid());

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
    public void Validate_EmptyKeyId_HasError()
    {
        var result = _validator.TestValidate(ValidCommand() with { KeyId = Guid.Empty });
        result.ShouldHaveValidationErrorFor(x => x.KeyId);
    }
}
