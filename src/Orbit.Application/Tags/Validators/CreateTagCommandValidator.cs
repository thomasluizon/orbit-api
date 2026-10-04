using Orbit.Application.Common;
using FluentValidation;
using Orbit.Application.Tags.Commands;

namespace Orbit.Application.Tags.Validators;

public class CreateTagCommandValidator : AbstractValidator<CreateTagCommand>
{
    public CreateTagCommandValidator()
    {
        RuleFor(x => x.UserId)
            .NotEmpty();

        RuleFor(x => x.Name)
            .NotEmpty()
            .WithFieldCopy(ValidationCopyKeys.TagNameRequired)
            .MaximumLength(50)
            .WithFieldCopy(ValidationCopyKeys.TagNameLength);

        RuleFor(x => x.Color)
            .NotEmpty()
            .WithFieldCopy(ValidationCopyKeys.TagColorRequired)
            .Matches(@"^#[0-9A-Fa-f]{6}$")
            .WithCopy(ValidationErrorCodes.TagColorFormat);
    }
}
