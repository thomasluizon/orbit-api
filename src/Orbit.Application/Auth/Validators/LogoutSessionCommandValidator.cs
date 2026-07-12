using FluentValidation;
using Orbit.Application.Auth.Commands;

namespace Orbit.Application.Auth.Validators;

public class LogoutSessionCommandValidator : AbstractValidator<LogoutSessionCommand>
{
    public LogoutSessionCommandValidator()
    {
        RefreshTokenRules.AddRefreshTokenRules(RuleFor(x => x.RefreshToken));
    }
}
