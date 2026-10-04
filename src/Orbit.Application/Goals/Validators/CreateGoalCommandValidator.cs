using FluentValidation;
using Orbit.Application.Common;
using Orbit.Application.Goals.Commands;

namespace Orbit.Application.Goals.Validators;

public class CreateGoalCommandValidator : AbstractValidator<CreateGoalCommand>
{
    public CreateGoalCommandValidator()
    {
        RuleFor(x => x.UserId).NotEmpty();
        RuleFor(x => x.Title).NotEmpty().WithFieldCopy(ValidationCopyKeys.GoalTitleRequired)
            .MaximumLength(200).WithFieldCopy(ValidationCopyKeys.GoalTitleLength);
        RuleFor(x => x.Description).MaximumLength(AppConstants.MaxGoalDescriptionLength)
            .WithFieldCopy(ValidationCopyKeys.GoalDescriptionLength);
        RuleFor(x => x.TargetValue).GreaterThan(0).WithFieldCopy(ValidationCopyKeys.GoalTargetPositive);
        RuleFor(x => x.Unit).NotEmpty().WithFieldCopy(ValidationCopyKeys.GoalUnitRequired)
            .MaximumLength(50).WithFieldCopy(ValidationCopyKeys.GoalUnitLength);
        RuleFor(x => x.Type).IsInEnum();
        RuleFor(x => x.HabitIds)
            .Must(ids => ids is null || ids.Count <= AppConstants.MaxHabitsPerGoal)
            .WithCopy(ErrorMessages.MaxHabitsPerGoal.Code, AppConstants.MaxHabitsPerGoal);
    }
}
