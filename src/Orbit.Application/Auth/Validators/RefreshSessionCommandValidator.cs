using FluentValidation;
using Orbit.Application.Auth.Commands;

namespace Orbit.Application.Auth.Validators;

public class RefreshSessionCommandValidator : AbstractValidator<RefreshSessionCommand>
{
    public RefreshSessionCommandValidator()
    {
        RuleFor(x => x.RefreshToken)
            .NotEmpty();
    }
}
