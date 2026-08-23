using FluentValidation.TestHelper;
using Orbit.Application.ApiKeys.Commands;
using Orbit.Application.ApiKeys.Validators;

namespace Orbit.Application.Tests.Validators;

public class ApiKeyCreationChallengeCommandValidatorTests
{
    [Fact]
    public void Request_EmptyUserId_HasError()
    {
        var validator = new RequestApiKeyCreationChallengeCommandValidator();

        var result = validator.TestValidate(new RequestApiKeyCreationChallengeCommand(Guid.Empty));

        result.ShouldHaveValidationErrorFor(command => command.UserId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("12345")]
    [InlineData("12345a")]
    public void Confirm_InvalidCode_HasError(string code)
    {
        var validator = new ConfirmApiKeyCreationChallengeCommandValidator();

        var result = validator.TestValidate(new ConfirmApiKeyCreationChallengeCommand(Guid.NewGuid(), code));

        result.ShouldHaveValidationErrorFor(command => command.Code);
    }

    [Fact]
    public void Confirm_ValidInput_HasNoErrors()
    {
        var validator = new ConfirmApiKeyCreationChallengeCommandValidator();

        var result = validator.TestValidate(
            new ConfirmApiKeyCreationChallengeCommand(Guid.NewGuid(), "123456"));

        result.ShouldNotHaveAnyValidationErrors();
    }
}
