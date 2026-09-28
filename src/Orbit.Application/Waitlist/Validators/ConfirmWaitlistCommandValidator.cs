using FluentValidation;
using Orbit.Application.Waitlist.Commands;

namespace Orbit.Application.Waitlist.Validators;

public sealed class ConfirmWaitlistCommandValidator : AbstractValidator<ConfirmWaitlistCommand>
{
    public ConfirmWaitlistCommandValidator()
    {
        RuleFor(command => command.Token).NotEmpty();
    }
}
