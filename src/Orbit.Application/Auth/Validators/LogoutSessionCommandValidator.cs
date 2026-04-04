using FluentValidation;
using Orbit.Application.Auth.Commands;

namespace Orbit.Application.Auth.Validators;

public class LogoutSessionCommandValidator : AbstractValidator<LogoutSessionCommand>
{
    public LogoutSessionCommandValidator()
    {
        RuleFor(x => x.RefreshToken)
            .NotEmpty();
    }
}
