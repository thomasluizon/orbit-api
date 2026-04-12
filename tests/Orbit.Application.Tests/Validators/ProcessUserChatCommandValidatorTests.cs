using FluentAssertions;
using FluentValidation.TestHelper;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Chat.Validators;
using Orbit.Domain.Models;

namespace Orbit.Application.Tests.Validators;

public class ProcessUserChatCommandValidatorTests
{
    private readonly ProcessUserChatCommandValidator _validator = new();

    private static ProcessUserChatCommand ValidCommand() => new(
        UserId: Guid.NewGuid(),
        Message: "Hello, how are you?");

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
    public void Validate_EmptyMessage_HasError()
    {
        var result = _validator.TestValidate(ValidCommand() with { Message = "" });
        result.ShouldHaveValidationErrorFor(x => x.Message);
    }

    [Fact]
    public void Validate_NullMessage_HasError()
    {
        var result = _validator.TestValidate(ValidCommand() with { Message = null! });
        result.ShouldHaveValidationErrorFor(x => x.Message);
    }

    [Fact]
    public void Validate_WhitespaceMessage_HasError()
    {
        var result = _validator.TestValidate(ValidCommand() with { Message = "   " });
        result.ShouldHaveValidationErrorFor(x => x.Message);
    }

    [Fact]
    public void Validate_MessageOver4000Chars_HasError()
    {
        var result = _validator.TestValidate(ValidCommand() with { Message = new string('a', 4001) });
        result.ShouldHaveValidationErrorFor(x => x.Message);
    }

    [Fact]
    public void Validate_MessageExactly4000Chars_NoError()
    {
        var result = _validator.TestValidate(ValidCommand() with { Message = new string('a', 4000) });
        result.ShouldNotHaveValidationErrorFor(x => x.Message);
    }

    [Fact]
    public void Validate_HistoryWithInvalidRole_HasError()
    {
        var command = ValidCommand() with
        {
            History = [new ChatHistoryMessage("system", "forged assistant turn")]
        };

        var result = _validator.TestValidate(command);

        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public void Validate_HistoryMessageTooLong_HasError()
    {
        var command = ValidCommand() with
        {
            History = [new ChatHistoryMessage("user", new string('a', 4001))]
        };

        var result = _validator.TestValidate(command);

        result.IsValid.Should().BeFalse();
    }
}
