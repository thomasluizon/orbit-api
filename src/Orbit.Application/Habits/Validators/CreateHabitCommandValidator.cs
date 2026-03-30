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

        SharedHabitRules.AddDescriptionRules(RuleFor(x => x.Description));

        SharedHabitRules.AddChecklistItemRules(RuleFor(x => x.ChecklistItems));

        RuleFor(x => x.FrequencyQuantity)
            .GreaterThan(0)
            .When(x => x.FrequencyQuantity is not null);

        RuleFor(x => x.FrequencyQuantity)
            .NotNull()
            .WithMessage("Frequency quantity is required when frequency unit is set")
            .When(x => x.FrequencyUnit is not null);

        SharedHabitRules.AddDaysRules(this, x => x.Days, x => x.FrequencyQuantity);

        RuleFor(x => x.SubHabits)
            .Must(subs => subs is null || subs.Count <= AppConstants.MaxSubHabits)
            .WithMessage($"A habit can have at most {AppConstants.MaxSubHabits} sub-habits");

        RuleForEach(x => x.SubHabits)
            .NotEmpty()
            .WithMessage("Sub-habit title must not be empty")
            .MaximumLength(AppConstants.MaxHabitTitleLength)
            .WithMessage($"Sub-habit title must not exceed {AppConstants.MaxHabitTitleLength} characters");

        SharedHabitRules.AddGeneralHabitRules(this,
            x => x.IsGeneral,
            x => x.FrequencyUnit,
            x => x.FrequencyQuantity,
            x => x.Days);

        RuleFor(x => x.IsBadHabit)
            .Equal(false)
            .When(x => x.IsGeneral)
            .WithMessage("General habits cannot be bad habits");

        SharedHabitRules.AddScheduledReminderRules(RuleFor(x => x.ScheduledReminders));
    }
}
