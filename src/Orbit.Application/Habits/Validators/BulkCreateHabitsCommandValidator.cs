using FluentValidation;
using Orbit.Application.Habits.Commands;

namespace Orbit.Application.Habits.Validators;

public class BulkCreateHabitsCommandValidator : AbstractValidator<BulkCreateHabitsCommand>
{
    public BulkCreateHabitsCommandValidator()
    {
        RuleFor(x => x.UserId)
            .NotEmpty();

        RuleFor(x => x.Habits)
            .NotEmpty()
            .WithMessage("Habits list must not be empty")
            .Must(habits => habits.Count <= 100)
            .WithMessage("Cannot create more than 100 habits at once");

        RuleForEach(x => x.Habits)
            .SetValidator(new BulkHabitItemValidator());
    }
}

public class BulkHabitItemValidator : AbstractValidator<BulkHabitItem>
{
    public BulkHabitItemValidator()
    {
        RuleFor(x => x.Title)
            .NotEmpty()
            .WithMessage("Title must not be empty")
            .MaximumLength(200)
            .WithMessage("Title must not exceed 200 characters");

        RuleFor(x => x.FrequencyQuantity)
            .GreaterThan(0)
            .When(x => x.FrequencyQuantity is not null)
            .WithMessage("Frequency quantity must be greater than 0");

        RuleFor(x => x.Days)
            .Must((item, days) => days is null || days.Count == 0 || item.FrequencyQuantity == 1)
            .WithMessage("Days can only be specified when frequency quantity is 1");

        RuleFor(x => x.SubHabits)
            .Must(subs => subs is null || subs.Count <= 20)
            .WithMessage("A habit can have at most 20 sub-habits");

        RuleForEach(x => x.SubHabits)
            .SetValidator(new BulkSubHabitItemValidator());
    }
}

public class BulkSubHabitItemValidator : AbstractValidator<BulkHabitItem>
{
    public BulkSubHabitItemValidator()
    {
        RuleFor(x => x.Title)
            .NotEmpty()
            .WithMessage("Sub-habit title must not be empty")
            .MaximumLength(200)
            .WithMessage("Sub-habit title must not exceed 200 characters");

        RuleFor(x => x.FrequencyQuantity)
            .GreaterThan(0)
            .When(x => x.FrequencyQuantity is not null)
            .WithMessage("Sub-habit frequency quantity must be greater than 0");

        RuleFor(x => x.Days)
            .Must((item, days) => days is null || days.Count == 0 || item.FrequencyQuantity == 1)
            .WithMessage("Sub-habit days can only be specified when frequency quantity is 1");
    }
}
