using FluentValidation;
using Orbit.Application.Tags.Commands;

namespace Orbit.Application.Tags.Validators;

public class AssignTagCommandValidator : AbstractValidator<AssignTagCommand>
{
    public AssignTagCommandValidator()
    {
        RuleFor(x => x.UserId)
            .NotEmpty();

        RuleFor(x => x.HabitId)
            .NotEmpty();

        RuleFor(x => x.TagId)
            .NotEmpty();
    }
}
