using FluentValidation.TestHelper;
using Orbit.Application.Notifications.Commands;
using Orbit.Application.Notifications.Validators;

namespace Orbit.Application.Tests.Validators;

public class UnsubscribePushCommandValidatorTests
{
    private readonly UnsubscribePushCommandValidator _validator = new();

    private static UnsubscribePushCommand ValidCommand() => new(
        UserId: Guid.NewGuid(),
        Endpoint: "https://push.example.com/sub/abc123");

    [Fact]
    public void Validate_ValidInput_NoErrors()
    {
        var command = ValidCommand();

        var result = _validator.TestValidate(command);

        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Validate_EmptyEndpoint_HasError()
    {
        var command = ValidCommand() with { Endpoint = "" };

        var result = _validator.TestValidate(command);

        result.ShouldHaveValidationErrorFor(x => x.Endpoint);
    }

    [Fact]
    public void Validate_EndpointOver2000Chars_HasError()
    {
        var command = ValidCommand() with { Endpoint = new string('a', 2001) };

        var result = _validator.TestValidate(command);

        result.ShouldHaveValidationErrorFor(x => x.Endpoint);
    }

    [Fact]
    public void Validate_EmptyUserId_HasError()
    {
        var command = ValidCommand() with { UserId = Guid.Empty };

        var result = _validator.TestValidate(command);

        result.ShouldHaveValidationErrorFor(x => x.UserId);
    }
}
