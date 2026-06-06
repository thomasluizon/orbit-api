using FluentValidation.TestHelper;
using Orbit.Application.Support.Commands;
using Orbit.Application.Support.Validators;

namespace Orbit.Application.Tests.Validators;

public class SendSupportCommandValidatorTests
{
    private readonly SendSupportCommandValidator _validator = new();

    private static SendSupportCommand ValidCommand() => new(
        UserId: Guid.NewGuid(),
        Name: "Test User",
        Email: "user@example.com",
        Subject: "Bug report",
        Message: "Something is broken");

    [Fact]
    public void Validate_ValidInput_NoErrors()
    {
        var command = ValidCommand();

        var result = _validator.TestValidate(command);

        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Validate_EmptyName_HasError()
    {
        var command = ValidCommand() with { Name = "" };

        var result = _validator.TestValidate(command);

        result.ShouldHaveValidationErrorFor(x => x.Name);
    }

    [Fact]
    public void Validate_EmptySubject_HasError()
    {
        var command = ValidCommand() with { Subject = "" };

        var result = _validator.TestValidate(command);

        result.ShouldHaveValidationErrorFor(x => x.Subject);
    }

    [Fact]
    public void Validate_SubjectOver200Chars_HasError()
    {
        var command = ValidCommand() with { Subject = new string('a', 201) };

        var result = _validator.TestValidate(command);

        result.ShouldHaveValidationErrorFor(x => x.Subject);
    }

    [Fact]
    public void Validate_EmptyMessage_HasError()
    {
        var command = ValidCommand() with { Message = "" };

        var result = _validator.TestValidate(command);

        result.ShouldHaveValidationErrorFor(x => x.Message);
    }

    [Fact]
    public void Validate_MessageOver5000Chars_HasError()
    {
        var command = ValidCommand() with { Message = new string('a', 5001) };

        var result = _validator.TestValidate(command);

        result.ShouldHaveValidationErrorFor(x => x.Message);
    }

    [Fact]
    public void Validate_InvalidEmail_HasError()
    {
        var command = ValidCommand() with { Email = "not-an-email" };

        var result = _validator.TestValidate(command);

        result.ShouldHaveValidationErrorFor(x => x.Email);
    }
}
