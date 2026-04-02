using FluentValidation;
using Orbit.Application.Notifications.Commands;

namespace Orbit.Application.Notifications.Validators;

public class DeleteNotificationCommandValidator : AbstractValidator<DeleteNotificationCommand>
{
    public DeleteNotificationCommandValidator()
    {
        RuleFor(x => x.UserId).NotEmpty();
        RuleFor(x => x.NotificationId).NotEmpty();
    }
}
