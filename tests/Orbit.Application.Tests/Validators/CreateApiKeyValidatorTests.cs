using FluentValidation.TestHelper;
using FluentAssertions;
using Orbit.Application.ApiKeys.Commands;
using Orbit.Application.ApiKeys.Validators;
using Orbit.Application.Common;
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

    [Theory]
    [InlineData("Claude.ai")]
    [InlineData("claude.ai")]
    [InlineData("CLAUDE.AI")]
    [InlineData("  cLaUdE.aI  ")]
    [InlineData("\tClaude.ai\r\n")]
    public void Validate_ConnectorName_ReturnsTypedLocalizedError(string name)
    {
        var result = _validator.TestValidate(ValidCommand() with { Name = name });

        result.ShouldHaveValidationErrorFor(x => x.Name)
            .WithErrorCode("VALIDATION_API_KEY_NAME_RESERVED").Only();
        var error = result.Errors.Single();
        error.LocalizedMessage(false).Should().Contain("Claude.ai");
        error.LocalizedMessage(true).Should().Contain("Claude.ai").And.NotBe(error.LocalizedMessage(false));
    }

    [Theory]
    [InlineData("Claude.ai personal")]
    [InlineData("My Claude.ai")]
    public void Validate_OtherNameContainingConnectorName_NoError(string name)
    {
        _validator.TestValidate(ValidCommand() with { Name = name }).ShouldNotHaveAnyValidationErrors();
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

    [Theory]
    [InlineData("read_user_facts")]
    [InlineData("delete_user_facts")]
    public void Validate_RetiredMemoryScope_HasError(string scope)
    {
        var result = _validator.TestValidate(ValidCommand() with { Scopes = [scope] });

        result.ShouldHaveValidationErrorFor("Scopes[0]");
    }

    [Fact]
    public void Validate_ScopeCaseInsensitive_NoError()
    {
        var command = ValidCommand() with { Scopes = ["READ_HABITS"] };
        var result = _validator.TestValidate(command);
        result.ShouldNotHaveValidationErrorFor("Scopes[0]");
    }
}
