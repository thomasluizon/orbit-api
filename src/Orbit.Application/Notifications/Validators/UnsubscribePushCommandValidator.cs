using FluentValidation;
using Orbit.Application.Notifications.Commands;

namespace Orbit.Application.Notifications.Validators;

public class UnsubscribePushCommandValidator : AbstractValidator<UnsubscribePushCommand>
{
    public UnsubscribePushCommandValidator()
    {
        RuleFor(x => x.UserId).NotEmpty();
        RuleFor(x => x.Endpoint).NotEmpty().MaximumLength(2000);
    }
}
