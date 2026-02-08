using FluentValidation;
using Orbit.Application.Habits.Commands;
using Orbit.Domain.Enums;

namespace Orbit.Application.Habits.Validators;

public class CreateHabitCommandValidator : AbstractValidator<CreateHabitCommand>
{
    public CreateHabitCommandValidator()
    {
        RuleFor(x => x.UserId)
            .NotEmpty();

        RuleFor(x => x.Title)
            .NotEmpty()
            .MaximumLength(200);

        RuleFor(x => x.FrequencyQuantity)
            .GreaterThan(0);

        RuleFor(x => x.Days)
            .Must((command, days) => days is null || days.Count == 0 || command.FrequencyQuantity == 1)
            .WithMessage("Days can only be specified when frequency quantity is 1");

        RuleFor(x => x.Unit)
            .NotEmpty()
            .When(x => x.Type == HabitType.Quantifiable)
            .WithMessage("Unit is required for quantifiable habits");
    }
}
