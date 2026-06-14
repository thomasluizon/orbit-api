using FluentValidation;
using Orbit.Application.Common;
using Orbit.Application.Goals.Commands;

namespace Orbit.Application.Goals.Validators;

public class UpdateGoalProgressCommandValidator : AbstractValidator<UpdateGoalProgressCommand>
{
    public UpdateGoalProgressCommandValidator()
    {
        RuleFor(x => x.UserId).NotEmpty();
        RuleFor(x => x.GoalId).NotEmpty();
        RuleFor(x => x.NewValue).GreaterThanOrEqualTo(0);
        RuleFor(x => x.Note).MaximumLength(AppConstants.MaxGoalProgressNoteLength);
    }
}
