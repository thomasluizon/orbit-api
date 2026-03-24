using FluentValidation;
using Orbit.Application.Habits.Commands;

namespace Orbit.Application.Habits.Validators;

public class UpdateHabitCommandValidator : AbstractValidator<UpdateHabitCommand>
{
    public UpdateHabitCommandValidator()
    {
        RuleFor(x => x.UserId)
            .NotEmpty();

        RuleFor(x => x.HabitId)
            .NotEmpty();

        SharedHabitRules.AddTitleRules(RuleFor(x => x.Title));

        RuleFor(x => x.FrequencyQuantity)
            .GreaterThan(0)
            .When(x => x.FrequencyQuantity is not null);

        RuleFor(x => x.FrequencyQuantity)
            .NotNull()
            .WithMessage("Frequency quantity is required when frequency unit is set")
            .When(x => x.FrequencyUnit is not null);

        SharedHabitRules.AddDaysRules(this, x => x.Days, x => x.FrequencyQuantity);
    }
}
