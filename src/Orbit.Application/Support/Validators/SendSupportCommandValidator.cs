using FluentValidation;
using Orbit.Application.Support.Commands;

namespace Orbit.Application.Support.Validators;

public class SendSupportCommandValidator : AbstractValidator<SendSupportCommand>
{
    public SendSupportCommandValidator()
    {
        RuleFor(x => x.Name)
            .NotEmpty();

        RuleFor(x => x.Email)
            .NotEmpty()
            .EmailAddress();

        RuleFor(x => x.Subject)
            .NotEmpty()
            .MaximumLength(200);

        RuleFor(x => x.Message)
            .NotEmpty()
            .MaximumLength(5000);
    }
}
