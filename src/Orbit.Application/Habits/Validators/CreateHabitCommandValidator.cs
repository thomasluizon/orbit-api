using FluentValidation;
using Orbit.Application.Common;
using Orbit.Application.Habits.Commands;

namespace Orbit.Application.Habits.Validators;

public class CreateHabitCommandValidator : AbstractValidator<CreateHabitCommand>
{
    public CreateHabitCommandValidator()
    {
        RuleFor(x => x.UserId)
            .NotEmpty();

        SharedHabitRules.AddTitleRules(RuleFor(x => x.Title));

        RuleFor(x => x.FrequencyQuantity)
            .GreaterThan(0)
            .When(x => x.FrequencyQuantity is not null);

        SharedHabitRules.AddDaysRules(this, x => x.Days, x => x.FrequencyQuantity);

        RuleFor(x => x.SubHabits)
            .Must(subs => subs is null || subs.Count <= AppConstants.MaxSubHabits)
            .WithMessage($"A habit can have at most {AppConstants.MaxSubHabits} sub-habits");

        RuleForEach(x => x.SubHabits)
            .NotEmpty()
            .WithMessage("Sub-habit title must not be empty")
            .MaximumLength(AppConstants.MaxHabitTitleLength)
            .WithMessage($"Sub-habit title must not exceed {AppConstants.MaxHabitTitleLength} characters");
    }
}
