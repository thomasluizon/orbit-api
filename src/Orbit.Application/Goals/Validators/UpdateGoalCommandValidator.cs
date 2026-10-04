using FluentValidation;
using Orbit.Application.Common;
using Orbit.Application.Goals.Commands;

namespace Orbit.Application.Goals.Validators;

public class UpdateGoalCommandValidator : AbstractValidator<UpdateGoalCommand>
{
    public UpdateGoalCommandValidator()
    {
        RuleFor(x => x.UserId).NotEmpty();
        RuleFor(x => x.GoalId).NotEmpty();
        RuleFor(x => x.Title).NotEmpty().WithFieldCopy(ValidationCopyKeys.GoalTitleRequired)
            .MaximumLength(200).WithFieldCopy(ValidationCopyKeys.GoalTitleLength);
        RuleFor(x => x.Description).MaximumLength(AppConstants.MaxGoalDescriptionLength)
            .WithFieldCopy(ValidationCopyKeys.GoalDescriptionLength);
        RuleFor(x => x.TargetValue).GreaterThan(0).WithFieldCopy(ValidationCopyKeys.GoalTargetPositive);
        RuleFor(x => x.Unit).NotEmpty().WithFieldCopy(ValidationCopyKeys.GoalUnitRequired)
            .MaximumLength(50).WithFieldCopy(ValidationCopyKeys.GoalUnitLength);
    }
}
