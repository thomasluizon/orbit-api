using FluentValidation;
using Orbit.Application.Goals.Commands;

namespace Orbit.Application.Goals.Validators;

public class DeleteGoalCommandValidator : AbstractValidator<DeleteGoalCommand>
{
    public DeleteGoalCommandValidator()
    {
        RuleFor(x => x.UserId).NotEmpty();
        RuleFor(x => x.GoalId).NotEmpty();
    }
}
