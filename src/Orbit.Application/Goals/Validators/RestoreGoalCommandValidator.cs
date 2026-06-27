using FluentValidation;
using Orbit.Application.Goals.Commands;

namespace Orbit.Application.Goals.Validators;

public class RestoreGoalCommandValidator : AbstractValidator<RestoreGoalCommand>
{
    public RestoreGoalCommandValidator()
    {
        RuleFor(x => x.UserId).NotEmpty();
        RuleFor(x => x.GoalId).NotEmpty();
    }
}
