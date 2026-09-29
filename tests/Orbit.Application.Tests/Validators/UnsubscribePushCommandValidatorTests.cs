using FluentValidation.TestHelper;
using Orbit.Application.Notifications.Commands;
using Orbit.Application.Notifications.Validators;

namespace Orbit.Application.Tests.Validators;

public class UnsubscribePushCommandValidatorTests
{
    private readonly UnsubscribePushCommandValidator _validator = new();

    private static UnsubscribePushCommand ValidCommand() => new(
        UserId: Guid.NewGuid(),
        Endpoint: "https://push.example.com/sub/abc123",
        P256dh: "p256dh-key",
        Auth: "auth-secret");

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
    public void Validate_WithoutDeviceKeys_NoErrors()
    {
        var command = ValidCommand() with { P256dh = null, Auth = null };

        var result = _validator.TestValidate(command);

        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Validate_P256dhOver500Chars_HasError()
    {
        var command = ValidCommand() with { P256dh = new string('a', 501) };

        var result = _validator.TestValidate(command);

        result.ShouldHaveValidationErrorFor(x => x.P256dh);
    }

    [Fact]
    public void Validate_AuthOver500Chars_HasError()
    {
        var command = ValidCommand() with { Auth = new string('a', 501) };

        var result = _validator.TestValidate(command);

        result.ShouldHaveValidationErrorFor(x => x.Auth);
    }

    [Fact]
    public void Validate_EmptyUserId_HasError()
    {
        var command = ValidCommand() with { UserId = Guid.Empty };

        var result = _validator.TestValidate(command);

        result.ShouldHaveValidationErrorFor(x => x.UserId);
    }
}
