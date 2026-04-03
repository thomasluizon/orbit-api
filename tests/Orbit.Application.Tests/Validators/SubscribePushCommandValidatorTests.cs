using FluentValidation.TestHelper;
using Orbit.Application.Notifications.Commands;
using Orbit.Application.Notifications.Validators;

namespace Orbit.Application.Tests.Validators;

public class SubscribePushCommandValidatorTests
{
    private readonly SubscribePushCommandValidator _validator = new();

    private static SubscribePushCommand ValidCommand() => new(
        UserId: Guid.NewGuid(),
        Endpoint: "https://push.example.com/sub/abc123",
        P256dh: "test-p256dh-key-value",
        Auth: "test-auth-value");

    [Fact]
    public void Validate_ValidInput_NoErrors()
    {
        // Arrange
        var command = ValidCommand();

        // Act
        var result = _validator.TestValidate(command);

        // Assert
        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Validate_EmptyEndpoint_HasError()
    {
        // Arrange
        var command = ValidCommand() with { Endpoint = "" };

        // Act
        var result = _validator.TestValidate(command);

        // Assert
        result.ShouldHaveValidationErrorFor(x => x.Endpoint);
    }

    [Fact]
    public void Validate_EndpointOver2000Chars_HasError()
    {
        // Arrange
        var command = ValidCommand() with { Endpoint = new string('a', 2001) };

        // Act
        var result = _validator.TestValidate(command);

        // Assert
        result.ShouldHaveValidationErrorFor(x => x.Endpoint);
    }

    [Fact]
    public void Validate_EmptyP256dh_HasError()
    {
        // Arrange
        var command = ValidCommand() with { P256dh = "" };

        // Act
        var result = _validator.TestValidate(command);

        // Assert
        result.ShouldHaveValidationErrorFor(x => x.P256dh);
    }

    [Fact]
    public void Validate_EmptyAuth_HasError()
    {
        // Arrange
        var command = ValidCommand() with { Auth = "" };

        // Act
        var result = _validator.TestValidate(command);

        // Assert
        result.ShouldHaveValidationErrorFor(x => x.Auth);
    }

    [Fact]
    public void Validate_EmptyUserId_HasError()
    {
        // Arrange
        var command = ValidCommand() with { UserId = Guid.Empty };

        // Act
        var result = _validator.TestValidate(command);

        // Assert
        result.ShouldHaveValidationErrorFor(x => x.UserId);
    }
}
