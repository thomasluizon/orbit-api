using FluentValidation;
using Orbit.Application.Notifications.Commands;

namespace Orbit.Application.Notifications.Validators;

public class SubscribePushCommandValidator : AbstractValidator<SubscribePushCommand>
{
    public SubscribePushCommandValidator()
    {
        RuleFor(x => x.UserId).NotEmpty();
        RuleFor(x => x.Endpoint).NotEmpty().MaximumLength(2000);
        RuleFor(x => x.P256dh).NotEmpty().MaximumLength(500);
        RuleFor(x => x.Auth).NotEmpty().MaximumLength(500);
    }
}
