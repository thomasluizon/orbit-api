using FluentValidation;
using Orbit.Application.Common;
using Orbit.Application.Habits.Commands;

namespace Orbit.Application.Habits.Validators;

public class BulkMoveHabitParentCommandValidator
    : BulkHabitCommandValidatorBase<BulkMoveHabitParentCommand, BulkMoveHabitParentItem>
{
    public BulkMoveHabitParentCommandValidator() : base(ValidationErrorCodes.BulkMoveParentLimit)
    {
        RuleFor(x => x.ParentId).NotEqual(Guid.Empty).When(x => x.ParentId.HasValue);
    }
}
