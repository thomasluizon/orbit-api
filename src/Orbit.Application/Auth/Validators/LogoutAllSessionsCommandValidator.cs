using FluentValidation;
using Orbit.Application.Auth.Commands;

namespace Orbit.Application.Auth.Validators;

public class LogoutAllSessionsCommandValidator : AbstractValidator<LogoutAllSessionsCommand>
{
    public LogoutAllSessionsCommandValidator()
    {
        RuleFor(x => x.UserId)
            .NotEmpty();
    }
}
