using FluentValidation;
using Orbit.Application.Goals.Commands;

namespace Orbit.Application.Goals.Validators;

public class ReorderGoalsCommandValidator : AbstractValidator<ReorderGoalsCommand>
{
    public ReorderGoalsCommandValidator()
    {
        RuleFor(x => x.UserId).NotEmpty();
        RuleFor(x => x.Positions).NotEmpty();
    }
}
