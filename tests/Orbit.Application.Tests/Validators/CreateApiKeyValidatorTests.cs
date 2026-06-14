using FluentValidation.TestHelper;
using Orbit.Application.ApiKeys.Commands;
using Orbit.Application.ApiKeys.Validators;
using Orbit.Domain.Models;

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

    [Fact]
    public void Validate_RecognizedScopes_NoError()
    {
        var command = ValidCommand() with { Scopes = [AgentScopes.ReadHabits, AgentScopes.WriteGoals] };
        var result = _validator.TestValidate(command);
        result.ShouldNotHaveValidationErrorFor("Scopes[0]");
        result.ShouldNotHaveValidationErrorFor("Scopes[1]");
    }

    [Fact]
    public void Validate_UnrecognizedScope_HasError()
    {
        var command = ValidCommand() with { Scopes = [AgentScopes.ReadHabits, "not_a_real_scope"] };
        var result = _validator.TestValidate(command);
        result.ShouldHaveValidationErrorFor("Scopes[1]");
    }

    [Fact]
    public void Validate_ScopeCaseInsensitive_NoError()
    {
        var command = ValidCommand() with { Scopes = ["READ_HABITS"] };
        var result = _validator.TestValidate(command);
        result.ShouldNotHaveValidationErrorFor("Scopes[0]");
    }
}
