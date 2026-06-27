using FluentValidation;
using Orbit.Application.Tags.Commands;

namespace Orbit.Application.Tags.Validators;

public class RestoreTagCommandValidator : AbstractValidator<RestoreTagCommand>
{
    public RestoreTagCommandValidator()
    {
        RuleFor(x => x.UserId).NotEmpty();
        RuleFor(x => x.TagId).NotEmpty();
    }
}
