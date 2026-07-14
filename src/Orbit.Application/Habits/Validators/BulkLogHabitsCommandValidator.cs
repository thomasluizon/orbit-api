using Orbit.Application.Common;
using Orbit.Application.Habits.Commands;

namespace Orbit.Application.Habits.Validators;

public class BulkLogHabitsCommandValidator
    : BulkHabitCommandValidatorBase<BulkLogHabitsCommand, BulkLogItem>
{
    public BulkLogHabitsCommandValidator()
        : base($"Bulk log cannot exceed {AppConstants.MaxBulkOperationSize} items")
    {
    }
}
