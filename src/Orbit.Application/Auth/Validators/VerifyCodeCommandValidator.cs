using FluentValidation;
using Orbit.Application.Auth.Commands;

namespace Orbit.Application.Auth.Validators;

public class VerifyCodeCommandValidator : AbstractValidator<VerifyCodeCommand>
{
    public VerifyCodeCommandValidator()
    {
        RuleFor(x => x.Email)
            .NotEmpty()
            .EmailAddress();

        RuleFor(x => x.Code)
            .NotEmpty()
            .Length(6)
            .Matches(@"^\d{6}$")
            .WithMessage("Code must be a 6-digit number");
    }
}
