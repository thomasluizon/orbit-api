using FluentValidation.TestHelper;
using Orbit.Application.ApiKeys.Commands;
using Orbit.Application.ApiKeys.Validators;

namespace Orbit.Application.Tests.Validators;

public class CreateApiKeyValidatorTests
{
    private readonly CreateApiKeyValidator _validator = new();

    private static CreateApiKeyCommand ValidCommand() => new(
        UserId: Guid.NewGuid(),
        Name: "My API Key");

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
    public void Validate_EmptyName_HasError()
    {
        var result = _validator.TestValidate(ValidCommand() with { Name = "" });
        result.ShouldHaveValidationErrorFor(x => x.Name);
    }

    [Fact]
    public void Validate_NullName_HasError()
    {
        var result = _validator.TestValidate(ValidCommand() with { Name = null! });
        result.ShouldHaveValidationErrorFor(x => x.Name);
    }

    [Fact]
    public void Validate_NameOver50Chars_HasError()
    {
        var result = _validator.TestValidate(ValidCommand() with { Name = new string('a', 51) });
        result.ShouldHaveValidationErrorFor(x => x.Name);
    }

    [Fact]
    public void Validate_NameExactly50Chars_NoError()
    {
        var result = _validator.TestValidate(ValidCommand() with { Name = new string('a', 50) });
        result.ShouldNotHaveValidationErrorFor(x => x.Name);
    }
}
