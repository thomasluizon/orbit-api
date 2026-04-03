using FluentValidation.TestHelper;
using Orbit.Application.Notifications.Commands;
using Orbit.Application.Notifications.Validators;

namespace Orbit.Application.Tests.Validators;

public class DeleteNotificationCommandValidatorTests
{
    private readonly DeleteNotificationCommandValidator _validator = new();

    private static DeleteNotificationCommand ValidCommand() => new(
        UserId: Guid.NewGuid(),
        NotificationId: Guid.NewGuid());

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
    public void Validate_EmptyNotificationId_HasError()
    {
        var result = _validator.TestValidate(ValidCommand() with { NotificationId = Guid.Empty });
        result.ShouldHaveValidationErrorFor(x => x.NotificationId);
    }
}
