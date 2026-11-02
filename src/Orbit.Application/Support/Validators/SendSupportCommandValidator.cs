using FluentValidation;
using Orbit.Application.Common;
using Orbit.Application.Support.Commands;

namespace Orbit.Application.Support.Validators;

public class SendSupportCommandValidator : AbstractValidator<SendSupportCommand>
{
    public SendSupportCommandValidator()
    {
        RuleFor(x => x.Subject)
            .NotEmpty().WithFieldCopy(ValidationCopyKeys.SupportSubjectRequired)
            .MaximumLength(200).WithFieldCopy(ValidationCopyKeys.SupportSubjectLength);

        RuleFor(x => x.Message)
            .NotEmpty().WithFieldCopy(ValidationCopyKeys.SupportMessageRequired)
            .MaximumLength(5000).WithFieldCopy(ValidationCopyKeys.SupportMessageLength);
    }
}
