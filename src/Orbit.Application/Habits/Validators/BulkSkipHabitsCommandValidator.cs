using Orbit.Application.Common;
using Orbit.Application.Habits.Commands;

namespace Orbit.Application.Habits.Validators;

public class BulkSkipHabitsCommandValidator
    : BulkHabitCommandValidatorBase<BulkSkipHabitsCommand, BulkSkipItem>
{
    public BulkSkipHabitsCommandValidator()
        : base($"Bulk skip cannot exceed {AppConstants.MaxBulkOperationSize} items")
    {
    }
}
