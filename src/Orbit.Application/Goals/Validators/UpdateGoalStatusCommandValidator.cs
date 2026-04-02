using FluentValidation;
using Orbit.Application.Goals.Commands;

namespace Orbit.Application.Goals.Validators;

public class UpdateGoalStatusCommandValidator : AbstractValidator<UpdateGoalStatusCommand>
{
    public UpdateGoalStatusCommandValidator()
    {
        RuleFor(x => x.UserId).NotEmpty();
        RuleFor(x => x.GoalId).NotEmpty();
        RuleFor(x => x.NewStatus).IsInEnum();
    }
}
