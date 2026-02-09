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

        RuleFor(x => x.Title)
            .NotEmpty()
            .MaximumLength(200);

        RuleFor(x => x.FrequencyQuantity)
            .GreaterThan(0)
            .When(x => x.FrequencyQuantity is not null);

        RuleFor(x => x.Days)
            .Must((command, days) => days is null || days.Count == 0 || command.FrequencyQuantity == 1)
            .WithMessage("Days can only be specified when frequency quantity is 1");
    }
}
