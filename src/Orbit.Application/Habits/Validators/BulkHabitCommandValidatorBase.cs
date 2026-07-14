using FluentValidation;
using Orbit.Application.Common;
using Orbit.Application.Habits.Commands;

namespace Orbit.Application.Habits.Validators;

/// <summary>
/// Shared FluentValidation rules for bulk habit commands: non-empty user, a non-empty item
/// list capped at <see cref="AppConstants.MaxBulkOperationSize"/>, and a non-empty habit id per item.
/// The exceed-cap message differs per operation and is supplied by the derived validator.
/// </summary>
public abstract class BulkHabitCommandValidatorBase<TCommand, TItem> : AbstractValidator<TCommand>
    where TCommand : IBulkHabitCommand<TItem>
    where TItem : IBulkHabitItem
{
    protected BulkHabitCommandValidatorBase(string exceedLimitMessage)
    {
        RuleFor(x => x.UserId)
            .NotEmpty();

        RuleFor(x => x.Items)
            .NotEmpty()
            .WithMessage("Items list must not be empty");

        RuleFor(x => x.Items)
            .Must(items => items is null || items.Count <= AppConstants.MaxBulkOperationSize)
            .WithMessage(exceedLimitMessage);

        RuleForEach(x => x.Items)
            .ChildRules(item =>
            {
                item.RuleFor(i => i.HabitId)
                    .NotEmpty();
            });
    }
}
