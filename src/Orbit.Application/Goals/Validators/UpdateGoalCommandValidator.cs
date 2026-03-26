using FluentValidation;
using Orbit.Application.Goals.Commands;

namespace Orbit.Application.Goals.Validators;

public class UpdateGoalCommandValidator : AbstractValidator<UpdateGoalCommand>
{
    public UpdateGoalCommandValidator()
    {
        RuleFor(x => x.UserId).NotEmpty();
        RuleFor(x => x.GoalId).NotEmpty();
        RuleFor(x => x.Title).NotEmpty().MaximumLength(200);
        RuleFor(x => x.TargetValue).GreaterThan(0);
        RuleFor(x => x.Unit).NotEmpty().MaximumLength(50);
    }
}
